"""Verify the generated FBX/PNG source contract without launching the game."""
from pathlib import Path
import hashlib
import json
import struct
import zlib

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Asset' / 'SWB6109EV12G'
SRC = ROOT / 'ModelSource' / 'SWB6109EV12G'
NAME = 'SWB6109EV12G'
verified_bytes = {}

def remember(path, data):
    verified_bytes[path.name] = {'path': path.relative_to(ROOT).as_posix(),
        'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}

def png(path):
    data=path.read_bytes()
    remember(path, data)
    assert data[:8]==b'\x89PNG\r\n\x1a\n', path
    w,h,depth,kind=struct.unpack('>IIBB',data[16:26])
    assert w==h==64 and depth==8 and kind==6,(path,'constant material palette must be 64x64 RGBA8',w,h,depth,kind)
    pos=8; compressed=b''
    while pos<len(data):
        n=struct.unpack('>I',data[pos:pos+4])[0];typ=data[pos+4:pos+8];chunk=data[pos+8:pos+8+n]
        crc=struct.unpack('>I',data[pos+8+n:pos+12+n])[0]
        assert zlib.crc32(typ+chunk)&0xffffffff==crc,(path,'CRC')
        if typ==b'IDAT':compressed+=chunk
        pos+=n+12
    raw=zlib.decompress(compressed);stride=w*4
    rows=[];prev=bytearray(stride);pos=0
    for y in range(h):
        filt=raw[pos];row=bytearray(raw[pos+1:pos+1+stride]);pos+=stride+1
        for i in range(stride):
            a=row[i-4] if i>=4 else 0;b=prev[i];c=prev[i-4] if i>=4 else 0
            if filt==1:v=a
            elif filt==2:v=b
            elif filt==3:v=(a+b)//2
            elif filt==4:
                p=a+b-c;pa,pb,pc=abs(p-a),abs(p-b),abs(p-c)
                v=a if pa<=pb and pa<=pc else b if pb<=pc else c
            else:
                assert filt==0,(path,'filter');v=0
            row[i]=(row[i]+v)&255
        rows.append(row);prev=row
    return (w,h,depth,kind),rows

expected=set();result=[];glass_map_hashes={}
for lod in range(3):
    name=NAME+(f'_LOD{lod}' if lod else '')
    for mesh,suffixes in [(name,['BaseColor','MaskMap','Normal','ControlMask','Emissive0','EmissiveID0']),
                          (name+'_Gls',['BaseColor','MaskMap','Normal'])]:
        fbx=OUT/(mesh+'.fbx');expected.add(fbx.name)
        fbx_data=fbx.read_bytes();remember(fbx,fbx_data);header=fbx_data[:27]
        assert header[:23]==b'Kaydara FBX Binary  \x00\x1a\x00' and struct.unpack('<I',header[23:27])[0]==7400,(fbx,'FBX 7.4')
        headers={};maps={}
        for suffix in suffixes:
            path=OUT/(mesh+'_'+suffix+'.png');expected.add(path.name)
            headers[suffix],maps[suffix]=png(path)
        assert all(value==0 for row in maps['MaskMap'] for value in row[2::4]),(mesh,'unused MaskMap B channel is not zero')
        expected_green=255 if mesh.endswith('_Gls') else 0
        assert all(value==expected_green for row in maps['MaskMap'] for value in row[1::4]),(mesh,'MaskMap G must be unoccluded AO=255 for BH/GlsShader, uncoated=0 for BH/SG_DefaultShader')
        assert all(row[i:i+4]==bytes((128,128,255,255)) for row in maps['Normal'] for i in range(0,len(row),4)),(mesh,'flat normal map was gamma encoded or damaged')
        if mesh.endswith('_Gls'):
            hashes={suffix:verified_bytes[mesh+'_'+suffix+'.png']['sha256'] for suffix in suffixes}
            if glass_map_hashes:assert hashes==glass_map_hashes,(mesh,'LOD glass texture aliases differ')
            else:glass_map_hashes=hashes
        if 'EmissiveID0' in maps:
            assert headers['Emissive0']==headers['EmissiveID0']
            ids={value for row in maps['EmissiveID0'] for value in row[::4]}
            assert set(range(10))<=ids,(mesh,'missing light ID',ids)
            control=maps['ControlMask'];base=maps['BaseColor'];maskpixels=0
            lightpixels={str(i):0 for i in range(10)}
            for y,row in enumerate(control):
                for x in range(len(row)//4):
                    j=x*4;identifier=maps['EmissiveID0'][y][j]
                    assert identifier in range(10),(mesh,'invalid light ID',identifier)
                    assert maps['EmissiveID0'][y][j+1:j+3]==bytes((0,0)),(mesh,'nonzero light ID data channels')
                    emission=maps['Emissive0'][y][j:j+3]
                    if identifier:
                        assert max(emission)>0,(mesh,'empty emission for active light ID',identifier)
                        assert row[j]==0,(mesh,'light texel incorrectly receives line colour',identifier)
                    else:
                        assert max(emission)==0,(mesh,'emission has no light ID')
                    lightpixels[str(identifier)]+=1
                    if row[x*4]>250:
                        maskpixels+=1
                        assert min(base[y][x*4:x*4+3])>250,(mesh,'tinted livery texel')
            assert maskpixels>=16,(mesh,'missing complete 4x4 livery swatch')
            result.append({'lod':lod,'light_ids':sorted(ids),'livery_pixels':maskpixels,'light_id_texels':lightpixels,
                           'emission_id_binding':'Every emissive texel has ID1..9; ID0 is unlit, and lights are outside the line-colour mask.'})
actual={p.name for p in OUT.iterdir() if p.suffix in ['.fbx','.png']}
assert actual==expected,{'missing':sorted(expected-actual),'unexpected':sorted(actual-expected)}
assert (SRC/(NAME+'.blend')).is_file(),'editable Blender source missing'
for row in verified_bytes.values():
    assert hashlib.sha256((ROOT/row['path']).read_bytes()).hexdigest()==row['sha256'],('asset changed during verification',row['path'])
source=SRC/(NAME+'.blend')
report={'status':'PASS','files':len(expected),'checks':'FBX 7.4, PNG CRC/format, uncoated material mask and linear normal map, 9 light layers, neutral livery mask, Blender source','lods':result,
        'verified_files':list(verified_bytes.values()),'source_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),
        'palette_atlas':{'resolution':[64,64],'grid':[16,16],'swatch_pixels':[4,4]},
        'native_mask_contract':{'R':'metallic','G_body':'SG_DefaultShader coat; opaque parts zero','G_glass':'BH/GlsShader AO; unoccluded 1, not coat','B':'unused zero','A':'smoothness; 1 minus roughness','glass_coat':'Independent _CoatMask=.65 on the model-specific packaged SurfaceAsset'},
        'glass_texture_contract':{'lod_source_aliases_identical':True,'hashes':glass_map_hashes,
            'packaging':'The native importer requires LOD-matched PNG aliases; BusVehicleAssetSystem shares the LOD0 glass SurfaceAsset across all three packaged LODs.'}}
(SRC/'asset-validation.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
