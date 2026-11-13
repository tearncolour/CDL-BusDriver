"""Render the final saved source, retain camera/LOD/action/hash evidence and mesh diagnostics."""
import argparse,hashlib,json,math,sys
from pathlib import Path
import bpy
import numpy as np
from mathutils import Vector

def main():
    p=argparse.ArgumentParser();p.add_argument('--blend',type=Path,required=True);p.add_argument('--data',type=Path,required=True)
    p.add_argument('--out',type=Path,required=True);p.add_argument('--quick',action='store_true');p.add_argument('--details-only',action='store_true')
    p.add_argument('--lod',type=int,choices=(0,1,2));p.add_argument('--samples',type=int,default=16);p.add_argument('--doors-only',action='store_true')
    p.add_argument('--door',type=int);p.add_argument('--door-side',choices=('inside','outside'))
    p.add_argument('--views',help='Comma-separated existing camera names')
    p.add_argument('--inspection-light',action='store_true')
    p.add_argument('--mount-report',type=Path)
    p.add_argument('--frames',help='Comma-separated saved action frames for door views')
    p.add_argument('--hip-device',help='Exact HIP device name; selection is process-local and preferences are not saved')
    a=p.parse_args(sys.argv[sys.argv.index('--')+1:]);a.out=a.out.resolve();a.out.mkdir(parents=True,exist_ok=False)
    bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()));scene=bpy.context.scene
    scene.render.engine='CYCLES';scene.cycles.samples=a.samples;scene.cycles.use_denoising=True
    scene.render.use_persistent_data=True
    if a.hip_device:
        prefs=bpy.context.preferences.addons['cycles'].preferences;prefs.compute_device_type='HIP';prefs.get_devices()
        selected=[d for d in prefs.devices if d.type=='HIP' and d.name==a.hip_device]
        if len(selected)!=1:raise ValueError('HIP device not uniquely available: '+a.hip_device)
        for device in prefs.devices:device.use=device==selected[0]
        scene.cycles.device='GPU'
    scene.render.resolution_x=1280;scene.render.resolution_y=960;scene.render.resolution_percentage=100
    d=json.loads((a.data/'layout.json').read_text());eye=Vector((-d['cameraEye'][0],-d['cameraEye'][2],d['cameraEye'][1]))
    cam=bpy.data.objects.new('Review camera',bpy.data.cameras.new('Review camera'));scene.collection.objects.link(cam);scene.camera=cam;cam.data.clip_start=.025;cam.data.clip_end=100
    views=[('front-quarter',(10,-14,5.7),(0,0,1.4),38),('rear-quarter',(-10,14,5),(0,0,1.4),38),
      ('front',(0,-14,2),(0,-5,1.5),50),('rear',(0,14,2),(0,5,1.5),50),
      ('cab',eye,eye+Vector((0,-4,-.3)),26),('front-aisle',(0,-3.7,1.7),(0,5,1.5),24),
      ('rear-aisle',(0,4.1,2.05),(0,-4.8,1.6),24),('footwell',(.45,-5.1,1.35),(.62,-5.35,.83),24),
      ('front-low',(0,-1, .65),(.80,-3.4,.9),25),('rear-low',(0,3.9,1.05),(.65,2.5,.8),26)]
    views.extend([('wheel-close',eye,(.62,-5.16,1.5),38),('trim-close',eye,(-.15,-5.35,1.23),35),
        ('driver-side',(15,0,3),(0,0,1.5),36),('door-side',(-15,0,3),(0,0,1.5),36)])
    for i,m in enumerate(d['mirrors']):
        pos=Vector((-m['centre'][0],-m['centre'][2],m['centre'][1]))
        views.append(('mirror-eye-'+str(i),eye,pos,52 if i==0 else 65))
        if i<2:views.append(('mirror-mount-'+str(i),pos+Vector((1 if i==0 else -1,.4,.3)),pos+Vector((-.18 if i==0 else .18,.04,.18)),55))
    if a.mount_report:
        mounts=json.loads(a.mount_report.read_text())
        if mounts['source_sha256']!=hashlib.sha256(a.blend.read_bytes()).hexdigest():raise ValueError('Stale mirror support evidence')
        for i,m in enumerate(mounts['lods'][0]['mounts']):
            row=min(m['rows'],key=lambda r:abs(r['u'])+abs(r['v']));foot=Vector(row['hit'])
            offset=Vector((.65,-.2,.15) if m['driver'] else (-.4,-.8,.2))
            views.append(('mirror-body-'+str(i),foot+offset,foot,38))
    if a.quick:views=[v for v in views if v[0] in ('front-quarter','cab','mirror-eye-0','mirror-eye-1','footwell')]
    if a.details_only:views=[v for v in views if v[0] in ('wheel-close','trim-close')]
    if a.doors_only:views=[]
    if a.views:
        names=set(a.views.split(','));known={v[0] for v in views}
        if names-known:raise ValueError('Unknown views: '+str(names-known))
        views=[v for v in views if v[0] in names]
    fill=None
    if a.inspection_light:
        light=bpy.data.lights.new('Documented camera inspection fill','AREA');light.energy=90;light.shape='DISK';light.size=1.2
        fill=bpy.data.objects.new(light.name,light);scene.collection.objects.link(fill)
    records=[]
    for lod in ([a.lod] if a.lod is not None else [0] if a.quick else range(3)):
        for i in range(3):bpy.data.collections['LOD%d'%i].hide_render=bpy.data.collections['LOD%d'%i].hide_viewport=i!=lod
        scene.frame_set(0);bpy.context.view_layer.update()
        for name,pos,target,lens in views:
            cam.location=pos;cam.rotation_euler=(Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.lens=lens
            if fill:
                fill.location=cam.location+Vector((0,0,.18));fill.rotation_euler=(Vector(target)-fill.location).to_track_quat('-Z','Y').to_euler()
            path=a.out/('LOD%d-%s.png'%(lod,name));scene.render.filepath=str(path);bpy.ops.render.render(write_still=True)
            records.append({'image':path.name,'lod':lod,'frame':0,'camera':list(cam.location),'target':list(target),'lens_mm':lens,'resolution':[1280,960]})
            if name in ('wheel-close','trim-close'):
                links=[]
                for mat in bpy.data.materials:
                    if not mat.use_nodes:continue
                    for node in mat.node_tree.nodes:
                        if node.type=='BSDF_PRINCIPLED':
                            for link in list(node.inputs['Normal'].links):
                                links.append((mat.node_tree,link.from_socket,link.to_socket));mat.node_tree.links.remove(link)
                path=a.out/('LOD%d-%s-normal-off.png'%(lod,name));scene.render.filepath=str(path);bpy.ops.render.render(write_still=True)
                records.append({'image':path.name,'lod':lod,'frame':0,'normal_maps':False,'camera':list(cam.location),'target':list(target),'lens_mm':lens,'resolution':[1280,960]})
                for nt,src,dst in links:nt.links.new(src,dst)
        if a.doors_only:
            profile=json.loads((a.data/'vehicle.json').read_text())
            for frame in ([int(v) for v in a.frames.split(',')] if a.frames else (0,50,100)):
                scene.frame_set(frame);bpy.context.view_layer.update()
                for di,door in enumerate(profile['doors']):
                    if a.door is not None and di!=a.door:continue
                    y=-door['centreZ']
                    for name,pos,target,lens in [('outside',(-3.8,y-.4,1.5),(-1.2,y,1.4),30),('inside',(.15,y,1.65),(-1.2,y,1.4),16)]:
                        if a.door_side and name!=a.door_side:continue
                        if di==0 and name=='inside':pos=eye;lens=20
                        cam.location=pos;cam.rotation_euler=(Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.lens=lens
                        path=a.out/('LOD%d-door%d-%s-%d.png'%(lod,di,name,frame));scene.render.filepath=str(path);bpy.ops.render.render(write_still=True)
                        records.append({'image':path.name,'lod':lod,'frame':frame,'camera':list(cam.location),'target':list(target),'lens_mm':lens,'resolution':[1280,960]})
        elif not a.quick and not a.details_only and not a.views:
            for frame in (50,100):
                scene.frame_set(frame);cam.location=(-6,-8,2.7);target=Vector((-.8,-3,1.4));cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.lens=38
                path=a.out/('LOD%d-doors-%d.png'%(lod,frame));scene.render.filepath=str(path);bpy.ops.render.render(write_still=True)
                records.append({'image':path.name,'lod':lod,'frame':frame,'camera':list(cam.location),'target':list(target),'lens_mm':38,'resolution':[1280,960]})
    report={'source':str(a.blend.resolve()),'source_sha256':hashlib.sha256(a.blend.read_bytes()).hexdigest(),'views':records,
      'lighting':scene['review_lighting'],'samples':a.samples,'native_or_game_validated':False}
    if fill:report['inspection_fill']={'type':'AREA','power_watts':90,'diameter_m':1.2,'position':'camera + (0,0,0.18) m','aim':'camera target','purpose':'Supplement dark recess geometry inspection; not game lighting'}
    report['cycles_device']=a.hip_device or 'CPU'
    (a.out/'views.json').write_text(json.dumps(report,indent=2),encoding='utf-8')

if __name__=='__main__':main()
