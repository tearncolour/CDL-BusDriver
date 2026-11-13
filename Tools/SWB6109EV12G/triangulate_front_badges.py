"""Bake valid vector polygons once; the model builder needs no extra packages.

Regeneration uses Shapely 2.2 / mapbox-earcut 2.1. The checked JSON contains
the resulting normalized vertices/triangles so normal Blender builds stay
independent of these authoring-only packages.
"""
from pathlib import Path
import sys,json
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'out/SWB6109EV12G/badge-tools'))
import numpy as np
import shapely
from shapely.geometry import Polygon,GeometryCollection
import mapbox_earcut

path=ROOT/'ModelSource/SWB6109EV12G/front-badge-paths.json'
data=json.loads(path.read_text(encoding='utf-8'))
only=set(sys.argv[sys.argv.index('--only')+1].split(',')) if '--only' in sys.argv else None
for key,group in data['groups'].items():
    if only and key not in only:continue
    geometry=GeometryCollection();invalid=0
    for points in group['paths']:
        p=Polygon(points)
        if not p.is_valid:invalid+=1;p=shapely.make_valid(p)
        geometry=geometry.symmetric_difference(p)
    geometry=geometry.buffer(0).simplify(.001,preserve_topology=True)
    polygons=list(geometry.geoms) if geometry.geom_type=='MultiPolygon' else [geometry]
    vertices=[];triangles=[];paths=[]
    for polygon in polygons:
        if polygon.area<1e-9:continue
        rings=[list(polygon.exterior.coords)[:-1]]+[list(r.coords)[:-1] for r in polygon.interiors]
        coords=[];ends=[]
        for ring in rings:
            coords+=ring;ends.append(len(coords));paths.append([[round(x,7),round(y,7)] for x,y in ring])
        indexes=mapbox_earcut.triangulate_float64(np.asarray(coords,dtype=np.float64),np.asarray(ends,dtype=np.uint32))
        base=len(vertices);vertices+=coords
        triangles += [[base+int(indexes[i+j]) for j in range(3)] for i in range(0,len(indexes),3)]
    group['paths']=paths;group['vertices_normalized']=vertices;group['triangles']=triangles
    group['polygon_repair']={'invalid_input_loops':invalid,'valid':geometry.is_valid,'area':geometry.area,'polygons':len(polygons)}
    group['contour_count']=len(paths);group['point_count']=sum(map(len,paths))
    print(key,group['polygon_repair'],len(vertices),len(triangles),flush=True)
data['triangulation']='Shapely 2.2 even-odd polygon normalization followed by mapbox-earcut 2.1, baked vertices/indices; no build-time dependency.'
path.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
