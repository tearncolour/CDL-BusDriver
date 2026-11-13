"""Trace every current magenta pixel against actual compiled/posed Kit meshes.

Run with Blender's Python (mathutils BVH), after run_review.ps1. No source .blend
is loaded and no model geometry is changed. Reports retain each pixel, rather
than accepting a historical component count as proof.
"""
import hashlib
import json
import math
import struct
import sys
import zlib
from collections import defaultdict, deque
from pathlib import Path

import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
HERE = Path(args[0]).resolve() if args else Path(__file__).resolve().parent
ROOT = next(path for path in HERE.parents if (path / 'ModelSource').is_dir())


def png(path):
    raw = path.read_bytes()
    at = 8
    compressed = bytearray()
    while at < len(raw):
        count = struct.unpack_from('>I', raw, at)[0]
        kind = raw[at + 4:at + 8]
        block = raw[at + 8:at + 8 + count]
        at += count + 12
        if kind == b'IHDR':
            width, height, bits, mode, _, _, _ = struct.unpack('>IIBBBBB', block)
            assert bits == 8 and mode == 2
        elif kind == b'IDAT':
            compressed.extend(block)
    decoded = np.frombuffer(zlib.decompress(compressed), dtype=np.uint8).reshape(height, width * 3 + 1)
    assert not np.any(decoded[:, 0]), 'This reader expects the existing Preview PNG writer filter 0'
    return decoded[:, 1:].reshape(height, width, 3)


def write_png(path, rgb):
    height, width, _ = rgb.shape
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    rows = np.concatenate([np.zeros((height, 1), dtype=np.uint8), rgb.reshape(height, -1)], axis=1)
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
                     + chunk(b'IDAT', zlib.compress(rows.tobytes(), 6)) + chunk(b'IEND', b''))


blob = (HERE / 'runtime-geometry.dat').read_bytes()
offset = 0


def take(fmt):
    global offset
    values = struct.unpack_from(fmt, blob, offset)
    offset += struct.calcsize(fmt)
    return values[0] if len(values) == 1 else values


def text():
    length = 0
    shift = 0
    while True:
        byte = take('<B')
        length |= (byte & 127) << shift
        if byte < 128:
            break
        shift += 7
    global offset
    value = blob[offset:offset + length].decode('utf-8')
    offset += length
    return value


assert blob[:4] == b'KRV1'
offset = 4
parts, lods = take('<ii')
meshes = [[] for _ in range(lods)]
names = []
for part in range(parts):
    names.append(text())
    for lod in range(lods):
        vertices, indices = take('<ii')
        positions = np.frombuffer(blob, '<f4', vertices * 3, offset).reshape(-1, 3).astype(np.float64)
        offset += vertices * 12
        triangles = np.frombuffer(blob, '<i4', indices, offset).reshape(-1, 3).copy()
        offset += indices * 4
        meshes[lod].append((positions, triangles))
poses = {}
for _ in range(take('<i')):
    pose = text()
    rows = []
    for part in range(parts):
        visible = bool(take('<?'))
        matrix = np.array(take('<16f')).reshape(4, 4).T
        rows.append((visible, matrix))
    poses[pose] = rows
assert offset == len(blob)

manifest = json.loads((HERE / 'render-manifest.json').read_text(encoding='utf-8-sig'))
image_rows = []
groups = defaultdict(list)
for source in manifest['images']:
    rgb = png(ROOT / source['path'])
    mask = (rgb[:, :, 0] > 240) & (rgb[:, :, 1] < 15) & (rgb[:, :, 2] > 240)
    pixels = [(int(x), int(y)) for y, x in zip(*np.where(mask))]
    todo = set(pixels)
    components = []
    while todo:
        seed = todo.pop()
        queue = deque([seed])
        connected = [seed]
        while queue:
            x, y = queue.popleft()
            for point in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                if point in todo:
                    todo.remove(point)
                    queue.append(point)
                    connected.append(point)
        components.append({'count': len(connected), 'bounds_xyxy': [min(p[0] for p in connected), min(p[1] for p in connected), max(p[0] for p in connected), max(p[1] for p in connected)]})
    row = dict(source, pink_pixels=len(pixels), components=components, pixel_hits=[])
    image_rows.append(row)
    if pixels:
        groups[(source['lod'], source['pose'])].append((row, pixels))
    print('PIXELS', source['lod'], source['pose'], source['name'], len(pixels), flush=True)

for (lod, pose), images in groups.items():
    positions, triangles, ids = [], [], []
    first_vertex = 0
    for part, ((vertices, faces), (visible, matrix)) in enumerate(zip(meshes[lod], poses[pose])):
        if not visible or not len(faces):
            continue
        transformed = vertices @ matrix[:3, :3].T + matrix[:3, 3]
        positions.append(transformed)
        triangles.append(faces + first_vertex)
        ids.append(np.full(len(faces), part, dtype=np.int32))
        first_vertex += len(vertices)
    vertices = np.concatenate(positions)
    faces = np.concatenate(triangles)
    part_ids = np.concatenate(ids)
    tri = vertices[faces]
    crosses = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    bvh = BVHTree.FromPolygons(vertices.tolist(), faces.tolist(), all_triangles=True, epsilon=0.0)
    for row, pixels in images:
        eye = np.array(row['eye'], dtype=float)
        forward = np.array(row['target'], dtype=float) - eye
        forward /= np.linalg.norm(forward)
        right = np.cross([0., 1., 0.], forward)
        right /= np.linalg.norm(right)
        up = np.cross(forward, right)
        focal = 300. / math.tan(math.radians(row['fov']) * .5)

        def cast(x, y):
            direction = forward + right * ((x + .5 - 480.) / focal) + up * ((300. - y - .5) / focal)
            direction /= np.linalg.norm(direction)
            near = .03000001 / np.dot(direction, forward)
            hit, _, index, distance = bvh.ray_cast(Vector(eye + direction * near), Vector(direction))
            if index is None:
                return {'front': None, 'no_hit': True}
            a, b, c = tri[index]
            direction_dot = float(np.dot(crosses[index], eye - a))
            return {'front': direction_dot > 0., 'part': int(part_ids[index]), 'part_name': names[part_ids[index]],
                    'triangle': int(index), 'point': list(hit), 'distance_m': float(distance + near),
                    'orientation_dot': direction_dot}

        for x, y in pixels:
            hit = cast(x, y)
            hit['pixel'] = [x, y]
            hit['subpixel_offsets'] = [dict(offset=[dx, dy], **cast(x + dx, y + dy))
                                       for dx, dy in ((.001, 0.), (-.001, 0.), (0., .001), (0., -.001))]
            row['pixel_hits'].append(hit)
        row['nearest_ray_front_pixels'] = sum(hit['front'] is True for hit in row['pixel_hits'])
        row['nearest_ray_back_pixels'] = sum(hit['front'] is False for hit in row['pixel_hits'])
        print('RAYS', lod, pose, row['name'], row['nearest_ray_front_pixels'], row['nearest_ray_back_pixels'], flush=True)
    del bvh, vertices, faces, tri

report = {
    'model': 'SWB6109EV12G', 'binary_sha256': manifest['binary_sha256'],
    'compiled_test_dll_sha256': manifest['compiled_test_dll_sha256'],
    'geometry_dump_sha256': hashlib.sha256(blob).hexdigest(),
    'method': 'Every magenta pixel traced against the compiled KitBuild output under the exact BusInterior.Apply pose and visible flags, with 0.001-pixel neighbouring rays. No historical pixel counts accepted.',
    'images': image_rows,
    'totals': {'images': len(image_rows), 'pink_pixels': sum(row['pink_pixels'] for row in image_rows),
               'nearest_ray_front_pixels': sum(row.get('nearest_ray_front_pixels', 0) for row in image_rows),
               'nearest_ray_back_pixels': sum(row.get('nearest_ray_back_pixels', 0) for row in image_rows)},
}
(HERE / 'pixel-attribution.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report['totals']), flush=True)

# Focused reviews (for example the six mirror mounting views) still run the
# identical per-pixel audit, but do not contain the standard cabin sheet set.
if '--no-contact-sheets' in args:
    raise SystemExit(0)

# Comparison sheets retain all images in the manifest; labels/order are in JSON.
categories = [
    ['cab', 'cab-up', 'cab-door', 'saloon', 'rear'],
    ['front-aisle-low', 'rear-aisle-low', 'front-wheelhouse-left', 'front-wheelhouse-right', 'rear-wheelhouse-left', 'rear-wheelhouse-right'],
    ['ceiling-front', 'ceiling-rear', 'front-door', 'middle-door'],
]
for group_index, category in enumerate(categories):
    canvas = np.full((len(category) * 304, 3 * 484, 3), 245, dtype=np.uint8)
    for y, name in enumerate(category):
        for lod in range(3):
            row = next(r for r in image_rows if r['name'] == name and r['lod'] == lod and r['pose'] == 'open')
            rgb = png(ROOT / row['path'])[::2, ::2]
            canvas[y * 304:y * 304 + 300, lod * 484:lod * 484 + 480] = rgb
    write_png(HERE / f'contact-sheet-{group_index + 1}.png', canvas)
