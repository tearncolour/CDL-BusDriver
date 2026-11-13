"""Limited cabin details supported by the user's 2026-10-15 photographs.

The side service ducts, grouped perforations and light channels are visible
evidence. Their dimensions and panel/slot counts are modelling estimates.
The user explicitly specified no curtains for this vehicle on 2026-10-15;
this overrides the curtain detail in the selected interior reference video.
Do not generate curtains, curtain tracks, tie bands or hanging clips.
patch_photo_cabin_details(master, scene=None) changes memory only. The caller
owns saving/export. Existing seats, passenger locations, doors and straps are
never moved. Construction and corrected physical-source frames are supported.
"""
import math
import re
import bpy
import bmesh
from mathutils import Matrix, Vector
from source_coordinates import canonical_export_context, canonical_world_matrix, _add_source_metadata

REVISION = 3
MARKER = 'photo_cabin_detail_revision'
PREFIX = 'Photo cabin | '
REFERENCE = 'references/user-reference-20261015-01.jpg'


def _material(name, rgb, metallic=0., roughness=.5, light=0):
    mat = bpy.data.materials.get(PREFIX+name) or bpy.data.materials.new(PREFIX+name)
    mat.diffuse_color = (*rgb, 1.)
    mat.use_nodes = True
    shader = next((n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if shader is None:
        shader = mat.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
        output = next((n for n in mat.node_tree.nodes if n.type == 'OUTPUT_MATERIAL'), None) or mat.node_tree.nodes.new('ShaderNodeOutputMaterial')
        mat.node_tree.links.new(shader.outputs['BSDF'], output.inputs['Surface'])
    shader.inputs['Base Color'].default_value = (*rgb, 1.)
    shader.inputs['Metallic'].default_value = metallic
    shader.inputs['Roughness'].default_value = roughness
    shader.inputs['Emission Color'].default_value = (*rgb, 1.)
    shader.inputs['Emission Strength'].default_value = .6 if light else 0.
    mat['light_id'] = light
    mat['livery_control'] = False
    mat['glass'] = False
    return mat


def _remove(obj):
    data = obj.data
    bpy.data.objects.remove(obj, do_unlink=True)
    if data and data.users == 0:
        if isinstance(data, bpy.types.Mesh): bpy.data.meshes.remove(data)
        elif isinstance(data, bpy.types.Curve): bpy.data.curves.remove(data)


def _mesh(master, name, vertices, faces, mats, pivot, material_indices=None):
    name = PREFIX+name
    if bpy.data.objects.get(name):
        raise RuntimeError('Unowned object uses photo cabin name: '+name)
    data = bpy.data.meshes.new(name)
    data.from_pydata(vertices, [], faces)
    for mat in mats: data.materials.append(mat)
    if material_indices:
        for face, index in zip(data.polygons, material_indices): face.material_index = index
    data.update()
    bm = bmesh.new()
    bm.from_mesh(data)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=1e-8)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bmesh.ops.triangulate(bm, faces=list(bm.faces))
    boundary = sum(not edge.is_manifold for edge in bm.edges)
    small = sum(face.calc_area() <= 1e-13 for face in bm.faces)
    if boundary or small or any(not math.isfinite(c) for v in bm.verts for c in v.co):
        bm.free()
        bpy.data.meshes.remove(data)
        raise RuntimeError(f'{name}: nonmanifold={boundary}, degenerate={small}')
    if bm.calc_volume(signed=True) <= 0:
        bm.free()
        bpy.data.meshes.remove(data)
        raise RuntimeError(name+': expected an outward closed solid')
    bm.to_mesh(data)
    bm.free()
    obj = bpy.data.objects.new(name, data)
    master.objects.link(obj)
    obj.parent = pivot
    obj.matrix_parent_inverse = pivot.matrix_world.inverted()
    obj.matrix_basis = Matrix.Identity(4)
    obj['cdlc_part'] = 14
    obj['cdlc_part_name'] = 'Ceiling'
    obj['cdlc_parent_part'] = 0
    obj['cdlc_assignment'] = 'photo-supported cabin service fittings'
    obj['cdlc_pivot_object'] = pivot.name
    for key in ('cdlc_pivot', 'cdlc_pivot_unity', 'cdlc_rest_rotation'):
        if key in pivot: obj[key] = list(pivot[key])
    obj['preserve_lod'] = True
    obj[MARKER] = REVISION
    obj['reference_dimensions'] = 'Photo-supported service ducts; estimated dimensions and slot counts'
    bpy.context.view_layer.update()
    _add_source_metadata(obj)
    return obj


def _box(master, name, centre, size, mat, pivot):
    vertices = [tuple(centre[k]+sign[k]*size[k]/2 for k in range(3))
                for sign in ((-1,-1,-1),(-1,-1,1),(-1,1,-1),(-1,1,1),(1,-1,-1),(1,-1,1),(1,1,-1),(1,1,1))]
    return _mesh(master, name, vertices, [(0,2,3,1),(4,5,7,6),(0,1,5,4),(2,6,7,3),(0,4,6,2),(1,3,7,5)], [mat], pivot)


def _duct(master, name, side, a, b, mats, pivot):
    """A closed service box whose underside has blind, backed pockets.

    Slot walls replace the underside surface, rather than dark decals or
    through-holes. All grid edges match the side/end surfaces exactly.
    """
    # The photograph shows a broad sloping inboard fascia, not a vertical
    # hanging cupboard. Its lamp channel sits toward the aisle on that slope.
    z = 2.700
    def surface(x): return z+max(0.,.980-x)*(.260/.240)
    light_x = (.768, .803)
    slot_x = [(1.024, 1.038), (1.102, 1.116), (1.180, 1.194)]
    centre = (a+b)/2
    slot_y = [(centre+d-.011, centre+d+.011) for d in (-.1125,-.0675,-.0225,.0225,.0675,.1125)]
    light_y = (a+.045, b-.045)
    xs = sorted({.740,.980,1.232,*light_x,*[v for interval in slot_x for v in interval]})
    ys = sorted({a,b,*light_y,*[v for interval in slot_y for v in interval]})
    vertices=[];faces=[];indices=[];lookup={}
    def point(x,y,h):
        value=(round(side*x,9),round(y,9),round(h,9))
        if value not in lookup: lookup[value]=len(vertices);vertices.append(value)
        return lookup[value]
    def face(points,index=0):
        faces.append(tuple(point(*p) for p in points));indices.append(index)
    def in_interval(value,interval): return interval[0] < value < interval[1]
    for xa,xb in zip(xs,xs[1:]):
        for ya,yb in zip(ys,ys[1:]):
            cx,cy=(xa+xb)/2,(ya+yb)/2
            lamp=in_interval(cx,light_x) and in_interval(cy,light_y)
            vent=any(in_interval(cx,s) for s in slot_x) and any(in_interval(cy,s) for s in slot_y)
            depth=.008 if lamp else (.012 if vent else 0.)
            inward=Vector((.260/.240,0,1)).normalized() if lamp else Vector((0,0,1))
            rim=[(xa,ya,surface(xa)),(xb,ya,surface(xb)),(xb,yb,surface(xb)),(xa,yb,surface(xa))]
            floor=[tuple(Vector(p)+inward*depth) for p in rim]
            face(floor,2 if lamp else (1 if vent else 0))
            if depth:
                # Adjacent cells of the light channel share one recess; no
                # internal wall is inserted across that continuous cavity.
                for j in range(4):
                    if lamp and j in (0,2):
                        edge_y=ya if j==0 else yb
                        if light_y[0]+1e-8 < edge_y < light_y[1]-1e-8: continue
                    k=(j+1)%4
                    face([rim[j],rim[k],floor[k],floor[j]],0)
    # Outline includes the complete bottom grid, so end caps have no T joints.
    outline=[(x,surface(x)) for x in xs]+[(1.232,2.790),(1.195,3.023),(.740,3.023)]
    for j in range(len(xs)-1,len(outline)):
        k=(j+1)%len(outline)
        for ya,yb in zip(ys,ys[1:]):
            face([(outline[j][0],ya,outline[j][1]),(outline[k][0],ya,outline[k][1]),
                  (outline[k][0],yb,outline[k][1]),(outline[j][0],yb,outline[j][1])])
    face([(x,a,h) for x,h in outline])
    face([(x,b,h) for x,h in reversed(outline)])
    obj=_mesh(master,name,vertices,faces,mats,pivot,indices)
    obj['recess_depth_m']=.012
    obj['blind_vent_pockets']=len(slot_x)*len(slot_y)
    obj['light_channel_depth_m']=.008
    obj['static_lamp_housing']=True
    return obj


def _bounds(obj,deps):
    evaluated=obj.evaluated_get(deps)
    mesh=evaluated.to_mesh()
    try:
        points=[evaluated.matrix_world@v.co for v in mesh.vertices]
    finally:
        evaluated.to_mesh_clear()
    if not points: raise RuntimeError('Empty geometry in cabin clearance check: '+obj.name)
    return [min(p[k] for p in points) for k in range(3)], [max(p[k] for p in points) for k in range(3)]


def _separation(a,b):
    return math.sqrt(sum(max(a[0][k]-b[1][k],b[0][k]-a[1][k],0.)**2 for k in range(3)))


def patch_photo_cabin_details(master,scene=None):
    scene=scene or bpy.context.scene
    old_frame=scene.frame_current
    created=[];windows=[];poses=[];removed_lights=[]
    try:
        scene.frame_set(0);bpy.context.view_layer.update()
        with canonical_export_context(master):
            pivots=[o for o in master.objects if o.get('cdlc_is_part_pivot') and int(o.get('cdlc_part',-1))==14]
            if len(pivots)!=1: raise RuntimeError('Photo cabin details require the unique part 14 rig pivot')
            pivot=pivots[0]
            for obj in list(master.objects):
                if obj.get(MARKER): _remove(obj)
            panel=_material('service box warm grey',(.61,.63,.63),0.,.45)
            dark=_material('backed recess interior',(.035,.043,.046),0.,.66)
            light=_material('light channel diffuser',(.84,.88,.86),0.,.38,8)
            # Five independent closed boxes per side retain a real 3 mm panel
            # joint; recessed pockets already have their own sealed backing.
            for side in (-1,1):
                for index in range(5):
                    a=-3.45+index*1.632+.0015;b=-3.45+(index+1)*1.632-.0015
                    created.append(_duct(master,f'{side} service duct panel {index+1}',side,a,b,[panel,dark,light],pivot))
            for obj in list(master.objects):
                match=re.fullmatch(r'Side glazing (-?1) (\d+)',obj.name)
                if not match: continue
                points=[canonical_world_matrix(obj)@Vector(c) for c in obj.bound_box]
                lo,hi=min(p.y for p in points),max(p.y for p in points)
                if hi<=-3.60: continue  # driver/front-quarter visibility remains clear
                a,b=lo+.035,min(hi-.035,4.71)
                if b-a<.45: raise RuntimeError('Unexpected narrow saloon window '+obj.name)
                # Retain the window-bay audit without adding fittings that
                # the user explicitly excluded from this vehicle.
                windows.append({'glazing':obj.name,'canonical_y':[lo,hi],
                                'glass_width_m':hi-lo,'curtains':False})
            if len(windows)!=11: raise RuntimeError(f'Expected eleven passenger window bays, found {len(windows)}')
            # These are the two superseded featureless strips, not seats or
            # any independently authored light/door component.
            for obj in list(master.objects):
                if re.fullmatch(r'Ceiling passenger light(?:\.\d{3})?',obj.name):
                    removed_lights.append(obj.name);_remove(obj)
        for frame in (0,25,30,40):
            scene.frame_set(frame);bpy.context.view_layer.update();deps=bpy.context.evaluated_depsgraph_get()
            doors=[o for o in master.objects if o.type in {'MESH','CURVE'} and int(o.get('cdlc_part',-1)) in (4,5,6,7)]
            if not doors: raise RuntimeError('No door geometry available for cabin clearance validation')
            minimum=math.inf;closest=None;closest_bounds=None
            cabin_bounds={obj:_bounds(obj,deps) for obj in created}
            for door in doors:
                db=_bounds(door,deps)
                for obj in created:
                    cb=cabin_bounds[obj]
                    distance=_separation(db,cb)
                    if distance<minimum:
                        minimum=distance;closest=[door.name,obj.name]
                        closest_bounds={'door':db,'cabin':cb}
                    if distance<.001: raise RuntimeError(f'Photo cabin/door bounds not separated at frame {frame}: {door.name}, {obj.name}')
            poses.append({'frame':frame,'minimum_aabb_separation_m':minimum,'closest_pair':closest,
                          'closest_evaluated_world_bounds':closest_bounds})
        triangles=sum(len(o.data.polygons) for o in created)
        return {'status':'PASS','revision':REVISION,'reference':REFERENCE,'objects':len(created),'triangles':triangles,
                'nonmanifold_edges':0,'degenerate_triangles':0,'windows':windows,'door_clearance':poses,
                'door_clearance_scope':'Conservative disjoint evaluated bounds at frames 0/25/30/40; not a continuous swept-volume proof.',
                'removed_superseded_lights':removed_lights,
                'confirmed':['continuous side service ducts','grouped underside openings','panel joints and light channels'],
                'estimates':['duct section and five-panel division','eighteen backed slots per panel and 12 mm blind depth'],
                'user_override':'2026-10-15: no curtains on this vehicle; overrides the selected interior video for this detail',
                'excluded_fittings':['curtains','curtain tracks','tie bands','hanging clips'],
                'unchanged':['seats and passenger points','door leaves and pivots','straps and handrails'],
                'coordinate_space':'Details authored in canonical +X-door frame under part14; source hierarchy restores physical handedness.'}
    finally:
        scene.frame_set(old_frame);bpy.context.view_layer.update()
