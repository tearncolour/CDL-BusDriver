"""Photo-shaped, closed passenger seats. Does not save or export a scene.

The passenger locations are deliberately unchanged. The supplied interior photo
supports a bowed shoulder, narrower waist and stainless top grip; dimensions are
proportion estimates, not an OEM seat drawing. Surface helpers use the existing
backrest/rear-shell object's local coordinates, so a printed pattern can follow
the same surface without floating line tubes.
"""
import math
import bpy
import bmesh
from mathutils import Vector

STAINLESS_NAME = '6109 photo | brushed stainless handrails'


def stainless_material():
    material = bpy.data.materials.get(STAINLESS_NAME)
    if material is None:
        material = bpy.data.materials.new(STAINLESS_NAME)
    material.diffuse_color = (.56, .58, .59, 1.)
    material['cdlc_palette_swatch'] = 'Chrome'
    material.use_nodes = True
    bsdf = next((n for n in material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'),None)
    if bsdf is None:
        bsdf = material.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
        output = next((n for n in material.node_tree.nodes if n.type == 'OUTPUT_MATERIAL'),None)
        if output is None: output = material.node_tree.nodes.new('ShaderNodeOutputMaterial')
        material.node_tree.links.new(bsdf.outputs['BSDF'],output.inputs['Surface'])
    bsdf.inputs['Base Color'].default_value = material.diffuse_color
    bsdf.inputs['Metallic'].default_value = 1.
    bsdf.inputs['Roughness'].default_value = .25
    return material


def _shape(point, width, height):
    x, y, z = point
    t = (z / height + .5)
    # Broad lower shoulders and a gentle waist. The very top narrows before
    # turning down into a continuous, arched shoulder rather than a box corner.
    factor = 1. - .075 * math.exp(-((t - .40) / .22) ** 2) - .065 * t ** 5
    u = x / (width / 2.)
    x *= factor
    z -= .026 * abs(u) ** 4 * t ** 4
    # A shallow compound bow is visible on both the upholstery and rear shell.
    # Equal bow on both keeps the hard shell's thickness and laminated join.
    y += .012 * (1. - u * u) + .007 * math.sin(math.pi * t)
    return Vector((x, y, z))


def _rounded_point(x, y, z, width, depth, height):
    half = (width / 2., depth / 2., height / 2.)
    radius = min(.018, depth * .32)
    p = Vector((x * half[0], y * half[1], z * half[2]))
    core = Vector(tuple(max(-h + radius, min(h - radius, q)) for q, h in zip(p, half)))
    direction = p - core
    p = core + direction.normalized() * radius if direction.length_squared else core
    return _shape(p, width, height)


def seat_surface(u, v, *, rear_shell=False, side='rear'):
    """Return local surface point for a conforming print (u=-1..1, v=0..1).

    Use abs(u)<=.88 and .06<=v<=.94 for the printed field. `rear_shell=True`
    means the thin hard rear panel object, whose own matrix already includes
    its normal offset. Rear is +local Y, front is -local Y.
    """
    if not (-1. <= u <= 1. and 0. <= v <= 1.):
        raise ValueError('Seat surface coordinates outside the closed panel')
    if side not in ('front', 'rear'):
        raise ValueError('Seat side must be front or rear')
    width, depth, height = (.445, .018, .62) if rear_shell else (.435, .060, .66)
    return _rounded_point(u, 1. if side == 'rear' else -1., 2. * v - 1., width, depth, height)


def seat_surface_normal(u, v, *, rear_shell=False, side='rear'):
    epsilon = 1.e-5
    kwargs = {'rear_shell':rear_shell, 'side':side}
    du = seat_surface(min(1., u + epsilon), v, **kwargs) - seat_surface(max(-1., u - epsilon), v, **kwargs)
    dv = seat_surface(u, min(1., v + epsilon), **kwargs) - seat_surface(u, max(0., v - epsilon), **kwargs)
    normal = dv.cross(du).normalized()
    return normal if side == 'rear' else -normal


def shaped_backrest_mesh(name, *, rear_shell=False):
    width, depth, height = (.445, .018, .62) if rear_shell else (.435, .060, .66)
    # Weld six sampled rounded-box faces before the compound bow. Every edge is
    # shared twice, including all corner transitions; no hidden open cover.
    vertices, faces, lookup = [], [], {}
    def point(raw):
        key = tuple(round(a, 10) for a in raw)
        if key not in lookup:
            lookup[key] = len(vertices)
            vertices.append(tuple(_rounded_point(*raw, width, depth, height)))
        return lookup[key]
    # face fixed axis/sign, free axes and subdivisions
    for axis, sign, a, b, na, nb in ((1,-1,0,2,12,20),(1,1,0,2,12,20),
                                    (0,-1,1,2,4,20),(0,1,1,2,4,20),
                                    (2,-1,0,1,12,4),(2,1,0,1,12,4)):
        rows = []
        for j in range(nb + 1):
            row = []
            for i in range(na + 1):
                raw = [0.,0.,0.]; raw[axis] = sign
                raw[a], raw[b] = 2. * i / na - 1., 2. * j / nb - 1.
                row.append(point(raw))
            rows.append(row)
        for j in range(nb):
            for i in range(na):
                faces.append((rows[j][i], rows[j][i+1], rows[j+1][i+1], rows[j+1][i]))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    bm = bmesh.new(); bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh); bm.free()
    for face in mesh.polygons: face.use_smooth = True
    return mesh


def grip_local_points():
    """Backrest-local centreline, 32 mm stainless tube, 55 mm top corners.

    Outer top is about 31 mm lower than revision 2. Feet enter the closed back's
    upper corners. The free upper span stands clear of the bowed rear panel.
    """
    x, y, top, radius = .207, .082, .456, .055
    points = [(-.186,.042,.220),(-x,y,.300),(-x,y,top-radius)]
    for i in range(1, 9):
        angle = math.pi - math.pi / 2. * i / 8.
        points.append((-x+radius+radius*math.cos(angle),y,top-radius+radius*math.sin(angle)))
    points.append((x-radius,y,top))
    for i in range(1, 9):
        angle = math.pi / 2. - math.pi / 2. * i / 8.
        points.append((x-radius+radius*math.cos(angle),y,top-radius+radius*math.sin(angle)))
    points.extend(((x,y,.300),(.186,.042,.220)))
    return [Vector(p) for p in points]


def _objects(collection):
    result = list(collection.objects)
    for child in collection.children: result.extend(_objects(child))
    return result


def apply_photo_seat_geometry(master):
    """Change existing passenger backs; only the four removed design IDs may be absent."""
    from seat_access import REMOVED_SEAT_NUMBERS
    objects = {obj.name:obj for obj in _objects(master)}
    changed, grip_names = [], []
    steel = stainless_material()
    for number in range(1,32):
        name = f'Passenger seat {number:02d}'
        if number in REMOVED_SEAT_NUMBERS and name+' backrest' not in objects:continue
        back = objects[name + ' backrest']
        for suffix in (' backrest',' rear shell'):
            obj = objects.get(name + suffix)
            if obj is None: continue
            old = obj.data; materials = list(old.materials)
            obj.data = shaped_backrest_mesh(name + suffix + ' photo shaped',rear_shell=suffix==' rear shell')
            for material in materials: obj.data.materials.append(material)
            for modifier in list(obj.modifiers):
                if modifier.type in ('BEVEL','WEIGHTED_NORMAL'): obj.modifiers.remove(modifier)
            if old.users == 0: bpy.data.meshes.remove(old)
            obj['seat_photo_shape_revision'] = 3
            obj['preserve_lod'] = True
            changed.append(obj.name)
        grip = objects.get(name + ' upper grab')
        if grip is None: continue
        inverse = grip.matrix_world.inverted()
        points = [inverse @ back.matrix_world @ p for p in grip_local_points()]
        grip.data.splines.clear(); spline = grip.data.splines.new('POLY')
        spline.points.add(len(points)-1)
        for target, point in zip(spline.points, points): target.co = (*point,1.)
        grip.data.bevel_depth = .016; grip.data.bevel_resolution = 3; grip.data.use_fill_caps = True
        grip.data.materials.clear(); grip.data.materials.append(steel)
        grip['seat_photo_shape_revision'] = 3
        grip['preserve_lod'] = True
        grip['cdlc_preserve_section_lod'] = True
        grip_names.append(grip.name)
    bpy.context.view_layer.update()
    return {'status':'PASS','scope':'Photo-estimated closed passenger seat shoulders/waist and stainless top grips; no seat/pivot/cushion positions changed',
            'passenger_backrests':sum(name.endswith(' backrest') for name in changed),'changed_panels':changed,'stainless_grips':grip_names,
            'grip_outer_width_m':.446,'grip_tube_diameter_m':.032,'grip_corner_radius_m':.055,
            'grip_top_reduction_m':.031,'surface_api':'seat_geometry_photo.seat_surface / seat_surface_normal in object local coordinates',
            'print_domain':{'u':[-.88,.88],'v':[.06,.94]},
            'reference':'references/user-reference-20261015-01.jpg',
            'dimension_status':'Estimated from photograph, not an OEM drawing'}
