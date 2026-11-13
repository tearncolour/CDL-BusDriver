"""Bind completed vehicle validations to the same saved source and runtime mesh."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
ASSET=ROOT/'Asset/SWB6109EV12G'
TOOLS=ROOT/'Tools/SWB6109EV12G'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def entry(path):
    return {'path':path.relative_to(ROOT).as_posix(),'bytes':path.stat().st_size,'sha256':sha(path)}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--extra-report',type=Path,action='append',default=[])
    args=parser.parse_args()
    required=('runtime-validation.json','source-validation.json','native-validation.json',
              'asset-validation.json','wheel-clearance.json','sign-enclosure-validation.json',
              'mirror-mount-validation.json','mirror-joint-validation.json',
              'window-configuration-validation.json')
    reports={name:json.loads((SRC/name).read_text(encoding='utf-8')) for name in required}
    for name,data in reports.items():
        if data.get('status')!='PASS':raise RuntimeError('Validation is not PASS: '+name)
    master=SRC/'SWB6109EV12G.blend';binary=SRC/'sunwin-mesh.bin.gz'
    source_hash=sha(master);runtime_hash=sha(binary)
    if reports['runtime-validation.json']['sha256']!=runtime_hash:raise RuntimeError('Runtime validation is stale')
    for name in ('source-validation.json','asset-validation.json','wheel-clearance.json',
                 'sign-enclosure-validation.json','mirror-mount-validation.json','mirror-joint-validation.json'):
        if reports[name].get('source_sha256')!=source_hash:raise RuntimeError('Saved source validation is stale: '+name)
    native=reports['native-validation.json']
    for row in native['final_artifacts']:
        if sha(SRC/row['file'])!=row['sha256']:raise RuntimeError('Native artifact binding is stale: '+row['file'])
    for row in native['fbx']:
        if sha(ASSET/row['file'])!=row['sha256']:raise RuntimeError('Native FBX validation is stale: '+row['file'])
    for row in reports['asset-validation.json']['verified_files']:
        if sha(ROOT/row['path'])!=row['sha256']:raise RuntimeError('Asset validation is stale: '+row['path'])
    validation_paths=[SRC/name for name in required]
    for path in args.extra_report:
        path=path.resolve();data=json.loads(path.read_text(encoding='utf-8'))
        status=data.get('status',data.get('final_classification',{}).get('status'))
        if status!='PASS':raise RuntimeError('Extra validation is not PASS: '+str(path))
        for key in ('source_sha256','source_sha256_before','source_sha256_after','master_sha256'):
            if key in data and data[key].lower()!=source_hash:raise RuntimeError('Extra report source binding is stale: '+str(path))
        if 'runtime_sha256' in data and data['runtime_sha256'].lower()!=runtime_hash:
            raise RuntimeError('Extra report runtime binding is stale: '+str(path))
        binary_binding=data.get('binary')
        if isinstance(binary_binding,dict) and binary_binding.get('sha256','').lower()!=runtime_hash:
            raise RuntimeError('Extra report binary binding is stale: '+str(path))
        resource_records=data.get('embedded_resources',{}).get('records',[])
        model_records=[row for row in resource_records
                       if row.get('model','SWB6109EV12G')=='SWB6109EV12G']
        if resource_records and not model_records:
            raise RuntimeError('Extra report has no embedded resource for this model: '+str(path))
        # A full regression report also records other vehicles. Compare this
        # manifest's binary only with records explicitly belonging to this bus.
        for row in model_records:
            if row.get('sha256','').lower()!=runtime_hash:
                raise RuntimeError('Extra report embedded resource binding is stale: '+str(path))
        saved_master=data.get('saved_master',{})
        if saved_master and saved_master.get('sha256','').lower()!=source_hash:
            raise RuntimeError('Extra report saved master binding is stale: '+str(path))
        for row in data.get('reviewed_views',[]):
            image_hash=row.get('sha256',row.get('image_sha256'))
            if image_hash:
                image_path=SRC/'previews'/row.get('file',row.get('view','')+'.png')
                if sha(image_path)!=image_hash.lower():raise RuntimeError('Reviewed preview changed: '+str(image_path))
        for row in data.get('images',[]):
            if isinstance(row,dict) and 'file' in row and 'sha256' in row:
                if sha(SRC/row['file'])!=row['sha256'].lower():raise RuntimeError('Reviewed image changed: '+row['file'])
        validation_paths.append(path)
    model_files=[master,binary]+sorted(p for p in ASSET.iterdir() if p.suffix.lower() in ('.fbx','.png'))
    preview_path=SRC/'preview-manifest.json'
    if preview_path.exists():
        previews=json.loads(preview_path.read_text(encoding='utf-8'))
        if previews.get('source_sha256','').lower()!=source_hash:raise RuntimeError('Preview manifest refers to another saved source')
        for row in previews.get('views',{}).values():
            path=SRC/'previews'/row['file']
            if sha(path)!=row['sha256'].lower():raise RuntimeError('Preview file changed: '+str(path))
            model_files.append(path)
    metadata=[p for p in SRC.iterdir() if p.is_file() and p.suffix.lower() in ('.json','.md','.txt','.svg') and p.name!='validation-manifest.json']
    source_files=sorted([p for p in TOOLS.iterdir() if p.suffix.lower() in ('.py','.ps1','.txt')]+list((TOOLS/'KitExport').glob('*.cs'))+list((TOOLS/'KitExport').glob('*.csproj')))
    report={'status':'PASS','vehicle':'SWB6109EV12G','created_utc':datetime.now(timezone.utc).isoformat(),
            'scope':'Generated editable model, native import assets and CDLC mesh; no installed-game playtest is asserted.',
            'source_sha256':source_hash,'runtime_sha256':runtime_hash,
            'checks':{name:'PASS' for name in required},
            'validation_reports':[entry(p) for p in validation_paths],
            'files':[entry(p) for p in sorted(set(model_files+metadata))],
            'source_scripts':[entry(p) for p in source_files]}
    (SRC/'validation-manifest.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('SWB6109_MANIFEST_PASS source='+source_hash+' runtime='+runtime_hash)


if __name__=='__main__':main()
