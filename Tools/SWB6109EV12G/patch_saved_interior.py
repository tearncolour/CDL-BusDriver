"""Idempotent in-memory repair of the saved interior; caller controls saving.

No file is opened or saved on import. Use patch_saved_interior(master, reader_json)
after loading a physical Blender source, before the coordinated re-export.
"""
import math
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

from card_reader import replace_common_card_reader, _collection_objects
from source_coordinates import canonical_world_matrix, source_is_corrected
from seat_access import REMOVED_SEAT_NUMBERS,apply_saved_seat_access


def _flat(matrix):
    return [float(matrix[r][c]) for c in range(4) for r in range(4)]


def seat_grab_offsets():
    """Photo-estimated 44 cm outer width, 32 mm tube, 55 mm corner radii."""
    points = [(-.207, .265, .940), (-.207, .335, 1.020), (-.207, .335, 1.150)]
    for i in range(1, 7):
        theta = math.pi - (math.pi / 2) * i / 6
        points.append((-.152 + .055 * math.cos(theta), .335, 1.150 + .055 * math.sin(theta)))
    points.append((.152, .335, 1.205))
    for i in range(1, 7):
        theta = math.pi / 2 - (math.pi / 2) * i / 6
        points.append((.152 + .055 * math.cos(theta), .335, 1.150 + .055 * math.sin(theta)))
    points.extend(((.207, .335, 1.020), (.207, .265, .940)))
    return points


def seat_materials():
    specifications = (
        ('Seat upholstery | photo dark grey', (.032, .038, .041), .68, 0.),
        ('Seat hard rear shell | photo dark grey', (.035, .042, .045), .28, .05),
        ('Seat upper grab | photo silver grey', (.263, .274, .274), .36, .35),
    )
    result = []
    for name, color, roughness, metallic in specifications:
        material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
        material.diffuse_color = (*color, 1.)
        material.use_nodes = True
        bs = next((node for node in material.node_tree.nodes if node.type == 'BSDF_PRINCIPLED'), None)
        if bs is None:bs = material.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
        output = next((node for node in material.node_tree.nodes if node.type == 'OUTPUT_MATERIAL'), None)
        if output is None:output = material.node_tree.nodes.new('ShaderNodeOutputMaterial')
        if not output.inputs['Surface'].is_linked:material.node_tree.links.new(bs.outputs['BSDF'], output.inputs['Surface'])
        bs.inputs['Base Color'].default_value = (*color, 1.)
        bs.inputs['Roughness'].default_value = roughness
        bs.inputs['Metallic'].default_value = metallic
        material['livery_control'] = False; material['light_id'] = 0; material['glass'] = False
        result.append(material)
    return tuple(result)


def _resize_depth(obj, depth):
    if obj.type != 'MESH':raise RuntimeError('Expected editable seat box mesh: ' + obj.name)
    size = max(v.co.y for v in obj.data.vertices) - min(v.co.y for v in obj.data.vertices)
    factor = depth / size
    for vertex in obj.data.vertices:vertex.co.y *= factor
    for modifier in obj.modifiers:
        if modifier.type == 'BEVEL':modifier.width = min(modifier.width, depth * .45)
    obj.data.update()


def _paint(obj, material):
    obj.data.materials.clear(); obj.data.materials.append(material)
    if obj.type == 'MESH':
        for face in obj.data.polygons:face.material_index = 0


def patch_saved_interior(master, reader_json, *, coordinate_space='blender_source'):
    if coordinate_space not in {'blender_source', 'canonical_preparation'}:
        raise ValueError('Declare the saved source coordinate space')
    objects = {obj.name: obj for obj in _collection_objects(master)}
    rear_shells, grips, removed, removed_shells = [], [], [], []
    witness_updates = []
    seat_names = ['Driver seat'] + [f'Passenger seat {i:02d}' for i in range(1, 32)
                                  if i not in REMOVED_SEAT_NUMBERS]
    offsets = seat_grab_offsets()
    fabric, hard_shell, grab_material = seat_materials()
    for name in seat_names:
        back = objects.get(name + ' backrest')
        shell = objects.get(name + ' rear shell')
        cushion = objects.get(name + ' cushion')
        if back is None or cushion is None:
            raise RuntimeError('Incomplete saved seat assembly: ' + name)
        if name != 'Driver seat':
            _resize_depth(back, .060)
            _paint(back, fabric); _paint(cushion, fabric)
        if name != 'Driver seat' and cushion.matrix_world.translation.y > 4.65:
            if shell is not None:
                removed_shells.append(shell.name)
                bpy.data.objects.remove(shell, do_unlink=True)
            grip = objects.get(name + ' upper grab')
            if grip is not None:
                removed.append(grip.name)
                bpy.data.objects.remove(grip, do_unlink=True)
            continue
        if shell is None:
            raise RuntimeError('Missing saved seat rear shell: ' + name)
        offset = .066 if name == 'Driver seat' else .0385
        old_witness = list(shell.get('cdlc_source_canonical_matrix_at_conversion', []))
        old_world = _flat(shell.matrix_world)
        if name != 'Driver seat':
            _resize_depth(shell, .018)
            _paint(shell, hard_shell)
        shell.matrix_world = back.matrix_world @ Matrix.Translation((0., offset, 0.))
        shell['seat_shell_revision'] = 2
        if source_is_corrected(shell):
            new_witness = _flat(canonical_world_matrix(shell))
            shell['cdlc_source_canonical_matrix_at_conversion'] = new_witness
            witness_updates.append({'object':shell.name,'old_witness':old_witness,
                                    'old_world_matrix':old_world,'new_witness':new_witness,
                                    'reason':'Intentional rear-shell alignment with the existing backrest tilt'})
        rear_shells.append(shell.name)
        if name == 'Driver seat':
            continue
        grip = objects.get(name + ' upper grab')
        if grip is None or grip.type != 'CURVE':
            raise RuntimeError('Expected editable shared seat grip curve: ' + name)
        inverse = grip.matrix_world.inverted()
        world = [cushion.matrix_world @ Vector((x, y, z - .355)) for x, y, z in offsets]
        grip.data.splines.clear()
        spline = grip.data.splines.new('POLY')
        spline.points.add(len(world) - 1)
        for target, position in zip(spline.points, world):
            target.co = (*tuple(inverse @ position), 1.)
        grip.data.use_fill_caps = True
        grip.data.bevel_depth = .016
        _paint(grip, grab_material)
        grip['seat_grip_revision'] = 2
        grips.append(grip.name)

    mirrors = []
    for obj in _collection_objects(master):
        if obj.type in {'MESH', 'CURVE', 'FONT'} and obj.get('mirror_assembly'):
            obj['preserve_lod'] = True
            obj['cdlc_preserve_section_lod'] = True
            mirrors.append(obj.name)
    reader = replace_common_card_reader(master, Path(reader_json), coordinate_space=coordinate_space)
    reader_object = next(obj for obj in _collection_objects(master) if obj.name == reader['object'])
    if source_is_corrected(reader_object):
        reader_object['cdlc_source_canonical_matrix_at_conversion'] = _flat(canonical_world_matrix(reader_object))
        reader_object['cdlc_coordinate_note'] = 'New shared-reader source copy; physical Blender coordinates with an explicit canonical transform witness.'
        witness_updates.append({'object':reader_object.name,'old_witness':None,
                                'new_witness':list(reader_object['cdlc_source_canonical_matrix_at_conversion']),
                                'reason':'New shared reader replacing the old source copy'})
    from seat_geometry_photo import apply_photo_seat_geometry
    photo_seats = apply_photo_seat_geometry(master)
    seat_access = apply_saved_seat_access(master)
    bpy.context.view_layer.update()
    return {'status': 'PASS', 'scope': 'Explicit in-memory seat rear-shell/grip, mirror LOD metadata and shared CardReader repairs; caller saves and re-exports',
            'coordinate_space': coordinate_space, 'rear_shells_aligned': rear_shells,
            'passenger_grips_repositioned': grips, 'rear_bench_grips_removed': removed,
            'rear_bench_rear_shells_removed': removed_shells,
            'passenger_rear_shell_normal_offset_m': .0385, 'grip_upper_bend_radius_m': .055,
            'grip_cross_section_radius_m': .016, 'photo_seat_geometry': photo_seats,
            'seat_access':seat_access,
            'reference': 'references/user-reference-20261015-01.jpg',
            'reference_status': 'Photo-estimated 44 cm outer grip width, 32 mm tube, 55 mm corners; opaque dark-grey hard backs, not inferred transparent.',
            'pattern_status': 'Yellow and pale-blue irregular printed fabric pattern is visible in the reference but is not implemented in this geometry/color patch.',
            'mirrors_preserve_lod': mirrors, 'shared_reader': reader,
            'matrix_witness_updates':witness_updates,
            'witness_scope':'Only intentionally moved rear shells and the newly replaced shared reader are updated; unrelated object witnesses remain untouched.'}
