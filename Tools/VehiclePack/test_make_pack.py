"""Offline assembly pack regression; no game installation or live pack is touched."""
import copy
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('make_pack', Path(__file__).with_name('make_pack.py'))
pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pack)


class AssemblyPackTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        pack.ROOT = self.root
        self.source = self.root/'ModelSource'/'Fixture'
        self.source.mkdir(parents=True)
        (self.root/'Asset').mkdir()
        self.tool = self.root/'Tools'/'Fixture'
        self.tool.mkdir(parents=True)
        (self.source/'kit-cabin.json').write_text('{"palette":[0,1]}')
        for name in ('Front','Rear'):
            (self.source/(name+'.gz')).write_bytes(gzip.compress(struct.pack('<4sii3f', b'SWB1', 3, 34, 0, 2, 3)))
        (self.source/'joint.gz').write_bytes(gzip.compress(struct.pack('<4si', b'CJA1', 3)))
        self.profile = {
            'format':'cdlb-vehicle','version':2,'model':'Fixture',
            'sections': [{'id':n.lower(),'prefab':n,'restPosition':[0,0,0] if n=='Front' else [0,0,-2],
                'restRotation':[0,0,0,1], 'profile':{'model':n,'mesh':n+'.swb1.gz','axles':[{'z':0,'parts':[21,22],'steer':False}],
                    'doors':[{'centreZ':0,'width':1.2,'boardingPosition':[1.25,.35,0],
                        'boardingRotation':[0,-.707106781,0,.707106781],'boarding':n=='Front','alighting':n=='Rear'}]}}
                for n in ('Front','Rear')],
            'articulation':{'frontSection':'front','rearSection':'rear','frontAnchor':[0,1,-1],
                'rearAnchor':[0,1,1],'maxSeparation':.05,
                'joint':{'format':'cdlb-joint-v1','mesh':'Fixture.joint.gz','maxBendDegrees':55}},
            'pack':{'template':'Bus02','capacity':151,'jointSource':'joint.gz','sections':{
                'front':{'capacity':85,'guid':'1'*32,'meshSource':'Front.gz'},
                'rear':{'capacity':66,'guid':'2'*32,'meshSource':'Rear.gz'}}}}

    def tearDown(self):
        self.temp.cleanup()

    def run_pack(self, profile=None):
        (self.tool/'vehicle.json').write_text(json.dumps(profile or self.profile))
        return pack.main(['Fixture'])

    def test_independent_resources_and_explicit_manifest_entrances(self):
        self.assertEqual(self.run_pack(),0)
        folder=self.root/'Packs'/'CDLC.Bus.Fixture'
        manifests=[(folder/(name+'.cdlb.json')).read_bytes() for name in ('Fixture','Front','Rear')]
        self.assertEqual(manifests[0],manifests[1]);self.assertEqual(manifests[1],manifests[2])
        data=json.loads(manifests[0])
        for section in data['sections']:
            p=section['profile']
            self.assertEqual(p['meshSha256'],hashlib.sha256((folder/p['mesh']).read_bytes()).hexdigest())
            self.assertEqual(p['paletteRows'],2)
        self.assertEqual(data['articulation']['joint']['meshSha256'],hashlib.sha256((folder/'Fixture.joint.gz').read_bytes()).hexdigest())
        metadata=json.loads((folder/'mod.json').read_text())
        self.assertFalse(metadata['isCodeMod'])
        self.assertEqual(metadata['dependencies'],['CDLC.BusDriver'])
        self.assertIn('0.9.0',metadata['description'])
        self.assertIn('STAGING ONLY',metadata['description'])
        readme=(folder/'README.txt').read_text(encoding='utf-8')
        self.assertEqual(readme.splitlines()[0],'SOURCE/RESOURCE STAGING ONLY — NOT INSTALLABLE OR PLAYABLE')
        self.assertIn('CDLC Bus Driver 0.9.0 or later',readme)
        self.assertNotIn('Install the whole folder',readme)

    def test_invalid_inputs_preserve_previous_pack(self):
        self.run_pack()
        marker=self.root/'Packs'/'CDLC.Bus.Fixture'/'keep.txt';marker.write_text('previous valid output')
        changes=[lambda p:p['pack']['sections']['rear'].update(capacity=65),
            lambda p:p['pack']['sections']['rear'].update(guid='1'*32),
            lambda p:p['pack']['sections']['rear'].update(meshSource='../outside.gz'),
            lambda p:p['sections'][1]['profile'].update(mesh='../escape.gz'),
            lambda p:p['sections'][1].update(prefab='Front'),
            lambda p:p['sections'][1].update(prefab='Fixture_Rear'),
            lambda p:p['sections'][1].update(restPosition=[0,0,-2.1]),
            lambda p:p['articulation']['joint'].update(format='SWB1'),
            lambda p:p['sections'][0]['profile'].update(meshSha256='0'*64),
            lambda p:p['sections'][0]['profile'].update(paletteRows=99),
            lambda p:p['articulation']['joint'].update(meshSha256='0'*64)]
        for change in changes:
            p=copy.deepcopy(self.profile);change(p)
            with self.assertRaises(SystemExit):self.run_pack(p)
            self.assertEqual(marker.read_text(),'previous valid output')

    def test_missing_native_asset_rejected_when_required(self):
        (self.tool/'vehicle.json').write_text(json.dumps(self.profile))
        with self.assertRaises(SystemExit):pack.main(['Fixture','--require-cok'])
        self.assertFalse((self.root/'Packs'/'CDLC.Bus.Fixture').exists())

    def test_v1_keeps_single_resource_and_profile_format(self):
        (self.source/'sunwin-mesh.bin.gz').write_bytes((self.source/'Front.gz').read_bytes())
        profile={'format':'cdlb-vehicle','version':1,'model':'Fixture',
            'pack':{'template':'Bus02','capacity':80},'spec':{'unchanged':'v1'}}
        self.assertEqual(self.run_pack(profile),0)
        folder=self.root/'Packs'/'CDLC.Bus.Fixture'
        shipped=json.loads((folder/'Fixture.cdlb.json').read_text())
        self.assertEqual(shipped['version'],1)
        self.assertEqual(shipped['spec'],profile['spec'])
        self.assertEqual(shipped['mesh'],'Fixture.swb1.gz')
        self.assertNotIn('sections',shipped)
        metadata=json.loads((folder/'mod.json').read_text())
        self.assertEqual(metadata['dependencies'],[])
        self.assertIn('STAGING ONLY',metadata['description'])
        self.assertEqual((folder/'README.txt').read_text(encoding='utf-8').splitlines()[0],
            'SOURCE/RESOURCE STAGING ONLY — NOT INSTALLABLE OR PLAYABLE')
        self.assertFalse(any(f.suffix == '.png' for f in folder.iterdir()))

    def test_pack_icon_checked_not_copied(self):
        # the in-game packer reads it beside Tools/<model>/vehicle.json and puts it into the .cok; a loose copy in the pack
        # would be indexed by the game's user database as an image of its own
        png = b'\x89PNG\r\n\x1a\n' + b'fixture'
        (self.tool/'icon.png').write_bytes(png)
        (self.source/'sunwin-mesh.bin.gz').write_bytes((self.source/'Front.gz').read_bytes())
        profile={'format':'cdlb-vehicle','version':1,'model':'Fixture','pack':{'template':'Bus02','icon':'icon.png'}}
        self.assertEqual(self.run_pack(profile),0)
        folder=self.root/'Packs'/'CDLC.Bus.Fixture'
        self.assertFalse((folder/'icon.png').exists())
        self.assertEqual(json.loads((folder/'Fixture.cdlb.json').read_text())['pack']['icon'], 'icon.png')
        assembly=copy.deepcopy(self.profile); assembly['pack']['icon']='icon.png'
        self.assertEqual(self.run_pack(assembly),0)
        self.assertFalse((folder/'icon.png').exists())

    def test_pack_icon_rejected(self):
        (self.source/'sunwin-mesh.bin.gz').write_bytes((self.source/'Front.gz').read_bytes())
        (self.tool/'fake.png').write_bytes(b'not a png')
        for bad in ('../icon.png', 'sub/icon.png', 'icon.jpg', 'missing.png', 'fake.png', 5):
            profile={'format':'cdlb-vehicle','version':1,'model':'Fixture','pack':{'template':'Bus02','icon':bad}}
            with self.assertRaises(SystemExit, msg=repr(bad)):
                self.run_pack(profile)


if __name__ == '__main__': unittest.main()
