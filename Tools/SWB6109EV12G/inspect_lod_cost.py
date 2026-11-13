"""Read-only per-object geometry budget report for the saved source."""
import bpy
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'ModelSource/SWB6109EV12G'
bpy.ops.wm.open_mainfile(filepath=str(SRC / 'SWB6109EV12G.blend'))
bpy.context.scene.frame_set(0)
deps = bpy.context.evaluated_depsgraph_get()
rows = []
for obj in bpy.data.collections['SWB6109EV12G | editable components'].objects:
    if obj.type not in {'MESH', 'CURVE', 'FONT'}:
        continue
    data = bpy.data.meshes.new_from_object(obj.evaluated_get(deps), depsgraph=deps)
    data.calc_loop_triangles()
    rows.append({'name': obj.name, 'type': obj.type, 'part': obj.get('cdlc_part', -1),
                 'triangles': len(data.loop_triangles),
                 'modifiers': [{'name': m.name, 'type': m.type} for m in obj.modifiers]})
    bpy.data.meshes.remove(data)
totals = {}
for row in rows:
    key = str(row['part'])
    totals[key] = totals.get(key, 0) + row['triangles']
rows.sort(key=lambda row: row['triangles'], reverse=True)
out = ROOT / 'out/swb6109-spec/lod-cost.json'
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps({'parts': totals, 'objects': rows}, indent=2), encoding='utf-8')
print(json.dumps({'parts': totals, 'top': rows[:40]}, indent=2))
