"""Read-only final-source and SWB1 delta proof for the display enclosure repair."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import struct
import sys
import bpy

TOOLS = Path(__file__).resolve().parent
ROOT = TOOLS.parents[1]
SRC = ROOT / 'ModelSource/SWB6109EV12G'
sys.path.insert(0, str(TOOLS))
from apply_aqua_runtime_route import snapshot, material_state, props, value, digest
from animation_rig import collect_cdlc_parts
from sign_enclosures import SPECS


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def source_state(path):
    bpy.ops.wm.open_mainfile(filepath=str(path))
    master = bpy.data.collections['SWB6109EV12G | editable components']
    targets = {ob.name for ob in master.all_objects if any(
        ob.name == spec['name'] or ob.name.startswith(spec['name'] + '.') for spec in SPECS.values())}
    unrelated = {ob.name for ob in master.all_objects} - targets
    meshes = {ob.data.name for ob in master.all_objects if ob.name in unrelated and ob.type == 'MESH'}
    whole = snapshot(master)
    materials = {}
    for mat in bpy.data.materials:
        state = material_state(mat); state['settings'].pop('users', None)
        materials[mat.name] = state
    keys = ('index', 'name', 'parent', 'pivot_object', 'pivot_unity',
            'rest_rotation_unity', 'rest_matrix_blender', 'posed_part_to_blender')
    state = {'objects': {n: whole['object_hashes'][n] for n in sorted(unrelated)},
             'meshes': {n: whole['mesh_hashes'][n] for n in sorted(meshes)},
             'curves': whole['hashes']['curves'], 'actions': whole['hashes']['actions'],
             'images': whole['hashes']['images'], 'native_materials': digest(materials),
             'aqua_all_properties': props(bpy.data.materials['02 Shanghai aqua twin stripe']),
             'frames': [{k: value(frame.get(k)) for k in keys} for frame in collect_cdlc_parts(master)],
             'posed_matrices': {}}
    for frame in (0, 30, 40):
        bpy.context.scene.frame_set(frame); bpy.context.view_layer.update()
        state['posed_matrices'][str(frame)] = digest({ob.name: value(ob.matrix_world)
            for ob in master.all_objects if ob.name in unrelated})
    return state, {'targets': sorted(targets), 'source_sha256': sha(path),
                   'master_objects': len(master.all_objects), 'unchanged_objects': len(unrelated)}


def runtime_parts(path):
    data = gzip.decompress(Path(path).read_bytes())
    if data[:4] != b'SWB1': raise RuntimeError('Not a SWB1 file')
    lods, parts = struct.unpack_from('<ii', data, 4)
    offset = 24 + 36 * parts
    header = data[:offset]
    result = []
    for lod in range(lods):
        rows = []
        for part in range(parts):
            count, = struct.unpack_from('<i', data, offset)
            end = offset + 4 + count * 73
            if count < 0 or end > len(data): raise RuntimeError('Invalid SWB1 block')
            rows.append({'part': part, 'triangles': count,
                         'payload_sha256': hashlib.sha256(data[offset:end]).hexdigest()})
            offset = end
        result.append(rows)
    if offset != len(data): raise RuntimeError('SWB1 trailing data')
    return header, result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--audit-dir', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    audit = args.audit_dir.resolve()
    before, binfo = source_state(audit / 'frozen/SWB6109EV12G.blend')
    after, ainfo = source_state(SRC / 'SWB6109EV12G.blend')
    if before != after:
        (audit / 'final-delta-mismatch.json').write_text(json.dumps({'before': before, 'after': after}, indent=2))
        raise RuntimeError('Unrelated authored source data changed during formal export')
    bhead, bparts = runtime_parts(audit / 'frozen/sunwin-mesh.bin.gz')
    ahead, aparts = runtime_parts(SRC / 'sunwin-mesh.bin.gz')
    if bhead != ahead or len(bparts) != 3 or len(aparts) != 3:
        raise RuntimeError('SWB1 header, 43 frames or LOD count changed')
    rows = []
    for lod, (bs, af) in enumerate(zip(bparts, aparts)):
        changed = [i for i, (b, a) in enumerate(zip(bs, af)) if b != a]
        if changed != [17]: raise RuntimeError('Unexpected runtime part delta: ' + str((lod, changed)))
        rows.append({'lod': lod, 'changed_parts': changed, 'unchanged_part_count': len(bs)-1,
                     'part17_before': bs[17], 'part17_after': af[17]})
    pngs = []
    for old in sorted((audit / 'frozen/Asset').glob('*.png')):
        current = ROOT / 'Asset/SWB6109EV12G' / old.name
        if sha(old) != sha(current): raise RuntimeError('Native texture changed: ' + old.name)
        pngs.append({'file': old.name, 'sha256': sha(current)})
    report = {'status': 'PASS', 'scope': 'Final authored source delta and all runtime part payloads, after saved-source export',
              'source_before': binfo, 'source_after': ainfo,
              'runtime_before_sha256': sha(audit / 'frozen/sunwin-mesh.bin.gz'),
              'runtime_sha256': sha(SRC / 'sunwin-mesh.bin.gz'),
              'unrelated_source_hash': digest(before), 'all_other_authored_objects_unchanged': True,
              'curves_actions_native_materials_images_aqua_unchanged': True,
              'fixed_and_dynamic_frames_unchanged': len(before['frames']),
              'runtime_delta': rows, 'native_pngs_unchanged': pngs,
              'code': {p.name: sha(p) for p in (TOOLS/'sign_enclosures.py', TOOLS/'exterior.py')},
              'limitations': 'Package and actual game images must be rebuilt and reviewed for the new geometry.'}
    (audit/'final-export-delta.json').write_text(json.dumps(report, indent=2)+'\n')
    (SRC/'sign-enclosure-repair-report.json').write_text(json.dumps(report, indent=2)+'\n')
    print('SIGN_EXPORT_DELTA_PASS ' + ainfo['source_sha256'], flush=True)


if __name__ == '__main__': main()
