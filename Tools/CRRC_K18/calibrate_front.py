"""Photo camera fitting from a dimension scaffold, never from the stylised nose.

Run with Blender's bundled Python (numpy only):
  python calibrate_front.py --samples 80
Run the independent Blender projection cross-check, without loading an asset:
  blender -b --factory-startup --python calibrate_front.py -- --verify-blender REPORT.json

The scaffold and image picks are estimates, not a survey. In particular the
frontal/side cutouts do not identify focal length. Their deliverable cameras are
explicit orthographic approximations; an arbitrary distance is not a measured
camera location. No source blend, generator, image, or game resource is modified.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
from datetime import datetime, timezone

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
OUTPUT_ROOT = ROOT / "out" / "k18-front-refinement"
REFERENCE = ROOT / "ModelSource" / "CRRC_K18" / "reference"
U_TO_B = np.array([[-1., 0., 0.], [0., 0., -1.], [0., 1., 0.]])
SENSOR_WIDTH_MM = 36.0
SCAFFOLD = {
    "length_m": 17.90, "width_m": 2.54, "height_m": 3.30,
    "wheel_radius_m": .49, "front_axle_z_m": 6.37,
    "front_middle_spacing_m": 5.80, "middle_rear_spacing_m": 6.10,
    "front_face_z_m": 8.85, "rear_face_z_m": -8.85,
    "upper_body_rail_y_m": 2.98, "lower_body_edge_y_m": .25,
}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def pick(name, xyz, uv, sigma, note=""):
    return {"id": name, "unity_m": xyz, "pixel": uv,
            "pixel_sigma_estimate": sigma, "note": note}


def image_sets():
    """Original-raster picks; no lamp, grille, badge, V-strip or glass geometry."""
    a = SCAFFOLD["front_axle_z_m"]
    b = a - SCAFFOLD["front_middle_spacing_m"]
    c = b - SCAFFOLD["middle_rear_spacing_m"]
    hero = [
        pick("front_near_wheel_hub", [1.255,.49,a], [755,786], 6,
             "Ellipse hub centre; polished highlights and hub recess limit accuracy."),
        pick("middle_near_wheel_hub", [1.255,.49,b], [520,724], 5),
        pick("rear_near_wheel_hub", [1.255,.49,c], [395,686], 6),
        pick("front_near_upper_body_corner", [1.22,2.98,8.85], [926,313], 9,
             "Upper structural rail, below rounded cap. Exact corner surface is uncertain."),
        pick("front_far_upper_body_corner", [-1.22,2.98,8.85], [1366,335], 9),
        pick("front_near_lower_body_corner", [1.22,.25,8.85], [906,858], 7),
        pick("front_far_lower_body_corner", [-1.22,.25,8.85], [1383,831], 7),
        pick("rear_near_upper_body_corner", [1.22,2.98,-8.85], [366,497], 12,
             "Rear rounded return is foreshortened; lower weight than wheel hubs."),
        pick("rear_near_lower_body_corner", [1.22,.25,-8.85], [359,698], 12),
        pick("front_tyre_ground_tangent", [1.255,0.,a], [759,868], 9,
             "Photographic tyre contact, not composited road vanishing point."),
        pick("middle_tyre_ground_tangent", [1.255,0.,b], [523,774], 8),
        pick("rear_tyre_ground_tangent", [1.255,0.,c], [397,725], 9),
    ]
    front = [
        pick("near_lower_body_corner", [1.22,.25,8.85], [242,289], 3),
        pick("far_lower_body_corner", [-1.22,.25,8.85], [449,289], 3),
        pick("near_upper_rail_corner", [1.18,2.98,8.85], [247,87], 4),
        pick("far_upper_rail_corner", [-1.18,2.98,8.85], [444,87], 4),
        pick("cap_top_centre", [0.,3.30,8.72], [345,60], 4),
        pick("near_tyre_bottom", [1.10,0.,a], [270,300], 5),
        pick("far_tyre_bottom", [-1.10,0.,a], [427,300], 5),
    ]
    side = [
        pick("rear_wheel_hub", [1.255,.49,c], [181,240], 3),
        pick("middle_wheel_hub", [1.255,.49,b], [359,240], 3),
        pick("front_wheel_hub", [1.255,.49,a], [529,240], 3),
        pick("rear_lower_corner", [1.22,.25,-8.85], [83,246], 3),
        pick("front_lower_corner", [1.22,.25,8.85], [603,247], 3),
        pick("front_upper_rail_corner", [1.22,2.98,8.85], [600,175], 4),
        pick("rear_upper_rail_corner", [1.22,2.98,-8.85], [85,175], 4),
        pick("front_cap_top", [1.00,3.30,8.60], [583,165], 5),
        pick("front_tyre_bottom", [1.255,0.,a], [529,255], 3),
        pick("middle_tyre_bottom", [1.255,0.,b], [359,255], 3),
        pick("rear_tyre_bottom", [1.255,0.,c], [181,255], 3),
    ]
    return [
        {"id":"hero", "file":"official-k18-detail-00.jpg", "size":[1920,1000],
         "type":"PERSP", "picks":hero},
        {"id":"front", "file":"new-bakelong-2024-02.png", "size":[700,393],
         "type":"ORTHO", "picks":front, "yaw":math.pi,
         "target":[0.,1.65,8.85]},
        {"id":"side", "file":"new-bakelong-2024-03.png", "size":[700,393],
         "type":"ORTHO", "picks":side, "yaw":-math.pi/2,
         "target":[0.,1.65,0.]},
    ]


def basis(yaw, pitch=0., roll=0.):
    """Rows: image right, image up, viewing direction, in Unity coordinates."""
    sy, cy, sp, cp = math.sin(yaw), math.cos(yaw), math.sin(pitch), math.cos(pitch)
    forward = np.array([sy*cp, sp, cy*cp])
    right = np.array([cy, 0., -sy])
    up = np.array([-sy*sp, cp, -cy*sp])
    cr, sr = math.cos(roll), math.sin(roll)
    return np.stack((cr*right+sr*up, -sr*right+cr*up, forward))


def project_unity(points, camera):
    """Independent numpy projector. Pixels use top-left origin, square pixels.

    camera contains location_unity_m, basis_rows_unity, principal_point_px,
    projection and focal_length_px (PERSP) or pixels_per_metre (ORTHO).
    """
    p = np.asarray(points, dtype=float).reshape(-1, 3)
    local = (p-np.asarray(camera["location_unity_m"])) @ np.asarray(camera["basis_rows_unity"]).T
    if camera["projection"] == "PERSP":
        scale = camera["focal_length_px"] / local[:,2]
    else:
        scale = camera["pixels_per_metre"]
    xy = np.column_stack((local[:,0]*scale, -local[:,1]*scale))
    return xy + np.asarray(camera["principal_point_px"]), local[:,2]


def camera_from_params(p, spec):
    width, height = spec["size"]
    if spec["type"] == "PERSP":
        location = p[:3]
        rows = basis(*p[3:6])
        focal = math.exp(p[6])
        principal = np.array([width/2, height/2])
        target = location + rows[2]*10.
        extra = {"focal_length_px":focal, "lens_mm":focal*SENSOR_WIDTH_MM/width}
        roll = p[5]
    else:
        rows = basis(spec["yaw"], 0., p[1])
        target = np.asarray(spec["target"])
        location = target-rows[2]*50.
        principal = p[2:4]
        scale = math.exp(p[0])
        extra = {"pixels_per_metre":scale, "ortho_scale_m":width/scale,
                 "lens_mm":50., "lens_used":False,
                 "camera_distance_m_is_arbitrary":50.}
        roll = p[1]
    # Blender looks down local -Z, with local Y up. The reflection between
    # Unity/Blender is intentional and produces a proper camera rotation.
    rb = rows @ U_TO_B.T
    matrix = np.eye(4)
    matrix[:3,:3] = np.column_stack((rb[0], rb[1], -rb[2]))
    matrix[:3,3] = U_TO_B @ location
    return {"projection":spec["type"], "resolution":spec["size"],
            "location_unity_m":location.tolist(), "target_unity_m":target.tolist(),
            "basis_rows_unity":rows.tolist(), "principal_point_px":principal.tolist(),
            "location_blender_m":(U_TO_B @ location).tolist(),
            "target_blender_m":(U_TO_B @ target).tolist(),
            "roll_degrees_basis_convention":math.degrees(roll),
            "matrix_world_blender":matrix.tolist(), "sensor_width_mm":SENSOR_WIDTH_MM,
            "sensor_fit":"HORIZONTAL", "pixel_aspect":[1.,1.],
            "shift_x":float((width/2-principal[0])/width),
            "shift_y":float((principal[1]-height/2)/width), **extra}


def project_params(p, spec, points):
    return project_unity(points, camera_from_params(p, spec))


def optimize(initial, residual, bounds, max_iter=130):
    """Small finite-difference Levenberg-Marquardt with bounded parameters."""
    p = np.clip(np.asarray(initial, dtype=float), bounds[0], bounds[1])
    damping = .001
    r = residual(p)
    value = float(r @ r)
    accepted = 0
    for iteration in range(max_iter):
        jac = np.empty((len(r), len(p)))
        for j in range(len(p)):
            step = 1.e-5 * max(1., abs(p[j]))
            q = p.copy(); q[j] += step
            jac[:,j] = (residual(q)-r)/step
        jtj = jac.T @ jac
        diagonal = np.maximum(np.diag(jtj), 1.e-8)
        try:
            delta = np.linalg.solve(jtj+damping*np.diag(diagonal), -jac.T@r)
        except np.linalg.LinAlgError:
            damping *= 10.
            continue
        candidate = np.clip(p+delta, bounds[0], bounds[1])
        trial = residual(candidate)
        trial_value = float(trial @ trial)
        if trial_value < value:
            change = value-trial_value
            p, r, value = candidate, trial, trial_value
            accepted += 1
            damping = max(damping*.3, 1.e-10)
            if change < 1.e-9 or np.linalg.norm(delta) < 1.e-8:
                break
        else:
            damping = min(damping*8., 1.e12)
            if damping >= 1.e12:
                break
    return p, {"weighted_squared_error":value, "iterations":iteration+1,
               "accepted_steps":accepted}


def fit(spec, points, observations, sigma, initial=None, iterations=130):
    def residual(p):
        projected, depth = project_params(p, spec, points)
        result = ((projected-observations)/sigma[:,None]).ravel()
        if np.min(depth) <= .1:
            result += np.maximum(.1-np.min(depth), 0.)*1.e5
        return result
    if spec["type"] == "PERSP":
        low = np.array([1.3,-5.,9.1,-math.pi,-.7,-.2,math.log(250)])
        high = np.array([60.,20.,130.,-math.pi/2,.7,.2,math.log(18000)])
        starts = []
        for loc in ([5,2,15],[8,3,19],[12,4,25],[4,1.7,14]):
            look = np.array([0.,1.6,2.])-loc
            yaw = math.atan2(look[0],look[2])
            pitch = math.atan2(look[1],math.hypot(look[0],look[2]))
            starts.append([*loc,yaw,pitch,0.,math.log(1400.)])
    else:
        low = np.array([math.log(5.),-.1,-500.,-500.])
        high = np.array([math.log(1000.),.1,2000.,2000.])
        starts = [[math.log(80. if spec["id"] == "front" else 29.5),0.,345.,190.]]
    if initial is not None:
        starts = [initial]
    solutions = [optimize(start,residual,(low,high),iterations) for start in starts]
    p, info = min(solutions,key=lambda entry:entry[1]["weighted_squared_error"])
    info["starts"] = len(starts)
    info["on_parameter_bound"] = bool(np.any(np.isclose(p,low,atol=1.e-4)) or np.any(np.isclose(p,high,atol=1.e-4)))
    return p, info


def sensitivity(spec, points, observations, sigma, nominal, count):
    rng = np.random.default_rng(6182024 + {"hero":0,"front":1,"side":2}[spec["id"]])
    values, rejected = [], 0
    for _ in range(count):
        sample = points.copy()
        sample[:,0] *= rng.normal(1., .02/SCAFFOLD["width_m"])
        sample[:,1] *= rng.normal(1., .12/SCAFFOLD["height_m"])
        sample[:,2] *= rng.normal(1., .08/SCAFFOLD["length_m"])
        # Axle locations have additional uncertainty due to variant/overhang.
        for i, landmark in enumerate(spec["picks"]):
            if "wheel_hub" in landmark["id"] or "tyre" in landmark["id"]:
                sample[i,2] += rng.normal(0.,.10)
        obs = observations+rng.normal(size=observations.shape)*sigma[:,None]
        p, info = fit(spec,sample,obs,sigma,nominal,80)
        camera = camera_from_params(p,spec)
        if info["on_parameter_bound"] or not np.all(np.isfinite(p)):
            rejected += 1
            continue
        values.append([*camera["location_blender_m"],camera["lens_mm"],
                       camera.get("ortho_scale_m",0.),camera["roll_degrees_basis_convention"]])
    names = ["blender_x_m","blender_y_m","blender_z_m","lens_mm","ortho_scale_m","basis_roll_degrees"]
    ranges = {}
    if values:
        quantiles = np.percentile(values,[5,50,95],axis=0)
        ranges = {name:{"p05":float(v[0]),"median":float(v[1]),"p95":float(v[2])}
                  for name,v in zip(names,quantiles.T)}
    return {"requested_samples":count,"accepted_samples":len(values),"bound_or_nonfinite_rejections":rejected,
            "seed":6182024 + {"hero":0,"front":1,"side":2}[spec["id"]],
            "meaning":"Illustrative sensitivity envelope, not a calibrated confidence interval. Includes manual pick and scaffold perturbations; unknown lens distortion/composite edits not modelled.",
            "perturbations_1sigma":{"width_m":.02,"height_m":.12,"length_m":.08,"individual_axle_z_m":.10},
            "parameter_ranges":ranges}


def solve_image(spec, samples):
    xyz = np.array([p["unity_m"] for p in spec["picks"]])
    uv = np.array([p["pixel"] for p in spec["picks"]])
    sigma = np.array([p["pixel_sigma_estimate"] for p in spec["picks"]])
    p, solver = fit(spec,xyz,uv,sigma)
    camera = camera_from_params(p,spec)
    predicted, depths = project_unity(xyz,camera)
    errors = predicted-uv
    norms = np.linalg.norm(errors,axis=1)
    rows = []
    for original, projected, error, norm, depth in zip(spec["picks"],predicted,errors,norms,depths):
        rows.append({**original,"projected_pixel":projected.tolist(),"residual_xy_px":error.tolist(),
                     "residual_norm_px":float(norm),"camera_depth_m":float(depth)})
    return {"id":spec["id"],"reference_file":str((REFERENCE/spec["file"]).relative_to(ROOT)),
            "reference_sha256":sha(REFERENCE/spec["file"]),"camera":camera,
            "scaffold_landmarks":rows,"solver":solver,
            "pixel_residuals":{"rms_2d_px":float(np.sqrt(np.mean(norms**2))),
                               "max_2d_px":float(np.max(norms)),"median_2d_px":float(np.median(norms))},
            "fit_status":"APPROXIMATE_PHOTO_MATCH_NOT_GEOMETRY_ACCEPTANCE",
            "sensitivity":sensitivity(spec,xyz,uv,sigma,p,samples),
            "limitations": [
                "No lamp, badge, silver strip, grille or source nose vertex is fitted.",
                "Principal point fixed to raster centre for perspective; shift is a display placement parameter in orthographic cameras.",
                "Scaffold corner coordinates represent approximate structural surfaces, not measured intersections.",
                "Some systematic residual can arise from height/version mismatch, corner roundness, pick error, lens distortion or brochure processing. These causes are not separable here.",
                "Orthographic front/side cameras do not measure focal length, camera distance or perspective. Do not present them as recovered original cameras."
            ]}


def self_check():
    rows = basis(-2.8,.06,.015)
    camera = {"location_unity_m":[6,2,17],"basis_rows_unity":rows.tolist(),
              "projection":"PERSP","focal_length_px":1300.,"principal_point_px":[960,500]}
    # Build points by camera coordinates and verify exact round trip.
    local = np.array([[0,0,10],[1,2,10],[-2,-1,15]],float)
    world = local@rows+camera["location_unity_m"]
    predicted,depth = project_unity(world,camera)
    expected = np.array([[960,500],[1090,240],[960-2600/15,500+1300/15]])
    error = float(np.max(np.abs(predicted-expected)))
    assert error < 1.e-8 and np.all(depth > 0), (error,depth)
    assert np.max(np.abs(rows@rows.T-np.eye(3))) < 1.e-12
    return {"synthetic_projection_max_pixel_error":error,"status":"PASS"}


def verify_blender(report_path):
    import bpy
    from mathutils import Matrix, Vector
    from bpy_extras.object_utils import world_to_camera_view
    report = json.loads(Path(report_path).read_text(encoding="utf-8"))
    scene = bpy.context.scene
    results = []
    for entry in report["fits"]:
        c = entry["camera"]
        data = bpy.data.cameras.new("Calibration verification only")
        obj = bpy.data.objects.new("Calibration verification only",data)
        scene.collection.objects.link(obj)
        data.type=c["projection"];data.sensor_fit="HORIZONTAL"
        data.sensor_width=c["sensor_width_mm"];data.lens=c["lens_mm"]
        data.shift_x=c["shift_x"];data.shift_y=c["shift_y"]
        if data.type == "ORTHO":data.ortho_scale=c["ortho_scale_m"]
        obj.matrix_world=Matrix(c["matrix_world_blender"])
        w,h=c["resolution"]
        scene.render.resolution_x=w;scene.render.resolution_y=h;scene.render.resolution_percentage=100
        scene.render.pixel_aspect_x=1.;scene.render.pixel_aspect_y=1.
        bpy.context.view_layer.update()
        errors=[]
        for landmark in entry["scaffold_landmarks"]:
            coord=Vector((U_TO_B@np.array(landmark["unity_m"])).tolist())
            ndc=world_to_camera_view(scene,obj,coord)
            px=np.array([ndc.x*w,(1.-ndc.y)*h])
            errors.append(float(np.max(np.abs(px-landmark["projected_pixel"]))))
        maximum=max(errors)
        results.append({"id":entry["id"],"max_pixel_error_vs_numpy":maximum,
                        "status":"PASS" if maximum<.01 else "FAIL"})
        bpy.data.objects.remove(obj,do_unlink=True);bpy.data.cameras.remove(data)
    output = Path(report_path).with_name(Path(report_path).stem+"-blender-projection-check.json")
    output.write_text(json.dumps({"report_sha256":sha(report_path),"blender_version":bpy.app.version_string,
                                "status":"PASS" if all(r["status"]=="PASS" for r in results) else "FAIL",
                                "checks":results},indent=2)+"\n",encoding="utf-8")
    print(output)
    if any(r["status"]!="PASS" for r in results):raise RuntimeError(results)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--samples",type=int,default=80)
    parser.add_argument("--output",type=Path,default=OUTPUT_ROOT/"camera-calibration.json")
    parser.add_argument("--verify-blender",type=Path)
    args=parser.parse_args(sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else None)
    if args.verify_blender:
        verify_blender(args.verify_blender);return
    if not 0 <= args.samples <= 1000:raise ValueError("samples must be 0..1000")
    output=args.output.resolve()
    if not output.is_relative_to(OUTPUT_ROOT.resolve()):raise ValueError("Reports must remain in out/k18-front-refinement")
    if output.exists():raise FileExistsError("Preserve previous evidence; select a new --output filename")
    report={"schema_version":1,"created_utc":datetime.now(timezone.utc).isoformat(),
            "tool_sha256":sha(__file__),"status":"APPROXIMATE_CAMERA_GUIDES",
            "coordinate_contract":"Unity right +X, up +Y, front +Z. Blender=(-Unity.x,-Unity.z,Unity.y). Pixel origin top left.",
            "scaffold":SCAFFOLD,
            "constraint_provenance":{
                "official_page":"https://www.crrcgc.cc/zcdd/zccp/gjkc/k18/index.html",
                "official_parameter_block":"17980 x 2540 x 3300 mm, 5800+6100 mm axles. Page variant/configuration inconsistencies remain unresolved.",
                "length":"17.90 m selected modelling baseline / 2024 article; differs by .08 m from official parameter block.",
                "overhang_and_axle_origin":"Front axle +6.37 m and approximate ±8.85 m face planes are photo/model estimates. Axle spacings use official 5.80+6.10 m for this independent scaffold, not generator axle positions.",
                "width_height":"2.54 m width and 3.30 m height are attributed published values; photographed vehicle configuration not independently measured.",
                "corner_surfaces":"x=±1.22 m structural return, y=.25/2.98 m edges and tyre radius .49 m are explicit geometric estimates.",
                "no_model_fitting":"Does not read the current blend, source nose geometry or lamp/trim coordinates."},
            "self_check":self_check(),"fits":[solve_image(s,args.samples) for s in image_sets()],
            "application":"Use matrix_world_blender directly (row-major 4x4), camera type, lens/ortho_scale, HORIZONTAL 36mm sensor, shifts, exact resolution and square pixels. Do not also apply roll to the supplied matrix.",
            "excluded_evidence":["Road/horizon/shadow in the composite", "Mirror endpoints", "Lamp/trim/grille/badge landmarks", "Current stylised front-face vertices"],
            "uncertainty_warning":"A small reprojection residual does not establish the true lens, survey accuracy, front-surface depth or factory dimensions. Frontal/side orthographic assumptions and height mismatch are particularly material."}
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(report,indent=2,ensure_ascii=False)+"\n",encoding="utf-8")
    print(output)
    for entry in report["fits"]:
        print(entry["id"],entry["camera"]["projection"],entry["pixel_residuals"],entry["solver"])


if __name__ == "__main__":
    main()
