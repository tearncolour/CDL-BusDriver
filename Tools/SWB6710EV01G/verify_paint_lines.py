"""Check the painted livery lines (paint_lines.py) against the body of every LOD, on the saved master.

The lines keep their full topology at every LOD (cdlc_keep_topology); the panels under them are reduced like build_model.py's
merged() and write_runtime.py do (remove doubles, DECIMATE at the LOD's ratio). For every line vertex and face centre this casts a ray
along the line's own outward normal, the way the eye looks at it, and measures its height above the first paint of that LOD behind it:
it must stay above the paint (not buried, which z-fights or hides the line) and close to it (not floating). A nearest-point measure
is not used: at a panel joint it finds the joint's return walls, which the eye never sees. At LOD0 nothing else may stand in front
of the line along that normal. Reports per line and LOD; exits non-zero on a failure.

blender -b ModelSource/SWB6710EV01G/SWB6710EV01G.blend --python-exit-code 1 --python Tools/SWB6710EV01G/verify_paint_lines.py
"""
import json
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'ModelSource' / 'SWB6710EV01G'
# (LOD, reduction ratio, export): build_model.py's FBX (the game's native vehicle) and write_runtime.py's SWB1 (the driven bus)
CASES = [(0, 1., 'both'), (1, .38, 'fbx'), (2, .12, 'fbx'), (1, .32, 'runtime'), (2, .085, 'runtime')]
MIN_ABOVE = .0005              # buried below this
MAX_ABOVE = {0: .004, 1: .008, 2: .010}   # floating above this (the far LODs' lines stand 4 mm off a surface that moved)
PAINT = '01 Shanghai aqua body | CDLC line colour'

master = bpy.data.collections['SWB6710EV01G | editable components']

lines = [o for o in master.all_objects if o.get('cdlc_paint_line')]
if not lines:
    sys.exit('verify_paint_lines: no painted lines in the master')


def outward(normal, at):
    return normal if normal.dot(Vector((at.x, at.y, 0))) >= 0 else -normal


def flat(obj):
    deps = bpy.context.evaluated_depsgraph_get()   # fresh: temporary objects come and go between calls
    ev = obj.evaluated_get(deps)
    data = bpy.data.meshes.new_from_object(ev, depsgraph=deps)
    data.transform(obj.matrix_world)
    return data


def lod_tree(ratio):
    verts, tris, owner = [], [], []
    for obj in list(master.all_objects):   # a snapshot: the loop links and removes temporary objects
        if obj.type not in {'MESH', 'CURVE'} or obj.hide_render or obj.get('cdlc_paint_line'):
            continue
        if obj.name.startswith('Wrap surface') or 'lining' in obj.name.lower():
            continue
        if not any(s.material and s.material.name == PAINT for s in obj.material_slots):
            continue
        data = flat(obj)
        reduce = (ratio < 1 and int(obj.get('cdlc_part', -1)) != 0 and not obj.get('front_c2_roof_transition')
                  and not obj.get('cdlc_keep_topology') and len(data.polygons) > 8
                  and not any(m and m.get('light_id') for m in data.materials))
        if reduce:
            bm = bmesh.new(); bm.from_mesh(data)
            bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=0.000001)
            bm.normal_update(); bm.to_mesh(data); bm.free()
            temp = bpy.data.objects.new('__paint_lod', data); bpy.context.scene.collection.objects.link(temp)
            mod = temp.modifiers.new('LOD', 'DECIMATE'); mod.ratio = ratio
            reduced = bpy.data.meshes.new_from_object(temp.evaluated_get(bpy.context.evaluated_depsgraph_get()))
            bpy.data.objects.remove(temp, do_unlink=True); bpy.data.meshes.remove(data)
            data = reduced
        data.calc_loop_triangles()
        mats = [m.name if m else None for m in data.materials]
        base = len(verts)
        verts.extend(v.co.copy() for v in data.vertices)
        for t in data.loop_triangles:
            if t.material_index < len(mats) and mats[t.material_index] == PAINT:
                tris.append(tuple(base + i for i in t.vertices)); owner.append(obj.name)
        bpy.data.meshes.remove(data)
    return BVHTree.FromPolygons(verts, tris), owner


# LOD0: nothing may stand in front of a line's point along the paint's outward normal (a seal ring, a hatch: the line would lie
# hidden under it)
def visible_tree():
    verts, tris = [], []
    for obj in list(master.all_objects):
        if obj.type not in {'MESH', 'CURVE', 'FONT'} or obj.hide_render or obj.get('cdlc_paint_line') or obj.name.startswith('Wrap surface'):
            continue
        data = flat(obj)
        data.calc_loop_triangles()
        base = len(verts)
        verts.extend(v.co.copy() for v in data.vertices)
        tris.extend(tuple(base + i for i in t.vertices) for t in data.loop_triangles)
        bpy.data.meshes.remove(data)
    return BVHTree.FromPolygons(verts, tris)


report, failed = {'cases': CASES, 'min_above_mm': MIN_ABOVE * 1000, 'max_above_mm': {k: v * 1000 for k, v in MAX_ABOVE.items()}, 'lines': {}}, []
line_points, line_covers = {}, {}
for obj in lines:
    me = obj.data
    rot = obj.matrix_world.to_3x3().inverted_safe().transposed()
    pts = [(obj.matrix_world @ v.co, (rot @ v.normal).normalized()) for v in me.vertices]
    spans = set(obj.get('paint_bridge_faces', []))   # faces painting over a seam: covered-checked only
    heights = [(obj.matrix_world @ f.center, (rot @ f.normal).normalized()) for f in me.polygons if f.index not in spans]
    covers = [(obj.matrix_world @ f.center, (rot @ f.normal).normalized()) for f in me.polygons if f.index in spans]
    pts = [(p, outward(n, p)) for p, n in pts + heights]
    line_covers[obj.name] = pts + [(p, outward(n, p)) for p, n in covers]
    line_points[obj.name] = (pts, list(obj.get('cdlc_lods', (0, 1, 2))))
REACH = .03
for lod, ratio, export in CASES:
    tree, owner = lod_tree(ratio)
    for name, (pts, lods) in line_points.items():
        if lod not in lods:
            continue
        low, high, missing, low_at, high_at = 1., -1., 0, None, None
        for p, n in pts:
            hit = tree.ray_cast(p + n * REACH, -n, 2 * REACH)
            if hit[0] is None:
                missing += 1
                continue
            near = hit
            h = hit[3] - REACH          # past the line: its height above the paint
            if h < low: low, low_at = h, (owner[near[2]], [round(c, 3) for c in p])
            if h > high: high, high_at = h, (owner[near[2]], [round(c, 3) for c in p])
        entry = report['lines'].setdefault(name, {})
        entry[f'lod{lod} {export} {ratio}'] = {'above_mm': [round(low * 1000, 2), round(high * 1000, 2)], 'no_paint_within_3cm': missing,
                              'lowest_at': low_at, 'highest_at': high_at}
        if missing or low < MIN_ABOVE or high > MAX_ABOVE[lod]:
            failed.append(f'{name} LOD{lod} {export} {ratio}: {low * 1000:.2f}..{high * 1000:.2f} mm, {missing} points without paint within 3 cm')
visible = visible_tree()
for name, (pts, lods) in line_points.items():
    if 0 not in lods:
        continue
    covered = []
    for p, out in line_covers[name]:
        if visible.ray_cast(p + out * .0002, out, .3)[0] is not None:
            covered.append([round(c, 4) for c in p])
    report['lines'][name]['covered_points_lod0'] = len(covered)
    if covered:
        failed.append(f'{name}: {len(covered)} points covered by another surface, first at {covered[0]}')
report['status'] = 'FAIL' if failed else 'PASS'
report['failures'] = failed
(SRC / 'paint-lines-validation.json').write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding='utf-8')
print('PAINT_LINES_' + report['status'] + ' ' + json.dumps(failed[:12], ensure_ascii=False))
if failed:
    sys.exit(1)
