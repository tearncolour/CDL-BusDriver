"""User-selected side glazing and curtain-free cabin (2026-10-15).

The video remains the cabin reference, but its curtains are explicitly excluded
by the user. Only side-window panes receive the slightly darker glass; the
windscreen, destination glazing, rear window and animated door panes keep their
existing material. This module changes memory only.
"""
import re
import bpy

BASE_GLASS = '07 Solar glass | separate Gls export'
SIDE_GLASS = '07 Solar glass | side windows lightly tinted'
SIDE_LINEAR_RGB = (.30, .39, .3825)  # 75% of the original linear RGB
CURTAIN_OBJECT = re.compile(
    r'Photo cabin \| -?1 window \d+ (?:curtain track|gathered curtain|tie band|hanging clip [123])')
SIDE_WINDOW = re.compile(r'Side glazing -?1 \d+')


def apply_window_configuration(master, scene=None):
    scene = scene or bpy.context.scene
    removed = []
    for obj in list(master.objects):
        if not CURTAIN_OBJECT.fullmatch(obj.name):
            continue
        if not obj.get('photo_cabin_detail_revision') or int(obj.get('cdlc_part', -1)) != 14:
            raise RuntimeError('Unexpected ownership of curtain component: '+obj.name)
        removed.append(obj.name)
        data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        if data and not data.users:
            if isinstance(data, bpy.types.Mesh): bpy.data.meshes.remove(data)
            elif isinstance(data, bpy.types.Curve): bpy.data.curves.remove(data)
    if any('curtain' in obj.name.lower() or CURTAIN_OBJECT.fullmatch(obj.name) for obj in master.objects):
        raise RuntimeError('Unrecognized curtain component remains in the editable vehicle')
    base = bpy.data.materials[BASE_GLASS]
    side = bpy.data.materials.get(SIDE_GLASS)
    if side is None:
        side = base.copy()
        side.name = SIDE_GLASS
    side.diffuse_color = (*SIDE_LINEAR_RGB, 1.)
    bs = next(n for n in side.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bs.inputs['Base Color'].default_value = (*SIDE_LINEAR_RGB, 1.)
    side['cdlc_window_configuration'] = 'side-light-tint-no-curtains-v1'
    side['cdlc_tint_basis'] = 'User preference; linear RGB 0.75 of original glass, not measured optical transmittance'
    panes = []
    for obj in master.objects:
        if not SIDE_WINDOW.fullmatch(obj.name):
            continue
        if len(obj.data.materials) != 1 or obj.data.materials[0].name not in (BASE_GLASS, SIDE_GLASS):
            raise RuntimeError('Unexpected side-window material slots: '+obj.name)
        obj.data.materials[0] = side
        panes.append(obj.name)
    if len(panes) != 12:
        raise RuntimeError('Expected 12 fixed side-window panes, found '+str(len(panes)))
    scene['cdlc_window_configuration'] = 'side-light-tint-no-curtains-v1'
    return dict(status='APPLIED_IN_MEMORY', removed_curtain_objects=sorted(removed),
                tinted_side_panes=sorted(panes), side_linear_rgb=list(SIDE_LINEAR_RGB),
                base_material=BASE_GLASS, side_material=SIDE_GLASS,
                scope='Fixed side windows only; native alpha .28, roughness, IOR, coat, transmission and non-side glass unchanged')
