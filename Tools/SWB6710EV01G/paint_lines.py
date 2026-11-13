"""The white livery lines as paint (user review 2026-10-19: "这个白色条带实际上是车辆的一个涂装，而不是一个机械结构或者物理突起").

Executed in build_model.py's namespace after every shell is built and before taper.py (which leaves everything below 1.6 m where it
is, so the lines keep their place on the tapered body). Each line paint_line() recorded (side_shell.py: the factory white U stripes
and the sill dashes; rear_shell.py: the inverted U round the service hatch) becomes a single-sided ribbon PAINT_OFFSET (2 mm) off the aqua
paint:

* its centre line and four more points across it are sampled every PAINT_STEP along the line (a smooth line is first rounded by corner cutting,
  and its tightest bend must stay wider than the ribbon, or the inner edge would fold);
* every sample is cast onto the paint only (the body's faces with the aqua material, the hidden wrap skins left out) along the
  line's axis from outside, the first paint the eye would see; a sample on a panel's edge or a hatch's rim does not face out
  and is cut away;
* the ribbon is cut where a sample finds no paint (a black seal, a lamp pod), the paint folds or steps (a panel seam, a proud charging
  hatch's edge), so no face spans a step or covers a seal;
* straight stretches are thinned to PAINT_TOLERANCE, which is under the offset, so a chord across a curved panel stays above it;
* vertex normals are the body's own (interpolated corner normals), so a ribbon shades like the paint under it.

The build fails if a line cannot be laid, if a vertex or face centre ends up buried in or floating off the paint, or if a ribbon
loses more than a small share of its samples. ModelSource/SWB6710EV01G/paint-lines.json records every line.
"""
import bmesh as _pl_bmesh
from mathutils.bvhtree import BVHTree as _PL_BVH

PAINT_OFFSET = .002         # off the paint: 2 mm keeps chords over curved panels and flush joints clear, and reads as paint
# The far LODs reduce the panels under the lines (build_model.py's FBX .38 / .12, write_runtime.py's .32 / .085): their surfaces move by a
# few millimetres (verify_paint_lines.py: up to 4.2 mm, the door-side rear run at the SWB1's LOD2). Every line is laid twice: at
# PAINT_OFFSET for LOD0 and at PAINT_OFFSET_FAR for LOD1 and LOD2 (cdlc_lods; only the near one renders in Blender), so no LOD buries it.
PAINT_OFFSET_FAR = .005
PAINT_STEP = .005
PAINT_TOLERANCE = .0005
PAINT_REACH = .06          # how far from the line's drawing plane the paint may lie (a proud hatch, a corner return)
PAINT_MAX_GAP = .10        # share of a line's samples allowed without paint (seals, lamp pods, a hatch's edges)
PAINT_RAILS = (-1., -.5, 0., .5, 1.)   # across the width: a section lies on paint only if all five do
PAINT_FLAT = math.cos(math.radians(60))  # and their faces agree within this, across and along: a rolled rim passes (the rear
                                         # U's legs follow the hatch recess's 4 cm rim through 45 degrees), a hatch's square edge not
PAINT_SMOOTH = math.cos(math.radians(10))  # a corner normal further than this from its face is a boundary's, not the panel's
PAINT_FACING = .5          # the paint under a line faces its axis at least this much (cos 60 degrees)
PAINT_STRETCH = 1.5        # neighbouring points may lie at most this times their drawn spacing (+2 mm) apart
PAINT_SEAM = .001          # and at most this far apart along the paint's normal: a deeper step is a panel seam or a hatch
PAINT_COVER = .0005        # a surface this much or more in front of the paint along the axis covers it
PAINT_BRIDGE = .030        # a seam break up to this long (drawn) is painted over


def _pl_out(normal, at):
    """The normal turned away from the bus's centre line (the body is convex round it where the lines run)."""
    return normal if normal.dot(Vector((at.x, at.y, 0))) >= 0 else -normal


def _pl_paint_bvh(paint):
    """One world-space tree of the paint's triangles, with their panel and corner normals."""
    deps = bpy.context.evaluated_depsgraph_get()
    verts, tris, owner, corner = [], [], [], []
    for obj in MASTER.all_objects:
        # outside paint only: the hidden wrap skins and the saloon's lining (aqua too, 4 cm in) are no ground for a line
        if obj.type not in {'MESH', 'CURVE'} or obj.hide_render or obj.name.startswith('Wrap surface') or 'lining' in obj.name.lower():
            continue
        if not any(slot.material and slot.material.name == paint.name for slot in obj.material_slots):
            continue
        ev = obj.evaluated_get(deps)
        me = ev.to_mesh()
        me.calc_loop_triangles()
        world = obj.matrix_world
        normal_world = world.to_3x3().inverted_safe().transposed()
        mats = [m.name if m else None for m in me.materials]
        normals = me.corner_normals
        base = len(verts)
        verts.extend(world @ v.co for v in me.vertices)
        for t in me.loop_triangles:
            if t.material_index >= len(mats) or mats[t.material_index] != paint.name:
                continue
            tris.append(tuple(base + i for i in t.vertices))
            owner.append(obj.name)
            corner.append(tuple((normal_world @ normals[l].vector).normalized() for l in t.loops))
        ev.to_mesh_clear()
    if not tris:
        raise RuntimeError('paint_lines: no paint faces found')
    return _PL_BVH.FromPolygons(verts, tris), verts, tris, owner, corner


def _pl_blockers():
    """Every surface the eye can see from outside (any material): a line lies only where the first surface along its axis is the
    paint itself (a hatch's black seal, a lamp pod or a proud hatch in front of the panel hides it)."""
    deps = bpy.context.evaluated_depsgraph_get()
    verts, tris = [], []
    for obj in MASTER.all_objects:
        if obj.type not in {'MESH', 'CURVE', 'FONT'} or obj.hide_render or obj.name.startswith('Wrap surface') or obj.get('cdlc_paint_line'):
            continue
        ev = obj.evaluated_get(deps)
        me = ev.to_mesh()
        me.calc_loop_triangles()
        base = len(verts)
        verts.extend(obj.matrix_world @ v.co for v in me.vertices)
        tris.extend(tuple(base + i for i in t.vertices) for t in me.loop_triangles)
        ev.to_mesh_clear()
    return _PL_BVH.FromPolygons(verts, tris)


def _pl_corner_cut(points, rounds=3):
    """Chaikin corner cutting with the end points kept."""
    for _ in range(rounds):
        out = [points[0]]
        for a, b in zip(points, points[1:]):
            out.append((.75 * a[0] + .25 * b[0], .75 * a[1] + .25 * b[1]))
            out.append((.25 * a[0] + .75 * b[0], .25 * a[1] + .75 * b[1]))
        out.append(points[-1])
        points = out
    return points


def _pl_resample(points, step):
    out = []
    for a, b in zip(points, points[1:]):
        n = max(1, math.ceil(math.hypot(b[0] - a[0], b[1] - a[1]) / step))
        out.extend((a[0] + (b[0] - a[0]) * i / n, a[1] + (b[1] - a[1]) * i / n) for i in range(n))
    out.append(points[-1])
    return out


def _pl_min_radius(points):
    best = float('inf')
    for a, b, c in zip(points, points[1:], points[2:]):
        ab, bc, ca = math.dist(a, b), math.dist(b, c), math.dist(c, a)
        area2 = abs((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]))
        if area2 > 1e-12:
            best = min(best, ab * bc * ca / (2 * area2))
    return best


def _pl_place(line, u, v):
    """The drawing point (u, v) of a line in 3D, the ray's origin outside and its direction."""
    if line['axis'] == 'x':
        s = line['side']
        return Vector((s * SIDE, u, v)), Vector((s * (SIDE + .3), u, v)), Vector((-s, 0, 0))
    # the tail is sculpted (rear_shell.py's rear_y): it lies up to 12 cm ahead of the bus's end
    return Vector((u, rear_y(u, v), v)), Vector((u, HALF + .4, v)), Vector((0, -1, 0))


def _pl_cast(tree, verts, tris, owner, corner, line, u, v, offset=PAINT_OFFSET):
    plane, origin, ray = _pl_place(line, u, v)
    # the first paint along the line's axis, as the eye sees it: no nearest-point fallback (at a proud hatch's rounded edge it found
    # the panel under the hatch and laid the line inside it); a grazing hit is rejected by the facing test and cuts the line
    hit = tree.ray_cast(origin, ray, 1.0)
    if hit[0] is None:
        return None
    loc, _n, index, _d = hit
    # something else in front of the paint (the charging hatch's seal ring): the eye does not see the paint there
    if _PL_BLOCKERS.ray_cast(origin, ray, hit[3] - PAINT_COVER)[0] is not None:
        return None
    off_plane = (loc - plane).dot(ray)
    if abs(off_plane) > PAINT_REACH:
        return None
    a, b, c = (verts[i] for i in tris[index])
    # barycentric weights of the hit on its triangle -> the body's own shading normal there
    v0, v1, v2 = b - a, c - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-12
    wb, wc = (d11 * d20 - d01 * d21) / den, (d00 * d21 - d01 * d20) / den
    na, nb, nc = corner[index]
    face = _pl_out((b - a).cross(c - a).normalized(), loc)
    normal = _pl_out((na * (1 - wb - wc) + nb * wb + nc * wc).normalized(), loc)
    # at a panel's boundary its corner normals lean into the boundary return (the tail's two shells meet under the rear U's
    # top bar at 27 degrees): there the face's own normal, so the line neither shifts nor shades a dark seam
    if normal.dot(face) < PAINT_SMOOTH:
        normal = face
    return loc + normal * offset, normal, owner[index], face


def _pl_keep(rails, lo, hi, keep):
    """Douglas-Peucker on the three rails together, samples lo..hi of one piece."""
    if hi - lo < 2:
        return
    worst, at = 0., None
    for i in range(lo + 1, hi):
        f = (i - lo) / (hi - lo)
        for rail in rails:
            p = rail[lo][0].lerp(rail[hi][0], f)
            d = (rail[i][0] - p).length
            if d > worst:
                worst, at = d, i
    if worst > PAINT_TOLERANCE:
        keep.add(at)
        _pl_keep(rails, lo, at, keep)
        _pl_keep(rails, at, hi, keep)


def _pl_lay(line, tree, verts, tris, owner, corner, offset=PAINT_OFFSET, suffix='', lods=(0,)):
    pts = []
    for p in line['points']:
        # a repeated point (side_shell.py joins its U end and the run at one) would make a zero-length leg and a kink
        if not pts or math.dist(pts[-1], p) > 1e-6:
            pts.append(tuple(p))
    if line['smooth']:
        pts = _pl_corner_cut(pts)
    # the bend measured on the rounded polygon itself (three resampled points across one of its corners would read far too tight)
    radius = _pl_min_radius(pts) if line['smooth'] else float('inf')
    pts = _pl_resample(pts, PAINT_STEP)
    if radius < 1.25 * line['half']:
        raise RuntimeError(f"{line['name']}: the line bends at {radius * 1000:.1f} mm, too tight for a {line['half'] * 2000:.0f} mm ribbon")
    rails = tuple([] for _ in PAINT_RAILS)
    mid = len(PAINT_RAILS) // 2
    for i, (u, v) in enumerate(pts):
        a, b = pts[max(0, i - 1)], pts[min(len(pts) - 1, i + 1)]
        tu, tv = b[0] - a[0], b[1] - a[1]
        length = math.hypot(tu, tv) or 1.
        nu, nv = -tv / length * line['half'], tu / length * line['half']
        for rail, k in zip(rails, PAINT_RAILS):
            rail.append(_pl_cast(tree, verts, tris, owner, corner, line, u + k * nu, v + k * nv, offset))

    ray = _pl_place(line, 0., 0.)[2]
    across = line['half'] * (PAINT_RAILS[1] - PAINT_RAILS[0])

    def close(p, q, drawn, normal):
        # no step: a panel seam (the front corner shoulder stands 2.4 mm proud of the quarter panel, with a groove behind its lip)
        # or a proud hatch ends the piece, as a real panel gap interrupts the paint, instead of a face bridging it. The height
        # difference a smooth slope makes over the distance (the tail's flank turns 17 degrees from the axis) is no step.
        d = p - q
        along = (d - normal * d.dot(normal)).length
        return d.length <= PAINT_STRETCH * drawn + .002 and abs(d.dot(normal)) <= PAINT_SEAM + .1 * along

    def on_paint(i):
        # every point on paint that faces the side the line is painted on, flat and without a step across the width (panels
        # meeting flush may share a section: the rear U runs along the joint of the tail's two shells)
        hits = [r[i] for r in rails]
        if not all(hits):
            return False
        if any(-h[3].dot(ray) < PAINT_FACING or h[3].dot(hits[mid][3]) < PAINT_FLAT for h in hits):
            return False
        return all(close(a[0], b[0], across, (a[1] + b[1]).normalized()) for a, b in zip(hits, hits[1:]))

    def joins(i, j):
        # the next section continues the piece: no fold and no step between them (a proud hatch's edge breaks it)
        drawn = math.dist(pts[i], pts[j])
        return (rails[mid][i][3].dot(rails[mid][j][3]) >= PAINT_FLAT and
                all(close(r[i][0], r[j][0], drawn, (r[i][1] + r[j][1]).normalized()) for r in rails))

    # pieces: runs of sections lying on one panel's flat paint
    good = [on_paint(i) for i in range(len(pts))]
    pieces, start = [], None
    for i in range(len(pts) + 1):
        same = i < len(pts) and good[i] and (start is None or joins(i - 1, i))
        if same and start is None:
            start = i
        elif not same and start is not None:
            if i - start >= 2:
                pieces.append((start, i - 1))
            start = i if i < len(pts) and good[i] else None
    missing = good.count(False) / len(pts)
    if not pieces or missing > PAINT_MAX_GAP:
        why = {'no paint': 0, 'not facing': 0, 'not flat': 0, 'step': 0}
        for i in range(len(pts)):
            hits = [r[i] for r in rails]
            if good[i]:
                continue
            if not all(hits):
                why['no paint'] += 1
            elif any(-h[3].dot(ray) < PAINT_FACING for h in hits):
                why['not facing'] += 1
            elif any(h[3].dot(hits[mid][3]) < PAINT_FLAT for h in hits):
                why['not flat'] += 1
            else:
                why['step'] += 1
        raise RuntimeError(f"{line['name']}: {missing:.0%} of the line finds no paint to lie on ({why})")
    # A short break where the paint is there but folds or steps (the front corner shoulder's seam with the quarter panel: a lip
    # 2.4 mm proud and a groove between them) is painted over, as the real stripe runs on across the joint: the pieces on both
    # sides are joined by one span of faces, if nothing covers it and no surface comes through it. A break where the paint is
    # covered or missing (a hatch's seal ring, a lamp pod) stays open.
    def bridgeable(a, b):
        if math.dist(pts[a], pts[b]) > PAINT_BRIDGE:
            return False
        if not all(r[i] for r in rails for i in range(a + 1, b)):
            return False
        for r in rails:
            p, q = r[a][0], r[b][0]
            out = (r[a][1] + r[b][1]).normalized()
            for f in (.25, .5, .75):
                if _PL_BLOCKERS.ray_cast(p.lerp(q, f) + out * .0002, out, .3)[0] is not None:
                    return False
        return True

    groups = [[pieces[0]]]
    for piece in pieces[1:]:
        if bridgeable(groups[-1][-1][1], piece[0]):
            groups[-1].append(piece)
        else:
            groups.append([piece])
    co, normals, faces, bridges = [], [], [], []
    n = len(PAINT_RAILS)
    for group in groups:
        order = []
        for lo, hi in group:
            keep = {lo, hi}
            _pl_keep(rails, lo, hi, keep)
            order += sorted(keep)
        base = len(co)
        for i in order:
            for rail in rails:
                co.append(rail[i][0]); normals.append(rail[i][1])
        for j in range(len(order) - 1):
            r0, r1 = base + n * j, base + n * (j + 1)
            span = not good[order[j] + 1] if order[j + 1] > order[j] + 1 else False
            for k in range(n - 1):
                quad = [r0 + k, r1 + k, r1 + k + 1, r0 + k + 1]
                e1, e2 = co[quad[1]] - co[quad[0]], co[quad[3]] - co[quad[0]]
                if e1.cross(e2).dot(sum((normals[q] for q in quad), Vector())) < 0:
                    quad.reverse()
                if span:
                    bridges.append(len(faces))
                faces.append(tuple(quad))
    global current_collection
    previous, current_collection = current_collection, line['collection']
    obj = mesh(line['name'] + suffix, [tuple(c) for c in co], faces, line['mat'])
    current_collection = previous
    for poly in obj.data.polygons:
        poly.use_smooth = True
    obj.data.normals_split_custom_set_from_vertices([tuple(n) for n in normals])
    obj['cdlc_keep_topology'] = True       # a single-sided strip: decimation would fold it
    obj['cdlc_paint_line'] = True
    obj['paint_offset_m'] = offset
    obj['cdlc_lods'] = list(lods)
    obj['paint_bridge_faces'] = bridges   # verify_paint_lines.py: their height is no paint offset (they span a seam)
    obj.hide_render = 0 not in lods     # the far version is for the reduced LODs only, not the Blender renders
    # every vertex and face centre must lie on the paint's side, between half and twice the offset
    worst_low, worst_high, low_at, high_at = 1., 0., None, None
    probes = list(co) + [sum((co[q] for q in f), Vector()) / 4 for i, f in enumerate(faces) if i not in bridges]
    for p in probes:
        near = tree.find_nearest(p, .05)
        if near[0] is None:
            raise RuntimeError(f"{line['name']}: a ribbon point has no paint within 5 cm")
        # height above the nearest paint along its outward face normal: negative = buried
        gap = (p - near[0]).dot(_pl_out(near[1], near[0]))
        if gap < worst_low:
            worst_low, low_at = gap, (tuple(round(c, 4) for c in p), owner[near[2]])
        if (p - near[0]).length > worst_high:
            worst_high, high_at = (p - near[0]).length, (tuple(round(c, 4) for c in p), owner[near[2]])
    if worst_low < .5 * offset or worst_high > 2 * offset:
        raise RuntimeError(f"{line['name']}: ribbon {worst_low * 1000:.2f} mm at {low_at} to {worst_high * 1000:.2f} mm at {high_at} off the paint")
    return {'name': line['name'] + suffix, 'lods': list(lods), 'width_mm': round(line['half'] * 2000, 1), 'samples': len(pts),
            'samples_without_paint': good.count(False), 'pieces': len(groups), 'seams_painted_over': sum(len(g) - 1 for g in groups),
            'panels': sorted({r[i][2] for lo, hi in pieces for i in range(lo, hi + 1) for r in rails}), 'vertices': len(co), 'triangles': 2 * len(faces),
            'min_bend_mm': None if radius == float('inf') else round(radius * 1000, 1),
            'offset_mm': [round(worst_low * 1000, 3), round(worst_high * 1000, 3)]}


_pl_tree = _pl_paint_bvh(white)
_PL_BLOCKERS = _pl_blockers()
PAINT_REPORT = {'offset_mm': PAINT_OFFSET * 1000, 'far_offset_mm': PAINT_OFFSET_FAR * 1000, 'step_mm': PAINT_STEP * 1000,
                'tolerance_mm': PAINT_TOLERANCE * 1000,
                'lines': [_pl_lay(line, *_pl_tree) for line in PAINT_LINES] +
                         [_pl_lay(line, *_pl_tree, offset=PAINT_OFFSET_FAR, suffix=' | far LODs', lods=(1, 2)) for line in PAINT_LINES]}
if len(PAINT_REPORT['lines']) != 2 * len(PAINT_LINES) or not PAINT_LINES:
    raise RuntimeError('paint_lines: nothing laid')
(SRC / 'paint-lines.json').write_text(json.dumps(PAINT_REPORT, indent=1, ensure_ascii=False), encoding='utf-8')
print(f"Paint lines: {len(PAINT_REPORT['lines'])} laid, {sum(l['triangles'] for l in PAINT_REPORT['lines'])} triangles")
