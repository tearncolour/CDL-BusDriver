"""Read-only, repeatable K18 photo-camera and detail renders."""
import bpy,sys,json,hashlib,argparse
from pathlib import Path
from mathutils import Vector,Matrix
p=argparse.ArgumentParser();p.add_argument('--source',type=Path);p.add_argument('--output',type=Path);p.add_argument('--lod',type=int,choices=(0,1,2),default=0);p.add_argument('--lamp-state',choices=('photo','off','headlights','brake','left','right'),default='photo');p.add_argument('--views',nargs='+',default=['front','detail','hero','driver']);p.add_argument('--backface',action='store_true',help='Grey front faces, magenta back faces (runtime single-sided check)')
args=p.parse_args(sys.argv[sys.argv.index('--')+1:])
root=Path(__file__).resolve().parents[2]
src=args.source or root/'ModelSource/CRRC_K18/CRRC_K18.blend'
if not src.is_absolute():src=root/src
out=args.output or root/'ModelSource/CRRC_K18/previews/refined-front'
if not out.is_absolute():out=root/out
out.mkdir(parents=True,exist_ok=True)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
digest=sha(src);bpy.ops.wm.open_mainfile(filepath=str(src));sc=bpy.context.scene;cam=sc.camera
driver_eye=Vector(bpy.data.objects['section_front']['driver_eye_unity'])
u=Matrix(((-1,0,0),(0,0,-1),(0,1,0)));sc.frame_set(1)
for ob in bpy.data.objects:
    if ob.type in ('MESH','CURVE','FONT') and ob.get('cab_section') in ('front','rear'):
        ob.hide_render=not(int(ob.get('first_lod',0))<=args.lod<=int(ob.get('last_lod',2)))
active_lights={'photo':{1,2,8,9},'off':{8,9},'headlights':{1,2,3,8,9},'brake':{1,2,3,4,8,9},'left':{1,2,3,5,8,9},'right':{1,2,3,6,8,9}}[args.lamp_state]
for material in bpy.data.materials:
    if material.use_nodes and material.get('light_id',0):
        shader=material.node_tree.nodes.get('Principled BSDF')
        if shader and material['light_id'] not in active_lights:shader.inputs['Emission Strength'].default_value=0.
sc.render.engine='CYCLES';sc.cycles.samples=32;sc.cycles.use_denoising=True
if args.backface:
    diag=bpy.data.materials.new('Review backface diagnostic');diag.use_nodes=True
    nt=diag.node_tree;nt.nodes.clear();o=nt.nodes.new('ShaderNodeOutputMaterial');e=nt.nodes.new('ShaderNodeEmission');g=nt.nodes.new('ShaderNodeNewGeometry');m=nt.nodes.new('ShaderNodeMixRGB')
    m.inputs[1].default_value=(.72,.72,.72,1);m.inputs[2].default_value=(1,0,.35,1);e.inputs['Strength'].default_value=.8
    nt.links.new(g.outputs['Backfacing'],m.inputs[0]);nt.links.new(m.outputs[0],e.inputs[0]);nt.links.new(e.outputs[0],o.inputs[0])
    for ob in bpy.data.objects:
        if ob.type=='MESH':
            for slot in ob.material_slots:slot.link=('OBJECT');slot.material=diag
sc.render.resolution_percentage=100
cal=json.loads((root/'out/k18-front-refinement/camera-calibration.json').read_text())
rows=[]
if (out/'manifest.json').exists():
    old=json.loads((out/'manifest.json').read_text())
    if old.get('source_sha256')==digest and old.get('lod',0)==args.lod and old.get('lamp_state','photo')==args.lamp_state:
        rows=[row for row in old['views'] if row['view'] not in args.views]
for view in args.views:
    sc.frame_set(1)
    cam.data.type='PERSP';cam.data.shift_x=0;cam.data.shift_y=0
    sc.render.resolution_x=1000;sc.render.resolution_y=1000
    if view=='photo-cab':sc.render.resolution_x=1100;sc.render.resolution_y=500
    cab_view = view in ('driver','driver-controls','cockpit-detail','seat-aisle','seat-rear','cab-overview','photo-cab','entrance-dash','saloon-rear','rear-aisle-forward','rear-steps','photo-saloon','joint-forward','wheelchair-bay','rear-quarter','rear-low','rear-straight','rear-wall-inside','rear-wall-corner','rear-wall-corner-l','rear-wall-low')
    inspection_fill=bpy.data.objects.get('K18 cockpit inspection fill')
    if cab_view and inspection_fill is None:
        light=bpy.data.lights.new('K18 cockpit inspection fill','AREA');light.energy=85;light.shape='DISK';light.size=1.25
        inspection_fill=bpy.data.objects.new('K18 cockpit inspection fill',light);sc.collection.objects.link(inspection_fill)
        inspection_fill.location=u@Vector((-.45,2.45,7.0))
        inspection_fill.rotation_euler=(u@Vector((-.7,1.25,8.3))-inspection_fill.location).to_track_quat('-Z','Y').to_euler()
    if inspection_fill is not None:inspection_fill.hide_render=not cab_view
    if view in ('hero','photo-front','photo-side'):
        c=next(x for x in cal['fits'] if x['id']=={'hero':'hero','photo-front':'front','photo-side':'side'}[view])['camera']
        cam.matrix_world=Matrix(c['matrix_world_blender']);cam.data.type=c['projection']
        cam.data.lens=c['lens_mm'];cam.data.sensor_width=c['sensor_width_mm'];cam.data.sensor_fit=c['sensor_fit']
        if 'ortho_scale_m' in c:cam.data.ortho_scale=c['ortho_scale_m']
        cam.data.shift_x=c.get('shift_x',0);cam.data.shift_y=c.get('shift_y',0)
        sc.render.resolution_x=c['resolution'][0];sc.render.resolution_y=c['resolution'][1]
    else:
        pos,target,lens={'front':((0,1.72,18.8),(0,1.71,8.6),54),
                         'detail':((5.9,3.6,16.0),(0,1.68,8.0),56),
                         'lamp-close':((1.10,.82,10.35),(1.04,.80,8.86),80),
                         'saloon-rear':((0,1.70,5.95),(0,1.70,-6.5),20),
                         'rear-aisle-forward':((0,2.45,-8.20),(0,1.65,4.8),20),
                         'rear-steps':((0,1.42,-4.00),(0,1.70,-6.75),30),
                         'rear-quarter':((-5.2,2.3,-14.2),(0,1.6,-8.6),42),
                         'rear-low':((1.4,0.8,-13.0),(0,1.4,-8.8),48),
                         'rear-straight':((0,1.8,-16),(0,1.8,-8.7),46),
                         'rear-wall-inside':((0,1.9,-5.2),(0,1.9,-8.6),40),
                         'rear-wall-corner':((0,2.45,-6.6),(1.08,2.2,-8.55),50),
                         'rear-wall-corner-l':((0,2.45,-6.6),(-1.08,2.2,-8.55),50),
                         'rear-wall-low':((0,1.45,-6.2),(0,1.0,-8.6),55),
                         'rear-exterior':((-7,4,-17),(0,1.5,-6.7),52),
                         'side-door':((60,1.65,.07),(0,1.65,.07),50),
                         'side-driver':((-60,1.65,.07),(0,1.65,.07),50),
                         'side-front-door':((7.5,1.5,6.2),(0,1.5,6.2),38),
                         'side-front-driver':((-7.5,1.5,6.2),(0,1.5,6.2),38),
                         'side-joint':((7.5,1.6,-.8),(0,1.6,-.8),38),
                         'side-rear-wheel':((7.5,1.3,-5.6),(0,1.3,-5.6),38),
                         'side-rear-driver':((-7.5,1.3,-5.6),(0,1.3,-5.6),38),
                         'side-low':((9,.55,3.0),(0,.55,3.0),30),
                         'side-detail':((4.2,1.45,6.95),(0,1.45,6.95),50),
                         'doors-open':((14,5,5),(0,1.45,0),42),
                         'articulated':((16,10,18),(0,1.4,-1),46),
                         'cockpit-detail':(tuple(driver_eye+Vector((.44,.33,-.44))),tuple(driver_eye+Vector((.05,-.57,.56))),28),
                         'driver-controls':(tuple(driver_eye),tuple(driver_eye+Vector((0,-.54,.72))),24),
                         'seat-aisle':(tuple(driver_eye+Vector((1.55,-.55,-.95))),tuple(driver_eye+Vector((0,-.80,.20))),30),
                         'seat-rear':(tuple(driver_eye+Vector((.15,-.10,1.15))),tuple(driver_eye+Vector((-.05,-.30,-.30))),34),
                         'entrance-dash':((.75,1.75,6.9),(-.15,1.25,8.45),24),
                         'wheelchair-bay':((-.35,1.65,2.30),(1.05,.85,3.40),22),
                         'joint-forward':((0,1.62,-1.85),(0,1.38,8.0),18),
                         'photo-saloon':((0,1.62,-.75),(0,1.38,-8.0),18),
                         'photo-cab':(tuple(driver_eye+Vector((.05,.45,-.40))),tuple(driver_eye+Vector((.10,-.62,.85))),17),
                         'cab-overview':(tuple(driver_eye+Vector((1.45,.55,-2.15))),tuple(driver_eye+Vector((-.30,-.75,.85))),34),
                         'driver':(tuple(driver_eye),tuple(driver_eye+Vector((0,-.10,4.38))),24)}[view]
        sc.frame_set(85 if view=='doors-open' else 160 if view=='articulated' else 1)
        if view in ('side-door','side-driver'):
            cam.data.type='ORTHO';cam.data.ortho_scale=20.4;sc.render.resolution_x=2448;sc.render.resolution_y=432
        cam.location=u@Vector(pos);cam.rotation_euler=(u@Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.lens=lens
    sc.render.filepath=str(out/(view+'.png'));bpy.ops.render.render(write_still=True)
    rows.append({'view':view,'source_sha256':digest,'camera_matrix_world':[list(r) for r in cam.matrix_world],
                 'lens_mm':cam.data.lens,'type':cam.data.type,'ortho_scale_m':cam.data.ortho_scale,
                 'cockpit_inspection_fill_watts':85 if cab_view else 0,
                 'shift':[cam.data.shift_x,cam.data.shift_y],
                 'resolution':[sc.render.resolution_x,sc.render.resolution_y]})
assert sha(src)==digest,'Source changed while rendering'
(out/'manifest.json').write_text(json.dumps({'source':str(src),'source_sha256':digest,'lod':args.lod,'lamp_state':args.lamp_state,'backface_diagnostic':args.backface,'lighting':'Blender neutral studio, not game renderer','views':rows},indent=2))
print('K18_FRONT_RENDERS '+str(out),flush=True)
