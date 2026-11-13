"""Check persisted native glass coat and depth-pass compatibility in a game-created .cok.

The version-1 Surface header and float dictionary follow the installed game's
SurfaceAsset.LoadProperties and Colossal.IO.BinaryReaderExtensions. No package
or game resource is changed by this check.
"""
import argparse
import hashlib
import json
import math
import struct
import zipfile
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def surface_floats(data):
    pos = 0

    def read(fmt):
        nonlocal pos
        value = struct.unpack_from('<' + fmt, data, pos)
        pos += struct.calcsize('<' + fmt)
        return value[0] if len(value) == 1 else value

    version, template, vt = read('HiB')
    if version != 1 or vt != 0:
        raise ValueError('Expected version-1 non-VT glass Surface')
    null_tag = read('B')
    if null_tag not in (0, 255):
        raise ValueError('Invalid nullable dictionary marker')
    values = {}
    if null_tag == 255:
        count = read('i')
        if count < 0 or count > 65536:
            raise ValueError('Invalid float dictionary count')
        for _ in range(count):
            size, shift = 0, 0
            while True:
                byte = read('B')
                size |= (byte & 127) << shift
                if not byte & 128:
                    break
                shift += 7
                if shift > 28:
                    raise ValueError('Invalid string length')
            if pos + size > len(data):
                raise ValueError('Truncated float property name')
            name = data[pos:pos + size].decode('utf-8')
            pos += size
            if name in values:
                raise ValueError('Duplicate float property')
            values[name] = read('f')
    return dict(version=version, template_hash=template, is_vt=False,
                floats=values, float_dictionary_end=pos)


def inspect(package):
    rows = []
    with zipfile.ZipFile(package) as archive:
        for entry in archive.infolist():
            if entry.filename.endswith('.Surface') and '_Gls_' in entry.filename:
                data = archive.read(entry)
                row = surface_floats(data)
                row.update(entry=entry.filename, bytes=len(data),
                           sha256=hashlib.sha256(data).hexdigest())
                rows.append(row)
    errors = []
    if len(rows) != 1:
        errors.append(f'Expected one shared native glass surface, found {len(rows)}')
    for row in rows:
        coat = row['floats'].get('_CoatMask')
        if row['template_hash'] != 11:
            errors.append('Glass does not use the audited template hash 11')
        if coat is None or not math.isfinite(coat) or abs(coat - .65) > .0001:
            errors.append(f"{row['entry']}: persisted _CoatMask is {coat}, expected .65")
        for name in ('_ReceivesSSRTransparent', '_TransparentDepthPrepassEnable'):
            value = row['floats'].get(name)
            if value is None or not math.isfinite(value) or value != 0:
                errors.append(f"{row['entry']}: persisted {name} is {value}, expected exact zero")
    return dict(status='FAIL' if errors else 'PASS', scope='Persisted native package surface properties only',
                package=str(package.resolve()), package_sha256=sha(package), surfaces=rows, errors=errors)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('package', type=Path)
    parser.add_argument('--report', required=True, type=Path)
    args = parser.parse_args()
    result = inspect(args.package)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))
    raise SystemExit(0 if result['status'] == 'PASS' else 1)


if __name__ == '__main__':
    main()
