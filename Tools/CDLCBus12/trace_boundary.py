"""Trace a failing review pixel and nearby rays against the saved complete assembly."""
import argparse,json,sys,hashlib
from pathlib import Path
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ap=argparse.ArgumentParser();ap.add_argument('--blend',type=Path,required=True);ap.add_argument('--views',type=Path,required=True);ap.add_argument('--out',type=Path,required=True)
a=ap.parse_args(sys.argv[sys.argv.index('--')+1:]);bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()))
view=next(v for v in json.loads(a.views.read_text())['views'] if v['image'].endswith('LOD0-rear-low.png'))
bpy.context.scene.frame_set(view['frame']);bpy.context.view_layer.update();verts=[];faces=[];owners=[]
for ob in bpy.data.collections['LOD0'].all_objects:
    if ob.type!='MESH' or ob.get('bake')=='only':continue
    mesh=ob.data;mesh.calc_loop_triangles();offset=len(verts);verts.extend(ob.matrix_world@v.co for v in mesh.vertices)
    for t in mesh.loop_triangles:
        faces.append(tuple(offset+i for i in t.vertices));owners.append((ob.name,t.polygon_index,mesh.materials[t.material_index].name))
tree=BVHTree.FromPolygons(verts,faces,all_triangles=True);origin=Vector(view['camera']);q=(Vector(view['target'])-origin).to_track_quat('-Z','Y')
rows=[]
for shift in (0.,-.001,.001,-.01,.01,-.1,.1):
    x,y=7+shift,21;half=.5*36/view['lens_mm'];aspect=view['resolution'][0]/view['resolution'][1]
    direction=q@Vector((((x+.5)/80*2-1)*half,(1-(y+.5)/60*2)*half/aspect,-1)).normalized();start=origin+direction*.025;hits=[]
    for i in range(10):
        p,n,index,distance=tree.ray_cast(start,direction,100)
        if p is None:break
        hits.append({'point':list(p),'normal':list(n),'dot':n.dot(direction),'owner':owners[index],'triangle':index,'vertices':[list(verts[j]) for j in faces[index]]});start=p+direction*.00002
    rows.append({'pixel_offset':shift,'direction':list(direction),'hits':hits})
a.out.write_text(json.dumps({'source_sha256':hashlib.sha256(a.blend.read_bytes()).hexdigest(),'view':view,'rays':rows},indent=2))
print(json.dumps(rows[0],indent=2))
