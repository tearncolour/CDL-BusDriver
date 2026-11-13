"""6109 source materials matched to the real CDLC runtime palette.

The C# palette is authoritative: KitExport writes sRGB, while Blender BSDF
colour inputs are linear. Apply after all geometry patches, before validation
and export. This module changes material properties only and never saves.
"""
import json
from pathlib import Path
import bpy

ROOT = Path(__file__).resolve().parents[2]
PALETTE_PATH = ROOT/'ModelSource/SWB6109EV12G/kit-cabin.json'
# These native materials keep their BSDF and therefore their existing native
# atlas pixels. Only their runtime swatch metadata is assigned. The runtime
# glossy dielectric approximates metallic .15 as 0; colour and roughness match.
SOURCE_PRESERVING_SWATCHES = {
    '02 Shanghai aqua twin stripe': {'swatch':'ShanghaiAqua',
        'linear_rgb':(.12,.47,.51), 'metallic':.15, 'roughness':.28},
}
MATERIAL_SWATCHES = {
    **{name:row['swatch'] for name,row in SOURCE_PRESERVING_SWATCHES.items()},
    '06 Cabin graphite': 'CabinGraphite',
    '11 Low floor charcoal vinyl': 'CharcoalVinyl',
    '6109 photo | brushed stainless handrails': 'BrushedStainless',
    'Seat upholstery | photo dark grey': 'SeatFabricGrey',
    'Seat hard rear shell | photo dark grey': 'SeatShellGrey',
    '6109 seat print | Yellow': 'SeatPrintYellow',
    '6109 seat print | Grey': 'SeatPrintGrey',
    '6109 rear seat print | Yellow': 'RearPrintYellow',
    '6109 rear seat print | Grey': 'RearPrintGrey',
    '6109 photo | grey moulded hand grips': 'HandGripGrey',
    'Photo cabin | blue grey curtain fabric': 'CurtainBlueGrey',
    'Photo cabin | curtain rail silver': 'CabinSilver',
    'Photo cabin | service box warm grey': 'CabinPanelGrey',
    'Photo cabin | curtain tie and clips': 'CurtainClipGrey',
    'Photo cabin | backed recess interior': 'CabinRecessGrey',
    '6109 photo | door handle mounting feet': 'DoorFeetGrey',
    '6109 photo | amber request-button enclosure': 'RequestAmber',
    '6109 photo | dark request-button face': 'RequestDark',
    '6109 photo | exterior button red centre': 'RequestRed',
    '6109 photo | exterior button yellow surround': 'RequestYellow',
    '6109 photo | accessibility green field': 'AccessibilityGreen',
    '6109 photo | accessibility white ink': 'AccessibilityWhite',
    '6109 photo | exterior button mounting gasket': 'GasketGrey',
}


def linear(value):
    return value/12.92 if value <= .04045 else ((value+.055)/1.055)**2.4


def apply_material_contract(master, palette_path=None):
    path = Path(palette_path) if palette_path else PALETTE_PATH
    palette = {row['name']: row for row in json.loads(path.read_text(encoding='utf-8'))['palette']}
    missing = set(MATERIAL_SWATCHES.values())-set(palette)
    if missing:
        raise RuntimeError('Regenerate KitExport palette before applying material contract; missing '+', '.join(sorted(missing)))
    # The photographed ink follows the substrate: front upholstery remains
    # textile, while the hard rear shell and its printed bands share a smooth finish.
    # Reassign only material slots; do not touch the fitted ribbon mesh or parent.
    for obj in master.all_objects:
        if not obj.name.startswith(('6109 seat print | ', 'Photo seat print | ')) or 'rear shell' not in obj.name:
            continue
        for i, material in enumerate(obj.data.materials):
            if material is None or material.name not in {'6109 seat print | Yellow','6109 seat print | Grey'}:
                continue
            target='6109 rear seat print | '+material.name.rsplit(' | ',1)[1]
            replacement=bpy.data.materials.get(target)
            if replacement is None:
                replacement=material.copy();replacement.name=target
            obj.data.materials[i]=replacement
    materials = {mat.name: mat for obj in master.all_objects
                 if obj.type in {'MESH','CURVE','FONT'} for mat in obj.data.materials if mat}
    rows = []
    for name, material in sorted(materials.items()):
        swatch = MATERIAL_SWATCHES.get(name)
        if name.startswith('6109 MeshKit '):
            swatch = name[len('6109 MeshKit '):]
        if swatch is None:
            continue
        if swatch not in palette:
            raise RuntimeError('Unknown MeshKit source material '+name)
        entry = palette[swatch]
        shader = next((node for node in material.node_tree.nodes if node.type == 'BSDF_PRINCIPLED'), None)
        if shader is None:
            raise RuntimeError('Missing source Principled BSDF: '+name)
        before = {'linear_rgb':list(material.diffuse_color[:3]),
                  'metallic':float(shader.inputs['Metallic'].default_value),
                  'roughness':float(shader.inputs['Roughness'].default_value),
                  'swatch':material.get('cdlc_palette_swatch')}
        color = tuple(linear(float(value)) for value in entry['rgb'])
        if name in SOURCE_PRESERVING_SWATCHES:
            native = SOURCE_PRESERVING_SWATCHES[name]
            if max(abs(a-b) for a,b in zip(color,native['linear_rgb'])) > 1e-6:
                raise RuntimeError('Native-preserving runtime palette changed colour: '+name)
            if max(abs(a-b) for a,b in zip(before['linear_rgb'],native['linear_rgb'])) > 1e-6 or \
               max(abs(a-b) for a,b in zip(shader.inputs['Base Color'].default_value[:3],native['linear_rgb'])) > 1e-6 or \
               abs(before['metallic']-native['metallic']) > 1e-6 or abs(before['roughness']-native['roughness']) > 1e-6:
                raise RuntimeError('Unexpected native stripe BSDF; review before assigning runtime metadata: '+name)
            if material.get('light_id') or material.get('livery_control') or material.get('glass'):
                raise RuntimeError('Fixed-colour native stripe cannot use light, livery or glass routing: '+name)
            material['cdlc_palette_swatch'] = swatch
            material['cdlc_runtime_finish'] = entry['finish']
            material['cdlc_material_contract_revision'] = 2
            material['cdlc_runtime_material_approximation'] = json.dumps({
                'native_metallic':before['metallic'], 'runtime_metallic':entry['metallic'],
                'native_roughness':before['roughness'], 'runtime_roughness':entry['roughness'],
                'note':'Native BSDF/atlas unchanged; runtime uses existing glossy dielectric finish.'},sort_keys=True)
            rows.append({'material':name,'before':before,'swatch':swatch,'finish':entry['finish'],
                'linear_rgb':before['linear_rgb'],'srgb':entry['rgb'],'metallic':before['metallic'],
                'roughness':before['roughness'],'coat':float(shader.inputs['Coat Weight'].default_value),
                'native_preserved':True,'runtime_metallic':entry['metallic'],'runtime_roughness':entry['roughness'],
                'runtime_approximation':'Exact colour and roughness; metallic .15 approximated by existing dielectric 0 finish.'})
            continue
        material.diffuse_color = (*color, 1.)
        shader.inputs['Base Color'].default_value = (*color, 1.)
        shader.inputs['Metallic'].default_value = float(entry['metallic'])
        shader.inputs['Roughness'].default_value = float(entry['roughness'])
        shader.inputs['Coat Weight'].default_value = 0.
        material['cdlc_palette_swatch'] = swatch
        material['cdlc_material_contract_revision'] = 1
        material['cdlc_color_space'] = 'Blender linear, converted from KitExport palette.rgb sRGB'
        # The new photo materials are opaque, non-emissive and independent of line colour.
        # Keep pre-existing generic MeshKit light assignments intact.
        if name in MATERIAL_SWATCHES:
            material['light_id'] = 0
            material['livery_control'] = False
            material['glass'] = False
            shader.inputs['Emission Strength'].default_value = 0.
        rows.append({'material':name,'before':before,'swatch':swatch,'finish':entry['finish'],
                     'linear_rgb':list(color),'srgb':entry['rgb'],'metallic':entry['metallic'],
                     'roughness':entry['roughness'],'coat':0.})
    return {'status':'PASS','scope':'Material-only alignment; no object transforms or mesh changed',
            'palette':str(path),'materials':rows,
            'estimated_parameters':'Photo colour estimates; ordinary contract entries align native/runtime finishes. native_preserved entries retain their native BSDF and explicitly report runtime approximations.'}
