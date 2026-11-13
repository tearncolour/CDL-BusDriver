"""Evidence-linked passenger rails; count and metric placement remain estimates.

The visible instances are enumerated in handrail-layout-evidence.json.  That
document deliberately does not assert a complete factory count.  This module
edits an in-memory master and never saves a source file or deploys an asset.
"""
import hashlib
import json
import math
from pathlib import Path

import bpy
from mathutils import Vector

from source_coordinates import canonical_export_context

ROOT = Path(__file__).resolve().parents[2]
LAYOUT_PATH = ROOT / 'ModelSource/SWB6109EV12G/handrail-layout-evidence.json'


def load_layout(path=LAYOUT_PATH):
    layout = json.loads(Path(path).read_text(encoding='utf-8'))
    ids = [row['id'] for key in ('rails', 'vertical_members', 'grips',
                                 'ceiling_fittings', 'request_buttons')
           for row in layout[key]]
    if len(ids) != len(set(ids)):
        raise ValueError('Evidence layout identifiers must be unique')
    rails = {row['id']: row for row in layout['rails']}
    for grip in layout['grips']:
        rail = rails[grip['rail']]
        lo, hi = sorted(point[1] for point in rail['points'])
        if not lo <= grip['y_m'] <= hi:
            raise ValueError('Grip is outside its supporting rail: ' + grip['id'])
    return layout


def _tag(obj, row, role, revision):
    obj['handrail_layout_revision'] = revision
    obj['handrail_evidence_id'] = row['id']
    obj['handrail_role'] = role
    obj['handrail_source'] = json.dumps(row.get('source', {}), ensure_ascii=False)
    obj['handrail_dimension_status'] = row.get('metric_status', 'estimated')
    obj['handrail_reference_completeness'] = 'REFERENCE_INCOMPLETE'
    return obj


def _bent_upright(row):
    """A tangent quarter circle with radius well above the tube radius."""
    x, y, z = row['base']
    ex, ey, ez = row['upper_end']
    if abs(ey-y) > 1e-9:
        raise ValueError('Upright bend is not in its documented XZ plane')
    radius = abs(ex-x)
    if radius < .06:
        raise ValueError('Upright bend would be too tight for a 32 mm tube')
    direction = 1 if ex > x else -1
    points = [(x, y, z), (x, y, ez-radius)]
    points.extend((x+direction*radius*(1-math.cos(t)), y,
                   ez-radius+radius*math.sin(t))
                  for t in [math.pi*i/48 for i in range(1,25)])
    return points


def _closed_tube(master,name,points,radius,material,part,pivot):
    """A shared-vertex sweep with explicit centre-fan end caps.

    Curve fill caps can evaluate to disconnected rim vertices.  Use one ring
    index for both the wall and cap so the saved mesh itself is watertight.
    Parallel transport keeps the frame stable through the upright's bend.
    """
    from door_details import _mesh
    points=[Vector(p) for p in points]
    cyclic=len(points)>2 and (points[0]-points[-1]).length<1e-10
    if cyclic:points.pop()
    sides=16;count=len(points)
    tangents=[]
    for i in range(count):
        if not cyclic and i==0:tangent=points[1]-points[0]
        elif not cyclic and i==count-1:tangent=points[-1]-points[-2]
        else:
            tangent=(points[i]-points[(i-1)%count]).normalized()+(points[(i+1)%count]-points[i]).normalized()
        if tangent.length<1e-10:raise ValueError('Degenerate sweep tangent: '+name)
        tangents.append(tangent.normalized())
    seed=min((Vector((1,0,0)),Vector((0,1,0)),Vector((0,0,1))),
             key=lambda axis:abs(axis.dot(tangents[0])))
    normal=(seed-tangents[0]*seed.dot(tangents[0])).normalized()
    vertices=[];previous=tangents[0]
    for point,tangent in zip(points,tangents):
        normal=previous.rotation_difference(tangent)@normal
        normal=(normal-tangent*normal.dot(tangent)).normalized()
        binormal=tangent.cross(normal).normalized()
        vertices.extend(tuple(point+radius*(normal*math.cos(2*math.pi*j/sides)+
                                             binormal*math.sin(2*math.pi*j/sides)))
                        for j in range(sides))
        previous=tangent
    faces=[]
    for i in range(count if cyclic else count-1):
        following=(i+1)%count
        for j in range(sides):
            nxt=(j+1)%sides
            faces.append((i*sides+j,i*sides+nxt,following*sides+nxt,following*sides+j))
    side_faces=len(faces)
    if not cyclic:
        start=len(vertices);vertices.append(tuple(points[0]))
        end=len(vertices);vertices.append(tuple(points[-1]))
        for j in range(sides):
            nxt=(j+1)%sides
            faces.append((start,nxt,j))
            faces.append((end,(count-1)*sides+j,(count-1)*sides+nxt))
    obj=_mesh(master,name,vertices,faces,material,part,pivot)
    for poly in obj.data.polygons:poly.use_smooth=poly.index<side_faces
    radii=[]
    for i in range(count if cyclic else max(0,count-2)):
        a,b,c=(points[i%count],points[(i+1)%count],points[(i+2)%count])
        twice_area=(b-a).cross(c-a).length
        if twice_area>1e-12:
            radii.append((b-a).length*(c-b).length*(c-a).length/(2*twice_area))
    minimum=min(radii) if radii else 0.
    if radii and minimum<=radius*1.25:
        raise ValueError('Swept tube bend would intersect its own inside: '+name)
    obj['closed_shared_vertex_sweep']=True
    obj['sweep_minimum_centreline_radius_m']=minimum
    return obj


def patch_evidence_handrails(master, scene=None, path=LAYOUT_PATH):
    # Import at call time so door_details can expose its public patch wrapper.
    from door_details import (_box, _material, _mesh, _record_source_witness,
                              _remove_object, _rounded_triangle,
                              stainless_handrail_material)
    from animation_rig import prepare_strap_rig

    layout = load_layout(path)
    params = layout['parameters']
    revision = int(layout['revision'])
    scene = scene or bpy.context.scene
    old_frame = scene.frame_current
    created, removed = [], []
    _tube = _closed_tube
    try:
        scene.frame_set(0)
        bpy.context.view_layer.update()
        with canonical_export_context(master):
            fixed = next(o for o in master.objects if o.get('cdlc_is_part_pivot')
                         and int(o.get('cdlc_part', -1)) == 13)
            prefixes = ('Ceiling longitudinal grab rail', 'Saloon upper cross rail',
                        'Saloon vertical grab pole', 'Stop request push button',
                        'Standing-zone ceiling attachment', 'Standing-zone rail hanger',
                        'Standing-zone pole foot', 'Standing-zone strap collar',
                        'Hand strap stem', 'Passenger hand strap')
            for obj in list(master.objects):
                if obj.name.startswith(prefixes) or obj.get('handrail_layout_revision'):
                    if obj.get('cdlc_is_part_pivot'):
                        continue
                    removed.append(obj.name)
                    _remove_object(obj)
            for obj in list(master.objects):
                if obj.get('cdlc_is_part_pivot') and int(obj.get('cdlc_part', -1)) >= 34:
                    if obj.children:
                        raise RuntimeError('Unreviewed object remains under old strap pivot: '+obj.name)
                    removed.append(obj.name)
                    _remove_object(obj)
            steel = stainless_handrail_material()
            grey = _material('6109 photo | grey moulded hand grips', (.22,.24,.25), .05, .48)
            amber = _material('6109 photo | amber request-button enclosure', (.67,.28,.025), 0., .36)
            dark = _material('6109 photo | dark request-button face', (.045,.035,.025), 0., .4)

            def remember(obj, row, role):
                created.append(_tag(obj, row, role, revision))
                return obj

            rails = {row['id']: row for row in layout['rails']}
            for row in layout['rails']:
                remember(_tube(master, 'Ceiling longitudinal grab rail '+row['id'],
                               row['points'], params['tube_radius_m'], steel, 13, fixed),
                         row, 'longitudinal_'+('high' if row['id'].endswith('high') else 'low'))
            for row in layout['vertical_members']:
                points = _bent_upright(row)
                remember(_tube(master, 'Saloon vertical grab pole '+row['id'], points,
                               params['tube_radius_m'], steel, 13, fixed), row, 'upright')
                x,y,z = row['base']
                remember(_box(master, 'Standing-zone pole foot '+row['id'],
                              (x,y,z+.005), (.058,.058,.010), steel, 13, fixed, .003),
                         row, 'upright_foot')
            for row in layout['ceiling_fittings']:
                remember(_tube(master, 'Standing-zone rail hanger '+row['id'],
                               [row['rail_point'], row['roof_point']], row['radius_m'],
                               steel, 13, fixed), row, 'estimated_ceiling_attachment')
                x,y,z = row['roof_point']
                remember(_box(master, 'Standing-zone ceiling attachment '+row['id'],
                              (x,y,z-.004), (.044,.052,.008), steel, 13, fixed, .0024),
                         row, 'estimated_ceiling_attachment')
            uprights = {row['id']: row for row in layout['vertical_members']}
            for row in layout['request_buttons']:
                x,y,_ = uprights[row['pole']]['base']
                remember(_box(master, 'Stop request push button '+row['id'],
                              (x,y-.023,row['z_m']), (.044,.032,.075), amber, 13, fixed, .009),
                         row, 'request_button')
                remember(_box(master, 'Stop request push button face '+row['id'],
                              (x,y-.041,row['z_m']+.005), (.027,.006,.034), dark, 13, fixed, .0018),
                         row, 'request_button_face')

            # Each enumerated visible instance gets one complete stem/grip pair.
            # Existing rounded triangle outline is retained; only its supporting
            # rail, metric station and the actual free-band length are corrected.
            for row in layout['grips']:
                rail = rails[row['rail']]
                x,_,top = rail['points'][0]
                y = row['y_m']
                bottom = top-params['top_clamp_height_m']-params['band_free_length_m']
                vertices,faces,material_ids = [],[],[]
                for z0,z1,width,depth,material_id in (
                    (bottom, top-params['top_clamp_height_m'], .026,.006,0),
                    (top-params['top_clamp_height_m']-.001, top, .034,.012,1)):
                    offset = len(vertices)
                    vertices.extend((x+sx*width/2,y+sy*depth/2,z) for sx,sy,z in
                                    ((-1,-1,z0),(-1,-1,z1),(-1,1,z0),(-1,1,z1),
                                     (1,-1,z0),(1,-1,z1),(1,1,z0),(1,1,z1)))
                    new_faces = [tuple(offset+i for i in face) for face in
                                 ((0,2,3,1),(4,5,7,6),(0,1,5,4),(2,6,7,3),(0,4,6,2),(1,3,7,5))]
                    faces.extend(new_faces)
                    material_ids.extend([material_id]*len(new_faces))
                stem = remember(_mesh(master, 'Hand strap stem '+row['id'], vertices,faces,
                                      grey,13,fixed), row,'strap_stem')
                stem.data.materials.append(steel)
                for face,material_id in zip(stem.data.polygons,material_ids):
                    face.material_index = material_id
                stem['handrail_free_band_length_m'] = params['band_free_length_m']
                outer,inner = _rounded_triangle(bottom+.016, params['grip_width_m']/2,
                                                params['grip_height_m'])
                n = len(outer)
                vertices = []
                for depth in (-params['grip_depth_m']/2, params['grip_depth_m']/2):
                    vertices.extend((x+p.x,y+depth,p.y) for p in outer)
                    vertices.extend((x+p.x,y+depth,p.y) for p in inner)
                faces = []
                for i in range(n):
                    j = (i+1)%n
                    faces.extend(((i,j,n+j,n+i),(2*n+i,3*n+i,3*n+j,2*n+j),
                                  (i,2*n+i,2*n+j,j),(n+i,n+j,3*n+j,3*n+i)))
                grip = remember(_mesh(master, 'Passenger hand strap '+row['id'], vertices,
                                      faces,grey,13,fixed), row,'triangular_grip')
                grip['photo_triangular_grip_revision'] = revision
                bevel = grip.modifiers.new('Moulded grip edge rounds','BEVEL')
                bevel.width=.002
                bevel.segments=3
                collar = [(x+.020*math.cos(2*math.pi*i/32),y,
                           top+.020*math.sin(2*math.pi*i/32)) for i in range(32)]
                collar.append(collar[0])
                remember(_tube(master,'Standing-zone strap collar '+row['id'],collar,.0035,
                               steel,13,fixed),row,'fixed_strap_collar')
            bpy.context.view_layer.update()
            rig = prepare_strap_rig(master,scene)
            for obj in created:
                _record_source_witness(obj,master)
            digest = hashlib.sha256(Path(path).read_bytes()).hexdigest()
            scene['handrail_layout_evidence_sha256'] = digest
            scene['handrail_reference_completeness'] = 'REFERENCE_INCOMPLETE'
            scene['handrail_layout_report'] = json.dumps({
                'revision':revision,'evidence_sha256':digest,
                'reference_completeness':layout['reference_completeness'],
                'modeled_visible_grips':len(layout['grips']),
                'modeled_visible_uprights':len(layout['vertical_members'])},ensure_ascii=False)
            return {'revision':revision,'layout_file':Path(path).relative_to(ROOT).as_posix(),
                    'layout_sha256':digest,'removed_objects':removed,
                    'created_objects':[o.name for o in created],
                    'modeled_visible_grips':len(layout['grips']),
                    'modeled_visible_uprights':len(layout['vertical_members']),
                    'reference_completeness':layout['reference_completeness'],
                    'reference_uncertainties':layout['reference_completeness']['missing'],
                    'cross_aisle_rails':0,'straps':rig,
                    'parameters':params,'coordinate_space':layout['coordinate_space']}
    finally:
        scene.frame_set(old_frame)
        bpy.context.view_layer.update()
