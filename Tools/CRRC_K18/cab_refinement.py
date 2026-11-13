"""Photo-guided K18 cockpit; call after build_interior_completion(ctx).

All construction coordinates are Unity vehicle metres. The supplied builder
maps them to Blender once. This module changes only cockpit meshes and the
three cockpit animation pivots. The seated driver station moves as one unit;
the mirrors and body remain unchanged.
Photographs establish arrangement and silhouette, not surveyed dimensions.
"""


def build_k18_cab_refinement(ctx):
    import hashlib
    import json
    import math
    import runpy
    from pathlib import Path
    import bpy
    from mathutils import Matrix, Quaternion, Vector

    root = Path(ctx['ROOT'])
    reference_dir = root / 'ModelSource/CRRC_K18/reference'
    observations = {
        'official-k18-dashboard.jpg': 'Wide arched hood over a gloss-black fascia: two dials (left x100 r/min with green-amber-red arc, right 0-140 km/h), two small upper gauges, central colour LCD with six buttons below; hooded monitors on both towers; D/N/R column.',
        'official-k18-instruments.jpg': 'Deep instrument tunnel with driver camera on the hood crown; thick rim, horizontal side spokes, broad lower spoke, shield horn pad with a chrome strip on its lower edge; coiled microphone cord to a wall-mounted handset.',
        'official-k18-driver-monitor.jpg': 'Quilted black seat with red stitching and integrated headrest, hi-vis belt, diamond tread plate in the footwell.',
        'official-k18-driver-seat.jpg': 'One moulded dashboard: window-side tower (7-inch hooded monitor, rotary light switch, three red rockers, stepped green switch banks), arched hood, aisle-side tower (10-inch hooded monitor, two switch banks, stepped bank with three red buttons); dark dash top to the windshield; window-side console with keypad and lever; handheld microphone on the wall.',
    }
    references = []
    for name, observation in observations.items():
        p = reference_dir / name
        if not p.is_file():
            raise FileNotFoundError(p)
        references.append({'path': str(p), 'sha256': hashlib.sha256(p.read_bytes()).hexdigest(),
                           'observation': observation})

    ctx['select_section']('front')
    box, mesh, rod = (ctx[n] for n in ('box', 'mesh', 'rod'))
    U, scene, parent = ctx['U'], ctx['scene'], ctx['ROOTS']['front']
    contract = json.loads(scene['k18_interior_contract'])
    old_cockpit = contract['cockpit']
    original_eye = list(parent['driver_eye_unity'])
    # Reserve a real outboard bay for the left monitor. Translate the whole
    # seated driving station (not just its camera) 130 mm inward from the old
    # layout, remaining entirely on the existing raised driver floor.
    driver_x = -.660
    driver_shift = driver_x-float(old_cockpit['eye_unity'][0])
    eye = list(original_eye)
    eye[0] = driver_x
    removed, made = [], []
    roles = {'driver_seat', 'driver_seat_support', 'dashboard', 'instrument',
             'instrument_needle', 'steering', 'steering_mount', 'driver_pedal'}
    old_prefixes = ('Dashboard ', 'Instrument ', 'LCD instrument', 'Central status display',
                    'Status display ', 'Speed gauge ', 'Rpm gauge ', 'Speed needle', 'Rpm needle',
                    'Steering ', 'Driver cushion', 'Driver seat back', 'Driver seat head restraint',
                    'Driver suspension pedestal', 'Driver armrest', 'Driver pedal',
                    'Driver side controls', 'Driver switch', 'Driver auxiliary monitor',
                    'Driver monitor fixed mount', 'Driver console red pushbutton')
    # Collect first: removing children/pivots while iterating a live RNA list
    # invalidates iterators. The partition and actual footwell floor are retained.
    targets = [o for o in list(bpy.data.objects) if o.get('cab_section') == 'front' and
               ((o.type == 'MESH' and (o.get('k18_cab_refinement') or
                  o.get('k18_completion_role') in roles or o.name.startswith(old_prefixes))) or
                (o.type == 'EMPTY' and o.get('cdlc_part') in (1, 2, 3)))]
    for ob in sorted(targets, key=lambda o: o.type == 'EMPTY'):
        removed.append(ob.name)
        bpy.data.objects.remove(ob, do_unlink=True)

    def material(name, rgb, rough, swatch, metallic=0):
        return ctx['mat']('K18 cockpit | ' + name, rgb, rough, metallic, swatch)

    dash = material('molded graphite', (.023, .027, .030), .72, 'DashBlack')
    face = material('instrument charcoal', (.050, .055, .058), .56, 'DarkGrey')
    rubber = material('grip and seat vinyl', (.018, .022, .024), .78, 'Rubber')
    screen = material('unpowered monitor face', (.007, .012, .014), .24, 'Screen')
    white = material('printed markings', (.72, .76, .73), .60, 'SafetyWhite')
    green = material('green status legends', (.055, .48, .12), .48, 'Green')
    red = material('red safety switches', (.62, .012, .008), .52, 'Red')
    # Amber's palette finish is Beacon; use the non-emissive orange swatch
    # for printed legends, retaining light_id=0 throughout this cockpit.
    amber = material('amber status legends', (.95, .38, .015), .48, 'Orange')
    belt = material('yellow green safety webbing', (.58, .72, .013), .91, 'Yellow')
    silver, steel = ctx['CHROME'], ctx['METAL']

    def tag(ob, role, last=2):
        ob['k18_cab_refinement'] = 1
        ob['k18_completion_role'] = role
        # Explicit unchanged structure across LODs: never let the generic
        # simplifier remove all part 11/12 geometry from the distant section.
        ob['first_lod'], ob['last_lod'] = 0, last
        made.append(ob.name)
        return ob

    def frame(origin, yaw=0, tilt=0):
        yaw, tilt = math.radians(yaw), math.radians(tilt)
        h = Vector((math.cos(yaw), 0, math.sin(yaw)))
        v = Vector((-math.sin(yaw)*math.sin(tilt), math.cos(tilt), math.cos(yaw)*math.sin(tilt)))
        n = v.cross(h).normalized()  # driver-facing, normally -Z
        origin = Vector(origin)
        def at(x, y, depth=0):
            return tuple(origin + h*x + v*y + n*depth)
        return at, h, v, n

    def rounded(w, h, radius, steps=5):
        r = min(radius, w*.45, h*.45)
        pts = []
        for x, y, a in ((w/2-r, h/2-r, 0), (-w/2+r, h/2-r, 90),
                        (-w/2+r, -h/2+r, 180), (w/2-r, -h/2+r, 270)):
            for j in range(steps+1):
                t = math.radians(a+90*j/steps)
                pts.append((x+r*math.cos(t), y+r*math.sin(t)))
        return pts

    def closed_loft(name, rings, mat, role, part=11, owner=None, last=2, smooth=False):
        n = len(rings[0])
        if n < 3 or any(len(r) != n for r in rings):
            raise ValueError('Invalid cockpit loft ' + name)
        pts = [tuple(p) for ring in rings for p in ring]
        # Explicit center-fan caps keep valid small faces through the exporter.
        cap0 = tuple(sum((Vector(p) for p in rings[0]), Vector())/n)
        cap1 = tuple(sum((Vector(p) for p in rings[-1]), Vector())/n)
        pts += [cap0, cap1]
        a, b = len(pts)-2, len(pts)-1
        faces = [(a, (i+1) % n, i) for i in range(n)]
        off = (len(rings)-1)*n
        faces += [(b, off+i, off+(i+1) % n) for i in range(n)]
        for j in range(len(rings)-1):
            for i in range(n):
                p, q = j*n+i, j*n+(i+1) % n
                faces += [(p, q, q+n), (p, q+n, p+n)]
        ob = tag(mesh(name, pts, faces, mat, part, owner), role, last)
        if smooth:
            for poly in ob.data.polygons:
                poly.use_smooth = poly.index >= 2*n
        return ob

    def plate(name, f, outline, depth, thickness, mat, role='instrument', part=11, owner=None, last=2):
        return closed_loft(name, [[f(x,y,depth-thickness) for x,y in outline],
                                  [f(x,y,depth) for x,y in outline]], mat, role, part, owner, last)

    def rim(name, f, outer, inner, front, back, mat, role='dashboard', part=11, owner=None, last=2):
        if len(outer) != len(inner):
            raise ValueError('Cockpit rim boundaries differ')
        rings = [[f(x,y,d) for x,y in ring] for ring,d in
                 ((outer,front),(outer,back),(inner,back),(inner,front))]
        n = len(outer); pts = [p for row in rings for p in row]; faces = []
        for row in range(4):
            next_row = (row+1) % 4
            for i in range(n):
                a, b = row*n+i, row*n+(i+1) % n
                c, d = next_row*n+(i+1) % n, next_row*n+i
                faces += [(a,b,c),(a,c,d)]
        return tag(mesh(name, pts, faces, mat, part, owner), role, last)

    def pillow(name, f, w, h, radius, depth, thickness, mat, role='dashboard', part=11, owner=None, last=2):
        """Moulded slab whose back/front edges step in, so the rim reads as a rounded return
        instead of a razor-edged extrusion. Same face convention as plate()."""
        rings = []
        for inset, frac in ((.008, 0.), (.002, .20), (0., .55), (.002, .85), (.007, 1.)):
            outline = rounded(w-2*inset, h-2*inset, max(.006, radius-inset), 5)
            rings.append([f(x, y, depth-thickness+frac*thickness) for x, y in outline])
        return closed_loft(name, rings, mat, role, part, owner, last, smooth=True)

    def line(name, f, points, radius, mat=white, part=11, owner=None, last=0, depth=0):
        for a,b in zip(points,points[1:]):
            tag(rod(name, f(a[0],a[1],depth), f(b[0],b[1],depth), radius, mat,
                    part, owner, n=6), 'instrument', last)

    def circle(name, f, x, y, r, front, mat, role='instrument', part=11, owner=None, last=2, thickness=.004):
        loop = [(x+r*math.cos(i*2*math.pi/40),y+r*math.sin(i*2*math.pi/40)) for i in range(40)]
        return plate(name,f,loop,front,thickness,mat,role,part,owner,last)

    def numeral(label,text,f,x,y,depth,scale=1.):
        """Small closed stroke geometry for legible dial numerals, no font dependency."""
        glyphs={
            '0':[[(.5+.43*math.cos(t*math.pi/6),.5+.5*math.sin(t*math.pi/6)) for t in range(13)]],
            '1':[[(.18,.78),(.52,1),(.52,0)],[(.18,0),(.84,0)]],
            '2':[[(.08,.82),(.20,.98),(.70,1),(.92,.80),(.88,.64),(.10,0),(.94,0)]],
            '3':[[(.09,.92),(.42,1),(.80,.91),(.88,.71),(.60,.52),(.36,.52)],[(.60,.52),(.90,.34),(.89,.15),(.68,0),(.23,0),(.06,.12)]],
            '4':[[(.73,0),(.73,1),(.08,.29),(.98,.29)]],
            '5':[[(.92,1),(.13,1),(.10,.55),(.62,.55),(.91,.39),(.91,.14),(.70,0),(.22,0),(.06,.13)]],
            '6':[[(.88,.91),(.70,1),(.30,.95),(.10,.68),(.10,.20),(.28,.02),(.67,0),(.90,.16),(.90,.40),(.71,.55),(.34,.56),(.10,.38)]],
            '7':[[(.04,1),(.95,1),(.30,0)]],
            '8':[[(.50,.52),(.16,.68),(.12,.86),(.31,1),(.73,1),(.90,.82),(.84,.65),(.50,.52),(.15,.38),(.09,.17),(.29,0),(.73,0),(.92,.17),(.85,.37),(.50,.52)]],
            '9':[[(.89,.58),(.64,.44),(.27,.46),(.09,.64),(.10,.86),(.33,1),(.69,.98),(.90,.78),(.90,.32),(.69,.03),(.27,0),(.09,.10)]]}
        width,height,gap=.0065*scale,.011*scale,.0023*scale
        total=len(text)*width+(len(text)-1)*gap
        for i,char in enumerate(text):
            for path in glyphs[char]:
                line(label+' gauge scale number '+text,f,
                     [(x-total/2+i*(width+gap)+px*width,y+(py-.5)*height) for px,py in path],
                     .00045*min(1.,scale*1.15),white,last=0,depth=depth)

    # Factory K18 cockpit (official-k18-driver-seat/dashboard/instruments.jpg): one moulded
    # dashboard. A tall window-side control tower (hooded 7-inch monitor, rotary light switch,
    # three red rockers, stepped green-lit switch banks), a wide arched instrument hood with a
    # driver camera on top, and an aisle-side tower (hooded 10-inch monitor, two switch banks
    # with a D/N/R column, a stepped lower bank with three red buttons). The dark dash top runs
    # forward to the windshield and the lower dash closes the floor gap to the front bulkhead.
    from mathutils.bvhtree import BVHTree
    import bmesh
    B = U.inverted()

    def inherited(o, key, default):
        while o is not None:
            if key in o:
                return o[key]
            o = o.parent
        return default

    # Fixed body (incl. glass) already in the front section, to clamp the dash to the actual
    # bulkhead/windshield surfaces instead of assumed numbers.
    bpy.context.view_layer.update()
    deps = bpy.context.evaluated_depsgraph_get()
    body_points, body_tris = [], []
    for o in list(bpy.data.objects):
        if o.type != 'MESH' or inherited(o, 'cab_section', None) != 'front' or o.get('k18_cab_refinement'):
            continue
        if int(inherited(o, 'first_lod', 0)) > 0 or o.name.startswith(('LOD1 ', 'LOD2 ')):
            continue
        ev = o.evaluated_get(deps); data = ev.to_mesh()
        try:
            data.calc_loop_triangles(); start = len(body_points)
            body_points += [B @ (ev.matrix_world @ v.co) for v in data.vertices]
            body_tris += [tuple(start+i for i in t.vertices) for t in data.loop_triangles]
        finally:
            ev.to_mesh_clear()
    body = BVHTree.FromPolygons(body_points, body_tris, all_triangles=True)

    def ahead(x, y, z0=8.40):
        hit = body.ray_cast(Vector((x, y, z0)), Vector((0, 0, 1)), 2.0)[0]
        return hit.z if hit is not None else z0+2.0

    def front_limit(x, y0, y1, clearance):
        ys = [y0+(y1-y0)*k/6 for k in range(7)]
        if x < -.36:  # the raised driver platform occupies y < .54 there
            ys = [y for y in ys if y >= .55] or [.55]
        return min(ahead(x, y) for y in ys)-clearance

    def sharpen(ob, degrees=32):
        """Smooth shading across fillets, hard creases at panel steps (exported as corner normals)."""
        bm = bmesh.new(); bm.from_mesh(ob.data)
        limit = math.radians(degrees)
        for f in bm.faces:
            f.smooth = True
        for e in bm.edges:
            e.smooth = not (len(e.link_faces) == 2 and e.calc_face_angle(0) > limit)
        bm.to_mesh(ob.data); bm.free(); ob.data.update()
        return ob

    def fillet_ring(corners, radii, steps=4):
        pts = []
        for i, c in enumerate(corners):
            c = Vector(c); a = Vector(corners[i-1]); b = Vector(corners[(i+1) % len(corners)])
            p0 = c+(a-c).normalized()*radii[i]; p2 = c+(b-c).normalized()*radii[i]
            for j in range(steps+1):
                t = j/steps
                pts.append(tuple((1-t)**2*p0+2*(1-t)*t*c+t*t*p2))
        return pts

    def arched(w, h, r, arch, steps=8):
        return [(x, y+arch*(1-(2*x/w)**2)*max(0., y)/(h/2)) for x, y in rounded(w, h, r, steps)]

    def rect(x0, x1, y0, y1):
        return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]

    gloss = material('gloss instrument fascia', (.010, .012, .014), .22, 'Screen')
    dial_black = material('dial face black', (.012, .013, .015), .62, 'DashBlack')
    blue = material('blue microphone key', (.02, .16, .62), .45, 'Blue')

    def label(f, x0, x1, y):
        plate('Driver switch white label strip', f, rect(x0, x1, y-.0035, y+.0035), .0045, .003, white, last=1)

    # ---------------- lower dash and dash top ----------------
    def x_section_solid(name, stations, corners_at, radii, mat, part=11):
        rings = [fillet_ring(corners_at(x), radii) for x in stations]
        return sharpen(closed_loft(name, rings, mat, 'dashboard', part))

    def knee_corners(y_bot):
        def corners(x):
            zf = front_limit(x, y_bot+.01, 1.372, .012)
            if zf < 8.50:
                raise RuntimeError('Lower dash has no room in front of the knee panel at x=%.3f' % x)
            return [(x, y_bot, 8.455), (x, 1.372, 8.425), (x, 1.372, zf), (x, y_bot, zf)]
        return corners
    x_section_solid('Dashboard lower knee panel driver side', (-1.150, -1.100, -1.000, -.880, -.660, -.450, -.370),
                    knee_corners(.541), (.006, .010, .006, .006), dash)
    # Continues across the entrance to x=+0.52: the open front door leaf sweeps x>=0.61 (z 8.35-8.58).
    x_section_solid('Dashboard lower knee panel aisle side', (-.390, -.300, -.200, -.085, .100, .300, .520),
                    knee_corners(contract['low_floor_y']+.0005), (.006, .010, .006, .006), dash)

    def deck_corners(x):
        zf = front_limit(x, 1.36, 1.49, .018)
        if zf < 8.70:
            raise RuntimeError('Dash top cannot reach the windshield base at x=%.3f' % x)
        return [(x, 1.49, 8.54), (x, 1.49, zf), (x, 1.36, zf), (x, 1.36, 8.54)]
    x_section_solid('Dashboard continuous top deck', (-1.150, -1.120, -1.090, -1.050, -1.000, -.950, -.880,
                    -.800, -.700, -.550, -.350, -.200, -.085), deck_corners, (.012, .010, .006, .006), face)

    # Entrance side: one flat-topped cabinet, its top flush with the lower panel's rear face.
    def entrance_deck_corners(x):
        return [(x, 1.49, 8.420)]+deck_corners(x)[1:3]+[(x, 1.36, 8.420)]
    x_section_solid('Dashboard entrance side top deck', (-.100, .100, .300, .520), entrance_deck_corners,
                    (.024, .010, .006, .006), face)
    for vx, vw in ((driver_x, .30), (-.24, .20)):
        vents, _, _, _ = frame((vx, 1.490, front_limit(vx, 1.36, 1.49, .018)-.055), tilt=90)
        plate('Driver demister vent recessed bed', vents, rounded(vw, .046, .012), .002, .006, rubber)
        for y in (-.012, 0, .012):
            line('Driver demister vent louver', vents, [(-vw/2+.014, y), (vw/2-.014, y)], .0032, dash, last=2, depth=.0025)

    # ---------------- arched instrument hood ----------------
    centre, ch, cv, cn = frame((driver_x, 1.535, 8.425), tilt=10)
    outer, inner = arched(.560, .335, .060, .022), arched(.505, .284, .045, .018)
    sharpen(rim('Instrument hood continuous recessed enclosure', centre, outer, inner, .035, -.135, dash))
    plate('Instrument gloss dial fascia', centre, inner, -.097, .018, gloss)
    rim('Instrument inset lens gasket', centre, arched(.499, .278, .042, .017),
        arched(.483, .262, .036, .016), -.074, -.101, rubber, 'instrument')
    for sx in (-1, 1):
        for sy in (-1, 1):
            circle('Instrument fascia screw head', centre, sx*.226, sy*.112, .0042, -.0945, steel, last=1, thickness=.004)

    needle_records = []
    for name, x, part, top, step, minor in (('Speed', .150, 2, 140, 20, 4), ('Rpm', -.150, 3, 80, 10, 4)):
        y, radius, depth = -.027, .068, -.070
        circle(name+' gauge integral socket', centre, x, y, radius+.007, depth, rubber, thickness=.038)
        circle(name+' gauge printed face', centre, x, y, radius, depth+.003, dial_black)
        majors = top//step; divisions = majors*minor
        for j in range(divisions+1):
            angle = math.radians(-124+j*248/divisions)
            r0 = radius-.005; r1 = r0-(.010 if j % minor == 0 else .005)
            line(name+' gauge printed tick', centre, [(x+r0*math.sin(angle), y+r0*math.cos(angle)),
                 (x+r1*math.sin(angle), y+r1*math.cos(angle))], .0008, white, depth=depth+.0038)
        for j in range(majors+1):
            angle = math.radians(-124+j*248/majors)
            numeral(name, str(j*step), centre, x+.046*math.sin(angle), y+.046*math.cos(angle), depth+.0033, .80)
        # Printed colour arc: green/amber/red on the motor-speed dial, red end band on km/h.
        segments = 24
        for j in range(segments):
            t0, t1 = j/segments, (j+1)/segments
            if name == 'Rpm':
                mat = green if t1 <= .60 else amber if t1 <= .80 else red
            elif t0 >= .85:
                mat = red
            else:
                continue
            a0, a1 = math.radians(-124+t0*248), math.radians(-124+t1*248)
            rr = radius-.0022
            line(name+' gauge colour arc', centre, [(x+rr*math.sin(a0), y+rr*math.cos(a0)),
                 (x+rr*math.sin(a1), y+rr*math.cos(a1))], .0014, mat, depth=depth+.0042)
        p = Vector(centre(x, y, depth+.015))
        pivot = ctx['empty'](name+' needle pivot', p, parent, part)
        basis = Matrix((tuple(-ch), tuple(cv), tuple(cn))).transposed()
        q = basis.to_quaternion()
        pivot['rest_rotation_unity'] = [q.x, q.y, q.z, q.w]
        plate(name+' needle', centre, [(x-.0024, y-.014), (x+.0024, y-.014),
              (x+.0014, y+.058), (x-.0014, y+.058)], depth+.018, .0025, red,
              'instrument_needle', part, pivot)
        circle(name+' needle spindle', centre, x, y, .008, depth+.021, dash, 'instrument_needle', part, pivot, thickness=.020)
        axis = (U.to_3x3() @ cn).normalized()
        pivot.rotation_mode = 'QUATERNION'
        for frame_number, degrees in ((1, 0), (130, 0), (160, 65), (190, -65), (220, 0), (310, 0)):
            pivot.rotation_quaternion = Quaternion(axis, math.radians(degrees))
            pivot.keyframe_insert('rotation_quaternion', frame=frame_number)
        pivot.rotation_quaternion = Quaternion()
        needle_records.append({'name': pivot.name, 'part': part, 'pivot_unity': list(p),
                               'normal_unity': list(cn), 'rest_rotation_unity': list(pivot['rest_rotation_unity'])})
    for x in (-.031, .031):
        circle('Instrument upper small gauge socket', centre, x, .090, .025, -.069, rubber, thickness=.038)
        circle('Instrument upper small gauge face', centre, x, .090, .021, -.065, dial_black)
        line('Upper small gauge needle', centre, [(x, .090), (x-.010, .104)], .0013, red, depth=-.064)
    for x, mat in ((-.066, green), (-.084, red), (-.102, green), (.066, green), (.084, amber), (.102, green)):
        circle('Instrument status telltale', centre, x, .094, .0045, -.059, mat, last=1, thickness=.040)
    lcd = [(x, y-.008) for x, y in rounded(.170, .112, .006)]
    plate('Central status display', centre, lcd, -.058, .047, screen)
    # Static schematic of the photographed LCD: green bus side view, red battery stacks,
    # coloured top status icons and white information lines (no live factory electronics).
    line('LCD bus silhouette', centre, [(-.050, -.016), (-.050, .014), (.036, .014), (.050, -.001),
         (.050, -.016), (-.050, -.016)], .0012, green, depth=-.0565)
    for x in (-.032, .032):
        circle('LCD axle symbol', centre, x, -.016, .0055, -.0559, green, last=1, thickness=.0021)
    for x in (-.069, .069):
        for j in range(5):
            plate('LCD battery stack cell', centre, rect(x-.006, x+.006, -.034+j*.011, -.026+j*.011),
                  -.0565, .0015, red if j < 3 else white, last=1)
    for j, mat in enumerate((green, red, amber, green, green)):
        circle('LCD status icon', centre, -.040+j*.020, .036, .0035, -.0562, mat, last=1, thickness=.0018)
    for y, width in ((-.036, .09), (-.046, .07), (-.056, .09)):
        line('LCD status information band', centre, [(-width/2, y), (width/2, y)], .0010, white, depth=-.0567)
    for j in range(6):
        circle('Instrument menu button bezel', centre, -.0475+j*.019, -.088, .0072, -.0935, rubber, last=2, thickness=.006)
        circle('Instrument menu button', centre, -.0475+j*.019, -.088, .0052, -.0905, dash, last=1, thickness=.004)
    # Driver-monitoring camera on the hood crown (low enough to clear the forward road rays).
    hood_top = .1675+.022
    plate('Driver camera mounting foot', centre, rect(-.022, .022, hood_top-.008, hood_top+.008), -.028, .048, dash)
    tag(rod('Driver camera ball post', centre(0, hood_top+.004, -.052), centre(0, hood_top+.030, -.052), .008, dash, 11, n=12), 'instrument')
    def camera_frame(x, y, d=0):
        return centre(x, y+hood_top+.042, d)
    pillow('Driver camera head', camera_frame, .050, .032, .010, -.020, .060, dash, 'instrument')
    circle('Driver camera lens', centre, 0, hood_top+.042, .0085, -.017, screen, last=1, thickness=.004)

    # ---------------- switch helpers ----------------
    def switch(f, x, y, w=.034, h=.038, mat=dash, last=1, icon=None):
        plate('Driver switch recessed socket', f, rect(x-w/2-.002, x+w/2+.002, y-h/2-.002, y+h/2+.002), .014, .018, rubber, last=last)
        points = [(x+u, y+v) for u, v in rounded(w, h, .004, 3)]
        plate('Driver switch rocker', f, points, .019, .011, mat, last=last)
        icons = {
            'door': [[(-1, -1), (-1, 1), (1, 1), (1, -1), (-1, -1)], [(0, -1), (0, 1)]],
            'light': [[(-1, -.7), (-1, .7), (-.5, .9), (0, .65), (0, -.65), (-.5, -.9), (-1, -.7)],
                      [(.3, -.65), (1, -.65)], [(.3, 0), (1, 0)], [(.3, .65), (1, .65)]],
            'hazard': [[(-1, -.85), (0, 1), (1, -.85), (-1, -.85)], [(0, .45), (0, -.25)]],
            'fan': [[(-.1, 0), (-.9, .3), (-.8, .7), (-.4, .95), (-.1, 0)],
                    [(0, .1), (.3, .9), (.7, .8), (.95, .4), (0, .1)],
                    [(.1, 0), (.9, -.3), (.8, -.7), (.4, -.95), (.1, 0)],
                    [(0, -.1), (-.3, -.9), (-.7, -.8), (-.95, -.4), (0, -.1)]],
            'power': [[(-.45, .65), (-.8, .2), (-.75, -.45), (-.3, -.9), (.3, -.9), (.75, -.45), (.8, .2), (.45, .65)],
                      [(0, .2), (0, 1)]],
            'bar': [[(-.7, 0), (.7, 0)]]}
        if icon:
            colour = white if mat is red else green
            for shape in icons[icon]:
                line('Driver switch '+icon+' pictogram', f, [(x+px*w*.22, y+py*h*.22) for px, py in shape],
                     .00065, colour, depth=.0195, last=0)

    def tower(name, xa, xb, slope, profile, z_back):
        xc = (xa+xb)/2; rings, fronts = [], []
        for y, zc in profile:
            za, zb = zc+slope*(xa-xc), zc+slope*(xb-xc)
            rings.append(fillet_ring([(xa, y, za), (xb, y, zb), (xb, y, z_back), (xa, y, z_back)], (.020, .020, .010, .010)))
            fronts.append((Vector((xa, y, za)), Vector((xb, y, zb))))
        sharpen(closed_loft(name, rings, dash, 'dashboard'))
        return fronts

    def band(fronts, i, j):
        (a0, b0), (a1, b1) = fronts[i], fronts[j]
        h = (b0-a0).normalized(); rise = a1-a0
        v = (rise-h*rise.dot(h)).normalized(); n = v.cross(h)
        if n.z > 0:
            raise RuntimeError('Cockpit control face points away from the driver')
        mid = (a0+b0+a1+b1)/4

        def at(x, y, d=0):
            return tuple(mid+h*x+v*y+n*d)
        return at, (b0-a0).length-.040, rise.dot(v)

    def fascia(label_, f, w, h):
        plate('Dashboard '+label_+' inset switch fascia', f, rounded(w-.010, h-.012, .010), .003, .009, rubber, 'dashboard')

    # ---------------- window-side tower ----------------
    # Wall inner faces: sill x=-1.170 (y 1.115-1.225), glass -1.227, lower skin -1.205.
    left_fronts = tower('Dashboard left continuous control wing', -1.150, -.930, math.tan(math.radians(22)),
                        ((.985, 8.420), (1.015, 8.190), (1.060, 8.130), (1.230, 8.270), (1.240, 8.285),
                         (1.248, 8.300), (1.505, 8.340), (1.520, 8.360)), 8.560)
    lu, lw, lh = band(left_fronts, 5, 6)
    fascia('left upper', lu, lw, lh)
    circle('Driver lighting rotary bezel', lu, -.064, .072, .026, .012, rubber)
    circle('Driver lighting rotary control', lu, -.064, .072, .019, .030, dash)
    line('Driver lighting rotary pointer', lu, [(-.064, .060), (-.064, .086)], .0035, white, depth=.031)
    for j, icon in enumerate(('power', 'bar', 'bar')):
        switch(lu, -.020+j*.034, .072, .029, .052, red, icon=icon)
    plate('Driver small black pushbutton', lu, rect(.068, .086, .062, .080), .016, .016, dash, last=1)
    for j in range(4):
        switch(lu, -.060+j*.040, .002, icon=('light', 'light', 'fan', 'door')[j])
    label(lu, -.080, .080, -.024)
    switch(lu, -.060, -.064, mat=red, icon='hazard')
    for j in range(3):
        switch(lu, -.020+j*.040, -.064, icon=('light', 'door', 'fan')[j])
    label(lu, -.080, .080, -.090)
    ll, lw2, lh2 = band(left_fronts, 2, 3)
    fascia('left lower', ll, lw2, lh2)
    for row, y in enumerate((.050, -.030)):
        for j in range(4):
            switch(ll, -.060+j*.040, y, icon=(('door', 'door', 'light', 'fan'), ('light', 'fan', 'fan', 'door'))[row][j])
        label(ll, -.080, .080, y-.026)

    # ---------------- aisle-side tower ----------------
    right_fronts = tower('Dashboard right continuous control wing', -.390, -.085, -math.tan(math.radians(24)),
                         ((.960, 8.420), (.990, 8.190), (1.020, 8.145), (1.190, 8.285), (1.200, 8.300),
                          (1.208, 8.315), (1.485, 8.358), (1.500, 8.378)), 8.560)
    ru, rw, rh = band(right_fronts, 5, 6)
    fascia('right upper', ru, rw, rh)
    for row, y in enumerate((.040, -.040)):
        for j in range(6):
            switch(ru, -.128+j*.040, y, icon=(('door', 'door', 'light', 'fan', 'fan', 'hazard'),
                                             ('light', 'light', 'fan', 'fan', 'door', 'door'))[row][j])
        label(ru, -.148, .092, y-.026)
    glyphs = {'D': [(-.5, -.7), (-.5, .7), (.2, .7), (.6, .3), (.6, -.3), (.2, -.7), (-.5, -.7)],
              'N': [(-.5, -.7), (-.5, .7), (.5, -.7), (.5, .7)],
              'R': [(-.5, -.7), (-.5, .7), (.3, .7), (.55, .4), (.3, .05), (-.5, .05), (.55, -.7)]}
    for letter, y, cap in (('D', .055, white), ('N', .010, amber), ('R', -.035, white)):
        circle('Driver range button bezel '+letter, ru, .120, y, .019, .016, rubber)
        circle('Driver range button cap '+letter, ru, .120, y, .0135, .022, cap)
        line('Driver range button letter '+letter, ru, [(.120+gx*.0075, y+gy*.0075) for gx, gy in glyphs[letter]],
             .0007, dash, depth=.0232, last=0)
    rl, rw2, rh2 = band(right_fronts, 2, 3)
    fascia('right lower', rl, rw2, rh2)
    for j in range(5):
        switch(rl, -.090+j*.040, .048, icon=('door', 'light', 'fan', 'fan', 'hazard')[j])
    label(rl, -.110, .090, .022)
    for x in (-.060, 0, .060):
        circle('Driver console red pushbutton bezel', rl, x, -.042, .022, .016, rubber)
        circle('Driver console red pushbutton', rl, x, -.042, .016, .024, red)
    tag(box('Dashboard right pedestal column', (-.240, (contract['low_floor_y']+.985)/2, 8.430),
            (.180, .985-contract['low_floor_y'], .260), dash, 11, bevel=.018), 'dashboard')
    grille, _, _, _ = frame((-.240, .640, 8.300))
    plate('Dashboard pedestal grille bed', grille, rounded(.120, .200, .012), .002, .006, rubber)
    for j in range(8):
        line('Dashboard pedestal grille louver', grille, [(-.048, -.080+j*.023), (.048, -.080+j*.023)], .0028, dash, last=1, depth=.003)

    # ---------------- hooded monitors on the tower tops ----------------
    monitor_records = []
    # (along-face shift keeps each housing >= 8 mm clear of the hood's side walls)
    for side, fronts, z_back, yaw, tilt, w, h, shift in (('left', left_fronts, 8.560, 22, 18, .176, .124, -.014),
                                                         ('right', right_fronts, 8.560, -24, 22, .255, .168, .010)):
        a, b = fronts[-1]
        top_y = a.y
        mid = Vector(((a.x+b.x)/2, top_y, ((a.z+b.z)/2+z_back)/2))
        ty = math.radians(yaw); tt = math.radians(tilt)
        n_h = Vector((math.sin(ty), 0, -math.cos(ty)))
        origin = mid+n_h*.012+Vector((math.cos(ty), 0, math.sin(ty)))*shift
        origin.y = top_y+h/2*math.cos(tt)+.074*math.sin(tt)-.006
        mf, mh, mv, mn = frame(tuple(origin), yaw, tilt)
        bezel_in = [(x, y+.008) for x, y in rounded(w-.024, h-.044, .006)]
        sharpen(rim('Driver '+side+' monitor continuous frame', mf, rounded(w, h, .014), bezel_in, .014, -.050, dash, 'instrument'))
        pillow('Driver '+side+' monitor enclosed back', mf, w-.004, h-.004, .014, -.044, .030, dash, 'instrument')
        plate('Driver '+side+' monitor screen', mf, [(x, y+.008) for x, y in rounded(w-.028, h-.048, .005)], .003, .006, screen)
        plate('Driver '+side+' monitor sunshade top', mf, rect(-w/2, w/2, h/2-.012, h/2-.002), .054, .042, dash, 'instrument')
        for s in (-1, 1):
            plate('Driver '+side+' monitor sunshade side', mf, rect(min(s*(w/2-.001), s*(w/2-.011)), max(s*(w/2-.001), s*(w/2-.011)),
                  -h/2+.040, h/2-.006), .046, .034, dash, 'instrument')
        for j in range(6):
            circle('Driver '+side+' monitor lower key', mf, -w*.30+j*w*.09, -h/2+.015, .0032, .0155, white, last=1, thickness=.002)
        circle('Driver '+side+' monitor power key', mf, w*.33, -h/2+.015, .0036, .0155, red, last=1, thickness=.002)
        # Cradle: fills the wedge between the reclined housing and the flat tower top (contacts both).
        front_y = (Vector(mf(0, -h/2, .014))).y+.004
        cradle = []
        for y in (top_y-.010, front_y):
            ring = []
            for sx, d in ((-1, .006), (1, .006), (1, -.062), (-1, -.062)):
                p = Vector(mf(sx*w*.42, -h/2, d)); ring.append((p.x, y, p.z))
            cradle.append(ring)
        closed_loft('Driver '+side+' monitor integrated neck', cradle, dash, 'instrument')
        monitor_records.append({'side': side, 'centre_unity': list(Vector(mf(0, .008, 0))), 'normal_unity': list(mn),
                                'horizontal_unity': list(mh), 'vertical_unity': list(mv),
                                'face_size_m': [w, h], 'fixed_supports': 1, 'sunshade_depth_m': .040,
                                'screen_state': 'unpowered black; no fabricated live feed'})

    # ---------------- window-side console beside the seat ----------------
    # Sloped top (keypad unit and lever as in the official seat-area photograph), clear of the
    # sill (bottom y 1.115) and of the lower skin (x -1.205).
    def console_ring(z, inset):
        return fillet_ring([(-1.168+inset, .541, z), (-1.030-inset, .541, z), (-1.030-inset, 1.068-inset, z),
                            (-1.168+inset, 1.103-inset, z)], (.006, .006, .018, .010))
    sharpen(closed_loft('Driver side controls molded pod', [console_ring(7.400, .010), console_ring(7.420, 0.),
                        console_ring(8.020, 0.), console_ring(8.040, .010)], dash, 'dashboard'))
    cv_up = Vector((-1.168+1.030, 1.103-1.068, 0)).normalized()
    con_h = Vector((0, 0, 1)); con_n = cv_up.cross(con_h); con_c = Vector(((-1.168-1.030)/2, (1.103+1.068)/2, 7.720))

    def con(x, y, d=0):
        return tuple(con_c+con_h*x+cv_up*y+con_n*d)
    pillow('Driver side keypad unit', con, .110, .074, .010, .024, .028, dash, 'dashboard')
    plate('Driver side keypad display', con, [(x-.020, y+.018) for x, y in rounded(.050, .022, .003)], .0255, .003, screen, last=1)
    for i in range(3):
        for j in range(3):
            circle('Driver side keypad key', con, -.040+i*.017, -.004-j*.012, .0042, .0262, face, last=1, thickness=.003)
    circle('Driver side keypad red key', con, .032, .014, .0055, .0262, red, last=1, thickness=.003)
    circle('Driver side keypad blue key', con, .032, -.010, .0055, .0262, blue, last=1, thickness=.003)

    def lever_frame(x, y, d=0):
        return con(x+.120, y, d)
    pillow('Driver side lever base', lever_frame, .062, .058, .008, .010, .014, dash, 'dashboard')
    boot = []
    for k, (s, d) in enumerate(((.026, .006), (.021, .018), (.017, .030), (.013, .040))):
        boot.append([con(.120+px, py, d) for px, py in rounded(s*2, s*2, s*.8, 3)])
    closed_loft('Driver side lever boot', boot, rubber, 'dashboard', smooth=True)
    tag(rod('Driver side lever shaft', con(.120, 0, .030), con(.140, 0, .105), .0065, steel, 11, n=10), 'dashboard')
    knob_c = Vector(con(.142, 0, .112))
    knob = []
    for k in range(7):
        a = math.pi*(k+.5)/7
        r = .016*math.sin(a); dz = .016*math.cos(a)
        knob.append([tuple(knob_c+con_n*dz+con_h*(r*math.cos(t))+cv_up*(r*math.sin(t)))
                     for t in [2*math.pi*i/12 for i in range(12)]])
    closed_loft('Driver side lever knob', knob, dash, 'dashboard', smooth=True)
    for j in range(4):
        switch(con, .205+j*.026, 0, .022, .040, icon=('door', 'door', 'light', 'fan')[j])

    # ---------------- handheld microphone with coiled cord ----------------
    mic_c = Vector((-1.179, 1.010, 8.150))
    tag(box('Driver microphone wall clip', (-1.1992, 1.030, 8.150), (.0145, .060, .030), dash, 11, bevel=.003), 'mic')
    mic_rings = []
    for x, s in ((-1.194, .92), (-1.190, 1.), (-1.168, 1.), (-1.164, .92)):
        mic_rings.append([(x, mic_c.y+py*s, mic_c.z+px*s) for px, py in rounded(.060, .100, .024, 5)])
    closed_loft('Driver handheld microphone body', mic_rings, rubber, 'mic', smooth=True)
    mic_face, _, _, _ = frame((-1.164, mic_c.y, mic_c.z), yaw=90)
    circle('Driver microphone red key', mic_face, 0, .022, .0068, .002, red, 'mic', last=1, thickness=.003)
    circle('Driver microphone blue key', mic_face, 0, -.004, .0068, .002, blue, 'mic', last=1, thickness=.003)
    path = [Vector((-1.179, .975, 8.150)), Vector((-1.168, .900, 8.190)), Vector((-1.150, .870, 8.270)),
            Vector((-1.130, .930, 8.350)), Vector((-1.120, .995, 8.430))]

    def catmull(points, samples):
        out = []
        pts = [points[0]]+points+[points[-1]]
        for i in range(1, len(pts)-2):
            p0, p1, p2, p3 = pts[i-1], pts[i], pts[i+1], pts[i+2]
            for k in range(samples):
                t = k/samples
                out.append(.5*((2*p1)+(-p0+p2)*t+(2*p0-5*p1+4*p2-p3)*t*t+(-p0+3*p1-3*p2+p3)*t**3))
        out.append(points[-1])
        return out
    centre_line = catmull(path, 40)

    def transport(line_pts):
        frames = []
        t0 = (line_pts[1]-line_pts[0]).normalized()
        n0 = t0.cross(Vector((0, 1, 0)))
        if n0.length < 1e-4:
            n0 = t0.cross(Vector((1, 0, 0)))
        n0.normalize()
        for i, p in enumerate(line_pts):
            t = (line_pts[min(i+1, len(line_pts)-1)]-line_pts[max(i-1, 0)]).normalized()
            n0 = (n0-t*n0.dot(t)).normalized()
            frames.append((p, t, n0, t.cross(n0)))
        return frames
    length = sum((b-a).length for a, b in zip(centre_line, centre_line[1:]))
    helix, travelled = [], 0.
    frames = transport(centre_line)
    for i, (p, t, n, bn) in enumerate(frames):
        if i:
            travelled += (centre_line[i]-centre_line[i-1]).length
        # 8 samples per turn along the coil: subdivide each centre-line step accordingly.
        helix.append((travelled, p, n, bn))
    pitch, coil_r, wire_r = .0075, .0085, .0022
    turns = length/pitch; count = int(turns*8)
    coil_pts = []
    for k in range(count+1):
        s = length*k/count
        i = max(j for j in range(len(helix)) if helix[j][0] <= s+1e-9)
        i = min(i, len(helix)-2)
        s0, p0, n0, b0 = helix[i]; s1, p1, n1, b1 = helix[i+1]
        u = 0 if s1 == s0 else (s-s0)/(s1-s0)
        p = p0.lerp(p1, u); n = n0.lerp(n1, u).normalized(); bn = b0.lerp(b1, u).normalized()
        phi = 2*math.pi*s/pitch
        coil_pts.append(p+coil_r*(n*math.cos(phi)+bn*math.sin(phi)))
    wire_rings = [[tuple(p+wire_r*(n*math.cos(2*math.pi*j/5)+bn*math.sin(2*math.pi*j/5))) for j in range(5)]
                  for p, t, n, bn in transport(coil_pts)]
    closed_loft('Driver microphone coiled cord', wire_rings, rubber, 'mic', last=0, smooth=True)
    plain = [[tuple(p+.0095*(n*math.cos(2*math.pi*j/6)+bn*math.sin(2*math.pi*j/6))) for j in range(6)]
             for p, t, n, bn in transport(catmull(path, 6))]
    cord_far = closed_loft('Driver microphone cord far envelope', plain, rubber, 'mic', smooth=True)
    cord_far['first_lod'] = 1

    # Three-spoke wheel with the large horn pad occupying the upper/middle
    # opening. Its asymmetric lower outline follows the photographed wheel.
    wc = Vector(old_cockpit['steering_pivot_unity'])
    wc.x += driver_shift
    wn = Vector(old_cockpit['steering_axis_towards_driver_unity']).normalized()
    wh = Vector((1,0,0)); wv = wh.cross(wn).normalized()
    def wheel_at(x,y,d=0): return tuple(wc+wh*x+wv*y+wn*d)
    steer = ctx['empty']('Steering wheel pivot',wc,parent,1)
    q = Matrix((tuple(-wh),tuple(wv),tuple(wn))).transposed().to_quaternion()
    steer['rest_rotation_unity'] = [q.x,q.y,q.z,q.w]
    # Factory wheel (official photos): thick leather rim, horizontal 3/9 o'clock spokes set a
    # little below centre, one broad 6 o'clock spoke, a large shield-shaped horn pad and a
    # chrome U strip along the pad's lower edge only; oval CRRC badge in the upper pad.
    radial, tube, pts, faces = 72, 12, [], []
    for k in range(tube):
        a = k*2*math.pi/tube
        for j in range(radial):
            t = j*2*math.pi/radial
            r = .226+.024*math.cos(a)
            pts.append(wheel_at(r*math.cos(t), r*math.sin(t), .021*math.sin(a)))
    for k in range(tube):
        for j in range(radial):
            faces.append((k*radial+j, k*radial+(j+1) % radial, ((k+1) % tube)*radial+(j+1) % radial, ((k+1) % tube)*radial+j))
    rim_ob = tag(mesh('Steering rim continuous molded grip', pts, faces, rubber, 1, steer), 'steering')
    for p in rim_ob.data.polygons:
        p.use_smooth = True
    # Distant tier: the same rim torus at 24 x 6 sections (seen through the windshield at LOD2).
    radial, tube, pts, faces = 24, 6, [], []
    for k in range(tube):
        a = k*2*math.pi/tube
        for j in range(radial):
            t = j*2*math.pi/radial
            r = .226+.024*math.cos(a)
            pts.append(wheel_at(r*math.cos(t), r*math.sin(t), .021*math.sin(a)))
    for k in range(tube):
        for j in range(radial):
            faces.append((k*radial+j, k*radial+(j+1) % radial, ((k+1) % tube)*radial+(j+1) % radial, ((k+1) % tube)*radial+j))
    far_rim = tag(mesh('LOD2 Steering rim continuous molded grip', pts, faces, rubber, 1, steer), 'steering')
    far_rim['first_lod'] = far_rim['last_lod'] = 2
    for p in far_rim.data.polygons:
        p.use_smooth = True
    for shape in ([(-.126, .004), (-.207, -.006), (-.207, -.064), (-.120, -.062)],
                  [(.126, .004), (.207, -.006), (.207, -.064), (.120, -.062)],
                  [(-.042, -.106), (.042, -.106), (.036, -.207), (-.036, -.207)]):
        plate('Steering molded spoke', wheel_at, shape, .010, .038, dash, 'steering', 1, steer)
    curves = [((-.116, .066), (-.058, .088), (.058, .088), (.116, .066)),
              ((.116, .066), (.137, .057), (.140, .020), (.130, -.010)),
              ((.130, -.010), (.116, -.074), (.058, -.120), (0, -.126)),
              ((0, -.126), (-.058, -.120), (-.116, -.074), (-.130, -.010)),
              ((-.130, -.010), (-.140, .020), (-.137, .057), (-.116, .066))]

    def bezier(curve, samples):
        a, b, c, d = map(Vector, curve)
        return [tuple((1-t)**3*a+3*(1-t)**2*t*b+3*(1-t)*t*t*c+t**3*d) for t in (j/samples for j in range(samples))]
    pad = [p for curve in curves for p in bezier(curve, 12)]
    sharpen(closed_loft('Steering horn module rear housing',
                        [[wheel_at(x*s, y*s, d) for x, y in pad] for s, d in ((.80, -.030), (.88, -.018), (.92, .008))],
                        dash, 'steering', 1, steer))
    closed_loft('Steering large padded horn centre',
                [[wheel_at(x*s, y*s, d) for x, y in pad] for s, d in ((1., .006), (1., .030), (.965, .046), (.90, .054))],
                rubber, 'steering', 1, steer, smooth=True)
    # Chrome strip swept along the lower pad edge (shoulder to shoulder), 2 mm into the pad.
    lower = bezier(curves[2], 16)+bezier(curves[3], 16)+[curves[3][3]]
    strip = []
    for i, p in enumerate(lower):
        a = Vector(lower[max(i-1, 0)]); b = Vector(lower[min(i+1, len(lower)-1)])
        tangent = (b-a).normalized(); out = Vector((tangent.y, -tangent.x))
        if out.dot(Vector(p)) < 0:
            out = -out
        p = Vector(p)
        strip.append([wheel_at(*(p+out*o), d) for o, d in ((-.002, .016), (.008, .016), (.008, .034), (-.002, .034))])
    sharpen(closed_loft('Steering horn pad chrome lower strip', strip, silver, 'steering', 1, steer))
    oval = [(.036*math.cos(2*math.pi*i/40), .030+.025*math.sin(2*math.pi*i/40)) for i in range(40)]
    plate('Steering emblem chrome oval', wheel_at, oval, .0570, .0045, silver, 'steering', 1, steer)
    plate('Steering emblem dark oval', wheel_at, [(x*.86, .030+(y-.030)*.82) for x, y in oval], .0582, .0018, dash, 'steering', 1, steer)
    # Reuse the checked manufacturer vector contours, not a substitute font.
    badge = runpy.run_path(str(root/'Tools/CRRC_K18/badges.py'), init_globals={'math': math, 'Vector': Vector})
    for label_, contour in badge['k18_badge_contours'](4).items():
        triangles = badge['_k18_badge_triangles'](contour)
        flat = [((x-146)*.032/236, .030+(148-y)*.032/236) for x, y in contour]
        n = len(flat); vv = [wheel_at(x, y, d) for d in (.0578, .0594) for x, y in flat]
        ff = [tuple(reversed(t)) for t in triangles]+[tuple(i+n for i in t) for t in triangles]
        ff += [(i, (i+1) % n, (i+1) % n+n, i+n) for i in range(n)]
        tag(mesh('Steering traced CRRC emblem '+label_, vv, ff, white, 1, steer), 'steering', 1)
    axis=(U.to_3x3()@wn).normalized();steer.rotation_mode='QUATERNION'
    for frame_number,degrees in ((1,0),(130,0),(160,100),(190,-100),(220,0),(310,0)):
        steer.rotation_quaternion=Quaternion(axis,math.radians(degrees));steer.keyframe_insert('rotation_quaternion',frame=frame_number)
    steer.rotation_quaternion=Quaternion()
    tag(rod('Steering column structural shaft',(driver_x,.547,8.395),wc-wn*.030,.031,steel,11,n=18),'steering')
    tag(rod('Steering column tapered upper sleeve',wc-wn*.250,wc-wn*.028,.055,dash,11,n=24),'steering')
    tag(box('Steering column floor bearing plate',(driver_x,.546,8.395),(.14,.012,.105),steel,11),'steering_mount')
    for sign in (-1,1):
        start=wc-wn*.122+wh*(sign*.035)
        end=wc-wn*.057+wh*(sign*.172)+wv*.010
        tag(rod('Steering column '+('left' if sign<0 else 'right')+' stalk',start,end,.009,dash,11,n=12),'steering')
        tag(rod('Steering stalk molded grip',end-wh*(sign*.038),end+wh*(sign*.019),.014,rubber,11,n=12),'steering')
    # Moulded upper shroud closes over the column sleeve and stalk roots (the stalks leave through
    # its sides), then a pleated boot carries on to the floor plate along the shaft direction.
    shroud = []
    for s,w,h in ((.045,.150,.112),(.110,.160,.120),(.200,.152,.114),(.300,.140,.106)):
        c0 = wc-wn*s
        shroud.append([tuple(c0+wh*px+wv*py) for px,py in rounded(w,h,.040,4)])
    closed_loft('Steering column molded shroud',shroud,dash,'steering',11,None,2,True)
    boot_a, boot_b = wc-wn*.300, Vector((driver_x,.560,8.395))
    boot_dir = (boot_b-boot_a).normalized()
    boot_v = boot_dir.cross(wh).normalized()
    boot = []
    for k in range(9):
        c0 = boot_a+(boot_b-boot_a)*(.05+.90*k/8)
        flare = 1.+.35*k/8
        s = (1. if k % 2 == 0 else .84)*flare
        boot.append([tuple(c0+wh*px*s+boot_v*py*s) for px,py in rounded(.130,.096,.036,4)])
    closed_loft('Steering column pleated floor boot',boot,rubber,'steering',11,None,2,True)

    def grid_plate(name, rows, direction, thickness, mat, role, part=12):
        ny,nx=len(rows),len(rows[0]);front=[Vector(p) for row in rows for p in row];n=len(front)
        verts=[tuple(p) for p in front]+[tuple(p+Vector(direction)*thickness) for p in front];ff=[]
        for y in range(ny-1):
            for x in range(nx-1):
                a=y*nx+x;b=a+1;c=b+nx;d=a+nx
                ff += [(a,b,c),(a,c,d),(n+d,n+c,n+b),(n+d,n+b,n+a)]
        edge=list(range(nx))+[j*nx+nx-1 for j in range(1,ny)]+list(range(n-2,n-nx-1,-1))+[j*nx for j in range(ny-2,0,-1)]
        ff += [(a,b,n+b,n+a) for a,b in zip(edge,edge[1:]+edge[:1])]
        return tag(mesh(name,verts,ff,mat,part),role)

    # Seat dimensions retain the previously checked hip/knee/foot envelope; the shape is a
    # contoured suspension seat (bolstered cushion with waterfall edge, lumbar/shoulder wings,
    # integrated headrest), lofted as closed solids instead of flat slabs.
    sx,sy,sz=old_cockpit['seat_centre_unity']
    sx += driver_shift

    def seat_ring(cx, top, w, h, r, z, dish):
        pts = []
        for px,py in rounded(w,h,r,5):
            lift = dish*(2*px/w)**2 if py > 0 else 0.
            pts.append((cx+px, top-h/2+py+lift, z))
        return pts

    rings = []
    for dz,w,h,dish in ((-.235,.440,.060,.030),(-.215,.468,.092,.030),(-.100,.470,.100,.030),
                        (.050,.462,.096,.028),(.170,.440,.082,.018),(.235,.405,.062,.008),
                        (.262,.350,.030,0.)):
        rings.append(seat_ring(sx,sy-.014*(dz+.235)/.5,w,h,min(.032,h*.42),sz+dz,dish))
    closed_loft('Driver cushion',rings,rubber,'driver_seat',12,None,2,True)

    def back_front_z(y):
        t = min(max((y-1.015)/.855,0.),1.05)
        return 7.225-.065*t-.025*math.sin(math.pi*min(t,1.))
    back_levels = ((1.000,.470,.110,.040),(1.080,.480,.115,.042),(1.180,.495,.120,.045),
                   (1.300,.505,.130,.048),(1.420,.505,.130,.050),(1.540,.495,.125,.046),
                   (1.640,.460,.115,.040),(1.720,.410,.100,.030),(1.800,.380,.100,.025),
                   (1.880,.370,.100,.020),(1.940,.310,.070,.010))
    rings = []
    for y,w,d,bolster in back_levels:
        zc = back_front_z(y)-d/2
        rings.append([(sx+px,y,zc+py+(bolster*(2*px/w)**2 if py > 0 else 0.))
                      for px,py in rounded(w,d,min(.040,d*.42),5)])
    closed_loft('Driver seat back',rings,rubber,'driver_seat',12,None,2,True)
    def back_surface(y, x_off):
        level = next((l for l in back_levels if l[0] >= y), back_levels[-1])
        return back_front_z(y)+level[3]*(2*x_off/level[1])**2
    # Quilted diamond panel with red stitching on the back centre, as in the factory seat photo.
    stitch = material('red seat stitching', (.42,.018,.014), .80, 'Red')
    for direction in (1,-1):
        for k in range(-7,8):
            run = []
            for i in range(0,15):
                xo = -.14+i*.02
                yy = 1.42+direction*(xo+k*.062)
                if 1.16 <= yy <= 1.68:
                    run.append((sx+xo,yy,back_surface(yy,xo)+.0012))
                elif run:
                    break
            for p0,p1 in zip(run,run[1:]):
                tag(rod('Driver seat quilting stitch',p0,p1,.0012,stitch,12,n=4),'driver_seat',0)
    # Sewn panel lines follow the dished surfaces.
    for sign in (-1,1):
        for a,b in zip(back_levels[1:-1],back_levels[2:-1]):
            p0 = (sx+sign*.150,a[0],back_surface(a[0],sign*.150)+.0015)
            p1 = (sx+sign*.150,b[0],back_surface(b[0],sign*.150)+.0015)
            tag(rod('Driver seat vertical stitched edge',p0,p1,.0015,stitch,12,n=6),'driver_seat',1)
        seam = [(-.15,.030),(-.05,.030),(.05,.028),(.15,.019)]
        for (a,da),(b,db) in zip(seam,seam[1:]):
            ya = sy-.014*(a+.235)/.5+da*.515+.0015
            yb = sy-.014*(b+.235)/.5+db*.515+.0015
            tag(rod('Driver cushion stitched seam',(sx+sign*.165,ya,sz+a),(sx+sign*.165,yb,sz+b),.0015,stitch,12,n=6),'driver_seat',1)
    # Recliner housings and the seat frame under the cushion.
    for sign in (-1,1):
        tag(box('Driver seat recliner housing',(sx+sign*.250,1.040,7.225),(.050,.130,.150),dash,12,bevel=.014),'driver_seat_support')
        tag(rod('Driver seat recliner pivot',(sx+sign*.222,1.040,7.225),(sx+sign*.282,1.040,7.225),.026,steel,12,n=14),'driver_seat_support')
        tag(box('Driver seat runner',(sx+sign*.105,.995,sz),(.035,.035,.40),steel,12,bevel=.007),'driver_seat_support')
    tag(box('Driver suspension pedestal',(sx,(.54+.995)/2,sz),(.315,.995-.54,.33),dash,12,bevel=.026),'driver_seat_support')
    for j in range(5):
        tag(box('Driver suspension bellows fold',(sx,.685+j*.045,sz),(.330,.018,.345),rubber,12,bevel=.007),'driver_seat_support',1)
    tag(box('Driver suspension top plate',(sx,.998,sz),(.380,.012,.420),steel,12,bevel=.006),'driver_seat_support')
    for sign in (-1,1):
        ax = sx+sign*.272
        rings = []
        for dz,w,h,dy in ((-.19,.044,.040,-.004),(-.15,.056,.050,0.),(0.,.060,.052,.004),
                          (.15,.056,.050,.004),(.20,.044,.038,0.)):
            rings.append([(ax+px,1.28+dy+py,7.43+dz) for px,py in rounded(w,h,.016,4)])
        closed_loft('Driver armrest',rings,rubber,'driver_seat',12,None,2,True)
        tag(rod('Driver armrest support',(ax,1.095,7.26),(ax,1.265,7.26),.016,steel,12),'driver_seat_support')
    # Belt ribbon has thickness, sewn edges and actual anchors at both ends.
    belt_path=[Vector((sx-.125,1.790,back_surface(1.790,-.125)+.006)),Vector((sx-.105,1.56,back_surface(1.56,-.105)+.012)),
               Vector((sx-.030,1.27,7.350)),Vector((sx+.194,1.095,7.585))]
    # Swept webbing: 3 mm thickness along the ribbon's own normal, so the twist from shoulder
    # to lap cannot fold the two sheets through each other.
    belt_rings=[]
    sweep=catmull(belt_path,10)
    for i,p in enumerate(sweep):
        tangent=(sweep[min(i+1,len(sweep)-1)]-sweep[max(i-1,0)]).normalized()
        side=tangent.cross(Vector((0,0,1))).normalized()
        normal=side.cross(tangent).normalized()
        if normal.z<0:normal=-normal
        belt_rings.append([tuple(p+side*u*.022-normal*d) for u,d in ((-1,0),(1,0),(1,.003),(-1,.003))])
    closed_loft('Driver yellow green shoulder safety belt',belt_rings,belt,'driver_seat',12)
    tag(box('Driver safety belt buckle body',(sx+.205,1.085,7.581),(.061,.040,.082),dash,12,bevel=.006),'driver_seat')
    tag(box('Driver safety belt red release',(sx+.205,1.109,7.587),(.039,.009,.041),red,12,bevel=.003),'driver_seat')
    tag(rod('Driver safety belt lower anchor',(sx+.205,.990,7.545),(sx+.205,1.071,7.581),.018,steel,12,n=10),'driver_seat_support')
    tag(rod('Driver belt upper anchor',(sx-.125,1.775,back_surface(1.775,-.125)-.030),(sx-.125,1.790,back_surface(1.790,-.125)+.004),.021,dash,12,n=12),'driver_seat')


    pedal_records=[]
    for label,x,w in (('brake',driver_x-.11,.118),('accelerator',driver_x+.14,.078)):
        pf,_,_,pn=frame((x,.627,8.205),tilt=44)
        plate('Driver pedal '+label,pf,rounded(w,.198,.014),0,.018,rubber,'driver_pedal',12)
        for j in range(7):
            line('Driver pedal transverse tread',pf,[(-w*.39,-.071+j*.023),(w*.39,-.071+j*.023)],.0024,
                 dash,12,last=1,depth=.003)
        tag(rod('Driver pedal support',(x,.547,8.268),pf(0,0,-.017),.012,steel,12,n=12),'driver_pedal')
        tag(box('Driver pedal floor hinge',(x,.550,8.268),(w*.80,.020,.054),steel,12,bevel=.005),'driver_pedal')
        pedal_records.append({'name':label,'centre_unity':[x,.627,8.205],'size_m':[w,.198],
                              'normal_unity':list(pn),'floor_y':.54})


    # Footwell dressing: ribbed rubber mat under the pedals, heel plate, and metal nosings on the
    # raised platform edge and entry step (the platform tops are 0.54 / 0.44 m; parts sit on them,
    # each embedded 1 mm so nothing floats or shares a coplanar face with the floor).
    mat_x, mat_w = driver_x-.115, .560
    # Diamond (checker) tread plate under the pedals, as in the factory photographs; each lug is a
    # short closed bar at +-45 degrees, alternating by cell, embedded 1 mm into the plate.
    tag(box('Driver footwell tread plate',(mat_x,.5425,7.99),(mat_w,.007,.72),steel,12,bevel=.002),'footwell_mat')
    for ix in range(13):
        for iz in range(17):
            cx, cz = mat_x-.240+ix*.040, 7.670+iz*.040
            d = .0105 if (ix+iz) % 2 == 0 else -.0105
            tag(rod('Driver tread plate lug',(cx-d,.5485,cz-.0105),(cx+d,.5485,cz+.0105),.0036,steel,12,n=4),'footwell_mat',0)
    tag(box('Driver platform edge nosing',(-.376,.5445,7.455),(.044,.010,.46),steel,12,bevel=.002),'footwell_mat')
    tag(box('Driver entry step nosing',(-.205,.4435,7.455),(.050,.009,.45),steel,12,bevel=.002),'footwell_mat')
    for j in range(5):
        tag(box('Driver entry step grip rib',(-.285,.4415,7.30+j*.085),(.140,.004,.012),dash,12),'footwell_mat',1)

    bpy.context.view_layer.update()
    parent['driver_eye_unity'] = eye
    eye_object=bpy.data.objects.get('driver_eye')
    if eye_object:
        eye_object.matrix_world.translation = U @ Vector(eye)
    cockpit=dict(old_cockpit)
    occupant=json.loads(json.dumps(old_cockpit['occupant_review_points']))
    for key,value in occupant.items():
        if value and isinstance(value[0],list):
            for p in value: p[0] += driver_shift
        else: value[0] += driver_shift
    cockpit.update({'eye_unity':eye,'seat_centre_unity':[sx,sy,sz],
        'steering_pivot_unity':list(wc),'steering_axis_towards_driver_unity':list(wn),
        'steering_diameter_m':.492,'instrument_pivots':needle_records,
        'monitor_faces':monitor_records,'pedals':pedal_records,
        'instruments':{'speed':{'fullScale':140,'minAngle':-124,'maxAngle':124}},
        'dial_graphic_scales':{'speed':{'minimum':0,'maximum':140,'step':20,'sweep_degrees':248},
                               'left':{'minimum':0,'maximum':80,'step':10,'sweep_degrees':248,
                                       'unit':'Unconfirmed from photo; part 3 is the existing runtime RpmNeedle binding, not proof of factory gauge semantics.'}},
        'occupant_review_points':occupant,
        'reference_images':references,'revision':'official-cockpit-refinement-3',
        'status':'PHOTO_GUIDED_GEOMETRY_BUILT_REQUIRES_ASSEMBLY_AND_DRIVER_EYE_REVIEW',
        'dimensional_evidence':'All cockpit sizes/depths/angles are photo proportions fitted to the retained source eye and occupant envelope, not manufacturer measurements.',
        'reference_limits':['Photo lenses/perspective and exact control labels are unknown.',
                            'LCD line art is static schematic information, not an implementation of live factory electronics.',
                            'The official cockpit photographs may differ from the 2024 LCD marketing description; their provenance is retained.',
                            'Switch pictograms distinguish visible control classes; exact blurred switch-by-switch assignments are not verified.',
                            'K18 speed requires instruments.speed propagation to the runtime profile; left-dial units remain unconfirmed and retain the default runtime RPM mapping.',
                            'Green/yellow-green markings use existing palette approximations; no new emission or exterior light IDs.'],
        'lod_strategy':'Closed cockpit structure and animation pivots retained for all three LODs; fine printed legends, LCD linework, stitching and tread details in LOD0/1.',
        'mirrors_body_unchanged':True,
        'driver_station_translation_from_initial_m':[.130,0,0],
        'eye_change_reason':'Whole seat, steering column/wheel, pedals and occupant move inward together to reserve a real left-monitor bay; mirror optical setup must use this updated source eye.'})
    contract['cockpit']=cockpit
    contract.setdefault('removed_objects',[]).extend(removed)
    contract.setdefault('new_objects',[]).extend(made)
    contract['status']='BUILT_REQUIRES_INDEPENDENT_ASSEMBLY_AUDIT'
    scene['k18_interior_contract']=json.dumps(contract,ensure_ascii=False)
    scene['k18_cab_refinement']=json.dumps({'revision':2,'references':references,
        'removed_objects':removed,'new_objects':made,'fixed_moving_parts':[1,2,3],
        'static_cockpit_parts':[11,12],'source_eye_unity':eye,
        'status':'PENDING_GEOMETRY_AND_VISUAL_VALIDATION'},ensure_ascii=False)
    # Distance tiers (BUS_ASSET_SPEC 3): fine switches, legends, buttons and small fittings only up
    # close (LOD0); controls, instruments, wheel and pedals in LOD1; LOD2 keeps the cockpit
    # silhouette seen through the windshield (towers, hood, deck, knee panels, seat, monitors).
    silhouette = ('Dashboard left continuous control wing', 'Dashboard right continuous control wing',
                  'Dashboard lower knee panel', 'Dashboard continuous top deck', 'Dashboard entrance side top deck',
                  'Dashboard right pedestal column', 'Instrument hood continuous recessed enclosure',
                  'Instrument gloss dial fascia', 'Driver cushion', 'Driver seat back', 'Driver suspension pedestal',
                  'Driver left monitor continuous frame', 'Driver right monitor continuous frame',
                  'Driver left monitor enclosed back', 'Driver right monitor enclosed back',
                  'Driver side controls molded pod', 'LOD2 Steering rim', 'Steering molded spoke',
                  'Steering large padded horn centre')
    bpy.context.view_layer.update()
    tiers = {0: 0, 1: 0, 2: 0}
    for name in made:
        ob = bpy.data.objects.get(name)
        if ob is None or ob.type != 'MESH':
            continue
        if name == 'Steering rim continuous molded grip':
            ob['last_lod'] = 1
        elif name.startswith('LOD2 '):
            pass
        elif max(ob.dimensions) < .06:
            ob['last_lod'] = 0
        elif not name.startswith(silhouette):
            ob['last_lod'] = min(int(ob['last_lod']), 1)
        tiers[int(ob['last_lod'])] += 1
    cockpit['lod_tiers'] = {'last_lod_counts': tiers, 'rule': 'max dimension < 60 mm -> LOD0 only; non-silhouette -> LOD0-1; silhouette list -> LOD0-2'}
    contract['cockpit']=cockpit
    scene['k18_interior_contract']=json.dumps(contract,ensure_ascii=False)
    sightlines=audit_k18_cab_sightlines(ctx)
    cockpit['driver_sightline_audit']=sightlines
    contract['cockpit']=cockpit
    scene['k18_interior_contract']=json.dumps(contract,ensure_ascii=False)
    if sightlines['failure_count']:
        print('K18_CAB_SIGHTLINE_FAILURE',json.dumps(sightlines,ensure_ascii=False),flush=True)
        raise RuntimeError('K18 cockpit has obstructed driver sightlines; see source audit')
    return cockpit


def audit_k18_cab_sightlines(ctx):
    """Actual opaque-triangle rays from the declared eye, in all three LODs.

    Transparent glass remains present in the scene, but refraction is outside
    this direct geometric visibility test and needs the later game review.
    """
    import json
    import math
    import bpy
    from mathutils import Vector
    from mathutils.bvhtree import BVHTree
    scene=ctx['scene'];U=ctx['U'];B=U.inverted()
    c=json.loads(scene['k18_interior_contract'])['cockpit']
    eye=Vector(c['eye_unity']);targets=[]
    a=math.radians(10);h=Vector((1,0,0));v=Vector((0,math.cos(a),math.sin(a)))
    n=v.cross(h);origin=Vector((eye.x,1.535,8.425))
    for label,x,allowed in (('Rpm',-.150,('Rpm gauge','Rpm needle')),
                            ('Speed',.150,('Speed gauge','Speed needle'))):
        for dx,dy in ((0,0),(-.050,0),(.050,0),(0,-.050),(0,.050),
                      (-.035,-.035),(-.035,.035),(.035,-.035),(.035,.035)):
            p=origin+h*(x+dx)+v*(-.027+dy)+n*(-.067)
            targets.append((label+' dial',p,allowed))
    for x in (-.055,0,.055):
        for y in (-.044,0,.044):
            targets.append(('central LCD',origin+h*x+v*y+n*(-.058),('Central status display','LCD ')))
    for m in c['monitor_faces']:
        base=Vector(m['centre_unity']);mh=Vector(m['horizontal_unity']);mv=Vector(m['vertical_unity']);mn=Vector(m['normal_unity'])
        w,hh=m['face_size_m']
        for x in (-.34,0,.34):
            for y in (-.34,0,.34):
                targets.append((m['side']+' monitor',base+mh*(x*(w-.034))+mv*(y*(hh-.048))+mn*.003,
                                ('Driver '+m['side']+' monitor screen',)))
    for offset in (-.75,0,.75):
        targets.append(('road forward',Vector((eye.x+offset,.05,18.)),()))
    def prop(o,key,default):
        while o is not None:
            if key in o:return o[key]
            o=o.parent
        return default
    prepared=[]
    for obj in list(bpy.data.objects):
        if obj.type!='MESH' or obj.get('cab_section')!='front':continue
        ev=obj.evaluated_get(bpy.context.evaluated_depsgraph_get());data=ev.to_mesh()
        try:
            data.calc_loop_triangles()
            points=[ev.matrix_world@p.co for p in data.vertices]
            triangles=[tuple(t.vertices) for t in data.loop_triangles
                       if not (t.material_index<len(data.materials) and data.materials[t.material_index]
                               and data.materials[t.material_index].get('glass',False))]
            if triangles:prepared.append((obj.name,int(prop(obj,'first_lod',0)),int(prop(obj,'last_lod',2)),points,triangles))
        finally:ev.to_mesh_clear()
    rows=[]
    for lod in range(3):
        points=[];triangles=[];owners=[]
        for name,first,last,vv,ff in prepared:
            if first<=lod<=last:
                start=len(points);points.extend(vv);triangles.extend(tuple(start+i for i in f) for f in ff);owners.extend([name]*len(ff))
        tree=BVHTree.FromPolygons(points,triangles,all_triangles=True)
        for label,p,allowed in targets:
            if lod==2 and allowed:
                continue  # instruments/screens are not modelled in the distant silhouette; road rays still checked
            start=U@eye;delta=(U@p)-start;hit,normal,index,distance=tree.ray_cast(start,delta.normalized(),delta.length+.002)
            owner=owners[index] if index is not None else None
            ok=(owner is None) if not allowed else owner is not None and owner.startswith(allowed)
            rows.append({'lod':lod,'target':label,'target_unity':list(p),'status':'PASS' if ok else 'FAIL',
                         'first_object':owner,'hit_unity':list(B@hit) if hit is not None else None,'distance_m':distance})
    failed=sum(r['status']=='FAIL' for r in rows)
    return {'status':'PASS_DIRECT_GEOMETRIC_VISIBILITY' if not failed else 'FAIL','failure_count':failed,
            'eye_unity':list(eye),'samples':len(rows),'evidence':rows,
            'scope':'Actual full-front-assembly opaque triangle rays; all LODs, gauge/LCD/screen interior samples plus three forward road rays. Does not certify glass refraction, HUD text legibility, or actual mirror cameras.'}
