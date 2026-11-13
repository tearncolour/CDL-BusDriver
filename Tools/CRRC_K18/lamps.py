"""K18 lamp assemblies, traced from the manufacturer's C18/4.jpg close-up.

The photograph resolves three optical zones, a sloping row of small reflector
cells, and a clear cover over a bright formed reflector. Pixel locations are
recorded in reference/lamp-landmarks.json; the exact electrical split is not
claimed to be measured. This module is executed in the vehicle builder scope.
"""

LAMP_GLASS=mat('K18 clear lamp cover',(.82,.89,.92),.025,0,'GlassClear',glass=True)
LAMP_GLASS.node_tree.nodes.get('Principled BSDF').inputs['Transmission Weight'].default_value=1.
LAMP_GLASS['glass_alpha']=.09
REFLECTOR=mat('K18 formed aluminium lamp reflector',(.70,.73,.76),.15,.98,'Chrome')
OPTICAL_DARK=mat('K18 optical barrel shadow',(.025,.029,.034),.28,.5,'DarkGrey')
LOW_EMITTER=mat('K18 low beam filament',(.90,.95,1.),.12,0,'LampWhite',light=2)
LOW_EMITTER.node_tree.nodes.get('Principled BSDF').inputs['Emission Strength'].default_value=12.
DRL_EMITTER=mat('K18 small white lamp elements',(.85,.94,1.),.18,0,'LampWhite',light=1)
_optical_recess=0.

def optical_axis_frame(x,y):
    n=front_normal(x,y)
    horizontal=Vector((1,0,0));horizontal=(horizontal-n*horizontal.dot(n)).normalized()
    vertical=n.cross(horizontal).normalized()
    return Vector((x,y,front_z(x,y)-_optical_recess)),horizontal,vertical,n

def lamp_outline_ring(name,outer,inner_scale,material,front=.012,back=-.085):
    cx=sum(x for x,y in outer)/len(outer);cy=sum(y for x,y in outer)/len(outer)
    inner=[(cx+(x-cx)*inner_scale,cy+(y-cy)*inner_scale) for x,y in outer]
    vertices=[];n=len(outer)
    for loop,d in ((outer,front),(inner,front),(inner,back),(outer,back)):
        vertices.extend((x,y,front_z(x,y)+d) for x,y in loop)
    faces=[]
    for j in range(4):
        k=(j+1)%4
        faces.extend((j*n+i,j*n+(i+1)%n,k*n+(i+1)%n,k*n+i) for i in range(n))
    ob=mesh(name,vertices,faces,material,16)
    ob['k18_front_role']='lamp_detail'
    return ob

def cut_lamp_aperture(side,outline):
    """Real aperture through each authored lower-front LOD, before assembly."""
    cutter=skin('K18 temporary headlamp aperture cutter',[(x,y,8.0) for x,y in outline],(0,0,1.5),DARK,0)
    cutter.hide_render=True
    for body in list(CURRENT.objects):
        if body.type!='MESH' or 'K18 lower curved front skin' not in body.name:continue
        modifier=body.modifiers.new('Actual lamp aperture '+str(side),'BOOLEAN')
        modifier.operation='DIFFERENCE';modifier.solver='EXACT';modifier.object=cutter
        bpy.context.view_layer.update()
        graph=bpy.context.evaluated_depsgraph_get()
        evaluated=body.evaluated_get(graph)
        replacement=bpy.data.meshes.new_from_object(evaluated,preserve_all_data_layers=True,depsgraph=graph)
        body.modifiers.remove(modifier)
        bm=bmesh.new();bm.from_mesh(replacement)
        bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-7)
        bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=1e-7)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        bm.to_mesh(replacement);bm.free();replacement.update()
        old=body.data;body.data=replacement
        if old.users==0:bpy.data.meshes.remove(old)
        body['headlamp_apertures']=int(body.get('headlamp_apertures',0))+1
    bpy.data.objects.remove(cutter,do_unlink=True)

def lamp_lathe(name,x,y,profile,material,part=23,lod=2,segments=40):
    p,u,v,n=optical_axis_frame(x,y);vertices=[]
    for r,d in profile:
        vertices.extend(p+n*d+(u*math.cos(i*2*math.pi/segments)+v*math.sin(i*2*math.pi/segments))*r for i in range(segments))
    faces=[];rings=len(profile)
    for j in range(rings):
        k=(j+1)%rings
        faces.extend((j*segments+i,j*segments+(i+1)%segments,k*segments+(i+1)%segments,k*segments+i) for i in range(segments))
    ob=mesh(name,vertices,faces,material,part,lod=lod)
    ob['k18_front_role']='lamp_detail';ob['radial_segments']=segments;ob['radial_rings']=rings
    for face in ob.data.polygons:face.use_smooth=True
    return ob

def optical_lens(name,x,y,r,depth,material=LAMP_GLASS,part=17,lod=2):
    # A closed shallow lens, with a finite central disc rather than pole
    # degeneracies. Radial profile supports analytic LOD sampling.
    p,u,v,n=optical_axis_frame(x,y);segments=40;vertices=[]
    rings=((r,depth),(r*.79,depth+.006),(r*.38,depth+.010),
           (r*.38,depth+.005),(r*.79,depth+.002),(r,depth-.003))
    for rad,d in rings:
        vertices.extend(p+n*d+(u*math.cos(i*2*math.pi/segments)+v*math.sin(i*2*math.pi/segments))*rad for i in range(segments))
    front=len(vertices);vertices.append(p+n*(depth+.011))
    back=len(vertices);vertices.append(p+n*(depth+.006))
    faces=[]
    for a,b in ((0,1),(1,2),(3,4),(4,5),(5,0)):
        faces.extend((a*segments+i,a*segments+(i+1)%segments,b*segments+(i+1)%segments,b*segments+i) for i in range(segments))
    for i in range(segments):
        faces.append((2*segments+i,2*segments+(i+1)%segments,front))
        faces.append((3*segments+(i+1)%segments,3*segments+i,back))
    ob=mesh(name,vertices,faces,material,part,lod=lod)
    ob['k18_front_role']='lamp_detail'
    for face in ob.data.polygons:face.use_smooth=True
    return ob

def formed_reflector(name,xy,material=REFLECTOR,outer_depth=-.023):
    # Nested boundary rings create a real concave pressed bowl. The closed
    # back is behind the bowl, and every ring follows the local front surface.
    cx=sum(p[0] for p in xy)/len(xy);cy=sum(p[1] for p in xy)/len(xy)
    layers=((1.,outer_depth),(.94,outer_depth-.003),(.79,-.054),(.48,-.063),(.025,-.064),
            (.025,-.069),(.48,-.069),(.79,-.064),(.94,outer_depth-.010),(1.,outer_depth-.010))
    vertices=[];n=len(xy)
    for scale,depth in layers:
        for x,y in xy:
            xx=cx+(x-cx)*scale;yy=cy+(y-cy)*scale
            vertices.append((xx,yy,front_z(xx,yy)+depth))
    faces=[]
    for j in range(len(layers)):
        k=(j+1)%len(layers)
        faces.extend((j*n+i,j*n+(i+1)%n,k*n+(i+1)%n,k*n+i) for i in range(n))
    ob=mesh(name,vertices,faces,material,16)
    ob['k18_front_role']='lamp_detail'
    # A pressed reflector has distinct facets, not a featureless black bowl.
    for face in ob.data.polygons:face.use_smooth=False
    return ob

def build_k18_headlamp(side):
    global _optical_recess
    _optical_recess=.075
    # Shoulder, sloping inner side and inward lower point from the newer
    # frontal photograph. This is shorter and broader than the old triangle.
    control=[(1.207,1.018),(1.089,.973),(1.040,.916),(.831,.549),
             (1.084,.618),(1.173,.690),(1.201,.810)]
    border=rounded_triangle([(side*x,y) for x,y in control],.085,5)
    cx=sum(x for x,y in border)/len(border);cy=sum(y for x,y in border)/len(border)
    scaled=lambda s:[(cx+(x-cx)*s,cy+(y-cy)*s) for x,y in border]
    cut_lamp_aperture(side,scaled(.982))
    lamp_outline_ring('K18 traced lamp mounting return '+str(side),scaled(1.018),.958,RED)
    lamp_outline_ring('K18 lamp perimeter gasket '+str(side),border,.984,RUBBER,front=.011,back=-.012)
    lamp_outline_ring('K18 thin perimeter chrome '+str(side),scaled(.992),.978,CHROME,front=.013,back=.005)
    front_polygon('K18 sealed lamp rear housing '+str(side),scaled(.975),OPTICAL_DARK,16,offset=-.086,thickness=.008)
    formed_reflector('K18 full pressed reflector '+str(side),scaled(.967))
    # Two stacked outer optical bowls plus the inward lower circular optic.
    # Reflective barrels remain visible with the light switched off.
    optics=((1.088,.840,.049,False),(1.090,.724,.060,True),(1.006,.679,.041,False))
    for index,(xx,y,r,is_low) in enumerate(optics):
        x=side*xx
        lamp_lathe(f'K18 reflector bowl {side} {index}',x,y,
            [(r*1.23,.033),(r*1.16,.044),(r*.97,.052),(r*.69,.027),
             (r*.24,.024),(r*.24,.014),(r*.78,.017),(r*1.23,.023)],REFLECTOR)
        lamp_lathe(f'K18 optical bezel {side} {index}',x,y,
            [(r,.053),(r,.058),(r*.87,.060),(r*.87,.053)],CHROME)
        p,u,v,n=optical_axis_frame(x,y)
        # Small real source within the reflector, not an emissive solid disc.
        emitter=(AMBER if side<0 else AMBER_RIGHT) if index==0 else LOW_EMITTER if is_low else HEAD
        part=(26 if side<0 else 27) if index==0 else 23
        rod(f'K18 optical source {side} {index}',p+n*.024,p+n*.029,
            .019 if is_low else .010,emitter,part,n=20)
        optical_lens(f'K18 convex optical lens {side} {index}',x,y,r*.86,.057)
        if index==2:
            # Lower lens flutes visible in the close-up, real geometry with
            # sealed short prisms. They stay inside the outer cover.
            for j in range(-3,4):
                yy=y+j*.008;length=math.sqrt(max(0.,(r*.75)**2-(j*.008)**2))
                front_trace('K18 inner lens horizontal flute',[(x-length,yy),(x+length,yy)],.0013,LAMP_GLASS,17,offset=-.008)
    # Five visible reflector divisions. Their count is photographic, not a
    # claim that the lamp uses five LEDs or five separate electrical channels.
    for index in range(5):
        xx=1.177-index*.023;y=.966-index*.0105;x=side*xx
        points=[(x-side*.010,y-.013),(x+side*.010,y-.010),(x+side*.009,y+.012),(x-side*.009,y+.008)]
        front_polygon('K18 small cell cavity '+str(side)+' '+str(index),points,OPTICAL_DARK,16,offset=-.033,thickness=.005)
        lamp_outline_ring('K18 sloping small reflector cell '+str(side)+' '+str(index),points,.73,REFLECTOR,front=-.025,back=-.033)
        front_polygon('K18 small cell clear face '+str(side)+' '+str(index),points,LAMP_GLASS,17,offset=-.021,thickness=.002)
        p,u,v,n=optical_axis_frame(x,y)
        rod('K18 small reflector white element',p+n*.050,p+n*.053,.004,DRL_EMITTER,23,n=12)
    # Narrow ribs divide the lens edge; no invented thick perimeter light tube.
    for chain in ([control[0],control[1],control[2],control[3]], [control[0],control[-1],control[-2],control[-3],control[3]]):
        front_trace('K18 lens perimeter inner moulding',[(side*x,y) for x,y in chain],.0025,CHROME,16,offset=.005)
    front_polygon('K18 clear one-piece lamp cover '+str(side),scaled(.984),LAMP_GLASS,17,
                  offset=.010,thickness=.005,role='lamp_glass')
    _optical_recess=0.

def build_k18_foglamp(side):
    x=side*1.025;y=.365
    lamp_lathe('K18 round lower fog housing',x,y,[(.064,.005),(.064,.013),(.050,.024),
        (.038,.010),(.006,.009),(.006,.002),(.050,.002)],OPTICAL_DARK,16)
    lamp_lathe('K18 fog reflector facets',x,y,[(.051,.014),(.047,.020),(.026,.012),
        (.006,.011),(.006,.005),(.047,.007)],REFLECTOR,16)
    lamp_lathe('K18 fog lens outer ring',x,y,[(.051,.022),(.051,.026),(.047,.028),(.047,.022)],CHROME,16)
    p,u,v,n=optical_axis_frame(x,y)
    rod('K18 fog bulb',p+n*.011,p+n*.015,.013,HEAD,23,n=16)
    optical_lens('K18 fog lens',x,y,.047,.025)
    for j in range(-3,4):
        yy=y+j*.010;length=math.sqrt(max(0.,.042**2-(j*.010)**2))
        front_trace('K18 fog lens flute',[(x-length,yy),(x+length,yy)],.0015,LAMP_GLASS,17,offset=.035)
    front_trace('K18 lower corner bumper recess',[(side*.78,.240),(side*1.16,.245),
        (side*1.205,.275),(side*1.182,.411),(side*1.122,.460),(side*.930,.477)],.0035,BLACK,16,offset=.004)
