"""Deterministic vector outlines from identified reference artwork.

Run with Blender's Python. This writes vector paths, not a baked bus texture.
To regenerate the final checked set, follow this initial extraction with
refine_sunwin_badge.py (physical vehicle insert) and then
triangulate_front_badges.py (authoring-only polygon normalization).
"""
import bpy,json,math
from pathlib import Path
import numpy as np

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
REF=SRC/'references'

def pixels(name):
    im=bpy.data.images.load(str(REF/name),check_existing=False)
    w,h=im.size; arr=np.asarray(im.pixels[:],dtype=np.float32).reshape(h,w,4)[::-1].copy()
    bpy.data.images.remove(im)
    return arr

def upsample(a,n=4):
    h,w=a.shape;xx=np.arange(w);yy=np.arange(h)
    tmp=np.array([np.interp(np.arange(w*n)/n,xx,row) for row in a])
    return np.array([np.interp(np.arange(h*n)/n,yy,col) for col in tmp.T]).T

def rdp(points,epsilon):
    if len(points)<3:return points
    a=np.array(points[0],float);b=np.array(points[-1],float);d=b-a
    pp=np.array(points[1:-1]);t=np.clip((pp-a)@d/max(d@d,1e-12),0,1)
    distance=np.sqrt(((pp-(a+t[:,None]*d))**2).sum(axis=1));idx=int(distance.argmax())+1
    if distance[idx-1]<=epsilon:return [points[0],points[-1]]
    return rdp(points[:idx+1],epsilon)[:-1]+rdp(points[idx:],epsilon)

def contours(alpha):
    # Pixel edges with consistent winding also preserve holes in glyphs.
    a=upsample(alpha)>0.5; h,w=a.shape;edges={}
    def edge(p,q):edges.setdefault(p,[]).append(q)
    for y,x in zip(*np.nonzero(a)):
        x=int(x);y=int(y)
        if y==0 or not a[y-1,x]:edge((x,y),(x+1,y))
        if x==w-1 or not a[y,x+1]:edge((x+1,y),(x+1,y+1))
        if y==h-1 or not a[y+1,x]:edge((x+1,y+1),(x,y+1))
        if x==0 or not a[y,x-1]:edge((x,y+1),(x,y))
    paths=[];directions={(1,0):0,(0,1):1,(-1,0):2,(0,-1):3}
    while edges:
        start=next(iter(edges));p=start;q=edges[p].pop()
        if not edges[p]:del edges[p]
        points=[p,q]
        while q!=start:
            opts=edges[q];prev=directions[(q[0]-p[0],q[1]-p[1])]
            order={1:0,0:1,3:2,2:3}
            nxt=min(opts,key=lambda s:order[(directions[(s[0]-q[0],s[1]-q[1])]-prev)%4])
            opts.remove(nxt)
            if not opts:del edges[q]
            p,q=q,nxt;points.append(q)
        # Split at a distant point before simplifying a closed loop.
        if len(points)<12:continue
        far=max(range(1,len(points)-1),key=lambda i:(points[i][0]-start[0])**2+(points[i][1]-start[1])**2)
        poly=rdp(points[:far+1],.72)[:-1]+rdp(points[far:],.72)[:-1]
        area=sum(p[0]*q[1]-q[0]*p[1] for p,q in zip(poly,poly[1:]+poly[:1]))/2
        if abs(area)<5:continue
        paths.append([[round(x/w-.5,6),round(.5-y/h,6)] for x,y in poly])
    return paths

front=pixels('official-front-5585-original-3000.jpg')
official=pixels('sunwin-official-logo.png')
jiushi=pixels('jiushi-logo-commons.jpg')
groups={}
def save(name,alpha,source,box):
    paths=contours(alpha)
    groups[name]={'paths':paths,'source':source,'source_crop_xywh':box,
                  'contour_count':len(paths),'point_count':sum(map(len,paths))}

# The website's transparent mark is the same rounded-square double-arrow S
# visible on the 3000 px photograph, rather than an invented ellipse/slash.
save('sunwin',official[:,:70,3],
     'https://www.sunwinbus.com/img/carLogo.png',[0,0,70,len(official)])
_word=official[:,80:,3];_ys,_xs=np.nonzero(_word>.5)
_x0,_x1=int(_xs.min()),int(_xs.max())+1;_y0,_y1=int(_ys.min()),int(_ys.max())+1
save('sunwin_wordmark',_word[_y0:_y1,_x0:_x1],
     'https://www.sunwinbus.com/img/carLogo.png',[80+_x0,_y0,_x1-_x0,_y1-_y0])

# Official photograph confirms these exact proprietary letter shapes and
# the same triangular Jiushi mark. Commons artwork is used only to obtain
# its cleaner outlines; the bus-specific spacing/size comes from the photo.
save('jiushi_triangle',(1-jiushi[248:447,4:229,:3].mean(axis=2))*.99,
     'https://commons.wikimedia.org/wiki/File:久事公交集团logo.jpg',[4,248,225,199])
save('jiushi_wordmark',1-jiushi[276:396,268:788,:3].mean(axis=2),
     'https://commons.wikimedia.org/wiki/File:久事公交集团logo.jpg',[268,276,520,120])

# Isolate white ink inside the blue circular field. Fixed crops and the
# circular gate exclude white bodywork without any hand-drawn pictogram.
for name,box,centre,radius in [
    ('elder',(731,1545,90,104),(43,51),46),
    ('wheelchair',(822,1570,98,105),(49,52),47)]:
    x,y,w,h=box;rgb=front[y:y+h,x:x+w,:3];yy,xx=np.mgrid[:h,:w]
    light=rgb.mean(axis=2);sat=rgb.max(axis=2)-rgb.min(axis=2)
    gate=((xx-centre[0])/radius)**2+((yy-centre[1])/radius)**2<.90**2
    alpha=np.clip((light-.30)/.27,0,1)*np.clip((.25-sat)/.12,0,1)*gate
    save(name,alpha,'https://www.sunwinbus.com/UploadFile/image/20260804/20260804131306_5585.jpg',list(box))

report={'model':'SWB6109EV12G','method':'Deterministic raster alpha/ink contour trace, 4x interpolated boundary, 0.18 source-pixel simplification; no typeface substitution.',
        'normalized_coordinates':'x and y centred on the fixed reference crop, y up; full crop width/height are 1.',
        'groups':groups}
(SRC/'front-badge-paths.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

# An inspectable editable SVG, with five separate groups and correct holes.
svg=['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1100 250">','<rect width="1100" height="250" fill="#888"/>']
for i,(name,g) in enumerate(groups.items()):
    x=i*220+110;y=115;scale=160
    svg.append(f'<g id="{name}" transform="translate({x},{y}) scale({scale}, {-scale})">')
    if name in ('elder','wheelchair'):svg.append('<ellipse rx=".5" ry=".5" fill="#092b69"/>')
    if name=='sunwin':svg.append('<rect x="-.5" y="-.5" width="1" height="1" rx=".1" fill="#111"/>')
    d=' '.join('M '+' L '.join(f'{a},{b}' for a,b in p)+' Z' for p in g['paths'])
    svg.append(f'<path fill="#eee" fill-rule="evenodd" d="{d}"/></g>')
    svg.append(f'<text x="{x}" y="230" font-size="16" text-anchor="middle">{name}</text>')
svg.append('</svg>');(SRC/'front-badge-vector-proof.svg').write_text('\n'.join(svg),encoding='utf-8')
print('BADGE_TRACE '+json.dumps({k:[v['contour_count'],v['point_count']] for k,v in groups.items()}),flush=True)
