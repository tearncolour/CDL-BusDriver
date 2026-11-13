"""Trace only the physical S/arrow insert from the 3000px manufacturer photo.

The enclosing chrome frame is separately constructed in front_badges.py;
the low-resolution website header and its specular highlights are not used.
"""
from pathlib import Path
import json
import numpy as np
ROOT=Path(__file__).resolve().parents[2]
ns={'__file__':str(ROOT/'Tools/SWB6109EV12G/trace_front_badges.py')}
code=(ROOT/'Tools/SWB6109EV12G/trace_front_badges.py').read_text(encoding='utf-8')
exec(code[:code.index('front=pixels(')],ns)
image=ns['pixels']('sunwin-original-crop.png')
x,y,w,h=94,129,83,92
rgb=image[y:y+h,x:x+w,:3];grey=rgb.mean(axis=2)
alpha=np.clip((grey-.26)/.15,0,1)
# The inner chrome border enters the rectangular crop at its lower/right
# edges. It is separated from the arrow tips in the photograph: exclude
# those frame-only strips before connected-component extraction.
yy,xx=np.mgrid[y:y+h,x:x+w]
alpha[(yy>=217)|((xx>=173)&(yy>145))]=0
mask=alpha>.5;visited=np.zeros(mask.shape,bool);components=[]
for yy,xx in zip(*np.nonzero(mask)):
    if visited[yy,xx]:continue
    todo=[(int(yy),int(xx))];visited[yy,xx]=True;component=[]
    while todo:
        cy,cx=todo.pop();component.append((cy,cx))
        for dy,dx in [(0,1),(0,-1),(1,0),(-1,0)]:
            ny,nx=cy+dy,cx+dx
            if 0<=ny<h and 0<=nx<w and mask[ny,nx] and not visited[ny,nx]:
                visited[ny,nx]=True;todo.append((ny,nx))
    components.append(component)
component=max(components,key=len);gate=np.zeros(mask.shape,bool)
for cy,cx in component:
    gate[max(0,cy-1):min(h,cy+2),max(0,cx-1):min(w,cx+2)]=True
alpha*=gate
paths=ns['contours'](alpha)
p=ROOT/'ModelSource/SWB6109EV12G/front-badge-paths.json';data=json.loads(p.read_text(encoding='utf-8'))
data['groups']['sunwin']={
 'paths':paths,'source':'https://www.sunwinbus.com/UploadFile/image/20260804/20260804131306_5585.jpg',
 'source_crop_xywh':[1370+x,1480+y,w,h],
 'intermediate_crop':'references/sunwin-original-crop.png',
 'contour_count':len(paths),'point_count':sum(map(len,paths)),
 'scope':'Physical central S-shaped paired arrow only. Chrome rounded frame explicitly excluded and modelled independently.',
 'reference_photo_badge_outer_box':[1445,1603,116,108],
 'trace_threshold':'Grey linear blend 0.26..0.41; largest 4-connected component, preserving antialiased edge.',
 'reference_component_pixels':len(component)}
p.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
# Inspectable mask, not used as model texture.
bpy=ns['bpy'];im=bpy.data.images.new('Sunwin physical insert mask',w,h,alpha=True)
rgba=np.ones((h,w,4),dtype=np.float32);rgba[:,:,:3]=alpha[:,:,None];rgba[:,:,3]=1
im.pixels.foreach_set(rgba[::-1].ravel());im.filepath_raw=str(ROOT/'ModelSource/SWB6109EV12G/references/sunwin-physical-insert-mask.png');im.file_format='PNG';im.save()
print('PHYSICAL_S_TRACE',len(paths),sum(map(len,paths)),[len(c) for c in sorted(components,key=len,reverse=True)[:5]],flush=True)
