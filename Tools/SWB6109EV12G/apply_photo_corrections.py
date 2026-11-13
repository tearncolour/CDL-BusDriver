"""Apply reviewed reference corrections to a saved source, with a backup.

No reconstruction of the vehicle exterior. Geometry and fixed motion frames
outside the enumerated photo corrections must remain unchanged.
"""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import shutil
import sys
import bpy
from array import array
import struct
sys.path.insert(0,str(Path(__file__).resolve().parent))
from animation_rig import prepare_strap_rig, collect_cdlc_parts
from source_coordinates import canonical_world_matrix, sync_coordinate_metadata
from patch_saved_interior import patch_saved_interior
from door_details import patch_photo_door_details
from photo_cabin_details import patch_photo_cabin_details
from floor_seams import patch_floor_seams
from seat_print import apply_seat_print
from verify_handrails import verify_master
from seat_access import apply_saved_seat_access, update_seat_access_metadata
from material_contract import apply_material_contract

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
source=SRC/'SWB6109EV12G.blend'
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--expect-sha256',required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
digest=hashlib.sha256(source.read_bytes()).hexdigest()
if digest!=args.expect_sha256:raise RuntimeError('Source changed before the coordinated patch')
backup=ROOT/'out/swb6109-spec'/('before-photo-'+datetime.now().strftime('%Y%m%d-%H%M%S'))
backup.mkdir(parents=True)
for filename in ('SWB6109EV12G.blend','sunwin-mesh.bin.gz','animation-report.json','build-report.json','preview-manifest.json','cabin-layout.json','model-spec.json','photo-corrections.json'):
    path=SRC/filename
    if path.is_file():shutil.copy2(path,backup/filename)
bpy.ops.wm.open_mainfile(filepath=str(source))
scene=bpy.context.scene;scene.frame_set(0);bpy.context.view_layer.update()
master=bpy.data.collections['SWB6109EV12G | editable components']
frames_before=collect_cdlc_parts(master)[:34]
def geometry_fingerprint(obj):
    digest=hashlib.sha256(struct.pack('<16f',*[v for row in obj.matrix_world for v in row]))
    if obj.type=='MESH':
        values=array('f',[0])*(len(obj.data.vertices)*3)
        obj.data.vertices.foreach_get('co',values);digest.update(values.tobytes())
        indices=array('i',[0])*len(obj.data.loops)
        obj.data.loops.foreach_get('vertex_index',indices);digest.update(indices.tobytes())
    elif obj.type=='CURVE':
        digest.update(str((obj.data.bevel_depth,obj.data.bevel_resolution)).encode())
        for spline in obj.data.splines:
            for point in spline.points:digest.update(struct.pack('<4f',*point.co))
    elif obj.type=='FONT':
        digest.update(str((obj.data.body,obj.data.size,obj.data.extrude)).encode())
    for mod in obj.modifiers:
        if mod.type=='BEVEL':digest.update(str((mod.name,mod.type,mod.width,mod.segments)).encode())
    return digest.hexdigest()
floor_targets={'Floor continuous central low aisle'}|{obj.name for obj in master.objects if obj.name.startswith('Floor outboard wing ')}
frozen={obj.name:geometry_fingerprint(obj) for obj in master.objects
        if obj.name not in floor_targets and obj.type in {'MESH','CURVE','FONT'} and int(obj.get('cdlc_part',-1)) in (0,15,16,17,18,19,20,21,22,23,24,25,26,27,28)}
report={'source_sha256_before':digest,'backup':str(backup)}
report['interior']=patch_saved_interior(master,SRC/'common-card-reader.json')
report['seat_access']=apply_saved_seat_access(master)
report['straps']=prepare_strap_rig(master,scene)
report['doors']=patch_photo_door_details(master,scene)
report['straps']=report['doors']['straps']
report['cabin']=patch_photo_cabin_details(master,scene)
report['floor_seams']=patch_floor_seams(master)
report['seat_print']=apply_seat_print(master)
report['materials']=apply_material_contract(master)
bpy.context.view_layer.update()
report['handrail_geometry']=verify_master(master)
if report['handrail_geometry']['errors']:
    raise RuntimeError('Coordinated interior clearance failed: '+str(report['handrail_geometry']['errors']))
for name,fingerprint in frozen.items():
    obj=master.objects.get(name)
    if obj is None or geometry_fingerprint(obj)!=fingerprint:
        raise RuntimeError('Photo correction unexpectedly changed exterior/shell: '+name)
report['unchanged_shell_exterior_wheel_objects']=len(frozen)
frames_after=collect_cdlc_parts(master)
for before,after in zip(frames_before,frames_after[:34]):
    for key in ('index','name','parent','pivot_object','pivot_unity','rest_rotation_unity','rest_matrix_blender','posed_part_to_blender'):
        if before[key]!=after[key]:raise RuntimeError('Fixed motion frame changed: '+str(before['index'])+' '+key)
report['fixed_34_frames_unchanged']=True
report['runtime_parts']=len(frames_after)
rig=json.loads(scene.get('cdlc_rig_report','{}'))
rig.update(parts=frames_after,part_object_counts={str(p['index']):len(p['objects']) for p in frames_after},
           straps=report['straps'])
(SRC/'animation-report.json').write_text(json.dumps(rig,indent=2)+'\n',encoding='utf-8')
(SRC/'common-card-reader-import.json').write_text(json.dumps(report['interior']['shared_reader'],indent=2)+'\n',encoding='utf-8')
scene['cdlc_rig_report']=json.dumps({k:v for k,v in rig.items() if k!='parts'})
scene['photo_reference_revision']='2026-10-15: retain SWB6109EV12G; user-selected BV1y7bv6UE2A interior and supplied door photographs'
scene['status']='Photo corrections applied; export and game release checks pending'
report['seat_access_metadata']=update_seat_access_metadata(ROOT)
sync_coordinate_metadata(SRC)
if hashlib.sha256(source.read_bytes()).hexdigest()!=digest:raise RuntimeError('Source changed during patch')
bpy.ops.wm.save_as_mainfile(filepath=str(source))
report['source_sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
report['status']='PASS'
report['scope']='Coordinated source edits only; subsequent export, geometry and visual checks are mandatory.'
(SRC/'photo-corrections.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('PHOTO_CORRECTIONS_SAVED '+report['source_sha256'])
