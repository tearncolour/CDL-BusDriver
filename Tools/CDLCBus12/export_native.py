"""Vehicle export with explicit periodic tile splitting before the standard bake and FBX checks."""
import hashlib,json,runpy,sys
from pathlib import Path
import bpy

devkit=Path.home()/'Desktop/CDLDevKit';sys.path.insert(0,str(devkit/'blender'))
import export_vehicle,pattern_tiles
sys.path.insert(0,str(Path(__file__).resolve().parent))
import periodic_uv
periodic_uv.install()
original=export_vehicle.section_objects
splits=[]
original_values=export_vehicle.vehicle_values
split_complete=False

def vehicle_values(material):
    value=original_values(material)
    if value.get('pattern'):
        if split_complete and material.name not in periodic_uv.PATTERNED:value['pattern']=None
        else:value['pattern']['_cdlc_material']=material.name
    return value
export_vehicle.vehicle_values=vehicle_values
def section_objects(*args,**kwargs):
    global split_complete
    objects=original(*args,**kwargs)
    names={name for ob in objects for _,_,name in ob['tris']}
    values={name:export_vehicle.vehicle_values(bpy.data.materials[name]) for name in sorted(names)}
    splits.append(periodic_uv.split(objects,values))
    split_complete=True
    return objects
export_vehicle.section_objects=section_objects
args=sys.argv[sys.argv.index('--')+1:]
out=Path(args[args.index('--out')+1])/'export-summary.json'
try:runpy.run_path(str(Path(__file__).resolve().parents[1]/'VehicleExport/export_v3.py'),run_name='__main__')
finally:
    if out.is_file():
        report=json.loads(out.read_text());report['periodic_pattern_split']=splits
        report['periodic_pattern_adapter_sha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
        report['periodic_uv_sha256']=hashlib.sha256(Path(periodic_uv.__file__).read_bytes()).hexdigest()
        report['periodic_uv_policy']={'parts':[1,11,12],'lods':[0],'continuation_per_edge':periodic_uv.PAD,
            'geometry':'Only grid cut positions change within the continued tile; source vertices and per-triangle area are preserved.',
            'runtime':'All eligible SWB2 finishes retain their detail UV. Native far LOD omits these micro-surface tiles.'}
        out.write_text(json.dumps(report,indent=2),encoding='utf-8')
