"""Photograph-calibrated front badges with editable, traced vector geometry.

Execute after exterior.py has defined end_point/width_at. No font substitutes
are used: all five ink/metal outlines come from front-badge-paths.json.
"""
import bmesh as _badge_bmesh
import json as _badge_json

_BADGE_PATHS=_badge_json.loads((SRC/'front-badge-paths.json').read_text(encoding='utf-8'))['groups']

def front_badge_shape(name, paths, centre, size, mat, stand_off=.008, half_thickness=.0008, triangulation=None, rear=False):
    """Closed vector extrusion subdivided and mapped onto the actual nose."""
    if triangulation is not None:
        meshdata=bpy.data.meshes.new(name+' traced vector tessellation')
        meshdata.from_pydata([(x*size[0],y*size[1],0) for x,y in triangulation['vertices_normalized']],[],triangulation['triangles'])
        meshdata.materials.append(mat)
    else:
        data=bpy.data.curves.new(name+' vector outlines','CURVE')
        data.dimensions='2D';data.resolution_u=1;data.fill_mode='BOTH'
        data.extrude=0;data.resolution_u=1
        for path in paths:
            spline=data.splines.new('POLY');spline.points.add(len(path)-1)
            for p,(x,y) in zip(spline.points,path):p.co=(x*size[0],y*size[1],0,1)
            spline.use_cyclic_u=True
        obj=bpy.data.objects.new(name,data);MASTER.objects.link(obj);data.materials.append(mat)
        bpy.context.view_layer.update();deps=bpy.context.evaluated_depsgraph_get()
        meshdata=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
        bpy.data.objects.remove(obj,do_unlink=True);bpy.data.curves.remove(data)
    obj=bpy.data.objects.new(name,meshdata);MASTER.objects.link(obj)
    bm=_badge_bmesh.new();bm.from_mesh(meshdata)
    _badge_bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.0000001)
    _badge_bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=.0000001)
    _badge_bmesh.ops.triangulate(bm,faces=list(bm.faces))
    # Coplanar letters still need tessellation before projection, otherwise
    # long diagonals would bridge across the real curved coachwork surface.
    projection_step=.040 if abs(centre[0])<.5 else .018
    for _ in range(7):
        edges=[e for e in bm.edges if e.calc_length()>projection_step]
        if not edges:break
        _badge_bmesh.ops.subdivide_edges(bm,edges=edges,cuts=1,use_grid_fill=True)
        _badge_bmesh.ops.triangulate(bm,faces=list(bm.faces))
    _badge_bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=.0000001)
    _badge_bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.0000001)
    _badge_bmesh.ops.triangulate(bm,faces=list(bm.faces))
    _badge_bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.verts.index_update();verts=[tuple(v.co) for v in bm.verts]
    faces=[tuple(v.index for v in f.verts) if f.normal.z>=0 else tuple(v.index for v in reversed(f.verts)) for f in bm.faces]
    bm.free();count=len(verts);edges={}
    for face in faces:
        for a,b in zip(face,face[1:]+face[:1]):
            key=tuple(sorted((a,b)))
            if key in edges:edges[key]=None
            else:edges[key]=(a,b)
    solid_faces=faces+[tuple(i+count for i in reversed(f)) for f in faces]
    for edge in edges.values():
        if edge:
            a,b=edge;solid_faces.append((b,a,a+count,b+count))
    projected=[]
    for depth in (half_thickness,-half_thickness):
        for x,y,_ in verts:
            xx=centre[0]+(-x if rear else x);zz=centre[1]+y
            projected.append(end_point(xx/width_at(zz),zz,rear,-stand_off-depth))
    meshdata.clear_geometry();meshdata.from_pydata(projected,[],solid_faces);meshdata.update()
    bm=_badge_bmesh.new();bm.from_mesh(meshdata)
    _badge_bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    assert all(e.is_manifold for e in bm.edges),name+' has an open or nonmanifold outline'
    bm.to_mesh(meshdata);bm.free();meshdata.update()
    obj['cdlc_part']=16;obj['cdlc_part_name']='Exterior';obj['bone']='Root'
    obj['preserve_lod']=True;obj['source']='Deterministic vector trace; see front-badge-paths.json'
    obj['surface_fitted']=True
    # The two Jiushi objects share the complete lockup's centre, preserving
    # triangle-before-wordmark order when reflected into the left-handed game.
    obj['cdlc_vector_mirror_x']=0. if 'Jiushi' in name else float(centre[0])
    if rear:obj['cdlc_vector_mirror_rear']=True
    return obj

def _badge_ellipse(rx=.5,ry=.5,segments=128):
    return [[rx*math.cos(2*math.pi*i/segments),ry*math.sin(2*math.pi*i/segments)] for i in range(segments)]

def _badge_roundrect(radius=.105,segments=18):
    pts=[]
    for cx,cy,start in [( .5-radius, .5-radius,0),(-.5+radius,.5-radius,90),
                        (-.5+radius,-.5+radius,180),(.5-radius,-.5+radius,270)]:
        for i in range(segments+1):
            a=math.radians(start+90*i/segments)
            pts.append([cx+radius*math.cos(a),cy+radius*math.sin(a)])
    return pts

_badge_blue=material('31 Accessibility decal deep blue',(.007,.033,.14),.38)
_badge_ink=material('32 Accessibility decal white ink',(.84,.86,.84),.43)
_badge_dark=material('33 Sunwin emblem dark enamel',(.014,.019,.020),.23)

# The circular field remains circular on the actual body. The traced ink's
# original pixel aspect is corrected for the front-corner photo projection.
for key,centre,ink_size in [
    ('elder',(1.110,.885),(.152,.159)),
    ('wheelchair',(.965,.845),(.159,.160))]:
    front_badge_shape('Front badge | '+key+' circular blue field',[_badge_ellipse()],centre,(.145,.145),_badge_blue,.0005,.00015)
    front_badge_shape('Front badge | '+key+' white pictogram',_BADGE_PATHS[key]['paths'],centre,ink_size,_badge_ink,.001,.00015,triangulation=_BADGE_PATHS[key])

# Triangular Jiushi symbol plus the manufacturer's custom four-character
# wordmark, traced separately so the photo's equal-height lockup is retained.
front_badge_shape('Front badge | Jiushi triangular emblem',_BADGE_PATHS['jiushi_triangle']['paths'],(-.160,1.094),(.070,.068),chrome,.0024,.0006,triangulation=_BADGE_PATHS['jiushi_triangle'])
front_badge_shape('Front badge | Jiushi proprietary wordmark',_BADGE_PATHS['jiushi_wordmark']['paths'],(.039,1.094),(.320,.072),chrome,.0024,.0006,triangulation=_BADGE_PATHS['jiushi_wordmark'])

# The physical vehicle has one clean chrome rounded frame and an independent
# S/paired-arrow insert. The insert is traced from the full-size vehicle photo,
# excluding the header logo's antialiasing, glints and concentric frame marks.
front_badge_shape('Front badge | Sunwin dark enamel plinth',[_badge_roundrect(.14)],(0,.750),(.180,.176),_badge_dark,.002,.0013)
_frame_outer=_badge_roundrect(.14)
_frame_inner=[(x*(.163/.176),y*(.159/.172)) for x,y in reversed(_badge_roundrect(.11))]
front_badge_shape('Front badge | Sunwin clean chrome rounded frame',[_frame_outer,_frame_inner],(0,.750),(.176,.172),chrome,.0038,.0006)
front_badge_shape('Front badge | Sunwin genuine S double arrow',_BADGE_PATHS['sunwin']['paths'],(0,.750),(.129,.150),chrome,.0038,.0006,triangulation=_BADGE_PATHS['sunwin'])

# Rear wordmark uses the same official artwork, maintaining its 281:29
# proportion. No generic SUNWIN font remains in this badge.
front_badge_shape('Rear badge | Sunwin proprietary wordmark',_BADGE_PATHS['sunwin_wordmark']['paths'],(0,1.790),(.540,.540*29/281),chrome,.0024,.0006,triangulation=_BADGE_PATHS['sunwin_wordmark'],rear=True)

(SRC/'front-badge-placement.json').write_text(_badge_json.dumps({
    'model':'SWB6109EV12G','units':'metres','status':'Photo-calibrated estimates, not OEM badge dimensions',
    'reference':'references/official-front-5585-original-3000.jpg',
    'vector_artwork':'front-badge-paths.json',
    'estimated_position_uncertainty_m':.025,'estimated_size_uncertainty_m':.012,
    'front_plane_basis':'Photo lower coachwork at Z=0.23 and roof centre at Z=3.16, crosschecked 2.55m body width; perspective limits remain.',
    'badges':{
      'elder':{'centre_xz':[1.110,.885],'disc_diameter':.145,'source_photo_crop_xywh':[731,1545,90,104]},
      'wheelchair':{'centre_xz':[.965,.845],'disc_diameter':.145,'source_photo_crop_xywh':[822,1570,98,105]},
      'jiushi_triangle':{'centre_xz':[-.160,1.094],'size_xz':[.070,.068]},
      'jiushi_wordmark':{'centre_xz':[.039,1.094],'size_xz':[.320,.072]},
      'sunwin':{'centre_xz':[0,.750],'size_xz':[.180,.176],'core_trace_size_xz':[.129,.150],'chrome_frame_outer_xz':[.176,.172],'chrome_frame_inner_xz':[.163,.159],'chrome_thickness_m':.0012},
      'rear_sunwin':{'centre_xz':[0,1.790],'size_xz':[.540,.540*29/281],'rear':True}},
    'construction':'Closed thin vector extrusions with real holes; projection mesh edges <=18mm at curved corners and <=40mm at the shallow centre; CDLC Exterior 16; no bitmap/logo text fallback.',
    'accessibility_adjustment':'Both circular fields raised 40–43mm after checking real lamp-frame clearance; the photograph shows the elder circle about 24–26px higher, so the 40mm relative height is preserved. Positions are perspective-limited estimates.',
    'official_artwork_url':'https://www.sunwinbus.com/UploadFile/image/20260804/20260804131306_5585.jpg',
    'jiushi_outline_url':'https://commons.wikimedia.org/wiki/File:久事公交集团logo.jpg',
    'jiushi_outline_note':'Third-party host credits Jiushi; triangle and four proprietary characters are independently matched to the manufacturer vehicle photo.'
},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
