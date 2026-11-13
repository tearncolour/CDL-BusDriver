"""Correct an existing editable master, preserving its canonical export mesh.

Creates a recoverable snapshot under out/, checks every object's exported
positions/normals/topology before saving, and leaves all native FBXs untouched.
"""
import ast
import argparse
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import shutil
import sys
import bpy
import numpy as np
from mathutils import Matrix, Vector

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
target=SRC/'SWB6109EV12G.blend'
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--base-model',type=Path,default=target)
parser.add_argument('--replace-card-reader',action='store_true')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source=args.base_model.resolve()
target_hash=hashlib.sha256(target.read_bytes()).hexdigest()
sys.path.insert(0,str(Path(__file__).resolve().parent))
from source_coordinates import apply_source_coordinates,source_root,sync_coordinate_metadata

bpy.ops.wm.open_mainfile(filepath=str(source))
scene=bpy.context.scene;scene.frame_set(0);bpy.context.view_layer.update()
MASTER=bpy.data.collections['SWB6109EV12G | editable components']
if source_root(MASTER) is not None:
    raise RuntimeError('Source is already corrected; use reexport_saved.py')
SPEC=json.loads((SRC/'model-spec.json').read_text(encoding='utf-8'))
L,W=SPEC['length_m'],SPEC['width_m'];HALF,SIDE=L/2,W/2
main=ast.parse(Path(__file__).with_name('build_model.py').read_text(encoding='utf-8'))
exec(compile(ast.Module(body=[n for n in main.body if isinstance(n,ast.FunctionDef)],type_ignores=[]),'<build helpers>','exec'),globals())
exec(Path(__file__).with_name('exterior.py').read_text(encoding='utf-8').split('end_surface();end_surface(True)')[0],globals())
exec(Path(__file__).with_name('export_helpers.py').read_text(encoding='utf-8'),globals())

reader_change=None
if args.replace_card_reader:
    from card_reader import replace_common_card_reader
    reader_change=replace_common_card_reader(MASTER,SRC/'common-card-reader.json')
    (SRC/'common-card-reader-import.json').write_text(json.dumps(reader_change,indent=2)+'\n',encoding='utf-8')
    scene['kit_reuse']=scene.get('kit_reuse','')+', CardReader'

def exported_geometry(ob):
    data=flatten_object(ob)
    points=np.empty(len(data.vertices)*3,dtype=np.float32);data.vertices.foreach_get('co',points)
    corners=np.empty(len(data.corner_normals)*3,dtype=np.float32);data.corner_normals.foreach_get('vector',corners)
    indices=np.empty(len(data.loops),dtype=np.int32);data.loops.foreach_get('vertex_index',indices)
    faces=np.empty(len(data.polygons),dtype=np.int32);data.polygons.foreach_get('loop_total',faces)
    bpy.data.meshes.remove(data)
    return points,corners,indices,faces

objects=[ob for ob in MASTER.objects if ob.type in {'MESH','CURVE','FONT'}]
before={ob.name:exported_geometry(ob) for ob in objects}
backup=ROOT/'out/swb6109-handedness'/datetime.now().strftime('backup-%Y%m%d-%H%M%S')
backup.mkdir(parents=True)
for path in (source,source.parent/'sunwin-mesh.bin.gz',source.parent/'validation-manifest.json'):
    if path.exists():shutil.copy2(path,backup/path.name)
if source!=target:shutil.copy2(target,backup/'previous-target.blend')
before_hash=hashlib.sha256(source.read_bytes()).hexdigest()
conversion=apply_source_coordinates(MASTER,end_point,width_at,scene)
rows=[];failures=[]
for ob in objects:
    old=before[ob.name];new=exported_geometry(ob)
    same_shape=all(a.shape==b.shape for a,b in zip(old,new))
    same_topology=same_shape and np.array_equal(old[2],new[2]) and np.array_equal(old[3],new[3])
    position_error=float(np.max(np.abs(old[0]-new[0]))) if same_shape and old[0].size else 0.
    normal_error=float(np.max(np.abs(old[1]-new[1]))) if same_shape and old[1].size else 0.
    exact=same_shape and all(np.array_equal(a,b) for a,b in zip(old,new))
    row={'object':ob.name,'same_topology':same_topology,'exact':exact,
         'max_position_error_m':position_error,'max_normal_component_error':normal_error}
    rows.append(row)
    # Re-evaluation after a parent reflection rounds world positions to float32.
    # At thin bevels this also perturbs reconstructed normals; 0.005 component
    # error is below 0.5 degree, while the positional limit is two micrometres.
    if not same_topology or position_error>2e-6 or normal_error>.005:failures.append(row)
report={'status':'FAIL' if failures else 'PASS','base_master_sha256':before_hash,
        'backup':str(backup.relative_to(ROOT)),'conversion':conversion,
        'card_reader_change':reader_change,
        'checked_objects':len(rows),'exact_objects':sum(r['exact'] for r in rows),
        'position_tolerance_m':2e-6,'normal_component_tolerance':.005,
        'max_position_error_m':max(r['max_position_error_m'] for r in rows),
        'max_normal_component_error':max(r['max_normal_component_error'] for r in rows),
        'failures':failures,'objects':rows}
report_path=SRC/'source-coordinate-migration.json'
if failures:
    (backup.parent/'migration-failure.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    raise RuntimeError('Source coordinate migration changed canonical geometry: '+json.dumps(failures[:5]))
cam=scene.camera
cam.location=(-12,-16,5);cam.rotation_euler=(Vector((0,0,1.5))-cam.location).to_track_quat('-Z','Y').to_euler()
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_rotation=cam.rotation_euler.to_quaternion()
if hashlib.sha256(target.read_bytes()).hexdigest()!=target_hash:
    raise RuntimeError('Target master changed during migration; refusing to replace concurrent edits')
bpy.ops.wm.save_as_mainfile(filepath=str(target))
sync_coordinate_metadata(SRC)
report['source_sha256']=hashlib.sha256(target.read_bytes()).hexdigest()
report_path.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('SOURCE_COORDINATES_CORRECTED '+json.dumps({k:v for k,v in report.items() if k not in ('objects','conversion')}),flush=True)
