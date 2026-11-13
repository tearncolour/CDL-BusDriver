"""Emit the SWB1 rest-geometry resource consumed by VehicleMesh.cs (the vehicle pack's <model>.swb1.gz, make_pack.py)."""
def write_cdlc_runtime(objects, parts, flatten, root, spec):
    import bpy, gzip, io, json, struct, math
    from mathutils import Matrix,Vector,Quaternion
    palette=json.loads((root/'ModelSource/SWB6710EV01G/kit-cabin.json').read_text(encoding='utf-8'))['palette']
    indices={x['name']:x['index'] for x in palette}
    transforms=[]
    for p in parts:
        a=p['posed_part_to_blender']
        posed=Matrix([[a[c*4+r] for c in range(4)] for r in range(4)])
        q=p['rest_rotation_unity'];rot=Quaternion((q[3],q[0],q[1],q[2])).to_matrix().to_4x4()
        rest=Matrix.Translation(p['pivot_unity'])@rot
        # the door drives (parts 34.., VehicleMesh: frame bit 0x10000) store their geometry about their pivot, like the straps
        local=Matrix.Translation(-Vector(p['pivot_unity'])) if p.get('door_drive') or p.get('wrap_surface') else Matrix.Identity(4)
        transforms.append(local@rest@posed.inverted())
    drives=sum(1 for p in parts if p.get('door_drive'))
    surfaces=sum(1 for p in parts if p.get('wrap_surface'))
    if [p['index'] for p in parts if p.get('door_drive')]!=list(range(34,34+drives)) or \
       [p['index'] for p in parts if p.get('wrap_surface')]!=list(range(34+drives,34+drives+surfaces)):
        raise ValueError('door drive parts must be 34.. and the wrap surface parts right after them, without gaps')
    # Hand straps swing on their own (BusInterior.ApplyStraps): every stem with its loop becomes a part after the 34 contract
    # parts, pivot at the stem's top (the rail clamp), rest axes as the body's (X across the bus, Z along the rail). The
    # authored fare validator gives way to the mod's own card reader (drawn at the profile's CardReaders point).
    strap_groups={}
    for obj in objects:
        if obj.name.startswith('Hand strap stem') or obj.name.startswith('Passenger hand strap'):
            corners=[obj.matrix_world@Vector(c) for c in obj.bound_box]
            cx=sum(c.x for c in corners)/8;cy=sum(c.y for c in corners)/8
            key=(round(cx,2),round(cy,2))
            strap_groups.setdefault(key,[]).append((obj,max(c.z for c in corners)))
    strap_part={}
    parts=list(parts)
    for key in sorted(strap_groups,key=lambda k:(k[1],k[0])):
        group=strap_groups[key];top=max(z for _,z in group)
        pivot=[key[0],top,-key[1]]
        index=len(parts)
        parts.append({'index':index,'name':'Strap'+str(index-34-drives-surfaces),'parent':0,'pivot_unity':pivot,'rest_rotation_unity':[0,0,0,1]})
        transforms.append(Matrix.Translation(-Vector(pivot))@transforms[13])
        for obj,_ in group:strap_part[obj.name]=index
    skip_names=('Door fare validator','Fare validator blue screen')
    swatch_cache={}
    def uncached_swatch(mat,obj):
        if obj.get('cdlc_finish_override')=='Beacon':return indices['Amber']
        if obj.get('mirror_surface'):return indices['Chrome']
        if mat.get('livery_control'):return indices['White']
        light=int(mat.get('light_id',0))
        if light in (1,2,7,8):return indices['LampWhite']
        if light in (3,4):return indices['LampRed']
        if light in (5,6):return indices['Amber']
        if light==9:return indices['LedAmber']
        # stainless steel rails and poles: the palette's brushed metal (metallic, not a grey paint picked by colour)
        if 'stainless' in mat.name.lower() and 'BrushedStainless' in indices:return indices['BrushedStainless']
        if 'stop request' in mat.name.lower() and 'RequestRed' in indices:return indices['RequestRed']
        if 'strap handle' in mat.name.lower() and 'HandGripGrey' in indices:return indices['HandGripGrey']
        if 'Chrome' in mat.name or 'aluminium' in mat.name.lower():return indices['Chrome']
        if 'frit' in mat.name.lower():return indices['GlassTinted']
        rgb=mat.diffuse_color[:3]
        return min(palette,key=lambda x:sum((a-b)**2 for a,b in zip(rgb,x['rgb'])))['index']
    def swatch(mat,obj):
        key=(mat.as_pointer(),obj.get('cdlc_finish_override',''),bool(obj.get('mirror_surface')))
        if key not in swatch_cache:swatch_cache[key]=uncached_swatch(mat,obj)
        return swatch_cache[key]
    output=io.BytesIO();output.write(b'SWB1');output.write(struct.pack('<ii3f',3,len(parts),-.56,2.08,2.18))
    for p in parts:
        output.write(struct.pack('<ii7f',p['parent'],0x10000 if p.get('door_drive') else 0x20000 if p.get('wrap_surface') else 0,*p['pivot_unity'],*p['rest_rotation_unity']))
    stats=[]
    for lod,ratio in enumerate((1.,.32,.085)):
        records=[bytearray() for p in parts];counts=[0]*len(parts)
        for obj in objects:
            if any(m and m.get('glass') for m in obj.data.materials):continue
            if lod not in obj.get('cdlc_lods',(0,1,2)):continue   # paint_lines.py: a painted line's near or far version
            if obj.name.startswith(skip_names):continue
            name=obj.name.lower()
            if ('route' in name or 'destination' in name or 'led dots' in name) and any(m and int(m.get('light_id',0))==9 for m in obj.data.materials):continue
            if lod==2 and any(s in name for s in ['countersunk','mould line','air valve','torque','seat foot','latch','fastener','grab']):continue
            part=strap_part.get(obj.name,int(obj.get('cdlc_part',16)))
            trans=transforms[part];normal=trans.to_3x3().inverted().transposed()
            data=flatten(obj)
            temp=bpy.data.objects.new('__runtime_reduce',data);bpy.context.scene.collection.objects.link(temp)
            if ratio<1 and part!=0 and not obj.get('front_c2_roof_transition') and not obj.get('cdlc_keep_topology') and len(data.polygons)>12 and not any(m and int(m.get('light_id',0)) for m in data.materials):
                import bmesh
                bm=bmesh.new();bm.from_mesh(data)
                bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=0.000001)
                bm.normal_update();bm.to_mesh(data);bm.free()
                mod=temp.modifiers.new('Runtime LOD reduction','DECIMATE');mod.ratio=ratio
                reduced=flatten(temp);bpy.data.objects.remove(temp,do_unlink=True);bpy.data.meshes.remove(data);data=reduced
            else:bpy.data.objects.remove(temp,do_unlink=True)
            if obj.get('front_common_surface'):
                fs_apply_common_normals(data)
            data.calc_loop_triangles()
            for tri in data.loop_triangles:
                mat=data.materials[tri.material_index]
                sw=swatch(mat,obj);values=[]
                points=[trans@data.vertices[vi].co for vi in tri.vertices]
                face=(points[1]-points[0]).cross(points[2]-points[0])
                if face.length_squared<1e-12:continue
                face.normalize()
                for p,li in zip(points,tri.loops):
                    n=normal@data.corner_normals[li].vector
                    if n.length_squared<1e-12:n=face.copy()
                    else:n.normalize()
                    values.extend((*p,*n))
                if not all(math.isfinite(x) for x in values):raise ValueError('Nonfinite '+obj.name)
                records[part].extend(struct.pack('<B18f',sw,*values));counts[part]+=1
            bpy.data.meshes.remove(data)
        for count,data in zip(counts,records):output.write(struct.pack('<i',count));output.write(data)
        stats.append({'lod':lod,'triangles':sum(counts),'per_part':counts})
        print('CDLC runtime LOD',lod,sum(counts),flush=True)
    path=root/'ModelSource/SWB6710EV01G/sunwin-mesh.bin.gz'
    path.write_bytes(gzip.compress(output.getvalue(),compresslevel=6,mtime=0))
    report={'format':'SWB1','parts':len(parts),'door_drives':drives,'wrap_surfaces':surfaces,'straps':len(strap_part) and len(parts)-34-drives-surfaces,'bytes':path.stat().st_size,'lods':stats,'eye':[-.56,2.08,2.18]}
    (path.parent/'runtime-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print('CDLC_RUNTIME_OK '+str(path)+' '+str(path.stat().st_size))
    return report
