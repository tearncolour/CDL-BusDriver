"""Read-only check of independent source straps and optional runtime records.

Checks the actual stem/grip geometry, rail-top pivots, physical source H bridge,
body-aligned Unity frames, sampled source motions, and optional SWB1 local mesh.
No claim of continuous collision-free swing is made by this check.
"""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import struct
import sys
import bpy
from mathutils import Matrix,Quaternion,Vector

HERE=Path(__file__).resolve().parent;ROOT=HERE.parents[1]
sys.path.insert(0,str(HERE))
from animation_rig import discover_strap_groups,collect_cdlc_parts,_RIG_COORD
from source_coordinates import canonical_world_matrix,canonical_export_context,source_is_corrected,SOURCE_REFLECTION

def verify(blend,binary=None):
    digest=hashlib.sha256(blend.read_bytes()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(blend))
    scene=bpy.context.scene;scene.frame_set(0);bpy.context.view_layer.update()
    master=bpy.data.collections['SWB6109EV12G | editable components']
    errors=[];rows=[]
    def check(test,message):
        if not test:errors.append(message)
    with canonical_export_context(master):
        groups=discover_strap_groups(master);parts=collect_cdlc_parts(master)
        check(len(groups)>0,'No authored hand straps found')
        check(len(parts)==34+len(groups),'Part count does not match stem/grip group count')
        rest={o.name:canonical_world_matrix(o) for group in groups for o in (group['stem'],group['grip'])}
        for ordinal,group in enumerate(groups):
            index=34+ordinal
            if index>=len(parts):continue
            part=parts[index];pivot=master.objects.get(part['pivot_object'])
            expected={group['stem'].name,group['grip'].name}
            check(set(part['objects'])==expected,f'{index}: pair is not a separate complete part')
            check(part['name']=='Strap'+str(ordinal),f'{index}: wrong sequential name')
            check(part['parent']==0,f'{index}: not parented to Shell in runtime')
            check(max(abs(float(a)-b) for a,b in zip(part['rest_rotation_unity'],(0,0,0,1)))<1e-6,f'{index}: Unity axes are not identity')
            top=Vector(group['pivot'])
            check((Vector(part['pivot_blender'])-top).length<1e-6,f'{index}: pivot is not actual stem top')
            check(pivot is not None and all(o.parent==pivot for o in (group['stem'],group['grip'])),f'{index}: pair is not under its editable pivot')
            check(all(int(o.get('cdlc_part',-1))==index for o in (group['stem'],group['grip'])),f'{index}: pair remains in static poles')
            check(all(o.get('preserve_lod') for o in (group['stem'],group['grip'])),f'{index}: strap topology not retained at all LODs')
            check(all(source_is_corrected(o) for o in (pivot,group['stem'],group['grip'])),f'{index}: bypasses physical source root')
            # The actual rail's center is at the stem's upper endpoint.
            rail_objects=[o for o in master.objects if o.name.startswith('Ceiling longitudinal grab rail')]
            rail_bounds=[]
            for rail in rail_objects:
                corners=[canonical_world_matrix(rail)@Vector(c) for c in rail.bound_box]
                rail_bounds.append([[min(p[k] for p in corners),max(p[k] for p in corners)] for k in range(3)])
            check(any(all(b[k][0]-.002<=top[k]<=b[k][1]+.002 for k in range(3)) for b in rail_bounds),f'{index}: top is not within an actual longitudinal rail')
            rows.append({'index':index,'objects':sorted(expected),'pivot_canonical':list(top),
                         'pivot_source_blender':list(SOURCE_REFLECTION@top),'pivot_unity':part['pivot_unity'],'sampled_matrix_error':{}})
        for frame,angles in ((0,(0,0)),(12,(10,-8)),(20,(0,0)),(40,(0,0)),(72,(-12,9)),(80,(0,0))):
            scene.frame_set(frame);bpy.context.view_layer.update()
            for ordinal,(group,row) in enumerate(zip(groups,rows)):
                top=Vector(group['pivot']);factor=.8+.4*((ordinal*.618034)%1.)
                q=Quaternion((0,0,1),math.radians(angles[1]*factor))@Quaternion((1,0,0),math.radians(angles[0]*factor))
                rotation=_RIG_COORD@q.to_matrix().to_4x4()@_RIG_COORD.inverted()
                pose=Matrix.Translation(top)@rotation@Matrix.Translation(-top)
                error=max(abs((pose@rest[o.name])[r][c]-canonical_world_matrix(o)[r][c])
                          for o in (group['stem'],group['grip']) for r in range(4) for c in range(4))
                row['sampled_matrix_error'][str(frame)]=error
                check(error<3e-6,f'{row["index"]}: source motion disagrees with Unity axes at frame {frame}: {error}')
        scene.frame_set(0);bpy.context.view_layer.update()
    for row in rows:
        pivot=master.objects[parts[row['index']]['pivot_object']]
        check((pivot.matrix_world.translation-Vector(row['pivot_source_blender'])).length<2e-6,f'{row["index"]}: physical source pivot reflection is wrong')
    runtime=None
    if binary:
        data=gzip.decompress(binary.read_bytes());magic,lods,count,*eye=struct.unpack_from('<4sii3f',data)
        check(magic==b'SWB1' and lods==3 and count==len(parts),'Runtime header does not match source strap count')
        offset=24;frames=[]
        for i in range(count):
            record=struct.unpack_from('<ii7f',data,offset);offset+=36;frames.append(record)
            if i>=34 and i<len(parts):
                check(max(abs(a-b) for a,b in zip(record[2:5],parts[i]['pivot_unity']))<2e-6,f'{i}: runtime pivot differs from source')
        runtime={'sha256':hashlib.sha256(binary.read_bytes()).hexdigest(),'parts':count,'lods':[]}
        for lod in range(lods):
            counts=[]
            for i in range(count):
                n,=struct.unpack_from('<i',data,offset);offset+=4
                if i>=34:
                    check(n>0,f'LOD {lod}, {i}: empty strap')
                    local=[]
                    for t in range(n):
                        tri=struct.unpack_from('<B18f',data,offset+t*73)
                        local.extend(Vector(tri[k:k+3]) for k in (1,7,13))
                    if local:
                        check(all(math.isfinite(v) for p in local for v in p),f'LOD {lod}, {i}: nonfinite strap vertices')
                        check(abs(max(p.y for p in local))<.003 and min(p.y for p in local)<-.05,f'LOD {lod}, {i}: not local beneath top')
                    counts.append(n)
                offset+=n*73
            runtime['lods'].append({'lod':lod,'strap_triangles':counts})
        check(offset==len(data),'Runtime parsing has trailing bytes')
    check(hashlib.sha256(blend.read_bytes()).hexdigest()==digest,'Read-only verification changed source file')
    return {'status':'PASS' if not errors else 'FAIL','source':str(blend),'source_sha256':digest,
            'count':len(rows),'part_count':len(parts),'straps':rows,'runtime':runtime,'errors':errors,
            'scope':'Stem/grip pairs, actual rail attachment, source/canonical/Unity coordinates and six sampled animation poses; not continuous swing clearance.'}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/SWB6109EV12G.blend')
    parser.add_argument('--binary',type=Path)
    parser.add_argument('--report',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/strap-validation.json')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    result=verify(args.blend,args.binary)
    args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('STRAP_VERIFY_'+result['status']+' '+json.dumps({'count':result['count'],'parts':result['part_count'],'errors':result['errors']}))
    if result['errors']:raise RuntimeError('Hand strap validation failed')
