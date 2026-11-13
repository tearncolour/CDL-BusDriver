"""Render the verified editable master without rebuilding any geometry."""
from pathlib import Path
import bpy
import math
from mathutils import Vector
ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6710EV01G'
bpy.ops.wm.open_mainfile(filepath=str(SRC/'SWB6710EV01G.blend'))
scene=bpy.context.scene;cam=scene.camera;data=cam.data
views=[('front-right',(9,-11.8,3.6),(0,0,1.4),50),('rear-left',(-10,12,3.8),(0,0,1.4),50),
       ('front',(0,-13,1.58),(0,0,1.58),52),('right-profile',(14,0,1.52),(0,0,1.52),52),
       ('left-profile',(-14,0,1.52),(0,0,1.52),52),
       ('driver-cab',(-.56,-2.18,2.08),(-.56,-2.85,1.36),24),
       ('saloon',(0,-1.55,1.95),(0,2.4,1.55),24),
       ('front-detail',(2.45,-6.25,1.70),(0,-3.38,1.36),65),
       ('front-glass',(1.8,-7.3,2.6),(0,-3.30,2.06),60),
       ('front-profile',(14,-2.7,1.5),(0,-2.7,1.5),52),
       ('rear-upper-corner',(-3.8,5.8,3.15),(-.95,3.12,2.65),65),
       ('rear-lower-corner',(-3.5,5.5,.7),(-.95,2.95,.55),65),
       ('doors-open',(9,-11.8,3.6),(0,0,1.4),50),
       ('doors-half-open',(9,-11.8,3.6),(0,0,1.4),50),
       ('doors-rear-closing',(9,-11.8,3.6),(0,0,1.4),50),
       ('doors-front-closing',(9,-11.8,3.6),(0,0,1.4),50),
       ('doors-open-profile',(14,0,1.52),(0,0,1.52),52),
       ('doors-open-plan',(.6,-.3,9.5),(.6,-.3,0),40),
       ('door-front-inside',(-.15,-.25,1.7),(1.05,-1.24,1.25),24),
       ('door-front-outside',(3.6,-2.7,1.55),(1.2,-1.24,1.25),28),
       ('door-rear-inside',(-.2,.5,1.7),(1.05,1.42,1.25),24),
       ('door-rear-outside',(3.6,3.0,1.55),(1.2,1.42,1.25),28),
       ('door-header-inside',(.25,-1.24,1.55),(1.05,-1.24,2.30),26)]
# the door timeline (door_linkage.py): open 0-24, held to 48, rear closes 48-72, front pair 77-101
DOOR_FRAMES={'doors-open':36,'doors-half-open':12,'doors-rear-closing':60,'doors-front-closing':89,'doors-open-profile':36,
             'doors-open-plan':36,'door-front-inside':36,'door-front-outside':36,'door-rear-inside':36,'door-rear-outside':36,
             'door-header-inside':36}
for name,eye,target,lens in views:
    scene.frame_set(DOOR_FRAMES.get(name,0))
    cam.location=eye;cam.rotation_euler=(Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler();data.lens=lens
    data.type='ORTHO' if name in ['front','right-profile','left-profile','front-profile','doors-open-profile','doors-open-plan'] else 'PERSP'
    if data.type=='ORTHO':data.ortho_scale=3.35 if name=='front-profile' else (5.2 if name=='front' else (5.4 if name=='doors-open-plan' else 8.7))
    if name=='doors-open-plan':cam.rotation_euler=(0,0,math.pi/2)
    scene.render.filepath=str(SRC/'previews'/(name+'.png'))
    bpy.ops.render.render(write_still=True)
print('PREVIEWS_OK')
