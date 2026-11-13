"""Build the editable Sunwin iEV7 model and CS2 import sources in Blender.

Blender frame: X right, -Y forward, Z up; metres. See model-spec.json for
documented reference dimensions and estimates. Run with Blender --background
--factory-startup --python this_file -- [--quick]. No game files are changed.
"""
import bpy
import json
import math
import sys
from pathlib import Path
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'ModelSource' / 'SWB6710EV01G'
OUT = ROOT / 'Asset' / 'SWB6710EV01G'
PREVIEW = SRC / 'previews'
for folder in (SRC, OUT, PREVIEW):
    folder.mkdir(parents=True, exist_ok=True)
SPEC = json.loads((SRC / 'model-spec.json').read_text(encoding='utf-8'))
L, W = SPEC['length_m'], SPEC['width_m']
HALF, SIDE = L / 2, W / 2
AXLES = [-HALF + SPEC['front_overhang_m'], HALF - SPEC['rear_overhang_m']]
R = SPEC['wheel_radius_m']
ROOF = SPEC['body_roof_m']
QUICK = '--quick' in sys.argv
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1
MASTER = bpy.data.collections.new('SWB6710EV01G | editable components')
scene.collection.children.link(MASTER)
STUDIO = bpy.data.collections.new('Studio | excluded from exports')
scene.collection.children.link(STUDIO)
current_collection = MASTER
MATERIALS = []

def material(name, rgb, rough=.4, metal=0., emission=0, power=0, glass=False, control=False):
    m = bpy.data.materials.new(name)
    m.diffuse_color = (*rgb, 1)
    m.use_nodes = True
    bs = m.node_tree.nodes.get('Principled BSDF')
    bs.inputs['Base Color'].default_value = (*rgb, 1)
    bs.inputs['Roughness'].default_value = rough
    bs.inputs['Metallic'].default_value = metal
    if glass:
        bs.inputs['Transmission Weight'].default_value = .92
        bs.inputs['IOR'].default_value = 1.46
        bs.inputs['Coat Weight'].default_value = .65
    if power:
        bs.inputs['Emission Color'].default_value = (*rgb, 1)
        bs.inputs['Emission Strength'].default_value = power
    m['light_id'] = emission
    m['livery_control'] = control
    m['glass'] = glass
    MATERIALS.append(m)
    return m

white = material('01 Shanghai aqua body | CDLC line colour', (.34, .69, .65), .27, .2, control=True)
green = material('02 Shanghai white accent', (.83, .90, .87), .28, .15)
mint = material('03 Secondary aqua accent | CDLC secondary', (.29, .59, .56), .3, .1)
black = material('04 Gloss black glazing frit', (.009, .018, .020), .18)
rubber = material('05 EPDM seals and tyre', (.014, .019, .020), .82)
plastic = material('06 Cabin graphite', (.038, .05, .054), .55)
glass = material('07 Solar glass | separate Gls export', (.49, .63, .61), .065, glass=True)
# Privacy-dark side and rear windows (user request 2026-10-18): the fixed side panes and the rear window take this tint; the
# windscreen, the door glass and the camera lens keep the solar glass above. Same transmission, roughness, IOR and coat (the native
# alpha stays .28), only the base colour is darker: about a quarter of the solar glass's linear colour.
deep_glass = material('07b Deep tinted glass | side and rear windows', (.105, .135, .131), .065, glass=True)
# The game's glass shows its base colour by its alpha (.28 for the solar glass: 72 % of the scene behind comes through untouched), so a
# darker colour alone changed the windows by a few per cent (user, 2026-10-18: "the glass did not turn dark"). The deep glass also
# takes a higher alpha so the dark colour is seen: .62, the deepest (38 % of the light through). The windows looked "too dark" at .62,
# .45, .36 and .32 in turn, but the cause was the wrap (its opaque layer covering the windows, user 2026-10-18: unloading the wrap made
# the windows normal), not the glass: back to .62.
deep_glass['glass_alpha']=.62
chrome = material('08 Satin aluminium', (.53, .61, .64), .25, .85)
hubdark = material('09 Recessed wheel steel', (.06, .08, .09), .45, .65)
yellow = material('10 Safety yellow grab rails', (.95, .61, .055), .3, .25)
floor = material('11 Low floor charcoal vinyl', (.12, .15, .16), .78)
panel = material('12 Cabin warm panels', (.66, .69, .65), .6)
blue = material('13 Shanghai blue upholstery', (.045, .19, .32), .77)
redseat = material('14 Priority seat upholstery', (.56, .1, .09), .74)
head = material('15 Low beam projector', (.83, .94, 1.), .12, emission=2, power=2.5)
drl = material('16 Daytime running light rings', (.85, .98, 1.), .15, emission=1, power=2.2)
tail = material('17 Tail light lens', (.46, .009, .006), .18, emission=3, power=.5)
brake = material('18 Brake light lens', (.9, .018, .007), .18, emission=4, power=1.)
turnL = material('19 Left turn signal', (1., .30, .013), .2, emission=5, power=.5)
turnR = material('20 Right turn signal', (1., .30, .013), .2, emission=6, power=.5)
reverse = material('21 Reverse light', (.75, .83, .85), .2, emission=7, power=.2)
saloon = material('22 Interior lamp', (.8, .93, .84), .45, emission=8, power=.7)
led = material('23 Destination amber LEDs', (1., .45, .04), .45, emission=9, power=1.4)
screen = material('24 Instrument screen', (.035, .27, .34), .3, emission=9, power=.4)
# Saloon finishes after the operator's interior photographs (2026-10-15 review): brushed stainless poles and rails, grey
# triangular strap handles, light grey seat shells with blue cushions and an orange top handle.
steel = material('25 Brushed stainless aluminium grab rails', (.60, .63, .64), .32, .85)
seatshell = material('26 Seat shell light grey', (.56, .58, .59), .5)
handle = material('27 Seat top handle orange', (.86, .34, .06), .35)
strapgrey = material('28 Strap handle grey', (.30, .32, .33), .55)
stopred = material('29 Stop request red', (.72, .04, .03), .35)

def link(obj):
    for col in list(obj.users_collection):
        col.objects.unlink(obj)
    current_collection.objects.link(obj)
    return obj

def assign(obj, mat):
    obj.data.materials.append(mat)
    return obj

def mesh(name, verts, faces, mat):
    data = bpy.data.meshes.new(name)
    data.from_pydata(verts, [], faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    current_collection.objects.link(obj)
    assign(obj, mat)
    return obj

def bevel(obj, amount=.025, segments=3):
    mod = obj.modifiers.new('Manufactured edge radius', 'BEVEL')
    mod.width = amount
    mod.segments = segments
    mod.affect = 'EDGES'
    obj.modifiers.new('Weighted corner normals', 'WEIGHTED_NORMAL')
    return obj

def box(name, loc, size, mat, radius=.012):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = link(bpy.context.object)
    obj.name = name
    obj.dimensions = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    assign(obj, mat)
    if radius:
        bevel(obj, radius)
    return obj

def rod(name, a, b, rad, mat, vertices=12):
    delta = Vector(b) - Vector(a)
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=rad, depth=delta.length, location=(Vector(a) + Vector(b)) / 2)
    obj = link(bpy.context.object)
    obj.name = name
    obj.rotation_euler = delta.to_track_quat('Z', 'Y').to_euler()
    assign(obj, mat)
    for p in obj.data.polygons:
        p.use_smooth = True
    return obj

def tube(name, points, rad, mat, closed=False):
    curve = bpy.data.curves.new(name, 'CURVE')
    curve.dimensions = '3D'
    curve.bevel_depth, curve.bevel_resolution = rad, 2
    curve.use_fill_caps = not closed
    poly = curve.splines.new('POLY')
    poly.points.add(len(points) - 1)
    for p, co in zip(poly.points, points):
        p.co = (*co, 1)
    poly.use_cyclic_u = closed
    obj = bpy.data.objects.new(name, curve)
    current_collection.objects.link(obj)
    assign(obj, mat)
    return obj

# Painted livery lines (the white stripes are paint, not trim: user review 2026-10-19). side_shell.py and rear_shell.py record each
# line here (its points in the plane it is drawn in, half its width, the axis it is cast along); paint_lines.py lays them on the
# finished body as single-sided ribbons, once every shell they cross exists.
PAINT_LINES = []

def paint_line(name, points, half_width, mat, axis, side=1, smooth=False):
    PAINT_LINES.append({'name': name, 'points': [tuple(p) for p in points], 'half': half_width, 'mat': mat,
                        'axis': axis, 'side': side, 'smooth': smooth, 'collection': current_collection})

FONT = bpy.data.fonts.load('C:/Windows/Fonts/simhei.ttf')
def label(name, body, loc, size, mat, normal=(0, -1, 0)):
    data = bpy.data.curves.new(name, 'FONT')
    data.body, data.size = body, size
    data.font, data.align_x, data.align_y = FONT, 'CENTER', 'CENTER'
    data.extrude = .0004
    data.resolution_u = 3
    obj = bpy.data.objects.new(name, data)
    current_collection.objects.link(obj)
    obj.location = loc
    obj.rotation_euler = Vector(normal).to_track_quat('Z', 'Y').to_euler()
    assign(obj, mat)
    return obj

def rounded_rect_points(width, height, radius, steps=7):
    points = []
    for cx, cz, start in [(width/2-radius, height/2-radius, 0), (-width/2+radius, height/2-radius, 90),
                           (-width/2+radius, -height/2+radius, 180), (width/2-radius, -height/2+radius, 270)]:
        for i in range(steps + 1):
            t = math.radians(start + i * 90/steps)
            points.append((cx + radius*math.cos(t), cz + radius*math.sin(t)))
    return points

def front_panel(name, width, height, z, y, radius, mat, back=False):
    pts = rounded_rect_points(width, height, radius)
    # Bow the front screen and fascia across the width, giving real curved reflection.
    verts = [(x, y + (1 if not back else -1) * .085*(x / SIDE)**2, z+v) for x, v in pts]
    if back:
        verts.reverse()
    return mesh(name, verts, [tuple(range(len(verts)))], mat)

def front_ring(name, outer, inner, y, mat, back=False):
    op=rounded_rect_points(outer[0],outer[1],outer[3])
    ip=rounded_rect_points(inner[0],inner[1],inner[3])
    verts=[(x,y+(1 if not back else -1)*.085*(x/SIDE)**2,z+shape[2])
           for shape,pts in [(outer,op),(inner,ip)] for x,z in pts]
    n=len(op)
    faces=[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    if back:faces=[tuple(reversed(f)) for f in faces]
    return mesh(name,verts,faces,mat)

def side_ring(name, y, width, bottom_z, top_z, x, mat):
    # An open window frame, not an opaque pane behind the glass.
    t=.04
    coords=[(y-width/2,bottom_z),(y+width/2,bottom_z),(y+width/2,top_z),(y-width/2,top_z),
            (y-width/2+t,bottom_z+t),(y+width/2-t,bottom_z+t),(y+width/2-t,top_z-t),(y-width/2+t,top_z-t)]
    faces=[(i,(i+1)%4,(i+1)%4+4,i+4) for i in range(4)]
    if x<0:faces=[tuple(reversed(f)) for f in faces]
    return mesh(name,[(x,a,b) for a,b in coords],faces,mat)

# Structural floor, low entry and wheelhouse arches. The front ends stop behind
# the nose's inner lining at floor height (0.25-0.36 m), where the bumper rolls
# under: rearmost nose skin over the central floor -3.356 m, over the wings
# -3.196 m (front_shell.py's photo-measured profile), lining 28 mm behind it.
for start,end,width in [(-HALF+.25,-2.90,1.44),(-2.90,-1.85,1.20),(-1.85,HALF-.14,1.44)]:
    box('Floor central low aisle',(0,(start+end)/2,.305),(width,end-start,.11),floor)
for side in [-1,1]:
    # Side wings stop inside the curved end skins; the rectangular corner of
    # the previous full-length floor visibly pierced the front bumper.
    for a,b in [(-3.16,AXLES[0]-.46),(AXLES[0]+.46,AXLES[1]-.46),(AXLES[1]+.46,3.30)]:
        box('Floor wing clear of tyres',(side*.91,(a+b)/2,.305),(.38,b-a,.11),floor)
    for ay in AXLES:
        box('Raised inner wheelhouse',(side*.91,ay,.825),(.38,.91,.055),panel,.02)
box('Underframe battery protection', (0, .25, .205), (1.48, 4.3, .13), hubdark)
# The rear axle is just 845 mm from the tail: a conventional full-depth
# cabinet intersects the aft quarter of the rear tyres. Keep it beyond the
# complete tyre envelope, including a 45 mm service clearance.
rear_cabinet_front=AXLES[1]+R+.045
rear_cabinet_back=HALF-.095
box('Rear drive equipment cabinet', (0,(rear_cabinet_front+rear_cabinet_back)/2,.65),
    (1.91,rear_cabinet_back-rear_cabinet_front,.62),panel,.035)

def bottom(y):
    z = .24
    for ay in AXLES:
        if abs(y-ay) < R+.045:
            z = max(z, R + math.sqrt(max(0, (R+.045)**2 - (y-ay)**2)))
    return z

exec((Path(__file__).with_name('door_linkage.py')).read_text(encoding='utf-8'),globals())   # also WRAP_SURFACE_OUT for the shells
exec((Path(__file__).with_name('front_shell.py')).read_text(encoding='utf-8'), globals())

exec((Path(__file__).with_name('side_shell.py')).read_text(encoding='utf-8'), globals())
fs_finish_front_join()

exec((Path(__file__).with_name('rear_shell.py')).read_text(encoding='utf-8'), globals())

DOOR_LEAVES=door_leaves(DOORS,SIDE)
# Door leaves: front pair, rear single, both between the axles; no hinges: each leaf runs on a track and opens mostly into
# the bus, as the Nanjing EV03G video and the user's review show (door_linkage.py: the evidence, the layout, the linkage and
# the timing). Each leaf rides on its crank pin pivot at its top (the door shaft's crank carries it there; fine_detail.py
# builds the shafts, cranks, rollers, the header rails and the floor slots after the tumblehome, whose coordinates the
# linkage uses), and turns about it as its roller runs in the rail (animation_rig.py).

for DL in DOOR_LEAVES:
    di,leaf,yy,lw=DL['door'],DL['leaf'],DL['centre_y'],DL['w']
    z0,z1=DOOR_LEAF_Z
    ob=side_ring(f'Door {di+1} leaf {leaf+1} | pivot-ready',yy,lw,z0,z1,SIDE+.008,black)
    joint=bpy.data.objects.new(DL['name']+' joint',None);MASTER.objects.link(joint)
    joint.location=(DL['joint'][0],DL['joint'][1],z1)
    shaft=bpy.data.objects.new(DL['name']+' shaft',None);MASTER.objects.link(shaft)
    shaft.location=(DL['shaft'][0],DL['shaft'][1],DOOR_SHAFT_Z[0])
    gl=box(f'Door glass {di+1}.{leaf+1}',(SIDE+.015,yy,(.62+2.25)/2),(.008,lw-.077,2.25-.62),glass,.012)
    lower=box(f'Door lower panel {di+1}.{leaf+1}',(SIDE+.021,yy,(z0+.01+.62)/2),(.012,lw-.077,.62-z0-.01),plastic,.012)
    # The leaf's own grab rail: one straight stainless bar across the leaf's inner face, both ends bent round into the
    # stiles (not standing on the glass); it moves with the leaf and, the leaf open, faces the doorway for the passengers.
    # User's marks on the official right profile (2026-10-16) and the photograph: the front pair forms a V, low at the
    # meeting stiles and high at the jamb stiles; the rear leaf is low at its rear (jamb) stile and rises towards the
    # front. Heights read from the photograph's door scale (DOOR_GRAB_RAIL_Z, about +-3 cm).
    zo,zc=DOOR_GRAB_RAIL_Z[di]
    ends=[Vector((SIDE-.050,DL['jamb_y']+DL['d']*(DOOR_LEAF_GAP+.02),zo)),Vector((SIDE-.050,DL['jamb_y']+DL['d']*(DOOR_LEAF_GAP+lw-.02),zc))]
    run=(ends[1]-ends[0]).normalized()
    path=[]
    for k,(c,d) in enumerate(((ends[0],run),(ends[1],-run))):
        foot,bend=c+Vector((.060,0,0)),.030           # the foot ends inside the frame, 2 mm behind its face
        p0,p2=c+Vector((bend,0,0)),c+d*bend
        arc=[foot]+[(1-t)**2*p0+2*(1-t)*t*c+t*t*p2 for t in (i/6 for i in range(7))]
        path+=arc if k==0 else arc[::-1]
    bar=tube(f'Door grab rail {di+1}.{leaf+1}',[tuple(p) for p in path],.016,steel)
    for o in (ob,gl,lower,bar):
        o.parent=joint;o.matrix_parent_inverse=Matrix.Translation(joint.location).inverted()
    # The leaf's piece of the body wrap (BusDriver: the picture goes with the door): skin WRAP_SURFACE_OUT out from the door glass
    # and from the lower panel, on the leaf's own wrap surface part (a child of the leaf part). Not part of the .cok.
    sp=bpy.data.objects.new(f"Wrap surface pivot | {DL['name']}",None);MASTER.objects.link(sp)
    sp.location=joint.location
    sp.parent=joint;sp.matrix_parent_inverse=Matrix.Translation(joint.location).inverted()
    # The leaf's frame included (the meeting stile, the top rail, the bottom edge), not only the glass: the picture is one piece over
    # the leaf (user review 2026-10-18: "the door frame between the leaves has nothing"). Two planes, the glass's and the lower
    # panel's (8 mm apart, so the picture is flush with its neighbours on each side of the step). The jamb stile stays inset by its
    # 38.5 mm: the skin stands 28-36 mm outside the leaf, and swinging with it would drive its edge into the post's rubber lip and the
    # quarter panel's boundary return (door_clearance.py: 26 overlaps at the first and last frames of the travel).
    half=lw/2
    inset=.0385
    ylo,yhi=(yy-half+inset,yy+half) if DL['d']>0 else (yy-half,yy+half-inset)
    xg,xl=SIDE+.019+WRAP_SURFACE_OUT,SIDE+.027+WRAP_SURFACE_OUT
    vs=[(xg,ylo,.62),(xg,yhi,.62),(xg,yhi,z1),(xg,ylo,z1),
        (xl,ylo,z0),(xl,yhi,z0),(xl,yhi,.62),(xl,ylo,.62)]
    ws=mesh(f"Wrap surface | door {DL['name']}",vs,[(0,1,2,3),(4,5,6,7)],white)
    ws.parent=sp;ws.matrix_parent_inverse=Matrix.Translation(sp.location).inverted()
    DL['wrap_pivot']=sp
# the windows' wrap skins (side_shell.py, rear_shell.py): one wrap surface part, a child of the shell
wp=bpy.data.objects.new('Wrap surface pivot | windows',None);MASTER.objects.link(wp);wp.location=(0,0,0)
for o in list(MASTER.objects):
    if o.name.startswith('Wrap surface | window'):
        o.parent=wp;o.matrix_parent_inverse=Matrix.Identity(4)
WRAP_WINDOW_PIVOT=wp
for o in MASTER.objects:
    if o.name.startswith('Wrap surface |'):
        o.hide_render=True             # skin for BusDriver's wrap only: out of the previews (render_wrap.py shows it) and the .cok
        o['cdlc_keep_topology']=True
for di,(cy,dw) in enumerate(DOORS):
    # the step tread stops at the skin (the leaves close outside it, 9 mm above it, and their inner ends swing in over it)
    box(f'Door {di+1} yellow threshold',(SIDE-.11,cy,.37),(.22,dw,.032),yellow,.005)
    frit=box(f'Door {di+1} upper black frit',(SIDE+.004,cy,2.54),(.015,dw+.015,.40),black,.009)
    frit['cdlc_part']=17        # the frit over the door is glazing, so the wrap covers it like the other frits (not renamed: fine_detail.py lines 'window frit' names)
    # the body skin under the step, between the posts (the leaves end above the tread)
    a,b=cy-dw/2,cy+dw/2
    mesh(f'Aqua door sill skin {di+1}',[(SIDE-.012,a,.24),(SIDE-.012,b,.24),(SIDE-.002,b,.353),(SIDE-.002,a,.353)],[(0,1,2,3)],white)
    mesh(f'Door sill underside {di+1}',[(SIDE-.012,a,.24),(1.10,a,.25),(1.10,b,.25),(SIDE-.012,b,.24)],[(0,1,2,3)],floor)
    if di==0:
        # the front door's stainless step nosing (official right profile)
        box('Door 1 stainless step nosing',(SIDE+.0045,cy,.359),(.006,dw-.02,.058),chrome,.002)
    # No 上车/下车 lettering over the doors: not on this bus (user review 2026-10-15).
(SRC/'door-linkage.json').write_text(json.dumps({'source':'Tools/SWB6710EV01G/door_linkage.py','coordinates':'Blender plan after taper.py; unity = (x, -y)',
    'timing':{str(k):dict(zip(('openSeconds','closeSeconds','closeDelay'),v)) for k,v in DOOR_TIMING.items()},
    'leaves':[{'name':DL['name'],'part':DL['part'],'drive_part':DL['drive_part'],'design':dict(zip(('jamb_end_along_m','jamb_end_depth_m','guide_span_m','open_degrees'),DL['design'])),
               'blender':{key:list(DL[key]) for key in ('shaft','joint','guide','open_joint','open_guide')},'profile_linkage':door_profile_linkage(DL),
               'checks':door_check(DL)} for DL in DOOR_LEAVES]},indent=2),encoding='utf-8')

# The two physical sides use different arms, heads and auxiliary optics.
exec((Path(__file__).with_name('mirror_shell.py')).read_text(encoding='utf-8'), globals())

# Wheels: one local origin per wheel, concentric rim, lug nuts, shoulder grooves.
for ai, ay in enumerate(AXLES):
    track=SPEC['front_track_m'] if ai==0 else SPEC['rear_track_m']
    for side in [-1,1]:
        bone=('WheelF' if ai==0 else 'WheelR')+('L' if side<0 else 'R')
        c=(side*track/2,ay,R)
        before=set(MASTER.objects)
        rod(bone+' tyre',(side*(track/2-.10),ay,R),(side*(track/2+.10),ay,R),R,rubber,64)
        outer=side*(track/2+.108)
        rod(bone+' bead',(outer,ay,R),(outer+side*.007,ay,R),R*.78,hubdark,48)
        rod(bone+' rim',(outer+side*.01,ay,R),(outer+side*.024,ay,R),R*.64,chrome,48)
        rod(bone+' hub',(outer+side*.026,ay,R),(outer+side*.051,ay,R),R*.23,chrome,32)
        for i in range(8):
            t=2*math.pi*i/8
            yy,zz=ay+R*.34*math.cos(t),R+R*.34*math.sin(t)
            rod(bone+' nut',(outer+side*.03,yy,zz),(outer+side*.048,yy,zz),.014,chrome,6)
            yy,zz=ay+R*.50*math.cos(t+.17),R+R*.50*math.sin(t+.17)
            rod(bone+' rim vent',(outer+side*.025,yy,zz),(outer+side*.028,yy,zz),.028,hubdark,16)
        for dx in [-.07,0,.07]:
            tube(bone+' tread groove',[(c[0]+dx,ay+(R+.001)*math.cos(t),R+(R+.001)*math.sin(t)) for t in [2*math.pi*i/64 for i in range(64)]],.003,hubdark,True)
        parent=bpy.data.objects.new(bone+' pivot',None)
        MASTER.objects.link(parent)
        parent.location=c
        for obj in set(MASTER.objects)-before-{parent}:
            obj['bone']=bone
            obj.parent=parent
            obj.matrix_parent_inverse=Matrix.Translation(parent.location).inverted()

# Reuse MeshKit's high-detail driver's hardware. Passenger layout is authored for iEV7.
kit_file=SRC/'kit-cabin.json'
kit_imported=[]
if kit_file.exists():
    kit=json.loads(kit_file.read_text(encoding='utf-8'))
    palette={}
    for sw in kit['palette']:
        # The export keeps the existing project's exact swatch colours.
        palette[sw['index']]=material('MeshKit '+sw['name'],tuple(sw['rgb']),.55)
    for part in kit['lods'][0]['parts']:
        if part['name'] not in ['Dash','SteeringWheel','SpeedNeedle','RpmNeedle']:
            continue
        # Original cab sits behind a front axle. Here the driver sits OVER the axle.
        verts=[(p[0],-p[2]+.26,p[1]) for p in part['positions']]
        inds=part['indices']; faces=[tuple(inds[i:i+3]) for i in range(0,len(inds),3)]
        for i,f in enumerate(faces):
            expected=part['normals'][f[0]]
            n=Vector((expected[0],-expected[2],expected[1]))
            actual=(Vector(verts[f[1]])-Vector(verts[f[0]])).cross(Vector(verts[f[2]])-Vector(verts[f[0]]))
            if actual.dot(n)<0:faces[i]=tuple(reversed(f))
        if part['name']=='Dash':
            faces=[f for f in faces if all(verts[j][2]>=.80 for j in f)]
        obj=mesh('MeshKit '+part['name'],verts,faces,plastic)
        obj.data.materials.clear()
        for key in sorted(palette):obj.data.materials.append(palette[key])
        for face in obj.data.polygons:
            u=part['uv'][face.vertices[0]][1]
            face.material_index=max(0,min(len(palette)-1,int(u*len(palette))))
        if part['name']=='Dash':
            # The imported two dashboard housings are extruded open profiles.
            # Close their actual profile boundaries, retaining the original
            # driving faces and leaving separate dials, buttons and labels alone.
            import bmesh as _dash_bmesh
            _dash_bm=_dash_bmesh.new();_dash_bm.from_mesh(obj.data)
            _dash_bmesh.ops.remove_doubles(_dash_bm,verts=list(_dash_bm.verts),dist=1e-6)
            _dash_seen=set();_dash_closed=0;_dash_small_closed=0
            for _dash_seed in list(_dash_bm.faces):
                if _dash_seed in _dash_seen:continue
                _dash_stack=[_dash_seed];_dash_faces=set()
                while _dash_stack:
                    _dash_face=_dash_stack.pop()
                    if _dash_face in _dash_faces:continue
                    _dash_faces.add(_dash_face);_dash_seen.add(_dash_face)
                    for _dash_edge in _dash_face.edges:
                        _dash_stack.extend(f for f in _dash_edge.link_faces if f not in _dash_faces)
                _dash_vertices={v for f in _dash_faces for v in f.verts}
                _dash_edges={e for f in _dash_faces for e in f.edges if e.is_boundary}
                _dash_low=min(v.co.z for v in _dash_vertices)
                _dash_high=max(v.co.z for v in _dash_vertices)
                if (len(_dash_edges)==4 and abs(_dash_low-1.3332994)<.001 and abs(_dash_high-1.5033443)<.001
                        and abs(min(v.co.x for v in _dash_vertices)+.79)<.001
                        and abs(max(v.co.x for v in _dash_vertices)+.25)<.001):
                    # The small tilted instrument hood has a separate missing
                    # rectangular rear face. Close that exact four-edge rim.
                    _dash_caps=_dash_bmesh.ops.holes_fill(_dash_bm,edges=list(_dash_edges),sides=4)['faces']
                    _dash_mat=max(_dash_faces,key=lambda f:f.calc_area()).material_index
                    for _dash_cap in _dash_caps:_dash_cap.material_index=_dash_mat
                    _dash_bmesh.ops.recalc_face_normals(_dash_bm,faces=list(_dash_faces)+list(_dash_caps))
                    _dash_bmesh.ops.triangulate(_dash_bm,faces=list(_dash_caps),quad_method='BEAUTY',ngon_method='BEAUTY')
                    if any(e.is_boundary for f in _dash_faces for e in f.edges):
                        raise RuntimeError('Instrument hood still has an open rear face')
                    _dash_small_closed+=1
                    continue
                if not _dash_edges or abs(_dash_low-.80)>.0001 or _dash_high-_dash_low<.25:
                    continue
                # The only boundary edges running across the extrusion are
                # its two open rear edges. Joining them closes the back/bottom
                # and leaves two planar, possibly concave, end profiles.
                _dash_cross=sorted(_dash_edges,key=lambda e:e.calc_length(),reverse=True)[:2]
                _dash_a,_dash_b=_dash_cross[0].verts
                _dash_c,_dash_d=_dash_cross[1].verts
                if abs((_dash_b.co-_dash_a.co).normalized().dot((_dash_d.co-_dash_c.co).normalized()))<.999:
                    raise RuntimeError('Dashboard extrusion edges are not parallel')
                if (_dash_b.co-_dash_d.co).length_squared<(_dash_b.co-_dash_c.co).length_squared:
                    _dash_c,_dash_d=_dash_d,_dash_c
                _dash_back=_dash_bm.faces.new((_dash_a,_dash_b,_dash_c,_dash_d))
                _dash_edges.update(_dash_back.edges)
                _dash_caps=[_dash_back]+_dash_bmesh.ops.holes_fill(_dash_bm,edges=[e for e in _dash_edges if e.is_boundary],sides=0)['faces']
                _dash_mat=max(_dash_faces,key=lambda f:f.calc_area()).material_index
                for _dash_cap in _dash_caps:_dash_cap.material_index=_dash_mat
                _dash_all=list(_dash_faces)+list(_dash_caps)
                _dash_bmesh.ops.recalc_face_normals(_dash_bm,faces=_dash_all)
                _dash_bmesh.ops.triangulate(_dash_bm,faces=list(_dash_caps),quad_method='BEAUTY',ngon_method='BEAUTY')
                if any(e.is_boundary for f in _dash_faces for e in f.edges):
                    raise RuntimeError('SWB dashboard housing still has an open profile')
                _dash_closed+=1
            if _dash_closed!=2:raise RuntimeError('Expected two imported dashboard housings')
            if _dash_small_closed!=1:raise RuntimeError('Expected one open instrument hood')
            _dash_bm.to_mesh(obj.data);_dash_bm.free();obj.data.update()
            obj['dash_closed_housing_components']=_dash_closed
            obj['dash_closed_instrument_hoods']=_dash_small_closed
        kit_imported.append(obj.name)
else:
    box('Driver dashboard',( -.51,-2.99,1.16),(1.01,.45,.24),plastic,.07)
    box('Driver instrument cluster',(-.51,-2.91,1.32),(.55,.10,.18),screen,.035)
    tube('Driver steering wheel',[(-.51+.18*math.cos(t),-2.69+.09*math.sin(t),1.30+.15*math.sin(t)) for t in [2*math.pi*i/48 for i in range(48)]],.017,rubber,True)

def seat(name,x,y,z=.38,priority=False,driver=False):
    mat=redseat if priority else blue
    if driver:
        box(name+' base',(x,y,z+.27),(.40,.43,.095),plastic,.035)
        box(name+' cushion',(x,y,z+.33),(.405,.405,.095),mat,.042)
        back=box(name+' backrest',(x,y+.18,z+.69),(.41,.085,.61),mat,.05)
        back.rotation_euler.x=math.radians(-6)
        for dx in [-.15,.15]:rod(name+' seat foot',(x+dx,y,z+.02),(x+dx,y,z+.24),.016,chrome)
        return
    # A city-bus seat: a moulded light grey shell (pan and high back) with an upholstered insert on the cushion and the
    # back, an orange grab handle along the top of the back, on a pedestal from the floor.
    box(name+' shell pan',(x,y,z+.29),(.43,.43,.05),seatshell,.02)
    box(name+' cushion',(x,y-.01,z+.335),(.38,.37,.05),mat,.02)
    back=box(name+' backrest shell',(x,y+.195,z+.72),(.43,.05,.72),seatshell,.02)
    back.rotation_euler.x=math.radians(-8)
    ins=box(name+' backrest insert',(x,y+.17,z+.68),(.37,.02,.52),mat,.012)
    ins.rotation_euler.x=math.radians(-8)
    tube(name+' back grab',[(x-.16,y+.245,z+1.04),(x-.13,y+.25,z+1.10),(x+.13,y+.25,z+1.10),(x+.16,y+.245,z+1.04)],.016,handle)
    rod(name+' seat foot',(x,y+.05,z),(x,y+.05,z+.27),.035,chrome)

# 17 passenger seats: 8 left singles, 6 right singles, 3 rear bench places.
for i,y in enumerate([-1.64,-.98,-.32,.34,1.,1.66,2.32,2.82]):
    seat(f'Passenger seat L{i+1}',-.83,y,.40 if abs(y)<2.1 else .81,i<2)
# No seat beside the driver: the door side ahead of the front door is the entrance (the fare reader stands there).
for i,y in enumerate([-.27,.40,2.19,2.83]):
    seat(f'Passenger seat R{i+2}',.82,y,.40 if -.4<y<1 else .81,i==0)
seat('Passenger seat R6',.37,2.81,.81)
for i,x in enumerate([-.65,-.12,.41]):
    seat(f'Rear bench {i+1}',x,3.26,.81)
seat('Driver seat',-.56,-2.05,.905,driver=True)
for x in [-.43,.43]:
    rod('Ceiling longitudinal rail',(x,-2.4,2.39),(x,2.65,2.39),.019,steel)
    # hung from the ceiling (2.835 m) by hangers about every 1.3 m: the rail was floating 0.45 m under the ceiling
    for y in [-2.3,-1.05,.2,1.45,2.55]:
        rod('Ceiling rail hanger',(x,y,2.39),(x,y,2.835),.012,steel)
        rod('Ceiling rail hanger flange',(x,y,2.822),(x,y,2.837),.03,steel)
for y in [-1.75,-.40,.94,1.97]:
    for x in [-.54,.62]:
        rod('Vertical grab pole',(x,y,.38),(x,y,2.835),.019,steel)
        rod('Vertical grab pole ceiling flange',(x,y,2.822),(x,y,2.837),.035,steel)
        box('Pole floor anchor',(x,y,.37),(.075,.075,.025),chrome,.01)
        # stop request push on the pole at hand height, facing the aisle
        box('Stop request button housing',(x-math.copysign(.025,x),y,1.42),(.03,.045,.065),stopred,.008)
for y in [-1.37,-.67,.09,.80,1.54,2.22]:
    for x in [-.43,.43]:
        # grey belt from the rail clamp, a triangular moulded grip
        box('Hand strap stem',(x,y,2.27),(.012,.035,.22),strapgrey,.004)
        tube('Passenger hand strap',[(x,y,2.17),(x-.075,y,2.04),(x+.075,y,2.04)],.013,strapgrey,True)
for x in [-.76,.76]:
    box('Ceiling passenger lamp',(x,0,2.805),(.065,4.72,.035),saloon,.01)
# Central ceiling air duct with its diffuser slots, as on the operator's interior photographs.
box('Ceiling air duct',(0,.1,2.81),(.46,4.9,.05),panel,.02)
for i in range(16):
    box('Ceiling air duct diffuser slot',(0,-2.15+i*.3,2.783),(.32,.035,.006),plastic,.002)
# The emergency hammers on the window pillars and the side route screen are
# placed on the narrowed (tapered) body in fine_detail.py.
box('Door fare validator',(.66,-1.74,1.30),(.18,.10,.26),plastic,.03)
box('Fare validator blue screen',(.66,-1.804,1.35),(.124,.009,.084),screen,.012)
label('Accessible area sign','♿',(.985,.78,1.35),.17,white,(1,0,0))
exec((Path(__file__).with_name('paint_lines.py')).read_text(encoding='utf-8'),globals())
exec((Path(__file__).with_name('taper.py')).read_text(encoding='utf-8'),globals())
exec((Path(__file__).with_name('fine_detail.py')).read_text(encoding='utf-8'),globals())
exec((Path(__file__).with_name('animation_rig.py')).read_text(encoding='utf-8'),globals())
rig_report=prepare_animation_rig(MASTER,scene,SPEC)
(SRC/'animation-report.json').write_text(json.dumps(rig_report,indent=2),encoding='utf-8')
# the door motion against the body and each other, every frame (door_clearance.py); a failure stops the build
exec((Path(__file__).with_name('door_clearance.py')).read_text(encoding='utf-8'),globals())
door_clearance=check_door_clearance(MASTER,scene,DOOR_LEAVES,list(range(DOOR_TIMELINE_END+1)))
_door_report=json.loads((SRC/'door-linkage.json').read_text(encoding='utf-8'))
_door_report['clearance']=door_clearance
(SRC/'door-linkage.json').write_text(json.dumps(_door_report,indent=2),encoding='utf-8')
print('DOOR_CLEARANCE '+json.dumps({k:v for k,v in door_clearance.items() if k!='overlaps'})+' overlaps '+str(len(door_clearance['overlaps'])),flush=True)
if door_clearance['overlaps']:
    raise RuntimeError('Door motion clearance: '+json.dumps(door_clearance['overlaps'][:40]))

# Wheel centres and door entry positions are explicit in the master file.
for name,pos in [('DriverEye',(-.56,-2.18,2.08)),('BoardingDoor',(1.22,-1.24,.36)),('AlightingDoor',(1.22,1.42,.36))]:
    obj=bpy.data.objects.new(name,None); MASTER.objects.link(obj); obj.location=pos; obj.empty_display_size=.12

scene['model_id']='SWB6710EV01G'
scene['status']='Reference-based editable model; game packaging not yet performed'
scene['coordinates']='metres; X right, -Y front, Z up'
scene['reference_dimensions']='7145 x 2350 mm; 5100 mm wheelbase; 1200/845 mm overhangs'
scene['cdlc_livery']='Aqua body material 01 -> ControlMask red. Glass separately exported.'
scene['kit_reuse']=', '.join(kit_imported)

exec((Path(__file__).with_name('livery_uv.py')).read_text(encoding='utf-8'), globals())
prepare_livery_uv(MASTER,L,W)

def flatten_object(obj):
    deps=bpy.context.evaluated_depsgraph_get()
    evaluated=obj.evaluated_get(deps)
    data=bpy.data.meshes.new_from_object(evaluated,depsgraph=deps)
    if obj.type=='FONT':
        # The game is left-handed. Keep authored text readable when viewed
        # from its front after the Blender-to-game coordinate conversion.
        data.transform(Matrix.Diagonal((-1.,1.,1.,1.)))
        data.flip_normals()
    data.transform(obj.matrix_world)
    return data

# Single material atlas: every material has a safe padded tile, retaining all lamp IDs.
def write_atlas(name,mats):
    import numpy as np
    n=1024; tile=64
    arrays={key:np.zeros((n,n,4),dtype=np.float32) for key in ['BaseColor','MaskMap','Normal','ControlMask','Emissive0','EmissiveID0']}
    arrays['Normal'][:]=(.5,.5,1,1)
    arrays['MaskMap'][:]=(0,1,0,.3)
    arrays['BaseColor'][:]=(.01,.01,.01,1)
    arrays['ControlMask'][:,:,(3,)]=1
    arrays['Emissive0'][:,:,(3,)]=1
    arrays['EmissiveID0'][:,:,(3,)]=1
    for i,m in enumerate(mats):
        row,col=divmod(i,16)
        sl=(slice(row*tile,(row+1)*tile),slice(col*tile,(col+1)*tile))
        rgb=tuple(m.diffuse_color[:3]); bs=m.node_tree.nodes.get('Principled BSDF')
        # Blender's image buffer is linear; data maps saved as Non-Color preserve bytes.
        arrays['BaseColor'][sl]=(*( (1.,1.,1.) if m.get('livery_control') else rgb ),float(m.get('glass_alpha',.28)) if m.get('glass') else 1.)
        arrays['MaskMap'][sl]=(bs.inputs['Metallic'].default_value,1,0,1-bs.inputs['Roughness'].default_value)
        if m.get('livery_control'):arrays['ControlMask'][sl]=(1,0,0,1)
        lid=m.get('light_id',0)
        if lid:
            arrays['Emissive0'][sl]=(*rgb,1)
            arrays['EmissiveID0'][sl]=(lid/255.,0,0,1)
    for key,a in arrays.items():
        if all(m.get('glass') for m in mats) and key in ['ControlMask','Emissive0','EmissiveID0']:
            continue
        im=bpy.data.images.new(name+'_'+key,n,n,alpha=True)
        im.colorspace_settings.name='sRGB' if key in ['BaseColor','Emissive0'] else 'Non-Color'
        im.pixels.foreach_set(a.ravel())
        im.filepath_raw=str(OUT/(name+'_'+key+'.png')); im.file_format='PNG'
        im.save()
        bpy.data.images.remove(im)

def merged(name,objects,mats,ratio=1.):
    verts=[]; faces=[]; uvcentres=[]; groups={}; corner_normals=[]
    for obj in objects:
        if ratio<.2 and any(word in obj.name for word in [' nut',' vent','tread groove',' seat foot','Hand strap','hand strap','anchor','Window latch','Pole floor','grab','badge emblem',
                                                           'Door drive','Door joint','Door roller']):
            continue
        data=flatten_object(obj)
        temp=bpy.data.objects.new('__evaluate',data); scene.collection.objects.link(temp)
        if ratio<1 and int(obj.get('cdlc_part',-1))!=0 and not obj.get('front_c2_roof_transition') and not obj.get('cdlc_keep_topology') and len(data.polygons)>8 and not any(m and m.get('light_id') for m in data.materials):
            import bmesh
            bm=bmesh.new();bm.from_mesh(data)
            bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=0.000001)
            bm.normal_update();bm.to_mesh(data);bm.free()
            dec=temp.modifiers.new('LOD silhouette reduction','DECIMATE'); dec.ratio=ratio
            reduced=flatten_object(temp)
            bpy.data.objects.remove(temp,do_unlink=True); bpy.data.meshes.remove(data)
            data=reduced
        else:bpy.data.objects.remove(temp,do_unlink=True)
        if obj.get('front_common_surface'):
            fs_apply_common_normals(data)
        data.calc_loop_triangles()
        base=len(verts); verts.extend(tuple(v.co) for v in data.vertices)
        bone=obj.get('bone','Root'); groups.setdefault(bone,[]).extend(range(base,len(verts)))
        for tri in data.loop_triangles:
            points=[data.vertices[vi].co for vi in tri.vertices]
            face=(points[1]-points[0]).cross(points[2]-points[0])
            if face.length_squared<1e-12:continue
            face.normalize()
            faces.append(tuple(base+i for i in tri.vertices))
            for li in tri.loops:
                normal=data.corner_normals[li].vector
                corner_normals.append(tuple(normal if normal.length_squared>1e-12 else face))
            mat=data.materials[tri.material_index] if data.materials else plastic
            idx=mats.index(mat) if mat in mats else 0
            row,col=divmod(idx,16)
            uvcentres.append(((col+.5)/16,(row+.5)/16))
        bpy.data.meshes.remove(data)
    ob=mesh(name,verts,faces,white)
    uv=ob.data.uv_layers.new(name='AtlasUV')
    for poly,centre in zip(ob.data.polygons,uvcentres):
        # Real UV area is required by the game's emissive-ID rasterizer.
        for li,offset in zip(poly.loop_indices,[(-.018,-.018),(.018,-.018),(0,.018)]):
            uv.data[li].uv=(centre[0]+offset[0],centre[1]+offset[1])
        poly.use_smooth=True
    ob.data.normals_split_custom_set(corner_normals)
    for bone,indices in groups.items():ob.vertex_groups.new(name=bone).add(indices,1.,'REPLACE')
    return ob

def export_fbx(obj,name):
    bpy.ops.object.select_all(action='DESELECT')
    # Source remains metres. Only an export copy is converted to the centimetre pipeline.
    data=obj.data.copy()
    data.transform(Matrix.Diagonal((-100.,100.,100.,1.)))
    data.flip_normals()  # PipelinePlugin mirrors X again when creating Unity geometry.
    # The export copy and its mesh data must carry exactly `name`: the game's FBX reader names the model after the mesh data,
    # and rigs only the one named like the asset folder (a "SWB6710EV01G.001" became a mesh without bones). The merged
    # source object and its mesh give the name up while it is exported.
    source_name=obj.name; source_data=obj.data.name
    obj.name=name+' (source)'; obj.data.name=name+' (source)'; data.name=name
    export=bpy.data.objects.new(name,data); scene.collection.objects.link(export)
    if export.name!=name or data.name!=name: raise RuntimeError(f'FBX object/mesh named {export.name!r}/{data.name!r}, not {name!r}')
    for vg in obj.vertex_groups:
        group=export.vertex_groups.new(name=vg.name)
        indices=[v.index for v in obj.data.vertices if any(g.group==vg.index for g in v.groups)]
        if indices:group.add(indices,1,'REPLACE')
    armdata=bpy.data.armatures.new(name+' Rig'); arm=bpy.data.objects.new(name+' Rig',armdata); scene.collection.objects.link(arm)
    arm.select_set(True); bpy.context.view_layer.objects.active=arm
    bpy.ops.object.mode_set(mode='EDIT')
    for bn,co in [('Root',(0,0,0))]+[(('WheelF' if ai==0 else 'WheelR')+('L' if side<0 else 'R'),(side*(SPEC['front_track_m'] if ai==0 else SPEC['rear_track_m'])/2,ay,R)) for ai,ay in enumerate(AXLES) for side in [-1,1]]:
        b=armdata.edit_bones.new(bn); b.head=Vector((-co[0],co[1],co[2]))*100; b.tail=b.head+Vector((0,0,30)); b.roll=0
        if bn!='Root':b.parent=armdata.edit_bones['Root']
    bpy.ops.object.mode_set(mode='OBJECT')
    mod=export.modifiers.new('Wheel deformation','ARMATURE');mod.object=arm
    export.select_set(True)
    scene.unit_settings.scale_length=.01
    bpy.ops.export_scene.fbx(filepath=str(OUT/(name+'.fbx')),use_selection=True,object_types={'MESH','ARMATURE'},add_leaf_bones=False,
        axis_forward='-Z',axis_up='Y',apply_scale_options='FBX_SCALE_ALL',apply_unit_scale=True,bake_space_transform=True,
        path_mode='STRIP',mesh_smooth_type='FACE',use_armature_deform_only=False,primary_bone_axis='Y',secondary_bone_axis='X',bake_anim=False)
    scene.unit_settings.scale_length=1
    bpy.data.objects.remove(export,do_unlink=True);bpy.data.meshes.remove(data);bpy.data.objects.remove(arm,do_unlink=True);bpy.data.armatures.remove(armdata)
    obj.name=source_name; obj.data.name=source_data

# Export selected model components only; Studio and reference empties never enter FBX.
bpy.context.view_layer.update()
source_objects=[o for o in MASTER.objects if o.type in {'MESH','CURVE','FONT'}]
exec((Path(__file__).with_name('wheel_clearance.py')).read_text(encoding='utf-8'),globals())
clearance=check_wheel_clearance(source_objects,flatten_object,AXLES,R,SPEC['front_track_m'],SPEC['rear_track_m'])
(SRC/'wheel-clearance.json').write_text(json.dumps(clearance,indent=2),encoding='utf-8')
if clearance['penetrations']:
    raise RuntimeError('Tyre/body penetration: '+json.dumps(clearance['penetrations']))
exec((Path(__file__).with_name('write_runtime.py')).read_text(encoding='utf-8'),globals())
if '--skip-runtime' not in sys.argv:
    write_cdlc_runtime(source_objects,collect_cdlc_parts(MASTER),flatten_object,ROOT,SPEC)
body_objects=[o for o in source_objects if not any(m and m.get('glass') for m in o.data.materials) and not o.name.startswith('Wrap surface')]
glass_objects=[o for o in source_objects if any(m and m.get('glass') for m in o.data.materials)]
body_mats=[m for m in MATERIALS if not m.get('glass')]
glass_mats=[glass,deep_glass]
LOD=bpy.data.collections.new('Export LOD meshes | hidden in render');scene.collection.children.link(LOD)
current_collection=LOD
stats=[]
for lod,ratio in enumerate([1.,.38,.12]):
    name='SWB6710EV01G'+(f'_LOD{lod}' if lod else '')
    row={'lod':lod}
    for isglass,objects,mats in [(False,body_objects,body_mats),(True,glass_objects,glass_mats)]:
        nm=name+('_Gls' if isglass else '')
        # an object with cdlc_lods (paint_lines.py: a painted line's near and far versions) goes only into those LODs
        ob=merged(nm,[o for o in objects if lod in o.get('cdlc_lods',(0,1,2))],mats,1. if isglass else ratio)
        row['glass_triangles' if isglass else 'body_triangles']=len(ob.data.polygons)
        write_atlas(nm,mats)
        export_fbx(ob,nm)
        ob.hide_render=True;ob.hide_set(True)
    stats.append(row)
current_collection=MASTER
(SRC/'build-report.json').write_text(json.dumps({'model':'SWB6710EV01G','lods':stats,'editable_objects':len(source_objects),
    'kit_reused':kit_imported,'game_packaged':False,'dimensions':SPEC},ensure_ascii=False,indent=2),encoding='utf-8')

# A neutral studio presents real geometry rather than baked photography.
current_collection=STUDIO
box('Studio ground',(0,0,-.07),(200,200,.10),material('Studio floor',(.62,.66,.68),.85),0)
world=bpy.data.worlds.new('Studio ambient');scene.world=world;world.use_nodes=True
world.node_tree.nodes['Background'].inputs[0].default_value=(.28,.34,.39,1)
world.node_tree.nodes['Background'].inputs[1].default_value=.55
for name,location,power,size in [('Key',(3,-5,8),1800,7),('Rim',(-5,2,7),2100,6),('Front fill',(0,-7,3),600,5)]:
    ld=bpy.data.lights.new(name,'AREA');ld.energy=power;ld.shape='DISK';ld.size=size
    ob=bpy.data.objects.new(name,ld);STUDIO.objects.link(ob);ob.location=location
    ob.rotation_euler=(Vector((0,0,1.1))-ob.location).to_track_quat('-Z','Y').to_euler()
camdata=bpy.data.cameras.new('Presentation camera');cam=bpy.data.objects.new('Presentation camera',camdata);STUDIO.objects.link(cam);scene.camera=cam
scene.render.engine='CYCLES';scene.cycles.samples=24 if QUICK else 64;scene.cycles.use_denoising=True
scene.render.resolution_x=1600;scene.render.resolution_y=1050;scene.render.resolution_percentage=100
scene.view_settings.view_transform='AgX'
views=[('front-right',(9,-11.8,3.6),(0,0,1.4),50),('rear-left',(-10,12,3.8),(0,0,1.4),50),
       ('front',(0,-13,1.58),(0,0,1.58),52),
       ('right-profile',(14,0,1.52),(0,0,1.52),52),('driver-cab',(-.56,-2.18,2.08),(-.56,-2.85,1.36),24),
       ('front-detail',(2.45,-6.25,1.70),(0,-3.38,1.36),65),
       ('front-glass',(1.8,-7.3,2.6),(0,-3.30,2.06),60),
       ('front-profile',(14,-2.7,1.5),(0,-2.7,1.5),52),
       ('rear-upper-corner',(-3.8,5.8,3.15),(-.95,3.12,2.65),65),
       ('rear-lower-corner',(-3.5,5.5,.7),(-.95,2.95,.55),65)]
for name,eye,target,lens in ([] if '--no-render' in sys.argv else views):
    cam.location=eye;cam.rotation_euler=(Vector(target)-cam.location).to_track_quat('-Z','Y').to_euler();camdata.lens=lens
    if name in ['right-profile','front','front-profile']:
        camdata.type='ORTHO';camdata.ortho_scale=3.35 if name=='front-profile' else (8.7 if name=='right-profile' else 5.2)
    else:camdata.type='PERSP'
    scene.render.filepath=str(PREVIEW/(name+'.png'))
    bpy.ops.render.render(write_still=True)
if '--no-render' not in sys.argv:
    scene.frame_set(36)
    cam.location=(9,-11.8,3.6);cam.rotation_euler=(Vector((0,0,1.4))-cam.location).to_track_quat('-Z','Y').to_euler();camdata.type='PERSP';camdata.lens=50
    scene.render.filepath=str(PREVIEW/'doors-open.png')
    bpy.ops.render.render(write_still=True)
    scene.frame_set(0)
cam.location=(9,-11.8,3.6);cam.rotation_euler=(Vector((0,0,1.4))-cam.location).to_track_quat('-Z','Y').to_euler();camdata.type='PERSP';camdata.lens=50
# Save in a clean, immediately inspectable material-preview view.
for screen_data in bpy.data.screens:
    for area in screen_data.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_distance=11
            area.spaces.active.region_3d.view_location=(0,0,1.4)
            area.spaces.active.region_3d.view_rotation=cam.rotation_euler.to_quaternion()
            area.spaces.active.shading.type='MATERIAL'
            area.spaces.active.clip_end=1000
bpy.ops.object.select_all(action='DESELECT')
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=str(SRC/'SWB6710EV01G.blend'))
print('SWB_BUILD_OK '+json.dumps(stats))
