"""Join the existing wheelhouse shell to the actual sampled side-panel opening.

No extra cover plate, tyre change or exterior silhouette change. Coordinates
are the canonical construction frame; callers own saving the source.
"""
import bisect
import collections
import math
import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from source_coordinates import canonical_export_context, canonical_world_matrix

REVISION=2
JOIN_WIDTH=.10
THICKNESS=.012
OVERLAP=.014  # existing wheelhouse reaches this far into the 32 mm wall return
BLENDS=(0.,0.,.103515625,.5,.896484375,1.,1.)
TARGET_OBJECT_NAMES=tuple(name for ai in (0,1) for side in (-1,1)
                          for name in (f'Interior wheelhouse {ai} {side} closed arch',
                                       f'Interior wheelhouse {ai} {side} lower leg -1',
                                       f'Interior wheelhouse {ai} {side} lower leg 1'))


def _points(obj):
    matrix=canonical_world_matrix(obj)
    return [matrix@v.co for v in obj.data.vertices]


def _dimensions(master):
    result=[]
    for ai in (0,1):
        for side in (-1,1):
            obj=master.all_objects.get(f'Interior wheelhouse {ai} {side} closed arch')
            if obj is None:raise RuntimeError('Missing wheelhouse '+str((ai,side)))
            points=_points(obj)
            ay=float(obj.get('wheelhouse_axle_y',(min(p.y for p in points)+max(p.y for p in points))/2))
            cz=float(obj.get('wheelhouse_axle_z',min(p.z for p in points)))
            radius=float(obj.get('wheelhouse_original_inner_radius',min(math.hypot(p.y-ay,p.z-cz) for p in points)))
            xi=min(abs(p.x) for p in points)
            result.append((ai,side,obj,ay,cz,xi,radius))
    return result


def _wall_points(master,side):
    return [point for obj in master.all_objects if obj.name.startswith(f'Side lower structural panel {side} ')
            for point in _points(obj)]


def _contour(points,ay,cz):
    groups={}
    for p in points:
        radius=math.hypot(p.y-ay,p.z-cz)
        if p.z<cz-1e-5 or abs(radius-.585)>2e-5:continue
        key=(round(p.y,6),round(p.z,6))
        if key not in groups or abs(p.x)<abs(groups[key].x):groups[key]=p
    contour=sorted(groups.values(),key=lambda p:math.atan2(p.z-cz,p.y-ay))
    if len(contour)<20:raise RuntimeError('Insufficient actual side-panel arch samples')
    angles=[max(0.,min(math.pi,math.atan2(p.z-cz,p.y-ay))) for p in contour]
    if angles[0]>.0001 or angles[-1]<math.pi-.0001:raise RuntimeError('Incomplete side-panel arch endpoints')
    return contour,angles


def _rim(contour,angles,ay,cz,theta):
    i=max(0,min(len(contour)-2,bisect.bisect_right(angles,theta)-1))
    a,b=contour[i],contour[i+1]
    p=Vector((a.y-ay,a.z-cz));delta=Vector((b.y-a.y,b.z-a.z))
    direction=Vector((math.cos(theta),math.sin(theta)))
    cross=lambda v,w:v.x*w.y-v.y*w.x
    radial=cross(p,delta)/cross(direction,delta)
    target=direction*radial
    fraction=(target-p).dot(delta)/delta.length_squared
    return Vector((a.x+(b.x-a.x)*fraction,ay+target.x,cz+target.y))


def _xs(xi,join):
    return (xi,join-JOIN_WIDTH,join-.075,join-.05,join-.025,join,join+OVERLAP)


def _replace(obj,vertices,faces):
    old=obj.data;inverse=canonical_world_matrix(obj).inverted()
    # The old box bevel rounded its wall-contact/top faces as well as the visible
    # corners. On the new fitted joint that would reopen a millimetre-scale gap.
    for modifier in list(obj.modifiers):
        if modifier.type not in {'BEVEL','WEIGHTED_NORMAL'}:
            raise RuntimeError('Unexpected wheelhouse modifier '+modifier.name)
        obj.modifiers.remove(modifier)
    mesh=bpy.data.meshes.new(obj.name+' joined wall seam')
    mesh.from_pydata([inverse@Vector(v) for v in vertices],[],faces)
    for material in old.materials:mesh.materials.append(material)
    bm=bmesh.new();bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    if any(len(edge.link_faces)!=2 for edge in bm.edges):raise RuntimeError('Open rebuilt wheelhouse '+obj.name)
    if any(face.calc_area()<1e-12 for face in bm.faces):raise RuntimeError('Degenerate rebuilt wheelhouse '+obj.name)
    bm.to_mesh(mesh);bm.free();mesh.update()
    _surface_shading(mesh,apply=True)
    obj.data=mesh
    if old.users==0:bpy.data.meshes.remove(old)
    obj['wheelhouse_seam_revision']=REVISION
    obj['preserve_lod']=True
    obj['wheelhouse_seam_basis']='Existing shell shaped to actual side-panel inner arch; no additional cover'


def _surface_shading(mesh,apply=False):
    """Smooth the two continuous shell surfaces; keep actual thickness caps hard.

    _grid_shell stores the two surfaces in separate equal vertex ranges. This
    is a topology contract, not an angle heuristic that could smooth a cap or
    split a fine angular station. Geometry and its closed boundary stay intact.
    """
    n=len(mesh.vertices)//2
    if len(mesh.vertices)%2:raise RuntimeError('Wheelhouse shell has unequal layers')
    edge_faces={};smooth_count=0
    for polygon in mesh.polygons:
        surface=all(v<n for v in polygon.vertices) or all(v>=n for v in polygon.vertices)
        if apply:polygon.use_smooth=surface
        elif polygon.use_smooth!=surface:raise RuntimeError('Wheelhouse curved surface/cap shading mismatch: '+mesh.name)
        smooth_count+=surface
        for edge in polygon.edge_keys:edge_faces.setdefault(tuple(sorted(edge)),[]).append(surface)
    sharp_count=0
    for edge in mesh.edges:
        sides=edge_faces[tuple(sorted(edge.vertices))]
        if len(sides)!=2:raise RuntimeError('Open wheelhouse shading boundary: '+mesh.name)
        sharp=any(sides) and not all(sides)
        if apply:edge.use_edge_sharp=sharp
        elif edge.use_edge_sharp!=sharp:raise RuntimeError('Wheelhouse thickness cap lost its sharp boundary: '+mesh.name)
        sharp_count+=sharp
    if not smooth_count or not sharp_count:raise RuntimeError('Missing curved shell or hard thickness cap: '+mesh.name)
    if apply:mesh.update()
    return {'smooth_surface_faces':smooth_count,'flat_thickness_faces':len(mesh.polygons)-smooth_count,'sharp_cap_edges':sharp_count}


def _grid_shell(faces,count_rows,count_columns):
    # Both surfaces have identical grid topology; outer border joins close all edges.
    n=count_rows*count_columns
    for layer in (0,1):
        offset=layer*n
        for row in range(count_rows-1):
            for col in range(count_columns-1):
                a=offset+row*count_columns+col
                face=(a,a+1,a+1+count_columns,a+count_columns)
                faces.append(tuple(reversed(face)) if layer else face)
    border=list(range(count_columns))
    border += [r*count_columns+count_columns-1 for r in range(1,count_rows)]
    border += [(count_rows-1)*count_columns+c for c in range(count_columns-2,-1,-1)]
    border += [r*count_columns for r in range(count_rows-2,0,-1)]
    for a,b in zip(border,border[1:]+border[:1]):faces.append((a,b,b+n,a+n))


def _throat(points,y,low,high):
    groups={}
    for p in points:
        if abs(p.y-y)>2e-5 or p.z<low-1e-5 or p.z>high+1e-5:continue
        key=round(p.z,6)
        if key not in groups or abs(p.x)<groups[key][1]:groups[key]=(p.z,abs(p.x))
    rows=sorted(groups.values())
    if len(rows)<3:raise RuntimeError('Missing actual side-panel vertical throat')
    def at(z):
        if z<=rows[0][0]:return rows[0][1]
        if z>=rows[-1][0]:return rows[-1][1]
        index=bisect.bisect_right([r[0] for r in rows],z)-1
        a,b=rows[index:index+2];t=(z-a[0])/(b[0]-a[0])
        return a[1]+t*(b[1]-a[1])
    zs=sorted(set([low,high]+[row[0] for row in rows if low<row[0]<high]))
    return [(z,at(z)) for z in zs]


def apply_saved_wheelhouse_seams(master):
    if bpy.context.scene.frame_current!=0:raise RuntimeError('Fit wheelhouse seams at the closed/rest frame 0')
    changed=[];details=[]
    with canonical_export_context(master):
        for ai,side,arch,ay,cz,xi,inner_radius in _dimensions(master):
            wall=_wall_points(master,side);contour,angles=_contour(wall,ay,cz)
            # Include every original angular station and every actual body corner.
            raw_samples=sorted([math.pi*i/64 for i in range(65)]+angles)
            samples=[]
            for sample in raw_samples:
                if not samples or sample-samples[-1]>1e-5:samples.append(sample)
            samples[0]=0.;samples[-1]=math.pi
            vertices=[]
            for layer in (0,1):
                for theta in samples:
                    target=_rim(contour,angles,ay,cz,theta)
                    radial=math.hypot(target.y-ay,target.z-cz)
                    destination=radial-(THICKNESS if layer==0 else 0.)
                    initial=inner_radius+(0. if layer==0 else THICKNESS)
                    for x,blend in zip(_xs(xi,abs(target.x)),BLENDS):
                        r=initial+(destination-initial)*blend
                        vertices.append((side*x,ay+r*math.cos(theta),cz+r*math.sin(theta)))
            faces=[];_grid_shell(faces,len(samples),len(BLENDS));_replace(arch,vertices,faces)
            arch['wheelhouse_original_inner_radius']=inner_radius;arch['wheelhouse_axle_y']=ay;arch['wheelhouse_axle_z']=cz
            changed.append(arch.name)
            for end in (-1,1):
                leg=master.all_objects.get(f'Interior wheelhouse {ai} {side} lower leg {end}')
                if leg is None:raise RuntimeError('Missing existing wheelhouse lower leg')
                low=min(p.z for p in _points(leg))
                throat=_throat(wall,ay+end*.585,low,cz)
                vertices=[]
                for layer in (0,1):
                    initial=inner_radius+(0. if layer==0 else THICKNESS)
                    destination=.585-(THICKNESS if layer==0 else 0.)
                    for z,join in throat:
                        for x,blend in zip(_xs(xi,join),BLENDS):
                            r=initial+(destination-initial)*blend
                            vertices.append((side*x,ay+end*r,z))
                faces=[];_grid_shell(faces,len(throat),len(BLENDS));_replace(leg,vertices,faces)
                changed.append(leg.name)
            details.append({'arch':arch.name,'side':side,'axle_y':ay,'inner_radius_unchanged':inner_radius,
                            'old_outer_radius':inner_radius+THICKNESS,'wall_cut_radius':.585,
                            'join_width_m':JOIN_WIDTH,'side_panel_samples':len(contour),'new_angle_stations':len(samples)})
        bpy.context.view_layer.update()
        validation=verify_wheelhouse_seams(master)
    return {'status':'PASS','changed_objects':changed,'interfaces':details,'validation':validation,
            'exterior_and_tyres_changed':False,'new_cover_objects':0}


def verify_wheelhouse_seams(master):
    rows=[]
    for ai,side,arch,ay,cz,xi,inner_radius in _dimensions(master):
        if arch.get('wheelhouse_seam_revision')!=REVISION:raise RuntimeError('Wheelhouse is not joined: '+arch.name)
        members=[arch]+[master.all_objects.get(f'Interior wheelhouse {ai} {side} lower leg {e}') for e in (-1,1)]
        verts=[];faces=[];count=0;shading={}
        for obj in members:
            if obj.modifiers:raise RuntimeError('An unvalidated modifier changes the fitted wheelhouse: '+obj.name)
            edges=collections.Counter(tuple(sorted((p.vertices[i],p.vertices[(i+1)%len(p.vertices)]))) for p in obj.data.polygons for i in range(len(p.vertices)))
            if any(value!=2 for value in edges.values()):raise RuntimeError('Wheelhouse solid has open edges: '+obj.name)
            shading[obj.name]=_surface_shading(obj.data)
            obj.data.calc_loop_triangles();base=len(verts);verts.extend(_points(obj))
            for tri in obj.data.loop_triangles:
                a,b,c=(verts[base+i] for i in tri.vertices)
                if (b-a).cross(c-a).length_squared<1e-18:raise RuntimeError('Wheelhouse degenerate triangle: '+obj.name)
                faces.append(tuple(base+i for i in tri.vertices));count+=1
        tree=BVHTree.FromPolygons(verts,faces,all_triangles=True)
        contour,angles=_contour(_wall_points(master,side),ay,cz)
        errors=[];probes=[]
        for i in range(1,64):
            theta=math.pi*i/64;target=_rim(contour,angles,ay,cz,theta)
            nearest=tree.find_nearest(target)
            if nearest[0] is None:raise RuntimeError('No shell at wall interface')
            errors.append(nearest[3])
            radius=(inner_radius+THICKNESS+math.hypot(target.y-ay,target.z-cz))/2
            start=Vector((0.,ay+radius*math.cos(theta),cz+radius*math.sin(theta)))
            hit,normal,index,distance=tree.ray_cast(start,Vector((side,0,0)),abs(target.x)+OVERLAP+.01)
            if hit is None:raise RuntimeError('Former wheelhouse gap still open: '+str((ai,side,i)))
            probes.append({'theta':theta,'hit':list(hit),'wall_x':target.x})
        if max(errors)>2e-5:raise RuntimeError('Wheelhouse edge does not meet actual wall cut: '+str(max(errors)))
        throat_probes=[]
        for end,leg in zip((-1,1),members[1:]):
            low=min(v.z for v in _points(leg))
            throat=_throat(_wall_points(master,side),ay+end*.585,low,cz)
            for z,join in throat:
                target=Vector((side*join,ay+end*.585,z))
                nearest=tree.find_nearest(target)
                if nearest[0] is None or nearest[3]>2e-5:raise RuntimeError('Vertical throat joint misses side panel: '+leg.name)
                errors.append(nearest[3])
                if z<=low+1e-5 or z>=cz-1e-5:continue
                radius=(inner_radius+THICKNESS+.585)/2
                start=Vector((0.,ay+end*radius,z))
                hit,normal,index,distance=tree.ray_cast(start,Vector((side,0,0)),join+OVERLAP+.01)
                if hit is None:raise RuntimeError('Vertical throat retains a gap: '+leg.name)
                throat_probes.append({'point':list(start),'hit':list(hit)})
        rows.append({'arch':arch.name,'closed_solids':len(members),'triangles':count,'wall_connection_max_error_m':max(errors),'surface_shading':shading,
                     'former_gap_blocked_rays':len(probes),'probes':probes,'vertical_throat_blocked_rays':len(throat_probes),'throat_probes':throat_probes})
    return {'status':'PASS','interfaces':rows,'method':'Actual sampled wall-cut nearest distances and outward rays through the former radial gap; original wheel-facing radius retained.'}


verify_master=verify_wheelhouse_seams
