"""Read-only audit of the saved CRRC K18 Blender study.

blender --background --python-exit-code 1 --python Tools/CRRC_K18/verify_source.py
  -- --blend ModelSource/CRRC_K18/CRRC_K18.blend --output out/crrc-k18/source-audit.json

No source repair, modifier application, save, export, or game operation is performed.
PASS describes a measured check only. Missing reference/production validation stays
PENDING, and any failed geometric check makes the aggregate result FAIL.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
import sys

import bpy
from mathutils import Matrix, Vector


ROOT = Path(__file__).resolve().parents[2]
B_TO_U = Matrix(((-1., 0., 0.), (0., 0., 1.), (0., -1., 0.)))
GEOMETRY = {'MESH', 'CURVE', 'FONT', 'SURFACE'}
# Match the current K18 exporter; do not remove legitimate small triangles.
MIN_CROSS_SQUARED = 1e-18
WELD_TOLERANCE = 1e-7
POSITION_TOLERANCE = 1e-5
WIDTH_TOLERANCE = .001
COLLECTIONS = ('K18 Front', 'K18 Rear', 'K18 Joint')


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def inherited(obj, key, default=None):
    while obj is not None:
        if key in obj:
            return obj[key]
        obj = obj.parent
    return default


def base_name(obj):
    return re.sub(r'^LOD[012] ', '', obj.name)


def lod_limits(obj):
    return int(inherited(obj, 'first_lod', 0)), int(inherited(obj, 'last_lod', 2))


def in_lod(obj, lod):
    first, last = lod_limits(obj)
    return first <= lod <= last


def under(obj, parent):
    while obj is not None:
        if obj == parent:
            return True
        obj = obj.parent
    return False


def xyz(v):
    return [float(x) for x in v]


def bounds(points):
    return {'min': [min(p[i] for p in points) for i in range(3)],
            'max': [max(p[i] for p in points) for i in range(3)]}


def check(name, failures=None, **evidence):
    return {'check': name, 'status': 'FAIL' if failures else 'PASS',
            'failures': failures or [], **evidence}


def evaluated(obj):
    owner = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    data = owner.to_mesh()
    try:
        data.calc_loop_triangles()
        vertices = [owner.matrix_world @ v.co for v in data.vertices]
        triangles = [tuple(t.vertices) for t in data.loop_triangles]
        return vertices, triangles
    finally:
        owner.to_mesh_clear()


def topology(points, triangles, weld=False):
    """Report geometric edge incidence without altering the Blender mesh."""
    if weld:
        keys, mapped, unique = {}, [], []
        for p in points:
            key = tuple(round(float(x) / WELD_TOLERANCE) for x in p)
            if key not in keys:
                keys[key] = len(unique)
                unique.append(p)
            mapped.append(keys[key])
        faces = [tuple(mapped[i] for i in face) for face in triangles]
        points = unique
    else:
        faces = triangles
    edge_faces = defaultdict(list)
    duplicates = Counter()
    degenerate = []
    parents = list(range(len(points)))

    def find(i):
        while parents[i] != i:
            parents[i] = parents[parents[i]]
            i = parents[i]
        return i

    def union(a, b):
        a, b = find(a), find(b)
        if a != b:
            parents[b] = a

    for fi, face in enumerate(faces):
        a, b, c = (points[i] for i in face)
        if len(set(face)) != 3 or (b-a).cross(c-a).length_squared <= MIN_CROSS_SQUARED:
            degenerate.append(fi)
        duplicates[tuple(sorted(face))] += 1
        for i, j in zip(face, (face[1], face[2], face[0])):
            edge_faces[tuple(sorted((i, j)))].append((fi, i < j))
            union(i, j)
    component_faces = defaultdict(list)
    for fi, face in enumerate(faces):
        component_faces[find(face[0])].append(fi)
    components = []
    for face_ids in component_faces.values():
        ids = {v for fi in face_ids for v in faces[fi]}
        origin = sum((points[v] for v in ids), Vector()) / len(ids)
        volume = 0.
        for fi in face_ids:
            a, b, c = (points[i]-origin for i in faces[fi])
            volume += a.dot(b.cross(c)) / 6.
        edges = {tuple(sorted((a, b))) for fi in face_ids for a, b in
                 zip(faces[fi], (faces[fi][1], faces[fi][2], faces[fi][0]))}
        closed = all(len(edge_faces[e]) == 2 for e in edges)
        components.append({'triangles': len(face_ids), 'closed': closed,
                           'signed_volume_blender_m3': volume})
    return {
        'vertices': len(points), 'triangles': len(faces),
        'boundary_edges': sum(len(v) == 1 for v in edge_faces.values()),
        'nonmanifold_edges': sum(len(v) > 2 for v in edge_faces.values()),
        'same_direction_shared_edges': sum(len(v) == 2 and v[0][1] == v[1][1]
                                           for v in edge_faces.values()),
        'degenerate_triangles': len(degenerate), 'degenerate_examples': degenerate[:12],
        'duplicate_triangles': sum(n-1 for n in duplicates.values() if n > 1),
        'components': components,
        'negative_closed_components': sum(c['closed'] and c['signed_volume_blender_m3'] < -1e-14
                                          for c in components),
        'zero_volume_closed_components': sum(c['closed'] and abs(c['signed_volume_blender_m3']) <= 1e-14
                                             for c in components),
    }


def center(points):
    b = bounds(points)
    return Vector([(a+b)/2 for a, b in zip(b['min'], b['max'])])


def main(args):
    source = args.blend.resolve()
    before = sha(source)
    bpy.ops.wm.open_mainfile(filepath=str(source))
    scene = bpy.context.scene
    loaded_frame = scene.frame_current
    rest = int(scene.get('export_rest_frame', 1))
    scene.frame_set(rest)
    bpy.context.view_layer.update()
    checks = []
    missing = [n for n in COLLECTIONS if bpy.data.collections.get(n) is None]
    objects = sorted({o for n in COLLECTIONS if bpy.data.collections.get(n)
                      for o in bpy.data.collections[n].all_objects}, key=lambda o: o.name)
    roots = {n: bpy.data.objects.get(n) for n in ('section_front', 'section_rear', 'articulation_system')}
    failures = ['Missing collection: '+n for n in missing]
    failures += ['Missing root: '+n for n, o in roots.items() if o is None]
    root_evidence = {}
    for name, obj in roots.items():
        if obj is not None:
            root_evidence[name] = {'blender': xyz(obj.matrix_world.translation),
                                   'unity': xyz(B_TO_U @ obj.matrix_world.translation),
                                   'parent': obj.parent.name if obj.parent else None}
            if obj.parent is not None:
                failures.append(name+': section root unexpectedly parented')
    front, rear, joint = (roots[n] for n in roots)
    if front and front.matrix_world.translation.length > POSITION_TOLERANCE:
        failures.append('Front root is not at the vehicle origin')
    if rear and joint:
        if (rear.matrix_world.translation-joint.matrix_world.translation).length > POSITION_TOLERANCE:
            failures.append('Rear root and articulation root do not share a pivot')
        p = B_TO_U @ rear.matrix_world.translation
        if abs(p.x) > POSITION_TOLERANCE or abs(p.y) > POSITION_TOLERANCE or not -3.0 < p.z < 0.:
            failures.append('Rear articulation pivot is not centered near the expected joint region')
    for label, root in (('front', front), ('rear', rear), ('joint', joint)):
        if root:
            for obj in objects:
                if obj != root and inherited(obj, 'cab_section') == label and not under(obj, root):
                    # Rigid attachment frames and thresholds are categorized with
                    # the joint assembly but correctly follow one rigid section.
                    if label == 'joint' and any(r and under(obj, r) for r in (front, rear)):
                        continue
                    failures.append(obj.name+': outside its section-root hierarchy')
    checks.append(check('section_roots_and_actual_pivot', failures, roots=root_evidence,
                        exact_factory_joint_location='PENDING: photographic estimate only'))

    cache, meshes, failures = {}, [], []
    for obj in objects:
        if obj.type not in GEOMETRY:
            continue
        points, triangles = evaluated(obj)
        cache[obj.name] = (points, triangles)
        row = {'object': obj.name, 'type': obj.type,
               'section': str(inherited(obj, 'cab_section', '')),
               'part': inherited(obj, 'cdlc_part'),
               'mirror_assembly': bool(obj.get('k18_mirror_assembly',False)),
               'runtime_generated': str(inherited(obj, 'runtime_generated', ''))}
        first, last = lod_limits(obj)
        row.update(first_lod=first, last_lod=last,
                   active_lods=[lod for lod in range(3) if in_lod(obj, lod)])
        if not points or not triangles:
            row['status'] = 'FAIL'; row['reason'] = 'Empty evaluated geometry'
            failures.append(obj.name+': empty evaluated geometry')
            meshes.append(row); continue
        if not all(math.isfinite(x) for p in points for x in p):
            row['status'] = 'FAIL'; row['reason'] = 'Nonfinite vertex'
            failures.append(obj.name+': nonfinite vertex')
            meshes.append(row); continue
        raw, welded = topology(points, triangles), topology(points, triangles, weld=True)
        row.update(blender_bounds=bounds(points), unity_bounds=bounds([B_TO_U @ p for p in points]),
                   raw=raw, geometric_weld=welded)
        bad = [k for k in ('boundary_edges', 'nonmanifold_edges', 'same_direction_shared_edges',
                          'degenerate_triangles', 'duplicate_triangles', 'negative_closed_components',
                          'zero_volume_closed_components') if welded[k]]
        row['status'] = 'FAIL' if bad else 'PASS'
        if bad:
            row['failed_metrics'] = bad
            failures.append(obj.name+': '+', '.join(bad))
        meshes.append(row)
    checks.append(check('evaluated_mesh_integrity', failures, evaluated_objects=len(meshes),
                        welded_tolerance_m=WELD_TOLERANCE,
                        triangle_min_cross_squared_m4=MIN_CROSS_SQUARED,
                        method='World-space Blender right-handed triangle orientation. Raw and coordinate-welded topology both retained; no meshes are changed. Closed volume is tested per connected component, not only per object.'))

    failures, inventories = [], []
    for obj in objects:
        if obj.type in GEOMETRY:
            first, last = lod_limits(obj)
            if not 0 <= first <= last <= 2:
                failures.append(obj.name+': invalid first_lod/last_lod interval')
    for lod in range(3):
        active = [o for o in objects if o.type in GEOMETRY and in_lod(o, lod)]
        counts = Counter(base_name(o).split('.')[0] for o in active)
        inventories.append({'lod': lod, 'objects': len(active),
                            'triangles': sum(len(cache[o.name][1]) for o in active),
                            'seat_cushions': counts['Passenger seat cushion'],
                            'seat_legs': counts['Seat leg'],
                            'tyre_meshes': sum(' tyre ' in base_name(o) for o in active),
                            'sections': dict(Counter(str(inherited(o, 'cab_section', '')) for o in active))})
    checks.append(check('explicit_lod_membership', failures, inventories=inventories,
                        method='Every authored mesh is audited once; per-LOD counts include only first_lod <= level <= last_lod, regardless of preview hide flags.'))

    failures, support_rows = [], []
    for lod in range(3):
        active = [o for o in objects if o.type in GEOMETRY and in_lod(o, lod)]
        legs = [o for o in active if base_name(o).startswith(('Seat leg', 'Seat support'))]
        for seat in (o for o in active if base_name(o).startswith('Passenger seat cushion')):
            sb = bounds([B_TO_U @ p for p in cache[seat.name][0]])
            matches = []
            for leg in legs:
                lb = bounds([B_TO_U @ p for p in cache[leg.name][0]])
                lx, lz = ((lb['min'][i]+lb['max'][i])/2 for i in (0, 2))
                if (sb['min'][0] <= lx <= sb['max'][0] and
                        sb['min'][2] <= lz <= sb['max'][2] and
                        lb['max'][1] >= sb['min'][1]-.10 and
                        lb['min'][1] < sb['min'][1]-.12):
                    matches.append(leg.name)
            support_rows.append({'lod': lod, 'seat': seat.name, 'supports_below': matches})
            if not matches:
                failures.append('LOD%d %s: no support below this separately modeled cushion' % (lod, seat.name))
    checks.append(check('separate_passenger_seats_have_support_in_each_lod', failures,
                        seats=support_rows,
                        limitation='Position/extent check of the authored separate-leg design, not structural strength or exact surface-contact certification. An alternative integral support design requires a corresponding geometric check.'))

    failures, spans, mirror_spans = [], [], []
    for row in meshes:
        if 'blender_bounds' not in row:
            continue
        b = row['blender_bounds']
        outer = max(abs(b['min'][0]), abs(b['max'][0]))
        entry = {'object': row['object'], 'max_abs_blender_x_m': outer,
                 'excess_each_side_m': max(0., outer-args.width/2)}
        if ((row['mirror_assembly'] and row['part']==15) or
                re.sub(r'^LOD[012] ', '', row['object']).lower().startswith('mirror ')):
            mirror_spans.append(entry)
        elif outer > args.width/2 + WIDTH_TOLERANCE:
            spans.append(entry)
            failures.append(row['object']+': outside declared body width')
    checks.append(check('body_width_including_small_attachments', failures, width_m=args.width,
                        tolerance_m=WIDTH_TOLERANCE, offending_bounds=spans,
                        explicit_mirror_exceptions=mirror_spans,
                        limitation='Rest pose only; articulated body is expected to move laterally. No automatic exemptions for non-mirror trim.'))

    failures, evidence = [], []
    for name in ('Driver cushion', 'Driver seat back', 'Steering wheel pivot', 'driver_eye'):
        obj = bpy.data.objects.get(name)
        if obj is None:
            failures.append('Missing '+name); continue
        point = center(cache[name][0]) if name in cache else obj.matrix_world.translation
        evidence.append({'object': name, 'blender_center': xyz(point)})
        if point.x <= .2 or point.y >= -5.5:
            failures.append(name+': not on actual Blender +X driver side near front')
    door_pivots = [o for o in objects if re.match(r'^Door [123] leaf -?1 pivot$', o.name)]
    if len(door_pivots) != 6:
        failures.append('Expected six independently movable door-leaf pivots')
    for obj in door_pivots:
        p = obj.matrix_world.translation
        evidence.append({'object': obj.name, 'blender_center': xyz(p)})
        if p.x >= -.8:
            failures.append(obj.name+': not on actual Blender -X door side')
    checks.append(check('physical_blender_handedness', failures, measured=evidence,
                        coordinate_contract='Blender +X driver side / -X doors / -Y front / +Z up; not inferred from camera images or metadata.'))

    failures, seat_bounds = [], []
    for obj in objects:
        if not base_name(obj).startswith(('Passenger seat cushion', 'Passenger seat back', 'Seat lower shell')):
            continue
        pts = cache.get(obj.name, ([], []))[0]
        if not pts:
            continue
        u = [B_TO_U @ p for p in pts]
        b = bounds(u)
        # The current lower skin is 1.25 m outside and 1.205 m inside.
        # Above the waist, the fixed side pane's inside surface is 1.227 m.
        bad_vertices = [i for i, p in enumerate(u)
                        if abs(p.x) > (1.205 if p.y < 1.14 else 1.227) + POSITION_TOLERANCE]
        seat_bounds.append({'object': obj.name, 'unity_bounds': b,
                            'active_lods': [lod for lod in range(3) if in_lod(obj, lod)],
                            'outside_wall_vertices': len(bad_vertices)})
        if bad_vertices:
            failures.append(obj.name+': vertices extend into/beyond the current side-wall envelope')
    if not seat_bounds:
        failures.append('No passenger seat shells found')
    checks.append(check('seat_shells_within_side_wall_envelope', failures, seats=seat_bounds,
                        limitation='Checks the current wall planes, not full ergonomics, jamb intersections, knee room, hidden contacts or sitting posture. Those need separate actual-mesh and visual review.'))

    failures, wheels = [], []
    for axle in (1, 2, 3):
        for side in ('left', 'right'):
            name = 'Axle %d %s pivot' % (axle, side)
            obj = bpy.data.objects.get(name)
            if obj is None:
                failures.append('Missing '+name); continue
            p = B_TO_U @ obj.matrix_world.translation
            row = {'pivot': name, 'unity': xyz(p), 'bone': obj.get('bone'),
                   'section': inherited(obj, 'cab_section'), 'lods': []}
            for lod in range(3):
                children = [o for o in objects if in_lod(o, lod) and
                            base_name(o).startswith('Axle %d %s tyre' % (axle, side))]
                points = [B_TO_U @ v for o in children for v in cache.get(o.name, ([], []))[0]]
                detail = {'lod': lod, 'tyre_objects': len(children)}
                if points:
                    detail['tyre_unity_bounds'] = bounds(points)
                    if abs(min(v.y for v in points)) > .03:
                        failures.append(name+': LOD%d tyres not resting near ground' % lod)
                    c = center(points)
                    if abs(c.y-p.y) > .01 or abs(c.z-p.z) > .01:
                        failures.append(name+': LOD%d pivot not centered in tyre extent' % lod)
                else:
                    failures.append(name+': LOD%d has no tyre mesh' % lod)
                row['lods'].append(detail)
            if (p.x < 0) != (side == 'left'):
                failures.append(name+': side sign incorrect')
            if row['section'] != ('rear' if axle == 3 else 'front'):
                failures.append(name+': wrong articulated section')
            wheels.append(row)
    if len(wheels) == 6:
        axles = [sum(w['unity'][2] for w in wheels if w['pivot'].startswith('Axle %d ' % a))/2
                 for a in (1, 2, 3)]
        if not axles[0] > axles[1] > axles[2]:
            failures.append('Axles not ordered front to rear')
        for a in (1, 2, 3):
            pair = [w['unity'] for w in wheels if w['pivot'].startswith('Axle %d ' % a)]
            if abs(pair[0][2]-pair[1][2]) > POSITION_TOLERANCE:
                failures.append('Axle %d left/right not at same longitudinal station' % a)
    checks.append(check('three_axles_and_actual_tyre_station', failures, wheels=wheels,
                        limitation='No claim of tyre-to-wheelhouse clearance at all steering angles; this requires independent swept geometry checks.'))

    failures, poses = [], []
    motion_failures, door_motion, joint_motion = [], [], []
    tracked = door_pivots + [o for o in (rear, joint) if o]
    bellows = bpy.data.objects.get('Flexible double-wall articulation bellows')
    rest_matrices = {o.name: o.matrix_world.copy() for o in tracked}
    rest_bellows = evaluated(bellows)[0] if bellows else []
    bellows_u = [B_TO_U @ p for p in rest_bellows]
    if bellows_u:
        rear_z, front_z = min(p.z for p in bellows_u), max(p.z for p in bellows_u)
        rear_end = [i for i, p in enumerate(bellows_u) if abs(p.z-rear_z) <= POSITION_TOLERANCE]
        front_end = [i for i, p in enumerate(bellows_u) if abs(p.z-front_z) <= POSITION_TOLERANCE]
    else:
        rear_end, front_end = [], []
    door_glass = {}
    rest_glass_centers = {}
    for pivot in door_pivots:
        glass = [o for o in objects if in_lod(o, 0) and under(o, pivot)
                 and base_name(o).startswith('Door upper glass')]
        door_glass[pivot.name] = glass
        pts = [B_TO_U @ p for o in glass for p in cache.get(o.name, ([], []))[0]]
        if pts:
            rest_glass_centers[pivot.name] = center(pts)
        else:
            motion_failures.append(pivot.name+': no actual upper glass for inward-motion measurement')
    attachments = [o for o in objects if o.type in GEOMETRY and in_lod(o, 0) and
                   base_name(o).startswith(('Bellows attachment frame', 'Articulation floor threshold',
                                             'Articulation rail upright'))]
    rest_attachments = {o.name: evaluated(o)[0] for o in attachments}
    if not bellows or not bellows.data.shape_keys or len(bellows.data.shape_keys.key_blocks) < 3:
        failures.append('Missing bellows deformation keys')
    for frame in (1, 65, 85, 160, 190):
        scene.frame_set(frame); bpy.context.view_layer.update()
        row = {'frame': frame, 'objects': {}}
        for obj in tracked:
            delta = max(abs(obj.matrix_world[i][j]-rest_matrices[obj.name][i][j])
                        for i in range(4) for j in range(4))
            row['objects'][obj.name] = {'matrix_delta_from_rest': delta,
                                        'location_blender': xyz(obj.matrix_world.translation),
                                        'rotation_euler': xyz(obj.rotation_euler)}
            is_door = obj in door_pivots
            if (is_door and frame in (65, 85)) or (obj == rear and frame in (160, 190)):
                if delta < .01 or obj.animation_data is None or obj.animation_data.action is None:
                    failures.append('%s: no evaluated motion/keyframed action at frame %d' % (obj.name, frame))
        if bellows:
            pts = evaluated(bellows)[0]
            delta = max(((a-b).length for a, b in zip(pts, rest_bellows)), default=0.)
            row['bellows_max_vertex_motion_m'] = delta
            if frame in (160, 190) and delta < .01:
                failures.append('Bellows does not deform at frame %d' % frame)
            if frame in (160, 190) and rear and rear_end and front_end:
                transform = rear.matrix_world @ rest_matrices[rear.name].inverted()
                rear_error = max((pts[i] - transform @ rest_bellows[i]).length for i in rear_end)
                front_error = max((pts[i] - rest_bellows[i]).length for i in front_end)
                detail = {'frame': frame, 'rear_vertices': len(rear_end), 'front_vertices': len(front_end),
                          'rear_end_vs_actual_rear_root_max_error_m': rear_error,
                          'front_end_fixed_max_error_m': front_error,
                          'rear_root_euler_blender': xyz(rear.rotation_euler),
                          'rigid_attachments': []}
                if rear_error > POSITION_TOLERANCE or front_error > POSITION_TOLERANCE:
                    motion_failures.append('Frame %d: bellows endpoint does not follow its section' % frame)
                for obj in attachments:
                    start = rest_attachments[obj.name]
                    now = evaluated(obj)[0]
                    on_rear = under(obj, rear)
                    expected = [transform @ p if on_rear else p for p in start]
                    error = max(((a-b).length for a, b in zip(now, expected)), default=0.)
                    detail['rigid_attachments'].append({'object': obj.name,
                        'owner': 'rear' if on_rear else 'front', 'max_error_m': error})
                    if error > POSITION_TOLERANCE:
                        motion_failures.append('Frame %d: %s does not follow its rigid section' % (frame, obj.name))
                joint_motion.append(detail)
        if frame in (65, 85):
            for pivot in door_pivots:
                if pivot.name not in rest_glass_centers:
                    continue
                pts = [B_TO_U @ p for o in door_glass[pivot.name] for p in evaluated(o)[0]]
                current = center(pts)
                initial = rest_glass_centers[pivot.name]
                dx = current.x-initial.x
                door_motion.append({'frame': frame, 'pivot': pivot.name,
                                    'rest_glass_center_unity': xyz(initial),
                                    'current_glass_center_unity': xyz(current),
                                    'delta_unity_x_m': dx, 'inward': dx < -.05})
                if dx >= -.05:
                    motion_failures.append('Frame %d: %s upper glass center does not move inward' % (frame, pivot.name))
        poses.append(row)
    scene.frame_set(rest); bpy.context.view_layer.update()
    checks.append(check('evaluated_door_and_articulation_animation', failures, poses=poses,
                        limitation='Existence and evaluated motion only: not proof of correct real-door kinematics, joint seal continuity, or collision-free articulation.'))
    checks.append(check('door_inward_motion_and_joint_end_following', motion_failures,
                        door_samples=len(door_motion), joint_samples=len(joint_motion),
                        max_position_error_m=POSITION_TOLERANCE))
    motion_report = {'schema_version': 1, 'source_sha256': before, 'script_sha256': sha(__file__),
                     'status': 'FAIL' if motion_failures else 'PASS', 'failures': motion_failures,
                     'door_glass_centers': door_motion, 'joint_endpoints': joint_motion,
                     'limit': 'Independent comparison to actual evaluated rigid-root transforms. Confirms direction and attachment following only; not real-world joint calibration, full sweep collision, watertight seams or production animation integration.'}

    failures, reader_evidence = [], {}
    reader_path = ROOT/'ModelSource/SWB6109EV12G/common-card-reader.json'
    readers = [o for o in objects if inherited(o, 'runtime_generated') == 'CardReader']
    if len(readers) != 1:
        failures.append('Expected one preview-only shared CardReader mesh')
    if not reader_path.exists():
        failures.append('Shared source reader JSON missing')
    elif len(readers) == 1:
        obj = readers[0]
        document = json.loads(reader_path.read_text(encoding='utf-8'))
        original = document['lods'][0]['parts'][0]
        original_points = [Vector(v) for v in original['positions']]
        actual = [B_TO_U @ (obj.matrix_world @ v.co) for v in obj.data.vertices]
        reader_evidence = {'object': obj.name, 'source_path': str(reader_path),
                           'source_sha256': sha(reader_path), 'stored_source_sha256': obj.get('shared_source_sha256'),
                           'runtime_generated': obj.get('runtime_generated'),
                           'source_vertices': len(original_points), 'actual_vertices': len(actual)}
        if obj.get('shared_source_sha256') != sha(reader_path):
            failures.append('Shared reader source hash mismatch')
        if len(actual) != len(original_points):
            failures.append('Shared reader vertex count mismatch')
        elif actual:
            translation = actual[0]-original_points[0]
            error = max((a-b-translation).length for a, b in zip(actual, original_points))
            reader_evidence.update(unity_translation=xyz(translation), max_translation_only_error_m=error)
            if error > POSITION_TOLERANCE:
                failures.append('Shared reader geometry differs beyond translation')
        expected = Counter(tuple(sorted(original['indices'][i:i+3])) for i in range(0, len(original['indices']), 3))
        obj.data.calc_loop_triangles()
        got = Counter(tuple(sorted(t.vertices)) for t in obj.data.loop_triangles)
        reader_evidence['same_triangle_vertex_sets'] = got == expected
        if got != expected:
            failures.append('Shared reader triangle topology mismatch')
        export_source = (ROOT/'Tools/CRRC_K18/export_model.py').read_text(encoding='utf-8')
        reader_evidence['exporter_sha256'] = sha(ROOT/'Tools/CRRC_K18/export_model.py')
        reader_evidence['runtime_exclusion_branch_present'] = bool(re.search(
            r"if obj\['runtime_generated'\]:.*?excluded\.add\(obj\['name'\]\)\s+continue", export_source, re.S))
        if not reader_evidence['runtime_exclusion_branch_present']:
            failures.append('Exporter lacks explicit runtime_generated exclusion branch')
        reader_evidence['scope'] = 'Source geometry and exclusion code contract; actual final exported bytes require export-report verification.'
    checks.append(check('shared_card_reader_source_and_exclusion_contract', failures, **reader_evidence))

    checks.extend([
        {'check': 'real_vehicle_reference_completeness', 'status': 'PENDING',
         'reason': 'Exact current model code/dimensions, left side, rear face, roof plan, full seat/strap distribution and real door/articulation trajectories are not all established.'},
        {'check': 'full_assembly_and_ergonomics', 'status': 'PENDING',
         'reason': 'Per-object closed meshes and bounds cannot prove assembly seams, foot support, collision-free door/steering sweeps or valid mirror mounts. Final complete-model views and local mesh diagnostics remain required.'},
        {'check': 'three_lod_native_and_game_delivery', 'status': 'PENDING',
         'reason': 'This script does not package or load the game. Articulated front/rear runtime profiles, native tractor/trailer binding, all-LOD material/animation and actual gameplay are not verified.'},
    ])
    scene.frame_set(loaded_frame)
    checks.append(check('source_file_unchanged', [] if sha(source) == before else ['Source file changed during audit'],
                        before_sha256=before, after_sha256=sha(source)))
    report = {'schema_version': 1, 'model': 'CRRC_K18', 'source': str(source),
              'source_sha256': before, 'script_sha256': sha(__file__),
              'status': 'FAIL' if any(c['status'] == 'FAIL' for c in checks) else 'PENDING',
              'geometry_status': 'FAIL' if any(c['status'] == 'FAIL' for c in checks) else 'PASS',
              'game_delivery_status': 'PENDING', 'saved_source': False,
              'blender_version': bpy.app.version_string, 'rest_frame': rest,
              'checks': checks, 'objects': meshes}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    motion_output = args.motion_output or args.output.with_name(args.output.stem+'-motion.json')
    motion_output.parent.mkdir(parents=True, exist_ok=True)
    motion_output.write_text(json.dumps(motion_report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print('K18_SOURCE_AUDIT '+json.dumps({'status': report['status'], 'source_sha256': before,
          'objects': len(meshes), 'failed_checks': [c['check'] for c in checks if c['status'] == 'FAIL'],
          'report': str(args.output.resolve())}))
    if report['geometry_status'] == 'FAIL':
        raise RuntimeError('K18 source audit found failed measured checks; see JSON report')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend', type=Path, default=ROOT/'ModelSource/CRRC_K18/CRRC_K18.blend')
    parser.add_argument('--output', type=Path, default=ROOT/'out/crrc-k18/source-audit.json')
    parser.add_argument('--width', type=float, default=2.54)
    parser.add_argument('--motion-output', type=Path)
    arguments = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    main(arguments)
