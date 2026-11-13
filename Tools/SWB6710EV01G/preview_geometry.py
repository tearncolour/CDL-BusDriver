"""Quick geometry review without overwriting the delivered mesh or FBX files.

Run through Blender with --python-exit-code 1. Renders are written to out/.
"""
from pathlib import Path
import sys

path = Path(__file__).with_name('build_model.py')
code = path.read_text(encoding='utf-8')
sys.argv.extend(['--skip-runtime', '--quick'])
ns = {'__file__': str(path), '__name__': '__main__'}
exec(compile(code[:code.index('body_objects=')], str(path), 'exec'), ns)
studio = code[code.index('# A neutral studio'):]
exec(compile(studio[:studio.index('views=[')], str(path), 'exec'), ns)
scene, cam, camdata, Vector = (ns[k] for k in ('scene', 'cam', 'camdata', 'Vector'))
out = ns['ROOT'] / 'out/sunwin-geometry'
out.mkdir(parents=True, exist_ok=True)
scene.render.resolution_percentage = 75
scene.cycles.samples = 16
for name, eye, target, lens in [
    ('front-right', (9,-11.8,3.6), (0,0,1.4), 50),
    ('front-detail', (2.45,-6.25,1.70), (0,-3.38,1.36), 65),
    ('front-lower-corner', (2.8,-5.7,.65), (.7,-3.2,.60), 65),
    ('front-upper-corner', (3.0,-5.7,2.8), (.7,-3.1,2.75), 65),
    ('front-glass', (1.8,-7.3,2.6), (0,-3.30,2.06), 60),
    ('rear-left', (-10,12,3.8), (0,0,1.4), 50),
]:
    cam.location = eye
    cam.rotation_euler = (Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler()
    camdata.type = 'PERSP'
    camdata.lens = lens
    scene.render.filepath = str(out / (name + '.png'))
    ns['bpy'].ops.render.render(write_still=True)
print('GEOMETRY_PREVIEW_OK ' + str(out), flush=True)
