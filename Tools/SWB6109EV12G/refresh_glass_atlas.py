"""Rebuild only the nine glass PNG aliases from the saved, unmodified source.

Run with Blender --background --python-exit-code 1 --python this_file --
--report <json>. No FBX, SWB1, body PNG or blend is written.
"""
import argparse
import ast
import hashlib
import json
from pathlib import Path
import sys
import bpy

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]
NAME='SWB6109EV12G'
OUT=ROOT/'Asset'/NAME
SOURCE=ROOT/'ModelSource'/NAME/(NAME+'.blend')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--report',type=Path,required=True)
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    protected=[SOURCE,SOURCE.parent/'sunwin-mesh.bin.gz',*sorted(OUT.glob('*.fbx')),
               *sorted(p for p in OUT.glob('*.png') if '_Gls_' not in p.name)]
    before={str(p.relative_to(ROOT)):digest(p) for p in protected}
    old_glass={p.name:digest(p) for p in sorted(OUT.glob('*_Gls_*.png'))}
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    master=bpy.data.collections[NAME+' | editable components']
    mats=sorted({m for o in master.all_objects if o.type in {'MESH','CURVE','FONT'}
                 for m in o.data.materials if m is not None and m.get('glass')},key=lambda m:m.name)
    assert mats,'No source glass materials'
    tree=ast.parse((HERE/'export_helpers.py').read_text(encoding='utf-8-sig'))
    node=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='write_atlas')
    namespace={'bpy':bpy,'OUT':OUT}
    exec(compile(ast.Module(body=[node],type_ignores=[]),str(HERE/'export_helpers.py'),'exec'),namespace)
    for lod in range(3):namespace['write_atlas'](NAME+(f'_LOD{lod}' if lod else '')+'_Gls',mats)
    after={str(p.relative_to(ROOT)):digest(p) for p in protected}
    assert before==after,'Glass-only refresh changed protected source, runtime, FBX or body textures'
    new_glass={p.name:digest(p) for p in sorted(OUT.glob('*_Gls_*.png'))}
    assert set(new_glass)==set(old_glass) and len(new_glass)==9
    changed=[name for name in old_glass if old_glass[name]!=new_glass[name]]
    assert len(changed)==3 and all(name.endswith('_MaskMap.png') for name in changed),changed
    report={'status':'PASS','source_sha256':digest(SOURCE),'protected_before':before,'protected_after':after,
            'protected_bytes_unchanged':True,'source_materials':[m.name for m in mats],
            'glass_png_before':old_glass,'glass_png_after':new_glass,'changed_files':changed,
            'change':'BH/GlsShader MaskMap G is AO=1 including padding; independent packaged _CoatMask preserves source coat=.65',
            'writer_sha256':digest(HERE/'export_helpers.py')}
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(report,indent=2)+'\n')
    print('GLASS_ATLAS_REFRESH_PASS',json.dumps({'source_sha256':report['source_sha256'],'changed_files':changed}),flush=True)


if __name__=='__main__':main()
