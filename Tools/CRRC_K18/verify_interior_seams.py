import bpy, bmesh, sys, json, hashlib, math, gzip, struct, argparse
from collections import Counter
from pathlib import Path
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree
ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser(description='Read-only K18 sign-return and wheelhouse seam regression')
parser.add_argument('--blend',type=Path,required=True)
parser.add_argument('--runtime',type=Path,required=True)
parser.add_argument('--report',type=Path,required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
src=args.blend.resolve();runtime=args.runtime.resolve()
source_before=hashlib.sha256(src.read_bytes()).hexdigest()
runtime_before=hashlib.sha256(runtime.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(src)); bpy.context.scene.frame_set(1)
M=Matrix(((-1,0,0),(0,0,1),(0,-1,0)))
def inh(o,k,d=None):
 while o:
  if k in o:return o[k]
  o=o.parent
 return d
def vec(v):return [float(x) for x in v]
def ray(pixel,view):
 if view=='driver':eye=Vector((-.79,1.97,7.62));forward=Vector((0,-.15,2)).normalized();fov=88
 else:eye=Vector((0,.8375,.47000003));forward=(Vector((0,1.0375,8.41))-eye).normalized();fov=85
 right=forward.cross(Vector((0,1,0))).normalized()*-1
 up=forward.cross(right).normalized()
 xx=((pixel[0]+.5)/1440*2-1)*1.5*math.tan(math.radians(fov/2))
 yy=(1-(pixel[1]+.5)/960*2)*math.tan(math.radians(fov/2))
 return eye,(forward+right*xx+up*yy).normalized()
pixelgroups={'driver':[(800,y) for y in (262,265,267,269,271,274,278)]+[(x,269) for x in (680,700,1000,1200,1350,1390)],
 'front-low':[(100,440),(100,420),(165,500),(215,640),(230,780),(1330,430),(1330,410),(1240,580),(1250,580),(1205,703),(1205,725)]}
sources=[[] for _ in range(3)]
deps=bpy.context.evaluated_depsgraph_get()
for o in bpy.data.objects:
 if o.type!='MESH' or inh(o,'cab_section')!='front' or inh(o,'runtime_generated',''):continue
 if int(inh(o,'cdlc_part',-1)) not in (0,14,17):continue
 ev=o.evaluated_get(deps);me=ev.to_mesh()
 me.calc_loop_triangles();verts=[M@(o.matrix_world@v.co) for v in me.vertices]
 faces=[];met=[]
 for tr in me.loop_triangles:
  mat=me.materials[tr.material_index] if tr.material_index<len(me.materials) else None
  if mat and mat.get('glass',False):continue
  faces.append(tuple(tr.vertices));met.append({'tri':tr.index,'material':mat.name if mat else '', 'swatch':mat.get('cdlc_swatch') if mat else None})
 if faces:
  bvh=BVHTree.FromPolygons(verts,faces,all_triangles=True)
  info={'name':o.name,'part':int(inh(o,'cdlc_part')),'bounds':[vec(Vector(tuple(min(v[i] for v in verts) for i in range(3)))),vec(Vector(tuple(max(v[i] for v in verts) for i in range(3))))],
       'modifiers':[{'name':m.name,'type':m.type,'thickness':getattr(m,'thickness',None),'offset':getattr(m,'offset',None)} for m in o.modifiers]}
  for lod in range(int(inh(o,'first_lod',0)),int(inh(o,'last_lod',2))+1):sources[lod].append((bvh,met,info))
 ev.to_mesh_clear()
raw=gzip.decompress(runtime.read_bytes());nl,np=struct.unpack_from('<ii',raw,4);pos=24+np*36
rt=[]
for lod in range(nl):
 vertices=[];faces=[];meta=[]
 for part in range(np):
  n=struct.unpack_from('<i',raw,pos)[0];pos+=4
  if part not in (0,14,17):pos+=73*n;continue
  for i in range(n):
   sw,*val=struct.unpack_from('<B18f',raw,pos);pos+=73
   start=len(vertices);vv=[Vector(val[j*6:j*6+3]) for j in range(3)]
   vertices+=vv;faces.append((start,start+1,start+2));meta.append({'part':part,'tri':i,'swatch':sw,'normals':[val[j*6+3:j*6+6] for j in range(3)]})
 rt.append((BVHTree.FromPolygons(vertices,faces,all_triangles=True),meta))
report={'source_sha256':hashlib.sha256(src.read_bytes()).hexdigest(),'runtime_sha256':hashlib.sha256(runtime.read_bytes()).hexdigest(),'scope':'Read-only opaque source and static SWB1 parts 0/14/17 ray comparison at frame1; no glass, scene mutation saved, or repairs. Screenshot camera pinhole reconstructed from supplied 1440x960 vertical FOV.','rows':[],'objects':{}}
for lod in range(3):
 for view,pixels in pixelgroups.items():
  for pixel in pixels:
   eye,direction=ray(pixel,view);hits=[]
   for bvh,met,info in sources[lod]:
    loc,norm,idx,dist=bvh.ray_cast(eye,direction,100)
    if loc is not None:
     hits.append({'distance':dist,'point':vec(loc),'normal':vec(norm),'object':info['name'],**met[idx]})
     report['objects'][info['name']]=info
   hits.sort(key=lambda a:a['distance'])
   bvh,meta=rt[lod];loc,norm,idx,dist=bvh.ray_cast(eye,direction,100)
   rh=None if loc is None else {'distance':dist,'point':vec(loc),'normal':vec(norm),**meta[idx]}
   report['rows'].append({'lod':lod,'view':view,'pixel':pixel,'eye':vec(eye),'direction':vec(direction),'source_hits':hits[:8],'runtime_hit':rh})
checks=[]
for r in report['rows']:
 if r['view']=='driver' and tuple(r['pixel']) in {(800,267),(800,269),(800,271),(700,269),(1000,269),(1200,269),(1350,269)}:
  checks.append({'lod':r['lod'],'pixel':r['pixel'],'region':'sign-return','pass':bool(r['source_hits']) and 'Front inset destination housing' in r['source_hits'][0]['object'] and r['runtime_hit'] is not None and r['runtime_hit']['part']==14})
 if r['view']=='front-low' and tuple(r['pixel']) in {(100,440),(165,500),(215,640),(1330,430),(1240,580),(1205,703),(1205,725)}:
  hits=r['source_hits'];close=[h for h in hits if h['distance']<=hits[0]['distance']+1e-5] if hits else []
  palette=json.loads((ROOT/'Tools/CRRC_K18/palette.json').read_text(encoding='utf8'))['palette']
  expected={x['name']:x['index'] for x in palette}.get(close[0]['swatch']) if len(close)==1 else None
  checks.append({'lod':r['lod'],'pixel':r['pixel'],'region':'wheelhouse-single-surface','expected_palette_index':expected,'pass':len(close)==1 and close[0]['material']=='Light grey headlining' and r['runtime_hit'] is not None and r['runtime_hit']['swatch']==expected})
geometry=[]
for o in bpy.data.objects:
 if o.type!='MESH' or not ('Continuous wheelhouse unified shell' in o.name or 'Front inset destination housing' in o.name or 'K18 destination mask folded surround' in o.name):continue
 ev=o.evaluated_get(bpy.context.evaluated_depsgraph_get());me=ev.to_mesh();me.calc_loop_triangles()
 edges=Counter();mincross=math.inf;badnormal=0
 for t in me.loop_triangles:
  p=[me.vertices[i].co for i in t.vertices];cross=(p[1]-p[0]).cross(p[2]-p[0]);mincross=min(mincross,cross.length_squared)
  if cross.length_squared<1e-18:continue
  badnormal+=sum(cross.normalized().dot(me.corner_normals[li].vector)<-1e-6 for li in t.loops)
 for poly in me.polygons:
  for a,b in zip(poly.vertices,list(poly.vertices[1:])+[poly.vertices[0]]):edges[tuple(sorted((a,b)))]+=1
 bm=bmesh.new();bm.from_mesh(me);volume=bm.calc_volume(signed=True);bm.free()
 geometry.append({'object':o.name,'triangles':len(me.loop_triangles),'nonmanifold_edges':sum(n!=2 for n in edges.values()),'min_cross_squared':mincross,'opposed_corner_normals':badnormal,'signed_volume':volume})
 ev.to_mesh_clear()
source_unchanged=hashlib.sha256(src.read_bytes()).hexdigest()==source_before
runtime_unchanged=hashlib.sha256(runtime.read_bytes()).hexdigest()==runtime_before
ok=(len(geometry)==12 and all(x['nonmanifold_edges']==0 and x['min_cross_squared']>=1e-18 and x['opposed_corner_normals']==0 and x['signed_volume']>0 for x in geometry)
    and len(checks)==42 and all(x['pass'] for x in checks) and source_unchanged and runtime_unchanged)
report.update(status='PASS' if ok else 'FAIL',checks=checks,geometry=geometry,source_unchanged=source_unchanged,runtime_unchanged=runtime_unchanged,verifier_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),limitations=['Regression rays cover the recorded defects, not a full screen/scene visibility proof.','Ordinary material previews and final game renders remain required.','Wheelhouse cavity/tire steering clearance remains the independent completion audit.'])
args.report.parent.mkdir(parents=True,exist_ok=True)
args.report.write_text(json.dumps(report,indent=2),encoding='utf8')
print('INTERIOR_SEAM_CHECK',report['status'],len(checks),str(args.report))
if not ok:raise RuntimeError('Interior seam regression failed; see report')
