"""Verify appended palette row and unchanged geometry/semantic UV rows in KitExport."""
import argparse
import copy
import hashlib
import json
from pathlib import Path


def compare(before,after):
    old=json.loads(before.read_text(encoding='utf-8'))
    new=json.loads(after.read_text(encoding='utf-8'))
    if len(old['palette'])!=83 or len(new['palette'])!=84 or old['palette']!=new['palette'][:83] or new['palette'][83]['name']!='ShanghaiAqua':
        raise ValueError('Expected exactly one appended ShanghaiAqua palette row')
    normalized=copy.deepcopy(new)
    normalized['palette']=old['palette']
    records=[]
    maximum=0.
    for old_lod,new_lod,norm_lod in zip(old['lods'],new['lods'],normalized['lods']):
        for old_part,new_part,norm_part in zip(old_lod['parts'],new_lod['parts'],norm_lod['parts']):
            if len(old_part['uv'])!=len(new_part['uv']):
                raise ValueError('UV count changed')
            count=0
            for a,b in zip(old_part['uv'],new_part['uv']):
                error=abs(a[1]*83-b[1]*84)
                maximum=max(maximum,error)
                if a[0]!=b[0] or error>0.00002:
                    raise ValueError('UV semantic palette row changed')
                count+=a!=b
            norm_part['uv']=old_part['uv']
            if count:
                records.append({'lod':old_lod['lod'],'part':old_part['index'],'uv_y_renormalized':count})
    if old!=normalized:
        raise ValueError('Non-palette/UV data changed')
    return {'status':'PASS','before_sha256':hashlib.sha256(before.read_bytes()).hexdigest(),
        'after_sha256':hashlib.sha256(after.read_bytes()).hexdigest(),
        'old_palette_rows_unchanged':83,'appended_row':new['palette'][83],
        'all_non_palette_data_unchanged_except_normalized_uv_y':True,
        'uv_rule':'old_v * 83 == new_v * 84 within float32 export precision; same semantic palette row',
        'maximum_semantic_row_error':maximum,'renormalized_uv':records}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before',type=Path,required=True)
    parser.add_argument('--after',type=Path,required=True)
    parser.add_argument('--report',type=Path,required=True)
    args=parser.parse_args()
    result=compare(args.before,args.after)
    args.report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('KIT_PALETTE_DELTA_PASS '+str(len(result['renormalized_uv']))+' part/LOD UV sets normalized; geometry unchanged')
