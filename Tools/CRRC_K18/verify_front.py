"""Read-only, per-LOD K18 front assembly audit (Blender Python).

blender -b --python-exit-code 1 --python Tools/CRRC_K18/verify_front.py -- \
  --blend ModelSource/CRRC_K18/CRRC_K18.blend --report out/k18-front.json

Optional scene['k18_front_contract'] JSON (or --contract JSON file) overrides
documented engineering bounds, not geometry results. Optional object property
k18_front_role: shell, glass, sign, mirror_mount, mount_support, floor, ceiling.
The fallback names support the initial source so it remains a useful negative
control. Hidden explicit LOD substitutes are evaluated, not silently omitted.
No normals are repaired, no modifiers applied and no source is saved.
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
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

ROOT = Path(__file__).resolve().parents[2]
B_TO_U = Matrix(((-1., 0., 0.), (0., 0., 1.), (0., -1., 0.)))
U_TO_B = B_TO_U.inverted()
FORWARD = U_TO_B @ Vector((0., 0., 1.))
WELD = 1e-6
MIN_CROSS_SQUARED = 1e-18
DEFAULT = {
    'max_half_width': 1.27, 'max_height': 3.22, 'max_forward': 9.01,
    'min_sign_gap_m': .003, 'max_mount_gap_m': .0002,
    'min_mount_exposure_m': .002,
    'front_region_min_z': 8.30,
    'front_probe_z': 9.10, 'front_probe_min_z': 8.30,
    # Broad central region, deliberately inside rounded outer silhouette.
    'coverage_regions': [{'name': 'central_front', 'x': [-1.08, 1.08],
                          'y': [.31, 3.15], 'pitch': .01}],
    # Near-vertical rays cannot accept the vertical nose as horizontal floor.
    'floor_region': {'x': [-1.05, 1.05], 'z': [8.48, 8.72],
                     'pitch': .025, 'y': [.12, .50]},
    'ceiling_region': {'x': [-.80, .80], 'z': [8.45, 8.65],
                       'pitch': .025, 'y': [2.70, 3.22]},
    # Optional rows defined independently of mesh topology, for example an
    # agreed physical roof-corner section: origin, direction and max_distance.
    # These do not take a source object's own vertices as expected geometry.
    'seam_rays': [],
}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def xyz(p):
    return [float(v) for v in p]


def inherited(o, key, fallback=None):
    while o is not None:
        if key in o:
            return o[key]
        o = o.parent
    return fallback


def basename(o):
    return re.sub(r'\.\d{3}$', '', re.sub(r'^LOD[012] ', '', o.name))


def bounds(points):
    return {'min': [min(p[i] for p in points) for i in range(3)],
            'max': [max(p[i] for p in points) for i in range(3)]}


def linspace(a, b, pitch):
    n = max(1, math.ceil(abs(b-a)/pitch))
    return [a+(b-a)*i/n for i in range(n+1)]


def result(name, failures, **data):
    return {'check': name, 'status': 'FAIL' if failures else 'PASS',
            'failure_count': len(failures), 'failures': failures[:80], **data}


def role(o):
    if 'k18_front_role' in o:
        return str(o['k18_front_role'])
    name = basename(o)
    if name == 'Continuous curved windshield': return 'glass'
    if name in ('Front inset destination housing', 'Route preview'): return 'sign'
    if name == 'Mirror attachment foot': return 'mirror_mount'
    if name in ('Front corner structural return', 'Black front pillar',
                'Upper window band', 'Eave structural rail'): return 'mount_support'
    if name in ('front continuous low-floor aisle', 'Outboard floor',
                'Driver footwell floor'): return 'floor'
    if name in ('front interior ceiling', 'Front roof return'): return 'ceiling'
    return ''


class Mesh:
    def __init__(self, obj):
        self.obj = obj
        self.role = role(obj)
        self.first = int(inherited(obj, 'first_lod', 0))
        self.last = int(inherited(obj, 'last_lod', 2))
        self.part = int(inherited(obj, 'cdlc_part', -1))
        ev = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        data = ev.to_mesh()
        try:
            data.calc_loop_triangles()
            self.points = [ev.matrix_world @ v.co for v in data.vertices]
            self.faces = [tuple(t.vertices) for t in data.loop_triangles]
            nm = ev.matrix_world.to_3x3().inverted().transposed()
            self.normals = [[nm @ data.corner_normals[i].vector for i in t.loops]
                            for t in data.loop_triangles]
            self.glass = any(m and bool(m.get('glass', False)) for m in data.materials)
            self.glass_faces = [bool(data.materials[t.material_index] and
                                data.materials[t.material_index].get('glass', False))
                                if t.material_index < len(data.materials) else False
                                for t in data.loop_triangles]
        finally:
            ev.to_mesh_clear()
        self.finite = all(math.isfinite(v) for p in self.points for v in p)
        self.unity = [B_TO_U @ p for p in self.points]
        self.bounds = bounds(self.unity) if self.points else None
        self.blender_bounds = bounds(self.points) if self.points else None
        self.tree = BVHTree.FromPolygons(self.points, self.faces, all_triangles=True) if self.faces and self.finite else None

    def active(self, lod):
        return self.first <= lod <= self.last


def topology(m):
    """Coordinate welding affects incidence only; raw small faces stay audited."""
    tree = KDTree(len(m.points))
    for i, p in enumerate(m.points): tree.insert(p, i)
    tree.balance()
    mapping = []
    for i, p in enumerate(m.points):
        earlier = [mapping[j] for _, j, d in tree.find_range(p, WELD) if j < i]
        mapping.append(min(earlier) if earlier else i)
    faces = [tuple(mapping[i] for i in f) for f in m.faces]
    edge = defaultdict(list); graph = defaultdict(set)
    degenerate = []; bad_normals = []; duplicates = Counter()
    min_dot = 1.; max_unit_error = 0.
    for k, (face, ns) in enumerate(zip(m.faces, m.normals)):
        a, b, c = [m.points[i] for i in face]
        cross = (b-a).cross(c-a)
        if cross.length_squared <= MIN_CROSS_SQUARED:
            degenerate.append(k)
        else:
            normal = cross.normalized()
            for j, n in enumerate(ns):
                if not all(math.isfinite(v) for v in n) or n.length < 1e-10:
                    bad_normals.append([k, j, 'nonfinite_or_zero']); continue
                unit_error = abs(n.length-1.)
                dot = n.normalized().dot(normal)
                min_dot = min(min_dot, dot); max_unit_error = max(max_unit_error, unit_error)
                if dot < -1e-6 or unit_error > .001:
                    bad_normals.append([k, j, dot, unit_error])
        f = faces[k]; duplicates[tuple(sorted(f))] += 1
        for a, b in zip(f, (f[1], f[2], f[0])):
            edge[tuple(sorted((a, b)))].append((k, a < b))
    for uses in edge.values():
        for a, _ in uses:
            graph[a].update(b for b, _ in uses if a != b)
    components = []; unseen = set(range(len(faces)))
    while unseen:
        todo = [unseen.pop()]; ids = []
        while todo:
            i = todo.pop(); ids.append(i)
            new = graph[i] & unseen; unseen.difference_update(new); todo.extend(new)
        origin = m.points[faces[ids[0]][0]]
        volume = 0.
        for i in ids:
            a, b, c = [m.points[j]-origin for j in faces[i]]
            volume += a.dot(b.cross(c))/6
        closed = all(len(edge[tuple(sorted((a, b)))]) == 2 for i in ids
                     for a, b in zip(faces[i], (faces[i][1], faces[i][2], faces[i][0])))
        components.append({'triangles': len(ids), 'closed': closed, 'signed_volume_m3': volume})
    stats = {'boundary_edges': sum(len(v) == 1 for v in edge.values()),
             'nonmanifold_edges': sum(len(v) > 2 for v in edge.values()),
             'same_direction_shared_edges': sum(len(v) == 2 and v[0][1] == v[1][1] for v in edge.values()),
             'duplicate_triangles': sum(n-1 for n in duplicates.values() if n > 1),
             'degenerate_triangles': len(degenerate), 'invalid_corner_normals': len(bad_normals),
             'nonpositive_closed_components': sum(c['closed'] and c['signed_volume_m3'] <= 1e-14 for c in components)}
    return {'status': 'FAIL' if any(stats.values()) else 'PASS', **stats,
            'degenerate_examples': degenerate[:12], 'normal_examples': bad_normals[:12],
            'min_corner_dot_face': min_dot, 'max_normal_unit_error': max_unit_error,
            'components': components}


class RayGroup:
    """One immutable BVH, preserving each hit's original object and triangle."""
    def __init__(self, meshes):
        points = []; faces = []; self.owners = []
        for m in meshes:
            offset = len(points); points.extend(m.points)
            faces.extend(tuple(offset+i for i in f) for f in m.faces)
            self.owners.extend((m, i) for i in range(len(m.faces)))
        self.tree = BVHTree.FromPolygons(points, faces, all_triangles=True) if faces else None


def ray(meshes, origin, direction, distance):
    if isinstance(meshes, RayGroup):
        if meshes.tree is None: return None
        p, normal, index, length = meshes.tree.ray_cast(origin, direction, distance)
        if p is None: return None
        m, face = meshes.owners[index]
        return (length, p, normal, m, face)
    best = None
    for m in meshes:
        if m.tree is None: continue
        p, normal, face, length = m.tree.ray_cast(origin, direction, distance)
        if p is not None and (best is None or length < best[0]):
            best = (length, p, normal, m, face)
    return best


def hit_record(hit):
    if hit is None: return None
    length, point, normal, mesh, face = hit
    return {'distance_m': length, 'point_unity': xyz(B_TO_U @ point),
            'normal_unity': xyz(B_TO_U @ normal), 'object': mesh.obj.name, 'triangle': face}


def triangle_samples(a, b, c, pitch=.025):
    n = max(1, math.ceil(max((a-b).length, (b-c).length, (c-a).length)/pitch))
    for i in range(n+1):
        for j in range(n+1-i):
            yield a+(b-a)*(i/n)+(c-a)*(j/n)


def sign_clearance(active, cfg):
    glasses = [m for m in active if m.role == 'glass' and m.glass]
    glass_tree = RayGroup(glasses)
    signs = [m for m in active if m.role == 'sign']
    failures = []; rows = []; minimum = None; nearest_minimum = None
    if not glasses or not signs:
        failures.append('Missing explicitly identified front glass or destination sign')
    for m in signs:
        samples = {}; front_faces = 0
        for face in m.faces:
            a, b, c = [m.points[i] for i in face]
            if (b-a).cross(c-a).normalized().dot(FORWARD) < .15: continue
            front_faces += 1
            for p in triangle_samples(a, b, c):
                samples[tuple(round(v, 7) for v in p)] = p
        if not front_faces: failures.append(m.obj.name+': no front-facing triangles')
        for p in samples.values():
            hit = ray(glass_tree, p+FORWARD*1e-7, FORWARD, .60)
            # Always test backwards as well. A second front glazing layer
            # must not hide the fact that the sign has passed the first one.
            back = ray(glass_tree, p-FORWARD*1e-7, -FORWARD, .60)
            gap = -back[0]-1e-7 if back else (hit[0]+1e-7 if hit else None)
            nearest = glass_tree.tree.find_nearest(p) if glass_tree.tree else (None, None, None, None)
            nearest_distance = nearest[3]
            good = (hit is not None and hit[3].glass_faces[hit[4]]
                    and back is None and hit[2].dot(FORWARD) < -.1
                    and gap >= cfg['min_sign_gap_m'] and nearest_distance is not None
                    and nearest_distance >= cfg['min_sign_gap_m'])
            item = {'sign': m.obj.name, 'sample_unity': xyz(B_TO_U @ p),
                    'signed_forward_gap_m': gap, 'nearest_glass_distance_m': nearest_distance,
                    'hit': hit_record(back or hit), 'forward_hit': hit_record(hit),
                    'backward_hit': hit_record(back),
                    'status': 'PASS' if good else 'FAIL'}
            if not good: failures.append(item)
            if gap is not None and (minimum is None or gap < minimum['signed_forward_gap_m']): minimum = item
            if nearest_distance is not None and (nearest_minimum is None or nearest_distance < nearest_minimum['nearest_glass_distance_m']): nearest_minimum = item
            rows.append(item)
    return result('destination_full_surface_signed_glass_clearance', failures,
                  threshold_m=cfg['min_sign_gap_m'], samples=len(rows), worst_sample=minimum,
                  nearest_glass_sample=nearest_minimum,
                  glass=[m.obj.name for m in glasses], sign=[m.obj.name for m in signs],
                  sampling='All front-facing destination triangles on <=25mm barycentric lattice; forward ray must first enter glass inner face, any rearward glazing hit fails. Sample-to-glass nearest Euclidean distance is also checked. Backward hits are negative, not absolute clearance.')


def inside_solid(mesh, point):
    bb = mesh.blender_bounds
    if any(point[i] < bb['min'][i]-1e-7 or point[i] > bb['max'][i]+1e-7 for i in range(3)):
        return False, [False, False, False]
    votes = []
    for raw in ((1., .371, .193), (-.277, 1., .414), (.229, -.379, 1.)):
        direction = Vector(raw).normalized(); origin = point.copy(); hits = 0
        for _ in range(256):
            pos, _, _, distance = mesh.tree.ray_cast(origin, direction, 50.)
            if pos is None: break
            hits += 1; origin = pos+direction*1e-6
        else:
            raise RuntimeError('Solid parity ray exceeded 256 crossings: '+mesh.obj.name)
        votes.append(bool(hits % 2))
    return sum(votes) >= 2, votes


def signed_body_distance(supports, group, point):
    if group.tree is None: return None, None, []
    near, normal, index, distance = group.tree.find_nearest(point)
    mesh, face = group.owners[index]
    inside = []
    for candidate in supports:
        yes, votes = inside_solid(candidate, point)
        if yes:
            p, n, f, d = candidate.tree.find_nearest(point)
            inside.append((d, p, n, candidate, f, votes))
    if inside:
        d, p, n, m, f, _ = min(inside, key=lambda item: item[0])
        return -d, hit_record((d, p, n, m, f)), [
            {'object': item[3].obj.name, 'inside_votes': item[5]} for item in inside]
    return distance, hit_record((distance, near, normal, mesh, face)), []


def layered_mount_contacts(mesh, supports, group, axis_unity, cfg):
    """Separate a declared extrusion's actual two layers from its edge caps.

    Pairing is verified from world vertices, not inferred from triangle order.
    This mode is for a surface-following shoe with an intentionally buried back.
    """
    axis = (U_TO_B@Vector(axis_unity)).normalized()
    tangent = axis.orthogonal().normalized(); other = axis.cross(tangent).normalized()
    buckets = defaultdict(list)
    for i, p in enumerate(mesh.points):
        buckets[(round(p.dot(tangent)/WELD), round(p.dot(other)/WELD))].append((p.dot(axis), i))
    labels = {}; mates = {}; invalid = []
    for coords, values in buckets.items():
        lo = min(z for z, _ in values); hi = max(z for z, _ in values)
        if hi-lo <= WELD or any(min(abs(z-lo), abs(z-hi)) > WELD for z, _ in values):
            invalid.append({'projection': coords, 'levels': [z for z, _ in values]}); continue
        low_index = min(values)[1]; high_index = max(values)[1]
        for z, i in values:
            labels[i] = 'outer' if abs(z-hi) <= WELD else 'bearing'
            mates[i] = low_index if labels[i] == 'outer' else high_index
    if invalid:
        return [], [{'mount': mesh.obj.name, 'reason': 'Declared two-layer geometry not found', 'examples': invalid[:8]}]
    rows = []; failures = []; counts = Counter()
    for face in mesh.faces:
        kinds = {labels[i] for i in face}
        if len(kinds) != 1: continue  # True edge cap, not either bearing layer.
        kind = next(iter(kinds)); counts[kind] += 1
        a, b, c = [mesh.points[i] for i in face]
        ma, mb, mc = [mesh.points[mates[i]] for i in face]
        for u, w in ((1/3, 1/3), (.2, .2), (.6, .2), (.2, .6)):
            p = a+(b-a)*u+(c-a)*w
            mate = ma+(mb-ma)*u+(mc-ma)*w
            outer_point, back_point = (p, mate) if kind == 'outer' else (mate, p)
            segment = back_point-outer_point; length = segment.length
            entry = ray(group, outer_point, segment.normalized(), length+cfg['max_mount_gap_m'])
            gap = max(0., entry[0]-length) if entry else None
            overlap = None; exit_hit = None
            if entry:
                direction = segment.normalized()
                exit_hit = ray([entry[3]], entry[1]+direction*1e-6, direction, 1.)
                if exit_hit and exit_hit[2].dot(direction) > .05:
                    exit_distance = entry[0]+1e-6+exit_hit[0]
                    overlap = max(0., min(length, exit_distance)-entry[0])
            contact_good = (entry is not None and entry[2].dot(segment.normalized()) < -.05
                            and gap <= cfg['max_mount_gap_m'] and overlap is not None)
            distance, hit, inside = signed_body_distance(supports, group, p)
            good = contact_good and distance is not None and (distance >= cfg['min_mount_exposure_m']
                                                              if kind == 'outer' else True)
            row = {'mount': mesh.obj.name, 'mode': 'verified_extrusion_layers', 'layer': kind,
                   'declared_layer_axis_unity': xyz(Vector(axis_unity)), 'sample_unity': xyz(B_TO_U@p),
                   'signed_nearest_body_distance_m': distance, 'hit': hit, 'inside_fixed_solids': inside,
                   'paired_outer_unity': xyz(B_TO_U@outer_point), 'paired_back_unity': xyz(B_TO_U@back_point),
                   'body_entry': hit_record(entry), 'body_exit': hit_record(exit_hit),
                   'body_overlap_along_layer_axis_m': overlap, 'contact_gap_m': gap,
                   'status': 'PASS' if good else 'FAIL'}
            rows.append(row)
            if not good: failures.append(row)
    if not counts['outer'] or not counts['bearing']:
        failures.append({'mount': mesh.obj.name, 'reason': 'Missing outer or bearing triangles', 'counts': dict(counts)})
    return rows, failures


def mount_contacts(active, cfg, lod):
    mounts = [m for m in active if m.role == 'mirror_mount']
    supports = [m for m in active if m.role in ('shell', 'mount_support')
                and m.part in (0, 17) and not m.glass]
    support_tree = RayGroup(supports)
    failures = []; rows = []
    # Far LOD may explicitly omit the complete mirror group. Missing mounts
    # remain an inventory fact; present feet must always have fixed support.
    sides = Counter('door' if m.bounds['max'][0]+m.bounds['min'][0] > 0 else 'driver' for m in mounts)
    mirror_objects = [m.obj.name for m in active if 'mirror' in m.obj.name.lower() or m.role.startswith('mirror')]
    if mounts or lod < 2 or mirror_objects:
        if sides != {'driver': 1, 'door': 1}:
            failures.append({'reason': 'Expected exactly one fixed mounting shoe on each side',
                             'actual_sides': dict(sides), 'active_mirror_objects': mirror_objects})
    for m in mounts:
        layer_axis = m.obj.get('mount_layer_axis_unity') if hasattr(m.obj, 'get') else None
        if layer_axis is None:
            layer_axis = cfg.get('mirror_mount_layer_axis_unity')
        if layer_axis is not None:
            layer_rows, layer_failures = layered_mount_contacts(m, supports, support_tree, layer_axis, cfg)
            rows.extend(layer_rows); failures.extend(layer_failures)
            continue
        sign = 1 if (m.bounds['min'][0]+m.bounds['max'][0]) > 0 else -1
        towards_axis = U_TO_B @ Vector((-sign, 0., 0.))
        seen = set()
        for f in m.faces:
            a, b, c = [m.points[i] for i in f]
            inward = (b-a).cross(c-a).normalized()
            if inward.dot(towards_axis) < .8: continue
            # Triangle centroids and interior barycentric points avoid exact
            # coplanar cap/edge ray ambiguity while covering the bearing face.
            for u, v in ((1/3, 1/3), (.2, .2), (.6, .2), (.2, .6)):
                p = a+(b-a)*u+(c-a)*v
                key = tuple(round(x, 7) for x in p)
                if key in seen: continue
                seen.add(key)
                origin = p-inward*.12
                hit = ray(support_tree, origin, inward, .28)
                outer = ray([m], origin, inward, .28)
                gap = hit[0]-.12 if hit else None
                # Use actual front surface on the same local-normal section.
                # Global X extrema belong to different sections on a curved
                # foot and can report false protrusion or false burial.
                exposure = hit[0]-outer[0] if hit and outer else None
                good = (hit is not None and hit[2].dot(inward) < -.1
                        and outer is not None and outer[2].dot(inward) < -.1
                        and gap <= cfg['max_mount_gap_m']
                        and exposure >= cfg['min_mount_exposure_m'])
                item = {'mount': m.obj.name, 'sample_unity': xyz(B_TO_U@p),
                        'local_bearing_normal_unity': xyz(B_TO_U@inward),
                        'gap_m': gap, 'outward_exposure_m': exposure,
                        'hit': hit_record(hit), 'shoe_outer_hit': hit_record(outer),
                        'status': 'PASS' if good else 'FAIL'}
                rows.append(item)
                if not good: failures.append(item)
        if not seen: failures.append(m.obj.name+': no inward bearing-face samples')
    return result('mirror_shoe_to_fixed_opaque_structure', failures,
                  mounts=[m.obj.name for m in mounts], samples=rows,
                  maximum_gap_m=cfg['max_mount_gap_m'], minimum_exposure_m=cfg['min_mount_exposure_m'],
                  omitted_group=not mounts,
                  exclusion='No doors, glazing, mirror arms/heads or self-contact accepted as body support.')


def coverage(active, cfg):
    # Exclude accessories: a light, mirror, wiper or destination text cannot
    # stand in for the envelope behind it.
    envelope = [m for m in active if m.part in (0, 17) and m.role != 'mirror_mount']
    envelope_tree = RayGroup(envelope)
    failures = []; count = 0; worst = None
    for region in cfg['coverage_regions']:
        ys = linspace(*region['y'], region['pitch'])
        # Include interval midpoints: a row on a glass/header shared boundary
        # must not be the only evidence for a narrow missing connecting strip.
        ys = sorted(ys+[(a+b)/2 for a, b in zip(ys, ys[1:])])
        for x in linspace(*region['x'], region['pitch']):
            for y in ys:
                p = U_TO_B@Vector((x, y, cfg['front_probe_z']))
                hit = ray(envelope_tree, p, -FORWARD, cfg['front_probe_z']-cfg['front_probe_min_z'])
                count += 1
                if hit is None:
                    failures.append({'region': region['name'], 'origin_unity': xyz(B_TO_U@p)})
                elif worst is None or hit[0] > worst['distance_m']:
                    worst = hit_record(hit)
    return result('front_local_envelope_coverage', failures, ray_count=count, farthest_hit=worst,
                  regions=cfg['coverage_regions'], vertical_interval_midpoints=True,
                  limitation='Finite engineering grid inside intended silhouette; a hit does not prove tangency, curvature continuity, or all sub-grid seams.')


def seam_rays(active, cfg):
    tree = RayGroup([m for m in active if m.part in (0, 17) and m.role != 'mirror_mount'])
    rows = []; failures = []
    for spec in cfg['seam_rays']:
        origin = U_TO_B@Vector(spec['origin_unity'])
        direction = (U_TO_B@Vector(spec['direction_unity'])).normalized()
        hit = ray(tree, origin, direction, float(spec['max_distance_m']))
        good = hit is not None and hit[2].dot(direction) < -.05
        row = {'ray': spec, 'hit': hit_record(hit), 'status': 'PASS' if good else 'FAIL'}
        rows.append(row)
        if not good: failures.append(row)
    return result('independent_corner_section_rays', failures, rows=rows,
                  scope='Additional explicit contract rays; empty means NOT_SUPPLIED, not proof of corner continuity.',
                  coverage_status='MEASURED' if rows else 'NOT_SUPPLIED')


def horizontal_join(active, cfg, name, upward):
    reg = cfg[name+'_region']; wanted = 'ceiling' if upward else 'floor'
    # Check the actual named load-bearing surface. A curved bulkhead can be
    # encountered above that surface without removing the floor beneath it.
    # Keep the complete structural first hit separately; it is not substituted
    # for the required horizontal floor/ceiling hit.
    structural = [m for m in active if m.role in (wanted, 'shell') or
                  (m.part in ((0, 14) if upward else (0,)) and not m.glass)]
    candidates = [m for m in structural if m.role == wanted or
                  any(s in m.obj.name.lower() for s in
                      (('ceiling','roof to cap') if upward else ('floor','footwell')))]
    candidate_tree = RayGroup(candidates)
    full_tree = RayGroup(structural)
    d = U_TO_B@Vector((0, 1 if upward else -1, 0)); failures = []; count = 0; occlusions=[]
    for x in linspace(*reg['x'], reg['pitch']):
        for z in linspace(*reg['z'], reg['pitch']):
            origin = U_TO_B@Vector((x, reg['y'][0] if upward else reg['y'][1], z))
            hit = ray(candidate_tree, origin, d, reg['y'][1]-reg['y'][0])
            first = ray(full_tree, origin, d, reg['y'][1]-reg['y'][0])
            if first and (hit is None or first[3] != hit[3]):
                occlusions.append({'origin_unity':xyz(B_TO_U@origin),
                                   'structural_first_hit':hit_record(first),
                                   'required_surface_hit':hit_record(hit)})
            count += 1
            if hit is None or hit[2].dot(d) > -.65:
                failures.append({'origin_unity': xyz(B_TO_U@origin), 'hit': hit_record(hit)})
    return result('front_'+name+'_horizontal_join', failures, ray_count=count, region=reg,
                  structural_occlusions=occlusions,
                  required_normal_dot_ray_max=-.65)


def audit(cfg):
    collection = bpy.data.collections.get('K18 Front')
    if collection is None: raise RuntimeError('Missing K18 Front source collection')
    objects = list(collection.all_objects)  # Snapshot before evaluated meshes.
    meshes = [Mesh(o) for o in objects if o.type in ('MESH', 'CURVE', 'FONT', 'SURFACE')]
    invalid = [m.obj.name for m in meshes if not m.points or not m.faces or not m.finite]
    if invalid:
        return {'status': 'FAIL', 'checks': [result('finite_nonempty_evaluated_meshes', invalid)],
                'scope': 'Invalid geometry prevents safe BVH and normal evaluation.'}
    selected = [m for m in meshes if m.bounds and
                (m.bounds['max'][2] >= cfg['front_region_min_z'] or m.role in ('sign', 'mirror_mount', 'glass'))]
    geometry = []
    for m in selected:
        geometry.append({'object': m.obj.name, 'role': m.role, 'part': m.part,
                         'first_lod': m.first, 'last_lod': m.last,
                         'unity_bounds': m.bounds, 'vertices': len(m.points),
                         'triangles': len(m.faces), **topology(m)})
    checks = []
    bad_intervals = [m.obj.name for m in meshes if not 0 <= m.first <= m.last <= 2]
    checks.append(result('explicit_lod_intervals', bad_intervals))
    lods = []
    for lod in range(3):
        active = [m for m in meshes if m.active(lod)]
        focused = [m for m in selected if m.active(lod)]
        rows = [g for g in geometry if g['first_lod'] <= lod <= g['last_lod']]
        group = [result('evaluated_front_mesh_integrity', [r['object'] for r in rows if r['status'] != 'PASS'],
                        objects=len(rows), triangles=sum(r['triangles'] for r in rows))]
        failures = []; extrema = []
        for m in focused:
            # Accessories legitimately extend beyond body width (mirrors).
            if m.part not in (0, 17): continue
            bb = m.bounds
            if max(abs(bb['min'][0]), abs(bb['max'][0])) > cfg['max_half_width']+1e-6:
                failures.append(m.obj.name+': body exceeds half-width')
            if bb['max'][1] > cfg['max_height']+1e-6: failures.append(m.obj.name+': body exceeds height')
            if bb['max'][2] > cfg['max_forward']+1e-6: failures.append(m.obj.name+': body exceeds front envelope')
            extrema.append({'object': m.obj.name, 'bounds': bb})
        group.append(result('front_body_engineering_envelope', failures, objects=extrema,
                            limits={k: cfg[k] for k in ('max_half_width', 'max_height', 'max_forward')}))
        group.extend((sign_clearance(active, cfg), mount_contacts(active, cfg, lod),
                      coverage(active, cfg), seam_rays(active, cfg), horizontal_join(active, cfg, 'floor', False),
                      horizontal_join(active, cfg, 'ceiling', True)))
        lods.append({'lod': lod, 'status': 'FAIL' if any(c['status'] == 'FAIL' for c in group) else 'PASS',
                     'checks': group, 'selected_objects': [m.obj.name for m in focused]})
    return {'status': 'FAIL' if any(c['status'] == 'FAIL' for c in checks+lods) else 'PASS',
            'checks': checks, 'lods': lods, 'geometry': geometry,
            'weld_tolerance_m': WELD, 'minimum_cross_squared_m4': MIN_CROSS_SQUARED,
            'scope': 'Read-only saved-source front structural audit; no game, FBX, SWB1, photographic likeness or visual acceptance implied.',
            'limitations': ['Finite ray sampling cannot certify arbitrary sub-grid holes or global self-intersection.',
                            'Engineering envelope and sample regions are explicit model contracts, not manufacturer measurements.',
                            'Source topology/normal checks do not replace final exported LOD and full-assembly image review.',
                            'Mirror feet are tested only when present in that LOD; a completely omitted far-LOD mirror is reported, not silently required.']}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--blend', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--contract', type=Path)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    source = args.blend.resolve(); before = sha(source)
    bpy.ops.wm.open_mainfile(filepath=str(source))
    scene = bpy.context.scene; loaded_frame = scene.frame_current
    scene.frame_set(int(scene.get('export_rest_frame', 1))); bpy.context.view_layer.update()
    cfg = json.loads(json.dumps(DEFAULT))
    encoded = scene.get('k18_front_contract')
    if encoded: cfg.update(json.loads(str(encoded)))
    if args.contract: cfg.update(json.loads(args.contract.read_text(encoding='utf-8-sig')))
    if cfg.get('source_sha256') and cfg['source_sha256'].lower() != before.lower():
        raise RuntimeError('Sidecar contract is bound to a different source SHA256')
    report = audit(cfg)
    report.update(source=str(source), source_sha256=before, source_unchanged=sha(source) == before,
                  verifier_sha256=sha(__file__), coordinate_contract='Unity=(-Blender.x,Blender.z,-Blender.y); metric coordinates.',
                  frame=scene.frame_current, saved_frame=loaded_frame, contract=cfg)
    if not report['source_unchanged']: report['status'] = 'FAIL'
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print('K18_FRONT_AUDIT '+json.dumps({'status': report['status'], 'source_sha256': before,
                                      'report': str(args.report)}, ensure_ascii=False), flush=True)
    if report['status'] != 'PASS': raise RuntimeError('K18 front audit failed; see evidence report')


if __name__ == '__main__': main()
