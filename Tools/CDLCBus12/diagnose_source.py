import argparse,json,sys
from pathlib import Path
import bpy
from mathutils import Vector

p=argparse.ArgumentParser();p.add_argument('--blend',required=True);p.add_argument('--out',type=Path,required=True)
a=p.parse_args(sys.argv[sys.argv.index('--')+1:]);bpy.ops.wm.open_mainfile(filepath=a.blend)
rows=[]
for ob in bpy.data.objects:
    if ob.type!='MESH':continue
    me=ob.data;me.calc_loop_triangles()
    for t in me.loop_triangles:
        normals=[me.corner_normals[l].vector for l in t.loops]
        if any(n.length_squared<1e-12 or n.dot(t.normal)<-1e-6 for n in normals):
            rows.append({'object':ob.name,'triangle':t.index,'vertices':[list(me.vertices[i].co) for i in t.vertices],
              'normals':[list(n) for n in normals],'face_normal':list(t.normal),'area':t.area})
a.out.write_text(json.dumps(rows,indent=2),encoding='utf-8');print('BAD_TRIANGLES',len(rows),rows[:2])
eye=Vector((.62,-4.6,1.92));rotation=Vector((0,-4,-.3)).to_track_quat('-Z','Y');rays=[]
for x,y in ((260,944),(270,930),(250,950)):
    direction=rotation@Vector(((x/1280-.5)*36/26,(.5-y/960)*27/26,-1)).normalized();origin=eye.copy();hits=[]
    for _ in range(8):
        hit,point,normal,face,ob,matrix=bpy.context.scene.ray_cast(bpy.context.evaluated_depsgraph_get(),origin,direction)
        if not hit:break
        poly=ob.data.polygons[face] if face<len(ob.data.polygons) else None
        hits.append({'object':ob.name,'point':list(point),'normal':list(normal),'face':face,'material':ob.data.materials[poly.material_index].name if poly else None})
        origin=point+direction*.00002
    rays.append({'pixel':[x,y],'hits':hits})
a.out.with_name(a.out.stem+'-pillar-rays.json').write_text(json.dumps(rays,indent=2));print('PILLAR_RAYS',rays)
