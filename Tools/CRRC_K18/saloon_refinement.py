"""Saloon fittings from the official cabin photographs; call after build_interior_completion(ctx).

official-k18-interior.jpg / official-c18-en-1.jpg show, in the standing area: stainless aisle poles
behind the aisle seats running floor to ceiling, a black grip sleeve and a yellow stop button on
each, short transverse arms out to the longitudinal rail, a perforated A/C return grille and dome
cameras in the ceiling centre, and red emergency hammers on the window pillars. Positions follow
the modelled seat map and stop where the existing standing-area system stops (z > -4.55, before
the rear stair); they are photo-supported estimates, not a factory drawing. Unity metres.
"""


def build_k18_saloon_refinement(ctx):
    import json
    import math
    import bpy
    from mathutils import Vector

    box, rod, mesh = (ctx[n] for n in ('box', 'rod', 'mesh'))
    select, U = ctx['select_section'], ctx['U']
    mat = ctx['mat']
    metal, panel, dark, black = ctx['METAL'], ctx['PANEL'], ctx['DARK'], ctx['BLACK']
    yellow = mat('Stop request yellow push button', (.93, .62, .045), .30, 0, 'RequestYellow')
    hammer_red = mat('Emergency hammer red holder', (.62, .025, .020), .35, 0, 'RequestRed')
    grille_dark = mat('A/C return grille perforated', (.10, .11, .12), .70, 0, 'GasketGrey')
    lens = mat('Dome camera smoked cover', (.03, .035, .04), .15, 0, 'Screen')
    contract = json.loads(ctx['scene']['k18_interior_contract'])
    low, ceiling_y = contract['low_floor_y'], 2.9075
    made = []

    def tag(ob, role):
        ob['k18_saloon_refinement'] = 1
        ob['k18_completion_role'] = role
        made.append(ob.name)
        return ob

    def section_of(z):
        return 'front' if z > ctx['FRONT_JOIN'] else 'rear'

    # ---- aisle poles ----
    door_stanchions = []
    for ob in bpy.data.objects:
        if ob.type == 'MESH' and ob.name.startswith('Door bay vertical stanchion'):
            c = sum((U.inverted() @ (ob.matrix_world @ v.co) for v in ob.data.vertices), Vector())/len(ob.data.vertices)
            door_stanchions.append(c)
    poles = []
    for seat in contract['seats']:
        x, _, z = seat['cushion_unity']
        if abs(abs(x)-.49) > .03 or seat['raised'] or z < -4.55+.40:
            continue
        side = 1 if x > 0 else -1
        px, pz = side*.285, z-.400
        if any(abs(c.z-pz) < .30 and c.x*side > 0 for c in door_stanchions):
            continue
        poles.append((section_of(pz), side, px, pz))
    for section, side, px, pz in poles:
        select(section)
        tag(rod('Aisle vertical stanchion', (px, low, pz), (px, ceiling_y+.012, pz), .018, metal, 13, n=16), 'aisle_pole')
        tag(rod('Aisle stanchion floor flange', (px, low, pz), (px, low+.010, pz), .038, metal, 13, n=16, lod=1), 'aisle_pole')
        tag(rod('Aisle stanchion ceiling flange', (px, ceiling_y-.010, pz), (px, ceiling_y+.004, pz), .036, metal, 13, n=16, lod=1), 'aisle_pole')
        tag(rod('Aisle stanchion black grip sleeve', (px, 1.20, pz), (px, 1.86, pz), .0215, black, 13, n=16, lod=1), 'aisle_pole')
        # Stop button pod faces the aisle centre, clamped round the pole.
        tag(box('Stop request button', (px-side*.020, 1.47, pz), (.034, .075, .052), yellow, 13, bevel=.010, lod=1), 'stop_button')
        # Transverse arm to the longitudinal rail with clamps at both ends.
        rail_x = side*.57
        tag(rod('Aisle stanchion transverse arm', (px, 2.18, pz), (rail_x, 2.18, pz), .016, metal, 13, n=12), 'aisle_pole')
        for cx in (px, rail_x):
            tag(rod('Aisle stanchion T clamp', (cx, 2.18, pz-.032), (cx, 2.18, pz+.032), .025, metal, 13, n=12, lod=1), 'aisle_pole')

    # ---- ceiling: A/C return grilles and dome cameras ----
    for section, zs in (('front', (3.65, .95)), ('rear', (-3.35,))):
        select(section)
        for zc in zs:
            tag(box('A/C return grille frame', (0, ceiling_y-.004, zc), (.56, .012, .40), panel, 14, bevel=.004), 'ceiling_fitting')
            tag(box('A/C return grille perforated panel', (0, ceiling_y-.0115, zc), (.49, .005, .33), grille_dark, 14), 'ceiling_fitting')
            for j in range(13):
                tag(box('A/C return grille louver', (0, ceiling_y-.0145, zc-.150+j*.025), (.47, .003, .006), panel, 14, lod=0), 'ceiling_fitting')
            cz = zc-.36
            tag(rod('Dome camera base ring', (0, ceiling_y-.012, cz), (0, ceiling_y+.004, cz), .048, panel, 14, n=20, lod=1), 'ceiling_fitting')
            rings = []
            for k in range(6):
                a = math.pi/2*k/5
                r, dy = .040*math.cos(a)+.0005, .040*math.sin(a)
                rings.append([(r*math.cos(2*math.pi*i/20), ceiling_y-.011-dy, cz+r*math.sin(2*math.pi*i/20)) for i in range(20)])
            n = 20; pts = [p for ring in rings for p in ring]+[(0, ceiling_y-.011, cz), (0, ceiling_y-.0515, cz)]
            a, b = len(pts)-2, len(pts)-1
            faces = [(a, (i+1) % n, i) for i in range(n)]+[(b, 5*n+i, 5*n+(i+1) % n) for i in range(n)]
            faces += [(j*n+i, j*n+(i+1) % n, (j+1)*n+(i+1) % n, (j+1)*n+i) for j in range(5) for i in range(n)]
            dome = tag(mesh('Dome camera smoked cover', pts, faces, lens, 14, lod=1), 'ceiling_fitting')
            for poly in dome.data.polygons:
                poly.use_smooth = poly.index >= 2*n

    # ---- red emergency hammers on window pillars ----
    jambs = []
    for ob in bpy.data.objects:
        if ob.type == 'MESH' and ob.name.startswith('Cabin inner window jamb') and not ob.name.startswith('LOD'):
            vs = [U.inverted() @ (ob.matrix_world @ v.co) for v in ob.data.vertices]
            lo = Vector([min(v[i] for v in vs) for i in range(3)]); hi = Vector([max(v[i] for v in vs) for i in range(3)])
            jambs.append((ob.get('cab_section'), lo, hi))
    # Every other pillar per side, away from the driver area and the door bays.
    hammers = 0; used = {-1: 0, 1: 0}
    for section, lo, hi in sorted(jambs, key=lambda j: ((j[1].x > 0), j[1].z)):
        side = 1 if lo.x > 0 else -1
        zc = (lo.z+hi.z)/2
        if section not in ('front', 'rear') or zc > 6.1:
            continue
        if side > 0 and any(abs(zc-d) < .95 for d in ctx['DOORS']):
            continue
        used[side] += 1
        if used[side] % 2:
            continue
        face_x = lo.x if side > 0 else hi.x
        select(section)
        tag(box('Emergency hammer holder', (face_x-side*.012, 2.24, zc), (.026, .150, .040), hammer_red, 0, bevel=.006, lod=1), 'safety_fitting')
        tag(box('Emergency hammer head', (face_x-side*.030, 2.31, zc), (.022, .030, .060), hammer_red, 0, bevel=.005, lod=1), 'safety_fitting')
        hammers += 1

    # ---- wheelchair bay (door side, between the door-2 jamb z=2.80 and the window jamb z=3.895) ----
    # new-bakelong-2024-04.png: two seats folded up against the lower wall (orange back cushion,
    # dark grey seat underside facing the aisle), a rail above them, a tall orange backrest pad on
    # the door stanchion; official-c18-en-2.jpg: yellow floor field with the wheelchair glyph.
    bay = contract.get('wheelchair_bay')
    bay_made = 0
    if bay:
        select(bay['section'])
        orange, shell = ctx['PRIORITY'], dark
        floor_yellow = mat('Wheelchair bay yellow floor field', (.95, .62, .02), .70, 0, 'Yellow')
        glyph = mat('Wheelchair bay floor glyph', (.30, .03, .03), .60, 0, 'Burgundy')
        wall_x, fy = bay['x'][1], bay['floor_y']
        z0, z1 = bay['z']
        count = bay['folding_seats']
        pitch = (z1-z0-.08)/count
        for k in range(count):
            zc = z0+.04+pitch*(k+.5)
            tag(box('Folding seat wall plate', (wall_x-.010, .815, zc), (.024, .53, .36), metal, 10), 'folding_seat')
            tag(box('Folding seat back cushion', (wall_x-.045, .885, zc), (.050, .36, .38), orange, 10, bevel=.018), 'folding_seat')
            tag(box('Folding seat folded pan underside', (wall_x-.1025, .705, zc), (.055, .44, .38), shell, 10, bevel=.014), 'folding_seat')
            tag(box('Folding seat folded pan cushion edge', (wall_x-.0715, .705, zc), (.010, .40, .34), orange, 10), 'folding_seat')
            for side in (-1, 1):
                tag(box('Folding seat hinge bracket', (wall_x-.060, .505, zc+side*.172), (.110, .07, .024), metal, 10, bevel=.004), 'folding_seat')
        rail_x, rail_y = 1.195, 1.330
        for zz in (z0+.07, z1-.06):
            tag(rod('Wheelchair bay rail post', (rail_x, 1.222, zz), (rail_x, rail_y, zz), .014, metal, 13, n=12), 'wheelchair_rail')
        tag(rod('Wheelchair bay wall rail', (rail_x, rail_y, z0+.05), (rail_x, rail_y, z1-.04), .016, metal, 13, n=12), 'wheelchair_rail')
        # Backrest pad bolted to the existing door stanchion just behind the bay's rear edge.
        pole = min(door_stanchions, key=lambda c: (c.z-z0)**2+(c.x-.59)**2)
        pz = pole.z+.018
        tag(box('Wheelchair backrest pad back plate', (.80, .96, pz+.008), (.28, .68, .012), shell, 13), 'wheelchair_pad')
        tag(box('Wheelchair backrest pad cushion', (.80, .96, pz+.038), (.27, .67, .050), orange, 13, bevel=.020), 'wheelchair_pad')
        for yy in (.75, 1.17):
            tag(box('Wheelchair backrest pad bracket', (pole.x+.040, yy, pz+.002), (.092, .040, .030), metal, 13, bevel=.004), 'wheelchair_pad')
        # Floor field and glyph: thin raised decals (no coplanar faces with the floor top).
        fx0, fx1 = bay['x'][0]+.02, wall_x-.015
        tag(box('Wheelchair bay floor field', ((fx0+fx1)/2, fy+.0015, (z0+z1)/2), (fx1-fx0, .004, z1-z0-.04), floor_yellow, 0), 'wheelchair_floor')
        gx, gz, gy = (fx0+fx1)/2, (z0+z1)/2, fy+.0042
        for name, u0, u1, v0, v1 in (('torso', -.075, -.040, .02, .20), ('seat', -.075, .110, .020, .055),
                                    ('leg', .080, .115, -.12, .055), ('foot', .080, .175, -.12, -.090)):
            tag(box('Wheelchair floor glyph '+name, (gx+(u0+u1)/2, gy, gz+(v0+v1)/2), (u1-u0, .0024, v1-v0), glyph, 0, lod=1), 'wheelchair_floor')
        tag(rod('Wheelchair floor glyph head', (gx-.057, gy-.0012, gz+.265), (gx-.057, gy+.0012, gz+.265), .036, glyph, 0, n=20, lod=1), 'wheelchair_floor')
        ri, ro, cu, cv = .105, .135, gx-.010, gz-.035
        n = 32
        verts = [(cu+r*math.cos(2*math.pi*i/n), y, cv+r*math.sin(2*math.pi*i/n))
                 for y in (gy-.0012, gy+.0012) for r in (ro, ri) for i in range(n)]
        faces = []
        for i in range(n):
            j = (i+1) % n
            ob_, ib_, ot, it = 0, n, 2*n, 3*n
            faces += [(ot+i, ot+j, it+j, it+i), (ob_+i, ib_+i, ib_+j, ob_+j),
                      (ob_+i, ob_+j, ot+j, ot+i), (ib_+i, it+i, it+j, ib_+j)]
        wheel = tag(mesh('Wheelchair floor glyph wheel', verts, faces, glyph, 0, lod=1), 'wheelchair_floor')
        bay_made = count

    # Existing door-bay stop buttons use the same yellow pod as the photographed poles.
    for ob in bpy.data.objects:
        if ob.type == 'MESH' and ob.name.startswith('Stop request button'):
            ob.data.materials.clear(); ob.data.materials.append(yellow)

    contract['saloon_refinement'] = {
        'revision': 2, 'aisle_poles': len(poles), 'hammers': hammers, 'wheelchair_bay_folding_seats': bay_made,
        'references': ['reference/official-k18-interior.jpg', 'reference/official-c18-en-1.jpg'],
        'pole_rule': 'Behind each low-floor aisle seat (x=+-0.285, 0.40 m behind the cushion centre), skipping door-bay stanchions within 0.30 m; none beyond z=-4.15 (standing system ends before the rear stair).',
        'limits': ['Pole, grille, camera and hammer counts/positions are photo-supported estimates.'],
        'new_objects': made}
    ctx['scene']['k18_interior_contract'] = json.dumps(contract, ensure_ascii=False)
    return contract['saloon_refinement']
