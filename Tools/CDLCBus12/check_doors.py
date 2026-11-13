"""Inspect actual saved door motion, guide constraint and triangle intersections across every LOD."""
import argparse,hashlib,json,sys
from pathlib import Path
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

def meshset(objects):
    verts=[];faces=[];owners=[]
    for ob in objects:
        me=ob.data;me.calc_loop_triangles();off=len(verts);verts.extend(ob.matrix_world@v.co for v in me.vertices)
        for t in me.loop_triangles:
            faces.append(tuple(off+i for i in t.vertices));owners.append((ob.name,me.materials[t.material_index].name,t.index))
    return BVHTree.FromPolygons(verts,faces,all_triangles=True),verts,faces,owners

ap=argparse.ArgumentParser();ap.add_argument('--blend',type=Path,required=True);ap.add_argument('--data',type=Path,required=True);ap.add_argument('--out',type=Path,required=True);ap.add_argument('--step',type=int,default=5)
a=ap.parse_args(sys.argv[sys.argv.index('--')+1:]);bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()))
profile=json.loads((a.data/'vehicle.json').read_text());drives={l['drivePart'] for d in profile['doors'] for l in d['linkage']};moving=set(range(4,4+2*len(profile['doors'])))|drives
rows=[]
for lod in range(3):
    for i in range(3):bpy.data.collections['LOD%d'%i].hide_viewport=i!=lod
    bpy.context.scene.frame_set(0);bpy.context.view_layer.update()
    obs=[o for o in bpy.data.collections['LOD%d'%lod].all_objects if o.type=='MESH' and o.get('bake')!='only']
    fixed=meshset([o for o in obs if o['cdlc_part'] not in moving]);sets={p:[o for o in obs if o['cdlc_part']==p] for p in moving}
    for frame in range(0,101,a.step):
        bpy.context.scene.frame_set(frame);bpy.context.view_layer.update()
        trees={p:meshset(v) for p,v in sets.items()};contacts=[]
        for part,tree in trees.items():
            for other,against in [(0,fixed)]+[(p,v) for p,v in trees.items() if p>part]:
                pairs=tree[0].overlap(against[0])
                if not pairs:continue
                samples=[];bounds=[];groups={};seal_touch=True
                for i,j in pairs:
                    va=[tree[1][k] for k in tree[2][i]];vb=[against[1][k] for k in against[2][j]]
                    low=[max(min(p[k] for p in va),min(p[k] for p in vb)) for k in range(3)]
                    high=[min(max(p[k] for p in va),max(p[k] for p in vb)) for k in range(3)]
                    bounds.append((low,high))
                    seal_touch &= frame==0 and part in (4,6,8) and other==part+1 and tree[3][i][1]=='Rubber' and against[3][j][1]=='Rubber' and high[1]-low[1]<2e-6
                    key=(tree[3][i][1],against[3][j][0],against[3][j][1],round((low[2]+high[2])*.5,1))
                    group=groups.setdefault(key,{'material':key[0],'other':key[1],'other_material':key[2],'height':key[3],'count':0,'point':[(a+b)*.5 for a,b in zip(low,high)],'triangles':[i,j]})
                    group['count']+=1
                    if len(samples)<12:samples.append({'a':tree[3][i],'b':against[3][j],'overlap_low':low,'overlap_high':high,'vertices_a':[list(v) for v in va],'vertices_b':[list(v) for v in vb]})
                contacts.append({'part':part,'against':other,'pairs':len(pairs),'closed_seal_contact_only':bool(seal_touch),'bounds_low':[min(v[0][k] for v in bounds) for k in range(3)],'bounds_high':[max(v[1][k] for v in bounds) for k in range(3)],'groups':list(groups.values()),'examples':samples})
        rows.append({'lod':lod,'frame':frame,'contacts':contacts})
        print('DOOR_CONTACTS',lod,frame,[(c['part'],c['against'],c['pairs']) for c in contacts],flush=True)
failures=[{'lod':r['lod'],'frame':r['frame'],'part':c['part'],'against':c['against'],'pairs':c['pairs']} for r in rows for c in r['contacts'] if not c['closed_seal_contact_only']]
a.out.write_text(json.dumps({'source_sha256':hashlib.sha256(a.blend.read_bytes()).hexdigest(),'step_percent':a.step,'rows':rows,'failures':failures,'scope':'Actual opaque triangles over sampled poses. Only the closed mating rubber-seal plane with at most 2 micrometres overlap is classified as contact. No mechanism or body intersection exclusions. Glass optics, human reach and continuous collision proof are separate.'},indent=2))
print('DOOR_CHECK',len(failures),'unexpected intersection groups')
if failures:raise RuntimeError('Door/body or moving-part intersections remain')
