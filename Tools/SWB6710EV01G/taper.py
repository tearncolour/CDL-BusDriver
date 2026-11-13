"""iEV7 tumblehome: the body narrows from the waist to the roof, as the official front photograph shows.
Executed in build_model.py's namespace after the body, doors, mirrors and saloon fittings are built and before
fine_detail.py (whose interior lining is derived from the tapered skin).

Measured from references/20260804092603_4898.jpg (body width 2.35 m at the bumper as the scale; review 2026-10-15).
The photograph is a perspective view from about 10.3 m ahead of the bumper (the front tyres, 1.2 m behind the bumper,
appear 11.6 % narrower than their 1.939 m track plus tyre width), and the raked front face puts the silhouette further
back the higher it is (0.13 m at 1.62 m, 0.37 m at 2.53 m, 0.51 m at 2.81 m): its upper widths read 1-5 % small and
are scaled back by (10.3 + depth) / 10.3. Corrected half widths by height:
    <=1.6 m 1.175 | 1.93 1.162 | 2.57 1.084 | 2.71 1.046 | 2.86 0.993 | 2.90 0.967
fitted by the inset 0.09 t^2 + 0.0085 t^6 (t = height above 1.6 m; within about 1.5 cm of every reading). The first
version used the uncorrected readings and made the roof 6-8 cm a side too narrow (user review: "narrower than the real
bus").
The untapered model was vertical at 1.177 m up to the eyebrow (2.70 m), then its own roof shoulder.

Every vertex moves inward by the inset its untapered silhouette needs at that height, weighted by a smoothstep across the
width: the middle of the bus (|x| <= 0.45 m: the aisle, ceiling rails, roof crown) stays where it is. Saloon fittings keep
their shape (they stand clear of the walls); the mirrors move rigidly with their A-pillar mounting shoes.
"""
import json as _taper_json
from mathutils import Vector as _TV

TAPER_WAIST = 1.60
TAPER_INNER = .45
TAPER_TOP = 2.86            # above: the inset of this height (the roof crown keeps its own round)
# The untapered silhouette (half width by height), as built by side_shell.py / the roof.
_TAPER_OLD = [(2.70, 1.177), (2.75, 1.165), (2.80, 1.152), (2.838, 1.10), (2.86, 1.065)]


def taper_old_half(z):
    if z <= _TAPER_OLD[0][0]:
        return _TAPER_OLD[0][1]
    for (za, xa), (zb, xb) in zip(_TAPER_OLD, _TAPER_OLD[1:]):
        if z <= zb:
            return xa + (xb - xa) * (z - za) / (zb - za)
    return _TAPER_OLD[-1][1]


def taper_real_half(z):
    t = max(0., min(z, TAPER_TOP) - TAPER_WAIST)
    return 1.175 - (.09 * t * t + .0085 * t ** 6)


def taper_shift(x, z):
    """Inward displacement (signed, towards the centre) of a point at |x|, height z."""
    zc = min(z, TAPER_TOP)
    old = taper_old_half(zc)
    d = max(0., old - taper_real_half(zc))
    if d <= 0. or abs(x) <= TAPER_INNER:
        return 0.
    u = min(1., (abs(x) - TAPER_INNER) / max(.05, old - TAPER_INNER))
    return -math.copysign(d * u * u * (3 - 2 * u), x)


def _taper_excluded(name):
    n = name.lower()
    return any(s in n for s in ('passenger seat', 'rear bench', 'driver', 'grab pole', 'pole floor', 'ceiling longitudinal rail',
                                'ceiling rail hanger', 'hand strap', 'stop request', 'meshkit', 'dashboard', 'instrument',
                                'steering', 'pedal', 'fire extinguisher', 'ceiling air duct', ' tyre', ' rim', ' hub', ' nut',
                                ' bead', 'rim vent'))


def _taper_slice(obj):
    """Cut a coarse mesh along horizontal planes every 8 cm through the tapered band, so a big flat face (a window
    pane, a frit ring, the eyebrow) bends with the curve instead of running straight between its few vertices."""
    import bmesh
    mw = obj.matrix_world
    zs = [(mw @ _TV(c)).z for c in obj.bound_box]
    lo, hi = max(min(zs), TAPER_WAIST), min(max(zs), 2.96)
    if hi - lo < .12 or len(obj.data.polygons) > 400 or obj.data.has_custom_normals:
        return
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    inv = mw.inverted()
    up = (mw.to_3x3().transposed() @ _TV((0, 0, 1))).normalized()
    z = TAPER_WAIST + .08
    while z < hi:
        if z > lo:
            geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
            bmesh.ops.bisect_plane(bm, geom=geom, plane_co=inv @ _TV((0, 0, z)), plane_no=up, dist=1e-6)
        z += .08
    bm.to_mesh(obj.data)
    bm.free()


def apply_taper(master):
    moved = 0
    # mirrors: one rigid shift per side, the one their mounting shoe needs
    mirror_shift = {}
    for side in (-1, 1):
        shoes = [o for o in master.objects if o.name.lower().startswith('mirror') and 'mounting shoe' in o.name.lower()
                 and math.copysign(1, (o.matrix_world @ _TV(o.bound_box[0])).x) == side]
        if shoes:
            o = shoes[0]
            c = sum((o.matrix_world @ _TV(v) for v in o.bound_box), _TV()) / 8
            mirror_shift[side] = taper_shift(c.x, c.z)
    for obj in list(master.objects):
        if obj.type not in {'MESH', 'CURVE', 'FONT'}:
            continue
        if obj.name.lower().startswith('mirror'):
            c = obj.matrix_world.translation if obj.type == 'FONT' else \
                sum((obj.matrix_world @ _TV(v) for v in obj.bound_box), _TV()) / 8
            s = mirror_shift.get(math.copysign(1, c.x), 0.)
            obj.matrix_world = Matrix.Translation((s, 0, 0)) @ obj.matrix_world
            moved += 1
            continue
        if _taper_excluded(obj.name):
            continue
        mw = obj.matrix_world.copy()
        inv = mw.inverted()
        if obj.type == 'MESH':
            _taper_slice(obj)
            for v in obj.data.vertices:
                w = mw @ v.co
                w.x += taper_shift(w.x, w.z)
                v.co = inv @ w
            obj.data.update()
        elif obj.type == 'CURVE':
            for spline in obj.data.splines:
                for p in spline.points:
                    w = mw @ _TV(p.co[:3])
                    w.x += taper_shift(w.x, w.z)
                    l = inv @ w
                    p.co = (l.x, l.y, l.z, p.co[3])
                for p in spline.bezier_points:
                    for attr in ('co', 'handle_left', 'handle_right'):
                        w = mw @ getattr(p, attr)
                        w.x += taper_shift(w.x, w.z)
                        setattr(p, attr, inv @ w)
        else:  # text: moved with its anchor point
            w = mw.translation
            obj.matrix_world = Matrix.Translation((taper_shift(w.x, w.z), 0, 0)) @ mw
        moved += 1
    # the mirror surfaces the mod's real mirrors use (mirror-surfaces.json; Unity x = Blender x)
    for surface in MIRROR_SURFACES:
        side = 1 if surface['centre_unity'][0] > 0 else -1
        surface['centre_unity'][0] += mirror_shift.get(side, 0.)
    if '_mirror_report' in globals():
        (SRC / 'mirror-surfaces.json').write_text(_taper_json.dumps(_mirror_report, indent=2) + chr(10), encoding='utf-8')
    report = {'source': 'references/20260804092603_4898.jpg', 'waist_m': TAPER_WAIST, 'inner_m': TAPER_INNER,
              'half_width_by_height': {f'{z:.2f}': round(taper_real_half(z), 3) for z in (1.6, 1.93, 2.2, 2.57, 2.71, 2.86, 2.9)},
              'mirror_shift_m': {str(k): round(v, 4) for k, v in mirror_shift.items()}, 'objects_moved': moved}
    (SRC / 'taper-report.json').write_text(_taper_json.dumps(report, indent=2), encoding='utf-8')
    print('TAPER ' + _taper_json.dumps(report), flush=True)
    return report


apply_taper(MASTER)
