"""Read-only SWB1 proof: only the selected part's selected palette byte changed.

Compare the frozen export with a newly regenerated export. All header/frame,
triangle-count, position, normal, ordering and unrelated palette bytes must be
identical. This tool never patches or writes either runtime binary.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import struct


def compare_bytes(before, after, part=16, old_index=80, new_index=83):
    if before[:4] != b'SWB1' or after[:4] != b'SWB1':
        raise ValueError('Both inputs must be SWB1')
    if len(before) != len(after):
        raise ValueError('Runtime byte length changed')
    lods, parts = struct.unpack_from('<ii', before, 4)
    header_end = 24 + 36 * parts
    if before[:header_end] != after[:header_end]:
        raise ValueError('Header, eye, fixed/appended part frames or transforms changed')
    if lods != 3 or not 0 <= part < parts:
        raise ValueError('Expected three LODs and a valid target part')
    offset = header_end
    rows = []
    geometry = hashlib.sha256(before[:header_end])
    for lod in range(lods):
        changes = 0
        triangles = 0
        for current_part in range(parts):
            if before[offset:offset+4] != after[offset:offset+4]:
                raise ValueError(f'Triangle count changed in LOD{lod} part{current_part}')
            count, = struct.unpack_from('<i', before, offset)
            if count < 0 or offset + 4 + count * 73 > len(before):
                raise ValueError('Invalid triangle count')
            geometry.update(before[offset:offset+4])
            offset += 4
            for triangle in range(count):
                a, b = before[offset], after[offset]
                expected = new_index if current_part == part and a == old_index else a
                if b != expected:
                    raise ValueError(f'Unexpected swatch change/omission at LOD{lod} part{current_part} triangle{triangle}: {a}->{b}, expected {expected}')
                if before[offset+1:offset+73] != after[offset+1:offset+73]:
                    raise ValueError(f'Position/normal payload changed at LOD{lod} part{current_part} triangle{triangle}')
                geometry.update(before[offset+1:offset+73])
                changes += a != b
                offset += 73
            triangles += count
        if changes <= 0:
            raise ValueError(f'No target palette fields changed in LOD{lod}')
        rows.append({'lod':lod, 'triangles':triangles, 'changed_palette_bytes':changes})
    if offset != len(before):
        raise ValueError('Unexpected trailing or truncated SWB1 payload')
    return {'status':'PASS', 'scope':'Regenerated SWB1 differs only in the specified palette bytes; no binary patch performed',
            'part':part, 'old_index':old_index, 'new_index':new_index, 'parts':parts, 'lods':rows,
            'unchanged_geometry_payload_sha256':geometry.hexdigest(), 'uncompressed_bytes':len(before)}


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--before',type=Path,required=True)
    parser.add_argument('--after',type=Path,required=True)
    parser.add_argument('--report',type=Path)
    args=parser.parse_args()
    before=args.before.read_bytes()
    after=args.after.read_bytes()
    result=compare_bytes(gzip.decompress(before),gzip.decompress(after))
    result.update(before=str(args.before.resolve()),after=str(args.after.resolve()),
                  before_sha256=hashlib.sha256(before).hexdigest(),after_sha256=hashlib.sha256(after).hexdigest())
    text=json.dumps(result,indent=2)+'\n'
    if args.report:
        args.report.parent.mkdir(parents=True,exist_ok=True)
        args.report.write_text(text,encoding='utf-8')
    print(text,end='')


if __name__=='__main__':main()
