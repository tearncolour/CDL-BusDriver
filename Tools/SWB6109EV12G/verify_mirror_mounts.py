"""Read-only SWB6109 mirror shoe contact with fixed coachwork (not glass/doors).

Blender --background --python-exit-code 1 --python this.py -- --blend source.blend --report result.json
Only this vehicle's named fixed shell contract is accepted. This is not a photo
placement, mirror-camera, main-arm or auxiliary-stay joint certification.
"""
import argparse, hashlib, json, sys
from pathlib import Path
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
sys.path.insert(0,str(Path(__file__).resolve().parent))
from source_coordinates import canonical_world_matrix

TOLERANCE=.0002
def arr(v):return [float(x) for x in v]
def tree(obj):
    deps=bpy.context.evaluated_depsgraph_get()
    data=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
    world=canonical_world_matrix(obj)
    points=[world@v.co for v in data.vertices];data.calc_loop_triangles()
    result=BVHTree.FromPolygons(points,[tuple(t.vertices) for t in data.loop_triangles],all_triangles=True)
    bpy.data.meshes.remove(data)
    return result, points

def allowed(name,side):
    return name in {'Front continuous coachwork','Front quarter closure '+str(side),
                    'Roof rounded side shoulder '+str(side)} or (
        side<0 and name=='Side black window frame -1 0') or (
        side>0 and name=='Door upper black header 0')

def intersections(bvh,side,y,z):
    origin=Vector((side*1.6,y,z));direction=Vector((-side,0,0));travel=0.;hits=[]
    for _ in range(16):
        hit,normal,face,d=bvh.ray_cast(origin,direction,.65-travel)
        if hit is None:break
        hits.append({'point':arr(hit),'normal':arr(normal),'triangle':face,'travel_m':travel+d})
        travel+=d+.000002
        if travel>=.65:break
        origin=hit+direction*.000002
    return hits

def contact(side,point,hosts,outward_point=None):
    # Outward-to-inward rays traverse an isolated, named fixed shell. Pair
    # crossings to retain actual solid intervals, rather than nearest trim.
    records=[];separation=None;support=[];nearest=None
    for name,bvh in hosts:
        close,normal,face,distance=bvh.find_nearest(point)
        if close is not None and (nearest is None or distance<nearest['distance_m']):
            nearest={'object':name,'point_canonical_m':arr(close),'distance_m':distance,'triangle':face}
        hits=intersections(bvh,side,point.y,point.z)
        intervals=[]
        for a,b in zip(hits[::2],hits[1::2]):
            lo=min(abs(a['point'][0]),abs(b['point'][0]));hi=max(abs(a['point'][0]),abs(b['point'][0]))
            x=abs(point.x);gap=max(lo-x,x-hi,0.)
            external=None if outward_point is None else abs(outward_point.x)-hi
            intervals.append({'x_abs_min_m':lo,'x_abs_max_m':hi,'shoe_gap_m':gap,
                              'shoe_inward_face_inside_body':lo-TOLERANCE<=x<=hi+TOLERANCE,
                              'inset_from_outer_surface_m':hi-x,'shoe_thickness_outside_body_m':external})
            separation=gap if separation is None else min(separation,gap)
            if gap<=TOLERANCE and (external is None or external>=.002-TOLERANCE):support.append(name)
        if hits:records.append({'object':name,'hits':hits,'solid_intervals':intervals})
    return {'supported':bool(support),'supporting_objects':support,
            'nearest_inward_face_to_fixed_solid_m':separation,'nearest_fixed_surface':nearest,'fixed_body_sections':records}

def verify_master(master, include_scan=False):
    rows=[];scans=[];objects=list(master.all_objects)
    for side,assembly in [(-1,'driver side small'),(1,'door side large')]:
        name='Mirror '+assembly+' A-pillar mounting shoe'
        shoe=next(o for o in objects if o.name==name)
        st,points=tree(shoe)
        host_objects=[o for o in objects if allowed(o.name,side)]
        hosts=[(o.name,tree(o)[0]) for o in host_objects]
        assert hosts,'Fixed shell missing'
        assert all(not any(m and m.get('glass') for m in bpy.data.objects[n].data.materials) for n,_ in hosts)
        ymin,ymax=min(v.y for v in points),max(v.y for v in points)
        zmin,zmax=min(v.z for v in points),max(v.z for v in points)
        samples=[]
        # Interior footprint samples avoid exact bevel/edge tangent ambiguity,
        # but include 81% of each span. The shoe surface itself is ray-sampled.
        for iy in range(9):
            y=(ymin+ymax)/2+(ymax-ymin)*(-.40+.10*iy)
            for iz in range(11):
                z=(zmin+zmax)/2+(zmax-zmin)*(-.40+.08*iz)
                hit,normal,face,d=st.ray_cast(Vector((0,y,z)),Vector((side,0,0)),1.6)
                assert hit is not None,'Missing actual shoe inward face'
                outer,_,_,_=st.ray_cast(Vector((side*1.6,y,z)),Vector((-side,0,0)),1.6)
                assert outer is not None
                c=contact(side,hit,hosts,outer)
                samples.append({'shoe_point_canonical_m':arr(hit),'shoe_triangle':face,**c})
        bad=sum(not s['supported'] for s in samples)
        rows.append({'side':'driver' if side<0 else 'door','object':name,
                     'bounds_canonical_m':{'min':[min(v[i] for v in points) for i in range(3)],'max':[max(v[i] for v in points) for i in range(3)]},
                     'host_objects':[n for n,_ in hosts],'samples':samples,'failed':bad,
                     'status':'PASS' if not bad else 'FAIL'})
        if include_scan:
            for y in [-5.08,-5.06,-5.04,-5.02,-5.01,-5.,-4.98,-4.96,-4.95,-4.94,-4.90,-4.83,-4.76,-4.69]:
                for z in [2.37,2.40,2.46,2.48,2.52,2.55,2.66,2.68,2.72,2.75,2.80,2.83,2.85]:
                    scans.append({'side':side,'y':y,'z':z,**contact(side,Vector((side*1.273,y,z)),hosts)})
    checks=sum(len(r['samples']) for r in rows);failed=sum(r['failed'] for r in rows)
    result={'status':'PASS' if not failed else 'FAIL','checks':checks,'failed':failed,
            'scope':'SWB6109EV12G fixed coachwork contact at actual inward shoe surface only; glass, dynamic doors, mirrors, rubber trims and incidental fittings cannot support a shoe.',
            'coordinate_space':'Private construction frame: door +X, driver -X, front -Y, up +Z; final Blender source X is the opposite.',
            'tolerance_m':TOLERANCE,'minimum_supported_fraction':1.,'minimum_external_shoe_thickness_m':.002,'rows':rows,
            'not_tested':['Photo-correct placement','Arm-to-hinge or auxiliary-stay joints','Dynamic mirror reflection','Other vehicle models']}
    if include_scan:result['candidate_fixed_body_scan_at_abs_x_1_273_m']=scans
    return result

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--blend',required=True,type=Path);p.add_argument('--report',required=True,type=Path)
    p.add_argument('--scan',action='store_true');a=p.parse_args(sys.argv[sys.argv.index('--')+1:])
    before=hashlib.sha256(a.blend.read_bytes()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()));bpy.context.scene.frame_set(0)
    result=verify_master(bpy.data.collections['SWB6109EV12G | editable components'],a.scan)
    result.update(source=str(a.blend.resolve()),source_sha256=before,
                  verifier_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    assert hashlib.sha256(a.blend.read_bytes()).hexdigest()==before
    a.report.parent.mkdir(parents=True,exist_ok=True)
    a.report.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print('MIRROR_MOUNTS',result['status'],result['checks'],result['failed'])
    if result['failed']:raise SystemExit(1)
if __name__=='__main__':main()
