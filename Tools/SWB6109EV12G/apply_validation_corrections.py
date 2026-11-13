"""Apply only the inspected wheelhouse seam and material corrections.

The saved model is guarded by its digest. All geometry outside the named
wheelhouse pieces and all fixed motion frames must remain byte-identical.
"""
import argparse
from array import array
from datetime import datetime
import hashlib
import json
from pathlib import Path
import shutil
import struct
import sys
import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from animation_rig import collect_cdlc_parts
from material_contract import apply_material_contract
from wheelhouse_seams import apply_saved_wheelhouse_seams, TARGET_OBJECT_NAMES
from card_reader import replace_common_card_reader
from source_coordinates import canonical_world_matrix, source_is_corrected

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT/'ModelSource/SWB6109EV12G'


def fingerprint(obj):
    digest = hashlib.sha256(obj.type.encode())
    digest.update(struct.pack('<16f', *[v for row in obj.matrix_world for v in row]))
    if obj.type == 'MESH':
        vertices = array('f', [0]) * (3*len(obj.data.vertices))
        obj.data.vertices.foreach_get('co', vertices)
        indices = array('i', [0]) * len(obj.data.loops)
        obj.data.loops.foreach_get('vertex_index', indices)
        counts = array('i', [0]) * len(obj.data.polygons)
        obj.data.polygons.foreach_get('loop_total', counts)
        digest.update(vertices.tobytes()+indices.tobytes()+counts.tobytes())
    elif obj.type == 'CURVE':
        for spline in obj.data.splines:
            digest.update(str((spline.type, spline.use_cyclic_u)).encode())
            for point in spline.points:
                digest.update(struct.pack('<4f', *point.co))
            for point in spline.bezier_points:
                digest.update(struct.pack('<9f', *point.co, *point.handle_left, *point.handle_right))
        digest.update(str((obj.data.bevel_depth, obj.data.bevel_resolution)).encode())
    elif obj.type == 'FONT':
        digest.update(str((obj.data.body, obj.data.size, obj.data.extrude, obj.data.bevel_depth)).encode())
    for modifier in obj.modifiers:
        digest.update(str((modifier.name, modifier.type)).encode())
        if modifier.type == 'BEVEL':
            digest.update(str((modifier.width, modifier.segments)).encode())
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--expect-sha256', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    source = SRC/'SWB6109EV12G.blend'
    before_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    if before_hash != args.expect_sha256:
        raise RuntimeError('Saved source changed before the validation correction')
    backup = ROOT/'out/swb6109-spec'/('before-validation-'+datetime.now().strftime('%Y%m%d-%H%M%S'))
    backup.mkdir(parents=True)
    for path in [source, SRC/'sunwin-mesh.bin.gz', *SRC.glob('*.json')]:
        if path.is_file():
            shutil.copy2(path, backup/path.name)
    shutil.copytree(SRC/'previews', backup/'previews')
    bpy.ops.wm.open_mainfile(filepath=str(source))
    scene = bpy.context.scene
    scene.frame_set(0)
    bpy.context.view_layer.update()
    master = bpy.data.collections['SWB6109EV12G | editable components']
    before = {obj.name: fingerprint(obj) for obj in master.all_objects}
    frames = collect_cdlc_parts(master)
    allowed = set(TARGET_OBJECT_NAMES)
    if not allowed.issubset(before):
        raise RuntimeError('A specifically named wheelhouse target is missing')
    seams = apply_saved_wheelhouse_seams(master)
    # An append-only palette update changes the shared export's UV row scale
    # and digest. Regenerate the exact common reader, while requiring that its
    # physical geometry stays identical to the previously reviewed source.
    reader = replace_common_card_reader(master, SRC/'common-card-reader.json', coordinate_space='blender_source')
    reader_object = master.all_objects[reader['object']]
    bpy.context.view_layer.update()
    if source_is_corrected(reader_object):
        matrix = canonical_world_matrix(reader_object)
        reader_object['cdlc_source_canonical_matrix_at_conversion'] = [float(matrix[r][c]) for c in range(4) for r in range(4)]
    materials = apply_material_contract(master)
    bpy.context.view_layer.update()
    after = {obj.name: fingerprint(obj) for obj in master.all_objects}
    if set(before) != set(after):
        raise RuntimeError('The seam/material correction added or removed model objects')
    changed = {name for name in before if before[name] != after[name]}
    if changed-allowed:
        raise RuntimeError('Unexpected geometry edits: '+', '.join(sorted(changed-allowed)))
    after_frames = collect_cdlc_parts(master)
    for left, right in zip(frames, after_frames):
        for key in ('index', 'name', 'parent', 'pivot_object', 'pivot_unity',
                    'rest_rotation_unity', 'rest_matrix_blender', 'posed_part_to_blender'):
            if left[key] != right[key]:
                raise RuntimeError('Changed motion frame: '+str(left['index'])+' '+key)
    if len(frames) != len(after_frames):
        raise RuntimeError('Changed motion frame count')
    if hashlib.sha256(source.read_bytes()).hexdigest() != before_hash:
        raise RuntimeError('Saved source changed during the validation correction')
    scene['validation_correction_revision'] = 'Wheelhouse-to-sidewall boundary and explicit neutral interior finishes'
    scene['status'] = 'Validation corrections saved; re-export and complete checks pending'
    bpy.ops.wm.save_as_mainfile(filepath=str(source))
    (SRC/'common-card-reader-import.json').write_text(json.dumps(reader, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    report = {'status': 'PASS', 'scope': 'Narrow saved-source correction only; export and full validation remain required',
              'source_sha256_before': before_hash,
              'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
              'backup': str(backup), 'changed_geometry_objects': sorted(changed),
              'unchanged_geometry_objects': len(before)-len(changed),
              'motion_frames_unchanged': len(frames), 'wheelhouse_seams': seams, 'materials': materials,
              'shared_card_reader': reader, 'shared_reader_geometry_unchanged': reader['object'] not in changed}
    (SRC/'validation-corrections.json').write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print('VALIDATION_CORRECTIONS_SAVED '+report['source_sha256'])


if __name__ == '__main__':
    main()
