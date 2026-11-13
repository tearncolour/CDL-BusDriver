"""Export actual saved K18 joint geometry to the generic independent CJA1 resource.

Blender -b --python-exit-code 1 --python this_file -- --blend INPUT --output-dir DIR
Never saves INPUT, creates no SWB1 part, and does not import a ready-made vehicle model.
"""
import argparse
import gzip
import hashlib
import importlib.util
import io
import json
import math
from pathlib import Path
import struct
import sys

import bpy
import bmesh
from mathutils import Matrix, Quaternion, Vector

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('k18_section_export', Path(__file__).with_name('export_model.py'))
section_export = importlib.util.module_from_spec(spec)
spec.loader.exec_module(section_export)
B_TO_U = section_export.B_TO_U


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def rest_frame(obj):
    # U is a reflection, so conjugate the rotation (not just multiply it by U).
    rotation = B_TO_U @ obj.matrix_world.to_3x3() @ B_TO_U.inverted()
    q = rotation.to_quaternion().normalized()
    return {'position': list(B_TO_U @ obj.matrix_world.translation), 'rotation': [q.x, q.y, q.z, q.w]}


def closed_triangles(vertices, faces, weights, swatch, smooth_sides=False):
    """Explicit reduced topology, never Decimate or area-based deletion."""
    data=bpy.data.meshes.new('Temporary CJA1 reduced topology')
    try:
        data.from_pydata(vertices,[],faces)
        data.update()
        bm=bmesh.new();bm.from_mesh(data)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        if bm.calc_volume(signed=True)<0:
            bmesh.ops.reverse_faces(bm,faces=list(bm.faces))
        if any(not edge.is_manifold for edge in bm.edges):
            raise ValueError('Reduced joint topology is not closed')
        bm.to_mesh(data);bm.free();data.update()
        if smooth_sides:
            for poly in data.polygons:
                poly.use_smooth=len(poly.vertices)==4
        data.calc_loop_triangles()
        result=[]
        for tri in data.loop_triangles:
            corners=[(tuple(data.vertices[i].co),tuple(data.corner_normals[loop].vector),weights[i])
                     for i,loop in zip(tri.vertices,tri.loops)]
            p=[Vector(c[0]) for c in corners]
            if (p[1]-p[0]).cross(p[2]-p[0]).length_squared<1e-18:
                raise ValueError('Reduced joint contains a tiny triangle')
            result.append((swatch,corners))
        return result
    finally:
        bpy.data.meshes.remove(data)


def bellows_rows(obj):
    return len(obj.data.vertices)//(2*56)


def reduced_topology(obj, data, weights, swatch, lod):
    if obj.name == 'Flexible double-wall articulation bellows':
        # Preserve alternating crests/valleys when removing folds. Every-other-row sampling
        # would select only crests and turn the bellows into a smooth sleeve.
        total=bellows_rows(obj)
        rows=[j for j in range(total) if j%4 in (0,1)]+([total-1] if (total-1)%4 not in (0,1) else []) if lod==1 else             [j for j in range(total) if j%8 in (0,1)]+([total-1] if (total-1)%8 not in (0,1) else [])
        around=28 if lod==1 else 14
        old_indices=[inside*total*56+j*56+k*(56//around) for inside in range(2) for j in rows for k in range(around)]
        vertices=[data['vertices'][i] for i in old_indices]
        reduced_weights=[weights[i] for i in old_indices]
        count=len(rows);half=count*around;faces=[]
        for inside in (0,1):
            for j in range(count-1):
                for k in range(around):
                    p=inside*half+j*around+k;q=inside*half+j*around+(k+1)%around
                    faces.append((p,q,q+around,p+around))
        for j in (0,count-1):
            for k in range(around):
                p=j*around+k;q=j*around+(k+1)%around
                faces.append((p,q,q+half,p+half))
        return closed_triangles(vertices,faces,reduced_weights,swatch)
    n=int(obj.get('radial_segments',0));rings=int(obj.get('radial_rings',0))
    factor=4 if lod==2 and n%4==0 and n//4>=6 else 2
    if rings==2 and n%factor==0 and n//factor>=6 and len(data['vertices'])==2*n:
        nn=n//factor;indices=[ring*n+k*factor for ring in range(2) for k in range(nn)]
        vertices=[data['vertices'][i] for i in indices];reduced_weights=[weights[i] for i in indices]
        faces=[tuple(reversed(range(nn))),tuple(range(nn,2*nn))]
        faces += [(k,(k+1)%nn,(k+1)%nn+nn,k+nn) for k in range(nn)]
        return closed_triangles(vertices,faces,reduced_weights,swatch,True)
    return None


def verify_bends(triangles,joint):
    checks=[]
    for lod,tris in enumerate(triangles):
        for angle in (0,-15,15,-25,25,-45,45,-50,50):
            minimum=float('inf');flipped=0;tiny=0;endpoint_error=0.0
            for sw,corners in tris:
                positions=[];normals=[]
                for p,n,w in corners:
                    q=Quaternion(Vector((0,1,0)),math.radians(angle*w))
                    positions.append(joint+q@(Vector(p)-joint));normals.append(q@Vector(n))
                    if w in (0.0,1.0):
                        end=Quaternion(Vector((0,1,0)),math.radians(angle*w))
                        endpoint_error=max(endpoint_error,(positions[-1]-(joint+end@(Vector(p)-joint))).length)
                cross=(positions[1]-positions[0]).cross(positions[2]-positions[0])
                minimum=min(minimum,cross.length_squared)
                tiny += cross.length_squared<1e-18
                flipped += cross.dot(sum(normals,Vector()))<=0
            row={'lod':lod,'degrees':angle,'triangles':len(tris),'tiny':tiny,'normal_inversions':flipped,
                 'minimum_cross_squared':minimum,'endpoint_residual_m':endpoint_error}
            checks.append(row)
            if tiny or flipped or endpoint_error>1e-6:
                raise ValueError('Joint bend audit failed: '+json.dumps(row))
    return checks


def main(args):
    parser = argparse.ArgumentParser()
    parser.add_argument('--blend', type=Path, default=ROOT/'ModelSource/CRRC_K18/CRRC_K18.blend')
    parser.add_argument('--output-dir', type=Path, required=True)
    options = parser.parse_args(args)
    before = sha(options.blend)
    bpy.ops.wm.open_mainfile(filepath=str(options.blend.resolve()))
    bpy.context.scene.frame_set(1)
    objects = list(bpy.data.collections['K18 Joint'].objects)
    front, rear = bpy.data.objects['section_front'], bpy.data.objects['section_rear']
    bellows = bpy.data.objects['Flexible double-wall articulation bellows']
    raw_points = [B_TO_U @ (bellows.matrix_world @ v.co) for v in bellows.data.vertices]
    front_z, rear_z = max(p.z for p in raw_points), min(p.z for p in raw_points)
    if front_z-rear_z < .1:
        raise ValueError('Source joint has no longitudinal span')
    f_rest, r_rest = rest_frame(front), rest_frame(rear)
    joint = B_TO_U @ bpy.data.objects['articulation_system'].matrix_world.translation
    rows = json.loads((ROOT/'Tools/CRRC_K18/palette.json').read_text(encoding='utf-8'))['palette']
    palette = {r['name']: r for r in rows}
    triangles = [[] for _ in range(3)]
    evidence, weighted_objects = [], []
    for obj in sorted(objects, key=lambda o: o.name):
        if obj.type not in section_export.GEOMETRY:
            continue
        if section_export.inherited(obj, 'runtime_generated', ''):
            raise ValueError('Dynamic runtime object unexpectedly present in joint: '+obj.name)
        if obj.parent == front:
            binding = 'front'
        elif obj.parent == rear:
            binding = 'rear'
        elif obj == bellows or obj.name.startswith('Articulation folding handrail'):
            binding = 'span'
        elif obj.name.startswith('Articulation anti-slip turntable'):
            binding = 'bisector'
        else:
            raise ValueError('Unspecified source joint binding: '+obj.name)
        data = section_export.read_object(obj, 'joint', Vector((0,0,0)), palette)
        weights = [0.0 if binding == 'front' else 1.0 if binding == 'rear' else .5 if binding == 'bisector'
                   else min(1.0,max(0.0,(front_z-p[2])/(front_z-rear_z))) for p in data['vertices']]
        # Explicit endpoint rounding prevents floating conversion noise from unpinning attachment rows.
        weights = [0.0 if w < 1e-6 else 1.0 if w > 1-1e-6 else w for w in weights]
        out = []
        for indices, normals, material_name in data['triangles']:
            material = bpy.data.materials[material_name]
            sw = section_export.runtime_swatch(material,palette)
            if material.get('glass') or int(material.get('light_id',0)) or sw['finish'] in ('Glass','Glow','Lamp','Tail','Beacon','Aviation','NavGreen'):
                raise ValueError('Joint resource permits only opaque static finishes: '+material_name)
            vertices = [(data['vertices'][i],normals[c],weights[i]) for c,i in enumerate(indices)]
            out.append((sw['index'],vertices))
        per_lod=[]
        for lod in range(3):
            if data['first_lod'] <= lod <= data['last_lod']:
                reduced=reduced_topology(obj,data,weights,out[0][0],lod) if lod else None
                current=out if reduced is None else reduced
                triangles[lod].extend(current);per_lod.append(len(current))
            else:
                per_lod.append(0)
        evidence.append({'name':obj.name,'binding':binding,'triangles':len(out),
                         'first_lod':data['first_lod'],'last_lod':data['last_lod'],
                         'lod_triangles':per_lod,'weight_min':min(weights),'weight_max':max(weights)})
        if binding == 'span':
            weighted_objects.append((obj,data['vertices'],weights))
    # Compare against saved authored shape keys, including the normal large-turn operating range.
    motion = []
    for frame, angle in ((160,25),(190,-25),(250,50),(280,-50)):
        bpy.context.scene.frame_set(frame)
        worst = 0.0
        for obj, points, weights in weighted_objects:
            evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
            mesh = evaluated.to_mesh()
            try:
                if len(mesh.vertices) != len(points):
                    raise ValueError('Source deformation changed joint topology: '+obj.name)
                for i, vertex in enumerate(mesh.vertices):
                    expected = B_TO_U @ (obj.matrix_world @ vertex.co)
                    q = Quaternion(Vector((0,1,0)), angle*weights[i]*3.141592653589793/180)
                    actual = joint + q @ (Vector(points[i])-joint)
                    worst = max(worst,(expected-actual).length)
            finally:
                evaluated.to_mesh_clear()
        motion.append({'source_frame':frame,'bend_degrees':angle,'max_position_residual_m':worst,
                       'pass':worst < .0001})
        if worst >= .0001:
            raise ValueError('Joint export differs from source motion: %.8fm' % worst)
    bpy.context.scene.frame_set(1)
    bend_audit=verify_bends(triangles,joint)
    options.output_dir.mkdir(parents=True,exist_ok=True)
    stream = io.BytesIO()
    stream.write(struct.pack('<4si',b'CJA1',3))
    for tris in triangles:
        if not tris or len(tris)>100000:
            raise ValueError('Invalid triangle budget for CJA1')
        stream.write(struct.pack('<i',len(tris)))
        for sw, vertices in tris:
            stream.write(struct.pack('<B',sw))
            for p,n,w in vertices:
                stream.write(struct.pack('<7f',*p,*n,w))
    target = options.output_dir/'CRRC_K18.joint.gz'
    with target.open('wb') as file:
        with gzip.GzipFile(fileobj=file,mode='wb',mtime=0,filename='') as zipped:
            zipped.write(stream.getvalue())
    # Both anchors are local to their actual saved source section roots.
    inverse = lambda obj: (B_TO_U @ obj.matrix_world.to_3x3() @ B_TO_U.inverted()).inverted()
    front_anchor = inverse(front) @ (joint-Vector(f_rest['position']))
    rear_anchor = inverse(rear) @ (joint-Vector(r_rest['position']))
    report = {'status':'PASS','source':str(options.blend.resolve()),'source_sha256':before,
              'exporter_sha256':sha(Path(__file__)),'mesh':target.name,'mesh_sha256':sha(target),
              'format':'cdlb-joint-v1','binary':'CJA1','lod_triangles':[len(t) for t in triangles],
              'section_rest':{'front':f_rest,'rear':r_rest},'front_anchor':list(front_anchor),
              'rear_anchor':list(rear_anchor),'span_z':[front_z,rear_z],'objects':evidence,'source_motion':motion,
              'bend_audit':bend_audit,
              'scope':['Actual source geometry; no imported premade model.',
                       'Explicit closed LOD topology: bellows 49x56, 25x28, 13x14 rows x ring samples; fold valleys preserved.',
                       'Source +/-25 and +/-50deg shapes compared; all LOD triangles and attached endpoints numerically checked through +/-50deg. These checks do not prove absence of nonlocal self-intersection or game acceptance.',
                       'Turntable is rigidly aligned with the angle bisector; the source turntable is rotationally symmetric.',
                       'Runtime/game visibility, interpenetration, and material rendering not certified by this export.']}
    if sha(options.blend) != before:
        raise RuntimeError('Read-only source changed during joint export')
    (options.output_dir/'joint-export.json').write_text(json.dumps(report,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    print('K18_JOINT_EXPORT '+json.dumps({'status':'PASS','mesh':str(target),'triangles':report['lod_triangles']}),flush=True)


if __name__ == '__main__':
    main(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
