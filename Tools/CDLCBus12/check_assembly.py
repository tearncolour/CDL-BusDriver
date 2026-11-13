"""Actual saved-mesh mirror support, sight-line, normal and steering-envelope measurements."""
import argparse,collections,hashlib,json,math,sys
from pathlib import Path
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

def B(p):return Vector((-p[0],-p[2],p[1]))
def barrel(p,s):
    p=np.array(p,dtype=float);belt=s['FloorY']+1.02;t=np.clip((belt-p[1])/(belt-s['FloorY']+.12) if p[1]<belt else (p[1]-belt)/(s['RoofY']-belt),0,1)
    inset=(.045 if p[1]<belt else .115)*t*t*(3-2*t);t=np.clip((abs(p[0])-.70)/(s['HalfWidth']-.70),0,1);n=np.clip((s['FrontZ']+.10-p[2])/.10,0,1)
    p[0]-=np.sign(p[0])*inset*t*t*(3-2*t)*n*n*(3-2*n);return p

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--blend',type=Path,required=True);ap.add_argument('--data',type=Path,required=True);ap.add_argument('--out',type=Path,required=True)
    a=ap.parse_args(sys.argv[sys.argv.index('--')+1:]);a.out.mkdir(parents=True,exist_ok=False)
    bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()));scene=bpy.context.scene;scene.frame_set(0)
    s=json.loads((a.data/'spec.json').read_text());layout=json.loads((a.data/'layout.json').read_text());frames=json.loads((a.data/'frames.json').read_text())
    failures=[];results=[]
    for lod in range(3):
        selected=[o for o in bpy.data.collections['LOD%d'%lod].all_objects if o.type=='MESH' and o.get('bake')!='only']
        verts=[];faces=[];owners=[];tris=[];parts=[];normal_errors=[]
        for ob in selected:
            me=ob.data;me.calc_loop_triangles();offset=len(verts);vs=[ob.matrix_world@v.co for v in me.vertices];verts.extend(vs)
            for t in me.loop_triangles:
                face=tuple(offset+i for i in t.vertices);faces.append(face);owners.append(ob.name);tris.append([tuple(vs[i]) for i in t.vertices]);parts.append(int(ob['cdlc_part']))
                ns=[me.corner_normals[i].vector for i in t.loops]
                if any(n.length_squared<.99 or n.dot(t.normal)<-1e-6 for n in ns):normal_errors.append((ob.name,t.index))
        tree=BVHTree.FromPolygons(verts,faces,all_triangles=True,epsilon=0)
        shellfaces=[face for face,part in zip(faces,parts) if part==0];shell=BVHTree.FromPolygons(verts,shellfaces,all_triangles=True,epsilon=0)
        mirrors=[];eye=B(layout['cameraEye'])
        for mi,m in enumerate(layout['mirrors']):
            n=Vector(m['normal']);right=Vector(m['up']).cross(-n).normalized();up=(-n).cross(right).normalized();hits=[];ok=0
            for y in np.linspace(-.42,.42,11):
                for x in np.linspace(-.42,.42,11):
                    if mi==2 and 4*(x*x+y*y)>.9:continue
                    point=Vector(m['centre'])+right*(x*m['size'][0])+up*(y*m['size'][1])+n*(m['convexDepth']*(1-4*(x*x+y*y)))
                    target=B(point);direction=(target-eye).normalized();hit,normal,index,distance=tree.ray_cast(eye,direction,(target-eye).length+.01)
                    good=hit is not None and parts[index]==15 and (hit-target).length<.004
                    ok+=int(good)
                    if not good:hits.append({'target':list(target),'hit':list(hit) if hit else None,'object':owners[index] if index is not None else None})
            mirrors.append({'index':mi,'visible_samples':ok,'sample_count':ok+len(hits),'blocked':hits})
            if hits:failures.append('LOD%d mirror%d has %d blocked geometry samples'%(lod,mi,len(hits)))
        mounts=[]
        for driver in (True,False):
            side=s['DriverSide'] if driver else -s['DriverSide'];normal=np.array((side,0,0) if driver else (0,0,1),float);rows=[]
            for u in np.linspace(-.026 if driver else -.048,.026 if driver else .048,7):
                for v in np.linspace(-.055,.055,9):
                    p=(side*s['HalfWidth'],s['RoofY']-.56+v,s['FrontZ']-.295+u) if driver else (side*(s['HalfWidth']-.18)+u,s['RoofY']-.30+v,s['FrontZ']-.06)
                    mount=barrel(p,s);start=B(mount+normal*.05);hit,nn,idx,dist=shell.ray_cast(start,B(-normal),.12)
                    rows.append({'u':float(u),'v':float(v),'gap_m':float(dist-.05) if hit else None,'hit':list(hit) if hit else None})
            missing=sum(r['gap_m'] is None for r in rows);maxgap=max((r['gap_m'] for r in rows if r['gap_m'] is not None),default=999)
            mounts.append({'driver':driver,'max_gap_m':maxgap,'missing_samples':missing,'rows':rows})
            if missing or maxgap>.002:failures.append('LOD%d %s mirror foot unsupported: gap %.6f, missing %d'%(lod,'driver' if driver else 'door',maxgap,missing))
        # Conservative tyre interior: all body triangles sampled at <= 15 mm after steering.
        arr=np.asarray(tris);partarr=np.asarray(parts);body=arr[(partarr<19)|(partarr>22)];wheelchecks=[]
        # Convert Blender points back into Unity for the production steering axis.
        body=body[:,:,[0,2,1]]*np.array((-1,1,-1))
        for wi in range(19,23):
            centre=np.asarray(frames[wi]['pivot']);half=.14 if wi<21 else .17
            for deg in ((-35,-25,-15,0,15,25,35) if wi<21 else (0,)):
                angle=math.radians(deg);rot=np.array(((math.cos(angle),0,math.sin(angle)),(0,1,0),(-math.sin(angle),0,math.cos(angle))))
                q=(body-centre)@rot
                lo=q.min(axis=1);hi=q.max(axis=1);candidate=np.all((hi>=(-half,-.46,-.46))&(lo<=(half,.46,.46)),axis=1);hits=[]
                for ti in np.nonzero(candidate)[0]:
                    tri=q[ti];count=min(100,max(1,int(math.ceil(max(np.linalg.norm(tri[1]-tri[0]),np.linalg.norm(tri[2]-tri[0]))/.015))))
                    grid=np.array([(i/count,j/count) for i in range(count+1) for j in range(count+1-i)])
                    pts=tri[0]+grid[:,:1]*(tri[1]-tri[0])+grid[:,1:]*(tri[2]-tri[0]);inside=(np.abs(pts[:,0])<half-.01)&(np.sum(pts[:,1:]**2,axis=1)<.45**2)
                    if inside.any():hits.append({'triangle':int(ti),'wheel_local_point':pts[np.nonzero(inside)[0][0]].tolist()})
                wheelchecks.append({'wheel':wi,'steer_deg':deg,'intrusion_samples':hits,'tested_triangles':int(candidate.sum())})
                if hits:failures.append('LOD%d wheel%d at %d degrees: %d body triangles inside conservative tyre'%(lod,wi,deg,len(hits)))
        arms=[]
        for mi in (0,1):
            driver=mi==0;side=s['DriverSide'] if driver else -s['DriverSide'];m=layout['mirrors'][mi]
            centre=np.array(m['centre']);normal=np.array(m['normal']);mount_normal=np.array((side,0,0) if driver else (0,0,1))
            mount=barrel((side*s['HalfWidth'],s['RoofY']-.56,s['FrontZ']-.295) if driver else (side*(s['HalfWidth']-.18),s['RoofY']-.30,s['FrontZ']-.06),s)
            start=mount+mount_normal*.045;end=centre-normal*.045+np.array((0,m['size'][1]*.5+.015,0))
            p1=start+np.array((side*.16,.04,.03) if driver else (side*.16,.05,.44));p2=end+np.array((side*.04,.10,-.04) if driver else (side*.04,.24,.04))
            t=np.linspace(0,1,2001)[:,None];u=1-t
            d=3*(u*u*(p1-start)+2*u*t*(p2-p1)+t*t*(end-p2));dd=6*(u*(p2-2*p1+start)+t*(end-2*p2+p1))
            radii=np.linalg.norm(d,axis=1)**3/np.maximum(np.linalg.norm(np.cross(d,dd),axis=1),1e-15);radius=.021 if driver else .026
            count=(16,10,7)[lod];t=np.linspace(0,1,count+1)[:,None];u=1-t;path=u*u*u*start+3*u*u*t*p1+3*u*t*t*p2+t*t*t*end
            spans=np.diff(path,axis=0);length=np.linalg.norm(spans,axis=1);directions=spans/length[:,None];cost=np.zeros(count+1)
            cost[1:-1]=radius*np.tan(np.arccos(np.clip(np.sum(directions[:-1]*directions[1:],axis=1),-1,1))*.5)
            remaining=length-cost[:-1]-cost[1:]
            arms.append({'driver':driver,'minimum_curve_radius_m':float(radii.min()),'tube_radius_m':radius,'minimum_miter_span_m':float(remaining.min()),'path_unity':path.tolist()})
            if radii.min()<radius*1.25 or remaining.min()<.001:failures.append('LOD%d mirror%d arm has insufficient bend clearance'%(lod,mi))
        results.append({'lod':lod,'triangles':len(faces),'normal_errors':normal_errors,'mirrors':mirrors,'mounts':mounts,'arms':arms,'wheel_envelopes':wheelchecks})
        if normal_errors:failures.append('LOD%d invalid corner normals'%lod)
    report={'source_sha256':hashlib.sha256(a.blend.read_bytes()).hexdigest(),'runtime_sha256':hashlib.sha256((a.data/'CDLCBus12.swb2.gz').read_bytes()).hexdigest(),
      'eye':list(eye),'lods':results,'failures':failures,'status':'FAILED' if failures else 'GEOMETRY_CHECKS_PASSED',
      'limits':['Mirror rays measure opaque geometry only; optical glass/refraction and reflected target pixels require game tests.',
                'Wheel sampling tests a conservative tyre interior, not a continuous collision proof.','Door and seat usability are separate checks.']}
    (a.out/'assembly.json').write_text(json.dumps(report,indent=2),encoding='utf-8');print(json.dumps({'status':report['status'],'failures':failures}))
    if failures:raise RuntimeError('Assembly measurements failed; inspect assembly.json')

if __name__=='__main__':main()
