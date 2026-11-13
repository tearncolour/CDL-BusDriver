"""Make an audited candidate copy removing one native trailer back-reference.

No import, CID changes, deployment or writes to the input package.
"""
import argparse
from copy import copy
import hashlib
import json
from pathlib import Path
import re
import tempfile
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def inspect_pair(entries, front, rear):
    roots = {}
    for filename, data in entries.items():
        if not filename.endswith('.Prefab'):
            continue
        match = re.search(rb'"name"\s*:\s*"([^"\\]+)"', data)
        if not match or match.group(1).decode('utf-8') not in (front, rear):
            continue
        name = match.group(1).decode('utf-8')
        if name in roots:
            raise ValueError('Duplicate native root ' + name)
        roots[name] = filename
    if set(roots) != {front, rear}:
        raise ValueError('Both exact native roots must exist')
    for name, expected in ((front, b'Game.Prefabs.CarPrefab, Game'), (rear, b'Game.Prefabs.CarTrailerPrefab, Game')):
        if expected not in entries[roots[name]].split(b'"name"', 1)[0]:
            raise ValueError('Wrong native root type: ' + name)
    cids = {name: entries[filename + '.cid'].decode('ascii').strip() for name, filename in roots.items()}
    if any(not re.fullmatch(r'[0-9a-fA-F]{32}', cid) for cid in cids.values()) or cids[front] == cids[rear]:
        raise ValueError('Invalid or equal root CIDs')
    return roots, cids


def repair(source, output, front, rear, expected_sha):
    source, output = Path(source).resolve(), Path(output).resolve()
    if source == output or output.exists():
        raise ValueError('Output must be a new candidate, never the source or an existing file')
    raw = source.read_bytes()
    if digest(raw).lower() != expected_sha.lower():
        raise ValueError('Source package hash differs from reviewed input')
    with zipfile.ZipFile(source) as archive:
        infos = archive.infolist()
        if len({x.filename.casefold() for x in infos}) != len(infos):
            raise ValueError('Duplicate ZIP entries')
        entries = {x.filename: archive.read(x) for x in infos}
        comment = archive.comment
    metadata_fields = ('compress_type', 'date_time', 'external_attr', 'internal_attr',
                       'create_system', 'create_version', 'extract_version', 'extra', 'comment')
    # ZipFile.writestr mutates the supplied ZipInfo.  Snapshot values before
    # writing so validation cannot accidentally compare an object to itself.
    original_metadata = {info.filename: {field: getattr(info, field) for field in metadata_fields}
                         for info in infos}
    roots, cids = inspect_pair(entries, front, rear)
    forward = rb'"m_FixedTrailer"\s*:\s*\$fstrref:"CID:' + cids[rear].encode() + rb'"'
    if len(re.findall(forward, entries[roots[front]])) != 1:
        raise ValueError('Front must have exactly one fixed reference to the declared rear')
    reverse = rb'("m_FixedTractor"\s*:\s*)\$fstrref:"CID:' + cids[front].encode() + rb'"'
    changed = roots[rear]
    patched, count = re.subn(reverse, lambda m: m.group(1) + b'null', entries[changed])
    if count != 1:
        raise ValueError('Rear must have exactly one reciprocal fixed reference to remove')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(prefix='.' + output.name + '-', suffix='.tmp', dir=output.parent, delete=False) as temp:
        temporary = Path(temp.name)
    try:
        with zipfile.ZipFile(temporary, 'w') as archive:
            archive.comment = comment
            for info in infos:
                written = copy(info)
                archive.writestr(written, patched if info.filename == changed else entries[info.filename])
                # Python treats zero as an unspecified mode and substitutes
                # 0600.  Restore the authored value before the central directory
                # is emitted; external attributes are not in the local header.
                written.external_attr = original_metadata[info.filename]['external_attr']
        with zipfile.ZipFile(temporary) as archive:
            after = {x.filename: archive.read(x) for x in archive.infolist()}
            if set(after) != set(entries) or archive.comment != comment:
                raise ValueError('Entry set or archive comment changed')
            for info in infos:
                current = archive.getinfo(info.filename)
                if any(getattr(current, field) != original_metadata[info.filename][field]
                       for field in metadata_fields):
                    raise ValueError('ZIP metadata changed: ' + info.filename)
                if after[info.filename] != (patched if info.filename == changed else entries[info.filename]):
                    raise ValueError('Unexpected entry content change: ' + info.filename)
            inspect_pair(after, front, rear)
            if not re.search(rb'"m_FixedTractor"\s*:\s*null', after[changed]):
                raise ValueError('Trailer back-reference was not removed')
        temporary.replace(output)
    finally:
        if temporary.exists():
            temporary.unlink()
    return {'status': 'CANDIDATE_BYTES_VERIFIED_GAME_VALIDATION_PENDING', 'source': str(source), 'output': str(output),
            'source_sha256': digest(raw), 'output_sha256': digest(output.read_bytes()), 'changed_entry': changed,
            'changed_entries': 1, 'unchanged_entries': len(entries) - 1, 'root_cids': cids,
            'original_entry_sha256': digest(entries[changed]), 'patched_entry_sha256': digest(patched),
            'edit': 'rear.m_FixedTractor: CID:' + cids[front] + ' -> null; front fixed rear retained',
            'all_other_uncompressed_bytes_identical': True, 'all_cids_identical': True,
            'zip_metadata_identical': True, 'deployed': False}


if __name__ == '__main__':
    cli = argparse.ArgumentParser(description=__doc__)
    for option in ('source', 'output', 'front', 'rear', 'expected-sha256'):
        cli.add_argument('--' + option, required=True)
    args = cli.parse_args()
    result = repair(args.source, args.output, args.front, args.rear, args.expected_sha256)
    Path(args.output + '.repair.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))
