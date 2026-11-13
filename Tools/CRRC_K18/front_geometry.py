"""K18 front rebuilt from the cited 2024 photographs.

Executed in build_model's metre/Unity-coordinate namespace. Photographs constrain
the silhouettes, not hidden engineering dimensions. All surface parts share one
front envelope; the sign is inset from that envelope over its *whole* width.
"""

def front_z(x,y):
    a=min(1.,abs(x)/1.25)
    crown=math.sqrt(max(0.,1-a**8))
    t=max(0.,min(1.,(y-2.80)/.395));t=t*t*(3-2*t)
    return 8.50+crown*(.455-.170*t-.027*((y-1.50)/1.65)**2)

def front_normal(x,y):
    e=.00005
    return Vector((-(front_z(x+e,y)-front_z(x-e,y))/(2*e),
                   -(front_z(x,y+e)-front_z(x,y-e))/(2*e),1)).normalized()

def tagged(ob,role):
    ob['k18_front_role']=role
    return ob

def low_glass(x):return .95+.255*(abs(x)/1.205)**8
def high_glass(x):return 2.800-.030*(abs(x)/1.205)**8
def top_front(x):return 3.195-.115*(abs(x)/1.25)**12

def front_patch(name,width,bottom,top,material,part=0,offset=0.,thickness=.026,
                role='detail',grids=((44,8),(22,4),(12,3)),x_range=None):
    """Closed variable-outline ribbon, including the back and four real edges."""
    result=None
    for level,(nx,ny) in enumerate(grids):
        xs=[width*math.sin((i/nx-.5)*math.pi) for i in range(nx+1)]
        if width==1.25:
            # Actual shared seam vertices; different curve sampling would leave
            # sliver openings even when the analytic boundary functions agree.
            xs=[-1.25]+[1.205*math.sin((i/nx-.5)*math.pi) for i in range(nx+1)]+[1.25]
            nx=len(xs)-1
        if x_range:
            xs=[x_range[0]+(x_range[1]-x_range[0])*i/nx for i in range(nx+1)]
        verts=[]
        for dz in (offset,offset-thickness):
            for j in range(ny+1):
                for i in range(nx+1):
                    # Cluster samples where the curved face turns into its sides.
                    x=xs[i]
                    lo=bottom(x) if callable(bottom) else bottom
                    hi=top(x) if callable(top) else top
                    y=lo+(hi-lo)*j/ny
                    verts.append((x,y,front_z(x,y)+dz))
        row=nx+1;half=row*(ny+1);faces=[]
        for j in range(ny):
            for i in range(nx):
                p=j*row+i
                faces.extend(((p,p+1,p+row+1,p+row),
                              (half+p+row,half+p+row+1,half+p+1,half+p)))
        ring=list(range(row))+[j*row+nx for j in range(1,ny+1)]+list(range(ny*row+nx-1,ny*row-1,-1))+[j*row for j in range(ny-1,0,-1)]
        faces.extend((a,b,half+b,half+a) for a,b in zip(ring,ring[1:]+ring[:1]))
        ob=tagged(mesh(('LOD'+str(level)+' ' if level else '')+name,verts,faces,material,part,lod=level),role)
        ob['front_surface_offset_m']=offset;ob['front_thickness_m']=thickness
        ob['first_lod']=level
        for p in ob.data.polygons:p.use_smooth=p.index<2*nx*ny
        if level:ob.hide_render=True;ob.hide_set(True)
        else:result=ob
    return result

def front_destination_surround():
    """One 8 mm opaque sheet with a real aft fold around the opening.

    A rectangular XY grid keeps neighboring curved samples aligned.
    The front, back and return share vertices; no overlapping cover faces.
    """
    for level, (nx, ny) in enumerate(((44, 8), (22, 4), (12, 3))):
        xs = sorted(set([-1.205, -.883, -.875, .875, .883, 1.205] +
                        [1.205*math.sin((i/nx-.5)*math.pi) for i in range(nx+1)]))
        ys = [2.355, 2.395]+[2.403+.335*j/ny for j in range(ny+1)]+[2.746, 2.800]
        verts, faces, smooth, vertex_index = [], [], [], {}
        depths = (-.013, -.021, -.040)
        def point(i, j):
            return xs[i], high_glass(xs[i]) if j == len(ys)-1 else ys[j]
        def vertex(i, j, layer):
            key = (i, j, layer)
            if key not in vertex_index:
                x, y = point(i, j)
                vertex_index[key] = len(verts)
                verts.append((x, y, front_z(x, y)+depths[layer]))
            return vertex_index[key]
        def quad(corners, layer, use_smooth=True):
            faces.append(tuple(vertex(i, j, layer) for i, j in corners))
            smooth.append(use_smooth)
        def inside(x, y, width, bottom, top):
            return -width < x < width and bottom < y < top
        boundaries = []
        for layer in range(3):
            edge_counts = {}
            for j in range(len(ys)-1):
                for i in range(len(xs)-1):
                    x = (xs[i]+xs[i+1])/2
                    y = sum(point(ii, jj)[1] for ii, jj in
                            ((i,j),(i+1,j),(i+1,j+1),(i,j+1)))/4
                    aperture = inside(x,y,.875,2.403,2.738)
                    rear_aperture = inside(x,y,.883,2.395,2.746)
                    keep = (not aperture if layer == 0 else
                            not rear_aperture if layer == 1 else rear_aperture and not aperture)
                    if not keep:
                        continue
                    corners = ((i,j),(i+1,j),(i+1,j+1),(i,j+1))
                    quad(corners, layer)
                    for u, v in zip(corners, corners[1:]+corners[:1]):
                        edge = tuple(sorted((u,v)))
                        edge_counts[edge] = edge_counts.get(edge,0)+1
            boundaries.append([e for e,count in edge_counts.items() if count == 1])
        for layer in (0,1):
            for u, v in boundaries[layer]:
                exterior = ((u[0] == v[0] and u[0] in (0,len(xs)-1)) or
                            (u[1] == v[1] and u[1] in (0,len(ys)-1)))
                if exterior and layer == 1:
                    continue
                other = 1 if exterior else 2
                faces.append((vertex(*u,layer),vertex(*v,layer),
                              vertex(*v,other),vertex(*u,other)))
                smooth.append(False)
        ob = tagged(mesh(('LOD'+str(level)+' ' if level else '')+'K18 destination mask folded surround',
                         verts, faces, BLACK, 17, lod=level), 'frit')
        ob['first_lod'] = level
        ob['mask_sheet_thickness_m'] = .008
        ob['opening_return_back_offset_m'] = -.040
        for poly in ob.data.polygons:
            poly.use_smooth = smooth[poly.index]
        if level:
            ob.hide_render = True
            ob.hide_set(True)

def front_inset_sign_housing():
    """Closed pressed pan, with real returns into all four frit edges.

    The 1.75 x .335 m screen bed is unchanged. Its old coplanar-height
    boundary sat 51 mm behind the frit and left an oblique sightline to sky.
    The surrounding 8 mm flange and sloped return are part of this pan,
    not a separate cover over that opening.
    """
    result = None
    for level, (nx, ny) in enumerate(((44, 8), (22, 4), (12, 3))):
        xs = [-.883]+[.875*math.sin((i/nx-.5)*math.pi) for i in range(nx+1)]+[.883]
        ys = [2.395]+[2.403+.335*j/ny for j in range(ny+1)]+[2.746]
        row, rows = len(xs), len(ys)
        verts = []
        for back in (False, True):
            for j, y in enumerate(ys):
                for i, x in enumerate(xs):
                    flange = i in (0, row-1) or j in (0, rows-1)
                    # Meet the actual folded surround return at -.035;
                    # the windscreen clearance remains above 12 mm.
                    offset = -.076 if back else (-.035 if flange else -.060)
                    verts.append((x, y, front_z(x, y)+offset))
        half = row*rows
        faces, smooth = [], []
        for j in range(rows-1):
            for i in range(row-1):
                p = j*row+i
                faces.extend(((p, p+1, p+row+1, p+row),
                              (half+p+row, half+p+row+1, half+p+1, half+p)))
                smooth.extend((0 < i < row-2 and 0 < j < rows-2, True))
        ring = list(range(row))+[j*row+row-1 for j in range(1, rows)]+list(range(half-2, half-row-1, -1))+[j*row for j in range(rows-2, 0, -1)]
        faces.extend((a, b, half+b, half+a) for a, b in zip(ring, ring[1:]+ring[:1]))
        ob = tagged(mesh(('LOD'+str(level)+' ' if level else '')+'Front inset destination housing',
                         verts, faces, DARK, 14, lod=level), 'sign')
        ob['first_lod'] = level
        ob['front_surface_offset_m'] = -.060
        ob['front_thickness_m'] = .016
        ob['screen_bed_bounds'] = [-.875, .875, 2.403, 2.738]
        ob['frit_return_offset_m'] = -.035
        ob['frit_return_flange_m'] = .008
        for poly in ob.data.polygons:
            poly.use_smooth = smooth[poly.index] if poly.index < len(smooth) else False
        if level:
            ob.hide_render = True
            ob.hide_set(True)
        else:
            result = ob
    return result

def front_polygon(name,xy,material,part=16,offset=.015,thickness=.010,role='detail',surface=None,lod=2):
    """Convex face subdivided radially, with matching sealed back and rim."""
    cx=sum(p[0] for p in xy)/len(xy);cy=sum(p[1] for p in xy)/len(xy)
    verts=[];n=len(xy);nr=4;surface=surface or front_z
    for dz in (offset,offset-thickness):
        verts.append((cx,cy,surface(cx,cy)+dz))
        for j in range(1,nr+1):
            for x,y in xy:
                xx=cx+(x-cx)*j/nr;yy=cy+(y-cy)*j/nr
                verts.append((xx,yy,surface(xx,yy)+dz))
    half=1+n*nr;faces=[]
    for q in (0,half):
        faces.extend((q,q+1+i,q+1+(i+1)%n) for i in range(n))
        for j in range(nr-1):
            a=q+1+j*n;b=a+n
            faces.extend((a+i,b+i,b+(i+1)%n,a+(i+1)%n) for i in range(n))
    start=1+(nr-1)*n
    faces.extend((start+i,start+(i+1)%n,half+start+(i+1)%n,half+start+i) for i in range(n))
    ob=tagged(mesh(name,verts,faces,material,part,lod=lod),role)
    for p in ob.data.polygons:p.use_smooth=len(p.vertices)==4 and p.index<2*(n+(nr-1)*n)
    return ob

def rounded_triangle(points,r=.10,steps=5):
    out=[]
    for i,b in enumerate(points):
        a=points[i-1];c=points[(i+1)%len(points)]
        p=Vector(b).lerp(Vector(a),r);q=Vector(b).lerp(Vector(c),r)
        for j in range(steps):
            t=j/(steps-1)
            v=(1-t)**2*p+2*(1-t)*t*Vector(b)+t*t*q
            out.append(tuple(v))
    return out

def front_trace(name,xy,r,material,part=16,offset=.010):
    pts=[]
    for a,b in zip(xy,xy[1:]):
        for i in range(6):
            x=a[0]+(b[0]-a[0])*i/6;y=a[1]+(b[1]-a[1])*i/6
            pts.append((x,y,front_z(x,y)+offset))
    x,y=xy[-1];pts.append((x,y,front_z(x,y)+offset))
    path(name,pts,r,material,part)

def bridge_ribbon(name,ys,material,top=False):
    """Actual floor/roof closure from the side body's end to the new nose."""
    if top:
        # One U-shaped solid. Separate side plates and a capped roof would
        # overlap in the same x plane and produce a black self-shadowed strip.
        for level,(nx,ny) in enumerate(((44,12),(22,6),(12,4))):
            xs=[-1.25]+[1.205*math.sin((i/nx-.5)*math.pi) for i in range(nx+1)]+[1.25]
            v=[];n=len(xs)+2
            for inner in (False,True):
                for j in range(ny+1):
                    t=j/ny;s=t*t*(3-2*t);edge=3.095-.325*s
                    # Left bottom, whole curved roof, right bottom.
                    v.append((-1.226 if inner else -1.25,edge,7.35+1.15*t))
                    for x in xs:
                        rear_y=3.195-.035*(abs(x)/1.25)**12
                        y=(1-s)*rear_y+s*top_front(x)-(.026 if inner else 0.)
                        z=(1-t)*7.35+t*front_z(x,top_front(x))
                        v.append((x*(1.226/1.25 if inner else 1.),y,z))
                    v.append((1.226 if inner else 1.25,edge,7.35+1.15*t))
            half=n*(ny+1);f=[]
            for j in range(ny):
                for i in range(n-1):
                    q=j*n+i
                    f.extend(((q,q+1,q+1+n,q+n),(half+q+n,half+q+1+n,half+q+1,half+q)))
            # Close the lower side edges and front/rear annular ends only.
            for j in range(ny):
                for i in (0,n-1):
                    a=j*n+i;b=a+n;f.append((a,b,half+b,half+a))
            for j in (0,ny):
                for i in range(n-1):
                    a=j*n+i;f.append((a,a+1,half+a+1,half+a))
            ob=tagged(mesh(('LOD'+str(level)+' ' if level else '')+name,v,f,material,0,lod=level),'shell')
            ob['first_lod']=level
            for poly in ob.data.polygons:poly.use_smooth=poly.index<2*(n-1)*ny
            if level:ob.hide_render=True;ob.hide_set(True)
        return ob
    nx=48;v=[]
    for thick in (0.,-.055):
        for row in (0,1):
            for i in range(nx+1):
                x=1.205*math.sin((i/nx-.5)*math.pi)
                y=(top_front(x) if top and row else ys)+thick
                z=front_z(x,y)-.028 if row else 8.48
                v.append((x,y,z))
    n=nx+1;f=[]
    for k in range(nx):
        f.extend(((k,k+1,n+k+1,n+k),(2*n+k,3*n+k,3*n+k+1,2*n+k+1),
                  (k,2*n+k,2*n+k+1,k+1),(n+k,n+k+1,3*n+k+1,3*n+k)))
    f.extend(((0,n,3*n,2*n),(n-1,3*n-1,4*n-1,2*n-1)))
    return tagged(mesh(name,v,f,material,0),'ceiling' if top else 'floor')

def build_nose_refined():
    select_section('front')
    # The red cap wraps down the last side-roof bay. Replace that part of the
    # rectangular black band/eave rather than placing another painted box over it.
    for side in (-1,1):
        for ma,upper in ((BLACK,False),):
            n=24;v=[]
            for dx in (0.,-side*.024):
                for row in (0,1):
                    for i in range(n+1):
                        t=i/n;z=7.35+1.15*t;s=t*t*(3-2*t)
                        edge=3.095-.325*s
                        topy=(1-s)*3.160+s*top_front(1.25)
                        y=(edge if row==0 else topy) if upper else (2.625 if row==0 else edge)
                        v.append((side*1.25+dx,y,z))
            nn=n+1;f=[]
            for i in range(n):
                f.extend(((i,i+1,nn+i+1,nn+i),(2*nn+i,3*nn+i,3*nn+i+1,2*nn+i+1),
                          (i,2*nn+i,2*nn+i+1,i+1),(nn+i,nn+i+1,3*nn+i+1,3*nn+i)))
            f.extend(((0,nn,3*nn,2*nn),(n,2*nn-1,4*nn-1,3*nn-1)))
            tagged(mesh('K18 cap side return '+str(side)+' '+str(upper),v,f,ma,0 if upper else 17),'shell')
    # Painted skin and glazing meet along exactly the same sampled outlines.
    front_patch('K18 rounded red upper cap',1.25,lambda x:high_glass(min(1.205,max(-1.205,x))),top_front,RED,role='shell')
    front_patch('K18 lower curved front skin',1.25,.22,lambda x:low_glass(min(1.205,max(-1.205,x))),RED,role='shell')
    # Narrow A pillars continue the real front envelope to the side shell.
    for side in (-1,1):
        for level,(ny,nx) in enumerate(((28,8),(14,4),(8,3))):
            vv=[]
            for dz in (0.,-.036):
                for j in range(ny+1):
                    for i in range(nx+1):
                        x=side*(1.205+.045*math.sin(i/nx*math.pi/2))
                        y=low_glass(side*1.205)+(high_glass(side*1.205)-low_glass(side*1.205))*j/ny
                        vv.append((x,y,front_z(x,y)+dz))
            row=nx+1;half=row*(ny+1);ff=[]
            for j in range(ny):
                for i in range(nx):
                    p=j*row+i;ff.extend(((p,p+1,p+row+1,p+row),(half+p+row,half+p+row+1,half+p+1,half+p)))
            ring=list(range(row))+[j*row+nx for j in range(1,ny+1)]+list(range(ny*row+nx-1,ny*row-1,-1))+[j*row for j in range(ny-1,0,-1)]
            ff.extend((a,b,half+b,half+a) for a,b in zip(ring,ring[1:]+ring[:1]))
            ob=tagged(mesh(f'K18 front corner pillar {side} LOD{level}',vv,ff,BLACK,0,lod=level),'shell');ob['first_lod']=level
            for p in ob.data.polygons:p.use_smooth=p.index<2*nx*ny
            if level:ob.hide_render=True;ob.hide_set(True)
    front_patch('K18 single panoramic front glazing',1.205,low_glass,high_glass,FRONT_GLASS,17,offset=.003,thickness=.012,role='glass')
    front_destination_surround()
    front_patch('K18 curved lower ceramic mask',1.205,low_glass,lambda x:1.255+.045*(abs(x)/1.205)**8,BLACK,17,offset=-.013,thickness=.008,role='frit')
    # The actual inner front bulkhead separates the cab from lamp cavities.
    # It is a full connected inner shell, with a reveal back to the windscreen
    # sill, rather than exposed backs of exterior headlamp reflectors.
    front_patch('K18 cabin front bulkhead',1.205,.30,low_glass,DARK,0,offset=-.145,thickness=.016,role='shell')
    front_patch('K18 windshield lower internal reveal',1.205,lambda x:low_glass(x)-.018,
                low_glass,BLACK,0,offset=-.009,thickness=.152,role='shell',grids=((44,1),(22,1),(12,1)))
    # Sign is curved, not a flat cuboid intersecting the windscreen corners.
    sign=front_inset_sign_housing()
    redled=mat('K18 photographed red destination',(1,.012,.009),.40,0,'LampRed',light=9)
    # Preview lettering is behind glass; runtime LED is a separate generated part.
    font=bpy.data.fonts.load('C:/Windows/Fonts/msyh.ttc')
    words='中车电动巴客龙';spacing=.223
    for i,char in enumerate(words):
        x=((len(words)-1)/2-i)*spacing;y=2.574
        ob=text('K18 reference destination '+str(i),char,(x,y,front_z(x,y)-.041),.217,redled)
        ob.data.font=font;ob['runtime_generated']='LedSign';tagged(ob,'sign')
    # Header camera and shallow pressed styling line follow the body surface.
    front_trace('K18 cap shallow pressed seam',[(-.96,2.922),(-.88,3.064),(-.65,3.11),(.65,3.11),(.88,3.064),(.96,2.922)],.0027,RED,offset=.001)
    x,y=0,2.883;z=front_z(x,y)
    tagged(box('K18 front camera housing',(x,y,z+.013),(.072,.052,.032),DARK,16,bevel=.012),'detail')
    rod('K18 front camera lens',(x,y,z+.030),(x,y,z+.034),.017,BLACK,16,n=16)
    # A broad, almost level silver bar; full centre span and raised narrow ends.
    front_patch('K18 continuous silver crossbar',.926,lambda x:.526+.109*(abs(x)/.926)**6,
                lambda x:.688+.018*(abs(x)/.926)**5,METAL,16,offset=.018,thickness=.014,grids=((36,4),(18,2),(10,2)))
    front_trace('K18 crossbar bright upper lip',[(-.924,.707),(-.45,.689),(.45,.689),(.924,.707)],.005,CHROME,offset=.022)
    for side in (-1,1):
        build_k18_headlamp(side)
        for j in range(5):
            a=.605+j*.045
            front_polygon('K18 crossbar end slot',[(side*a,.536),(side*(a+.028),.536),(side*(a+.075),.624),(side*(a+.047),.630)],DARK,16,offset=.036,thickness=.005)
        build_k18_foglamp(side)
        front_trace('K18 bumper diagonal panel joint',[(side*.79,.47),(side*.63,.235)],.0022,BLACK,offset=.001)
        front_trace('K18 outer bumper joint',[(side*1.218,.493),(side*.842,.502)],.0018,BLACK,offset=.001)
        # Short real parked wipers, surface sampled rather than long straight rods.
        x=side*.942;y=1.001;z=front_z(x,y)
        rod('K18 wiper spindle',(x,y,z+.003),(x,y,z+.026),.026,DARK,15,n=16)
        front_trace('K18 parked wiper arm',[(x,y),(side*.62,1.199),(side*.37,1.291)],.008,DARK,15,offset=.036)
        front_trace('K18 parked wiper blade',[(side*.70,1.285),(side*.38,1.304),(side*.052,1.319)],.012,DARK,15,offset=.015)
        box('Front wheel cab partition',(side*.87,1.04,6.25),(.61,.035,1.18),PANEL,12)
    front_patch('K18 front registration plate',.258,.277,.479,PANEL,16,offset=.028,thickness=.012,grids=((16,2),(8,2),(6,2)))
    ob=text('K18 registration lettering','中国中车',(0,.381,front_z(0,.381)+.031),.104,DARK);ob.data.font=font
    bridge_ribbon('K18 curved nose floor closure',.300,FLOOR)
    bridge_ribbon('K18 roof to cap structural closure',3.195,RED,top=True)
    # Inner forehead from existing ceiling to the sign's concealed rear housing.
    tagged(box('K18 inner forehead connection',(0,2.914,8.66),(1.84,.065,.40),PANEL,14,bevel=.012),'shell')
    scene['k18_front_contract']=json.dumps({'max_forward':9.04,'max_half_width':1.27,'max_height':3.22,'min_sign_gap_m':.012})
    scene['front_reference_revision']='2024-photo-refinement-1; measured photo landmarks; camera calibration separate'

def build_mirrors_refined():
    select_section('front')
    previous=set(CURRENT.objects)
    for side in (-1,1):
        # Photo-specific asymmetric support: near door side has long twin rails
        # and a round auxiliary mirror; opposite side has a compact upright arm.
        x=side*1.231;y=2.565;z=front_z(x,y)
        for level,nx in ((0,8),(1,4)):
            # Sample the actual pillar's piecewise surface at this LOD, not an
            # unrelated smooth approximation that can sink beneath its chords.
            def pillar_surface(px,py,nx=nx):
                ax=abs(px);xs=[1.205+.045*math.sin(i/nx*math.pi/2) for i in range(nx+1)]
                i=next((i for i in range(nx) if xs[i]<=ax<=xs[i+1]),nx-1)
                t=(ax-xs[i])/(xs[i+1]-xs[i])
                return (1-t)*front_z(xs[i],py)+t*front_z(xs[i+1],py)
            foot=front_polygon(('LOD1 ' if level else '')+'K18 mirror structural mounting shoe '+str(side),
                               [(side*1.215,2.48),(side*1.245,2.48),(side*1.245,2.65),(side*1.215,2.65)],
                               DARK,15,offset=.055,thickness=.100,role='mirror_mount',surface=pillar_surface,lod=level)
            foot['mount_side']=side;foot['first_lod']=level
            foot['mount_layer_axis_unity']=[0.,0.,1.]
            if level:foot.hide_render=True;foot.hide_set(True)
        if side>0:
            # Joint fit to hero position and frontal body-relative overhang.
            # Dimensions remain photographic estimates, recorded separately.
            xx=1.456;zz=9.64
            for yy in (2.50,2.67):
                path('K18 door-side twin mirror support',[(1.231,yy,z-.015),(1.30,yy+.032,8.79),(1.42,yy+.032,zz),(xx,yy+.010,zz)],.021,METAL,15)
            path('K18 door-side downturned mirror stem',[(xx,2.69,zz),(xx+.024,2.65,zz),(xx+.024,2.372,zz)],.028,DARK,15)
            box('K18 door-side mirror body',(xx+.024,2.15,zz),(.240,.440,.13),DARK,15,bevel=.045)
            box('K18 door-side reflective insert',(xx+.024,2.15,zz-.070),(.202,.395,.009),CHROME,15,bevel=.030)
            p=Vector((1.27,2.40,9.64));n=Vector((-.2,-.4,-1)).normalized()
            path('K18 auxiliary mirror connecting bracket',[(xx+.024,2.49,zz),(1.37,2.46,zz),(1.27,2.40,zz)],.012,METAL,15)
            rod('K18 round auxiliary mirror rim',p-n*.018,p+n*.004,.090,DARK,15,n=32)
            # The supplementary optic is a real convex spherical cap; its
            # rim plane/depth also drive the runtime reflection surface.
            radius=.076;depth=.010;centre=p+n*.004
            sphere=(radius*radius+depth*depth)/(2*depth)
            ax=n.cross(Vector((0,1,0))).normalized();ay=n.cross(ax).normalized()
            segments=32;radial=6;verts=[tuple(centre+n*depth)];faces=[]
            for j in range(1,radial+1):
                r=radius*j/radial;sag=math.sqrt(sphere*sphere-r*r)-(sphere-depth)
                for k in range(segments):
                    a=2*math.pi*k/segments
                    verts.append(tuple(centre+ax*(r*math.cos(a))+ay*(r*math.sin(a))+n*sag))
            for k in range(segments):faces.append((0,1+k,1+(k+1)%segments))
            for j in range(radial-1):
                a=1+j*segments;b=a+segments
                for k in range(segments):faces.append((a+k,b+k,b+(k+1)%segments,a+(k+1)%segments))
            outer=1+(radial-1)*segments;back=len(verts)
            verts.append(tuple(centre-n*.002))
            for k in range(segments):faces.append((back,outer+(k+1)%segments,outer+k))
            optic=mesh('K18 round auxiliary reflective face',verts,faces,CHROME,15)
            for f in optic.data.polygons:f.use_smooth=f.index<segments*(radial-1)+segments
            optic['mirror_base_origin_unity']=list(centre);optic['mirror_normal_unity']=list(n)
            optic['mirror_convex_depth_m']=depth;optic['mirror_radius_m']=radius
        else:
            xx=-1.48;zz=8.79
            path('K18 driver-side compact mirror arm',[(x,y,z-.026),(-1.36,2.58,8.65),(xx,2.57,zz),(xx,2.41,zz)],.025,DARK,15)
            box('K18 driver-side mirror body',(xx,2.18,zz),(.185,.425,.118),DARK,15,bevel=.033)
            box('K18 driver-side reflective insert',(xx,2.18,zz-.064),(.15,.385,.008),CHROME,15,bevel=.025)
    for ob in set(CURRENT.objects)-previous:
        ob['k18_mirror_assembly']=True
