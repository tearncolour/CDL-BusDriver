"""Source-only rays through front/rear destination and glazing enclosures.

Checks actual opaque intersections from both sides, not object closure alone.
The inner rectangles are the existing production LED apertures; they are NOT
required to be opaque. Dimensions describe this model's assembly contract, not
manufacturer measurements. No source file or scene geometry is changed.
"""
import argparse
import ast
import hashlib
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

sys.path.insert(0, str(Path(__file__).resolve().parent))
from source_coordinates import (SOURCE_REFLECTION, CANONICAL_EXPORT_MARKER,
                                canonical_world_matrix, source_root)

CONTRACTS = {
    'front': dict(inner=(-.84, .84, 2.48, 2.72), outer=(-.91, .91, 2.445, 2.76),
                  depth=(-5.15, -4.98), exterior_y=-5.55, interior_y=-4.85,
                  names=('Front destination ceramic backing', 'Front destination recessed carrier'),
                  unity_centre=(0., 2.60, 5.09), width=1.68),
    'rear': dict(inner=(-.77, .77, 2.50, 2.72), outer=(-.86, .86, 2.46, 2.76),
                 depth=(4.98, 5.18), exterior_y=5.55, interior_y=4.85,
                 names=('Rear glazing ceramic backing', 'Rear destination recessed carrier'),
                 unity_centre=(0., 2.61, -5.12), width=1.54),
}
PIXELS = [('front', (200, 374)), ('front', (350, 264)),
          ('front', (480, 375)), ('rear', (200, 374)), ('rear', (480, 265))]


def _end_geometry_functions():
    """Read only the pure existing coachwork formulae; never import its builder."""
    path = Path(__file__).with_name('exterior.py')
    tree = ast.parse(path.read_text(encoding='utf-8'))
    functions = {'spline', 'width_at', 'front_at', 'rear_at', 'end_point', 'end_inset_vector'}
    variables = {'_front_profile', '_rear_profile', '_front_shift', '_rear_shift'}
    nodes = [node for node in tree.body if
             isinstance(node, ast.FunctionDef) and node.name in functions or
             isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id in variables for t in node.targets)]
    spec = json.loads((Path(__file__).resolve().parents[2]/'ModelSource/SWB6109EV12G/model-spec.json').read_text(encoding='utf-8'))
    namespace = {'math': math, 'Vector': Vector, 'HALF': spec['length_m']/2}
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(path), 'exec'), namespace)
    return namespace


class AssemblyRays:
    """Canonical, evaluated physical joint only, without unrelated occluders.

    These are explicitly section tests. Exterior lamps, seals and cabin parts
    cannot cover a failed joint or be mistaken for its actual closure.
    """
    def __init__(self, master, rear, components='all'):
        end = 'Rear' if rear else 'Front'
        names = (end+' continuous coachwork',
                 'Rear glazing ceramic backing' if rear else 'Front destination ceramic backing')
        if components == 'body':
            names = names[:1]
        elif components == 'backing':
            names = names[1:]
        graph = bpy.context.evaluated_depsgraph_get()
        vertices, triangles, self.labels = [], [], []
        base_names = names
        names = [obj.name for obj in master.objects if any(
            obj.name == base or obj.name.startswith(base+'.') for base in base_names)]
        if any(not any(name == base for name in names) for base in base_names):
            raise RuntimeError('Missing enclosure joint component: '+str(base_names))
        for name in names:
            original = master.objects.get(name)
            if original is None:
                raise RuntimeError('Missing enclosure joint component: '+name)
            obj = original.evaluated_get(graph)
            data = obj.to_mesh()
            try:
                data.calc_loop_triangles()
                world = canonical_world_matrix(original)
                offset = len(vertices)
                vertices.extend(world @ v.co for v in data.vertices)
                for tri in data.loop_triangles:
                    triangles.append(tuple(offset+i for i in tri.vertices))
                    material = data.materials[tri.material_index] if tri.material_index < len(data.materials) else None
                    self.labels.append((name, material.name if material else None))
            finally:
                obj.to_mesh_clear()
        self.tree = BVHTree.FromPolygons(vertices, triangles, all_triangles=True)
        self.low = tuple(min(v[i] for v in vertices) for i in range(3))
        self.high = tuple(max(v[i] for v in vertices) for i in range(3))

    def outside_bounds(self, point):
        return any(point[i] < self.low[i] or point[i] > self.high[i] for i in range(3))

    def cast(self, origin, direction, distance):
        direction = Vector(direction).normalized()
        point, normal, index, travel = self.tree.ray_cast(Vector(origin), direction, distance)
        if index is None:
            return None
        name, material = self.labels[index]
        return dict(object=name, material=material, canonical_point=list(point),
                    distance_m=travel, ray_dot_normal=direction.dot(normal))

    def through(self, centre, direction, tolerance=.001):
        """Trace from outside the whole solid, proving entry/exit orientation.

        A short local ray may start within the original plate next to a valid
        return. Its first backface is then expected. Starting beyond the full
        AABB distinguishes that case from crossed/inverted return surfaces.
        """
        centre, direction = Vector(centre), Vector(direction).normalized()
        span = (Vector(self.high)-Vector(self.low)).length+.2
        origin = centre-direction*span
        if not self.outside_bounds(origin):
            raise RuntimeError('Enclosure trace origin must be outside the whole backing')
        cursor, hits = origin.copy(), []
        for _ in range(64):
            hit = self.cast(cursor, direction, 2*span)
            if hit is None:
                break
            point = Vector(hit['canonical_point'])
            signed = (point-centre).dot(direction)
            if signed > span:
                break
            hit['signed_distance_from_sample_m'] = signed
            hits.append(hit)
            cursor = point+direction*.000005
        else:
            raise RuntimeError('Enclosure solid trace exceeded intersection budget')
        oriented = bool(hits and len(hits)%2 == 0 and all(
            h['ray_dot_normal'] < -.000001 if i%2 == 0 else h['ray_dot_normal'] > .000001
            for i, h in enumerate(hits)))
        intervals = []
        if oriented:
            for entry, exit in zip(hits[::2], hits[1::2]):
                low, high = entry['signed_distance_from_sample_m'], exit['signed_distance_from_sample_m']
                intervals.append(dict(entry_m=low, exit_m=high,
                                      distance_to_sample_m=max(low, -high, 0.)))
        nearest = min((v['distance_to_sample_m'] for v in intervals), default=None)
        return dict(origin_canonical=list(origin), direction_canonical=list(direction),
                    origin_provably_outside_backing_aabb=True, all_intersections=hits,
                    ordered_outward_entry_exit=oriented, solid_intervals=intervals,
                    distance_to_actual_solid_m=nearest,
                    passed=oriented and nearest is not None and nearest <= tolerance)


def _outer_boundary_checks(master):
    functions = _end_geometry_functions()
    checks = []
    for rear in (False, True):
        name = 'rear' if rear else 'front'
        assembly = AssemblyRays(master, rear)
        width, bottom, top = (1.18, 1.77, 2.98) if rear else (1.15, 2.39, 2.82)
        plate_y = 5.055 if rear else -5.055
        sections = []
        # Both sides cover the full backed region up to the original glazing
        # aperture corner. Ten-millimetre longitudinal stations, no central
        # transparent window or LED opening included.
        high = 2.97-.045*.94**6 if rear else 2.77-.035*.94**4
        steps = math.ceil((high-bottom)/.01)
        for side in (-1, 1):
            for i in range(steps+1):
                z = bottom+.001+(high-bottom-.002)*i/steps
                p = Vector(functions['end_point'](side*.94, z, rear))
                q = p+functions['end_inset_vector'](p, rear)
                sections.append((f'side={side}/station={i}', Vector((side*width, plate_y, z)), q))
        # The top spans the full width, including both upper corners.
        for i in range(97):
            u = -.94+1.88*i/96
            z = 2.97-.045*abs(u)**6 if rear else 2.77-.035*abs(u)**4
            p = Vector(functions['end_point'](u, z, rear))
            q = p+functions['end_inset_vector'](p, rear)
            sections.append((f'top/station={i}', Vector((width*u/.94, plate_y, top)), q))
        # The rear lower aperture is intentionally transparent. Only the two
        # backed legs meet the coachwork lower return.
        if rear:
            for side in (-1, 1):
                for i in range(17):
                    x = side*(1.0805+(width-1.0805)*i/16)
                    u = x/functions['width_at'](bottom)
                    p = Vector(functions['end_point'](u, bottom, True))
                    q = p+functions['end_inset_vector'](p, True)
                    sections.append((f'bottom-leg={side}/station={i}', Vector((x, plate_y, bottom)), q))
        for label, a, b in sections:
            # Sample inside each boundary span, not at an exact mesh vertex or
            # tangent silhouette, where a ray has no unique first face.
            for band in range(17):
                t = .02+.96*band/16
                sample = a.lerp(b, t)
                for view, origin_y, direction in (
                        ('outside', 5.40 if rear else -5.40, (0, -1 if rear else 1, 0)),
                        ('inside', 5.01 if rear else -5.01, (0, 1 if rear else -1, 0))):
                    origin = Vector((sample.x, origin_y, sample.z))
                    hit = assembly.cast(origin, direction, .39)
                    passed = bool(hit and hit['ray_dot_normal'] <= .00001)
                    checks.append(dict(screen=name, sample=f'outer/{label}/band={band}/{view}',
                                       kind='outer-joint-section-projection', origin_canonical=list(origin),
                                       direction_canonical=direction, first_opaque=hit,
                                       local_enclosure_hit=bool(hit), first_surface_faces_ray=passed,
                                       passed=passed))
        # The front lower backing edge is a deliberate boundary to the clear
        # driver's windshield, not a body joint. Check its finite vertical
        # thickness at the existing plate, without demanding opaque glazing.
        if not rear:
            for i in range(97):
                x = -width+.001+(2*width-.002)*i/96
                origin = Vector((x, plate_y, bottom-.02))
                hit = assembly.cast(origin, (0, 0, 1), .04)
                passed = bool(hit and hit['object'] == 'Front destination ceramic backing'
                              and hit['ray_dot_normal'] < -.00001)
                checks.append(dict(screen=name, sample=f'outer/clear-windshield-bottom-edge/station={i}',
                                   kind='intentional-clear-window-edge-thickness', origin_canonical=list(origin),
                                   direction_canonical=(0, 0, 1), first_opaque=hit, passed=passed))
    return checks


def _return_surface_checks(master):
    """Verify the real three-dimensional returns, including their body contact.

    Longitudinal silhouette overlap alone is insufficient: normal rays from
    outside the complete solid must enter and exit its actual return. Target
    stations lie within the existing coachwork, beyond its glazing aperture.
    They are projected onto the actual evaluated inner shell before sampling.
    """
    functions = _end_geometry_functions()
    checks = []
    for rear in (False, True):
        name = 'rear' if rear else 'front'
        body = AssemblyRays(master, rear, 'body')
        backing = AssemblyRays(master, rear, 'backing')
        width, bottom, top = (1.18, 1.77, 2.98) if rear else (1.15, 2.39, 2.82)
        plate_y = 5.055 if rear else -5.055
        stations = []
        for side in (-1, 1):
            count = math.ceil((top-bottom)/.02)
            for i in range(count):
                z = bottom+(top-bottom)*(i+.5)/count
                stations.append((f'side={side}/station={i}', Vector((side*width, plate_y, z)),
                                 side*.946, z, Vector((0, 0, 1))))
        for i in range(96):
            t = (i+.5)/96
            x = width*(-1+2*t)
            stations.append((f'top/station={i}', Vector((x, plate_y, top)),
                             .946*x/width, top, Vector((1, 0, 0))))
        if rear:
            for side in (-1, 1):
                for i in range(24):
                    x = side*(1.08+(width-1.08)*(i+.5)/24)
                    stations.append((f'bottom-leg={side}/station={i}', Vector((x, plate_y, bottom)),
                                     .946*x/width, 1.765, Vector((1, 0, 0))))
        for label, a, u, z, tangent in stations:
            # A top return must continue above the plate, not turn back down
            # through its solid volume. Independently locate the actual inner
            # shell 4mm above the existing plate; this is an assembly station,
            # not a manufacturer's dimension.
            bottom_joint = rear and label.startswith('bottom-leg=')
            target_inner_z = top+.004 if label.startswith('top/') else 1.765 if bottom_joint else None
            if target_inner_z is not None:
                for _ in range(12):
                    test = Vector(functions['end_point'](u, z, rear))
                    test += functions['end_inset_vector'](test, rear)
                    z += target_inner_z-test.z
            outer = Vector(functions['end_point'](u, z, rear))
            inset = functions['end_inset_vector'](outer, rear)
            normal = -inset.normalized()
            if bottom_joint:
                inner = outer+inset
                target = body.cast((inner.x, 4.9, 1.765), (0, 1, 0), .5)
            else:
                target = body.cast(outer+inset-normal*.03, normal, .08)
            if not target or target['ray_dot_normal'] >= 0:
                checks.append(dict(screen=name, sample='return/'+label+'/body-target',
                                   kind='actual-inner-body-contact-target', passed=False,
                                   first_opaque=target))
                continue
            b = Vector(target['canonical_point'])
            bridge_normal = (b-a).cross(tangent).normalized()
            for band in (.1, .3, .5, .7, .9):
                centre = a.lerp(b, band)
                for side in (-1, 1):
                    direction = bridge_normal*(-side)
                    trace = backing.through(centre, direction)
                    checks.append(dict(screen=name, sample=f'return/{label}/band={band}/face={side}',
                                       kind='three-dimensional-return-two-face', bridge_start=list(a),
                                       actual_inner_body_target=target, expected_mid_surface=list(centre),
                                       **trace))
            # At the actual shell interface the return must cross the shell,
            # rather than stop short of it or hide a cavity in projection.
            # A 0.5mm inboard shift avoids casting along the terminal cap's
            # exact mesh edge; it remains inside the intended 2mm overlap.
            # The actual return uses piecewise-linear boundary stations. Use
            # its nearest real surface (within the 1mm profile sampling bound)
            # rather than asserting the analytic curve lies on that mesh.
            # Contact is still proved independently against the body below.
            terminal, _, _, terminal_distance = backing.tree.find_nearest(b)
            terminal_valid = terminal is not None and terminal_distance <= .001
            contact_anchor = terminal if terminal_valid else b
            contact_probe = contact_anchor+(a-contact_anchor).normalized()*.0005
            for side in (-1, 1):
                # The lip has fixed longitudinal thickness. Compare actual
                # solid intervals on that thickness axis, not along an oblique
                # ray which may leave the terminal cap before entering the body.
                contact_direction = Vector((0, side, 0))
                trace = backing.through(contact_probe, contact_direction)
                body_trace = body.through(contact_probe, contact_direction)
                overlap = max((min(a['exit_m'], b['exit_m'])-max(a['entry_m'], b['entry_m'])
                               for a in trace['solid_intervals'] for b in body_trace['solid_intervals']),
                              default=0.)
                trace['passed'] = trace['passed'] and terminal_valid and body_trace['ordered_outward_entry_exit'] and overlap > .00001
                checks.append(dict(screen=name, sample=f'return/{label}/body-contact/face={side}',
                                   kind='return-contact-at-actual-body-surface', actual_inner_body_target=target,
                                   terminal_inboard_offset_m=.0005, actual_body_solid_intervals=body_trace['solid_intervals'],
                                   actual_terminal_point=list(terminal) if terminal is not None else None,
                                   terminal_distance_from_body_target_m=terminal_distance,
                                   actual_backing_body_overlap_m=max(0., overlap), **trace))
    return checks


def _annulus_samples(contract):
    """Three rings across the true surround, including all four corners."""
    inner, outer = contract['inner'], contract['outer']
    samples = []
    for band in (.15, .5, .85):
        left, right, bottom, top = [a+(b-a)*band for a, b in zip(inner, outer)]
        for edge in ('top', 'bottom', 'left', 'right'):
            for step in range(9):
                t = step/8
                if edge in ('top', 'bottom'):
                    x, z = left+(right-left)*t, top if edge == 'top' else bottom
                else:
                    x, z = left if edge == 'left' else right, bottom+(top-bottom)*t
                samples.append((f'{edge}/band={band}/position={t}', x, z))
    return samples


class SourceRays:
    def __init__(self, master):
        self.master_names = {o.name for o in master.objects}
        self.graph = bpy.context.evaluated_depsgraph_get()
        root = source_root(master)
        self.frame = SOURCE_REFLECTION if root and not root.get(CANONICAL_EXPORT_MARKER) else Matrix.Identity(4)

    def cast(self, origin, direction, distance=16., allow_front_outer_corner=False):
        """Return first actual opaque source hit, retaining skipped glass proof."""
        origin = self.frame @ Vector(origin)
        direction = (self.frame.to_3x3() @ Vector(direction)).normalized()
        travel, skipped = 0., []
        for _ in range(64):
            hit, point, normal, index, obj, _matrix = bpy.context.scene.ray_cast(
                self.graph, origin, direction, distance=max(0., distance-travel))
            if not hit:
                return None, skipped
            travel += (point-origin).length
            evaluated = obj.evaluated_get(self.graph)
            material = None
            if hasattr(evaluated.data, 'polygons') and 0 <= index < len(evaluated.data.polygons):
                slot = evaluated.data.polygons[index].material_index
                if slot < len(evaluated.data.materials):
                    material = evaluated.data.materials[slot]
            glass = bool(material and material.get('glass', False))
            dynamic_led = bool(material and int(material.get('light_id', 0)) == 9)
            # The two outer glazing seals occlude a few corner rays. Continue
            # through them to test the actual enclosure behind: a seal alone
            # must never conceal a failed/missing return wall from this check.
            outer_seal = obj.name in ('Front glazing perimeter EPDM', 'Rear glazing perimeter EPDM')
            canonical = self.frame.inverted() @ point
            # Only the four explicit extreme-corner outside probes traverse
            # the original curved front skin. Its opacity is not acceptance:
            # these section-style probes must still reach the real enclosure.
            outer_corner = bool(allow_front_outer_corner and obj.name == 'Front continuous coachwork'
                                and .88 < abs(canonical.x) < .92
                                and 2.745 < canonical.z < 2.765
                                and -5.2 < canonical.y < -5.07)
            row = dict(object=obj.name, material=material.name if material else None,
                       distance_m=travel, canonical_point=list(canonical),
                       ray_dot_normal=float(direction.dot(normal)), glass=glass,
                       dynamic_led=dynamic_led, opaque_outer_seal=outer_seal,
                       opaque_external_corner_skin=outer_corner)
            if obj.name in self.master_names and not glass and not dynamic_led and not outer_seal and not outer_corner:
                return row, skipped
            row['skip_reason'] = ('explicit opaque outer glazing seal; continue to test the enclosure behind' if outer_seal
                                  else 'bounded outer-corner front skin, section-style probe; must still hit enclosure' if outer_corner
                                  else 'transparent glazing' if glass else 'replaced dynamic LED' if dynamic_led
                                  else 'outside authored master collection')
            skipped.append(row)
            origin = point+direction*.00002
            travel += .00002
            if travel >= distance:
                return None, skipped
        raise RuntimeError('Sign enclosure ray exceeded its explicit surface budget')


def _evaluate(rays, name, label, origin, direction):
    contract = CONTRACTS[name]
    corner_labels = {'outside/top/band=0.85/position=0.0', 'outside/top/band=0.85/position=1.0',
                     'outside/left/band=0.85/position=1.0', 'outside/right/band=0.85/position=1.0'}
    first, skipped = rays.cast(origin, direction, allow_front_outer_corner=name == 'front' and label in corner_labels)
    local = bool(first and any(first['object'].startswith(n) for n in contract['names'])
                 and contract['depth'][0] <= first['canonical_point'][1] <= contract['depth'][1])
    facing = bool(first and first['ray_dot_normal'] <= .00001)
    return dict(screen=name, sample=label, origin_canonical=list(origin),
                direction_canonical=list(direction), first_opaque=first,
                skipped_surfaces=skipped, local_enclosure_hit=local,
                first_surface_faces_ray=facing, passed=local and facing)


def verify_master(master):
    rays, checks = SourceRays(master), []
    for name, contract in CONTRACTS.items():
        sign = 1 if name == 'front' else -1
        for label, x, z in _annulus_samples(contract):
            for view, y, direction in [('outside', contract['exterior_y'], (0, sign, 0)),
                                       ('inside', contract['interior_y'], (0, -sign, 0))]:
                checks.append(_evaluate(rays, name, view+'/'+label, (x, y, z), direction))
    for name, pixel in PIXELS:
        contract = CONTRACTS[name]
        centre = Vector(contract['unity_centre'])
        facing = Vector((0, 0, 1 if name == 'front' else -1))
        eye = centre+facing*max(2, contract['width']*1.65)
        forward = (centre-eye).normalized()
        right = Vector((0, 1, 0)).cross(forward).normalized()
        up = forward.cross(right)
        nx = (2*(pixel[0]+.5)/960-1)*1.5*math.tan(math.radians(35)/2)
        ny = (1-2*(pixel[1]+.5)/640)*math.tan(math.radians(35)/2)
        direction = (forward+right*nx+up*ny).normalized()
        canonical = lambda v: Vector((v.x, -v.z, v.y))
        checks.append(_evaluate(rays, name, f'production-closeup-pixel={pixel}',
                                canonical(eye), canonical(direction)))
    central_count = len(checks)
    # Exact pixel centres from the saved 1600x1050 rear orthographic preview
    # which exposed the outer-side slits. These are full-scene visible rays,
    # separate from the isolated assembly section checks below.
    for px in (442, 443, 1156, 1157):
        for py in (250, 330):
            x = (2*(px+.5)/1600-1)*2.65
            z = 1.65+(1-2*(py+.5)/1050)*(2.65*1050/1600)
            row = _evaluate(rays, 'rear', f'rear-orthographic-preview-pixel=({px},{py})',
                            (x, 15, z), (0, -1, 0))
            row['kind'] = 'observed-outer-slit-preview-pixel'
            checks.append(row)
    checks.extend(_outer_boundary_checks(master))
    checks.extend(_return_surface_checks(master))
    bounds = []
    for obj in master.objects:
        if any(obj.name.startswith(n) for c in CONTRACTS.values() for n in c['names']):
            world = canonical_world_matrix(obj)
            corners = [world@Vector(v) for v in obj.bound_box]
            bounds.append(dict(object=obj.name, low=[min(v[i] for v in corners) for i in range(3)],
                               high=[max(v[i] for v in corners) for i in range(3)]))
    failed = [c for c in checks if not c['passed']]
    return dict(status='FAIL' if failed else 'PASS', checks=len(checks), failed=len(failed),
                central_aperture_checks=central_count, outer_boundary_checks=len(checks)-central_count,
                scope='Saved source geometry only. No runtime/package or refractive-render claim.',
                coordinate_space='Private canonical model frame: X door side, -Y front, Z up.',
                method='437 unchanged central-aperture checks, 8 observed rear-preview slit pixels, and actual evaluated backing/coachwork section rays over both side joints, complete upper return and rear lower backed legs. Return-normal traces start outside the full backing AABB, require alternating outward entry/exit and actual solid within 1mm of the independently projected sample. This avoids treating an expected exit from inside a sound plate as a backface defect. Exterior attachments and interior fittings cannot satisfy the isolated outer assembly checks. The front clear-windshield edge is checked for real finite thickness; transparent windows and central LED apertures remain excluded. Existing central tests record and traverse the two EPDM seals and four bounded front corner occluders. Section checks do not claim camera visibility.',
                verification_contract_revision=2,
                sampling_tolerance_m=.001,
                contracts=CONTRACTS, closeup_camera=dict(width=960, height=640, vertical_fov=35,
                                                       pixels_origin='top-left', refraction=False),
                enclosure_bounds=bounds, results=checks)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', required=True, type=Path)
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    source = Path(bpy.data.filepath)
    if not source.is_file():
        raise RuntimeError('Open the saved source .blend before running this verifier')
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    report = verify_master(bpy.data.collections['SWB6109EV12G | editable components'])
    report.update(source=str(source), source_sha256=before,
                  verifier_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  coachwork_formula_sha256=hashlib.sha256(Path(__file__).with_name('exterior.py').read_bytes()).hexdigest())
    if hashlib.sha256(source.read_bytes()).hexdigest() != before:
        raise RuntimeError('Source changed during read-only sign enclosure verification')
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print('SIGN_ENCLOSURES', report['status'], report['checks'], 'checks;', report['failed'], 'failed')
    if report['status'] != 'PASS':
        raise SystemExit(1)


if __name__ == '__main__':
    main()
