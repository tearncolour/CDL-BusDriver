"""Assign only the authored aqua material's runtime metadata, with a full delta proof.

Run in Blender after freezing the old source/resources. Native BSDF, FBX and
textures are never regenerated. The normal SWB1 writer regenerates all LODs
into staging; byte comparison must prove that only Exterior 80->83 changes.
"""
import argparse
import ast
from array import array
import gzip
import hashlib
import json
import math
from pathlib import Path
import shutil
import sys
from types import SimpleNamespace
import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
SRC = ROOT/'ModelSource/SWB6109EV12G'
TARGET = '02 Shanghai aqua twin stripe'
ALLOWED = {'cdlc_palette_swatch','cdlc_runtime_finish',
           'cdlc_material_contract_revision','cdlc_runtime_material_approximation'}
sys.path.insert(0,str(TOOLS))
from material_contract import apply_material_contract
from verify_runtime_palette_delta import compare_bytes
from verify_palette_json_delta import compare as compare_palette_json
from source_coordinates import canonical_export_context


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def value(item):
    if item is None or isinstance(item,(str,int,float,bool)):
        return item
    if isinstance(item,bpy.types.ID):
        return {'id_type':item.bl_rna.identifier,'name':item.name}
    if hasattr(item,'to_dict'):
        return {str(k):value(v) for k,v in item.to_dict().items()}
    try:
        return [value(v) for v in item]
    except TypeError:
        raise TypeError('Unsupported snapshot value '+type(item).__name__)


def props(item,ignored=()):
    return {key:value(item[key]) for key in sorted(item.keys()) if key not in ignored}


def simple_rna(item):
    result={}
    for prop in item.bl_rna.properties:
        # session_uid is deliberately reallocated on every file open; it is
        # not stored geometry, animation or material state. A read-only audit
        # records this exact difference before excluding it from persistence.
        if prop.identifier in {'rna_type','id_data','session_uid'} or prop.type not in {'BOOLEAN','INT','FLOAT','STRING','ENUM'}:
            continue
        val=getattr(item,prop.identifier)
        result[prop.identifier]=sorted(val) if isinstance(val,set) else value(val)
    return result


def nested_rna(item,depth=0):
    if depth>12:
        raise RuntimeError('Snapshot RNA recursion exceeded: '+item.bl_rna.identifier)
    result=simple_rna(item)
    for prop in item.bl_rna.properties:
        if prop.identifier in {'rna_type','id_data'}:
            continue
        val=getattr(item,prop.identifier)
        if prop.type=='COLLECTION':
            result[prop.identifier]=[value(v) if isinstance(v,bpy.types.ID) else nested_rna(v,depth+1) for v in val]
        elif prop.type=='POINTER' and (val is None or isinstance(val,bpy.types.ID)):
            result[prop.identifier]=value(val)
    return result


def digest(item):
    return hashlib.sha256(json.dumps(item,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()).hexdigest()


def bulk(collection,field,code,count):
    data=array(code,[0])*(len(collection)*count)
    if data:
        collection.foreach_get(field,data)
    return hashlib.sha256(data.tobytes()).hexdigest()


def mesh_state(data):
    result={'name':data.name,'settings':simple_rna(data),'props':props(data),
        'materials':[value(m) for m in data.materials],
        'vertices':bulk(data.vertices,'co','f',3),
        'edges':bulk(data.edges,'vertices','i',2),
        'loops':bulk(data.loops,'vertex_index','i',1),
        'loop_start':bulk(data.polygons,'loop_start','i',1),
        'loop_total':bulk(data.polygons,'loop_total','i',1),
        'material_index':bulk(data.polygons,'material_index','i',1),
        'smooth':bulk(data.polygons,'use_smooth','b',1),
        'corner_normals':bulk(data.corner_normals,'vector','f',3),
        'uv_layers':{},'attributes':{}}
    for layer in data.uv_layers:
        result['uv_layers'][layer.name]=bulk(layer.data,'uv','f',2)
    for attr in data.attributes:
        record={'type':attr.data_type,'domain':attr.domain,'count':len(attr.data),'fields':{}}
        if attr.data:
            for prop in attr.data[0].bl_rna.properties:
                if prop.type in {'BOOLEAN','INT','FLOAT'}:
                    count=prop.array_length if prop.is_array else 1
                    record['fields'][prop.identifier]=bulk(attr.data,prop.identifier,'f' if prop.type=='FLOAT' else 'i',count)
        result['attributes'][attr.name]=record
    if data.shape_keys:
        result['shape_keys']=[{'name':block.name,'value':block.value,'coordinates':bulk(block.data,'co','f',3)} for block in data.shape_keys.key_blocks]
    return result


def material_state(mat):
    state={'settings':simple_rna(mat),'props':props(mat,ALLOWED if mat.name==TARGET else ())}
    if mat.node_tree:
        state['nodes']=[]
        for node in mat.node_tree.nodes:
            state['nodes'].append({'settings':simple_rna(node),'props':props(node),
                'image':value(getattr(node,'image',None)),
                'node_tree':value(getattr(node,'node_tree',None)),
                'inputs':[{'id':s.identifier,'value':value(s.default_value) if hasattr(s,'default_value') else None} for s in node.inputs],
                'outputs':[{'id':s.identifier,'value':value(s.default_value) if hasattr(s,'default_value') else None} for s in node.outputs]})
        state['links']=[(x.from_node.name,x.from_socket.identifier,x.to_node.name,x.to_socket.identifier) for x in mat.node_tree.links]
    return state


def snapshot(master):
    objects={}
    for ob in bpy.data.objects:
        objects[ob.name]={'type':ob.type,'data':value(ob.data),'parent':value(ob.parent),
            'settings':simple_rna(ob),'props':props(ob),'collections':sorted(c.name for c in ob.users_collection),
            'matrix_world':value(ob.matrix_world),'matrix_basis':value(ob.matrix_basis),
            'matrix_parent_inverse':value(ob.matrix_parent_inverse),
            'modifiers':[nested_rna(m) for m in ob.modifiers],
            'constraints':[nested_rna(c) for c in ob.constraints],
            'animation':nested_rna(ob.animation_data) if ob.animation_data else None,
            'material_slots':[(s.link,value(s.material)) for s in ob.material_slots]}
    meshes={m.name:mesh_state(m) for m in bpy.data.meshes}
    curves={c.name:nested_rna(c) for c in bpy.data.curves}
    actions={a.name:nested_rna(a) for a in bpy.data.actions}
    materials={m.name:material_state(m) for m in bpy.data.materials}
    images={im.name:{'path':im.filepath,'colorspace':im.colorspace_settings.name,
        'size':list(im.size),'packed_sha256':hashlib.sha256(im.packed_file.data).hexdigest() if im.packed_file else None} for im in bpy.data.images}
    scenes={s.name:{'props':props(s),'frame':s.frame_current,'world':value(s.world),'camera':value(s.camera)} for s in bpy.data.scenes}
    # Real posed matrices at closed/half/open supplement complete action data.
    scene=bpy.context.scene
    original=scene.frame_current
    motion={}
    for frame in (0,30,40):
        scene.frame_set(frame);bpy.context.view_layer.update()
        motion[str(frame)]={ob.name:value(ob.matrix_world) for ob in master.all_objects}
    scene.frame_set(original);bpy.context.view_layer.update()
    components={'objects':objects,'meshes':meshes,'curves':curves,'actions':actions,
        'materials_native_bsdf':materials,'images':images,'scenes':scenes,'posed_motion':motion}
    return {'counts':{'objects':len(objects),'master_objects':len(master.all_objects),
            'meshes':len(meshes),'curves':len(curves),'actions':len(actions),'materials':len(materials)},
        'hashes':{k:digest(v) for k,v in components.items()},
        'object_hashes':{k:digest(v) for k,v in objects.items()},
        'mesh_hashes':{k:digest(v) for k,v in meshes.items()},
        'material_hashes':{k:digest(v) for k,v in materials.items()}}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--audit-dir',type=Path,required=True)
    parser.add_argument('--palette',type=Path,required=True)
    parser.add_argument('--expected-source-sha256',required=True)
    parser.add_argument('--commit',action='store_true',help='Commit only after all proofs pass; default stages only')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    audit=args.audit_dir.resolve();audit.mkdir(parents=True,exist_ok=True)
    source=SRC/'SWB6109EV12G.blend';binary=SRC/'sunwin-mesh.bin.gz'
    if sha(source)!=args.expected_source_sha256.lower():
        raise RuntimeError('Source changed before aqua export')
    frozen=audit/'frozen'
    if not (frozen/source.name).exists() or sha(frozen/source.name)!=sha(source) or sha(frozen/binary.name)!=sha(binary):
        raise RuntimeError('Freeze source and runtime into audit/frozen before running')
    palette=json.loads(args.palette.read_text(encoding='utf-8'))
    old_palette=json.loads((SRC/'kit-cabin.json').read_text(encoding='utf-8'))
    if len(old_palette['palette'])!=83 or len(palette['palette'])!=84 or old_palette['palette']!=palette['palette'][:83]:
        raise RuntimeError('Palette must preserve all 83 old rows and append only ShanghaiAqua')
    kit_delta=compare_palette_json(SRC/'kit-cabin.json',args.palette)
    (audit/'kit-palette-delta.json').write_text(json.dumps(kit_delta,indent=2)+'\n',encoding='utf-8')
    assets={str(p.relative_to(ROOT)):sha(p) for p in (ROOT/'Asset/SWB6109EV12G').iterdir() if p.suffix.lower() in {'.fbx','.png'}}
    assets['Asset/SWB6109EV12G.cok']=sha(ROOT/'Asset/SWB6109EV12G.cok')
    bpy.ops.wm.open_mainfile(filepath=str(source))
    master=bpy.data.collections['SWB6109EV12G | editable components']
    scene=bpy.context.scene
    if scene.frame_current!=0:
        raise RuntimeError('Expected canonical saved frame 0')
    before=snapshot(master)
    (audit/'source-before.json').write_text(json.dumps(before,indent=2)+'\n',encoding='utf-8')
    mat=bpy.data.materials[TARGET];old_props=props(mat)
    proxy=SimpleNamespace(all_objects=[SimpleNamespace(name='aqua material only',type='MESH',data=SimpleNamespace(materials=[mat]))])
    contract=apply_material_contract(proxy,args.palette)
    if snapshot(master)!=before:
        raise RuntimeError('Aqua metadata assignment altered source structure or native material')
    new_props=props(mat)
    changed=[k for k in set(old_props)|set(new_props) if old_props.get(k)!=new_props.get(k)]
    if not changed or set(changed)-ALLOWED:
        raise RuntimeError('Unexpected source material property delta')
    stage=audit/'staged';stage_src=stage/'ModelSource/SWB6109EV12G';stage_src.mkdir(parents=True,exist_ok=True)
    shutil.copyfile(args.palette,stage_src/'kit-cabin.json')
    # Load exactly the saved-source exporter helpers, without construction,
    # native atlas/FBX export, rig regeneration or any metadata mass rewrite.
    spec=json.loads((SRC/'model-spec.json').read_text(encoding='utf-8'))
    namespace=globals().copy()
    namespace.update(ROOT=ROOT,SRC=SRC,OUT=ROOT/'Asset/SWB6109EV12G',scene=scene,MASTER=master,SPEC=spec,
        L=spec['length_m'],W=spec['width_m'],HALF=spec['length_m']/2,SIDE=spec['width_m']/2,
        AXLES=[-spec['length_m']/2+spec['front_overhang_m'],spec['length_m']/2-spec['rear_overhang_m']],
        R=spec['wheel_radius_m'],ROOF=spec['body_roof_m'],QUICK=True)
    main_ast=ast.parse((TOOLS/'build_model.py').read_text(encoding='utf-8'))
    exec(compile(ast.Module(body=[n for n in main_ast.body if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef))],type_ignores=[]),str(TOOLS/'build_model.py'),'exec'),namespace)
    exec((TOOLS/'exterior.py').read_text(encoding='utf-8').split('end_surface();end_surface(True)')[0],namespace)
    for name in ('animation_rig.py','export_helpers.py','write_runtime.py'):
        exec((TOOLS/name).read_text(encoding='utf-8'),namespace)
    objects=[o for o in master.objects if o.type in {'MESH','CURVE','FONT'}]
    with canonical_export_context(master):
        parts=namespace['collect_cdlc_parts'](master)
        namespace['write_cdlc_runtime'](objects,parts,namespace['flatten_object'],stage,spec)
    if snapshot(master)!=before:
        raise RuntimeError('Runtime regeneration altered original source structure')
    delta=compare_bytes(gzip.decompress(binary.read_bytes()),gzip.decompress((stage_src/binary.name).read_bytes()))
    delta.update(before_sha256=sha(binary),after_sha256=sha(stage_src/binary.name))
    (audit/'runtime-palette-delta.json').write_text(json.dumps(delta,indent=2)+'\n',encoding='utf-8')
    candidate=audit/source.name
    master_name=master.name
    bpy.ops.wm.save_as_mainfile(filepath=str(candidate),copy=True,relative_remap=False)
    bpy.ops.wm.open_mainfile(filepath=str(candidate))
    saved=snapshot(bpy.data.collections[master_name])
    (audit/'source-after.json').write_text(json.dumps(saved,indent=2)+'\n',encoding='utf-8')
    if saved!=before:
        raise RuntimeError('Saved/reopened source changed invariant structure')
    if props(bpy.data.materials[TARGET])!=new_props:
        raise RuntimeError('Aqua runtime metadata did not survive save/reopen')
    if any(sha(ROOT/p)!=h for p,h in assets.items()):
        raise RuntimeError('Native asset changed during aqua export')
    report={'status':'STAGED_PASS','source_sha256_before':sha(source),'source_sha256_after':sha(candidate),
        'runtime_sha256_before':sha(binary),'runtime_sha256_after':sha(stage_src/binary.name),
        'source_invariants':before,'source_reopened_invariants':saved,'source_structure_unchanged':True,
        'native_asset_sha256_unchanged':assets,'material':TARGET,'property_changes':{k:{'before':old_props.get(k),'after':new_props.get(k)} for k in sorted(changed)},
        'contract':contract,'runtime_delta':delta,'kit_palette_delta':kit_delta,
        'snapshot_excluded_rna':['session_uid (runtime-only identity reallocated on file open)'],
        'parts':len(parts),'palette_rows_unchanged':83,
        'new_palette_row':palette['palette'][83],
        'limitations':'Runtime glossy dielectric preserves linear RGB and roughness, but approximates native metallic 0.15 as 0. No game visual result is asserted.'}
    if args.commit:
        # No generated data is installed until source invariants and every SWB1
        # byte outside the intended palette fields have passed.
        for src,dst in ((candidate,source),(stage_src/binary.name,binary),(stage_src/'kit-cabin.json',SRC/'kit-cabin.json'),(stage_src/'runtime-report.json',SRC/'runtime-report.json')):
            shutil.copyfile(src,dst)
        report['status']='PASS'
        (SRC/'aqua-runtime-route-report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    (audit/'aqua-runtime-route-report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print('AQUA_RUNTIME_ROUTE_'+report['status']+' source='+report['source_sha256_after']+' runtime='+report['runtime_sha256_after'],flush=True)


if __name__=='__main__':
    main()
