"""Read-only source/atlas audit for the user-selected tint and curtain removal.

Blender -b --python-exit-code 1 --python this.py -- --blend final.blend
    --report report.json [--before-blend previous.blend] [--source-only]

verify_master(master) checks only memory and is safe before saving/exporting.
CLI additionally reads existing PNGs and derived glass UVs unless --source-only.
No source, export, package, shader setting or game state is written.
"""
import argparse
from array import array
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import struct
import sys
import zlib
import bpy

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
from source_coordinates import canonical_world_matrix
from lod_geometry import flatten_lod

MASTER = 'SWB6109EV12G | editable components'
BASE = '07 Solar glass | separate Gls export'
SIDE = '07 Solar glass | side windows lightly tinted'
BASE_RGB = (.40, .52, .51)
SIDE_RGB = (.30, .39, .3825)
SIDE_NAMES = {f'Side glazing -1 {i}' for i in range(7)} | {f'Side glazing 1 {i}' for i in range(5)}
OTHER_NAMES = {'Front continuous glazing', 'Rear continuous glazing', 'Driver rear partition glazing',
               'Door 1.1 glazing', 'Door 1.2 glazing', 'Door 2.1 glazing', 'Door 2.2 glazing'}
CURTAIN = re.compile(r'Photo cabin \| -?1 window \d+ (?:curtain track|gathered curtain|tie band|hanging clip [123])')
WINDOW_LABELS = [f'-1 window {i}' for i in range(1, 7)] + [f'1 window {i}' for i in range(5)]
REMOVED_NAMES = {f'Photo cabin | {label} {part}' for label in WINDOW_LABELS
                 for part in ('curtain track', 'gathered curtain', 'tie band', 'hanging clip 1', 'hanging clip 2', 'hanging clip 3')}
OPTICS = {'Alpha': 1., 'Roughness': .045, 'Metallic': 0., 'Transmission Weight': .92,
          'IOR': 1.46, 'Coat Weight': .65}


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def close(a, b, tolerance=1e-6):
    if isinstance(a, (list, tuple)) and isinstance(b, (list, tuple)):
        return len(a) == len(b) and all(close(x, y, tolerance) for x, y in zip(a, b))
    return abs(float(a)-float(b)) <= tolerance


def value(v):
    if isinstance(v, (str, int, float, bool)) or v is None:
        return v
    try:
        return [value(x) for x in v]
    except TypeError:
        return str(v)


def material_record(mat):
    if mat is None:
        return None
    bs = [n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'] if mat.use_nodes else []
    return {'name': mat.name, 'diffuse': list(mat.diffuse_color),
            'glass': bool(mat.get('glass')), 'light_id': int(mat.get('light_id', 0)),
            'livery_control': bool(mat.get('livery_control')), 'principled_count': len(bs),
            'inputs': {s.name: value(s.default_value) for s in bs[0].inputs if hasattr(s, 'default_value')} if len(bs) == 1 else {},
            'linked_inputs': [s.name for s in bs[0].inputs if s.is_linked] if len(bs) == 1 else []}


def verify_master(master):
    checks = []
    def check(name, ok, detail=None):
        checks.append({'check': name, 'passed': bool(ok), 'detail': detail})
    objects = list(master.all_objects)
    by_name = {o.name: o for o in objects}
    glasses = {o.name: o for o in objects if o.type in {'MESH', 'CURVE', 'FONT'}
               and any(m and m.get('glass') for m in o.data.materials)}
    check('exact 19 glass objects', set(glasses) == SIDE_NAMES | OTHER_NAMES,
          {'missing': sorted((SIDE_NAMES | OTHER_NAMES)-set(glasses)), 'unexpected': sorted(set(glasses)-(SIDE_NAMES | OTHER_NAMES))})
    rows = []
    for name in sorted(SIDE_NAMES | OTHER_NAMES):
        obj = by_name.get(name)
        actual = [m.name if m else None for m in obj.data.materials] if obj and obj.type == 'MESH' else []
        expected = SIDE if name in SIDE_NAMES else BASE
        check(name+' material route', actual == [expected], {'actual': actual, 'expected': expected})
        rows.append({'object': name, 'side_window': name in SIDE_NAMES, 'materials': actual})
    remnants = [o.name for o in objects if 'curtain' in o.name.lower() or CURTAIN.fullmatch(o.name)]
    check('no curtains, tracks, ties or hanging clips', not remnants, remnants)
    materials = {}
    for name, rgb in ((BASE, BASE_RGB), (SIDE, SIDE_RGB)):
        mat = bpy.data.materials.get(name)
        row = material_record(mat)
        materials[name] = row
        check(name+' material exists', row is not None)
        if row is None:
            continue
        check(name+' diffuse and BSDF RGB', close(row['diffuse'], (*rgb, 1.))
              and close(row['inputs'].get('Base Color', ()), (*rgb, 1.)), row)
        check(name+' glass optical contract', row['principled_count'] == 1
              and all(k in row['inputs'] and close(row['inputs'][k], v) for k, v in OPTICS.items())
              and not row['linked_inputs'] and row['glass'] and row['light_id'] == 0 and not row['livery_control'])
    if all(materials.values()):
        base_inputs = {k:v for k,v in materials[BASE]['inputs'].items() if k != 'Base Color'}
        side_inputs = {k:v for k,v in materials[SIDE]['inputs'].items() if k != 'Base Color'}
        check('all non-colour BSDF socket defaults unchanged between glass types', base_inputs == side_inputs)
    failed = [c for c in checks if not c['passed']]
    return {'status': 'FAIL' if failed else 'PASS', 'checks': len(checks), 'failed': len(failed),
            'scope': '12 fixed side panes tinted; other seven glass objects unchanged; all 66 named curtain attachments absent. Colour is a user preference, not measured transmission.',
            'expected_native_alpha': .28, 'source_checks': checks, 'glass_objects': rows, 'materials': materials,
            'not_tested': ['In-game appearance, mirror visibility/reflection and loaded SurfaceAsset compatibility',
                           'Road driving or optical transmittance measurement']}


def png(path):
    data = Path(path).read_bytes()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', 'Invalid PNG signature: '+str(path)
    width, height, depth, kind = struct.unpack('>IIBB', data[16:26])
    assert (width, height, depth, kind) == (64, 64, 8, 6), (path, '64x64 RGBA8 required')
    compressed = bytearray(); pos = 8
    while pos < len(data):
        n = struct.unpack_from('>I', data, pos)[0]
        typ = data[pos+4:pos+8]; chunk = data[pos+8:pos+8+n]
        crc = struct.unpack_from('>I', data, pos+8+n)[0]
        assert zlib.crc32(typ+chunk) & 0xffffffff == crc, (path, 'PNG CRC')
        if typ == b'IDAT': compressed.extend(chunk)
        pos += n+12
    raw = zlib.decompress(compressed); stride = width*4; rows=[]; pos=0; previous=bytearray(stride)
    for _ in range(height):
        filt = raw[pos]; row=bytearray(raw[pos+1:pos+1+stride]); pos += stride+1
        for i in range(stride):
            a=row[i-4] if i>=4 else 0; b=previous[i]; c=previous[i-4] if i>=4 else 0
            if filt==0: predictor=0
            elif filt==1: predictor=a
            elif filt==2: predictor=b
            elif filt==3: predictor=(a+b)//2
            elif filt==4:
                p=a+b-c; distances=(abs(p-a),abs(p-b),abs(p-c))
                predictor=(a,b,c)[distances.index(min(distances))]
            else: raise ValueError('Unknown PNG filter')
            row[i]=(row[i]+predictor)&255
        rows.append(row); previous=row
    assert pos == len(raw), 'Unexpected PNG scanline bytes'
    return rows


def expected_srgb(rgb):
    return [round(255*(12.92*v if v<=.0031308 else 1.055*v**(1/2.4)-.055)) for v in rgb]


def verify_atlases(directory):
    checks=[]; files=[]; first={}
    for lod in range(3):
        stem='SWB6109EV12G'+(f'_LOD{lod}' if lod else '')+'_Gls'
        maps={}
        for kind in ('BaseColor', 'MaskMap', 'Normal'):
            path=Path(directory)/(stem+'_'+kind+'.png')
            maps[kind]=png(path)
            h=digest(path); files.append({'path':str(path.resolve()),'sha256':h})
            if lod: checks.append({'check':stem+' '+kind+' matches LOD0', 'passed':h==first[kind]})
            else: first[kind]=h
        bad=[]
        for y in range(64):
            for x in range(64):
                tile=0 if y>=60 and x<4 else 1 if y>=60 and 4<=x<8 else None
                expected_base=expected_srgb(BASE_RGB if tile==0 else SIDE_RGB)+[71] if tile is not None else [3,3,3,255]
                expected_mask=[0,255,0,244 if tile is not None else 77]
                for kind, expected in (('BaseColor', expected_base), ('MaskMap', expected_mask), ('Normal',[128,128,255,255])):
                    actual=list(maps[kind][y][4*x:4*x+4])
                    if actual != expected:
                        bad.append({'x':x,'png_y_top_down':y,'map':kind,'actual':actual,'expected':expected})
        checks.append({'check':stem+' all pixels follow two-tile contract', 'passed':not bad,
                       'mismatch_count':len(bad), 'first_mismatches':bad[:12]})
    return {'status':'PASS' if all(c['passed'] for c in checks) else 'FAIL', 'checks':checks,'files':files,
            'tile_layout':{'base':{'index':0,'uv_center':[.03125,.03125],'rgba':expected_srgb(BASE_RGB)+[71]},
                           'side':{'index':1,'uv_center':[.09375,.03125],'rgba':expected_srgb(SIDE_RGB)+[71]}},
            'mask_contract':'Both occupied tiles R0/G255(AO)/B0/A244; coat remains separate .65. Default unused padding smoothness 77. Flat linear normals unchanged.'}


def flattened(obj):
    deps=bpy.context.evaluated_depsgraph_get()
    data=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
    data.transform(canonical_world_matrix(obj))
    return data


def triangle_key(data, indices):
    return struct.pack('<9f', *(v for point in sorted(tuple(data.vertices[i].co) for i in indices) for v in point))


def verify_export_uvs(master):
    objects={o.name:o for o in list(master.all_objects)}
    rows=[]
    for lod in range(3):
        expected={}; expected_counts=Counter()
        for name in sorted(SIDE_NAMES|OTHER_NAMES):
            obj=objects.get(name)
            if obj is None: continue
            data=flatten_lod(obj,lod,flattened)
            try:
                data.calc_loop_triangles()
                tile=1 if name in SIDE_NAMES else 0
                for tri in data.loop_triangles:
                    points=[data.vertices[i].co for i in tri.vertices]
                    if (points[1]-points[0]).cross(points[2]-points[0]).length_squared<1e-18: continue
                    key=triangle_key(data,tri.vertices)
                    assert key not in expected or expected[key]==tile,'Overlapping source glass of different tint'
                    expected[key]=tile; expected_counts[(key,tile)]+=1
            finally: bpy.data.meshes.remove(data)
        name='SWB6109EV12G'+(f'_LOD{lod}' if lod else '')+'_Gls'
        obj=bpy.data.objects.get(name); failures=[]; counts=Counter(); actual_counts=Counter()
        if obj is None or obj.type!='MESH' or not obj.data.uv_layers.active:
            failures.append({'reason':'missing saved derived mesh or UVs','object':name})
        else:
            data=obj.data; data.calc_loop_triangles(); uv=data.uv_layers.active.data
            offsets=((- .018,- .018),(.018,- .018),(0.,.018))
            for tri in data.loop_triangles:
                key=triangle_key(data,tri.vertices); tile=expected.get(key)
                if tile is None:
                    failures.append({'triangle':tri.index,'reason':'not an exact source glass triangle'}); continue
                centre=((tile+.5)/16,.5/16)
                expected_uv=sorted((centre[0]+a,centre[1]+b) for a,b in offsets)
                actual_uv=sorted(tuple(uv[i].uv) for i in tri.loops)
                if not close(actual_uv,expected_uv):
                    failures.append({'triangle':tri.index,'tile':tile,'actual_uv':actual_uv,'expected_uv':expected_uv})
                actual_counts[(key,tile)]+=1; counts[tile]+=1
            if actual_counts!=expected_counts:
                failures.append({'reason':'export source triangle multiset mismatch','expected':sum(expected_counts.values()),'actual':sum(actual_counts.values())})
        rows.append({'lod':lod,'mesh':name,'status':'PASS' if not failures else 'FAIL',
                     'triangles_by_tile':dict(counts),'failures':len(failures),'first_failures':failures[:10]})
    return {'status':'PASS' if all(r['status']=='PASS' for r in rows) else 'FAIL','lods':rows,
            'scope':'Every saved derived glass triangle is matched to its evaluated canonical source and exact expected UV tile in each LOD; FBX reimport and native packed UVs require separate asset checks.'}


def append_array(h, collection, field, typecode, width):
    values=array(typecode,[0])*(len(collection)*width)
    if values: collection.foreach_get(field,values); h.update(values.tobytes())


def snapshot(master):
    scene=bpy.context.scene; old=scene.frame_current; objects=list(master.all_objects); records={}
    try:
        scene.frame_set(0); bpy.context.view_layer.update()
        for obj in objects:
            h=hashlib.sha256()
            h.update(json.dumps([obj.type,obj.parent.name if obj.parent else None,int(obj.get('cdlc_part',-1)),
                                 [[float(x) for x in row] for row in obj.matrix_basis],
                                 [[float(x) for x in row] for row in obj.matrix_parent_inverse]],sort_keys=True).encode())
            if obj.type in {'MESH','CURVE','FONT'}:
                data=flattened(obj)
                try:
                    for coll, field, kind, width in ((data.vertices,'co','f',3),(data.loops,'vertex_index','i',1),
                         (data.polygons,'loop_total','i',1),(data.polygons,'material_index','i',1),
                         (data.polygons,'use_smooth','b',1),(data.corner_normals,'vector','f',3)):
                        append_array(h,coll,field,kind,width)
                finally: bpy.data.meshes.remove(data)
                mats=[material_record(m) for m in obj.data.materials]
            else: mats=[]
            records[obj.name]={'geometry_sha256':h.hexdigest(),'materials':mats,'pose_matrices':[]}
        for frame in (0,25,30,40):
            scene.frame_set(frame); bpy.context.view_layer.update()
            for obj in objects:
                records[obj.name]['pose_matrices'].append([float(v) for row in obj.matrix_world for v in row])
    finally:
        scene.frame_set(old); bpy.context.view_layer.update()
    return records


def compare_before(before, after):
    removed=set(before)-set(after); added=set(after)-set(before); geometry=[]; materials=[]; poses=[]
    for name in sorted(set(before)&set(after)):
        a,b=before[name],after[name]
        if a['geometry_sha256']!=b['geometry_sha256']: geometry.append(name)
        if a['pose_matrices']!=b['pose_matrices']: poses.append(name)
        if name in SIDE_NAMES:
            x,y=a['materials'],b['materials']
            if len(x)!=1 or len(y)!=1: materials.append(name); continue
            x=dict(x[0]); y=dict(y[0]); x.pop('name'); y.pop('name'); x.pop('diffuse'); y.pop('diffuse')
            x['inputs']=dict(x['inputs']); y['inputs']=dict(y['inputs'])
            x['inputs'].pop('Base Color'); y['inputs'].pop('Base Color')
            if x!=y: materials.append(name)
        elif a['materials']!=b['materials']: materials.append(name)
    passed=removed==REMOVED_NAMES and not added and not geometry and not materials and not poses
    return {'status':'PASS' if passed else 'FAIL','removed_objects':sorted(removed),'expected_removed_count':66,
            'added_objects':sorted(added),'retained_objects':len(set(before)&set(after)),
            'unexpected_geometry_changes':geometry,'unexpected_material_changes':materials,'unexpected_pose_changes':poses,
            'scope':'Exact retained evaluated canonical geometry/normals/indices at frame0 and world matrices at 0/25/30/40; only 12 side-pane colour/material-name differences allowed. Does not prove every intermediate animation sample.'}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend',type=Path,required=True);parser.add_argument('--report',type=Path,required=True)
    parser.add_argument('--before-blend',type=Path);parser.add_argument('--source-only',action='store_true')
    parser.add_argument('--assets',type=Path,default=ROOT/'Asset/SWB6109EV12G')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    protected={str(p.resolve()):digest(p) for p in (args.blend,args.before_blend) if p}
    before=None
    if args.before_blend:
        bpy.ops.wm.open_mainfile(filepath=str(args.before_blend.resolve()))
        before=snapshot(bpy.data.collections[MASTER])
    bpy.ops.wm.open_mainfile(filepath=str(args.blend.resolve()))
    bpy.context.scene.frame_set(0);bpy.context.view_layer.update();master=bpy.data.collections[MASTER]
    result=verify_master(master)
    if before is not None: result['before_after']=compare_before(before,snapshot(master))
    if not args.source_only:
        result['atlas']=verify_atlases(args.assets);result['derived_glass_uvs']=verify_export_uvs(master)
    result.update(source=str(args.blend.resolve()),source_sha256=protected[str(args.blend.resolve())],
                  verifier_sha256=digest(__file__),before_sha256=protected.get(str(args.before_blend.resolve())) if args.before_blend else None)
    if any(result.get(k,{}).get('status')=='FAIL' for k in ('before_after','atlas','derived_glass_uvs')): result['status']='FAIL'
    assert all(digest(p)==h for p,h in protected.items()),'Read-only source file changed during validation'
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('WINDOW_CONFIGURATION',result['status'],json.dumps({'source_checks':result['checks'],'source_failed':result['failed']}),flush=True)
    if result['status']!='PASS':raise SystemExit(1)


if __name__=='__main__':main()
