"""Validate all four saved wheelhouse/side-panel interfaces without modifying files."""
import argparse,hashlib,json,sys
from pathlib import Path
import bpy
sys.path.insert(0,str(Path(__file__).resolve().parent))
from wheelhouse_seams import verify_wheelhouse_seams
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--report',type=Path,required=True)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
source=Path(bpy.data.filepath);before=hashlib.sha256(source.read_bytes()).hexdigest()
report=verify_wheelhouse_seams(bpy.data.collections['SWB6109EV12G | editable components'])
report['source_sha256']=before;report['source']=str(source)
assert hashlib.sha256(source.read_bytes()).hexdigest()==before
args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(report,indent=2)+'\n')
print('WHEELHOUSE_SEAMS_PASS')
