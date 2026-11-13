"""Stage verified native source files in the project and retain every replaced file."""
import argparse,hashlib,json,shutil
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument('--native',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True)
a=p.parse_args();root=Path(__file__).resolve().parents[2];native=a.native.resolve();evidence=a.evidence.resolve();destination=root/'Asset/CDLCBus12'
if not native.is_relative_to(root) or not evidence.is_relative_to(root):raise ValueError('Project paths required')
if evidence.exists():raise ValueError('New evidence directory required')
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
summary=native/'export-summary.json';export=json.loads(summary.read_text());verify=json.loads((native/'verify-summary.json').read_text())
assert verify['export_summary_sha256']==sha(summary)
assert sha(Path(export['source']))==export['source_sha256']
files={}
for row in export['meshes']:
    path=Path(row['path']);files[path]=row['sha256']
for atlas in export['atlases']:
    for row in atlas['files']:files[Path(row['path'])]=row['sha256']
for path,digest in files.items():
    assert path.resolve().is_relative_to(native) and path.name.startswith('CDLCBus12') and path.suffix.lower() in ('.png','.fbx')
    assert sha(path)==digest,path
evidence.mkdir();backup=evidence/'before';backup.mkdir();rows=[]
for path,digest in files.items():
    target=destination/path.name;previous=sha(target) if target.is_file() else None
    if previous:shutil.copy2(target,backup/path.name)
    shutil.copy2(path,target);assert sha(target)==digest
    rows.append({'file':str(target),'before_sha256':previous,'after_sha256':digest,'bytes':target.stat().st_size})
legacy=root/'out/cdlc12-rebuild-candidate/Asset/CDLCBus12.cok';excluded=None
if legacy.is_file():
    retained=evidence/'excluded-legacy-CDLCBus12.cok';digest=sha(legacy);shutil.move(str(legacy),str(retained));assert sha(retained)==digest
    excluded={'former_candidate_path':str(legacy),'retained_path':str(retained),'sha256':digest,'reason':'Old COK copied by the project build does not match the rebuilt source; excluded from the private compile candidate.'}
record={'source_sha256':export['source_sha256'],'export_summary_sha256':sha(summary),'files':rows,'excluded_candidate_cok':excluded,
    'scope':'Project source assets and private compile candidate only. Installed Mods and running game resources were not changed. New COK packaging and game import are still required.'}
(evidence/'manifest.json').write_text(json.dumps(record,indent=2));print(json.dumps({'files':len(rows),'bytes':sum(r['bytes'] for r in rows),'legacy_candidate_cok_excluded':bool(excluded)}))
