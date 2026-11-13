"""Re-export a saved master after protecting its authored interior LOD topology.

This does not reconstruct the vehicle. MASTER geometry is hashed before/after;
only preserve_lod flags, derived export meshes and reports may change.
"""
import ast
from array import array
import hashlib
import json
import math
from pathlib import Path
import struct
import sys
import bpy
from mathutils import Matrix,Vector

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G';OUT=ROOT/'Asset/SWB6109EV12G'
source=SRC/'SWB6109EV12G.blend';before_file_hash=hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source))
scene=bpy.context.scene;scene.frame_set(0);bpy.context.view_layer.update()
MASTER=bpy.data.collections['SWB6109EV12G | editable components']
SPEC=json.loads((SRC/'model-spec.json').read_text(encoding='utf-8'))
L,W=SPEC['length_m'],SPEC['width_m'];HALF,SIDE=L/2,W/2
AXLES=[-HALF+SPEC['front_overhang_m'],HALF-SPEC['rear_overhang_m']]
R=SPEC['wheel_radius_m'];ROOF=SPEC['body_roof_m'];QUICK=True

def master_geometry_hash():
    h=hashlib.sha256()
    for ob in sorted(MASTER.objects,key=lambda x:x.name):
        h.update((ob.name+'|'+ob.type+'|'+(ob.parent.name if ob.parent else '')).encode())
        h.update(struct.pack('<16f',*[v for row in ob.matrix_world for v in row]))
        if ob.type=='MESH':
            data=ob.data
            a=array('f',[0])*(len(data.vertices)*3);data.vertices.foreach_get('co',a);h.update(a.tobytes())
            a=array('i',[0])*len(data.loops);data.loops.foreach_get('vertex_index',a);h.update(a.tobytes())
            a=array('i',[0])*len(data.polygons);data.polygons.foreach_get('loop_total',a);h.update(a.tobytes())
        elif ob.type=='FONT':
            h.update(json.dumps([ob.data.body,ob.data.size,ob.data.extrude,ob.data.bevel_depth],ensure_ascii=False).encode())
        elif ob.type=='CURVE':
            for spline in ob.data.splines:
                h.update(str((spline.type,spline.use_cyclic_u)).encode())
                for point in spline.points:
                    h.update(struct.pack('<4f',*point.co))
                for point in spline.bezier_points:
                    h.update(struct.pack('<9f',*point.co,*point.handle_left,*point.handle_right))
    return h.hexdigest()

before_geometry=master_geometry_hash();changed=[]
for ob in MASTER.objects:
    part=int(ob.get('cdlc_part',-1))
    if ob.type in {'MESH','CURVE','FONT'} and (part in range(15) or part in (19,20,21,22) or ob.type=='FONT'):
        if not ob.get('preserve_lod'):changed.append(ob.name)
        ob['preserve_lod']=True
        if part in (19,20,21,22):
            if 'fine tread ring' in ob.name.lower():ob['cdlc_last_lod']=0
            elif 'wheel nut' in ob.name.lower():ob['cdlc_last_lod']=1

# Load reusable helpers without executing the main script's scene constructor.
main=ast.parse(Path(__file__).with_name('build_model.py').read_text(encoding='utf-8'))
exec(compile(ast.Module(body=[n for n in main.body if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef))],type_ignores=[]),str(Path(__file__).with_name('build_model.py')),'exec'),globals())
exterior=Path(__file__).with_name('exterior.py').read_text(encoding='utf-8')
exec(exterior.split('end_surface();end_surface(True)')[0],globals())
exec(Path(__file__).with_name('animation_rig.py').read_text(encoding='utf-8'),globals())
exec(Path(__file__).with_name('export_helpers.py').read_text(encoding='utf-8'),globals())
exec(Path(__file__).with_name('write_runtime.py').read_text(encoding='utf-8'),globals())
from source_coordinates import sync_coordinate_metadata,canonical_export_context,source_root
if source_root(MASTER) is None:
    raise RuntimeError('Migrate the saved source to physical left-hand drive before re-exporting')
strap_report=prepare_strap_rig(MASTER,scene)
# Appending motion pivots changes parenting, but not the authored geometry.
# The strap patch has its own world-space invariance audit; export invariance
# starts after the idempotent rig step.
before_geometry=master_geometry_hash()
white=bpy.data.materials['01 Shanghai white body | CDLC line colour'];plastic=bpy.data.materials['06 Cabin graphite']
source_objects=[o for o in MASTER.objects if o.type in {'MESH','CURVE','FONT'}]
MATERIALS=sorted({m for ob in source_objects for m in ob.data.materials if m is not None},key=lambda m:m.name)

for old in list(bpy.data.collections):
    if old.name.startswith('Export LOD meshes | hidden in render'):
        for ob in list(old.objects):
            data=ob.data;bpy.data.objects.remove(ob,do_unlink=True)
            if data and data.users==0 and isinstance(data,bpy.types.Mesh):bpy.data.meshes.remove(data)
        bpy.data.collections.remove(old)
current_collection=MASTER
print('CACHED_REEXPORT_START '+json.dumps({'base_sha256':before_file_hash,'protected_objects':len(changed)}),flush=True)
with canonical_export_context(MASTER):
    runtime_parts=collect_cdlc_parts(MASTER)
    rig_report=json.loads(scene.get('cdlc_rig_report','{}'))
    rig_report['parts']=runtime_parts
    rig_report['straps']=strap_report
    rig_report['part_object_counts']={str(p['index']):len(p['objects']) for p in runtime_parts}
    (SRC/'animation-report.json').write_text(json.dumps(rig_report,indent=2)+'\n',encoding='utf-8')
    scene['cdlc_rig_report']=json.dumps({k:v for k,v in rig_report.items() if k!='parts'})
    if any(o.get('cdlc_runtime_generated')=='CardReader' for o in source_objects) and 'CardReader' not in scene.get('kit_reuse',''):
        scene['kit_reuse']=scene.get('kit_reuse','')+', CardReader'
    runtime_report=write_cdlc_runtime(source_objects,runtime_parts,flatten_object,ROOT,SPEC)
    body_objects=[o for o in source_objects if not any(m and m.get('glass') for m in o.data.materials)]
    glass_objects=[o for o in source_objects if any(m and m.get('glass') for m in o.data.materials)]
    body_mats=[m for m in MATERIALS if not m.get('glass')];glass_mats=[m for m in MATERIALS if m.get('glass')]
    LOD=bpy.data.collections.new('Export LOD meshes | hidden in render');scene.collection.children.link(LOD);current_collection=LOD
    stats=[]
    for lod,ratio in enumerate(SPEC['runtime_lod_ratios']):
        name='SWB6109EV12G'+(f'_LOD{lod}' if lod else '');row={'lod':lod}
        for isglass,objects,mats in ((False,body_objects,body_mats),(True,glass_objects,glass_mats)):
            nm=name+('_Gls' if isglass else '');ob=merged(nm,objects,mats,1. if isglass else ratio)
            row['glass_triangles' if isglass else 'body_triangles']=len(ob.data.polygons)
            write_atlas(nm,mats);export_fbx(ob,nm);ob.hide_render=True;ob.hide_set(True)
        stats.append(row)
LOD.hide_render=True;scene.frame_set(0);bpy.context.view_layer.update()
after_geometry=master_geometry_hash()
if before_geometry!=after_geometry:raise RuntimeError('Cached export altered MASTER geometry')
scene['cached_reexport_base_sha256']=before_file_hash
sync_coordinate_metadata(SRC)
bpy.ops.wm.save_as_mainfile(filepath=str(source))
report={'status':'PASS','base_master_sha256':before_file_hash,'source_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),
        'master_geometry_sha256_before':before_geometry,'master_geometry_sha256_after':after_geometry,
        'master_geometry_unchanged':True,'protected_objects':changed,'lods':stats,'runtime_bytes':runtime_report['bytes'],
        'changes':'preserve_lod metadata and regenerated derived meshes/atlases only; MASTER coordinates/topology/transforms unchanged'}
(SRC/'cached-reexport-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(SRC/'build-report.json').write_text(json.dumps({'model':'SWB6109EV12G','lods':stats,'editable_objects':len(source_objects),
    'kit_reused':[name.strip() for name in scene.get('kit_reuse','').split(',') if name.strip()],
    'game_packaged':False,'dimensions':SPEC,'cached_reexport':True},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('CACHED_REEXPORT_OK '+json.dumps({k:v for k,v in report.items() if k!='protected_objects'}),flush=True)
