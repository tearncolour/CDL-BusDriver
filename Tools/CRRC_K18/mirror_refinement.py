"""Optical calibration after build_mirrors_refined(), before explicit LODs.

Mirror centres and vehicle mounting shoes retain the photograph-based placement.
The glass and its complete head turn together; closed swivel links connect the
new head attachment to the existing fixed stem. These are predicted rearward
views, not a substitute for the game's mirror-camera acceptance test.
"""
import json
import math
import re
from mathutils import Matrix, Vector


def refine_k18_mirror_optics():
    select_section('front')
    to_blender = Matrix(((-1., 0., 0., 0.), (0., 0., -1., 0.),
                         (0., 1., 0., 0.), (0., 0., 0., 1.)))
    to_unity = to_blender.inverted()
    eye = Vector(bpy.data.objects['section_front']['driver_eye_unity'])
    rows = []

    def base_name(ob):
        return re.sub(r'^LOD[012] ', '', ob.name)

    def points(ob):
        evaluated = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
        data = evaluated.to_mesh()
        try:
            return [to_unity @ (evaluated.matrix_world @ v.co) for v in data.vertices]
        finally:
            evaluated.to_mesh_clear()

    def plane(ob, normal, up):
        normal = normal.normalized()
        up = (up-normal*up.dot(normal)).normalized()
        right = normal.cross(up).normalized()
        ps = points(ob)
        offset = max(p.dot(normal) for p in ps)
        flat = [p for p in ps if abs(p.dot(normal)-offset) < .00003]
        if len(flat) < 4:
            raise RuntimeError('Mirror has no measured planar front: '+ob.name)
        u0, u1 = min(p.dot(right) for p in flat), max(p.dot(right) for p in flat)
        v0, v1 = min(p.dot(up) for p in flat), max(p.dot(up) for p in flat)
        centre = right*((u0+u1)/2)+up*((v0+v1)/2)+normal*offset
        return centre, normal, up, (u1-u0, v1-v0)

    def bent_tube(name, nodes, radius, material, level):
        """Circular fillets joined tangentially to lines; one closed tube."""
        nodes = [Vector(p) for p in nodes]
        arc_step = math.radians((8, 14, 22)[level])
        line_step = (.10, .18, .30)[level]
        radial = (12, 8, 6)[level]
        centres, tangents, radii = [], [], []

        def add(point, tangent):
            # mathutils uses float32; at Z~10 m equivalent arc endpoints
            # may differ by a micron. Never sweep an almost-zero interval.
            if centres and (point-centres[-1]).length < 1e-5:
                tangents[-1] = tangent.normalized()
                return
            centres.append(point.copy())
            tangents.append(tangent.normalized())

        def line(a, b, tangent):
            steps = max(1, math.ceil((b-a).length/line_step))
            for i in range(steps+1): add(a.lerp(b, i/steps), tangent)

        cursor = nodes[0]
        for i in range(1, len(nodes)-1):
            a, b = (nodes[i]-nodes[i-1]).normalized(), (nodes[i+1]-nodes[i]).normalized()
            angle = math.acos(max(-1., min(1., a.dot(b))))
            if angle < 1e-5:
                line(cursor, nodes[i], a)
                cursor = nodes[i]
                continue
            tangent_factor = math.tan(angle/2)
            before = (nodes[i]-nodes[i-1]).length*(.75 if i == 1 else .45)
            after = (nodes[i+1]-nodes[i]).length*(.75 if i == len(nodes)-2 else .45)
            bend_radius = min(.07, min(before, after)/tangent_factor)
            if bend_radius <= radius+.005:
                raise RuntimeError('Mirror arm bend lacks tube clearance: '+name)
            radii.append(bend_radius)
            tangent_distance = bend_radius*tangent_factor
            start, end = nodes[i]-a*tangent_distance, nodes[i]+b*tangent_distance
            axis = a.cross(b).normalized()
            centre = start+axis.cross(a)*bend_radius
            line(cursor, start, a)
            steps = max(2, math.ceil(angle/arc_step))
            for k in range(1, steps+1):
                q = Matrix.Rotation(angle*k/steps, 3, axis)
                add(centre+q@(start-centre), q@a)
            cursor = end
        line(cursor, nodes[-1], (nodes[-1]-nodes[-2]).normalized())
        ref = Vector((0, 1, 0)) if abs(tangents[0].y) < .9 else Vector((1, 0, 0))
        u = tangents[0].cross(ref).normalized()
        vertices = []
        previous = tangents[0]
        for centre, tangent in zip(centres, tangents):
            u = previous.rotation_difference(tangent)@u
            u = (u-tangent*u.dot(tangent)).normalized()
            v = tangent.cross(u)
            vertices.extend(centre+radius*(u*math.cos(j*math.tau/radial)+v*math.sin(j*math.tau/radial)) for j in range(radial))
            previous = tangent
        faces = [tuple(reversed(range(radial))), tuple(range((len(centres)-1)*radial, len(centres)*radial))]
        for ring in range(len(centres)-1):
            for j in range(radial):
                a, b = ring*radial+j, ring*radial+(j+1)%radial
                faces.append((a, b, b+radial, a+radial))
        ob = mesh(('LOD'+str(level)+' ' if level else '')+name, vertices, faces, material, 15, lod=level)
        for polygon in ob.data.polygons: polygon.use_smooth = len(polygon.vertices) == 4
        ob['first_lod'] = level
        ob['last_lod'] = level
        ob['k18_mirror_assembly'] = True
        ob['k18_mirror_tube_explicit_lods'] = True
        ob['mirror_tube_radius_m'] = radius
        ob['mirror_min_bend_radius_m'] = min(radii) if radii else 0.
        ob['mirror_centerline_nodes_unity'] = [list(p) for p in nodes]
        if level:
            ob.hide_render = True
            ob.hide_set(True)
        return ob, {'object':ob.name, 'rings':len(centres), 'radial_segments':radial,
                    'minimum_bend_radius_m':min(radii) if radii else None,
                    'tube_radius_m':radius, 'closed_end_caps':True}

    # Re-execution in an isolated review is idempotent. Original head meshes,
    # mounting points and centres are kept, not inferred from a rotated AABB.
    for ob in list(bpy.data.objects):
        if ob.get('k18_mirror_refinement_connector') or ob.get('k18_mirror_explicit_lod2') or ob.get('k18_mirror_tube_explicit_lods'):
            bpy.data.objects.remove(ob, do_unlink=True)
    for ob in bpy.data.objects:
        if 'k18_mirror_rest_matrix' in ob:
            values = ob['k18_mirror_rest_matrix']
            ob.matrix_world = Matrix([values[i:i+4] for i in range(0, 16, 4)])
    bpy.context.view_layer.update()

    for label, side, fixed_tip, old_head_top in (
        ('driver-side', -1, (-1.48, 2.41, 8.79), (-1.48, 2.389, 8.79)),
        ('door-side', 1, (1.48, 2.372, 9.64), (1.48, 2.367, 9.64)),
    ):
        glass = bpy.data.objects['K18 '+label+' reflective insert']
        centre, _, _, _ = plane(glass, Vector((0, 0, -1)), Vector((0, 1, 0)))
        # Centre between the flank and the adjacent lane, ten metres behind
        # the front section, at a road-object height. Explicit design targets,
        # not claimed factory mirror-angle dimensions.
        target = Vector((side*1.9, 1.20, -2.0))
        toward_eye = (eye-centre).normalized()
        toward_target = (target-centre).normalized()
        normal = (toward_eye+toward_target).normalized()
        up = (Vector((0, 1, 0))-normal*normal.y).normalized()
        right = normal.cross(up).normalized()
        rotation = Matrix((right, up, -normal)).transposed().to_4x4()
        delta = to_blender @ Matrix.Translation(centre) @ rotation @ Matrix.Translation(-centre) @ to_unity
        members = []
        for ob in bpy.data.objects:
            if base_name(ob) not in ('K18 '+label+' mirror body', 'K18 '+label+' reflective insert'):
                continue
            if 'k18_mirror_rest_matrix' not in ob:
                ob['k18_mirror_rest_matrix'] = [float(x) for row in ob.matrix_world for x in row]
            ob.matrix_world = delta @ ob.matrix_world
            ob['k18_mirror_head_calibrated'] = True
            members.append(ob.name)
        bpy.context.view_layer.update()
        measured, measured_n, measured_up, size = plane(glass, normal, up)
        if (measured-centre).length > .00002:
            raise RuntimeError('Mirror calibration moved its retained centre: '+label)
        for ob in bpy.data.objects:
            if base_name(ob) == 'K18 '+label+' reflective insert':
                ob['mirror_base_origin_unity'] = list(measured)
                ob['mirror_normal_unity'] = list(measured_n)
                ob['mirror_up_unity'] = list(measured_up)
                ob['mirror_size_m'] = list(size)
                ob['mirror_convex_depth_m'] = 0.
        attachment = centre + rotation.to_3x3() @ (Vector(old_head_top)-centre)
        link = rod('K18 '+label+' mirror closed swivel link', fixed_tip, attachment, .025, DARK, 15, n=12)
        link['k18_mirror_assembly'] = True
        link['k18_mirror_refinement_connector'] = True
        link['mirror_fixed_stem_tip_unity'] = list(fixed_tip)
        link['mirror_head_attachment_unity'] = list(attachment)
        incoming = (centre-eye).normalized()
        reflected = incoming-2*incoming.dot(normal)*normal
        predictions = []
        for z in (2., -2., -5.68):
            t = (z-centre.z)/reflected.z
            predictions.append({'plane_z': z, 'centre_ray_intersection': list(centre+reflected*t)})
        rows.append({'side': label, 'eye_unity': list(eye), 'retained_centre_unity': list(centre),
                     'normal_unity': list(normal), 'up_unity': list(up), 'flat_aperture_size_m': list(size),
                     'design_aim_target_unity': list(target), 'reflected_centre_direction': list(reflected),
                     'near_far_predictions': predictions, 'head_objects': members,
                     'fixed_stem_tip_unity': list(fixed_tip), 'head_attachment_unity': list(attachment),
                     'closed_swivel_length_m': (attachment-Vector(fixed_tip)).length})

    auxiliary = bpy.data.objects['K18 round auxiliary reflective face']
    n = Vector(auxiliary['mirror_normal_unity']).normalized()
    auxiliary['mirror_up_unity'] = list((Vector((0, 1, 0))-n*n.y).normalized())
    radius = float(auxiliary['mirror_radius_m'])
    auxiliary['mirror_size_m'] = [2*radius, 2*radius]

    # Replace the old individually capped straight segments, preserving all
    # authored path nodes and their mounting endpoints. Tangential arc sweeps
    # remove the exposed wedge-shaped caps at elbows without cover pieces.
    prefixes = ('K18 driver-side compact mirror arm', 'K18 door-side twin mirror support',
                'K18 door-side downturned mirror stem', 'K18 auxiliary mirror connecting bracket')
    for ob in list(bpy.data.objects):
        if any(base_name(ob).startswith(prefix) for prefix in prefixes):
            bpy.data.objects.remove(ob, do_unlink=True)
    mount_z = front_z(1.231, 2.565)
    tube_paths = [
        ('K18 driver-side continuous mirror arm',
         [(-1.231,2.565,mount_z-.026),(-1.36,2.58,8.65),(-1.48,2.57,8.79),(-1.48,2.41,8.79)], .025, DARK),
        ('K18 door-side continuous upper mirror rail',
         [(1.231,2.67,mount_z-.015),(1.30,2.702,8.79),(1.42,2.702,9.64),(1.456,2.68,9.64)], .021, METAL),
        ('K18 door-side continuous lower mirror rail',
         [(1.231,2.50,mount_z-.015),(1.30,2.532,8.79),(1.42,2.532,9.64),(1.456,2.51,9.64)], .021, METAL),
        ('K18 door-side continuous mirror stem',
         [(1.456,2.69,9.64),(1.48,2.65,9.64),(1.48,2.372,9.64)], .028, DARK),
        ('K18 auxiliary continuous connecting bracket',
         [(1.48,2.49,9.64),(1.37,2.46,9.64),(1.27,2.40,9.64)], .012, METAL),
    ]
    tube_rows = []
    for name, nodes, radius, material in tube_paths:
        for level in range(3):
            _, evidence = bent_tube(name, nodes, radius, material, level)
            tube_rows.append(evidence)

    # The general far-LOD omission drops part 15. Provide a compact explicit
    # mirror assembly instead: six-sided closed tubes, single-segment bevels,
    # and the existing small convex optic. Do not copy unrelated accessories.
    lod2_count = 0
    for original in list(bpy.data.objects):
        if not original.get('k18_mirror_assembly') or original.name.startswith('LOD'):
            continue
        if original.get('k18_mirror_tube_explicit_lods'):
            continue
        if original.type != 'MESH':
            continue
        clone = original.copy()
        clone.data = original.data.copy()
        CURRENT.objects.link(clone)
        clone.name = 'LOD2 '+original.name
        clone['first_lod'] = 2
        clone['last_lod'] = 2
        clone['k18_mirror_explicit_lod2'] = True
        clone.hide_render = True
        clone.hide_set(True)
        for modifier in clone.modifiers:
            if modifier.type == 'BEVEL': modifier.segments = 1
        count = int(clone.get('radial_segments', 0))
        if int(clone.get('radial_rings', 0)) == 2 and count >= 8 and len(clone.data.vertices) == 2*count:
            centres = [sum((v.co for v in clone.data.vertices[k*count:(k+1)*count]), Vector())/count for k in (0, 1)]
            axis = (centres[1]-centres[0]).normalized()
            u = clone.data.vertices[0].co-centres[0]
            v = axis.cross(u)
            verts = [c+u*math.cos(j*math.tau/6)+v*math.sin(j*math.tau/6) for c in centres for j in range(6)]
            faces = [tuple(reversed(range(6))), tuple(range(6, 12))]
            faces += [(j, (j+1)%6, (j+1)%6+6, j+6) for j in range(6)]
            data = bpy.data.meshes.new(clone.name)
            data.from_pydata(verts, [], faces)
            for material in clone.data.materials: data.materials.append(material)
            data.update()
            clone.data = data
            for polygon in data.polygons: polygon.use_smooth = len(polygon.vertices) == 4
            clone['radial_segments'] = 6
        lod2_count += 1
    report = {'status': 'OPTICAL_PREDICTION_ONLY_GAME_TEST_PENDING', 'coordinate_frame': 'Unity physical metres',
              'main_mirrors': rows, 'explicit_lod2_objects': lod2_count,
              'continuous_arm_lods':tube_rows,
              'unchanged': ['mirror centres', 'vehicle-side mounting shoes', 'support path control nodes and endpoints', 'auxiliary mirror aim'],
              'limits': ['Design target band is an estimate, not factory optical calibration.',
                         'Actual game reflection cameras and complete glass must still be tested.']}
    bpy.context.scene['k18_mirror_optics'] = json.dumps(report)
    print('K18_MIRROR_OPTICS '+json.dumps(report), flush=True)
    return report


refine_k18_mirror_optics()
