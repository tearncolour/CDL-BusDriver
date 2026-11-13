"""K18 body sides: sliding-window rails, door-status and marker lamps, access hatches, louvre grilles, mudflaps, roof-edge cameras.

References (official K18 / C18 promotional photographs, ModelSource/CRRC_K18/reference/): official-side-inspection-nearest3.png
(door side, ~108 px/m), driver-side-graphic-inspection.png (driver side, lower body) and their crops. The marks are photo
estimates (about +-0.05 m): the real positions, sizes and slot counts are not published.

Unity metres in assembly coordinates, door side +X. Every part stays inside the body half-width limit (1.27 m): the paint
skin is at about 1.25, so the lamps, seams and grilles stand 3-6 mm proud of it.
"""
import math

SKIN = 1.25             # painted lower side surface


def build_k18_side_refinement(ctx):
    import bpy
    from mathutils import Vector
    mesh, box, rod = ctx['mesh'], ctx['box'], ctx['rod']
    select, mat = ctx['select_section'], ctx['mat']
    BLACK, DARK, RUBBER = ctx['BLACK'], ctx['DARK'], ctx['RUBBER']
    DOORS, AXLES = ctx['DOORS'], ctx['AXLES']
    U = ctx['U']
    made = []

    marker_amber = mat('K18 side marker amber', (.95, .42, .05), .3, 0, 'Orange')
    status_green = mat('K18 door status green', (.18, .46, .20), .3, 0, 'Green')
    status_yellow = mat('K18 door status yellow', (.97, .68, .02), .3, 0, 'Yellow')
    marker_red = mat('K18 side marker red', (.8, .04, .06), .3, 0, 'Red')
    grille_dark = mat('K18 side grille recess', (.05, .05, .05), .6, 0, 'Black')

    def section_of(z):
        return 'rear' if z < -2.0 else 'front'

    def tag(ob, role, last=None):
        ob['k18_side_refinement'] = 1
        ob['k18_completion_role'] = role
        made.append(ob.name)
        return ob

    def disc(name, side, z, h, radius, material, depth=.008, part=16, lod=1):
        select(section_of(z))
        x = side * SKIN
        return tag(rod(name, (x - side * .002, h, z), (x + side * (depth - .002), h, z), radius, material, part, n=14, lod=lod), 'side_lamp')

    def frame(name, side, z, h, width, height, line, material, part=16, lod=1):
        """A thin rectangular seam line (an access hatch outline) on the painted skin."""
        select(section_of(z))
        x = side * (SKIN + .001)
        t = .005
        for label, c, size in (('top', (x, h + height / 2, z), (t, line, width)), ('bottom', (x, h - height / 2, z), (t, line, width)),
                               ('front', (x, h, z + width / 2), (t, height, line)), ('rear', (x, h, z - width / 2), (t, height, line))):
            tag(box(name + ' ' + label, c, size, material, part, lod=lod), 'side_hatch')

    def louvre(name, side, z, h, width, height, slots, vertical, part=16):
        """A recessed grille: dark plate with slats (LOD0 only for the slats)."""
        select(section_of(z))
        x = side * (SKIN + .001)
        tag(box(name + ' plate', (x, h, z), (.006, height, width), grille_dark, part, lod=1), 'side_grille')
        for i in range(slots):
            f = (i + .5) / slots - .5
            if vertical:
                c = (x + side * .004, h, z + f * (width - .04)); size = (.004, height - .05, .012)
            else:
                c = (x + side * .004, h + f * (height - .03), z); size = (.004, .012, width - .05)
            tag(box(name + ' slat', c, size, BLACK, part, lod=0), 'side_grille')

    # --- sliding-window rail: the lower pane of every side bay is set off by a black rail (both sides, photo: driver side)
    bpy.context.view_layer.update()
    panes = [ob for ob in bpy.data.objects if ob.type == 'MESH' and ob.name.startswith('Fixed side pane')]
    for pane in panes:
        corners = [U.inverted() @ (pane.matrix_world @ Vector(c)) for c in pane.bound_box]
        z0, z1 = min(c.z for c in corners), max(c.z for c in corners)
        side = 1. if sum(c.x for c in corners) > 0 else -1.
        select(section_of((z0 + z1) / 2))
        tag(box('Window slide rail', (side * 1.246, 1.64, (z0 + z1) / 2), (.020, .036, z1 - z0 - .004), BLACK, 17, lod=1), 'side_window_rail')

    # --- door status lamps behind the rear jamb of each door (photo: green, yellow ring, amber), door side
    for z in DOORS:
        zl = z - 1.04
        disc('Door status lamp green', 1, zl, 1.03, .022, status_green)
        disc('Door status lamp bezel', 1, zl + .15, 1.03, .032, BLACK, depth=.006)
        disc('Door status lamp yellow', 1, zl + .15, 1.03, .022, status_yellow, depth=.010)
        disc('Door status lamp amber', 1, zl + .15, .90, .022, marker_amber)

    # --- amber side marker lamps, both sides, on the lower body (photo: fender, mid body, ahead of the rear wheels)
    markers = (8.28, 3.5, -.28, -2.60, -4.95, -8.40)
    for side in (-1, 1):
        for z in markers:
            if z in (8.28, -8.40) and side > 0:
                continue                      # the door-side front corner is the front door; the tail has its lamp clusters
            disc('Side marker lamp', side, z, .52, .028, marker_amber)
    # red marker lamps on the panels beside the articulation (photo)
    for side in (-1, 1):
        disc('Side marker lamp red', side, -.42, .40, .024, marker_red)
        disc('Side marker lamp red', side, -2.22, .40, .024, marker_red)

    # --- rear section, door side: vent patches, indicator, hatch, grille (photo)
    louvre('Rear side vent patch', 1, -8.37, .71, .48, .13, 6, False)
    louvre('Rear side vent patch', 1, -8.37, .53, .48, .13, 6, False)
    disc('Rear side indicator green', 1, -8.44, .98, .018, status_green)
    frame('Rear side hatch', 1, -7.67, .98, .30, .20, .008, BLACK)
    louvre('Rear side vent patch', 1, -7.67, .39, .38, .10, 5, False)
    louvre('Rear side grille', 1, -6.91, .505, .48, .29, 8, True)

    # --- driver side: flush hatch outlines on the lower body (photo: panel between the wheels)
    frame('Driver side hatch', -1, 2.45, .62, .62, .32, .008, BLACK)
    frame('Driver side hatch', -1, -2.95, .62, .50, .26, .008, BLACK)

    # --- mudflaps behind every wheel
    for az in AXLES:
        for side in (-1, 1):
            select(section_of(az))
            tag(box('Mudflap', (side * .98, .27, az - .648), (.42, .38, .012), RUBBER, 16, lod=1), 'side_mudflap')

    # --- roof-edge side cameras, door side (photo: black stubs on the roof line)
    for z in (4.29, -5.44):
        select(section_of(z))
        tag(box('Side camera pod', (1.20, 3.222, z), (.06, .045, .08), BLACK, 16, bevel=.01, lod=1), 'side_camera')
    return made
