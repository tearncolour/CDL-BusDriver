"""Check actual mirror support contacts using mesh segment intersections.

Bounding boxes select candidates only. A joint passes through an actual edge /
triangle intersection or a vertex inside the adjacent closed solid. All faces,
shells and fasteners remain the authored production objects.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree
sys.path.insert(0, str(Path(__file__).resolve().parent))
from source_coordinates import canonical_world_matrix


def mesh(obj):
    deps = bpy.context.evaluated_depsgraph_get()
    data = bpy.data.meshes.new_from_object(obj.evaluated_get(deps), depsgraph=deps)
    matrix = canonical_world_matrix(obj)
    points = [matrix @ v.co for v in data.vertices]
    data.calc_loop_triangles()
    triangles = [tuple(t.vertices) for t in data.loop_triangles]
    edges = sorted({tuple(sorted((a,b))) for t in triangles for a,b in zip(t, t[1:]+t[:1])})
    tree = BVHTree.FromPolygons(points, triangles, all_triangles=True)
    bpy.data.meshes.remove(data)
    return dict(points=points, edges=edges, tree=tree,
                low=Vector(tuple(min(p[i] for p in points) for i in range(3))),
                high=Vector(tuple(max(p[i] for p in points) for i in range(3))))


def inside(point, target):
    if any(point[i] < target['low'][i] or point[i] > target['high'][i] for i in range(3)):
        return False
    votes=0
    for direction in (Vector((.371,.513,.774)), Vector((-.622,.703,.341)), Vector((.503,-.811,.301))):
        direction.normalize()
        origin=point.copy();count=0
        for _ in range(128):
            hit,_,_,_=target['tree'].ray_cast(origin,direction,10.)
            if hit is None:
                break
            count+=1
            origin=hit+direction*1e-6
        else:
            raise RuntimeError('Non-terminating solid parity ray')
        votes+=count%2
    return votes>=2


def joint(a,b):
    # Search both objects' triangle edges. BVH broad-phase overlap alone would
    # permit false positives across a hole or between disconnected surfaces.
    nearest=float('inf')
    for source,target in ((a,b),(b,a)):
        for point in source['points']:
            _,_,_,distance=target['tree'].find_nearest(point)
            if distance is not None:
                nearest=min(nearest,distance)
        for i,j in source['edges']:
            start,end=source['points'][i],source['points'][j]
            if any(max(start[k],end[k]) < target['low'][k]-1e-6 or
                   min(start[k],end[k]) > target['high'][k]+1e-6 for k in range(3)):
                continue
            vector=end-start
            if vector.length<1e-8:
                continue
            hit,_,face,d=target['tree'].ray_cast(start-vector.normalized()*1e-7,
                                               vector.normalized(),vector.length+2e-7)
            if hit is not None:
                return dict(status='PASS',method='Actual triangle-edge/triangle intersection',
                            witness_canonical_m=list(hit), nearest_vertex_surface_distance_m=nearest)
        for point in source['points']:
            if inside(point,target):
                return dict(status='PASS',method='Vertex inside adjacent solid, majority of three parity rays',
                            witness_canonical_m=list(point),nearest_vertex_surface_distance_m=nearest)
    return dict(status='FAIL',method='No intersecting edges or contained vertices',
                nearest_vertex_surface_distance_m=nearest)


def verify(master):
    pairs=[]
    for assembly,arm in (('driver side small','Mirror driver side short black curved arm'),
                         ('door side large','Mirror door side long black swept arm')):
        shoe='Mirror '+assembly+' A-pillar mounting shoe'
        hinge='Mirror '+assembly+' pivot barrel'
        shell='Mirror shell '+assembly+' rounded teardrop'
        pairs.extend(((shoe,hinge),(hinge,arm),(arm,shell)))
    pairs.extend((('Mirror door side long black swept arm','Mirror door side auxiliary independent black stay'),
                  ('Mirror door side auxiliary independent black stay','Mirror door side auxiliary ball joint'),
                  ('Mirror door side auxiliary ball joint','Mirror shell door side independent oval auxiliary')))
    meshes={name:mesh(master.all_objects[name]) for name in {n for pair in pairs for n in pair}}
    rows=[dict(first=a,second=b,**joint(meshes[a],meshes[b])) for a,b in pairs]
    return dict(status='PASS' if all(r['status']=='PASS' for r in rows) else 'FAIL',joints=rows,
                scope='Authored three-mirror support contact graph only. Requires separate fixed-coachwork shoe test, curvature, normals and visual review.')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report',required=True,type=Path)
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    bpy.context.scene.frame_set(0);bpy.context.view_layer.update()
    source=Path(bpy.data.filepath)
    result=verify(bpy.data.collections['SWB6109EV12G | editable components'])
    result.update(source_sha256=hashlib.sha256(source.read_bytes()).hexdigest())
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result))
    if result['status']!='PASS':raise SystemExit(1)
