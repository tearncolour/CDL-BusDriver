"""Continuous display backing plates with closed returns to the existing carrier.

Dimensions use the private canonical construction frame.  The visible display,
carrier and curved outer glazing are deliberately not rebuilt here.
"""
from copy import deepcopy


class BodyReturn:
    """Locate the existing inner structural skin, not a guessed wider plate."""
    def __init__(self, which):
        import ast
        import json
        import math
        from pathlib import Path
        import bpy
        from mathutils import Vector
        from mathutils.bvhtree import BVHTree
        from source_coordinates import canonical_world_matrix
        path = Path(__file__).with_name('exterior.py')
        tree = ast.parse(path.read_text(encoding='utf-8'))
        names = {'spline', 'width_at', 'front_at', 'rear_at', 'end_point', 'end_inset_vector'}
        variables = {'_front_profile', '_rear_profile', '_front_shift', '_rear_shift'}
        nodes = [n for n in tree.body if
                 isinstance(n, ast.FunctionDef) and n.name in names or
                 isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id in variables for t in n.targets)]
        spec = json.loads((path.parents[2] / 'ModelSource/SWB6109EV12G/model-spec.json').read_text())
        self.functions = {'math': math, 'Vector': Vector, 'HALF': spec['length_m']/2}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), str(path), 'exec'), self.functions)
        self.which, self.rear, self.samples = which, which == 'rear', {}
        self.name = ('Rear' if self.rear else 'Front') + ' continuous coachwork'
        original = bpy.data.objects[self.name]
        evaluated = original.evaluated_get(bpy.context.evaluated_depsgraph_get())
        data = evaluated.to_mesh()
        try:
            data.calc_loop_triangles()
            world = canonical_world_matrix(original)
            self.tree = BVHTree.FromPolygons([world @ v.co for v in data.vertices],
                         [tuple(t.vertices) for t in data.loop_triangles], all_triangles=True)
        finally:
            evaluated.to_mesh_clear()

    def contact(self, point):
        from mathutils import Vector
        key = tuple(round(v, 10) for v in point)
        if key in self.samples:
            return self.samples[key]
        outer = SPECS[self.which]['outer']
        u = .946 * point[0] / outer[1]
        bottom = self.rear and abs(point[1]-outer[2]) < 1e-9
        top = abs(point[1]-outer[3]) < 1e-9
        z = 1.765 if bottom else point[1]
        if top or bottom:
            # A normal inset also lowers the top skin. Keep the actual upper
            # return above the plate edge instead of folding it back through
            # the plate's occupied volume.
            for _ in range(12):
                trial = Vector(self.functions['end_point'](u, z, self.rear))
                inner = trial+self.functions['end_inset_vector'](trial, self.rear)
                z += (outer[3]+.004 if top else 1.765)-inner.z
        surface = Vector(self.functions['end_point'](u, z, self.rear))
        inset = self.functions['end_inset_vector'](surface, self.rear)
        nominal = surface + inset
        outward = -inset.normalized()
        if bottom:
            # Keep the complete lower terminal edge in one real horizontal
            # section. Micrometre Z interpolation errors otherwise amplify
            # into a longitudinal gap on this nearly horizontal return.
            nominal.z = 1.765
            outward = Vector((0, 1, 0))
        origin = nominal-outward*.08
        hit, normal, face, distance = self.tree.ray_cast(origin, outward, .10)
        if hit is None or (hit-nominal).length > .001:
            raise RuntimeError('Actual inner coachwork skin is absent near ' + str((self.which, point, u, z)))
        # Preserve the two original Y layers through the turn. Offsetting the
        # terminal along the body normal would reverse their transverse order
        # on the long side returns and make the two faces cross each other.
        lip_direction = Vector((0, 1 if self.rear else -1, 0))
        sample = dict(plate_point_xz=list(point), body_object=self.name, u=u, z=z,
                      nominal_inner=list(nominal), actual_inner=list(hit),
                      actual_triangle=face, section_origin=list(origin),
                      section_direction=list(outward), section_distance_m=distance,
                      nominal_to_actual_m=(hit-nominal).length,
                      lip_thickness_axis='canonical Y', lip_thickness_m=.004,
                      embed_projected_body_normal_m=.002*abs(normal.dot(lip_direction)),
                      interior_tip=list(hit-lip_direction*.002), exterior_tip=list(hit+lip_direction*.002))
        self.samples[key] = sample
        return sample

    def tip(self, point, index):
        sample = self.contact(point)
        exterior = index == (1 if self.rear else 0)
        return sample['exterior_tip' if exterior else 'interior_tip']

SPECS = {
    'front': {
        'name': 'Front destination ceramic backing',
        'outer': (-1.15, 1.15, 2.39, 2.82),
        'opening': (-.91, .91, 2.445, 2.76),
        'plate_y': (-5.0625, -5.0475),
        'return_opening': (-.84, .84, 2.48, 2.72),
        'return_y': (-5.065, -5.060),
        'window': None,
    },
    'rear': {
        'name': 'Rear glazing ceramic backing',
        'outer': (-1.18, 1.18, 1.77, 2.98),
        'opening': (-.86, .86, 2.46, 2.76),
        'plate_y': (5.046, 5.064),
        'return_opening': (-.77, .77, 2.50, 2.72),
        'return_y': (5.087, 5.091),
        'window': (-1.08, 1.08, 1.77, 2.15),
    },
}


def geometry(which, body_return=None):
    """One watertight solid, including both plate faces and the aperture return."""
    spec = SPECS[which]
    outer, hole = spec['outer'], spec['opening']
    exclusions = [hole] + ([spec['window']] if spec['window'] else [])
    xs = sorted({x for rect in [outer] + exclusions for x in rect[:2]})
    zs = sorted({z for rect in [outer] + exclusions for z in rect[2:]})
    if body_return:
        import math
        # Shared stations subdivide the real plate and return together; there
        # are no T-junctions, detached strips or overlapping cover polygons.
        nx, nz = math.ceil((outer[1]-outer[0])/.04), math.ceil((outer[3]-outer[2])/.02)
        xs = sorted({round(v, 10) for v in xs + [outer[0]+(outer[1]-outer[0])*i/nx for i in range(1, nx)]})
        zs = sorted({round(v, 10) for v in zs + [outer[2]+(outer[3]-outer[2])*i/nz for i in range(1, nz)]})
    vertices, faces, vertex_ids, edges = [], [], {}, {}

    def vertex(x, y, z):
        key = tuple(round(v, 10) for v in (x, y, z))
        if key not in vertex_ids:
            vertex_ids[key] = len(vertices)
            vertices.append((x, y, z))
        return vertex_ids[key]

    def layer(point, index):
        return vertex(point[0], spec['plate_y'][index], point[1])

    def tip(point, index):
        target = spec['return_opening']
        u = (point[0] - hole[0]) / (hole[1] - hole[0])
        v = (point[1] - hole[2]) / (hole[3] - hole[2])
        return vertex(target[0] + u * (target[1] - target[0]),
                      spec['return_y'][index],
                      target[2] + v * (target[3] - target[2]))

    def body_tip(point, index):
        return vertex(*body_return.tip(point, index))

    def body_edge(a, b):
        return body_return and (any(abs(a[0]-x) < 1e-9 and abs(b[0]-x) < 1e-9 for x in outer[:2]) or
                abs(a[1]-outer[3]) < 1e-9 and abs(b[1]-outer[3]) < 1e-9 or
                which == 'rear' and abs(a[1]-outer[2]) < 1e-9 and abs(b[1]-outer[2]) < 1e-9)

    def in_rect(x, z, rect):
        return rect[0] < x < rect[1] and rect[2] < z < rect[3]

    def hole_edge(a, b):
        eps = 1e-9
        return any(abs(a[0] - x) < eps and abs(b[0] - x) < eps and
                   hole[2] - eps <= min(a[1], b[1]) and max(a[1], b[1]) <= hole[3] + eps
                   for x in hole[:2]) or any(
                   abs(a[1] - z) < eps and abs(b[1] - z) < eps and
                   hole[0] - eps <= min(a[0], b[0]) and max(a[0], b[0]) <= hole[1] + eps
                   for z in hole[2:])

    for x0, x1 in zip(xs, xs[1:]):
        for z0, z1 in zip(zs, zs[1:]):
            x, z = (x0 + x1) / 2, (z0 + z1) / 2
            if any(in_rect(x, z, rect) for rect in exclusions):
                continue
            ring = [(x0, z0), (x1, z0), (x1, z1), (x0, z1)]
            faces.append(tuple(layer(p, 0) for p in ring))
            faces.append(tuple(layer(p, 1) for p in reversed(ring)))
            for a, b in zip(ring, ring[1:] + ring[:1]):
                key = tuple(sorted((a, b)))
                edges[key] = None if key in edges else (a, b)
    return_ends = {}
    for edge in edges.values():
        if edge is None:
            continue
        a, b = edge
        if hole_edge(a, b):
            # Two sloping faces continue the plate into the carrier's rear
            # perimeter; the last face closes the inner lip thickness.
            faces.append((layer(b, 0), layer(a, 0), tip(a, 0), tip(b, 0)))
            faces.append((layer(a, 1), layer(b, 1), tip(b, 1), tip(a, 1)))
            faces.append((tip(b, 0), tip(a, 0), tip(a, 1), tip(b, 1)))
        elif body_edge(a, b):
            faces.append((layer(b, 0), layer(a, 0), body_tip(a, 0), body_tip(b, 0)))
            faces.append((layer(a, 1), layer(b, 1), body_tip(b, 1), body_tip(a, 1)))
            faces.append((body_tip(b, 0), body_tip(a, 0), body_tip(a, 1), body_tip(b, 1)))
            for p in (a, b):
                return_ends[p] = return_ends.get(p, 0)+1
        else:
            faces.append((layer(b, 0), layer(a, 0), layer(a, 1), layer(b, 1)))
    for p, count in return_ends.items():
        if count == 1:
            # Ends at the front clear-windshield edge or rear passenger-window
            # lower corners retain a closed finite return thickness.
            faces.append((layer(p, 0), layer(p, 1), body_tip(p, 1), body_tip(p, 0)))
    return vertices, faces


def create_backings(mesh, stamp, material):
    """Build-model entry point, before source handedness and animation parenting."""
    import bmesh
    result = []
    for which, spec in SPECS.items():
        body_return = BodyReturn(which)
        verts, faces = geometry(which, body_return)
        obj = mesh(spec['name'], verts, faces, material)
        bm = bmesh.new(); bm.from_mesh(obj.data)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        if any(not edge.is_manifold for edge in bm.edges) or bm.calc_volume(signed=True) <= 0:
            raise RuntimeError(which + ' generated enclosure must be closed and outward oriented')
        bm.to_mesh(obj.data); bm.free(); obj.data.update()
        stamp(obj, 17)
        obj['preserve_lod'] = True
        obj['cdlc_sign_enclosure_revision'] = 2
        result.append(obj)
    return result


def replace_saved(master):
    """Update only the two existing backing groups in a saved authored model."""
    import bpy
    import bmesh
    from mathutils import Vector
    from source_coordinates import canonical_world_matrix
    result = []
    for which, spec in SPECS.items():
        targets = sorted((ob for ob in master.all_objects if
                          ob.name == spec['name'] or ob.name.startswith(spec['name'] + '.')),
                         key=lambda ob: ob.name)
        expected = 4 if which == 'front' else 6
        if len(targets) not in (1, expected):
            raise RuntimeError('Expected original %s backing group of %d objects, got %d' %
                               (which, expected, len(targets)))
        keep = next(ob for ob in targets if ob.name == spec['name'])
        materials = list(keep.data.materials)
        if len(materials) != 1:
            raise RuntimeError('Backing must retain exactly its existing black material')
        old_data = [ob.data for ob in targets]
        inverse = canonical_world_matrix(keep).inverted()
        body_return = BodyReturn(which)
        verts, faces = geometry(which, body_return)
        data = bpy.data.meshes.new(spec['name'] + ' | continuous plate and return')
        data.from_pydata([inverse @ Vector(v) for v in verts], [], faces)
        data.materials.append(materials[0])
        bm = bmesh.new(); bm.from_mesh(data)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        # Explicit short quads avoid large ngon triangulation and preserve
        # every small return face through the normal export threshold.
        if any(not edge.is_manifold for edge in bm.edges):
            raise RuntimeError(which + ' backing is not a closed two-manifold solid')
        volume = bm.calc_volume(signed=True)
        if volume <= 0:
            raise RuntimeError(which + ' backing has inward orientation or no volume')
        bm.to_mesh(data); bm.free(); data.update()
        keep.data = data
        keep.modifiers.clear()
        keep['preserve_lod'] = True
        keep['cdlc_sign_enclosure_revision'] = 2
        removed = [ob.name for ob in targets if ob != keep]
        for ob in targets:
            if ob != keep:
                bpy.data.objects.remove(ob, do_unlink=True)
        for old in old_data:
            if old.users == 0:
                bpy.data.meshes.remove(old)
        result.append({'side': which, 'object': keep.name, 'removed_objects': removed,
                       'vertices': len(data.vertices), 'faces': len(data.polygons),
                       'closed_manifold': True, 'signed_volume_m3': volume,
                       'geometry_contract': deepcopy(spec),
                       'actual_body_contacts': list(body_return.samples.values())})
    return result
