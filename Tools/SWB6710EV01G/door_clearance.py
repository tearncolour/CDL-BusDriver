"""Door motion clearance: every moving door part against the body, the other leaves and their drives, at every frame of the
door timeline (animation_rig.py keys the linkage of door_linkage.py). Executed in build_model.py's namespace after the rig.

Each leaf (Door0A, Door0B, Door1A) and each door drive (DoorDrive0..2) is one moving set; everything else near the doors is
the static set. Triangles are compared exactly (mathutils.bvhtree overlap, the evaluated meshes in world space). The only
contacts allowed are the joints and guides themselves (_dc_allowed). Any other overlap fails the build, with the frame and
the objects.
"""
import bpy as _dc_bpy
from mathutils.bvhtree import BVHTree as _DcBVH
from mathutils import Vector as _DcVector


def _dc_mesh(obj, depsgraph):
    ev = obj.evaluated_get(depsgraph)
    me = ev.to_mesh()
    mw = ev.matrix_world
    verts = [mw @ v.co for v in me.vertices]
    me.calc_loop_triangles()
    tris = [tuple(t.vertices) for t in me.loop_triangles]
    ev.to_mesh_clear()
    return verts, tris


def _dc_tree(objects, depsgraph):
    verts, tris, owner = [], [], []
    for obj in objects:
        v, t = _dc_mesh(obj, depsgraph)
        base = len(verts)
        verts.extend(v)
        tris.extend(tuple(base + i for i in tri) for tri in t)
        owner.extend([obj.name] * len(t))
    return (_DcBVH.FromPolygons(verts, tris, epsilon=0.0) if tris else None), owner, verts


def _dc_box(objects, depsgraph, pad):
    lo, hi = _DcVector((1e9,) * 3), _DcVector((-1e9,) * 3)
    for obj in objects:
        for v in _dc_mesh(obj, depsgraph)[0]:
            lo = _DcVector(map(min, lo, v)); hi = _DcVector(map(max, hi, v))
    return lo - _DcVector((pad,) * 3), hi + _DcVector((pad,) * 3)


def _dc_allowed(a, b, leaf_of, door_of):
    """The joints and guides themselves: a leaf's crank pin through its own crank's eye; a door's shafts in its header cover;
    a leaf's roller and stem in its door's roller channel; its floor pin in its slot."""
    names = (a, b)
    def has(prefix): return any(n.startswith(prefix) for n in names)
    same_leaf = leaf_of.get(a) is not None and leaf_of.get(a) == leaf_of.get(b)
    same_door = door_of.get(a) is not None and door_of.get(a) == door_of.get(b)
    if same_leaf and has('Door crank pin') and has('Door drive crank'):
        return True
    if same_door and has('Door drive shaft') and any(n.endswith('actuator cover') for n in names):
        return True
    if same_door and (has('Door roller ') or has('Door roller stem')) and any('roller channel' in n for n in names):
        return True
    if same_leaf and has('Door floor guide pin') and has('Door floor guide slot'):
        return True
    return False


def check_door_clearance(master, scene, leaves, frames):
    depsgraph = _dc_bpy.context.evaluated_depsgraph_get()
    geometry = [o for o in master.objects if o.type in {'MESH', 'CURVE', 'FONT'} and (not o.hide_render or o.name.startswith('Wrap surface'))]
    moving = {}
    leaf_of = {}
    for L in leaves:
        for key, part in (('leaf', L['part']), ('drive', L['drive_part'])):
            objs = [o for o in geometry if int(o.get('cdlc_part', -1)) == part]
            if key == 'leaf':      # the leaf's wrap surface moves with it
                objs += [o for o in geometry if int(o.get('cdlc_part', -1)) == L['surface_part']]
            if not objs:
                raise RuntimeError(f"door clearance: {L['name']} has no {key} geometry")
            moving[(L['name'], key)] = objs
            for o in objs:
                leaf_of[o.name] = L['name']
    moving_names = {o.name for objs in moving.values() for o in objs}
    # the static neighbourhood: anything within reach of a door part over the whole travel
    scene.frame_set(0)
    lo, hi = _DcVector((1e9,) * 3), _DcVector((-1e9,) * 3)
    for f in frames[::6] + [frames[-1]]:
        scene.frame_set(f)
        dg = _dc_bpy.context.evaluated_depsgraph_get()
        a, b = _dc_box([o for objs in moving.values() for o in objs], dg, .05)
        lo = _DcVector(map(min, lo, a)); hi = _DcVector(map(max, hi, b))
    scene.frame_set(0)
    dg = _dc_bpy.context.evaluated_depsgraph_get()
    static = []
    for o in geometry:
        if o.name in moving_names or o.name.startswith('SWB6710EV01G'):
            continue
        corners = [o.matrix_world @ _DcVector(c) for c in o.bound_box]
        if any(min(c[i] for c in corners) > hi[i] or max(c[i] for c in corners) < lo[i] for i in range(3)):
            continue
        static.append(o)
    # which leaf / door a static door fitting belongs to (by the "<door>.<leaf>" or "Door <door> " in its name)
    door_of = {}
    for L in leaves:
        for o in moving[(L['name'], 'leaf')] + moving[(L['name'], 'drive')]:
            door_of[o.name] = L['door']
    for o in static:
        for L in leaves:
            if o.name.startswith('Door floor guide slot') and o.name.endswith(f"{L['door'] + 1}.{L['leaf'] + 1}"):
                leaf_of[o.name] = L['name']
            if o.name.startswith(f"Door {L['door'] + 1} "):
                door_of[o.name] = L['door']
    static_tree, static_owner, _ = _dc_tree(static, dg)
    report = {'frames': len(frames), 'static_objects': len(static), 'moving_sets': {f'{k[0]} {k[1]}': len(v) for k, v in moving.items()},
              'allowed_joint_contacts': 0, 'overlaps': []}
    keys = list(moving)
    for f in frames:
        scene.frame_set(f)
        dg = _dc_bpy.context.evaluated_depsgraph_get()
        trees = {k: _dc_tree(moving[k], dg) for k in keys}
        for i, k in enumerate(keys):
            tree, owner, _ = trees[k]
            others = [(static_tree, static_owner, 'static')] + [(trees[k2][0], trees[k2][1], f'{k2[0]} {k2[1]}') for k2 in keys[i + 1:]]
            for other, other_owner, label in others:
                if other is None:
                    continue
                for ta, tb in tree.overlap(other):
                    a, b = owner[ta], other_owner[tb]
                    if _dc_allowed(a, b, leaf_of, door_of):
                        report['allowed_joint_contacts'] += 1
                        continue
                    row = {'frame': f, 'moving': a, 'other': b, 'against': label}
                    if row not in report['overlaps']:
                        report['overlaps'].append(row)
    scene.frame_set(0)
    report['status'] = 'PASS' if not report['overlaps'] else 'FAIL'
    return report
