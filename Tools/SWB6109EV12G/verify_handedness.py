"""Read-only physical handedness audit of the saved SWB6109 Blender source.

This validates actual world geometry, rather than treating a coordinate label
or a mirror of the preview image as proof of a left-hand-drive vehicle.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'ModelSource/SWB6109EV12G'
sys.path.insert(0, str(Path(__file__).resolve().parent))
from source_coordinates import (SOURCE_REFLECTION, canonical_world_matrix,
                                source_is_corrected, source_root)

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--blend', type=Path, default=SRC/'SWB6109EV12G.blend')
parser.add_argument('--report', type=Path, default=SRC/'handedness-validation.json')
args = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source = args.blend.resolve()
source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source))
scene = bpy.context.scene
scene.frame_set(0)
bpy.context.view_layer.update()
master = bpy.data.collections.get('SWB6109EV12G | editable components')
if master is None:
    raise RuntimeError('Missing SWB6109 editable components')
spec = json.loads((SRC/'model-spec.json').read_text(encoding='utf-8'))
errors = []


def check(condition, message):
    if not condition:
        errors.append(message)


def matrix(values):
    return Matrix([[float(values[column*4+row]) for column in range(4)] for row in range(4)])


def matrix_error(a, b):
    return max(abs(a[r][c]-b[r][c]) for r in range(4) for c in range(4))


def bounds(objects):
    deps = bpy.context.evaluated_depsgraph_get()
    points = []
    for ob in objects:
        data = bpy.data.meshes.new_from_object(ob.evaluated_get(deps), depsgraph=deps)
        points.extend(ob.matrix_world @ vertex.co for vertex in data.vertices)
        bpy.data.meshes.remove(data)
    if not points:
        return None
    return [[min(point[i] for point in points), max(point[i] for point in points)] for i in range(3)]


root = source_root(master)
check(root is not None, 'No physical left-hand-drive source root')
if root is not None:
    check(matrix_error(root.matrix_world, SOURCE_REFLECTION) < 1e-7,
          'Source root is not the single intended X reflection')
    check(root.parent is None, 'Source coordinate root has an unexpected parent')

# In a RH world, left = up cross forward. This is +X for this source.
forward = Vector((0., -1., 0.))
up = Vector((0., 0., 1.))
physical_left = up.cross(forward).normalized()
driver = master.objects.get('DriverEye')
check(driver is not None, 'DriverEye marker is missing')
driver_report = {}
if driver is not None:
    position = driver.matrix_world.translation
    canonical = canonical_world_matrix(driver).translation
    unity = Vector((canonical.x, canonical.z, -canonical.y))
    check(position.dot(physical_left) > .5, 'Driver is on the physical right in Blender')
    check((unity-Vector(spec['driver_eye_unity'])).length < 1e-5,
          'Correcting the source changed the established Unity driver position')
    driver_report = {'source_world': list(position), 'physical_left_distance_m': position.dot(physical_left),
                     'canonical': list(canonical), 'unity': list(unity)}

geometry = [ob for ob in master.objects if ob.type in {'MESH', 'CURVE', 'FONT'}]
fonts = []
vectors = []
canonical_error = 0.
rest_error = 0.
for ob in master.objects:
    if ob == root:
        continue
    check(source_is_corrected(ob), ob.name+': object is outside corrected hierarchy')
    saved = ob.get('cdlc_source_canonical_matrix_at_conversion')
    check(saved is not None, ob.name+': canonical conversion witness is missing')
    if saved is not None:
        error = matrix_error(canonical_world_matrix(ob), matrix(saved))
        canonical_error = max(canonical_error, error)
        check(error < 2e-5, ob.name+': source migration changed canonical frame at rest')
    if 'cdlc_rest_matrix_blender' in ob:
        actual = ob.get('cdlc_rest_matrix_source_blender')
        check(actual is not None, ob.name+': actual source rest metadata missing')
        if actual is not None:
            error = matrix_error(matrix(actual), SOURCE_REFLECTION @ matrix(ob['cdlc_rest_matrix_blender']))
            rest_error = max(rest_error, error)
            check(error < 1e-6, ob.name+': source and canonical rest metadata disagree')
    if ob.type == 'FONT':
        basis = ob.matrix_world.to_3x3()
        readability = basis.col[0].normalized().dot(
            basis.col[1].normalized().cross(basis.col[2].normalized()))
        check(bool(ob.get('cdlc_font_source_preflipped')), ob.name+': font reflection was not compensated')
        check(readability > .999, ob.name+': glyph basis is mirrored in the saved source')
        fonts.append({'object': ob.name, 'text': ob.data.body, 'readability_orientation': readability})
    if 'cdlc_vector_mirror_x' in ob:
        check(bool(ob.get('cdlc_vector_source_preflipped')), ob.name+': vector reflection was not compensated')
        vectors.append(ob.name)

mirror_rows = []
for ob in geometry:
    if not ob.get('mirror_surface'):
        continue
    box = bounds([ob])
    centre = [(lo+hi)/2 for lo,hi in box]
    driver_side = 'driver' in ob.name.lower()
    check(centre[0] > 1.2 if driver_side else centre[0] < -1.2,
          ob.name+': mirror is on the wrong physical side')
    check('mirror_normal_source_blender' in ob, ob.name+': source mirror normal metadata missing')
    mirror_rows.append({'object': ob.name, 'physical_side': 'driver/left' if driver_side else 'door/right',
                        'source_bounds': box, 'bounds_centre': centre})
check(len(mirror_rows) == 3, 'Exactly three actual reflective mirror surfaces are required')

pose_rows = []
for frame in (0, 30, 40, 80):
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    doors = [ob for ob in geometry if int(ob.get('cdlc_part', -1)) in range(4, 8)]
    seats = [ob for ob in geometry if ob.name.startswith('Driver seat')]
    steering = [ob for ob in geometry if int(ob.get('cdlc_part', -1)) == 1]
    door_bounds, seat_bounds, steering_bounds = bounds(doors), bounds(seats), bounds(steering)
    check(door_bounds is not None and door_bounds[0][1] < -.1,
          f'Frame {frame}: passenger doors leave the physical right half of the source')
    check(seat_bounds is not None and seat_bounds[0][0] > .1,
          f'Frame {frame}: driver seat is not in the physical left half')
    check(steering_bounds is not None and sum(steering_bounds[0])/2 > .3,
          f'Frame {frame}: steering wheel is not in the physical left half')
    if door_bounds and seat_bounds:
        check(door_bounds[0][1] < seat_bounds[0][0],
              f'Frame {frame}: door and driver seat transverse ranges overlap')
    pose_rows.append({'frame': frame, 'door_source_bounds': door_bounds,
                      'driver_seat_source_bounds': seat_bounds, 'steering_source_bounds': steering_bounds})

scene.frame_set(0)
check(hashlib.sha256(source.read_bytes()).hexdigest() == source_hash,
      'Saved source changed during read-only handedness audit')
report = {'status': 'FAIL' if errors else 'PASS', 'source': str(source), 'source_sha256': source_hash,
          'scope': 'Actual saved-world driver, doors and three mirror surfaces; editable glyph orientation; '
                   'canonical/rest transform witnesses; closed/intermediate/open physical sides. '
                   'This is not a replacement for full collision or game-play validation.',
          'physical_basis': {'forward': list(forward), 'up': list(up), 'left': list(physical_left)},
          'driver': driver_report, 'geometry_count': len(geometry),
          'maximum_canonical_frame_error': canonical_error, 'maximum_rest_metadata_error': rest_error,
          'font_readability': fonts, 'corrected_vectors': vectors,
          'mirrors': mirror_rows, 'poses': pose_rows, 'errors': errors}
args.report.parent.mkdir(parents=True, exist_ok=True)
args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print('SWB6109_HANDEDNESS_'+report['status']+' '+str(args.report), flush=True)
if errors:
    raise RuntimeError('; '.join(errors[:12]))
