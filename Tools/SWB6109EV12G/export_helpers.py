# Authored Blender geometry is right-handed and left-hand drive. Temporary
# export meshes use the historical CDLC preparation frame; normalize only
# descendants of the source coordinate root, never an already flattened mesh.
import sys as _coordinates_sys
_coordinates_sys.path.insert(0,str(Path(__file__).resolve().parent))
from source_coordinates import canonical_world_matrix, SOURCE_REFLECTION
from lod_geometry import flatten_lod

def flatten_object(obj):
    deps=bpy.context.evaluated_depsgraph_get()
    evaluated=obj.evaluated_get(deps)
    data=bpy.data.meshes.new_from_object(evaluated,depsgraph=deps)
    if obj.type=='FONT':
        # The game is left-handed. Keep authored text readable when viewed
        # from its front after the Blender-to-game coordinate conversion.
        data.transform(Matrix.Diagonal((-1.,1.,1.,1.)))
        data.flip_normals()
        # Evaluated glyph caps and extrusion sides can arrive as separate
        # vertices with an inconsistent side winding. Join their exact edges
        # before recalculating the closed glyph's outward orientation.
        import bmesh
        glyph=bmesh.new();glyph.from_mesh(data)
        bmesh.ops.remove_doubles(glyph,verts=list(glyph.verts),dist=.0000001)
        bmesh.ops.recalc_face_normals(glyph,faces=list(glyph.faces))
        glyph.to_mesh(data);glyph.free();data.update()
    world=canonical_world_matrix(obj)
    if obj.type=='FONT' and obj.get('cdlc_font_source_preflipped'):
        # Remove the source-only glyph basis correction. Keep the established
        # glyph mesh/weld operation above, including its stable triangulation.
        world=world @ SOURCE_REFLECTION
    data.transform(world)
    if 'cdlc_vector_mirror_x' in obj and not obj.get('cdlc_vector_source_preflipped'):
        centre=float(obj['cdlc_vector_mirror_x'])
        rear=bool(obj.get('cdlc_vector_mirror_rear'))
        for v in data.vertices:
            x,z=v.co.x,v.co.z
            offset=v.co.y-end_point(x/width_at(z),z,rear)[1]
            v.co.x=2*centre-x
            v.co.y=end_point(v.co.x/width_at(z),z,rear)[1]+offset
        data.flip_normals();data.update()
    return data

# Single material atlas: every material has a safe padded tile, retaining all lamp IDs.
def write_atlas(name,mats):
    import numpy as np
    def srgb(value):
        return 12.92*value if value<=.0031308 else 1.055*value**(1/2.4)-.055
    # These are constant material swatches, not photographic body artwork.
    # Keep the established 16x16 UV grid with four pixels per padded tile.
    n=64; tile=4
    if len(mats)>256:raise RuntimeError('The 16x16 material palette is full')
    arrays={key:np.zeros((n,n,4),dtype=np.float32) for key in ['BaseColor','MaskMap','Normal','ControlMask','Emissive0','EmissiveID0']}
    arrays['Normal'][:]=(.5,.5,1,1)
    # These shaders do not share a G-channel contract. SG_DefaultShader uses
    # coat; BH/GlsShader uses AO and reads coat from a separate _CoatMask float.
    # No AO is baked for glass, so its entire atlas (including padding) is 1.
    is_glass=bool(mats) and all(m.get('glass') for m in mats)
    if any(m.get('glass') for m in mats) and not is_glass:
        raise RuntimeError('Body and glass require separate shader atlases')
    arrays['MaskMap'][:]=(0,1 if is_glass else 0,0,.3)
    arrays['BaseColor'][:]=(.01,.01,.01,1)
    arrays['ControlMask'][:,:,(3,)]=1
    arrays['Emissive0'][:,:,(3,)]=1
    arrays['EmissiveID0'][:,:,(3,)]=1
    for i,m in enumerate(mats):
        row,col=divmod(i,16)
        sl=(slice(row*tile,(row+1)*tile),slice(col*tile,(col+1)*tile))
        # An 8-bit sRGB image's pixels are stored as encoded values; image.save()
        # does not apply the scene-linear BSDF-to-sRGB transfer for this buffer.
        rgb=tuple(srgb(value) for value in m.diffuse_color[:3]); bs=next(node for node in m.node_tree.nodes if node.type=='BSDF_PRINCIPLED')
        arrays['BaseColor'][sl]=(*( (1.,1.,1.) if m.get('livery_control') else rgb ),.28 if m.get('glass') else 1.)
        arrays['MaskMap'][sl]=(bs.inputs['Metallic'].default_value,1 if is_glass else bs.inputs['Coat Weight'].default_value,0,1-bs.inputs['Roughness'].default_value)
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
    lod=2 if '_LOD2' in name else 1 if '_LOD1' in name else 0
    for obj in objects:
        if lod>int(obj.get('cdlc_last_lod',2)):continue
        if ratio<.2 and not obj.get('preserve_lod') and obj.type!='FONT' and any(word in obj.name for word in [' nut',' vent','tread groove',' seat foot','Hand strap','hand strap','anchor','Window latch','Pole floor','grab','badge emblem']):
            continue
        data=flatten_lod(obj,lod,flatten_object)
        temp=bpy.data.objects.new('__evaluate',data); scene.collection.objects.link(temp)
        if ratio<1 and obj.type!='FONT' and int(obj.get('cdlc_part',-1))!=0 and not obj.get('preserve_lod') and not obj.get('front_c2_roof_transition') and len(data.polygons)>8 and not any(m and m.get('light_id') for m in data.materials):
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
            # Match verify_source's actual degeneracy limit. The previous
            # 1e-12 cut removed valid sub-mm2 shell triangles, opening holes.
            if face.length_squared<1e-18:continue
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
    source_data=obj.data
    source_name,source_data_name=obj.name,source_data.name
    source_scale=scene.unit_settings.scale_length
    data=export=armdata=arm=None
    try:
        # The game binds the rig to the mesh with the exact asset name. Release
        # both Blender names before creating the copy, or it silently gets .001.
        # An unrelated object/datablock claiming the name must fail, not be renamed.
        obj.name=name+' (source)'; source_data.name=name+' (source)'
        # Source remains metres. Only the copy enters the centimetre pipeline.
        data=source_data.copy()
        data.name=name
        data.transform(Matrix.Diagonal((-100.,100.,100.,1.)))
        data.flip_normals()  # PipelinePlugin mirrors X again when creating Unity geometry.
        export=bpy.data.objects.new(name,data); scene.collection.objects.link(export)
        if export.name!=name or data.name!=name:
            raise RuntimeError(f'FBX object/mesh named {export.name!r}/{data.name!r}, not {name!r}')
        for vg in obj.vertex_groups:
            group=export.vertex_groups.new(name=vg.name)
            indices=[v.index for v in source_data.vertices if any(g.group==vg.index for g in v.groups)]
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
        result=bpy.ops.export_scene.fbx(filepath=str(OUT/(name+'.fbx')),use_selection=True,object_types={'MESH','ARMATURE'},add_leaf_bones=False,
            axis_forward='-Z',axis_up='Y',apply_scale_options='FBX_SCALE_ALL',apply_unit_scale=True,bake_space_transform=True,
            path_mode='STRIP',mesh_smooth_type='FACE',use_armature_deform_only=False,primary_bone_axis='Y',secondary_bone_axis='X',bake_anim=False)
        if 'FINISHED' not in result:
            raise RuntimeError(f'FBX export did not finish for {name!r}: {result!r}')
    finally:
        # Restore even after name collisions or an FBX operator exception.
        scene.unit_settings.scale_length=source_scale
        try:
            if arm is not None and arm.mode!='OBJECT':
                bpy.context.view_layer.objects.active=arm
                bpy.ops.object.mode_set(mode='OBJECT')
            if export is not None:bpy.data.objects.remove(export,do_unlink=True)
            if arm is not None:bpy.data.objects.remove(arm,do_unlink=True)
            if data is not None:bpy.data.meshes.remove(data)
            if armdata is not None:bpy.data.armatures.remove(armdata)
        finally:
            obj.name=source_name; source_data.name=source_data_name

