"""Validate SWB1 normals, frames and tyre clearance after LOD reduction.

Run with Python 3 from any directory. Exit 1 means the binary is malformed,
cannot be loaded safely by VehicleMesh (Interior/Models/VehicleMesh.cs), violates its frame contract, or contains
body geometry inside a tyre clearance cylinder at any exported LOD. Front
wheels are tested straight and at +/-15, +/-25 and +/-35 degrees of steering.
"""

import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import struct
import sys


PART_NAMES = (
    'Shell', 'SteeringWheel', 'SpeedNeedle', 'RpmNeedle',
    'Door0A', 'Door0B', 'Door1A', 'Door1B', 'Door2A', 'Door2B',
    'Fittings', 'Dash', 'Cab', 'Poles', 'Ceiling', 'Outside',
    'Exterior', 'Glazing', 'Livery', 'WheelFL', 'WheelFR', 'WheelRL', 'WheelRR',
    'HeadLamps', 'TailLamps', 'BrakeLamps', 'IndicatorL', 'IndicatorR', 'ReverseLamps',
    'WrapLeft', 'WrapRight', 'WrapFront', 'WrapRear', 'WrapRoof',
)
HEADER = struct.Struct('<4sii3f')
DRIVE_BIT = 0x10000   # VehicleMesh.DriveBit: a door drive part
SURFACE_BIT = 0x20000   # VehicleMesh.SurfaceBit: a wrap surface part
FRAME = struct.Struct('<ii7f')
COUNT = struct.Struct('<i')
TRIANGLE = struct.Struct('<B18f')
MAX_TRIANGLES = 2_000_000
FRONT_STEERING_ANGLES_DEG = (-35, -25, -15, 0, 15, 25, 35)


def clip_x(poly, limit, keep_above):
    """Sutherland-Hodgman clipping to one face of a tyre's axial slab."""
    out = []
    if not poly:
        return out
    previous = poly[-1]
    previous_inside = previous[0] >= limit if keep_above else previous[0] <= limit
    for current in poly:
        current_inside = current[0] >= limit if keep_above else current[0] <= limit
        if previous_inside != current_inside:
            fraction = (limit - previous[0]) / (current[0] - previous[0])
            out.append(tuple(previous[k] + fraction * (current[k] - previous[k]) for k in range(3)))
        if current_inside:
            out.append(current)
        previous, previous_inside = current, current_inside
    return out


def projected_polygon_overlaps_circle(poly, center_y, center_z, radius):
    """Check vertices, closest points on edges, and circle center containment.

    After clipping the triangle to the X slab, its Y/Z projection is convex.
    These three cases completely cover intersection with the circular section.
    Exact boundary contact is accepted; a point strictly inside is penetration.
    """
    points = [(p[1] - center_y, p[2] - center_z) for p in poly]
    radius_squared = radius * radius
    signs = []
    for a, b in zip(points, points[1:] + points[:1]):
        if a[0] * a[0] + a[1] * a[1] < radius_squared:
            return True
        dx, dz = b[0] - a[0], b[1] - a[1]
        length_squared = dx * dx + dz * dz
        if length_squared > 1e-20:
            fraction = max(0.0, min(1.0, -(a[0] * dx + a[1] * dz) / length_squared))
            if (a[0] + fraction * dx) ** 2 + (a[1] + fraction * dz) ** 2 < radius_squared:
                return True
        cross = dx * (-a[1]) - dz * (-a[0])
        if abs(cross) > 1e-12:
            signs.append(cross > 0)
    return len(signs) >= 3 and (all(signs) or not any(signs))


def triangle_hits_tyre(points, center, radius, half_width):
    x, y, z = center
    if max(p[0] for p in points) < x - half_width or min(p[0] for p in points) > x + half_width:
        return False
    if max(p[1] for p in points) < y - radius or min(p[1] for p in points) > y + radius:
        return False
    if max(p[2] for p in points) < z - radius or min(p[2] for p in points) > z + radius:
        return False
    poly = clip_x(clip_x(points, x - half_width, True), x + half_width, False)
    return bool(poly) and projected_polygon_overlaps_circle(poly, y, z, radius)


def triangle_hits_steered_tyre(points, center, radius, half_width, cosine, sine):
    """Exact same cylinder test after inverse steering about Unity's up axis.

    A steered wheel's axis is no longer model X. First reject by the oriented
    cylinder's world bounds, then rotate the triangle into wheel-local space.
    This tests the complete triangle, including intersections away from its
    vertices; a rotated AABB alone is not a collision test.
    """
    cx, cy, cz = center
    extent_x = half_width * abs(cosine) + radius * abs(sine)
    extent_z = radius * abs(cosine) + half_width * abs(sine)
    if max(p[0] for p in points) < cx-extent_x or min(p[0] for p in points) > cx+extent_x:
        return False
    if max(p[1] for p in points) < cy-radius or min(p[1] for p in points) > cy+radius:
        return False
    if max(p[2] for p in points) < cz-extent_z or min(p[2] for p in points) > cz+extent_z:
        return False
    local = [(cosine*(p[0]-cx)-sine*(p[2]-cz), p[1]-cy,
              sine*(p[0]-cx)+cosine*(p[2]-cz)) for p in points]
    return triangle_hits_tyre(local, (0., 0., 0.), radius, half_width)


def cross_squared(points):
    a = [points[1][k] - points[0][k] for k in range(3)]
    b = [points[2][k] - points[0][k] for k in range(3)]
    return ((a[1] * b[2] - a[2] * b[1]) ** 2 +
            (a[2] * b[0] - a[0] * b[2]) ** 2 +
            (a[0] * b[1] - a[1] * b[0]) ** 2)


def audit(binary, spec, radial_clearance=0., half_width=.103, max_samples=10, envelope_radius=None):
    packed = binary.read_bytes()
    report = {
        'status': 'FAIL', 'binary': str(binary.resolve()),
        'sha256': hashlib.sha256(packed).hexdigest(), 'compressed_bytes': len(packed),
        'errors': [], 'lods': [],
        'clearance_check': {
            'space': 'Unity model metres; +X right, +Y up, +Z front',
            'method': 'Triangle clipped to tyre axial slab, then exact convex polygon/circle overlap in YZ.',
            'radial_clearance_m': radial_clearance, 'tested_half_width_m': half_width,
            'tested_parts': 'All parts except the four wheel parts 19..22',
            'front_steering_angles_degrees': list(FRONT_STEERING_ANGLES_DEG),
            'front_steering_sampling_range_degrees': [min(FRONT_STEERING_ANGLES_DEG), max(FRONT_STEERING_ANGLES_DEG)],
            'steering_method': 'Inverse yaw into each wheel frame, exact triangle/cylinder intersection at every listed pose; rear wheels remain straight.',
        },
    }
    errors = report['errors']
    data = gzip.decompress(packed)
    report['uncompressed_bytes'] = len(data)
    offset = 0

    def read(record):
        nonlocal offset
        if offset + record.size > len(data):
            raise ValueError(f'Truncated SWB1 at byte {offset}; expected {record.size} more bytes.')
        value = record.unpack_from(data, offset)
        offset += record.size
        return value

    magic, lod_count, part_count, *eye = read(HEADER)
    # the 34 contract parts, then optional extra moving parts (hand straps: Strap0, Strap1, ...)
    if magic != b'SWB1' or lod_count != 3 or part_count < len(PART_NAMES) or part_count > 256:
        raise ValueError(f'Unsupported SWB1 header: {magic!r}, {lod_count} LODs, {part_count} parts.')
    report.update(format='SWB1', part_count=part_count, lod_count=lod_count, eye=eye)
    # the 34 contract parts, then the door drives (frame bit 0x10000: DoorDrive0, ...), then the straps (Strap0, ...)
    if not all(math.isfinite(x) and abs(x) <= 100 for x in eye):
        errors.append('Driver eye contains invalid coordinates.')
    frames = []
    names = []
    drives = 0
    surfaces = 0
    for index in range(part_count):
        parent, flags, *values = read(FRAME)
        drive = bool(flags & DRIVE_BIT)
        surface = bool(flags & SURFACE_BIT)
        if index < len(PART_NAMES):
            name = PART_NAMES[index]
        elif drive:
            name = f'DoorDrive{drives}'
            drives += 1
        elif surface:
            name = f'WrapSurface{surfaces}'
            surfaces += 1
        else:
            name = f'Strap{index - len(PART_NAMES) - drives - surfaces}'
        names.append(name)
        pivot, rotation = values[:3], values[3:]
        valid = all(math.isfinite(x) for x in values)
        if parent < -1 or parent >= index or (index == 0 and parent != -1) or flags & ~(15 | DRIVE_BIT | SURFACE_BIT) or (drive and surface):
            errors.append(f'Part {index} {name}: invalid parent or flags.')
        if surface and (index != len(PART_NAMES) + drives + surfaces - 1 or not (parent == 0 or 4 <= parent <= 9) or
                        any(abs(a - b) > 1e-6 for a, b in zip(values[3:], (0, 0, 0, 1)))):
            errors.append(f'Part {index} {name}: a wrap surface follows the door drives, hangs from the shell or a door leaf, in the body axes.')
        if drive and (index != len(PART_NAMES) + drives - 1 or parent != 0 or any(abs(a - b) > 1e-6 for a, b in zip(rotation, (0, 0, 0, 1)))):
            errors.append(f'Part {index} {name}: a door drive must follow the contract parts (before the straps), under the shell, in the body axes.')
        if not valid or any(abs(x) > 100 for x in pivot):
            errors.append(f'Part {index} {name}: nonfinite or excessive frame coordinates.')
        if not valid or abs(sum(x * x for x in rotation) - 1) > 1e-4:
            errors.append(f'Part {index} {name}: rest quaternion is not normalized.')
        frames.append({'index': index, 'name': name, 'parent': parent,
                       'flags': flags, 'pivot': pivot, 'rotation_xyzw': rotation})
    report['frames'] = frames
    gap = frames[1]['pivot'][2] - eye[2]
    report['steering_hub_ahead_of_eye_m'] = gap
    if not math.isfinite(gap) or gap <= 0:
        errors.append('Steering hub is not forward of the driver eye (+Z).')

    length = float(spec.get('length_m', 7.145))
    front_z = length / 2 - float(spec.get('front_overhang_m', 1.2))
    rear_z = -length / 2 + float(spec.get('rear_overhang_m', .845))
    radius = float(spec.get('wheel_radius_m', .385))
    # Include the authored fine tread rings, without moving the wheel hubs.
    outer_radius = radius + .004 if envelope_radius is None else float(envelope_radius)
    if not (0 <= radial_clearance < outer_radius and half_width > 0 and math.isfinite(outer_radius)):
        raise ValueError('Tyre dimensions or clearance are invalid.')
    wheels = []
    for index, name in enumerate(PART_NAMES[19:23], 19):
        is_front, is_left = index < 21, index in (19, 21)
        track = float(spec.get('front_track_m' if is_front else 'rear_track_m', 1.939 if is_front else 1.808))
        expected = [(-1 if is_left else 1) * track / 2, radius, front_z if is_front else rear_z]
        frame = frames[index]
        if max(abs(a - b) for a, b in zip(frame['pivot'], expected)) > 1e-4:
            errors.append(f'Part {index} {name}: pivot does not match axle, track and tyre specifications.')
        q = frame['rotation_xyzw']
        if max(abs(x) for x in q[:3]) > 1e-5 or abs(abs(q[3]) - 1) > 1e-5:
            errors.append(f'Part {index} {name}: runtime road-wheel rest rotation must be identity.')
        wheels.append((name, tuple(expected)))
    report['clearance_check']['wheel_hub_height_m'] = radius
    report['clearance_check']['outer_envelope_radius_m'] = outer_radius
    report['clearance_check']['effective_radius_m'] = outer_radius - radial_clearance
    wheel_poses = [(name, center, angle, math.cos(math.radians(angle)), math.sin(math.radians(angle)))
                   for name, center in wheels
                   for angle in (FRONT_STEERING_ANGLES_DEG if name in ('WheelFL', 'WheelFR') else (0,))]

    total = 0
    for lod in range(lod_count):
        row = {'lod': lod, 'triangles': 0, 'zero_normals': 0, 'nonunit_normals': 0,
               'nonfinite_vertices': 0, 'degenerate_triangles': 0, 'unknown_swatches': 0,
               'tyre_penetration_count': 0, 'parts': [],
               'steering_pose_penetrations': {str(angle): 0 for angle in FRONT_STEERING_ANGLES_DEG}}
        for part, name in enumerate(names):
            count, = read(COUNT)
            if count < 0 or total + count > MAX_TRIANGLES:
                raise ValueError(f'LOD {lod} part {part}: invalid/excessive triangle count {count}.')
            total += count
            row['triangles'] += count
            part_row = {'index': part, 'name': name, 'triangles': count,
                        'tyre_penetration_count': 0, 'tyre_samples': []}
            for triangle_index in range(count):
                triangle = read(TRIANGLE)
                if triangle[0] >= PALETTE_ROWS:  # the MeshKit palette's rows (kit-cabin.json; it only grows)
                    row['unknown_swatches'] += 1
                points = [triangle[k:k+3] for k in (1, 7, 13)]
                finite = all(math.isfinite(x) and abs(x) <= 100 for p in points for x in p)
                if not finite:
                    row['nonfinite_vertices'] += 1
                for k in (4, 10, 16):
                    normal = triangle[k:k+3]
                    length_squared = sum(x*x for x in normal)
                    if not math.isfinite(length_squared) or length_squared < 1e-8:
                        row['zero_normals'] += 1
                    elif abs(length_squared - 1) > 1e-3:
                        row['nonunit_normals'] += 1
                if not finite:
                    continue
                if cross_squared(points) < 1e-20:
                    row['degenerate_triangles'] += 1
                    continue
                if 19 <= part <= 22:
                    continue
                for wheel_name, center, angle, cosine, sine in wheel_poses:
                    if triangle_hits_steered_tyre(points, center, outer_radius-radial_clearance, half_width, cosine, sine):
                        row['tyre_penetration_count'] += 1
                        row['steering_pose_penetrations'][str(angle)] += 1
                        part_row['tyre_penetration_count'] += 1
                        if len(part_row['tyre_samples']) < max_samples:
                            part_row['tyre_samples'].append({'wheel': wheel_name,
                                'steering_degrees': angle, 'triangle': triangle_index, 'vertices': points})
            row['parts'].append(part_row)
        counts = [part['triangles'] for part in row['parts']]
        if any(counts[i] == 0 for i in (4, 5, 6)) or any(counts[i] != 0 for i in (7, 8, 9)):
            errors.append(f'LOD {lod}: expected only front A/B and rear A door meshes.')
        if counts[25] == 0:
            errors.append(f'LOD {lod}: independently controlled brake-light part 25 is empty.')
        if drives != 3 or any(counts[i] == 0 for i in range(len(PART_NAMES), len(PART_NAMES) + drives)):
            errors.append(f'LOD {lod}: expected the three door drive parts (one per leaf) with geometry.')
        if surfaces != 4 or any(counts[i] == 0 for i in range(len(PART_NAMES) + drives, len(PART_NAMES) + drives + surfaces)):
            errors.append(f'LOD {lod}: expected the four wrap surface parts (windows, then one per door leaf) with geometry.')
        for field in ('zero_normals', 'nonunit_normals', 'nonfinite_vertices',
                      'degenerate_triangles', 'unknown_swatches', 'tyre_penetration_count'):
            if row[field]:
                errors.append(f'LOD {lod}: {field} = {row[field]}.')
        report['lods'].append(row)
    if total == 0 or offset != len(data):
        errors.append(f'Empty mesh or trailing bytes: parsed {offset} of {len(data)} bytes.')
    report['status'] = 'PASS' if not errors else 'FAIL'
    return report


PALETTE_ROWS = 60  # replaced from kit-cabin.json in main()


def main():
    root = Path(__file__).resolve().parents[2]
    global PALETTE_ROWS
    cabin = root/'ModelSource/SWB6710EV01G/kit-cabin.json'
    if cabin.exists():
        PALETTE_ROWS = len(json.loads(cabin.read_text(encoding='utf-8'))['palette'])
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, default=root/'ModelSource/SWB6710EV01G/sunwin-mesh.bin.gz')
    parser.add_argument('--spec', type=Path, default=root/'ModelSource/SWB6710EV01G/model-spec.json')
    parser.add_argument('--report', type=Path, default=root/'ModelSource/SWB6710EV01G/runtime-validation.json')
    parser.add_argument('--radial-clearance', type=float, default=0.)
    parser.add_argument('--half-tyre-width', type=float, default=.103)
    parser.add_argument('--envelope-radius', type=float, help='Outer tyre/tread radius (default: specified wheel radius + 4 mm tread); hub height stays unchanged.')
    parser.add_argument('--max-samples', type=int, default=10)
    args = parser.parse_args()
    try:
        spec = json.loads(args.spec.read_text(encoding='utf-8')) if args.spec.exists() else {}
        report = audit(args.binary, spec, args.radial_clearance, args.half_tyre_width, max(0, args.max_samples), args.envelope_radius)
    except (OSError, ValueError, EOFError, struct.error) as error:
        report = {'status': 'FAIL', 'binary': str(args.binary.resolve()), 'errors': [str(error)], 'lods': []}
    def json_safe(value):
        if isinstance(value, float) and not math.isfinite(value):
            return str(value)
        if isinstance(value, dict):
            return {key: json_safe(item) for key, item in value.items()}
        if isinstance(value, (list, tuple)):
            return [json_safe(item) for item in value]
        return value
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(json_safe(report), ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')
    counts = ', '.join(str(lod['triangles']) for lod in report['lods'])
    print(f"SWB_RUNTIME_{report['status']} triangles [{counts}]; report {args.report}")
    for error in report['errors'][:10]:
        print(error, file=sys.stderr)
    return 0 if report['status'] == 'PASS' else 1


if __name__ == '__main__':
    raise SystemExit(main())
