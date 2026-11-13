"""Measure proposed short-mirror positions against the unchanged saved body's opaque mesh."""
import argparse,json,sys
from pathlib import Path
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

def B(p):return Vector((-p[0],-p[2],p[1]))

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--blend',type=Path,required=True);ap.add_argument('--data',type=Path,required=True);ap.add_argument('--out',type=Path,required=True)
    a=ap.parse_args(sys.argv[sys.argv.index('--')+1:]);bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()))
    s=json.loads((a.data/'spec.json').read_text());d=json.loads((a.data/'layout.json').read_text());eye=Vector(d['cameraEye']);trees=[]
    for lod in range(3):
        verts=[];faces=[]
        for ob in bpy.data.collections['LOD%d'%lod].all_objects:
            if ob.type!='MESH' or ob.get('bake')=='only' or ob.get('cdlc_part')==15:continue
            me=ob.data;me.calc_loop_triangles();offset=len(verts);verts.extend(ob.matrix_world@v.co for v in me.vertices)
            faces.extend(tuple(offset+i for i in t.vertices) for t in me.loop_triangles)
        trees.append(BVHTree.FromPolygons(verts,faces,all_triangles=True))
    result=[]
    for width in (.20,.23,.26,.29,.32):
        for z in np.arange(-.18,.101,.02):
            centre=Vector((s['DriverSide']*(s['HalfWidth']+width),s['RoofY']-.91,s['FrontZ']+z))
            target=Vector((s['DriverSide']*(s['HalfWidth']+.8),1,s['RearZ']-7));normal=((eye-centre).normalized()+(target-centre).normalized()).normalized()
            right=Vector((0,1,0)).cross(-normal).normalized();up=(-normal).cross(right).normalized();visible=[]
            for tree in trees:
                clear=0
                for y in np.linspace(-.42,.42,11):
                    for x in np.linspace(-.42,.42,11):
                        end=B(centre+right*(x*.205)+up*(y*.32));start=B(eye);direction=end-start
                        hit,_,_,_=tree.ray_cast(start,direction.normalized(),direction.length-.01);clear+=hit is None
                visible.append(clear)
            result.append({'width':width,'front_offset':round(float(z),4),'visible':visible})
    a.out.write_text(json.dumps({'candidate_measurements':result,'limit':'Opaque body visibility only; final mirror/arm geometry and optical glass still need review.'},indent=2))
    print(json.dumps([r for r in result if min(r['visible'])==121]))

if __name__=='__main__':main()
