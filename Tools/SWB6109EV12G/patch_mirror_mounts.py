"""Rebuild only the two primary mirror supports from the shared generator.

Load a saved master in Blender and pass --output to create a review candidate.
Reflector planes, shells, auxiliary mirror, materials and all other objects stay
unchanged. This script does not export, package or modify installed game files.
"""
import argparse
import ast
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

import bpy
from mathutils import Matrix, Vector

TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))
from source_coordinates import canonical_export_context, canonical_world_matrix, source_root, _flat


def refresh_support_canonical_metadata(master):
    """Refresh only the saved-frame witness after an intentional support move.

    This is audit metadata, not a transform or a CDLC animation/rest frame. It
    must describe the rebuilt object's current canonical matrix rather than the
    matrix copied from the previous support. Works inside or outside the export
    context through canonical_world_matrix's explicit context marker.
    """
    components = {'pillar mount', 'hinge', 'accordion hinge', 'moulded arm'}
    objects = [obj for obj in master.objects if obj.get('mirror_assembly') in
               {'driver side small', 'door side large'} and obj.get('mirror_component') in components]
    if len(objects) != 20:
        raise RuntimeError('Expected exactly 20 support witnesses, found ' + str(len(objects)))
    bpy.context.view_layer.update()
    rows = []
    key = 'cdlc_source_canonical_matrix_at_conversion'
    for obj in sorted(objects, key=lambda item: item.name):
        before = list(obj.get(key, []))
        after = _flat(canonical_world_matrix(obj))
        obj[key] = after
        rows.append(dict(object=obj.name, before=before, after=after, changed=before != after))
    return dict(property=key, checked=len(rows), changed=sum(row['changed'] for row in rows), objects=rows)


def object_digest(obj):
    digest = hashlib.sha256()
    digest.update((obj.name + '|' + obj.type + '|' + (obj.parent.name if obj.parent else '')).encode())
    digest.update(struct.pack('<16f', *[v for row in obj.matrix_world for v in row]))
    if obj.type in {'MESH', 'CURVE', 'FONT'}:
        deps = bpy.context.evaluated_depsgraph_get()
        data = bpy.data.meshes.new_from_object(obj.evaluated_get(deps), depsgraph=deps)
        for vertex in data.vertices:
            digest.update(struct.pack('<3f', *vertex.co))
        for polygon in data.polygons:
            digest.update(struct.pack('<ii?', len(polygon.vertices), polygon.material_index, polygon.use_smooth))
            digest.update(struct.pack('<' + 'I' * len(polygon.vertices), *polygon.vertices))
        for normal in data.corner_normals:
            digest.update(struct.pack('<3f', *normal.vector))
        for material in data.materials:
            digest.update((material.name if material else '').encode())
        bpy.data.meshes.remove(data)
    return digest.hexdigest()


def copied_property(value):
    # IDPropertyArray wrappers retain native pointers. Copy their values before
    # deleting the old object, otherwise later assignment dereferences freed RNA.
    if hasattr(value, 'to_list'):
        return value.to_list()
    if hasattr(value, 'to_dict'):
        return {key: copied_property(item) for key,item in value.to_dict().items()}
    return value


def rebuild(master):
    if source_root(master) is None:
        raise RuntimeError('Expected physically left-hand-drive saved source')
    bpy.context.scene.frame_set(0)
    bpy.context.view_layer.update()
    components = {'pillar mount', 'hinge', 'accordion hinge', 'moulded arm'}
    old = [o for o in master.objects if o.get('mirror_assembly') in
           {'driver side small', 'door side large'} and o.get('mirror_component') in components]
    if len(old) != 20:
        raise RuntimeError('Expected exactly 20 support objects, found ' + str(len(old)))
    allowed = {o.name for o in old}
    before = {o.name: object_digest(o) for o in master.objects if o.name not in allowed}
    parents = {o.name: o.parent for o in old}
    metadata = {o.name: {k: copied_property(o[k]) for k in o.keys() if k.startswith('cdlc_') or k == 'bone'} for o in old}
    environment = dict(bpy=bpy, math=math, Vector=Vector, Matrix=Matrix,
                       current_collection=master, MIRROR_ARM_CHECKS=[],
                       plastic=bpy.data.materials['06 Cabin graphite'],
                       rubber=bpy.data.materials['05 EPDM seals and tyre'],
                       black=bpy.data.materials['04 Gloss black glazing frit'])
    definitions = {
        'build_model.py': {'link', 'assign', 'mesh', 'bevel', 'box', 'rod'},
        'mirror_shell.py': {'mirror_stamp', 'mirror_smooth', 'mirror_bezier',
                           'mirror_arm_curvature', 'mirror_arm', 'mirror_hinge',
                           'mirror_support_spec', 'mirror_primary_support'},
    }
    for filename, names in definitions.items():
        tree = ast.parse((TOOLS / filename).read_text(encoding='utf-8'))
        nodes = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in names]
        if {n.name for n in nodes} != names:
            raise RuntimeError('Missing generator helpers in ' + filename)
        exec(compile(ast.Module(body=nodes, type_ignores=[]), filename, 'exec'), environment)
    with canonical_export_context(master):
        for obj in old:
            data = obj.data
            bpy.data.objects.remove(obj, do_unlink=True)
            if data.users == 0:
                bpy.data.meshes.remove(data)
        environment['mirror_primary_support'](1, 'door side large')
        environment['mirror_primary_support'](-1, 'driver side small')
        generated = [o for o in master.objects if o.name not in before]
        if {o.name for o in generated} != allowed:
            raise RuntimeError('Regeneration changed support object names')
        for obj in generated:
            world = obj.matrix_world.copy()
            obj.parent = parents[obj.name]
            obj.matrix_parent_inverse = obj.parent.matrix_world.inverted() if obj.parent else Matrix.Identity(4)
            obj.matrix_world = world
            for key, value in metadata[obj.name].items():
                obj[key] = value
            obj['mirror_support_revision'] = 2
        bpy.context.view_layer.update()
    bpy.context.view_layer.update()
    canonical_metadata = refresh_support_canonical_metadata(master)
    after = {o.name: object_digest(o) for o in master.objects if o.name not in allowed}
    if before != after:
        raise RuntimeError('Non-support objects changed: ' + str([n for n in before if before[n] != after.get(n)]))
    return dict(status='PASS', changed_objects=sorted(allowed), unchanged_objects=len(before),
                unchanged_geometry_sha256=hashlib.sha256(json.dumps(before, sort_keys=True).encode()).hexdigest(),
                arm_checks=environment['MIRROR_ARM_CHECKS'],
                canonical_metadata=canonical_metadata,
                scope='Only primary shoes, hinges, ribs and arms; all reflector planes and shells preserved')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
    source = Path(bpy.data.filepath).resolve()
    before_sha = hashlib.sha256(source.read_bytes()).hexdigest()
    result = rebuild(bpy.data.collections['SWB6109EV12G | editable components'])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(args.output.resolve()), relative_remap=False)
    result.update(source_before=str(source), source_before_sha256=before_sha,
                  candidate=str(args.output.resolve()), candidate_sha256=hashlib.sha256(args.output.read_bytes()).hexdigest())
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print('MIRROR_SUPPORT_PATCH', json.dumps(result))
