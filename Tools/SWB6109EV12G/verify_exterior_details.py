"""Check final saved plate visibility and thin lines on the curved lower body."""
import bpy, hashlib, json, math, sys
from pathlib import Path
from mathutils import Vector

sys.path.insert(0,str(Path(__file__).resolve().parent))
from source_coordinates import canonical_world_matrix

ROOT=Path(__file__).resolve().parents[2];SRC=ROOT/'ModelSource/SWB6109EV12G'
source=SRC/'SWB6109EV12G.blend';source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source));scene=bpy.context.scene;scene.frame_set(0)
MASTER=bpy.data.collections['SWB6109EV12G | editable components'];HALF=5.25
exec(Path(__file__).with_name('exterior.py').read_text(encoding='utf-8').split('end_surface();end_surface(True)')[0],globals())
bpy.context.view_layer.update();deps=bpy.context.evaluated_depsgraph_get()
bad=[];hits={};rows=[]
for i in range(41):
    for j in range(13):
        x=-.255+.510*i/40;z=.294+.120*j/12
        result=scene.ray_cast(deps,Vector((x,-6,z)),Vector((0,1,0)),distance=2)
        name=result[4].original.name if result[0] else 'MISS'
        hits[name]=hits.get(name,0)+1
        if name not in ('Front licence plate recess','Front licence plate'):bad.append([x,z,name])
for ob in MASTER.objects:
    is_seam=ob.name.startswith('Service hatch shutline ')
    is_vent=ob.name.startswith('Rear drive compartment ventilation slot ')
    if not (is_seam or is_vent):continue
    offset=.003 if is_seam else .010
    matrix=canonical_world_matrix(ob)
    pts=[matrix@p.co for p in ob.data.splines[0].points]
    errors=[abs(abs(p.x)-width_at(p.z)-offset) for p in pts]
    midpoint_errors=[abs(abs((a.x+b.x)/2)-width_at((a.z+b.z)/2)-offset) for a,b in zip(pts,pts[1:])]
    rows.append({'name':ob.name,'samples':len(pts),'max_point_error_m':max(errors),'max_chord_error_m':max(midpoint_errors)})
    if len(pts)<3 or max(errors)>1e-6 or max(midpoint_errors)>.0001:bad.append([ob.name,'curve fitting'])
bumper=[]
for ob in MASTER.objects:
    if not ob.name.startswith('Bumper mould line '):continue
    spline=ob.data.splines[0]
    matrix=canonical_world_matrix(ob)
    pts=[matrix@p.co for p in spline.points]
    row={'name':ob.name,'closed':bool(spline.use_cyclic_u),'sealed_ends':bool(ob.data.use_fill_caps)}
    bumper.append(row)
    if row['closed'] or not row['sealed_ends']:bad.append([ob.name,'path end contract'])
    if 'lower' in ob.name and min(abs(p.x) for p in pts)<.295-1e-6:bad.append([ob.name,'plate overlap'])
if len(bumper)!=3:bad.append(['bumper','expected three open paths'])
if len(rows)!=19:bad.append(['side lines','expected ten seams and nine slots'])
if hashlib.sha256(source.read_bytes()).hexdigest()!=source_hash:bad.append(['source changed'])
report={'status':'FAIL' if bad else 'PASS','source_sha256':source_hash,'plate_rays':533,'plate_hits':hits,
        'side_curves':rows,'bumper_paths':bumper,'failures':bad,
        'curve_coordinate_space':'Private canonical construction frame: +X door side, -X driver side, -Y forward, +Z up; object and parent transforms included.',
        'plate_ray_coordinate_space':'Actual Blender source world; the centred plate sampling region is symmetric about X=0.',
        'source_coordinate_version':int(scene.get('source_coordinate_version',0))}
(SRC/'exterior-details-validation.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
if bad:raise RuntimeError(json.dumps(bad))
print('EXTERIOR_DETAILS_PASS '+source_hash)
