"""Render complete-assembly support closeups, without saving the loaded source."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

import bpy
from mathutils import Vector

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--views', default='')
args = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source = Path(bpy.data.filepath)
source_sha = hashlib.sha256(source.read_bytes()).hexdigest()
scene = bpy.context.scene
scene.frame_set(0)
scene.render.engine = 'CYCLES'
scene.cycles.device = 'CPU'
scene.cycles.samples = 16
scene.cycles.use_denoising = True
scene.render.threads_mode = 'FIXED'
scene.render.threads = 4
scene.render.resolution_x = 1024
scene.render.resolution_y = 800
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.view_settings.exposure = .65
camera = scene.camera
camera.data.type = 'PERSP'
camera.data.clip_start = .04

def physical(point):
    return Vector((-point[0], point[1], point[2]))

for name, position, target in (
        ('driver mount fill', (-2.7,-4.3,3.3),(-1.3,-5.,2.45)),
        ('door mount fill', (2.7,-4.3,3.3),(1.3,-5.,2.45)),
        ('driver eye fill', (0.,-4.,2.8),(-1.3,-5.,2.45))):
    data = bpy.data.lights.new(name, 'AREA')
    data.energy = 120
    data.shape = 'DISK'
    data.size = 1.8
    lamp = bpy.data.objects.new(name, data)
    scene.collection.objects.link(lamp)
    lamp.location = physical(position)
    lamp.rotation_euler = (physical(target)-lamp.location).to_track_quat('-Z','Y').to_euler()

views = (
    ('driver-eye',(-.69,-4.18,1.95),(-1.293,-4.995,2.46),0),
    ('door-eye',(-.69,-4.18,1.95),(1.293,-5.014,2.46),0),
    ('driver-exterior',(-2.5,-4.6,2.50),(-1.275,-4.995,2.46),65),
    ('driver-front-oblique',(-2.0,-5.5,2.62),(-1.3,-5.0,2.46),55),
    ('door-exterior',(2.5,-4.7,2.50),(1.275,-5.014,2.46),60),
    ('door-front-oblique',(2.1,-5.8,2.7),(1.38,-5.25,2.42),45),
)
selected = set(args.views.split(',')) if args.views else None
args.output.mkdir(parents=True, exist_ok=True)
rows=[]
for name, eye, target, lens in views:
    if selected and name not in selected:
        continue
    camera.location=physical(eye)
    camera.rotation_euler=(physical(target)-camera.location).to_track_quat('-Z','Y').to_euler()
    if lens:
        camera.data.lens = lens
        camera.data.sensor_fit = 'AUTO'
    else:
        camera.data.sensor_fit = 'VERTICAL'
        camera.data.sensor_height = 24
        camera.data.lens = 24 / (2 * __import__('math').tan(__import__('math').radians(58)/2))
    path=args.output/(name+'.png')
    scene.render.filepath=str(path.resolve())
    bpy.ops.render.render(write_still=True)
    rows.append(dict(name=name,file=path.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                     eye_source_m=list(camera.location), lens_mm=camera.data.lens,
                     lighting='Original studio plus three 120 W mount review area lights, +0.65 EV; complete source assembly'))
    (args.output/'manifest.json').write_text(json.dumps(dict(source=str(source),source_sha256=source_sha,images=rows),indent=2)+'\n',encoding='utf-8')
assert hashlib.sha256(source.read_bytes()).hexdigest() == source_sha
print('MIRROR_MOUNT_REVIEW_RENDERED', len(rows))
