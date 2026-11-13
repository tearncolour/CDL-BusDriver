"""Bind the native PipelinePlugin read-back to the exact exported section assets."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--summary', type=Path, required=True)
    parser.add_argument('--log', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    summary = json.loads(args.summary.read_text(encoding='utf-8'))
    assert sha(summary['source']) == summary['source_sha256'], 'Source changed after export'
    log = args.log.read_text(encoding='utf-8-sig')
    assert "check: all files fit the game's importer" in log
    blocks = dict(re.findall(r'^== ([^\n]+)\n(.*?)(?=^== |\Z)', log, re.M | re.S))
    assert len(blocks) == 12, 'Expected all twelve section / glass / LOD assets'
    results = []
    for section, info in summary['sections'].items():
        for asset in info['native']:
            path = Path(asset['path'])
            assert sha(path) == asset['sha256'], path
            text = blocks[path.name]
            match = re.search(r"  mesh 0 '([^']+)': (\d+) verts, (\d+) tris, (\d+) submeshes, (\d+) bones \(root (\d+)\)", text)
            assert match is not None, path
            name, vertices, triangles, submeshes, bones, root = match.groups()
            assert name == asset['mesh_name'] == path.stem
            assert int(triangles) == asset['triangles'] and int(submeshes) == 1 and int(root) == 0
            decoded = []
            for bone in re.finditer(r"    bone (\d+) '([^']+)' parent (-?\d+) pos \(([^)]+)\) rot \(([^)]+)\) scale \(([^)]+)\)", text):
                index, label, parent, pos, rotation, scale = bone.groups()
                pos = [float(v.strip()) for v in pos.split(',')]
                rotation = [float(v.strip()) for v in rotation.split(',')]
                scale = [float(v.strip()) for v in scale.split(',')]
                assert rotation == [0., 0., 0., 1.] and scale == [1., 1., 1.]
                if label == 'Root':
                    expected = [0., 0., 0.]
                    assert parent == '-1'
                else:
                    number = {'WheelFL': 19, 'WheelFR': 20, 'WheelRL': 21, 'WheelRR': 22}[label]
                    expected = info['runtime']['frames'][number]['pivot_unity']
                    assert parent == '0'
                assert max(abs(a-b) for a,b in zip(pos, expected)) <= .00051
                decoded.append({'name': label, 'position_m_rounded': pos, 'parent': int(parent)})
            assert [b['name'] for b in decoded] == asset['bones'] and len(decoded) == int(bones)
            if section == 'rear':
                assert [b['name'] for b in decoded] == ['Root', 'WheelRL', 'WheelRR']
            winding = re.search(r'winding: cross product along the normal (\d+), against (\d+)', text)
            assert winding and int(winding[1]) == int(triangles) and int(winding[2]) == 0
            results.append({'path': str(path), 'sha256': sha(path), 'section': section,
                            'lod': asset['lod'], 'glass': asset['glass'], 'triangles': int(triangles),
                            'vertices': int(vertices), 'bones': decoded})
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({'status': 'PASS', 'source_sha256': summary['source_sha256'],
        'summary_sha256': sha(args.summary), 'log_sha256': sha(args.log), 'assets': results,
        'scope': 'Actual installed PipelinePlugin read-back. Winding is native first-corner check; all-corner SWB1 validation is separate. No game material/road acceptance implied.'}, indent=2)+'\n', encoding='utf-8')
    print('K18 native read-back: PASS (12 assets)')


if __name__ == '__main__':
    main()
