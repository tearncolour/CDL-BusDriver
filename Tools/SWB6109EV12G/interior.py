"""SWB6109EV12G: independent 27-seat design layout, low floor and four doors.

Seat count/layout are design choices within the announcement capacity range,
not a claim that a photographed production bus has this exact seat schedule.
"""
import bpy
import bmesh
import json
import math
import sys
from mathutils import Matrix,Vector
if str(ROOT/'Tools/SWB6109EV12G') not in sys.path:sys.path.insert(0,str(ROOT/'Tools/SWB6109EV12G'))
from patch_saved_interior import seat_grab_offsets,seat_materials
_seat_fabric,_seat_hard_shell,_seat_grab_material=seat_materials()
for _seat_mat in (_seat_fabric,_seat_hard_shell,_seat_grab_material):
    if _seat_mat not in MATERIALS:MATERIALS.append(_seat_mat)
_interior_before=set(MASTER.objects)


def _inside(ob,part):
    ob['cdlc_part']=part
    ob['cdlc_assignment']='authored explicit'
    ob['preserve_lod']=True
    return ob


def _closed(name,vertices,faces,mat,part=0):
    ob=mesh(name,vertices,faces,mat)
    bm=bmesh.new();bm.from_mesh(ob.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(ob.data);bm.free()
    return _inside(ob,part)


def _prism_x(name,x0,x1,outline,mat,part=0):
    n=len(outline);verts=[(x,y,z) for x in (x0,x1) for y,z in outline]
    faces=[tuple(reversed(range(n))),tuple(range(n,2*n))]
    faces.extend((i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n))
    return _closed(name,verts,faces,mat,part)


_floor=float(SPEC.get('floor_height_m',SPEC.get('floor_m',.34)))
_arch_radius=R+.080
_arch_thickness=.012
_arch_extents=[]
for ai,ay in enumerate(AXLES):
    _arch_extents.append((ay-_arch_radius-.025,ay+_arch_radius+.025))
    for side in (-1,1):
        xi=side*(.600 if ai==0 else .615);xo=side*(SIDE-.018)
        verts=[];steps=64
        for x,r in ((xi,_arch_radius),(xo,_arch_radius),(xi,_arch_radius+_arch_thickness),(xo,_arch_radius+_arch_thickness)):
            verts.extend((x,ay+r*math.cos(math.pi*i/steps),R+r*math.sin(math.pi*i/steps)) for i in range(steps+1))
        stride=steps+1;faces=[]
        for i in range(steps):
            faces += [(i,i+1,stride+i+1,stride+i),
                      (2*stride+i,3*stride+i,3*stride+i+1,2*stride+i+1),
                      (i,2*stride+i,2*stride+i+1,i+1),
                      (stride+i,stride+i+1,3*stride+i+1,3*stride+i)]
        for i in (0,steps):faces.append((i,stride+i,3*stride+i,2*stride+i))
        _closed(f'Interior wheelhouse {ai} {side} closed arch',verts,faces,panel)
        outline=[(ay-_arch_radius,_floor-.02),(ay+_arch_radius,_floor-.02)]
        outline += [(ay+_arch_radius*math.cos(math.pi*i/steps),R+_arch_radius*math.sin(math.pi*i/steps)) for i in range(steps+1)]
        _prism_x(f'Interior wheelhouse {ai} {side} inner wall',xi-side*.012,xi+side*.012,outline,panel)
        # Close the vertical lower outside legs, leaving the tyre opening open.
        for edge in (-1,1):
            _inside(box(f'Interior wheelhouse {ai} {side} lower leg {edge}',
                ((xi+xo)/2,ay+edge*(_arch_radius+.006),(R+_floor-.02)/2),
                (abs(xo-xi),.012,R-_floor+.02),panel,.003),0)

# Narrow central floor remains outside the complete +/-35 degree front envelope.
_inside(box('Floor continuous central low aisle',(0,0,_floor-.035),(1.16,L-.46,.07),floor,0),0)
_segments=[];start=-HALF+.45
for lo,hi in _arch_extents:
    if lo>start:_segments.append((start,lo))
    start=max(start,hi)
if start<HALF-.40:_segments.append((start,HALF-.40))
for side in (-1,1):
    for j,(a,b) in enumerate(_segments):
        _inside(box(f'Floor outboard wing {side} {j}',(side*(SIDE+.58-.06)/2,(a+b)/2,_floor-.035),
            (SIDE-.58-.06,b-a,.07),floor,0),0)
_inside(box('Floor front entrance leading infill',(.81,-4.875,_floor-.035),(.46,.15,.07),floor,.004),0)

# The rear deck clears the whole dual-tyre crown. Side seats start beside the
# central staircase, so row pitch remains 700 mm through the rear bench.
_rear_floor=R+_arch_radius+.043
for y,length,z in ((1.025,.18,.525),(1.205,.18,.71),(1.385,.18,.895),(1.565,.18,_rear_floor)):
    _inside(box('Floor rear saloon access step',(0,y,(_floor+z)/2),(1.16,length,z-_floor),floor,.012),0)
_inside(box('Floor rear central platform',(0,(1.655+HALF-.40)/2,_rear_floor-.030),
    (1.16,HALF-.40-1.655,.06),floor,.008),0)
for side in (-1,1):
    _inside(box('Floor rear outboard platform',(side*.91,(1.20+HALF-.40)/2,_rear_floor-.030),
        (.66,HALF-.40-1.20,.06),floor,.008),0)
_inside(box('Rear bench equipment pedestal',(0,HALF-.54,.825),(2.27,.75,.23),panel,.025),0)

# Cabin is entirely ahead of the front axle.  Its platform and partition are
# independently placed rather than importing a generic bus's cabin shell.
_driver_x=-.69;_driver_y=-3.95;_platform=.68
_inside(box('Driver platform',(-.72,-4.23,(_floor+_platform)/2),(1.00,1.54,_platform-_floor),floor,.025),0)
_inside(box('Driver rear partition lower',(-.69,-3.48,1.02),(1.10,.045,.82),panel,.015),12)
_inside(box('Driver aisle partition',(-.16,-4.13,.93),(.045,1.30,.65),panel,.012),12)
_inside(box('Driver rear partition glazing',(-.69,-3.475,1.71),(1.05,.014,.50),glass,.02),12)
for x in (-1.19,-.19):
    _inside(rod('Driver partition frame',(x,-3.48,.66),(x,-3.48,2.01),.017,chrome),12)

kit_imported=[]
kit_path=SRC/'kit-cabin.json'
if not kit_path.exists():
    raise RuntimeError('Run the new-model KitExport before building its interior')
_kit=json.loads(kit_path.read_text(encoding='utf-8'))
_parts={p['name']:p for p in _kit['lods'][0]['parts']}
_wheel_pivot=_parts['SteeringWheel']['restPivot']
_target=SPEC.get('steering_hub_unity',[-.69,1.48,4.45])
_offset=[_target[0]-_wheel_pivot[0],-(_target[2]-_wheel_pivot[2]),_target[1]-_wheel_pivot[1]]
SPEC['kit_offset_blender']=_offset
_palette={s['index']:material('6109 MeshKit '+s['name'],tuple(s['rgb']),float(s.get('roughness',.5)),float(s.get('metallic',0))) for s in _kit['palette']}
for name in ('Dash','SteeringWheel','SpeedNeedle','RpmNeedle'):
    part=_parts[name];verts=[(p[0]+_offset[0],-p[2]+_offset[1],p[1]+_offset[2]) for p in part['positions']]
    faces=[]
    for i in range(0,len(part['indices']),3):
        f=tuple(part['indices'][i:i+3]);normal=part['normals'][f[0]]
        expected=Vector((normal[0],-normal[2],normal[1]))
        if (Vector(verts[f[1]])-Vector(verts[f[0]])).cross(Vector(verts[f[2]])-Vector(verts[f[0]])).dot(expected)<0:f=tuple(reversed(f))
        faces.append(f)
    ob=mesh('MeshKit '+name,verts,faces,plastic);ob.data.materials.clear()
    for i in sorted(_palette):ob.data.materials.append(_palette[i])
    for face in ob.data.polygons:
        face.material_index=min(len(_palette)-1,max(0,int(part['uv'][face.vertices[0]][1]*len(_palette))))
    if name=='Dash':
        # Remove the generic right-entry cabinet as a whole component. Keep
        # the driver's dashboard and its actual switch/column hardware.
        bm=bmesh.new();bm.from_mesh(ob.data);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
        # Trim the generic housing to this model's inner left wall and curved
        # nose envelope. Actual polygon cuts retain closed, reviewable edges;
        # the steering column and dial pivots are unchanged.
        def cut(co,normal):
            bmesh.ops.bisect_plane(bm,geom=list(bm.verts)+list(bm.edges)+list(bm.faces),
                plane_co=co,plane_no=normal,dist=1e-7,clear_outer=True,clear_inner=False)
        cut((-SIDE+.065,0,0),(-1,0,0))
        stations=[(-SIDE+.065)+(2*SIDE-.130)*i/24 for i in range(25)]
        def nose_inside(x):return -HALF+.080+.160*(abs(x)/SIDE)**8
        for x0,x1 in zip(stations,stations[1:]):
            y0,y1=nose_inside(x0),nose_inside(x1);slope=(y1-y0)/(x1-x0)
            cut((x0,y0,0),(slope,-1,0))
        seen=set()
        for seed in list(bm.faces):
            if seed in seen or not seed.is_valid:continue
            stack=[seed];group=set()
            while stack:
                f=stack.pop()
                if f in group:continue
                group.add(f);seen.add(f)
                for e in f.edges:stack.extend(g for g in e.link_faces if g not in group)
            vs={v for f in group for v in f.verts}
            if min(v.co.x for v in vs)>.0:
                bmesh.ops.delete(bm,geom=list(group),context='FACES');continue
            boundary={e for f in group for e in f.edges if e.is_boundary}
            if boundary:
                caps=bmesh.ops.holes_fill(bm,edges=list(boundary),sides=0)['faces']
                for cap in caps:cap.material_index=seed.material_index
                bmesh.ops.triangulate(bm,faces=list(caps))
        # Partition at non-manifold junctions. Printed faces and louvre blades
        # retain their exact authored front; a real 0.8 mm back and edge returns
        # make each open patch solid without an overlapping cover panel.
        bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=1e-7)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        seen=set();patches=[]
        for seed in list(bm.faces):
            if seed in seen:continue
            stack=[seed];group=set()
            while stack:
                face=stack.pop()
                if face in group:continue
                group.add(face);seen.add(face)
                for edge in face.edges:
                    if len(edge.link_faces)==2:stack.extend(f for f in edge.link_faces if f not in group)
            edge_counts={edge:sum(f in group for f in edge.link_faces) for face in group for edge in face.edges}
            patches.extend([{face} for face in group] if any(n>2 for n in edge_counts.values()) else [group])
        vertices=[];polygons=[];materials=[];thickened=0
        for group in patches:
            vv=list({v for face in group for v in face.verts});mapping={v:i for i,v in enumerate(vv)}
            edge_counts={edge:sum(f in group for f in edge.link_faces) for face in group for edge in face.edges}
            boundary=[edge for edge,n in edge_counts.items() if n==1]
            base=len(vertices);vertices.extend(tuple(v.co) for v in vv)
            for face in group:
                polygons.append(tuple(base+mapping[v] for v in face.verts));materials.append(face.material_index)
            if not boundary:continue
            thickened+=1;back=len(vertices)
            for vertex in vv:
                normal=sum((face.normal for face in vertex.link_faces if face in group),Vector())
                if normal.length_squared<1e-12:normal=next(iter(group)).normal.copy()
                vertices.append(tuple(vertex.co-normal.normalized()*.0008))
            for face in group:
                polygons.append(tuple(back+mapping[v] for v in reversed(face.verts)));materials.append(face.material_index)
            for edge in boundary:
                a,b=edge.verts
                polygons.append((base+mapping[a],base+mapping[b],back+mapping[b],back+mapping[a]));materials.append(edge.link_faces[0].material_index)
        bm.free()
        old_materials=list(ob.data.materials);ob.data.clear_geometry();ob.data.from_pydata(vertices,[],polygons);ob.data.update()
        for face,material_index in zip(ob.data.polygons,materials):face.material_index=material_index
        bm=bmesh.new();bm.from_mesh(ob.data)
        bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
        bmesh.ops.triangulate(bm,faces=list(bm.faces))
        bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=1e-7)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        remaining=sum(edge.is_boundary for edge in bm.edges)
        if remaining:raise RuntimeError(f'Closed dashboard patches retain {remaining} boundary edges')
        ob['closed_thin_detail_components']=thickened
        bm.to_mesh(ob.data);bm.free()
    _inside(ob,11 if name=='Dash' else {'SteeringWheel':1,'SpeedNeedle':2,'RpmNeedle':3}[name]);kit_imported.append(name)

def _seat(name,x,y,z,priority=False,driver=False):
    before=set(MASTER.objects);mat=_seat_fabric
    if driver:mat=plastic
    if driver:
        _inside(box(name+' pedestal',(x,y,z+.12),(.38,.38,.24),plastic,.025),12)
        for i in range(5):box(name+' suspension bellows',(x,y,z+.05+i*.035),(.40-(i%2)*.025,.43,.040),rubber,.014)
    box(name+' base',(x,y,z+.27),(.43,.44,.085),plastic,.03)
    box(name+' cushion',(x,y,z+.355),(.43,.43,.105),mat,.042)
    back=box(name+' backrest',(x,y+.195,z+.73),(.435,.10 if driver else .060,.66),mat,.045 if driver else .027);back.rotation_euler.x=math.radians(-7)
    if driver or y < HALF-.60:
        rear_offset=Matrix.Rotation(math.radians(-7),4,'X') @ Vector((0,.066 if driver else .0385,0))
        rear=box(name+' rear shell',(x,y+.195+rear_offset.y,z+.73+rear_offset.z),(.445,.035 if driver else .018,.62),plastic if driver else _seat_hard_shell,.025 if driver else .0081)
        rear.rotation_euler.x=math.radians(-7)
        rear['seat_shell_revision']=2
    for dx in (-.16,.16):
        support=z
        if not driver and abs(x+dx)<.58 and .935<=y<1.655:
            support=.525 if y<1.115 else (.71 if y<1.295 else (.895 if y<1.475 else _rear_floor))
        rod(name+' floor fixing',(x+dx,y+.03,support+.005),(x+dx,y+.03,z+.25),.018,chrome)
    rod(name+' under-seat cross member',(x-.18,y+.03,z+.24),(x+.18,y+.03,z+.24),.019,chrome)
    if driver:
        for dx in (-.075,.075):rod(name+' headrest post',(x+dx,y+.27,z+1.00),(x+dx,y+.27,z+1.13),.010,chrome)
        box(name+' headrest',(x,y+.27,z+1.16),(.32,.11,.19),plastic,.04)
    elif y < HALF-.60:
        grip=tube(name+' upper grab',[(x+dx,y+dy,z+dz) for dx,dy,dz in seat_grab_offsets()],.016,_seat_grab_material)
        grip['seat_grip_revision']=2
    for ob in set(MASTER.objects)-before:_inside(ob,12 if driver else 10)

_seat('Driver seat',_driver_x,_driver_y,_platform,driver=True)
for px,py,width in ((-.77,-4.61,.13),(-.50,-4.61,.08)):
    ob=_inside(box('Driver floor pedal',(px,py,.74),(width,.22,.025),rubber,.008),12);ob.rotation_euler.x=math.radians(24)

seat_rows=[]
for y in (-1.60,-.85,-.10):
    for x in (-1.00,-.55):seat_rows.append((x,y,_floor,True))
for y in (1.40,2.10,2.80,3.50,4.20):
    level=_rear_floor
    for x in (-1.00,-.55,.55,1.00):seat_rows.append((x,y,level,False))
for x in (-.90,-.45,0,.45,.90):seat_rows.append((x,HALF-.37,_rear_floor,False))
from seat_access import REMOVED_SEAT_NUMBERS,CUSHION_OFFSET,apply_saved_seat_access
_seat_inventory=[(i+1,row) for i,row in enumerate(seat_rows) if i+1 not in REMOVED_SEAT_NUMBERS]
for i,(x,y,z,priority) in _seat_inventory:_seat(f'Passenger seat {i:02d}',x,y,z,priority)
from seat_geometry_photo import apply_photo_seat_geometry,stainless_material
_photo_seat_report=apply_photo_seat_geometry(MASTER)
_seat_access_report=apply_saved_seat_access(MASTER)
_seat_stainless=stainless_material()
if _seat_stainless not in MATERIALS:MATERIALS.append(_seat_stainless)

# Wheelchair bay and separate entry/exit circulation remain free of seat legs.
_inside(box('Wheelchair bay floor inset',(.89,-1.30,_floor+.002),(.64,1.28,.006),blue,.008),10)
_inside(box('Wheelchair back support',(1.16,-.64,.91),(.10,.45,.42),plastic,.035),10)
for x in (-.41,.41):
    _inside(rod('Ceiling longitudinal grab rail',(x,-2.10,2.77),(x,4.35,2.77),.021,yellow),13)
for y in (-2.05,-.38,1.10,2.05,3.45,4.45):
    base=_floor if y<.935 else (.525 if y<1.115 else _rear_floor)
    _inside(rod('Saloon upper cross rail',(-.41,y,2.77),(.41,y,2.77),.019,yellow),13)
    for x in (-.30,.30):
        _inside(rod('Saloon vertical grab pole',(x,y,base),(x,y,2.77),.022,yellow),13)
        _inside(box('Stop request push button',(x,y-.023,base+1.05),(.045,.022,.07),redseat,.012),13)
for y in (-1.7,-.95,-.2,1.65,2.4,3.15,3.9):
    for x in (-.41,.41):
        handle=2.19 if y<1.1 else 2.61
        _inside(rod('Hand strap stem',(x,y,2.77),(x,y,handle+.11),.010,plastic),13)
        _inside(tube('Passenger hand strap',[(x-.055,y,handle+.10),(x-.055,y,handle),(x+.055,y,handle),(x+.055,y,handle+.10)],.013,yellow,True),13)
_inside(box('Ceiling interior lining',(0,.125,ROOF-.115),(W-.20,L-1.25,.055),panel,.02),0)
for x in (-.88,.88):_inside(box('Ceiling passenger light',(x,.2,ROOF-.15),(.07,8.45,.028),saloon,.012),14)

door_rows=[]
for di,cy in enumerate((-4.30,.30)):
    width=1.30
    for leaf in range(2):
        part=4+di*2+leaf;lo=cy-width/2+leaf*width/2;hi=lo+width/2;yy=(lo+hi)/2
        hinge=bpy.data.objects.new(f'Door{di+1}{chr(65+leaf)} hinge',None);MASTER.objects.link(hinge)
        hinge.location=(SIDE-.060,lo if leaf==0 else hi,.36)
        before=set(MASTER.objects)
        for y in (lo+.023,hi-.023):box(f'Door {di+1}.{leaf+1} stile',(SIDE,y,1.43),(.038,.046,2.10),black,.01)
        for z in (.400,.68,2.455):box(f'Door {di+1}.{leaf+1} crossbar',(SIDE,yy,z),(.038,.61,.046),black,.008)
        box(f'Door {di+1}.{leaf+1} glazing',(SIDE,yy,1.56),(.010,.575,1.750),glass,.015)
        box(f'Door {di+1}.{leaf+1} lower closed panel',(SIDE,yy,.53),(.025,.575,.245),black,.012)
        rod(f'Door {di+1}.{leaf+1} interior grab',(SIDE-.08,yy,.98),(SIDE-.08,yy,1.67),.016,yellow)
        for ob in set(MASTER.objects)-before:
            _inside(ob,part);ob.parent=hinge;ob.matrix_parent_inverse=Matrix.Translation(hinge.location).inverted()
        door_rows.append({'part':part,'pivot':hinge.name,'open_degrees':86 if leaf==0 else -86,
                          'centre_unity':[SIDE,1.42,-yy],'hinge_unity':[hinge.location.x,.36,-hinge.location.y]})
    _inside(box(f'Door {di+1} threshold',(SIDE-.11,cy,.355),(.25,width,.025),yellow,.005),0)
    _inside(box(f'Door {di+1} actuator cover',(SIDE-.08,cy,2.545),(.20,width+.07,.11),plastic,.025),14)

_card=(.89,-3.55,1.20)
_inside(rod('Fare validator support',(.89,-3.55,.38),(.89,-3.55,1.16),.025,chrome),12)
if str(ROOT/'Tools/SWB6109EV12G') not in sys.path:sys.path.insert(0,str(ROOT/'Tools/SWB6109EV12G'))
from card_reader import replace_common_card_reader
_reader_report=replace_common_card_reader(MASTER,SRC/'common-card-reader.json')
for _reader_ob in MASTER.objects:
    if _reader_ob.get('cdlc_runtime_generated')=='CardReader':
        for _reader_mat in _reader_ob.data.materials:
            if _reader_mat not in MATERIALS:MATERIALS.append(_reader_mat)
(SRC/'common-card-reader-import.json').write_text(json.dumps(_reader_report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
SPEC['door_leaves']=[{key:row[key] for key in ('part','pivot','open_degrees')} for row in door_rows]
SPEC['driver_eye_unity']=list(SPEC.get('driver_eye_unity',[-.69,1.95,4.18]))

def _unity(p):return [float(p[0]),float(p[2]),-float(p[1])]
cabin_layout={'model':'SWB6109EV12G','units':'metres','axes':'+X door side, +Y up, +Z front',
    'layout_status':'27-seat usability-corrected design within published 20–38 seat range; not factory seat drawing',
    'passenger_seats':[{'index':j,'source_seat_number':i,'cushion_unity':_unity((x,y,z+CUSHION_OFFSET)),'floor_height_m':z,'priority':priority} for j,(i,(x,y,z,priority)) in enumerate(_seat_inventory)],
    'passenger_seat_count':len(_seat_inventory),'removed_original_seat_numbers':list(REMOVED_SEAT_NUMBERS),
    'seat_cushion_height_above_floor_m':CUSHION_OFFSET,
    'driver_seat_unity':_unity((_driver_x,_driver_y,_platform+.4075)),
    'driver_eye_unity':SPEC['driver_eye_unity'],'steering_hub_unity':_target,
    'card_reader_unity':_unity(_card),'wheelchair_bay':{'centre_unity':_unity((.89,-1.30,_floor)),'width_m':.64,'length_m':1.28},
    'doors':door_rows,'door_centres_unity':[[SIDE,.36,4.30],[SIDE,.36,-.30]],
    'door_opening_width_m':1.30,'kit_offset_blender':_offset,
    'rear_floor_height_m':_rear_floor,'rear_seat_pitch_m':.70,
    'wheelhouse_inner_x_m':{'front':.600,'rear':.615},'wheelhouse_inner_radius_m':_arch_radius}
(SRC/'cabin-layout.json').write_text(json.dumps(cabin_layout,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

# Prevent bevel clamping from collapsing the edges of thin seat backs,
# glazing and faceplates; use the local solid thickness, not rotated bounds.
for _ob in set(MASTER.objects)-_interior_before:
    if _ob.type in {'MESH','CURVE','FONT'}:_ob['preserve_lod']=True
    if _ob.type!='MESH' or not _ob.data.vertices:continue
    _extent=[max(v.co[k] for v in _ob.data.vertices)-min(v.co[k] for v in _ob.data.vertices) for k in range(3)]
    for _mod in _ob.modifiers:
        if _mod.type=='BEVEL':_mod.width=min(_mod.width,min(_extent)*.45)
