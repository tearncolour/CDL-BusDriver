"""Photo-referenced K18 demonstrator colour areas and visible rooftop covers.

Execute in build_model globals, then call build_k18_exterior_completion() before
build_explicit_lods().  Does not replace the front nose, lights or badges.
Reference: official-c18-en-dt3.png, official-k18-detail-00.jpg and
official-k18-static-dt18_bgC18.jpg. Measurements remain photo estimates.
"""


def _k18ex_clip(poly, distance):
    if not poly:
        return []
    out = []
    for a,b in zip(poly, poly[1:]+poly[:1]):
        da,db = distance(a[0]),distance(b[0])
        ina,inb = da >= -1e-10,db >= -1e-10
        if ina:
            out.append(a)
        if ina != inb:
            t = da/(da-db)
            out.append((a[0].lerp(b[0],t),a[1].lerp(b[1],t).normalized()))
    cleaned = []
    for p,n in out:
        if not cleaned or (cleaned[-1][0]-p).length > 1e-9:
            cleaned.append((p,n))
    if len(cleaned)>1 and (cleaned[0][0]-cleaned[-1][0]).length < 1e-9:
        cleaned.pop()
    return cleaned


def _k18ex_region(poly, zy):
    area = sum(a[0]*b[1]-b[0]*a[1] for a,b in zip(zy,zy[1:]+zy[:1]))
    sign = 1 if area>0 else -1
    for a,b in zip(zy,zy[1:]+zy[:1]):
        poly = _k18ex_clip(poly,lambda p,a=a,b=b: sign*((b[0]-a[0])*(p.y-a[1])-(b[1]-a[1])*(p.z-a[0])))
    return poly


def _k18ex_surfaces(objects, side=None, roof=False):
    """Read actual evaluated exterior, including roof/eave edge rounding."""
    bpy.context.view_layer.update()
    deps = bpy.context.evaluated_depsgraph_get()
    triangles = []
    inverse_u = U.inverted()
    for ob in objects:
        evaluated = ob.evaluated_get(deps)
        data = evaluated.to_mesh()
        data.calc_loop_triangles()
        transform = inverse_u @ ob.matrix_world
        normal_transform = transform.to_3x3().inverted().transposed()
        for tri in data.loop_triangles:
            points = [transform @ data.vertices[i].co for i in tri.vertices]
            face_normal = (points[1]-points[0]).cross(points[2]-points[0]).normalized()
            # U reverses handedness; use evaluated vertex normals to select
            # outward faces instead of raw triangle winding after conversion.
            normals = [(normal_transform @ data.vertices[i].normal).normalized() for i in tri.vertices]
            average = sum(normals,Vector()).normalized()
            centre = sum(points,Vector())/3
            if side is not None:
                if any(abs(p.x-side*1.25)>1e-5 for p in points) or average.x*side<.5:
                    continue
                normals = [Vector((side,0,0)) for _ in points]
            if roof:
                if centre.y<3.025 or abs(average.z)>.65:
                    continue
                if not (average.y>.1 or average.x*centre.x>.1):
                    continue
            triangles.append(list(zip(points,normals)))
        evaluated.to_mesh_clear()
    return triangles


def _k18ex_patch(name, triangles, region, material, level, offset=.00065):
    points=[];normals=[];index={};faces=[];seen=set()
    for triangle in triangles:
        poly=_k18ex_region(triangle,region)
        if len(poly)<3:
            continue
        ids=[]
        for p,n in poly:
            # mathutils interpolation is float32: identical intersections
            # reached from adjacent triangles can differ below one micrometre.
            key=tuple(round(float(c),6) for c in p)
            if key not in index:
                index[key]=len(points);points.append(Vector(key));normals.append(n)
            ids.append(index[key])
        for j in range(1,len(ids)-1):
            face=(ids[0],ids[j],ids[j+1])
            if len(set(face))<3:
                continue
            a,b,c=(points[i] for i in face)
            if (b-a).cross(c-a).length<2e-12:
                continue
            key=tuple(sorted(face))
            if key not in seen:
                faces.append(face);seen.add(key)
    if not faces:
        return None
    # Remove points introduced by negligible clipped cells but not used.
    used=sorted(set(i for face in faces for i in face));remap={v:i for i,v in enumerate(used)}
    points=[points[i] for i in used];normals=[normals[i] for i in used]
    faces=[tuple(remap[i] for i in f) for f in faces]
    # Weld across quantisation-cell boundaries before constructing the rim.
    # This fixes coincident float32 clip intersections, not visible geometry.
    bm=bmesh.new();layer=bm.verts.layers.float_vector.new('paint_normal')
    bv=[bm.verts.new(p) for p in points]
    for v,normal in zip(bv,normals):v[layer]=normal
    for face in faces:bm.faces.new([bv[i] for i in face])
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000002)
    bmesh.ops.dissolve_degenerate(bm,edges=list(bm.edges),dist=.0000001)
    bm.verts.index_update()
    points=[v.co.copy() for v in bm.verts];normals=[Vector(v[layer]).normalized() for v in bm.verts]
    faces=[tuple(v.index for v in f.verts) for f in bm.faces]
    bm.free()
    edge_counts={}
    for face in faces:
        for a,b in zip(face,face[1:]+face[:1]):
            edge_counts[tuple(sorted((a,b)))]=edge_counts.get(tuple(sorted((a,b))),0)+1
    invalid=[(tuple(points[a]),tuple(points[b]),count) for (a,b),count in edge_counts.items() if count>2]
    if invalid:
        raise ValueError('Nonmanifold source colour patch '+name+' LOD'+str(level)+' '+str(invalid[:3]))
    n=len(points)
    verts=[tuple(p+normal*d) for d in (.00005,offset) for p,normal in zip(points,normals)]
    closed=[tuple(reversed(f)) for f in faces]+[tuple(i+n for i in f) for f in faces]
    closed += [(a,b,b+n,a+n) for (a,b),count in edge_counts.items() if count==1]
    ob=mesh(name+' LOD'+str(level),verts,closed,material,18,lod=level)
    ob['first_lod']=level;ob['last_lod']=level
    ob['k18_exterior_completion']='surface_colour_region'
    ob['reference_basis']='Official demonstrator side/front-quarter photographs; estimated landmarks'
    ob['paint_thickness_m']=offset-.00005
    ob['livery_control_preserved']=bool(material.get('livery_control',False))
    if level:
        ob.hide_render=True;ob.hide_set(True)
    return ob


def _k18ex_roof_cover(name,centre_z,level):
    """Low yellow rounded enclosure visible in the side and both hero photos.

    No invented fan count, branding or hidden functional layout is modelled.
    The whole broad bottom meets the original roof's y=3.195 exterior.
    """
    steps=(8,5,3)[level]
    verts=[]
    sections=((1.60,2.65,3.194,.12),(1.60,2.65,3.211,.12),
              (1.53,2.53,3.281,.14),(1.40,2.37,3.310,.17))
    for width,length,y,radius in sections:
        for cx,cz,start in ((width/2-radius,length/2-radius,0),
                            (-width/2+radius,length/2-radius,90),
                            (-width/2+radius,-length/2+radius,180),
                            (width/2-radius,-length/2+radius,270)):
            for j in range(steps):
                angle=math.radians(start+j*90/steps)
                verts.append((cx+radius*math.cos(angle),y,centre_z+cz+radius*math.sin(angle)))
    n=steps*4;faces=[]
    # Centre fans avoid concave/global n-gon triangulation for the two caps.
    bottom=len(verts);verts.append((0,sections[0][2],centre_z))
    top=len(verts);verts.append((0,sections[-1][2],centre_z))
    for i in range(n):
        j=(i+1)%n;faces.append((bottom,j,i));faces.append((top,3*n+i,3*n+j))
        for ring in range(3):faces.append((ring*n+i,ring*n+j,(ring+1)*n+j,(ring+1)*n+i))
    ob=mesh(name+' LOD'+str(level),verts,faces,YELLOW,16,lod=level)
    ob['first_lod']=level;ob['last_lod']=level
    ob['k18_exterior_completion']='photo_estimated_roof_cover'
    ob['reference_basis']='Side profile yellow low enclosure; hidden equipment not inferred'
    for poly in ob.data.polygons:poly.use_smooth=len(poly.vertices)==4
    if level:ob.hide_render=True;ob.hide_set(True)
    return ob


def build_k18_exterior_completion():
    """Replace only identified generic graphics/HVAC; preserve shell/control."""
    for ob in list(bpy.data.objects):
        if ob.type!='MESH' or ob.get('cab_section') not in ('front','rear'):
            continue
        if ob.get('k18_exterior_completion') or ob.name.startswith((
                'Official white wheel-cleared graphic','Roof coloured graphic',
                'Roof HVAC enclosure','HVAC fin')):
            bpy.data.objects.remove(ob,do_unlink=True)
    results=[]
    white_triangle=[(2.80,1.14),(5.77,1.14),(5.77,.35)]
    black_diagonal=[(2.80,1.14),(5.77,.35),(5.77,.27),(2.80,1.06)]
    rectangle=lambda a,b,lo=.22,hi=1.14:[(a,lo),(b,lo),(b,hi),(a,hi)]
    for section,collection in (('front',FRONT),('rear',REAR)):
        select_section(section)
        for side in (-1,1):
            for level in (0,1,2):
                sources=[ob for ob in list(collection.objects) if ob.type=='MESH' and
                         ((level==0 and ob.name.startswith(section+' side '+str(side)+' lower skin')) or
                          (level>0 and ob.name.startswith('LOD'+str(level)+' lower side '+str(side)+' ')))]
                if not sources:
                    raise ValueError('K18 lower-side source unavailable for '+str((section,side,level)))
                surfaces=_k18ex_surfaces(sources,side=side)
                regions=([('main white wedge',white_triangle,WHITE),('diagonal dark separation',black_diagonal,BLACK),
                          ('joint forward white panel',rectangle(FRONT_JOIN,-.075),WHITE)] if section=='front' else
                         [('rear wheel yellow field',rectangle(-7.20,DOORS[2]-.65),YELLOW),
                          ('joint rear white panel',rectangle(DOORS[2]+.65,REAR_JOIN),WHITE)])
                for label,region,material in regions:
                    ob=_k18ex_patch('K18 '+section+' side '+str(side)+' '+label,surfaces,region,material,level)
                    if ob:results.append(ob)
        roof_sources=[ob for ob in list(collection.objects) if ob.type=='MESH' and
                      (ob.name==section+' roof' or ob.name.startswith('Eave structural rail'))]
        surfaces=_k18ex_surfaces(roof_sources,roof=True)
        roof_rectangle=lambda a,b:[(a,3.0),(b,3.0),(b,3.5),(a,3.5)]
        regions=([('front yellow roof band',roof_rectangle(5.98,7.35),YELLOW),
                  ('front white roof band',roof_rectangle(1.50,5.98),WHITE),
                  ('forward joint white roof band',roof_rectangle(FRONT_JOIN,.05),WHITE)] if section=='front' else
                 [('rear white roof wedge',[(-7.20,3.0),(-5.80,3.0),(-7.20,3.22)],WHITE),
                  ('rear blue roof band',[(-7.20,3.22),(-5.80,3.0),(REAR_JOIN,3.0),(REAR_JOIN,3.5),(-7.20,3.5)],BLUE)])
        for level in (0,1,2):
            for label,region,material in regions:
                ob=_k18ex_patch('K18 '+label,surfaces,region,material,level)
                if ob:results.append(ob)
            results.append(_k18ex_roof_cover('K18 '+section+' visible yellow roof enclosure',1.30 if section=='front' else -3.65,level))
    return results
