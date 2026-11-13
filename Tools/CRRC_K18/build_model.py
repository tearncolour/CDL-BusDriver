"""Editable CRRC K18 study, authored in metres in the physical Blender frame.

Construct in vehicle coordinates (right +X, up +Y, front +Z), then map once to
Blender (-X,-Z,+Y). No existing vehicle geometry or game files are modified.
Unverified dimensions are explicitly identified in model-spec.json.
"""
import bpy, bmesh, math, json, sys, hashlib
from pathlib import Path
from mathutils import Vector, Matrix

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/CRRC_K18'
if '--output-dir' in sys.argv:
    SRC=Path(sys.argv[sys.argv.index('--output-dir')+1])
    if not SRC.is_absolute():SRC=ROOT/SRC
SRC.mkdir(parents=True,exist_ok=True)
(SRC/'previews').mkdir(exist_ok=True)
U=Matrix(((-1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
bpy.ops.wm.read_factory_settings(use_empty=True)
scene=bpy.context.scene
scene.unit_settings.system='METRIC';scene.unit_settings.scale_length=1
MATS={};CURRENT=None;SECTION='front';PARENT=None
L=17.9;H=3.32;W=2.54;R=.49;JOINT=-1.32
AXLES=(6.37,.53,-5.68)
DOORS=(7.83,2.15,-3.78)
FRONT_JOIN=-.53;REAR_JOIN=-2.11

def coll(name):
    c=bpy.data.collections.new(name);scene.collection.children.link(c);return c
FRONT=coll('K18 Front');REAR=coll('K18 Rear');JOINTCOL=coll('K18 Joint');STUDIO=coll('Studio')

def mat(name,rgb,rough=.4,metal=0,swatch='Black',glass=False,light=0,control=False):
    m=bpy.data.materials.new(name);m.use_nodes=True;m.diffuse_color=(*rgb,1)
    p=m.node_tree.nodes.get('Principled BSDF')
    p.inputs['Base Color'].default_value=(*rgb,1);p.inputs['Roughness'].default_value=rough;p.inputs['Metallic'].default_value=metal
    if glass:
        p.inputs['Transmission Weight'].default_value=.94;p.inputs['IOR'].default_value=1.46;p.inputs['Coat Weight'].default_value=.5
    if light:
        p.inputs['Emission Color'].default_value=(*rgb,1);p.inputs['Emission Strength'].default_value=1.5
    m['glass']=glass;m['light_id']=light;m['livery_control']=control;m['cdlc_swatch']=swatch
    MATS[name]=m;return m
RED=mat('K18 red demonstration paint',(.59,.009,.018),.24,.15,'Red')
WHITE=mat('K18 neutral livery white',(.81,.84,.82),.3,.12,'White',control=True)
BLUE=mat('K18 demonstration blue',(.012,.10,.42),.27,.12,'Blue')
YELLOW=mat('K18 demonstration yellow',(.94,.58,.009),.3,.1,'Yellow')
BLACK=mat('Ceramic window frit',(.006,.009,.012),.2,0,'Black')
RUBBER=mat('EPDM and tyre rubber',(.012,.015,.019),.82,0,'Rubber')
GLASS=mat('Separate neutral solar glass',(.43,.55,.59),.045,0,'GlassTinted',glass=True)
GLASS['glass_alpha']=.24
FRONT_GLASS=mat('Laminated front windshield',(.63,.72,.75),.035,0,'GlassClear',glass=True)
FRONT_GLASS['glass_alpha']=.18
FRONT_GLASS.node_tree.nodes.get('Principled BSDF').inputs['Transmission Weight'].default_value=.98
METAL=mat('Brushed stainless steel',(.45,.49,.52),.24,.94,'BrushedStainless')
CHROME=mat('Polished wheel and mirror',(.66,.70,.73),.14,.98,'Chrome')
DARK=mat('Graphite cabin plastics',(.031,.038,.043),.64,0,'Black')
STEEL=mat('K18 steel grey door header and bellows',(.52,.55,.57),.45,0,'SteelGrey')
HEADER=mat('K18 door header graphite',(.22,.25,.26),.5,0,'CabinGraphite')
PANEL=mat('Light grey headlining',(.64,.67,.65),.72,0,'CabinPanelGrey')
FLOOR=mat('Dark blue safety flooring',(.040,.072,.118),.80,0,'FloorDarkBlue')
SEAT=mat('Official catalogue yellow seat shell',(.86,.58,.020),.28,0,'SeatMouldedYellow')
PRIORITY=mat('Accessible bay orange insert',(.91,.22,.015),.6,0,'Orange')
HANDGRIP=mat('Grey moulded oval passenger handgrip',(.22,.24,.25),.50,0,'HandGripGrey')
HEAD=mat('Headlamp active optical element',(.78,.9,1),.16,0,'LampWhite',light=2)
TAIL=mat('Tail lamp active element',(.55,.008,.005),.18,0,'LampRed',light=3)
BRAKE=mat('Brake active element',(.85,.015,.008),.18,0,'LampRed',light=4)
DRL=mat('Running lamps active element',(.76,.91,1),.18,0,'LampWhite',light=1)
AMBER=mat('Turn indicator active element',(1,.33,.005),.22,0,'Amber',light=5)
AMBER_RIGHT=mat('Right turn indicator active element',(1,.33,.005),.22,0,'Amber',light=6)
REVERSE=mat('Reversing active element',(.78,.9,1),.16,0,'LampWhite',light=7)
INTERIOR=mat('Saloon light diffuser',(.77,.86,1.),.55,0,'LampWhite',light=8)
LED=mat('Destination amber dots',(1,.27,.015),.5,0,'LedAmber',light=9)

def empty(name,pos=(0,0,0),parent=None,part=None):
    ob=bpy.data.objects.new(name,None);CURRENT.objects.link(ob);ob.empty_display_size=.12
    ob.location=U@Vector(pos);ob['cab_section']=SECTION
    if parent: ob.parent=parent;ob.matrix_parent_inverse=parent.matrix_world.inverted()
    if part is not None:
        ob['cdlc_part']=part;ob['rest_pivot_unity']=list(Vector(pos)-Vector((0,0,JOINT if SECTION=='rear' else 0)))
    bpy.context.view_layer.update()
    return ob

def mesh(name,verts,faces,material,part=0,parent=None,lod=2,open_surface=False):
    data=bpy.data.meshes.new(name);data.from_pydata([U@Vector(p) for p in verts],[],[tuple(reversed(f)) for f in faces]);data.update()
    if not name.startswith('CDLC common'):
        bm=bmesh.new();bm.from_mesh(data)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        if bm.calc_volume(signed=True)<0:bmesh.ops.reverse_faces(bm,faces=list(bm.faces))
        bm.to_mesh(data);bm.free();data.update()
    ob=bpy.data.objects.new(name,data);CURRENT.objects.link(ob);data.materials.append(material)
    ob['cab_section']=SECTION;ob['cdlc_part']=part;ob['last_lod']=lod;ob['designed_open_surface']=open_surface
    owner=parent or PARENT
    if owner:
        ob.parent=owner;ob.matrix_parent_inverse=owner.matrix_world.inverted()
    return ob

def box(name,c,size,material,part=0,parent=None,bevel=0,lod=2):
    x,y,z=c;a,b,d=[v/2 for v in size]
    v=[(x+sx*a,y+sy*b,z+sz*d) for sx,sy,sz in ((-1,-1,-1),(1,-1,-1),(1,1,-1),(-1,1,-1),(-1,-1,1),(1,-1,1),(1,1,1),(-1,1,1))]
    ob=mesh(name,v,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],material,part,parent,lod)
    if bevel:
        mod=ob.modifiers.new('Manufactured edge radius','BEVEL');mod.width=min(bevel,min(size)*.4);mod.segments=2
        ob.modifiers.new('Corner normals','WEIGHTED_NORMAL')
    return ob

def rod(name,a,b,r,material,part=13,parent=None,n=10,lod=2):
    a,b=Vector(a),Vector(b);axis=(b-a).normalized();ref=Vector((0,1,0))
    if abs(axis.dot(ref))>.96:ref=Vector((1,0,0))
    u=axis.cross(ref).normalized();v=axis.cross(u).normalized()
    verts=[p+r*(u*math.cos(j*2*math.pi/n)+v*math.sin(j*2*math.pi/n)) for p in (a,b) for j in range(n)]
    faces=[tuple(reversed(range(n))),tuple(range(n,2*n))]+[(j,(j+1)%n,(j+1)%n+n,j+n) for j in range(n)]
    ob=mesh(name,verts,faces,material,part,parent,lod)
    ob['radial_segments']=n;ob['radial_rings']=2
    for p in ob.data.polygons:p.use_smooth=len(p.vertices)==4
    return ob

def path(name,pts,r,material,part=13,parent=None,lod=2):
    # Each short segment is closed. Junctions overlap inside matching tube radii.
    for i in range(len(pts)-1):rod(name+' '+str(i),pts[i],pts[i+1],r,material,part,parent,lod=lod)

def skin(name,points,offset,material,part=0,parent=None,lod=2):
    # Closed extrusion of an explicit convex/triangulable boundary, not a two-sided patch.
    n=len(points);verts=points+[tuple(Vector(p)+Vector(offset)) for p in points]
    faces=[tuple(reversed(range(n))),tuple(range(n,2*n))]+[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    return mesh(name,verts,faces,material,part,parent,lod)

def curve_front_y(x,y):
    # Swept family face: windscreen, upper fascia and bumper share lateral crown.
    return 8.93-.15*(abs(x)/1.22)**2-.14*((y-1.55)/1.55)**2

def nose_band(name,y0,y1,x0,x1,material,part,depth_bias=0,nx=30,ny=10,level=0):
    verts=[]
    for depth in (0,-.026):
        for j in range(ny+1):
            y=y0+(y1-y0)*j/ny
            for i in range(nx+1):
                x=x0+(x1-x0)*i/nx;verts.append((x,y,curve_front_y(x,y)+depth+depth_bias))
    row=nx+1;side=row*(ny+1);faces=[]
    for j in range(ny):
        for i in range(nx):
            p=j*row+i;faces += [(p,p+1,p+1+row,p+row),(side+p+row,side+p+1+row,side+p+1,side+p)]
    ring=list(range(row))+[j*row+nx for j in range(1,ny+1)]+list(range(ny*row+nx-1,ny*row-1,-1))+[j*row for j in range(ny-1,0,-1)]
    faces += [(a,b,side+b,side+a) for a,b in zip(ring,ring[1:]+ring[:1])]
    ob=mesh(name,verts,faces,material,part)
    for p in ob.data.polygons:p.use_smooth=True
    ob['last_lod']=level
    if level:
        ob['first_lod']=level;ob.hide_render=True;ob.hide_set(True)
    else:
        for lv,sx,sy in ((1,12,4),(2,8,2)):
            nose_band('LOD'+str(lv)+' '+name,y0,y1,x0,x1,material,part,depth_bias,sx,sy,lv)
    return ob

def text(name,body,position,size,material,front=True,parent=None):
    data=bpy.data.curves.new(name,'FONT');data.body=body;data.size=size;data.align_x='CENTER';data.align_y='CENTER';data.extrude=.001
    ob=bpy.data.objects.new(name,data);CURRENT.objects.link(ob);ob.location=U@Vector(position)
    # Font local XY: right along physical vehicle left, up vertically, front normal forward.
    ob.rotation_euler=(math.pi/2,0,0) if front else (math.pi/2,0,math.pi)
    data.materials.append(material);ob['cab_section']=SECTION;ob['cdlc_part']=16;ob['last_lod']=0
    if material==LED:ob['runtime_generated']='LedSign'
    owner=parent or PARENT
    if owner:ob.parent=owner;ob.matrix_parent_inverse=owner.matrix_world.inverted()
    return ob

def annotate_animation(ob,axis,states):
    for frame,value in states:
        ob.rotation_euler[axis]=math.radians(value);ob.keyframe_insert('rotation_euler',index=axis,frame=frame)
    ob.rotation_euler[axis]=0

CURRENT=FRONT;SECTION='front';front_root=empty('section_front');PARENT=front_root
front_root['driver_eye_unity']=[-.79,1.97,7.62]
CURRENT=REAR;SECTION='rear';rear_root=empty('section_rear',(0,0,JOINT));PARENT=rear_root
CURRENT=JOINTCOL;SECTION='joint';joint_root=empty('articulation_system',(0,0,JOINT));PARENT=joint_root
ROOTS={'front':front_root,'rear':rear_root,'joint':joint_root}

def select_section(section):
    global CURRENT,SECTION,PARENT
    SECTION=section;CURRENT={'front':FRONT,'rear':REAR,'joint':JOINTCOL}[section];PARENT=ROOTS[section]

wheel_records=[];door_records=[];seat_records=[];strap_records=[]

def wheel(z,index,side):
    select_section('front' if index<2 else 'rear')
    part=(19 if side<0 else 20) if index==0 else (21 if side<0 else 22)
    x=side*1.06;name=f'Axle {index+1} '+('left' if side<0 else 'right')
    pivot=empty(name+' pivot',(x,R,z),PARENT,part)
    pivot['bone']=('WheelF' if index==0 else 'WheelR')+('L' if side<0 else 'R')
    for dual in ([0] if index==0 else [0,-side*.245]):
        centre=x+dual;N=56;cross=[(-.13,.34),(-.14,.43),(-.10,.485),(-.065,R),(.065,R),(.10,.485),(.14,.43),(.13,.34)]
        verts=[(centre+u,R+r*math.sin(2*math.pi*j/N),z+r*math.cos(2*math.pi*j/N)) for u,r in cross for j in range(N)]
        faces=[(k*N+j,k*N+(j+1)%N,((k+1)%len(cross))*N+(j+1)%N,((k+1)%len(cross))*N+j) for k in range(len(cross)) for j in range(N)]
        tyre=mesh(name+' tyre '+str(dual),verts,faces,RUBBER,part,pivot)
        tyre['radial_segments']=N;tyre['radial_rings']=len(cross)
        for p in tyre.data.polygons:p.use_smooth=True
    rod(name+' wheel rim',(x-side*.04,R,z),(x+side*.152,R,z),.339,METAL,part,pivot,n=48)
    rod(name+' centre hub',(x+side*.14,R,z),(x+side*.18,R,z),.145,CHROME,part,pivot,n=32)
    for j in range(10):
        a=j*2*math.pi/10
        rod(name+' nut '+str(j),(x+side*.165,R+.214*math.sin(a),z+.214*math.cos(a)),(x+side*.195,R+.214*math.sin(a),z+.214*math.cos(a)),.022,CHROME,part,pivot,n=6,lod=1)
    if index==0:annotate_animation(pivot,2,[(1,0),(130,0),(160,25),(190,-25),(220,0)])
    wheel_records.append({'name':name,'centre_unity':[x,R,z],'radius':R,'max_x':abs(x)+.195,'steered':index==0})

def arch_height(z):
    y=.22
    for az in AXLES:
        d=abs(z-az)
        if d<.59:y=max(y,R+math.sqrt(max(0,.59**2-d*d)))
    return y

def side_lower(side,start,end,doors):
    cuts=[start,end]+[d+s*.65 for d in doors for s in (-1,1) if start<d+s*.65<end]
    for a,b in zip(sorted(cuts),sorted(cuts)[1:]):
        mid=(a+b)/2
        if any(abs(mid-d)<.65 for d in doors):continue
        zs=[a+(b-a)*j/max(2,math.ceil((b-a)/.035)) for j in range(max(2,math.ceil((b-a)/.035))+1)]
        for i in range(len(zs)-1):
            z0,z1=zs[i:i+2];h0,h1=arch_height(z0),arch_height(z1)
            skin(f'{SECTION} side {side} lower skin {a:.2f} {i}',[(side*1.25,h0,z0),(side*1.25,h1,z1),(side*1.25,1.14,z1),(side*1.25,1.14,z0)],(-side*.045,0,0),RED,18,lod=0)
        for lv,step in ((1,.10),(2,.20)):
            nn=max(2,math.ceil((b-a)/step));zz=[a+(b-a)*j/nn for j in range(nn+1)]
            pts=[(side*1.25,arch_height(z),z) for z in zz]+[(side*1.25,1.14,b),(side*1.25,1.14,a)]
            low=skin(f'LOD{lv} lower side {side} {a:.2f}',pts,(-side*.045,0,0),RED,18,lod=lv)
            low['first_lod']=lv;low.hide_render=True;low.hide_set(True)
        box('Lower rub rail',(side*1.25,1.11,mid),(.024,.055,b-a),BLACK,17)
        # Thin painted polygon regions following the side surface.
        if b-a>.05:
            count=max(2,math.ceil((b-a)/.12))
            for j in range(count):
                p=a+(b-a)*j/count;q=a+(b-a)*(j+1)/count
                hp=.30+.80*(p-a)/(b-a);hq=.30+.80*(q-a)/(b-a)
                bp=max(.30,arch_height(p)+.006);bq=max(.30,arch_height(q)+.006)
                if min(hp-bp,hq-bq)>.008:
                    skin('Official white wheel-cleared graphic',[(side*1.254,bp,p),(side*1.254,bq,q),(side*1.254,hq,q),(side*1.254,hp,p)],(-side*.003,0,0),WHITE,18)
    for az in AXLES:
        if not start<az<end:continue
        pts=[(side*1.243,R+.598*math.sin(a),az+.598*math.cos(a)) for a in [j*math.pi/28 for j in range(29)]]
        path(f'{SECTION} wheel arch edge',pts,.022,RUBBER,17)
        # Crown and side walls form a wheel box, with real curved tire clearance.
        n=30;verts=[]
        for xx in (side*.68,side*1.205):
            for j in range(n+1):
                a=math.pi*j/n;verts.append((xx,R+.615*math.sin(a),az+.615*math.cos(a)))
        faces=[(j,j+1,n+2+j,n+1+j) for j in range(n)]
        shell=mesh(f'{SECTION} {side} wheelhouse crown',verts,faces,DARK,0)
        sol=shell.modifiers.new('Closed wheelhouse wall','SOLIDIFY');sol.thickness=.022
        for zz in (az-.615,az+.615):box('Wheelhouse end',(side*.94,.40,zz),(.55,.30,.035),DARK,0)
        # Inner wall follows arch outline; no tire intersecting floor below the crown.
        outline=[(side*.68,.32,az-.615)]+[(side*.68,R+.615*math.sin(math.pi*j/n),az-.615*math.cos(math.pi*j/n)) for j in range(n+1)]+[(side*.68,.32,az+.615)]
        skin('Wheelhouse aisle wall',outline,(side*.022,0,0),PANEL,0)

def section_shell(which,start,end,doors):
    select_section(which);length=end-start;mid=(start+end)/2
    box(which+' continuous low-floor aisle',(0,.30,mid),(1.32,.075,length),FLOOR,0)
    for side in (-1,1):
        intervals=sorted([start,end]+[max(start,min(end,az+d)) for az in AXLES for d in (-.64,.64) if start<az<end])
        for a,b in zip(intervals,intervals[1:]):
            if any(abs((a+b)/2-az)<.63 for az in AXLES):continue
            box('Outboard floor',(side*.93,.30,(a+b)/2),(.56,.075,b-a),FLOOR,0)
        localdoors=doors if side>0 else []
        side_lower(side,start,end,localdoors)
        band_end=7.35 if which=='front' else end
        spans=[(start,band_end)]
        if side>0:
            # the door headers (door()) take the lower part of the band over a door; the upper part stays black
            spans=[];cursor=start
            for d in sorted(localdoors):
                if start+.72<d<band_end-.72:
                    spans.append((cursor,d-.72));cursor=d+.72
                    box('Upper window band over door',(side*1.225,2.9325,d),(.09,.325,1.44),BLACK,17)
            spans.append((cursor,band_end))
        for sa,sb in spans:
            box('Upper window band',(side*1.225,2.86,(sa+sb)/2),(.09,.47,sb-sa),BLACK,17)
        box('Eave structural rail',(side*1.195,3.10,(start+band_end)/2),(.13,.20,band_end-start),RED,0,bevel=.045)
        box('Inside window sill',(side*1.17,1.17,mid),(.14,.11,length),PANEL,0,bevel=.024)
        cuts=sorted([start,end]+[d+s*.65 for d in localdoors for s in (-1,1)])
        for a,b in zip(cuts,cuts[1:]):
            if b-a<.065 or any(abs((a+b)/2-d)<.65 for d in localdoors):continue
            count=max(1,round((b-a)/1.23))
            for i in range(count):
                p=a+(b-a)*i/count;q=a+(b-a)*(i+1)/count
                box('Fixed side pane',(side*1.232,1.925,(p+q)/2),(.010,1.48,q-p-.046),GLASS,17,bevel=.008)
                box('Window mullion',(side*1.242,1.91,p),(.05,1.56,.040),BLACK,17,bevel=.008)
                box('Cabin inner window jamb',(side*1.176,1.92,p),(.075,1.58,.050),PANEL,0,bevel=.01)
    roof_end=7.35 if which=='front' else end
    box(which+' roof',(0,3.105,(start+roof_end)/2),(2.34,.18,roof_end-start),RED,0,bevel=.072)
    box(which+' interior ceiling',(0,2.94,mid),(1.87,.065,length-.06),PANEL,14,bevel=.028)
    for side in (-1,1):
        box('Air-conditioning duct',(side*1.00,2.82,mid),(.37,.22,length-.06),PANEL,14,bevel=.055)
        box('Saloon longitudinal lamp',(side*.79,2.798,mid),(.067,.015,length-.18),INTERIOR,14)
        for j in range(int(length/.21)):
            box('HVAC vent',(side*.98,2.697,start+.14+j*.21),(.19,.008,.018),DARK,14,lod=0)
    for a in range(math.ceil(start),math.floor(end),2):
        if which=='front' and a>6.5:continue
        box('Roof coloured graphic',(0,3.204,a), (2.28,.006,1.05), [WHITE,YELLOW,BLUE][abs(a)%3],18)
    box('Roof HVAC enclosure',(0,3.23,mid-.35),(1.58,.15,2.45),PANEL,16,bevel=.06)
    for side in (-1,1):
        for j in range(12):box('HVAC fin',(side*.56,3.312,mid-1.31+j*.16),(.23,.013,.027),DARK,16,lod=0)

def door(index,z):
    select_section('rear' if index==2 else 'front')
    localindex=0 if index==2 else index
    box('Door overhead mechanism',(1.09,2.70,z),(.24,.22,1.33),DARK,14,bevel=.028)
    # the door header housing: flush with the black window band over a door under the band, a little inside the cap return at the front door
    if localindex==0 and index==0:
        box('Door header housing',(1.236,2.6975,z),(.064,.145,1.44),HEADER,0,bevel=.012)
    else:
        box('Door header housing',(1.225,2.6975,z),(.09,.145,1.44),HEADER,0,bevel=.012)
    box('Yellow boarding threshold',(1.145,.34,z),(.23,.027,1.25),YELLOW,0,bevel=.01)
    for sign in (-1,1):
        box('Door fixed jamb',(1.207,1.47,z+sign*.642),(.12,2.30,.043),PANEL,0,bevel=.012)
        zz=z+sign*.61;part=4+2*localindex+(1 if sign<0 else 0)
        pivot=empty(f'Door {index+1} leaf {sign} pivot',(1.23,.35,zz),PARENT,part)
        leafmid=z+sign*.307
        for yy in (.40,1.13,2.57):box('Door horizontal frame',(1.24,yy,leafmid),(.045,.055,.591),BLACK,part,pivot)
        for dz in (-.292,.292):box('Door vertical frame',(1.24,1.485,leafmid+dz),(.055,2.20,.045),BLACK,part,pivot)
        box('Door upper glass',(1.243,1.875,leafmid),(.01,1.34,.52),GLASS,part,pivot,bevel=.008)
        box('Door lower glass',(1.243,.75,leafmid),(.01,.62,.52),GLASS,part,pivot,bevel=.007)
        path('Door slanted grab handle',[(1.15,.73,leafmid-sign*.21),(1.09,.76,leafmid-sign*.21),(1.09,1.35,leafmid+sign*.19),(1.15,1.38,leafmid+sign*.19)],.013,METAL,part,pivot)
        for yy,dz in ((.75,-sign*.21),(1.36,sign*.19)):box('Handle mounting foot',(1.173,yy,leafmid+dz),(.069,.051,.040),DARK,part,pivot,lod=1)
        path('Door top actuation link',[(1.10,2.66,z+sign*.48),(1.02,2.63,leafmid),(1.14,2.62,leafmid-sign*.18)],.014,METAL,part,pivot,lod=1)
        annotate_animation(pivot,2,[(1,0),(45,0),(85,-sign*85),(115,-sign*85),(135,0)])
    door_records.append({'index':index,'centre_unity':[1.23,1.5,z],'clear_width_m':1.2,'section':SECTION,'leaf_count':2})

section_shell('front',FRONT_JOIN,8.50,DOORS[:2]);section_shell('rear',-8.68,REAR_JOIN,DOORS[2:])
for index,z in enumerate(AXLES):
    for side in (-1,1):wheel(z,index,side)
for i,z in enumerate(DOORS):door(i,z)

# Front geometry shares a measured, continuous envelope.
exec(compile((Path(__file__).with_name('front_geometry.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('front_geometry.py')), 'exec'))
exec(compile((Path(__file__).with_name('lamps.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('lamps.py')), 'exec'))
build_nose_refined()

# Rear face, glazing and high-mounted lamp housings.
select_section('rear')
box('Rear lower body',(0,.77,-8.75),(2.43,.96,.12),RED,0,bevel=.07)
box('Rear destination surround',(0,2.45,-8.78),(2.29,1.11,.06),BLACK,17,bevel=.09)
box('Rear window',(0,1.97,-8.823),(2.10,.75,.012),GLASS,17,bevel=.045)
box('Rear upper cap',(0,3.02,-8.77),(2.32,.29,.12),RED,0,bevel=.095)
box('Rear route housing',(0,2.74,-8.9055),(1.35,.25,.035),DARK,14,bevel=.006)
text('Rear route preview','K18',(0,2.74,-8.9325),.17,LED,front=False)
for side in (-1,1):
    box('Rear lamp surround',(side*1.08,.97,-8.83),(.18,.81,.07),BLACK,16,bevel=.059)
    for yy,ma,part in ((1.23,BRAKE,25),(.99,TAIL,24),(.75,AMBER if side<0 else AMBER_RIGHT,26 if side<0 else 27),(.58,REVERSE,28)):
        box('Rear lens',(side*1.08,yy,-8.875),(.115,.14,.026),ma,part,bevel=.035)
box('Rear bumper',(0,.285,-8.79),(2.38,.16,.19),DARK,0,bevel=.049)

# Cab: driver left, reachable controls and no duplicated runtime card reader.
select_section('front')
box('Dashboard lower console',(-.43,.87,8.03),(1.43,.62,.55),DARK,11,bevel=.13)
box('Instrument hood',(-.79,1.26,7.93),(.68,.21,.32),DARK,11,bevel=.07)
box('LCD instrument',(-.79,1.27,7.744),(.46,.14,.014),BLACK,11)
box('Driver cushion',(-.79,.85,7.02),(.49,.14,.48),DARK,12,bevel=.06)
box('Driver seat back',(-.79,1.30,6.78),(.49,.84,.16),DARK,12,bevel=.09)
box('Driver suspension pedestal',(-.79,.56,7.02),(.33,.40,.31),DARK,12,bevel=.035)
rod('Steering column',(-.79,.80,7.74),(-.79,1.17,7.60),.035,DARK,11)
steer=empty('Steering wheel pivot',(-.79,1.20,7.61),PARENT,1)
steer['rest_rotation_unity']=[math.sin(math.radians(55)/2),0,0,math.cos(math.radians(55)/2)]
pts=[(-.79+.205*math.cos(j*2*math.pi/36),1.20+.082*math.sin(j*2*math.pi/36),7.61+.188*math.sin(j*2*math.pi/36)) for j in range(37)]
path('Steering rim',pts,.020,DARK,1,steer)
for a in (0,2.1,4.2):rod('Steering spoke',(-.79,1.20,7.61),(-.79+.185*math.cos(a),1.20+.077*math.sin(a),7.61+.174*math.sin(a)),.024,METAL,1,steer)
for xx in (-.89,-.62):box('Driver pedal',(xx,.44,7.77),(.11,.038,.24),DARK,12,bevel=.012)
box('Driver footwell floor',(-.77,.33,7.70),(.79,.055,1.05),FLOOR,0)
box('Driver rear partition',(-.66,.75,6.48),(1.16,.85,.06),PANEL,12,bevel=.025)
box('Driver protective partition glass',(-.66,1.70,6.47),(1.08,1.02,.01),GLASS,12)
box('Driver side controls',(-1.00,1.015,7.30),(.25,.28,.77),DARK,11,bevel=.045)
for i in range(8):box('Driver switch',(-.98,1.16,7.05+i*.07),(.08,.012,.044),BLACK,11,lod=0)
empty('driver_eye',(-.79,1.97,7.62),PARENT)

build_mirrors_refined()

def seat(x,z,raised=False,priority=False):
    select_section('front' if z>-1.5 else 'rear')
    base=1.105 if raised else .34;y=base+.45
    ma=PRIORITY if priority else SEAT
    box('Passenger seat cushion',(x,y,z),(.42,.12,.445),ma,10,bevel=.055)
    back=box('Passenger seat back',(x,y+.40,z-.245),(.42,.72,.085),ma,10,bevel=.046)
    box('Seat lower shell',(x,y-.068,z),(.435,.055,.46),DARK,10,bevel=.025)
    for xx in (x-.16,x+.16):rod('Seat leg',(xx,base,z-.10),(xx,y-.07,z-.10),.022,METAL,10)
    path('Seat back grab handle',[(x-.18,y+.66,z-.25),(x-.18,y+.85,z-.25),(x+.18,y+.85,z-.25),(x+.18,y+.66,z-.25)],.019,METAL,10)
    if raised:
        box('Seat front foot platform',(x,base-.02,z+.34),(.46,.045,.28),FLOOR,0)
    seat_records.append({'section':SECTION,'cushion_unity':[x,y,z],'foot_floor_y':base,'raised':raised})

# Explicit seat rows with clear door bays, aisle and articulation passage.
for side in (-1,1):
    for z in (5.55,4.77,3.99,.30,-.47,-2.75,-3.53,-4.31,-6.38,-7.17,-8.04):
        if side>0 and any(abs(z-d)<1.02 for d in DOORS):continue
        if z>6.1:continue
        raised=any(abs(z-a)<.80 for a in AXLES)
        for x in (side*.49,side*.95):seat(x,z,raised,priority=(z==3.99 and side<0))

# Handrail layout is bounded by actual seating and door spaces, not repeated portals.
for which,start,end in (('front',FRONT_JOIN+.10,6.35),('rear',-7.10,REAR_JOIN-.10)):
    select_section(which)
    for side in (-1,1):
        rod('Upper longitudinal handrail',(side*.57,2.18,start),(side*.57,2.18,end),.017,METAL,13)
        for zz in ([5.9,3.3,.85,-.7] if which=='front' else [-2.4,-4.4,-6.8]):
            # Visible manufacturer interior: each rail turns out toward its
            # roof fixing, with a rounded shoulder rather than a thin hanger.
            pts=[(side*.57,2.18,zz),(side*.57,2.60,zz)]
            for j in range(1,9):
                a=math.pi*.5*j/8
                pts.append((side*(.57+.22*(1-math.cos(a))),2.60+.22*math.sin(a),zz))
            pts.append((side*.79,2.91,zz))
            path('Suspended rail ceiling anchor',pts,.017,METAL,13)
        for zz in ([4.95,4.12,3.30,2.48,1.66,.84] if which=='front' else [-2.6,-3.45,-4.3,-6.7]):
            part=34+len([s for s in strap_records if s['section']==which]);p=empty('Strap pivot',(side*.57,2.18,zz),PARENT,part)
            rod('Short handgrip attachment',(side*.57,2.18,zz),(side*.57,2.128,zz),.016,HANDGRIP,part,p)
            # One closed swept oval (torus-like ring of 28 sections), not 28 capped rod segments;
            # the authored-LOD pass halves its radial sampling for LOD1 and omits straps in LOD2.
            gv=[];gf=[];gn=10
            for j in range(28):
                a=j*2*math.pi/28;c=Vector((side*.57+.063*math.cos(a),2.015+.115*math.sin(a),zz))
                t=Vector((-.063*math.sin(a),.115*math.cos(a),0)).normalized();w=Vector((0,0,1));r=t.cross(w)
                gv+=[tuple(c+.013*(r*math.cos(2*math.pi*k/gn)+w*math.sin(2*math.pi*k/gn))) for k in range(gn)]
            gf=[(j*gn+k,j*gn+(k+1)%gn,((j+1)%28)*gn+(k+1)%gn,((j+1)%28)*gn+k) for j in range(28) for k in range(gn)]
            grip=mesh('Oval passenger hand grip',gv,gf,HANDGRIP,part,p)
            grip['radial_segments']=gn;grip['radial_rings']=28
            for poly in grip.data.polygons:poly.use_smooth=True
            strap_records.append({'section':which,'part':part,'pivot_unity':[side*.57,2.18,zz]})
    for zz in ([6.65,2.93,1.37] if which=='front' else [-6.75]):
        rod('Door bay vertical stanchion',(.59,.34,zz),(.59,2.18,zz),.018,METAL,13)
        box('Stop request button',(.565,1.21,zz),(.04,.08,.065),RED,13,bevel=.012,lod=1)

# Shared reader comes from the existing CDLC generated mesh, with no copied simplified housing.
select_section('front')
reader_path=ROOT/'ModelSource/SWB6109EV12G/common-card-reader.json'
if reader_path.exists():
    reader=json.loads(reader_path.read_text(encoding='utf-8'))
    entry=reader['lods'][0]['parts'][0]
    positions=entry['positions'];indices=entry['indices'];old=Vector(reader['readers'][0]['originUnity']);target=Vector((.78,1.20,6.97))
    points=[Vector(p)-old+target for p in positions]
    ob=mesh('CDLC common contactless validator',points,[tuple(indices[i:i+3]) for i in range(0,len(indices),3)],DARK,12)
    ob['runtime_generated']='CardReader';ob['shared_source_sha256']=hashlib.sha256(reader_path.read_bytes()).hexdigest()
    # Preserve shared per-face finish colours using the generator's vertex colour data.
    if 'uv' in entry:
        uv=ob.data.uv_layers.new(name='Shared reader UV')
        for poly in ob.data.polygons:
            for li in poly.loop_indices:uv.data[li].uv=entry['uv'][ob.data.loops[li].vertex_index]
    reader_mats={}
    for poly in ob.data.polygons:
        cols=[entry['colors'][vi] for vi in poly.vertices]
        rgb=tuple(round(sum(c[k] for c in cols)/len(cols),3) for k in range(3))
        if rgb not in reader_mats:
            ma=mat('Shared validator colour '+str(len(reader_mats)),rgb,.5,0,'CabinGraphite')
            ob.data.materials.append(ma);reader_mats[rgb]=len(ob.data.materials)-1
        poly.material_index=reader_mats[rgb]
    rod('Validator support',(.78,.34,6.97),(.78,1.08,6.97),.023,METAL,12)

# Flexible annular bellows: no opaque polygon closes the walk-through aperture.
select_section('joint')
BELLOWS_ROWS=49          # 24 pleats (photo: about 24 fine vertical folds); the export keeps crest/valley pairs per LOD
def bellows_vertices(angle=0):
    result=[];count=BELLOWS_ROWS;around=56
    for inside in (False,True):
        for j in range(count):
            t=j/(count-1);zz=FRONT_JOIN+(REAR_JOIN-FRONT_JOIN)*t;ripple=.026*math.cos(j*math.pi)
            hw=(1.19 if not inside else 1.12)+ripple;bottom=.27 if not inside else .34;top=3.09 if not inside else 2.98
            for k in range(around):
                a=2*math.pi*k/around
                # Rounded rectangle, matched inner/outer wall topology.
                xx=hw*math.copysign(abs(math.cos(a))**.20,math.cos(a));yy=(top+bottom)/2+(top-bottom)/2*math.copysign(abs(math.sin(a))**.20,math.sin(a))
                p=Vector((xx,yy,zz));az=math.radians(angle)*t;dx,dz=p.x,p.z-JOINT
                result.append((math.cos(az)*dx+math.sin(az)*dz,p.y,JOINT-math.sin(az)*dx+math.cos(az)*dz))
    return result
verts=bellows_vertices();n=56;rows=BELLOWS_ROWS;half=n*rows;faces=[]
for inside in (0,1):
    for j in range(rows-1):
        for k in range(n):
            p=inside*half+j*n+k;q=inside*half+j*n+(k+1)%n
            face=(p,q,q+n,p+n);faces.append(tuple(reversed(face)) if inside else face)
for j in (0,rows-1):
    for k in range(n):
        p=j*n+k;q=j*n+(k+1)%n;faces.append((p,q,q+half,p+half))
bellows=mesh('Flexible double-wall articulation bellows',verts,faces,STEEL,0)
bellows.shape_key_add(name='Straight')
joint_poses={1:0,130:0,160:25,190:-25,220:0,250:50,280:-50,310:0}
for name,angle in (('Left bend 25',25),('Right bend 25',-25),('Left bend 50',50),('Right bend 50',-50)):
    key=bellows.shape_key_add(name=name)
    for point,pos in zip(key.data,bellows_vertices(angle)):point.co=U@Vector(pos)
    for frame,pose in joint_poses.items():key.value=1. if pose==angle else 0.;key.keyframe_insert('value',frame=frame)
rod('Articulation anti-slip turntable',(0,.29,JOINT),(0,.335,JOINT),.84,FLOOR,0,n=64)
for z in (FRONT_JOIN-.12,REAR_JOIN+.12):
    box('Articulation floor threshold',(0,.325,z),(1.40,.035,.32),FLOOR,0,parent=front_root if z>JOINT else rear_root)
for side in (-1,1):
    for zz in (FRONT_JOIN,REAR_JOIN):
        on=front_root if zz>JOINT else rear_root
        box('Bellows attachment frame',(side*1.17,1.70,zz),(.065,2.71,.065),METAL,0,parent=on,bevel=.014)
        rod('Articulation rail upright',(side*.94,.34,zz),(side*.94,1.15,zz),.025,METAL,0,parent=on)
    rail=rod('Articulation folding handrail',(side*.94,1.15,FRONT_JOIN),(side*.94,1.15,REAR_JOIN),.025,METAL,0,n=12)
    rail.shape_key_add(name='Straight')
    for label,angle in (('Left25',25),('Right25',-25),('Left50',50),('Right50',-50)):
        key=rail.shape_key_add(name=label)
        for i,p in enumerate(rail.data.vertices):
            pt=U.inverted()@p.co;t=max(0,min(1,(FRONT_JOIN-pt.z)/(FRONT_JOIN-REAR_JOIN)));a=math.radians(angle)*t
            key.data[i].co=U@Vector((math.cos(a)*pt.x+math.sin(a)*(pt.z-JOINT),pt.y,JOINT-math.sin(a)*pt.x+math.cos(a)*(pt.z-JOINT)))
        for frame,pose in joint_poses.items():key.value=1. if pose==angle else 0.;key.keyframe_insert('value',frame=frame)
annotate_animation(rear_root,2,[(frame,-angle) for frame,angle in joint_poses.items()])
scene.frame_start=1;scene.frame_end=310;scene.render.fps=30;scene.frame_set(1)

def build_explicit_lods():
    """Whole-part omission and analytic radial sampling; no arbitrary Decimate."""
    for collection in (FRONT,REAR):
        for original in list(collection.objects):
            if original.type!='MESH' or original.data.shape_keys or 'first_lod' in original:continue
            if original.get('runtime_generated')=='CardReader':original['last_lod']=0;continue
            if any(m and m.get('glass') for m in original.data.materials):continue
            last=int(original.get('last_lod',2));original['last_lod']=0
            for level in range(1,last+1):
                name=original.name.lower();part=int(original.get('cdlc_part',0))
                if level==2:
                    if part>=34 or part in (1,2,3,11,12,13,15):continue
                    if part==10 and not any(s in name for s in ('seat cushion','seat back','lower shell','seat leg')):continue
                    if part==14 and not any(s in name for s in ('interior ceiling','longitudinal lamp','route housing','destination housing')):continue
                    if any(s in name for s in ('air-conditioning duct','seat front foot','wheel arch edge','wiper','nut','vent','fin','switch','projector')):continue
                clone=original.copy();clone.data=original.data.copy();collection.objects.link(clone)
                clone.name='LOD'+str(level)+' '+original.name;clone['first_lod']=level;clone['last_lod']=level
                clone.hide_render=True;clone.hide_set(True)
                for modifier in list(clone.modifiers):
                    if modifier.type in ('BEVEL','WEIGHTED_NORMAL'):clone.modifiers.remove(modifier)
                n=int(clone.get('radial_segments',0));rings=int(clone.get('radial_rings',0))
                if n and rings and len(clone.data.vertices)==n*rings:
                    factor=4 if level==2 and n%4==0 and n//4>=6 else 2
                    if n%factor==0 and n//factor>=5:
                        nn=n//factor;v=[tuple(clone.data.vertices[k*n+j*factor].co) for k in range(rings) for j in range(nn)]
                        f=[]
                        for k in range(rings if rings>2 else 1):
                            kk=(k+1)%rings
                            for j in range(nn):f.append((k*nn+j,k*nn+(j+1)%nn,kk*nn+(j+1)%nn,kk*nn+j))
                        if rings==2:f += [tuple(reversed(range(nn))),tuple(range(nn,2*nn))]
                        data=bpy.data.meshes.new(clone.name);data.from_pydata(v,[],f);data.update()
                        for material in clone.data.materials:data.materials.append(material)
                        bm=bmesh.new();bm.from_mesh(data);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
                        if bm.calc_volume(signed=True)<0:bmesh.ops.reverse_faces(bm,faces=list(bm.faces))
                        bm.to_mesh(data);bm.free();clone.data=data
exec(compile((Path(__file__).with_name('rear_refinement.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('rear_refinement.py')), 'exec'))
exec(compile((Path(__file__).with_name('interior_completion.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('interior_completion.py')), 'exec'))
build_interior_completion(globals())
exec(compile((Path(__file__).with_name('saloon_refinement.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('saloon_refinement.py')), 'exec'))
build_k18_saloon_refinement(globals())
build_k18_rear_details(globals())
exec(compile((Path(__file__).with_name('side_refinement.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('side_refinement.py')), 'exec'))
build_k18_side_refinement(globals())
exec(compile((Path(__file__).with_name('exterior_completion.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('exterior_completion.py')), 'exec'))
build_k18_exterior_completion()
exec(compile((Path(__file__).with_name('badges.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('badges.py')), 'exec'))
build_k18_badges()
exec(compile((Path(__file__).with_name('cab_refinement.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('cab_refinement.py')), 'exec'))
build_k18_cab_refinement(globals())
# The optical calibration reads the final cabin eye and rotates each complete
# mirror head before authored LOD generation.
exec(compile((Path(__file__).with_name('mirror_refinement.py')).read_text(encoding='utf-8'), str(Path(__file__).with_name('mirror_refinement.py')), 'exec'))
build_explicit_lods()

# Source file contains only original geometry plus the documented shared validator.
scene['model']='CRRC_K18';scene['status']='WORK_IN_PROGRESS_NOT_GAME_RELEASE'
scene['coordinate_contract']='Blender +X driver side; -Y front; +Z up. Unity=(-Bx,Bz,-By).'
scene['assembly_note']='Front/rear remain separate; articulation is not encoded as a passenger strap.'
scene['reference_note']='2024 new Bakelong K18 promotional reference; vehicle announcement code and concealed details not yet confirmed.'
scene['review_frames']='1 closed straight; 85 doors open; 160 articulation left25; 190 right25; 250 left50; 280 right50'

# Simple clean studio; never exported as a vehicle part.
CURRENT=STUDIO;PARENT=None;SECTION='studio'
ground=box('Studio ground',(0,-.08,0),(70,.10,70),mat('Studio grey',(.24,.28,.30),.85),0)
world=bpy.data.worlds.new('Neutral studio environment');world.use_nodes=True;world.node_tree.nodes['Background'].inputs[0].default_value=(.58,.66,.74,1);world.node_tree.nodes['Background'].inputs[1].default_value=.48;scene.world=world
def area(name,pos,power,size,target=(0,1,0)):
    data=bpy.data.lights.new(name,'AREA');data.energy=power;data.shape='DISK';data.size=size
    ob=bpy.data.objects.new(name,data);STUDIO.objects.link(ob);ob.location=U@Vector(pos);ob.rotation_euler=((U@Vector(target))-ob.location).to_track_quat('-Z','Y').to_euler()
area('Key softbox',(8,11,7),3200,10);area('Fill softbox',(-7,7,-1),2400,9);area('Rim softbox',(0,10,-12),3000,8)
data=bpy.data.cameras.new('Review Camera');cam=bpy.data.objects.new('Review Camera',data);STUDIO.objects.link(cam);scene.camera=cam
scene.render.engine='CYCLES';scene.cycles.samples=24;scene.cycles.use_denoising=True
scene.render.resolution_x=1600;scene.render.resolution_y=900;scene.render.resolution_percentage=100
scene.view_settings.view_transform='AgX';scene.render.image_settings.file_format='PNG'
def camera(pos,target,lens=48):
    cam.location=U@Vector(pos);cam.rotation_euler=((U@Vector(target))-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.lens=lens
camera((20,9,23),(0,1.5,0),48)
scene.frame_set(1)
bpy.ops.wm.save_as_mainfile(filepath=str(SRC/'CRRC_K18.blend'))
report={
    'status':'SOURCE_GENERATED_GAME_VALIDATION_PENDING',
    'model':'CRRC_K18',
    'scope':'Source generation only; export, package and game evidence are recorded separately in construction-status.json.',
    'dimensions_m':{'length':L,'width':W,'height_estimate':H},
    'dimensions_status':{'length':'2024 promotional publication','width':'2024 promotional publication','height':'unconfirmed estimate','axles':'photo estimate','joint':'photo estimate'},
    'driver_side':'left','doors':door_records,'wheels':wheel_records,
    'seats':seat_records,'straps':strap_records,
    'source_objects':sum(o.type=='MESH' for c in (FRONT,REAR,JOINTCOL) for o in c.objects),
    'runtime_profile_version':2,
    'articulation_support':'Authored front/rear profiles and independent CJA1 joint are implemented; actual AI, driving and save/reload validation remains required.',
    'game_packaged':False,
    'game_packaged_scope':'This source-generation invocation does not create or validate a COK package.',
    'game_validated':False,
    'blocking_issues':['Game acceptance is pending; source generation alone is not a release result.'],
    'reference_limitations':['Vehicle announcement code, exact rear demonstration livery and complete seat map are not fully confirmed.'],
    'shared_card_reader_used':reader_path.exists(),
}
(SRC/'build-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
views=[('front-right',(20,9,23),(0,1.5,0),48,1),('rear-left',(-17,7,-21),(0,1.4,-1),48,1),('right-side',(28,5,0),(0,1.3,0),48,1),('doors-open',(15,5,18),(0,1.4,1),45,85),('articulated',(18,13,22),(0,1.4,-1),48,160),('saloon',(0,1.72,-7.9),(0,1.68,4.8),20,1),('driver',(.47,1.95,6.42),(-.73,1.37,7.84),23,1)]
if '--render' in sys.argv:
    for name,pos,target,lens,frame in views:
        scene.frame_set(frame);camera(pos,target,lens);scene.render.filepath=str(SRC/'previews'/f'{name}.png');bpy.ops.render.render(write_still=True)
scene.frame_set(1);camera((20,9,23),(0,1.5,0),48)
bpy.ops.wm.save_as_mainfile(filepath=str(SRC/'CRRC_K18.blend'))
print('K18_SOURCE_BUILT '+json.dumps({'objects':report['source_objects'],'file':str(SRC/'CRRC_K18.blend')}),flush=True)
