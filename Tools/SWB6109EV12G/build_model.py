"""Build the editable Sunwin iEV10 model and CS2 import sources in Blender.

Saved Blender frame: +X vehicle left, -Y forward, +Z up; metres. See model-spec.json for
documented reference dimensions and estimates. Run with Blender --background
--factory-startup --python this_file -- [--quick]. No game files are changed.
"""
import bpy
import json
import math
import sys
from pathlib import Path
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'ModelSource' / 'SWB6109EV12G'
OUT = ROOT / 'Asset' / 'SWB6109EV12G'
PREVIEW = SRC / 'previews'
for folder in (SRC, OUT, PREVIEW):
    folder.mkdir(parents=True, exist_ok=True)
SPEC = json.loads((SRC / 'model-spec.json').read_text(encoding='utf-8'))
L, W = SPEC['length_m'], SPEC['width_m']
HALF, SIDE = L / 2, W / 2
AXLES = [-HALF + SPEC['front_overhang_m'], HALF - SPEC['rear_overhang_m']]
R = SPEC['wheel_radius_m']
ROOF = SPEC['body_roof_m']
QUICK = '--quick' in sys.argv
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1
MASTER = bpy.data.collections.new('SWB6109EV12G | editable components')
scene.collection.children.link(MASTER)
STUDIO = bpy.data.collections.new('Studio | excluded from exports')
scene.collection.children.link(STUDIO)
current_collection = MASTER
MATERIALS = []

def material(name, rgb, rough=.4, metal=0., emission=0, power=0, glass=False, control=False):
    m = bpy.data.materials.new(name)
    m.diffuse_color = (*rgb, 1)
    m.use_nodes = True
    bs = next(node for node in m.node_tree.nodes if node.type=='BSDF_PRINCIPLED')
    bs.inputs['Base Color'].default_value = (*rgb, 1)
    bs.inputs['Roughness'].default_value = rough
    bs.inputs['Metallic'].default_value = metal
    if glass:
        bs.inputs['Transmission Weight'].default_value = .92
        bs.inputs['IOR'].default_value = 1.46
        bs.inputs['Coat Weight'].default_value = .65
    if power:
        bs.inputs['Emission Color'].default_value = (*rgb, 1)
        bs.inputs['Emission Strength'].default_value = power
    m['light_id'] = emission
    m['livery_control'] = control
    m['glass'] = glass
    MATERIALS.append(m)
    return m

white = material('01 Shanghai white body | CDLC line colour', (.83, .88, .87), .27, .2, control=True)
green = material('02 Shanghai aqua twin stripe', (.12, .47, .51), .28, .15)
mint = material('03 Secondary aqua accent', (.12, .47, .51), .3, .1)
black = material('04 Gloss black glazing frit', (.009, .018, .020), .18)
rubber = material('05 EPDM seals and tyre', (.014, .019, .020), .82)
plastic = material('06 Cabin graphite', (.038, .05, .054), .55)
glass = material('07 Solar glass | separate Gls export', (.40, .52, .51), .045, glass=True)
chrome = material('08 Satin aluminium', (.53, .61, .64), .25, .85)
hubdark = material('09 Recessed wheel steel', (.06, .08, .09), .45, .65)
yellow = material('10 Safety yellow grab rails', (.95, .61, .055), .3, .25)
floor = material('11 Low floor charcoal vinyl', (.12, .15, .16), .78)
panel = material('12 Cabin warm panels', (.66, .69, .65), .6)
blue = material('13 Shanghai blue upholstery', (.045, .19, .32), .77)
redseat = material('14 Priority seat upholstery', (.56, .1, .09), .74)
head = material('15 Low beam projector', (.83, .94, 1.), .12, emission=2, power=2.5)
drl = material('16 Daytime running light rings', (.85, .98, 1.), .15, emission=1, power=2.2)
tail = material('17 Tail light lens', (.46, .009, .006), .18, emission=3, power=.5)
brake = material('18 Brake light lens', (.9, .018, .007), .18, emission=4, power=1.)
turnL = material('19 Left turn signal', (1., .30, .013), .2, emission=5, power=.5)
turnR = material('20 Right turn signal', (1., .30, .013), .2, emission=6, power=.5)
reverse = material('21 Reverse light', (.75, .83, .85), .2, emission=7, power=.2)
saloon = material('22 Interior lamp', (.8, .93, .84), .45, emission=8, power=.7)
led = material('23 Destination amber LEDs', (1., .45, .04), .45, emission=9, power=1.4)
screen = material('24 Instrument screen', (.035, .27, .34), .3, emission=9, power=.4)

def link(obj):
    for col in list(obj.users_collection):
        col.objects.unlink(obj)
    current_collection.objects.link(obj)
    return obj

def assign(obj, mat):
    obj.data.materials.append(mat)
    return obj

def mesh(name, verts, faces, mat):
    data = bpy.data.meshes.new(name)
    data.from_pydata(verts, [], faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    current_collection.objects.link(obj)
    assign(obj, mat)
    return obj

def bevel(obj, amount=.025, segments=3):
    mod = obj.modifiers.new('Manufactured edge radius', 'BEVEL')
    mod.width = amount
    mod.segments = segments
    mod.affect = 'EDGES'
    obj.modifiers.new('Weighted corner normals', 'WEIGHTED_NORMAL')
    return obj

def box(name, loc, size, mat, radius=.012):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = link(bpy.context.object)
    obj.name = name
    obj.dimensions = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    assign(obj, mat)
    if radius:
        bevel(obj, min(radius, min(size)*.45))
    return obj

def rod(name, a, b, rad, mat, vertices=12):
    delta = Vector(b) - Vector(a)
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=rad, depth=delta.length, location=(Vector(a) + Vector(b)) / 2)
    obj = link(bpy.context.object)
    obj.name = name
    obj.rotation_euler = delta.to_track_quat('Z', 'Y').to_euler()
    assign(obj, mat)
    for p in obj.data.polygons:
        p.use_smooth = True
    return obj

def tube(name, points, rad, mat, closed=False):
    curve = bpy.data.curves.new(name, 'CURVE')
    curve.dimensions = '3D'
    curve.bevel_depth, curve.bevel_resolution = rad, 2
    curve.use_fill_caps = not closed
    poly = curve.splines.new('POLY')
    poly.points.add(len(points) - 1)
    for p, co in zip(poly.points, points):
        p.co = (*co, 1)
    poly.use_cyclic_u = closed
    obj = bpy.data.objects.new(name, curve)
    current_collection.objects.link(obj)
    assign(obj, mat)
    return obj

FONT = bpy.data.fonts.load('C:/Windows/Fonts/simhei.ttf')
def label(name, body, loc, size, mat, normal=(0, -1, 0)):
    data = bpy.data.curves.new(name, 'FONT')
    data.body, data.size = body, size
    data.font, data.align_x, data.align_y = FONT, 'CENTER', 'CENTER'
    data.extrude = .0004
    data.resolution_u = 3
    obj = bpy.data.objects.new(name, data)
    current_collection.objects.link(obj)
    obj.location = loc
    obj.rotation_euler = Vector(normal).to_track_quat('Z', 'Y').to_euler()
    assign(obj, mat)
    return obj

def rounded_rect_points(width, height, radius, steps=7):
    points = []
    for cx, cz, start in [(width/2-radius, height/2-radius, 0), (-width/2+radius, height/2-radius, 90),
                           (-width/2+radius, -height/2+radius, 180), (width/2-radius, -height/2+radius, 270)]:
        for i in range(steps + 1):
            t = math.radians(start + i * 90/steps)
            points.append((cx + radius*math.cos(t), cz + radius*math.sin(t)))
    return points

def front_panel(name, width, height, z, y, radius, mat, back=False):
    pts = rounded_rect_points(width, height, radius)
    # Bow the front screen and fascia across the width, giving real curved reflection.
    verts = [(x, y + (1 if not back else -1) * .085*(x / SIDE)**2, z+v) for x, v in pts]
    if back:
        verts.reverse()
    return mesh(name, verts, [tuple(range(len(verts)))], mat)

def front_ring(name, outer, inner, y, mat, back=False):
    op=rounded_rect_points(outer[0],outer[1],outer[3])
    ip=rounded_rect_points(inner[0],inner[1],inner[3])
    verts=[(x,y+(1 if not back else -1)*.085*(x/SIDE)**2,z+shape[2])
           for shape,pts in [(outer,op),(inner,ip)] for x,z in pts]
    n=len(op)
    faces=[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    if back:faces=[tuple(reversed(f)) for f in faces]
    return mesh(name,verts,faces,mat)

def side_ring(name, y, width, bottom_z, top_z, x, mat):
    # An open window frame, not an opaque pane behind the glass.
    t=.04
    coords=[(y-width/2,bottom_z),(y+width/2,bottom_z),(y+width/2,top_z),(y-width/2,top_z),
            (y-width/2+t,bottom_z+t),(y+width/2-t,bottom_z+t),(y+width/2-t,top_z-t),(y-width/2+t,top_z-t)]
    faces=[(i,(i+1)%4,(i+1)%4+4,i+4) for i in range(4)]
    if x<0:faces=[tuple(reversed(f)) for f in faces]
    return mesh(name,[(x,a,b) for a,b in coords],faces,mat)


for module in ('export_helpers.py','exterior.py','front_badges.py','wheels.py','interior.py','mirror_shell.py','animation_rig.py','livery_uv.py'):
    exec(Path(__file__).with_name(module).read_text(encoding='utf-8'),globals())
rig_report=prepare_animation_rig(MASTER,scene,SPEC)
rig_report['straps']=prepare_strap_rig(MASTER,scene)
from door_details import patch_photo_door_details
from photo_cabin_details import patch_photo_cabin_details
rig_report['photo_doors']=patch_photo_door_details(MASTER,scene)
rig_report['straps']=rig_report['photo_doors']['straps']
rig_report['photo_cabin']=patch_photo_cabin_details(MASTER,scene)
from seat_print import apply_seat_print
rig_report['seat_print']=apply_seat_print(MASTER)
from material_contract import apply_material_contract
rig_report['material_contract']=apply_material_contract(MASTER)
from window_configuration import apply_window_configuration
rig_report['window_configuration']=apply_window_configuration(MASTER,scene)
from wheelhouse_seams import apply_saved_wheelhouse_seams
rig_report['wheelhouse_seams']=apply_saved_wheelhouse_seams(MASTER)
rig_report['parts']=collect_cdlc_parts(MASTER)
rig_report['part_object_counts']={str(p['index']):len(p['objects']) for p in rig_report['parts']}
(SRC/'animation-report.json').write_text(json.dumps(rig_report,indent=2),encoding='utf-8')
prepare_livery_uv(MASTER,L,W,SPEC['body_roof_m'])
for name,pos in [('DriverEye',(-.69,-4.18,1.95)),('BoardingDoor',(1.275,-4.3,.36)),('AlightingDoor',(1.275,.3,.36))]:
    obj=bpy.data.objects.new(name,None);MASTER.objects.link(obj);obj.location=pos;obj.empty_display_size=.12
scene['model_id']='SWB6109EV12G'
scene['status']='Photo-referenced editable model; game packaging not performed'
scene['coordinates']='Internal CDLC preparation frame; converted to physical Blender coordinates before saving'
scene['reference_dimensions']='10500 x 2550 x 3200 mm; 5600 mm wheelbase; 2400/2500 mm overhangs'
scene['cdlc_livery']='White body -> ControlMask red; five independent wrap UV faces; glass exported separately'
scene['kit_reuse']=', '.join(kit_imported)
(SRC/'model-spec.json').write_text(json.dumps(SPEC,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.context.view_layer.update()
source_objects=[o for o in MASTER.objects if o.type in {'MESH','CURVE','FONT'}]
MATERIALS=sorted({m for o in source_objects for m in o.data.materials if m is not None},key=lambda m:m.name)
exec(Path(__file__).with_name('wheel_clearance.py').read_text(encoding='utf-8'),globals())
clearance=check_wheel_clearance(source_objects,flatten_object,AXLES,R,SPEC['front_track_m'],SPEC['rear_track_m'],
    half_width=SPEC['tyre_half_width_m'],tread_depth=SPEC['tyre_tread_depth_m'],rear_dual_offset=SPEC['rear_dual_offset_m'])
(SRC/'wheel-clearance.json').write_text(json.dumps(clearance,indent=2),encoding='utf-8')
if clearance['penetrations']:
    raise RuntimeError('Tyre/body penetration: '+json.dumps(clearance['penetrations']))
stats=[]
if '--geometry-only' not in sys.argv:
    exec(Path(__file__).with_name('write_runtime.py').read_text(encoding='utf-8'),globals())
    write_cdlc_runtime(source_objects,collect_cdlc_parts(MASTER),flatten_object,ROOT,SPEC)
    body_objects=[o for o in source_objects if not any(m and m.get('glass') for m in o.data.materials)]
    glass_objects=[o for o in source_objects if any(m and m.get('glass') for m in o.data.materials)]
    body_mats=[m for m in MATERIALS if not m.get('glass')]
    glass_mats=sorted({m for ob in glass_objects for m in ob.data.materials if m and m.get('glass')},key=lambda m:m.name)
    LOD=bpy.data.collections.new('Export LOD meshes | hidden in render');scene.collection.children.link(LOD)
    current_collection=LOD
    for lod,ratio in enumerate(SPEC['runtime_lod_ratios']):
        name='SWB6109EV12G'+(f'_LOD{lod}' if lod else '')
        row={'lod':lod}
        for isglass,objects,mats in [(False,body_objects,body_mats),(True,glass_objects,glass_mats)]:
            nm=name+('_Gls' if isglass else '')
            ob=merged(nm,objects,mats,1. if isglass else ratio)
            row['glass_triangles' if isglass else 'body_triangles']=len(ob.data.polygons)
            write_atlas(nm,mats);export_fbx(ob,nm)
            ob.hide_render=True;ob.hide_set(True)
        stats.append(row)
    LOD.hide_render=True
    (SRC/'build-report.json').write_text(json.dumps({'model':'SWB6109EV12G','lods':stats,'editable_objects':len(source_objects),
        'kit_reused':kit_imported,'game_packaged':False,'dimensions':SPEC},ensure_ascii=False,indent=2),encoding='utf-8')

current_collection=STUDIO
box('Studio ground',(0,0,-.07),(200,200,.10),material('Studio floor',(.62,.66,.68),.85),0)
world=bpy.data.worlds.new('Studio ambient');scene.world=world;world.use_nodes=True
background=next(node for node in world.node_tree.nodes if node.type=='BACKGROUND')
background.inputs[0].default_value=(.28,.34,.39,1)
background.inputs[1].default_value=.55
for name,location,power,size in [('Key',(4,-6,9),2300,8),('Rim',(-5,4,8),2700,7),('Front fill',(0,-8,4),850,5)]:
    ld=bpy.data.lights.new(name,'AREA');ld.energy=power;ld.shape='DISK';ld.size=size
    ob=bpy.data.objects.new(name,ld);STUDIO.objects.link(ob);ob.location=location
    ob.rotation_euler=(Vector((0,0,1.4))-ob.location).to_track_quat('-Z','Y').to_euler()
camdata=bpy.data.cameras.new('Presentation camera');cam=bpy.data.objects.new('Presentation camera',camdata);STUDIO.objects.link(cam);scene.camera=cam
cam.location=(-12,-16,5);cam.rotation_euler=(Vector((0,0,1.5))-cam.location).to_track_quat('-Z','Y').to_euler();camdata.lens=50
scene.render.engine='CYCLES';scene.cycles.samples=24 if QUICK else 64;scene.cycles.use_denoising=True
scene.render.resolution_x=1600;scene.render.resolution_y=1050;scene.render.resolution_percentage=100
scene.view_settings.view_transform='AgX'
for screen_data in bpy.data.screens:
    for area in screen_data.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_distance=15
            area.spaces.active.region_3d.view_location=(0,0,1.5)
            area.spaces.active.region_3d.view_rotation=cam.rotation_euler.to_quaternion()
            area.spaces.active.shading.type='MATERIAL';area.spaces.active.clip_end=1000
bpy.ops.object.select_all(action='DESELECT');scene.frame_set(0)
from source_coordinates import apply_source_coordinates, sync_coordinate_metadata
apply_source_coordinates(MASTER,end_point,width_at,scene)
sync_coordinate_metadata(SRC)
bpy.ops.file.pack_all()
savepath=ROOT/'out/SWB6109EV12G-geometry.blend' if '--geometry-only' in sys.argv else SRC/'SWB6109EV12G.blend'
savepath.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(savepath))
if '--no-render' not in sys.argv:
    exec(Path(__file__).with_name('render_model.py').read_text(encoding='utf-8'),globals())
print('SWB_BUILD_OK '+json.dumps(stats))

