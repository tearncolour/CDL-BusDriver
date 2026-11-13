"""Verify the generated FBX/PNG source contract without launching the game."""
from pathlib import Path
import json
import struct
import zlib

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'Asset' / 'SWB6710EV01G'
SRC = ROOT / 'ModelSource' / 'SWB6710EV01G'
NAME = 'SWB6710EV01G'

def png(path):
    data=path.read_bytes()
    assert data[:8]==b'\x89PNG\r\n\x1a\n', path
    w,h,depth,kind=struct.unpack('>IIBB',data[16:26])
    assert w==h and w>=16 and not w&(w-1) and depth==8 and kind==6,(path,w,h,depth,kind)
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

expected=set();result=[]
for lod in range(3):
    name=NAME+(f'_LOD{lod}' if lod else '')
    for mesh,suffixes in [(name,['BaseColor','MaskMap','Normal','ControlMask','Emissive0','EmissiveID0']),
                          (name+'_Gls',['BaseColor','MaskMap','Normal'])]:
        fbx=OUT/(mesh+'.fbx');expected.add(fbx.name)
        header=fbx.read_bytes()[:27]
        assert header[:23]==b'Kaydara FBX Binary  \x00\x1a\x00' and struct.unpack('<I',header[23:27])[0]==7400,(fbx,'FBX 7.4')
        headers={};maps={}
        for suffix in suffixes:
            path=OUT/(mesh+'_'+suffix+'.png');expected.add(path.name)
            headers[suffix],maps[suffix]=png(path)
        if 'EmissiveID0' in maps:
            assert headers['Emissive0']==headers['EmissiveID0']
            ids={value for row in maps['EmissiveID0'] for value in row[::4]}
            assert set(range(10))<=ids,(mesh,'missing light ID',ids)
            control=maps['ControlMask'];base=maps['BaseColor'];maskpixels=0
            for y,row in enumerate(control):
                for x in range(len(row)//4):
                    if row[x*4]>250:
                        maskpixels+=1
                        assert min(base[y][x*4:x*4+3])>250,(mesh,'tinted livery texel')
            assert maskpixels>100,(mesh,'missing livery mask')
            result.append({'lod':lod,'light_ids':sorted(ids),'livery_pixels':maskpixels})
actual={p.name for p in OUT.iterdir() if p.suffix in ['.fbx','.png']}
assert actual==expected,{'missing':sorted(expected-actual),'unexpected':sorted(actual-expected)}
assert (SRC/(NAME+'.blend')).is_file(),'editable Blender source missing'
report={'status':'PASS','files':len(expected),'checks':'FBX 7.4, PNG CRC/format, 9 light layers, neutral livery mask, Blender source','lods':result}
(SRC/'asset-validation.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
