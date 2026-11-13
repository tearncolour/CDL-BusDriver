"""Photo-estimated yellow/grey seat print; memory-only, LOD0, closed ribbons.

Six hand-traced families and a short broken line suggest the supplied textile.
The exact textile repeat and colours are not manufacturer measurements. Each
edge is ray-projected onto the evaluated seat mesh, with a sub-millimetre paint
separation and adaptive checks between samples. No seat mesh is changed.
"""
import math
import bpy
import bmesh
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform
from seat_geometry_photo import seat_surface
from source_coordinates import canonical_export_context, _add_source_metadata

MARKER = 'seat_photo_print_revision'
REVISION = 1
PREFIX = '6109 seat print | '
BASE_OFFSET = .000080
PAINT_THICKNESS = .000065
# Bottom-of-paint clearance at ray samples, including inter-sample chord tests.
MIN_CLEARANCE = .000008
MAX_CLEARANCE = .000180

# u coordinates, from bottom to top; v runs .09 .. .91 in eleven intervals.
# The asymmetrical changes are estimated from the visible seat, not noise or
# a claim to have recovered the manufacturer's full textile repeat.
LINES = (
    ('Yellow', .0042, (-.68,-.67,-.64,-.60,-.56,-.51,-.43,-.31,-.20,-.11,-.08,-.06)),
    ('Grey',   .0031, (-.53,-.46,-.42,-.37,-.35,-.28,-.22,-.14,-.05,.07,.21,.28)),
    ('Yellow', .0044, (-.25,-.22,-.15,-.12,-.06,.03,.08,.11,.18,.31,.37,.40)),
    ('Grey',   .0032, (.03,.11,.15,.21,.23,.28,.30,.36,.46,.57,.62,.65)),
    ('Yellow', .0040, (.45,.42,.40,.39,.40,.45,.49,.53,.60,.69,.75,.81)),
    ('Grey',   .0028, (-.78,-.77,-.76,-.74,-.72,-.70,-.68,-.65,-.60,-.55)),
)


def _material(swatch):
    name = PREFIX + swatch
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    color = (.55,.72,.008) if swatch == 'Yellow' else (.32,.37,.36)
    material.diffuse_color = (*color,1.)
    material.use_nodes = True
    shader = next(n for n in material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    shader.inputs['Base Color'].default_value = (*color,1.)
    shader.inputs['Metallic'].default_value = 0.
    shader.inputs['Roughness'].default_value = .53
    shader.inputs['Emission Strength'].default_value = 0.
    material['cdlc_palette_swatch'] = swatch
    material['light_id'] = 0
    material['livery_control'] = False
    material['glass'] = False
    return material


class _Projector:
    def __init__(self, obj, rear_shell, deps):
        self.rear_shell = rear_shell
        self.sign = 1 if rear_shell else -1
        evaluated = obj.evaluated_get(deps)
        mesh = evaluated.to_mesh()
        mesh.calc_loop_triangles()
        self.vertices = [v.co.copy() for v in mesh.vertices]
        self.normals = [v.normal.copy() for v in mesh.vertices]
        self.faces = [tuple(t.vertices) for t in mesh.loop_triangles]
        self.bvh = BVHTree.FromPolygons(self.vertices,self.faces,all_triangles=True)
        evaluated.to_mesh_clear()
        self.cache = {}
        self.rays = 0
    def cast(self, x, z):
        key = (float(x),float(z))
        if key in self.cache: return self.cache[key]
        self.rays += 1
        hit, normal, index, distance = self.bvh.ray_cast(Vector((x,self.sign*.25,z)),Vector((0,-self.sign,0)),.5)
        if hit is None or normal.y*self.sign < .75:
            raise RuntimeError('Seat print ray missed the intended broad seat surface')
        ids = self.faces[index]
        smooth = barycentric_transform(hit,*(self.vertices[i] for i in ids),*(self.normals[i] for i in ids)).normalized()
        result = (hit,smooth)
        self.cache[key] = result
        return result
    def point(self,u,v):
        p = seat_surface(u,v,rear_shell=self.rear_shell,side='rear' if self.rear_shell else 'front')
        return Vector((p.x,p.z))


def _ribbon(projector, samples, width, material_index, vertices, faces, indices, shading):
    """Return a closed strip and measured paint clearance; never a round tube."""
    samples = [(float(u),float(v),float(w)) for u,v,w in samples]
    max_refinements = 5
    for iteration in range(max_refinements+1):
        centres = [projector.point(u,v) for u,v,w in samples]
        rows=[]
        for i,(u,v,weight) in enumerate(samples):
            tangent = centres[min(i+1,len(centres)-1)]-centres[max(i-1,0)]
            tangent.normalize()
            across = Vector((-tangent.y,tangent.x)) * width*weight*.5
            row=[]
            for point in (centres[i]+across,centres[i]-across):
                hit,normal=projector.cast(point.x,point.y)
                row.append((hit,normal))
            rows.append(row)
        refine=[]; clearances=[]
        for i in range(len(rows)-1):
            corners=[rows[i][0][0],rows[i][1][0],rows[i+1][1][0],rows[i+1][0][0]]
            tests=[sum(corners,Vector())/4]
            # Check either possible exporter diagonal, plus long-edge midpoints.
            for ids in ((0,1,2),(0,2,3),(0,1,3),(1,2,3)):
                tests.append(sum((corners[j] for j in ids),Vector())/3)
            tests.extend(((corners[0]+corners[3])/2,(corners[1]+corners[2])/2))
            gaps=[]
            for test in tests:
                actual,_=projector.cast(test.x,test.z)
                gaps.append((test.y-actual.y)*projector.sign+BASE_OFFSET)
            clearances.extend(gaps)
            if min(gaps)<MIN_CLEARANCE or max(gaps)>MAX_CLEARANCE: refine.append(i)
        if not refine: break
        if iteration==max_refinements:
            raise RuntimeError(f'Seat print did not converge: rear={projector.rear_shell}, min={min(clearances)}, max={max(clearances)}, samples={[(samples[i], samples[i+1]) for i in refine]}')
        updated=[]
        for i,sample in enumerate(samples):
            updated.append(sample)
            if i in refine:
                updated.append(tuple((a+b)/2 for a,b in zip(sample,samples[i+1])))
        samples=updated
    first=len(vertices)
    for row in rows:
        # Four points at each cross-section: bottom L/R, visible top L/R.
        for top in (False,True):
            offset=BASE_OFFSET+(PAINT_THICKNESS if top else 0.)
            for point,normal in row:
                p=point+Vector((0,projector.sign*offset,0))
                vertices.append(tuple(p));shading.append(tuple(normal))
    def face(ids):
        faces.append(tuple(first+i for i in ids));indices.append(material_index)
    for i in range(len(rows)-1):
        a=i*4;b=a+4
        face((a,a+1,b+1,b));face((a+2,b+2,b+3,a+3))
        face((a,b,b+2,a+2));face((a+1,a+3,b+3,b+1))
    face((0,2,3,1))
    a=(len(rows)-1)*4;face((a,a+1,a+3,a+2))
    return {'segments':len(rows)-1,'minimum_bottom_clearance_m':min(clearances),'maximum_bottom_clearance_m':max(clearances)}


def _print_mesh(obj,rear_shell,materials,deps):
    projector=_Projector(obj,rear_shell,deps)
    vertices=[];faces=[];indices=[];shading=[];reports=[]
    for index,(swatch,width,us) in enumerate(LINES):
        samples=[(u,.09+.82*i/(len(us)-1),.96+.10*math.sin(i*.73+index)) for i,u in enumerate(us)]
        reports.append(_ribbon(projector,samples,width,0 if swatch=='Yellow' else 1,vertices,faces,indices,shading))
    # Four small isolated strokes read as a broken yellow textile line.
    for index in range(4):
        v=.18+index*.195
        u=.14+.43*(v-.09)/.82
        reports.append(_ribbon(projector,[(u,v,1.),(u+.006,v+.016,1.)],.0027,0,vertices,faces,indices,shading))
    mesh=bpy.data.meshes.new(PREFIX+('rear shell' if rear_shell else 'front upholstery'))
    mesh.from_pydata(vertices,[],faces)
    for material in materials:mesh.materials.append(material)
    for face,index in zip(mesh.polygons,indices):face.material_index=index
    mesh.update()
    bm=bmesh.new();bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    bad=sum(not e.is_manifold for e in bm.edges)
    tiny=sum(f.calc_area()<=1.e-13 for f in bm.faces)
    if bad or tiny or bm.calc_volume(signed=True)<=0:
        bm.free();bpy.data.meshes.remove(mesh)
        raise RuntimeError(f'Seat print not a valid closed paint mesh: edges={bad}, tiny={tiny}')
    bm.to_mesh(mesh);bm.free();mesh.update()
    normals=[]
    for face in mesh.polygons:
        top=all(i%4 in (2,3) for i in face.vertices)
        bottom=all(i%4 in (0,1) for i in face.vertices)
        face.use_smooth=top
        for i in face.vertices:
            normals.append(shading[i] if top else tuple(face.normal))
    mesh.normals_split_custom_set(normals)
    return mesh,{'surface':'rear' if rear_shell else 'front','triangles':len(mesh.polygons),
                 'ray_count':projector.rays,'ribbons':reports,'nonmanifold_edges':bad,'degenerate_triangles':tiny}


def _surface_signature(obj,deps):
    # Sharing a paint mesh is valid only for exactly identical evaluated local
    # seats. A future per-seat geometry edit must get its own ray projection.
    evaluated=obj.evaluated_get(deps)
    mesh=evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        return (tuple(tuple(v.co) for v in mesh.vertices),
                tuple(tuple(t.vertices) for t in mesh.loop_triangles))
    finally:
        evaluated.to_mesh_clear()


def apply_seat_print(master):
    """Apply only print objects; caller must have applied photo seat geometry."""
    scene=bpy.context.scene;old_frame=scene.frame_current
    created=[];templates={};reports=[]
    try:
        scene.frame_set(0)
        with canonical_export_context(master):
            for obj in list(master.objects):
                if not obj.get(MARKER):continue
                mesh=obj.data;bpy.data.objects.remove(obj,do_unlink=True)
                if mesh.users==0:bpy.data.meshes.remove(mesh)
            materials=(_material('Yellow'),_material('Grey'))
            panels=sorted((o for o in master.objects if o.name.startswith('Passenger seat ') and
                           (o.name.endswith(' backrest') or o.name.endswith(' rear shell'))),key=lambda o:o.name)
            back_ids={int(o.name.split()[2]) for o in panels if o.name.endswith(' backrest')}
            rear_ids={int(o.name.split()[2]) for o in panels if o.name.endswith(' rear shell')}
            original=set(range(1,32));removed={7,8,9,10}
            if not ((back_ids==original and rear_ids==set(range(1,27))) or
                    (back_ids==original-removed and rear_ids==set(range(1,27))-removed)):
                raise RuntimeError('Seat print requires the complete original layout or the approved 07..10 removal; '+str((sorted(back_ids),sorted(rear_ids))))
            deps=bpy.context.evaluated_depsgraph_get()
            for panel in panels:
                if panel.get('seat_photo_shape_revision')!=3:
                    raise RuntimeError('Apply photo seat geometry before printing: '+panel.name)
                rear=panel.name.endswith(' rear shell')
                key=(rear,_surface_signature(panel,deps))
                if key not in templates:
                    mesh,report=_print_mesh(panel,rear,materials,deps)
                    templates[key]=mesh;reports.append(report)
                name=PREFIX+panel.name
                if bpy.data.objects.get(name):raise RuntimeError('An unowned object has a reserved seat-print name: '+name)
                obj=bpy.data.objects.new(name,templates[key]);master.objects.link(obj)
                obj.parent=panel;obj.matrix_parent_inverse=Matrix.Identity(4);obj.matrix_basis=Matrix.Identity(4)
                for key in panel.keys():
                    if key.startswith('cdlc_'):obj[key]=panel[key]
                obj['cdlc_part']=10;obj['cdlc_last_lod']=0;obj['preserve_lod']=True
                obj[MARKER]=REVISION
                obj['seat_print_reference']='references/user-reference-20261015-01.jpg'
                obj['seat_print_status']='Photo-estimated repeat/colours; ray-fit to actual seat mesh; LOD0 only'
                created.append(obj)
            bpy.context.view_layer.update()
            for obj in created:_add_source_metadata(obj)
        triangles=sum(len(o.data.polygons) for o in created)
        if triangles>40000:raise RuntimeError(f'Seat print exceeds the agreed 40000-triangle LOD0 allowance: {triangles}')
        return {'status':'PASS','revision':REVISION,'objects':len(created),'triangles_lod0':triangles,
                'triangles_lod1':0,'triangles_lod2':0,'unique_surface_reports':reports,
                'verified_surface_instances':len(created),'shared_mesh_requires_exact_evaluated_geometry':True,
                'pattern':'Three yellow and three grey irregular closed ribbons plus four short yellow dashes per surface',
                'paint_thickness_m':PAINT_THICKNESS,'ray_sample_base_offset_m':BASE_OFFSET,
                'surface_fit':'Actual evaluated-mesh rays at edges and both candidate face diagonals; adaptive local refinement',
                'estimate_note':'Textile repeat and colours estimated from user photograph; runtime uses Yellow/Grey palette swatches'}
    finally:
        scene.frame_set(old_frame);bpy.context.view_layer.update()
