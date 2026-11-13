"""Editable Blender motion rig and the fixed BusInterior/CDLC part contract.

Execute in build_model.py's globals, then call
    report = prepare_animation_rig(MASTER, scene, SPEC)

collect_cdlc_parts(MASTER) returns canonical rest frames plus the current posed
frame used to turn world-space geometry into part-space mesh coordinates.
No mesh export, game installation or runtime source mutation occurs here.
"""

import json as _rig_json
import math as _rig_math
from pathlib import Path as _RigPath
import bpy as _rig_bpy
import sys as _rig_sys
from mathutils import Matrix as _RigMatrix, Quaternion as _RigQuaternion, Vector as _RigVector
_rig_sys.path.insert(0,str(_RigPath(__file__).resolve().parent))
from source_coordinates import (canonical_world_matrix as _rig_canonical_world,
    canonical_export_context as _rig_canonical_context, source_root as _rig_source_root,
    SOURCE_REFLECTION as _RIG_SOURCE_REFLECTION)


CDLC_PART_NAMES = (
    'Shell', 'SteeringWheel', 'SpeedNeedle', 'RpmNeedle',
    'Door0A', 'Door0B', 'Door1A', 'Door1B', 'Door2A', 'Door2B',
    'Fittings', 'Dash', 'Cab', 'Poles', 'Ceiling', 'Outside',
    'Exterior', 'Glazing', 'Livery', 'WheelFL', 'WheelFR', 'WheelRL', 'WheelRR',
    'HeadLamps', 'TailLamps', 'BrakeLamps', 'IndicatorL', 'IndicatorR', 'ReverseLamps',
    'WrapLeft', 'WrapRight', 'WrapFront', 'WrapRear', 'WrapRoof',
)
CDLC_LIGHT_PARTS = {1:23, 2:23, 3:24, 4:25, 5:26, 6:27, 7:28}
_RIG_COORD = _RigMatrix(((1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))


def cdlc_part_name(index):
    if not 0 <= index < 256:
        raise ValueError('CDLC part index outside supported range: '+str(index))
    return CDLC_PART_NAMES[index] if index < len(CDLC_PART_NAMES) else 'Strap'+str(index-len(CDLC_PART_NAMES))


def _rig_flat(matrix):
    return [float(matrix[row][column]) for column in range(4) for row in range(4)]


def _rig_matrix(values):
    return _RigMatrix([[float(values[column*4+row]) for column in range(4)] for row in range(4)])


def _rig_descendants(obj):
    out=[]
    for child in list(obj.children):
        out.append(child)
        out.extend(_rig_descendants(child))
    return out


def _rig_reparent(obj, parent):
    world=obj.matrix_world.copy()
    obj.parent=parent
    obj.matrix_parent_inverse=parent.matrix_world.inverted()
    obj.matrix_world=world


def _rig_empty(master, name, matrix=None, size=.075):
    obj=master.objects.get(name)
    if obj is None:
        obj=_rig_bpy.data.objects.new(name,None)
        master.objects.link(obj)
    obj.empty_display_type='PLAIN_AXES'
    obj.empty_display_size=size
    if matrix is not None:
        obj.matrix_world=matrix
    return obj


def _rig_stamp(obj, part, reason='part hierarchy'):
    obj['cdlc_part']=int(part)
    obj['cdlc_part_name']=cdlc_part_name(part)
    obj['cdlc_parent_part']=-1 if part==0 else 0
    obj['cdlc_assignment']=reason


def _rig_record_pivot(obj, part, rest_matrix, export_basis=None):
    _rig_stamp(obj,part,'canonical part pivot')
    obj['cdlc_is_part_pivot']=True
    obj['cdlc_rest_matrix_blender']=_rig_flat(rest_matrix)
    # Rest/posed export frames map Unity-oriented PART coordinates directly
    # into Blender model coordinates. Thus only the world side is converted.
    unity=_RIG_COORD.inverted() @ rest_matrix
    obj['cdlc_export_frame_basis']=_rig_flat(export_basis if export_basis is not None else _RigMatrix.Identity(4))
    obj['cdlc_pivot']=[float(v) for v in rest_matrix.translation]
    obj['cdlc_pivot_unity']=[float(v) for v in unity.translation]
    q=unity.to_quaternion()
    obj['cdlc_rest_rotation']=[float(q.x),float(q.y),float(q.z),float(q.w)]
    obj['cdlc_pose_frame_note']='Use current matrix_world inverse to bake vertices; use cdlc_rest_matrix_blender for the canonical part rest pose.'


def _rig_key_rotation(obj, axis, samples, action_name, linear=False):
    obj.rotation_mode='XYZ'
    obj.animation_data_clear()
    for frame, angle in samples:
        obj.rotation_euler[axis]=_rig_math.radians(angle)
        obj.keyframe_insert(data_path='rotation_euler',index=axis,frame=frame,group=action_name)
    action=obj.animation_data.action
    action.name=action_name
    # Blender 4.4+ stores F-curves in layered action channel bags.
    curves=[]
    if hasattr(action,'fcurves'):
        curves.extend(action.fcurves)
    else:
        for layer in action.layers:
            for strip in layer.strips:
                if hasattr(strip,'channelbags'):
                    for bag in strip.channelbags:
                        curves.extend(bag.fcurves)
    for curve in curves:
        for key in curve.keyframe_points:
            key.interpolation='LINEAR' if linear else 'BEZIER'
            key.handle_left_type='AUTO_CLAMPED'
            key.handle_right_type='AUTO_CLAMPED'


def assign_cdlc_semantics(master):
    """Assign each geometry object once, with a per-material light override map.

    A moving object's ancestor takes precedence over cosmetic material names.
    Light material overrides are provided for static multilateral lamp meshes.
    """
    geometry={'MESH','CURVE','FONT','SURFACE','META'}
    wheel_parts={'WheelFL':19,'WheelFR':20,'WheelRL':21,'WheelRR':22}
    totals={str(i):0 for i in range(len(CDLC_PART_NAMES))}
    for obj in master.objects:
        if obj.get('cdlc_is_part_pivot') or obj.get('cdlc_rig_base'):
            continue
        if obj.type not in geometry:
            continue
        inherited=None
        p=obj.parent
        while p is not None:
            if p.get('cdlc_is_part_pivot'):
                inherited=int(p['cdlc_part']); break
            p=p.parent
        materials=list(obj.data.materials) if hasattr(obj.data,'materials') else []
        lights=sorted({int(m.get('light_id',0)) for m in materials if m and int(m.get('light_id',0))})
        light_overrides={str(i):CDLC_LIGHT_PARTS[int(m.get('light_id',0))] for i,m in enumerate(materials)
                         if m and int(m.get('light_id',0)) in CDLC_LIGHT_PARTS}
        obj['cdlc_light_ids']=lights
        obj['cdlc_light_part_by_material']=_rig_json.dumps(light_overrides,sort_keys=True)
        name=obj.name.lower()
        authored=obj.get('cdlc_part')
        if obj.get('bone') in wheel_parts:
            part=wheel_parts[obj['bone']];reason='wheel bone'
        elif inherited is not None:
            part=inherited; reason='animated ancestor'
        elif authored is not None and 0<=int(authored)<len(CDLC_PART_NAMES) and obj.get('cdlc_assignment') in (None,'authored explicit'):
            part=int(authored);reason='authored explicit'
        elif any(s in name for s in ('mirror','wiper','cctv','camera')):
            part=15;reason='external cab fittings'
        elif materials and all(m and m.get('livery_control') for m in materials):
            part=18;reason='line-colour controlled body paint'
        elif 'door opening warning light' in name:
            part=14;reason='door warning beacon'
            obj['cdlc_finish_override']='Beacon'
        elif len(lights)==1 and lights[0] in CDLC_LIGHT_PARTS and all(m and int(m.get('light_id',0))==lights[0] for m in materials):
            part=CDLC_LIGHT_PARTS[lights[0]]; reason='exterior lamp material'
        elif name.startswith('meshkit dash') or 'dashboard' in name or 'instrument cluster' in name:
            part=11;reason='dashboard'
        elif name.startswith('driver ') or any(s in name for s in ('driver platform','pedal','fare validator','cab partition','driver console','extinguisher','emergency hammer')):
            part=12;reason='driver cabin'
        elif name.startswith('passenger seat') or name.startswith('rear bench'):
            part=10;reason='passenger fittings'
        elif any(s in name for s in ('hand strap','grab pole','longitudinal rail','pole floor','stop request','grab rail')):
            part=13;reason='saloon poles'
        elif name.startswith('ceiling') or any(s in name for s in ('interior lamp','ceiling lining','saloon light')):
            part=14;reason='ceiling fittings'
        elif any(s in name for s in ('mirror','wiper','cctv','camera')):
            part=15;reason='external cab fittings'
        elif any(s in name for s in ('stripe','pinstripe','wordmark','fleet name','operator name','sill dash')):
            part=16;reason='fixed-colour exterior markings'
        elif any(m and m.get('glass') for m in materials) or any(s in name for s in ('glazing','window frit','window gasket','window seal','ceramic','black eyebrow','windscreen gasket','black mask','window epdm','window release','sliding window')):
            part=17;reason='glazing and window frames'
        elif name.startswith('floor') or any(s in name for s in ('wheelhouse','wheel housing','wheel arch housing','equipment cabinet')):
            part=0;reason='structural shell'
        else:
            part=16;reason='exterior body and fixed equipment'
        _rig_stamp(obj,part,reason)
        totals[str(part)]=totals.get(str(part),0)+1
    return totals


def collect_cdlc_parts(master):
    """Fixed 34-row prefix followed by individually articulated straps.

    posed_part_to_blender is sampled at the CURRENT frame. For a geometry
    vertex world position p, local rest geometry is inverse(posed_frame) @ p.
    The serialized rest pivot/rotation must come from rest_matrix_blender or
    pivot_unity/rest_rotation_unity, never the current animated matrix.
    """
    pivots={int(o['cdlc_part']):o for o in master.objects if o.get('cdlc_is_part_pivot')}
    parts=[]
    count=max(len(CDLC_PART_NAMES),max(pivots,default=-1)+1)
    if any(i not in pivots for i in range(len(CDLC_PART_NAMES),count)):
        raise ValueError('Strap part indices must be contiguous after 33')
    for index in range(count):
        name=cdlc_part_name(index)
        pivot=pivots.get(index)
        rest=_rig_matrix(pivot['cdlc_rest_matrix_blender']) if pivot else _RigMatrix.Identity(4)
        # The import transform is authored explicitly in model-spec.json.
        unity=_RIG_COORD.inverted() @ rest
        q=unity.to_quaternion()
        parts.append({'index':index,'name':name,'parent':-1 if index==0 else 0,
                      'pivot_object':pivot.name if pivot else None,
                      'pivot_blender':list(rest.translation),
                      'pivot_unity':list(unity.translation),
                      'rest_rotation_unity':[q.x,q.y,q.z,q.w],
                      'rest_matrix_blender':_rig_flat(rest),
                      'posed_part_to_blender':_rig_flat(_rig_canonical_world(pivot) @ _rig_matrix(pivot['cdlc_export_frame_basis']) if pivot else rest),
                      'objects':[o.name for o in master.objects if o.type in {'MESH','CURVE','FONT'} and int(o.get('cdlc_part',-1))==index]})
    return parts


def discover_strap_groups(master, tolerance=.003):
    """Pair the actual stem and grip at each attachment, in canonical metres."""
    def center_top(obj):
        matrix=_rig_canonical_world(obj)
        corners=[matrix @ _RigVector(p) for p in obj.bound_box]
        return ((min(p.x for p in corners)+max(p.x for p in corners))/2,
                (min(p.y for p in corners)+max(p.y for p in corners))/2,
                max(p.z for p in corners))
    stems=[o for o in master.objects if o.name.startswith('Hand strap stem')]
    grips=[o for o in master.objects if o.name.startswith('Passenger hand strap')]
    if len(stems)!=len(grips):
        raise ValueError('Each hand strap requires exactly one stem and one grip')
    unused=set(grips);groups=[]
    for stem in stems:
        x,y,top=center_top(stem)
        candidates=[o for o in unused if sum((a-b)**2 for a,b in zip(center_top(o)[:2],(x,y))) < tolerance*tolerance]
        if len(candidates)!=1:
            raise ValueError('Ambiguous or missing grip for '+stem.name)
        grip=candidates[0];unused.remove(grip)
        groups.append({'stem':stem,'grip':grip,'pivot':(x,y,top)})
    if unused:raise ValueError('Unpaired hand strap grips')
    return sorted(groups,key=lambda g:(round(g['pivot'][1],4),round(g['pivot'][0],4)))


def prepare_strap_rig(master, scene=None):
    """Add spec 4.2 strap pivots without reconstructing any authored geometry.

    Run at frame zero before source correction on new builds, or on a saved
    corrected master. The context restores the source reflection afterwards.
    The pivot itself has canonical Blender axes; its exact export basis maps
    Unity X/up Y/rail Z onto Blender X/Z/-Y without a 90-degree decomposition.
    """
    scene=scene or _rig_bpy.context.scene
    old_frame=scene.frame_current
    scene.frame_set(0);_rig_bpy.context.view_layer.update()
    try:
        with _rig_canonical_context(master):
            groups=discover_strap_groups(master)
            if len(groups)>256-len(CDLC_PART_NAMES):raise ValueError('Too many hand straps')
            source_root=_rig_source_root(master)
            existing={int(o['cdlc_part']):o for o in master.objects
                      if o.get('cdlc_is_part_pivot') and int(o.get('cdlc_part',-1))>=34}
            if set(existing)-set(range(34,34+len(groups))):
                raise ValueError('Stale strap pivots require explicit review')
            rows=[]
            for ordinal,group in enumerate(groups):
                index=34+ordinal;position=_RigVector(group['pivot'])
                pivot=existing.get(index)
                if pivot is None:
                    pivot=_rig_empty(master,'CDLC Part '+str(index)+' '+cdlc_part_name(index),size=.045)
                    pivot.parent=source_root
                    pivot.matrix_parent_inverse=_RigMatrix.Identity(4)
                    pivot.location=position
                    pivot.rotation_euler=(0,0,0);pivot.scale=(1,1,1)
                    _rig_bpy.context.view_layer.update()
                    for obj in (group['stem'],group['grip']):
                        # Keep authored basis channels intact; changing the
                        # parent inverse avoids decomposing a mirrored matrix.
                        inherited=(obj.parent.matrix_world @ obj.matrix_parent_inverse
                                   if obj.parent else _RigMatrix.Identity(4))
                        obj.parent=pivot
                        obj.matrix_parent_inverse=pivot.matrix_world.inverted() @ inherited
                    factor=.8+.4*((ordinal*.618034)%1.)
                    pivot.rotation_mode='QUATERNION'
                    for frame,x,z in ((0,0,0),(12,10,-8),(20,0,0),(40,0,0),(60,0,0),(72,-12,9),(80,0,0)):
                        # ApplyStraps uses RotateZ(z*f) * RotateX(x*f).
                        qx=_RigQuaternion((1,0,0),_rig_math.radians(x*factor))
                        qz=_RigQuaternion((0,0,1),_rig_math.radians(z*factor))
                        rotation=_RIG_COORD.to_3x3() @ (qz @ qx).to_matrix() @ _RIG_COORD.to_3x3().inverted()
                        pivot.rotation_quaternion=rotation.to_quaternion()
                        pivot.keyframe_insert(data_path='rotation_quaternion',frame=frame,group='CDLC strap swing')
                    pivot.animation_data.action.name='CDLC | '+cdlc_part_name(index)+' | independent swing'
                elif any(o.parent!=pivot for o in (group['stem'],group['grip'])):
                    raise ValueError('Existing strap pivot does not own its original pair: '+pivot.name)
                rest=_RigMatrix.Translation(position) @ _RIG_COORD
                _rig_record_pivot(pivot,index,rest,_RIG_COORD)
                pivot['cdlc_strap_rig_version']=1
                pivot['cdlc_motion']='Unity local X fore/aft and local Z sideways, RotateZ * RotateX; frame 0 rest; demonstration 12/72.'
                pivot['cdlc_strap_objects']=[group['stem'].name,group['grip'].name]
                for obj in (group['stem'],group['grip']):
                    _rig_stamp(obj,index,'animated strap pair')
                    obj['preserve_lod']=True
                    obj['cdlc_pivot_object']=pivot.name
                    for key in ('cdlc_pivot','cdlc_pivot_unity','cdlc_rest_rotation'):
                        obj[key]=list(pivot[key])
                rows.append({'index':index,'name':cdlc_part_name(index),'objects':list(pivot['cdlc_strap_objects']),
                             'pivot_blender':list(position),'pivot_unity':list(pivot['cdlc_pivot_unity'])})
            scene.frame_set(0);_rig_bpy.context.view_layer.update()
            if source_root:
                for row in rows:
                    pivot=master.objects['CDLC Part '+str(row['index'])+' '+row['name']]
                    for obj in (pivot,master.objects[row['objects'][0]],master.objects[row['objects'][1]]):
                        obj['cdlc_source_canonical_matrix_at_conversion']=_rig_flat(obj.matrix_world)
                        obj['cdlc_pivot_source_blender']=list(_RIG_SOURCE_REFLECTION @ _RigVector(obj['cdlc_pivot']))
                        if 'cdlc_rest_matrix_blender' in obj:
                            obj['cdlc_rest_matrix_source_blender']=_rig_flat(_RIG_SOURCE_REFLECTION @ _rig_matrix(obj['cdlc_rest_matrix_blender']))
            report={'version':1,'count':len(rows),'first_part':34,'part_count':34+len(rows),
                    'pivot_basis':'Unity body-aligned X transverse, Y up, Z longitudinal; rest rotation identity',
                    'demonstration_frames':[12,72],'rest_frames':[0,20,40,60,80],'straps':rows}
            scene['cdlc_strap_rig_report']=_rig_json.dumps(report)
            scene['cdlc_part_contract']='BusInterior fixed 0..33; independent Strap0..N from 34; optional fixed slots retained.'
            return report
    finally:
        scene.frame_set(old_frame);_rig_bpy.context.view_layer.update()


def prepare_animation_rig(master, scene, spec):
    """Create a closed-at-frame-0 demonstration with usable editable actions."""
    if scene.get('cdlc_animation_rig_version')==1:
        report=_rig_json.loads(scene.get('cdlc_rig_report','{}'))
        report.update(version=1,reused=True,parts=collect_cdlc_parts(master))
        return report
    scene.frame_set(0)
    _rig_bpy.context.view_layer.update()
    pivots={}
    warnings=[]

    # The new model authors its actual hinge locations; never relocate a hinge
    # according to a different vehicle's door geometry.
    default_leaves=[{'part':4+i,'pivot':f'Door{1+i//2}{"A" if i%2==0 else "B"} hinge',
                     'open_degrees':86 if i%2==0 else -86} for i in range(6)]
    leaves=spec.get('door_leaves',[row for row in default_leaves if master.objects.get(row['pivot'])])
    active_door_parts=set()
    for leaf in leaves:
        name,index=leaf['pivot'],int(leaf['part'])
        if index not in range(4,10) or index in active_door_parts:
            raise ValueError('Door leaves require unique CDLC indices 4..9')
        active_door_parts.add(index)
        hinge=master.objects.get(name)
        if hinge is None:
            raise ValueError('Missing authored door hinge '+name)
        rest=hinge.matrix_world.copy()
        _rig_record_pivot(hinge,index,rest @ _RIG_COORD,_RIG_COORD)
        pivots[index]=hinge
        degrees=float(leaf.get('open_degrees',86 if index%2==0 else -86))
        _rig_key_rotation(hinge,2,[(0,0),(20,0),(40,degrees),(60,degrees),(80,0)],'CDLC | '+CDLC_PART_NAMES[index]+' | open close')
        hinge['cdlc_motion']='Local +Z hinge rotation; degrees; frame 0 closed.'
        for child in _rig_descendants(hinge):_rig_stamp(child,index)

    # Steering acts about vertical Z; rolling is about the wheel's local X.
    # Separate transforms prevent steering from changing the axle spin axis.
    steering_samples=[(0,0),(12,-16),(20,0),(40,0),(60,0),(72,24),(80,0)]
    rolling_samples=[(0,0),(20,720),(60,720),(80,1440)]
    for name,index in (('WheelFL',19),('WheelFR',20),('WheelRL',21),('WheelRR',22)):
        steer=master.objects.get(name+' pivot')
        if steer is None:
            warnings.append('Missing '+name+' pivot');continue
        rest=steer.matrix_world.copy()
        original_children=list(steer.children)
        roll=_rig_empty(master,name+' roll pivot',rest,.11)
        _rig_reparent(roll,steer)
        _rig_bpy.context.view_layer.update()
        for child in original_children:_rig_reparent(child,roll)
        steer['cdlc_rig_base']=True
        _rig_stamp(steer,index,'road wheel steering base')
        _rig_record_pivot(roll,index,rest @ _RIG_COORD,_RIG_COORD)
        pivots[index]=roll
        if index in (19,20):
            _rig_key_rotation(steer,2,steering_samples,'CDLC | '+name+' | steering')
        _rig_key_rotation(roll,0,rolling_samples,'CDLC | '+name+' | rolling',linear=True)
        roll['cdlc_motion']='Local +X rolling, parent +Z steering; forward travel is -Y.'
        for child in _rig_descendants(roll):_rig_stamp(child,index)

    # Recover the exact steering-column/dial axes from the actual MeshKit
    # posed export. These meshes were imported as baked world coordinates.
    root=_RigPath(__file__).resolve().parents[2]
    kit_path=root/'ModelSource'/'SWB6109EV12G'/'kit-cabin.json'
    kit_parts={}
    if kit_path.exists():
        kit=_rig_json.loads(kit_path.read_text(encoding='utf-8'))
        kit_parts={p['name']:p for p in kit['lods'][0]['parts']}
    else:warnings.append('MeshKit export unavailable; instrument pivots use geometric centres.')
    shift=_RigMatrix.Translation(spec.get('kit_offset_blender',[0,0,0]))
    instrument_samples={
        1:[(frame,degrees*14) for frame,degrees in steering_samples],
        2:[(0,0),(12,42),(20,0),(40,0),(60,0),(72,90),(80,0)],
        3:[(0,0),(12,64),(20,0),(40,0),(60,0),(72,120),(80,0)],
    }
    baselines={1:0.0,2:-120.0,3:240.0*(800.0/3000.0-.5)}
    for index in (1,2,3):
        name=CDLC_PART_NAMES[index]
        mesh=master.objects.get('MeshKit '+name)
        if mesh is None:
            warnings.append('Missing MeshKit '+name);continue
        part=kit_parts.get(name)
        if part:
            # Matrix is column-major, already includes speed=0 / RPM=800 pose.
            posed=shift @ _RIG_COORD @ _rig_matrix(part['partToModel'])
        else:
            center=sum((mesh.matrix_world@_RigVector(c) for c in mesh.bound_box),_RigVector())/8
            posed=_RigMatrix.Translation(center)
        correction=_RigMatrix.Rotation(_rig_math.radians(-baselines[index]),4,'Z')
        canonical=posed @ correction
        base=_rig_empty(master,'CDLC '+name+' rest frame',posed)
        base['cdlc_rig_base']=True
        _rig_stamp(base,index,'MeshKit posed column/dial base')
        motion=_rig_empty(master,'CDLC '+name+' motion pivot',posed)
        motion.parent=base
        motion.matrix_parent_inverse=_RigMatrix.Identity(4)
        motion.matrix_basis=_RigMatrix.Identity(4)
        _rig_bpy.context.view_layer.update()
        _rig_reparent(mesh,motion)
        _rig_record_pivot(motion,index,canonical)
        motion['cdlc_export_pose_baseline_deg']=baselines[index]
        motion['cdlc_motion']='Local +Z instrument axis; action is relative to MeshKit board pose 0.'
        pivots[index]=motion
        _rig_key_rotation(motion,2,instrument_samples[index],'CDLC | '+name+' | demonstration')
        _rig_stamp(mesh,index)

    # Empty optional parts retain the complete BusInterior 0..33 contract.
    for index,name in enumerate(CDLC_PART_NAMES):
        if index in pivots:continue
        frame=_RigMatrix.Identity(4)
        if index==7 and 6 in pivots:frame=pivots[6].matrix_world.copy()
        pivot=_rig_empty(master,f'CDLC Part {index:02d} {name}',frame,.045)
        _rig_record_pivot(pivot,index,frame @ _RIG_COORD,_RIG_COORD)
        pivot['cdlc_optional_empty']=(index in range(4,10) and index not in active_door_parts) or index in (29,30,31,32,33)
        pivots[index]=pivot

    # The real upper red optics serve both tail and brake circuits. Keep the
    # existing low-intensity tail lens and overlay its brake circuit as an
    # independently switchable part, as BusInterior.Apply expects. It starts
    # hidden in Blender but remains in the source object set for export.
    brake_material=next((m for m in _rig_bpy.data.materials if int(m.get('light_id',0))==4),None)
    if brake_material is not None:
        for original in list(master.objects):
            name=original.name.lower()
            if original.type!='MESH' or not original.get('cdlc_tail_brake_shared'):
                continue
            overlay=original.copy()
            overlay.data=original.data.copy()
            overlay.name='CDLC Brake overlay | '+original.name
            master.objects.link(overlay)
            overlay.data.materials.clear()
            overlay.data.materials.append(brake_material)
            overlay.location+=_RigVector(original.get('cdlc_brake_overlay_offset_blender',[0,.0008,0]))
            overlay.animation_data_clear()
            overlay['cdlc_runtime_light']=True
            overlay['cdlc_export_even_when_hidden']=True
            for frame,hidden in ((0,True),(16,False),(24,False),(28,True),(68,True),(76,False),(80,True)):
                overlay.hide_render=hidden
                overlay.hide_viewport=hidden
                overlay.keyframe_insert(data_path='hide_render',frame=frame)
                overlay.keyframe_insert(data_path='hide_viewport',frame=frame)
            overlay.animation_data.action.name='CDLC | brake circuit | '+original.name

    # Restore the true closed geometry before semantic assignment/export.
    scene.frame_start=0;scene.frame_end=80
    scene.render.fps=24
    for name,frame in (('CDLC | closed and stopped',0),('CDLC | stopped at door',20),('CDLC | doors fully open',40),('CDLC | close doors',60),('CDLC | closed',80)):
        marker=scene.timeline_markers.get(name)
        if marker is None:marker=scene.timeline_markers.new(name)
        marker.frame=frame
    scene.frame_set(0)
    _rig_bpy.context.view_layer.update()
    counts=assign_cdlc_semantics(master)
    for obj in master.objects:
        if 'cdlc_part' in obj:
            pivot=pivots[int(obj['cdlc_part'])]
            obj['cdlc_pivot_object']=pivot.name
            if not obj.get('cdlc_is_part_pivot'):
                obj['cdlc_pivot']=list(pivot['cdlc_pivot'])
                obj['cdlc_pivot_unity']=list(pivot['cdlc_pivot_unity'])
                obj['cdlc_rest_rotation']=list(pivot['cdlc_rest_rotation'])
    scene['cdlc_animation_rig_version']=1
    scene['cdlc_animation_instructions']='Frames 0/80 closed, 40 fully open. Road-wheel steering and local-axis rolling; column and two needles animated. Actions are editable per pivot.'
    scene['cdlc_part_contract']='BusInterior fixed indices 0..33; only authored door leaves are active; unused door and wrap slots remain empty.'
    report={'version':1,'frame_start':0,'frame_end':80,'closed_frame':0,'open_frame':40,
            'part_object_counts':counts,'active_door_parts':sorted(active_door_parts),'warnings':warnings,'parts':collect_cdlc_parts(master)}
    scene['cdlc_rig_report']=_rig_json.dumps({k:v for k,v in report.items() if k!='parts'},ensure_ascii=False)
    return report
