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
from collections import Counter


PART_NAMES = (
    'Shell', 'SteeringWheel', 'SpeedNeedle', 'RpmNeedle',
    'Door0A', 'Door0B', 'Door1A', 'Door1B', 'Door2A', 'Door2B',
    'Fittings', 'Dash', 'Cab', 'Poles', 'Ceiling', 'Outside',
    'Exterior', 'Glazing', 'Livery', 'WheelFL', 'WheelFR', 'WheelRL', 'WheelRR',
    'HeadLamps', 'TailLamps', 'BrakeLamps', 'IndicatorL', 'IndicatorR', 'ReverseLamps',
    'WrapLeft', 'WrapRight', 'WrapFront', 'WrapRear', 'WrapRoof',
)
HEADER = struct.Struct('<4sii3f')
FRAME = struct.Struct('<ii7f')
COUNT = struct.Struct('<i')
TRIANGLE = struct.Struct('<B18f')
MAX_TRIANGLES = 2_000_000
FRONT_STEERING_ANGLES_DEG = (-35, -25, -15, 0, 15, 25, 35)
DEGENERATE_CROSS_SQUARED_M4 = 1e-18
DEFAULT_PALETTE = Path(__file__).resolve().parents[2]/'ModelSource/SWB6109EV12G/kit-cabin.json'


def load_palette_ids(path):
    """Read exact byte IDs from the same exported palette used by the writer.

    Do not infer a contiguous range from the row count: a missing ID must stay
    invalid, and malformed or unavailable palette metadata must fail closed.
    """
    path = Path(path)
    packed = path.read_bytes()
    document = json.loads(packed.decode('utf-8-sig'))
    rows = document.get('palette') if isinstance(document, dict) else None
    if not isinstance(rows, list) or not rows:
        raise ValueError('Palette contract requires a nonempty palette array.')
    identifiers = set()
    for ordinal, row in enumerate(rows):
        index = row.get('index') if isinstance(row, dict) else None
        if type(index) is not int or not 0 <= index <= 255:
            raise ValueError(f'Palette row {ordinal} has an invalid byte index: {index!r}.')
        if index in identifiers:
            raise ValueError(f'Palette contract contains duplicate index {index}.')
        identifiers.add(index)
    return frozenset(identifiers), {
        'source': str(path.resolve()), 'sha256': hashlib.sha256(packed).hexdigest(),
        'defined_id_count': len(identifiers), 'defined_ids': sorted(identifiers),
        'validation': 'Every triangle byte must be an explicitly defined palette index.',
    }


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


def strap_pose_point(point, pivot, ordinal, swing_x_degrees=0., swing_z_degrees=0.):
    """The same local X then local Z rotation order as ApplyStraps."""
    factor=.8+.4*((ordinal*.618034)%1.)
    x,y,z=point
    ax,az=math.radians(swing_x_degrees*factor),math.radians(swing_z_degrees*factor)
    y,z=math.cos(ax)*y-math.sin(ax)*z,math.sin(ax)*y+math.cos(ax)*z
    x,y=math.cos(az)*x-math.sin(az)*y,math.sin(az)*x+math.cos(az)*y
    return (pivot[0]+x,pivot[1]+y,pivot[2]+z)


def audit(binary, spec, radial_clearance=0., half_width=None, max_samples=10, envelope_radius=None,
          palette=None):
    allowed_swatches, palette_contract = load_palette_ids(DEFAULT_PALETTE if palette is None else palette)
    required=('length_m','front_overhang_m','rear_overhang_m','wheel_radius_m',
              'front_track_m','rear_track_m','driver_eye_unity','door_leaves')
    missing=[key for key in required if key not in spec]
    if missing:raise ValueError('Missing new-model specifications: '+', '.join(missing))
    if half_width is None:half_width=float(spec['tyre_half_width_m'])
    active_doors={int(leaf['part']) for leaf in spec['door_leaves']}
    if not active_doors or not active_doors<=set(range(4,10)):
        raise ValueError('door_leaves must specify nonempty, valid CDLC parts 4..9')
    packed = binary.read_bytes()
    report = {
        'status': 'FAIL', 'binary': str(binary.resolve()),
        'sha256': hashlib.sha256(packed).hexdigest(), 'compressed_bytes': len(packed),
        'degenerate_cross_squared_threshold_m4': DEGENERATE_CROSS_SQUARED_M4,
        'degenerate_area_threshold_m2': math.sqrt(DEGENERATE_CROSS_SQUARED_M4)/2,
        'palette_contract': palette_contract,
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
    if magic != b'SWB1' or lod_count != 3 or not len(PART_NAMES)<=part_count<=256:
        raise ValueError(f'Unsupported SWB1 header: {magic!r}, {lod_count} LODs, {part_count} parts.')
    part_names=list(PART_NAMES)+['Strap'+str(i) for i in range(part_count-len(PART_NAMES))]
    if part_count==34:errors.append('SWB6109 has no independent hand-strap parts after fixed part 33.')
    report.update(format='SWB1', part_count=part_count, lod_count=lod_count, eye=eye)
    if not all(math.isfinite(x) and abs(x) <= 100 for x in eye):
        errors.append('Driver eye contains invalid coordinates.')
    if len(spec['driver_eye_unity'])!=3 or max(abs(a-float(b)) for a,b in zip(eye,spec['driver_eye_unity']))>1e-4:
        errors.append('Driver eye does not match this model specification.')
    frames = []
    for index, name in enumerate(part_names):
        parent, flags, *values = read(FRAME)
        pivot, rotation = values[:3], values[3:]
        valid = all(math.isfinite(x) for x in values)
        if parent < -1 or parent >= index or (index == 0 and parent != -1) or flags & ~15:
            errors.append(f'Part {index} {name}: invalid parent or flags.')
        if not valid or any(abs(x) > 100 for x in pivot):
            errors.append(f'Part {index} {name}: nonfinite or excessive frame coordinates.')
        if not valid or abs(sum(x * x for x in rotation) - 1) > 1e-4:
            errors.append(f'Part {index} {name}: rest quaternion is not normalized.')
        if index>=34 and (parent!=0 or max(abs(x) for x in rotation[:3])>1e-6 or abs(abs(rotation[3])-1)>1e-6):
            errors.append(f'Part {index} {name}: straps require Shell parent and identity Unity rest rotation.')
        frames.append({'index': index, 'name': name, 'parent': parent,
                       'flags': flags, 'pivot': pivot, 'rotation_xyzw': rotation})
    report['frames'] = frames
    gap = frames[1]['pivot'][2] - eye[2]
    report['steering_hub_ahead_of_eye_m'] = gap
    if not math.isfinite(gap) or gap <= 0:
        errors.append('Steering hub is not forward of the driver eye (+Z).')

    length = float(spec['length_m'])
    front_z = length / 2 - float(spec['front_overhang_m'])
    rear_z = -length / 2 + float(spec['rear_overhang_m'])
    radius = float(spec['wheel_radius_m'])
    # Include the authored fine tread rings, without moving the wheel hubs.
    outer_radius = radius + float(spec['tyre_tread_depth_m']) if envelope_radius is None else float(envelope_radius)
    if not (0 <= radial_clearance < outer_radius and half_width > 0 and math.isfinite(outer_radius)):
        raise ValueError('Tyre dimensions or clearance are invalid.')
    wheels = []
    for index, name in enumerate(PART_NAMES[19:23], 19):
        is_front, is_left = index < 21, index in (19, 21)
        track = float(spec['front_track_m' if is_front else 'rear_track_m'])
        expected = [(-1 if is_left else 1) * track / 2, radius, front_z if is_front else rear_z]
        frame = frames[index]
        if max(abs(a - b) for a, b in zip(frame['pivot'], expected)) > 1e-4:
            errors.append(f'Part {index} {name}: pivot does not match axle, track and tyre specifications.')
        q = frame['rotation_xyzw']
        if max(abs(x) for x in q[:3]) > 1e-5 or abs(abs(q[3]) - 1) > 1e-5:
            errors.append(f'Part {index} {name}: runtime road-wheel rest rotation must be identity.')
        dual=float(spec.get('rear_dual_offset_m',0)) if not is_front else 0.
        if dual:
            for label,direction in ((' inner',-1),(' outer',1)):
                tyre=list(expected);tyre[0]+=(-1 if is_left else 1)*dual*direction
                wheels.append((name+label,tuple(tyre)))
        else:wheels.append((name, tuple(expected)))
    report['clearance_check']['wheel_hub_height_m'] = radius
    report['clearance_check']['outer_envelope_radius_m'] = outer_radius
    report['clearance_check']['effective_radius_m'] = outer_radius - radial_clearance
    report['clearance_check']['physical_tyre_count'] = len(wheels)
    wheel_poses = [(name, center, angle, math.cos(math.radians(angle)), math.sin(math.radians(angle)))
                   for name, center in wheels
                   for angle in (FRONT_STEERING_ANGLES_DEG if name in ('WheelFL', 'WheelFR') else (0,))]

    total = 0
    for lod in range(lod_count):
        row = {'lod': lod, 'triangles': 0, 'zero_normals': 0, 'nonunit_normals': 0,
               'nonfinite_vertices': 0, 'degenerate_triangles': 0, 'unknown_swatches': 0,
               'unknown_swatch_ids': {},
               'wheel_negative_corner_normal_sum': 0,
               'tyre_penetration_count': 0, 'parts': [],
               'steering_pose_penetrations': {str(angle): 0 for angle in FRONT_STEERING_ANGLES_DEG}}
        for part, name in enumerate(part_names):
            count, = read(COUNT)
            if count < 0 or total + count > MAX_TRIANGLES:
                raise ValueError(f'LOD {lod} part {part}: invalid/excessive triangle count {count}.')
            total += count
            row['triangles'] += count
            part_row = {'index': part, 'name': name, 'triangles': count,
                        'tyre_penetration_count': 0, 'tyre_samples': [],'negative_corner_normal_sum':0}
            strap_bounds=[[math.inf,-math.inf] for _ in range(3)] if part>=34 else None
            strap_edges=Counter() if part>=34 else None
            strap_pose_bounds={str(pose):[[math.inf,-math.inf] for _ in range(3)] for pose in ((0,0),(10,-8),(-12,9))} if part>=34 else None
            part_data_start = offset
            for triangle_index in range(count):
                triangle = read(TRIANGLE)
                if triangle[0] not in allowed_swatches:
                    row['unknown_swatches'] += 1
                    key = str(triangle[0])
                    row['unknown_swatch_ids'][key] = row['unknown_swatch_ids'].get(key, 0) + 1
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
                if part>=34:
                    keys=[tuple(round(v,6) for v in p) for p in points]
                    for a,b in zip(keys,keys[1:]+keys[:1]):strap_edges[tuple(sorted((a,b)))]+=1
                    for p in points:
                        for k in range(3):strap_bounds[k]=[min(strap_bounds[k][0],p[k]),max(strap_bounds[k][1],p[k])]
                        for pose,bounds in strap_pose_bounds.items():
                            angles={'(0, 0)':(0,0),'(10, -8)':(10,-8),'(-12, 9)':(-12,9)}[pose]
                            posed=strap_pose_point(p,frames[part]['pivot'],part-34,*angles)
                            if not all(math.isfinite(v) for v in posed):errors.append(f'LOD {lod} {name}: nonfinite swing geometry.')
                            for k in range(3):bounds[k]=[min(bounds[k][0],posed[k]),max(bounds[k][1],posed[k])]
                    # Unlike the first 34 legacy parts, strap records are local
                    # to the header pivot; collision checks need model space.
                    points=[strap_pose_point(p,frames[part]['pivot'],part-34) for p in points]
                if cross_squared(points) < DEGENERATE_CROSS_SQUARED_M4:
                    row['degenerate_triangles'] += 1
                    continue
                if 19 <= part <= 22:
                    a,b,c=points
                    u=[b[i]-a[i] for i in range(3)];v=[c[i]-a[i] for i in range(3)]
                    cross=(u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0])
                    normal_sum=[triangle[4+i]+triangle[10+i]+triangle[16+i] for i in range(3)]
                    if sum(cross[i]*normal_sum[i] for i in range(3))<0:
                        part_row['negative_corner_normal_sum']+=1
                        row['wheel_negative_corner_normal_sum']+=1
                    continue
                for wheel_name, center, angle, cosine, sine in wheel_poses:
                    if triangle_hits_steered_tyre(points, center, outer_radius-radial_clearance, half_width, cosine, sine):
                        row['tyre_penetration_count'] += 1
                        row['steering_pose_penetrations'][str(angle)] += 1
                        part_row['tyre_penetration_count'] += 1
                        if len(part_row['tyre_samples']) < max_samples:
                            part_row['tyre_samples'].append({'wheel': wheel_name,
                                'steering_degrees': angle, 'triangle': triangle_index, 'vertices': points})
            part_row['geometry_sha256'] = hashlib.sha256(memoryview(data)[part_data_start:offset]).hexdigest()
            if part>=34:
                if not count:errors.append(f'LOD {lod} {name}: missing independent strap geometry.')
                else:
                    part_row['local_bounds']=strap_bounds
                    part_row['swing_model_bounds']=strap_pose_bounds
                    part_row['boundary_edges']=sum(n==1 for n in strap_edges.values())
                    part_row['nonmanifold_edges']=sum(n>2 for n in strap_edges.values())
                    if part_row['boundary_edges'] or part_row['nonmanifold_edges']:
                        errors.append(f'LOD {lod} {name}: exported strap is not closed and manifold.')
                    if abs(strap_bounds[1][1])>.003 or strap_bounds[1][0]>=-.05 or max(abs(v) for axis in (0,2) for v in strap_bounds[axis])>.2:
                        errors.append(f'LOD {lod} {name}: mesh is not local beneath its top attachment.')
            row['parts'].append(part_row)
        counts = [part['triangles'] for part in row['parts']]
        if any(counts[i] == 0 for i in active_doors) or any(counts[i] != 0 for i in set(range(4,10))-active_doors):
            errors.append(f'LOD {lod}: exported door leaves do not match specified parts {sorted(active_doors)}.')
        if counts[25] == 0:
            errors.append(f'LOD {lod}: independently controlled brake-light part 25 is empty.')
        for field in ('zero_normals', 'nonunit_normals', 'nonfinite_vertices',
                      'degenerate_triangles', 'unknown_swatches', 'tyre_penetration_count','wheel_negative_corner_normal_sum'):
            if row[field]:
                errors.append(f'LOD {lod}: {field} = {row[field]}.')
        report['lods'].append(row)
    # Imported thin instrument faces remain exact. Other cabin parts use
    # lower-resolution manufactured bevels/tube sections; their closure is
    # checked by verify_lod_source.py, then rendered at every exported LOD.
    preserved_parts=[1,2,3,11]
    report['preserved_lod_parts']=preserved_parts
    report['preserved_lod_geometry_identical']=True
    for part in preserved_parts:
        first=report['lods'][0]['parts'][part]['geometry_sha256']
        if any(row['parts'][part]['geometry_sha256']!=first for row in report['lods'][1:]):
            errors.append(f'Part {part} {PART_NAMES[part]}: protected cabin geometry changed between LODs.')
            report['preserved_lod_geometry_identical']=False
    report['straps']={'count':part_count-34,'first_part':34,'local_axes':'X transverse, Y up, Z rail',
        'sampled_swing_degrees':[[0,0],[10,-8],[-12,9]],
        'scope':'Finite sampled poses and top-local geometry; not a continuous motion/collision proof.'}
    if total == 0 or offset != len(data):
        errors.append(f'Empty mesh or trailing bytes: parsed {offset} of {len(data)} bytes.')
    report['status'] = 'PASS' if not errors else 'FAIL'
    return report


def main():
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary', type=Path, default=root/'ModelSource/SWB6109EV12G/sunwin-mesh.bin.gz')
    parser.add_argument('--spec', type=Path, default=root/'ModelSource/SWB6109EV12G/model-spec.json')
    parser.add_argument('--palette', type=Path, default=DEFAULT_PALETTE,
                        help='Authoritative KitExport JSON containing explicitly defined palette byte indices.')
    parser.add_argument('--report', type=Path, default=root/'ModelSource/SWB6109EV12G/runtime-validation.json')
    parser.add_argument('--radial-clearance', type=float, default=0.)
    parser.add_argument('--half-tyre-width', type=float, help='Override tyre_half_width_m from this model specification.')
    parser.add_argument('--envelope-radius', type=float, help='Override wheel_radius_m + tyre_tread_depth_m; hub height stays unchanged.')
    parser.add_argument('--max-samples', type=int, default=10)
    args = parser.parse_args()
    try:
        spec = json.loads(args.spec.read_text(encoding='utf-8')) if args.spec.exists() else {}
        report = audit(args.binary, spec, args.radial_clearance, args.half_tyre_width,
                       max(0, args.max_samples), args.envelope_radius, args.palette)
    except (OSError, ValueError, KeyError, EOFError, struct.error) as error:
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
