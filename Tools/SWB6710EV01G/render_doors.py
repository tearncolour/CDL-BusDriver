"""Door review renders of the saved master (out/door-review/): the outward-swing doors outside and inside, shut, opening,
open and in the closing order (rear door first), from the door timeline (door_linkage.py). Changes no source or asset.

    blender --background --factory-startup --python Tools/SWB6710EV01G/render_doors.py -- [--samples N] [--only name,name]
"""
import math
import sys
from pathlib import Path
import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'ModelSource/SWB6710EV01G'
OUT = ROOT / 'out/door-review'
OUT.mkdir(parents=True, exist_ok=True)
argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
samples = int(argv[argv.index('--samples') + 1]) if '--samples' in argv else 32
only = set(argv[argv.index('--only') + 1].split(',')) if '--only' in argv else None
bpy.ops.wm.open_mainfile(filepath=str(SRC / 'SWB6710EV01G.blend'))
scene = bpy.context.scene
cam, data = scene.camera, scene.camera.data
scene.cycles.samples = samples
scene.render.resolution_x, scene.render.resolution_y = 1400, 920
# name, frame, eye, target, lens (ortho scale when the lens is negative)
views = [
    ('side-shut', 0, (9.5, -1.2, 1.5), (1.1, -.2, 1.3), 30),
    ('side-opening', 12, (9.5, -1.2, 1.5), (1.1, -.2, 1.3), 30),
    ('side-open', 36, (9.5, -1.2, 1.5), (1.1, -.2, 1.3), 30),
    ('side-rear-closing', 60, (9.5, -1.2, 1.5), (1.1, -.2, 1.3), 30),
    ('side-front-closing', 89, (9.5, -1.2, 1.5), (1.1, -.2, 1.3), 30),
    ('rear-quarter-open', 36, (6.0, 7.5, 2.2), (1.1, -.2, 1.3), 34),
    ('front-quarter-open', 36, (6.0, -7.5, 2.0), (1.1, -.2, 1.3), 34),
    ('plan-open', 36, (1.0, -.1, 1.6), (1.0, -.1, 0.), -4.2),
    ('plan-half', 12, (1.0, -.1, 1.6), (1.0, -.1, 0.), -4.2),
    ('plan-rear-closing', 60, (1.0, -.1, 1.6), (1.0, -.1, 0.), -4.2),
    ('plan-front-closing', 89, (1.0, -.1, 1.6), (1.0, -.1, 0.), -4.2),
    ('front-door-outside-open', 36, (3.4, -2.9, 1.5), (1.15, -1.24, 1.25), 26),
    ('front-door-outside-half', 12, (3.4, -2.9, 1.5), (1.15, -1.24, 1.25), 26),
    ('rear-door-outside-open', 36, (3.4, 3.1, 1.5), (1.15, 1.42, 1.25), 26),
    ('front-door-inside-shut', 0, (-.2, -.15, 1.65), (1.05, -1.24, 1.3), 22),
    ('front-door-inside-open', 36, (-.2, -.15, 1.65), (1.05, -1.24, 1.3), 22),
    ('rear-door-inside-shut', 0, (-.2, .45, 1.65), (1.05, 1.42, 1.3), 22),
    ('rear-door-inside-open', 36, (-.2, .45, 1.65), (1.05, 1.42, 1.3), 22),
    ('front-header-inside', 36, (.35, -1.0, 1.45), (1.05, -1.24, 2.3), 22),
    ('front-floor-inside', 36, (.45, -.6, .9), (1.05, -1.5, .42), 24),
    ('driver-eye-open', 36, (-.56, -2.18, 2.08), (.9, -1.3, 1.45), 24),
]
for name, frame, eye, target, lens in views:
    if only and name not in only:
        continue
    scene.frame_set(frame)
    cam.location = eye
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat('-Z', 'Y').to_euler()
    if lens < 0:
        # a plan cut at 1.6 m: the camera's clip start removes the roof and everything above
        data.type, data.ortho_scale = 'ORTHO', -lens
        cam.rotation_euler = (0, 0, math.pi / 2)
        data.clip_start, data.clip_end = .001, 3.
    else:
        data.type, data.lens = 'PERSP', lens
        data.clip_start, data.clip_end = .01, 1000.
    scene.render.filepath = str(OUT / (name + '.png'))
    bpy.ops.render.render(write_still=True)
    print('DOOR_RENDER', name, frame, flush=True)
scene.frame_set(0)
print('DOOR_RENDERS_OK', OUT)
