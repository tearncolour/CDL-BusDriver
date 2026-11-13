"""Close review renders of the painted livery lines (paint_lines.py) from the saved master, never saving it.

blender -b ModelSource/SWB6710EV01G/SWB6710EV01G.blend --python Tools/SWB6710EV01G/render_paint_review.py -- [samples]
Writes ModelSource/SWB6710EV01G/previews/paint-*.png. Source coordinates (door side +X, front -Y), like the other previews.
"""
import sys
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'ModelSource' / 'SWB6710EV01G' / 'previews'
args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
samples = int(args[0]) if args else 64

VIEWS = [
    ('paint-door-side', (5.6, -5.2, 1.55), (1.17, -0.9, .78), 32),
    ('paint-front-u-door', (2.9, -4.7, .95), (1.17, -3.12, .74), 42),
    ('paint-driver-side', (-6.2, .6, 1.6), (-1.17, .1, .78), 28),
    ('paint-rear-corner-door', (2.9, 5.6, 1.05), (1.12, 3.15, .76), 42),
    ('paint-rear-u', (1.0, 7.0, 1.7), (0, 3.5, 1.34), 40),
    ('paint-rear-u-corner', (1.7, 4.75, 1.72), (.95, 3.47, 1.55), 55),
]

scene = bpy.context.scene
scene.frame_set(0)
scene.render.resolution_x, scene.render.resolution_y = 1280, 800
scene.render.resolution_percentage = 100
if scene.render.engine == 'CYCLES':
    scene.cycles.samples = samples
cam = scene.camera
OUT.mkdir(parents=True, exist_ok=True)
for name, eye, target, lens in VIEWS:
    cam.location = eye
    cam.rotation_euler = (Vector(target) - Vector(eye)).to_track_quat('-Z', 'Y').to_euler()
    cam.data.type = 'PERSP'
    cam.data.lens = lens
    scene.render.filepath = str(OUT / (name + '.png'))
    bpy.ops.render.render(write_still=True)
    print('PAINT_REVIEW ' + name)
print('PAINT_REVIEW_OK')
