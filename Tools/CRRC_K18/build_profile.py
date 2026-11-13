"""Build the v2 K18 manifest from one saved source and its matching exports.

No source, pack, deployment or game mutation. Run inside Blender after both
export_model.py and export_articulation.py. Study dimensions and rider layout
remain estimates; capacity allocation is a mod setting, not OEM sectional data.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import uuid

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

ROOT = Path(__file__).resolve().parents[2]
B_TO_U = Matrix(((-1., 0., 0.), (0., 0., 1.), (0., -1., 0.)))


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def rounded(v):
    return [round(float(x), 8) for x in v]


def points(name):
    ob = bpy.data.objects[name]
    ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = ev.to_mesh()
    result = [B_TO_U @ (ob.matrix_world @ v.co) for v in mesh.vertices]
    ev.to_mesh_clear()
    return result


def bounds(name):
    ps = points(name)
    return (Vector(tuple(min(p[i] for p in ps) for i in range(3))),
            Vector(tuple(max(p[i] for p in ps) for i in range(3))))


def mirror_surface(name):
    """Read the authored optical frame and verify it against final mesh planes."""
    ob = bpy.data.objects[name]
    required = ('mirror_base_origin_unity', 'mirror_normal_unity', 'mirror_up_unity', 'mirror_size_m')
    if any(key not in ob for key in required):
        raise ValueError('Mirror lacks measured optical metadata: '+name)
    centre = Vector(ob['mirror_base_origin_unity'])
    normal = Vector(ob['mirror_normal_unity'])
    up = Vector(ob['mirror_up_unity'])
    width, height = map(float, ob['mirror_size_m'])
    depth = float(ob.get('mirror_convex_depth_m', 0.))
    if abs(normal.length-1) > 1e-5 or abs(up.length-1) > 1e-5 or abs(normal.dot(up)) > 1e-5:
        raise ValueError('Mirror optical basis is not orthonormal: '+name)
    right = normal.cross(up).normalized()
    ps = points(name)
    offsets = [(p-centre).dot(normal) for p in ps]
    if depth == 0:
        maximum = max(offsets)
        flat = [p-centre for p, d in zip(ps, offsets) if abs(d-maximum) < .00003]
        measured = (max(p.dot(right) for p in flat)-min(p.dot(right) for p in flat),
                    max(p.dot(up) for p in flat)-min(p.dot(up) for p in flat))
        if abs(maximum) > .00003 or max(abs(measured[0]-width), abs(measured[1]-height)) > .00003:
            raise ValueError('Mirror frame/size does not match its actual front plane: '+name)
    else:
        maximum = max(offsets)
        radial = max(math.hypot((p-centre).dot(right)/(width/2),
                                (p-centre).dot(up)/(height/2)) for p in ps)
        if abs(maximum-depth) > .00003 or abs(radial-1) > .001:
            raise ValueError('Convex mirror metadata does not match its actual cap: '+name)
    entry = {'centre':rounded(centre), 'normal':rounded(normal), 'up':rounded(up),
             'size':[round(width,8), round(height,8)]}
    if depth > 0: entry['convexDepth'] = depth
    return entry, {'object':name, 'measured_front_offset_m':maximum,
                   'source_basis_unity':entry, 'method':'authored frame checked against evaluated final mesh'}


def boarding_point(world_z):
    """Measure a supported point near the threshold outer edge, not its bevel."""
    candidates = []
    for ob in bpy.data.objects:
        if not ob.name.startswith('Yellow boarding threshold'):
            continue
        lo, hi = bounds(ob.name)
        if lo.z < world_z < hi.z:
            candidates.append((ob, lo, hi))
    if len(candidates) != 1:
        raise ValueError('Expected one actual boarding threshold at '+str(world_z))
    ob, lo, hi = candidates[0]
    ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = ev.to_mesh()
    try:
        vertices = [B_TO_U @ (ob.matrix_world @ v.co) for v in mesh.vertices]
        # B_TO_U changes handedness; reverse winding so the physical outward
        # normal remains outward in this read-only Unity-space BVH.
        tree = BVHTree.FromPolygons(vertices, [list(reversed(p.vertices)) for p in mesh.polygons])
        x = hi.x-.015
        hit, normal, face, distance = tree.ray_cast(Vector((x, hi.y+.1, world_z)), Vector((0,-1,0)), .2)
        if hit is None or abs(hit.y-hi.y) > .001 or normal.y < .95:
            raise ValueError('Boarding point is not on the threshold top: '+ob.name)
        return hit, {'object':ob.name, 'bounds_unity':[rounded(lo),rounded(hi)],
                     'point_unity':rounded(hit), 'surface_normal_unity':rounded(normal),
                     'outer_edge_inset_m':.015}
    finally:
        ev.to_mesh_clear()


def main(args):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend', type=Path, required=True)
    parser.add_argument('--section-summary', type=Path, required=True)
    parser.add_argument('--joint-report', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=ROOT/'Tools/CRRC_K18/vehicle.json')
    options = parser.parse_args(args)
    source_hash = sha(options.blend)
    sections, joint = read(options.section_summary), read(options.joint_report)
    if sections['source_sha256'] != source_hash or joint['source_sha256'] != source_hash:
        raise ValueError('Both resource exports must be bound to this saved source')
    palette = sections['palette']
    if sha(palette['path']) != palette['sha256']:
        raise ValueError('Exported palette changed after section export')
    palette_rows = len(read(palette['path'])['palette'])
    joint_path = options.joint_report.parent/joint['mesh']
    if sha(joint_path) != joint['mesh_sha256']:
        raise ValueError('Joint resource changed after export')
    bpy.ops.wm.open_mainfile(filepath=str(options.blend.resolve()))
    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()
    interior = json.loads(bpy.context.scene['k18_interior_contract'])
    profiles = []
    identities = {}
    records = {'source_sha256': source_hash, 'section_summary_sha256':sha(options.section_summary),
               'joint_report_sha256':sha(options.joint_report), 'sections':{},
               'scope':'Source/metadata registration only. Does not certify native packaging or game behaviour.'}
    for section in ('front', 'rear'):
        item = sections['sections'][section]
        runtime = item['runtime']
        if sha(runtime['path']) != runtime['sha256']:
            raise ValueError('Section resource changed after export: '+section)
        origin = Vector(joint['section_rest'][section]['position'])
        local = lambda p: rounded(Vector(p)-origin)
        frames = runtime['frames']
        model = item['model']
        axles = []
        for pair in ((19,20),(21,22)):
            if not frames[pair[0]]['objects']:
                continue
            z = frames[pair[0]]['pivot_unity'][2]
            if abs(z-frames[pair[1]]['pivot_unity'][2]) > 1e-5:
                raise ValueError('Paired wheel pivots differ')
            axles.append({'z':round(z,8),'steer':pair[0]==19,'parts':list(pair)})
        doors = []
        boarding = []
        for index in range(3):
            a,b = frames[4+2*index],frames[5+2*index]
            if not a['objects'] and not b['objects']:
                continue
            if not a['objects'] or not b['objects']:
                raise ValueError('K18 requires two leaves per declared doorway')
            centre_z = (a['pivot_unity'][2]+b['pivot_unity'][2])/2
            position, evidence = boarding_point(centre_z+origin.z)
            doors.append({'centreZ':round(centre_z,8), 'width':1.2,'leaves':2,'openDegrees':85,
                          'boardingPosition':local(position),
                          'boardingRotation':[0,-2**-.5,0,2**-.5],
                          'boarding':section=='front' and index==0,
                          'alighting':not (section=='front' and index==0)})
            boarding.append(evidence)
        front_z = 9.01 if section=='front' else joint['span_z'][1]-origin.z
        rear_z = joint['span_z'][0] if section=='front' else -8.885-origin.z
        riders = [{'kind':'Seated','position':local(s['cushion_unity'])}
                  for s in interior['seats'] if s['section']==section]
        # Explicit standing bays on the verified low-floor centreline. No new
        # positions are invented on the staircase, in the joint or in equipment.
        standing = [6.15,5.40,4.65,3.90,3.15,2.40,1.65,.90,.15] if section=='front' else [-3.45,-4.15]
        riders.extend({'kind':'Standing','position':local((0,interior['low_floor_y'],z))} for z in standing)
        if section=='front':
            riders.append({'kind':'Driver','position':local(interior['cockpit']['seat_centre_unity'])})
        profile = {'model':model,'mesh':model+'.swb1.gz','meshSha256':runtime['sha256'],
                   'paletteRows':palette_rows,
                   'spec':{'frontZ':round(front_z,8),'rearZ':round(rear_z,8),'halfWidth':1.27,
                           'roofY':3.32,'floorY':interior['low_floor_y'],'frontAxleZ':axles[0]['z'],
                           'rearAxleZ':axles[-1]['z'],'doors':len(doors),'driverSide':-1,
                           'section':section.title()},'axles':axles,'wheelRadius':.49,
                   'wrap':{'bottom':.2,'thresholds':[.8,2.65,4.2]},'bodyColor':[255,255,255],
                   'doors':doors,'signs':{},'nextStop':{'enabled':False,'centre':[0,0,0]},
                   'cardReaders':[],'mirrors':[],'riders':riders}
        if section=='front':
            profile['signs']['front']={'centre':[0,2.57,8.903],'width':1.72,'columns':96}
            reader = read(ROOT/'ModelSource/SWB6109EV12G/common-card-reader.json')
            reference = reader['lods'][0]['parts'][0]['positions']
            actual = points('CDLC common contactless validator')
            if len(actual)!=len(reference):
                raise ValueError('Shared reader vertex count changed; recover its transform explicitly')
            shifts = [p-Vector(q) for p,q in zip(actual,reference)]
            # mathutils Vector accumulates float32; repeated large translations
            # otherwise bias the mean while every individual vertex is correct.
            shift = Vector(tuple(math.fsum(v[i] for v in shifts)/len(shifts) for i in range(3)))
            max_reader_error = max((v-shift).length for v in shifts)
            if max_reader_error>1e-5:
                raise ValueError('Shared reader is not the expected translated common geometry; max error '+str(max_reader_error))
            entry = reader['readers'][0]
            profile['cardReaders']=[{'origin':local(Vector(entry['originUnity'])+shift),
                                    'rotation':entry['frameRotationUnityXyzw'],'xSign':entry['xSign']}]
            records['shared_reader']={'source_sha256':sha(ROOT/'ModelSource/SWB6109EV12G/common-card-reader.json'),
                                      'vertices':len(actual),'max_position_residual_m':max_reader_error,
                                      'translation_unity':rounded(shift)}
            records['mirrors'] = []
            for name in ('K18 driver-side reflective insert', 'K18 door-side reflective insert',
                         'K18 round auxiliary reflective face'):
                optical, evidence = mirror_surface(name)
                optical['centre'] = local(optical['centre'])
                profile['mirrors'].append(optical)
                records['mirrors'].append(evidence)
        else:
            profile['signs']['rear']={'centre':local((0,2.74,-8.9275)),'width':1.28,'columns':96}
        rest=joint['section_rest'][section]
        profiles.append({'id':section,'prefab':model,'restPosition':rest['position'],
                         'restRotation':rest['rotation'],'profile':profile})
        identities[section]={'capacity':85 if section=='front' else 66,
                             'guid':uuid.uuid5(uuid.NAMESPACE_URL,'cdlc.busdriver/vehicle/CRRC_K18/'+section).hex,
                             'meshSource':'runtime-sections/'+model+'.swb1.gz'}
        records['sections'][section]={'prefab':model,'runtime_file':runtime['path'],
                                     'runtime_sha256':runtime['sha256'],'seat_count':sum(r['kind']=='Seated' for r in riders),
                                     'standing_count':len(standing),'door_count':len(doors),'axles':axles,
                                     'boarding_surface_measurements':boarding}
    # maxSeparation is checked on the game's interpolated render poses (position lerp + rotation slerp per section), not on
    # the simulation transforms, which coincide exactly. Turning at speed opens the two interpolated anchors by several cm
    # (road probe 2026-10-16: 4.3 cm for a 10 degree bend change at 25 m/s), and over the limit the joint is hidden: a gap
    # flickers between the sections. .20 m leaves ~4x headroom; the CJA1 deformation stretches between the live anchors.
    manifest={'format':'cdlb-vehicle','version':2,'model':'CRRC_K18',
              'note':'2024 new Bakelong K18 study. Dimensions, axle/joint placement and complete seating remain photograph-based estimates. Capacity 151 and its 85/66 split are mod configuration.',
              'sections':profiles,'articulation':{'frontSection':'front','rearSection':'rear',
                 'frontAnchor':joint['front_anchor'],'rearAnchor':joint['rear_anchor'],'maxSeparation':.20,
                 'joint':{'format':'cdlb-joint-v1','mesh':joint['mesh'],'meshSha256':joint['mesh_sha256'],'maxBendDegrees':50}},
              'pack':{'template':'Bus02','capacity':151,'livery':[1,1,1],'strict':True,'sections':identities,
                      'jointSource':'runtime-sections/'+joint['mesh'],'paletteSource':'kit-cabin.json',
                      'title':'CRRC K18 (2024 Bakelong study)'}}
    if sha(options.blend)!=source_hash:
        raise ValueError('Source changed during profile generation')
    options.output.parent.mkdir(parents=True,exist_ok=True)
    options.output.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    records['profile_sha256']=sha(options.output)
    options.output.with_suffix('.source.json').write_text(json.dumps(records,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('K18_PROFILE_WRITTEN',str(options.output),flush=True)


if __name__=='__main__':
    main(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
