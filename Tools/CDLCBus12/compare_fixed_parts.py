"""Prove a strap layout revision preserved the fixed contract parts and door mechanisms."""
import hashlib,json,sys
from pathlib import Path
before,after,out=map(Path,sys.argv[1:]);a=json.loads((before/'kit.json').read_text());b=json.loads((after/'kit.json').read_text());rows=[]
def digest(value):return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':')).encode()).hexdigest()
for la,lb in zip(a['lods'],b['lods']):
    aa={p['index']:p for p in la['parts']};bb={p['index']:p for p in lb['parts']}
    for index in range(40):
        same=aa[index]==bb[index];rows.append({'lod':la['lod'],'part':index,'name':aa[index]['name'],'same':same,'before_sha256':digest(aa[index]),'after_sha256':digest(bb[index])})
frames_a=json.loads((before/'frames.json').read_text());frames_b=json.loads((after/'frames.json').read_text())
record={'before':str(before.resolve()),'after':str(after.resolve()),'fixed_frames_unchanged':frames_a[:40]==frames_b[:40],
        'palette_unchanged':a['palette']==b['palette'],'parts':rows,'scope':'Exact decoded export data for parts 0..39, including positions, normals, UVs, indices, materials and rest transforms; straps are intentionally changed.'}
out.write_text(json.dumps(record,indent=2));assert record['fixed_frames_unchanged'] and record['palette_unchanged'] and all(r['same'] for r in rows)
print('All 120 fixed-part LOD records and first 40 frames are unchanged')
