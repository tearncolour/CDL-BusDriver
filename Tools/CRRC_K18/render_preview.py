"""Render the saved K18 study without rebuilding or resaving it."""
import bpy,sys,json,hashlib
from pathlib import Path
from mathutils import Vector,Matrix
ROOT=Path(__file__).resolve().parents[2];SRC=ROOT/'ModelSource/CRRC_K18'
path=SRC/'CRRC_K18.blend';digest=hashlib.sha256(path.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(path));scene=bpy.context.scene;cam=scene.camera
U=Matrix(((-1,0,0),(0,0,-1),(0,1,0)))
scene.render.engine='CYCLES';scene.cycles.samples=16;scene.cycles.use_denoising=True
scene.render.resolution_x=1280;scene.render.resolution_y=800;scene.render.resolution_percentage=100
views=[('front-right',(19,9,22),(0,1.45,0),48,1),('right-side',(28,4,0),(0,1.5,0),48,1),('rear-left',(-18,8,-21),(0,1.5,-1),48,1),('doors-open',(18,6,17),(0,1.4,0),46,85),('articulated',(16,12,22),(0,1.4,-1),48,160),('saloon',(0,1.8,-7.95),(0,1.6,5.5),18,1),('driver',(-.79,1.97,7.62),(-.79,1.87,12),24,1)]
chosen=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
rows=[]
for name,pos,target,lens,frame in views:
    if chosen and name not in chosen:continue
    scene.frame_set(frame);cam.location=U@Vector(pos);cam.rotation_euler=(U@Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.lens=lens
    scene.render.filepath=str(SRC/'previews'/f'{name}.png');bpy.ops.render.render(write_still=True)
    rows.append({'view':name,'frame':frame,'path':scene.render.filepath,'source_sha256':digest})
assert hashlib.sha256(path.read_bytes()).hexdigest()==digest
(SRC/'previews'/'render-manifest.json').write_text(json.dumps({'source_sha256':digest,'renderer':'Blender Cycles, studio lighting; not CSL2','views':rows},indent=2),encoding='utf-8')
print('K18_PREVIEWS_OK',len(rows),flush=True)
