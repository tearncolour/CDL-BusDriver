"""Reference-shaped iEV7 nose, executed in build_model.py's modelling namespace.

The photographs show a continuous raked, laterally bowed face.  Every front
surface below is sampled on that face, including the transparent windscreen;
the aqua skin and black frit are annuli with a real windscreen aperture.
Blender coordinates: X right, -Y front, Z up; dimensions in metres.
"""

# Heights, centreline setback from the foremost point, and outside half width.
# The crown rolls rearwards into the 2.96 m roof; the black side edge at 2.70 m
# is near Y=-3.10 m. Helpers remain public for adjoining side/roof authoring.
# Setbacks: the front silhouette of both official side photographs
# (references/right-profile-official.jpg, 20260804092714_5921.jpg; scale from the
# hub centres, 5.100 m; the two agree within ~1 cm), fitted within 6 mm by this
# spline with no curvature reversal: one convex face leaning back from its
# foremost point at ~1.05 m, the lower bumper rolling under. The first profile
# (estimated, not measured) stood almost upright from 2.25 to 2.70 m and then
# rolled back sharply, and put the foremost point at the bumper's 0.43 m: an
# S-bend in profile (user review 2026-10-15: "the front should be one
# continuous curvature"). Perspective: the centreline silhouette lies ~1 m
# beyond the hub plane, 1-2 % small by the two photos' overall lengths; heights
# near the crown may read up to ~3 cm low (camera height unknown).
# Half widths: the previous profile's spline, resampled at these stations.
FRONT_PROFILE = [
    (.19, .275, 1.105), (.25, .197, 1.143), (.32, .126, 1.169),
    (.43, .080, 1.173), (.60, .040, 1.173), (.80, .012, 1.176),
    (1.05, .000, 1.174), (1.35, .023, 1.172), (1.80, .100, 1.170),
    (2.20, .188, 1.167), (2.50, .268, 1.164), (2.70, .352, 1.155),
    (2.82, .455, 1.115), (2.96, .655, 1.045),
]


def fs_cubic_coefficients(column):
    """One natural cubic spline; second derivatives match at every station.

    Solve the tridiagonal interpolation system once. Coefficients use metres
    of height, not a per-segment normalized parameter, so derivative equality
    is explicit and can be checked without finite-difference tolerances.
    """
    count=len(FRONT_PROFILE)
    h=[FRONT_PROFILE[i+1][0]-FRONT_PROFILE[i][0] for i in range(count-1)]
    delta=[(FRONT_PROFILE[i+1][column]-FRONT_PROFILE[i][column])/h[i]
           for i in range(count-1)]
    lower=[0.0]*count;diag=[1.0]*count;upper=[0.0]*count;rhs=[0.0]*count
    for i in range(1,count-1):
        lower[i]=h[i-1];diag[i]=2*(h[i-1]+h[i]);upper[i]=h[i]
        rhs[i]=6*(delta[i]-delta[i-1])
    for i in range(1,count):
        factor=lower[i]/diag[i-1]
        diag[i]-=factor*upper[i-1];rhs[i]-=factor*rhs[i-1]
    second=[0.0]*count;second[-1]=rhs[-1]/diag[-1]
    for i in range(count-2,-1,-1):
        second[i]=(rhs[i]-upper[i]*second[i+1])/diag[i]
    return [(FRONT_PROFILE[i][column],
             delta[i]-h[i]*(2*second[i]+second[i+1])/6,
             second[i]/2,(second[i+1]-second[i])/(6*h[i]))
            for i in range(count-1)]


FS_PROFILE_CUBICS={column:fs_cubic_coefficients(column) for column in (1,2)}


def fs_cubic_sample(segment, offset):
    a,b,c,d=segment
    return (a+offset*(b+offset*(c+offset*d)),
            b+offset*(2*c+3*d*offset),2*c+6*d*offset)


def fs_profile_sample(z, column):
    # The complete visible nose lies in this interval. Endpoint extrapolation
    # also preserves value/slope/curvature for tiny optical overlays.
    i=0
    while i<len(FRONT_PROFILE)-2 and z>FRONT_PROFILE[i+1][0]:i+=1
    return fs_cubic_sample(FS_PROFILE_CUBICS[column][i],z-FRONT_PROFILE[i][0])


def fs_profile_value(z, column):
    return fs_profile_sample(z,column)[0]


def fs_nose_halfwidth(z):
    return fs_profile_value(z, 2)


def fs_surface_sample(z,x=0.):
    """Y, dY/dX, dY/dZ and Hessian of the common C2 front surface."""
    p,pz,pzz=fs_profile_sample(z,1)
    w,wz,wzz=fs_profile_sample(z,2)
    u=x/w
    t=max(0.,min(1.,z-1.3))
    blend=t*t*t*(10+t*(-15+6*t))
    blend_z=30*t*t*(1-t)*(1-t) if 0<t<1 else 0.
    blend_zz=60*t*(1-t)*(1-2*t) if 0<t<1 else 0.
    # The corner bow above 2.3 m (.085 m behind the centre's .038 bow): not
    # measured; bounded by the side glazing's front end at Y=-3.09, which the
    # nose corner must stay ahead of up to the eyebrow (2.69 m). With the
    # measured, more raked crown the former .115 put the corner behind it
    # above 2.65 m and the corner shoulder folded back on itself there (the
    # eyebrow seen from behind from the driver's seat).
    shoulder=.196-.111*blend
    sz=-.111*blend_z;szz=-.111*blend_zz
    uz=-u*wz/w;uzz=u*(2*(wz/w)**2-wzz/w)
    g=.076*u+8*shoulder*u**7
    gu=.076+56*shoulder*u**6
    value=-HALF+p+.038*u*u+shoulder*u**8
    dx=g/w
    dz=pz+g*uz+sz*u**8
    dxx=gu/(w*w)
    dxz=(gu*uz+8*sz*u**7)/w-g*wz/(w*w)
    dzz=pzz+gu*uz*uz+g*uzz+16*sz*u**7*uz+szz*u**8
    return value,dx,dz,dxx,dxz,dzz


def fs_nose_y(z, x=0.):
    return fs_surface_sample(z,x)[0]


def fs_normal(x,z):
    _,dx,dz,_,_,_=fs_surface_sample(z,x)
    return Vector((dx,-1.,dz)).normalized()


def fs_apply_common_normals(data):
    """Apply after a mesh is reduced too: material borders cannot change N."""
    for poly in data.polygons:poly.use_smooth=True
    data.normals_split_custom_set_from_vertices([tuple(fs_normal(v.co.x,v.co.z))
                                                for v in data.vertices])


def fs_point(x, z, proud=0.):
    return (x, fs_nose_y(z, x) - proud, z)


def fs_smooth(obj):
    fs_apply_common_normals(obj.data)
    obj['reference_section'] = 'Official Sunwin iEV7 front / 20260804'
    obj['front_common_surface'] = True
    obj['front_normal_field'] = 'analytic C2 surface dY/dX,-1,dY/dZ'
    return obj


def fs_contour(width, low, high, lower_radius, upper_radius, taper=True):
    """CCW (X,Z) rounded contour with independently rounded top and bottom.

    All contours use the same sample topology: 12 steps around each corner and
    18 steps down each straight.  Rings therefore remain properly quadded.
    """
    half = width * .5
    corners = [
        (half-upper_radius, high-upper_radius, upper_radius, 0),
        (-half+upper_radius, high-upper_radius, upper_radius, 90),
        (-half+lower_radius, low+lower_radius, lower_radius, 180),
        (half-lower_radius, low+lower_radius, lower_radius, 270),
    ]
    pts = []
    for k, (cx, cz, rad, angle) in enumerate(corners):
        for j in range(12):
            theta = math.radians(angle + 90*j/12)
            pts.append((cx + rad*math.cos(theta), cz + rad*math.sin(theta)))
        end_angle = math.radians(angle+90)
        end = (cx+rad*math.cos(end_angle), cz+rad*math.sin(end_angle))
        nx, nz, nr, na = corners[(k+1) % 4]
        theta = math.radians(na)
        start = (nx+nr*math.cos(theta), nz+nr*math.sin(theta))
        for j in range(18):
            t = j / 18
            pts.append((end[0]*(1-t)+start[0]*t, end[1]*(1-t)+start[1]*t))
    if taper:
        pts = [(x * fs_nose_halfwidth(z)/SIDE, z) for x, z in pts]
    return pts


def fs_ring(name, outside, inside, mat, proud=0., bands=5):
    if len(outside) != len(inside):
        raise ValueError('Nose ring contours must have matching topology')
    n = len(outside)
    verts, faces = [], []
    for j in range(bands+1):
        t = j/bands
        verts.extend(fs_point(a[0]*(1-t)+b[0]*t,
                              a[1]*(1-t)+b[1]*t, proud)
                     for a, b in zip(outside, inside))
    for j in range(bands):
        for i in range(n):
            nxt = (i+1) % n
            faces.append((j*n+i, j*n+nxt, (j+1)*n+nxt, (j+1)*n+i))
    return fs_smooth(mesh(name, verts, faces, mat))


def fs_patch(name, outline, mat, proud=0., bands=7):
    """Fill a convex contour without an enormous non-planar n-gon."""
    n = len(outline)
    cx = sum(p[0] for p in outline)/n
    cz = sum(p[1] for p in outline)/n
    verts, faces = [], []
    for j in range(bands):
        t = j/bands
        verts.extend(fs_point(x*(1-t)+cx*t, z*(1-t)+cz*t, proud)
                     for x, z in outline)
    verts.append(fs_point(cx, cz, proud))
    for j in range(bands-1):
        for i in range(n):
            nxt = (i+1) % n
            faces.append((j*n+i, j*n+nxt, (j+1)*n+nxt, (j+1)*n+i))
    center = len(verts)-1
    for i in range(n):
        faces.append(((bands-1)*n+i, (bands-1)*n+(i+1) % n, center))
    obj = fs_smooth(mesh(name, verts, faces, mat))
    if proud > 0 and mat is not glass:
        # A part standing proud is a solid down into the skin, not a floating
        # sheet: on the raked face (the lower bumper rolls under, the crown
        # rolls back) a sheet offset along -Y showed its back and a gap from
        # the side (the front camera, the reflector strip, the lamp sockets).
        body = obj.modifiers.new('Proud part body into the skin', 'SOLIDIFY')
        body.thickness = proud+.004
        body.offset = -1
        body.use_rim = True
        body.use_even_offset = True
        obj['front_common_surface'] = False
    return obj


def fs_circle(x, z, radius, count=48):
    return [(x+radius*math.cos(2*math.pi*i/count),
             z+radius*math.sin(2*math.pi*i/count)) for i in range(count)]


def fs_lens(name, x, z, outer, inner, mat, proud):
    obj=fs_ring(name, fs_circle(x, z, outer), fs_circle(x, z, inner),
                mat, proud, bands=2)
    # down into the skin like the patches (a 2 mm ring floated off the raked
    # bumper and showed its back from the side)
    gauge=obj.modifiers.new('Lamp bezel metal thickness','SOLIDIFY')
    gauge.thickness=proud+.004;gauge.offset=-1;gauge.use_rim=True;gauge.use_even_offset=True
    obj['front_common_surface']=False
    return obj


def fs_trace(name, points, radius, mat, proud=.012, closed=False):
    return tube(name, [fs_point(x, z, proud) for x, z in points], radius, mat, closed)


def fs_label(name, text, x, z, size, mat, proud=.02):
    return label(name, text, fs_point(x, z, proud), size, mat,fs_normal(x,z))


def fs_clip_height(points, height, above):
    result=[]
    a=points[-1];ina=a[1]>=height if above else a[1]<=height
    for b in points:
        inb=b[1]>=height if above else b[1]<=height
        if ina!=inb:
            t=(height-a[1])/(b[1]-a[1]);result.append((a[0]+t*(b[0]-a[0]),height))
        if inb:result.append(b)
        a,ina=b,inb
    clean=[]
    for point in result:
        if not clean or math.hypot(point[0]-clean[-1][0],point[1]-clean[-1][1])>1e-9:clean.append(point)
    if len(clean)>1 and math.hypot(clean[0][0]-clean[-1][0],clean[0][1]-clean[-1][1])<1e-9:clean.pop()
    return clean


def fs_radial_boundary(points, center, count=160, angles=None):
    """Resample a convex aperture by angle so two different outlines share topology."""
    result=[]
    for i in range(count):
        angle=angles[i] if angles else 2*math.pi*i/count;dx,dz=math.cos(angle),math.sin(angle);hits=[]
        for a,b in zip(points,points[1:]+points[:1]):
            ex,ez=b[0]-a[0],b[1]-a[1];den=dx*ez-dz*ex
            if abs(den)<1e-12:continue
            ax,az=a[0]-center[0],a[1]-center[1]
            t=(ax*ez-az*ex)/den;s=(ax*dz-az*dx)/den
            if t>=0 and -1e-8<=s<=1+1e-8:hits.append(t)
        if not hits:raise ValueError('Front ceramic aperture cannot be resampled')
        t=min(hits);result.append((center[0]+t*dx,center[1]+t*dz))
    return result


def fs_aperture_ring(name, outer, opening, center, mat, proud, bands=8, sharp=(), count=160):
    # The outline's sharp corners (the ends of the line where the two frit
    # rings meet, z=2.34) are sampled exactly: the uniform angles cut them,
    # and the two rings' cut corners left an 8 mm wedge open to the outside
    # at each A-pillar, next to the bottom of the destination display (seen
    # from the driver's seat).
    angles=[2*math.pi*i/count for i in range(count)]
    for x,z in sharp:
        a=math.atan2(z-center[1],x-center[0])%(2*math.pi)
        angles[round(a/(2*math.pi/count))%count]=a
    return fs_ring(name,fs_radial_boundary(outer,center,count,angles),fs_radial_boundary(opening,center,count,angles),mat,proud,bands)


def fs_flat_patch(name, outline, y, mat):
    # Hardware is a flat board behind the common curved laminated glass.
    return mesh(name,[(x,y,z) for x,z in outline],[tuple(range(len(outline)))],mat)


# The broad aqua bonnet and radiused crown surround one continuous black
# glazed face.  There is no solid aqua/black mesh inside the glass opening.
fs_outer = fs_contour(W, .20, 2.96, .16, .27)
fs_black = fs_contour(W-.080, .785, 2.72, .11, .255)
fs_clear = fs_contour(W-.224, 1.15, 2.30, .105, .105)
FS_GLASS_PROUD=.014
FS_CERAMIC_PROUD=.006
FS_LED_CENTER_Z=2.513
FS_LED_SIZE=(1.50,.205)
FS_BOARD_HALF_WIDTH=.805
FS_BOARD_BOTTOM=2.378
FS_BOARD_TOP=2.647
FS_BOARD_PLANE_Y=max(fs_nose_y(FS_BOARD_BOTTOM+(FS_BOARD_TOP-FS_BOARD_BOTTOM)*j/32,
                              -FS_BOARD_HALF_WIDTH+2*FS_BOARD_HALF_WIDTH*i/32)-FS_GLASS_PROUD
                     for i in range(33) for j in range(33))+.035
FS_LED_PLANE_Y=FS_BOARD_PLANE_Y-.003
fs_front_glass=fs_contour(W-.090,1.105,2.718,.115,.255)
fs_display_opening=fs_contour(1.640,2.370,2.655,.014,.014,taper=False)
fs_ring('iEV7 raked aqua nose and crown | windscreen aperture',
        fs_outer, fs_black, white, bands=7)
fs_frit_split=[p for p in fs_clip_height(fs_black,2.340,False) if abs(p[1]-2.340)<1e-9]
fs_aperture_ring('iEV7 continuous black panoramic frit | lower ceramic aperture',
                fs_clip_height(fs_black,2.340,False),fs_clear,(0,1.725),black,FS_CERAMIC_PROUD,sharp=fs_frit_split)
fs_aperture_ring('iEV7 continuous black panoramic frit | display aperture',
                fs_clip_height(fs_black,2.340,True),fs_display_opening,(0,2.5125),black,FS_CERAMIC_PROUD,sharp=fs_frit_split)
# The black frit stands FS_CERAMIC_PROUD ahead of the aqua skin. Along their
# common contour that step was open at the side: from the driver's seat a sight
# line passed between the aqua ring's inner return and the frit's lining, a
# bright slit all the way up the A-pillar (user review 2026-10-15; ray fans from
# the driver's eye hit nothing in a 0.1 degree run at every height). A real
# edge closes it: a closed 1.5 mm wall swept along the aqua ring's own inner
# contour, from just ahead of the frit face back 10 mm behind the aqua skin,
# with a lip just behind the frit (which also covers the sub-millimetre chords
# where the frit's radial samples cut the contour's corners).
def fs_edge_closure(name, contour, mat):
    n=len(contour)
    section=[(0.,.0065),(0.,-.010),(.0015,-.010),(.0015,.0035),(.008,.0035),(.008,.005),(.0015,.005)]
    verts=[];faces=[]
    for i,(x,z) in enumerate(contour):
        (xa,za),(xb,zb)=contour[i-1],contour[(i+1)%n]
        tx,tz=xb-xa,zb-za;length=math.hypot(tx,tz)
        ix,iz=-tz/length,tx/length          # inward in the CCW (X,Z) contour
        for inset,proud in section:
            verts.append(fs_point(x+ix*inset,z+iz*inset,proud))
    m=len(section)
    for i in range(n):
        j=(i+1)%n
        for k in range(m):
            l=(k+1)%m
            faces.append((i*m+k,j*m+k,j*m+l,i*m+l))
    obj=mesh(name,verts,faces,mat)
    import bmesh
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces[:])
    bm.to_mesh(obj.data);bm.free()
    obj['front_common_surface']=False
    obj['closes']='aqua nose ring inner edge to the proud black frit'
    return obj
fs_edge_closure('iEV7 front glazing edge closure',fs_black,black)
front_laminated=fs_patch('iEV7 single continuous curved front glass',
                        fs_front_glass,glass,proud=FS_GLASS_PROUD,bands=28)
front_laminated['single_sheet']='One uninterrupted optical surface over destination and driver clear field.'
front_laminated['front_glass_proud_m']=FS_GLASS_PROUD
# Only the outside perimeter has a seal. The edge at z=2.30 is printed ceramic
# under the sheet, with no tube, separate window or horizontal glazing joint.
fs_trace('iEV7 glazing perimeter seal',fs_front_glass,.004,rubber,FS_GLASS_PROUD+.001,True)

# A flat internal signboard sits at least 35 mm behind the cover's most recessed
# point across its rectangle. The dot matrix is 3 mm ahead of that board.
fs_board = fs_contour(1.61, 2.378, 2.647, .012, .012, taper=False)
board=fs_flat_patch('iEV7 destination board | internal flat frame',fs_board,FS_BOARD_PLANE_Y,hubdark)
board['behind_front_glass']=True
board_case=board.modifiers.new('Sign controller casing thickness','SOLIDIFY')
board_case.thickness=.016;board_case.offset=-1
fs_flat_patch('iEV7 destination board recessed dark face',
              fs_contour(1.590,2.386,2.639,.008,.008,taper=False),FS_BOARD_PLANE_Y-.0006,plastic)
n=len(fs_display_opening)
case_vertices=[fs_point(x,z,FS_CERAMIC_PROUD) for x,z in fs_display_opening]+[(x,FS_BOARD_PLANE_Y,z) for x,z in fs_board]
case_faces=[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
sidewalls=mesh('iEV7 destination aperture | recessed internal sidewalls',case_vertices,case_faces,plastic)
# The open display well is visible from the cab below it too. Keep its
# original face toward the aperture and add the real outside wall and rims.
well_gauge=sidewalls.modifiers.new('Recessed display well real wall thickness','SOLIDIFY')
well_gauge.thickness=.004;well_gauge.offset=-1;well_gauge.use_rim=True
well_gauge.use_even_offset=True
sidewalls['front_common_surface']=False
sidewalls['display_well_wall_thickness_m']=.004
# The preview label is replaced by real font dots in fine_detail.py. It starts
# behind the glass too, so running this module alone never produces a front box.
label('iEV7 front amber destination','申沃客车',(0,FS_LED_PLANE_Y,FS_LED_CENTER_Z),.205,led,(0,-1,0))

# Build-time evidence for the common cover and the recessed flat sign. This
# describes the generated geometry, not an assumption based on object names.
_fs_parent=list(range(len(front_laminated.data.vertices)))
def _fs_find(v):
    while _fs_parent[v]!=v:
        _fs_parent[v]=_fs_parent[_fs_parent[v]];v=_fs_parent[v]
    return v
_fs_edges={}
for _fs_face in front_laminated.data.polygons:
    _fs_indices=list(_fs_face.vertices)
    for _fs_a,_fs_b in zip(_fs_indices,_fs_indices[1:]+_fs_indices[:1]):
        _fs_parent[_fs_find(_fs_b)]=_fs_find(_fs_a)
        _fs_edge=tuple(sorted((_fs_a,_fs_b)))
        _fs_edges[_fs_edge]=_fs_edges.get(_fs_edge,0)+1
_fs_components=len({_fs_find(i) for i in range(len(_fs_parent))})
_fs_board_gaps=[];_fs_led_gaps=[]
for _fs_i in range(65):
    for _fs_j in range(65):
        _fs_x=-FS_BOARD_HALF_WIDTH+2*FS_BOARD_HALF_WIDTH*_fs_i/64
        _fs_z=FS_BOARD_BOTTOM+(FS_BOARD_TOP-FS_BOARD_BOTTOM)*_fs_j/64
        _fs_glass_y=fs_nose_y(_fs_z,_fs_x)-FS_GLASS_PROUD
        _fs_board_gaps.append(FS_BOARD_PLANE_Y-_fs_glass_y)
        _fs_led_gaps.append(FS_LED_PLANE_Y-_fs_glass_y)
_fs_glazing_report={
    'status':'PASS' if _fs_components==1 and min(_fs_led_gaps)>0 else 'FAIL',
    'glass_object':front_laminated.name,
    'common_front_glass_panes':1,
    'mesh_connected_components':_fs_components,
    'mesh_vertices':len(front_laminated.data.vertices),
    'mesh_faces':len(front_laminated.data.polygons),
    'boundary_edges':sum(count==1 for count in _fs_edges.values()),
    'single_sheet_covers':['destination display','driver clear field'],
    'separate_glass_at_clear_field_top':False,
    'profile_interpolation':'Natural cubic spline (C2) with a quintic C2 shoulder blend; one analytic normal field for all front materials',
    'profile_stations':FRONT_PROFILE,
    'board_bounds_xz':[-FS_BOARD_HALF_WIDTH,FS_BOARD_HALF_WIDTH,FS_BOARD_BOTTOM,FS_BOARD_TOP],
    'board_plane_blender_y_m':FS_BOARD_PLANE_Y,
    'led_plane_blender_y_m':FS_LED_PLANE_Y,
    'clearance_sampling':'65 x 65 points over board bounds; positive +Y is behind the glass',
    'board_to_glass_clearance_m':{'min':min(_fs_board_gaps),'max':max(_fs_board_gaps)},
    'led_to_glass_clearance_m':{'min':min(_fs_led_gaps),'max':max(_fs_led_gaps)},
}
(SRC/'front-glazing-report.json').write_text(json.dumps(_fs_glazing_report,indent=2),encoding='utf-8')
if _fs_glazing_report['status']!='PASS':raise ValueError('Front glass must be one connected cover ahead of the LED hardware')

# Small upper marker lamps and the forward camera, visible in the official
# head-on image.  These details are flush with the curved front rather than
# widening the crown.
for fs_side in (-1, 1):
    fs_x = fs_side * .946
    fs_marker = [(x+fs_x, z+2.540) for x, z in
                 fs_contour(.068, -.018, .018, .015, .015, taper=False)]
    fs_patch('iEV7 upper white clearance marker '+str(fs_side),
             fs_marker, chrome, .013, 2)
fs_patch('iEV7 small front camera mount', fs_circle(0, 2.805, .024, 24),
         plastic, .012, 2)
fs_patch('iEV7 front camera lens', fs_circle(0, 2.805, .012, 20),
         black, .017, 2)

# Two large, genuinely circular flush headlights in the lower BLACK mask.
# Concentric thin rings match the distinctive iEV7 eyes instead of large
# protruding cylindrical lamps.  All discs follow the same curved body skin.
for fs_side in (-1, 1):
    fs_x, fs_z = fs_side*.925, .937
    fs_patch('iEV7 main lamp black socket '+str(fs_side),
             fs_circle(fs_x, fs_z, .123), black, .006, 3)
    fs_lens('iEV7 main lamp bright outer rim '+str(fs_side),
            fs_x, fs_z, .113, .105, chrome, .012)
    fs_lens('iEV7 main lamp dark reveal '+str(fs_side),
            fs_x, fs_z, .104, .092, hubdark, .014)
    fs_lens('iEV7 circular daytime halo '+str(fs_side),
            fs_x, fs_z, .091, .082, drl, .017)
    # its casing: fs_patch's body down into the skin
    fs_patch('iEV7 projector dark bowl '+str(fs_side),
             fs_circle(fs_x, fs_z, .080), plastic, .015, 3)
    fs_lens('iEV7 projector internal chrome '+str(fs_side),
            fs_x, fs_z, .055, .047, chrome, .020)
    fs_patch('iEV7 low beam lens '+str(fs_side),
             fs_circle(fs_x, fs_z, .046), head, .024, 4)

# Four small fog/auxiliary lights below, two per side, in the aqua bonnet.
for fs_side in (-1, 1):
    for fs_index, fs_x0 in enumerate((.842, .989)):
        fs_x, fs_z = fs_side*fs_x0, .637
        fs_patch('iEV7 fog socket %s.%s' % (fs_side, fs_index),
                 fs_circle(fs_x, fs_z, .053), rubber, .005, 2)
        fs_lens('iEV7 fog chrome bezel %s.%s' % (fs_side, fs_index),
                fs_x, fs_z, .047, .038, chrome, .010)
        fs_patch('iEV7 fog glass %s.%s' % (fs_side, fs_index),
                 fs_circle(fs_x, fs_z, .035), head, .014, 3)

# The proper small rounded rectangular Sunwin emblem, with a stylised linked
# S mark.  It is centred between the eyes, never a large free-standing glyph.
fs_badge = fs_contour(.166, .852, 1.022, .027, .027, taper=False)
fs_patch('iEV7 badge graphite recess', fs_badge, hubdark, .009, 3)
fs_trace('iEV7 badge chrome rectangular surround', fs_badge, .0065, chrome, .017, True)
fs_trace('iEV7 badge inset border',
         fs_contour(.140, .865, 1.009, .024, .024, taper=False),
         .0025, chrome, .018, True)
fs_smark = [(-.047,.890),(-.043,.879),(-.022,.877),(.004,.897),(.027,.929),
            (.044,.961),(.044,.980),(.031,.988),(.010,.984),(-.019,.959),
            (-.043,.929),(-.047,.910),(-.033,.905),(-.012,.916),(.018,.951)]
fs_trace('iEV7 badge linked S insignia', fs_smark, .005, chrome, .024)

# Bonnet service seam: U-shaped sides and a soft horizontal bottom line.
fs_seam = [(-1.105,.798),(-1.107,.704),(-1.100,.574),(-1.077,.492),
           (-1.029,.439),(-.951,.403),(-.824,.386),(-.60,.381),(0,.379),
           (.60,.381),(.824,.386),(.951,.403),(1.029,.439),(1.077,.492),
           (1.100,.574),(1.107,.704),(1.105,.798)]
fs_trace('iEV7 bonnet U shaped service seam', fs_seam, .0045, rubber, .006)
fs_plate_mat = material('iEV7 brushed light number plate', (.68,.71,.66), .45, .2)
fs_plate_outline = fs_contour(.485, .490, .630, .004, .004, taper=False)
fs_patch('iEV7 flush rectangular number plate', fs_plate_outline, fs_plate_mat, .009, 2)
fs_trace('iEV7 number plate black keyline',
         fs_contour(.449, .505, .615, .004, .004, taper=False),
         .0018, plastic, .013, True)
fs_label('iEV7 front plate lettering', '申沃客车', 0, .561, .080, black, .017)
fs_patch('iEV7 short lower white reflector',
         fs_contour(.68, .428, .459, .010, .010, taper=False),
         green, .007, 2)
for fs_x in (-.883,-.484,.484,.883):
    fs_patch('iEV7 bonnet parking sensor '+str(fs_x),
             fs_circle(fs_x, .475, .010, 16), chrome, .008, 2)

# Short opposed wipers park along the LOWER field, as in the photographs.
# The original tall upright stalks obscured most of the panoramic glass.
for fs_side in (-1, 1):
    fs_x = fs_side*.893
    # A solid mounting boss (fs_patch's body, 33 mm into the skin): only its
    # original front samples the common face; its side/back normals are kept.
    fs_patch('iEV7 wiper spindle '+str(fs_side),
             fs_circle(fs_x, 1.176, .025, 24), plastic, .029, 2)
fs_trace('iEV7 left parked wiper arm',
         [(-.893,1.176),(-.83,1.197),(-.537,1.306),(-.445,1.339)],
         .010, plastic, .031)
fs_trace('iEV7 right parked wiper arm',
         [(.893,1.176),(.821,1.208),(.628,1.391),(.539,1.425)],
         .010, plastic, .033)
fs_trace('iEV7 lower parked wiper blade',
         [(-.635,1.328),(-.308,1.335),(.032,1.338)],
         .0068, rubber, .041)
fs_trace('iEV7 upper parked wiper blade',
         [(-.224,1.420),(.182,1.425),(.635,1.417)],
         .0068, rubber, .043)


def fs_quintic_coefficients(p0,p1,d0,d1,dd0,dd1):
    """Quintic Hermite curve with exact first/second endpoint derivatives."""
    a=p0;b=d0;c=dd0*.5
    s=p1-a-b-c;t=d1-b-2*c;u=dd1-2*c
    return (a,b,c,10*s-4*t+u*.5,-15*s+7*t-u,6*s-3*t+u*.5)


def fs_quintic_sample(coeff,t,order=0):
    result=Vector((0,0,0))
    for power,a in enumerate(coeff):
        if power<order:continue
        factor=1
        for i in range(order):factor*=power-i
        result+=a*(factor*t**(power-order))
    return result


def fs_front_edge_x(z):
    hits=[]
    for a,b in zip(fs_outer,fs_outer[1:]+fs_outer[:1]):
        if abs(b[1]-a[1])<1e-9:
            if abs(z-a[1])<1e-7:hits.extend((a[0],b[0]))
        elif min(a[1],b[1])-1e-8<=z<=max(a[1],b[1])+1e-8:
            t=(z-a[1])/(b[1]-a[1]);hits.append(a[0]+t*(b[0]-a[0]))
    return max(hits)


def fs_replace_data(obj,vertices,faces,normals=None):
    old=obj.data
    data=bpy.data.meshes.new(obj.name+' C2 surface')
    data.from_pydata(vertices,[],faces);data.update()
    for mat in old.materials:data.materials.append(mat)
    for face in data.polygons:face.use_smooth=True
    if normals is not None:data.normals_split_custom_set_from_vertices(normals)
    obj.data=data
    if old.users==0:bpy.data.meshes.remove(old)


def fs_finish_front_join():
    """Called after side_shell: join the complete crown to the existing roof.

    The previous straight ruled loft had an independent tangent at its front
    edge. This patch has the front surface's first and second derivatives at
    t=0 and the roof's first and second derivatives at t=1. Its two side-edge
    curves also define the black eyebrow's leading cut, so no rectangular
    black tab projects above the round cap.
    """
    obj=bpy.data.objects.get('Matched nose to roof shoulder loft')
    if obj is None:return
    arch=[p for p in fs_outer if p[1]>=2.69-1e-5]
    extent=max(abs(p[0]) for p in arch)
    coeffs=[]
    front_tangent_error=[];front_curvature_error=[];roof_tangent_error=[]
    def roof_derivatives(x):
        a=ROOF-2.80;u=abs(x)/SS_ROOF_HALF
        return (-7*a*math.copysign(u**6,x)/SS_ROOF_HALF,
                -42*a*u**5/(SS_ROOF_HALF**2))
    for x,z in arch:
        a=Vector(fs_point(x,z))
        tx=x/extent*SS_ROOF_HALF
        b=Vector((tx,-2.60,ss_roof_z(tx)))
        _,fx,fz,fxx,fxz,fzz=fs_surface_sample(z,x)
        # Keep enough tangent length for a visible manufactured crown radius.
        # A very short tangent was C2 but turned over in only a few mm.
        # Limit its vertical speed at the low side corners to avoid overshoot.
        dx=(b.x-a.x)*.12;dy=(b.y-a.y)*.55
        dz=(dy-fx*dx)/fz
        if abs(dz)>.18:
            scale=.18/abs(dz);dx*=scale;dy*=scale;dz*=scale
        d0=Vector((dx,dy,dz))
        dd0=Vector((0,0,-(fxx*dx*dx+2*fxz*dx*dz+fzz*dz*dz)/fz))
        roof_dx,roof_dxx=roof_derivatives(tx)
        d1=Vector(((b.x-a.x)*.45,(b.y-a.y)*.90,0))
        d1.z=roof_dx*d1.x
        dd1=Vector((0,0,roof_dxx*d1.x*d1.x))
        coeffs.append(fs_quintic_coefficients(a,b,d0,d1,dd0,dd1))
        front_tangent_error.append(abs(fx*d0.x-d0.y+fz*d0.z))
        front_curvature_error.append(abs(fx*dd0.x-dd0.y+fz*dd0.z
                                           +fxx*d0.x*d0.x+2*fxz*d0.x*d0.z+fzz*d0.z*d0.z))
        roof_tangent_error.append(abs(d1.z-roof_dx*d1.x))
    steps=12;n=len(arch);vertices=[];faces=[];normals=[]
    for row in range(steps+1):
        t=row/steps
        for i,coeff in enumerate(coeffs):
            p=fs_quintic_sample(coeff,t)
            dt=fs_quintic_sample(coeff,t,1)
            # The sampled crown direction is only used away from the joins;
            # both boundary rows receive the exact common analytic normals.
            ds=fs_quintic_sample(coeffs[min(n-1,i+1)],t)-fs_quintic_sample(coeffs[max(0,i-1)],t)
            normal=ds.cross(dt).normalized()
            if normal.z<0:normal=-normal
            if row==0:normal=fs_normal(arch[i][0],arch[i][1])
            elif row==steps:normal=Vector((-roof_derivatives(p.x)[0],0,1)).normalized()
            vertices.append(tuple(p));normals.append(tuple(normal))
    for row in range(steps):
        for i in range(n-1):
            face=(row*n+i,row*n+i+1,(row+1)*n+i+1,(row+1)*n+i)
            a,b,c=[Vector(vertices[k]) for k in face[:3]]
            if (b-a).cross(c-a).z<0:face=tuple(reversed(face))
            faces.append(face)
    fs_replace_data(obj,vertices,faces,normals)
    obj['front_c2_roof_transition']=True
    obj['front_common_surface']=False
    # The first roof row uses the same analytic roof tangent as the loft.
    roof=bpy.data.objects.get('Continuous low rounded roof')
    if roof:
        ns=[tuple(Vector((-roof_derivatives(v.co.x)[0],0,1)).normalized())
            if v.co.y<=-2.6+1e-6 else tuple(v.normal) for v in roof.data.vertices]
        roof.data.normals_split_custom_set_from_vertices(ns)
    for side in (-1,1):
        band=bpy.data.objects.get('Continuous black eyebrow '+str(side))
        if band is None:continue
        edge=coeffs[0 if arch[0][0]*side>0 else -1]
        edge_start=fs_quintic_sample(edge,0)
        # Rows closer than 0.5 mm merge: the crown edge's start lies a hair
        # from the 2.69 m row, and a second row there made zero-area faces
        # whose doubled bottom edge was no longer a boundary, so the lining
        # got no return wall along the eyebrow's front bottom edge (the
        # eyebrow seen from behind from the driver's seat).
        zs=[]
        for z in sorted([2.69+.11*i/20 for i in range(21)]+[float(edge_start.z)]):
            if not zs or z-zs[-1]>.0005:zs.append(z)
        vs=[];fs=[]
        for z in zs:
            barx=side*((SIDE+.005)*(1-(z-2.69)/.11)+(SIDE-.023)*(z-2.69)/.11)
            if z<=edge_start.z:
                xx=fs_front_edge_x(z);lead=Vector((side*xx,fs_nose_y(z,xx)+.003,z))
            else:
                lo,hi=0.,1.
                for _ in range(48):
                    mid=(lo+hi)/2
                    if fs_quintic_sample(edge,mid).z<z:lo=mid
                    else:hi=mid
                lead=fs_quintic_sample(edge,(lo+hi)/2)
                lead.y+=.003;lead.x-=side*.001
            vs.extend((tuple(lead),(barx,max(lead.y+.14,-2.46),z),(barx,3.34,z)))
        for i in range(len(zs)-1):
            for j in range(2):
                a=i*3+j;face=(a,a+1,a+4,a+3)
                if side<0:face=tuple(reversed(face))
                fs.append(face)
        fs_replace_data(band,vs,fs)
        band['front_crown_trim']='Exact quintic crown side edge; no projecting rectangular tab'
    # Exact left/right polynomial derivatives demonstrate C2, independently
    # of tessellation density or the normals Blender happens to average.
    jumps={str(column):[] for column in (1,2)}
    for column in (1,2):
        for i in range(1,len(FRONT_PROFILE)-1):
            left=fs_cubic_sample(FS_PROFILE_CUBICS[column][i-1],FRONT_PROFILE[i][0]-FRONT_PROFILE[i-1][0])
            right=fs_cubic_sample(FS_PROFILE_CUBICS[column][i],0)
            jumps[str(column)].append([abs(a-b) for a,b in zip(left,right)])
    maxima=[max(row[k] for rows in jumps.values() for row in rows) for k in range(3)]
    layers=[o for o in MASTER.objects if o.get('front_common_surface')]
    normal_error=0.;normal_error_object=''
    for ob in layers:
        for loop,normal in zip(ob.data.loops,ob.data.corner_normals):
            point=ob.data.vertices[loop.vertex_index].co
            # Blender stores custom loop normals in a quantized tangent-space
            # representation. Compare directions, not bitwise vector distance.
            error=max(0.,1-normal.vector.normalized().dot(fs_normal(point.x,point.z)))
            if error>normal_error:normal_error=error;normal_error_object=ob.name
    report={'status':'PASS' if max(maxima)<1e-9 and normal_error<1e-5 and max(p[2] for p in vertices)<3.0 else 'FAIL',
        'surface':'Y=-HALF+P(z)+.038*(x/W(z))^2+B(z)*(x/W(z))^8',
        'profile':'Natural cubic interpolation: C2 exact at all retained reference stations',
        'shoulder':'Quintic smootherstep: first and second derivatives vanish at endpoints',
        'profile_max_knot_jumps_value_d1_d2':maxima,
        'profile_knot_jumps':jumps,'analytic_normal_objects':len(layers),
        'max_mesh_corner_normal_one_minus_dot':normal_error,
        'max_mesh_corner_normal_angle_degrees':math.degrees(math.acos(max(-1.,min(1.,1-normal_error)))),
        'max_mesh_corner_normal_error_object':normal_error_object,
        'mesh_normal_tolerance_one_minus_dot':1e-5,
        'normal_rule':'normalize(dY/dX,-1,dY/dZ), common to body, frit, glass and flush lamps',
        'roof_transition':'Quintic Hermite matching front and roof first/second derivatives',
        'roof_join_tangent_residual_max':max(front_tangent_error+roof_tangent_error),
        'roof_join_curvature_residual_max':max(front_curvature_error),
        'roof_transition_max_height_m':max(p[2] for p in vertices),
        'roof_transition_mesh_vertices':len(vertices),
        'eyebrow_front_cut':'Crown side curve, with 1 mm inward clearance',
        'reference_station_positions_preserved':True}
    (SRC/'front-surface-report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print('FRONT_C2_SURFACE '+json.dumps({k:report[k] for k in ('status','profile_max_knot_jumps_value_d1_d2','analytic_normal_objects','roof_join_tangent_residual_max','roof_join_curvature_residual_max','roof_transition_max_height_m')}),flush=True)
    if report['status']!='PASS':raise ValueError('Common front surface continuity/height check failed')

