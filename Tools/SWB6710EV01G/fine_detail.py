"""Manufactured details and cabin lining for the iEV7 editable master."""
# Interior wall skins: real reversed inner surfaces, not single-sided exterior.
for original in list(MASTER.objects):
    if original.type!='MESH':continue
    lower=original.name.lower()
    # The thin leaf frame is visible from both sides when the door swings in.
    # Copying the object preserves its hinge parent and parent inverse; the rig
    # assigned below therefore stamps this face with the same moving part.
    is_door_frame=lower.startswith('door ') and ' leaf ' in lower and 'pivot-ready' in lower
    if is_door_frame:
        data=original.data.copy()
        for v in data.vertices:v.co-=v.normal*.006
        data.flip_normals()
        obj=original.copy();obj.data=data
        obj.name='Inside door frame | '+original.name
        MASTER.objects.link(obj)
        obj['cdlc_backface_of']=original.name
        # Close both the outside perimeter and the real glazing aperture.
        # An inward parallel sheet alone is missed by oblique sight lines at
        # a frame edge. Keep the return in the same local frame and hinge
        # parent so all three pieces move as one door leaf.
        edges={}
        for poly in original.data.polygons:
            row=list(poly.vertices)
            for a,b in zip(row,row[1:]+row[:1]):
                key=tuple(sorted((a,b)))
                count,oriented=edges.get(key,(0,(a,b)))
                edges[key]=(count+1,oriented)
        count=len(original.data.vertices)
        vertices=[tuple(v.co) for v in original.data.vertices]+[tuple(v.co) for v in data.vertices]
        faces=[(a,a+count,b+count,b) for uses,(a,b) in edges.values() if uses==1]
        return_data=bpy.data.meshes.new('Closed door frame edges | '+original.name)
        return_data.from_pydata(vertices,[],faces);return_data.update()
        for mat in original.data.materials:return_data.materials.append(mat)
        returns=original.copy();returns.data=return_data
        returns.name='Door frame thickness returns | '+original.name
        MASTER.objects.link(returns)
        returns['cdlc_thickness_join']=original.name
        continue
    is_black_frit=any(s in lower for s in ('ceramic window frit','flush glazing frit','continuous black panoramic frit','continuous black eyebrow',
        'rear | curved black destination and window frit','rear | window epdm gasket'))
    is_shell=original.name.startswith('Aqua side quarter panels') or original.name=='Continuous low rounded roof' or any(s in lower for s in ('curved front corner shoulder','matched nose to roof','matched lower bumper','raked aqua nose and crown'))
    if is_shell or is_black_frit:
        data=original.data.copy()
        for v in data.vertices:
            if original.name=='Continuous low rounded roof':v.co.z-=.105
            elif original.name.startswith('Aqua side quarter panels'):v.co.x-=math.copysign(.043,v.co.x)
            elif original.name.startswith('Curved front corner shoulder'):
                # Keep the lining inside even at the tightly rolled bottom
                # corner, where averaged normals can cross the outer skin.
                v.co.x-=math.copysign(.035,v.co.x)
                v.co.y+=.025
                v.co.z+=.025*max(0.,1.-(v.co.z-.20)/.25)
            elif 'continuous black panoramic frit' in lower:
                v.co.x*=.988
                v.co.y+=.025
            elif any(s in lower for s in ('rear | curved black destination and window frit','rear | window epdm gasket')):
                v.co.y-=.022
            elif 'matched nose to roof' in lower:
                v.co.x*=.995
                v.co.y+=.015
                v.co.z-=.045
            elif 'raked aqua nose and crown' in lower:
                # Preserve the aperture footprint. A vertex-normal offset
                # moved this narrow annulus into the glass opening and folded
                # its left inner edge. +Y stays inside the front graph at
                # every point; the paired returns below close the depth.
                v.co.y+=.028
            elif 'continuous black eyebrow' in lower:
                v.co.x-=math.copysign(.035,v.co.x)
                v.co.z-=.015
            elif v.co.y>2.80 and any(s in lower for s in ('upper ceramic window frit','flush glazing frit')):
                # The rear outline is triangulated after clipping. Tiny edge
                # polygons can give averaged vertex normals an inward/upward
                # component; a normal offset then pushed the liner through
                # the outer frit. These side panes have an exact X-plane, so
                # put their rear liner explicitly inside that plane.
                v.co.x-=math.copysign(.022,v.co.x)
            else:v.co-=v.normal*.018
        data.flip_normals()
        data.materials.clear();data.materials.append(plastic if is_black_frit else panel)
        obj=bpy.data.objects.new('Interior lining | '+original.name,data);MASTER.objects.link(obj)
        obj['cdlc_part']=0
        if is_shell or is_black_frit:
            # Join every genuine skin boundary to its matching inner trim.
            # A shifted parallel liner alone leaves its reverse exposed at
            # oblique views through a window or around a panel edge. Count
            # geometric edges so separate strip vertices do not create false
            # internal walls between neighbouring quarter-panel quads.
            edges={}
            for poly in original.data.polygons:
                row=list(poly.vertices)
                for a,b in zip(row,row[1:]+row[:1]):
                    key=tuple(sorted(tuple(round(c,6) for c in original.data.vertices[i].co) for i in (a,b)))
                    if key[0]==key[1]:continue
                    count,oriented=edges.get(key,(0,(a,b)))
                    edges[key]=(count+1,oriented)
            vertices=[];faces=[]
            for count,(a,b) in edges.values():
                if count!=1:continue
                base=len(vertices)
                vertices.extend((tuple(original.data.vertices[a].co),tuple(data.vertices[a].co),
                                 tuple(data.vertices[b].co),tuple(original.data.vertices[b].co)))
                faces.append(tuple(base+i for i in range(4)))
            prefix='Front aperture inner returns | ' if 'continuous black panoramic frit' in lower else 'Skin boundary returns | '
            returns=mesh(prefix+original.name,vertices,faces,plastic if is_black_frit else panel)
            returns['cdlc_part']=0
            returns['cdlc_thickness_join']=original.name
for side in [-1,1]:
    for ay in AXLES:
        inner_x=.630 if ay==AXLES[0] else .744
        box('Inner wheelhouse wall',(side*inner_x,ay,.568),(.038,.84,.47),panel,.025)

# A real front wheelhouse allows the tyre's steering sweep while keeping the
# saloon closed above it. Its outer opening remains the authored wheel arch.
for side in (-1,1):
    ay=AXLES[0];radius=R+.065;steps=56
    xs=sorted((side*.610,side*(SIDE-.013)))
    verts=[(x,ay+radius*math.cos(math.pi*i/steps),R+radius*math.sin(math.pi*i/steps))
           for x in xs for i in range(steps+1)]
    faces=[(i,steps+1+i,steps+2+i,i+1) for i in range(steps)]
    arch=mesh('Front inner wheelhouse arch '+str(side),verts,faces,plastic)
    arch['cdlc_part']=0
    for face in arch.data.polygons:face.use_smooth=True
    arch.data.materials.append(panel)
    gauge=arch.modifiers.new('Wheelhouse real skin thickness','SOLIDIFY')
    gauge.thickness=.010;gauge.offset=-1;gauge.material_offset=1
    cap=[(side*.610,ay,R)]+[(side*.610,ay+radius*math.cos(math.pi*i/steps),R+radius*math.sin(math.pi*i/steps)) for i in range(steps+1)]
    cap_faces=[]
    for i in range(1,steps+1):
        f=(0,i,i+1)
        a,b,c=(Vector(cap[k]) for k in f)
        if (b-a).cross(c-a).x*side<0:f=tuple(reversed(f))
        cap_faces.append(f)
    wall=mesh('Front inner wheelhouse closure '+str(side),cap,cap_faces,plastic)
    wall['cdlc_part']=0
    wall.data.materials.append(panel)
    gauge=wall.modifiers.new('Wheelhouse wall thickness','SOLIDIFY')
    gauge.thickness=.010;gauge.offset=-1;gauge.material_offset=1

# Cab hardware and passenger operating equipment, all below the windscreen.
box('Driver raised floor',(-.53,-2.50,.855),(.93,1.25,.10),floor,.025)
box('Driver partition lower',(-.54,-1.72,.98),(.91,.035,.45),panel,.02)
box('Driver partition glazed',(-.54,-1.716,1.59),(.87,.008,.75),glass,.03)
rod('Driver partition grab',(-.03,-1.70,.36),(-.03,-1.70,2.835),.019,steel)
rod('Driver partition grab ceiling flange',(-.03,-1.70,2.822),(-.03,-1.70,2.837),.035,steel)
for x in [-.62,-.39]:
    pedal=box('Driver brake or accelerator',(x,-2.91,.93),(.095,.16,.035),rubber,.009)
    pedal.rotation_euler.x=math.radians(-25)
box('Driver footrest',(-.80,-2.93,.91),(.13,.21,.05),rubber,.015)
extinguisher=rod('Fire extinguisher',(-.91,-1.93,.52),(-.91,-1.93,.91),.055,redseat,24)
rod('Extinguisher handle',(-.91,-1.93,.91),(-.91,-1.93,.96),.024,plastic,12)
# Emergency hammers on the driver-side window pillars, back on the pillar's
# inner lining (18 mm inside the frit, which the narrowed body moved in:
# taper_shift). They hung 2 cm off the lining, and a second pair floated in
# front of the glass in the middle of two windows (removed).
for y in [-.89,.88,1.77]:
    fx=-(SIDE+.002);fx+=taper_shift(fx,1.95)
    box('Emergency hammer on window pillar',(fx+.018+.020,y,1.95),(.04,.05,.16),stopred,.01)

# Bulged tyre profiles, sidewall emboss rings, valve stems and orange torque
# indicators follow each wheel pivot and roll with it.
torque_orange=material('Wheel torque marker orange',(.93,.23,.03),.52)
for ai,ay in enumerate(AXLES):
    track=SPEC['front_track_m'] if ai==0 else SPEC['rear_track_m']
    for side in [-1,1]:
        bone=('WheelF' if ai==0 else 'WheelR')+('L' if side<0 else 'R')
        parent=bpy.data.objects.get(bone+' pivot')
        before=set(MASTER.objects)
        cx=side*track/2
        profile=[(-.103,.313),(-.101,.350),(-.085,.372),(-.056,.384),(.056,.384),(.085,.372),(.101,.350),(.103,.313)]
        vs=[];fs=[];count=80
        for dx,rad in profile:
            vs += [(cx+dx,ay+rad*math.cos(2*math.pi*i/count),R+rad*math.sin(2*math.pi*i/count)) for i in range(count)]
        for j in range(len(profile)-1):
            for i in range(count):
                a=j*count+i;b=j*count+(i+1)%count;c=(j+1)*count+(i+1)%count;d=(j+1)*count+i
                fs.append((a,b,c,d))
        tyre=mesh(bone+' moulded tyre shoulder',vs,fs,rubber)
        for p in tyre.data.polygons:p.use_smooth=True
        for rad in [.297,.321]:
            tube(bone+' sidewall mould line',[(cx+side*.107,ay+rad*math.cos(t),R+rad*math.sin(t)) for t in [2*math.pi*i/80 for i in range(80)]],.0015,hubdark,True)
        for i in range(8):
            t=2*math.pi*i/8
            yy,zz=ay+R*.34*math.cos(t),R+R*.34*math.sin(t)
            # Lug torque pointers are common visible workshop details.
            rod(bone+' orange lug pointer',(cx+side*.155,yy,zz),(cx+side*.158,yy+.020*math.cos(t),zz+.020*math.sin(t)),.009,torque_orange,6)
        t=2.15
        rod(bone+' air valve',(cx+side*.123,ay+.23*math.cos(t),R+.23*math.sin(t)),
            (cx+side*.155,ay+.23*math.cos(t),R+.23*math.sin(t)),.005,plastic,8)
        for obj in set(MASTER.objects)-before:
            obj['bone']=bone
            if parent:
                obj.parent=parent;obj.matrix_parent_inverse=Matrix.Translation(parent.location).inverted()

# Door seals and the door drive (door_linkage.py; user review 2026-10-15: light through the middle of the shut front door;
# 2026-10-16: no hinges, each leaf runs on a track and opens mostly into the bus, its grab rail towards the doorway). Per
# leaf, in the header cover: a short door shaft above the leaf's jamb end (shut) turning a crank to a pin on the leaf's top
# (the leaf's door drive part DoorDrive0.., after the 34 contract parts: it turns about the shaft's axis), and a roller on
# the leaf's top in a rail along the body. The jamb end runs straight into the bus; a pin under the leaf runs in a slot in
# the step tread. The cover holds the door engines (not modelled inside) and carries the door warning light. The leaves are
# 23 mm narrower than their openings (side_ring); rubber seals close that:
# - the front pair's meeting edges overlap in two layers (A's lip outside, B's seal inside, 2 mm apart); both meeting
#   stiles only move away from the centre as the leaves open (door_linkage.door_check), so they part without touching;
# - every jamb stile closes under a rubber lip on its post, outside (the jamb end only moves into the bus); the rear leaf's
#   meeting stile, which swings out, closes over a lip on the rear door's front post, inside.
# Everything on a leaf follows the narrowed body (taper_shift); the header parts are built on the tapered top coordinates
# the linkage uses.
def fd_strip(name,y0,y1,x0,x1,z0,z1,mat,steps=None):
    """A closed box strip whose X range follows the narrowed body (the shift of its outer face) every 8 cm of height."""
    n=steps or max(2,int((z1-z0)/.08)+1)
    vs=[]
    for i in range(n):
        z=z0+(z1-z0)*i/(n-1);sx=taper_shift(x1,z)
        vs+=[(x0+sx,y0,z),(x1+sx,y0,z),(x1+sx,y1,z),(x0+sx,y1,z)]
    fs=[(i*4+k,i*4+(k+1)%4,(i+1)*4+(k+1)%4,(i+1)*4+k) for i in range(n-1) for k in range(4)]
    fs+=[(3,2,1,0),tuple((n-1)*4+k for k in range(4))]
    obj=mesh(name,vs,fs,mat)
    import bmesh
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces[:])
    bm.to_mesh(obj.data);bm.free()
    return obj

def fd_leaf_x(z,offset=.002):
    """The leaf frame's inner sheet (side_ring at SIDE+.008, its inside copy 6 mm in) on the narrowed body."""
    x=SIDE+offset
    return x+taper_shift(x,z)

FD_SEAL_Z=(DOOR_LEAF_Z[0]+.01,DOOR_LEAF_Z[1]-.01)
def fd_drive(obj, part_pivot, keep=True):
    """Parent a door object to its leaf joint or door shaft pivot (rest pose), keeping its topology in the reduced LODs."""
    obj.parent=part_pivot;obj.matrix_parent_inverse=Matrix.Translation(part_pivot.location).inverted()
    if keep:obj['cdlc_keep_topology']=True
    return obj

for DL in DOOR_LEAVES:
    di,leaf,lw,d=DL['door'],DL['leaf'],DL['w'],DL['d']
    joint=bpy.data.objects[DL['name']+' joint'];shaft=bpy.data.objects[DL['name']+' shaft']
    g=DOOR_LEAF_GAP
    ys=lambda s:DL['jamb_y']+d*s          # along the opening from the leaf's jamb
    span=lambda a,b:(min(a,b),max(a,b))
    before=set(MASTER.objects)
    if di==0:
        line=ys(g+lw+g)                  # the opening's centre
        if leaf==0:   # A: the outer lip over the meeting line
            y0,y1=span(ys(g+lw-.004),line+d*.0035)
            fd_strip(f'Door meeting seal outer lip {di+1}.{leaf+1}',y0,y1,SIDE+.010,SIDE+.022,*FD_SEAL_Z,rubber)
        else:         # B: the inner seal under A's lip
            y0,y1=span(ys(g+lw-.004),line+d*.0035)
            fd_strip(f'Door meeting seal inner {di+1}.{leaf+1}',y0,y1,SIDE-.006,SIDE+.005,*FD_SEAL_Z,rubber)
    else:             # the single leaf's meeting stile: a seal on its end, 2.5 mm short of the post, outside the post's lip
        y0,y1=span(ys(g+lw-.004),ys(g+lw+g-.0025))
        fd_strip(f'Door free edge seal {di+1}.{leaf+1}',y0,y1,SIDE+.001,SIDE+.005,*FD_SEAL_Z,rubber)
    top=DOOR_LEAF_Z[1]
    # the leaf's top fittings: the crank pin's bracket (at the joint) and the roller's (at the guide) from the leaf's top rail
    for name,(px,py),r in (('crank pin',DL['joint'],.007),('roller',DL['guide'],.004)):
        lx=fd_leaf_x(top-.004)
        box(f'Door {name} bracket {di+1}.{leaf+1}',((lx+px)/2,py,top+.0015),(lx-px+.024,.032,.003),steel,.001)
    px,py=DL['joint']
    rod(f'Door crank pin {di+1}.{leaf+1}',(px,py,top+.0015),(px,py,DOOR_CRANK_Z[1]+.001),.006,chrome,12)
    gx,gy=DL['guide']
    rod(f'Door roller stem {di+1}.{leaf+1}',(gx,gy,top+.0015),(gx,gy,DOOR_ROLLER_Z[0]),.004,steel,10)
    rod(f'Door roller {di+1}.{leaf+1}',(gx,gy,DOOR_ROLLER_Z[0]),(gx,gy,DOOR_ROLLER_Z[1]),.010,plastic,16)
    # the jamb end's floor pin, under the leaf's bottom rail into the slot (it runs straight, door_linkage)
    fx,fy=door_point(DL,DL['design'][0],-DL['design'][1],top=False)
    rod(f'Door floor guide pin {di+1}.{leaf+1}',(fx,fy,DOOR_SLOT_Z[1]+.0002),(fx,fy,DOOR_LEAF_Z[0]+.012),.006,steel,12)
    for ob in set(MASTER.objects)-before:fd_drive(ob,joint,keep='seal' not in ob.name)
    # the door drive: the short shaft in the cover, the crank to the pin (the drive part: it turns about the shaft's axis)
    before=set(MASTER.objects)
    sx,sy=DL['shaft']
    rod(f'Door drive shaft {di+1}.{leaf+1}',(sx,sy,DOOR_SHAFT_Z[0]),(sx,sy,DOOR_SHAFT_Z[1]),.012,steel,20)
    zc=(DOOR_CRANK_Z[0]+DOOR_CRANK_Z[1])/2
    box(f'Door drive crank web {di+1}.{leaf+1}',((sx+px)/2,(sy+py)/2,zc),(abs(px-sx)+.012,abs(py-sy)+.012,DOOR_CRANK_Z[1]-DOOR_CRANK_Z[0]),steel,.002)
    rod(f'Door drive crank hub {di+1}.{leaf+1}',(sx,sy,DOOR_CRANK_Z[0]),(sx,sy,DOOR_CRANK_Z[1]),.020,steel,16)
    rod(f'Door drive crank eye {di+1}.{leaf+1}',(px,py,DOOR_CRANK_Z[0]),(px,py,DOOR_CRANK_Z[1]),.014,steel,16)
    for ob in set(MASTER.objects)-before:fd_drive(ob,shaft)
    # the floor slot: from the jamb end's shut place straight into the bus, in the step tread
    run=DL['span']*math.sin(math.radians(DL['open_degrees']))
    box(f'Door floor guide slot {di+1}.{leaf+1}',(fx-run/2,fy,(DOOR_SLOT_Z[0]+DOOR_SLOT_Z[1])/2),(run+.03,.016,DOOR_SLOT_Z[1]-DOOR_SLOT_Z[0]),black,.001)

for di,(cy,dw) in enumerate(DOORS):
    a,b=cy-dw/2,cy+dw/2
    leaves=[DL for DL in DOOR_LEAVES if DL['door']==di]
    # at each jamb a leaf parks at: the post's seal tube, set into the post, and a lip outside over the jamb stile, 1.5 mm outside
    # the leaf frame's outer sheet (side_ring at SIDE+.008), lapping it by 2 mm
    for DL in leaves:
        x=SIDE-.012
        jy=DL['jamb_y']-DL['d']*.006
        tube('Door stationary threshold seal',[(x+taper_shift(x,z),jy,z) for z in [.40+(2.30-.40)*k/24 for k in range(25)]],.008,rubber)
        y0,y1=sorted((DL['jamb_y']-DL['d']*.003,DL['jamb_y']+DL['d']*.0135))
        fd_strip('Door post seal lip',y0,y1,SIDE+.0095,SIDE+.024,*FD_SEAL_Z,rubber)
        y0,y1=sorted((DL['jamb_y']-DL['d']*.003,DL['jamb_y']+DL['d']*.0015))
        fd_strip('Door post seal lip root',y0,y1,SIDE+.0005,SIDE+.0095,*FD_SEAL_Z,rubber)
    if len(leaves)==1:
        # the single leaf's meeting stile swings out: it closes over a lip on the front post, inside (the leaf frame's inside
        # copy at SIDE+.002, 1.5 mm clear)
        fd_strip('Door post inner seal lip',a-.003,a+.0135,SIDE-.008,SIDE+.0005,*FD_SEAL_Z,rubber)
    # the header: the cover over the cranks and the roller rails (from the frit inward over the leaves' open sweep), the rail
    # channel along each leaf's roller line, the warning light on the cover's aisle face
    reach=max(DL['open_jamb_inside'] for DL in leaves)
    xc1=SIDE-.071                     # inside the frit's inner face at the cover's top (2.40 m: 1.110)
    xc0=SIDE-DOOR_TOP_INSET-reach-.05
    zc0,zc1=DOOR_COVER_Z
    box(f'Door {di+1} actuator cover',((xc0+xc1)/2,cy,(zc0+zc1)/2),(xc1-xc0,dw+.10,zc1-zc0),plastic,.012)
    for DL in leaves:
        gx=DL['guide'][0]
        ry=(DL['guide'][1],DL['open_guide'][1])
        r0,r1=min(ry)-.02,max(ry)+.02
        zr0,zr1=DOOR_RAIL_Z
        box(f'Door {di+1} roller channel web',(gx,(r0+r1)/2,zr1-.003),(.036,r1-r0,.006),black,.001)
        for side_x in (gx-.0155,gx+.0155):
            box(f'Door {di+1} roller channel flange',(side_x,(r0+r1)/2,(zr0+zr1)/2),(.005,r1-r0,zr1-zr0),black,.001)
    box('Door opening warning light',(xc0-.010,cy,zc0+.03),(.020,.12,.031),turnR,.006)

# The side route screen between the doors and its number, on the narrowed body:
# the screen's back 2 mm into the frit and its face 15 mm out, every 2 cm of
# height, so it leans in with the body. The live LED sign (vehicle.json signs.side:
# centre x, tilt) sits 2 mm ahead of the face at 2.46 m.
fd_sign_face=taper_real_half(2.46)+.015
fd_sign_tilt=math.atan(.18*(2.46-TAPER_WAIST)+6*.0085*(2.46-TAPER_WAIST)**5)
fd_strip('Right side route screen',.29-.435,.29+.435,SIDE,SIDE+.017,2.365,2.555,black,steps=11)
label('Side route number','42',(fd_sign_face+.002,.29,2.46),.145,led,(math.cos(fd_sign_tilt),0,math.sin(fd_sign_tilt)))
(SRC/'side-sign.json').write_text(json.dumps({'led_centre_unity':[round(fd_sign_face+.002,4),2.46,-.29],
    'tilt_rad':round(fd_sign_tilt,5),'tilt_deg':round(math.degrees(fd_sign_tilt),2),
    'note':'Tools/SWB6710EV01G/vehicle.json signs.side (make_pack.py --sync); the face leans in at the top with the tumblehome'},indent=2),encoding='utf-8')

# Flush compartment seams, tiny fasteners and the coachwork rain gutter.
for side in [-1,1]:
    for y in [-1.9,-.66,.62,1.90,3.17]:
        if any(abs(y-c)<w/2+.08 for c,w in DOORS) and side>0:continue
        if any(abs(y-ay)<.48 for ay in AXLES):continue
        tube('Lower equipment compartment seam',[(side*(SIDE+.003),y,.30),(side*(SIDE+.003),y,.93)],.0025,plastic)
        box('Quarter panel latch',(side*(SIDE+.016),y+.045,.85),(.011,.046,.018),chrome,.003)
    # on the narrowed body: it was laid at the untapered width after taper.py
    # ran and floated 12 cm off the eyebrow the whole length of the bus, a
    # thin dark line in the air on both sides (user review 2026-10-15)
    gx=side*(SIDE+.0065);gx+=taper_shift(gx,2.675)    # on the frit, under the eyebrow's edge
    gutter=tube('Window rain gutter',[(gx,-3.04,2.675),(gx,3.32,2.675)],.0045,rubber)
    gutter.data.use_fill_caps=True
    for y in [-2.0+i*.55 for i in range(10)]:
        if side>0 and any(abs(y-c)<w/2 for c,w in DOORS):continue
        if ss_bottom(y)>.56:continue
        rod('Coachwork countersunk fastener',(side*(SIDE+.002),y,.56),(side*(SIDE+.006),y,.56),.006,chrome,12)

# Fine surface response: subtle rubber/fabric texture, almost imperceptible
# paint texture. It does not replace the geometric edge and panel detail.
for mat,scale,strength,distance in [(rubber,210,.24,.012),(blue,420,.22,.004),(redseat,420,.22,.004),(white,260,.08,.0015)]:
    nodes=mat.node_tree.nodes;links=mat.node_tree.links
    tex=nodes.new('ShaderNodeTexNoise');tex.inputs['Scale'].default_value=scale;tex.inputs['Detail'].default_value=2
    bump=nodes.new('ShaderNodeBump');bump.inputs['Strength'].default_value=strength;bump.inputs['Distance'].default_value=distance
    links.new(tex.outputs['Fac'],bump.inputs['Height']);links.new(bump.outputs['Normal'],nodes['Principled BSDF'].inputs['Normal'])

# GNU Unifont dot patterns are the same font used by CDLC's live destination
# renderer. Each illuminated dot is a real small disc, not extruded type.
import gzip as _detail_gzip, struct as _detail_struct
_font=_detail_gzip.decompress((ROOT/'Fonts/led-font.bin.gz').read_bytes())
_count=_detail_struct.unpack_from('<I',_font,0)[0];_pos=4;_glyphs={}
for _ in range(_count):
    cp,width=_detail_struct.unpack_from('<IB',_font,_pos);_pos+=5
    size=16*(width//8);_glyphs[cp]=(width,_font[_pos:_pos+size]);_pos+=size

def dot_sign(name,text,width,height,map_point):
    glyphs=[_glyphs.get(ord(c),_glyphs.get(ord('?'))) for c in text]
    cols=sum(g[0]+1 for g in glyphs)-1
    sx=width/max(1,cols);sz=height/16;radius=min(sx,sz)*.30
    vs=[];fs=[];cursor=0
    for gw,bits in glyphs:
        for row in range(16):
            for col in range(gw):
                if not bits[row*(gw//8)+col//8]&(0x80>>(col%8)):continue
                x=(cursor+col+.5-cols/2)*sx;z=(7.5-row)*sz
                base=len(vs)
                vs += [map_point(x+radius*math.cos(t),z+radius*math.sin(t)) for t in [2*math.pi*i/8 for i in range(8)]]
                fs.append(tuple(base+i for i in range(8)))
        cursor+=gw+1
    return mesh(name,vs,fs,led)

old=bpy.data.objects.get('iEV7 front amber destination')
if old:bpy.data.objects.remove(old,do_unlink=True)
# Flat display hardware sits behind the single curved windshield. Both the
# housing and this 3 mm proud emitting plane share front_shell.py's clearance
# calculation, so no glyph intersects the glass at its most recessed corner.
dot_sign('iEV7 real LED dots front','42 上海体育馆',*FS_LED_SIZE,
         lambda x,z:(x,FS_LED_PLANE_Y,FS_LED_CENTER_Z+z))

# Convex optical cover in front of the emitting projector; real depth changes
# the highlight compared with a painted white circle.
for side in [-1,1]:
    loc=fs_point(side*.925,.937,.030)
    bpy.ops.mesh.primitive_uv_sphere_add(segments=32,ring_count=16,radius=1,location=loc)
    ob=link(bpy.context.object);ob.name='Projector convex optical glass'
    ob.scale=(.047,.010,.047);assign(ob,glass)
    for poly in ob.data.polygons:poly.use_smooth=True
for ob in MASTER.objects:
    if 'Mirror shell' in ob.name:
        for modifier in ob.modifiers:
            if modifier.type=='BEVEL':modifier.segments=7
