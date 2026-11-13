"""Sample first opaque triangle orientation from the recorded complete-assembly review cameras."""
import argparse,hashlib,json,math,sys
from pathlib import Path
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--blend',type=Path,required=True);ap.add_argument('--views',type=Path,required=True);ap.add_argument('--out',type=Path,required=True)
    ap.add_argument('--reuse-cameras',action='store_true')
    a=ap.parse_args(sys.argv[sys.argv.index('--')+1:]);review=json.loads(a.views.read_text());digest=hashlib.sha256(a.blend.read_bytes()).hexdigest()
    if digest!=review['source_sha256'] and not a.reuse_cameras:raise ValueError('Review/source hash mismatch')
    bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()));rows=[];cached={};failures=[]
    for view in review['views']:
        if view.get('normal_maps') is False:continue
        key=(view['lod'],view['frame'])
        if key not in cached:
            for i in range(3):bpy.data.collections['LOD%d'%i].hide_viewport=i!=view['lod']
            bpy.context.scene.frame_set(view['frame']);bpy.context.view_layer.update();verts=[];faces=[];owners=[]
            for ob in bpy.data.collections['LOD%d'%view['lod']].all_objects:
                if ob.type!='MESH' or ob.get('bake')=='only':continue
                mesh=ob.data;mesh.calc_loop_triangles();offset=len(verts);verts.extend(ob.matrix_world@v.co for v in mesh.vertices)
                for t in mesh.loop_triangles:faces.append(tuple(offset+i for i in t.vertices));owners.append(ob.name)
            cached[key]=(BVHTree.FromPolygons(verts,faces,all_triangles=True),owners)
        tree,owners=cached[key];origin=Vector(view['camera']);q=(Vector(view['target'])-origin).to_track_quat('-Z','Y')
        width,height=80,60;half=.5*36/view['lens_mm'];aspect=view['resolution'][0]/view['resolution'][1];back=[];hits=0
        for y in range(height):
            for x in range(width):
                direction=q@Vector((((x+.5)/width*2-1)*half,(1-(y+.5)/height*2)*half/aspect,-1)).normalized()
                p,n,index,distance=tree.ray_cast(origin+direction*.025,direction,100)
                if p is None:continue
                hits+=1
                if n.dot(direction)>1e-4:back.append({'pixel':[x,y],'point':list(p),'object':owners[index],'triangle':index,'dot':n.dot(direction)})
        rows.append({'image':view['image'],'lod':key[0],'frame':key[1],'opaque_hits':hits,'backface_samples':len(back),'examples':back[:40]})
        if back:failures.append(view['image']+': '+str(len(back)))
    report={'source_sha256':digest,'camera_plan_source_sha256':review['source_sha256'],'reused_camera_parameters':a.reuse_cameras,
        'prior_images_used':False,'grid':[80,60],'views':rows,'failures':failures,
        'scope':'First opaque surface winding. Transmissive glass is excluded from the ray tree; optical rendering and window/opening closure are separate checks.'}
    a.out.write_text(json.dumps(report,indent=2));print(json.dumps({'views':len(rows),'failures':failures}))
    if failures:raise RuntimeError('Visible backface samples need object/point review')

if __name__=='__main__':main()
