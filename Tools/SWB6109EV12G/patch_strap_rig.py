"""Append editable strap pivots to an existing master; never rebuild its body.

Use --output out/...blend for review. Replacing the input additionally requires
--in-place, so export coordination can keep formal source writes serial.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import bpy

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]
sys.path.insert(0,str(HERE))
from animation_rig import prepare_strap_rig, collect_cdlc_parts
from source_coordinates import canonical_world_matrix

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--blend',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/SWB6109EV12G.blend')
parser.add_argument('--output',type=Path,required=True)
parser.add_argument('--report',type=Path,required=True)
parser.add_argument('--expect-sha256')
parser.add_argument('--in-place',action='store_true')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source=args.blend.resolve();target=args.output.resolve()
digest=hashlib.sha256(source.read_bytes()).hexdigest()
if args.expect_sha256 and digest!=args.expect_sha256:raise RuntimeError('Input master changed since coordination')
if target==source and not args.in_place:raise RuntimeError('Replacing source requires --in-place')
bpy.ops.wm.open_mainfile(filepath=str(source))
scene=bpy.context.scene;scene.frame_set(0);bpy.context.view_layer.update()
master=bpy.data.collections['SWB6109EV12G | editable components']
objects=[o for o in master.objects if o.type in {'MESH','CURVE','FONT'}]
before={o.name:canonical_world_matrix(o) for o in objects}
parents={o.name:o.parent.name if o.parent else None for o in objects}
data_ids={o.name:o.data.as_pointer() for o in objects}
fixed_before=collect_cdlc_parts(master)[:34]
report=prepare_strap_rig(master,scene)
bpy.context.view_layer.update()
deltas={o.name:max(abs(before[o.name][r][c]-canonical_world_matrix(o)[r][c]) for r in range(4) for c in range(4)) for o in objects}
if max(deltas.values(),default=0)>2e-6:raise RuntimeError('Strap reparenting moved original geometry: '+str(sorted(deltas.items(),key=lambda x:-x[1])[:5]))
if any(o.data.as_pointer()!=data_ids[o.name] for o in objects):raise RuntimeError('Source geometry datablock was replaced')
parts=collect_cdlc_parts(master)
for before_part,after_part in zip(fixed_before,parts[:34]):
    for key in ('index','name','parent','pivot_object','pivot_unity','rest_rotation_unity','rest_matrix_blender','posed_part_to_blender'):
        if before_part[key]!=after_part[key]:raise RuntimeError('Fixed CDLC frame changed: '+str(before_part['index'])+' '+key)
second=prepare_strap_rig(master,scene)
if second!=report:raise RuntimeError('Strap preparation is not idempotent')
old_report=json.loads(scene.get('cdlc_rig_report','{}'))
old_report.update(straps=report,part_object_counts={str(p['index']):len(p['objects']) for p in parts})
scene['cdlc_rig_report']=json.dumps(old_report)
scene['cdlc_animation_instructions']=scene.get('cdlc_animation_instructions','')+' Independent hand straps swing at frames 12 and 72; rest at 0/20/40/60/80.'
target.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(target))
result={'status':'PASS','source_before':str(source),'source_sha256_before':digest,
        'output':str(target),'source_sha256':hashlib.sha256(target.read_bytes()).hexdigest(),
        'straps':report,'geometry_datablocks_unchanged':len(objects),
        'max_canonical_matrix_delta':max(deltas.values(),default=0),
        'changed_parents':[o.name for o in objects if parents[o.name]!=(o.parent.name if o.parent else None)],
        'fixed_34_frames_unchanged':True,'idempotent':True}
args.report.parent.mkdir(parents=True,exist_ok=True)
args.report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print('STRAP_PATCH_PASS '+json.dumps({'count':report['count'],'part_count':len(parts),'max_matrix_delta':result['max_canonical_matrix_delta']}))
