"""K18 rear end: rounded plan-view shell, wrap-around lamp clusters, bumper, plate and trim.

Reference: reference/official-c18-en-dt4.png (same-family rear view; its markings are not copied): a tall rear window
recessed in a dark surround, a silver strip with the emblem below it, vertical lamp strips that wrap the corners, a round
red lamp low on each side, a plate on a stepped bumper. Dimensions are photo-proportion estimates, not measurements.

Unity metres, assembly coordinates (the rear section stands at its place in the assembly; the tail is towards -z).
The structural wall replaces the old flat panels: its outer face is a convex "D" in plan, flat across the middle and
rounded into the sides, and everything else (bumper, lamps, trim) follows that contour.
"""
import math

# The rear route sign sits on the upper wall's rear face: housing centre z=-8.9055, LED plane z=-8.9275 (build_model, build_profile).
XC = .93                 # half width of the flat middle
HALF = 1.25              # side wall (x = +-HALF); the side skins end at Z_SIDE
Z_CENTRE = -8.89         # rearmost face (middle)
Z_SIDE = -8.67
Z_INNER = -8.65          # interior face of the rear wall (unchanged)
A = HALF-XC              # corner quarter-ellipse semi-axes
B = Z_SIDE-Z_CENTRE
QUARTER = math.pi/2


def _theta(x):
    return math.asin(max(0., min(1., (abs(x)-XC)/A)))


def z_outer(x):
    return Z_CENTRE if abs(x) <= XC else Z_SIDE-B*math.cos(_theta(x))


def _point(x):
    """(x, z, nx, nz) on the outer contour; the normal points away from the bus."""
    if abs(x) <= XC+1e-9:
        return (x, Z_CENTRE, 0., -1.)
    s = 1. if x > 0 else -1.
    th = _theta(x)
    nx, nz = B*math.sin(th), -A*math.cos(th)
    n = math.hypot(nx, nz)
    return (s*(XC+A*math.sin(th)), Z_SIDE-B*math.cos(th), s*nx/n, nz/n)


def contour(xa, xb, quarter_steps):
    """Outer contour from xa to xb (xa < xb): the flat middle by its end points, each corner sampled uniformly in angle."""
    xs = [-HALF+0.0]
    xs += [-(XC+A*math.sin(QUARTER*(quarter_steps-i)/quarter_steps)) for i in range(1, quarter_steps+1)]
    xs += [XC+A*math.sin(QUARTER*i/quarter_steps) for i in range(1, quarter_steps+1)]
    keep = [x for x in xs if xa+1e-6 < x < xb-1e-6]
    out = []
    for x in [xa]+keep+[xb]:
        if not out or abs(x-out[-1]) > 1e-6:
            out.append(x)
    return [_point(x) for x in out]


def _loft(mesh, name, rings, material, part):
    n = len(rings[0])
    verts = [p for r in rings for p in r]
    faces = [tuple(reversed(range(n))), tuple((len(rings)-1)*n+i for i in range(n))]
    for j in range(len(rings)-1):
        for i in range(n):
            p, q = j*n+i, j*n+(i+1) % n
            faces.append((p, q, q+n, p+n))
    return mesh(name, verts, faces, material, part)


def build_k18_rear_shell(ctx, tag, red, black, panel):
    """The rear wall (lower wall, window jambs, upper wall, roof cap); called by interior_completion in place of the flat panels."""
    mesh, U = ctx['mesh'], ctx['U']
    window = [-1.04, 1.04, 1.605, 2.335]
    steps = 12

    def prism(name, xa, xb, levels, material, part):
        base = contour(xa, xb, steps)
        rings = []
        for y, inset in levels:
            ring = [(px-nx*inset, y, pz-nz*inset) for px, pz, nx, nz in base]
            rings.append(ring+[(xb, y, Z_INNER), (xa, y, Z_INNER)])
        ob = _loft(mesh, name, rings, material, part)
        ob.data.materials.append(panel)
        for poly in ob.data.polygons:
            c = U.inverted() @ poly.center
            nrm = U.inverted().to_3x3() @ poly.normal
            outer = abs(nrm.y) < .5 and nrm.z < .5 and abs(c.z-z_outer(c.x)) < .08
            poly.material_index = 0 if outer else 1
            poly.use_smooth = outer and abs(c.x) > XC-.02
        ob['window_opening_unity'] = window
        return tag(ob, 'rear_wall', True)

    prism('Rear continuous lower wall', -HALF, HALF,
          [(.22, .055), (.34, .018), (.52, 0.), (1.45, 0.), (1.605, 0.)], red, 0)
    for side in (-1, 1):
        x0, x1 = sorted((side*1.04, side*HALF))
        prism('Rear window structural jamb', x0, x1, [(1.605, 0.), (2.335, 0.)], black, 17)
    prism('Rear window upper structural wall', -HALF, HALF, [(2.335, 0.), (2.94, 0.)], black, 17)
    # roof cap: the crown rolls over to the back and to the sides (top surface stays 5 mm under the roof box)
    prism('Rear continuous roof return', -HALF, HALF,
          [(2.94, 0.), (3.01, 0.), (3.09, .03), (3.15, .075), (3.19, .12)], red, 0)


def build_k18_rear_details(ctx):
    """Bumper, wrap-around lamps, plate, trim, grilles and emblem; replaces the old flat lamp blocks and bumper."""
    import bpy
    import runpy
    from mathutils import Vector
    mesh, box, rod = ctx['mesh'], ctx['box'], ctx['rod']
    select, mat = ctx['select_section'], ctx['mat']
    BLACK, DARK, CHROME, WHITE = ctx['BLACK'], ctx['DARK'], ctx['CHROME'], ctx['WHITE']
    TAIL, BRAKE, AMBER, AMBER_RIGHT, REVERSE = (ctx[n] for n in ('TAIL', 'BRAKE', 'AMBER', 'AMBER_RIGHT', 'REVERSE'))
    made = []

    for ob in list(bpy.data.objects):
        if ob.type == 'MESH' and ob.name.startswith(('Rear lamp surround', 'Rear lens', 'Rear bumper')):
            bpy.data.objects.remove(ob, do_unlink=True)

    plate_white = mat('Rear plate white', (.74, .76, .74), .45, 0, 'SafetyWhite')
    plate_ink = mat('Rear plate frame', (.012, .016, .02), .5, 0, 'Black')
    grille_mat = mat('Rear louvre grille', (.025, .03, .034), .6, 0, 'Black')
    select('rear')

    def fix(ob, first, last, role):
        ob['first_lod'], ob['last_lod'] = first, last
        ob['k18_rear_refinement'] = 1
        ob['k18_completion_role'] = role
        made.append(ob.name)
        return ob

    levels = ((0, 12), (1, 6), (2, 3))

    def band(name, xa, xb, y0, y1, d0, d1, bevel, material, part, role, only=(0, 1, 2), taper=0.):
        """A strip following the outer contour, from offset d0 (inside) to d1 (outside), rounded at top and bottom.
        taper: the outward offset falls to zero over this distance from the side wall, so the strip runs into the body."""
        for level, steps in levels:
            if level not in only:
                continue
            base = contour(xa, xb, steps)
            bev = min(bevel, (d1-d0)*.45) if level < 2 else 0.   # a bevel thicker than the strip would turn the ring inside out
            ys = [(y0, d1-bev), (y0+bev, d1), (y1-bev, d1), (y1, d1-bev)] if bev > 0 else [(y0, d1), (y1, d1)]
            rings = []
            for y, dd in ys:
                def reach(px):
                    return min(1., max(0., (HALF-abs(px))/taper)) if taper > 0 else 1.
                outer = [(px+nx*dd*reach(px), y, pz+nz*dd*reach(px)) for px, pz, nx, nz in base]
                inner = [(px+nx*d0, y, pz+nz*d0) for px, pz, nx, nz in reversed(base)]
                rings.append(outer+inner)
            ob = _loft(mesh, name, rings, material, part)
            ring_len = len(rings[0])
            for poly in ob.data.polygons:
                # smooth along the outer and inner contour; the two caps and the two end faces of the strip stay flat
                i = (poly.index-2) % ring_len if poly.index >= 2 else -1
                poly.use_smooth = poly.index >= 2 and i not in (len(base)-1, ring_len-1)
            if level:
                ob.name = 'LOD%d %s' % (level, name)
            fix(ob, level, level, role)

    band('Rear bumper', -HALF, HALF, .22, .54, -.012, .045, .035, DARK, 16, 'rear_bumper', taper=.15)
    band('Rear chrome trim strip', -.99, .99, 1.505, 1.552, 0., .012, .006, CHROME, 16, 'rear_trim', only=(0, 1))
    band('Rear bumper top seam', -HALF+.02, HALF-.02, .545, .56, 0., .006, 0., BLACK, 16, 'rear_trim', only=(0, 1))

    for side in (-1, 1):
        xa, xb = sorted((side*.985, side*1.238))
        band('Rear lamp surround', xa, xb, .60, 1.50, -.012, .02, .045, BLACK, 16, 'rear_lamp_housing')
        lxa, lxb = sorted((side*1.0, side*1.226))
        amber = AMBER if side < 0 else AMBER_RIGHT
        for y0, y1, material, part in ((1.215, 1.465, BRAKE, 25), (.955, 1.185, TAIL, 24),
                                       (.725, .925, amber, 26 if side < 0 else 27), (.625, .695, REVERSE, 28)):
            band('Rear lens', lxa, lxb, y0, y1, .004, .027, .018, material, part, 'rear_lens')
        # round low lamp on the bumper line (photo): graphite bezel + the brake lens
        cx, zc = side*.80, Z_CENTRE-.047
        fix(rod('Rear low lamp bezel', (cx, .39, zc-.004), (cx, .39, zc+.03), .075, BLACK, 16, n=20), 0, 2, 'rear_lamp_housing')
        fix(rod('Rear lens', (cx, .39, zc-.012), (cx, .39, zc+.026), .052, BRAKE, 25, n=20), 0, 2, 'rear_lens')

    zp = Z_CENTRE-.049
    fix(box('Rear plate frame', (0, .385, zp), (.58, .17, .012), plate_ink, 16, bevel=.004), 0, 2, 'rear_plate')
    fix(box('Rear plate', (0, .385, zp-.007), (.52, .125, .006), plate_white, 16), 0, 2, 'rear_plate')
    for sx in (-.2, .2):
        fix(box('Rear plate lamp', (sx, .48, zp-.004), (.05, .018, .02), WHITE, 16), 0, 1, 'rear_plate')

    for side in (-1, 1):
        gx, zg = side*.47, Z_CENTRE-.006
        fix(box('Rear louvre panel', (gx, .93, zg), (.30, .30, .012), grille_mat, 16, bevel=.012), 0, 1, 'rear_trim')
        for k in range(7):
            fix(box('Rear louvre slat', (gx, .805+k*.04, zg-.008), (.27, .014, .008), BLACK, 16), 0, 0, 'rear_trim')

    # emblem: the traced CRRC symbol of the front badge, small, on the lower wall under the trim strip
    root = ctx['ROOT']
    badge = runpy.run_path(str(root/'Tools/CRRC_K18/badges.py'), init_globals={'math': math, 'Vector': Vector})
    width = height = .11
    cy = 1.40
    z0 = Z_CENTRE+.0004
    for level, steps in ((0, 8), (1, 4), (2, 2)):
        for label, contour_px in badge['k18_badge_contours'](steps).items():
            tris = badge['_k18_badge_triangles'](contour_px)
            flat = [((px-146)*width/236, cy+(148-py)*height/236) for px, py in contour_px]
            n = len(flat)
            verts = [(x, y, z) for z in (z0, z0-.003) for x, y in flat]
            faces = [tuple(t) for t in tris]+[tuple(reversed([i+n for i in t])) for t in tris]
            faces += [(i, (i+1) % n, (i+1) % n+n, i+n) for i in range(n)]
            ob = mesh('K18 rear emblem '+label, verts, faces, CHROME, 16)
            if level:
                ob.name = 'LOD%d %s' % (level, ob.name)
            fix(ob, level, level, 'rear_emblem')
    return made
