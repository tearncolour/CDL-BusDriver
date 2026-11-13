"""Locate undefined serialized tangent frames without altering an export."""
import json,sys
from pathlib import Path
import bpy,numpy as np
sys.path.insert(0,str(Path.home()/'Desktop/CDLDevKit/blender'))
from fbx_corners import read_fbx_corners
from bake_detail import swatch_uv
native=Path(sys.argv[-1]);summary=json.loads((native/'export-summary.json').read_text());rows=[]
for mesh in summary['meshes']:
    c=read_fbx_corners(mesh['path'],rigged=bool(mesh.get('bones')));bad=np.linalg.norm(c[:,:,8:11],axis=2)<.5
    if not bad.any():continue
    atlas=next(a for a in summary['atlases'] if a['name']==mesh['mesh_name'])
    for ti in np.flatnonzero(bad.any(axis=1)):
        tri=c[ti];matches=[]
        for mat in atlas['materials']:
            if 'block' not in mat:continue
            uv=np.array(swatch_uv(mat['block']))
            matches.append({'distance':float(np.max(np.abs(tri[:,6:8]-uv))),'material':mat})
        row={'mesh':mesh['mesh_name'],'triangle':int(ti),'corners':tri.tolist(),'nearest_swatches':sorted(matches,key=lambda m:m['distance'])[:2]};rows.append(row)
filename='undefined-tangent-locations.json'
if '--source-match' in sys.argv:
    from mathutils import Vector
    from mathutils.bvhtree import BVHTree
    bpy.ops.wm.open_mainfile(filepath=summary['source']);verts=[];faces=[];source=[]
    for ob in bpy.data.collections['LOD0'].all_objects:
        if ob.type!='MESH' or ob.get('bake')=='only':continue
        me=ob.data;me.calc_loop_triangles();offset=len(verts);verts.extend(ob.matrix_world@v.co for v in me.vertices)
        for t in me.loop_triangles:
            faces.append(tuple(offset+i for i in t.vertices));source.append((ob,t.index))
    tree=BVHTree.FromPolygons(verts,faces,all_triangles=True)
    for row in rows:
        point=Vector(np.array(row['corners'])[:,:3].mean(axis=0));hit,n,index,distance=tree.find_nearest(point)
        ob,ti=source[index];t=ob.data.loop_triangles[ti];uv=ob.data.uv_layers.get('DetailUV')
        row['source_match']={'object':ob.name,'triangle':ti,'distance':distance,'vertices':[list(ob.matrix_world@ob.data.vertices[i].co) for i in t.vertices],
            'detail_uv':[list(uv.data[i].uv) for i in t.loops] if uv else None,'material':ob.data.materials[t.material_index].name}
    filename='undefined-tangent-source-locations.json'
(native/filename).write_text(json.dumps(rows,indent=2))
print(json.dumps(rows),flush=True)
