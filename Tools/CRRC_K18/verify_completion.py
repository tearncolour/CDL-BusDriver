"""Independent saved-source K18 assembly audit; does not save or repair a model.

blender -b --threads 2 --python-exit-code 1 --python Tools/CRRC_K18/verify_completion.py -- \
  --blend ModelSource/CRRC_K18/CRRC_K18.blend --report out/k18-completion/audit.json

Optional --render-dir emits complete assembly ordinary/backface diagnostic pairs
for every LOD. Materials are changed only in memory for the diagnostic pass.
Numerical PASS never substitutes for manual review of those images or game tests.
"""
import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
import sys
from types import SimpleNamespace

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import intersect_ray_tri

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import verify_front as front

B_TO_U = front.B_TO_U
U_TO_B = B_TO_U.inverted()
EPS = 1e-6
CONTACT = .003


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def xyz(v):
    return [float(x) for x in v]


def base(name):
    return re.sub(r'^LOD[012] ', '', name)


def grid(a, b, pitch):
    n = max(1, math.ceil(abs(b-a)/pitch))
    return [a+(b-a)*i/n for i in range(n+1)]


class Mesh:
    def __init__(self, source, points=None):
        self.source = source
        self.obj = source.obj
        self.name = source.obj.name
        self.role = str(self.obj.get('k18_completion_role', ''))
        self.part = source.part
        self.points = points if points is not None else source.unity
        # Unity's coordinate transform reflects handedness. Reverse the source
        # triangle here so BVH normals remain physical outward directions.
        self.faces = [(f[0], f[2], f[1]) for f in source.faces]
        self.tree = BVHTree.FromPolygons(self.points, self.faces, all_triangles=True)
        self.lo = Vector([min(p[i] for p in self.points) for i in range(3)])
        self.hi = Vector([max(p[i] for p in self.points) for i in range(3)])

    def active(self, lod):
        return self.source.active(lod)

    def contains(self, point):
        if any(point[i] <= self.lo[i]+EPS or point[i] >= self.hi[i]-EPS for i in range(3)):
            return False
        near = self.tree.find_nearest(point)
        if near[3] is None or near[3] < EPS:
            return False
        votes = []
        for d in ((1, .379, .173), (-.283, 1, .413), (.239, -.367, 1)):
            d = Vector(d).normalized(); origin = point.copy(); count = 0
            for _ in range(256):
                p, _, _, _ = self.tree.ray_cast(origin, d, 60)
                if p is None:
                    break
                count += 1; origin = p+d*EPS
            else:
                raise RuntimeError('Parity ray exceeded crossing limit: '+self.name)
            votes.append(count % 2)
        return sum(votes) >= 2


class Group:
    def __init__(self, meshes):
        self.meshes = list(meshes); self.owners = []; points = []; faces = []
        for m in self.meshes:
            n = len(points); points.extend(m.points)
            faces.extend(tuple(n+i for i in f) for f in m.faces)
            self.owners.extend((m, i) for i in range(len(m.faces)))
        self.tree = BVHTree.FromPolygons(points, faces, all_triangles=True) if faces else None

    def ray(self, origin, direction, distance):
        if self.tree is None:
            return None
        p, n, i, d = self.tree.ray_cast(Vector(origin), Vector(direction), distance)
        if p is None:
            return None
        m, f = self.owners[i]
        return {'object': m.name, 'triangle': f, 'point': xyz(p), 'normal': xyz(n), 'distance': d}


def face_points(m, i):
    return [m.points[j] for j in m.faces[i]]


def crossing(a, b):
    """Strict edge/triangle crossings; touching coplanar faces are not penetration."""
    for p, q, tri in [(a[i], a[(i+1)%3], b) for i in range(3)]+[(b[i], b[(i+1)%3], a) for i in range(3)]:
        d = q-p
        if d.length < EPS:
            continue
        hit = intersect_ray_tri(*tri, d, p, True)
        if hit is not None:
            t = (hit-p).dot(d)/d.length_squared
            if EPS/d.length < t < 1-EPS/d.length:
                # A ray hitting the *edge* of the other triangle is only a
                # touching witness, not proof of positive solid penetration.
                # Requiring strict interior barycentrics rejects such contacts;
                # the solid containment stage still catches coplanar overlaps.
                u, v, w = tri[1]-tri[0], tri[2]-tri[0], hit-tri[0]
                den = u.dot(u)*v.dot(v)-u.dot(v)**2
                if den <= 1e-24:
                    continue
                b = (v.dot(v)*w.dot(u)-u.dot(v)*w.dot(v))/den
                c = (u.dot(u)*w.dot(v)-u.dot(v)*w.dot(u))/den
                margin = EPS/max(u.length, v.length, EPS)
                if min(b, c, 1-b-c) > margin:
                    return xyz(hit)
    return None


def collision(moving, fixed):
    """BVH broad/narrow surface tests plus contained actual-surface witnesses."""
    if fixed.tree is None:
        return []
    found = {}; pairs = fixed.tree.overlap(moving.tree)
    for fi, mi in pairs:
        other, oi = fixed.owners[fi]
        if other.name in found:
            continue
        p = crossing(face_points(other, oi), face_points(moving, mi))
        if p is not None:
            found[other.name] = {'object': other.name, 'kind': 'surface_crossing',
                                 'point': p, 'moving_triangle': mi, 'fixed_triangle': oi}
    # Surface crossing alone misses a whole small item inside a larger solid.
    # Test actual vertices, never only a bbox centre (a tyre centre is a hole).
    stride = max(1, len(moving.points)//32)
    for other in fixed.meshes:
        if other.name in found or any(moving.hi[i] < other.lo[i] or moving.lo[i] > other.hi[i] for i in range(3)):
            continue
        for p in moving.points[::stride]:
            if other.contains(p):
                found[other.name] = {'object': other.name, 'kind': 'surface_vertex_inside_fixed_solid', 'point': xyz(p)}
                break
        if other.name not in found:
            for p in other.points[::max(1, len(other.points)//24)]:
                if moving.contains(p):
                    found[other.name] = {'object': other.name, 'kind': 'fixed_vertex_inside_moving_solid', 'point': xyz(p)}
                    break
    return list(found.values())


def status(name, rows, bad, **extra):
    print('COMPLETION', name, len(rows), 'samples', len(bad), 'failures', flush=True)
    return {'check': name, 'status': 'FAIL' if bad else 'PASS', 'samples': len(rows),
            'failure_count': len(bad), 'failures': bad, 'evidence': rows, **extra}


def rear_coverage(active, contract):
    wall = Group(m for m in active if m.role == 'rear_wall' or base(m.name).startswith(('Rear lower body', 'Rear destination surround', 'Rear upper cap')))
    glass = Group(m for m in active if m.source.glass and base(m.name).startswith('Rear window'))
    opening = contract.get('rear_window_opening', [-1.04, 1.04, 1.605, 2.335])
    rows = []; bad = []
    for x in grid(-1.22, 1.22, .025):
        for y in grid(.35, 3.15, .025):
            # A narrow top-corner region is outside the rounded body silhouette.
            if y > 3.055 and abs(x) > 1.10:
                continue
            inside_window = opening[0]+.006 < x < opening[1]-.006 and opening[2]+.006 < y < opening[3]-.006
            on_boundary = min(abs(x-opening[0]), abs(x-opening[1]), abs(y-opening[2]), abs(y-opening[3])) < .006
            if on_boundary:
                continue  # Separate edge strips below straddle the true boundary.
            for sense in (-1, 1):
                start = (x, y, -9.02 if sense > 0 else -8.48)
                h = wall.ray(start, (0, 0, sense), .60)
                if inside_window:
                    g = glass.ray(start, (0, 0, sense), .60)
                    ok = h is None and g is not None
                else:
                    g = None; ok = h is not None and h['normal'][2]*sense < -.1
                r = {'x': x, 'y': y, 'direction': sense, 'expected': 'transparent_window' if inside_window else 'opaque_rear_wall', 'hit': h, 'glass': g}
                rows.append(r)
                if not ok: bad.append(r)
    # Inside each of the four opaque lips, only 3 mm away from the opening.
    for edge in ('left', 'right', 'bottom', 'top'):
        vals = grid(opening[2]+.01, opening[3]-.01, .025) if edge in ('left', 'right') else grid(opening[0]+.01, opening[1]-.01, .025)
        for v in vals:
            x = opening[0]-.003 if edge == 'left' else opening[1]+.003 if edge == 'right' else v
            y = opening[2]-.003 if edge == 'bottom' else opening[3]+.003 if edge == 'top' else v
            for sense in (-1, 1):
                h = wall.ray((x, y, -9.02 if sense > 0 else -8.48), (0, 0, sense), .60)
                r = {'edge': edge, 'x': x, 'y': y, 'direction': sense, 'hit': h}; rows.append(r)
                if h is None or h['normal'][2]*sense >= -.1: bad.append(r)
    return status('rear_full_height_and_window_boundary', rows, bad, pitch_m=.025,
                  scope='Local rear structure only; remote seats/lights cannot substitute for the rear wall. Window centre remains genuinely open behind glass.')


def roof_coverage(active):
    structural = [m for m in active if not m.source.glass and
                  (m.role == 'rear_wall' or any(s in m.name.lower() for s in ('roof', 'eave', 'ceiling', 'air-conditioning duct', 'cap side return', 'upper cap')))
                  and not any(s in m.name.lower() for s in ('enclosure', 'graphic', 'lamp', 'vent', 'fin'))]
    group = Group(structural); rows = []; bad = []
    for section, low, high in (('front', -.47, 8.55), ('rear', -8.59, -2.17)):
        for x in grid(-1.13, 1.13, .08):
            for z in grid(low, high, .08):
                for sense in (-1, 1):
                    h = group.ray((x, 3.50 if sense < 0 else 2.52, z), (0, sense, 0), 1.0)
                    r = {'section': section, 'x': x, 'z': z, 'direction': sense, 'hit': h}; rows.append(r)
                    if h is None or not 2.60 < h['point'][1] < 3.35 or h['normal'][1]*sense >= -.05:
                        bad.append(r)
    return status('roof_and_cabin_ceiling_coverage', rows, bad, pitch_m=.08,
                  scope='Structural roof/eave/ceiling/duct surfaces; equipment boxes and decals cannot hide a missing roof. Articulation opening excluded.')


def floor_mesh(m):
    return m.role in ('walking_floor', 'stair', 'equipment_plinth', 'deck_edge') or any(s in base(m.name) for s in ('continuous low-floor aisle', 'Outboard floor', 'floor threshold', 'turntable'))


def audit_box(name, lo, hi):
    """Closed analytical solid used only in memory, never added to the scene."""
    p = [Vector((x, y, z)) for x, y, z in ((lo[0],lo[1],lo[2]), (hi[0],lo[1],lo[2]),
         (hi[0],hi[1],lo[2]), (lo[0],hi[1],lo[2]), (lo[0],lo[1],hi[2]),
         (hi[0],lo[1],hi[2]), (hi[0],hi[1],hi[2]), (lo[0],hi[1],hi[2]))]
    quads = ((0,3,2,1), (4,5,6,7), (0,1,5,4), (3,7,6,2), (0,4,7,3), (1,2,6,5))
    physical = [(q[0],q[j],q[j+1]) for q in quads for j in (1,2)]
    obj = SimpleNamespace(name=name, get=lambda k,d=None:d)
    source = SimpleNamespace(obj=obj, part=-1, unity=p, faces=[(a,c,b) for a,b,c in physical])
    return Mesh(source)


def narrow_phase_controls():
    cube = audit_box('closed reference cube', (0,0,0), (1,1,1))
    cases = [('contained solid', (.2,.2,.2), (.4,.4,.4), True),
             ('crossing solid', (.8,.2,.2), (1.2,.4,.4), True),
             ('separated solid', (1.01,.2,.2), (1.21,.4,.4), False),
             ('exact surface contact', (1.,.2,.2), (1.2,.4,.4), False)]
    rows = []; bad = []
    for name, low, high, expected in cases:
        hits = collision(audit_box(name,low,high), Group([cube]))
        r = {'case': name, 'expected_collision': expected, 'actual_collision': bool(hits), 'evidence': hits}; rows.append(r)
        if bool(hits) != expected: bad.append(r)
    return status('collision_narrow_phase_controls', rows, bad)


def seat_supports(active, contract):
    floors = Group(m for m in active if floor_mesh(m)); whole = Group(active)
    rows = []; bad = []; seats = contract.get('seats', [])
    if not seats:
        bad.append({'reason': 'Missing explicit seat/foot-zone contract; no zero-seat PASS'})
    feet = [m for m in active if m.role == 'seat_support' and 'floor foot' in m.name]
    if len(feet) != len(seats)*2:
        bad.append({'reason': 'Expected two real floor feet per declared seat', 'feet': len(feet), 'seats': len(seats)})
    for seat in seats:
        zone = seat['foot_zone_unity']; y = seat['foot_floor_y']
        prefix = seat['cushion_object']
        actual = [m for m in active if base(m.name) == prefix]
        if len(actual) != 1:
            bad.append({'seat': prefix, 'reason': 'Seat must actually exist once in this LOD', 'count': len(actual)})
        for x in grid(zone['x'][0]+.01, zone['x'][1]-.01, .06):
            for z in grid(zone['z'][0]+.01, zone['z'][1]-.01, .06):
                h = whole.ray((x, y+.14, z), (0, -1, 0), .35)
                # The authored articulation threshold is a distinct 5 mm
                # raised walking seam, not a levitating foot support. Record
                # this explicit exception without relaxing seat-foot contact.
                seam = h is not None and base(h['object']).startswith('Articulation floor threshold')
                height_ok = h is not None and (abs(h['point'][1]-y) <= CONTACT or
                                               (seam and -.0001 <= h['point'][1]-y <= .0051))
                r = {'seat': prefix, 'kind': 'shoe_floor', 'point': [x, y, z], 'hit': h,
                     'raised_articulation_seam': seam}; rows.append(r)
                if h is None or not height_ok or h['normal'][1] < .8 or not any(m.name == h['object'] for m in floors.meshes):
                    bad.append(r)
    for foot in feet:
        samples = {}
        for fi, f in enumerate(foot.faces):
            tri = [foot.points[i] for i in f]
            normal = (tri[1]-tri[0]).cross(tri[2]-tri[0]).normalized()
            if normal.y > -.8 or max(p.y for p in tri) > foot.lo.y+.001:
                continue
            p = sum(tri, Vector())/3
            samples[tuple(round(x, 7) for x in p)] = p
        if not samples:
            bad.append({'support': foot.name, 'reason': 'No real bottom bearing face found'})
        for p in samples.values():
            h = floors.ray(p+Vector((0, .025, 0)), (0, -1, 0), .10)
            r = {'support': foot.name, 'kind': 'bearing_face', 'point': xyz(p), 'hit': h}; rows.append(r)
            if h is None or abs(p.y-h['point'][1]) > CONTACT or h['normal'][1] < .8:
                bad.append(r)
    return status('seated_shoe_floor_and_real_bearing_faces', rows, bad, contact_tolerance_m=CONTACT)


def seated_knee_clearance(active, contract):
    fixed = Group(active); rows = []; bad = []
    for seat in contract.get('seats', []):
        zone = seat.get('knee_zone_unity')
        if not zone:
            bad.append({'seat': seat['cushion_object'], 'reason': 'Missing explicit knee envelope'}); continue
        box = audit_box('seated knee volume', [zone[a][0] for a in ('x','y','z')], [zone[a][1] for a in ('x','y','z')])
        hits = collision(box, fixed)
        r = {'seat': seat['cushion_object'], 'zone': zone, 'intersections': hits}; rows.append(r)
        if hits: bad.append(r)
    return status('seated_knee_actual_mesh_clearance', rows, bad,
                  scope='Declared study envelope versus whole assembled scene; not an ergonomic/manufacturer certification.')


def rear_stair_aisle(active, contract):
    whole = Group(active); floors = Group(m for m in active if floor_mesh(m))
    rows = []; bad = []
    for surface in contract.get('walking_surfaces', []):
        if not surface['object'].startswith(('Rear raised saloon deck', 'Rear saloon stair tread')):
            continue
        low = max(-.25, surface['x'][0]+.015); high = min(.25, surface['x'][1]-.015)
        if high < low:
            continue
        for x in grid(low, high, .05):
            for z in grid(surface['z'][0]+.01, surface['z'][1]-.01, .05):
                y = surface['top']; h = floors.ray((x,y+.20,z), (0,-1,0), .25)
                foreground = whole.ray((x,y+.20,z), (0,-1,0), .25)
                r = {'surface': surface['object'], 'expected_point': [x,y,z], 'hit':h,
                     'first_complete_assembly_hit': foreground}; rows.append(r)
                if h is None or abs(h['point'][1]-y)>CONTACT or h['normal'][1]<.8:
                    bad.append(r)
    if not rows:
        bad.append({'reason':'No declared actual rear walking surfaces'})
    return status('rear_stair_and_platform_central_aisle', rows, bad,
                  scope='Structural support continuity across a 0.50 m central sample strip of rear treads/platform. Full-scene first hits are also recorded: 3mm tread nosings and outer rail feet are not substitute floor support and do not establish an unobstructed 0.50m aisle. Not an accessibility or regulatory clearance certification.')


def contact(a, b, tolerance=.0002):
    if any(a.lo[i] > b.hi[i]+tolerance or b.lo[i] > a.hi[i]+tolerance for i in range(3)):
        return None
    hits = collision(a, Group([b]))
    if hits:
        return hits[0]
    # Contact without positive penetration: actual vertex-to-triangle distance.
    # Do not connect parts merely because expanded bounds overlap.
    for one, other in ((a,b), (b,a)):
        for p in one.points:
            q, _, face, d = other.tree.find_nearest(p, tolerance)
            if q is not None and d <= tolerance:
                return {'kind': 'surface_contact', 'point': xyz(p), 'other_point': xyz(q),
                        'gap_m': d, 'object': other.name, 'triangle': face}
    return None


def seat_structural_connectivity(active, contract):
    seats = contract.get('seats', []); rows = []; bad = []
    assigned = {s['cushion_object']: [] for s in seats}
    for m in active:
        if m.role not in ('seat_support','seat_structure','seat_cushion','seat_back') or not seats:
            continue
        centre = (m.lo+m.hi)*.5
        seat = min(seats, key=lambda s: (centre.x-s['cushion_unity'][0])**2+(centre.z-s['cushion_unity'][2])**2)
        assigned[seat['cushion_object']].append(m)
    for seat in seats:
        members = assigned[seat['cushion_object']]; adjacency = defaultdict(set); edges = []
        for i, a in enumerate(members):
            for j in range(i+1, len(members)):
                b = members[j]; witness = contact(a,b)
                if witness is not None:
                    adjacency[i].add(j); adjacency[j].add(i)
                    edges.append({'a': a.name, 'b': b.name, 'witness': witness})
        reached = {i for i,m in enumerate(members) if 'floor foot' in m.name}
        todo = list(reached)
        while todo:
            for i in adjacency[todo.pop()]-reached:
                reached.add(i); todo.append(i)
        detached = [m.name for i,m in enumerate(members) if i not in reached]
        r = {'seat': seat['cushion_object'], 'members': [m.name for m in members],
             'contact_edges': edges, 'detached_members': detached}; rows.append(r)
        if detached or not reached: bad.append(r)
    return status('seat_back_cushion_and_support_connection_to_real_feet', rows, bad,
                  contact_tolerance_m=.0002,
                  scope='Whole seat structural graph from its actual bearing feet. Positive mesh penetration or <=0.2mm actual surface contact creates an edge; unrelated handrails are excluded.')


def wheelhouse_coverage(active, contract):
    group = Group(m for m in active if m.role == 'wheelhouse')
    rows = []; bad = []
    wheels = contract.get('wheelhouses', [])
    if len(wheels) != 6:
        bad.append({'reason': 'Expected six separately declared wheelhouse cavities', 'count': len(wheels)})
    def cast(kind, wheel, origin, direction, distance):
        h = group.ray(origin, direction, distance)
        r = {'kind': kind, 'axle': wheel['axle'], 'side': wheel['side'],
             'origin': xyz(origin), 'direction': xyz(direction), 'hit': h}; rows.append(r)
        if h is None or Vector(h['normal']).dot(Vector(direction)) >= -.1:
            bad.append(r)
    for w in wheels:
        side = w['side']; z = w['centre_z']; ri = w['cavity_radius']; cy = .49
        # Rays start in the real tyre cavity and only the structural liner can
        # receive them. Seats, exterior skins and wheels never satisfy closure.
        for x in grid(.642, 1.235, .04):
            for angle in grid(.015, math.pi-.015, .045):
                radial = Vector((0, math.sin(angle), math.cos(angle)))
                cast('crown_from_cavity', w, Vector((side*x, cy, z))+radial*(ri-.025), radial, .09)
        for yy in grid(.275, cy+ri-.015, .04):
            for zz in grid(z-ri+.012, z+ri-.012, .04):
                if yy > cy and (yy-cy)**2+(zz-z)**2 > (ri-.012)**2:
                    continue
                cast('aisle_wall_from_cavity', w, Vector((side*.68, yy, zz)), Vector((-side, 0, 0)), .09)
        for direction in (-1, 1):
            for xx in grid(.645, 1.235, .04):
                for yy in grid(.275, .48, .035):
                    cast('floor_end_return', w, Vector((side*xx, yy, z+direction*(ri-.025))), Vector((0, 0, direction)), .10)
    return status('wheelhouse_cavity_structural_continuity', rows, bad,
                  scope='Crown, aisle wall and two bottom end returns, interrogated from the actual cavity. Road and exterior wheel opening intentionally remain open.')


def door_frame_coverage(active):
    group = Group(m for m in active if base(m.name).startswith(('Door fixed jamb', 'Yellow boarding threshold', 'Door overhead mechanism')))
    rows = []; bad = []
    for index, z in enumerate((7.83, 2.15, -3.78)):
        locations = []
        for sign in (-1, 1):
            locations.extend(('jamb', yy, z+sign*.642) for yy in grid(.34, 2.60, .035))
        for zz in grid(z-.62, z+.62, .025):
            locations.extend((('header', 2.61, zz), ('threshold', .34, zz)))
        for kind, yy, zz in locations:
            for direction in (-1, 1):
                origin = (1.42 if direction < 0 else .94, yy, zz)
                h = group.ray(origin, (direction, 0, 0), .50)
                r = {'door_index': index, 'kind': kind, 'origin': list(origin), 'direction': direction, 'hit': h}; rows.append(r)
                if h is None or h['normal'][0]*direction >= -.1: bad.append(r)
    return status('door_four_fixed_boundary_members', rows, bad,
                  scope='True jamb/header/threshold solids from both sides; moving leaves and surrounding seats cannot mask missing frame members.')


def door_checks(active, contract, frame, rest=False):
    doors = [m for m in active if 4 <= m.part <= 9]
    obstacles = Group(m for m in active if not 4 <= m.part <= 9 and
                      (m.role in ('window_sill', 'seat_cushion', 'seat_back', 'seat_structure', 'seat_support', 'equipment_cabinet', 'cab_divider', 'wheelhouse', 'door_header')
                       or m.part in (10, 11, 12, 13)))
    rows = []; bad = []
    if not doors: bad.append({'reason': 'No actual door leaves'})
    leaf_groups = defaultdict(list)
    for m in doors:
        p = m.obj
        while p is not None and not ('leaf' in p.name.lower() and 'pivot' in p.name.lower()):
            p = p.parent
        if p is not None:
            leaf_groups[p.name].append(m)
    if len(leaf_groups) != 6:
        bad.append({'reason': 'Three doors require six actual leaf-pivot assemblies', 'count': len(leaf_groups)})
    for name, members in leaf_groups.items():
        if not any(m.source.glass for m in members) or not any(not m.source.glass for m in members):
            bad.append({'reason': 'Door leaf needs both actual glazing and opaque frame at this LOD', 'pivot': name})
    for m in doors:
        hits = collision(m, obstacles)
        r = {'door_object': m.name, 'part': m.part, 'frame': frame, 'intersections': hits}; rows.append(r)
        if hits: bad.append(r)
    if rest:
        # Independently interrogate the walk-through aperture. Only fixed
        # obstacles participate, so a closed door is not its own obstruction.
        for d, z in enumerate((7.83, 2.15, -3.78)):
            for zz in grid(z-.53, z+.53, .06):
                for y in grid(.45, 2.30, .08):
                    h = obstacles.ray((1.35, y, zz), (-1, 0, 0), .39)
                    r = {'door_index': d, 'kind': 'fixed_aperture', 'point': [1.35, y, zz], 'hit': h}; rows.append(r)
                    if h: bad.append(r)
    return status('door_actual_cabin_clearance_frame_'+str(frame), rows, bad,
                  scope='Moving complete leaf meshes versus seats/sills/rails/cabinet/wheelhouses; intended hinge and seal contacts excluded. Does not certify unmeasured door linkage trajectory.')


def wheel_checks(active):
    wheels = [m for m in active if m.part in (19, 20, 21, 22)]
    fixed = Group(m for m in active if m.part not in (19, 20, 21, 22))
    groups = defaultdict(list)
    for m in wheels:
        p = m.obj.parent
        while p is not None and not str(p.get('bone', '')).startswith('Wheel'):
            p = p.parent
        if p is None:
            raise RuntimeError('Wheel mesh has no actual wheel pivot: '+m.name)
        groups[p.name].append((m, B_TO_U@p.matrix_world.translation))
    rows = []; bad = []
    if len(groups) != 6: bad.append({'reason': 'Expected six actual wheel pivots for three axles', 'count': len(groups)})
    for name, members in groups.items():
        angles = (0, -15, 15, -25, 25, -35, 35) if members[0][0].part in (19, 20) else (0,)
        for angle in angles:
            rot = Matrix.Rotation(math.radians(angle), 3, 'Y'); hits = []
            for m, centre in members:
                posed = Mesh(m.source, [centre+rot@(p-centre) for p in m.points])
                for hit in collision(posed, fixed):
                    hits.append({'moving_object': m.name, **hit})
            r = {'wheel': name, 'steering_degrees': angle, 'intersections': hits}; rows.append(r)
            if hits: bad.append(r)
    return status('all_axle_meshes_front_steering_envelope', rows, bad,
                  scope='All tyre/rim/hub/nut triangles, seven front steering angles and straight middle/rear dual wheels against complete remaining assembly. Surface intersection plus contained-surface witnesses; no bbox-only acceptance.')


def structure_integrity(active):
    rows = []; bad = []
    for m in active:
        if m.role not in ('wheelhouse', 'rear_wall', 'walking_floor', 'stair', 'deck_edge', 'door_header') and not base(m.name).startswith(('Door fixed jamb', 'Yellow boarding threshold', 'Door overhead mechanism')):
            continue
        r = {'object': m.name, **front.topology(m.source)}; rows.append(r)
        if r['status'] != 'PASS': bad.append(r)
    if not any(m.role == 'wheelhouse' for m in active):
        bad.append({'reason': 'Missing explicit real wheelhouse geometry'})
    return status('structural_closed_mesh_and_normal_integrity', rows, bad,
                  weld_tolerance_m=front.WELD, min_triangle_cross_squared_m4=front.MIN_CROSS_SQUARED)


VIEWS = [
    ('saloon-front-low', (0, .67, -3.25), (0, .87, 5.5), 18, 1),
    ('rear-steps-low', (0, .67, -2.50), (0, 1.1, -8.30), 20, 1),
    ('rear-seats-low', (0, 1.39, -8.30), (0, 1.25, -4.4), 18, 1),
    ('front-wheelhouse-low', (0, .65, 5.05), (-.8, .72, 6.37), 20, 1),
    ('ceiling', (0, 1.80, -4.3), (0, 2.65, 6), 18, 1),
    ('rear-complete', (7, 3.8, -14), (0, 1.6, -7.2), 46, 1),
    ('door-side-closed', (22, 3.5, .2), (0, 1.4, .2), 42, 1),
    ('door-side-half', (22, 3.5, .2), (0, 1.4, .2), 42, 65),
    ('door-side-open', (22, 3.5, .2), (0, 1.4, .2), 42, 85),
]


def render_pairs(objects, directory, source_sha, selected):
    directory = directory.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene; rows = []
    data = bpy.data.cameras.new('Completion audit camera')
    camera = bpy.data.objects.new(data.name, data); scene.collection.objects.link(camera)
    scene.camera = camera; camera.data.clip_start = .02; camera.data.clip_end = 100
    scene.render.engine = 'CYCLES'; scene.cycles.samples = 8; scene.cycles.use_denoising = True
    scene.render.resolution_x = 768; scene.render.resolution_y = 480; scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    for i, pos in enumerate(((0, 2.1, 4), (0, 2.1, -.2), (0, 2.0, -4.1), (0, 2.4, -7.2))):
        data = bpy.data.lights.new('Completion inspection fill '+str(i), 'POINT'); data.energy = 160; data.shadow_soft_size = .8
        ob = bpy.data.objects.new(data.name, data); scene.collection.objects.link(ob); ob.location = U_TO_B@Vector(pos)
    diagnostic = bpy.data.materials.new('Completion backface diagnostic'); diagnostic.use_nodes = True
    nodes = diagnostic.node_tree.nodes; nodes.clear(); links = diagnostic.node_tree.links
    out = nodes.new('ShaderNodeOutputMaterial'); emission = nodes.new('ShaderNodeEmission'); emission.inputs['Strength'].default_value = .8
    geom = nodes.new('ShaderNodeNewGeometry'); mix = nodes.new('ShaderNodeMixRGB')
    mix.inputs[1].default_value = (.72, .72, .72, 1); mix.inputs[2].default_value = (1, 0, .35, 1)
    links.new(geom.outputs['Backfacing'], mix.inputs[0]); links.new(mix.outputs[0], emission.inputs[0]); links.new(emission.outputs[0], out.inputs[0])
    original = {o.name: list(o.data.materials) for o in objects if o.type in ('MESH', 'CURVE', 'FONT', 'SURFACE')}
    views = [v for v in VIEWS if not selected or v[0] in selected]
    for lod in range(3):
        for o in objects:
            o.hide_render = not int(front.inherited(o, 'first_lod', 0)) <= lod <= int(front.inherited(o, 'last_lod', 2))
        for name, pos, target, lens, frame in views:
            scene.frame_set(frame); camera.location = U_TO_B@Vector(pos)
            camera.rotation_euler = (U_TO_B@Vector(target)-camera.location).to_track_quat('-Z', 'Y').to_euler(); camera.data.lens = lens
            for mode in ('ordinary', 'backface'):
                for o in objects:
                    if o.name not in original: continue
                    for j, mat in enumerate(original[o.name]):
                        o.data.materials[j] = diagnostic if mode == 'backface' and mat and not mat.get('glass', False) else mat
                path = directory/f'lod{lod}-{name}-{mode}.png'; scene.render.filepath = str(path)
                bpy.ops.render.render(write_still=True)
                rows.append({'path': str(path), 'sha256': sha(path), 'lod': lod, 'frame': frame, 'view': name, 'mode': mode,
                             'camera_unity': list(pos), 'target_unity': list(target), 'lens_mm': lens})
                (directory/'render-manifest.json').write_text(json.dumps({'source_sha256': source_sha, 'status': 'PENDING_MANUAL_REVIEW',
                    'scope': 'Saved source complete assembly, explicit LOD selection, Cycles 8 samples at 768x480, original studio plus four 160W inspection lights. Backface material diagnostic keeps original glass. Fast geometry review supplemented by precise mesh probes; not final material, SWB1 or game rendering.', 'images': rows}, indent=2), encoding='utf-8')
    return rows


def run(args):
    source = args.blend.resolve(); digest = sha(source)
    bpy.ops.wm.open_mainfile(filepath=str(source)); scene = bpy.context.scene
    scene.frame_set(int(scene.get('export_rest_frame', 1))); bpy.context.view_layer.update()
    objects = sorted({o for name in ('K18 Front', 'K18 Rear', 'K18 Joint') if bpy.data.collections.get(name)
                      for o in list(bpy.data.collections[name].all_objects) if o.type in ('MESH', 'CURVE', 'FONT', 'SURFACE')}, key=lambda o: o.name)
    contract = json.loads(scene.get('k18_interior_contract', '{}'))
    if args.render_only:
        report = json.loads(args.report.read_text(encoding='utf-8'))
        assert report['source_sha256'] == digest, 'Existing geometry report belongs to a different saved source'
        assert args.render_dir, '--render-only requires --render-dir'
        report['images'] = render_pairs(objects, args.render_dir, digest, args.views)
        report['render_checker_sha256'] = sha(__file__)
        assert sha(source) == digest, 'Rendering changed the saved source file'
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        return 1 if report['failure_count'] else 0
    report = {'source': str(source), 'source_sha256': digest, 'checker_sha256': sha(__file__),
              'front_checker_sha256': sha(Path(front.__file__)), 'coordinate_frame': 'physical Unity metres (+X doors,+Y up,+Z front)',
              'contract': contract, 'status': 'RUNNING', 'lods': [], 'manual_image_review': 'PENDING',
              'limitations': ['Sampled mesh interrogation does not prove all possible microscopic gaps.',
                             'Three requested door poses are checked, not a complete linkage or every continuous motion instant.',
                             'Support/foot-zone dimensions are explicit study layout data, not verified manufacturer dimensions.',
                             'This is saved-source geometry, not exported SWB1/native GPU mesh or actual game verification.']}
    report['controls'] = narrow_phase_controls()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    meshes = [Mesh(front.Mesh(o)) for o in objects]
    for lod in range(3):
        active = [m for m in meshes if m.active(lod)]
        checks = [structure_integrity(active), rear_coverage(active, contract), roof_coverage(active),
                  seat_supports(active, contract), rear_stair_aisle(active, contract), seated_knee_clearance(active, contract), seat_structural_connectivity(active, contract), wheelhouse_coverage(active, contract),
                  door_frame_coverage(active), wheel_checks(active), door_checks(active, contract, 1, True)]
        row = {'lod': lod, 'checks': checks}; report['lods'].append(row)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    for frame in (65, 85):
        scene.frame_set(frame); bpy.context.view_layer.update()
        changed = {o.name: Mesh(front.Mesh(o)) for o in objects if 4 <= int(front.inherited(o, 'cdlc_part', -1)) <= 9}
        posed = [changed.get(m.name, m) for m in meshes]
        for lod in range(3):
            report['lods'][lod]['checks'].append(door_checks([m for m in posed if m.active(lod)], contract, frame))
    checks = [c for r in report['lods'] for c in r['checks']]
    report['failure_count'] = sum(c['failure_count'] for c in checks)+report['controls']['failure_count']
    report['status'] = 'FAIL' if report['failure_count'] else 'PASS_MEASURED_GEOMETRY'
    report['source_unchanged'] = sha(source) == digest
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    if args.render_dir:
        report['images'] = render_pairs(objects, args.render_dir, digest, args.views)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    assert sha(source) == digest, 'Audit changed the saved source file'
    print('COMPLETION_AUDIT', report['status'], report['failure_count'], flush=True)
    return 1 if report['failure_count'] else 0


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--render-dir', type=Path)
    parser.add_argument('--render-only', action='store_true', help='Use a prior numerical report bound to this exact source, then render')
    parser.add_argument('--views', nargs='*')
    arguments = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    sys.exit(run(arguments))
