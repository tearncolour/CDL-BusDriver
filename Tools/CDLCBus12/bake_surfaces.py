"""Bake periodic high meshes to tangent-space surface tiles; keep high meshes in the saved source."""
import argparse, hashlib, json, math, sys
from pathlib import Path
import bpy
import numpy as np


def main():
    ap = argparse.ArgumentParser(); ap.add_argument('--out', type=Path, required=True)
    args = ap.parse_args(sys.argv[sys.argv.index('--') + 1:]); out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    scene = bpy.context.scene; scene.render.engine = 'CYCLES'; scene.cycles.samples = 8
    scene.render.bake.use_selected_to_active = True; scene.render.bake.use_cage = False
    scene.render.bake.cage_extrusion = .002; scene.render.bake.max_ray_distance = .004; scene.render.bake.margin = 0
    records = []; period = .08; grid = 256; padding = 4; high_grid = grid + 2*padding; size = 512
    for ordinal, name in enumerate(('polymer', 'woven', 'brushed', 'leather')):
        # Continue the periodic high surface beyond the receiver so edge vertex normals see both neighbours.
        samples=np.arange(-padding,grid+padding+1,dtype=float)/grid
        x, y = np.meshgrid(samples,samples)
        if name == 'polymer':
            z = sum(.000016 * np.sin(2 * math.pi * (a*x+b*y) + phase) for a,b,phase in
                    ((7,9,.1),(13,-8,.8),(19,5,1.4),(-11,21,2.2),(29,17,.5),(31,-27,1.1)))
        elif name == 'woven':
            z = .000065 * (np.cos(2*math.pi*32*x)*(.65+.35*np.cos(2*math.pi*16*y)) +
                          np.cos(2*math.pi*32*y)*(.65-.35*np.cos(2*math.pi*16*x)))
        elif name == 'brushed':
            z = .000025*np.sin(2*math.pi*61*x) + .000015*np.sin(2*math.pi*(43*x+y))
        else:
            # Periodic irregular grain: 3.2 mm padded cells, 0.16 mm shallow relief.
            cells=25;rng=np.random.default_rng(12);seeds=rng.uniform(.2,.8,(cells,cells,2))
            qx,qy=x*cells,y*cells;ix,iy=np.floor(qx).astype(int),np.floor(qy).astype(int);dist=[]
            for oy in (-1,0,1):
                for ox in (-1,0,1):
                    seed=seeds[(iy+oy)%cells,(ix+ox)%cells]
                    dist.append((qx-ix-ox-seed[:,:,0])**2+(qy-iy-oy-seed[:,:,1])**2)
            nearest=np.sort(np.asarray(dist),axis=0)[:2]
            gap=np.sqrt(nearest[1])-np.sqrt(nearest[0])
            z=.00004-.00016*np.exp(-(gap/.18)**2)
        verts = np.stack((x*period,y*period,z),axis=-1).reshape(-1,3).tolist(); faces=[]
        for j in range(high_grid):
            for i in range(high_grid):
                a=j*(high_grid+1)+i; faces.append((a,a+1,a+high_grid+2,a+high_grid+1))
        highmesh=bpy.data.meshes.new(name+' high'); highmesh.from_pydata(verts,[],faces); highmesh.update()
        high=bpy.data.objects.new(name+' high bake source',highmesh); scene.collection.objects.link(high)
        for p in highmesh.polygons:p.use_smooth=True
        lowmesh=bpy.data.meshes.new(name+' low');lowmesh.from_pydata([(0,0,0),(period,0,0),(period,period,0),(0,period,0)],[],[(0,1,2,3)])
        low=bpy.data.objects.new(name+' low receiver',lowmesh);scene.collection.objects.link(low)
        uv=lowmesh.uv_layers.new(name='SurfaceUV')
        for loop,co in zip(uv.data,((0,0),(1,0),(1,1),(0,1))):loop.uv=co
        material=bpy.data.materials.new(name);material.use_nodes=True;low.data.materials.append(material)
        image=bpy.data.images.new(name+' normal',width=size,height=size,alpha=False);image.colorspace_settings.name='Non-Color'
        image.generated_color=(.5,.5,1,1);nt=material.node_tree;tex=nt.nodes.new('ShaderNodeTexImage');tex.image=image;nt.nodes.active=tex
        bpy.ops.object.select_all(action='DESELECT');high.hide_set(False);high.hide_render=False;high.select_set(True);low.select_set(True)
        bpy.context.view_layer.objects.active=low
        bpy.ops.object.bake(type='NORMAL',normal_space='TANGENT',normal_r='POS_X',normal_g='POS_Y',normal_b='POS_Z')
        pixels=np.array(image.pixels[:],dtype=np.float32).reshape(size,size,4)
        changed=int(np.count_nonzero(np.linalg.norm(pixels[:,:,:2]-.5,axis=2)>.02))
        if changed<size*size*.2 or not np.isfinite(pixels).all():raise RuntimeError('Invalid or empty bake '+name)
        bsdf=next(n for n in nt.nodes if n.type=='BSDF_PRINCIPLED')
        normal=nt.nodes.new('ShaderNodeNormalMap');nt.links.new(tex.outputs['Color'],normal.inputs['Color']);nt.links.new(normal.outputs['Normal'],bsdf.inputs['Normal'])
        path=out/(name+'-Normal.png');image.filepath_raw=str(path);image.file_format='PNG';image.save();image.pack()
        # HDRP detail: R albedo, G normal Y, B smoothness, A normal X. Neutral tone/smoothness are .5.
        packed=np.empty_like(pixels);packed[:,:,0]=.5;packed[:,:,1]=pixels[:,:,1];packed[:,:,2]=.5;packed[:,:,3]=pixels[:,:,0]
        detail=bpy.data.images.new(name+' HDRP detail',width=size,height=size,alpha=True);detail.colorspace_settings.name='Non-Color'
        detail.pixels.foreach_set(packed.ravel());detail.filepath_raw=str(out/(name+'-Detail.png'));detail.file_format='PNG';detail.save();detail.pack()
        high.hide_render=True;high.hide_set(True);low.location.x=ordinal*.1;high.location.x=ordinal*.1
        records.append({'name':name,'period_m':period,'resolution':size,'high_triangles':high_grid*high_grid*2,'source_padding_m':padding*period/grid,'relief_range_m':[float(z.min()),float(z.max())],
                        'changed_pixels':changed,'normal_sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'cage_m':.002,'max_ray_m':.004,
                        'space':'tangent +Y linear','hdrp_channels':'R=.5 G=normalY B=.5 A=normalX','game_validated':False})
    bpy.ops.wm.save_as_mainfile(filepath=str(out/'SurfaceBakeSources.blend'))
    (out/'bake-report.json').write_text(json.dumps(records,indent=2),encoding='utf-8')

if __name__=='__main__':main()
