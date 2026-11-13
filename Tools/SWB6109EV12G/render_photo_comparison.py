"""Interior comparison cameras and documented inspection lighting.

No source is saved. --patch loads the photo corrections in memory for review.
The complete assembled vehicle stays visible; extra soft ceiling light only
makes the dark saloon readable, and is recorded in the image manifest.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import bpy
from mathutils import Vector

sys.path.insert(0,str(Path(__file__).resolve().parent))
ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--patch',action='store_true')
parser.add_argument('--out',default='out/swb6109-photo-review')
parser.add_argument('--views',default='rear-forward,door-interior,standing-zone')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source=SRC/'SWB6109EV12G.blend'
source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source))
scene=bpy.context.scene; scene.frame_set(0)
master=bpy.data.collections['SWB6109EV12G | editable components']
patches={}
if args.patch:
    from door_details import patch_photo_door_details
    from photo_cabin_details import patch_photo_cabin_details
    from seat_geometry_photo import apply_photo_seat_geometry
    patches['doors']=patch_photo_door_details(master,scene)
    patches['cabin']=patch_photo_cabin_details(master,scene)
    patches['seats']=apply_photo_seat_geometry(master)
    try:
        from seat_print import apply_seat_print
        patches['seat_print']=apply_seat_print(master)
    except ImportError:
        pass

# These are review lamps, not export geometry or claimed factory illumination.
for i,y in enumerate((-3.,-.5,2.,4.)):
    ld=bpy.data.lights.new('Interior review soft fill '+str(i),'AREA')
    ld.energy=65.;ld.shape='RECTANGLE';ld.size=1.6;ld.size_y=1.6
    ob=bpy.data.objects.new(ld.name,ld);scene.collection.objects.link(ob)
    ob.location=(0.,y,2.91)
    ob.rotation_euler=(Vector((0.,y,1.))-ob.location).to_track_quat('-Z','Y').to_euler()
scene.view_settings.exposure=.65
scene.render.engine='CYCLES';scene.cycles.samples=24;scene.cycles.use_denoising=True
scene.render.resolution_x=1500;scene.render.resolution_y=900;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'
out=ROOT/args.out;out.mkdir(parents=True,exist_ok=True)
manifest={'source_sha256':source_hash,'in_memory_corrections':bool(args.patch),
          'lighting':'Full assembly, four 65 W soft ceiling review lamps, exposure +0.65 EV; no geometry hidden or surface made double-sided.',
          'patches':patches,'views':{}}
views={
 'rear-reference-fit':((-.57,2.90,2.24),(-.57,-3.3,2.30),18),
 'rear-offset-high':((-.70,2.77,2.36),(-.70,-3.3,2.32),18),
 'rear-forward':((-.70,3.35,2.14),(0.,-3.3,2.05),25),
 'rear-aisle':((0.,4.35,2.05),(0.,-3.3,1.6),22),
 'door-interior':((-.50,-1.50,1.60),(1.25,.30,1.4),27),
 'standing-zone':((0.,-3.4,2.15),(0.,3.5,1.8),22),
 'seat-detail':((-.70,3.05,2.10),(-.70,2.25,1.95),35),
 'ceiling':((0.,-.5,2.2),(-1.15,.8,2.65),27),
}
for name in args.views.split(','):
    eye,target,lens=views[name]
    # Authoring camera coordinates -> actual reflected physical source.
    eye=(-eye[0],eye[1],eye[2]);target=(-target[0],target[1],target[2])
    cam=scene.camera;cam.location=eye
    cam.rotation_euler=(Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler()
    cam.data.type='PERSP';cam.data.lens=lens
    path=out/(name+'.png');scene.render.filepath=str(path)
    bpy.ops.render.render(write_still=True)
    manifest['views'][name]={'file':path.relative_to(ROOT).as_posix(),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),
                            'eye_blender':eye,'target_blender':target,'lens_mm':lens}
    (out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
if hashlib.sha256(source.read_bytes()).hexdigest()!=source_hash:raise RuntimeError('Source changed during comparison render')
print('PHOTO_COMPARISON_COMPLETE '+str(out))
