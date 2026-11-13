"""Locate statistical export outliers on the unchanged source assembly for human review."""
import collections,hashlib,json,sys
from pathlib import Path
import bpy,numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

native,evidence,out=map(Path,sys.argv[-3:]);export=json.loads((native/'export-summary.json').read_text());analysis=json.loads(evidence.read_text())
if analysis['hard_failures']:raise ValueError('Resolve deterministic failures before advisory review')
source=Path(export['source']);digest=hashlib.sha256(source.read_bytes()).hexdigest()
if digest!=export['source_sha256']:raise ValueError('Source changed after export')
bpy.ops.wm.open_mainfile(filepath=str(source));bpy.context.scene.frame_set(0);result=[]
for row in analysis['meshes']:
    lod=row['lod'];glass=row['glass']
    for i in range(3):bpy.data.collections['LOD%d'%i].hide_viewport=i!=lod
    bpy.context.view_layer.update();deps=bpy.context.evaluated_depsgraph_get()
    obs=[o for o in (bpy.data.collections['CDLCBus12 source'].objects if glass else bpy.data.collections['LOD%d'%lod].all_objects)
         if o.type=='MESH' and o.get('bake')!='only' and (not glass or any(m.get('glass') for m in o.data.materials))]
    vertices=[];faces=[];owners=[]
    for ob in obs:
        evaluated=ob.evaluated_get(deps);mesh=evaluated.to_mesh();mesh.calc_loop_triangles();offset=len(vertices);vertices.extend(ob.matrix_world@v.co for v in mesh.vertices)
        for t in mesh.loop_triangles:
            faces.append(tuple(offset+i for i in t.vertices));owners.append((ob.name,t.index,mesh.materials[t.material_index].name))
        evaluated.to_mesh_clear()
    tree=BVHTree.FromPolygons(vertices,faces,all_triangles=True)
    mesh=next(m for m in export['meshes'] if m['mesh_name']==row['mesh']);corners=np.load(mesh['corner_contract']['path'])['corners'];locations=[]
    for anomaly in row['anomalies']['top_triangles']:
        ti=anomaly['triangle_index'];p=corners[ti,:,:3];centre=p.mean(axis=0);hit,normal,index,distance=tree.find_nearest(Vector(centre))
        locations.append(dict(anomaly,centroid_blender_m=centre.tolist(),vertices=p.tolist(),source_object=owners[index][0],source_triangle=owners[index][1],material=owners[index][2],source_distance_m=distance))
    result.append({'mesh':row['mesh'],'lod':lod,'glass':glass,'flagged_count':row['anomalies']['flagged_count'],'measurements':row['measurements'],
        'top_source_groups':dict(collections.Counter(v['source_object'] for v in locations)),'locations':locations})
out.write_text(json.dumps({'source_sha256':digest,'analysis_sha256':hashlib.sha256(evidence.read_bytes()).hexdigest(),'meshes':result,'scope':'Advisory locations only; source/image inspection is separate and no deterministic threshold is relaxed.'},indent=2))
print(json.dumps([{'mesh':r['mesh'],'groups':r['top_source_groups'],'measurements':r['measurements']} for r in result]))
