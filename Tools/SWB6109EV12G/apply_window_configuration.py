"""Patch the saved master without reconstructing any authored vehicle geometry.

Run Blender --background --python-exit-code 1 --python this_file --
--blend <source> --report <report>. Export is a separate step.
"""
import argparse
from array import array
import hashlib
import json
from pathlib import Path
import struct
import sys
import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from window_configuration import apply_window_configuration, CURTAIN_OBJECT, SIDE_WINDOW


def digest(obj, materials=True):
    h = hashlib.sha256()
    h.update((obj.name+'|'+obj.type+'|'+(obj.parent.name if obj.parent else '')).encode())
    h.update(struct.pack('<16f', *[v for row in obj.matrix_world for v in row]))
    if obj.type == 'MESH':
        a = array('f', [0.]) * (len(obj.data.vertices)*3)
        obj.data.vertices.foreach_get('co', a); h.update(a.tobytes())
        a = array('i', [0]) * len(obj.data.loops)
        obj.data.loops.foreach_get('vertex_index', a); h.update(a.tobytes())
        for field in ('loop_total', 'material_index'):
            a = array('i', [0]) * len(obj.data.polygons)
            obj.data.polygons.foreach_get(field, a); h.update(a.tobytes())
    elif obj.type == 'CURVE':
        for spline in obj.data.splines:
            h.update(str((spline.type,spline.use_cyclic_u)).encode())
            for point in spline.points: h.update(struct.pack('<4f', *point.co))
            for point in spline.bezier_points:
                h.update(struct.pack('<9f', *point.co, *point.handle_left, *point.handle_right))
    elif obj.type == 'FONT':
        h.update(str((obj.data.body,obj.data.size,obj.data.extrude,obj.data.bevel_depth)).encode())
    if materials and obj.type in {'MESH','CURVE','FONT'}:
        h.update('|'.join(m.name if m else '' for m in obj.data.materials).encode())
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--blend', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    before_file = hashlib.sha256(args.blend.read_bytes()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(args.blend.resolve()))
    scene = bpy.context.scene; scene.frame_set(0); bpy.context.view_layer.update()
    master = bpy.data.collections['SWB6109EV12G | editable components']
    before = {o.name:digest(o, not bool(SIDE_WINDOW.fullmatch(o.name)))
              for o in master.objects if not CURTAIN_OBJECT.fullmatch(o.name)}
    report = apply_window_configuration(master, scene)
    if len(report['removed_curtain_objects']) not in (0,66):
        raise RuntimeError('Unexpected curtain assembly count')
    after = {o.name:digest(o, not bool(SIDE_WINDOW.fullmatch(o.name))) for o in master.objects}
    if before != after: raise RuntimeError('Unrelated objects or remaining geometry changed')
    rig = json.loads(scene.get('cdlc_rig_report','{}'))
    rig['window_configuration'] = report
    if 'photo_cabin' in rig:
        rig['photo_cabin']['historical_curtain_configuration_superseded'] = True
        rig['photo_cabin']['current_curtains'] = False
    scene['cdlc_rig_report'] = json.dumps(rig)
    bpy.ops.wm.save_as_mainfile(filepath=str(args.blend.resolve()))
    report.update(status='SOURCE_SAVED_PENDING_EXPORT', source_before_sha256=before_file,
                  source_sha256=hashlib.sha256(args.blend.read_bytes()).hexdigest(),
                  protected_object_count=len(before), protected_objects_unchanged=before==after,
                  protected_object_digests=after,
                  validation_scope='World transform, mesh positions/topology/material indices, curve data and material names. Side panes permit only material reassignment.')
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('WINDOW_SOURCE_SAVED', json.dumps({k:v for k,v in report.items() if k!='protected_object_digests'}), flush=True)


if __name__ == '__main__': main()
