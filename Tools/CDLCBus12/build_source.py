"""Create a reviewable Blender source from the decoded generic SWB2 export, with exact part frames."""
import argparse, hashlib, json, math, shutil, sys
from pathlib import Path
import bpy
import bmesh
import numpy as np
from mathutils import Matrix, Quaternion, Vector

DEVKIT=Path.home()/'Desktop/CDLDevKit'
sys.path.insert(0,str(DEVKIT/'blender'))
import pattern_tiles
import fine
sys.path.insert(0,str(Path(__file__).resolve().parent))
from lamp_layers import light_layer

U2B=Matrix(((-1,0,0),(0,0,-1),(0,1,0)))
def bpoint(p):return U2B@Vector(p)
def linear(c):return c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--kit',type=Path,required=True);ap.add_argument('--data',type=Path,required=True)
    ap.add_argument('--bakes',type=Path,required=True);ap.add_argument('--out',type=Path,required=True)
    a=ap.parse_args(sys.argv[sys.argv.index('--')+1:]);a.out=a.out.resolve();a.out.mkdir(parents=True,exist_ok=False)
    kit=json.loads(a.kit.read_text());layout=json.loads((a.data/'layout.json').read_text());frames=json.loads((a.data/'frames.json').read_text())
    detail_lods=[{p['index']:p['uv'] for p in lod} for lod in json.loads((a.data/'detail-uv.json').read_text())]
    bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
    scene=bpy.context.scene;scene.unit_settings.system='METRIC';scene.render.engine='CYCLES';scene.cycles.samples=24
    scene.render.resolution_x=1440;scene.render.resolution_y=1080;scene.render.resolution_percentage=100
    scene.world.use_nodes=True
    background=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
    background.inputs['Color'].default_value=(.55,.72,1,1);background.inputs['Strength'].default_value=.5
    scene.view_settings.view_transform='AgX'
    section=bpy.data.collections.new('CDLCBus12 source');scene.collection.children.link(section)
    root=bpy.data.objects.new('CDLCBus12 origin',None);section.objects.link(root);root['driver_eye_unity']=layout['cameraEye']
    root['formal_eye_unity']=layout['eye'];root['design_provenance']='Original generic CDLC 12 m bus; no claim of a specific production model.'
    scene['cdlc_vehicle']=json.dumps({'model':'CDLCBus12','sections':[{'id':'single','collection':section.name,'root':root.name,'prefab':'CDLCBus12'}]})
    scene['cdlc_bake_settings']=json.dumps({'sizes':[1024],'lod2_sizes':[512]})
    scene['review_lighting']='Uniform sky at strength .5, studio area lights, three 20 W interior fills; offline source preview, not game rendering.'
    scene['coordinate_mapping']='Unity = (-Blender.x, Blender.z, -Blender.y); driver +X in Blender, doors -X.'
    scene['normal_bake_source']=str((a.bakes/'SurfaceBakeSources.blend').resolve())
    pivots={};rest={}
    for f in frames:
        p=bpy.data.objects.new('Part %02d %s'%(f['index'],f['name']),None);section.objects.link(p);p.location=bpoint(f['pivot'])
        p['cdlc_part']=f['index'];p['rest_pivot_unity']=f['pivot'];p['rest_rotation_unity']=f['rotation'];p.empty_display_size=.04
        pivots[f['index']]=p;rest[f['index']]=p.location.copy()
    maps={};packs={}
    for tile in ('polymer','woven','brushed','leather'):
        im=bpy.data.images.load(str((a.bakes/(tile+'-Normal.png')).resolve()),check_existing=True);im.colorspace_settings.name='Non-Color';im.pack();maps[tile]=im
        pack=a.out/'patterns'/tile;pack.mkdir(parents=True);shutil.copyfile(a.bakes/(tile+'-Normal.png'),pack/'Normal.png')
        (pack/'pattern.json').write_text(json.dumps({'period_m':[.08,.08],'baked_source':str((a.bakes/'SurfaceBakeSources.blend').resolve())}))
        packs[tile]=pack
    palette=kit['palette'];materials={}
    def material(row,part,height=0):
        record=palette[row];name=record['name'];fin=record['finish']
        layer=light_layer(part,fin,height)
        key=(row,layer,part==18,part==1)
        if key in materials:return materials[key]
        m=bpy.data.materials.new(name+(' light%d'%layer if layer else '')+(' livery' if part==18 else ''));m.use_nodes=True
        m['cdlc_swatch']=name;m['light_id']=layer;m['livery_control']=part==18
        bs=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED');color=tuple(linear(c) for c in record['rgb'])+(1,)
        m.diffuse_color=color;bs.inputs['Base Color'].default_value=color
        bs.inputs['Metallic'].default_value=record['metallic'];bs.inputs['Roughness'].default_value=record['roughness']
        tile='woven' if fin=='Textile' else 'brushed' if fin in ('Metal','BrushedMetal') else 'polymer' if fin in ('Matte','Satin','MouldedPlastic') else None
        if part==1 and tile=='polymer':tile='leather'
        if tile=='leather':bs.inputs['Roughness'].default_value=.56
        if tile and not layer and part!=18:
            nt=m.node_tree;uv=nt.nodes.new('ShaderNodeUVMap');uv.uv_map='DetailUV'
            tex=nt.nodes.new('ShaderNodeTexImage');tex.image=maps[tile];tex.extension='REPEAT';nt.links.new(uv.outputs['UV'],tex.inputs['Vector'])
            normal=nt.nodes.new('ShaderNodeNormalMap');normal.uv_map='DetailUV';nt.links.new(tex.outputs['Color'],normal.inputs['Color']);nt.links.new(normal.outputs['Normal'],bs.inputs['Normal'])
            pattern_tiles.declare(m,packs[tile],lods=(0,))
        materials[key]=m;return m
    lodcols=[];counts=[]
    fixed={'Chrome':'Chrome','Lamp':'LampWhite','Tail':'LampRed','Beacon':'Amber','Glow':'LedAmber','Hazard':'Yellow'}
    byname={r['name']:r['index'] for r in palette}
    for lodrow in kit['lods']:
        lod=lodrow['lod'];col=bpy.data.collections.new('LOD%d'%lod);section.children.link(col);lodcols.append(col);total=0
        for p in lodrow['parts']:
            index=p['index']
            if 29<=index<=33:continue
            verts=np.asarray(p['positions'],dtype=float);normals=np.asarray(p['normals'],dtype=float);faces=np.asarray(p['indices']).reshape(-1,3)
            posed=np.array(p['partToModel']).reshape(4,4).T
            f=frames[index];q=f['rotation'];rr=np.asarray(Quaternion((q[3],*q[:3])).to_matrix());origin=np.asarray(f['pivot'])
            local=(verts-posed[:3,3])@posed[:3,:3];verts=local@rr.T+origin
            normals=normals@posed[:3,:3]@rr.T
            # The coordinate map reverses handedness; reverse triangle order once.
            vb=verts@np.asarray(U2B).T;nb=normals@np.asarray(U2B).T;fb=faces[:,::-1]
            triangle_ids=np.arange(len(faces))
            if index==12:
                xyz=vb[fb]
                selected=np.all((xyz[:,:,0]>.415)&(xyz[:,:,0]<.825)&(xyz[:,:,1]>-5.182)&(xyz[:,:,1]<-4.878)&(xyz[:,:,2]>.6999)&(xyz[:,:,2]<.7041),axis=1)
                if int(np.count_nonzero(selected))!=12:raise ValueError('Heel mat extraction must identify one complete closed box')
                heel_relief(col,lod,vb,fb[selected],material(byname['Rubber'],12))
                triangle_ids=triangle_ids[~selected];fb=fb[~selected];faces=faces[~selected]
            mesh=bpy.data.meshes.new(p['name']+' LOD%d'%lod);mesh.from_pydata(vb.tolist(),[],fb.tolist());mesh.update()
            ob=bpy.data.objects.new(mesh.name,mesh);col.objects.link(ob);ob.parent=pivots[index];ob.matrix_parent_inverse=Matrix.Identity(4)
            # parent transforms are identity except the declared pivot translation.
            ob.location=-rest[index];ob['cdlc_part']=index;ob['first_lod']=ob['last_lod']=lod
            if index in (19,20,21,22):ob['bone']={19:'WheelFL',20:'WheelFR',21:'WheelRL',22:'WheelRR'}[index]
            for poly in mesh.polygons:poly.use_smooth=True
            mesh.normals_split_custom_set([nb[v].tolist() for face in fb for v in face])
            base=mesh.uv_layers.new(name='PaletteUV');detail=mesh.uv_layers.new(name='DetailUV');uv=np.asarray(p['uv'])
            finishes=np.empty(len(p['indices'])//3,dtype=object)
            for sub in p['submeshes']:finishes[sub['start']//3:(sub['start']+sub['count'])//3]=sub['finish']
            used={}
            for ti,(orig,face) in enumerate(zip(faces,fb)):
                fin=finishes[triangle_ids[ti]];row=byname[fixed[fin]] if fin in fixed else min(len(palette)-1,int(math.floor(uv[orig[0],1]*len(palette))))
                m=material(row,index,float(np.mean(vb[face,2])))
                if m.name not in used:used[m.name]=len(mesh.materials);mesh.materials.append(m)
                mesh.polygons[ti].material_index=used[m.name]
                # Exported by the exact runtime UV generator, including the closed wheel unwrap.
                for k,vi in enumerate(face):
                    base.data[ti*3+k].uv=uv[vi];detail.data[ti*3+k].uv=detail_lods[lod][index][vi]
            total+=len(faces)
        counts.append(total+12)
    glass=bpy.data.materials.new('Separate vehicle glass');glass.use_nodes=True;glass['glass']=True;glass['glass_alpha']=.22
    glass.diffuse_color=(.47,.62,.70,.22);bs=next(n for n in glass.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    bs.inputs['Base Color'].default_value=(.94,.98,1,1);bs.inputs['Roughness'].default_value=.008;bs.inputs['Transmission Weight'].default_value=1;bs.inputs['IOR'].default_value=1.45
    glass['preview_limitation']='4 mm glazing in source. Native shader uses separately exported glass maps.'
    grouped={}
    for quad,part in zip(layout['windows'],layout['windowParts']):grouped.setdefault(part,[]).append(quad)
    for part,quads in grouped.items():
        vs=[];fs=[]
        for quad in quads:
            start=len(vs);vs.extend([bpoint(v) for v in quad]);ids=[start+i for i in range(4)]
            centre=sum((Vector(v) for v in quad),Vector())/4
            outward=Vector((math.copysign(1,centre.x),0,0)) if abs(centre.x)>1 else Vector((0,0,math.copysign(1,centre.z)))
            n=(vs[ids[1]]-vs[ids[0]]).cross(vs[ids[2]]-vs[ids[0]])
            fs.append(ids if n.dot(bpoint(outward))>0 else ids[::-1])
        me=bpy.data.meshes.new('Glass part%d'%part);me.from_pydata(vs,[],fs);me.materials.append(glass);me.update()
        ob=bpy.data.objects.new(me.name,me);section.objects.link(ob);ob.parent=pivots[part];ob.location=-rest[part];ob['cdlc_part']=part
        bm=bmesh.new();bm.from_mesh(me);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6);bm.to_mesh(me);bm.free()
        solid=ob.modifiers.new('Four millimetre pane','SOLIDIFY');solid.thickness=.004;solid.offset=-1;solid.use_even_offset=True
    studio=bpy.data.collections.new('Review studio');scene.collection.children.link(studio)
    def light(name,pos,power,size,target):
        data=bpy.data.lights.new(name,'AREA');data.energy=power;data.shape='DISK';data.size=size
        ob=bpy.data.objects.new(name,data);studio.objects.link(ob);ob.location=pos;ob.rotation_euler=(Vector(target)-ob.location).to_track_quat('-Z','Y').to_euler()
    light('Daylight key',(5,-6,9),1800,7,(0,0,1));light('Daylight fill',(-5,2,6),1400,6,(0,0,1));light('Rear daylight',(0,9,7),1600,5,(0,0,1))
    for y in (-4,0,4):light('Interior review fill '+str(y),(0,y,2.6),20,1.6,(0,y,.4))
    bpy.ops.mesh.primitive_plane_add(size=150,location=(0,0,-.02));ground=bpy.context.object;ground.name='Review ground (not exported)'
    for c in list(ground.users_collection):c.objects.unlink(ground)
    studio.objects.link(ground);gm=bpy.data.materials.new('Review ground');gm.diffuse_color=(.22,.25,.29,1);ground.data.materials.append(gm)
    for lod,col in enumerate(lodcols):col.hide_render=col.hide_viewport=lod!=0
    bpy.context.view_layer.update();scene.frame_start=0;scene.frame_end=100
    for pose in json.loads((a.data/'door-motion.json').read_text())+json.loads((a.data/'strap-motion.json').read_text()):
        for part in pose['parts']:
            ob=pivots[part['part']];q=part['rotation'];ob.rotation_mode='QUATERNION'
            ob.location=bpoint(part['position']);ob.rotation_quaternion=(U2B@Quaternion((q[3],*q[:3])).to_matrix()@U2B.inverted()).to_quaternion()
            ob.keyframe_insert('location',frame=pose['frame']);ob.keyframe_insert('rotation_quaternion',frame=pose['frame'])
    for ob in pivots.values():
        if not ob.animation_data:continue
        action=ob.animation_data.action;curves=list(getattr(action,'fcurves',[]))
        for layer in getattr(action,'layers',[]):
            for strip in layer.strips:
                for bag in getattr(strip,'channelbags',[]):curves.extend(bag.fcurves)
        for fc in curves:
            for key in fc.keyframe_points:key.interpolation='LINEAR'
    scene.frame_set(0)
    path=a.out/'CDLCBus12.blend';bpy.ops.wm.save_as_mainfile(filepath=str(path))
    report={'source':str(path),'sha256':digest(path),'kit_sha256':digest(a.kit),'runtime_sha256':digest(a.data/'CDLCBus12.swb2.gz'),
      'lod_triangles_without_wrap':counts,'source_axes':'driver +X, doors -X, front -Y, up +Z',
      'native_pattern_policy':'Steering wheel, dashboard and cab (parts 1, 11, 12), LOD0 preserves runtime detail UV in bordered atlas cells. Other native parts and LOD1 / LOD2 omit these micro-surface tiles. Heel relief is separately projected; SWB2 retains detail UV on all eligible finishes.',
      'source_normal_maps':[{'path':str(a.bakes/(t+'-Normal.png')),'sha256':digest(a.bakes/(t+'-Normal.png'))} for t in maps],
      'game_validated':False,'review_lighting':scene['review_lighting']}
    (a.out/'source-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print('SOURCE_SAVED',path,counts,flush=True)

def heel_relief(collection,lod,vertices,faces,material):
    used=sorted(set(int(i) for face in faces for i in face));lookup={old:i for i,old in enumerate(used)}
    lowverts=vertices[used];lowfaces=[tuple(lookup[int(i)] for i in f) for f in faces]
    me=bpy.data.meshes.new('Heel mat LOD%d'%lod);me.from_pydata(lowverts.tolist(),[],lowfaces);me.materials.append(material);me.update()
    uv=me.uv_layers.new(name='DetailUV')
    for loop in me.loops:uv.data[loop.index].uv=(-lowverts[loop.vertex_index,0]/.08,-lowverts[loop.vertex_index,1]/.08)
    ob=bpy.data.objects.new(me.name,me);collection.objects.link(ob);ob['cdlc_part']=12;ob['first_lod']=ob['last_lod']=lod
    ztop=float(lowverts[:,2].max());top=lowverts[np.abs(lowverts[:,2]-ztop)<1e-6];xmin,ymin=top[:,:2].min(axis=0);xmax,ymax=top[:,:2].max(axis=0)
    nx,ny=128,96;vs=[];fs=[]
    for j in range(ny+1):
        for i in range(nx+1):
            u,v=i/nx,j/ny;x=xmin+(xmax-xmin)*u;y=ymin+(ymax-ymin)*v
            fade=min(1,min(u,1-u,v,1-v)*20);fade=fade*fade*(3-2*fade)
            height=.00012*fade*math.sin(2*math.pi*20*u)*math.sin(2*math.pi*15*v)
            vs.append((x,y,ztop+height))
    for j in range(ny):
        for i in range(nx):
            k=j*(nx+1)+i;fs.append((k,k+1,k+nx+2,k+nx+1))
    # Complete high receiver: the unchanged low side/bottom faces keep the projection closed.
    offset=len(vs);vs.extend(lowverts.tolist())
    for f in lowfaces:
        if not all(abs(lowverts[i,2]-ztop)<1e-6 for i in f):fs.append(tuple(offset+i for i in f))
    hm=bpy.data.meshes.new('Heel mat high LOD%d'%lod);hm.from_pydata(vs,[],fs);hm.materials.append(material);hm.update()
    high=bpy.data.objects.new(hm.name,hm);collection.objects.link(high);high['cdlc_part']=12;high['first_lod']=high['last_lod']=lod
    fine.detail(high,.00013,targets=(ob.name,),lods=(lod,),mode='bake_only',replaces_host=True,feature_size_m=.01,
                reason='0.12 mm anti-slip heel mat relief; complete high includes low side and bottom surfaces.')
    high['cdlc_detail_family']='Heel mat relief';high.hide_render=True;high.hide_set(True)

if __name__=='__main__':main()
