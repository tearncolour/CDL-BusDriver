"""Put the source's metric/closed-wheel UVs into bordered native atlas cells.

Only export rows are split. The saved source and SWB2 geometry stay unchanged.
Interpolated UVs are retained explicitly so splitting a curved wheel cannot
recompute a different nonlinear projection at the new corners.
"""
import math
import bpy
import numpy as np
from mathutils import Vector
import pattern_tiles
import bake_detail

COORDS={}
PATTERNED=set()
ACTIVE_LOD=None
original_lod=bake_detail.bake_lod
PAD=.075

def partition(poly,axis):
    pieces=[poly]
    for edge in range(math.floor(min(p[axis] for p in poly))+1,math.ceil(max(p[axis] for p in poly))):
        next_pieces=[]
        for piece in pieces:
            lo=min(p[axis] for p in piece);hi=max(p[axis] for p in piece)
            if lo>=edge or hi<=edge or min(edge-lo,hi-edge)<PAD*.75:
                next_pieces.append(piece);continue
            nearest=min(piece,key=lambda p:abs(p[axis]-edge))[axis]
            line=nearest if abs(nearest-edge)<PAD*.75 else edge
            next_pieces.extend(p for p in (clip(piece,axis,line,True),clip(piece,axis,line,False)) if len(p)>=3)
        pieces=next_pieces
    return pieces

def resample(image,w,border):
    if image is None:return None
    h0,w0=image.shape[:2]
    phase=(np.arange(w)-border+.5)/(w-2*border)*(1+2*PAD)-PAD
    xs,ys=phase*w0-.5,phase*h0-.5;x0=np.floor(xs).astype(int);y0=np.floor(ys).astype(int)
    fx,fy=(xs-x0)[None,:,None],(ys-y0)[:,None,None]
    top=image[y0%h0][:,x0%w0]*(1-fx)+image[y0%h0][:,(x0+1)%w0]*fx
    bottom=image[(y0+1)%h0][:,x0%w0]*(1-fx)+image[(y0+1)%h0][:,(x0+1)%w0]*fx
    return top*(1-fy)+bottom*fy

def point_key(p):return tuple(round(float(v),9) for v in p)
def face_key(points,pattern):return (ACTIVE_LOD,pattern['_cdlc_material'],tuple(sorted(point_key(p) for p in points)))

def clip(poly,axis,edge,above):
    out=[]
    for i,a in enumerate(poly):
        b=poly[(i+1)%len(poly)];da=(a[axis]-edge)*(1 if above else -1);db=(b[axis]-edge)*(1 if above else -1)
        if da>=0:out.append(a)
        if (da<0<db) or (db<0<da):
            f=da/(da-db);p=a+(b-a)*f;p[axis]=edge;out.append(p)
    clean=[]
    for p in out:
        if not clean or np.dot(p-clean[-1],p-clean[-1])>1e-24:clean.append(p)
    if len(clean)>1 and np.dot(clean[0]-clean[-1],clean[0]-clean[-1])<1e-24:clean.pop()
    return clean

def split(objects,values):
    global ACTIVE_LOD
    for name,value in values.items():
        if value.get('pattern'):value['pattern']['_cdlc_material']=name
    report=[]
    for ob in objects:
        if ob['bake']=='only' or ob['name'].startswith('Heel mat') or ob['part'] not in (1,11,12):continue
        source=bpy.data.objects[ob['name']];mesh=source.data;uv=mesh.uv_layers.get('DetailUV')
        if uv is None:continue
        mesh.calc_loop_triangles();source_uv={}
        for t in mesh.loop_triangles:
            positions=[source.matrix_world@mesh.vertices[i].co for i in t.vertices]
            source_uv[tuple(sorted(point_key(p) for p in positions))]={point_key(p):Vector(uv.data[li].uv) for p,li in zip(positions,t.loops)}
        verts=ob['verts'];tris=[];count=0
        for ids,normals,mat in ob['tris']:
            pattern=values[mat].get('pattern');pts=[verts[i] for i in ids]
            if pattern is None or not set(range(ob['first'],ob['last']+1)) & set(pattern['lods']):
                tris.append((ids,normals,mat));continue
            PATTERNED.add(mat)
            lookup=source_uv[tuple(sorted(point_key(p) for p in pts))]
            st=[lookup[point_key(p)] for p in pts]
            poly=[np.array((s.x,s.y,float(k==0),float(k==1)),dtype=float) for k,s in enumerate(st)]
            for axis in range(2):
                # Snap the UV coordinate itself, never move or delete a geometric edge.
                for p in poly:
                    if abs(p[axis]-round(p[axis]))<1e-5:p[axis]=round(p[axis])
            precise=np.array(pts,dtype=float);area_before=np.linalg.norm(np.cross(precise[1]-precise[0],precise[2]-precise[0]))*.5;area_after=0.
            for strip in partition(poly,0):
                for piece in partition(strip,1):
                    x=math.floor((min(p[0] for p in piece)+max(p[0] for p in piece))*.5)
                    y=math.floor((min(p[1] for p in piece)+max(p[1] for p in piece))*.5)
                    if any(not(-PAD<=p[0]-x<=1+PAD and -PAD<=p[1]-y<=1+PAD) for p in piece):raise ValueError('Pattern continuation exceeded its declared extent: '+str([(p[0]-x,p[1]-y) for p in piece]))
                    corners=[]
                    for q in piece:
                        weights=np.array((q[2],q[3],1-q[2]-q[3]));exact=weights@precise;pos=Vector(exact)
                        normal=Vector(weights@np.asarray(normals,dtype=float)).normalized()
                        corners.append((pos,normal,(q[0]-x,q[1]-y),exact))
                    # A convex piece can use any fan origin; select the one avoiding needle diagonals.
                    def quality(offset):
                        seq=corners[offset:]+corners[:offset];score=1.
                        for i in range(1,len(seq)-1):
                            aa,bb,cc=seq[0][0],seq[i][0],seq[i+1][0]
                            for p,q,r in ((aa,bb,cc),(bb,cc,aa),(cc,aa,bb)):
                                score=min(score,(q-p).cross(r-p).length/max((q-p).length*(r-p).length,1e-20))
                        return score
                    offset=max(range(len(corners)),key=quality);corners=corners[offset:]+corners[:offset]
                    for j in range(1,len(corners)-1):
                        tri=[corners[0],corners[j],corners[j+1]];points=[c[0] for c in tri]
                        if (points[1]-points[0]).cross(points[2]-points[0]).length_squared<1e-18:continue
                        area_after+=np.linalg.norm(np.cross(tri[1][3]-tri[0][3],tri[2][3]-tri[0][3]))*.5
                        indices=tuple(range(len(verts),len(verts)+3));verts.extend(points);tris.append((indices,[c[1] for c in tri],mat));count+=1
                        coords={point_key(c[0]):c[2] for c in tri}
                        if len(coords)!=3:raise ValueError('Native UV split produced an unresolved sub-micrometre edge: '+ob['name'])
                        for lod in set(range(ob['first'],ob['last']+1)) & set(pattern['lods']):
                            ACTIVE_LOD=lod;key=face_key(points,pattern)
                            if key in COORDS:
                                delta=[Vector(coords[p])-Vector(COORDS[key][p]) for p in coords]
                                shift=Vector(tuple(round(v) for v in delta[0]))
                                if any((v-shift).length>1e-4 for v in delta):raise ValueError('Inconsistent coincident UV triangles: '+ob['name']+' '+str(delta))
                            else:COORDS[key]=coords
            if abs(area_before-area_after)>max(1e-12,area_before*1e-8):raise ValueError('Pattern split changed geometric area: '+ob['name']+' '+str((area_before,area_after)))
        report.append({'object':ob['name'],'source_triangles':len(ob['tris']),'export_triangles':len(tris),'pattern_triangles':count})
        ob['tris']=tris
        print('PATTERN_SPLIT',ob['name'],len(tris),flush=True)
    ACTIVE_LOD=None
    return report

def faces_up(points,pattern):return face_key(points,pattern) in COORDS

def uvs(points,pattern,cell,size):
    coords=COORDS[face_key(points,pattern)];x,y,w=(int(round(v*size)) for v in cell)
    border=max(1,int(round(w/pattern_tiles.BORDER_SHARE)));inner=w-2*border
    return [((x+border+(coords[point_key(p)][0]+PAD)/(1+2*PAD)*inner)/size,
             (y+border+(coords[point_key(p)][1]+PAD)/(1+2*PAD)*inner)/size) for p in points]

def bake_lod(objects,values,sources,lod,*args,**kwargs):
    global ACTIVE_LOD
    ACTIVE_LOD=lod
    try:return original_lod(objects,values,sources,lod,*args,**kwargs)
    finally:ACTIVE_LOD=None

def install():
    pattern_tiles.faces_up=faces_up;pattern_tiles.uvs=uvs;pattern_tiles._resample=resample;bake_detail.bake_lod=bake_lod
