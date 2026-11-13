"""Read-only physical rail-layout and stainless-material audit of the saved source.

The geometry checks evaluate actual triangles in the canonical vehicle frame,
including every part-13 object and articulated strap. Names supplement rather
than replace geometry bounds. This does not certify a continuous collision-free
envelope or claim factory dimensions for the estimated support stations.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree

HERE=Path(__file__).resolve().parent;ROOT=HERE.parents[1]
sys.path.insert(0,str(HERE))
from animation_rig import collect_cdlc_parts,discover_strap_groups
from handrail_layout import load_layout,LAYOUT_PATH
from source_coordinates import canonical_export_context,canonical_world_matrix,source_is_corrected


def _clip(poly,axis,limit,positive):
    if not poly:return []
    result=[]
    for a,b in zip(poly,poly[1:]+poly[:1]):
        da=(a[axis]-limit)*(1 if positive else -1)
        db=(b[axis]-limit)*(1 if positive else -1)
        if da>=0:result.append(a)
        if (da<0)!=(db<0):result.append(a+(b-a)*(da/(da-db)))
    return result


def _crosses_aisle_top(points):
    """Triangle clipped against the central upper aisle, not an AABB verdict."""
    poly=list(points)
    for axis,limit,positive in ((0,-.12,True),(0,.12,False),(2,2.55,True),(2,3.15,False)):
        poly=_clip(poly,axis,limit,positive)
    return len(poly)>=3 and sum((poly[i]-poly[0]).cross(poly[i+1]-poly[0]).length for i in range(1,len(poly)-1))>1e-12


def _metal_role(obj):
    name=obj.name
    if int(obj.get('cdlc_part',-1))==13 and not name.startswith('Stop request push button'):
        return 'fixed passenger rail/support'
    if name.startswith(('Driver partition frame','Fare validator support')):return 'fixed driver/fare support'
    if name.startswith('Door ') and name.endswith(('interior grab','top drive spindle','top crank pin')):return 'door stainless hardware'
    if name.endswith(' upper grab'):return 'seat upper stainless grab'
    return None


def _assembly_clearances(master,scene,layout):
    """Actual evaluated triangle checks plus physical floor/ceiling contact rays.

    This is a discrete-pose interference audit.  A triangle intersection test
    does not by itself prove a continuous collision-free swept volume.
    """
    errors=[];intersections=[];floor_contacts=[];ceiling_contacts=[]
    rails=[o for o in master.objects if o.get('handrail_layout_revision')
           and o.type in {'MESH','CURVE'}]
    cache={}
    def snapshot(obj):
        if obj.name in cache:return cache[obj.name]
        evaluated=obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        mesh=evaluated.to_mesh();mesh.calc_loop_triangles()
        try:
            matrix=canonical_world_matrix(obj)
            vertices=[matrix@v.co for v in mesh.vertices]
            triangles=[tuple(t.vertices) for t in mesh.loop_triangles]
            bounds=[[min(v[k] for v in vertices),max(v[k] for v in vertices)] for k in range(3)]
            result=(bounds,BVHTree.FromPolygons(vertices,triangles,all_triangles=True,epsilon=1e-7))
            cache[obj.name]=result
            return result
        finally:evaluated.to_mesh_clear()
    def overlaps(a,b):
        return all(a[k][0]<=b[k][1] and b[k][0]<=a[k][1] for k in range(3))
    def audit_pair(a,b,frame,kind):
        aa,at=snapshot(a);bb,bt=snapshot(b)
        if not overlaps(aa,bb):return
        hits=at.overlap(bt)
        if hits:
            intersections.append({'frame':frame,'kind':kind,'rail':a.name,
                                  'obstacle':b.name,'intersecting_triangle_pairs':len(hits)})
    stationary=[o for o in master.objects if o.type in {'MESH','CURVE'}
                and not o.get('handrail_layout_revision')
                and ('service duct panel' in o.name or 'reader' in o.name.lower()
                     or 'validator' in o.name.lower())]
    for a in rails:
        for b in stationary:audit_pair(a,b,0,'duct_or_card_reader')
    floors=[o for o in master.objects if o.type in {'MESH','CURVE'}
            and o.name.startswith(('Floor ','Interior wheelhouse'))]
    ceilings=[o for o in master.objects if o.type in {'MESH','CURVE'}
              and o.name.startswith('Ceiling interior lining')]
    def contact(objects,point,direction):
        hits=[]
        for obj in objects:
            _,tree=snapshot(obj)
            hit,normal,face,distance=tree.ray_cast(point,direction,.10)
            if hit is not None:hits.append((distance,obj.name,hit,face))
        return min(hits,key=lambda x:x[0]) if hits else None
    for row in layout['vertical_members']:
        base=Vector(row['base']);hit=contact(floors,base+Vector((0,0,.005)),Vector((0,0,-1)))
        gap=abs(float(hit[2].z-base.z)) if hit else None
        floor_contacts.append({'id':row['id'],'base':list(base),'support':hit[1] if hit else None,
                               'vertical_gap_m':gap,'hit_face':hit[3] if hit else None})
        if gap is None or gap>.002:errors.append(row['id']+': foot does not meet an actual floor/support surface')
    for row in layout['ceiling_fittings']:
        top=Vector(row['roof_point']);hit=contact(ceilings,top-Vector((0,0,.025)),Vector((0,0,1)))
        gap=abs(float(hit[2].z-top.z)) if hit else None
        ceiling_contacts.append({'id':row['id'],'top':list(top),'support':hit[1] if hit else None,
                                 'vertical_gap_m':gap,'hit_face':hit[3] if hit else None})
        if gap is None or gap>.002:errors.append(row['id']+': attachment does not meet actual ceiling surface')
    for frame in (0,25,30,40):
        scene.frame_set(frame);bpy.context.view_layer.update();cache.clear()
        doors=[o for o in master.objects if int(o.get('cdlc_part',-1)) in (4,5,6,7)
               and o.type in {'MESH','CURVE'}]
        for a in rails:
            for b in doors:audit_pair(a,b,frame,'door_leaf_or_hardware')
    scene.frame_set(0);bpy.context.view_layer.update()
    if intersections:errors.append('Actual rail triangles intersect a duct, reader or sampled door pose')
    return {'errors':errors,'intersections':intersections,'stationary_obstacle_objects':[o.name for o in stationary],
            'floor_contact_rays':floor_contacts,
            'ceiling_contact_rays':ceiling_contacts,'door_frames':[0,25,30,40],
            'scope':'Evaluated triangle interference and mounting rays; no continuous swept-volume claim.'}


def verify_master(master,scene=None):
    scene=scene or bpy.context.scene;old_frame=scene.frame_current
    errors=[];geometry=[];materials=[];crossings=[];rear=[];columns=[];longitudinals=[];buttons=[]
    layout=load_layout();expected_grips=len(layout['grips']);expected_uprights=len(layout['vertical_members'])
    rear_limit=float(layout['parameters']['rear_seat_bay_start_y_m'])
    layout_digest=hashlib.sha256(LAYOUT_PATH.read_bytes()).hexdigest();strap_proportions=[];closed_geometry=[]
    def check(condition,message):
        if not condition:errors.append(message)
    try:
        scene.frame_set(0);bpy.context.view_layer.update()
        with canonical_export_context(master):
            parts=collect_cdlc_parts(master);groups=discover_strap_groups(master)
            check(len(parts)==34+expected_grips,'Actual part count does not match enumerated evidence-linked instances')
            check(len(groups)==expected_grips,'Actual strap groups do not match enumerated visible instances')
            check([int(g['stem'].get('cdlc_part',-1)) for g in groups]==list(range(34,34+expected_grips)),'Actual strap groups are not contiguous after part 33')
            check(scene.get('handrail_layout_evidence_sha256')==layout_digest,'Saved handrail evidence hash differs from current parameter file')
            old_crossbars=[o.name for o in master.objects if o.name.startswith('Saloon upper cross rail')]
            check(not old_crossbars,'Legacy transverse rail objects remain: '+', '.join(old_crossbars))
            if scene.get('cdlc_strap_rig_report'):
                metadata=json.loads(scene['cdlc_strap_rig_report'])
                check(metadata.get('count')==expected_grips and metadata.get('part_count')==34+expected_grips,'Saved strap metadata was not refreshed after rail-layout correction')
            actual_ids={g['grip'].get('handrail_evidence_id') for g in groups}
            check(actual_ids=={row['id'] for row in layout['grips']},'Actual grip evidence IDs differ from enumerated visible instances')
            for group in groups:
                stem,grip=group['stem'],group['grip'];matrix=canonical_world_matrix(stem)
                indices={i for face in stem.data.polygons if face.material_index==0 for i in face.vertices}
                band=[matrix@stem.data.vertices[i].co for i in indices]
                corners=[canonical_world_matrix(grip)@Vector(c) for c in grip.bound_box]
                if not band:
                    check(False,'Missing actual grey free-band faces: '+stem.name);continue
                free=max(p.z for p in band)-min(p.z for p in band)
                height=max(p.z for p in corners)-min(p.z for p in corners)
                ratio=free/height if height>0 else float('inf')
                strap_proportions.append({'id':grip.get('handrail_evidence_id'),'free_band_m':free,'actual_triangle_height_m':height,'ratio':ratio})
                check(abs(free-layout['parameters']['band_free_length_m'])<2e-6,stem.name+': actual free band differs from evidence parameter')
                check(.45<=ratio<=.67,stem.name+': actual short band/triangle proportion is outside photo estimate')
            deps=bpy.context.evaluated_depsgraph_get()
            for obj in master.objects:
                if obj.type not in {'MESH','CURVE'}:continue
                part=int(obj.get('cdlc_part',-1));role=_metal_role(obj)
                if role:
                    check(source_is_corrected(obj),'Stainless hardware bypasses physical source root: '+obj.name)
                    check(bool(obj.data.materials),'Missing stainless material: '+obj.name)
                    for mat in obj.data.materials:
                        node=next((n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED'),None) if mat.use_nodes else None
                        row={'object':obj.name,'role':role,'material':mat.name,'palette':mat.get('cdlc_palette_swatch'),
                             'metallic':float(node.inputs['Metallic'].default_value) if node else None,
                             'roughness':float(node.inputs['Roughness'].default_value) if node else None}
                        materials.append(row)
                        check(row['palette']=='BrushedStainless',obj.name+': stainless is not explicitly mapped to BrushedStainless')
                        check(row['metallic'] is not None and row['metallic']>=.99,obj.name+': handrail material is painted/dielectric, not stainless')
                        check(row['roughness'] is not None and .18<=row['roughness']<=.35,obj.name+': stainless roughness outside documented range')
                        check(not mat.get('livery_control') and not mat.get('light_id'),obj.name+': stainless incorrectly uses livery/emission channel')
                # Ensure every material advertised as stainless has the actual
                # runtime palette hint, including clamps within mixed stems.
                for mat in obj.data.materials:
                    if 'stainless' in mat.name.lower():check(mat.get('cdlc_palette_swatch')=='BrushedStainless',mat.name+': absent explicit BrushedStainless palette')
                if part!=13 and part<34:continue
                check(source_is_corrected(obj),'Rail/strap bypasses physical source root: '+obj.name)
                evaluated=obj.evaluated_get(deps);mesh=evaluated.to_mesh();mesh.calc_loop_triangles()
                try:
                    if obj.get('handrail_layout_revision'):
                        bm=bmesh.new();bm.from_mesh(mesh)
                        topology={'object':obj.name,'boundary_edges':sum(e.is_boundary for e in bm.edges),
                                  'nonmanifold_edges':sum(not e.is_manifold for e in bm.edges),
                                  'degenerate_faces':sum(f.calc_area()<1e-12 for f in bm.faces),
                                  'signed_volume_m3':bm.calc_volume(signed=True)}
                        bm.free();closed_geometry.append(topology)
                        check(not topology['boundary_edges'] and not topology['nonmanifold_edges'],obj.name+': actual shared-vertex mesh is not closed')
                        check(not topology['degenerate_faces'],obj.name+': degenerate source face')
                        check(topology['signed_volume_m3']>0,obj.name+': closed source orientation is reversed')
                    matrix=canonical_world_matrix(obj);vertices=[matrix@v.co for v in mesh.vertices]
                    check(bool(vertices),'Empty rail/strap geometry: '+obj.name)
                    if not vertices:continue
                    check(all(math.isfinite(v) for p in vertices for v in p),'Nonfinite rail/strap geometry: '+obj.name)
                    low=[min(p[k] for p in vertices) for k in range(3)];high=[max(p[k] for p in vertices) for k in range(3)]
                    geometry.append({'object':obj.name,'part':part,'canonical_bounds_m':[low,high],
                                     'triangles':len(mesh.loop_triangles)})
                    # Every real evaluated vertex is checked: this also catches
                    # a rebuilt old rail under a new, otherwise plausible name.
                    offending=[p for p in vertices if p.y>rear_limit+1e-6]
                    if offending:rear.append({'object':obj.name,'vertices_in_rear_seat_bay':len(offending),'rear_most_y':high[1]})
                    if part==13:
                        count=sum(_crosses_aisle_top([vertices[i] for i in tri.vertices]) for tri in mesh.loop_triangles)
                        if count:crossings.append({'object':obj.name,'triangles_in_upper_centre_aisle':count})
                        if obj.get('handrail_role')=='upright':
                            low_section=[p for p in vertices if p.z<low[2]+.02]
                            columns.append({'object':obj.name,'evidence_id':obj.get('handrail_evidence_id'),'base_xy_m':[sum(p[k] for p in low_section)/len(low_section) for k in (0,1)],'actual_vertical_extent_m':high[2]-low[2]})
                            check(high[2]-low[2]>2.,obj.name+': upright does not retain its expected vertical extent')
                        if str(obj.get('handrail_role','')).startswith('longitudinal_'):
                            longitudinals.append({'object':obj.name,'evidence_id':obj.get('handrail_evidence_id'),'tier':obj.get('handrail_role'),'actual_bounds':[low,high]})
                            check(high[1]-low[1]>2.5,obj.name+': longitudinal run is unexpectedly short')
                        if obj.get('handrail_role')=='request_button':buttons.append({'object':obj.name,'centre':[(a+b)/2 for a,b in zip(low,high)]})
                finally:evaluated.to_mesh_clear()
            check(not rear,'Passenger rail/strap geometry extends into the rear seating bay')
            check(not crossings,'Fixed rail triangles cross the centre of the upper aisle')
            check(len(columns)==expected_uprights,'Actual vertical members differ from enumerated visible instances')
            check({c['evidence_id'] for c in columns}=={r['id'] for r in layout['vertical_members']},'Upright evidence IDs differ')
            check(len(longitudinals)==len(layout['rails']),'Actual high/low longitudinal runs differ from evidence layout')
            check({r['evidence_id'] for r in longitudinals}=={r['id'] for r in layout['rails']},'Longitudinal evidence IDs differ')
            check(len(buttons)==len(layout['request_buttons']),'Request button instances differ from explicitly visible button list')
            for button in buttons:
                distances=[math.hypot(button['centre'][0]-row['base_xy_m'][0],button['centre'][1]-row['base_xy_m'][1]) for row in columns]
                check(bool(distances) and min(distances)<.06,button['object']+': no retained vertical support; orphan request button')
            check(bool(materials),'No intended stainless handrail surfaces found')
            clearances=_assembly_clearances(master,scene,layout)
            errors.extend(clearances['errors'])
        return {'status':'PASS' if not errors else 'FAIL','scope':'Geometry, material, evidence binding and independent-part contract only; not a complete factory-layout acceptance','reference_completeness':'REFERENCE_INCOMPLETE','reference_uncertainties':layout['reference_completeness']['missing'],'layout_sha256':layout_digest,'strap_proportions':strap_proportions,'assembly_clearances':clearances,'errors':errors,'actual_parts':len(parts),'actual_straps':len(groups),
                'geometry':geometry,'closed_geometry':closed_geometry,'stainless_materials':materials,'legacy_crossbar_objects':old_crossbars,
                'upper_aisle_crossing_triangles':crossings,'rear_seating_bay_geometry':rear,
                'actual_vertical_supports':columns,'actual_longitudinal_rails':longitudinals,'request_buttons':buttons,
                'coordinates':'Canonical construction metres: front -Y, up +Z; source physical reflection has identical Y/Z and mirrored X. All evaluated vertices/triangles are transformed from their true source matrices.',
                'rear_region':f'y > {rear_limit} m is the documented model-fit rear seating boundary; first stair and transition region are separate. Not a manufacturer dimension.',
                'upper_aisle_region':'Triangle clipping against |x| <= 0.12 m and 2.55 <= z <= 3.15 m; prevents replacement crossbars under new names.',
                'limitations':['Support station dimensions are layout estimates, not factory measurements.','This is a geometry occupancy/material audit; dedicated door/wheel and strap-pose tests remain required.','No proof of continuous collision-free motion between sampled poses.']}
    finally:scene.frame_set(old_frame);bpy.context.view_layer.update()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--blend',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/SWB6109EV12G.blend')
    parser.add_argument('--report',type=Path,default=ROOT/'ModelSource/SWB6109EV12G/handrail-validation.json')
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    digest=hashlib.sha256(args.blend.read_bytes()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(args.blend))
    result=verify_master(bpy.data.collections['SWB6109EV12G | editable components'])
    if hashlib.sha256(args.blend.read_bytes()).hexdigest()!=digest:raise RuntimeError('Read-only rail verification changed the source file')
    result.update(source=str(args.blend),source_sha256=digest)
    args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('HANDRAIL_GEOMETRY_VERIFY_'+result['status']+' '+json.dumps({'parts':result['actual_parts'],'straps':result['actual_straps'],'errors':result['errors']}),flush=True)
    if result['errors']:raise RuntimeError('Physical handrail layout/material validation failed')


if __name__=='__main__':main()
