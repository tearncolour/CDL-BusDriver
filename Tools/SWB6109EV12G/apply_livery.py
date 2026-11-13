"""Make a separate editable custom-colour Blender variant, preserving the master.

blender --background --python apply_livery.py -- --color 245DB7 --output path.blend
The FBX atlas already uses neutral white under the ControlMask red channel, so
game-side line colours do not require rebuilding the mesh or this preview file.
"""
import bpy
import sys
import argparse
import hashlib
import re
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
parser=argparse.ArgumentParser()
parser.add_argument('--color',default='245DB7')
parser.add_argument('--output',required=True)
parser.add_argument('--texture',help='A PNG image using the five-zone CDLCWrapUV guide')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
hex_color=args.color.removeprefix('#')
if re.fullmatch(r'[0-9A-Fa-f]{6}',hex_color) is None:
    parser.error('--color must be exactly six hexadecimal RGB digits, with an optional # prefix')
srgb=tuple(int(hex_color[i:i+2],16)/255. for i in (0,2,4))
rgb=tuple(c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4 for c in srgb)
source=(ROOT/'ModelSource/SWB6109EV12G/SWB6109EV12G.blend').resolve()
out=Path(args.output).resolve()
if out==source or (out.exists() and out.samefile(source)):
    parser.error('--output must be a separate variant path; the master cannot be overwritten')
source_hash=hashlib.sha256(source.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(source))
for mat in bpy.data.materials:
    if mat.get('livery_control'):
        mat.diffuse_color=(*rgb,1)
        mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value=(*rgb,1)
        if args.texture:
            tex=mat.node_tree.nodes.new('ShaderNodeTexImage')
            tex.image=bpy.data.images.load(str(Path(args.texture).resolve()),check_existing=True)
            uv=mat.node_tree.nodes.new('ShaderNodeUVMap');uv.uv_map='CDLCWrapUV'
            mat.node_tree.links.new(uv.outputs['UV'],tex.inputs['Vector'])
            mat.node_tree.links.new(tex.outputs['Color'],mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
bpy.context.scene['custom_livery_hex']=hex_color.upper()
bpy.context.scene['custom_livery_srgb']=srgb
bpy.context.scene['custom_livery_linear']=rgb
bpy.context.scene['custom_livery_source_sha256']=source_hash
bpy.ops.file.pack_all()
out.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(out))
print('CUSTOM_LIVERY_OK '+str(out))
