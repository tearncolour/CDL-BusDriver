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
from mathutils import Matrix as _RigMatrix, Quaternion as _RigQuaternion, Vector as _RigVector


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


def cdlc_part_name(part):
    """The contract names, then the door drives and the wrap surfaces (door_linkage.py: parts 34.., before the straps)."""
    if part<len(CDLC_PART_NAMES):return CDLC_PART_NAMES[part]
    drives=len(globals().get('DOOR_LEAVES',()))
    return 'DoorDrive'+str(part-len(CDLC_PART_NAMES)) if part-len(CDLC_PART_NAMES)<drives else 'WrapSurface'+str(part-len(CDLC_PART_NAMES)-drives)


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
    _rig_interpolation(action,linear)


def _rig_key_transform(obj, samples, action_name):
    """Location and the turn about +Z, keyed at every listed frame (frame, (x, y, z), radians), linear between them."""
    obj.rotation_mode='XYZ'
    obj.animation_data_clear()
    for frame, location, angle in samples:
        obj.location=location
        obj.rotation_euler[2]=angle
        obj.keyframe_insert(data_path='location',frame=frame,group=action_name)
        obj.keyframe_insert(data_path='rotation_euler',index=2,frame=frame,group=action_name)
    action=obj.animation_data.action
    action.name=action_name
    _rig_interpolation(action,True)


def _rig_interpolation(action, linear):
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
    totals.update({str(int(o['cdlc_part'])):0 for o in master.objects if o.get('cdlc_is_part_pivot')})
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
        totals[str(part)]+=1
    return totals


def collect_cdlc_parts(master):
    """Stable 34-row export contract, including empty optional door/wrap parts.

    posed_part_to_blender is sampled at the CURRENT frame. For a geometry
    vertex world position p, local rest geometry is inverse(posed_frame) @ p.
    The serialized rest pivot/rotation must come from rest_matrix_blender or
    pivot_unity/rest_rotation_unity, never the current animated matrix.
    """
    pivots={int(o['cdlc_part']):o for o in master.objects if o.get('cdlc_is_part_pivot')}
    parts=[]
    drives=sorted(i for i in pivots if i>=len(CDLC_PART_NAMES))
    if drives!=list(range(len(CDLC_PART_NAMES),len(CDLC_PART_NAMES)+len(drives))):
        raise RuntimeError('door drive and wrap surface parts must follow the contract parts without gaps: '+str(drives))
    n_drive=len(globals().get('DOOR_LEAVES',()))
    for index,name in [(i,cdlc_part_name(i)) for i in list(range(len(CDLC_PART_NAMES)))+drives]:
        pivot=pivots.get(index)
        rest=_rig_matrix(pivot['cdlc_rest_matrix_blender']) if pivot else _RigMatrix.Identity(4)
        # Unity body coordinates preserve object translation; imported cab's
        # intentional +.26 Blender-Y offset becomes -.26 Unity-Z.
        unity=_RIG_COORD.inverted() @ rest
        q=unity.to_quaternion()
        surface=bool(pivot and pivot.get('cdlc_wrap_surface'))
        parts.append({'index':index,'name':name,'parent':-1 if index==0 else (int(pivot['cdlc_wrap_parent']) if surface else 0),
                      'door_drive':index>=len(CDLC_PART_NAMES) and not surface,'wrap_surface':surface,
                      'pivot_object':pivot.name if pivot else None,
                      'pivot_blender':list(rest.translation),
                      'pivot_unity':list(unity.translation),
                      'rest_rotation_unity':[q.x,q.y,q.z,q.w],
                      'rest_matrix_blender':_rig_flat(rest),
                      'posed_part_to_blender':_rig_flat(pivot.matrix_world @ _rig_matrix(pivot['cdlc_export_frame_basis']) if pivot else rest),
                      'objects':[o.name for o in master.objects if o.type in {'MESH','CURVE','FONT'} and int(o.get('cdlc_part',-1))==index]})
    return parts


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

    # Outward-swing doors (door_linkage.py): each leaf rides on its joint pivot, keyed every frame from the linkage solver (the
    # joint's place and the leaf's turn about it), and its door shaft, a drive part after the 34 contract parts, turns about
    # its own axis. Timeline: both doors open over frames 0-24 and stay open; the close command at 48 closes the rear door
    # over 48-72 and the front pair, 1.2 s later, over 77-101 (door_linkage.DOOR_TIMING, as vehicle.json doors[]).
    door_samples={}
    for L in DOOR_LEAVES:
        joint=master.objects.get(L['name']+' joint')
        shaft=master.objects.get(L['name']+' shaft')
        if joint is None or shaft is None:
            warnings.append('Missing pivots of '+L['name']);continue
        _rig_record_pivot(joint,L['part'],joint.matrix_world.copy() @ _RIG_COORD,_RIG_COORD)
        _rig_record_pivot(shaft,L['drive_part'],shaft.matrix_world.copy() @ _RIG_COORD,_RIG_COORD)
        pivots[L['part']]=joint;pivots[L['drive_part']]=shaft
        leaf_keys=[];shaft_keys=[];z=joint.location.z
        for frame in range(DOOR_TIMELINE_END+1):
            sol=door_solve(L,door_opening(L['door'],frame))
            leaf_keys.append((frame,(sol['joint'][0],sol['joint'][1],z),sol['yaw']))
            shaft_keys.append((frame,_rig_math.degrees(sol['theta'])))
        door_samples[L['name']]=[{'frame':f,'opening':round(door_opening(L['door'],f),6)} for f in range(0,DOOR_TIMELINE_END+1,4)]
        _rig_key_transform(joint,leaf_keys,'CDLC | '+CDLC_PART_NAMES[L['part']]+' | outward swing')
        _rig_key_rotation(shaft,2,shaft_keys,'CDLC | '+cdlc_part_name(L['drive_part'])+' | door shaft',linear=True)
        joint['cdlc_motion']='Outward-swing leaf: joint place and turn about +Z from door_linkage.door_solve, keyed each frame; frame 0 closed.'
        shaft['cdlc_motion']='Door shaft and arms: turn about the shaft axis (+Z), keyed each frame.'
        for child in _rig_descendants(joint):_rig_stamp(child,L['part'])
        for child in _rig_descendants(shaft):_rig_stamp(child,L['drive_part'])
        # the leaf's wrap surface: its own part, a child of the leaf part (it moves with it), recorded after the leaf's
        # descendants were stamped with the leaf's part
        wrap_pivot=L['wrap_pivot']
        _rig_record_pivot(wrap_pivot,L['surface_part'],wrap_pivot.matrix_world.copy() @ _RIG_COORD,_RIG_COORD)
        wrap_pivot['cdlc_wrap_parent']=L['part'];wrap_pivot['cdlc_wrap_surface']=True
        pivots[L['surface_part']]=wrap_pivot
    # the windows' wrap surface: one part under the shell
    wp=WRAP_WINDOW_PIVOT;wpart=wrap_window_part(DOOR_LEAVES)
    _rig_record_pivot(wp,wpart,wp.matrix_world.copy() @ _RIG_COORD,_RIG_COORD)
    wp['cdlc_wrap_parent']=0;wp['cdlc_wrap_surface']=True
    pivots[wpart]=wp

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
    kit_path=root/'ModelSource'/'SWB6710EV01G'/'kit-cabin.json'
    kit_parts={}
    if kit_path.exists():
        kit=_rig_json.loads(kit_path.read_text(encoding='utf-8'))
        kit_parts={p['name']:p for p in kit['lods'][0]['parts']}
    else:warnings.append('MeshKit export unavailable; instrument pivots use geometric centres.')
    shift=_RigMatrix.Translation((0,.26,0))
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
        pivot['cdlc_optional_empty']=index in (7,8,9,29,30,31,32,33)
        pivots[index]=pivot

    # The real upper red optics serve both tail and brake circuits. Keep the
    # existing low-intensity tail lens and overlay its brake circuit as an
    # independently switchable part, as BusInterior.Apply expects. It starts
    # hidden in Blender but remains in the source object set for export.
    brake_material=next((m for m in _rig_bpy.data.materials if int(m.get('light_id',0))==4),None)
    if brake_material is not None:
        for original in list(master.objects):
            name=original.name.lower()
            if original.type!='MESH' or not name.startswith('rear lamp ') or '.0 coloured circular lens' not in name:
                continue
            overlay=original.copy()
            overlay.data=original.data.copy()
            overlay.name='CDLC Brake overlay | '+original.name
            master.objects.link(overlay)
            overlay.data.materials.clear()
            overlay.data.materials.append(brake_material)
            overlay.location.y+=.0008
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
    scene.frame_start=0;scene.frame_end=DOOR_TIMELINE_END
    scene.render.fps=DOOR_FPS
    for marker in list(scene.timeline_markers):scene.timeline_markers.remove(marker)
    closed_at=lambda door:DOOR_CLOSE_FRAME+round((DOOR_TIMING[door][2]+DOOR_TIMING[door][1])*DOOR_FPS)
    for name,frame in (('CDLC | closed and stopped',0),('CDLC | doors fully open',DOOR_OPEN_FRAMES[1]),('CDLC | close doors',DOOR_CLOSE_FRAME),
                       ('CDLC | rear door closed',closed_at(1)),('CDLC | front doors closed',closed_at(0))):
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
    scene['cdlc_animation_instructions']=('Frame 0 closed, 24-48 fully open; the rear door closes 48-72, the front pair 77-101 (outward swing, '
        'door_linkage.py). Road-wheel steering and local-axis rolling; column and two needles animated. Actions are editable per pivot; '
        'the door keys are sampled from the linkage: change door_linkage.py and rebuild rather than the keys.')
    scene['cdlc_part_contract']='BusInterior fixed indices 0..33, then one DoorDrive part per door leaf; optional second rear leaf, third door and wrap faces retained as empty pivots.'
    report={'version':2,'frame_start':0,'frame_end':DOOR_TIMELINE_END,'closed_frame':0,'open_frame':DOOR_OPEN_FRAMES[1],
            'close_command_frame':DOOR_CLOSE_FRAME,'fps':DOOR_FPS,'door_openings':door_samples,
            'part_object_counts':counts,'warnings':warnings,'parts':collect_cdlc_parts(master)}
    scene['cdlc_rig_report']=_rig_json.dumps({k:v for k,v in report.items() if k!='parts'},ensure_ascii=False)
    return report
