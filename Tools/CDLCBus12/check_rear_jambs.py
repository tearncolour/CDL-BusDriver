"""Check rear liners and door post headers against the actual outer frame in every LOD."""
import argparse, hashlib, json, sys
from pathlib import Path
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ap = argparse.ArgumentParser()
ap.add_argument('--blend', type=Path, required=True)
ap.add_argument('--data', type=Path, required=True)
ap.add_argument('--out', type=Path, required=True)
a = ap.parse_args(sys.argv[sys.argv.index('--') + 1:])
bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()))
bpy.context.scene.frame_set(0)
bpy.context.view_layer.update()
spec = json.loads((a.data / 'spec.json').read_text())
profile = json.loads((a.data / 'vehicle.json').read_text())
rows, failures = [], []
for lod in range(3):
    trees = {}
    for part in (0, 17):
        vertices, faces = [], []
        for ob in bpy.data.collections['LOD%d' % lod].all_objects:
            if ob.type != 'MESH' or ob.get('cdlc_part') != part:
                continue
            mesh = ob.data
            mesh.calc_loop_triangles()
            offset = len(vertices)
            vertices.extend(ob.matrix_world @ v.co for v in mesh.vertices)
            faces.extend(tuple(offset + i for i in t.vertices) for t in mesh.loop_triangles)
        trees[part] = BVHTree.FromPolygons(vertices, faces, all_triangles=True)
    for side in (-1, 1):
        for inset in (.02, .06, .10):
            for sample in range(101):
                height = spec['FloorY'] + 1.02 + .50 * sample / 100
                origin = Vector((side * (spec['HalfWidth'] + 1), -spec['RearZ'] - inset, height))
                direction = Vector((-side, 0, 0))
                liner = trees[0].ray_cast(origin, direction, 4)
                cover = trees[17].ray_cast(origin, direction, 4)
                gap = liner[3] - cover[3] if liner[0] is not None and cover[0] is not None else None
                row = {'region': 'rear_jamb', 'lod': lod, 'side': side, 'origin': list(origin), 'liner_triangle': liner[2],
                       'cover_triangle': cover[2], 'cover_to_liner_gap_m': gap}
                rows.append(row)
                if gap is None or gap < 0:
                    failures.append(row)
    side = spec['DriverSide']
    for door in profile['doors']:
        for edge in (-1, 1):
            for height_offset in (.005, .015, .025):
                origin = Vector((side * (spec['HalfWidth'] + 1),
                                 -door['centreZ'] + edge * (door['width'] * .5 + .045),
                                 spec['RoofY'] - .64 + height_offset))
                direction = Vector((-side, 0, 0))
                liner = trees[0].ray_cast(origin, direction, 4)
                cover = trees[17].ray_cast(origin, direction, 4)
                gap = liner[3] - cover[3] if liner[0] is not None and cover[0] is not None else None
                row = {'region': 'door_header', 'lod': lod, 'side': side, 'origin': list(origin),
                       'liner_triangle': liner[2], 'cover_triangle': cover[2], 'cover_to_liner_gap_m': gap}
                rows.append(row)
                if gap is None or gap < 0:
                    failures.append(row)
report = {'source_sha256': hashlib.sha256(a.blend.read_bytes()).hexdigest(), 'samples': len(rows),
          'minimum_cover_to_liner_gap_m': min(r['cover_to_liner_gap_m'] for r in rows if r['cover_to_liner_gap_m'] is not None),
          'rows': rows, 'failures': failures,
          'scope': 'Side-directed rays against actual Shell and Glazing triangles: rear jambs at three longitudinal strips per side and 101 heights per LOD; both ends of every door at three heights above the window band boundary. Not a continuous clearance proof.'}
a.out.write_text(json.dumps(report, indent=2))
print(json.dumps({k: report[k] for k in ('samples', 'minimum_cover_to_liner_gap_m')}), 'failures', len(failures))
if failures:
    raise RuntimeError('Rear liner protrudes through the discrete outer pillar')
