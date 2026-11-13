"""Read-only Blender source audit, including interior solids and door poses.

blender --background --python-exit-code 1 --python verify_source.py --
    --blend path.blend --report report.json
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import bpy
import bmesh

ROOT=Path(__file__).resolve().parents[2]
DEGENERATE_CROSS_SQUARED_M4=1e-18
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--blend',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/SWB6109EV12G.blend')
parser.add_argument('--report',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/source-validation.json')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source=args.blend.resolve()
source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source))
scene=bpy.context.scene;scene.frame_set(0);bpy.context.view_layer.update()
master=bpy.data.collections.get('SWB6109EV12G | editable components')
if master is None:raise RuntimeError('The new-model editable collection is missing')
spec=json.loads((ROOT/'ModelSource/SWB6109EV12G/model-spec.json').read_text(encoding='utf-8'))
errors=[];issues=[];mirror_pieces={};light_layers=[];unlit_layers=[];used_lights=set();wheel_components=[];totals={'objects':0,'triangles':0,'degenerate_triangles':0,'invalid_normals':0,'nonfinite_vertices':0,'boundary_edges':0}
interior_names=('Shell','SteeringWheel','SpeedNeedle','RpmNeedle','Door0A','Door0B',
                'Door1A','Door1B','Door2A','Door2B','Fittings','Dash','Cab','Poles','Ceiling')
interior_counts={i:{'part':i,'name':name,'objects':0,'triangles':0,'degenerate_triangles':0,
                    'invalid_normals':0,'nonfinite_vertices':0,'nonfinite_world_vertices':0,
                    'boundary_edges':0,'touching_component_edges':0,'object_names':[]}
                 for i,name in enumerate(interior_names)}
for i in sorted({int(o.get('cdlc_part',-1)) for o in master.objects if int(o.get('cdlc_part',-1))>=34}):
    interior_counts[i]={'part':i,'name':'Strap'+str(i-34),'objects':0,'triangles':0,
                        'degenerate_triangles':0,'invalid_normals':0,'nonfinite_vertices':0,
                        'nonfinite_world_vertices':0,'boundary_edges':0,
                        'touching_component_edges':0,'object_names':[]}
interior_issues=[];alignment_bounds={}
deps=bpy.context.evaluated_depsgraph_get()
for ob in master.objects:
    if ob.type not in {'MESH','CURVE','FONT'}:continue
    base_part=int(ob.get('cdlc_part',16))
    if base_part in (19,20,21,22):
        wheel_components.append({'object':ob.name,'part':base_part,'preserve_lod':bool(ob.get('preserve_lod')),
                                 'cdlc_last_lod':int(ob.get('cdlc_last_lod',2))})
        if not ob.get('preserve_lod'):errors.append(ob.name+': wheel component permits decimation')
        expected_last=0 if 'fine tread ring' in ob.name.lower() else 1 if 'wheel nut' in ob.name.lower() else 2
        if int(ob.get('cdlc_last_lod',2))!=expected_last:errors.append(ob.name+': incorrect whole-component LOD policy')
    generic=json.loads(ob.get('cdlc_part_by_material','{}'))
    overrides=json.loads(ob.get('cdlc_light_part_by_material','{}'))
    light_ids=[]
    for slot,material in enumerate(ob.data.materials):
        if material is None:continue
        light_id=int(material.get('light_id',0));light_ids.append(light_id)
        target=int(overrides.get(str(slot),generic.get(str(slot),base_part)))
        if light_id:
            used_lights.add(light_id)
            light_layers.append({'object':ob.name,'material':material.name,'light_id':light_id,'cdlc_part':target})
        expected={1:23,2:23,3:24,4:25,5:26,6:27,7:28}.get(light_id)
        if expected is not None and target!=expected:
            errors.append(f'{ob.name}: light ID {light_id} is routed to part {target}, expected {expected}')
    if 'unlit' in ob.name.lower():
        unlit_layers.append({'object':ob.name,'light_ids':light_ids,'cdlc_part':base_part})
        if any(light_ids) or base_part in range(23,29):errors.append(ob.name+': static lens is assigned to a switched light circuit')
    data=bpy.data.meshes.new_from_object(ob.evaluated_get(deps),depsgraph=deps)
    assembly=ob.get('mirror_assembly')
    if assembly:
        mirror_pieces.setdefault(assembly,[]).append(([tuple(ob.matrix_world@v.co) for v in data.vertices],
                                                     [tuple(p.vertices) for p in data.polygons]))
    data.calc_loop_triangles();totals['objects']+=1;totals['triangles']+=len(data.loop_triangles)
    nonfinite=sum(not all(math.isfinite(c) for c in vertex.co) for vertex in data.vertices)
    world_nonfinite=0
    if base_part in interior_counts:
        world_points=[ob.matrix_world@vertex.co for vertex in data.vertices]
        world_nonfinite=sum(not all(math.isfinite(c) for c in point) for point in world_points)
        if ob.name=='Driver seat cushion' or base_part==1:
            key='driver_seat_cushion' if ob.name=='Driver seat cushion' else 'steering_wheel'
            current=alignment_bounds.get(key)
            if world_points:
                bounds=[[min(p[i] for p in world_points),max(p[i] for p in world_points)] for i in range(3)]
                alignment_bounds[key]=bounds if current is None else [
                    [min(a[0],b[0]),max(a[1],b[1])] for a,b in zip(current,bounds)]
    degenerate=0;normal_errors=0
    for tri in data.loop_triangles:
        a,b,c=[data.vertices[i].co for i in tri.vertices]
        if (b-a).cross(c-a).length_squared<DEGENERATE_CROSS_SQUARED_M4:degenerate+=1
        for index in tri.loops:
            n=data.corner_normals[index].vector
            if not all(math.isfinite(x) for x in n) or abs(n.length_squared-1)>1e-3:normal_errors+=1
    bm=bmesh.new();bm.from_mesh(data)
    # UV and hard-normal seams duplicate vertices. Test geometric closure after
    # a one-micrometre weld, while separately retaining all area failures above.
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
    bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=1e-7)
    boundary=sum(edge.is_boundary for edge in bm.edges)
    touching=sum(len(edge.link_faces)>2 for edge in bm.edges)
    bm.free()
    if base_part in interior_counts:
        row=interior_counts[base_part]
        row['objects']+=1;row['triangles']+=len(data.loop_triangles);row['object_names'].append(ob.name)
        for key,value in (('degenerate_triangles',degenerate),('invalid_normals',normal_errors),
                          ('nonfinite_vertices',nonfinite),('nonfinite_world_vertices',world_nonfinite),
                          ('boundary_edges',boundary),('touching_component_edges',touching)):
            row[key]+=value
        # Interior surfaces must close as authored objects; mirror-assembly
        # exemptions cannot suppress an interior boundary or invalid vertex.
        if degenerate or normal_errors or nonfinite or world_nonfinite or boundary:
            interior_issues.append({'object':ob.name,'part':base_part,'degenerate_triangles':degenerate,
                'invalid_normals':normal_errors,'nonfinite_vertices':nonfinite,
                'nonfinite_world_vertices':world_nonfinite,'boundary_edges':boundary})
    bpy.data.meshes.remove(data)
    totals['degenerate_triangles']+=degenerate;totals['invalid_normals']+=normal_errors
    totals['nonfinite_vertices']+=nonfinite;totals['boundary_edges']+=boundary
    if degenerate or normal_errors or nonfinite or boundary:
        issues.append({'object':ob.name,'cdlc_part':base_part,'degenerate_triangles':degenerate,'invalid_normals':normal_errors,
                       'nonfinite_vertices':nonfinite,'boundary_edges':boundary,'touching_component_edges':touching,
                       'mirror_assembly':assembly})
assembly_closure=[];closed_assemblies=set()
for name,pieces in mirror_pieces.items():
    vertices=[];faces=[]
    for points,polys in pieces:
        start=len(vertices);vertices.extend(points);faces.extend(tuple(start+i for i in f) for f in polys)
    mesh=bpy.data.meshes.new('audit mirror assembly');mesh.from_pydata(vertices,[],faces)
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
    bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=1e-7)
    boundary=sum(e.is_boundary for e in bm.edges);bm.free();bpy.data.meshes.remove(mesh)
    assembly_closure.append({'assembly':name,'boundary_edges_after_material_interfaces_joined':boundary})
    if boundary==0:closed_assemblies.add(name)
totals['unresolved_boundary_edges']=sum(r['boundary_edges'] for r in issues
    if r['mirror_assembly'] not in closed_assemblies or r['cdlc_part'] in interior_counts)
for key in ('degenerate_triangles','invalid_normals','nonfinite_vertices','unresolved_boundary_edges'):
    if totals[key]:errors.append(f'{key}: {totals[key]}')

required_parts=set((0,1,2,3,10,11,12,13,14))|{int(leaf['part']) for leaf in spec['door_leaves']}
required_components=[]
for part in sorted(required_parts):
    row=interior_counts.get(part)
    present=bool(row and row['objects']>0 and row['triangles']>0)
    required_components.append({'part':part,'name':interior_names[part] if part<len(interior_names) else str(part),
                                'has_actual_geometry':present})
    if not present:errors.append(f'Missing actual interior geometry for CDLC part {part}')
if interior_issues:errors.append(f'Interior geometry defects: {len(interior_issues)} authored objects')
common_readers=[ob for ob in master.objects if ob.get('cdlc_runtime_generated')=='CardReader']
reader_export=ROOT/'ModelSource/SWB6109EV12G/common-card-reader.json'
reader_hash=hashlib.sha256(reader_export.read_bytes()).hexdigest() if reader_export.exists() else None
reader_ok=(len(common_readers)==1 and reader_hash is not None
    and common_readers[0].get('shared_export_sha256')==reader_hash
    and not any(ob.name in ('Fare validator','Fare validator screen') for ob in master.objects))
if not reader_ok:errors.append('Expected exactly one current shared CardReader source copy, without the old placeholder boxes')

# A meaningful alignment check independent of left/right signs: the seat,
# driver's eye and wheel should share the designed transverse driving axis.
# This does not assert that their complete shapes never intersect.
alignment={'scope':'Closed-pose transverse alignment only; not a full inter-object collision test.',
           'bounds_source_blender':alignment_bounds,'maximum_transverse_offset_m':.08}
driver_eye=master.objects.get('DriverEye')
if driver_eye is None or any(key not in alignment_bounds for key in ('driver_seat_cushion','steering_wheel')):
    alignment['status']='FAIL';errors.append('Driver eye, seat cushion or steering geometry is missing')
else:
    seat_x=sum(alignment_bounds['driver_seat_cushion'][0])/2
    wheel_x=sum(alignment_bounds['steering_wheel'][0])/2
    eye_x=driver_eye.matrix_world.translation.x
    offsets={'seat_to_wheel_m':abs(seat_x-wheel_x),'seat_to_eye_m':abs(seat_x-eye_x)}
    aligned=all(math.isfinite(value) and value<=.08 for value in offsets.values())
    alignment.update(status='PASS' if aligned else 'FAIL',seat_x_m=seat_x,wheel_x_m=wheel_x,eye_x_m=eye_x,offsets=offsets)
    if not aligned:errors.append('Driver seat, eye and steering wheel transverse axes are misaligned')

doors=[]
for frame in (0,25,30,40,80):
    scene.frame_set(frame);bpy.context.view_layer.update();deps=bpy.context.evaluated_depsgraph_get()
    row={'frame':frame,'leaves':[],
         'angle_space':'Hinge local +Z Euler rotation; reflected parent determines source-world opening direction.',
         'bounds_space':'Actual saved Blender world, metres; +Z is vertical.'}
    for leaf in spec['door_leaves']:
        hinge=master.objects.get(leaf['pivot'])
        if hinge is None:
            errors.append('Missing door hinge '+leaf['pivot']);continue
        part=int(leaf['part']);world_vertices=[]
        for ob in master.objects:
            if ob.type not in {'MESH','CURVE','FONT'} or int(ob.get('cdlc_part',-1))!=part:continue
            data=bpy.data.meshes.new_from_object(ob.evaluated_get(deps),depsgraph=deps)
            world_vertices.extend(ob.matrix_world@vertex.co for vertex in data.vertices)
            bpy.data.meshes.remove(data)
        if not world_vertices:
            errors.append(f'Door part {part} has no geometry');continue
        angle=math.degrees(hinge.rotation_euler.z)
        expected=0. if frame in (0,80) else float(leaf['open_degrees']) if frame==40 else None
        if expected is not None and abs(angle-expected)>.01:errors.append(f'Door {part} angle at frame {frame}: {angle}, expected {expected}')
        minimum=min(p.z for p in world_vertices)
        if minimum<float(spec.get('floor_height_m',spec.get('floor_m',.34)))-1e-5:
            errors.append(f'Door {part} crosses the saloon floor at frame {frame}')
        if not all(math.isfinite(x) for p in world_vertices for x in p):errors.append(f'Door {part} nonfinite pose')
        row['leaves'].append({'part':part,'angle_degrees':angle,'minimum_z_m':minimum,
            'bounds_blender':[[min(p[i] for p in world_vertices),max(p[i] for p in world_vertices)] for i in range(3)]})
    doors.append(row)
scene.frame_set(0)
if hashlib.sha256(source.read_bytes()).hexdigest()!=source_hash:
    errors.append('Saved source changed during the read-only source audit')
report={'status':'PASS' if not errors else 'FAIL','source':str(source),'source_sha256':source_hash,
        'degenerate_cross_squared_threshold_m4':DEGENERATE_CROSS_SQUARED_M4,
        'degenerate_area_threshold_m2':math.sqrt(DEGENERATE_CROSS_SQUARED_M4)/2,
        'scope':'Read-only finite/area/normal/geometric-closure checks of exterior and all interior CDLC parts 0..14; actual component presence, driver-axis alignment, and door local angles/world bounds. Wheel clearance and visibility have separate reports.',
        'limitations':['Does not prove arbitrary inter-object collision freedom or exclude every self-intersection.',
                        'Closed edges and valid normals do not replace final multi-view source/export rendering.',
                        'Door minimum height and local angle checks do not replace complete swept-volume collision testing.'],
        'totals':totals,'issues':issues,'verified_mirror_assembly_closure':assembly_closure,'door_poses':doors,
        'light_layer_contract':{'used_light_ids':sorted(used_lights),'active_layers':light_layers,'static_unlit_layers':unlit_layers},'errors':errors}
report['interior_geometry']={'status':'FAIL' if interior_issues or not reader_ok or any(not r['has_actual_geometry'] for r in required_components) or alignment['status']!='PASS' else 'PASS',
    'scope':'Authored objects whose base CDLC part is 0..14; includes structural shell/floor, driving controls, doors, seating, cab, rails and ceiling. Optional unused third-door parts may be empty.',
    'boundary_policy':'No interior boundary exemptions; mirror assembly closure applies only to exterior mirror components.',
    'touching_edge_policy':'Edges shared by more than two faces are reported separately; touching closed components alone do not establish a hole or collision.',
    'parts':list(interior_counts.values()),'issues':interior_issues,
    'required_components':required_components,'driver_alignment':alignment}
report['shared_card_reader']={'status':'PASS' if reader_ok else 'FAIL','objects':[ob.name for ob in common_readers],
    'shared_export_sha256':reader_hash,'runtime_policy':'Generated once by BusInterior.CardReader; source copy excluded from SWB1.'}
report['wheel_lod_components']={'count':len(wheel_components),
    'tread_rings_lod0_only':sum('fine tread ring' in r['object'].lower() for r in wheel_components),
    'wheel_nuts_through_lod1':sum('wheel nut' in r['object'].lower() for r in wheel_components),
    'components':wheel_components}
args.report.parent.mkdir(parents=True,exist_ok=True)
args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('SWB6109_SOURCE_'+report['status']+' '+json.dumps(totals),flush=True)
if errors:raise RuntimeError('; '.join(errors[:8]))
