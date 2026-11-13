"""Import the mod's shared CardReader into the editable vehicle source.

The default input frame is the generator's canonical preparation frame. This
helper also accepts a saved physical Blender source, with an explicit space.
Original shared faces are retained. Only the open UI sheets receive a rear face
and thin side walls for a closed source/FBX mesh; runtime uses CardReader itself.
"""
from collections import defaultdict
import hashlib
import json
from pathlib import Path

import bpy
import bmesh
from mathutils import Matrix, Quaternion, Vector


def _linear(value):
    return value / 12.92 if value <= .04045 else ((value + .055) / 1.055) ** 2.4


def _collection_objects(collection):
    # Avoid Blender's cached all_objects iterator while deleting/replacing IDs.
    result = list(collection.objects)
    for child in collection.children:
        result.extend(_collection_objects(child))
    return list(dict.fromkeys(result))


def _material(entry):
    name = 'Shared CardReader | ' + entry['name']
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    color = tuple(_linear(float(v)) for v in entry['rgb'])
    material.diffuse_color = (*color, 1.)
    material.use_nodes = True
    shader = next((node for node in material.node_tree.nodes if node.type == 'BSDF_PRINCIPLED'), None)
    if shader is None:
        shader = material.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
    output = next((node for node in material.node_tree.nodes if node.type == 'OUTPUT_MATERIAL'), None)
    if output is None:
        output = material.node_tree.nodes.new('ShaderNodeOutputMaterial')
    if not output.inputs['Surface'].is_linked:
        material.node_tree.links.new(shader.outputs['BSDF'], output.inputs['Surface'])
    shader.inputs['Base Color'].default_value = (*color, 1.)
    shader.inputs['Roughness'].default_value = float(entry.get('roughness', .5))
    shader.inputs['Metallic'].default_value = float(entry.get('metallic', 0.))
    material['light_id'] = 0
    material['livery_control'] = False
    material['glass'] = False
    material['source_palette_index'] = int(entry['index'])
    material['cdlc_palette_swatch'] = entry['name']
    return material


def replace_common_card_reader(master, json_path, *, coordinate_space='canonical_preparation'):
    """Replace the old fare-reader boxes; return a JSON-serializable audit.

    master is the editable collection. json_path is KitExport --reader-only's
    output. The support post is retained and its attachment is checked.
    """
    path = Path(json_path).resolve()
    payload = json.loads(path.read_text(encoding='utf-8'))
    if payload.get('source') != 'BusInterior.CardReader(MeshKit,int)':
        raise ValueError('Expected the shared CardReader export, not a cabin or approximate reader mesh')
    if coordinate_space not in {'canonical_preparation', 'blender_source'}:
        raise ValueError('Declare canonical_preparation or blender_source explicitly')
    sign = 1. if coordinate_space == 'canonical_preparation' else -1.

    def point(p):
        return Vector((sign * float(p[0]), -float(p[2]), float(p[1])))

    lod = next(row for row in payload['lods'] if row['lod'] == 0)
    parts = lod['parts']
    if len(parts) != 1:
        raise ValueError('Expected one combined shared reader export part')
    part = parts[0]
    source_positions = [point(p) for p in part['positions']]
    source_normals = [point(n).normalized() for n in part['normals']]
    source_uvs = [tuple(v) for v in part['uv']]
    palette = sorted(payload['palette'], key=lambda row: row['index'])
    palette_size = len(palette)
    vertices = []
    weld = {}
    remap = []
    max_weld_error = 0.
    for position in source_positions:
        key = tuple(round(value * 1e7) for value in position)
        index = weld.get(key)
        if index is None:
            index = len(vertices)
            weld[key] = index
            vertices.append(position)
        else:
            max_weld_error = max(max_weld_error, (vertices[index] - position).length)
        remap.append(index)

    faces, materials, corner_normals, corner_uvs = [], [], [], []
    for start in range(0, len(part['indices']), 3):
        original = list(part['indices'][start:start + 3])
        a, b, c = (source_positions[i] for i in original)
        expected = sum((source_normals[i] for i in original), Vector())
        if (b - a).cross(c - a).dot(expected) < 0:
            original.reverse()
        face = tuple(remap[i] for i in original)
        if len(set(face)) != 3:
            raise RuntimeError('Shared reader contains a collapsed original triangle')
        faces.append(face)
        row = int(source_uvs[original[0]][1] * palette_size)
        materials.append(max(0, min(palette_size - 1, row)))
        corner_normals.append(tuple(source_normals[i] for i in original))
        corner_uvs.append(tuple(source_uvs[i] for i in original))

    original_face_count = len(faces)
    edges = defaultdict(list)
    for face_id, face in enumerate(faces):
        for a, b in zip(face, face[1:] + face[:1]):
            edges[tuple(sorted((a, b)))].append(face_id)
    adjacent = defaultdict(set)
    for linked in edges.values():
        for a in linked:
            adjacent[a].update(linked)
    remaining = set(range(len(faces)))
    sheets = []
    closed_components = 0
    thickness = .0005
    while remaining:
        stack = [remaining.pop()]
        component = set()
        while stack:
            face_id = stack.pop()
            if face_id in component:
                continue
            component.add(face_id)
            new = adjacent[face_id] & remaining
            remaining.difference_update(new)
            stack.extend(new)
        boundary = []
        for face_id in component:
            face = faces[face_id]
            for a, b in zip(face, face[1:] + face[:1]):
                if len(edges[tuple(sorted((a, b)))]) == 1:
                    boundary.append((a, b, materials[face_id]))
        if not boundary:
            closed_components += 1
            continue
        normal = Vector()
        used = set()
        for face_id in component:
            a, b, c = (vertices[i] for i in faces[face_id])
            normal += (b - a).cross(c - a)
            used.update(faces[face_id])
        normal.normalize()
        origin = vertices[next(iter(used))]
        flatness = max(abs((vertices[i] - origin).dot(normal)) for i in used)
        if flatness > 1e-6:
            raise RuntimeError('An open non-planar reader component requires a designed closure')
        rear = {}
        for index in used:
            rear[index] = len(vertices)
            vertices.append(vertices[index] - normal * thickness)
        for face_id in sorted(component):
            front = faces[face_id]
            faces.append(tuple(rear[i] for i in reversed(front)))
            materials.append(materials[face_id])
            corner_normals.append(tuple(-normal for _ in front))
            corner_uvs.append(tuple(reversed(corner_uvs[face_id])))
        for a, b, material_index in boundary:
            side = (b, a, rear[a], rear[b])
            faces.append(side)
            materials.append(material_index)
            side_normal = (vertices[a] - vertices[b]).cross(vertices[rear[a]] - vertices[b]).normalized()
            corner_normals.append(tuple(side_normal for _ in side))
            corner_uvs.append(((0., 0.),) * 4)
        sheets.append({'original_triangles': len(component), 'boundary_edges': len(boundary),
                       'rearward_thickness_m': thickness, 'flatness_m': flatness})

    data = bpy.data.meshes.new('Shared CardReader | exact front faces and source closures')
    data.from_pydata(vertices, [], faces)
    data.update()
    for entry in palette:
        data.materials.append(_material(entry))
    uv = data.uv_layers.new(name='MeshKitUV')
    loop_normals = []
    for polygon, material_index, normals, uvs in zip(data.polygons, materials, corner_normals, corner_uvs):
        polygon.material_index = material_index
        for loop_id, normal, source_uv in zip(polygon.loop_indices, normals, uvs):
            loop_normals.append(normal)
            uv.data[loop_id].uv = source_uv
    data.normals_split_custom_set(loop_normals)
    bm = bmesh.new()
    bm.from_mesh(data)
    boundary_count = sum(edge.is_boundary for edge in bm.edges)
    nonmanifold_count = sum(not edge.is_manifold for edge in bm.edges)
    bm.free()
    data.calc_loop_triangles()
    degenerate_count = sum((data.vertices[t.vertices[1]].co - data.vertices[t.vertices[0]].co).cross(
        data.vertices[t.vertices[2]].co - data.vertices[t.vertices[0]].co).length_squared < 1e-18
        for t in data.loop_triangles)
    if boundary_count or nonmanifold_count or degenerate_count:
        raise RuntimeError(f'Shared reader source closure failed: boundary={boundary_count}, nonmanifold={nonmanifold_count}, degenerate={degenerate_count}')

    support = next((obj for obj in _collection_objects(master) if obj.name == 'Fare validator support'), None)
    if support is None:
        raise RuntimeError('Shared reader needs the authored Fare validator support post')
    deps = bpy.context.evaluated_depsgraph_get()
    support_mesh = bpy.data.meshes.new_from_object(support.evaluated_get(deps), depsgraph=deps)
    support_vertices = [support.matrix_world @ vertex.co for vertex in support_mesh.vertices]
    bpy.data.meshes.remove(support_mesh)
    top_z = max(v.z for v in support_vertices)
    high = [v for v in support_vertices if top_z - v.z < 1e-5]
    support_top = sum(high, Vector()) / len(high)
    reader = payload['readers'][0]
    qx, qy, qz, qw = reader['frameRotationUnityXyzw']
    rotation = Quaternion((qw, qx, qy, qz))
    unity_top = Vector((sign * support_top.x, support_top.z, -support_top.y))
    local_top = rotation.inverted() @ (unity_top - Vector(reader['originUnity']))
    attached = all(abs(local_top[i]) < half for i, half in enumerate((.085, .135, .045)))
    if not attached:
        raise RuntimeError('Existing validator support does not enter the shared reader housing: ' + repr(tuple(local_top)))

    removed = []
    parent = next((ob for ob in _collection_objects(master) if ob.get('cdlc_is_part_pivot') and int(ob.get('cdlc_part', -1)) == 12), None)
    for obj in _collection_objects(master):
        if obj.name in {'Fare validator', 'Fare validator screen'} or obj.get('cdlc_runtime_generated') == 'CardReader':
            removed.append(obj.name)
            bpy.data.objects.remove(obj, do_unlink=True)
    obj = bpy.data.objects.new('Common CardReader | shared mod model', data)
    master.objects.link(obj)
    if parent:
        obj.parent = parent
        obj.matrix_parent_inverse = parent.matrix_world.inverted()
        obj.matrix_world = Matrix.Identity(4)
    obj['cdlc_part'] = 12
    obj['cdlc_assignment'] = 'shared mod CardReader source copy'
    obj['cdlc_runtime_generated'] = 'CardReader'
    obj['cdlc_native_policy'] = 'Keep exact shared LOD0 front faces plus source-only UI closures in all native LODs; SWB1 regenerates shared CardReader by LOD'
    obj['preserve_lod'] = True
    obj['source_coordinate_space'] = coordinate_space
    obj['shared_generator'] = payload['source']
    obj['shared_export_sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
    obj['source_only_closure_triangles'] = len(data.loop_triangles) - original_face_count
    return {
        'status': 'PASS', 'shared_export': str(path), 'shared_export_sha256': obj['shared_export_sha256'],
        'generator': payload['source'], 'coordinate_space': coordinate_space,
        'object': obj.name, 'removed_objects': removed, 'cdlc_part': 12,
        'runtime_generated': 'CardReader', 'runtime_lod_triangles': [row['triangleCount'] for row in payload['lods']],
        'original_lod0_triangles_retained': original_face_count,
        'source_only_closure_triangles': len(data.loop_triangles) - original_face_count,
        'source_total_triangles': len(data.loop_triangles), 'closed_components_original': closed_components,
        'closed_ui_sheets': sheets, 'maximum_original_vertex_weld_error_m': max_weld_error,
        'boundary_edges': boundary_count, 'nonmanifold_edges': nonmanifold_count, 'degenerate_triangles': degenerate_count,
        'support': {'object': support.name, 'top_in_reader_local': list(local_top), 'top_inside_housing': attached},
        'source_only_closure_policy': 'Rear caps and sidewalls of 0.5 mm UI sheets are editable-source/native-only. Original shared front faces and all runtime generator geometry remain unchanged.',
        'native_lod_policy': 'Preserve the complete shared LOD0 source assembly in all three native FBXs.'
    }
