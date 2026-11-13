"""Check exported production strap poses against complete opaque assembly and other straps."""
import argparse,hashlib,json,sys
from pathlib import Path
import bpy
from mathutils import Matrix,Quaternion,Vector
from mathutils.bvhtree import BVHTree

def tree(objects):
    vertices=[];faces=[];owners=[]
    for ob in objects:
        mesh=ob.data;mesh.calc_loop_triangles();offset=len(vertices);vertices.extend(ob.matrix_world@v.co for v in mesh.vertices)
        for t in mesh.loop_triangles:faces.append(tuple(offset+i for i in t.vertices));owners.append((ob.name,t.index))
    return BVHTree.FromPolygons(vertices,faces,all_triangles=True),owners

def free_mesh(ob):
    mesh=ob.data;mesh.calc_loop_triangles();vertices=[];faces=[]
    for t in mesh.loop_triangles:
        poly=[ob.matrix_local@mesh.vertices[i].co for i in t.vertices];clipped=[]
        for i,a in enumerate(poly):
            b=poly[(i+1)%3];da=-.05-a.z;db=-.05-b.z
            if da>=0:clipped.append(a)
            if da*db<0:clipped.append(a+(b-a)*(da/(da-db)))
        for i in range(1,len(clipped)-1):
            ids=tuple(range(len(vertices),len(vertices)+3));vertices.extend((clipped[0],clipped[i],clipped[i+1]));faces.append(ids)
    return vertices,faces

ap=argparse.ArgumentParser();ap.add_argument('--blend',type=Path,required=True);ap.add_argument('--data',type=Path,required=True);ap.add_argument('--out',type=Path,required=True);ap.add_argument('--rest-only',action='store_true')
a=ap.parse_args(sys.argv[sys.argv.index('--')+1:]);bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()))
poses=[{'frame':0,'doorOpen':0,'swing':[0,0]}] if a.rest_only else json.loads((a.data/'strap-motion.json').read_text())
rows=[];failures=[]
for lod in range(3):
    for i in range(3):bpy.data.collections['LOD%d'%i].hide_viewport=i!=lod
    bpy.context.scene.frame_set(0);bpy.context.view_layer.update()
    obs=[o for o in bpy.data.collections['LOD%d'%lod].all_objects if o.type=='MESH' and o.get('bake')!='only']
    straps=[o for o in obs if o.name.startswith('Strap')];fixed=[o for o in obs if o not in straps];local={o.name:free_mesh(o) for o in straps};cache={}
    for pose in poses:
        bpy.context.scene.frame_set(pose['frame']);bpy.context.view_layer.update();opening=pose['doorOpen']
        if not a.rest_only:
            expected={p['part']:p for p in pose['parts']};mapping=Matrix(((-1,0,0),(0,0,-1),(0,1,0)))
            for ob in straps:
                part=expected[ob['cdlc_part']];q=part['rotation'];rotation=mapping@Quaternion((q[3],q[0],q[1],q[2])).to_matrix()@mapping.transposed()
                target=rotation.to_4x4();target.translation=mapping@Vector(part['position'])
                error=max(abs(ob.parent.matrix_world[r][c]-target[r][c]) for r in range(4) for c in range(4))
                if error>2e-5:raise ValueError('Saved strap pose differs from production Apply: '+ob.name)
        if opening not in cache:cache[opening]=tree(fixed)
        assembly,owners=cache[opening];moving=[];contacts=[];minimum=100.
        for ob in straps:
            vs,fs=local[ob.name];vs=[ob.parent.matrix_world@p for p in vs];moving.append((ob,BVHTree.FromPolygons(vs,fs,all_triangles=True),vs,fs))
        for index,(ob,bvh,vs,fs) in enumerate(moving):
            pairs=bvh.overlap(assembly)
            if pairs:contacts.append({'strap':ob.name,'against':'assembly','pairs':len(pairs),'examples':[{'strap_triangle':i,'other':owners[j],'strap_vertices':[list(vs[v]) for v in fs[i]]} for i,j in pairs[:4]]})
            for other,other_tree,_,_ in moving[index+1:]:
                hits=bvh.overlap(other_tree)
                if hits:contacts.append({'strap':ob.name,'against':other.name,'pairs':len(hits)})
            for p in vs[::max(1,len(vs)//24)]:
                hit,normal,triangle,distance=assembly.find_nearest(p)
                if hit is not None:minimum=min(minimum,distance)
        rows.append({'lod':lod,'frame':pose['frame'],'doorOpen':opening,'swing':pose['swing'],'straps':len(straps),'sampled_vertex_surface_distance_m':minimum,'contacts':contacts})
        if contacts:failures.append({'lod':lod,'frame':pose['frame'],'groups':len(contacts)})
report={'source_sha256':hashlib.sha256(a.blend.read_bytes()).hexdigest(),'rest_only':a.rest_only,'pose_count_per_lod':len(poses),'rows':rows,'failures':failures,
    'scope':'Actual opaque mesh triangle overlaps. The upper 50 mm below each moving pivot is the intended rail attachment articulation zone; all strap geometry below it is checked against every fixed part and other straps. Production Apply transforms are stored in source keyframes. Sampled vertex distance is not an exact minimum or continuous swept-volume proof.'}
if not a.rest_only:report['motion_sha256']=hashlib.sha256((a.data/'strap-motion.json').read_bytes()).hexdigest()
a.out.write_text(json.dumps(report,indent=2));print(json.dumps({'poses':len(poses)*3,'failures':failures}))
if failures:raise RuntimeError('Strap intersections remain')
