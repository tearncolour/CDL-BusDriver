"""Correct export-only lamp tags on a saved source, preserving its render geometry."""
import argparse
import hashlib
import json
import sys
from collections import Counter
from pathlib import Path

import bpy
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lamp_layers import light_layer


def physical_signature():
    result = {}
    for ob in bpy.data.objects:
        if ob.type != 'MESH':
            continue
        mesh = ob.data
        digest = hashlib.sha256()
        for data, attr, width, dtype in ((mesh.vertices, 'co', 3, np.float32),
                                      (mesh.loops, 'vertex_index', 1, np.int32)):
            values = np.empty(len(data) * width, dtype=dtype)
            data.foreach_get(attr, values)
            digest.update(values.tobytes())
        for uv in mesh.uv_layers:
            values = np.empty(len(uv.data) * 2, dtype=np.float32)
            uv.data.foreach_get('uv', values)
            digest.update(uv.name.encode())
            digest.update(values.tobytes())
        digest.update(np.asarray([n.vector[:] for n in mesh.corner_normals], dtype=np.float32).tobytes())
        digest.update(np.asarray(ob.matrix_world, dtype=np.float64).tobytes())
        for poly in mesh.polygons:
            mat = mesh.materials[poly.material_index]
            # Copies keep the exact shader node trees, including packed normals.
            nodes = [(n.type, [(s.name, str(s.default_value)) for s in n.inputs
                      if hasattr(s, 'default_value')]) for n in mat.node_tree.nodes]
            links = [(l.from_node.name, l.from_socket.name, l.to_node.name, l.to_socket.name)
                     for l in mat.node_tree.links]
            digest.update(json.dumps([mat.get('cdlc_swatch'), nodes, links], sort_keys=True).encode())
        result[ob.name] = digest.hexdigest()
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--blend', type=Path, required=True)
    ap.add_argument('--kit', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    a = ap.parse_args(sys.argv[sys.argv.index('--') + 1:])
    a.out = a.out.resolve()
    a.out.mkdir(parents=True, exist_ok=False)
    bpy.ops.wm.open_mainfile(filepath=str(a.blend.resolve()))
    bpy.context.scene.frame_set(0)
    before = physical_signature()
    finishes = {r['name']: r['finish'] for r in json.loads(a.kit.read_text())['palette']}
    variants = {}
    counts = {lod: Counter() for lod in range(3)}
    for ob in bpy.data.objects:
        if ob.type != 'MESH' or 'cdlc_part' not in ob:
            continue
        lod = ob.get('first_lod')
        if lod not in counts:
            continue
        for poly in ob.data.polygons:
            mat = ob.data.materials[poly.material_index]
            finish = finishes.get(mat.get('cdlc_swatch'))
            if finish is None:
                continue
            height = sum((ob.matrix_world @ ob.data.vertices[i].co).z for i in poly.vertices) / len(poly.vertices)
            layer = light_layer(ob['cdlc_part'], finish, height)
            if mat.get('light_id', 0) != layer:
                key = (mat.name, layer)
                if key not in variants:
                    clone = mat.copy()
                    clone.name = mat.name + ' native-layer-' + str(layer)
                    clone['light_id'] = layer
                    variants[key] = clone
                clone = variants[key]
                index = ob.data.materials.find(clone.name)
                if index < 0:
                    ob.data.materials.append(clone)
                    index = len(ob.data.materials) - 1
                poly.material_index = index
            if layer:
                counts[lod][layer] += len(poly.vertices) - 2
    for lod, layers in counts.items():
        # Distant LODs omit the small decorative display glyphs (the final ID).
        # IDs 1–8 keep their order; the exporter checks shared LOD0 lamp sampling.
        required = set(range(1, 10 if lod == 0 else 9))
        if not required.issubset(layers) or not set(layers).issubset(range(1, 10)):
            raise ValueError(f'LOD{lod} is missing actual lamp geometry: {dict(layers)}')
    if before != physical_signature():
        raise ValueError('Lamp tagging changed geometry, UVs, normals, transforms or shader nodes')
    output = a.out / 'CDLCBus12.blend'
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    bpy.ops.wm.open_mainfile(filepath=str(output))
    if before != physical_signature():
        raise ValueError('Saved source physical signature changed')
    report = {'source': str(a.blend.resolve()), 'source_sha256': hashlib.sha256(a.blend.read_bytes()).hexdigest(),
              'output': str(output), 'output_sha256': hashlib.sha256(output.read_bytes()).hexdigest(),
              'changed': 'export-only light_id/material assignments; shader nodes unchanged',
              'native_light_triangles': counts, 'unchanged_physical_signatures': before}
    (a.out / 'light-tag-repair.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
