"""Export K18 section drafts from the saved, physically LHD Blender source.

Blender --background --python-exit-code 1 --python Tools/CRRC_K18/export_model.py
Optional arguments after --: --blend PATH --frame N --runtime-only --summary PATH.

This tool writes both section meshes. The independent joint is exported by
export_articulation.py and the explicit v2 profile by build_profile.py. Native
packaging and game validation are separate steps; this exporter certifies neither.
The source .blend is opened read-only and is never saved by this script.
"""
import argparse
import gzip
import hashlib
import io
import json
import math
from pathlib import Path
import shutil
import struct
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'ModelSource' / 'CRRC_K18'
PALETTE = ROOT / 'Tools' / 'CRRC_K18' / 'palette.json'
PART_NAMES = (
    'Shell', 'SteeringWheel', 'SpeedNeedle', 'RpmNeedle',
    'Door0A', 'Door0B', 'Door1A', 'Door1B', 'Door2A', 'Door2B',
    'Fittings', 'Dash', 'Cab', 'Poles', 'Ceiling', 'Outside',
    'Exterior', 'Glazing', 'Livery', 'WheelFL', 'WheelFR', 'WheelRL', 'WheelRR',
    'HeadLamps', 'TailLamps', 'BrakeLamps', 'IndicatorL', 'IndicatorR',
    'ReverseLamps', 'WrapLeft', 'WrapRight', 'WrapFront', 'WrapRear', 'WrapRoof',
)
WHEEL_PARTS = {19: 'WheelFL', 20: 'WheelFR', 21: 'WheelRL', 22: 'WheelRR'}
MOVING_PARTS = set(range(1, 10)) | set(WHEEL_PARTS)
GEOMETRY = {'MESH', 'CURVE', 'FONT', 'SURFACE'}
# Physical source: +X driver side, -Y forward, +Z up. Unity: -X driver side.
B_TO_U = Matrix(((-1., 0., 0.), (0., 0., 1.), (0., -1., 0.)))
# Historical native-export preparation frame, metres: +X door side, -Y forward.
U_TO_C = Matrix(((1., 0., 0.), (0., 0., -1.), (0., 1., 0.)))
SECTIONS = {
    'front': ('K18 Front', 'section_front', 'CRRCK18'),
    'rear': ('K18 Rear', 'section_rear', 'CRRCK18Rear'),
}
MIN_CROSS_SQUARED = 1e-18


def sha256(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def plain(value):
    if hasattr(value, 'to_list'):
        return value.to_list()
    if hasattr(value, 'to_dict'):
        return {str(k): plain(v) for k, v in value.to_dict().items()}
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    return [plain(v) for v in value]


def inherited(obj, key, default=None):
    current = obj
    while current is not None:
        if key in current:
            return current[key]
        current = current.parent
    return default


def vector(value, count, what):
    result = tuple(float(x) for x in value)
    if len(result) != count or not all(math.isfinite(x) for x in result):
        raise ValueError(what + ' must contain %d finite numbers' % count)
    return result


def integer(value, what, minimum, maximum):
    if isinstance(value, bool) or int(value) != value or not minimum <= int(value) <= maximum:
        raise ValueError('%s must be an integer in %d..%d' % (what, minimum, maximum))
    return int(value)


def json_write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def artifact(path):
    return {'path': str(Path(path).resolve()), 'sha256': sha256(path), 'bytes': Path(path).stat().st_size}


def runtime_swatch(material, palette):
    """Preserve the existing livery/lamp semantics before explicit paint swatches."""
    light = integer(material.get('light_id', 0), material.name + ' light_id', 0, 9)
    if material.get('livery_control'):
        name = 'White'
    elif light:
        name = {1: 'LampWhite', 2: 'LampWhite', 3: 'LampRed', 4: 'LampRed',
                5: 'Amber', 6: 'Amber', 7: 'LampWhite', 8: 'LampWhite', 9: 'LedAmber'}[light]
    else:
        name = material.get('cdlc_swatch')
    if name not in palette:
        raise ValueError(material.name + ': unknown runtime swatch ' + str(name))
    return palette[name]


def snapshot(objects):
    """Small source-state witness; file SHA also guards every saved property."""
    rows = []
    for obj in sorted(objects, key=lambda x: x.name):
        row = {'name': obj.name, 'type': obj.type,
               'parent': obj.parent.name if obj.parent else None,
               'matrix': [float(v) for r in obj.matrix_world for v in r],
               'properties': {k: plain(obj[k]) for k in sorted(obj.keys())}}
        if obj.type in GEOMETRY:
            row['materials'] = [m.name if m else None for m in obj.data.materials]
        rows.append(row)
    return hashlib.sha256(json.dumps(rows, sort_keys=True).encode()).hexdigest()


def part_frames(section, objects, origin):
    pivots = {}
    used = set()
    for obj in objects:
        if inherited(obj, 'cab_section', section) != section:
            raise ValueError(obj.name + ': cab_section disagrees with source collection')
        value = inherited(obj, 'cdlc_part')
        if obj.type in GEOMETRY:
            if value is None:
                raise ValueError(obj.name + ': missing cdlc_part')
            used.add(integer(value, obj.name + ' cdlc_part', 0, 255))
        if obj.type == 'EMPTY' and 'rest_pivot_unity' in obj:
            if 'cdlc_part' not in obj:
                raise ValueError(obj.name + ': part pivot has no cdlc_part')
            index = integer(obj['cdlc_part'], obj.name + ' cdlc_part', 0, 255)
            if index in pivots:
                raise ValueError('Duplicate part pivot %s/%s' % (section, index))
            pivots[index] = obj
    count = max(34, max(used | set(pivots), default=0) + 1)
    missing = ((used & MOVING_PARTS) | set(range(34, count))) - set(pivots)
    if missing:
        raise ValueError('%s missing moving-part pivots: %s' % (section, sorted(missing)))
    frames = []
    for index in range(count):
        obj = pivots.get(index)
        pivot = vector(obj['rest_pivot_unity'], 3, obj.name) if obj else (0., 0., 0.)
        rotation = vector(obj.get('rest_rotation_unity', (0., 0., 0., 1.)), 4, obj.name) if obj else (0., 0., 0., 1.)
        q = Quaternion((rotation[3], *rotation[:3]))
        input_norm = q.magnitude
        if abs(input_norm - 1.) > 1e-5:
            raise ValueError('%s/%d has a non-unit rest quaternion' % (section, index))
        q.normalize()
        rotation = (q.x, q.y, q.z, q.w)
        if index >= 34 and (max(abs(v) for v in rotation[:3]) > 1e-6 or abs(abs(rotation[3])-1.) > 1e-6):
            raise ValueError('SWB1 straps require body-aligned rest rotation')
        if obj:
            actual = B_TO_U @ obj.matrix_world.translation - origin
            if (actual - Vector(pivot)).length > .0001:
                raise ValueError('%s pivot is not at declared closed-pose location (%.6fm)' % (obj.name, (actual-Vector(pivot)).length))
        frames.append({'index': index, 'name': PART_NAMES[index] if index < 34 else 'Strap%d' % (index-34),
                       'parent': -1 if index == 0 else 0, 'flags': 0,
                       'pivot_unity': pivot, 'rest_rotation_unity': rotation,
                       'pivot_object': obj.name if obj else None,
                       'input_quaternion_norm': input_norm,
                       'rotation_source': 'explicit' if obj and 'rest_rotation_unity' in obj else 'identity default',
                       'objects': []})
    return frames


def eye_point(section, root, objects, origin):
    if 'driver_eye_unity' in root:
        return vector(root['driver_eye_unity'], 3, root.name + ' driver_eye_unity')
    candidates = [o for o in objects if o.get('driver_eye') or o.name.lower() in ('driver_eye', 'k18 driver eye')]
    if section == 'front':
        if len(candidates) != 1:
            raise ValueError('Front section requires root driver_eye_unity or one driver_eye marker')
        return tuple(B_TO_U @ candidates[0].matrix_world.translation - origin)
    return (0., 0., 0.)  # Rear header is not a usable driving viewpoint.


def read_object(obj, section, origin, palette):
    """Evaluate once, retaining source corner normals and all valid small faces."""
    if any(m.type == 'DECIMATE' and (m.show_viewport or m.show_render) for m in obj.modifiers):
        raise ValueError(obj.name + ': source Decimate conflicts with whole-object LOD contract')
    part = integer(inherited(obj, 'cdlc_part'), obj.name + ' cdlc_part', 0, 255)
    first_lod = integer(inherited(obj, 'first_lod', 0), obj.name + ' first_lod', 0, 2)
    last_lod = integer(inherited(obj, 'last_lod', 2), obj.name + ' last_lod', 0, 2)
    if first_lod > last_lod:
        raise ValueError(obj.name + ': first_lod must not exceed last_lod')
    bone = str(inherited(obj, 'bone', 'Root'))
    if bone not in ('Root', *WHEEL_PARTS.values()):
        raise ValueError(obj.name + ': unrecognized native bone ' + bone)
    if part in WHEEL_PARTS and bone != WHEEL_PARTS[part]:
        raise ValueError(obj.name + ': wheel cdlc_part/bone mismatch')
    if section == 'rear' and (part in (19, 20) or bone in ('WheelFL', 'WheelFR')):
        raise ValueError('Rear section must not contain a fictitious front axle: ' + obj.name)
    deps = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(deps)
    data = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=deps)
    try:
        data.calc_loop_triangles()
        transform = B_TO_U @ obj.matrix_world.to_3x3()
        normals = transform.inverted().transposed()
        translation = B_TO_U @ obj.matrix_world.translation - origin
        vertices = [tuple(transform @ v.co + translation) for v in data.vertices]
        if not all(math.isfinite(x) and abs(x) <= 100 for p in vertices for x in p):
            raise ValueError(obj.name + ': non-finite/out-of-range source coordinates')
        triangles = []
        rejected = 0
        bad_normals = 0
        materials = set()
        # Reflection changes winding. Reverse both indices and their normals.
        order = (0, 2, 1) if transform.determinant() < 0 else (0, 1, 2)
        for tri in data.loop_triangles:
            indices = tuple(tri.vertices[i] for i in order)
            p = [Vector(vertices[i]) for i in indices]
            face = (p[1]-p[0]).cross(p[2]-p[0])
            if face.length_squared < MIN_CROSS_SQUARED:
                rejected += 1
                continue
            face.normalize()
            if tri.material_index >= len(data.materials) or data.materials[tri.material_index] is None:
                raise ValueError(obj.name + ': unassigned material')
            mat = data.materials[tri.material_index]
            materials.add(mat.name)
            if not mat.get('glass') and mat.get('cdlc_swatch') not in palette:
                raise ValueError('%s: unknown/missing cdlc_swatch on %s: %r' % (obj.name, mat.name, mat.get('cdlc_swatch')))
            cn = []
            for i in order:
                n = normals @ data.corner_normals[tri.loops[i]].vector
                if n.length_squared < 1e-12 or not all(math.isfinite(x) for x in n):
                    raise ValueError(obj.name + ': invalid source corner normal')
                cn.append(tuple(n.normalized()))
            if sum((Vector(n) for n in cn), Vector((0., 0., 0.))).dot(face) <= 0:
                bad_normals += 1
            triangles.append((indices, tuple(cn), mat.name))
        if bad_normals:
            raise ValueError('%s: %d triangles have opposed source corner-normal means' % (obj.name, bad_normals))
        if rejected:
            raise ValueError('%s: %d genuinely degenerate source triangles; repair source rather than silently dropping faces' % (obj.name, rejected))
        return {'name': obj.name, 'part': part, 'bone': bone, 'first_lod': first_lod, 'last_lod': last_lod,
                'runtime_generated': str(inherited(obj, 'runtime_generated', '')),
                'vertices': vertices, 'triangles': triangles, 'materials': sorted(materials)}
    finally:
        evaluated.to_mesh_clear()


def runtime_export(path, section, eye, frames, objects, palette):
    output = io.BytesIO()
    output.write(b'SWB1')
    output.write(struct.pack('<ii3f', 3, len(frames), *eye))
    for f in frames:
        output.write(struct.pack('<ii7f', f['parent'], f['flags'], *f['pivot_unity'], *f['rest_rotation_unity']))
    lods = []
    excluded = set()
    for lod in range(3):
        blobs = [bytearray() for _ in frames]
        counts = [0] * len(frames)
        names = []
        for obj in objects:
            if not obj['first_lod'] <= lod <= obj['last_lod']:
                continue
            if obj['runtime_generated']:
                if obj['runtime_generated'] not in ('CardReader', 'LedSign'):
                    raise ValueError(obj['name'] + ': unknown runtime_generated exclusion')
                excluded.add(obj['name'])
                continue
            part = obj['part']
            frame = frames[part]
            inv = Quaternion((frame['rest_rotation_unity'][3], *frame['rest_rotation_unity'][:3])).inverted()
            accepted = 0
            for indices, cn, material in obj['triangles']:
                mat = bpy.data.materials[material]
                if mat.get('glass'):
                    continue
                swatch = runtime_swatch(mat, palette)['index']
                values = []
                for vi, n in zip(indices, cn):
                    p = Vector(obj['vertices'][vi])
                    normal = Vector(n)
                    if part >= 34:
                        p = inv @ (p - Vector(frame['pivot_unity']))
                        normal = inv @ normal
                    values.extend((*p, *normal))
                blobs[part].extend(struct.pack('<B18f', swatch, *values))
                counts[part] += 1
                accepted += 1
            if accepted:
                names.append(obj['name'])
        for count, blob in zip(counts, blobs):
            output.write(struct.pack('<i', count))
            output.write(blob)
        lods.append({'lod': lod, 'triangles': sum(counts), 'per_part': counts, 'included_objects': names})
        print('K18_SWB1_LOD', section, lod, sum(counts), flush=True)
    total = sum(x['triangles'] for x in lods)
    if not 0 < total <= 2000000:
        raise ValueError('SWB1 total triangle count outside runtime reader limit')
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(gzip.compress(output.getvalue(), compresslevel=6, mtime=0))
    # Independently walk the serialized stream, checking strides/counts/EOF.
    raw = gzip.decompress(path.read_bytes())
    offset = 24 + 36 * len(frames)
    for row in lods:
        for count in row['per_part']:
            actual, = struct.unpack_from('<i', raw, offset)
            if actual != count:
                raise AssertionError('SWB1 count mismatch')
            offset += 4 + 73 * count
    if offset != len(raw):
        raise AssertionError('SWB1 trailing or missing bytes')
    return {**artifact(path), 'format': 'SWB1', 'section': section, 'status': 'EXPORTED_DRAFT',
            'parts': len(frames), 'straps': len(frames)-34, 'frames': frames, 'eye': eye,
            'rear_eye_is_unused_placeholder': section == 'rear', 'lods': lods,
            'runtime_generated_excluded': sorted(excluded), 'glass_excluded': True}


def write_atlas(directory, name, material_names):
    """Same padded 16x16 tile and CSL2 channel convention as the audited bus."""
    import numpy as np
    mats = [bpy.data.materials[n] for n in material_names]
    if not mats or len(mats) > 256:
        raise ValueError('Native atlas needs 1..256 materials')
    is_glass = all(m.get('glass') for m in mats)
    if any(m.get('glass') for m in mats) != is_glass:
        raise ValueError('Glass and opaque materials must use separate native meshes')
    arrays = {k: np.zeros((64, 64, 4), dtype=np.float32) for k in
              ('BaseColor', 'MaskMap', 'Normal', 'ControlMask', 'Emissive0', 'EmissiveID0')}
    arrays['BaseColor'][:] = (.01, .01, .01, 1.)
    arrays['Normal'][:] = (.5, .5, 1., 1.)
    arrays['MaskMap'][:] = (0., 1. if is_glass else 0., 0., .3)
    for key in ('ControlMask', 'Emissive0', 'EmissiveID0'):
        arrays[key][:, :, 3] = 1.
    def srgb(v):
        return 12.92*v if v <= .0031308 else 1.055*v**(1./2.4)-.055
    routes = []
    for index, mat in enumerate(mats):
        shader = next((n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None) if mat.use_nodes and mat.node_tree else None
        metallic = float(shader.inputs['Metallic'].default_value) if shader else mat.metallic
        roughness = float(shader.inputs['Roughness'].default_value) if shader else mat.roughness
        coat = float(shader.inputs['Coat Weight'].default_value) if shader else 0.
        rgb = tuple(srgb(float(v)) for v in mat.diffuse_color[:3])
        alpha = float(mat.get('glass_alpha', .28)) if is_glass else 1.
        if not 0 <= alpha <= 1:
            raise ValueError(mat.name + ': invalid glass alpha')
        row, col = divmod(index, 16)
        tile = (slice(row*4, (row+1)*4), slice(col*4, (col+1)*4))
        livery = bool(mat.get('livery_control'))
        arrays['BaseColor'][tile] = (*((1., 1., 1.) if livery else rgb), alpha)
        arrays['MaskMap'][tile] = (metallic, 1. if is_glass else coat, 0., 1.-roughness)
        if livery:
            arrays['ControlMask'][tile] = (1., 0., 0., 1.)
        light = integer(mat.get('light_id', 0), mat.name + ' light_id', 0, 9)
        if light:
            arrays['Emissive0'][tile] = (*rgb, 1.)
            arrays['EmissiveID0'][tile] = (light/255., 0., 0., 1.)
        routes.append({'material': mat.name, 'tile': index, 'cdlc_swatch': mat.get('cdlc_swatch'),
                       'source_linear_rgb': list(mat.diffuse_color[:3]), 'alpha': alpha,
                       'metallic': metallic, 'roughness': roughness, 'light_id': light,
                       'livery_control': livery})
    files = []
    for key, pixels in arrays.items():
        if is_glass and key in ('ControlMask', 'Emissive0', 'EmissiveID0'):
            continue
        image = bpy.data.images.new('__K18_export_' + name + '_' + key, 64, 64, alpha=True)
        try:
            image.colorspace_settings.name = 'sRGB' if key in ('BaseColor', 'Emissive0') else 'Non-Color'
            image.pixels.foreach_set(pixels.ravel())
            path = directory / (name + '_' + key + '.png')
            image.filepath_raw = str(path)
            image.file_format = 'PNG'
            image.save()
            files.append(artifact(path))
        finally:
            bpy.data.images.remove(image)
    return files, routes


def native_export(directory, name, objects, frames, lod, glass, material_names, section):
    """Build temporary exact-named mesh/rig; never alter source objects.

    Algebraically the established pipeline's canonical-X reflection, 100x
    scale and winding reversal reduce to physical Blender section coordinates
    in centimetres. Explicit loop normals follow that same transform; they
    are set after topology, avoiding recalculation at the export-copy step.
    """
    verts, faces, corner_normals, uvs = [], [], [], []
    groups = {'Root': []}
    mat_indices = {n: i for i, n in enumerate(material_names)}
    included = []
    for obj in objects:
        if not obj['first_lod'] <= lod <= obj['last_lod']:
            continue
        accepted = [t for t in obj['triangles'] if bool(bpy.data.materials[t[2]].get('glass')) == glass]
        if not accepted:
            continue
        included.append(obj['name'])
        base = len(verts)
        used_vertices = sorted({vi for indices, _, _ in accepted for vi in indices})
        remap = {vi: base+i for i, vi in enumerate(used_vertices)}
        # U -> canonical B -> reflected centimetre export copy.
        for vi in used_vertices:
            p = obj['vertices'][vi]
            c = U_TO_C @ Vector(p)
            verts.append((-100.*c.x, 100.*c.y, 100.*c.z))
        groups.setdefault(obj['bone'], []).extend(range(base, len(verts)))
        for indices, cn, mat in accepted:
            # Second reflection reverses winding a second time.
            order = (0, 2, 1)
            faces.append(tuple(remap[indices[i]] for i in order))
            for i in order:
                c = U_TO_C @ Vector(cn[i])
                corner_normals.append((-c.x, c.y, c.z))
            row, col = divmod(mat_indices[mat], 16)
            centre = ((col+.5)/16., (row+.5)/16.)
            uvs.extend((centre[0]+x, centre[1]+y) for x, y in ((-.018, -.018), (.018, -.018), (0., .018)))
    if not faces:
        raise ValueError(name + ': empty native mesh')
    if bpy.data.objects.get(name) or bpy.data.meshes.get(name):
        raise ValueError('Exact FBX object/mesh name already occupied: ' + name)
    if section not in ('front', 'rear'):
        raise ValueError('Explicit section required for native axle contract')
    expected = ('WheelFL', 'WheelFR', 'WheelRL', 'WheelRR') if section == 'front' else ('WheelRL', 'WheelRR')
    bones = {'Root': (0., 0., 0.)}
    for part, bn in WHEEL_PARTS.items():
        if bn in expected:
            f = frames[part]
            if not f['pivot_object']:
                raise ValueError(name + ': missing actual wheel pivot ' + bn)
            c = U_TO_C @ Vector(f['pivot_unity'])
            bones[bn] = (-100.*c.x, 100.*c.y, 100.*c.z)
    if not glass and any(not groups.get(bn) for bn in expected):
        raise ValueError(name + ': one or more real wheel bones has no body geometry')
    old_scale = bpy.context.scene.unit_settings.scale_length
    selected = list(bpy.context.selected_objects)
    active = bpy.context.view_layer.objects.active
    data = obj = armdata = arm = None
    try:
        bpy.ops.object.select_all(action='DESELECT')
        data = bpy.data.meshes.new(name)
        data.from_pydata(verts, [], faces)
        data.update()
        # One native Surface owns the atlas. Per-triangle materials are encoded
        # by UV tiles rather than extra FBX material/submesh assignments.
        data.materials.append(bpy.data.materials[material_names[0]])
        obj = bpy.data.objects.new(name, data)
        bpy.context.scene.collection.objects.link(obj)
        if obj.name != name or data.name != name:
            raise ValueError('Blender changed required FBX name ' + name)
        uv = data.uv_layers.new(name='AtlasUV')
        for loop, coord in zip(uv.data, uvs):
            loop.uv = coord
        for poly in data.polygons:
            poly.use_smooth = True
        data.normals_split_custom_set(corner_normals)
        for bn, indices in groups.items():
            if bn not in bones:
                raise ValueError(name + ': geometry assigned to absent bone ' + bn)
            group = obj.vertex_groups.new(name=bn)
            if indices:
                group.add(indices, 1., 'REPLACE')
        armdata = bpy.data.armatures.new(name + ' Rig')
        arm = bpy.data.objects.new(name + ' Rig', armdata)
        bpy.context.scene.collection.objects.link(arm)
        arm.select_set(True)
        bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode='EDIT')
        for bn, head in bones.items():
            b = armdata.edit_bones.new(bn)
            b.head = head
            b.tail = Vector(head) + Vector((0., 0., 30.))
            b.roll = 0.
            if bn != 'Root':
                b.parent = armdata.edit_bones['Root']
        bpy.ops.object.mode_set(mode='OBJECT')
        modifier = obj.modifiers.new('Wheel deformation', 'ARMATURE')
        modifier.object = arm
        obj.select_set(True)
        bpy.context.scene.unit_settings.scale_length = .01
        path = directory / (name + '.fbx')
        result = bpy.ops.export_scene.fbx(
            filepath=str(path), use_selection=True, object_types={'MESH', 'ARMATURE'}, add_leaf_bones=False,
            axis_forward='-Z', axis_up='Y', apply_scale_options='FBX_SCALE_ALL', apply_unit_scale=True,
            bake_space_transform=True, path_mode='STRIP', mesh_smooth_type='FACE',
            use_armature_deform_only=False, primary_bone_axis='Y', secondary_bone_axis='X', bake_anim=False)
        if 'FINISHED' not in result:
            raise RuntimeError(name + ': FBX export did not finish')
        return {**artifact(path), 'lod': lod, 'glass': glass, 'triangles': len(faces),
                'mesh_name': name, 'bones': list(bones), 'wheel_bones_have_geometry': not glass,
                'included_objects': included}
    finally:
        bpy.context.scene.unit_settings.scale_length = old_scale
        if arm is not None and arm.mode != 'OBJECT':
            bpy.context.view_layer.objects.active = arm
            bpy.ops.object.mode_set(mode='OBJECT')
        for item in (obj, arm):
            if item is not None:
                bpy.data.objects.remove(item, do_unlink=True)
        if data is not None:
            bpy.data.meshes.remove(data)
        if armdata is not None:
            bpy.data.armatures.remove(armdata)
        for item in selected:
            item.select_set(True)
        bpy.context.view_layer.objects.active = active


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend', type=Path, default=SOURCE / 'CRRC_K18.blend')
    parser.add_argument('--frame', type=int, help='Closed/straight rest frame; defaults to scene export_rest_frame or frame_start')
    parser.add_argument('--runtime-only', action='store_true')
    parser.add_argument('--summary', type=Path, default=SOURCE / 'export-summary.json')
    args = parser.parse_args(argv)
    before_file = sha256(args.blend)
    palette_document = json.loads(PALETTE.read_text(encoding='utf-8'))
    rows = palette_document['palette']
    if [r['index'] for r in rows] != list(range(len(rows))) or not 0 < len(rows) <= 255:
        raise ValueError('Palette indices must be contiguous bytes')
    palette = {r['name']: r for r in rows}
    if len(palette) != len(rows):
        raise ValueError('Duplicate palette names')
    bpy.ops.wm.open_mainfile(filepath=str(args.blend.resolve()))
    rest_frame = args.frame if args.frame is not None else int(bpy.context.scene.get('export_rest_frame', bpy.context.scene.frame_start))
    bpy.context.scene.frame_set(rest_frame)
    bpy.context.view_layer.update()
    joint = bpy.data.collections.get('K18 Joint')
    all_objects = set(joint.all_objects) if joint else set()
    section_sources = {}
    for section, (collection_name, root_name, model) in SECTIONS.items():
        collection = bpy.data.collections.get(collection_name)
        root = bpy.data.objects.get(root_name)
        if collection is None or root is None:
            raise ValueError('Missing ' + collection_name + ' / ' + root_name)
        objects = list(collection.all_objects)
        all_objects.update(objects)
        all_objects.add(root)
        origin = B_TO_U @ root.matrix_world.translation
        if (section == 'front' and origin.length > 1e-5) or abs(origin.x) > 1e-5 or abs(origin.y) > 1e-5 or any(abs(root.matrix_world[r][c]-(1. if r == c else 0.)) > 1e-5 for r in range(3) for c in range(3)):
            raise ValueError(root.name + ': unexpected section origin/orientation/scale')
        section_sources[section] = (root, objects, origin, model)
    before_state = snapshot(all_objects)
    SOURCE.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(PALETTE, SOURCE / 'kit-cabin.json')
    report = {'schema_version': 1, 'model': 'CRRC_K18', 'status': 'EXPORT_IN_PROGRESS',
              'source': str(args.blend.resolve()), 'source_sha256': before_file,
              'exporter_sha256': sha256(Path(__file__)), 'palette': artifact(SOURCE/'kit-cabin.json'),
              'coordinate_mapping': 'Unity=(-Blender.x, Blender.z, -Blender.y); subtract section world origin',
              'source_frame': rest_frame, 'lod_policy': 'whole-object first_lod <= LOD <= last_lod; defaults 0..2; no Decimate',
              'runtime_profile_supported': True, 'required_profile_version': 2,
              'game_packaged': False, 'game_validated': False,
              'limitations': ['Section meshes require the explicit v2 assembly profile and generic BusDriver support.',
                              'Run export_articulation.py and build_profile.py separately before packaging.',
                              'No native reader round-trip or game validation has been run by this exporter.'],
              'joint_excluded': sorted(o.name for o in joint.all_objects if o.type in GEOMETRY) if joint else [],
              'sections': {}}
    try:
        for section, (root, source_objects, origin, model) in section_sources.items():
            frames = part_frames(section, source_objects, origin)
            objects = [read_object(o, section, origin, palette) for o in sorted(source_objects, key=lambda x: x.name) if o.type in GEOMETRY]
            for obj in objects:
                frames[obj['part']]['objects'].append(obj['name'])
            eye = eye_point(section, root, source_objects, origin)
            directory = ROOT / 'Asset' / model
            directory.mkdir(parents=True, exist_ok=True)
            section_report = {'model': model, 'origin_unity': list(origin), 'objects': len(objects),
                              'runtime': runtime_export(SOURCE/'runtime-sections'/(model+'.swb1.gz'), section, eye, frames, objects, palette),
                              'native': [], 'atlases': []}
            section_report['runtime_material_routes'] = [
                {'material': name, 'declared_swatch': bpy.data.materials[name].get('cdlc_swatch'),
                 'runtime_swatch': runtime_swatch(bpy.data.materials[name], palette)['name'],
                 'runtime_index': runtime_swatch(bpy.data.materials[name], palette)['index']}
                for name in sorted({m for o in objects for m in o['materials']})
                if not bpy.data.materials[name].get('glass')]
            report['sections'][section] = section_report
            if not args.runtime_only:
                for glass in (False, True):
                    names = sorted({m for o in objects for m in o['materials'] if bool(bpy.data.materials[m].get('glass')) == glass})
                    for lod in range(3):
                        name = model + ('_LOD%d' % lod if lod else '') + ('_Gls' if glass else '')
                        native = native_export(directory, name, objects, frames, lod, glass, names, section)
                        files, routes = write_atlas(directory, name, names)
                        section_report['native'].append(native)
                        section_report['atlases'].append({'name': name, 'materials': routes, 'files': files})
                        print('K18_NATIVE_EXPORTED', name, native['triangles'], flush=True)
        report['source_state_sha256_before'] = before_state
        report['source_state_sha256_after'] = snapshot(all_objects)
        report['source_file_unchanged'] = sha256(args.blend) == before_file
        if report['source_state_sha256_after'] != before_state or not report['source_file_unchanged']:
            raise RuntimeError('Export changed the source witness')
        report['status'] = 'EXPORTED_DRAFT'
    except Exception as error:
        report['status'] = 'FAILED'
        report['error'] = repr(error)
        raise
    finally:
        json_write(args.summary, report)
    print('K18_EXPORT_OK', str(args.summary), flush=True)


if __name__ == '__main__':
    main(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
