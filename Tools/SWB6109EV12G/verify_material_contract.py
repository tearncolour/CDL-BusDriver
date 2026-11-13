"""Isolated colour/finish/PNG checks; run with Blender, optionally on a source file.

No source, production texture or runtime binary is saved by this verifier.
"""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import sys
import tempfile
from array import array

import bpy

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]
sys.path.insert(0,str(HERE))
from material_contract import MATERIAL_SWATCHES, SOURCE_PRESERVING_SWATCHES, apply_material_contract, linear


def geometry_digest(collection):
    result=hashlib.sha256()
    for obj in sorted(collection.all_objects,key=lambda item:item.name):
        result.update(obj.name.encode())
        result.update(repr(tuple(tuple(row) for row in obj.matrix_world)).encode())
        if obj.type=='MESH':
            values=array('f',[0.])*(3*len(obj.data.vertices))
            obj.data.vertices.foreach_get('co',values)
            result.update(values.tobytes())
            indices=array('i',[0])*len(obj.data.loops)
            obj.data.loops.foreach_get('vertex_index',indices)
            result.update(indices.tobytes())
    return result.hexdigest()


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--palette',type=Path,required=True)
    parser.add_argument('--report',type=Path,required=True)
    parser.add_argument('--check-saved',action='store_true',help='Require the opened saved source to already satisfy the material contract before testing its in-memory idempotence.')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    palette={row['name']:row for row in json.loads(args.palette.read_text())['palette']}
    master=bpy.data.collections.get('SWB6109EV12G | editable components')
    source_path=Path(bpy.data.filepath) if bpy.data.filepath else None
    source_hash=hashlib.sha256(source_path.read_bytes()).hexdigest() if source_path else None
    saved_checked=0
    if args.check_saved:
        assert master is not None and source_path,'--check-saved requires the authored saved source'
        used={m.name:m for obj in master.all_objects if obj.type in {'MESH','CURVE','FONT'} for m in obj.data.materials if m}
        for name,material in used.items():
            sw=MATERIAL_SWATCHES.get(name)
            if name.startswith('6109 MeshKit '):sw=name[len('6109 MeshKit '):]
            if sw is None:continue
            entry=palette[sw]
            native=SOURCE_PRESERVING_SWATCHES.get(name,entry)
            shader=next(n for n in material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
            expected=[linear(v) for v in entry['rgb']]
            assert material.get('cdlc_palette_swatch')==sw,(name,'saved palette route')
            assert max(abs(a-b) for a,b in zip(material.diffuse_color[:3],expected))<1e-6,(name,'saved linear colour')
            assert max(abs(a-b) for a,b in zip(shader.inputs['Base Color'].default_value[:3],expected))<1e-6,(name,'saved BSDF colour')
            assert abs(shader.inputs['Metallic'].default_value-native['metallic'])<1e-6,(name,'saved metallic')
            assert abs(shader.inputs['Roughness'].default_value-native['roughness'])<1e-6,(name,'saved roughness')
            assert abs(shader.inputs['Coat Weight'].default_value)<1e-6,(name,'saved coat')
            if name in MATERIAL_SWATCHES:
                assert not material.get('light_id') and not material.get('livery_control') and not material.get('glass'),(name,'nonemissive opaque route')
                assert shader.inputs['Emission Strength'].default_value==0.,(name,'saved emission')
            saved_checked+=1
        assert saved_checked>=21,'Saved source lacks expected material coverage'
        for obj in master.all_objects:
            if obj.name.startswith(('6109 seat print | ','Photo seat print | ')):
                prefix='RearPrint' if 'rear shell' in obj.name else 'SeatPrint'
                assert {m.get('cdlc_palette_swatch') for m in obj.data.materials}=={prefix+'Yellow',prefix+'Grey'},(obj.name,'saved print substrate')
    if master is None:
        master=bpy.data.collections.new('Material contract fixture')
        bpy.context.scene.collection.children.link(master)
    # Include every supported authored material, even if the opened review source
    # predates a particular accessory. The contract is validated before final apply.
    mesh=bpy.data.meshes.new('Material contract fixture')
    fixture=bpy.data.objects.new('Material contract fixture',mesh)
    master.objects.link(fixture)
    for name in MATERIAL_SWATCHES:
        material=bpy.data.materials.get(name)
        created=material is None
        if created:material=bpy.data.materials.new(name)
        material.use_nodes=True
        if created and name in SOURCE_PRESERVING_SWATCHES:
            native=SOURCE_PRESERVING_SWATCHES[name]
            material.diffuse_color=(*native['linear_rgb'],1.)
            shader=next(n for n in material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
            shader.inputs['Base Color'].default_value=material.diffuse_color
            shader.inputs['Metallic'].default_value=native['metallic']
            shader.inputs['Roughness'].default_value=native['roughness']
            shader.inputs['Emission Strength'].default_value=0.
        mesh.materials.append(material)
    # Both substrate routes are exercised with the real object/material names.
    substrate_fixtures=[]
    for side in ['backrest','rear shell']:
        data=bpy.data.meshes.new('Print substrate fixture '+side)
        obj=bpy.data.objects.new('6109 seat print | Passenger seat 99 '+side,data)
        master.objects.link(obj)
        for colour in ['Yellow','Grey']:
            data.materials.append(bpy.data.materials['6109 seat print | '+colour])
        substrate_fixtures.append(obj)
    before=geometry_digest(master)
    def native_values(name):
        material=bpy.data.materials[name]
        shader=next(n for n in material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
        return (tuple(material.diffuse_color),tuple(shader.inputs['Base Color'].default_value),
                float(shader.inputs['Metallic'].default_value),float(shader.inputs['Roughness'].default_value),
                float(shader.inputs['Coat Weight'].default_value),tuple(shader.inputs['Emission Color'].default_value),
                float(shader.inputs['Emission Strength'].default_value),
                material.get('light_id'),material.get('livery_control'),material.get('glass'))
    native_before={name:native_values(name) for name in SOURCE_PRESERVING_SWATCHES}
    first=apply_material_contract(master,args.palette)
    assert native_before=={name:native_values(name) for name in SOURCE_PRESERVING_SWATCHES},'Native-preserving route changed a BSDF/atlas input'
    assert [m.get('cdlc_palette_swatch') for m in substrate_fixtures[0].data.materials]==['SeatPrintYellow','SeatPrintGrey']
    assert [m.get('cdlc_palette_swatch') for m in substrate_fixtures[1].data.materials]==['RearPrintYellow','RearPrintGrey']
    after=geometry_digest(master)
    assert before==after,'Material patch changed geometry or world matrices'
    second=apply_material_contract(master,args.palette)
    assert native_before=={name:native_values(name) for name in SOURCE_PRESERVING_SWATCHES},'Repeated native-preserving route changed a BSDF/atlas input'
    assert len(first['materials'])==len(second['materials'])
    for row in second['materials']:
        entry=palette[row['swatch']]
        expected=SOURCE_PRESERVING_SWATCHES.get(row['material'],entry)
        assert max(abs(a-b) for a,b in zip(row['before']['linear_rgb'],row['linear_rgb']))<1e-6
        assert abs(row['before']['metallic']-expected['metallic'])<1e-6
        assert abs(row['before']['roughness']-expected['roughness'])<1e-6
        if row['material'] in SOURCE_PRESERVING_SWATCHES:
            assert row['native_preserved'] and abs(row['metallic']-.15)<1e-6 and abs(row['runtime_metallic'])<1e-6
    # Execute exactly the production PNG writer and PNG decoder without either
    # module's top-level build/validation side effects.
    namespace={'bpy':bpy,'verified_bytes':{},'remember':lambda *args:None}
    for filename,function in [('export_helpers.py','write_atlas'),('verify_assets.py','png')]:
        tree=ast.parse((HERE/filename).read_text(encoding='utf-8-sig'))
        node=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name==function)
        exec(compile(ast.Module(body=[node],type_ignores=[]),str(HERE/filename),'exec'),namespace)
    import struct,zlib
    namespace.update(struct=struct,zlib=zlib)
    atlas_materials=list(mesh.materials)
    coated=bpy.data.materials.new('Explicit coat regression fixture');coated.use_nodes=True
    shader=next(node for node in coated.node_tree.nodes if node.type=='BSDF_PRINCIPLED')
    shader.inputs['Coat Weight'].default_value=.37
    shader.inputs['Metallic'].default_value=.24
    shader.inputs['Roughness'].default_value=.63
    coated.diffuse_color=(.18,.31,.52,1.)
    atlas_materials.append(coated)
    png_checks=[]
    with tempfile.TemporaryDirectory(prefix='swb6109-material-contract-') as directory:
        namespace['OUT']=Path(directory)
        namespace['write_atlas']('fixture',atlas_materials)
        maps={key:namespace['png'](Path(directory)/('fixture_'+key+'.png'))[1]
              for key in ['BaseColor','MaskMap','Normal','ControlMask','Emissive0','EmissiveID0']}
        for i,material in enumerate(atlas_materials):
            # PNG rows are top-down; Blender's image buffer is bottom-up.
            row,col=divmod(i,16);y=63-(row*4+2);x=(col*4+2)*4
            shader=next(node for node in material.node_tree.nodes if node.type=='BSDF_PRINCIPLED')
            expected=[shader.inputs['Metallic'].default_value,shader.inputs['Coat Weight'].default_value,
                      0.,1-shader.inputs['Roughness'].default_value]
            actual=list(maps['MaskMap'][y][x:x+4])
            assert max(abs(a-255*b) for a,b in zip(actual,expected))<=1.,(material.name,'mask',actual,expected)
            rgb=list(maps['BaseColor'][y][x:x+3])
            restored=[linear(value/255.) for value in rgb]
            assert max(abs(a-b) for a,b in zip(restored,material.diffuse_color[:3]))<.006,(material.name,'gamma',rgb,material.diffuse_color[:3])
            assert list(maps['Normal'][y][x:x+3])==[128,128,255]
            assert maps['EmissiveID0'][y][x]==0 and max(maps['Emissive0'][y][x:x+3])==0
            assert maps['ControlMask'][y][x]==0
            png_checks.append({'material':material.name,'mask_rgba':actual,'base_srgb_bytes':rgb})
        # A nonzero source coat must not leak into the glass shader's AO G.
        glass=coated.copy();glass.name='Glass AO versus coat regression fixture';glass['glass']=True
        next(node for node in glass.node_tree.nodes if node.type=='BSDF_PRINCIPLED').inputs['Coat Weight'].default_value=.65
        namespace['write_atlas']('fixture_Gls',[glass])
        glass_mask=namespace['png'](Path(directory)/'fixture_Gls_MaskMap.png')[1]
        assert all(value==255 for row in glass_mask for value in row[1::4]),'BH/GlsShader AO must be 1 including padding'
        actual=list(glass_mask[61][8:12])
        assert abs(actual[0]-.24*255)<=1 and abs(actual[3]-.37*255)<=1,(actual,'glass R/A changed')
        assert not (Path(directory)/'fixture_Gls_ControlMask.png').exists()
        png_checks.append({'material':glass.name,'mask_rgba':actual,'source_coat':.65,'shader':'BH/GlsShader','G':'AO=1; coat is independent'})
    report={'status':'PASS','source':bpy.data.filepath,'geometry_unchanged_sha256':before,
            'source_sha256':source_hash,'saved_source_checked_materials':saved_checked,
            'palette_sha256':hashlib.sha256(args.palette.read_bytes()).hexdigest(),
            'atlas_writer_sha256':hashlib.sha256((HERE/'export_helpers.py').read_bytes()).hexdigest(),
            'contract_sha256':hashlib.sha256((HERE/'material_contract.py').read_bytes()).hexdigest(),
            'idempotent':True,'palette':str(args.palette.resolve()),'materials':first['materials'],
            'native_preserved_materials':list(native_before),
            'actual_production_png_writer_checks':png_checks,
            'not_tested':'Real game rendered appearance and final saved source require subsequent validation.'}
    if source_path:assert hashlib.sha256(source_path.read_bytes()).hexdigest()==source_hash,'Saved source changed during verification'
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(report,indent=2)+'\n')
    print('MATERIAL_CONTRACT_PASS',len(first['materials']),'materials;',len(png_checks),'PNG swatches',flush=True)


if __name__=='__main__':main()
