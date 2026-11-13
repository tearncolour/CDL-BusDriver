"""Repair the K18 study's interior assembly before explicit LOD generation.

Call ``build_interior_completion(globals())`` from build_model.py after the
articulation has been built and before build_explicit_lods(). Coordinates below
are vehicle coordinates, not Blender world coordinates. The builder performs
the one required handedness conversion.

Reference: reference/official-k18-interior.jpg establishes yellow molded seats,
rear steps, a raised rear saloon and an equipment cabinet beside the joint.
It does NOT establish the complete seat map or platform dimensions. This is a
documented, usable 34-seat study layout, not a claimed factory seating plan.
"""


def build_interior_completion(ctx):
    import json
    import math
    import bpy
    from mathutils import Vector

    select = ctx['select_section']
    box, mesh, skin, rod, path = (ctx[n] for n in ('box', 'mesh', 'skin', 'rod', 'path'))
    U = ctx['U']
    materials = {n: ctx[n] for n in ('RED', 'BLACK', 'PANEL', 'FLOOR', 'DARK',
                                      'METAL', 'SEAT', 'PRIORITY', 'YELLOW')}
    red, black, panel, floor, dark, metal, yellow_seat, priority, yellow = materials.values()
    low = .3375
    high = 1.15
    made, removed, surfaces = [], [], []

    def tag(ob, role, continuous=False):
        ob['k18_completion_role'] = role
        ob['k18_completion_revision'] = 1
        if continuous:
            # Structural boundaries must not change between independently
            # simplified neighboring pieces. Modest analytic meshes suffice.
            ob['first_lod'] = 0
            ob['last_lod'] = 2
        made.append(ob.name)
        return ob

    def remove_prefixes(prefixes):
        for ob in list(bpy.data.objects):
            if ob.type == 'MESH' and any(ob.name.startswith(n) for n in prefixes):
                removed.append(ob.name)
                bpy.data.objects.remove(ob, do_unlink=True)

    def slab(name, vertices, faces, material, role, part=0):
        return tag(mesh(name, vertices, faces, material, part), role, True)

    def floor_box(name, x0, x1, z0, z1, top, bottom=None, role='walking_floor'):
        bottom = top - .045 if bottom is None else bottom
        ob = tag(box(name, ((x0+x1)/2, (bottom+top)/2, (z0+z1)/2),
                     (x1-x0, top-bottom, z1-z0), floor, 0), role, True)
        surfaces.append({'object': ob.name, 'x': [x0, x1], 'z': [z0, z1], 'top': top})
        return ob

    # Rebuild the rear wall itself, including its window opening and the return
    # through to the cabin. The old lower wall stopped 345 mm below the glass;
    # the old upper solid wall also occluded the upper half of that glass.
    remove_prefixes(('Rear lower body', 'Rear destination surround', 'Rear upper cap'))
    select('rear')

    # The rear wall is a plan-view rounded shell (rear_refinement.py): lower wall, window jambs, upper wall and roof cap.
    ctx['build_k18_rear_shell'](ctx, tag, red, black, panel)

    # A fixed window sill ends inside each fixed jamb. It must never cross an
    # actual door opening, either closed or during leaf travel.
    remove_prefixes(('Inside window sill',))
    sill_runs = []
    for section, start, end, doors in (('front', ctx['FRONT_JOIN'], 8.50, ctx['DOORS'][:2]),
                                      ('rear', -8.68, ctx['REAR_JOIN'], ctx['DOORS'][2:])):
        select(section)
        for side in (-1, 1):
            cutouts = sorted((d-.642, d+.642) for d in doors) if side > 0 else []
            cursor = start
            runs = []
            for a, b in cutouts:
                if a > cursor:
                    runs.append((cursor, a))
                cursor = max(cursor, b)
            if cursor < end:
                runs.append((cursor, end))
            for a, b in runs:
                ob = tag(box('Cabin window sill between door jambs',
                             (side*1.205, 1.17, (a+b)/2), (.070, .11, b-a), panel,
                             0, bevel=.012), 'window_sill', True)
                sill_runs.append({'object': ob.name, 'section': section,
                                  'side': side, 'z': [a, b]})

    # The drive housing is a folded sheet, open below the linkage. A solid
    # box here intersects both the top leaf rail and its moving actuation arm.
    # Keep the outboard return as the actual opening header in every LOD.
    remove_prefixes(('Door overhead mechanism',))
    header_profile = [(.98, 2.81), (1.26, 2.81), (1.26, 2.601),
                      (1.245, 2.601), (1.245, 2.795), (.995, 2.795),
                      (.995, 2.69), (.98, 2.69)]
    header_cap = [(1, 2, 3), (1, 3, 4), (0, 1, 4),
                  (0, 4, 5), (0, 5, 6), (0, 6, 7)]
    for index, dz in enumerate(ctx['DOORS']):
        select('front' if index < 2 else 'rear')
        vertices = [(x, y, z) for z in (dz-.665, dz+.665)
                    for x, y in header_profile]
        faces = list(header_cap)
        faces.extend(tuple(i+8 for i in reversed(face)) for face in header_cap)
        faces.extend((i, (i+1) % 8, (i+1) % 8+8, i+8) for i in range(8))
        tag(mesh('Door overhead mechanism', vertices, faces, dark, 14),
            'door_header', True)

    # Replace the open modifier-based arches and the inboard walls intersecting
    # the rear dual tires. Both sides of every wall are authored geometry.
    remove_prefixes(('front -1 wheelhouse crown', 'front 1 wheelhouse crown',
                     'rear -1 wheelhouse crown', 'rear 1 wheelhouse crown',
                     'Wheelhouse end', 'Wheelhouse aisle wall'))
    wheelhouses = []
    radius, radius_out, inner_x, outer_x = .602, .624, .615, 1.245
    segments = 40
    for axle_index, az in enumerate(ctx['AXLES']):
        section = 'front' if axle_index < 2 else 'rear'
        select(section)
        for side in (-1, 1):
            # One connected thick enclosure: the previous independent crown
            # end cap and aisle wall both exposed x=+/-inner_x, producing
            # two differently coloured coplanar faces in the game renderer.
            # Build their geometric union with shared boundary vertices.
            from mathutils.geometry import tessellate_polygon
            bottom, end_top, extent = .2625, .510, .650
            end_angle = math.asin((end_top-ctx['R'])/radius_out)
            outer = [(bottom, az-extent), (end_top, az-extent)]
            outer.extend((ctx['R']+radius_out*math.sin(a),
                          az-radius_out*math.cos(a))
                         for a in (end_angle+(math.pi-2*end_angle)*j/segments
                                   for j in range(segments+1)))
            outer.extend(((end_top, az+extent), (bottom, az+extent)))
            inner = [(bottom, az-radius)]
            inner.extend((ctx['R']+radius*math.sin(math.pi*j/segments),
                          az-radius*math.cos(math.pi*j/segments))
                         for j in range(segments+1))
            inner.append((bottom, az+radius))
            points, rings = [], []
            for x, profile in ((side*inner_x, outer), (side*outer_x, outer),
                               (side*(inner_x+.020), inner), (side*outer_x, inner)):
                rings.append(list(range(len(points), len(points)+len(profile))))
                points.extend((x, y, z) for y, z in profile)
            aisle, outside, cavity_back, cavity_outside = rings
            faces = []
            def cap(indices):
                vectors = [Vector(points[i]) for i in indices]
                lookup = {tuple(v): i for v, i in zip(vectors, indices)}
                faces.extend(tuple(indices[v] if isinstance(v, int) else lookup[tuple(v)] for v in tri)
                             for tri in tessellate_polygon([vectors]))
            cap(aisle)
            cap(cavity_back)
            # Sweep the outer cover and inner tire cavity. Their bottom
            # closing edges are deliberately excluded and connected below.
            for front, rear in ((aisle, outside), (cavity_back, cavity_outside)):
                faces.extend((front[j], front[j+1], rear[j+1], rear[j])
                             for j in range(len(front)-1))
            # The outboard annulus is an actual sealed thickness return.
            for j in range(segments):
                faces.append((outside[j+2], outside[j+3],
                              cavity_outside[j+2], cavity_outside[j+1]))
            cap((outside[0], outside[1], outside[2],
                 cavity_outside[1], cavity_outside[0]))
            cap((outside[-3], outside[-2], outside[-1],
                 cavity_outside[-1], cavity_outside[-2]))
            # A concave U-shaped underside connects both end returns and
            # the aisle-wall base, without closing the tire opening.
            cap((aisle[0], outside[0], cavity_outside[0], cavity_back[0],
                 cavity_back[-1], cavity_outside[-1], outside[-1], aisle[-1]))
            shell = slab('Continuous wheelhouse unified shell A%d %s' %
                         (axle_index+1, side), points, faces, dark, 'wheelhouse')
            shell.data.materials.append(panel)
            inv_u = U.inverted()
            for poly in shell.data.polygons:
                coordinates = [inv_u @ shell.data.vertices[i].co for i in poly.vertices]
                # Only the unique cabin face is light trim. No second face
                # at the same plane remains behind this material boundary.
                cabin_face = all(abs(v.x-side*inner_x) < 1e-6 for v in coordinates)
                poly.material_index = 1 if cabin_face else 0
                is_curved = (max(v.x for v in coordinates)-min(v.x for v in coordinates) > .10
                             and all(v.y >= ctx['R']-1e-6 for v in coordinates)
                             and max(v.y for v in coordinates)-min(v.y for v in coordinates) > 1e-6)
                poly.use_smooth = is_curved
            shell['wheelhouse_topology'] = 'shared-boundary union; one aisle face; open tire cavity'
            wheelhouses.append({'section': section, 'axle': axle_index, 'side': side,
                                'centre_z': az, 'inner_side_x': side*inner_x,
                                'wall_thickness': .020, 'cavity_radius': radius,
                                'outer_radius': radius_out,
                                'crown': shell.name, 'aisle_wall': shell.name})

    # The cab divider crossed the front left wheel. Its outboard bottom now
    # follows the actual wheelhouse crown; the inboard portion meets the floor.
    remove_prefixes(('Driver rear partition', 'Driver suspension pedestal'))
    select('front')
    tag(box('Driver divider inboard floor connection',
            ((-.635-.08)/2, (low+1.205)/2, 6.48),
            (.635-.08, 1.205-low, .06), panel, 12), 'cab_divider')
    verts = []
    for x in (-1.24, -.615):
        for z in (6.45, 6.51):
            bottom = ctx['R']+math.sqrt(radius_out**2-(z-ctx['AXLES'][0])**2)-.002
            verts.extend(((x, bottom, z), (x, 1.205, z)))
    tag(mesh('Driver divider fitted above wheelhouse', verts,
             [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4),
              (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)], panel, 12), 'cab_divider')
    tag(box('Driver suspension pedestal', (-.79, (low+.790)/2, 7.02),
            (.33, .790-low, .31), dark, 12, bevel=.025), 'driver_seat_support')

    # The high rear floor has a real connected deck and four supported treads.
    # There is no solid volume in the wheel cavity: the deck spans above it and
    # the stair is confined to the center aisle, between the wheelhouse walls.
    select('rear')
    deck_front = -5.71
    floor_box('Rear raised saloon deck', -1.205, 1.205, -8.68, deck_front, high)
    steps = []
    stair_start, tread = -4.55, .29
    for i in range(4):
        level = low+(high-low)*(i+1)/4
        front = stair_start-tread*i
        back = front-tread
        floor_box('Rear saloon stair tread %d' % (i+1), -.29, .29, back, front,
                  level, low-.020, 'stair')
        tag(box('Rear saloon stair visible edge %d' % (i+1),
                (0, level+.0015, front-.013), (.56, .003, .026), yellow, 0), 'stair_edge', True)
        steps.append({'front_z': front, 'back_z': back, 'height': level, 'width': .58})
    # Close the vertical leading edge of the deck outside the central stair.
    # These are original floor fascia edges, not panels over wheel openings.
    for side in (-1, 1):
        tag(box('Rear deck inboard leading fascia', (side*(.29+inner_x)/2,
                (low+high)/2, deck_front-.018),
                (inner_x-.29, high-low, .036), floor, 0), 'deck_edge', True)
        verts = []
        for x in (side*inner_x, side*1.205):
            for z in (deck_front-.036, deck_front):
                bottom = ctx['R']+math.sqrt(radius_out**2-(z-ctx['AXLES'][2])**2)-.002
                verts.extend(((x, bottom, z), (x, high, z)))
        slab('Rear deck wheelhouse leading fascia', verts,
             [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4),
              (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)], floor, 'deck_edge')

    # Remove seats whose raised feet had no floor, together with every dependent
    # support and isolated foot patch. Rebuild one explicit, synchronized map.
    remove_prefixes(('Passenger seat cushion', 'Passenger seat back', 'Seat lower shell',
                     'Seat leg', 'Seat back grab handle', 'Seat front foot platform'))
    ctx['seat_records'].clear()
    seating = []

    def curved_plate(name, rows, thickness, material, role, direction, part=10):
        # Each row contains left-to-right points. The two sampled surfaces and
        # their perimeter are closed before smoothing the broad faces only.
        ny, nx = len(rows), len(rows[0])
        a = [tuple(p) for row in rows for p in row]
        points = a+[tuple(Vector(p)+Vector(direction)*thickness) for p in a]
        n = nx*ny
        faces = []
        for j in range(ny-1):
            for i in range(nx-1):
                k = j*nx+i
                faces.extend(((k, k+1, k+1+nx, k+nx),
                              (n+k, n+k+nx, n+k+1+nx, n+k+1)))
        ring = (list(range(nx))+[j*nx+nx-1 for j in range(1, ny)]
                +list(range(n-2, n-nx-1, -1))+[j*nx for j in range(ny-2, 0, -1)])
        faces.extend((p, q, n+q, n+p) for p, q in zip(ring, ring[1:]+ring[:1]))
        ob = tag(mesh(name, points, faces, material, part), role)
        for poly in ob.data.polygons:
            poly.use_smooth = poly.index < 2*(nx-1)*(ny-1)
        return ob

    usb_blue = ctx['mat']('Seat USB blue indicator', (.05, .25, .90), .40, 0, 'Blue')

    def rounded_xy(w, d, r, steps=4):
        r = min(r, w*.45, d*.45); out = []
        for cx, cz, a0 in ((w/2-r, d/2-r, 0), (-w/2+r, d/2-r, 90), (-w/2+r, -d/2+r, 180), (w/2-r, -d/2+r, 270)):
            for j in range(steps+1):
                t = math.radians(a0+90*j/steps)
                out.append((cx+r*math.cos(t), cz+r*math.sin(t)))
        return out

    def continuous_loft(name, rings, material, part):
        n = len(rings[0]); pts = [p for r in rings for p in r]
        c0 = tuple(sum(c[i] for c in rings[0])/n for i in range(3))
        c1 = tuple(sum(c[i] for c in rings[-1])/n for i in range(3))
        pts += [c0, c1]; a, b = len(pts)-2, len(pts)-1
        faces = [(a, (i+1) % n, i) for i in range(n)]
        off = (len(rings)-1)*n
        faces += [(b, off+i, off+(i+1) % n) for i in range(n)]
        for j in range(len(rings)-1):
            for i in range(n):
                p, q = j*n+i, j*n+(i+1) % n
                faces += [(p, q, q+n), (p, q+n, p+n)]
        ob = mesh(name, pts, faces, material, part)
        for poly in ob.data.polygons:
            poly.use_smooth = poly.index >= 2*n
        return ob

    # One-piece injection-moulded shell as in the official cabin photographs:
    # waterfall front lip, dished pan, rounded seat/back transition, reclined
    # back with raised side edges and a rolled top. The profile is lofted with
    # its real thickness; each LOD is an explicit lower-resolution loft of the
    # same profile, never a decimated copy.
    def shell_loft(name, stations, width_at, dish_at, thickness, material, role, part, lod, u_count):
        rings = []
        for i, (py, pz) in enumerate(stations):
            a = stations[max(i-1, 0)]; b = stations[min(i+1, len(stations)-1)]
            ty, tz = b[0]-a[0], b[1]-a[1]; l = math.hypot(ty, tz)
            # Natural in-plane normal: forward on the lip and back, up on the pan, rotating continuously
            # over the rolled top. Every bend radius on the profile exceeds the 22 mm shell thickness.
            ny, nz = -tz/l, ty/l
            half, dish = width_at(i), dish_at(i)
            front, back = [], []
            for k in range(u_count+1):
                u = -1+2*k/u_count
                lift = dish*u**4
                cx = half*u*(1-.04*u*u)
                front.append((cx, py+ny*lift, pz+nz*lift))
                back.append((cx*(1-thickness/half*.6), py+ny*(lift-thickness), pz+nz*(lift-thickness)))
            edge_r = thickness*.5
            # Rounded side edges join the two surfaces.
            ring = list(front)
            fy, fz = front[-1][1:]; by_, bz = back[-1][1:]
            ring.append(((half+edge_r*.7), (fy+by_)/2, (fz+bz)/2))
            ring += list(reversed(back))
            fy, fz = front[0][1:]; by_, bz = back[0][1:]
            ring.append((-(half+edge_r*.7), (fy+by_)/2, (fz+bz)/2))
            rings.append([(x+cx, cy, cz) for cx, cy, cz in ring])
        n = len(rings[0]); pts = [p for r in rings for p in r]
        # End caps pair each sitting-face point with the matching underside point (ring layout:
        # front 0..u, edge u+1, underside u+2..2u+2 reversed, edge 2u+3). A centre fan would cut
        # through the side walls of these curved, thin crescents.
        u = u_count
        cap = [(k, k+1, 2*u+1-k, 2*u+2-k) for k in range(u)]+[(u, u+1, u+2), (2*u+2, 2*u+3, 0)]
        off = (len(rings)-1)*n
        faces = [tuple(reversed(f)) for f in cap]+[tuple(off+i for i in f) for f in cap]
        caps = len(faces)
        for j in range(len(rings)-1):
            for i in range(n):
                p, q = j*n+i, j*n+(i+1) % n
                faces += [(p, q, q+n), (p, q+n, p+n)]
        ob = mesh(name, pts, faces, material, part)
        for poly in ob.data.polygons:
            poly.use_smooth = poly.index >= caps
        ob['first_lod'] = ob['last_lod'] = lod
        return tag(ob, role)

    def seat_profile(y, z, samples):
        # (y, z) centre-line control points of the moulded shell's sitting face.
        ctrl = [(y-.060, z+.222), (y-.030, z+.233), (y-.003, z+.228), (y+.012, z+.200), (y+.016, z+.150),
                (y+.008, z+.040), (y-.004, z-.080), (y-.006, z-.150), (y+.020, z-.205),
                (y+.080, z-.236), (y+.230, z-.262), (y+.420, z-.290), (y+.600, z-.316),
                (y+.720, z-.330), (y+.776, z-.322), (y+.792, z-.300)]
        out = []
        pts = [ctrl[0]]+ctrl+[ctrl[-1]]
        for i in range(1, len(pts)-2):
            p0, p1, p2, p3 = pts[i-1], pts[i], pts[i+1], pts[i+2]
            for s in range(samples):
                t = s/samples
                out.append(tuple(.5*((2*p1[k])+(-p0[k]+p2[k])*t+(2*p0[k]-5*p1[k]+4*p2[k]-p3[k])*t*t
                                     +(-p0[k]+3*p1[k]-3*p2[k]+p3[k])*t**3) for k in (0, 1)))
        out.append(ctrl[-1])
        return out

    def seat(x, z, level, is_priority=False, pan_half=.205):
        section = 'front' if z > ctx['FRONT_JOIN'] else 'rear'
        select(section)
        y = level+.42
        shell_mat = priority if is_priority else yellow_seat
        primary = None
        for lod, samples, u_count in ((0, 2, 10), (1, 1, 6), (2, 1, 2)):
            stations = seat_profile(y, z, samples)
            if lod == 2:
                # Every other control station (keeps the lip, pan, roll and top) for the distant view.
                stations = stations[::2]+([stations[-1]] if len(stations) % 2 == 0 else [])
            count = len(stations)

            def width_at(i, stations=stations):
                # Full-width pan; the back narrows towards its rolled top, which also keeps the
                # outboard seats clear of the window-pillar trim (inner face |x|=1.1385).
                py = stations[i][0]-y
                return pan_half-.035*max(0., min(1., (py-.08)/.30))-.010*max(0., (py-.38)/.41)
            def dish_at(i, stations=stations):
                # Side bolsters on pan and back, faded out over the rolled top so the lift never
                # acts along the rotating normal there.
                py = stations[i][0]-y
                return .022 if py < .06 else .030*max(0., min(1., (.74-py)/.12))
            name = 'LOD%d %s' % (lod, primary.name) if lod else 'Passenger seat cushion'
            ob = shell_loft(name, stations, width_at, dish_at, .022, shell_mat, 'seat_cushion', 10, lod, u_count)
            if lod == 0:
                primary = ob
        ob = primary
        # Rear label plate printed on the back of the shell (as on the photographed seats).
        tag(box('Passenger seat back', (x, y+.50, z-.326), (.15, .060, .008),
                panel, 10), 'seat_back')
        lower = []
        for yy, dz, w, d in ((y-.088, 0, .300, .300), (y-.070, 0, .360, .360), (y-.010, -.005, .392, .405)):
            lower.append([(x+px, yy, z+dz+pz) for px, pz in rounded_xy(w, d, .040, 2)])
        tag(continuous_loft('Seat lower shell', lower, dark, 10), 'seat_structure')
        # USB charging module under the pan's front edge (official seat-underside photograph).
        tag(box('Seat USB charging module', (x+.12, y-.060, z+.175), (.050, .030, .040),
                black, 10, lod=1), 'seat_accessory')
        tag(box('Seat USB port light', (x+.12, y-.060, z+.1955), (.016, .010, .002),
                usb_blue, 10, lod=1), 'seat_accessory')
        for xx in (x-.15, x+.15):
            tag(rod('Seat leg', (xx, level+.006, z-.085),
                    (xx, y-.055, z-.085), .023, metal, 10, n=12), 'seat_support')
            tag(box('Seat support floor foot', (xx, level+.006, z-.085),
                    (.090, .012, .095), metal, 10, bevel=.004), 'seat_support')
        tag(rod('Seat support transverse beam', (x-.177, y-.075, z-.085),
                (x+.177, y-.075, z-.085), .023, metal, 10, n=12), 'seat_support')
        hz=z-.352  # rear face of the moulded back is at about z-.340 here; the tube seats into it
        # Uprights stay inside the tapered back (half width ~.16 at this height) so both feet
        # actually enter its rear face instead of standing beside the shell edge.
        hw=pan_half-.060
        handle=[(x-hw,y+.62,hz),(x-hw,y+.76,hz)]
        for j in range(1,7):
            a=math.pi-math.pi*.5*j/6
            handle.append((x-hw+.05+.05*math.cos(a),y+.76+.05*math.sin(a),hz))
        handle.append((x+hw-.05,y+.81,hz))
        for j in range(1,7):
            a=math.pi*.5-math.pi*.5*j/6
            handle.append((x+hw-.05+.05*math.cos(a),y+.76+.05*math.sin(a),hz))
        handle.append((x+hw,y+.62,hz))
        # One swept tube (closed end discs inside the shell) instead of 15 capped segments;
        # LOD1 halves the radial sampling, the distant LOD omits the handle as a whole.
        for lod, radial in ((0, 10), (1, 6)):
            rings = []
            for i, c in enumerate(handle):
                c = Vector(c); t = (Vector(handle[min(i+1, len(handle)-1)])-Vector(handle[max(i-1, 0)])).normalized()
                u = Vector((0, 0, 1)); v = t.cross(u).normalized()
                rings.append([tuple(c+.017*(u*math.cos(2*math.pi*j/radial)+v*math.sin(2*math.pi*j/radial))) for j in range(radial)])
            tube = continuous_loft(('LOD1 '+grab.name) if lod else 'Seat back grab handle', rings, metal, 10)
            tube['first_lod'] = tube['last_lod'] = lod
            if lod == 0:
                grab = tube
        # Two mounting bosses from inside the 22 mm shell to the tube: the tube (34 mm) cannot be
        # buried in the shell itself, so these carry it, as on the photographed seats.
        for sx in (-hw, hw):
            tag(rod('Seat grab handle mounting boss', (x+sx, y+.66, z-.322), (x+sx, y+.66, hz), .012,
                    metal, 10, n=10, lod=1), 'seat_accessory')
        row = {'section': section, 'cushion_unity': [x, y, z],
               'foot_floor_y': level, 'raised': level > .5,
               'facing_unity': [0, 0, 1], 'reference_status': 'estimated_complete_layout',
               'cushion_object': ob.name,
               'foot_zone_unity': {'x': [x-.17, x+.17], 'z': [z+.26, z+(.55 if abs(z+2.69)<.001 else .58)]},
               'knee_zone_unity': {'x': [x-.18, x+.18], 'y': [level+.38, level+.65],
                                   'z': [z+.26, z+.48]}}
        ctx['seat_records'].append(row)
        seating.append(row)

    # Door side just ahead of door 2 is the wheelchair bay (two wall-folded seats replace the two
    # fixed seats there, keeping the published 34-seat total); see saloon_refinement.py.
    wheelchair_bay = {'section': 'front', 'x': [.62, 1.205], 'z': [2.90, 3.88], 'floor_y': low,
                      'replaces_fixed_seats_at_z': 3.41, 'folding_seats': 2,
                      'reference': 'reference/new-bakelong-2024-04.png, reference/official-c18-en-2.jpg'}
    for side in (-1, 1):
        for z in (5.09, 4.25, 3.41):
            if side > 0 and z == 3.41:
                continue
            for x in (side*.49, side*.95):
                seat(x, z, low, is_priority=(side < 0 and z == 3.41))
    for z in (2.57, 1.73):
        for x in (-.49, -.95):
            seat(x, z, low)
    for z in (-3.70, -4.55):
        for x in (-.49, -.95):
            # Preserve hand-wrap clearance beside the stair rail without
            # moving the outboard seat into the side-window trim.
            seat(-.51 if z == -4.55 and x == -.49 else x, z, low)
    # official-k18-interior.jpg (rearward from the joint) is a mirrored marketing image: door 3
    # appears on the same side as the orange priority pair, the equipment cabinet opposite. So the
    # priority pair is on the door side (+x) just ahead of door 3 and the cabinet on the driver side.
    for x in (.49, .95):
        seat(x, -2.69, low, is_priority=True)
    for z in (-6.30, -7.15, -8.00):
        for side in (-1, 1):
            # Raised-deck pans sit at window-pillar height: slightly narrower shells and the
            # outboard seat 25 mm inboard keep 3 mm to the pillar trim (|x|=1.1385).
            for x in (side*.49, side*.925):
                seat(x, z, high, pan_half=.200)

    # Photo-supported stair handrails. Positions are engineering estimates.
    # The continuous swept tube has rounded elbows, closed ends and actual
    # floor-mounted posts. A 42 mm hand-envelope radius is audited separately.
    select('rear')
    stair_handholds = []
    for side in (-1, 1):
        x = side*.245
        raw = [Vector((x, 1.18, -4.40)), Vector((x, 1.18, -4.55)),
               Vector((x, 2.00, -5.71)), Vector((x, 2.00, -5.88))]
        centres = [raw[0]]
        for i in (1, 2):
            corner = raw[i]
            a = corner-(corner-raw[i-1]).normalized()*.060
            b = corner+(raw[i+1]-corner).normalized()*.060
            centres.append(a)
            for j in range(1, 7):
                t = j/6
                centres.append((1-t)**2*a+2*(1-t)*t*corner+t*t*b)
        centres.append(raw[-1])
        points = []
        radial = 12
        for i, centre in enumerate(centres):
            tangent = (centres[min(i+1, len(centres)-1)]-centres[max(0, i-1)]).normalized()
            u = Vector((1, 0, 0))
            v = tangent.cross(u).normalized()
            for j in range(radial):
                a = 2*math.pi*j/radial
                points.append(tuple(centre+.014*(u*math.cos(a)+v*math.sin(a))))
        faces = [tuple(reversed(range(radial))),
                 tuple((len(centres)-1)*radial+j for j in range(radial))]
        for i in range(len(centres)-1):
            for j in range(radial):
                faces.append((i*radial+j, i*radial+(j+1)%radial,
                              (i+1)*radial+(j+1)%radial, (i+1)*radial+j))
        rail = tag(mesh('Rear stair continuous handrail', points, faces, metal, 13), 'stair_handrail')
        for poly in rail.data.polygons:
            poly.use_smooth = poly.index >= 2
        for z, bottom, top in ((-4.43, low, 1.18), (-5.85, high, 2.00)):
            tag(box('Stair handrail floor mounting foot', (x, bottom+.005, z),
                    (.060, .010, .060), metal, 13), 'stair_handrail_mount')
            tag(rod('Stair handrail supported upright', (x, bottom+.005, z),
                    (x, top, z), .014, metal, 13, n=12), 'stair_handrail_mount')
        stair_handholds.append({'object':rail.name, 'side':side, 'radius':.014,
                                'hand_envelope_radius':.042,
                                'centreline_unity':[list(p) for p in centres]})

    # Re-layout the complete cockpit around the retained formal eye, rather
    # than moving the seat alone into the old steering wheel. Anthropometric
    # points below are a review envelope, not an unreferenced factory dummy.
    remove_prefixes(('Dashboard lower console', 'Instrument hood', 'LCD instrument',
                     'Driver cushion', 'Driver seat back', 'Driver suspension pedestal',
                     'Steering column', 'Steering rim', 'Steering spoke',
                     'Driver pedal', 'Driver footwell floor', 'Driver side controls',
                     'Driver switch'))
    old_steer = bpy.data.objects.get('Steering wheel pivot')
    if old_steer:
        bpy.data.objects.remove(old_steer, do_unlink=True)
    select('front')
    cab_floor = .54
    floor_box('Driver raised footwell floor', -1.17, -.37, 7.17, 8.45, cab_floor, low)
    floor_box('Driver footwell entry step', -.37, -.20, 7.23, 7.68, .44, low)
    driver_x, driver_z, cushion_y = -.79, 7.44, 1.065
    rows = []
    for j in range(7):
        t = j/6
        rows.append([(driver_x+.235*u, cushion_y+.027*u*u-.014*t,
                      driver_z-.225+.47*t) for u in (-1, -.75, -.4, 0, .4, .75, 1)])
    curved_plate('Driver cushion', rows, .090, dark, 'driver_seat', (0, -1, 0), 12)
    rows = []
    for j in range(9):
        t = j/8
        half = .235-.065*max(0, (t-.65)/.35)
        rows.append([(driver_x+half*u, 1.015+.735*t, 7.225-.070*t+.035*u*u)
                     for u in (-1, -.75, -.4, 0, .4, .75, 1)])
    curved_plate('Driver seat back', rows, .080, dark, 'driver_seat', (0, 0, -1), 12)
    tag(box('Driver seat head restraint', (-.79, 1.815, 7.16), (.29, .20, .14),
            dark, 12, bevel=.035), 'driver_seat')
    tag(box('Driver suspension pedestal', (-.79, (cab_floor+.990)/2, 7.44),
            (.32, .990-cab_floor, .33), dark, 12, bevel=.018), 'driver_seat_support')
    for side in (-1, 1):
        x = driver_x+side*.235
        tag(box('Driver armrest', (x, 1.28, 7.43), (.052, .050, .36),
                dark, 12, bevel=.015), 'driver_seat')
        tag(rod('Driver armrest support', (x, 1.00, 7.28), (x, 1.265, 7.28),
                .016, metal, 12), 'driver_seat_support')

    # The lower console consists of real side returns and a top cowl, leaving
    # the knee/foot cavity open. A solid box here intersected the seated legs.
    tag(box('Dashboard top cowl', (-.43, 1.235, 8.48), (1.48, .22, .40),
            dark, 11, bevel=.055), 'dashboard')
    for x, width in ((-1.13, .08), (-.25, .32)):
        tag(box('Dashboard lower side return', (x, .85, 8.44),
                (width, .69, .40), panel, 11, bevel=.023), 'dashboard')
    tag(box('Instrument hood', (-.79, 1.50, 8.565), (.69, .32, .32),
            dark, 11, bevel=.060), 'dashboard')
    tag(box('Instrument dial mounting panel', (-.79, 1.49, 8.396),
            (.624, .253, .022), dark, 11, bevel=.010), 'dashboard')

    # Non-emissive static dial markings are distinct from the movable needles.
    # Needle local +Z points towards the driver; their rest direction is +Y.
    from mathutils import Quaternion
    for name, x, part in (('Speed', -.595, 2), ('Rpm', -.985, 3)):
        y, z, r = 1.49, 8.380, .100
        tag(rod(name+' gauge bezel', (x, y, z-.006), (x, y, z+.006),
                r+.008, metal, 11, n=40), 'instrument')
        tag(rod(name+' gauge dial', (x, y, z-.010), (x, y, z-.006),
                r, dark, 11, n=40), 'instrument')
        for j in range(13):
            a = math.radians(-135+j*22.5)
            length = .017 if j % 3 == 0 else .010
            p = (x+(r-.009)*math.sin(a), y+(r-.009)*math.cos(a), z-.013)
            q = (x+(r-.009-length)*math.sin(a), y+(r-.009-length)*math.cos(a), z-.013)
            tag(rod(name+' gauge tick', p, q, .0018, panel, 11, n=6, lod=1), 'instrument')
        pivot = ctx['empty'](name+' needle pivot', (x, y, z-.021), ctx['ROOTS']['front'], part)
        pivot['rest_rotation_unity'] = [0., 1., 0., 0.]
        tag(skin(name+' needle', [(x-.003, y-.015, z-.022),
                (x+.003, y-.015, z-.022), (x+.002, y+.074, z-.022),
                (x-.002, y+.074, z-.022)], (0, 0, .003), red, part, pivot), 'instrument_needle')
        ctx['annotate_animation'](pivot, 1, [(1, 0), (130, 0), (160, 65), (190, -65), (220, 0)])
    tag(box('Central status display', (-.79, 1.49, 8.373), (.154, .158, .018),
            black, 11, bevel=.003), 'instrument')
    for yy in (1.44, 1.48, 1.54):
        tag(box('Status display static information row', (-.79, yy, 8.362),
                (.124, .003, .003), panel, 11, lod=1), 'instrument')
    for x in (-.824, -.790, -.756):
        tag(box('Status display indicator', (x, 1.51, 8.361), (.020, .015, .003),
                ctx['BLUE'], 11, lod=1), 'instrument')

    wheel_centre = Vector((-.79, 1.39, 8.04))
    wheel_normal = Vector((0., .866025403784, -.5))
    # With local X reversed, X cross Y equals the normal towards the driver.
    wheel_x, wheel_y = Vector((-1., 0., 0.)), Vector((0., .5, .866025403784))
    q = Quaternion((1., 0., 0.), math.radians(-120))
    steer = ctx['empty']('Steering wheel pivot', wheel_centre, ctx['ROOTS']['front'], 1)
    steer['rest_rotation_unity'] = [q.x, q.y, q.z, q.w]
    pts = []
    for k in range(10):
        a = k*2*math.pi/10
        for j in range(48):
            radial = wheel_x*math.cos(j*2*math.pi/48)+wheel_y*math.sin(j*2*math.pi/48)
            pts.append(tuple(wheel_centre+(.224+.021*math.cos(a))*radial+.021*math.sin(a)*wheel_normal))
    faces = []
    for k in range(10):
        for j in range(48):
            faces.append((k*48+j, k*48+(j+1)%48, ((k+1)%10)*48+(j+1)%48, ((k+1)%10)*48+j))
    rim = tag(mesh('Steering rim continuous molded grip', pts, faces, dark, 1, steer), 'steering')
    rim['radial_segments']=48
    rim['radial_rings']=10
    for poly in rim.data.polygons:
        poly.use_smooth=True
    for a in (math.radians(20), math.radians(160), math.radians(270)):
        p = wheel_centre+.202*(wheel_x*math.cos(a)+wheel_y*math.sin(a))
        tag(rod('Steering spoke', wheel_centre, p, .022, metal, 1, steer), 'steering')
    tag(rod('Steering hub', wheel_centre-wheel_normal*.035,
            wheel_centre+wheel_normal*.020, .090, dark, 1, steer, n=28), 'steering')
    tag(rod('Steering column', (-.79, .548, 8.395), wheel_centre-wheel_normal*.030,
            .034, dark, 11, n=16), 'steering')
    tag(box('Steering column floor bearing plate', (-.79, cab_floor+.006, 8.395),
            (.13, .012, .110), metal, 11), 'steering_mount')
    axis_b = (U.to_3x3() @ wheel_normal).normalized()
    steer.rotation_mode = 'QUATERNION'
    for frame, angle in ((1, 0), (130, 0), (160, 100), (190, -100), (220, 0)):
        steer.rotation_quaternion = Quaternion(axis_b, math.radians(angle))
        steer.keyframe_insert('rotation_quaternion', frame=frame)
    steer.rotation_quaternion = Quaternion()

    for x, width in ((-.90, .12), (-.65, .085)):
        tag(box('Driver pedal', (x, .598, 8.20), (width, .023, .21),
                dark, 12, bevel=.006), 'driver_pedal')
        tag(rod('Driver pedal support', (x, cab_floor, 8.27), (x, .59, 8.23),
                .014, metal, 12), 'driver_pedal')
    tag(box('Driver side controls', (-1.135, 1.08, 7.73), (.14, .23, .67),
            dark, 11, bevel=.018), 'dashboard')
    for j in range(6):
        tag(box('Driver switch', (-1.108, 1.199, 7.53+j*.074), (.065, .012, .044),
                red if j < 2 else black, 11, bevel=.003, lod=1), 'instrument')
    for centre, size in (((-.255, 1.55, 8.325), (.33, .25, .065)),
                         ((-1.075, 1.755, 8.50), (.22, .16, .05))):
        tag(box('Driver auxiliary monitor housing', centre, size, dark, 11, bevel=.015), 'instrument')
        x, y, z = centre
        tag(box('Driver auxiliary monitor screen', (x, y, z-size[2]/2-.003),
                (size[0]-.028, size[1]-.028, .008), black, 11, bevel=.002), 'instrument')
        tag(rod('Driver monitor fixed mount', (x, 1.34, z+.014),
                (x, y-size[1]/2, z+.014), .016, dark, 11), 'instrument')
    for x in (-.34, -.25, -.16):
        tag(rod('Driver console red pushbutton', (x, 1.25, 8.252),
                (x, 1.25, 8.237), .024, red, 11, n=12, lod=1), 'instrument')

    cockpit = {'eye_unity': [-.79, 1.97, 7.62],
               'seat_centre_unity': [driver_x, cushion_y, driver_z],
               'steering_pivot_unity': list(wheel_centre),
               'steering_axis_towards_driver_unity': list(wheel_normal),
               'foot_floor_y': cab_floor,
               'occupant_review_points': {'hip': [-.79, 1.17, 7.42],
                   'shoulder': [-.79, 1.73, 7.40], 'head_centre': [-.79, 1.94, 7.51],
                   'knees': [[-.91, 1.11, 7.91], [-.67, 1.11, 7.91]],
                   'ankles': [[-.91, .69, 8.13], [-.67, .69, 8.13]],
                   'toes': [[-.91, .62, 8.28], [-.67, .62, 8.28]]},
               'status': 'Photo-guided layout; anthropometric review envelope, not factory dimensions.'}

    # Cabinet and floor plinth just behind the joint on the driver side, opposite door 3 (the
    # official cabin view is mirrored, see the priority seats above); the fire extinguisher stands
    # on the plinth in front of the cabinet. Exact depth, vents and latch positions are estimates.
    select('rear')
    floor_box('Joint equipment cabinet plinth', -1.16, -.47, -3.03, -2.17,
              .515, low, 'equipment_plinth')
    tag(box('Joint equipment cabinet', (-.815, .99, -2.665),
            (.65, .95, .67), panel, 0, bevel=.024), 'equipment_cabinet')
    tag(box('Joint equipment cabinet service door', (-.481, .99, -2.665),
            (.014, .82, .58), panel, 0, bevel=.006), 'equipment_cabinet')
    for z in (-2.86, -2.47):
        tag(box('Equipment cabinet latch', (-.466, 1.02, z),
                (.012, .09, .035), dark, 0, lod=1), 'equipment_detail')
    for i in range(9):
        tag(box('Equipment cabinet vent', (-.467, .64+i*.035, -2.885),
                (.010, .015, .12), dark, 0, lod=1), 'equipment_detail')
    extinguisher_red = ctx['mat']('Fire extinguisher red', (.62, .025, .020), .35, 0, 'RequestRed')
    ex_x, ex_z, ex_r = -.620, -2.250, .062
    body = tag(rod('Fire extinguisher cylinder', (ex_x, .516, ex_z), (ex_x, .935, ex_z), ex_r,
                   extinguisher_red, 0, n=20), 'equipment_detail')
    tag(rod('Fire extinguisher shoulder', (ex_x, .934, ex_z), (ex_x, .960, ex_z), ex_r*.62,
            extinguisher_red, 0, n=16), 'equipment_detail')
    tag(rod('Fire extinguisher valve', (ex_x, .958, ex_z), (ex_x, 1.000, ex_z), .016, metal, 0, n=12, lod=1), 'equipment_detail')
    tag(box('Fire extinguisher lever', (ex_x-.030, 1.005, ex_z), (.075, .012, .022), black, 0, lod=1), 'equipment_detail')
    hose = [(ex_x+.012, .990, ex_z), (ex_x+.060, .960, ex_z+.010), (ex_x+.072, .840, ex_z+.040), (ex_x+.064, .700, ex_z+.050)]
    path('Fire extinguisher hose', hose, .008, black, 0, lod=1)
    for yy in (.62, .85):
        tag(box('Fire extinguisher wall bracket strap', (ex_x, yy, ex_z-.050), (.13, .025, .060),
                dark, 0), 'equipment_detail')

    # Original handrail anchors included one beyond the front section, and the
    # rear standing rail continued through the high rear seat row. Terminate
    # the standing-area system before the steps; seat grabs remain independent.
    for ob in list(bpy.data.objects):
        if ob.type != 'MESH':
            continue
        section = ob.get('cab_section')
        if ob.name.startswith('Suspended rail ceiling anchor'):
            z = sum((U.inverted() @ v.co).z for v in ob.data.vertices)/len(ob.data.vertices)
            if section == 'front' and z < ctx['FRONT_JOIN']:
                for v in ob.data.vertices:
                    p = U.inverted() @ v.co
                    p.z += -.38-z
                    v.co = U @ p
            elif section == 'rear' and z < -4.55:
                removed.append(ob.name)
                bpy.data.objects.remove(ob, do_unlink=True)
        elif section == 'rear' and ob.name.startswith('Upper longitudinal handrail'):
            for v in ob.data.vertices:
                p = U.inverted() @ v.co
                if p.z < -4.55:
                    p.z = -4.55
                    v.co = U @ p
        elif section == 'rear' and ob.name.startswith(('Door bay vertical stanchion', 'Stop request button')):
            z = sum((U.inverted() @ v.co).z for v in ob.data.vertices)/len(ob.data.vertices)
            if z < -4.55:
                removed.append(ob.name)
                bpy.data.objects.remove(ob, do_unlink=True)
    # Preserve every strap part number. The aft pair moves into the standing
    # bay; local geometry and its recorded pivot move by the same translation.
    for record in ctx['strap_records']:
        if record['section'] == 'rear' and record['pivot_unity'][2] < -4.55:
            old_z, new_z = record['pivot_unity'][2], -4.50
            for ob in bpy.data.objects:
                if (ob.type == 'EMPTY' and ob.get('cab_section') == 'rear'
                        and ob.get('cdlc_part') == record['part']):
                    ob.location += U @ Vector((0, 0, new_z-old_z))
                    pivot = list(ob['rest_pivot_unity'])
                    pivot[2] += new_z-old_z
                    ob['rest_pivot_unity'] = pivot
            record['pivot_unity'][2] = new_z

    # Keep the essential seat feet in the far silhouette. Existing LOD rules
    # retain names containing "seat leg", so make this relationship explicit.
    for ob in bpy.data.objects:
        role = ob.get('k18_completion_role')
        if role == 'seat_support' and 'Seat leg' not in ob.name:
            ob.name = ob.name.replace('Seat support', 'Seat leg support')
        # Ducts close the ceiling-to-side transition. Their structure is visible
        # in all LODs; fine louvers, rails, instruments and straps still simplify.
        if ob.name.startswith('Air-conditioning duct'):
            ob['first_lod'] = 0
            ob['last_lod'] = 2
        # This simple fixed housing is also the door opening's structural
        # header. Dropping it in LOD2 reopens the band from y=2.598 to 2.625.
        if ob.name.startswith('Door overhead mechanism'):
            ob['first_lod'] = 0
            ob['last_lod'] = 2
            ob['k18_completion_role'] = 'door_header'
        # Use the existing wall's real inner face for cabin trim. This changes
        # no boundary, adds no cover plate and leaves exterior paint untouched.
        if (ob.type == 'MESH' and (' lower skin ' in ob.name or ' lower side ' in ob.name)):
            if panel.name not in ob.data.materials:
                ob.data.materials.append(panel)
            slot=ob.data.materials.find(panel.name)
            for poly in ob.data.polygons:
                centre=U.inverted() @ poly.center
                if abs(centre.x)<1.22:
                    poly.material_index=slot

    bpy.context.view_layer.update()
    ctx['scene']['k18_interior_contract'] = json.dumps({
        'revision': 1, 'status': 'BUILT_REQUIRES_INDEPENDENT_ASSEMBLY_AUDIT',
        'reference': 'reference/official-k18-interior.jpg',
        'reference_limits': ['Complete seat map and 34-seat count are study estimates.',
                             'Platform heights, tread dimensions and equipment cabinet dimensions are estimates.',
                             'Cockpit adjustment travel and complete door linkage kinematics require review.'],
        'low_floor_y': low, 'rear_floor_y': high,
        'rear_window_opening': [-1.04, 1.04, 1.605, 2.335],
        'rear_wall_inside_z': -8.65, 'rear_deck_front_z': deck_front,
        'steps': steps, 'stair_handholds':stair_handholds,
        'walking_surfaces': surfaces, 'seats': seating, 'cockpit': cockpit, 'wheelchair_bay': wheelchair_bay,
        'window_sill_runs': sill_runs, 'wheelhouses': wheelhouses,
        'required_front_steering_degrees': [0, -15, 15, -25, 25, -35, 35],
        'removed_objects': removed, 'new_objects': made,
        'door_pivots_modified': False, 'shared_card_reader_modified': False,
        'lod_strategy': 'Closed floor/wheelhouse/rear-wall boundaries shared at all levels; seats simplify by the existing analytic rules; fine cabin details remain omittable.'
    }, ensure_ascii=False)
    return json.loads(ctx['scene']['k18_interior_contract'])
