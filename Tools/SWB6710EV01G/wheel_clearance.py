"""Reject body triangles inside the full tyre/tread envelope before export."""
def check_wheel_clearance(objects, flatten, axles, radius, front_track, rear_track):
    import bpy
    import math
    hits=[]
    def clip(poly,limit,above):
        out=[]
        if not poly:return out
        prev=poly[-1];pin=prev[0]>=limit if above else prev[0]<=limit
        for cur in poly:
            cin=cur[0]>=limit if above else cur[0]<=limit
            if pin!=cin:
                t=(limit-prev[0])/(cur[0]-prev[0])
                out.append(tuple(prev[k]+t*(cur[k]-prev[k]) for k in range(3)))
            if cin:out.append(cur)
            prev,pin=cur,cin
        return out
    def overlaps(poly,cy,cz,r):
        points=[(p[1]-cy,p[2]-cz) for p in poly]
        signs=[]
        for a,b in zip(points,points[1:]+points[:1]):
            if a[0]*a[0]+a[1]*a[1]<r*r:return True
            dx,dz=b[0]-a[0],b[1]-a[1]
            n=dx*dx+dz*dz
            if n>1e-14:
                t=max(0,min(1,-(a[0]*dx+a[1]*dz)/n))
                if (a[0]+t*dx)**2+(a[1]+t*dz)**2<r*r:return True
            cross=dx*(-a[1])-dz*(-a[0])
            if abs(cross)>1e-12:signs.append(cross>0)
        return len(signs)>=3 and (all(signs) or not any(signs))
    angles=(-35,-25,-15,0,15,25,35)
    r=radius+.004;half=.103
    wheels=[(('WheelF' if ai==0 else 'WheelR')+('L' if s<0 else 'R'),
             s*(front_track if ai==0 else rear_track)/2,y,radius,angle,
             math.cos(math.radians(angle)),math.sin(math.radians(angle)))
            for ai,y in enumerate(axles) for s in [-1,1]
            for angle in (angles if ai==0 else (0,))]
    for ob in objects:
        if ob.get('bone','').startswith('Wheel'):continue
        data=flatten(ob);data.calc_loop_triangles()
        pts=[tuple(v.co) for v in data.vertices]
        found=set()
        for tri in data.loop_triangles:
            p=[pts[i] for i in tri.vertices]
            for name,cx,cy,cz,angle,c,s in wheels:
                key=(name,angle)
                if key in found:continue
                ex=half*abs(c)+r*abs(s);ey=r*abs(c)+half*abs(s)
                if max(v[0] for v in p)<cx-ex or min(v[0] for v in p)>cx+ex:continue
                if max(v[1] for v in p)<cy-ey or min(v[1] for v in p)>cy+ey:continue
                if max(v[2] for v in p)<cz-r or min(v[2] for v in p)>cz+r:continue
                # Inverse Blender Z yaw: the wheel's rolling axis is local X.
                local=[(c*(v[0]-cx)+s*(v[1]-cy),-s*(v[0]-cx)+c*(v[1]-cy),v[2]-cz) for v in p]
                polygon=clip(clip(local,-half,True),half,False)
                if polygon and overlaps(polygon,0,0,r):
                    found.add(key)
                    hits.append({'wheel':name,'steering_degrees':angle,'object':ob.name,'triangle':tri.index})
        bpy.data.meshes.remove(data)
    return {'status':'PASS' if not hits else 'FAIL','tyre_radius_m':radius,
            'outer_envelope_radius_m':r,'tested_half_width_m':half,
            'radial_tolerance_m':0.,'tested_wheels':4,
            'front_steering_angles_degrees':list(angles),'tested_wheel_poses':len(wheels),'penetrations':hits}
