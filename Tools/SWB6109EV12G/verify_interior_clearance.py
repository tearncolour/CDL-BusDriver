"""Read-only door/interior intersection checks at explicitly sampled poses.

Checks actual evaluated world geometry, independent of model handedness.
AABB/BVH overlap only selects candidates. Triangle edge/plane intersections
and closed-component inside tests determine penetration or surface contact.
This is not a proof of continuous swept-volume clearance between the frames.
"""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import sys
import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ROOT=Path(__file__).resolve().parents[2]
EPS=1e-6
# A sub-ULP advance at vehicle-scale world coordinates can repeatedly hit the
# same face. Eight micrometres advances all three fixed ray axes reliably while
# staying well below the authored reader/door solid thicknesses.
RAY_STEP=8e-6
DIRECTIONS=tuple(Vector(v).normalized() for v in ((.742,.419,.523),(-.313,.861,.4),(.219,-.445,.869)))


def bounds(points):
    return [[min(p[i] for p in points),max(p[i] for p in points)] for i in range(3)]


def overlap(a,b):
    return all(a[i][0]<=b[i][1]+EPS and b[i][0]<=a[i][1]+EPS for i in range(3))


def bounds_separation(a,b):
    """A conservative lower bound on mesh distance, never a claimed exact gap."""
    gaps=[max(0.,a[i][0]-b[i][1],b[i][0]-a[i][1]) for i in range(3)]
    return math.sqrt(sum(gap*gap for gap in gaps))


def within(point,box):
    return all(box[i][0]-EPS<=point[i]<=box[i][1]+EPS for i in range(3))


def inside_component(point,component):
    """Odd/even rays for a single closed component; None means boundary."""
    if not within(point,component['bounds']):return False
    tree=component['tree'];near=tree.find_nearest(point)
    if near[0] is None:return False
    if near[3]<=EPS:return None
    votes=[]
    limit=2+3*math.sqrt(sum((hi-lo)**2 for lo,hi in component['bounds']))
    for direction in DIRECTIONS:
        origin=point.copy();count=0;travelled=0.
        for _ in range(256):
            hit=tree.ray_cast(origin,direction,limit-travelled)
            if hit[0] is None:
                votes.append(bool(count%2));break
            count+=1;travelled+=hit[3]+RAY_STEP
            origin=hit[0]+direction*RAY_STEP
        else:return None
    return sum(votes)>=2


class Body:
    def __init__(self,obj,deps):
        self.name=obj.name
        data=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
        bm=bmesh.new();bm.from_mesh(data)
        bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=EPS)
        bmesh.ops.triangulate(bm,faces=list(bm.faces))
        bm.verts.index_update();bm.faces.index_update()
        self.vertices=[obj.matrix_world@v.co for v in bm.verts]
        self.faces=[tuple(v.index for v in face.verts) for face in bm.faces]
        self.triangles=[tuple(self.vertices[i] for i in face) for face in self.faces]
        self.bounds=bounds(self.vertices)
        self.tree=BVHTree.FromPolygons(self.vertices,self.faces,all_triangles=True,epsilon=0.)
        self.components=[];seen=set()
        for initial in bm.faces:
            if initial.index in seen:continue
            pending=[initial];members=[];seen.add(initial.index)
            while pending:
                face=pending.pop();members.append(face)
                for edge in face.edges:
                    for neighbour in edge.link_faces:
                        if neighbour.index not in seen:
                            seen.add(neighbour.index);pending.append(neighbour)
            closed=all(len(edge.link_faces)==2 for face in members for edge in face.edges)
            faces=[self.faces[face.index] for face in members]
            points=[self.vertices[i] for face in faces for i in face]
            self.components.append({'closed':closed,'triangles':len(faces),'bounds':bounds(points),
                'tree':BVHTree.FromPolygons(self.vertices,faces,all_triangles=True,epsilon=0.)})
        self.closed=all(c['closed'] for c in self.components)
        bm.free();bpy.data.meshes.remove(data)

    def inside(self,point):
        if not self.closed:return None
        boundary=False
        # Treat disconnected solids as a union, not XOR. This matters for a
        # generated reader whose buttons/UI plates overlap its closed housing.
        for component in self.components:
            result=inside_component(point,component)
            if result is True:return True
            if result is None:boundary=True
        return None if boundary else False


def in_triangle(point,tri,normal):
    return all((b-a).cross(point-a).dot(normal)>=-EPS*(b-a).length
               for a,b in zip(tri,tri[1:]+tri[:1]))


def coplanar_overlap(a,b,normal):
    drop=max(range(3),key=lambda i:abs(normal[i]));axes=[i for i in range(3) if i!=drop]
    aa=[(v[axes[0]],v[axes[1]]) for v in a];bb=[(v[axes[0]],v[axes[1]]) for v in b]
    for tri in (aa,bb):
        for p,q in zip(tri,tri[1:]+tri[:1]):
            axis=(-(q[1]-p[1]),q[0]-p[0]);length=math.hypot(*axis)
            if length<1e-15:continue
            pa=[x*axis[0]+y*axis[1] for x,y in aa];pb=[x*axis[0]+y*axis[1] for x,y in bb]
            if max(pa)<min(pb)-EPS*length or max(pb)<min(pa)-EPS*length:return False
    return True


def triangle_intersection(a,b):
    na=(a[1]-a[0]).cross(a[2]-a[0]);nb=(b[1]-b[0]).cross(b[2]-b[0])
    if na.length_squared<1e-18 or nb.length_squared<1e-18:return [],False,None,None
    na.normalize();nb.normalize()
    da=[(p-b[0]).dot(nb) for p in a];db=[(p-a[0]).dot(na) for p in b]
    if min(da)>EPS or max(da)<-EPS or min(db)>EPS or max(db)<-EPS:return [],False,na,nb
    if na.cross(nb).length_squared<1e-12 and max(abs(x) for x in da)<=EPS:
        return [],coplanar_overlap(a,b,na),na,nb
    points=[]
    for tri,other,normal,distances in ((a,b,nb,da),(b,a,na,db)):
        for i in range(3):
            j=(i+1)%3;denom=distances[i]-distances[j]
            if abs(denom)<1e-15:
                if abs(distances[i])<=EPS and in_triangle(tri[i],other,normal):points.append(tri[i])
                continue
            t=distances[i]/denom
            if -1e-8<=t<=1+1e-8:
                point=tri[i].lerp(tri[j],max(0.,min(1.,t)))
                if in_triangle(point,other,normal) and all((point-p).length>EPS for p in points):points.append(point)
    return points,False,na,nb


def compare(a,b):
    candidates=a.tree.overlap(b.tree)
    contacts=[];intersection_count=0
    for ai,bi in candidates:
        points,coplanar,na,nb=triangle_intersection(a.triangles[ai],b.triangles[bi])
        if not points and not coplanar:continue
        intersection_count+=1
        if len(contacts)<5:
            contacts.append({'door_triangle':ai,'static_triangle':bi,'coplanar':coplanar,
                             'points':[list(p) for p in points]})
        if points and a.closed and b.closed:
            centre=sum(points,Vector())/len(points)
            for scale in (3e-6,3e-5,3e-4):
                for sa,sb in ((1,1),(1,-1),(-1,1),(-1,-1)):
                    candidate=centre+(sa*na+sb*nb)*scale
                    if a.inside(candidate) is True and b.inside(candidate) is True:
                        return {'classification':'penetration','proof':'Point strictly inside both closed-component unions beside intersecting triangles.',
                                'point_source_blender':list(candidate),'triangle_pair':[ai,bi],
                                'bvh_candidates':len(candidates)}
    # Detect complete containment too, including an entire disconnected button
    # or a thin door solid enclosed by another object without surface crossings.
    if a.closed and b.closed:
        for inner,outer,label in ((a,b,'door surface inside static'),(b,a,'static surface inside door')):
            for point in inner.vertices:
                if outer.inside(point) is True:
                    return {'classification':'penetration','proof':label+' closed-component union.',
                            'point_source_blender':list(point),'bvh_candidates':len(candidates)}
    if intersection_count:
        return {'classification':'contact_requires_review' if a.closed and b.closed else 'unresolved_open_geometry',
                'proof':'Actual triangle surface intersections; sampled inside tests did not certify a volume overlap.',
                'intersecting_triangle_pairs':intersection_count,'examples':contacts,'bvh_candidates':len(candidates)}
    if not a.closed or not b.closed:
        return {'classification':'unresolved_open_geometry','proof':'Containment cannot be certified for an open component.',
                'bvh_candidates':len(candidates)}
    return {'classification':'separated','bvh_candidates':len(candidates)}


def static_category(obj):
    part=int(obj.get('cdlc_part',-1));name=obj.name.lower()
    if str(obj.get('cdlc_runtime_generated','')).casefold()=='cardreader' or name.startswith('fare validator'):
        return 'card_reader'
    if obj.get('mirror_assembly'):
        return 'fixed_mirror_assembly'
    return {10:'passenger_fittings',11:'dashboard',12:'driver_cab_and_partitions',13:'handrails_and_straps'}.get(part)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/SWB6109EV12G.blend')
    parser.add_argument('--report',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/interior-clearance-validation.json')
    parser.add_argument('--frames',default='0,25,30,40')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    source=args.blend.resolve();source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(source));scene=bpy.context.scene
    master=bpy.data.collections.get('SWB6109EV12G | editable components')
    if master is None:raise RuntimeError('Missing SWB6109 editable components')
    spec=json.loads((ROOT/'ModelSource/SWB6109EV12G/model-spec.json').read_text(encoding='utf-8'))
    parts={int(row['part']) for row in spec['door_leaves']}
    geometry=[ob for ob in master.objects if ob.type in {'MESH','CURVE','FONT'}]
    moving=[ob for ob in geometry if int(ob.get('cdlc_part',-1)) in parts]
    static=[ob for ob in geometry if int(ob.get('cdlc_part',-1)) not in parts and static_category(ob)]
    errors=[];findings=[];poses=[]
    if parts!={4,5,6,7}:errors.append('Expected four modeled door leaves, parts 4..7')
    if {int(ob.get('cdlc_part',-1)) for ob in moving}!=parts:errors.append('A required door leaf has no geometry')
    if not any(static_category(ob)=='card_reader' for ob in static):errors.append('No actual card-reader geometry was selected')
    for frame in [int(value) for value in args.frames.split(',')]:
        scene.frame_set(frame);bpy.context.view_layer.update();deps=bpy.context.evaluated_depsgraph_get()
        a=[Body(ob,deps) for ob in moving];b=[Body(ob,deps) for ob in static]
        row={'frame':frame,'possible_pairs':len(a)*len(b),'aabb_candidates':0,'bvh_triangle_candidates':0,
             'separated_pairs':0,'findings':0,'minimum_aabb_separation_m':None,
             'card_reader_aabb_separation_lower_bound_m':None}
        reader_names={ob.name for ob in static if static_category(ob)=='card_reader'}
        for door in a:
            for interior in b:
                distance=bounds_separation(door.bounds,interior.bounds)
                if row['minimum_aabb_separation_m'] is None or distance<row['minimum_aabb_separation_m']:
                    row['minimum_aabb_separation_m']=distance
                    row['closest_bounds_pair']=[door.name,interior.name]
                if interior.name in reader_names and (row['card_reader_aabb_separation_lower_bound_m'] is None or distance<row['card_reader_aabb_separation_lower_bound_m']):
                    row['card_reader_aabb_separation_lower_bound_m']=distance
                    row['closest_reader_bounds_pair']=[door.name,interior.name]
                if not overlap(door.bounds,interior.bounds):continue
                row['aabb_candidates']+=1
                result=compare(door,interior);row['bvh_triangle_candidates']+=result['bvh_candidates']
                if result['classification']=='separated':row['separated_pairs']+=1;continue
                result.update(frame=frame,door=door.name,static=interior.name,
                              door_closed_components=door.closed,static_closed_components=interior.closed)
                findings.append(result);row['findings']+=1
        poses.append(row);print('INTERIOR_POSE '+json.dumps(row),flush=True)
    scene.frame_set(0)
    if hashlib.sha256(source.read_bytes()).hexdigest()!=source_hash:errors.append('Source file changed during read-only review')
    status='FAIL' if findings or errors else 'PASS'
    excluded=[ob.name for ob in geometry if ob not in moving and ob not in static and 'door' in ob.name.lower()]
    report={'status':status,'source':str(source),'source_sha256':source_hash,
            'scope':'Four door leaves against selected static interior geometry and both fixed mirror assemblies at explicit sampled frames only; actual world coordinates.',
            'method':'AABB broad phase; BVH triangle candidates; explicit triangle edge/plane and coplanar SAT intersections; '
                     'inside tests with three ray directions on each welded closed connected component, treating disconnected solids as a union; containment checks in both directions.',
            'contact_tolerance_m':EPS,'ray_advance_m':RAY_STEP,
            'execution_summary':'All selected pairs had disjoint evaluated world bounds; triangle/containment narrow phase was unnecessary.' if not any(row['aabb_candidates'] for row in poses) else 'Overlapping world bounds were checked by the reported triangle/containment narrow phase.',
            'distance_note':'Positive AABB distances are conservative mesh-clearance lower bounds at that sampled pose, not exact minimum mesh distances.',
            'limitations':['Not a continuous swept-volume proof between sampled frames.',
                           'Does not test arbitrary static-versus-static collisions or certify minimum clearance distances.',
                           'Door/frame, seal, threshold and actuator assembly interfaces are outside this targeted interior-object test; selected reader/fittings have no contact exemptions.'],
            'static_categories':dict(Counter(static_category(ob) for ob in static)),
            'selected_static_objects':[{'object':ob.name,'category':static_category(ob),
                'runtime_generated':ob.get('cdlc_runtime_generated')} for ob in static],
            'selected_door_objects':[ob.name for ob in moving],
            'door_related_objects_outside_scope':excluded,'poses':poses,'findings':findings,'errors':errors}
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('SWB6109_INTERIOR_CLEARANCE_'+status+' '+str(args.report),flush=True)
    if status!='PASS':raise RuntimeError(f'{len(findings)} door/interior findings; '+ '; '.join(errors))


if __name__=='__main__':main()
