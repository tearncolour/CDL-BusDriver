"""Small Blender fixture for variable SWB1 strap counts and posed exports.

Uses the real stem/grip components from a patched source, exporting only their
geometry under an output root. Never overwrites source, FBXs, or packed assets.
"""
import argparse
from collections import Counter
import gzip
import json
import math
from pathlib import Path
import struct
import sys
import bpy
from mathutils import Vector

HERE=Path(__file__).resolve().parent;ROOT=HERE.parents[1]
sys.path.insert(0,str(HERE))
from animation_rig import collect_cdlc_parts,discover_strap_groups
from write_runtime import write_cdlc_runtime
from source_coordinates import canonical_export_context,canonical_world_matrix
from lod_geometry import flatten_lod

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--blend',type=Path,required=True)
parser.add_argument('--output-root',type=Path,required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
out=args.output_root.resolve()
if ROOT/'ModelSource' in out.parents or out==ROOT:raise RuntimeError('Fixture requires isolated output root')
bpy.ops.wm.open_mainfile(filepath=str(args.blend.resolve()))
scene=bpy.context.scene;scene.frame_set(0);bpy.context.view_layer.update()
master=bpy.data.collections['SWB6109EV12G | editable components']
spec=json.loads((ROOT/'ModelSource/SWB6109EV12G/model-spec.json').read_text(encoding='utf-8'))
palette=json.loads((ROOT/'ModelSource/SWB6109EV12G/kit-cabin.json').read_text(encoding='utf-8'))['palette']
src=out/'ModelSource/SWB6109EV12G';src.mkdir(parents=True,exist_ok=True)
(src/'kit-cabin.json').write_text(json.dumps({'palette':palette}),encoding='utf-8')

def flatten(obj):
    deps=bpy.context.evaluated_depsgraph_get()
    mesh=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
    mesh.transform(canonical_world_matrix(obj));mesh.update()
    return mesh

def unpack(path):
    data=gzip.decompress(path.read_bytes())
    magic,lods,parts,*_=struct.unpack_from('<4sii3f',data)
    assert magic==b'SWB1' and lods==3
    offset=24;frames=[];geometry=[]
    for _ in range(parts):frames.append(struct.unpack_from('<ii7f',data,offset));offset+=36
    for lod in range(3):
        rows=[]
        for part in range(parts):
            n,=struct.unpack_from('<i',data,offset);offset+=4
            tris=[struct.unpack_from('<B18f',data,offset+t*73) for t in range(n)];offset+=n*73
            rows.append(tris)
        geometry.append(rows)
    assert offset==len(data)
    return frames,geometry

report={'status':'PASS','fixtures':[],'scope':'Real source straps, isolated runtime fixture; all fixed-part records deliberately empty.'}
with canonical_export_context(master):
    groups=discover_strap_groups(master)
    parts=collect_cdlc_parts(master)
    baseline={}
    for count in (0,1,3,len(groups)):
        objects=[o for p in parts[34:34+count] for name in p['objects'] for o in [master.objects[name]]]
        result=write_cdlc_runtime(objects,parts[:34+count],flatten,out,spec)
        frames,geometry=unpack(src/'sunwin-mesh.bin.gz')
        assert len(frames)==34+count and result['straps']==count
        counts=[]
        for lod,rows in enumerate(geometry):
            assert all(not row for row in rows[:34])
            lod_counts=[]
            for index,tris in enumerate(rows[34:],34):
                assert tris
                vertices=[Vector(tri[k:k+3]) for tri in tris for k in (1,7,13)]
                assert abs(max(p.y for p in vertices))<1e-5 and min(p.y for p in vertices)<-.05
                edge=Counter()
                for tri in tris:
                    keys=[tuple(round(v,6) for v in tri[k:k+3]) for k in (1,7,13)]
                    for a,b in zip(keys,keys[1:]+keys[:1]):edge[tuple(sorted((a,b)))]+=1
                assert all(n==2 for n in edge.values()),(lod,index,Counter(edge.values()))
                # Geometry reconstruction matches the actual evaluated source
                # at the same detail level, with no extra pivot translation.
                expected=[]
                for name in parts[index]['objects']:
                    mesh=flatten_lod(master.objects[name],lod,flatten)
                    mesh.calc_loop_triangles()
                    expected.extend(tuple(round(v,5) for v in (mesh.vertices[vi].co.x,mesh.vertices[vi].co.z,-mesh.vertices[vi].co.y))
                                    for tri in mesh.loop_triangles for vi in tri.vertices)
                    bpy.data.meshes.remove(mesh)
                pivot=Vector(frames[index][2:5])
                actual=[tuple(round(v,5) for v in p+pivot) for p in vertices]
                assert Counter(actual)==Counter(expected),(lod,index,'rest reconstruction differs')
                lod_counts.append(len(tris))
            counts.append(lod_counts)
        if count==3:baseline=geometry
        report['fixtures'].append({'count':count,'part_count':len(frames),'lod_triangles':counts,'closed':True,'rest_reconstruction':True})
    # A posed export must undo the source pivot and serialize rest-local mesh.
    scene.frame_set(12);bpy.context.view_layer.update()
    posed_parts=collect_cdlc_parts(master)
    objects=[master.objects[name] for p in posed_parts[34:37] for name in p['objects']]
    write_cdlc_runtime(objects,posed_parts[:37],flatten,out,spec)
    _,posed_geometry=unpack(src/'sunwin-mesh.bin.gz')
    errors=[max(abs(ta[k]-tb[k]) for l in range(3) for p in range(34,37)
                for ta,tb in zip(baseline[l][p],posed_geometry[l][p]) for k in fields)
            for fields in ((1,2,3,7,8,9,13,14,15),(4,5,6,10,11,12,16,17,18))]
    normal_angle=max(math.degrees(Vector(ta[k:k+3]).angle(Vector(tb[k:k+3])))
                     for l in range(3) for p in range(34,37)
                     for ta,tb in zip(baseline[l][p],posed_geometry[l][p]) for k in (4,10,16))
    # Tiny rounded grip faces amplify single-precision world-space normal
    # recomputation. Keep a strict metre-space test and bound direction error
    # in degrees instead of comparing individual normal components.
    assert errors[0]<3e-6 and normal_angle<.15,('posed export drift',errors,normal_angle)
    report['fixtures'].append({'frame':12,'count':3,'max_rest_position_error_m':errors[0],
                              'max_rest_normal_component_error':errors[1],
                              'max_rest_normal_angle_degrees':normal_angle,
                              'position_tolerance_m':3e-6,'normal_tolerance_degrees':.15})
    scene.frame_set(0);bpy.context.view_layer.update()
    # A regressed static assignment must fail explicitly rather than silently
    # exporting motionless handles in Poles again.
    target=groups[0]['stem'];old=target['cdlc_part'];target['cdlc_part']=13
    try:
        try:write_cdlc_runtime([target],parts,flatten,out,spec)
        except ValueError as exc:assert 'prepare_strap_rig' in str(exc)
        else:raise AssertionError('Static strap assignment was accepted')
    finally:target['cdlc_part']=old
    report['fixtures'].append({'static_assignment_rejected':True})
(out/'strap-export-fixture.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print('STRAP_EXPORT_FIXTURE_PASS '+json.dumps(report))
