"""Evaluate lower-detail manufactured edges without moving authored objects.

Only parametric bevels/tube sections are regenerated. Thin imported dashboard
faces, shell returns, letters, tyre solids and manually closed parts retain
their topology; no automatic collapse crosses their walls.
"""
from contextlib import contextmanager


@contextmanager
def detail_level(obj, lod):
    restored = []
    try:
        if lod and not obj.get('cdlc_preserve_section_lod') and int(obj.get('cdlc_part', -1)) not in (19, 20, 21, 22):
            for modifier in obj.modifiers:
                if modifier.type == 'BEVEL' and modifier.segments > 1:
                    restored.append((modifier, 'segments', modifier.segments))
                    modifier.segments = min(modifier.segments, 2 if lod == 1 else 1)
            if obj.type == 'CURVE' and obj.data.bevel_resolution > 0:
                restored.append((obj.data, 'bevel_resolution', obj.data.bevel_resolution))
                obj.data.bevel_resolution = min(obj.data.bevel_resolution, 1 if lod == 1 else 0)
        if restored:
            obj.update_tag(refresh={'OBJECT', 'DATA'})
            import bpy
            bpy.context.view_layer.update()
        yield
    finally:
        for target, name, value in reversed(restored):
            setattr(target, name, value)
        if restored:
            obj.update_tag(refresh={'OBJECT', 'DATA'})
            import bpy
            bpy.context.view_layer.update()


def flatten_lod(obj, lod, flatten):
    with detail_level(obj, lod):
        return flatten(obj)
