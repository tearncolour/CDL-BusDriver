"""Render review images from the saved master; --views name,name selects a subset."""
import bpy, sys, json, hashlib
from pathlib import Path
from mathutils import Vector
sys.path.insert(0,str(Path(__file__).resolve().parent))
from source_coordinates import source_root
root=Path(__file__).resolve().parents[2]
scene=bpy.context.scene
if scene.get('model_id')!='SWB6109EV12G':raise RuntimeError('Load the new model master before rendering')
dest=root/'ModelSource/SWB6109EV12G/previews';dest.mkdir(parents=True,exist_ok=True)
source=Path(bpy.data.filepath).resolve()
source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
manifest_path=dest.parent/'preview-manifest.json'
manifest={'source_file':str(source),'source_sha256':source_hash,'views':{}}
if manifest_path.exists():
    previous=json.loads(manifest_path.read_text(encoding='utf-8'))
    if previous.get('source_sha256')==source_hash:manifest=previous
cam=scene.camera
views=[('front-right',(12,-16,5),(0,0,1.5),50,0),('rear-left',(-12,16,5),(0,0,1.5),50,0),
 ('front',(0,-15,1.65),(0,0,1.65),52,5.5),('rear',(0,15,1.65),(0,0,1.65),52,5.3),
 ('right-profile',(17,0,1.65),(0,0,1.65),52,12.2),('left-profile',(-17,0,1.65),(0,0,1.65),52,12.2),
 ('driver-cab',(-.69,-4.18,1.95),(-.69,-4.9,1.37),24,0),
 ('saloon',(0,-3.4,2.15),(0,3.5,1.8),22,0),
 ('saloon-rear',(0,4.35,2.05),(0,-3.3,1.6),22,0),
 ('driver-footwell',(-.26,-4.04,1.13),(-.68,-4.61,.77),22,0),
 ('front-wheelhouse',(0,-1.55,1.2),(-.86,-2.85,.78),24,0),
 ('rear-wheelhouse',(0,1.25,1.15),(-.85,2.65,.78),24,0),
 ('saloon-low',(0,-1.65,.68),(0,3.35,1.15),22,0),
 ('interior-door-closed',(0,-2.9,1.15),(1.25,-4.3,1.4),20,0),
 ('interior-door-half',(0,-2.9,1.15),(1.25,-4.3,1.4),20,0),
 ('interior-door-open',(0,-2.9,1.15),(1.25,-4.3,1.4),20,0),
 ('card-reader',(.46,-4.15,1.40),(.89,-3.55,1.20),42,0),
 ('door-handle-interior',(-.5,-1.5,1.6),(1.25,.3,1.4),27,0),
 ('door-handle-exterior',(4.4,.3,1.55),(1.25,.3,1.45),42,0),
 ('seat-back-detail',(0,3.1,2.15),(-.7,2.1,1.85),35,0),
 ('ceiling-window-detail',(0,-.5,2.2),(-1.15,.8,2.65),27,0),
 ('rear-reference-fit',(-.57,2.90,2.24),(-.57,-3.3,2.30),18,0),
 ('rear-seat-access',(-.20,-.50,1.60),(.74,1.42,1.05),24,0),
 ('rear-seat-access-low',(-.15,.10,.88),(.75,1.42,.75),24,0),
 ('front-detail',(3,-8,2.1),(0,-5,1.6),52,0),
 ('front-badges',(.65,-8,1.25),(.48,-5.23,.94),68,0),
 ('front-badges-lights-off',(.65,-8,1.25),(.48,-5.23,.94),68,0),
 ('front-plate',(0,-7.8,.67),(0,-5.2,.43),58,0),
 ('front-right-lights-off',(12,-16,5),(0,0,1.5),50,0),
 ('rear-upper-corner',(-4,8,3.8),(-1,5,2.7),65,0),
 ('rear-lower-corner',(-4,8,.95),(-1,4.7,.75),65,0),
 ('doors-half',(12,-16,5),(0,0,1.5),50,0),('doors-open',(12,-16,5),(0,0,1.5),50,0)]
selected=None
if source_root() is not None:
    # Camera positions follow the corrected physical sides of the vehicle.
    # This changes the viewpoint only; the saved model itself is already LHD.
    views=[(name,(-eye[0],eye[1],eye[2]),(-target[0],target[1],target[2]),lens,ortho)
           for name,eye,target,lens,ortho in views]
if '--views' in sys.argv:selected=set(sys.argv[sys.argv.index('--views')+1].split(','))
scene.render.image_settings.file_format='PNG'
scene.cycles.samples=16 if '--quick' in sys.argv else 64
switchable=[obj for obj in scene.objects if int(obj.get('cdlc_part',-1)) in range(23,29)]
original_hidden={obj:obj.hide_render for obj in switchable}
ground=bpy.data.objects.get('Studio ground')
ground_hidden=ground.hide_render if ground else False
inspection_lights=[]
for i,y in enumerate((-3.,-.5,2.,4.)):
    data=bpy.data.lights.new('Interior inspection soft fill '+str(i),'AREA')
    data.energy=65.;data.shape='RECTANGLE';data.size=1.6;data.size_y=1.6
    lamp=bpy.data.objects.new(data.name,data);scene.collection.objects.link(lamp)
    lamp.location=(0.,y,2.91)
    lamp.rotation_euler=(Vector((0.,y,1.))-lamp.location).to_track_quat('-Z','Y').to_euler()
    inspection_lights.append(lamp)
original_exposure=scene.view_settings.exposure
interior_views={'driver-cab','saloon','saloon-rear','driver-footwell','front-wheelhouse','rear-wheelhouse',
                'saloon-low','interior-door-closed','interior-door-half','interior-door-open',
                'card-reader','door-handle-interior','seat-back-detail','ceiling-window-detail',
                'rear-reference-fit','rear-seat-access','rear-seat-access-low'}
for name,eye,target,lens,ortho in views:
    if selected and name not in selected:continue
    inside=name in interior_views
    for lamp in inspection_lights:lamp.hide_render=not inside
    scene.view_settings.exposure=.65 if inside else original_exposure
    scene.frame_set(30 if name in ('doors-half','interior-door-half') else 40 if name in ('doors-open','interior-door-open') else 0)
    for obj in switchable:obj.hide_render=original_hidden[obj] or name.endswith('lights-off')
    # Orthographic elevations use a plain background: the camera's low rows
    # otherwise sit inside the studio ground slab and render its back face.
    if ground:ground.hide_render=ground_hidden or bool(ortho)
    cam.location=eye;cam.rotation_euler=(Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.lens=lens
    cam.data.type='ORTHO' if ortho else 'PERSP'
    if ortho:cam.data.ortho_scale=ortho
    target_image=dest/(name+'.png')
    scene.render.filepath=str(target_image);bpy.ops.render.render(write_still=True)
    manifest['views'][name]={'file':target_image.name,'sha256':hashlib.sha256(target_image.read_bytes()).hexdigest(),
                             'samples':scene.cycles.samples,'frame':scene.frame_current,
                             'lighting':'Four 65 W interior review lamps; +0.65 EV; complete assembly' if inside else 'Original exterior studio'}
    manifest_path.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
scene.frame_set(0)
for obj in switchable:obj.hide_render=original_hidden[obj]
if ground:ground.hide_render=ground_hidden
scene.view_settings.exposure=original_exposure
for lamp in inspection_lights:
    data=lamp.data;bpy.data.objects.remove(lamp,do_unlink=True);bpy.data.lights.remove(data)
if hashlib.sha256(source.read_bytes()).hexdigest()!=source_hash:raise RuntimeError('Saved model changed during preview rendering')
print('SWB6109_PREVIEWS_OK')
