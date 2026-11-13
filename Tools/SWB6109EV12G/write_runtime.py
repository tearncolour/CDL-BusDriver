"""Export the fixed 34-part contract and independent straps to SWB1.

No exterior geometry is borrowed from another vehicle. ``flatten`` returns a
temporary mesh in the canonical CDLC preparation frame; this writer owns and
removes that mesh. Actual saved Blender world coordinates are normalized by
the caller using source_coordinates.canonical_world_matrix.
"""
def write_cdlc_runtime(objects, parts, flatten, root, spec, normal_callback=None):
    import bpy, gzip, io, json, struct, math
    from mathutils import Matrix,Vector,Quaternion
    from lod_geometry import flatten_lod
    from material_contract import SOURCE_PRESERVING_SWATCHES
    source=root/'ModelSource/SWB6109EV12G'
    palette=json.loads((source/'kit-cabin.json').read_text(encoding='utf-8'))['palette']
    if not 34<=len(parts)<=256 or [p['index'] for p in parts]!=list(range(len(parts))):
        raise ValueError('SWB1 requires ordered CDLC 0..33 followed by contiguous straps, at most 256 parts')
    eye=tuple(float(x) for x in spec['driver_eye_unity'])
    if len(eye)!=3 or not all(math.isfinite(x) for x in eye):
        raise ValueError('driver_eye_unity must contain three finite metre coordinates')
    ratios=spec.get('runtime_lod_ratios',[1.,.32,.085])
    if len(ratios)!=3 or ratios[0]!=1 or not all(0<float(r)<=1 for r in ratios):
        raise ValueError('runtime_lod_ratios must contain three values, starting with 1')
    indices={x['name']:x['index'] for x in palette}
    transforms=[]
    for p in parts:
        a=p['posed_part_to_blender']
        posed=Matrix([[a[c*4+r] for c in range(4)] for r in range(4)])
        q=p['rest_rotation_unity'];rot=Quaternion((q[3],q[0],q[1],q[2])).to_matrix().to_4x4()
        rest=Matrix.Translation(p['pivot_unity'])@rot
        if p['index']>=34:
            if p['name']!='Strap'+str(p['index']-34) or p['parent']!=0 or max(abs(v) for v in q[:3])>1e-6 or abs(abs(q[3])-1)>1e-6:
                raise ValueError('Straps require Shell parent and body-aligned Unity rest rotation')
            # The poser places a strap at its header pivot. Its mesh must be
            # local to that attachment, unlike the legacy fixed-part meshes.
            transforms.append(posed.inverted())
        else:
            transforms.append(rest@posed.inverted())
    normal_transforms=[t.to_3x3().inverted().transposed() for t in transforms]
    swatch_cache={}
    fallback_assignments={}
    def linear(value):
        return value/12.92 if value<=.04045 else ((value+.055)/1.055)**2.4
    # KitExport stores palette RGB in sRGB; Blender BSDF/diffuse colors are linear.
    palette_linear={row['index']:tuple(linear(value) for value in row['rgb']) for row in palette}
    explicit_only_swatches={row['swatch'] for row in SOURCE_PRESERVING_SWATCHES.values()}
    fallback_palette=[row for row in palette if row['name'] not in explicit_only_swatches and row['finish'] not in
                      {'Lamp','Tail','Beacon','Aviation','NavGreen','Glow','Hazard','Glass'}]
    def uncached_swatch(mat,obj):
        native_route=SOURCE_PRESERVING_SWATCHES.get(mat.name)
        if native_route and (mat.get('cdlc_palette_swatch')!=native_route['swatch'] or
                mat.get('livery_control') or mat.get('light_id') or mat.get('glass') or
                obj.get('cdlc_finish_override') or obj.get('mirror_surface')):
            raise ValueError('Authored fixed-colour stripe requires explicit '+native_route['swatch']+
                             ' routing without livery/light/mirror overrides: '+mat.name)
        if obj.get('cdlc_finish_override')=='Beacon':return indices['Amber']
        if obj.get('mirror_surface'):return indices['Chrome']
        if mat.get('livery_control'):return indices['White']
        light=int(mat.get('light_id',0))
        if light in (1,2,7,8):return indices['LampWhite']
        if light in (3,4):return indices['LampRed']
        if light in (5,6):return indices['Amber']
        if light==9:return indices['LedAmber']
        explicit=mat.get('cdlc_palette_swatch')
        if explicit:
            if explicit not in indices:raise ValueError('Unknown explicit runtime finish on '+mat.name+': '+str(explicit))
            return indices[explicit]
        if 'Chrome' in mat.name or 'aluminium' in mat.name.lower():return indices['Chrome']
        if 'frit' in mat.name.lower():return indices['GlassTinted']
        rgb=tuple(mat.diffuse_color[:3])
        shader=next((n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED'),None) if mat.use_nodes and mat.node_tree else None
        metallic=float(shader.inputs['Metallic'].default_value) if shader else float(mat.metallic)
        roughness=float(shader.inputs['Roughness'].default_value) if shader else float(mat.roughness)
        candidates=[row for row in fallback_palette if (float(row['metallic'])>=.5)==(metallic>=.5)]
        if not candidates:raise ValueError('No compatible non-emissive fallback for '+mat.name)
        def distance(row):
            return sum((a-b)**2 for a,b in zip(rgb,palette_linear[row['index']]))+\
                   .1*(metallic-float(row['metallic']))**2+.1*(roughness-float(row['roughness']))**2
        selected=min(candidates,key=distance)
        fallback_assignments[mat.name]={'material':mat.name,'source_linear_rgb':list(rgb),
            'source_metallic':metallic,'source_roughness':roughness,
            'swatch':selected['name'],'finish':selected['finish'],'index':selected['index'],
            'assigned_linear_rgb':list(palette_linear[selected['index']]),
            'assigned_metallic':selected['metallic'],'assigned_roughness':selected['roughness'],
            'fit_error':distance(selected),'status':'nearest available opaque non-emissive palette approximation'}
        return selected['index']
    def swatch(mat,obj):
        key=(mat.as_pointer(),obj.get('cdlc_finish_override',''),bool(obj.get('mirror_surface')))
        if key not in swatch_cache:swatch_cache[key]=uncached_swatch(mat,obj)
        return swatch_cache[key]
    output=io.BytesIO();output.write(b'SWB1');output.write(struct.pack('<ii3f',3,len(parts),*eye))
    for p in parts:
        output.write(struct.pack('<ii7f',p['parent'],0,*p['pivot_unity'],*p['rest_rotation_unity']))
    stats=[]
    for lod,ratio in enumerate(ratios):
        records=[bytearray() for p in parts];counts=[0]*len(parts)
        for obj in objects:
            # BusInterior adds the shared, interactive reader at the model's
            # CardReaders frames. Its source/FBX visualization must not create
            # a second overlapping housing in the SWB1 runtime mesh.
            if obj.get('cdlc_runtime_generated')=='CardReader':continue
            if lod>int(obj.get('cdlc_last_lod',2)):continue
            if any(m and m.get('glass') for m in obj.data.materials):continue
            name=obj.name.lower()
            if spec.get('runtime_dynamic_signs',True) and ('route' in name or 'destination' in name or 'led dots' in name) and any(m and int(m.get('light_id',0))==9 for m in obj.data.materials):continue
            if lod==2 and not obj.get('preserve_lod') and obj.type!='FONT' and any(s in name for s in ['countersunk','mould line','air valve','torque','seat foot','latch','fastener','grab']):continue
            part=int(obj.get('cdlc_part',16))
            if not 0<=part<len(parts):raise ValueError('Invalid CDLC part on '+obj.name)
            if obj.name.startswith(('Hand strap stem','Passenger hand strap')) and part<34:
                raise ValueError('prepare_strap_rig must separate '+obj.name+' before export')
            material_parts=json.loads(obj.get('cdlc_part_by_material','{}')) if part<34 else {}
            light_parts=json.loads(obj.get('cdlc_light_part_by_material','{}')) if part<34 and part not in (1,2,3,4,5,6,7,8,9,19,20,21,22) else {}
            data=flatten_lod(obj,lod,flatten)
            temp=bpy.data.objects.new('__runtime_reduce',data);bpy.context.scene.collection.objects.link(temp)
            if ratio<1 and obj.type!='FONT' and part!=0 and not obj.get('preserve_lod') and not obj.get('front_c2_roof_transition') and len(data.polygons)>12 and not any(m and int(m.get('light_id',0)) for m in data.materials):
                import bmesh
                bm=bmesh.new();bm.from_mesh(data)
                bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=0.000001)
                bm.normal_update();bm.to_mesh(data);bm.free()
                mod=temp.modifiers.new('Runtime LOD reduction','DECIMATE');mod.ratio=ratio
                reduced=flatten(temp);bpy.data.objects.remove(temp,do_unlink=True);bpy.data.meshes.remove(data);data=reduced
            else:bpy.data.objects.remove(temp,do_unlink=True)
            if obj.get('front_common_surface'):
                if normal_callback is None:
                    raise ValueError('front_common_surface requires the new model normal_callback')
                normal_callback(data)
            data.calc_loop_triangles()
            for tri in data.loop_triangles:
                mat=data.materials[tri.material_index]
                # Explicit surface assignment keeps a closed source solid while
                # routing its painted outside to Livery and its inner skin to
                # Shell. Static lamp circuits override this generic mapping.
                key=str(tri.material_index)
                triangle_part=int(light_parts.get(key,material_parts.get(key,part)))
                if not 0<=triangle_part<len(parts):raise ValueError('Invalid per-material CDLC part on '+obj.name)
                trans=transforms[triangle_part]
                normal=normal_transforms[triangle_part]
                sw=swatch(mat,obj);values=[]
                points=[trans@data.vertices[vi].co for vi in tri.vertices]
                face=(points[1]-points[0]).cross(points[2]-points[0])
                # Keep valid small badge/shell faces; same limit as source QA.
                if face.length_squared<1e-18:continue
                face.normalize()
                for p,li in zip(points,tri.loops):
                    n=normal@data.corner_normals[li].vector
                    if n.length_squared<1e-12:n=face.copy()
                    else:n.normalize()
                    values.extend((*p,*n))
                if not all(math.isfinite(x) for x in values):raise ValueError('Nonfinite '+obj.name)
                records[triangle_part].extend(struct.pack('<B18f',sw,*values));counts[triangle_part]+=1
            bpy.data.meshes.remove(data)
        for count,data in zip(counts,records):output.write(struct.pack('<i',count));output.write(data)
        stats.append({'lod':lod,'triangles':sum(counts),'per_part':counts})
        print('CDLC runtime LOD',lod,sum(counts),flush=True)
    path=source/spec.get('runtime_mesh_file','sunwin-mesh.bin.gz')
    if path.parent.resolve()!=source.resolve():
        raise ValueError('runtime_mesh_file must stay in this vehicle source directory')
    source.mkdir(parents=True,exist_ok=True)
    path.write_bytes(gzip.compress(output.getvalue(),compresslevel=6,mtime=0))
    finishes=[{'object':obj.name,'material':mat.name,'swatch':mat['cdlc_palette_swatch'],
               'index':swatch(mat,obj)} for obj in objects for mat in obj.data.materials
              if mat and mat.get('cdlc_palette_swatch')]
    report={'model':'SWB6109EV12G','format':'SWB1','parts':len(parts),'straps':len(parts)-34,'bytes':path.stat().st_size,'lods':stats,'eye':list(eye),
            'explicit_finishes':finishes,'fallback_materials':list(fallback_assignments.values()),
            'explicit_only_swatches':sorted(explicit_only_swatches),
            'native_preserved_runtime_routes':[{'material':name,'swatch':route['swatch'],
                'native_linear_rgb':list(route['linear_rgb']),'native_metallic':route['metallic'],
                'native_roughness':route['roughness'],
                'runtime_finish':next(row['finish'] for row in palette if row['name']==route['swatch']),
                'runtime_metallic':next(row['metallic'] for row in palette if row['name']==route['swatch']),
                'runtime_roughness':next(row['roughness'] for row in palette if row['name']==route['swatch'])}
                for name,route in SOURCE_PRESERVING_SWATCHES.items() if any(mat and mat.name==name for obj in objects for mat in obj.data.materials)],
            'fallback_policy':'Compare linear RGB plus 0.1 times squared metallic/roughness errors within the same >=0.5 metallic class; exclude emissive, glass, patterned finishes and explicit-only authored colours. Untagged materials remain palette approximations.'}
    (path.parent/'runtime-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print('CDLC_RUNTIME_OK '+str(path)+' '+str(path.stat().st_size))
    return report
