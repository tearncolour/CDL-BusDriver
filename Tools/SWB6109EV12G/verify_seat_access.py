"""Read-only seating/foot-support review of the final saved source.

Run with Blender --background --python-exit-code 1 --python this_file.py.
The explicit 27-seat inventory is this design revision, not a factory count.
"""
import argparse,hashlib,json,sys
from pathlib import Path
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(Path(__file__).resolve().parent))
from seat_access import RETAINED_SEAT_NUMBERS,REMOVED_SEAT_NUMBERS,CUSHION_OFFSET
from source_coordinates import canonical_world_matrix


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/SWB6109EV12G.blend')
    parser.add_argument('--report',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/seat-access-validation.json')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    source=args.blend.resolve();digest=hashlib.sha256(source.read_bytes()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(source));bpy.context.scene.frame_set(0)
    master=bpy.data.collections['SWB6109EV12G | editable components']
    objects={o.name:o for o in master.objects};deps=bpy.context.evaluated_depsgraph_get();errors=[]
    def geometry(obj):
        mesh=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
        m=canonical_world_matrix(obj);vertices=[m@v.co for v in mesh.vertices]
        faces=[tuple(p.vertices) for p in mesh.polygons];bpy.data.meshes.remove(mesh)
        return vertices,faces
    def combined(selection):
        vertices=[];faces=[];owners=[]
        for obj in selection:
            points,polys=geometry(obj);offset=len(vertices);vertices.extend(points)
            faces.extend(tuple(offset+i for i in face) for face in polys);owners.extend([obj.name]*len(polys))
        return BVHTree.FromPolygons(vertices,faces),owners
    supports=[o for o in objects.values() if o.type=='MESH' and
              (o.name.startswith('Floor ') or 'wheelhouse' in o.name.lower() or o.name=='Rear bench equipment pedestal')]
    floor,owners=combined(supports)
    obstacles=[o for o in objects.values() if o.type in ('MESH','CURVE') and
               (o.name.endswith(' backrest') or 'wheelhouse' in o.name.lower() or 'partition' in o.name.lower())]
    obstacle_trees=[]
    for obj in obstacles:
        points,polys=geometry(obj)
        obstacle_trees.append((obj.name,BVHTree.FromPolygons(points,polys)))
    ceilings=[o for o in objects.values() if o.type in ('MESH','CURVE') and
              ('ceiling' in o.name.lower() or 'winduct' in o.name.lower() or 'air duct' in o.name.lower()
               or int(o.get('cdlc_part',-1)) in (0,16,18))]
    ceiling,_=combined(ceilings)
    rows=[];legs=[]
    for number in REMOVED_SEAT_NUMBERS:
        if any(f'Passenger seat {number:02d} ' in name for name in objects):errors.append('Removed seat remains: '+str(number))
    for number in RETAINED_SEAT_NUMBERS:
        prefix=f'Passenger seat {number:02d} '
        if prefix+'cushion' not in objects:errors.append('Missing seat '+str(number));continue
        cushion=objects[prefix+'cushion'];p=canonical_world_matrix(cushion).translation
        level=.34 if number<=6 else 1.0795;nominal=level+CUSHION_OFFSET
        if abs(p.z-(level+.355+.0735))>.0001:errors.append('Cushion/marker alignment '+str(number))
        shoes=[]
        # These are the actual common BusRiders.Seated sole dimensions/pose,
        # not a smaller invented shoe chosen to make the layout pass.
        for side in (-1,1):
            for ix in range(5):
                for iy in range(9):
                    origin=Vector((p.x+side*.12-.052+.104*ix/4,p.y-.621+.262*iy/8,nominal+.1))
                    hit,n,index,d=floor.ray_cast(origin,Vector((0,0,-1)),5)
                    gap=None if hit is None else nominal-.481-hit.z
                    shoes.append({'gap_m':gap,'support':None if hit is None else owners[index],'toe_edge':iy==0})
                    tolerance=.006 if iy==0 else .001
                    if gap is None or gap<-.001 or gap>tolerance:errors.append(f'Shoe support seat {number}: {gap}')
        knee_clearance=float('inf');knee_owner=None
        for side in (-1,1):
            knee=Vector((p.x+side*.11,p.y-.37,nominal+.10))
            for name,tree in obstacle_trees:
                if name.startswith(prefix):continue
                hit,n,index,d=tree.find_nearest(knee)
                if hit is not None and d<knee_clearance:knee_clearance=d;knee_owner=name
        if knee_clearance<.062-.001:errors.append(f'Knee intersects {number}: {knee_owner}, {knee_clearance}')
        head_clearance=[]
        for dx in (-.10,0,.10):
            point=Vector((p.x+dx,p.y+.09,nominal+.90))
            hit,n,index,d=ceiling.ray_cast(point,Vector((0,0,1)),2)
            if hit is not None:head_clearance.append(d)
        if not head_clearance or min(head_clearance)<.05:errors.append('Head clearance '+str(number))
        rows.append({'source_seat_number':number,'cushion_unity':[p.x,nominal,-p.y],
                     'shoe_samples':len(shoes),'minimum_sole_gap_m':min(s['gap_m'] for s in shoes if s['gap_m'] is not None),
                     'maximum_sole_gap_m':max(s['gap_m'] for s in shoes if s['gap_m'] is not None),
                     'minimum_knee_surface_distance_m':knee_clearance,'nearest_knee_object':knee_owner,
                     'minimum_head_clearance_m':min(head_clearance) if head_clearance else None})
        fixings=[o for name,o in objects.items() if name.startswith(prefix+'floor fixing')]
        if len(fixings)!=2:errors.append('Seat fixing count '+str(number))
        for obj in fixings:
            points,_=geometry(obj);low=min(v.z for v in points);bottom=[v for v in points if v.z<low+1.e-6]
            gaps=[];contacts=set()
            for point in bottom:
                hit,n,index,d=floor.ray_cast(point+Vector((0,0,.02)),Vector((0,0,-1)),1.)
                if hit is None:errors.append('Unsupported fixing '+obj.name);continue
                gaps.append(point.z-hit.z);contacts.add(owners[index])
            if not gaps or max(abs(g) for g in gaps)>.001:errors.append('Fixing not seated '+obj.name)
            legs.append({'object':obj.name,'minimum_gap_m':min(gaps) if gaps else None,
                         'maximum_gap_m':max(gaps) if gaps else None,'support_objects':sorted(contacts)})
    if hashlib.sha256(source.read_bytes()).hexdigest()!=digest:errors.append('Source changed during verification')
    report={'status':'FAIL' if errors else 'PASS','source':str(source),'source_sha256':digest,
            'scope':'All 27 explicit design seats: common-rider soles, 54 physical leg ends, knee spheres and conservative head-top clearance. Door motion is checked separately.',
            'layout_status':'Design usability correction; not a measured factory seat plan',
            'removed_original_seat_numbers':list(REMOVED_SEAT_NUMBERS),'seats':rows,'fixings':legs,
            'thresholds':{'shoe_flat_contact_m':.001,'toe_roundover_clearance_m':.006,'fixing_contact_m':.001,'knee_radius_m':.062,'head_clearance_m':.05},
            'limitations':['Knee spheres/head envelopes are static conservative probes, not a full biomechanical entry animation.','Door 0/25/30/40 and structural closure are independent source validators.'],
            'errors':errors}
    args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(report,indent=2)+'\n')
    print('SEAT_ACCESS_'+report['status'],len(rows),'seats',len(legs),'fixings',len(errors),'errors',flush=True)
    if errors:raise RuntimeError(str(errors[:12]))


if __name__=='__main__':main()
