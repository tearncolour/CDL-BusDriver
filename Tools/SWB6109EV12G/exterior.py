"""Independent Series 9 iEV10 coachwork, reconstructed from its own references.

Body panels have inner skins and boundary returns. Glass is exported separately.
Dimensions are metres; styling stations are photographic estimates.
"""
import bmesh
_INNER_PAINT={}

def stamp(obj, part=0, preserve=True):
    if part==16 and obj.data.materials and all(m and m.get('livery_control') for m in obj.data.materials):part=18
    obj['cdlc_part']=part
    obj['preserve_lod']=preserve
    return obj

def smooth(obj):
    for p in obj.data.polygons:p.use_smooth=True
    return obj

def shell(name, vertices, faces, mats, mids=None, offset=(0,.025,0), part=0):
    """Close the actual open boundaries of a sampled outer skin."""
    # Clean collapsed silhouette rows before computing the thickness boundary.
    # Thickening first would leave an open sliver where a quarter tapers to zero.
    clean=bmesh.new();cv=[clean.verts.new(p) for p in vertices]
    for f,mi in zip(faces,mids or [0]*len(faces)):
        cf=clean.faces.new([cv[i] for i in f]);cf.material_index=mi
    bmesh.ops.remove_doubles(clean,verts=list(clean.verts),dist=.000001)
    bmesh.ops.dissolve_degenerate(clean,edges=list(clean.edges),dist=.000001)
    loose=[v for v in clean.verts if not v.link_faces]
    if loose:bmesh.ops.delete(clean,geom=loose,context='VERTS')
    clean.verts.index_update()
    vertices=[tuple(v.co) for v in clean.verts]
    faces=[tuple(v.index for v in f.verts) for f in clean.faces]
    mids=[f.material_index for f in clean.faces];clean.free()
    n=len(vertices);v=[tuple(p) for p in vertices]
    if callable(offset):v += [tuple(Vector(p)+Vector(offset(p))) for p in vertices]
    else:v += [tuple(Vector(p)+Vector(offset)) for p in vertices]
    allfaces=list(faces)+[tuple(i+n for i in reversed(f)) for f in faces]
    edges={}
    for f in faces:
        for a,b in zip(f,f[1:]+f[:1]):
            key=tuple(sorted((a,b)))
            if key in edges:edges[key]=None
            else:edges[key]=(a,b)
    for edge in edges.values():
        if edge:
            a,b=edge;allfaces.append((b,a,a+n,b+n))
    material_list=list(mats);inner_ids={}
    for i,m in enumerate(mats):
        if m.get('livery_control'):
            if m.name not in _INNER_PAINT:
                inside=m.copy();inside.name=m.name+' | inner structural skin';inside['livery_control']=False
                MATERIALS.append(inside);_INNER_PAINT[m.name]=inside
            inner_ids[i]=len(material_list);material_list.append(_INNER_PAINT[m.name])
        else:inner_ids[i]=i
    ob=mesh(name,v,allfaces,mats[0]);ob.data.materials.clear()
    for m in material_list:ob.data.materials.append(m)
    mi=list(mids or [0]*len(faces))
    for p,i in zip(ob.data.polygons,mi+[inner_ids[i] for i in mi]+[inner_ids[0]]*(len(allfaces)-2*len(mi))):p.material_index=i
    # Geometry normals, including inner walls and rims, must face outwards.
    bm=bmesh.new();bm.from_mesh(ob.data)
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
    bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=.000001)
    loose=[v for v in bm.verts if not v.link_faces]
    if loose:bmesh.ops.delete(bm,geom=loose,context='VERTS')
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bmesh.ops.triangulate(bm,faces=[f for f in bm.faces if len(f.verts)>4],quad_method='BEAUTY',ngon_method='BEAUTY')
    for _ in range(3):
        short=set()
        for f in bm.faces:
            if len(f.verts)==3 and f.calc_area()<5e-10:
                edge=min(f.edges,key=lambda e:e.calc_length())
                if edge.calc_length()<.0001:short.add(edge)
        if not short:break
        bmesh.ops.collapse(bm,edges=list(short),uvs=False)
        bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=.000001)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.normal_update()
    for e in bm.edges:
        e.smooth=len(e.link_faces)==2 and e.link_faces[0].normal.dot(e.link_faces[1].normal)>.866
    bm.to_mesh(ob.data);bm.free();ob.data.update()
    stamp(ob,part);smooth(ob)
    if part in (0,16):
        paint_map={str(i):18 for i,m in enumerate(material_list) if m.get('livery_control')}
        if paint_map:ob['cdlc_part_by_material']=json.dumps(paint_map)
    return ob

def spline(points):
    """C2 cubic spline, with natural endpoint second derivatives."""
    xs=[p[0] for p in points];ys=[p[1] for p in points];n=len(xs)
    lo=[0.]*n;diag=[1.]*n;up=[0.]*n;rhs=[0.]*n
    for i in range(1,n-1):
        a=xs[i]-xs[i-1];b=xs[i+1]-xs[i]
        lo[i]=a;diag[i]=2*(a+b);up[i]=b
        rhs[i]=6*((ys[i+1]-ys[i])/b-(ys[i]-ys[i-1])/a)
    for i in range(1,n):
        f=lo[i]/diag[i-1];diag[i]-=f*up[i-1];rhs[i]-=f*rhs[i-1]
    mm=[0.]*n;mm[-1]=rhs[-1]/diag[-1]
    for i in reversed(range(n-1)):mm[i]=(rhs[i]-up[i]*mm[i+1])/diag[i]
    def sample(x):
        x=max(xs[0],min(xs[-1],x));i=next((j for j in range(n-1) if x<=xs[j+1]),n-2)
        h=xs[i+1]-xs[i];a=(xs[i+1]-x)/h;b=(x-xs[i])/h
        return a*ys[i]+b*ys[i+1]+((a*a*a-a)*mm[i]+(b*b*b-b)*mm[i+1])*h*h/6
    return sample

def width_at(z):
    if z<.65:
        t=max(0,min(1,(z-.23)/.42));return 1.25+.025*t*t*t*(10-15*t+6*t*t)
    if z<=2.75:return 1.275
    return 1.275-.125*min(1,(z-2.75)/.41)**5
_front_profile=spline([(.23,-5.07),(.38,-5.22),(.65,-5.25),(1.0,-5.24),(1.3,-5.23),(2.35,-5.19),(2.70,-5.16),(2.85,-5.11),(3.0,-4.98),(3.12,-4.76),(3.16,-4.60)])
_rear_profile=spline([(.23,5.05),(.40,5.22),(.75,5.25),(1.7,5.25),(2.8,5.20),(3.0,5.13),(3.12,4.99),(3.16,4.87)])
_front_shift=-HALF-min(_front_profile(.23+2.93*i/10000) for i in range(10001))
_rear_shift=HALF-max(_rear_profile(.23+2.93*i/10000) for i in range(10001))
def front_at(z):return _front_profile(z)+_front_shift
def rear_at(z):return _rear_profile(z)+_rear_shift

def end_point(u,z,rear=False,depth=0):
    # A broad, gently bowed centre blends into the wrapped quarter corners.
    # The rounded return reaches the side with a vertical transverse tangent.
    # A finite-slope polynomial at u=1 would leave a visible crease at the join.
    s=.115*(1-math.sqrt(max(0,1-min(1,abs(u))**16)))
    return (u*width_at(z),(rear_at(z)-s-depth) if rear else (front_at(z)+s+depth),z)

END_EDGES={}
def edge_point(side,z,rear=False):
    points=END_EDGES.get((side,rear))
    if not points:return end_point(side,z,rear)
    for a,b in zip(points,points[1:]):
        if a[2]<=z<=b[2]+1e-8:
            t=(z-a[2])/(b[2]-a[2]);return tuple(a[k]+(b[k]-a[k])*t for k in range(3))
    return points[0] if z<points[0][2] else points[-1]

def end_inset_vector(p,rear=False,thickness=.034):
    """Move an end skin into the body, including its transverse side return.

    A fixed longitudinal shift becomes tangential at the +/-X side edge:
    that used to place the white inner skin on top of the black side shoulder.
    The limiting normal at u=+/-1 matches the shoulder's width(z) section.
    """
    x,y,z=p;w=width_at(z);u=x/w;h=.00001
    dw=(width_at(z+h)-width_at(z-h))/(2*h)
    if abs(u)>=.999999:
        normal=Vector((1 if u>0 else -1,0,-dw))
    else:
        ds=.92*math.copysign(abs(u)**15,u)/math.sqrt(max(1e-14,1-abs(u)**16))
        profile=rear_at if rear else front_at
        dy=(profile(z+h)-profile(z-h))/(2*h)
        normal=Vector((ds,w,-w*dy-ds*u*dw)) if rear else Vector((ds,-w,w*dy-ds*u*dw))
    return -normal.normalized()*thickness

def end_surface(rear=False):
    us=sorted(set([-1+2*i/64 for i in range(65)]+[side*u for side in (-1,1) for u in (.94,.98,.99,.996,.999)]))
    verts=[];outer=[];glassfaces=[];mids=[]
    for u in us:
        low=1.18+.18*abs(u)**6 if not rear else 1.77
        high=2.77-.035*abs(u)**4 if not rear else 2.97-.045*abs(u)**6
        top=3.16-.04*u*u
        zz=[.23,.30,.38,.48,.60,.73,.87,1.00,1.035,low]
        zz += [low+(high-low)*i/20 for i in range(1,21)]
        zz += [high+(top-high)*i/10 for i in range(1,11)]
        zz=[a+(b-a)*i/6 for a,b in zip(zz,zz[1:]) for i in range(6)]+[zz[-1]]
        if abs(u)==1:END_EDGES[(int(u),rear)]=[end_point(u,z,rear) for z in zz]
        verts.extend(end_point(u,z,rear) for z in zz)
    rows=len(zz)
    for i in range(len(us)-1):
        for j in range(rows-1):
            f=(i*rows+j,(i+1)*rows+j,(i+1)*rows+j+1,i*rows+j+1)
            if rear:f=tuple(reversed(f))
            if min(us[i],us[i+1])>=-.94-1e-8 and max(us[i],us[i+1])<=.94+1e-8 and 9*6<=j<29*6:
                glassfaces.append(f)
            else:
                outer.append(f)
                mids.append(1 if ((j>=8*6 and not rear) or (rear and j>=9*6 and j<30*6)) and abs((us[i]+us[i+1])/2)<.985 else 0)
    direction=-1 if rear else 1
    shell(('Rear' if rear else 'Front')+' continuous coachwork',verts,outer,[white,black],mids,lambda p:end_inset_vector(p,rear))
    shell(('Rear' if rear else 'Front')+' continuous glazing',verts,glassfaces,[glass],offset=(0,direction*.006,0),part=17)
    # A seal following the real aperture, with no separate protruding sign box.
    low=lambda u:1.18+.18*abs(u)**6 if not rear else 1.77
    high=lambda u:2.77-.035*abs(u)**4 if not rear else 2.97-.045*abs(u)**6
    pts=[end_point(u,low(u),rear,-.004) for u in [-.94+1.88*i/64 for i in range(65)]]
    pts += [end_point(.94,low(.94)+(high(.94)-low(.94))*i/32,rear,-.004) for i in range(1,33)]
    pts += [end_point(u,high(u),rear,-.004) for u in [.94-1.88*i/64 for i in range(1,65)]]
    pts += [end_point(-.94,high(-.94)-(high(-.94)-low(-.94))*i/32,rear,-.004) for i in range(1,33)]
    stamp(tube(('Rear' if rear else 'Front')+' glazing perimeter EPDM',pts,.009,rubber,True),17)

end_surface();end_surface(True)

def patch_end(name,points,mat,rear=False,depth=-.008,thickness=.010,part=16):
    # Tessellate the flat design, refine it, then project every vertex onto the
    # coachwork. A single non-planar n-gon would cut through the curved body.
    from mathutils.geometry import tessellate_polygon
    pp=[Vector((x,0,z)) for x,z in points];bm=bmesh.new()
    lookup={tuple(v):bm.verts.new(v) for v in pp}
    for tri in tessellate_polygon([pp]):bm.faces.new([lookup[tuple(pp[v] if isinstance(v,int) else v)] for v in tri])
    for _ in range(7):
        edges=[e for e in bm.edges if e.calc_length()>.045]
        if not edges:break
        bmesh.ops.subdivide_edges(bm,edges=edges,cuts=1,use_grid_fill=True)
    bm.verts.index_update()
    vs=[end_point(v.co.x/width_at(v.co.z),v.co.z,rear,depth) for v in bm.verts]
    ff=[tuple(v.index for v in f.verts) for f in bm.faces];bm.free()
    return shell(name,vs,ff,[mat],offset=(0,(-1 if rear else 1)*thickness,0),part=part)

def outline_end(name,points,mat,rear=False,depth=-.012,radius=.007,part=16,closed=True):
    sampled=[]
    for a,b in zip(points,points[1:]+(points[:1] if closed else [])):
        n=max(1,math.ceil(math.hypot(b[0]-a[0],b[1]-a[1])/.025))
        sampled += [(a[0]+(b[0]-a[0])*i/n,a[1]+(b[1]-a[1])*i/n) for i in range(n)]
    if not closed:sampled.append(points[-1])
    return stamp(tube(name,[end_point(x/width_at(z),z,rear,depth) for x,z in sampled],radius,mat,closed),part)

def ribbon_end(name,points,width,mat,rear=False,depth=-.012,thickness=.002,part=16):
    """Thin fitted lens with mitered bends, rather than a round external pipe."""
    edges=[]
    for i,p in enumerate(points):
        before=Vector(points[max(0,i-1)]);after=Vector(points[min(len(points)-1,i+1)])
        centre=Vector(p)
        a=(centre-before).normalized() if i else (after-centre).normalized()
        b=(after-centre).normalized() if i<len(points)-1 else a
        n1=Vector((-a.y,a.x));n2=Vector((-b.y,b.x))
        m=(n1+n2).normalized();delta=m*(width*.5/max(.35,m.dot(n1)))
        edges.append((centre+delta,centre-delta))
    vertices=[]
    for i,(a,b) in enumerate(zip(edges,edges[1:])):
        count=max(1,math.ceil((Vector(points[i+1])-Vector(points[i])).length/.020))
        for k in range(count):
            for side in (0,1):
                p=a[side].lerp(b[side],k/count)
                vertices.append(end_point(p.x/width_at(p.y),p.y,rear,depth))
    for p in edges[-1]:vertices.append(end_point(p.x/width_at(p.y),p.y,rear,depth))
    faces=[(i,i+1,i+3,i+2) for i in range(0,len(vertices)-2,2)]
    return shell(name,vertices,faces,[mat],offset=(0,(-1 if rear else 1)*thickness,0),part=part)

def end_label(name,body,x,z,size,mat,rear=False,depth=-.016):
    def yy(a,b):return end_point(a/width_at(b),b,rear)[1]
    dx=(yy(x+.001,z)-yy(x-.001,z))/.002
    dz=(yy(x,z+.001)-yy(x,z-.001))/.002
    sign=-1 if rear else 1
    return label(name,body,end_point(x/width_at(z),z,rear,depth),size,mat,(sign*dx,-sign,sign*dz))

# Genuine Series 9 angular DRL and four-cell main headlamp assemblies.
unlit_head=material('Unlit projector optical surface',(.12,.18,.20),.18,.35)
unlit_red=material('Unlit red lamp lens',(.18,.008,.009),.20)
unlit_clear=material('Unlit clear lamp lens',(.52,.60,.61),.20)
unlit_amber=material('Unlit amber lamp lens',(.55,.18,.02),.20)
for side in [-1,1]:
    lower=[(.70,.61),(.83,.72),(1.225,.77),(1.235,.55),(1.17,.50),(.82,.53)]
    points=[(side*x,z) for x,z in lower]
    patch_end('Headlamp recessed black four cell bowl '+str(side),points,black,depth=-.004,thickness=.027)
    outline_end('Headlamp satin bezel '+str(side),points,chrome,radius=.009)
    for k in range(4):
        x=.82+.099*k;z=.608+.025*k
        cell=[(side*(x-.031),z+.044),(side*(x+.031),z+.053),(side*(x+.027),z-.036),(side*(x-.020),z-.047)]
        patch_end('Headlamp projector unlit lens '+str(side)+' '+str(k),cell,unlit_head,depth=-.015,thickness=.006)
        patch_end('Headlamp projector lens '+str(side)+' '+str(k),cell,head,depth=-.017,thickness=.001,part=23)
        outline_end('Headlamp projector cell trim '+str(side)+' '+str(k),cell,chrome,radius=.004)
    points=[(side*x,z) for x,z in [(.72,1.07),(.88,1.03),(1.235,1.16),(1.233,1.205),(.90,1.082)]]
    patch_end('Upper swept DRL recess '+str(side),points,black,depth=-.009)
    line=[(side*x,z) for x,z in [(.76,1.072),(.90,1.050),(1.218,1.170)]]
    ribbon_end('Upper slender DRL unlit lens '+str(side),line,.020,unlit_clear,depth=-.012)
    ribbon_end('Upper slender DRL '+str(side),line,.020,drl,depth=-.014,thickness=.001,part=23)
    indicator=[(side*1.125,1.135),(side*1.218,1.170)]
    ribbon_end('Front amber indicator unlit lens '+str(side),indicator,.012,unlit_amber,depth=-.016)
    ribbon_end('Front amber indicator '+str(side),indicator,.012,turnL if side<0 else turnR,depth=-.018,thickness=.001,part=26 if side<0 else 27)

def bumper_mouldings():
    # These are open coachwork creases. Closing the paths added straight
    # return rails; the lower rail also crossed the licence plate lettering.
    # Stop each centreline 30 mm outside the 530 mm plate, with sealed ends.
    paths=[('upper',[(-1.20,.47),(-.74,.482),(-.62,.52),(.62,.52),(.74,.482),(1.20,.47)]),
           ('lower left',[(-1.20,.31),(-.74,.322),(-.62,.36),(-.295,.36)]),
           ('lower right',[(.295,.36),(.62,.36),(.74,.322),(1.20,.31)])]
    for suffix,points in paths:
        ob=outline_end('Bumper mould line '+suffix,points,white,radius=.013,closed=False)
        ob['preserve_lod']=True
bumper_mouldings()
plate=patch_end('Front licence plate recess',[(-.265,.286),(.265,.286),(.265,.422),(-.265,.422)],hubdark,depth=-.010)
end_label('Front licence plate','沪 A·6109',0,.351,.064,white,depth=-.024)
# Exact-outline operator, manufacturer and accessibility badges are authored
# in front_badges.py, independently of the structural coachwork.

# Rear upper strip and long angular, multi-function lamps; no circular stacks.
for side in [-1,1]:
    polygon=[(.57,1.79),(.88,1.80),(1.02,1.89),(1.235,1.87),(1.235,.65),(1.06,.51),(1.06,1.38),(1.17,1.57),(1.17,1.80),(.88,1.70),(.62,1.70)]
    points=[(side*x,z) for x,z in polygon]
    patch_end('Rear folded tail lamp black base '+str(side),points,black,True,-.01,.025)
    outline_end('Rear folded red unlit lens '+str(side),points,unlit_red,True,-.023,.014)
    outline_end('Rear folded red light guide '+str(side),points,tail,True,-.025,.014,24)
    for k in range(5):
        x=.67+.092*k
        patch_end('Rear wing brake unlit lens '+str(side)+' '+str(k),[(side*x,1.728),(side*(x+.041),1.736),(side*(x+.026),1.769),(side*(x-.014),1.757)],unlit_red,True,-.026,.006)
        patch_end('Rear wing brake facet '+str(side)+' '+str(k),[(side*x,1.728),(side*(x+.041),1.736),(side*(x+.026),1.769),(side*(x-.014),1.757)],brake,True,-.028,.006,25)
    patch_end('Rear reverse unlit lens '+str(side),[(side*1.101,.77),(side*1.150,.83),(side*1.150,1.27),(side*1.101,1.23)],unlit_clear,True,-.026,.006)
    patch_end('Rear reverse vertical lens '+str(side),[(side*1.101,.77),(side*1.150,.83),(side*1.150,1.27),(side*1.101,1.23)],reverse,True,-.028,.001,28)
    patch_end('Rear direction unlit lens '+str(side),[(side*1.180,1.31),(side*1.213,1.36),(side*1.213,1.56),(side*1.180,1.52)],unlit_amber,True,-.027,.006)
    patch_end('Rear direction segment '+str(side),[(side*1.180,1.31),(side*1.213,1.36),(side*1.213,1.56),(side*1.180,1.52)],turnL if side<0 else turnR,True,-.029,.006,26 if side<0 else 27)
    patch_end('Rear low reflector '+str(side),[(side*.98,.35),(side*1.19,.38),(side*1.17,.41),(side*1.01,.39)],unlit_red,True,-.012,.006)
ribbon_end('Rear full width upper stop unlit lens',[(-.91*width_at(3),3),(.91*width_at(3),3)],.038,unlit_red,True,-.004,.003)
ribbon_end('Rear full width upper stop light',[(-.91*width_at(3),3),(.91*width_at(3),3)],.038,brake,True,-.006,.001,25)
outline_end('Rear engine hatch shutline',[(-1.03,.53),(1.03,.53),(1.05,1.68),(.65,1.70),(-.65,1.70),(-1.05,1.68)],rubber,True,-.004,.006)
label('Rear model badge','iEV10',end_point(-.67, .76,True,-.013),.084,black,(0,1,0))
label('Rear electric badge','纯电动',end_point(-.67,.665,True,-.013),.079,black,(0,1,0))
label('Rear manufacturer badge','申沃客车',end_point(.62,.70,True,-.013),.077,black,(0,1,0))
patch_end('Rear licence plate recess',[(-.28,.28),(.28,.28),(.28,.44),(-.28,.44)],hubdark,True,-.008)
end_label('Rear licence plate','沪 A·6109',0,.36,.07,white,True,-.022)
for x in [-1.0,-.50,.50,1.0]:
    ob=rod('Rear parking sensor',end_point(x/width_at(.32),.32,True,-.011),end_point(x/width_at(.32),.32,True,-.017),.009,hubdark,16);stamp(ob,16)

# Destination boards sit behind the common glass. The rear backing has a real
# lower passenger-window opening and a separate inset sign opening.
SIGN_SURFACES={
 'front':{'centre_unity':[0,2.60,5.09],'normal_unity':[0,0,1],'size_m':[1.68,.24],'matrix':[112,16]},
 'side':{'centre_unity':[1.292,1.47,2.60],'normal_unity':[1,0,0],'size_m':[.96,.192],'matrix':[80,16]},
 'rear':{'centre_unity':[0,2.61,-5.12],'normal_unity':[0,0,-1],'size_m':[1.54,.22],'matrix':[112,16]},
 'next_stop':{'centre_unity':[0,2.49,3.37],'normal_unity':[0,0,-1],'size_m':[1.28,.20]}}
stamp(box('Front destination recessed carrier',(0,-5.075,2.60),(1.78,.026,.28),black,.024),15)
label('Front destination sample','794  上海火车站',(0,-5.095,2.60),.165,led)
stamp(box('Rear destination recessed carrier',(0,5.102,2.61),(1.66,.026,.26),black,.022),16)
label('Rear destination sample','794',(0,5.128,2.61),.165,led,(0,1,0))
from sign_enclosures import create_backings
create_backings(mesh, stamp, black)
stamp(box('Next stop display carrier',(0,-3.400,2.49),(1.34,.055,.25),plastic,.025),14)
label('Next stop destination sample','下一站 上海火车站',(0,-3.370,2.49),.10,led,(0,1,0))
(SRC/'sign-surfaces.json').write_text(json.dumps(SIGN_SURFACES,ensure_ascii=False,indent=2),encoding='utf-8')

def side_point(side,y,z,inset=0):
    y=max(edge_point(side,z)[1],min(edge_point(side,z,True)[1],y))
    return (side*(width_at(z)-inset),y,z)
def side_vertical_curve(side,y,bottom,top,inset=0):
    # A chord between two surface points cuts through the rounded lower body.
    count=max(1,math.ceil(abs(top-bottom)/.020))
    return [side_point(side,y,bottom+(top-bottom)*i/count,inset) for i in range(count+1)]
def side_patch(name,side,coords,mat,depth=0,thickness=.028,part=0):
    vs=[side_point(side,y,z,-depth) for y,z in coords]
    f=tuple(range(len(vs)))
    if side<0:f=tuple(reversed(f))
    return shell(name,vs,[f],[mat],offset=(-side*thickness,0,0),part=part)

def smoothstep(t):
    t=max(0,min(1,t));return t*t*t*(10+t*(-15+6*t))
def band_bottom(y):
    # White surrounds both arches; twin coloured lines sweep into each shoulder.
    front_bump=smoothstep((y+3.72)/.34)*(1-smoothstep((y+2.40)/.45))
    rear_rise=smoothstep((y-1.40)/.65)
    return .76+.40*front_bump+.40*rear_rise

def roof_stripe_height(y):
    return 2.97-.12*smoothstep((y-1.15)/.65)-.095*(1-smoothstep((y+4.82)/.55))

def open_door(y,side):
    return side>0 and any(abs(y-d['centre_blender_y'])<d['width_m']/2-1e-6 for d in SPEC['doors'])

for side in [-1,1]:
    segments=[(-4.95,5.045)] if side<0 else [(-5.015,-4.95),(-3.65,-.35),(.95,5.045)]
    for segid,(a,b) in enumerate(segments):
        ys=sorted(set([a+(b-a)*i/max(1,math.ceil((b-a)/.055)) for i in range(max(1,math.ceil((b-a)/.055))+1)] + [x for ay in AXLES for x in [ay-.585,ay+.585] if a<x<b]))
        # Split at arch endpoints so the vertical throat has its proper edge.
        breaks=sorted(set([a,b]+[x for ay in AXLES for x in [ay-.585,ay+.585] if a<x<b]))
        for qa,qb in zip(breaks,breaks[1:]):
            inarch=next((ay for ay in AXLES if abs((qa+qb)/2-ay)<.585-1e-7),None)
            steps=max(2,math.ceil((qb-qa)/(.045 if inarch is not None else .20)));samples=[qa+(qb-qa)*i/steps for i in range(steps+1)]
            bottoms=[R+math.sqrt(max(0,.585**2-(y-inarch)**2)) if inarch is not None else .23 for y in samples]
            levels=sorted(set([.23+.42*i/28 for i in range(29)]+[1.24]+[p[2] for rear in (False,True) for p in END_EDGES[(side,rear)] if .23<p[2]<1.24]))
            rows=len(levels)
            vs=[side_point(side,y,max(z0,z)) for y,z0 in zip(samples,bottoms) for z in levels]
            ff=[(rows*i+j,rows*(i+1)+j,rows*(i+1)+j+1,rows*i+j+1) for i in range(steps) for j in range(rows-1)]
            if side<0:ff=[tuple(reversed(f)) for f in ff]
            shell('Side lower structural panel '+str(side)+' '+str(segid)+' '+str(qa),vs,ff,[white],offset=(-side*.032,0,0))
        coords=[(y,band_bottom(y)) for y in ys]+[(b,1.245),(a,1.245)]
        side_patch('Black lower window field '+str(side)+' '+str(segid),side,coords,black,.002,.010,16)
        for zoff in [.024,.081]:
            coords=[(y,band_bottom(y)+zoff) for y in ys]+[(y,band_bottom(y)+zoff+.020) for y in reversed(ys)]
            side_patch('Aqua lower twin stripe '+str(side)+' '+str(segid)+' '+str(zoff),side,coords,green,.0028,.0004,16)

    # Door and side panels reach the same roof contour, with closed quarter returns.
    for rear in [False,True]:
        zz=sorted(set([p[2] for p in END_EDGES[(side,rear)] if p[2]<2.85]+[2.85,1.24]))
        vv=[]
        for z in zz:
            u=side;ep=edge_point(u,z,rear)
            bound=5.045 if rear else -4.95
            vv.extend([ep,side_point(side,bound,z)])
        ff=[(2*i,2*i+1,2*i+3,2*i+2) for i in range(len(zz)-1)]
        shell(('Rear' if rear else 'Front')+' quarter closure '+str(side),vv,ff,[white,black],[0 if zz[i]<1.24 else 1 for i in range(len(ff))],offset=(-side*.025,0,0))
    # Roof side shoulder follows the same rounded section as the front/rear.
    vs=[];ys=[-4.95+9.995*i/100 for i in range(101)]
    zs=sorted(set([2.85,2.91,2.97,3.02,3.06,3.09,3.12]+[p[2] for rear in (False,True) for p in END_EDGES[(side,rear)] if 2.85<p[2]<3.12]))
    for y in ys:
        for z in zs:
            yf=edge_point(side,z)[1];yr=edge_point(side,z,True)[1]
            alpha=(y+4.95)/9.995;yy=yf+(yr-yf)*alpha
            base_x=side*width_at(z)
            xx=base_x+(edge_point(side,z)[0]-base_x)*(1-alpha)**16+(edge_point(side,z,True)[0]-base_x)*alpha**16
            vs.append((xx,yy,z))
    ff=[];mi=[]
    for i in range(100):
        for j in range(len(zs)-1):
            quad=(i*len(zs)+j,(i+1)*len(zs)+j,(i+1)*len(zs)+j+1,i*len(zs)+j+1)
            # Split the paint boundary inside each quad, avoiding a stepped
            # material edge where the rear pinstripes descend.
            poly=[Vector(vs[k]) for k in quad]
            values=[p.z-roof_stripe_height(p.y)-.09 for p in poly]
            if min(values)>=0 or max(values)<=0:
                ff.append(quad);mi.append(0 if min(values)>=0 else 1)
            else:
                for upper in (False,True):
                    clipped=[]
                    for a,b,ga,gb in zip(poly,poly[1:]+poly[:1],values,values[1:]+values[:1]):
                        inside=ga>=0 if upper else ga<=0
                        nextinside=gb>=0 if upper else gb<=0
                        if inside:clipped.append(a)
                        if inside!=nextinside:clipped.append(a.lerp(b,ga/(ga-gb)))
                    base=len(vs);vs.extend(tuple(p) for p in clipped)
                    ff.append(tuple(range(base,len(vs))));mi.append(0 if upper else 1)
    shell('Roof rounded side shoulder '+str(side),vs,ff,[white,black],mi,(-side*.03,0,0))
    for offset in [0.,.053]:
        stripe_ys=[-4.82+9.68*i/192 for i in range(193)]
        stripe_vs=[side_point(side,y,roof_stripe_height(y)+offset+dz,-.0008) for y in stripe_ys for dz in (-.011,.011)]
        stripe_faces=[(i,i+1,i+3,i+2) for i in range(0,len(stripe_vs)-2,2)]
        shell('Roof aqua twin pinstripe '+str(side)+' '+str(offset),stripe_vs,stripe_faces,[green],offset=(-side*.0004,0,0),part=16)

    if side<0:
        cells=[(-4.95,-3.65,1.37),(-3.65,-2.2,1.30),(-2.2,-.65,1.30),(-.65,.95,1.30),(.95,2.10,1.60),(2.10,3.65,1.60),(3.65,5.045,1.60)]
    else:
        cells=[(-3.65,-2.1,1.30),(-2.1,-.35,1.30),(.95,2.12,1.60),(2.12,3.62,1.60),(3.62,5.045,1.60)]
    for idx,(a,b,low) in enumerate(cells):
        # Matching sampled contours: an actual aperture, not glass over a wall.
        outer=rounded_rect_points(b-a,2.85-1.24,.006,8)
        inner=rounded_rect_points(b-a-.13,2.66-low,.045,8)
        vs=[side_point(side,(a+b)/2+y,2.045+z) for y,z in outer]
        vs += [side_point(side,(a+b)/2+y,(2.66+low)/2+z) for y,z in inner]
        n=len(outer);ff=[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
        shell('Side black window frame '+str(side)+' '+str(idx),vs,ff,[black],offset=(-side*.033,0,0),part=17)
        coords=[((a+b)/2+y,(2.66+low)/2+z) for y,z in inner]
        side_patch('Side glazing '+str(side)+' '+str(idx),side,coords,glass,-.018,.006,17)
        stamp(tube('Window aperture EPDM '+str(side)+' '+str(idx),[side_point(side,y,z,-.003) for y,z in coords],.008,rubber,True),17)
        if b-a>1.25:
            z=2.25
            stamp(rod('Sliding window centre bar '+str(side)+' '+str(idx),side_point(side,(a+b)/2,low+.025,-.001),side_point(side,(a+b)/2,2.64,-.001),.011,black),17)
            stamp(rod('Sliding window horizontal bar '+str(side)+' '+str(idx),side_point(side,a+.085,z,-.002),side_point(side,b-.085,z,-.002),.011,black),17)
            stamp(box('Window latch '+str(side)+' '+str(idx),side_point(side,(a+b)/2,2.23,-.019),(.025,.085,.032),plastic,.008),17)
        for yy in [a+.045,b-.045]:
            stamp(tube('Window vertical seal '+str(side)+' '+str(idx),[side_point(side,yy,1.245,-.006),side_point(side,yy,2.85,-.006)],.007,rubber),17)
    if side>0:
        for di,d in enumerate(SPEC['doors']):
            a=d['centre_blender_y']-d['width_m']/2;b=d['centre_blender_y']+d['width_m']/2
            side_patch('Door upper black header '+str(di),side,[(a,2.48),(b,2.48),(b,2.85),(a,2.85)],black,0,.035,0)
    for ay in AXLES:
        pts=[side_point(side,ay+.585*math.cos(t),R+.585*math.sin(t),-.007) for t in [math.pi*i/72 for i in range(73)]]
        pts=[side_point(side,ay+.585,.24,-.007)]+pts+[side_point(side,ay-.585,.24,-.007)]
        stamp(tube('Wheel arch EPDM trim '+str(side)+' '+str(ay),pts,.016,rubber),16)
    # Compartment shut lines, side markers, recessed charging sockets.
    for y in [-3.65,-1.75,1.10,3.65,4.95]:
        if open_door(y,side) or any(abs(y-a)<.65 for a in AXLES):continue
        stamp(tube('Service hatch shutline '+str(side)+' '+str(y),side_vertical_curve(side,y,.28,band_bottom(y)-.045,-.003),.0035,hubdark),16)
        stamp(box('Service hatch latch '+str(side)+' '+str(y),side_point(side,y+.075,.64,-.01),(.018,.047,.018),chrome,.003),16)
    for y in [-3.45,-1.65,1.18,3.62,4.85]:
        if open_door(y,side):continue
        stamp(box('Side amber marker unlit lens '+str(side)+' '+str(y),side_point(side,y,.44,-.010),(.024,.098,.041),unlit_amber,.012),16)
        stamp(box('Side amber marker '+str(side)+' '+str(y),side_point(side,y,.44,-.012),(.024,.098,.041),turnL if side<0 else turnR,.012),26 if side<0 else 27)
    stamp(box('Charge socket surround '+str(side),side_point(side,4.59,.75,-.002),(.02,.30,.29),white,.025),16)
    stamp(box('Charge socket lock '+str(side),side_point(side,4.61,.78,-.015),(.023,.035,.065),black,.005),16)
    shoulder_slope=(width_at(3.014+.0001)-width_at(3.014-.0001))/.0002
    label('Side manufacturer wordmark '+str(side),'申沃客车 SUNWIN',side_point(side,3.95,3.014,-.002),.075,green,(side,0,-shoulder_slope))
    label('Side ELECTRIC wordmark '+str(side),'ELECTRIC',side_point(side,-3.50,3.014,-.002),.081,white,(side,0,-shoulder_slope))
    label('Side operator name '+str(side),'久事公交',side_point(side,-2.89,1.17,-.012),.058,chrome,(side,0,0))
for i in range(9):
    y=4.00+i*.048
    stamp(tube('Rear drive compartment ventilation slot '+str(i),side_vertical_curve(1,y,.52,.88,-.010),.012,black),16)
stamp(box('Side route screen carrier',(1.279,-2.60,1.47),(.025,1.05,.215),black,.012),16)
label('Side route destination sample','794 上海火车站',(1.294,-2.60,1.47),.090,led,(1,0,0))

# Roof skin closes both quarter panels. Low equipment respects 3200 mm height.
vs=[];nx=40;ny=120
for i in range(nx+1):
    u=-1+2*i/nx;z=3.16-.04*u*u;x=u*width_at(z)
    yf=end_point(u,z)[1];yr=end_point(u,z,True)[1]
    for j in range(ny+1):vs.append((x,yf+(yr-yf)*j/ny,z))
ff=[(i*(ny+1)+j,(i+1)*(ny+1)+j,(i+1)*(ny+1)+j+1,i*(ny+1)+j+1) for i in range(nx) for j in range(ny)]
shell('Continuous upper roof skin',vs,ff,[white],offset=(0,0,-.05))
for y,length in [(-.7,1.45),(1.65,1.9),(3.52,1.34)]:
    stamp(box('Roof low equipment cover '+str(y),(0,y,3.165),(1.67,length,.060),white,.025),16)
    for x in [-.80,.80]:stamp(tube('Roof cover gasket '+str(y)+' '+str(x),[(x,y-length/2,3.171),(x,y+length/2,3.171)],.006,rubber),16)
    for k in range(15):
        stamp(box('Roof cooler grille '+str(y)+' '+str(k),(-.52+k*.074,y,3.1985),(.019,length*.62,.003),hubdark,.001),16)
for x in [-1.07,1.07]:
    stamp(tube('Roof rain gutter '+str(x),[(x,-4.4,3.155),(x,4.8,3.155)],.012,white),16)

# Two articulated windscreen wipers follow the front surface rather than float.
for side in [-1,1]:
    pivot=(side*.88,1.20);joint=(side*.50,1.43);tip=(side*-.12,1.46)
    for a,b in [(pivot,joint),(joint,tip)]:
        pa=end_point(a[0]/width_at(a[1]),a[1],False,-.045);pb=end_point(b[0]/width_at(b[1]),b[1],False,-.045)
        stamp(rod('Front wiper articulated arm '+str(side),pa,pb,.009,plastic,16),15)
    pts=[end_point(x/width_at(1.46),1.46,False,-.056) for x in [side*(-.15+.72*i/20) for i in range(21)]]
    stamp(tube('Front wiper rubber blade '+str(side),pts,.012,rubber),15)
    p=end_point(pivot[0]/width_at(pivot[1]),pivot[1],False,-.02)
    stamp(rod('Front wiper spindle '+str(side),p,(p[0],p[1]-.027,p[2]),.035,black,24),15)
for rear in [False,True]:
    p=end_point(0,2.91 if not rear else 3.04,rear,-.014)
    stamp(box(('Rear' if rear else 'Front')+' CCTV housing',p,(.062,.032,.06),plastic,.015),15)
    stamp(rod(('Rear' if rear else 'Front')+' CCTV lens',p,(p[0],p[1]+(.025 if rear else -.025),p[2]),.016,black,24),15)

(SRC/'exterior-geometry-report.json').write_text(json.dumps({
 'model':'SWB6109EV12G','construction':'Independent Series 9 coachwork with sampled C2 profile stations, closed inner skins and real glazing apertures.',
 'front_curvature':'C2 cubic longitudinal profile and rounded transverse return tangent to the side; shape stations are photo estimates.',
 'front_rear_style':'Swept upper DRL, four-cell angular lower lamps, folded rear lamps and full-width upper brake strip.',
 'livery':'White body, black window field, aqua twin stripes; no reference photo textures baked into model.',
 'photo_estimates':True},indent=2),encoding='utf-8')

