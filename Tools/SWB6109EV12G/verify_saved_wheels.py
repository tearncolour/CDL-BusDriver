"""Recheck all six tyres from the saved, unmodified Blender source.

blender --background --python-exit-code 1 --python verify_saved_wheels.py --
    --blend source.blend --report wheel-clearance.json
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import bpy

sys.path.insert(0,str(Path(__file__).resolve().parent))
from source_coordinates import canonical_world_matrix

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--blend',type=Path,default=SRC/'SWB6109EV12G.blend')
parser.add_argument('--report',type=Path,default=SRC/'wheel-clearance.json')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source=args.blend.resolve();source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source));bpy.context.scene.frame_set(0);bpy.context.view_layer.update()
master=bpy.data.collections['SWB6109EV12G | editable components']
spec=json.loads((SRC/'model-spec.json').read_text(encoding='utf-8'))
exec(Path(__file__).with_name('wheel_clearance.py').read_text(encoding='utf-8'),globals())
deps=bpy.context.evaluated_depsgraph_get()
def flatten(ob):
    data=bpy.data.meshes.new_from_object(ob.evaluated_get(deps),depsgraph=deps)
    # Wheel names/steering signs in the clearance helper use the legacy CDLC
    # construction frame, not the reflected physical Blender source world.
    data.transform(canonical_world_matrix(ob))
    return data
half=float(spec['length_m'])/2
axles=[-half+float(spec['front_overhang_m']),half-float(spec['rear_overhang_m'])]
objects=[ob for ob in master.objects if ob.type in {'MESH','CURVE','FONT'}]
report=check_wheel_clearance(objects,flatten,axles,float(spec['wheel_radius_m']),
    float(spec['front_track_m']),float(spec['rear_track_m']),
    half_width=float(spec['tyre_half_width_m']),tread_depth=float(spec['tyre_tread_depth_m']),
    rear_dual_offset=float(spec.get('rear_dual_offset_m',0)))
report.update(source=str(source),source_sha256=source_hash,
    scope='Saved-source, closed-door six-tyre full-tread clearance at all 18 poses.',
    coordinate_space='Private canonical construction frame: +X door side, -X driver side, -Y forward, +Z up; wheel labels and steering signs follow this frame.',
    source_coordinate_version=int(bpy.context.scene.get('source_coordinate_version',0)))
if hashlib.sha256(source.read_bytes()).hexdigest()!=source_hash:
    raise RuntimeError('Saved source changed during the read-only wheel audit')
args.report.parent.mkdir(parents=True,exist_ok=True)
args.report.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(f"SWB6109_SAVED_WHEELS_{report['status']} {report['tested_wheel_poses']} poses; {args.report}",flush=True)
if report['penetrations']:raise RuntimeError('Tyre/body intersections: '+json.dumps(report['penetrations'][:10]))
