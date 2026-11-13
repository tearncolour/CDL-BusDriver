"""Stage the two display-enclosure repairs and prove the rest of the source unchanged."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import bpy

TOOLS = Path(__file__).resolve().parent
ROOT = TOOLS.parents[1]
SRC = ROOT / 'ModelSource/SWB6109EV12G'
sys.path.insert(0, str(TOOLS))
from apply_aqua_runtime_route import snapshot, props, value, digest, material_state
from animation_rig import collect_cdlc_parts
from sign_enclosures import SPECS, replace_saved


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def invariants(master):
    targets = {ob.name for ob in master.all_objects if any(
        ob.name == spec['name'] or ob.name.startswith(spec['name'] + '.')
        for spec in SPECS.values())}
    target_data = {bpy.data.objects[name].data.name for name in targets}
    whole = snapshot(master)
    native_materials = {}
    for mat in bpy.data.materials:
        state = material_state(mat)
        # Merging ten uses of the same backing material into two necessarily
        # changes its RNA reference count, not any BSDF or material property.
        state['settings'].pop('users', None)
        native_materials[mat.name] = state
    result = {'objects': {n: h for n, h in whole['object_hashes'].items() if n not in targets},
              'meshes': {n: h for n, h in whole['mesh_hashes'].items() if n not in target_data},
              'global_components': {n: h for n, h in whole['hashes'].items()
                                    if n in ('curves', 'actions', 'images', 'scenes')},
              'native_bsdf_excluding_reference_count': digest(native_materials),
              'aqua_all_properties': props(bpy.data.materials['02 Shanghai aqua twin stripe'])}
    frames = collect_cdlc_parts(master)
    keys = ('index', 'name', 'parent', 'pivot_object', 'pivot_unity',
            'rest_rotation_unity', 'rest_matrix_blender', 'posed_part_to_blender')
    result['frames'] = [{k: value(frame.get(k)) for k in keys} for frame in frames]
    scene = bpy.context.scene
    current = scene.frame_current
    result['unaffected_posed_matrices'] = {}
    for frame in (0, 30, 40):
        scene.frame_set(frame); bpy.context.view_layer.update()
        result['unaffected_posed_matrices'][str(frame)] = digest({
            ob.name: value(ob.matrix_world) for ob in master.all_objects if ob.name not in targets})
    scene.frame_set(current); bpy.context.view_layer.update()
    return result, whole


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--audit-dir', type=Path, required=True)
    parser.add_argument('--expect-source-sha256', required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
    audit = args.audit_dir.resolve(); audit.mkdir(parents=True, exist_ok=True)
    source = SRC / 'SWB6109EV12G.blend'
    if sha(source) != args.expect_source_sha256.lower():
        raise RuntimeError('Source is not the authorized frozen version')
    frozen = audit / 'frozen'; frozen.mkdir(exist_ok=True)
    for path in (source, SRC / 'sunwin-mesh.bin.gz', *SRC.glob('*.json')):
        dest = frozen / path.name
        if not dest.exists():
            shutil.copy2(path, dest)
        elif sha(path) != sha(dest):
            if path.suffix == '.json':
                # Parallel read-only validators and previews can finish while
                # a draft is refined. Preserve each report version verbatim.
                versions = frozen / 'report-versions'; versions.mkdir(exist_ok=True)
                shutil.copy2(path, versions / (path.stem + '-' + sha(path)[:16] + '.json'))
            else:
                raise RuntimeError('Existing backup differs: ' + path.name)
    if not (frozen / 'Asset').exists():
        shutil.copytree(ROOT / 'Asset/SWB6109EV12G', frozen / 'Asset')
    package = ROOT / 'Asset/SWB6109EV12G.cok'
    if not (frozen / package.name).exists():
        shutil.copy2(package, frozen / package.name)
    if not (frozen / 'previews').exists():
        shutil.copytree(SRC / 'previews', frozen / 'previews')
    bpy.ops.wm.open_mainfile(filepath=str(source))
    scene = bpy.context.scene
    if scene.frame_current != 0:
        raise RuntimeError('Saved source is not at frame zero')
    master = bpy.data.collections['SWB6109EV12G | editable components']
    master_name = master.name
    before, whole_before = invariants(master)
    (audit / 'source-before.json').write_text(json.dumps(whole_before, indent=2) + '\n')
    repair = replace_saved(master)
    bpy.context.view_layer.update()
    after, whole_after = invariants(master)
    if before != after:
        (audit / 'invariant-mismatch.json').write_text(json.dumps({'before': before, 'after': after}, indent=2))
        raise RuntimeError('Repair changed an unrelated object, mesh, action, pose or material')
    candidate = audit / 'sign-enclosures-draft.blend'
    bpy.ops.wm.save_as_mainfile(filepath=str(candidate), copy=True, relative_remap=False)
    bpy.ops.wm.open_mainfile(filepath=str(candidate))
    reopened, whole_reopened = invariants(bpy.data.collections[master_name])
    if reopened != before or whole_after != whole_reopened:
        raise RuntimeError('Saved/reopened draft differs from the checked repair')
    (audit / 'source-after.json').write_text(json.dumps(whole_reopened, indent=2) + '\n')
    report = {'status': 'STAGED_SOURCE_DELTA_PASS',
              'source_before_sha256': sha(source), 'draft_sha256': sha(candidate),
              'runtime_before_sha256': sha(SRC / 'sunwin-mesh.bin.gz'),
              'package_before_sha256': sha(package), 'draft': str(candidate),
              'repair': repair, 'unaffected_object_count': len(before['objects']),
              'unaffected_mesh_count': len(before['meshes']),
              'unaffected_source_invariant_sha256': digest(before),
              'unaffected_source_invariants_unchanged': True,
              'aqua_material_all_properties_unchanged': True,
              'all_native_bsdf_and_images_unchanged': True,
              'animation_frames_unchanged': len(before['frames']),
              'posed_frames': [0, 30, 40],
              'limitations': 'Staged source only; boundary rays, renders, export and game validation remain required.'}
    (audit / 'source-delta.json').write_text(json.dumps(report, indent=2) + '\n')
    print('SIGN_ENCLOSURES_DRAFT_READY ' + sha(candidate), flush=True)


if __name__ == '__main__':
    main()
