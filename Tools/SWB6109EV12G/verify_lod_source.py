"""Check closure and finite geometry after parametric LOD evaluation."""
import bpy
import bmesh
import hashlib
import json
import math
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lod_geometry import flatten_lod
from source_coordinates import canonical_world_matrix

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
source=SRC/'SWB6109EV12G.blend'
source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source))
bpy.context.scene.frame_set(0)
master=bpy.data.collections['SWB6109EV12G | editable components']

def evaluate(obj):
    deps=bpy.context.evaluated_depsgraph_get()
    data=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
    data.transform(canonical_world_matrix(obj))
    return data

def metrics(data):
    data.calc_loop_triangles()
    degenerate=sum((data.vertices[t.vertices[1]].co-data.vertices[t.vertices[0]].co).cross(
        data.vertices[t.vertices[2]].co-data.vertices[t.vertices[0]].co).length_squared<1e-18
        for t in data.loop_triangles)
    finite=all(math.isfinite(c) for v in data.vertices for c in v.co)
    bm=bmesh.new();bm.from_mesh(data)
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
    boundary=sum(e.is_boundary for e in bm.edges)
    nonmanifold=sum(len(e.link_faces)>2 for e in bm.edges)
    bm.free()
    return {'triangles':len(data.loop_triangles),'degenerate':degenerate,'finite':finite,
            'boundary':boundary,'nonmanifold':nonmanifold}

errors=[];rows=[];totals=[0,0,0]
for obj in master.objects:
    if obj.type not in {'MESH','CURVE'}:continue
    levels=[]
    for lod in range(3):
        data=flatten_lod(obj,lod,evaluate);row=metrics(data);bpy.data.meshes.remove(data)
        levels.append(row);totals[lod]+=row['triangles']
        if not row['finite'] or row['degenerate']>levels[0]['degenerate']:
            errors.append(f'{obj.name} LOD{lod}: invalid geometry')
        if row['boundary']>levels[0]['boundary'] or row['nonmanifold']>levels[0]['nonmanifold']:
            errors.append(f'{obj.name} LOD{lod}: introduced open/non-manifold edges')
        part=int(obj.get('cdlc_part',-1))
        if (part in range(15) or part>=34) and row['boundary']:
            errors.append(f'{obj.name} LOD{lod}: open interior solid')
    if levels[0]!=levels[1] or levels[0]!=levels[2]:
        rows.append({'object':obj.name,'part':int(obj.get('cdlc_part',-1)),'lods':levels})
if source_hash!=hashlib.sha256(source.read_bytes()).hexdigest():
    errors.append('Saved source changed during read-only validation')
report={'status':'FAIL' if errors else 'PASS','source_sha256':source_hash,
        'scope':'Parametric edge/tube LOD evaluation, before whole-object omissions and legacy exterior decimation; exported runtime validation remains required.',
        'triangles_before_omissions':totals,'changed_objects':rows,'errors':errors}
(SRC/'lod-source-validation.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k!='changed_objects'}))
if errors:raise RuntimeError('Parametric LOD introduced geometry errors')
