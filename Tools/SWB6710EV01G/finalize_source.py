"""Refresh authoring UVs after a build; does not change exported geometry."""
import bpy
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
path=ROOT/'ModelSource/SWB6710EV01G/SWB6710EV01G.blend'
bpy.ops.wm.open_mainfile(filepath=str(path))
exec(Path(__file__).with_name('livery_uv.py').read_text(encoding='utf-8'),globals())
prepare_livery_uv(bpy.data.collections['SWB6710EV01G | editable components'])
cam=bpy.context.scene.camera
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_rotation=cam.rotation_euler.to_quaternion()
            area.spaces.active.region_3d.view_location=(0,0,1.4)
            area.spaces.active.region_3d.view_distance=11
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=str(path))
print('AUTHORING_UV_OK')
