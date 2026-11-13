"""Read all six FBX files with the game's native reader and record exact hashes.

The external probe is read-only. Override --probe and --plugin-dir on another
workstation; neither the game install nor the generated geometry is modified.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
NAME = 'SWB6109EV12G'
SRC = ROOT/'ModelSource'/NAME
ASSET = ROOT/'Asset'/NAME


def snapshot(path):
    return {'file': path.name, 'bytes': path.stat().st_size,
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'modified_utc': datetime.fromtimestamp(path.stat().st_mtime, timezone.utc).isoformat()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--probe', type=Path, default=Path.home()/'Desktop/cs2_research/bus_asset/fbx_probe/bin/Release/net8.0/fbx_probe.exe')
    parser.add_argument('--plugin-dir', type=Path, default=Path('C:/Program Files (x86)/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Plugins/x86_64'))
    parser.add_argument('--report', type=Path, default=SRC/'native-validation.json')
    args = parser.parse_args()
    paths = [ASSET/(NAME+(f'_LOD{lod}' if lod else '')+suffix+'.fbx')
             for lod in range(3) for suffix in ('', '_Gls')]
    errors = []; rows = []; log = ''
    report = {'status': 'FAIL', 'reader': 'Cities Skylines II PipelinePlugin native model reader',
              'probe': str(args.probe), 'plugin_directory': str(args.plugin_dir),
              'scope': 'Read-only native import, exact mesh naming, coordinate, scale, bone and winding validation; not an in-game playtest.',
              'normal_face_dot_rejection_threshold_fraction': .01, 'fbx': rows, 'errors': errors}
    try:
        if not args.probe.is_file(): raise ValueError('Native reader probe is missing: '+str(args.probe))
        if not (args.plugin_dir/'PipelinePlugin.dll').is_file(): raise ValueError('PipelinePlugin.dll is missing: '+str(args.plugin_dir))
        before = {p.name: snapshot(p) for p in paths}
        if paths[0].stem != ASSET.name:
            errors.append('Main FBX stem must exactly match the asset folder name.')
        spec = json.loads((SRC/'model-spec.json').read_text(encoding='utf-8'))
        expected_bones = {'Root': (0.,0.,0.)}
        for front in (True,False):
            track = float(spec['front_track_m' if front else 'rear_track_m'])
            z = float(spec['length_m'])/2-float(spec['front_overhang_m']) if front else -float(spec['length_m'])/2+float(spec['rear_overhang_m'])
            for left in (True,False):
                expected_bones['Wheel'+('F' if front else 'R')+('L' if left else 'R')] = ((-1 if left else 1)*track/2,float(spec['wheel_radius_m']),z)
        command = [str(args.probe), str(args.plugin_dir), '--check', *map(str, paths)]
        process = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', errors='replace')
        log = process.stdout + ('\nSTDERR:\n'+process.stderr if process.stderr else '')
        (SRC/'native-probe.log').write_text(log, encoding='utf-8')
        if process.returncode: errors.append(f'Native probe exited {process.returncode}.')
        if "check: all files fit the game's importer" not in log: errors.append('Native reader did not report a successful import check.')
        for section in log.split('== ')[1:]:
            name = section.splitlines()[0].strip()
            if name not in before:
                errors.append('Unexpected native reader file: '+name); continue
            mesh_count = re.search(r'^\s*meshes (\d+),', section, re.MULTILINE)
            mesh_names = re.findall(r"^\s*mesh \d+ '([^']+)':", section, re.MULTILINE)
            expected_mesh_name = Path(name).stem
            counts = re.search(r'(\d+) verts, (\d+) tris, (\d+) submeshes, (\d+) bones', section)
            winding = re.search(r'along the normal (\d+), against (\d+)', section)
            if not counts or not winding:
                errors.append('Incomplete native reader output for '+name); continue
            row = dict(before[name], vertices=int(counts[1]), triangles=int(counts[2]),
                       submeshes=int(counts[3]), bones=int(counts[4]),
                       mesh_count=int(mesh_count[1]) if mesh_count else None,
                       mesh_names=mesh_names, expected_mesh_name=expected_mesh_name,
                       normal_face_dot_positive=int(winding[1]), normal_face_dot_negative=int(winding[2]))
            # The native reader can see all five bones even when the game will
            # not bind them: its asset importer selects the mesh by exact name.
            # Blender's automatic .001 suffix therefore invalidates the asset.
            if row['mesh_count'] != 1 or len(mesh_names) != 1:
                errors.append(name+': expected exactly one named mesh in the native reader output.')
            if mesh_names != [expected_mesh_name]:
                errors.append(f'{name}: native mesh names {mesh_names!r} must equal [{expected_mesh_name!r}].')
            row['normal_face_dot_negative_fraction'] = row['normal_face_dot_negative']/max(1, row['triangles'])
            row['bone_names'] = re.findall(r"bone \d+ '([^']+)'", section)
            row['bone_positions_unity'] = {name: [float(x) for x in coords.split(',')]
                for name,coords in re.findall(r"bone \d+ '([^']+)'[^\n]+? pos \(([^)]+)\)", section)}
            if row['bones'] != 5 or set(row['bone_names']) != {'Root', 'WheelFL', 'WheelFR', 'WheelRL', 'WheelRR'}:
                errors.append(name+': expected the five Root/road-wheel bones.')
            for bone,expected in expected_bones.items():
                position = row['bone_positions_unity'].get(bone)
                if position is None or max(abs(a-b) for a,b in zip(position,expected)) > .002:
                    errors.append(f'{name}: native bone {bone} does not match the vehicle specification.')
            if row['normal_face_dot_negative_fraction'] > .01:
                errors.append(name+': winding/normal disagreement exceeds the native reader limit.')
            if snapshot(ASSET/name) != before[name]: errors.append(name+': file changed during validation.')
            rows.append(row)
        if len(rows) != 6: errors.append(f'Expected six validated FBX files, got {len(rows)}.')
        report['final_artifacts'] = [snapshot(SRC/p) for p in ('sunwin-mesh.bin.gz', NAME+'.blend')]
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        errors.append(str(error))
    report['checks'] = ['one mesh per FBX, named exactly after its file stem; main stem matches asset folder',
                        'metre scale and identity mesh transforms', 'five unrotated unit-scale bones',
                        'native vertex attributes and triangle winding', 'unchanged input hashes during validation']
    report['status'] = 'PASS' if not errors else 'FAIL'
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(f"SWB6109_NATIVE_{report['status']} {len(rows)} FBX files; {args.report}")
    for error in errors: print(error)
    return 0 if not errors else 1


if __name__ == '__main__':
    raise SystemExit(main())
