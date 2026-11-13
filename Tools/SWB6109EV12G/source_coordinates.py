"""Bridge the physically left-hand-drive Blender source and the CDLC frame.

Blender is right-handed: with forward -Y and up +Z, physical right is -X.
The construction/export frame historically uses +X for the door side.  Keep
that private construction frame stable, but reflect the editable vehicle into
the correct Blender world.  Exporters must use canonical_world_matrix only on
authored descendants; temporary, already flattened meshes are not reflected.
"""
import json
from pathlib import Path
from contextlib import contextmanager
import bpy
from mathutils import Matrix, Vector

SOURCE_REFLECTION = Matrix.Diagonal((-1., 1., 1., 1.))
SOURCE_ROOT_NAME = 'SWB6109EV12G | physical left-hand-drive source'
SOURCE_VERSION = 1
SOURCE_ROOT_MARKER = 'swb6109_source_coordinate_root'
CANONICAL_EXPORT_MARKER = 'swb6109_canonical_export_active'


def source_root(master=None):
    objects = master.objects if master is not None else bpy.context.scene.objects
    roots = [ob for ob in objects if ob.get(SOURCE_ROOT_MARKER)]
    if len(roots) > 1:
        raise RuntimeError('Multiple SWB6109 source-coordinate roots')
    return roots[0] if roots else None


def source_is_corrected(obj):
    """True only for this authored source hierarchy, never a temporary mesh."""
    current = obj
    while current is not None:
        if current.get(SOURCE_ROOT_MARKER):
            return True
        current = current.parent
    return False


def canonical_world_matrix(obj):
    """Object-to-private-construction matrix, suitable for existing exporters."""
    world = obj.matrix_world.copy()
    current = obj
    while current is not None:
        if current.get(SOURCE_ROOT_MARKER):
            return world if current.get(CANONICAL_EXPORT_MARKER) else SOURCE_REFLECTION @ world
        current = current.parent
    return world


@contextmanager
def canonical_export_context(master):
    """Temporarily evaluate the authored hierarchy in its construction frame.

    Removing the reflection from dependency-graph evaluation avoids tiny
    negative-scale transform roundoffs changing Decimate's collapse choices.
    This does not save or edit the source geometry. Font preflips remain in
    their editable bases; flatten_object's existing local-X compensation is
    still required. Nested contexts and exceptions restore the exact prior
    root transform and marker. Never save a master inside this context.
    """
    root = source_root(master)
    if root is None:
        yield None
        return
    # Preserve channels, not a negative-determinant matrix. Assigning such a
    # matrix makes Blender decompose it into a pi rotation and negative scales;
    # sin(pi) roundoff then moves children and can change Decimate decisions.
    old_channels = {name: getattr(root, name).copy() if hasattr(getattr(root, name), 'copy')
                    else tuple(getattr(root, name)) for name in
                    ('location','rotation_euler','rotation_quaternion','rotation_axis_angle','scale')}
    old_mode = root.rotation_mode
    had_marker = CANONICAL_EXPORT_MARKER in root
    old_marker = root.get(CANONICAL_EXPORT_MARKER)
    try:
        root[CANONICAL_EXPORT_MARKER] = True
        root.location = (0., 0., 0.)
        root.rotation_mode = 'XYZ'
        root.rotation_euler = (0., 0., 0.)
        root.scale = (1., 1., 1.)
        bpy.context.view_layer.update()
        yield root
    finally:
        for name, value in old_channels.items():
            setattr(root, name, value)
        root.rotation_mode = old_mode
        if had_marker:
            root[CANONICAL_EXPORT_MARKER] = old_marker
        elif CANONICAL_EXPORT_MARKER in root:
            del root[CANONICAL_EXPORT_MARKER]
        bpy.context.view_layer.update()


def source_world_matrix(canonical_matrix):
    return SOURCE_REFLECTION @ canonical_matrix


def _flat(matrix):
    return [float(matrix[row][column]) for column in range(4) for row in range(4)]


def _matrix(values):
    return Matrix([[float(values[column*4+row]) for column in range(4)] for row in range(4)])


def _add_source_metadata(obj):
    # Existing properties stay in the private construction frame to preserve
    # the CDLC contract; explicitly named additions describe actual Blender.
    obj['cdlc_source_canonical_matrix_at_conversion'] = _flat(obj.matrix_world)
    if 'cdlc_rest_matrix_blender' in obj:
        obj['cdlc_rest_matrix_source_blender'] = _flat(
            source_world_matrix(_matrix(obj['cdlc_rest_matrix_blender'])))
    if 'cdlc_pivot' in obj:
        obj['cdlc_pivot_source_blender'] = list(SOURCE_REFLECTION @ Vector(obj['cdlc_pivot']))
    if 'mirror_normal_blender' in obj:
        obj['mirror_normal_source_blender'] = list(
            SOURCE_REFLECTION.to_3x3() @ Vector(obj['mirror_normal_blender']))
    obj['cdlc_coordinate_note'] = (
        'Source world: RH Blender, -Y forward, +Z up, +X physical left. '
        'Legacy cdlc_pivot and cdlc_rest_matrix_blender remain private '
        'construction-frame values; *_source_blender describe source world.')


def apply_source_coordinates(master, end_point, width_at, scene=None):
    """Idempotently correct the complete editable source after rig creation.

    end_point and width_at are the current vehicle's real surface functions.
    Fonts and traced vectors first receive the existing export-only readability
    corrections, so the final source is the reflection of the established
    export geometry rather than mirrored lettering.  No object is rebuilt or
    converted to an uneditable mesh, and animation channels remain intact.
    """
    scene = scene or bpy.context.scene
    existing = source_root(master)
    if existing is not None:
        if int(existing.get(SOURCE_ROOT_MARKER, 0)) != SOURCE_VERSION:
            raise RuntimeError('Unsupported source-coordinate revision')
        if any(not source_is_corrected(ob) for ob in master.objects):
            raise RuntimeError('Objects were added outside the corrected source hierarchy')
        return json.loads(existing['source_coordinate_report'])
    if scene.frame_current != 0:
        raise RuntimeError('Correct source coordinates at the canonical closed frame 0')
    if bpy.data.objects.get(SOURCE_ROOT_NAME) is not None:
        raise RuntimeError('Unrelated object owns the source-coordinate root name')
    objects = list(master.objects)
    members = set(objects)
    if any(ob.parent is not None and ob.parent not in members for ob in objects):
        raise RuntimeError('The source hierarchy has an external parent')
    bpy.context.view_layer.update()
    fonts = []
    vectors = []
    # Perform the legacy export's readability corrections in editable source.
    for ob in objects:
        if ob.type == 'FONT':
            if ob.animation_data is not None:
                raise RuntimeError('Animated font transforms require an explicit coordinate migration')
            # Multiplying the basis then assigning it decomposes a negative
            # determinant into a pi rotation. An explicit X scale keeps all
            # original rotation channels exactly and avoids sub-micron drift.
            ob.scale.x = -ob.scale.x
            ob['cdlc_font_source_preflipped'] = True
            fonts.append(ob.name)
        if 'cdlc_vector_mirror_x' in ob:
            if ob.type != 'MESH':
                raise RuntimeError('Traced source lettering must be a mesh: ' + ob.name)
            if ob.data.users > 1:
                ob.data = ob.data.copy()
            world = ob.matrix_world.copy()
            inverse = world.inverted()
            centre = float(ob['cdlc_vector_mirror_x'])
            rear = bool(ob.get('cdlc_vector_mirror_rear'))
            for vertex in ob.data.vertices:
                point = world @ vertex.co
                offset = point.y - end_point(point.x / width_at(point.z), point.z, rear)[1]
                point.x = 2*centre - point.x
                point.y = end_point(point.x / width_at(point.z), point.z, rear)[1] + offset
                vertex.co = inverse @ point
            ob.data.flip_normals()
            ob.data.update()
            ob['cdlc_vector_source_preflipped'] = True
            vectors.append(ob.name)
    bpy.context.view_layer.update()
    for ob in objects:
        _add_source_metadata(ob)
    root = bpy.data.objects.new(SOURCE_ROOT_NAME, None)
    master.objects.link(root)
    root.empty_display_type = 'PLAIN_AXES'
    root.empty_display_size = .35
    root[SOURCE_ROOT_MARKER] = SOURCE_VERSION
    root['coordinate_note'] = 'Actual Blender source is left-hand drive: driver +X, passenger doors -X, forward -Y.'
    # Parent while root is identity: transforms and existing F-curves keep their
    # values. The single reflection then affects the entire rig consistently.
    for ob in objects:
        if ob.parent is None:
            ob.parent = root
            ob.matrix_parent_inverse = Matrix.Identity(4)
    # Do not assign SOURCE_REFLECTION as a matrix: Blender's negative-scale
    # decomposition introduces a pi rotation with nonzero float sin(pi).
    root.scale = (-1., 1., 1.)
    bpy.context.view_layer.update()
    scene['source_coordinate_version'] = SOURCE_VERSION
    scene['coordinates'] = 'metres; right-handed Blender: +X physical left, -X physical right/doors, -Y forward, +Z up'
    scene['export_coordinates'] = 'Private construction frame is H @ source world; Unity is (canonical X, canonical Z, -canonical Y).'
    report = {'version': SOURCE_VERSION, 'root': root.name,
              'source_forward': '-Y', 'source_up': '+Z',
              'source_driver_side': '+X', 'source_door_side': '-X',
              'private_construction_driver_side': '-X',
              'private_construction_door_side': '+X',
              'authored_object_count': len(objects),
              'font_readability_corrections': fonts,
              'vector_readability_corrections': vectors,
              'legacy_rest_metadata_preserved': True,
              'source_rest_metadata_suffix': '_source_blender'}
    root['source_coordinate_report'] = json.dumps(report, ensure_ascii=False)
    return report


def sync_coordinate_metadata(source_dir):
    """Add explicit source-world counterparts without changing legacy fields.

    Existing ``*_blender*`` metadata was emitted in the private construction
    frame. Its consumers and all Unity coordinates stay unchanged. New
    ``*_source_blender*`` fields describe the physical, left-hand-drive source.
    This is deterministic/idempotent and can run after a new build, migration,
    or cached export. It does not read or mutate the Blender scene.
    """
    source_dir = Path(source_dir)
    names = ('mirror-surfaces.json', 'mirror-placement-estimates.json',
             'mirror-arm-validation.json', 'animation-report.json',
             'reference-notes.json', 'cabin-layout.json', 'front-badge-placement.json')
    contract = {
        'version': SOURCE_VERSION,
        'legacy_blender_fields': 'Private construction frame, retained verbatim: +X door/right in Unity, -X driver/left in Unity, -Y forward, +Z up. These are not current Blender world coordinates.',
        'source_blender_fields': 'Physical right-handed Blender world in metres: +X driver/left, -X door/right, -Y forward, +Z up.',
        'unity_fields': 'Existing left-handed game frame in metres: +X door/right, -X driver/left, +Y up, +Z forward. Retained verbatim.',
        'construction_to_source_blender': '(-x, y, z)',
        'unity_to_source_blender': '(-x, -z, y)',
        'matrix_layout': 'Column-major 4x4. Source-world matrix = diag(-1,1,1,1) @ construction-world matrix.',
        'historical_coordinate_text': 'Existing coordinate_system/coordinate_definition text and chosen_blender_metres name describe the legacy construction frame; use this explicit frame contract for current source world.',
        'reflection_invariants': 'Lengths, angles between vectors, curvature radii, clearance and visibility fractions do not change under the rigid reflection.'}

    def numeric_sequence(value, count):
        return isinstance(value, list) and len(value) == count and all(
            isinstance(v, (float, int)) and not isinstance(v, bool) for v in value)

    def reflected(value, unity=False):
        if numeric_sequence(value, 3):
            x, y, z = value
            return [-x, -z, y] if unity else [-x, y, z]
        if not unity and numeric_sequence(value, 16):
            # H left multiplication negates matrix row zero, including X
            # translation. Do not round via mathutils's float32 storage.
            return [-v if i % 4 == 0 else v for i, v in enumerate(value)]
        if isinstance(value, list) and value and all(numeric_sequence(v, 3) for v in value):
            return [reflected(v, unity) for v in value]
        return None

    def reflect_chosen(value):
        if isinstance(value, dict):
            return {key: reflect_chosen(child) for key, child in value.items()}
        mapped = reflected(value)
        # Chosen mirror widths/heights are two dimensions, not coordinates.
        return mapped if mapped is not None else value

    def augment(node):
        if isinstance(node, list):
            for child in node:
                augment(child)
            return
        if not isinstance(node, dict):
            return
        for key, value in list(node.items()):
            if 'source_blender' in key or key == 'coordinate_frames':
                continue
            augment(value)
            if key == 'chosen_blender_metres':
                node['chosen_source_blender_metres'] = reflect_chosen(value)
                continue
            if 'blender' in key:
                mapped = reflected(value)
                if mapped is None and key.endswith('_blender_x') and isinstance(value, (int, float)):
                    mapped = -value
                if mapped is not None:
                    node[key.replace('blender', 'source_blender')] = mapped
            elif key.endswith('_unity'):
                mapped = reflected(value, unity=True)
                if mapped is not None:
                    node[key[:-len('_unity')]+'_source_blender'] = mapped

    changed = []
    processed = []
    for name in names:
        path = source_dir/name
        if not path.exists():
            continue
        original = path.read_text(encoding='utf-8')
        document = json.loads(original)
        augment(document)
        document['coordinate_frames'] = contract.copy()
        if name == 'front-badge-placement.json':
            document['design_coordinate_note'] = (
                'centre_xz is a construction/design input before per-vector '
                'readability correction and final source X reflection. It is '
                'not a measured final source-world badge centre; inspect the '
                'saved object geometry for the actual placement.')
        if name == 'reference-notes.json':
            derived = document.get('dimensions', {}).get('derived_not_measured', {})
            for axle in ('front', 'rear'):
                key = 'unity_'+axle+'_axle_z_m'
                if key in derived:
                    derived['source_blender_'+axle+'_axle_y_m'] = -derived[key]
        output = json.dumps(document, ensure_ascii=False, indent=2)+'\n'
        if output != original:
            path.write_text(output, encoding='utf-8')
            changed.append(name)
        processed.append(name)
    return {'processed': processed, 'changed': changed,
            'legacy_and_unity_values_preserved': True,
            'source_coordinate_version': SOURCE_VERSION}
