"""Locate saved-review pixels on final opaque source triangles without moving the camera."""
import argparse, hashlib, json, sys
from pathlib import Path
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ap = argparse.ArgumentParser()
ap.add_argument('--blend', type=Path, required=True)
ap.add_argument('--views', type=Path, required=True)
ap.add_argument('--image', required=True)
ap.add_argument('--pixels', required=True, help='x,y;x,y in the original image')
ap.add_argument('--out', type=Path, required=True)
a = ap.parse_args(sys.argv[sys.argv.index('--') + 1:])
review = json.loads(a.views.read_text())
if hashlib.sha256(a.blend.read_bytes()).hexdigest() != review['source_sha256']:
    raise ValueError('Camera review/source hash mismatch')
view = next(v for v in review['views'] if Path(v['image']).name == a.image)
bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()))
bpy.context.scene.frame_set(view['frame'])
bpy.context.view_layer.update()
vertices, triangles, owners = [], [], []
for ob in bpy.data.collections['LOD%d' % view['lod']].all_objects:
    if ob.type != 'MESH' or ob.get('bake') == 'only':
        continue
    mesh = ob.data
    mesh.calc_loop_triangles()
    offset = len(vertices)
    vertices.extend(ob.matrix_world @ v.co for v in mesh.vertices)
    for tri in mesh.loop_triangles:
        triangles.append(tuple(offset + i for i in tri.vertices))
        material = mesh.materials[tri.material_index]
        owners.append({'object': ob.name, 'triangle': tri.index,
                       'material': material.name if material else None})
tree = BVHTree.FromPolygons(vertices, triangles, all_triangles=True)
origin = Vector(view['camera'])
rotation = (Vector(view['target']) - origin).to_track_quat('-Z', 'Y')
width, height = view['resolution']
half = 18 / view['lens_mm']
rows = []
for pair in a.pixels.split(';'):
    x, y = map(float, pair.split(','))
    direction = rotation @ Vector((((x + .5) / width * 2 - 1) * half,
                                   (1 - (y + .5) / height * 2) * half * height / width, -1)).normalized()
    point, normal, index, distance = tree.ray_cast(origin, direction, 100)
    rows.append({'pixel': [x, y], 'hit': None if point is None else {
        **owners[index], 'point': list(point), 'normal': list(normal), 'distance': distance,
        'vertices': [list(vertices[i]) for i in triangles[index]]}})
a.out.write_text(json.dumps({'source_sha256': review['source_sha256'], 'view': view,
                             'pixels': rows}, indent=2))
print(json.dumps(rows))
