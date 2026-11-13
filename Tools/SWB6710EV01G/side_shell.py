"""iEV7 side skin, sampled from the official orthographic profile references.
Executed in build_model.py's geometry namespace.
"""
DOORS=[(-1.24,1.12),(1.42,.88)]

def ss_bottom(y):
    z=.24
    for ay in AXLES:
        if abs(y-ay)<R+.042:
            z=max(z,R+math.sqrt(max(0,(R+.042)**2-(y-ay)**2)))
    return z

def ss_front_y(z):
    # Raked windscreen, curved bumper and domed front crown (metres).
    keys=[(.24,-3.39),(.45,-3.55),(.85,-3.56),(1.15,-3.51),(1.7,-3.40),(2.25,-3.26),(2.65,-3.12),(2.96,-2.93)]
    for (za,ya),(zb,yb) in zip(keys,keys[1:]):
        if za<=z<=zb:return ya+(yb-ya)*(z-za)/(zb-za)
    return keys[0][1] if z<keys[0][0] else keys[-1][1]

for side in [-1,1]:
    # Aqua quarter panels with open wheel arches and real entry gaps.
    ys=sorted(set([-3.09,3.37]+[-3.09+6.46*i/280 for i in range(281)]+
                  [v for ay in AXLES for v in (ay-R-.042,ay+R+.042)]+
                  [v for cy,dw in DOORS for v in (cy-dw/2,cy+dw/2)]))
    vs=[];fs=[]
    for a,b in zip(ys,ys[1:]):
        if side==1 and any(abs((a+b)/2-cy)<dw/2 for cy,dw in DOORS):continue
        off=len(vs)
        vs += [(side*(SIDE-.013),a,ss_bottom(a)),(side*(SIDE-.013),b,ss_bottom(b)),
               (side*SIDE,b,1.12),(side*SIDE,a,1.12)]
        f=(off,off+1,off+2,off+3)
        fs.append(f if side==1 else tuple(reversed(f)))
    mesh(f'Aqua side quarter panels {side}',vs,fs,white)
    # The entire return shares the real front perimeter. A partial corner patch
    # folded the bumper's lower arc onto itself, exposing its grey inside skin.
    def front_edge_x(z):
        hits=[]
        for a,b in zip(fs_outer,fs_outer[1:]+fs_outer[:1]):
            if abs(b[1]-a[1])<1e-8:
                if abs(z-a[1])<1e-6:hits.extend((a[0],b[0]))
            elif min(a[1],b[1])-1e-7<=z<=max(a[1],b[1])+1e-7:
                t=(z-a[1])/(b[1]-a[1]);hits.append(a[0]+t*(b[0]-a[0]))
        return max(hits)
    # Mirrored contour arcs differ below double precision but collapse to the
    # same float32 mesh row. Deduplicate before computing smooth normals.
    zs=sorted(set(round(z,5) for z in [.20+2.49*i/120 for i in range(121)]+[z for x,z in fs_outer if .20<=z<=2.69]))
    vs=[];fs=[]
    for z in zs:
        x0=front_edge_x(z);yf=fs_nose_y(z,x0)
        target_z=z+.04*max(0.,1.-(z-.20)/.25)**2
        p0=Vector((side*x0,yf,z))
        p1=Vector((side*(x0+(SIDE-x0)*.8),yf,z))
        p2=Vector((side*SIDE,-3.09-(-3.09-yf)*.35,target_z))
        p3=Vector((side*SIDE,-3.09,target_z))
        for j in range(13):
            t=j/12;u=1-t
            vs.append(tuple(p0*u**3+p1*(3*u*u*t)+p2*(3*u*t*t)+p3*t**3))
    for i in range(len(zs)-1):
        for j in range(12):
            a=i*13+j;f=(a,a+1,a+14,a+13)
            pa,pb,pc=[Vector(vs[k]) for k in f[:3]]
            if (pb-pa).cross(pc-pa).dot(Vector((side,-.3,0)))<0:f=tuple(reversed(f))
            fs.append(f)
    shell=mesh(f'Curved front corner shoulder {side}',vs,fs,white)
    for p in shell.data.polygons:p.use_smooth=True
    # Flush black band surrounds inset clear panes. Upper frit is broad, not a
    # collection of external frame boxes; this is a defining iEV7 feature.
    spans=[(-3.09,-2.06),(-2.02,-.91),(-.87,.86),(.90,1.75),(1.79,3.36)] if side==-1 else [(-3.09,-1.80),(-.68,.98),(1.86,3.36)]
    for i,(a,b) in enumerate(spans):
        x=side*(SIDE+.002)
        side_ring(f'Flush glazing frit {side}.{i}',(a+b)/2,b-a,1.11,2.70,x,black)
        # Broad top and bottom screen-printed mask, leaving glazing truly open.
        vs=[(x,a,2.24),(x,b,2.24),(x,b,2.70),(x,a,2.70)]
        mesh(f'Upper ceramic window frit {side}.{i}',vs,[(0,1,2,3) if side==1 else (3,2,1,0)],black)
        vs=[(x,a,1.11),(x,b,1.11),(x,b,1.27),(x,a,1.27)]
        mesh(f'Lower ceramic window frit {side}.{i}',vs,[(0,1,2,3) if side==1 else (3,2,1,0)],black)
        # Single zero-thickness flush glass sheet avoids the old opaque box effect.
        ga,gb=a+.025,b-.025
        vs=[(side*(SIDE+.004),ga,1.26),(side*(SIDE+.004),gb,1.26),(side*(SIDE+.004),gb,2.25),(side*(SIDE+.004),ga,2.25)]
        mesh(f'Side glass {side}.{i}',vs,[(0,1,2,3) if side==1 else (3,2,1,0)],deep_glass)
        # The wrap's skin over this window (BusDriver's body wrap, WRAP_SURFACE_OUT out from the glass; not part of the .cok): a
        # picture with see-through parts shows the glass through them. build_model.py hangs it from the window wrap pivot.
        vs=[(side*(SIDE+.004+WRAP_SURFACE_OUT),ga,1.26),(side*(SIDE+.004+WRAP_SURFACE_OUT),gb,1.26),
            (side*(SIDE+.004+WRAP_SURFACE_OUT),gb,2.25),(side*(SIDE+.004+WRAP_SURFACE_OUT),ga,2.25)]
        mesh(f'Wrap surface | window {side}.{i}',vs,[(0,1,2,3) if side==1 else (3,2,1,0)],white)
        # Factory sliding top panels are horizontal with rounded ends.
        if i>0:
            rod(f'Sliding window rail {side}.{i}',(side*(SIDE+.010),ga,2.04),(side*(SIDE+.010),gb,2.04),.010,black,8)
            if b-a>1.25:
                rod(f'Sliding window centre {side}.{i}',(side*(SIDE+.010),(ga+gb)/2,1.28),(side*(SIDE+.010),(ga+gb)/2,2.25),.011,black,8)
        box(f'Window release {side}.{i}',(side*(SIDE+.013),(ga+gb)/2,2.02),(.015,.064,.02),plastic,.005)
    # The 40 mm pillars between neighbouring window bays: one black frit strip each, the same flush plane and height as the
    # bays' rings (the bays' rings stop at their own spans: the street showed through a full-height slit between them).
    # Named as a flush glazing frit, fine_detail.py gives it the same inner lining and boundary returns.
    # Only the narrow mullions between bays (4 cm): the right side's wide gaps are its door openings, which a strip
    # across them painted black from 1.11 m to the eyebrow (user review 2026-10-15: black front doors).
    for (a0,b0),(a1,b1) in zip(spans,spans[1:]):
        if 1e-4<a1-b0<.10:
            x=side*(SIDE+.002)
            vs=[(x,b0-.002,1.11),(x,a1+.002,1.11),(x,a1+.002,2.70),(x,b0-.002,2.70)]
            mesh(f'Flush glazing frit pillar {side}.{b0:+.2f}',vs,[(0,1,2,3) if side==1 else (3,2,1,0)],black)
    # Upper black SUNWIN eyebrow, then a rolled aqua roof shoulder.
    vs=[(side*(SIDE+.005),-3.10,2.69),(side*(SIDE+.005),3.34,2.69),
        (side*(SIDE-.023),3.34,2.80),(side*(SIDE-.023),-3.10,2.80)]
    mesh(f'Continuous black eyebrow {side}',vs,[(0,1,2,3) if side==1 else (3,2,1,0)],black)
    # The eyebrow is a sloping plane, not the full-width vertical side wall.
    # Keep editable text on that plane so its edge cannot float above the nose
    # in a front view. Local text +Y follows the plane's uphill direction.
    word_z=2.744
    eyebrow_slope=.028/.11
    word_normal=Vector((side,0,eyebrow_slope)).normalized()
    word_surface=Vector((side*(SIDE+.005-eyebrow_slope*(word_z-2.69)),2.67,word_z))
    wordmark=label(f'SUNWIN side wordmark {side}','SUNWIN',word_surface+word_normal*.0007,.135,green,word_normal)
    wordmark.data.extrude=.00012
    wordmark['mount_surface_point']=list(word_surface)
    wordmark['mount_surface_normal']=list(word_normal)
    for ay in AXLES:
        tube(f'Wheel arch black lip {side} {ay}',[(side*(SIDE+.003),ay+(R+.043)*math.cos(t),R+(R+.043)*math.sin(t)) for t in [math.pi*i/48 for i in range(49)]],.015,rubber)
    # White side graphic terminates in U-shaped vertical ends; it does not trace
    # the wheel arches as a circular stripe.
    # on the door side the runs behind a door start 8 mm clear of its post (the outer stile's seal laps 2 mm onto the post)
    runs=[(-3.17,3.30)] if side==-1 else [(-3.17,-1.80),(-.672,.98),(1.868,3.30)]
    for n,(a,b) in enumerate(runs):
        pts=[(side*(SIDE+.013),a,.47),(side*(SIDE+.013),a-.065,.49),(side*(SIDE+.013),a-.085,.55),
             (side*(SIDE+.013),a-.085,.91),(side*(SIDE+.013),a,.97),(side*(SIDE+.013),a+.30,1.04)] if n==0 else []
        # The rear U end stays on the flat quarter panel (paint, 2026-10-19): it used to turn down at y 3.355, on the rounded
        # pillar return (rear_shell.py), whose stepped facets a paint line cannot follow cleanly (the 20 mm tube hid them).
        # Moved 85 mm forward, its outer edge ends 10 mm short of the joint at 3.30; the lower bend is drawn rounder and the
        # return still ends at 3.20, so it stays 19 mm clear of the rear wheel arch's black lip and bends no tighter than 49 mm.
        u=-.085
        pts += [(side*(SIDE+.013),a+.30 if n==0 else a,1.04),(side*(SIDE+.013),b-.08+(u if b>3.0 else 0),1.04)]
        if b>3.0:pts += [(side*(SIDE+.013),b+u,1.0),(side*(SIDE+.013),b+.055+u,.92),(side*(SIDE+.013),b+.055+u,.57),(side*(SIDE+.013),b+.035+u,.50),(side*(SIDE+.013),b-.015+u,.48)]
        # paint, laid on the body by paint_lines.py (it was a 20 mm tube standing 13 mm off the side)
        paint_line(f'Factory white U stripe {side}.{n}',[(y,z) for _,y,z in pts],.020,green,'x',side,smooth=True)
    for y in [-1.80+i*.40 for i in range(13)]:
        if any(abs(y-ay)<.55 for ay in AXLES):continue
        # a dash stops 15 mm short of a door opening (the one at -0.60 ran 35 mm into the front door's, through its leaf)
        a,b=y-.115,y+.115
        for cy,dw in (DOORS if side==1 else []):
            lo,hi=cy-dw/2-.015,cy+dw/2+.015
            if a<hi and b>lo:
                a,b=(hi,b) if y>cy else (a,lo)
        if b-a<.08:continue
        # painted, 35 mm high (it was a 12 mm thick block)
        paint_line(f'White sill dash {side}.{y}',[(a,.40),(b,.40)],.0175,green,'x',side)
    for y in [-2.93,2.93]:
        box(f'Amber side marker {side}.{y}',(side*(SIDE+.017),y,.89),(.025,.11,.042),turnR if side>0 else turnL,.014)
    # Side camera follows the same rounded roof section as ss_roof_z below.
    # Its old SIDE-width mounting point was in empty air above the shoulder.
    camera_z=min(2.87,ROOF-.01)
    camera_x=(SIDE-.023)*((ROOF-camera_z)/(ROOF-2.80))**(1/7)
    camera_surface=Vector((side*camera_x,0,camera_z))
    camera_normal=Vector((side*7*(ROOF-camera_z)/camera_x,0,1)).normalized()
    camera=rod(f'Side CCTV {side}',camera_surface-camera_normal*.008,
               camera_surface+camera_normal*.014,.022,black,24)
    camera['mount_surface_point']=list(camera_surface)
    camera['mount_surface_normal']=list(camera_normal)
    box(f'Charging service hatch {side}',(side*(SIDE+.010),3.12,1.0),(.02,.18,.16),white,.012)
    side_ring(f'Charging hatch seal {side}',3.12,.20,.91,1.09,side*(SIDE+.024),black)

# Shallow continuous roof loft and nearly flush HVAC cover, matching the low
# silhouette. The earlier tall rectangular housings are intentionally absent.
SS_ROOF_HALF=SIDE-.023
def ss_roof_z(x):
    # Exact shared edge with Continuous black eyebrow: inset X, Z=2.80 m.
    # The former full-width 2.77 m edge projected in front of that sloped frit.
    return ROOF-(ROOF-2.80)*(abs(x)/SS_ROOF_HALF)**7

vs=[];fs=[]
for j,y in enumerate([-2.60,-2.3,-1.8,-1.1,0,1.5,2.8,3.25,3.38]):
    for i in range(49):
        x=-SS_ROOF_HALF+2*SS_ROOF_HALF*i/48
        z=ss_roof_z(x)
        # Rolled front/rear shoulders blend into curved end panels.
        if y>3.25:z-=(y-3.25)*.40
        vs.append((x,y,z))
for j in range(8):
    for i in range(48):
        a=j*49+i;fs.append((a,a+1,a+50,a+49))
ob=mesh('Continuous low rounded roof',vs,fs,white)
for p in ob.data.polygons:p.use_smooth=True

# Match the exact nose perimeter, instead of intersecting an independently
# shaped rectangular roof. These lofts close the upper shoulder and lower
# bumper corner without a projecting lip or exposed back-facing wedge.
arch=[p for p in fs_outer if p[1]>=2.69-1e-5]
extent=max(abs(p[0]) for p in arch)
loft=[];faces=[];steps=12;n=len(arch)
for j in range(steps+1):
    t=j/steps
    for x,z in arch:
        a=Vector(fs_point(x,z,-.001));tx=x/extent*SS_ROOF_HALF
        b=Vector((tx,-2.60,ss_roof_z(tx)))
        loft.append(tuple(a.lerp(b,t)))
for j in range(steps):
    for i in range(n-1):
        f=(j*n+i,j*n+i+1,(j+1)*n+i+1,(j+1)*n+i)
        a,b,c=[Vector(loft[k]) for k in f[:3]]
        if (b-a).cross(c-a).z<0:f=tuple(reversed(f))
        faces.append(f)
ob=mesh('Matched nose to roof shoulder loft',loft,faces,white)
for p in ob.data.polygons:p.use_smooth=True
box('Flush roof HVAC lid',(0,.44,ROOF+.009),(1.46,2.18,.060),white,.029)
for y in [-.34+i*.10 for i in range(14)]:
    box('Roof intake slot',(0,y,ROOF+.041),(1.05,.019,.002),plastic,.001)
box('Rear roof electrical lid',(0,2.48,ROOF+.002),(1.47,.85,.046),white,.022)
