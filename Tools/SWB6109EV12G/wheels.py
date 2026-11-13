"""Independent 275/70R22.5-style wheel assemblies; rear wheels are dual tyres."""
import math
import bmesh
import bpy
from mathutils import Matrix


def _wheel_closed_mesh(name, vertices, faces, mat):
    ob=mesh(name,vertices,faces,mat)
    bm=bmesh.new();bm.from_mesh(ob.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(ob.data);bm.free()
    for face in ob.data.polygons:face.use_smooth=True
    return ob


def _wheel_lathe(name, centre_x, y, side, profile, mat, steps=96):
    vertices=[]
    for offset,radius in profile:
        for i in range(steps):
            theta=2*math.pi*i/steps
            vertices.append((centre_x+side*offset,y+radius*math.cos(theta),R+radius*math.sin(theta)))
    faces=[]
    for j in range(len(profile)):
        for i in range(steps):
            k=(i+1)%steps;n=(j+1)%len(profile)
            faces.append((j*steps+i,j*steps+k,n*steps+k,n*steps+i))
    return _wheel_closed_mesh(name,vertices,faces,mat)


_half=float(SPEC['tyre_half_width_m'])
_dual=float(SPEC.get('rear_dual_offset_m',.145))
wheel_layout=[]
for ai,ay in enumerate(AXLES):
    track=SPEC['front_track_m'] if ai==0 else SPEC['rear_track_m']
    for side in (-1,1):
        bone=('WheelF' if ai==0 else 'WheelR')+('L' if side<0 else 'R')
        index=(19 if ai==0 else 21)+(0 if side<0 else 1)
        before=set(MASTER.objects)
        centres=[side*track/2] if ai==0 else [side*(track/2-_dual),side*(track/2+_dual)]
        for ti,cx in enumerate(centres):
            title=bone+(' single' if ai==0 else (' inner dual' if ti==0 else ' outer dual'))
            profile=[(-_half,R*.65),(-_half,R*.84),(-_half*.96,R*.91),
                     (-_half*.78,R*.982),(-_half*.53,R),(_half*.53,R),
                     (_half*.78,R*.982),(_half*.96,R*.91),(_half,R*.84),(_half,R*.65)]
            tyre=_wheel_lathe(title+' closed tyre',cx,ay,side,profile,rubber)
            tyre['tyre_centre_blender']=[cx,ay,R]
            tyre['tyre_half_width_m']=_half
            for dx in (-_half*.55,0,_half*.55):
                tube(title+' fine tread ring',[(cx+dx,ay+(R+.001)*math.cos(2*math.pi*i/96),
                     R+(R+.001)*math.sin(2*math.pi*i/96)) for i in range(96)],.003,hubdark,True)
            outer=cx+side*(_half+.002)
            # Closed dished rim and bead; no opaque disc is placed over the tyre.
            _wheel_lathe(title+' aluminium dish',outer,ay,side,
                [(-.035,R*.61),(-.005,R*.665),(.011,R*.665),(.017,R*.62),
                 (-.012,R*.46),(.016,R*.20),(-.005,R*.18),(-.04,R*.45)],
                chrome if (ai==0 or ti==1) else hubdark,72)
            if ai==0 or ti==1:
                rod(title+' hub',(outer,ay,R),(outer+side*.036,ay,R),R*.16,chrome,48)
                for i in range(10):
                    theta=2*math.pi*i/10
                    yy,zz=ay+R*.30*math.cos(theta),R+R*.30*math.sin(theta)
                    rod(title+f' wheel nut {i+1}',(outer+side*.003,yy,zz),
                        (outer+side*.026,yy,zz),.017,chrome,6)
                    theta+=math.pi/10
                    yy,zz=ay+R*.49*math.cos(theta),R+R*.49*math.sin(theta)
                    rod(title+f' recessed ventilation {i+1}',(outer-side*.004,yy,zz),
                        (outer+side*.001,yy,zz),.031,hubdark,20)
        pivot=bpy.data.objects.new(bone+' pivot',None);MASTER.objects.link(pivot)
        pivot.location=(side*track/2,ay,R)
        for ob in set(MASTER.objects)-before-{pivot}:
            ob['bone']=bone;ob['cdlc_part']=index
            ob['preserve_lod']=True
            if 'fine tread ring' in ob.name.lower():ob['cdlc_last_lod']=0
            elif 'wheel nut' in ob.name.lower():ob['cdlc_last_lod']=1
            ob.parent=pivot;ob.matrix_parent_inverse=Matrix.Translation(pivot.location).inverted()
        wheel_layout.append({'part':index,'bone':bone,'pivot_blender':list(pivot.location),
                             'tyre_centres_blender':[[x,ay,R] for x in centres]})
