"""Focused SWB1 palette acceptance/rejection checks; no production assets written."""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import tempfile

import verify_runtime as verifier


ROOT = Path(__file__).resolve().parents[2]


def fixture(path, spec, swatch):
    """Small otherwise-valid three-LOD file, including a closed local strap."""
    eye = spec['driver_eye_unity']
    data = bytearray(verifier.HEADER.pack(b'SWB1', 3, 35, *eye))
    for part in range(35):
        pivot = (0., 0., 0.)
        if part == 1:
            pivot = (eye[0], eye[1], eye[2]+.1)
        elif 19 <= part <= 22:
            front = part < 21
            track = spec['front_track_m' if front else 'rear_track_m']
            z = spec['length_m']/2-spec['front_overhang_m'] if front else -spec['length_m']/2+spec['rear_overhang_m']
            pivot = ((-1 if part in (19, 21) else 1)*track/2, spec['wheel_radius_m'], z)
        elif part == 34:
            pivot = (0., 2.5, 0.)
        data += verifier.FRAME.pack(-1 if part == 0 else 0, 0, *pivot, 0., 0., 0., 1.)

    def triangle(points, color):
        a, b, c = points
        u, v = ([b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)])
        cross = (u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0])
        length = math.sqrt(sum(n*n for n in cross))
        normal = tuple(n/length for n in cross)
        values = [value for point in points for value in (*point, *normal)]
        return verifier.TRIANGLE.pack(color, *values)

    required_parts = {0, 25, *(int(leaf['part']) for leaf in spec['door_leaves'])}
    tetra = ((0., 0., 0.), (-.035, -.12, -.03), (.035, -.12, -.03), (0., -.12, .03))
    for _ in range(3):
        for part in range(35):
            faces = []
            if part in required_parts:
                faces = [triangle(((-.1, 2., 0.), (.1, 2., 0.), (0., 2., .1)), swatch if part == 0 else 0)]
            elif part == 34:
                faces = [triangle(tuple(tetra[i] for i in face), 0)
                         for face in ((0, 2, 1), (0, 1, 3), (0, 3, 2), (1, 2, 3))]
            data += verifier.COUNT.pack(len(faces))+b''.join(faces)
    path.write_bytes(gzip.compress(data))


def run(report_path):
    spec = json.loads((ROOT/'ModelSource/SWB6109EV12G/model-spec.json').read_text(encoding='utf-8'))
    identifiers, contract = verifier.load_palette_ids(verifier.DEFAULT_PALETTE)
    rows = []
    with tempfile.TemporaryDirectory(prefix='swb6109-runtime-palette-') as temporary:
        temporary = Path(temporary)
        binary = temporary/'fixture.bin.gz'

        def audit_case(name, index, palette, accepted):
            fixture(binary, spec, index)
            result = verifier.audit(binary, spec, palette=palette)
            expected_unknown = 0 if accepted else 1
            assert all(lod['unknown_swatches'] == expected_unknown for lod in result['lods']), name
            assert (result['status'] == 'PASS') == accepted, (name, result['errors'])
            rows.append({'case': name, 'status': 'PASS', 'triangle_id': index,
                         'expected_acceptance': accepted, 'unknown_per_lod': expected_unknown})

        audit_case('actual highest defined swatch is accepted', max(identifiers), verifier.DEFAULT_PALETTE, True)
        audit_case('legacy zero swatch remains accepted', 0, verifier.DEFAULT_PALETTE, True)
        missing = next(index for index in range(256) if index not in identifiers)
        audit_case('undefined byte is rejected', missing, verifier.DEFAULT_PALETTE, False)
        sparse = temporary/'sparse.json'
        sparse.write_text(json.dumps({'palette': [{'index': 0}, {'index': 2}]}))
        audit_case('sparse defined ID is accepted', 2, sparse, True)
        audit_case('gap inside row-count range is rejected', 1, sparse, False)

        for name, document in (
                ('missing palette', {}), ('empty palette', {'palette': []}),
                ('duplicate ID', {'palette': [{'index': 0}, {'index': 0}]}),
                ('negative ID', {'palette': [{'index': -1}]}),
                ('out-of-byte ID', {'palette': [{'index': 256}]}),
                ('boolean ID', {'palette': [{'index': True}]}),
                ('string ID', {'palette': [{'index': '0'}]}),
                ('missing ID', {'palette': [{}]})):
            malformed = temporary/'malformed.json'
            malformed.write_text(json.dumps(document))
            try:
                verifier.load_palette_ids(malformed)
            except ValueError:
                rows.append({'case': name, 'status': 'PASS', 'malformed_contract_rejected': True})
            else:
                raise AssertionError(name+' must fail closed')
        try:
            verifier.load_palette_ids(temporary/'absent.json')
        except FileNotFoundError:
            rows.append({'case': 'unavailable palette', 'status': 'PASS', 'missing_contract_rejected': True})
        else:
            raise AssertionError('Missing palette must fail closed')
    report = {'status': 'PASS', 'palette_contract': contract, 'checks': rows,
              'verifier_sha256': hashlib.sha256(Path(verifier.__file__).read_bytes()).hexdigest(),
              'scope': 'Temporary three-LOD fixtures and malformed palette contracts; production files unchanged.'}
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(f'SWB_RUNTIME_PALETTE_PASS {len(rows)} cases; {len(identifiers)} authoritative IDs; {report_path}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, default=ROOT/'out/swb6109-runtime-palette-validation.json')
    run(parser.parse_args().report)
