"""Sunwin rounded rear end, executed in build_model.py's globals.

The current iEV7 manufacturer gallery contains a same-family rear photograph
badged iEV8. Its panel/lamp language is used here, while body dimensions and
the model badge remain those of SWB6710EV01G. The rear glazing is a real hole
through the body and black frit, never glass placed over a solid dark panel.
"""


def rear_y(x, z, offset=0.0):
    """A soft crown across the tail, with substantial returns at both pillars."""
    shoulder = max(0.0, min(1.0, (z - 2.38) / max(.2, ROOF - 2.38)))
    foot = max(0.0, min(1.0, (.53 - z) / .28))
    return HALF - .012 - .165 * (abs(x) / SIDE) ** 3 - .175 * shoulder ** 2 - .022 * foot ** 2 + offset


# The tail's large panels and everything paired with them (rings pair outer and inner points by index, so a chain of outlines
# shares one count): their straight edges in 48 mm or finer steps instead of 194 mm. The coarse chords sagged up to 3 mm behind
# the crowned tail and the two shells that meet at 1.585 m (roof/pillars and lower quarter) were triangulated differently, so
# their surfaces stood up to 5 mm apart along the joint; the white U tube hid that, the paint line now lying on it does not.
REAR_FINE_EDGES = 48


def rear_outline(width, bottom, top, top_radius=.08, bottom_radius=.04, cx=0.0, steps=12, edge_steps=None):
    """Consistently sampled outlines, permitting smooth annular patch meshes."""
    edge_steps = steps if edge_steps is None else edge_steps
    half = width / 2
    out = []
    corners = (
        (half-top_radius, top-top_radius, top_radius, 0),
        (-half+top_radius, top-top_radius, top_radius, 90),
        (-half+bottom_radius, bottom+bottom_radius, bottom_radius, 180),
        (half-bottom_radius, bottom+bottom_radius, bottom_radius, 270),
    )
    for k, (center_x, center_z, radius, angle) in enumerate(corners):
        for j in range(steps):
            t = math.radians(angle + 90 * j / steps)
            out.append((cx + center_x + radius * math.cos(t), center_z + radius * math.sin(t)))
        # Straight edges must be subdivided too. Long quads across a bowed
        # panel produce star-shaped specular facets even with smooth normals.
        t = math.radians(angle + 90)
        ax, az = center_x + radius*math.cos(t), center_z + radius*math.sin(t)
        nx, nz, nr, na = corners[(k+1)%4]
        nt = math.radians(na)
        bx, bz = nx+nr*math.cos(nt), nz+nr*math.sin(nt)
        for j in range(edge_steps):
            f=j/edge_steps
            out.append((cx+ax+(bx-ax)*f,az+(bz-az)*f))
    return out


def rear_finish(obj, thickness=0.0):
    for face in obj.data.polygons:
        face.use_smooth = True
    if thickness:
        mod = obj.modifiers.new('Inner skin gauge', 'SOLIDIFY')
        mod.thickness = thickness
        mod.offset = -1
    obj['rear_reference'] = 'Sunwin manufacturer rear-family image 20260804092726_4894; iEV8 badge in reference'
    return obj


def rear_ring(name, outer, inner, mat, offset=0.0, inner_offset=None, rings=12):
    """A curved surface with a genuine central aperture; no backing polygon."""
    if inner_offset is None:
        inner_offset = offset
    verts, faces = [], []
    n = len(outer)
    for row in range(rings + 1):
        t = row / rings
        for (ax, az), (bx, bz) in zip(outer, inner):
            x, z = ax + (bx-ax)*t, az + (bz-az)*t
            verts.append((x, rear_y(x, z, offset + (inner_offset-offset)*t), z))
    for row in range(rings):
        for i in range(n):
            j = (i+1) % n
            # Rear outward normal is +Y, opposite to a front-facing X/Z fan.
            faces.append((row*n+i, (row+1)*n+i, (row+1)*n+j, row*n+j))
    return rear_finish(mesh(name, verts, faces, mat))


def rear_face(name, outline, mat, offset=0.0, rings=10):
    """Filled crowned patch, with radial subdivisions instead of a flat ngon."""
    cx = sum(p[0] for p in outline) / len(outline)
    cz = sum(p[1] for p in outline) / len(outline)
    verts = [(cx, rear_y(cx, cz, offset), cz)]
    n = len(outline)
    for row in range(1, rings+1):
        t = row / rings
        for x, z in outline:
            x, z = cx+(x-cx)*t, cz+(z-cz)*t
            verts.append((x, rear_y(x, z, offset), z))
    faces = [(0, 1+(i+1)%n, 1+i) for i in range(n)]
    for row in range(rings-1):
        a, b = 1+row*n, 1+(row+1)*n
        for i in range(n):
            j=(i+1)%n
            faces.append((a+i, a+j, b+j, b+i))
    return rear_finish(mesh(name, verts, faces, mat))


def rear_line(name, points, radius, mat, offset=.010, closed=False):
    samples=[]
    pairs=list(zip(points, points[1:] + ([points[0]] if closed else [])))
    for (ax,az),(bx,bz) in pairs:
        count=max(1,math.ceil(math.hypot(bx-ax,bz-az)/.045))
        for i in range(count):
            f=i/count
            x,z=ax+(bx-ax)*f,az+(bz-az)*f
            samples.append((x,rear_y(x,z,offset),z))
    if not closed:
        x,z=points[-1]
        samples.append((x,rear_y(x,z,offset),z))
    return tube(name, samples, radius, mat, closed)


# The upper surround and lower quarter shell join at the waist. Separating
# them allows two true openings: the rear screen and the recessed large hatch.
# The main roof rolls down gently into the rear crown. Its former rectangular
# last band extended behind the rounded rim and appeared as two white hooks.
rear_crown = ROOF - .065
rear_roof_join_y = 3.10
rear_roof_half = SIDE - .023


def rear_roof_base_z(x):
    return ROOF-(ROOF-2.80)*(abs(x)/rear_roof_half)**7


def rear_top_z(x):
    corner = max(0.0, abs(x) - (SIDE - .22))
    return rear_crown - .22 + math.sqrt(max(0.0, .22*.22 - corner*corner))


def rear_roof_point(x, t):
    """G1 roof-to-rear transition, including the existing end-face tangent."""
    rear_x=x/rear_roof_half*SIDE
    z0 = rear_roof_base_z(x)
    z1 = rear_top_z(rear_x)
    y0, y1 = rear_roof_join_y, rear_y(rear_x,z1)
    span, drop = y1-y0, max(.001,z0-z1)
    dy_dz = -2*.175*max(0.0,z1-2.38)/max(.2,ROOF-2.38)**2
    dz1 = -min(2.8*drop, .25*span/max(.01,abs(dy_dz)))
    dy1 = dy_dz*dz1
    h00=2*t*t*t-3*t*t+1;h10=t*t*t-2*t*t+t
    h01=-2*t*t*t+3*t*t;h11=t*t*t-t*t
    return (x+(rear_x-x)*(3*t*t-2*t*t*t),h00*y0+h10*span+h01*y1+h11*dy1,
            h00*z0+h01*z1+h11*dz1)


def rear_replace_roof_band():
    roof = bpy.data.objects.get('Continuous low rounded roof')
    if roof is None:
        return
    old = roof.data
    keep = [v.index for v in old.vertices if v.co.y <= 3.25001]
    remap = {index:i for i,index in enumerate(keep)}
    verts=[]
    last=[]
    for index in keep:
        p=old.vertices[index].co.copy()
        if p.y>rear_roof_join_y:
            p.y=rear_roof_join_y
            p.z=rear_roof_base_z(p.x)
            last.append(len(verts))
        verts.append(tuple(p))
    faces=[tuple(remap[i] for i in face.vertices) for face in old.polygons
           if all(i in remap for i in face.vertices)]
    last.sort(key=lambda i:verts[i][0])
    previous=last
    for row in range(1,17):
        current=[]
        for i in last:
            current.append(len(verts))
            verts.append(rear_roof_point(verts[i][0],row/16))
        for i in range(len(last)-1):
            faces.append((previous[i],previous[i+1],current[i+1],current[i]))
        previous=current
    old.clear_geometry()
    old.from_pydata(verts,[],faces)
    old.update()
    for face in old.polygons:face.use_smooth=True
    roof['rear_corner_join']='Rectangular end band replaced by a tangent-matched crown loft.'


def rear_trim_upper_side_frit():
    """Trim against the complete, non-convex roof/cap side silhouette.

    The tangent-matched roof edge has an inflection. Intersecting every edge's
    half-plane incorrectly treats that silhouette as convex, cutting a large
    triangular hole in the frit and exposing the pale roof liner. Triangulate
    the allowed region first, then retain the union of its intersections.
    """
    def clip(poly, distance):
        out=[]
        if not poly:return out
        a=poly[-1];da=distance(a);inside_a=da>=-1e-9
        for b in poly:
            db=distance(b);inside_b=db>=-1e-9
            if inside_a!=inside_b:
                t=da/(da-db)
                out.append(tuple(a[k]+t*(b[k]-a[k]) for k in range(3)))
            if inside_b:out.append(b)
            a,da,inside_a=b,db,inside_b
        clean=[]
        for p in out:
            if not clean or sum((a-b)**2 for a,b in zip(clean[-1],p))>1e-14:clean.append(p)
        if len(clean)>1 and sum((a-b)**2 for a,b in zip(clean[0],clean[-1]))<1e-14:clean.pop()
        return clean
    z_end=rear_top_z(SIDE)
    edge=[(-4.,.9),(rear_y(SIDE,.9)-.005,.9)]
    for i in range(1,49):
        z=.9+(z_end-.9)*i/48
        edge.append((rear_y(SIDE,z)-.005,z))
    for i in range(1,65):
        x,y,z=rear_roof_point(rear_roof_half,1-i/64)
        edge.append((y-.005,z-.004))
    edge.append((-4.,2.80))
    from mathutils.geometry import tessellate_polygon
    boundary=[Vector((y,z,0.)) for y,z in edge]
    triangles=[]
    for tri in tessellate_polygon([boundary]):
        points=[(boundary[v].x,boundary[v].y) if isinstance(v,int) else (v.x,v.y) for v in tri]
        a,b,c=points
        area=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
        if abs(area)<1e-12:continue
        if area<0:points.reverse()
        triangles.append(points)
    for obj in list(MASTER.objects):
        if obj.type!='MESH' or not obj.name.startswith(('Continuous black eyebrow','Upper ceramic window frit','Flush glazing frit')):
            continue
        if not obj.data.vertices or max(v.co.y for v in obj.data.vertices)<=rear_roof_join_y:
            continue
        verts=[];faces=[];materials=[]
        for face in obj.data.polygons:
            points=[tuple(obj.data.vertices[i].co) for i in face.vertices]
            front=clip(points,lambda p:3.0-p[1])
            back=clip(points,lambda p:p[1]-3.0)
            pieces=[front]
            if back:
                for tri in triangles:
                    piece=back
                    for a,b in zip(tri,tri[1:]+tri[:1]):
                        piece=clip(piece,lambda p,a=a,b=b:(b[0]-a[0])*(p[2]-a[1])-(b[1]-a[1])*(p[1]-a[0]))
                        if not piece:break
                    if piece:pieces.append(piece)
            for poly in pieces:
                if len(poly)<3:continue
                start=len(verts);verts.extend(poly)
                faces.append(tuple(range(start,start+len(poly))))
                materials.append(face.material_index)
        obj.data.clear_geometry();obj.data.from_pydata(verts,[],faces);obj.data.update()
        for face,index in zip(obj.data.polygons,materials):face.material_index=index


rear_replace_roof_band()
rear_trim_upper_side_frit()
rear_upper_outer = rear_outline(W, 1.585, rear_crown, .22, .010, edge_steps=REAR_FINE_EDGES)
rear_mask_outer = rear_outline(W-.20, 1.645, 2.575, .23, .060, edge_steps=REAR_FINE_EDGES)
rear_finish(rear_ring('Rear | rounded aqua roof and pillars', rear_upper_outer, rear_mask_outer, white), .009)

rear_lower_outer = rear_outline(W, .635, 1.585, .010, .025, edge_steps=REAR_FINE_EDGES)
rear_hatch_outer = rear_outline(1.945, .640, 1.557, .082, .040, edge_steps=REAR_FINE_EDGES)
rear_finish(rear_ring('Rear | sculpted lower quarter shell', rear_lower_outer, rear_hatch_outer, white), .009)

# Black destination surround with a low, broad glazed aperture, like the photo.
rear_window_outer = rear_outline(1.885, 1.692, 2.125, .074, .025, edge_steps=REAR_FINE_EDGES)
rear_window_inner = rear_outline(1.848, 1.710, 2.106, .062, .015, edge_steps=REAR_FINE_EDGES)
rear_ring('Rear | curved black destination and window frit', rear_mask_outer, rear_window_outer, black, .007)
rear_ring('Rear | window EPDM gasket', rear_window_outer, rear_window_inner, rubber, .013, rings=2)
rear_face('Rear | transparent curved lower screen', rear_window_inner, deep_glass, .014, rings=10)
rear_face('Wrap surface | window rear', rear_window_inner, white, .014+WRAP_SURFACE_OUT, rings=10)

# Destination unit is above the clear screen; its bezel sits entirely in frit.
rear_sign_outer = rear_outline(1.225, 2.226, 2.466, .012, .012)
rear_sign_inner = rear_outline(1.207, 2.235, 2.457, .006, .006)
rear_ring('Rear | destination display aluminium bezel', rear_sign_outer, rear_sign_inner, hubdark, .021, rings=2)
rear_face('Rear | destination display face', rear_sign_inner, plastic, .024)
label('Rear route LED', '42  上海体育馆', (0, rear_y(0, 2.345, .034), 2.345), .118, led, (0,1,0))

# The side-family photograph has two small red upper position lamps.
for side in (-1, 1):
    x, z = side*.868, 2.442
    y = rear_y(x,z,.020)
    rod(f'Rear upper marker housing {side}', (x,y-.009,z), (x,y+.006,z), .035, black, 32)
    rod(f'Rear upper marker lens {side}', (x,y+.006,z), (x,y+.013,z), .029, tail, 32)

rear_camera_z = min(2.66, ROOF-.10)
rear_face('Rear | high reversing camera fairing', rear_outline(.074, rear_camera_z-.027, rear_camera_z+.039, .023, .010), black, .027)
rod('Rear reversing camera glass', (0,rear_y(0,rear_camera_z,.043),rear_camera_z), (0,rear_y(0,rear_camera_z,.054),rear_camera_z), .014, glass, 24)

# A deep outline and separate hatch skin give the hatch a true 12 mm recess.
rear_hatch_inner = rear_outline(1.925, .650, 1.547, .071, .030, edge_steps=REAR_FINE_EDGES)
rear_hatch_seal=rear_ring('Rear | large service hatch shut line', rear_hatch_outer, rear_hatch_inner, rubber, .002, -.010, rings=2)
rear_finish(rear_hatch_seal,.003)
rear_service_hatch = rear_face('Rear | broad recessed electric service hatch', rear_hatch_inner, white, -.010, rings=16)
# The service door is a metal panel with a real inner skin and connected
# perimeter. Its inward face is visible through the rear window from the cab.
rear_finish(rear_service_hatch, .006)

# White livery runs over the upper edge and a short way down each side; it is
# intentionally open at the bottom, unlike a generic rectangular trim panel.
# As paint (2026-10-19) it lies on the tail's own panel round the hatch: its legs at 0.992 keep their inner edge 2 mm outside
# the hatch's shut line (outer outline half 0.9725), where the 20 mm tube used to bridge the seal.
u_pts = []
trim_half, trim_top, trim_bottom, trim_r = .992, 1.587, 1.286, .090
u_pts.append((-trim_half,trim_bottom))
u_pts.append((-trim_half,trim_top-trim_r))
for i in range(13):
    a=math.pi-math.pi*.5*i/12
    u_pts.append((-trim_half+trim_r+trim_r*math.cos(a), trim_top-trim_r+trim_r*math.sin(a)))
u_pts.append((trim_half-trim_r, trim_top))
for i in range(13):
    a=math.pi*.5-math.pi*.5*i/12
    u_pts.append((trim_half-trim_r+trim_r*math.cos(a), trim_top-trim_r+trim_r*math.sin(a)))
u_pts.append((trim_half,trim_bottom))
# paint like the side stripes, laid on the tail by paint_lines.py (it was a 17 mm tube 12 mm off the surface)
paint_line('Rear | white inverted-U hatch stripe', u_pts, .017, green, 'y')

# Tall narrow gloss pods nest into either edge of the service hatch. Their
# circular lenses have separate bezel, optical face and concentric moulding.
for side in (-1,1):
    x=side*.993
    rear_face(f'Rear | vertical triple lamp pod {side}', rear_outline(.178,.838,1.272,.080,.074,cx=x), black, .026, rings=10)
    for lamp_i,(z,lens) in enumerate(((1.184,tail),(1.056,turnL if side<0 else turnR),(.928,reverse))):
        y=rear_y(x,z,.034)
        rod(f'Rear lamp {side}.{lamp_i} black outer cup',(x,y-.006,z),(x,y+.010,z),.056,plastic,40)
        rod(f'Rear lamp {side}.{lamp_i} thin metal rim',(x,y+.009,z),(x,y+.016,z),.0495,chrome,40)
        rod(f'Rear lamp {side}.{lamp_i} coloured circular lens',(x,y+.015,z),(x,y+.024,z),.0435,lens,40)
        for radius in (.034,.022):
            pts=[(x+radius*math.cos(t),y+.025,z+radius*math.sin(t)) for t in [2*math.pi*i/48 for i in range(48)]]
            tube(f'Rear lamp {side}.{lamp_i} lens moulding {radius}',pts,.0015,lens,True)

# Sunwin badge and restrained maker/model lettering are integral to the hatch.
rear_face('Rear | Sunwin badge chrome frame', rear_outline(.122,1.302,1.437,.018,.018), chrome, .009)
rear_face('Rear | Sunwin badge inset', rear_outline(.104,1.311,1.428,.012,.012), black, .011)
rear_line('Rear | Sunwin badge monogram', [(-.040,1.344),(-.022,1.325),(.006,1.328),(.034,1.354),(.030,1.387),(.009,1.407),(-.017,1.403),(-.038,1.379),(-.025,1.353),(.003,1.351),(.037,1.408)],.006,chrome,.017)
label('Rear model badge','iEV7',(.706,rear_y(.706,.804,.006),.804),.057,chrome,(0,1,0))
label('Rear maker Chinese badge','申沃客车',(-.642,rear_y(-.642,.858,.006),.858),.055,chrome,(0,1,0))
label('Rear maker Latin badge','SUNWIN',(-.642,rear_y(-.642,.797,.006),.797),.041,chrome,(0,1,0))
for x in (-.624,.624):
    z=.683; y=rear_y(x,z,.003)
    rod('Rear hatch quarter-turn lock',(x,y-.009,z),(x,y+.010,z),.013,black,24)
    rod('Rear hatch lock insert',(x,y+.010,z),(x,y+.013,z),.006,chrome,16)
rear_face('Rear | hatch bottom pull handle',rear_outline(.13,.645,.671,.006,.003),black,.008)

# Integral crowned bumper: the number-plate well is inset into a real opening.
bumper_outer=rear_outline(W-.012,.285,.635,.056,.102)
plate_outer=rear_outline(.610,.389,.565,.035,.047)
rear_ring('Rear | rounded continuous bumper and plate scoop',bumper_outer,plate_outer,white,.018,-.009,rings=14)
plate_inner=rear_outline(.456,.397,.532,.012,.012)
rear_ring('Rear | recessed plate-well return',plate_outer,plate_inner,white,-.009,-.032,rings=4)
rear_face('Rear | number plate dark mount',rear_outline(.455,.397,.532,.012,.012),plastic,-.033)
rear_face('Rear | registration plate',rear_outline(.423,.411,.522,.007,.007),mint,-.029)
label('Rear number plate','沪 A·D0710',(0,rear_y(0,.466,-.023),.466),.054,plastic,(0,1,0))

for side in (-1,1):
    x=side*.934
    rear_face(f'Rear | reflector chrome trim {side}',rear_outline(.159,.493,.527,.011,.011,cx=x),chrome,.030,rings=3)
    rear_face(f'Rear | slim red bumper reflector {side}',rear_outline(.144,.500,.521,.006,.006,cx=x),tail,.032,rings=3)
for x in (-1.111,-.447,.447,1.111):
    z=.522; y=rear_y(x,z,.028)
    rod('Rear parking sensor ring',(x,y-.009,z),(x,y+.006,z),.014,black,24)
    rod('Rear parking sensor face',(x,y+.006,z),(x,y+.009,z),.0085,white,24)

rear_line('Rear | bumper upper panel separation',[(-1.161,.803),(-1.115,.679),(-1.037,.639),(-.89,.636),(.89,.636),(1.037,.639),(1.115,.679),(1.161,.803)],.004,rubber,.021)

# The lower side skin used to continue as a square flap to Y=3.37, below the
# bumper's rounded bottom. Its unattached rear edge exposed the inner liner.
# End that skin at a shared seam and lift its last 100 mm into the bumper lip.
rear_lower_join_y=3.30


def rear_trim_lower_quarter_panels():
    for obj in list(MASTER.objects):
        if obj.type!='MESH' or not obj.name.startswith('Aqua side quarter panels'):
            continue
        verts=[];faces=[]
        for face in obj.data.polygons:
            points=[tuple(obj.data.vertices[i].co) for i in face.vertices]
            out=[];a=points[-1];da=rear_lower_join_y-a[1]
            for b in points:
                db=rear_lower_join_y-b[1]
                if (da>=0)!=(db>=0):
                    t=da/(da-db)
                    out.append(tuple(a[k]+t*(b[k]-a[k]) for k in range(3)))
                if db>=0:out.append(b)
                a,da=b,db
            if len(out)<3:continue
            start=len(verts)
            for x,y,z in out:
                t=max(0.,min(1.,(y-3.20)/.10))
                lift=.045*t*t*(3-2*t)
                z+=lift*max(0.,min(1.,(1.12-z)/.88))
                verts.append((x,y,z))
            faces.append(tuple(range(start,start+len(out))))
        obj.data.clear_geometry();obj.data.from_pydata(verts,[],faces);obj.data.update()


def rear_outline_edge_x(outline,z):
    hits=[]
    for a,b in zip(outline,outline[1:]+outline[:1]):
        if abs(a[1]-b[1])<1e-9:
            if abs(z-a[1])<1e-7:hits.extend((a[0],b[0]))
        elif min(a[1],b[1])-1e-7<=z<=max(a[1],b[1])+1e-7:
            t=(z-a[1])/(b[1]-a[1]);hits.append(a[0]+t*(b[0]-a[0]))
    return max(hits)


rear_trim_lower_quarter_panels()

# Returns end on the actual rounded bumper outline, rather than a constant-X
# rectangle. Their winding faces outwards; the former inward winding pushed
# the solidify thickness outside the body and formed another narrow white fin.
for side in (-1,1):
    verts,faces=[],[]
    zs=sorted(set(round(z,6) for z in
                  [.285,.635,1.12,1.585,1.88,2.20,2.48,rear_crown-.22]+
                  [.285+(.635-.285)*i/24 for i in range(25)]+
                  [z for x,z in bumper_outer if z>=.285]))
    for z in zs:
        if z<=.635:
            back_x=rear_outline_edge_x(bumper_outer,z)
            back_y=rear_y(back_x,z,.018)
        else:
            back_x=SIDE;back_y=rear_y(back_x,z)
        start_x=SIDE-.013+.013*max(0.,min(1.,(z-.285)/(1.12-.285)))
        start_y=rear_lower_join_y if z<=1.12 else min(HALF-.235,back_y-.003)
        for j in range(9):
            t=j/8
            # Smoothly turn the bottom corner into the narrower bumper lip.
            smooth=t*t*(3-2*t)
            x=start_x+(back_x-start_x)*smooth
            y=start_y+(back_y-start_y)*t
            verts.append((side*x,y,z))
    for i in range(len(zs)-1):
        for j in range(8):
            f=(i*9+j,i*9+j+1,(i+1)*9+j+1,(i+1)*9+j)
            a,b,c=(Vector(verts[k]) for k in f[:3])
            if (b-a).cross(c-a).x*side<0:f=tuple(reversed(f))
            faces.append(f)
    rear_finish(mesh(f'Rear | rounded pillar side return {side}',verts,faces,white),.008)

# Continuous rear wheelhouse: the exterior arch opening must reveal the tyre
# and its dark housing, not saloon seats and yellow grab rails. The 55 mm radial
# clearance is outside the complete tyre envelope, including its shoulder.
for side in (-1,1):
    ay=AXLES[1];radius=R+.055
    xs=sorted((side*.715,side*(SIDE-.013)))
    verts=[];faces=[];steps=56
    for x in xs:
        for i in range(steps+1):
            a=math.pi*i/steps
            verts.append((x,ay+radius*math.cos(a),R+radius*math.sin(a)))
    for i in range(steps):
        faces.append((i,steps+1+i,steps+2+i,i+1))
    wheelhouse=mesh(f'Rear inner wheelhouse arch {side}',verts,faces,plastic)
    wheelhouse['cdlc_part']=0
    for face in wheelhouse.data.polygons:face.use_smooth=True
    wheelhouse.data.materials.append(panel)
    gauge=wheelhouse.modifiers.new('Wheelhouse real skin thickness','SOLIDIFY')
    gauge.thickness=.010;gauge.offset=-1;gauge.material_offset=1
    cap=[(side*.715,ay,R)]+[(side*.715,ay+radius*math.cos(math.pi*i/steps),R+radius*math.sin(math.pi*i/steps)) for i in range(steps+1)]
    cap_faces=[]
    for i in range(1,steps+1):
        f=(0,i,i+1)
        a,b,c=(Vector(cap[k]) for k in f)
        if (b-a).cross(c-a).x*side<0:f=tuple(reversed(f))
        cap_faces.append(f)
    wall=mesh(f'Rear inner wheelhouse wall {side}',cap,cap_faces,plastic)
    wall['cdlc_part']=0
    wall.data.materials.append(panel)
    gauge=wall.modifiers.new('Wheelhouse wall thickness','SOLIDIFY')
    gauge.thickness=.010;gauge.offset=-1;gauge.material_offset=1

