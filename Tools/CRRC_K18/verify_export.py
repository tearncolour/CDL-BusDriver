"""Independent, read-only SWB1/PNG parser. Writes only the requested out report."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys
import zlib

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
TRI = np.dtype([('swatch', 'u1'), ('v', '<f4', (3, 6))])
BUDGETS = (80000, 25000, 8000)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def sha(path):
    return digest(Path(path).read_bytes())


def require(ok, message):
    if not ok:
        raise ValueError(message)


def strict_inflate(data, wbits):
    d = zlib.decompressobj(wbits)
    raw = d.decompress(data) + d.flush()
    require(d.eof and not d.unused_data and not d.unconsumed_tail, 'Incomplete or trailing compressed stream')
    return raw


def png_decode(data):
    require(data[:8] == b'\x89PNG\r\n\x1a\n', 'Bad PNG signature')
    p = 8
    chunks, payloads, idat = [], {}, bytearray()
    ended = False
    while p < len(data):
        require(p + 12 <= len(data), 'Truncated PNG chunk')
        n, = struct.unpack_from('>I', data, p)
        require(p + 12 + n <= len(data), 'Truncated PNG payload')
        kind = data[p+4:p+8]
        body = data[p+8:p+8+n]
        crc, = struct.unpack_from('>I', data, p+8+n)
        require(zlib.crc32(kind+body) & 0xffffffff == crc, 'PNG CRC mismatch: ' + str(kind))
        chunks.append(kind.decode('ascii'))
        payloads.setdefault(kind, []).append(body)
        if kind == b'IDAT': idat.extend(body)
        p += n + 12
        if kind == b'IEND':
            require(n == 0, 'Nonempty PNG IEND')
            ended = True
            break
    require(ended and p == len(data), 'Missing IEND or trailing PNG bytes')
    require(chunks[0] == 'IHDR' and len(payloads.get(b'IHDR', [])) == 1, 'Invalid IHDR count/order')
    w, h, depth, colour, comp, filt, interlace = struct.unpack('>IIBBBBB', payloads[b'IHDR'][0])
    require((w, h) == (64, 64), 'Atlas dimensions are not 64x64')
    require(depth == 8 and colour in (2, 6) and comp == filt == interlace == 0, 'Unsupported atlas PNG encoding')
    bpp = 4 if colour == 6 else 3
    raw = strict_inflate(bytes(idat), 15)
    stride = w*bpp
    require(len(raw) == h*(stride+1), 'Wrong PNG scanline length')
    pixels = bytearray(h*stride)
    filters = []
    for y in range(h):
        mode = raw[y*(stride+1)]
        require(mode <= 4, 'Invalid PNG filter')
        filters.append(mode)
        for x in range(stride):
            v = raw[y*(stride+1)+1+x]
            left = pixels[y*stride+x-bpp] if x >= bpp else 0
            above = pixels[(y-1)*stride+x] if y else 0
            upperleft = pixels[(y-1)*stride+x-bpp] if y and x >= bpp else 0
            if mode == 1: v += left
            elif mode == 2: v += above
            elif mode == 3: v += (left+above)//2
            elif mode == 4:
                q = left+above-upperleft
                dl, da, du = abs(q-left), abs(q-above), abs(q-upperleft)
                v += left if dl <= da and dl <= du else above if da <= du else upperleft
            pixels[y*stride+x] = v & 255
    a = np.frombuffer(pixels, np.uint8).reshape(h, w, bpp)
    if bpp == 3:
        a = np.concatenate((a, np.full((h, w, 1), 255, np.uint8)), axis=2)
    return a, {'width': w, 'height': h, 'bit_depth': depth, 'colour_type': colour,
               'chunks': chunks, 'filter_types': sorted(set(filters)),
               'gamma_chunk': struct.unpack('>I', payloads[b'gAMA'][0])[0]/100000 if b'gAMA' in payloads else None,
               'srgb_rendering_intent': payloads[b'sRGB'][0][0] if b'sRGB' in payloads else None,
               'decoded_rgba_sha256': digest(a.tobytes()), 'crc_all_chunks': True,
               'exact_decompressed_length': len(raw), 'no_trailing_bytes': True}


def near(a, b, tolerance=2e-6):
    return np.max(np.abs(np.asarray(a)-np.asarray(b)), initial=0) <= tolerance


def swb_decode(data, reported, palette):
    raw = strict_inflate(data, 31)
    require(len(raw) >= 24 and raw[:4] == b'SWB1', 'Invalid SWB1 header')
    lods, parts, *eye = struct.unpack_from('<ii3f', raw, 4)
    require(lods == 3 and 34 <= parts <= 256, 'Invalid SWB1 LOD/part count')
    require(np.isfinite(eye).all() and max(map(abs, eye)) <= 100, 'Invalid driver eye')
    require(parts == reported['parts'] and near(eye, reported['eye']), 'Header differs from summary')
    require(len(reported['frames']) == parts and len(reported['lods']) == 3, 'Incomplete summary frames/LODs')
    offset, frames = 24, []
    for i in range(parts):
        parent, flags, *v = struct.unpack_from('<ii7f', raw, offset)
        offset += 36
        pivot, q = v[:3], v[3:]
        require(-1 <= parent < i and (i != 0 or parent == -1) and flags & ~15 == 0, 'Invalid part ancestry/flags')
        require(np.isfinite(v).all() and max(map(abs, pivot)) <= 100, 'Invalid part frame numbers')
        norm = float(np.linalg.norm(q))
        require(abs(norm-1) <= 1e-5, 'Nonunit rest quaternion')
        ref = reported['frames'][i]
        require(ref['index'] == i and ref['parent'] == parent and ref['flags'] == flags and
                near(pivot, ref['pivot_unity']) and near(q, ref['rest_rotation_unity']), 'Frame differs from summary')
        if i >= 34:
            require(parent == 0 and max(map(abs, q[:3])) <= 1e-6 and abs(abs(q[3])-1) <= 1e-6,
                    'Strap is not body-aligned or parented to Shell')
        frames.append({'index': i, 'parent': parent, 'flags': flags, 'pivot': pivot,
                       'rotation': q, 'quaternion_norm': norm})
    rows, total, failures = [], 0, []
    for lod in range(3):
        counts, swatches, per_part = [], set(), []
        for part in range(parts):
            require(offset+4 <= len(raw), 'Missing triangle count')
            n, = struct.unpack_from('<i', raw, offset)
            offset += 4
            require(0 <= n <= 2000000-total, 'Triangle reader limit exceeded')
            require(offset+73*n <= len(raw), 'Truncated triangle records')
            a = np.frombuffer(raw, dtype=TRI, count=n, offset=offset)
            offset += 73*n
            total += n
            counts.append(n)
            if not n: continue
            v = a['v'].astype(np.float64)
            pos, normals = v[:, :, :3], v[:, :, 3:]
            finite = np.isfinite(v).all(axis=(1, 2))
            cross = np.cross(pos[:, 1]-pos[:, 0], pos[:, 2]-pos[:, 0])
            area2 = np.einsum('ij,ij->i', cross, cross)
            norm_len = np.linalg.norm(normals, axis=2)
            dots = np.einsum('ij,ikj->ik', cross, normals)
            denom = np.sqrt(area2)[:, None]*norm_len
            normalized = np.divide(dots, denom, out=np.zeros_like(dots), where=denom>0)
            unique = [int(x) for x in np.unique(a['swatch'])]
            swatches.update(unique)
            diagnostics = {'nonfinite': int((~finite).sum()),
                'positions_out_of_range': int((np.abs(pos)>100).any(axis=(1, 2)).sum()),
                'degenerate': int((area2 < 1e-18).sum()),
                'nonunit_or_zero_corner_normals': int((np.abs(norm_len-1)>1e-3).sum()),
                'opposed_corner_normals': int((normalized < -1e-6).sum()),
                'opposed_or_zero_mean_normals': int((dots.sum(axis=1) <= 0).sum()),
                'invalid_palette_indices': int((a['swatch'] >= len(palette)).sum())}
            if any(diagnostics.values()):
                failures.append({'lod': lod, 'part': part, **diagnostics})
            per_part.append({'part': part, 'triangles': n, 'swatches': unique, **diagnostics,
                'min_cross_squared': float(area2.min()), 'minimum_corner_face_dot': float(normalized.min()),
                'maximum_normal_length_error': float(np.abs(norm_len-1).max()),
                'bounds_stored_space': {'min': pos.min(axis=(0,1)).tolist(), 'max': pos.max(axis=(0,1)).tolist()}})
        ref = reported['lods'][lod]
        require(ref['lod'] == lod and counts == ref['per_part'] and sum(counts) == ref['triangles'],
                'Actual SWB1 triangle counts differ from summary')
        rows.append({'lod': lod, 'triangles': sum(counts), 'per_part_counts': counts,
                     'swatches': sorted(swatches), 'nonempty_parts': per_part})
    require(total > 0 and offset == len(raw), 'Empty or trailing SWB1 payload')
    return {'status': 'FAIL' if failures else 'PASS', 'parts': parts, 'eye': eye, 'frames': frames,
            'lods': rows, 'total_triangles': total, 'failures': failures, 'full_parse_bytes': offset,
            'gzip_crc_and_size_verified': True, 'decompressed_sha256': digest(raw),
            'no_trailing_bytes': True, 'stored_space_note': 'Fixed parts model space; straps local to their attachment.'}


def srgb(v):
    return 12.92*v if v <= .0031308 else 1.055*v**(1/2.4)-.055


def expected_atlas(key, materials, glass):
    # Arrays are in Blender's bottom-up pixel order; PNG rows are top-down.
    default = {'BaseColor': (.01,.01,.01,1), 'Normal': (.5,.5,1,1),
               'MaskMap': (0,1 if glass else 0,0,.3),
               'ControlMask': (0,0,0,1), 'Emissive0': (0,0,0,1), 'EmissiveID0': (0,0,0,1)}[key]
    a = np.empty((64,64,4), np.float64); a[:] = default
    for m in materials:
        row, col = divmod(m['tile'], 16)
        rgb = tuple(srgb(float(v)) for v in m['source_linear_rgb'])
        require(all(np.isfinite(rgb)) and all(0 <= v <= 1 for v in rgb), 'Invalid source atlas RGB')
        light, livery = m['light_id'], m['livery_control']
        require(isinstance(light, int) and 0 <= light <= 9, 'Invalid lamp ID')
        value = default
        if key == 'BaseColor': value = (*((1,1,1) if livery else rgb), m['alpha'])
        elif key == 'MaskMap': value = (m['metallic'], 1 if glass else 0, 0, 1-m['roughness'])
        elif key == 'ControlMask' and livery: value = (1,0,0,1)
        elif key == 'Emissive0' and light: value = (*rgb,1)
        elif key == 'EmissiveID0' and light: value = (light/255,0,0,1)
        require(all(np.isfinite(value)) and all(0 <= v <= 1 for v in value), 'Atlas value outside 0..1')
        a[row*4:(row+1)*4, col*4:(col+1)*4] = value
    return np.floor(a[::-1]*255+.5).astype(np.uint8)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--summary', type=Path, default=ROOT/'ModelSource/CRRC_K18/export-summary.json')
    parser.add_argument('--expected-source-sha256', required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    report = {'schema_version': 1, 'status': 'FAILED', 'failures': [], 'runtime': {}, 'pngs': [],
              'scope': 'Independent exported SWB1 and PNG validation, not source visual/native-reader/game acceptance.'}
    files_before = {}
    def fail(message): report['failures'].append(message)
    def bind(path, expected=None, byte_count=None):
        path = Path(path).resolve()
        blob = path.read_bytes(); h = digest(blob)
        if expected is not None and h != expected: fail('Hash mismatch: '+str(path))
        if byte_count is not None and len(blob) != byte_count: fail('Byte count mismatch: '+str(path))
        files_before[str(path)] = h
        return blob
    try:
        summary_data = bind(args.summary)
        s = json.loads(summary_data)
        report.update(source_sha256=s['source_sha256'], export_summary_sha256=digest(summary_data),
                      exporter_sha256=s['exporter_sha256'], validator_sha256=sha(__file__))
        require(s['source_sha256'] == args.expected_source_sha256.lower(), 'Wrong requested source generation')
        bind(s['source'], s['source_sha256'])
        bind(ROOT/'Tools/CRRC_K18/export_model.py', s['exporter_sha256'])
        require(s['status'] == 'EXPORTED_DRAFT' and s['source_file_unchanged'] and
                s['source_state_sha256_before'] == s['source_state_sha256_after'], 'Unsuccessful or source-mutating export')
        palette_doc = json.loads(bind(s['palette']['path'], s['palette']['sha256'], s['palette']['bytes']))
        palette = palette_doc['palette']
        require([p['index'] for p in palette] == list(range(len(palette))) and len(palette) <= 256,
                'Invalid palette index contract')
        report['palette_rows'] = len(palette)
        require(set(s['sections']) == {'front','rear'}, 'Missing front/rear sections')
        artifacts, expected_png, expected_fbx = 1, set(), set()
        for section, model in (('front','CRRCK18'),('rear','CRRCK18Rear')):
            sec = s['sections'][section]
            require(sec['model'] == model, 'Section model mismatch')
            rt = sec['runtime']; blob = bind(rt['path'], rt['sha256'], rt['bytes']); artifacts += 1
            decoded = swb_decode(blob, rt, palette)
            decoded.update(path=rt['path'], sha256=digest(blob))
            report['runtime'][section] = decoded
            if decoded['failures']: fail(section+': SWB1 geometry diagnostics failed')
            if section == 'rear':
                require(all(l['per_part_counts'][19] == l['per_part_counts'][20] == 0 for l in decoded['lods']),
                        'Rear section has a fictitious front axle')
            require(len(sec['native']) == 6 and len(sec['atlases']) == 6, 'Wrong native/atlas inventory')
            by_native = {Path(n['path']).stem: n for n in sec['native']}
            for n in sec['native']:
                bind(n['path'], n['sha256'], n['bytes']); artifacts += 1; expected_fbx.add(str(Path(n['path']).resolve()))
            for atlas in sec['atlases']:
                name = atlas['name']; native = by_native[name]; glass = native['glass']
                mats = atlas['materials']
                require([m['tile'] for m in mats] == list(range(len(mats))) and 0 < len(mats) <= 256,
                        'Noncontiguous atlas tiles')
                keys = ('BaseColor','MaskMap','Normal') if glass else ('BaseColor','MaskMap','Normal','ControlMask','Emissive0','EmissiveID0')
                require({Path(p['path']).stem[len(name)+1:] for p in atlas['files']} == set(keys), 'Wrong PNG role inventory')
                for f in atlas['files']:
                    path = Path(f['path']); key = path.stem[len(name)+1:]
                    data = bind(path, f['sha256'], f['bytes']); artifacts += 1; expected_png.add(str(path.resolve()))
                    px, meta = png_decode(data)
                    wanted = expected_atlas(key, mats, glass)
                    delta = np.abs(px.astype(np.int16)-wanted.astype(np.int16))
                    # At most one quantization byte allowed; IDs/control/normal are exact.
                    tolerance = 1 if key in ('BaseColor','MaskMap','Emissive0') else 0
                    bad = np.any(delta>tolerance, axis=2)
                    if bad.any(): fail(path.name+': atlas pixels mismatch ('+str(int(bad.sum()))+' pixels)')
                    report['pngs'].append({'path':str(path.resolve()), 'sha256':digest(data), **meta,
                        'role':key, 'glass':glass, 'material_tiles':len(mats), 'compared_pixels':4096,
                        'max_channel_error_bytes':int(delta.max()), 'mismatched_pixels':int(bad.sum()),
                        'tolerance_bytes':tolerance, 'status':'FAIL' if bad.any() else 'PASS'})
        require(len(expected_png) == 54 and len(expected_fbx) == 12 and artifacts == 69, 'Wrong total artifact inventory')
        actual_png, actual_fbx = set(), set()
        for model in ('CRRCK18','CRRCK18Rear'):
            folder = ROOT/'Asset'/model
            actual_png.update(str(p.resolve()) for p in folder.glob('*.png'))
            actual_fbx.update(str(p.resolve()) for p in folder.glob('*.fbx'))
        require(actual_png == expected_png and actual_fbx == expected_fbx, 'Extra/missing asset PNG or FBX')
        report['verified_artifact_hashes'] = artifacts
        report['native_fbx_scope'] = 'All 12 bytes/hashes bound; native parser and mesh validation owned by parent reviewer.'
        report['budgets'] = []
        for lod, limit in enumerate(BUDGETS):
            counts = {k: sum(n['triangles'] for n in sec['native'] if n['lod'] == lod and not n['glass'])
                      for k, sec in s['sections'].items()}
            count = sum(counts.values())
            report['budgets'].append({'lod':lod, 'suggested_body_limit':limit, 'section_triangles':counts,
                'combined_native_body_triangles':count, 'combined_within_limit':count<=limit,
                'status':'PASS' if count<=limit and max(counts.values())<=limit else 'EXCEEDED'})
        report['budget_count_provenance'] = 'Native triangle counts in hash-bound exporter summary; parent native-reader must independently match.'
        report['budget_status'] = 'EXCEEDED' if any(b['status'] == 'EXCEEDED' for b in report['budgets']) else 'PASS'
        report['technical_validation_status'] = 'FAIL' if report['failures'] else 'PASS'
        report['status'] = 'FAILED' if report['failures'] else 'DRAFT_EXPORT_BUDGET_EXCEEDED' if report['budget_status']=='EXCEEDED' else 'DRAFT_EXPORT_VERIFIED'
    except Exception as error:
        fail(repr(error))
        report['status'] = 'FAILED'
    finally:
        changed = [p for p, h in files_before.items() if not Path(p).exists() or sha(p) != h]
        report['inputs_unchanged'] = not changed
        if changed:
            fail({'changed_during_validation':changed}); report['status'] = 'FAILED'
        report['bound_input_hashes'] = files_before
        report['mask_g_contract'] = 'Current K18 opaque source materials have no coat; glass has unoccluded AO=1. Any change requires explicit updated source evidence.'
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'status':report['status'], 'failures':report['failures'], 'report':str(args.report)}), flush=True)
    return 1 if report['status']=='FAILED' else 0


if __name__ == '__main__':
    sys.exit(main())
