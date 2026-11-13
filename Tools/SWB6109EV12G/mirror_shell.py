"""Independent iEV10 / SWB6109EV12G mirrors, in private construction coordinates.

Execute inside build_model.py after the doors. In this temporary construction
frame door side is +X and driver side is -X. source_coordinates reflects X when
saving the physical left-hand-drive Blender source: doors -X, driver +X.

The two main reflective faces are planar so the live CDLC mirror cameras can
use the exact same plane.  The small door-side auxiliary face is convex.
All pieces remain Outside (15), including their line-colour painted shells,
so the driver can see them when exterior/livery body panels are hidden.
"""

import json as _mirror_json


def mirror_stamp(obj, assembly, component):
    obj['cdlc_part'] = 15
    obj['cdlc_part_name'] = 'Outside'
    obj['bone'] = 'Root'
    obj['mirror_assembly'] = assembly
    obj['mirror_component'] = component
    obj['preserve_lod'] = True
    obj['cdlc_preserve_section_lod'] = True
    return obj


def mirror_smooth(obj):
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    return obj


def mirror_frame(normal):
    n = Vector(normal).normalized()
    u = Vector((0, 0, 1)).cross(n).normalized()
    v = n.cross(u).normalized()
    return u, v, n


def mirror_outline(width, height, segments=96, exponent=3.7, taper=.075):
    """Soft rectangular pod, slightly narrower at its rounded lower end."""
    result = []
    for i in range(segments):
        angle = 2 * math.pi * i / segments
        cx, sy = math.cos(angle), math.sin(angle)
        uu = math.copysign(abs(cx) ** (2 / exponent), cx)
        vv = math.copysign(abs(sy) ** (2 / exponent), sy)
        result.append((uu * width / 2 * (1 + taper * vv), vv * height / 2))
    return result


def mirror_point(centre, frame, p, depth):
    u, v, n = frame
    return Vector(centre) + u * p[0] + v * p[1] + n * depth


def mirror_loft(name, centre, frame, outline, rings, mat, assembly,
                opening=None, opening_depth=None, wall=.004):
    """A closed, thick cup: outer skin, joined lip, inner wall and two backs.

    The original one-sided outer loft exposed its reverse face beside the
    mirror when viewed from the driver's eye. The inner return is real
    geometry and faces into the cup; all original outer vertices stay put.
    """
    count = len(outline)
    sections=[(depth,[(p[0]*scale,p[1]*scale) for p in outline]) for depth,scale in rings]
    def inset(points):
        result=[]
        for x,y in points:
            length=math.hypot(x,y)
            scale=max(.05,1-wall/max(length,wall))
            result.append((x*scale,y*scale))
        return result
    # Join the bezel to the outside as part of the same manifold object.
    sections.append((rings[-1][0] if opening_depth is None else opening_depth,
                     inset(sections[-1][1]) if opening is None else opening))
    for depth,scale in reversed(rings[:-1]):
        sections.append((depth+wall,inset([(p[0]*scale,p[1]*scale) for p in outline])))
    vertices = [tuple(mirror_point(centre,frame,p,depth))
                for depth,points in sections for p in points]
    faces = []
    for ring in range(len(sections)-1):
        for i in range(count):
            j = (i+1) % count
            faces.append((ring*count+i, ring*count+j,
                          (ring+1)*count+j, (ring+1)*count+i))
    # Explicit centre fans avoid the skinny ear triangles produced by an
    # automatic tessellation of a 96-sided back cap. Such valid tiny ears
    # fell below the runtime writer's area cutoff and exposed the inner cup.
    # Only cap topology changes; every perimeter point and depth is retained.
    outer_centre=len(vertices)
    vertices.append(tuple(Vector(centre)+frame[2]*sections[0][0]))
    faces.extend((outer_centre,(i+1)%count,i) for i in range(count))
    base=(len(sections)-1)*count
    inner_centre=len(vertices)
    vertices.append(tuple(Vector(centre)+frame[2]*sections[-1][0]))
    faces.extend((inner_centre,base+i,base+(i+1)%count) for i in range(count))
    obj = mirror_smooth(mesh(name, vertices, faces, mat))
    obj['mirror_shell_wall_m']=wall
    obj['mirror_shell_closed_manifold']=True
    return mirror_stamp(obj, assembly, 'painted pod')


def mirror_face(name, centre, frame, outline, mat, assembly, convex=0):
    count = len(outline)
    u, v, n = frame
    radial_steps = 9 if convex else 1
    vertices = [tuple(Vector(centre) + n * convex)]
    for ring in range(1, radial_steps+1):
        radius = ring / radial_steps
        depth = convex * (1-radius*radius)
        for p in outline:
            vertices.append(tuple(mirror_point(centre, frame,
                                               (p[0]*radius, p[1]*radius), depth)))
    faces = [(0, 1+i, 1+(i+1) % count) for i in range(count)]
    for ring in range(radial_steps-1):
        a, b = 1+ring*count, 1+(ring+1)*count
        for i in range(count):
            j = (i+1) % count
            faces.append((a+i,b+i,b+j,a+j))
    obj = mesh(name, vertices, faces, mat)
    if convex:
        mirror_smooth(obj)
    mirror_stamp(obj, assembly, 'reflective face')
    obj['mirror_surface'] = True
    obj['mirror_convex_depth_m'] = convex
    obj['mirror_normal_blender'] = list(n)
    return obj


def mirror_rim(name, centre, frame, outer, inner, mat, assembly,
               outer_depth=-.003, inner_depth=-.004):
    count = len(outer)
    assert len(inner) == count
    vertices = [tuple(mirror_point(centre,frame,p,depth))
                for points,depth in ((outer,outer_depth),(inner,inner_depth))
                for p in points]
    faces = [(i,(i+1)%count,(i+1)%count+count,i+count) for i in range(count)]
    return mirror_stamp(mirror_smooth(mesh(name,vertices,faces,mat)),assembly,'continuous lip')


def mirror_bezier(points, steps=36):
    a, b, c, d = [Vector(p) for p in points]
    return [a*(1-t)**3 + b*(3*(1-t)**2*t) + c*(3*(1-t)*t*t) + d*t**3
            for t in (i/steps for i in range(steps+1))]


def mirror_arm_curvature(name, controls, radii, samples=4096):
    """Bound the complete section against analytic cubic curvature, in metres."""
    p = [Vector(point) for point in controls]
    a, b, c = [p[i+1]-p[i] for i in range(3)]
    minimum_radius, minimum_t, minimum_speed = math.inf, 0., math.inf
    for i in range(samples+1):
        t = i/samples
        velocity = 3*((1-t)**2*a + 2*(1-t)*t*b + t*t*c)
        acceleration = 6*((1-t)*(b-a) + t*(c-b))
        speed = velocity.length
        minimum_speed = min(minimum_speed, speed)
        assert speed > 1e-8, (name, 'stationary centreline', t)
        cross_length = velocity.cross(acceleration).length
        radius = speed**3/cross_length if cross_length > 1e-12 else math.inf
        if radius < minimum_radius:
            minimum_radius, minimum_t = radius, t
    # The moulded section is a rounded rectangle, not an ellipse.  Bound its
    # entire perimeter, including the diagonal corners and maximum arm taper.
    maximum_section_radius = max(math.hypot(
        radii[0]*abs(math.cos(i*2*math.pi/2048))**.72,
        radii[1]*abs(math.sin(i*2*math.pi/2048))**.72) for i in range(2048))
    clearance = minimum_radius-maximum_section_radius
    row = {'object':name, 'controls_blender_m':[list(q) for q in controls],
           'section_radii_m':list(radii), 'analytic_derivative_samples':samples+1,
           'section_perimeter_samples':2048, 'minimum_speed_m':minimum_speed,
           'minimum_curvature_radius_m':minimum_radius,
           'minimum_radius_parameter':minimum_t,
           'maximum_section_circumradius_m':maximum_section_radius,
           'curvature_radius_margin_m':clearance, 'required_margin_m':.005}
    assert clearance >= .005, (name, 'section folds inside centreline bend', row)
    return row


def mirror_arm(name, controls, radii, mat, assembly, steps=40):
    """Continuous broad moulded arm, with no polygonal elbows or T-joint."""
    MIRROR_ARM_CHECKS.append(mirror_arm_curvature(name, controls, radii))
    path = mirror_bezier(controls, steps)
    vertices, faces = [], []
    for i, point in enumerate(path):
        t = i / steps
        tangent = (path[min(i+1, steps)] - path[max(i-1, 0)]).normalized()
        up = Vector((0,0,1))
        up = (up-tangent*up.dot(tangent)).normalized()
        across = up.cross(tangent).normalized()
        # End widths taper gently into the rubber hinge and head.
        thickness = .89 + .11*math.sin(math.pi*t)
        for j in range(24):
            angle = 2*math.pi*j/24
            ca, sa = math.cos(angle), math.sin(angle)
            ca = math.copysign(abs(ca)**.72, ca)
            sa = math.copysign(abs(sa)**.72, sa)
            vertices.append(tuple(point + across*(ca*radii[0]*thickness)
                                  + up*(sa*radii[1]*thickness)))
    for i in range(steps):
        for j in range(24):
            k = (j+1) % 24
            faces.append((i*24+j,i*24+k,(i+1)*24+k,(i+1)*24+j))
    faces += [tuple(reversed(range(24))), tuple(steps*24+j for j in range(24))]
    return mirror_stamp(mirror_smooth(mesh(name,vertices,faces,mat)),assembly,'moulded arm')


def mirror_support_spec(side):
    """Independent fixed-pillar supports; dimensions remain photo estimates.

    The old shared y=-4.76 put the driver shoe inside the side-window aperture.
    The door shoe is shorter to stay wholly within the fixed front quarter,
    clear of the moving front door. Reflector locations are unchanged.
    """
    if side > 0:
        return dict(y=-5.014, z=2.46, shoe_length=.106,
                    name='Mirror door side long black swept arm',
                    controls=[(1.325,-5.032,2.470),(1.390,-5.220,2.580),
                              (1.480,-5.650,2.570),(1.460,-5.800,2.500)],
                    radii=(.060,.050), steps=56)
    return dict(y=-4.995, z=2.46, shoe_length=.134,
                name='Mirror driver side short black curved arm',
                controls=[(-1.325,-5.013,2.470),(-1.400,-5.013,2.470),
                          (-1.440,-4.990,2.450),(-1.440,-4.964,2.375)],
                radii=(.045,.040), steps=48)


def mirror_hinge(side, assembly, z, y, shoe_length):
    mount = box('Mirror '+assembly+' A-pillar mounting shoe',
                (side*(1.292 if side>0 else 1.293),y,z),(.040,shoe_length,.192),plastic,.022)
    mirror_stamp(mount,assembly,'pillar mount')
    mount['mirror_support_contact']='Fixed front quarter / driver front window frame; not transparent glazing or moving door'
    a = Vector((side*1.282,y-.006,z))
    b = Vector((side*1.332,y-.018,z))
    mirror_stamp(rod('Mirror '+assembly+' pivot barrel',a,b,.066,plastic,40),assembly,'hinge')
    axis = (b-a).normalized()
    for i in range(7):
        centre = a.lerp(b,(i+.5)/7)
        mirror_stamp(rod('Mirror '+assembly+f' accordion boot rib {i+1}',
                         centre-axis*.0035,centre+axis*.0035,
                         .074 if i in (0,6) else .077,rubber,40),assembly,'accordion hinge')


def mirror_primary_support(side, assembly):
    spec = mirror_support_spec(side)
    mirror_hinge(side,assembly,spec['z'],spec['y'],spec['shoe_length'])
    mirror_arm(spec['name'],spec['controls'],spec['radii'],black,assembly,steps=spec['steps'])


MIRROR_SURFACES = []
MIRROR_ARM_CHECKS = []
_mirror_material = material('25 Polished mirror silver',(.68,.73,.75),.035,1.0)


def mirror_main(assembly, centre, normal, shell_size, glass_size, depth):
    frame = mirror_frame(normal)
    outline = mirror_outline(*shell_size)
    edge = mirror_outline(*glass_size)
    # The shell curves all the way across its exposed back.  An open raised
    # lip and a thin separate EPDM edge hold the reflective plane at depth 0.
    mirror_loft('Mirror shell '+assembly+' rounded teardrop',centre,frame,outline,
                [(-depth,.18),(-depth*.97,.55),(-depth*.82,.86),
                 (-depth*.57,.985),(-depth*.27,1.00),(-.012,.984),(-.003,.963)],
                white,assembly,opening=[(x*1.055,y*1.055) for x,y in edge],opening_depth=-.004)
    count = len(edge)
    vertices = [tuple(mirror_point(centre,frame,p,offset))
                for offset,scale in [(-.004,1.055),(0.,1.0)]
                for p in [(x*scale,y*scale) for x,y in edge]]
    faces = [(i,(i+1)%count,(i+1)%count+count,i+count) for i in range(count)]
    mirror_stamp(mirror_smooth(mesh('Mirror '+assembly+' glazing gasket',vertices,faces,rubber)),assembly,'glass gasket')
    mirror_face('Mirror reflective insert '+assembly,centre,frame,edge,_mirror_material,assembly)
    u,v,n = frame
    row = {'id':assembly,'cdlc_part':15,'physical_side':'door' if centre[0]>0 else 'driver',
           'centre_blender':list(centre),'normal_blender':list(n),
           'u_blender':list(u),'v_blender':list(v),'width_m':glass_size[0],
           'height_m':glass_size[1],'outline_uv_m':[list(p) for p in edge],
           'convex_depth_m':0.0}
    MIRROR_SURFACES.append(row)
    return frame


# All dimensions below are estimates from the manufacturer's SWB6109EV
# front and two near-side photographs, checked against the EV12G declaration.
# The body is 2.55 m wide. These coordinates are independently authored for
# the 10.5 m bus; they are not scaled iEV7 mounting points.
MIRROR_DRIVER_EYE = Vector((-.69,-4.18,1.95))


def mirror_rearward_normal(centre, target=(0,1,0)):
    incoming=(Vector(centre)-MIRROR_DRIVER_EYE).normalized()
    # This plane reflects the centre sight ray rearward in Blender +Y.
    return (Vector(target).normalized()-incoming).normalized()


mirror_primary_support(1,'door side large')
door_centre=Vector((1.460,-5.740,2.300))
mirror_main('door side large',door_centre,mirror_rearward_normal(door_centre),
            (.250,.480),(.205,.425),.125)

# The driver's small mirror has its own short black elbow, not a shortened
# instance of the opposite long arm. Height follows the iEV10 photograph.
mirror_primary_support(-1,'driver side small')
driver_centre=Vector((-1.440,-4.940,2.170))
mirror_main('driver side small',driver_centre,mirror_rearward_normal(driver_centre),
            (.235,.465),(.192,.412),.110)

# Independent convex door-side auxiliary, facing down and aft. The official
# front view places it at the inboard front end of the long assembly, not
# above the door opening. That distinction keeps the front quarter clear of
# the driver's sight line. Its 7 mm sag is supplied to the live mirror UI.
_aux_centre=Vector((1.310,-5.320,2.230))
_aux_frame=mirror_frame(mirror_rearward_normal(_aux_centre,(.04,.78,-.62)))
_aux_outline=[(math.cos(i*2*math.pi/80)*.095,
               math.sin(i*2*math.pi/80)*.066) for i in range(80)]
mirror_loft('Mirror shell door side independent oval auxiliary',_aux_centre,_aux_frame,
            _aux_outline,[(-.030,.30),(-.028,.75),(-.018,1.),(-.003,1.)],
            plastic,'door side auxiliary',opening=[(a*.915,b*.90) for a,b in _aux_outline],opening_depth=0)
mirror_face('Mirror reflective insert door side convex auxiliary',_aux_centre,_aux_frame,
            [(a*.915,b*.90) for a,b in _aux_outline],_mirror_material,'door side auxiliary',convex=.007)
_aux_joint=_aux_centre-_aux_frame[2]*.038
_aux_controls=[(1.410,-5.223,2.499),(1.404,-5.252,2.405),
               (1.350,-5.340,2.295),tuple(_aux_joint)]
MIRROR_ARM_CHECKS.append(mirror_arm_curvature('Mirror door side auxiliary independent black stay',
                                            _aux_controls,(.012,.012)))
_aux_stay=mirror_bezier(_aux_controls,32)
_aux_stay_object=tube('Mirror door side auxiliary independent black stay',_aux_stay,.012,black)
if _aux_stay_object.type=='CURVE':_aux_stay_object.data.use_fill_caps=True
mirror_stamp(_aux_stay_object,'door side auxiliary','independent stay')
mirror_stamp(rod('Mirror door side auxiliary ball joint',_aux_centre-_aux_frame[2]*.046,
                 _aux_centre-_aux_frame[2]*.018,.024,plastic,32),'door side auxiliary','ball joint')
MIRROR_SURFACES.append({'id':'door side auxiliary','cdlc_part':15,'physical_side':'door',
    'centre_blender':list(_aux_centre),'normal_blender':list(_aux_frame[2]),
    'u_blender':list(_aux_frame[0]),'v_blender':list(_aux_frame[1]),
    'width_m':.190*.915,'height_m':.132*.90,'convex_depth_m':.007,
    'outline_uv_m':[[a*.915,b*.90] for a,b in _aux_outline],'radial_rings':9})


def mirror_unity(point):
    return [float(point[0]),float(point[2]),-float(point[1])]


for _surface in MIRROR_SURFACES:
    for _key in ('centre','normal','u','v'):
        _surface[_key+'_unity']=mirror_unity(_surface[_key+'_blender'])
    _surface['overlay_offset_m']=.0007

_mirror_report={'vehicle':'SWB6109EV12G','units':'metres',
    'reference':'Manufacturer SWB6109EV family front / near-side photographs, crosschecked EV12G declaration',
    'reference_images':['official-family-20260804131306_5585.jpg',
                        'official-family-20260804131307_1245.jpg',
                        'official-family-20260804162358_7426.jpg',
                        'announcement-352-z21407020.jpg'],
    'dimensions_status':'Photograph estimates, not OEM mirror dimensions; independent 10.5 m bus coordinates.',
    'estimated_position_uncertainty_m':.10,'estimated_shell_size_uncertainty_m':.025,
    'driver_eye_blender':list(MIRROR_DRIVER_EYE),
    'door_side_blender_x':1,'driver_side_blender_x':-1,
    'main_surfaces_planar':True,'native_bone':'Root','surfaces':MIRROR_SURFACES}
(SRC/'mirror-surfaces.json').write_text(_mirror_json.dumps(_mirror_report,indent=2)+'\n',encoding='utf-8')
(SRC/'mirror-arm-validation.json').write_text(_mirror_json.dumps({
    'status':'PASS','vehicle':'SWB6109EV12G',
    'method':'Analytic cubic derivatives; conservative full-section radial bound',
    'arms':MIRROR_ARM_CHECKS},indent=2)+'\n',encoding='utf-8')
print('MIRROR_SURFACES '+_mirror_json.dumps({p['id']:{'centre_unity':p['centre_unity'],
      'normal_unity':p['normal_unity'],'size':[p['width_m'],p['height_m']]}
      for p in MIRROR_SURFACES}),flush=True)
