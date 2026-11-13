"""Record BUS_ASSET_SPEC gates separately from geometry/build success.

Default mode records unfinished release work. --release fails unless every
required gate, including actual game screenshots, is current and passes.
"""
import argparse
import hashlib
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
SRC=ROOT/'ModelSource/SWB6109EV12G'
NAME='SWB6109EV12G'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release',action='store_true')
    args=parser.parse_args()
    source_hash=sha(SRC/(NAME+'.blend'))
    runtime_hash=sha(SRC/'sunwin-mesh.bin.gz')
    package_hash=sha(ROOT/'Asset'/(NAME+'.cok'))
    gates=[]
    for filename in ('source-validation.json','handedness-validation.json',
                     'interior-clearance-validation.json','lod-source-validation.json',
                     'handrail-validation.json',
                     'seat-access-validation.json','material-validation.json','window-configuration-validation.json','wheelhouse-seam-validation.json',
                     'sign-enclosure-validation.json',
                     'mirror-mount-validation.json','mirror-joint-validation.json',
                     'strap-validation.json','wheel-clearance.json','runtime-validation.json',
                     'asset-validation.json','native-validation.json',
                     'runtime-integration-review.json','interior-visual-review.json',
                     'game-package-validation.json','game-glass-validation.json',
                     'game-renderer-visual-review.json','game-mirror-validation.json',
                     'gameplay-visual-review.json'):
        path=SRC/filename
        if not path.is_file():
            gates.append({'report':filename,'status':'NOT_RUN'});continue
        report=json.loads(path.read_text(encoding='utf-8'))
        status=report.get('status','INCOMPLETE')
        bindings={k:report[k] for k in ('source_sha256','runtime_sha256','package_sha256') if k in report}
        stale=any(value!=dict(source_sha256=source_hash,runtime_sha256=runtime_hash,
                             package_sha256=package_hash)[key] for key,value in bindings.items())
        if filename=='runtime-validation.json':stale|=report.get('sha256')!=runtime_hash
        if filename.startswith('game') and report.get('package_sha256')!=package_hash:stale=True
        if filename.startswith('game') and not package_hash:stale=True
        if filename in ('game-renderer-visual-review.json','game-mirror-validation.json',
                        'gameplay-visual-review.json'):
            images=report.get('images',[])
            if not images:status='INCOMPLETE'
            for image in images:
                image_file=image.get('file',image.get('path'))
                if not image_file or not image.get('sha256'):
                    status='INCOMPLETE'
                elif sha(ROOT/image_file)!=image['sha256']:
                    stale=True
        gates.append({'report':filename,'status':'STALE' if stale else status})
    warnings=[]
    build_path=SRC/'build-report.json'
    if build_path.is_file():
        build=json.loads(build_path.read_text(encoding='utf-8'))
        for row,budget in zip(build.get('lods',[]),(80000,25000,8000)):
            if row['body_triangles']>budget:
                warnings.append(f"LOD{row['lod']}: {row['body_triangles']} triangles exceed suggested {budget}")
    package=ROOT/'Asset'/(NAME+'.cok')
    if package.is_file() and package.stat().st_size>10*1024*1024:
        warnings.append('Package exceeds suggested 10 MiB budget')
    pending=[gate for gate in gates if gate['status']!='PASS']
    report={'status':'INCOMPLETE' if pending else 'PASS','standard':'BUS_ASSET_SPEC.md',
            'standard_sha256':sha(ROOT/'BUS_ASSET_SPEC.md'),'source_sha256':source_hash,
            'runtime_sha256':runtime_hash,'package_sha256':package_hash,
            'gates':gates,'budget_warnings':warnings,
            'scope':'Release readiness; build/geometry PASS alone does not complete the game asset checks.'}
    (SRC/'spec-compliance.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False))
    if args.release and pending:raise SystemExit(1)


if __name__=='__main__':main()
