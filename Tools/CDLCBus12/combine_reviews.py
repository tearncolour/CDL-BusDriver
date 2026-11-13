"""Retain final-source review provenance and explicitly replace the obstructed front-door cameras."""
import argparse,copy,json,os
from pathlib import Path

p=argparse.ArgumentParser()
for key in ('full','doors','fill','mounts','out'):p.add_argument('--'+key,type=Path,required=True)
for key in ('front','straps'):p.add_argument('--'+key,type=Path)
a=p.parse_args();a.out.mkdir(parents=True,exist_ok=False)
reports={k:json.loads(getattr(a,k).read_text()) for k in ('full','doors','front','fill','mounts','straps') if getattr(a,k)}
hashes={v['source_sha256'] for v in reports.values()};assert len(hashes)==1,'Different source revisions'
replaced={v['image'] for v in reports.get('front',{}).get('views',[])}
assert not a.front or len(replaced)==9
assert all('-door0-inside-' in v for v in replaced)
all_views=[];doors=[];excluded=[]
for key,report in reports.items():
    for view in report['views']:
        path=getattr(a,key).parent/view['image'];assert path.is_file(),path
        if key=='doors' and view['image'] in replaced:
            excluded.append({'image':str(path.resolve()),'reason':'Original front-door interior camera was obstructed by farebox; replaced by formal driver eye.'});continue
        item=copy.deepcopy(view);item['image']=os.path.relpath(path.resolve(),a.out.resolve()).replace('\\','/')
        item['review_group']=key;item['lighting_evidence']=str(getattr(a,key).resolve())
        all_views.append(item)
        if key in ('doors','front'):doors.append(item)
assert len(doors)==54
base={'source_sha256':next(iter(hashes)),'source':reports['full']['source'],'native_or_game_validated':False,
      'reviews':{k:str(getattr(a,k).resolve()) for k in reports},'excluded':excluded}
for name,views in (('all-views',all_views),('door-views',doors)):
    (a.out/(name+'.json')).write_text(json.dumps(dict(base,views=views),indent=2),encoding='utf-8')
print(json.dumps({'accepted_images':len(all_views),'door_images':len(doors),'excluded':len(excluded)}))
