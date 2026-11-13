"""Bind candidate evidence to the actual source/resources; keep game acceptance separate."""
import argparse,hashlib,json,re
from pathlib import Path

ap=argparse.ArgumentParser();ap.add_argument('--data',type=Path,required=True);ap.add_argument('--evidence',type=Path,required=True);ap.add_argument('--revision',required=True);ap.add_argument('--out',type=Path,required=True)
ap.add_argument('--native',type=Path);ap.add_argument('--analysis',type=Path);ap.add_argument('--reviews',type=Path);ap.add_argument('--visual',type=Path)
a=ap.parse_args();r=a.revision;e=a.evidence
if a.out.exists():raise ValueError('Retain previous handoff; select a new report path')
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
source=a.data/'source/CDLCBus12.blend';runtime=a.data/'CDLCBus12.swb2.gz';uv=a.data/'CDLCBus12.swu1.gz'
checks={};paths={'doors':e/('doors-'+r+'.json'),'assembly':e/('assembly-'+r+'/assembly.json')}
rear_jambs=e/('rear-jambs-'+r+'.json')
if rear_jambs.is_file():paths['rear_jambs']=rear_jambs
backfaces=e/('backfaces-final-'+r+'.json');paths['backfaces']=backfaces if backfaces.is_file() else e/('backfaces-'+r+'.json')
if (a.data/'strap-motion.json').is_file():paths['straps']=e/('straps-'+r+'b.json')
for name,path in paths.items():
    value=json.loads(path.read_text());assert value['source_sha256']==digest(source),(name,'stale source');assert not value['failures'],name
    checks[name]={'report':str(path),'sha256':digest(path),'source_matched':True}
files=[source,runtime,uv,a.data/'vehicle.json',a.data/'door-motion.json',Path('out/cdlc12-rebuild-candidate/CDLC.BusDriver.dll')]
if (a.data/'strap-motion.json').is_file():files.append(a.data/'strap-motion.json')
frames=json.loads((a.data/'frames.json').read_text());strap_count=sum(f['name'].startswith('Strap') for f in frames)
record={'stage':'REGIONS_REVIEWED_FINAL_RENDER_EXPORT_IN_PROGRESS','revision':r,
    'files':[{'path':str(p.resolve()),'sha256':digest(p),'bytes':p.stat().st_size} for p in files],
    'geometry_checks':checks,'source_statistics':json.loads((a.data/'source/source-report.json').read_text()),
    'normal_tile_evidence':str(e/'surface-audit-15b.json'),
    'scope':'Original generic CDLC 12 m model and resolved generic interiors; not a factory replica of a named production vehicle.',
    'part_contract':'Indices and semantic roles 0..33 retained. Default bus door shafts 34..39, independent straps start at 40. Generic articulated fronts retain joint 34..42 and append drives before straps.',
    'door_design':'Same shaft/crank and straight-guide solver as SWB6710EV01G/SWB6109EV12G. Dimensions adapted to the generic bus, not copied as factory measurements.',
    'straps':{'count':strap_count,'placement':'Standing-area intervals minus actual pole stations and the full 0.7 rad pendulum envelope with the maximum 1.2 strap factor. Existing swing amplitude is retained.'},
    'deployment_performed':False,'game_validated':False,'native_pack_rebuilt':False,
    'unresolved':['Current running game belongs to an existing session: no live DLL/package replacement or process termination.',
        'Native game reader/import, new COK packaging/cache checks, actual game material/glass/mirror tests and cold load remain unperformed.',
        'Compiled DLL and native source exports are not a coordinated release package.',
        'Door checks sample opaque mesh intersections at 1% intervals and strap checks sample 75 production poses per LOD; continuous collision proof and occupant reach/usability are not established.']}
if a.native and a.analysis:
    export_path=a.native/'export-summary.json';verify_path=a.native/'verify-summary.json'
    exported=json.loads(export_path.read_text());verified=json.loads(verify_path.read_text());analysis=json.loads(a.analysis.read_text())
    assert exported['source_sha256']==digest(source);assert verified['export_summary_sha256']==digest(export_path)
    assert not analysis['hard_failures'];assert any(i['role']=='export-summary' and i['sha256']==digest(export_path) for i in analysis['inputs'])
    record['native']={'export':str(export_path.resolve()),'verify':str(verify_path.resolve()),'analysis':str(a.analysis.resolve()),
        'hard_failures':[],'advisory_status':analysis['status'],'advisory_locations':str((e/('native-anomaly-locations-'+r+'.json')).resolve()),
        'files':[{'path':m['path'],'sha256':m['sha256'],'triangles':m['triangles'],'lod':m['lod'],'glass':m['glass']} for m in exported['meshes']]}
    native=[next(m['triangles'] for m in exported['meshes'] if m['lod']==lod and not m['glass']) for lod in range(3)]
    record['budget']={'recommended_body_triangles':[80000,25000,8000],'source':record['source_statistics']['lod_triangles_without_wrap'],'native_opaque':native,
        'note':'Native detail-atlas cuts add triangles. Closed structural parts and door mechanisms are retained; dials, trim, wheel detail and native LOD2 microtiles are reduced or omitted. Recommendations are exceeded in some LODs; game performance has not been measured.'}
else:record['unresolved'].append('Final native readback and evidence analysis are incomplete.')
if a.reviews and a.visual:
    reviews=json.loads(a.reviews.read_text());visual=json.loads(a.visual.read_text())
    assert reviews['source_sha256']==digest(source) and visual['source_sha256']==digest(source) and visual['reviewed']
    record['visual']={'manifest':str(a.reviews.resolve()),'images':len(reviews['views']),'review':str(a.visual.resolve()),'scope':visual['scope']}
else:record['unresolved'].append('Final-source visual review is incomplete.')
if a.native and a.analysis and a.reviews and a.visual:record['stage']='OFFLINE_CANDIDATE_REVIEWED_PACKAGE_AND_GAME_PENDING'
embedded=e/('embedded-resources-'+r+'.json')
if embedded.is_file():
    value=json.loads(embedded.read_text());assert value['pass']
    record['compiled_resources']={'report':str(embedded.resolve()),'sha256':digest(embedded),'mvid':value['mvid'],'resource_count':len(value['resources'])}
regression=e/('generic-check-'+r+'b.log')
if not regression.is_file():regression=e/('generic-check-'+r+'.log')
match=re.search(r'Generic checks: (\d+), failures: (\d+)',regression.read_text(errors='replace'))
assert match and int(match[2])==0
record['regression']={'report':str(regression.resolve()),'sha256':digest(regression),'checks':int(match[1]),'failures':0}
staged=e/('repo-native-update-'+r+'/manifest.json')
if staged.is_file():
    value=json.loads(staged.read_text());assert value['source_sha256']==digest(source)
    for row in value['files']:assert digest(Path(row['file']))==row['after_sha256']
    record['project_native_assets']={'manifest':str(staged.resolve()),'sha256':digest(staged),'files':len(value['files']),'installed_resources_changed':False,'legacy_cok_excluded':value['excluded_candidate_cok']}
history=e/('history-'+r+'.json')
if history.is_file():record['retained_history']={'path':str(history.resolve()),'sha256':digest(history)}
a.out.write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
print(record['stage'])
