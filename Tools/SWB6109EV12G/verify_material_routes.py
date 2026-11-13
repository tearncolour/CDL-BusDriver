"""Exercise actual SWB1 export material selection in an isolated temporary root.

Run with Blender --background --python-exit-code 1 --python this_file.py.
It does not load or modify the authored master or production binary.
"""
import gzip
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile

import bpy
from mathutils import Matrix

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0,str(HERE))
from write_runtime import write_cdlc_runtime
from verify_runtime_palette_delta import compare_bytes


def run():
    parser=argparse.ArgumentParser()
    parser.add_argument('--palette',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/kit-cabin.json')
    parser.add_argument('--report',type=Path,default=ROOT/'out/swb6109-handedness/runtime-review/material-route-validation.json')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    palette_path = args.palette.resolve()
    palette = json.loads(palette_path.read_text(encoding='utf-8'))['palette']
    indices = {row['name']:row['index'] for row in palette}
    identity = [Matrix.Identity(4)[row][column] for column in range(4) for row in range(4)]
    parts = [{'index':i,'name':f'Part{i}','parent':-1 if i==0 else 0,
              'pivot_unity':[0.,0.,0.],'rest_rotation_unity':[0.,0.,0.,1.],
              'posed_part_to_blender':identity} for i in range(34)]
    cases = [('explicit stainless',{'cdlc_palette_swatch':'BrushedStainless'},'BrushedStainless'),
             ('livery has priority',{'cdlc_palette_swatch':'Chrome','livery_control':True},'White'),
             ('lamp has priority',{'cdlc_palette_swatch':'Chrome','light_id':5},'Amber'),
             ('explicit dark polymer',{'cdlc_palette_swatch':'DarkGrey'},'DarkGrey'),
             ('explicit cool graphite',{'cdlc_palette_swatch':'CabinGraphite'},'CabinGraphite'),
             ('explicit cool vinyl',{'cdlc_palette_swatch':'CharcoalVinyl'},'CharcoalVinyl'),
             ('moulded grey hand grip',{'cdlc_palette_swatch':'HandGripGrey'},'HandGripGrey'),
             ('printed cloth yellow',{'cdlc_palette_swatch':'SeatPrintYellow'},'SeatPrintYellow'),
             ('red non-emissive button',{'cdlc_palette_swatch':'RequestRed'},'RequestRed'),
             ('authored aqua stripe',{'fixture_name':'02 Shanghai aqua twin stripe','cdlc_palette_swatch':'ShanghaiAqua',
                  'fixture_rgb':(.12,.47,.51),'fixture_metallic':.15,'fixture_roughness':.28},'ShanghaiAqua'),
             ('aqua is not a new generic fallback',{'fixture_rgb':(.12,.47,.51),'fixture_metallic':.15,'fixture_roughness':.28},'RearPrintGrey'),
             ('fallback linear colour',{'fixture_rgb':(.22,.24,.25),'fixture_metallic':0.,'fixture_roughness':.5},'HandGripGrey'),
             ('fallback stainless',{'fixture_rgb':(.56,.58,.59),'fixture_metallic':1.,'fixture_roughness':.25},'BrushedStainless'),
             ('fallback bright red must not emit',{'fixture_rgb':(1.,.0134,.0072),'fixture_metallic':0.,'fixture_roughness':.28},'@unlit_nonmetal'),
             ('fallback bright white must not emit',{'fixture_rgb':(1.,1.,.9),'fixture_metallic':0.,'fixture_roughness':.4},'@unlit_nonmetal')]
    objects=[]
    try:
        for number,(label,properties,expected) in enumerate(cases):
            material=bpy.data.materials.new(properties.get('fixture_name','route fixture neutral '+str(number)))
            # All fixtures deliberately share the same nonmetal color and have
            # no chrome/aluminium keyword, so RGB/name fallback cannot pass.
            material.diffuse_color=(.55,.56,.56,1.)
            for key,value in properties.items():
                if not key.startswith('fixture_'):material[key]=value
            if 'fixture_rgb' in properties:
                material.use_nodes=True
                material.diffuse_color=(*properties['fixture_rgb'],1.)
                shader=next(n for n in material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
                shader.inputs['Metallic'].default_value=properties['fixture_metallic']
                shader.inputs['Roughness'].default_value=properties['fixture_roughness']
            mesh=bpy.data.meshes.new('route fixture geometry')
            x=number*2.
            mesh.from_pydata([(x,0,0),(x+.3,0,0),(x,.3,0),(x,0,.3)],[],
                             [(0,2,1),(0,1,3),(0,3,2),(1,2,3)])
            mesh.materials.append(material);mesh.update()
            obj=bpy.data.objects.new('route fixture object '+str(number),mesh)
            bpy.context.scene.collection.objects.link(obj)
            obj['cdlc_part']=0;obj['preserve_lod']=True;objects.append(obj)
        spec={'driver_eye_unity':[0.,1.8,0.],'runtime_lod_ratios':[1.,1.,1.]}
        checks=[]
        with tempfile.TemporaryDirectory(prefix='swb6109-material-routes-') as directory:
            sandbox=Path(directory);source=sandbox/'ModelSource/SWB6109EV12G'
            source.mkdir(parents=True)
            (source/'kit-cabin.json').write_text(json.dumps({'palette':palette}))
            write_cdlc_runtime(objects,parts,lambda obj:obj.data.copy(),sandbox,spec)
            data=gzip.decompress((source/'sunwin-mesh.bin.gz').read_bytes())
            lod_count,part_count=struct.unpack_from('<ii',data,4)
            offset=24+36*part_count
            for lod in range(lod_count):
                observed=[set() for _ in cases]
                for part in range(part_count):
                    count,=struct.unpack_from('<i',data,offset);offset+=4
                    for _ in range(count):
                        values=struct.unpack_from('<B18f',data,offset);offset+=73
                        index=round(min(values[1],values[7],values[13])/2.)
                        observed[index].add(values[0])
                for number,(label,properties,expected) in enumerate(cases):
                    if expected=='@unlit_nonmetal':
                        rows=[row for row in palette if row['index'] in observed[number]]
                        passed=len(rows)==1 and rows[0]['metallic']<.5 and rows[0]['finish'] not in {'Lamp','Tail','Beacon','Aviation','NavGreen','Glow','Glass','Hazard'}
                    else:passed=observed[number]=={indices[expected]}
                    checks.append({'lod':lod,'case':label,'expected_swatch':expected,
                                   'actual_indices':sorted(observed[number]),'passed':passed})
                    if not passed:raise AssertionError(checks[-1])
            aqua_object=next(obj for obj in objects if obj.data.materials[0].name=='02 Shanghai aqua twin stripe')
            aqua=aqua_object.data.materials[0]
            # Reproduce the previous untagged fallback on the same geometry, then
            # regenerate the authored route. Compare every non-palette byte.
            aqua.name='legacy aqua fallback fixture'
            del aqua['cdlc_palette_swatch']
            proof_objects=[objects[0],aqua_object]
            write_cdlc_runtime(proof_objects,parts,lambda obj:obj.data.copy(),sandbox,spec)
            proof_before=gzip.decompress((source/'sunwin-mesh.bin.gz').read_bytes())
            aqua.name='02 Shanghai aqua twin stripe';aqua['cdlc_palette_swatch']='ShanghaiAqua'
            write_cdlc_runtime(proof_objects,parts,lambda obj:obj.data.copy(),sandbox,spec)
            proof_after=gzip.decompress((source/'sunwin-mesh.bin.gz').read_bytes())
            palette_delta=compare_bytes(proof_before,proof_after,part=0,old_index=indices['RearPrintGrey'],new_index=indices['ShanghaiAqua'])
            assert all(row['changed_palette_bytes']==4 for row in palette_delta['lods'])
            # A real coordinate mutation must be rejected, not hidden by stripping
            # all triangle records or comparing only counts.
            corrupt=bytearray(proof_after);corrupt[24+36*34+4+1]^=1
            delta_rejects_geometry=False
            try:compare_bytes(proof_before,bytes(corrupt),part=0,old_index=indices['RearPrintGrey'],new_index=indices['ShanghaiAqua'])
            except ValueError as error:delta_rejects_geometry='Position/normal payload changed' in str(error)
            assert delta_rejects_geometry
            objects[0].data.materials[0]['cdlc_palette_swatch']='NotARealSwatch'
            rejected=False
            try:write_cdlc_runtime(objects,parts,lambda obj:obj.data.copy(),sandbox,spec)
            except ValueError as error:rejected='Unknown explicit runtime finish' in str(error)
            if not rejected:raise AssertionError('Invalid explicit material fell back silently')
            objects[0].data.materials[0]['cdlc_palette_swatch']='BrushedStainless'
            rejected_aqua=[]
            for label,changes in [('missing explicit tag',{'cdlc_palette_swatch':''}),
                                  ('wrong explicit tag',{'cdlc_palette_swatch':'RearPrintGrey'}),
                                  ('line colour override',{'livery_control':True}),
                                  ('emission override',{'light_id':5})]:
                aqua['cdlc_palette_swatch']='ShanghaiAqua';aqua['livery_control']=False;aqua['light_id']=0
                for key,value in changes.items():aqua[key]=value
                try:write_cdlc_runtime(objects,parts,lambda obj:obj.data.copy(),sandbox,spec)
                except ValueError as error:
                    if 'Authored fixed-colour stripe requires explicit ShanghaiAqua' in str(error):rejected_aqua.append(label)
                if label not in rejected_aqua:raise AssertionError('Unsafe aqua route was not rejected: '+label)
        report={'model':'SWB6109EV12G','status':'PASS','scope':'Actual writer material routing with isolated geometry; no production asset mutation',
                'palette_sha256':hashlib.sha256(palette_path.read_bytes()).hexdigest(),
                'writer_sha256':hashlib.sha256((HERE/'write_runtime.py').read_bytes()).hexdigest(),
                'checks':checks,'invalid_explicit_swatch_rejected':rejected,'invalid_aqua_routes_rejected':rejected_aqua,
                'regenerated_fixture_palette_delta':palette_delta,'palette_delta_rejects_geometry_mutation':delta_rejects_geometry,
                'runtime_followup':'C# regression checks appended stainless, nonmetal grips, and textile print finish parameters and actual submesh routing.'}
        output=args.report.resolve()
        output.parent.mkdir(parents=True,exist_ok=True)
        output.write_text(json.dumps(report,indent=2)+'\n')
        print('MATERIAL_ROUTE_CHECK_PASS',len(checks),'routing cases plus invalid-tag rejection',flush=True)
    finally:
        for obj in objects:
            mesh=obj.data;materials=list(mesh.materials)
            bpy.data.objects.remove(obj,do_unlink=True)
            bpy.data.meshes.remove(mesh)
            for material in materials:
                if material.users==0:bpy.data.materials.remove(material)


if __name__=='__main__':run()
