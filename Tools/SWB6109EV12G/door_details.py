"""Photo-informed door hardware, silver poles and grey triangular hand grips.

The two user references establish component type/colour, not factory sizes or
closed-door kinematics. All mounting dimensions below are modelling estimates.
Call patch_photo_door_details(master, scene) in memory at any timeline frame;
the original frame, source reflection and fixed door pivots are preserved.
The caller owns coordinated saving/export. This module never writes a file.
"""
import math
import json
from pathlib import Path
import bpy
import bmesh
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from source_coordinates import (canonical_export_context, canonical_world_matrix,
                                source_root, SOURCE_REFLECTION)

REVISION=4
REFERENCES=['references/user-reference-20261015-01.jpg','references/user-reference-20261015-02.jpg']


def _material(name, rgb, metallic=0., roughness=.4):
    material=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.diffuse_color=(*rgb,1.)
    material.use_nodes=True
    shader=next((node for node in material.node_tree.nodes if node.type=='BSDF_PRINCIPLED'),None)
    if shader is None:
        shader=material.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
        output=next((node for node in material.node_tree.nodes if node.type=='OUTPUT_MATERIAL'),None) or material.node_tree.nodes.new('ShaderNodeOutputMaterial')
        material.node_tree.links.new(shader.outputs['BSDF'],output.inputs['Surface'])
    shader.inputs['Base Color'].default_value=(*rgb,1.)
    shader.inputs['Metallic'].default_value=metallic
    shader.inputs['Roughness'].default_value=roughness
    material['light_id']=0;material['livery_control']=False;material['glass']=False
    return material


def _flat(matrix):
    return [float(matrix[r][c]) for c in range(4) for r in range(4)]


def stainless_handrail_material():
    """Shared, non-livery stainless finish for poles, grabs and seat handles."""
    material=_material('6109 photo | brushed stainless handrails',(.56,.58,.59),1.,.25)
    material['cdlc_palette_swatch']='Chrome'
    material['surface_description']='Neutral stainless steel; not grey paint'
    return material


def _remove_object(obj):
    data=obj.data;action=obj.animation_data.action if obj.animation_data else None
    bpy.data.objects.remove(obj,do_unlink=True)
    if data and data.users==0:
        if isinstance(data,bpy.types.Mesh):bpy.data.meshes.remove(data)
        elif isinstance(data,bpy.types.Curve):bpy.data.curves.remove(data)
    if action and action.users==0:bpy.data.actions.remove(action)


def _record_source_witness(obj,master):
    if source_root(master):
        obj['cdlc_source_canonical_matrix_at_conversion']=_flat(canonical_world_matrix(obj))
        if 'cdlc_pivot' in obj:obj['cdlc_pivot_source_blender']=list(SOURCE_REFLECTION@Vector(obj['cdlc_pivot']))


def patch_standing_zone_rails(master,scene=None):
    """Build explicit photo/video-linked instances without asserting a total count."""
    from handrail_layout import patch_evidence_handrails
    return patch_evidence_handrails(master,scene)


def _stamp(obj,part,pivot):
    obj['cdlc_part']=part;obj['cdlc_part_name']=pivot.get('cdlc_part_name','Door'+str(part))
    obj['cdlc_parent_part']=0;obj['cdlc_assignment']='photo reference door hardware'
    obj['preserve_lod']=True;obj['photo_door_detail_revision']=REVISION
    obj['cdlc_pivot_object']=pivot.name
    for key in ('cdlc_pivot','cdlc_pivot_unity','cdlc_rest_rotation'):
        if key in pivot:obj[key]=list(pivot[key])
    obj['reference_dimensions']='Photo-proportion estimate, not manufacturer mounting drawing'


def _replace(master,name,data,material,part,pivot):
    existing=master.objects.get(name)
    if existing:
        old=existing.data;bpy.data.objects.remove(existing,do_unlink=True)
        if old and old.users==0:
            if isinstance(old,bpy.types.Mesh):bpy.data.meshes.remove(old)
            elif isinstance(old,bpy.types.Curve):bpy.data.curves.remove(old)
    obj=bpy.data.objects.new(name,data);master.objects.link(obj)
    obj.data.materials.append(material)
    obj.parent=pivot;obj.matrix_parent_inverse=pivot.matrix_world.inverted()
    obj.matrix_basis=Matrix.Identity(4)
    _stamp(obj,part,pivot)
    return obj


def _mesh(master,name,vertices,faces,material,part,pivot):
    data=bpy.data.meshes.new(name);data.from_pydata(vertices,[],faces);data.update()
    bm=bmesh.new();bm.from_mesh(data);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(data);bm.free()
    return _replace(master,name,data,material,part,pivot)


def _tube(master,name,points,radius,material,part,pivot):
    data=bpy.data.curves.new(name,'CURVE');data.dimensions='3D'
    data.resolution_u=1;data.bevel_depth=radius;data.bevel_resolution=2;data.use_fill_caps=True
    cyclic=len(points)>2 and points[0]==points[-1]
    if cyclic:points=points[:-1]
    spline=data.splines.new('POLY');spline.points.add(len(points)-1);spline.use_cyclic_u=cyclic
    for point,co in zip(spline.points,points):point.co=(*co,1.)
    return _replace(master,name,data,material,part,pivot)


def _box(master,name,center,size,material,part,pivot,bevel=0.):
    vertices=[tuple(center[k]+sign[k]*size[k]/2 for k in range(3))
              for sign in ((-1,-1,-1),(-1,-1,1),(-1,1,-1),(-1,1,1),(1,-1,-1),(1,-1,1),(1,1,-1),(1,1,1))]
    faces=[(0,2,3,1),(4,5,7,6),(0,1,5,4),(2,6,7,3),(0,4,6,2),(1,3,7,5)]
    obj=_mesh(master,name,vertices,faces,material,part,pivot)
    if bevel:
        mod=obj.modifiers.new('Rounded hardware edges','BEVEL');mod.width=bevel;mod.segments=3
    return obj


def _rounded_triangle(top_z,half_width=.084,height=.147):
    """Matching outer/inner rounded outlines of a moulded flat triangle grip."""
    points=[Vector((0.,top_z)),Vector((half_width,top_z-height)),Vector((-half_width,top_z-height))]
    result=[]
    for i,point in enumerate(points):
        start=point+(points[i-1]-point)*.20
        end=point+(points[(i+1)%3]-point)*.20
        for j in range(7):
            t=j/6;result.append((1-t)**2*start+2*(1-t)*t*point+t*t*end)
    centroid=Vector((0,top_z-height*.64))
    inner=[centroid+(p-centroid)*.70 for p in result]
    return result,inner


class _ActualDoorSide:
    """Ray samples of the existing right-side opaque body, in canonical metres."""
    def __init__(self,master):
        vertices=[];triangles=[];owners=[]
        deps=bpy.context.evaluated_depsgraph_get()
        for obj in master.objects:
            if obj.type!='MESH' or not obj.name.startswith(('Side black window frame 1 ',
                    'Side lower structural panel 1 ','Black lower window field 1 ')):
                continue
            data=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
            data.calc_loop_triangles();matrix=canonical_world_matrix(obj);offset=len(vertices)
            vertices.extend(matrix@vertex.co for vertex in data.vertices)
            triangles.extend(tuple(offset+i for i in tri.vertices) for tri in data.loop_triangles)
            owners.extend([obj.name]*len(data.loop_triangles));bpy.data.meshes.remove(data)
        if not triangles:raise RuntimeError('No actual opaque door-side body to mount exterior button')
        self.tree=BVHTree.FromPolygons(vertices,triangles,all_triangles=True)
        self.owners=owners;self.vertices=vertices;self.triangles=triangles

    def sample(self,y,z):
        point,normal,index,distance=self.tree.ray_cast(Vector((5.,y,z)),Vector((-1.,0.,0.)),10.)
        if point is None:raise RuntimeError(f'Exterior hardware has no supporting body at {y}, {z}')
        if normal.x<0:normal=-normal
        if normal.x<.8:raise RuntimeError('Exterior hardware encountered an unexpected side-surface normal')
        # BVH ray hit coordinates are float32. Reproject onto that exact source
        # triangle plane in Python double arithmetic to avoid a sub-micron
        # sawtooth across what is actually one perfectly planar body patch.
        a=self.vertices[self.triangles[index][0]]
        point=Vector((float(a.x)-(float(normal.y)*(y-float(a.y))+float(normal.z)*(z-float(a.z)))/float(normal.x),y,z))
        return point,normal,self.owners[index]


def _radial_body_piece(master,name,profile,surface,y,z,material,pivot,segments=32):
    """Closed profile of a small surface-mounted ring or solid circular button."""
    vertices=[];rows=[];faces=[]
    for radius,depth in profile:
        if radius==0:
            point,_,_=surface.sample(y,z);rows.append([len(vertices)])
            vertices.append((point.x+depth,y,z))
        else:
            row=[]
            for i in range(segments):
                angle=2*math.pi*i/segments;py=y+radius*math.cos(angle);pz=z+radius*math.sin(angle)
                point,_,_=surface.sample(py,pz);row.append(len(vertices));vertices.append((point.x+depth,py,pz))
            rows.append(row)
    for first,second in zip(rows,rows[1:]+rows[:1]):
        if len(first)==1 and len(second)==1:continue
        for i in range(segments):
            j=(i+1)%segments
            if len(first)==1:faces.append((first[0],second[i],second[j]))
            elif len(second)==1:faces.append((first[i],second[0],first[j]))
            else:faces.append((first[i],second[i],second[j],first[j]))
    return _mesh(master,name,vertices,faces,material,16,pivot)


def _projected_badge_solid(master,name,points,triangles,surface,cy,cz,depth,thickness,material,pivot):
    # Use the project's already verified pictogram triangles, closing every
    # actual contour; do not synthesize the icon with a font or primitive rods.
    # The front trace was triangulated before curved projection and includes
    # a few collinear path samples. On this flat side panel they must be
    # dissolved in 2D before extrusion, never discarded from a closed solid.
    planar=bpy.data.meshes.new('__side_pictogram_planar')
    planar.from_pydata([(u,v,0.) for u,v in points],[],triangles);planar.update()
    bm=bmesh.new();bm.from_mesh(planar)
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-7)
    bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=1e-7)
    bmesh.ops.triangulate(bm,faces=list(bm.faces));bm.verts.index_update()
    points=[(float(v.co.x),float(v.co.y)) for v in bm.verts]
    triangles=[tuple(v.index for v in f.verts) for f in bm.faces]
    bm.free();bpy.data.meshes.remove(planar)
    edge_count={}
    for face in triangles:
        for a,b in zip(face,face[1:]+face[:1]):
            key=tuple(sorted((a,b)));edge_count[key]=edge_count.get(key,0)+1
    boundary=[edge for edge,count in edge_count.items() if count==1]
    if any(count>2 for count in edge_count.values()):raise RuntimeError('Pictogram contains nonmanifold source triangles')
    vertices=[]
    for d in (depth,depth+thickness):
        for u,v in points:
            y,z=cy-u,cz+v;point,_,_=surface.sample(y,z)
            vertices.append((point.x+d,y,z))
    n=len(points);faces=list(triangles)+[tuple(i+n for i in face) for face in triangles]
    faces.extend((a,b,b+n,a+n) for a,b in boundary)
    return _mesh(master,name,vertices,faces,material,16,pivot)


def patch_photo_exterior_button(master,scene=None):
    """Add the photo's fixed-body red/yellow button and verified wheelchair ink.

    Position and dimensions are explicit estimates. Its label is not legible,
    so no emergency/release function or unreadable text is invented.
    """
    scene=scene or bpy.context.scene;old_frame=scene.frame_current;created=[]
    try:
        scene.frame_set(0);bpy.context.view_layer.update()
        with canonical_export_context(master):
            pivots=[o for o in master.objects if o.get('cdlc_is_part_pivot') and int(o.get('cdlc_part',-1))==16]
            if len(pivots)!=1:raise RuntimeError('Exterior button requires the unique fixed Exterior part 16 pivot')
            pivot=pivots[0];surface=_ActualDoorSide(master)
            yellow=_material('6109 photo | exterior button yellow surround',(.92,.59,.018),.02,.35)
            red=_material('6109 photo | exterior button red centre',(.63,.025,.018),.02,.35)
            black=_material('6109 photo | exterior button mounting gasket',(.012,.015,.015),0.,.6)
            y,z=1.17,1.30
            centre,normal,body=surface.sample(y,z)
            for name,profile,material in (
                ('Exterior door-side button | dark mounting gasket',[(0,.00015),(.047,.00015),(.047,.0025),(0,.0025)],black),
                ('Exterior door-side button | yellow annular surround',[(.025,.0025),(.042,.0025),(.045,.006),(.045,.013),(.042,.017),(.025,.017),(.023,.014),(.023,.006)],yellow),
                ('Exterior door-side button | red recessed centre',[(0,.0025),(.0225,.0025),(.0225,.012),(.0205,.014),(0,.014)],red)):
                created.append(_radial_body_piece(master,name,profile,surface,y,z,material,pivot))
            # The existing front badge uses a verified traced wheelchair
            # pictogram. Reuse that actual ink on the green side field; the
            # unreadable fine print visible in the photo is deliberately absent.
            green=_material('6109 photo | accessibility green field',(.025,.24,.13),0.,.45)
            white=_material('6109 photo | accessibility white ink',(.87,.89,.86),0.,.45)
            cy,cz=1.34,1.16
            field=[(-.0525,-.06),(.0525,-.06),(.0525,.06),(-.0525,.06)]
            created.append(_projected_badge_solid(master,'Exterior door-side wheelchair sign | green field',field,[(0,1,2),(0,2,3)],surface,cy,cz,.00015,.00030,green,pivot))
            source=Path(__file__).resolve().parents[2]/'ModelSource/SWB6109EV12G/front-badge-paths.json'
            ink=json.loads(source.read_text(encoding='utf-8'))['groups']['wheelchair']
            points=[(u*.115,v*.115) for u,v in ink['vertices_normalized']]
            triangles=[tuple(t) for t in ink['triangles']]
            created.append(_projected_badge_solid(master,'Exterior door-side wheelchair sign | verified white pictogram',points,triangles,surface,cy,cz,.00045,.00020,white,pivot))
            outer=[(-.0495,-.057),(.0495,-.057),(.0495,.057),(-.0495,.057)]
            inner=[(-.0475,-.055),(.0475,-.055),(.0475,.055),(-.0475,.055)]
            points=outer+inner;triangles=[]
            for i in range(4):
                j=(i+1)%4;triangles.extend(((i,j,j+4),(i,j+4,i+4)))
            created.append(_projected_badge_solid(master,'Exterior door-side wheelchair sign | white border',points,triangles,surface,cy,cz,.00045,.00020,white,pivot))
            bpy.context.view_layer.update()
            for obj in created:
                obj['photo_exterior_button_revision']=REVISION
                obj['cdlc_assignment']='fixed body photo reference accessory'
                obj['surface_fitted']=True
                obj['cdlc_source_canonical_matrix_at_conversion']=_flat(canonical_world_matrix(obj))
                if source_root(master) and 'cdlc_pivot' in obj:
                    obj['cdlc_pivot_source_blender']=list(SOURCE_REFLECTION@Vector(obj['cdlc_pivot']))
            return {'revision':REVISION,'reference':REFERENCES[1],'objects':[o.name for o in created],
                    'part':16,'fixed_body_parent':pivot.name,'supporting_body':body,
                    'button_centre_canonical':list(centre),'button_surface_normal_canonical':list(normal),
                    'button_outer_diameter_m':.090,'button_max_projection_m':.017,
                    'green_sign_centre_yz':[cy,cz],'green_sign_size_m':[.105,.120],
                    'pictogram_source':'front-badge-paths.json groups.wheelchair, existing verified trace',
                    'confirmed':['red circular centre','yellow surrounding ring','fixed side-body placement aft of middle door','green wheelchair sign nearby'],
                    'estimates':['all metric coordinates and dimensions','green-field layout around reused verified pictogram'],
                    'unknown':['button function and unreadable labels; no functional text added'],
                    'fit':'All ring and decal vertices ray-sampled against actual opaque side surfaces, not maximum half-width'}
    finally:
        scene.frame_set(old_frame);bpy.context.view_layer.update()


def patch_photo_door_details(master,scene=None):
    scene=scene or bpy.context.scene;old_frame=scene.frame_current
    scene.frame_set(0);bpy.context.view_layer.update()
    silver=stainless_handrail_material()
    grey=_material('6109 photo | grey moulded hand grips',(.22,.24,.25),.05,.48)
    black=_material('6109 photo | door handle mounting feet',(.022,.025,.026),.1,.45)
    changed=[];pole_names=[];grip_names=[];door_rows=[]
    try:
        with canonical_export_context(master):
            rail_layout=patch_standing_zone_rails(master,scene)
            # Poles only: stop buttons, floor safety strips and all seats retain
            # their own materials and are intentionally outside this selector.
            for obj in master.objects:
                if obj.type not in {'MESH','CURVE'}:continue
                if obj.name.startswith(('Ceiling longitudinal grab rail','Saloon upper cross rail','Saloon vertical grab pole','Driver partition frame','Fare validator support','Standing-zone rail hanger','Standing-zone pole foot','Standing-zone ceiling attachment','Standing-zone strap collar')):
                    obj.data.materials.clear();obj.data.materials.append(silver)
                    obj['photo_pole_material_revision']=REVISION;pole_names.append(obj.name)
            hinges=[obj for obj in master.objects if obj.get('cdlc_is_part_pivot') and int(obj.get('cdlc_part',-1)) in range(4,8)]
            if len(hinges)!=4:
                hinges=[master.objects.get(f'Door{di}{leaf} hinge') for di in (1,2) for leaf in ('A','B')]
            if any(obj is None for obj in hinges) or len(hinges)!=4:raise RuntimeError('Expected four existing door leaf pivots')
            pivot_before={h.name:canonical_world_matrix(h).copy() for h in hinges}
            for hinge in sorted(hinges,key=lambda o:int(o.get('cdlc_part',4)) if 'cdlc_part' in o else o.name):
                if 'cdlc_part' in hinge:part=int(hinge['cdlc_part']);di=(part-4)//2+1;leaf=(part-4)%2+1
                else:di=int(hinge.name[4]);leaf=1 if 'A' in hinge.name else 2;part=4+(di-1)*2+leaf-1
                prefix=f'Door {di}.{leaf}'
                glazing=master.objects.get(prefix+' glazing')
                if glazing is None:raise RuntimeError('Missing actual door glazing '+prefix)
                corners=[canonical_world_matrix(glazing)@Vector(c) for c in glazing.bound_box]
                x=(min(c.x for c in corners)+max(c.x for c in corners))/2
                lo,hi=min(c.y for c in corners),max(c.y for c in corners)
                direction=1 if leaf==1 else -1
                # Both handles are reflected leaf-to-leaf: upper end towards
                # outer hinged stile, lower end towards the centre/free edge.
                y_high=(lo+.035) if direction==1 else (hi-.035)
                y_low=(hi-.055) if direction==1 else (lo+.055)
                z_high,z_low=1.76,.91
                x_grab=x-.080
                # Main sloping grip is a single closed tube, joined to compact
                # black mounting feet. No glass partition bars are added.
                handle=_tube(master,prefix+' interior grab',[(x_grab,y_low,z_low),(x_grab,y_high,z_high)],.0105,silver,part,hinge)
                changed.append(handle)
                for name,y,z in (('lower',y_low,z_low),('upper',y_high,z_high)):
                    changed.append(_tube(master,prefix+' '+name+' handle stand-off',[(x-.023,y,z),(x_grab,y,z)],.013,black,part,hinge))
                    changed.append(_box(master,prefix+' '+name+' handle foot',(x-.015,y,z),(.024,.054,.067),black,part,hinge,.006))
                hp=hinge.matrix_world.translation
                # A spindle on the unchanged hinge and a short arm under the
                # actual overhead drive cover remain coherent while opening.
                changed.append(_tube(master,prefix+' top drive spindle',[(hp.x,hp.y,2.445),(hp.x,hp.y,2.570)],.016,silver,part,hinge))
                arm_end=(x-.022,hp.y+direction*.205,2.462)
                changed.append(_tube(master,prefix+' top drive crank',[(hp.x,hp.y,2.462),arm_end],.012,black,part,hinge))
                changed.append(_tube(master,prefix+' top crank pin',[(arm_end[0],arm_end[1],2.435),(arm_end[0],arm_end[1],2.485)],.017,silver,part,hinge))
                door_rows.append({'part':part,'upper_canonical':[x_grab,y_high,z_high],
                                  'lower_canonical':[x_grab,y_low,z_low],'grip_radius_m':.0105,
                                  'hinge_unchanged':True,'dimensions_status':'photo proportion estimates; opened-leaf perspective prevents exact closed-position recovery'})
            # Retain each actual stem and its pivot, replacing only the moulded
            # grip geometry. The flat 18 mm thickness follows the visible grey
            # triangle type; absolute dimensions remain an explicit estimate.
            grips=[obj for obj in master.objects if obj.name.startswith('Passenger hand strap')]
            stems=[obj for obj in master.objects if obj.name.startswith('Hand strap stem')]
            if len(grips)!=len(stems):raise RuntimeError('Cannot pair all hand grips and stems')
            for grip in grips:
                if int(grip.get('handrail_layout_revision',0)) >= 4:
                    grip_names.append(grip.name)
                    continue
                corners=[canonical_world_matrix(grip)@Vector(c) for c in grip.bound_box]
                cx=(min(p.x for p in corners)+max(p.x for p in corners))/2
                cy=(min(p.y for p in corners)+max(p.y for p in corners))/2
                candidates=[]
                for stem in stems:
                    sc=[canonical_world_matrix(stem)@Vector(c) for c in stem.bound_box]
                    sx=(min(p.x for p in sc)+max(p.x for p in sc))/2;sy=(min(p.y for p in sc)+max(p.y for p in sc))/2
                    if math.hypot(sx-cx,sy-cy)<.003:candidates.append((stem,min(p.z for p in sc)))
                if len(candidates)!=1:raise RuntimeError('Ambiguous triangular grip attachment '+grip.name)
                stem,bottom=candidates[0]
                stem_bounds=[canonical_world_matrix(stem)@Vector(c) for c in stem.bound_box]
                top=max(p.z for p in stem_bounds)
                part=int(grip.get('cdlc_part',13));parent=grip.parent
                if parent is None:parent=source_root(master)
                if parent is None:raise RuntimeError('Create the strap rig before applying triangular grip details')
                # A narrow flat hanger replaces the generic round yellow stem.
                # Its top and bottom remain exact, preserving the established
                # grip height and animation pivot. The small silver top clamp
                # is included in this same stem object / independent part.
                vertices=[];faces=[];material_ids=[]
                for z0,z1,width,depth,material_index in ((bottom,top-.020,.026,.006,0),(top-.021,top,.034,.012,1)):
                    offset=len(vertices)
                    vertices.extend((cx+sx*width/2,cy+sy*depth/2,z) for sx,sy,z in
                                    ((-1,-1,z0),(-1,-1,z1),(-1,1,z0),(-1,1,z1),(1,-1,z0),(1,-1,z1),(1,1,z0),(1,1,z1)))
                    new_faces=[tuple(offset+i for i in face) for face in ((0,2,3,1),(4,5,7,6),(0,1,5,4),(2,6,7,3),(0,4,6,2),(1,3,7,5))]
                    faces.extend(new_faces);material_ids.extend([material_index]*len(new_faces))
                stem_name=stem.name
                stem=_mesh(master,stem_name,vertices,faces,grey,part,parent)
                stem.data.materials.append(silver)
                for face,index in zip(stem.data.polygons,material_ids):face.material_index=index
                stem['photo_flat_hanger_revision']=REVISION;changed.append(stem)
                stems=[obj for obj in master.objects if obj.name.startswith('Hand strap stem')]
                # Keep the upper moulded edge touching the original stem end.
                outer,inner=_rounded_triangle(bottom+.016)
                n=len(outer);vertices=[]
                for depth in (-.009,.009):
                    vertices.extend((cx+p.x,cy+depth,p.y) for p in outer)
                    vertices.extend((cx+p.x,cy+depth,p.y) for p in inner)
                faces=[]
                for i in range(n):
                    j=(i+1)%n
                    faces.extend(((i,j,n+j,n+i),(2*n+i,3*n+i,3*n+j,2*n+j),
                                  (i,2*n+i,2*n+j,j),(n+i,n+j,3*n+j,3*n+i)))
                name=grip.name
                replacement=_mesh(master,name,vertices,faces,grey,part,parent)
                replacement['photo_triangular_grip_revision']=REVISION
                bevel=replacement.modifiers.new('Moulded grip edge rounds','BEVEL')
                bevel.width=.002;bevel.segments=3
                changed.append(replacement);grip_names.append(name)
            bpy.context.view_layer.update()
            for hinge in hinges:
                if any(abs(pivot_before[hinge.name][r][c]-canonical_world_matrix(hinge)[r][c])>1e-8 for r in range(4) for c in range(4)):
                    raise RuntimeError('Photo details changed a door pivot')
            if source_root(master):
                for obj in changed:
                    obj['cdlc_source_canonical_matrix_at_conversion']=_flat(canonical_world_matrix(obj))
                    if 'cdlc_pivot' in obj:
                        obj['cdlc_pivot_source_blender']=list(SOURCE_REFLECTION@Vector(obj['cdlc_pivot']))
            exterior_button=patch_photo_exterior_button(master,scene)
            from animation_rig import prepare_strap_rig
            straps=prepare_strap_rig(master,scene)
            report={'revision':REVISION,'references':REFERENCES,'doors':door_rows,'exterior_button':exterior_button,'rail_layout':rail_layout,'straps':straps,
                    'triangular_grips':grip_names,'silver_poles':pole_names,'new_or_replaced_objects':[o.name for o in changed],
                    'confirmed_from_photos':['black framed large-glass double doors','thin silver slanted interior handles with dark feet','visible upper drive hardware','grey rounded triangular hand grips','silver saloon rails'],
                    'estimates':['all hardware mounting dimensions','diagonal angle in the closed-door frame','same middle-door detail applied to the partly obscured front doors','top-drive links are visible-form stand-ins following the existing leaf motion, not a reconstructed actuator mechanism','triangular grip 168 mm width / 147 mm height / 18 mm thickness'],
                    'preserved':['door hinges and 86 degree actions','each evidence-linked stem upper attachment','contiguous independently articulated strap parts from 34','grey triangle outlines','door glazing and black frames'],
                    'scope':'Photo-informed in-memory detail correction, not a manufacturer kinematic reconstruction'}
            return report
    finally:
        scene.frame_set(old_frame);bpy.context.view_layer.update()
