"""Explicit 27-seat usability correction; caller owns loading and saving.

Seat numbers 07..10 belonged to the earlier invented layout. Their foot space
overlapped the middle-door/stair transition. They are deliberately absent, not
silently tolerated missing geometry. Other seat stations/pitches stay fixed.
"""
import json
from pathlib import Path
import bpy
from mathutils import Matrix,Vector
from source_coordinates import canonical_world_matrix,source_is_corrected

REMOVED_SEAT_NUMBERS = (7,8,9,10)
RETAINED_SEAT_NUMBERS = tuple(n for n in range(1,32) if n not in REMOVED_SEAT_NUMBERS)
CUSHION_OFFSET = .481
LEGACY_CUSHION_OFFSET = .4075
SEAT_RISE = CUSHION_OFFSET-LEGACY_CUSHION_OFFSET


def _objects(collection):
    result=list(collection.objects)
    for child in collection.children:result.extend(_objects(child))
    return result


def _flat(matrix):return [float(matrix[r][c]) for c in range(4) for r in range(4)]


def _translate_world(obj, delta):
    """Translate basis channels, retaining reflected-parent scale/rotation."""
    parent=(obj.parent.matrix_world @ obj.matrix_parent_inverse) if obj.parent else Matrix.Identity(4)
    obj.location += parent.to_3x3().inverted() @ Vector(delta)


def apply_saved_seat_access(master):
    objects=_objects(master)
    removed=[]
    for obj in objects:
        if any(f'Passenger seat {n:02d} ' in obj.name for n in REMOVED_SEAT_NUMBERS):
            removed.append(obj.name)
    for obj in reversed(objects):
        if obj.name in removed:bpy.data.objects.remove(obj,do_unlink=True)
    objects={o.name:o for o in _objects(master)}
    pedestal=objects.get('Rear bench equipment pedestal')
    if pedestal is None:raise RuntimeError('Rear bench support pedestal is missing')
    pedestal_top=max((canonical_world_matrix(pedestal)@v.co).z for v in pedestal.data.vertices)
    moved=[];legs=[];witnesses=[];before={}
    for number in RETAINED_SEAT_NUMBERS:
        prefix=f'Passenger seat {number:02d} '
        cushion=objects.get(prefix+'cushion')
        if cushion is None:raise RuntimeError('Missing retained passenger seat: '+prefix)
        floor=.34 if number<=6 else 1.0795
        target_center=floor+.355+SEAT_RISE
        cushion_position=canonical_world_matrix(cushion).translation
        delta=target_center-cushion_position.z
        if abs(delta)<1.e-6:delta=0.
        target_y=(-1.60,-.85,-.10)[(number-1)//2] if number<=6 else cushion_position.y
        delta_y=target_y-cushion_position.y
        if abs(delta_y)<1.e-6:delta_y=0.
        for name,obj in objects.items():
            if not name.startswith(prefix):continue
            old_witness=list(obj.get('cdlc_source_canonical_matrix_at_conversion',[]))
            old_world=_flat(obj.matrix_world)
            before[name]=(old_witness,old_world,tuple(obj.rotation_euler),tuple(obj.scale))
            if 'floor fixing' in name:
                # The lower bolt stays on its original floor; lengthen the
                # physical upright to the raised cross-member, not into floor.
                if obj.type!='MESH':raise RuntimeError('Expected mesh seat leg')
                axis=obj.matrix_world.to_3x3().col[2].normalized()
                if abs(axis.z)<.99999:raise RuntimeError('Seat foot is not vertical: '+name)
                z0=min(v.co.z for v in obj.data.vertices);z1=max(v.co.z for v in obj.data.vertices)
                # Rear bench fixings lie behind the walkable deck's rear edge;
                # their actual support is the equipment pedestal, not the
                # nominal floor used by the passenger's feet in front.
                lower=pedestal_top if number>=27 else floor
                upper=floor+.25+SEAT_RISE
                if abs((z1-z0)-(upper-lower))>1.e-6:
                    for vertex in obj.data.vertices:
                        vertex.co.z=(vertex.co.z-(z0+z1)/2)*(upper-lower)/(z1-z0)
                move=(lower+upper)/2-obj.matrix_world.translation.z
                if abs(move)>1.e-6 or delta_y:_translate_world(obj,(0.,delta_y,move))
                obj.data.update();legs.append(name)
                obj['seat_leg_support_height_m']=lower
            elif delta or delta_y:_translate_world(obj,(0.,delta_y,delta))
            obj['seat_access_revision']=1
            obj['seat_floor_height_m']=floor
            obj['seat_cushion_height_above_floor_m']=CUSHION_OFFSET
            moved.append(name)
    bpy.context.view_layer.update()
    for name,(old_witness,old_world,old_rotation,old_scale) in before.items():
        obj=objects[name]
        if tuple(obj.rotation_euler)!=old_rotation or tuple(obj.scale)!=old_scale:
            raise RuntimeError('Translation changed rotation/scale: '+name)
        if source_is_corrected(obj):
            new_witness=_flat(canonical_world_matrix(obj))
            obj['cdlc_source_canonical_matrix_at_conversion']=new_witness
            if old_world!=_flat(obj.matrix_world):
                witnesses.append({'object':name,'old_world':old_world,'old_witness':old_witness,'new_witness':new_witness})
    return {'status':'PASS','scope':'Usability correction, not a factory seat-count claim',
            'removed_seat_numbers':list(REMOVED_SEAT_NUMBERS),'removed_objects':removed,
            'retained_seat_numbers':list(RETAINED_SEAT_NUMBERS),'passenger_seats':27,
            'seat_cushion_height_above_floor_m':CUSHION_OFFSET,'vertical_rise_m':SEAT_RISE,
            'low_row_rearward_shift_m':.20,
            'moved_objects':moved,'lengthened_legs':legs,'matrix_witness_updates':witnesses,
            'floor_objects_changed':[],
            'first_retained_high_row_canonical_y_m':2.10,
            'first_high_row_shoe_y_range_m':[2.10-.621,2.10-.359],
            'rationale':'Remove unsupported first four high seats; preserve the existing stair/high deck and other row pitches. Raise retained passenger seats to match the actual common seated figure sole height.'}


def update_seat_access_metadata(root):
    source=Path(root)/'ModelSource/SWB6109EV12G'
    spec_path=source/'model-spec.json';spec=json.loads(spec_path.read_text(encoding='utf-8'))
    spec['passenger_capacity_evidence']='Announcement: 80 total / 20–38 seats; this usability-corrected design has 27 passenger seats, not an asserted factory count.'
    spec['passenger_seat_count']=27
    spec['seat_access']={'revision':1,'removed_original_seat_numbers':list(REMOVED_SEAT_NUMBERS),
                         'retained_original_seat_numbers':list(RETAINED_SEAT_NUMBERS),
                         'cushion_height_above_floor_m':CUSHION_OFFSET,
                         'low_row_canonical_y_m':[-1.60,-.85,-.10],
                         'status':'Design usability correction; coordinates and count are not measured from the obstructed video view.'}
    spec_path.write_text(json.dumps(spec,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    path=source/'cabin-layout.json';layout=json.loads(path.read_text(encoding='utf-8'))
    seats=[]
    for row in layout['passenger_seats']:
        number=row.get('source_seat_number',row['index']+1)
        if number in REMOVED_SEAT_NUMBERS:continue
        row['source_seat_number']=number;row['index']=len(seats)
        row['cushion_unity'][1]=row['floor_height_m']+CUSHION_OFFSET
        if number<=6:row['cushion_unity'][2]=-(-1.60,-.85,-.10)[(number-1)//2]
        if 'cushion_source_blender' in row:
            row['cushion_source_blender'][2]=row['cushion_unity'][1]
            row['cushion_source_blender'][1]=-row['cushion_unity'][2]
        seats.append(row)
    if len(seats)!=27:raise RuntimeError('Metadata did not retain exactly the explicit 27 seat inventory')
    layout['passenger_seats']=seats;layout['passenger_seat_count']=len(seats)
    layout['layout_status']='27-seat usability-corrected design within announced range; not a measured factory seat plan.'
    layout['removed_original_seat_numbers']=list(REMOVED_SEAT_NUMBERS)
    layout['seat_cushion_height_above_floor_m']=CUSHION_OFFSET
    layout['standing_pose']='Standing (hands down); no unverified reach to an overhead grip is asserted.'
    path.write_text(json.dumps(layout,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return {'model_spec':str(spec_path),'cabin_layout':str(path),'passenger_seats':27}
