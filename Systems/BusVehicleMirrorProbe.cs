using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Game;
using Game.SceneFlow;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Opt-in MAIN MENU diagnostic only. The caller supplies its fully assembled,
    /// closed-door, cab-visible production CabInstance WITH its imported BusGlass.
    /// This helper does not create/pose/hide the bus, load a map, change settings,
    /// replace any production material, or manually render a mirror camera.
    /// Camera rendering is driven by Unity, with CabMirrors.Update in LateUpdate.
    /// </summary>
    internal static class BusVehicleMirrorProbe
    {
        // Keep the driver's projection unchanged while resolving the small convex
        // mirror and per-texel optical calibration through the production glass.
        const int Width = 8192, Height = 5120;
        const int MinFrames = 24, MinRenders = 6;
        const float TargetDistance = 4f;
        // The mirror RT is 8-bit sRGB under the camera's own exposure, so tile emission is set per mirror from a neutral
        // photometric step: CalibrationNits of white on every tile, read back from the RT, gives the actual exposure multiplier.
        // Primaries do not land at the white-derived level (.34 to 1.0 at a .5 to .7 aim), so each colour tile is then rescaled
        // (up to ColourPasses times) until its own channels read within ColourTolerance of TargetLinear in the RT. Brighter
        // targets leak red into the green tile (a .7 aim read green as yellow on the convex mirror). Fixed 15000 nits clipped;
        // 1 nit was black.
        const float CalibrationNits = 1000f, TargetLinear = .6f;
        const float CalibrationLow = .02f, CalibrationHigh = .85f;
        const int CalibrationAttempts = 8, ColourPasses = 3;
        const float ColourTolerance = .08f;
        const float TargetTileHalfSize = .40f;
        static readonly Color[] Colours = { Color.red, Color.green, Color.blue, Color.yellow };
        static readonly Vector2[] TileOffsets = {
            new Vector2(-.24f, .24f), new Vector2(.24f, .24f),
            new Vector2(-.24f, -.24f), new Vector2(.24f, -.24f)
        };

        sealed class MirrorInfo
        {
            internal string Name;
            internal Camera Camera;
            internal MeshFilter Glass;
            internal Material Material;
            internal RenderTexture Texture;
            internal GameObject Board;
            internal Vector3 Centre, Normal, Reflected, CameraPosition;
            internal Vector3[] TileCentres;
            internal Material[] TileMaterials;
            internal Color[] TileColours;
            internal int Renders;
        }

        sealed class Projection
        {
            internal bool[] Mask = new bool[Width * Height];
            internal Vector2[] Uv = new Vector2[Width * Height];
            internal float[] Depth = new float[Width * Height];
            internal int Pixels;
            internal int NearPlaneCrossings;
            internal float Area, Retained;
            internal bool AllInFront = true, OutlineInside = true;
        }

        /// <param name="verticalFov">58 is the core driver's view default; 40..85 is its production range.</param>
        /// <param name="bindings">Caller evidence such as package/source/runtime hashes and LOD provenance.</param>
        internal static async Task<Dictionary<string, object>> Run(CabInstance instance, BusInteriorSpec spec,
            string directory, int lod = 0, float verticalFov = 58f, IDictionary<string, object> bindings = null)
        {
            var rows = new List<Dictionary<string, object>>();
            var files = new List<object>();
            var cleanup = new List<Action>();
            var report = new Dictionary<string, object> {
                ["schema"] = "cdlc-main-menu-production-mirrors-v1", ["status"] = "running",
                ["utc"] = DateTime.UtcNow.ToString("o"), ["lod"] = lod,
                ["scope"] = "Production CabMirrors, complete caller-supplied bus assembly, controlled targets in a main-menu scene; fixed production eye with head turns.",
                ["not_tested"] = new[] { "map entity possession/driving", "road traffic visibility/streaming in mirrors", "distance LOD switching", "all user camera settings" },
                ["manual_review"] = "pending", ["automatic_passed"] = false,
                ["bindings"] = bindings, ["mirrors"] = rows, ["files"] = files,
                ["target_reference"] = new { shader = "HDRP/Lit", emission = "per mirror from its neutral luminance calibration (row luminance_calibration)",
                    calibration_nits = CalibrationNits, target_linear_rt = TargetLinear,
                    tile_half_size_in_board_half_extents = TargetTileHalfSize,
                    tile_offsets = TileOffsets.Select(v => new[] { v.x, v.y }).ToArray(),
                    albedo = "Black tiles; colour identity comes only from the fixed emission input.",
                    scope = "Controlled calibration markers, not a road-object brightness test; actual RT and complete-eye colour gates still apply." },
                ["thresholds"] = new { minimum_unity_frames_per_stage = MinFrames, minimum_mirror_and_eye_renders_per_stage = MinRenders,
                    minimum_projected_pixels = 100, minimum_projection_retained = .95f, maximum_camera_angle_error_deg = .1f,
                    maximum_camera_position_error_m = .003f, pixel_change = 35, minimum_uv_colour_agreement = .80f }
            };
            var mirrors = new CabMirrors();
            BusVehicleMirrorProbePump pump = null;
            Camera eye = null;
            int eyeRenders = 0;
            var info = new List<MirrorInfo>();
            Action<ScriptableRenderContext, Camera> onRendered = (context, camera) => {
                if (camera == eye) eyeRenders++;
                foreach (var item in info) if (item.Camera == camera) item.Renders++;
            };
            try
            {
                CheckMainMenu();
                Require(instance != null && instance.Alive && instance.Root != null, "A live complete CabInstance is required.");
                Require(verticalFov >= 40f && verticalFov <= 85f, "FOV must stay within the core production 40..85 degree range.");
                var profile = VehicleModels.Find(spec.ModelVariant) ?? GenericBusReview.Profile(spec);
                Require(profile != null && profile.MirrorCount > 0, "This diagnostic requires declared mirror surfaces.");
                int mirrorCount = profile.MirrorCount;
                report["expected_mirror_count"] = mirrorCount;
                var assemblyModule = typeof(BusVehicleMirrorProbe).Assembly;
                report["assembly_mvid"] = assemblyModule.ManifestModule.ModuleVersionId.ToString("D");
                report["runtime_resource"] = GenericBusReview.Resource(profile);
                report["uv_companion_sha256"] = GenericBusReview.UvHash(profile);
                using (Stream resource = GenericBusReview.Open(profile))
                using (var sha = SHA256.Create())
                    report["runtime_sha256"] = BitConverter.ToString(sha.ComputeHash(resource)).Replace("-", "").ToLowerInvariant();
                Require(!string.IsNullOrWhiteSpace(directory), "An output directory is required.");
                Directory.CreateDirectory(directory);
                Transform root = instance.Root;
                Require(!root.GetComponentsInChildren<Camera>(true).Any(c => c.name.StartsWith("Bus Driver mirror camera ", StringComparison.Ordinal)),
                    "Caller root already owns mirror cameras; refusing duplicate production mirrors.");
                Require((root.lossyScale - Vector3.one).sqrMagnitude < 1e-8f, "Caller root must retain unit production scale.");
                Renderer[] assembly = root.GetComponentsInChildren<Renderer>(true);
                Require(assembly.Count(r => r.enabled && r.gameObject.activeInHierarchy) >= 20, "Full assembly is not visible.");
                Require(assembly.Any(r => r.enabled && r.gameObject.activeInHierarchy && r.name.StartsWith("Glass ", StringComparison.Ordinal) &&
                    r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name == "BH/GlsShader")),
                    "Visible imported production BusGlass is required; a bare cab cannot pass this test.");
                report["assembly_renderers_before"] = assembly.Length;
                report["assembly_visibility"] = assembly.Select(r => new { r.name, enabled = r.enabled, active = r.gameObject.activeInHierarchy }).ToArray();
                Vector3 eyeModel = V(spec.ModelVariant == BusModelVariant.Generic ? BusInterior.CameraEye(spec) : BusInterior.Eye(spec));
                Vector3 eyeWorld = root.TransformPoint(eyeModel);
                report["production_eye_model_m"] = A(eyeModel);
                report["production_eye_world_m"] = A(eyeWorld);
                report["vertical_fov_degrees"] = verticalFov;
                report["head_turns"] = "Eye position fixed; forward view plus rotation to each authored mirror centre. Forward-view visibility is recorded separately and is not assumed.";

                var eyeObject = new GameObject("CDLC mirror probe driver eye") { hideFlags = HideFlags.DontSave };
                cleanup.Add(() => UnityEngine.Object.Destroy(eyeObject));
                eye = eyeObject.AddComponent<Camera>();
                eye.enabled = false;
                eye.nearClipPlane = .04f; eye.farClipPlane = 150f;
                eye.fieldOfView = verticalFov; eye.aspect = Width / (float)Height;
                eye.allowHDR = false; eye.allowMSAA = false; eye.depth = 100f;
                eye.useOcclusionCulling = false;
                BusGlassProbe.Plain(eyeObject.AddComponent<HDAdditionalCameraData>());
                var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "CDLC mirror probe driver view", hideFlags = HideFlags.DontSave };
                cleanup.Add(() => { target.Release(); UnityEngine.Object.Destroy(target); });
                target.Create(); eye.targetTexture = target;
                eye.transform.SetPositionAndRotation(eyeWorld, root.rotation);
                Require(mirrors.Create(root, spec), "Production CabMirrors.Create failed.");
                cleanup.Add(mirrors.Destroy);
                int createdCameras = root.GetComponentsInChildren<Camera>(true).Count(c => c.name.StartsWith("Bus Driver mirror camera ", StringComparison.Ordinal));
                int createdGlass = root.GetComponentsInChildren<MeshFilter>(true).Count(m => m.name.StartsWith("Bus Driver mirror glass ", StringComparison.Ordinal));
                report["created_mirror_cameras"] = createdCameras; report["created_mirror_glasses"] = createdGlass;
                Require(createdCameras == mirrorCount && createdGlass == mirrorCount, "Production mirror count differs from the authored profile.");
                for (int i = 0; i < mirrorCount; i++)
                {
                    var surface = MirrorSurface.Of(spec, i);
                    Transform ct = root.Find("Bus Driver mirror camera " + i);
                    Transform gt = root.Find("Bus Driver mirror glass " + i);
                    Require(ct != null && gt != null, "Production mirror child missing: " + i);
                    var item = new MirrorInfo { Name = MirrorLabel(surface.Centre.x, spec.DriverSide, surface.Oval),
                        Camera = ct.GetComponent<Camera>(), Glass = gt.GetComponent<MeshFilter>() };
                    item.Material = gt.GetComponent<Renderer>().sharedMaterial;
                    item.Texture = item.Camera.targetTexture;
                    item.Centre = root.TransformPoint(V(surface.OverlayCentre));
                    item.Normal = root.TransformDirection(V(surface.Normal)).normalized;
                    item.Reflected = Vector3.Reflect((item.Centre - eyeWorld).normalized, item.Normal).normalized;
                    item.CameraPosition = item.Centre + item.Reflected * .04f;
                    Require(item.Texture != null && item.Glass.sharedMesh != null, "Production reflection RT/mesh missing: " + i);
                    item.Board = MakeBoard(i, item, root, surface, cleanup);
                    item.Board.SetActive(false);
                    info.Add(item);
                }
                RenderPipelineManager.endCameraRendering += onRendered;
                cleanup.Add(() => RenderPipelineManager.endCameraRendering -= onRendered);
                pump = eyeObject.AddComponent<BusVehicleMirrorProbePump>();
                pump.Mirrors = mirrors; pump.Eye = eye; pump.Running = true;
                eye.enabled = true;

                // Full forward view records what is visible without turning the head.
                await Settle(pump, () => eyeRenders, null);
                Save(directory, "lod" + lod + "-mirrors-forward", target, files);
                report["forward_projection"] = info.Select((item, i) => {
                    Projection p = Project(item.Glass, eye);
                    return new { mirror = i, Names = item.Name, pixels = p.Pixels, retained = p.Retained,
                        all_in_front = p.AllInFront, outline_inside = p.OutlineInside };
                }).ToArray();

                for (int i = 0; i < info.Count; i++)
                {
                    CheckMainMenu();
                    MirrorInfo item = info[i];
                    var row = new Dictionary<string, object> { ["index"] = i, ["name"] = item.Name, ["status"] = "running" };
                    rows.Add(row);
                    try
                    {
                        // A head turn only; the driver eye is never translated to expose the mirror.
                        eye.transform.SetPositionAndRotation(eyeWorld, Quaternion.LookRotation(item.Centre - eyeWorld, root.up));
                        foreach (MirrorInfo other in info) other.Board.SetActive(false);
                        Projection projection = Project(item.Glass, eye);
                        Projection targetProjection = Project(item.Board.GetComponent<MeshFilter>(), eye, false);
                        int directTargetOverlap = 0, directTargetInFront = 0;
                        for (int pixel = 0; pixel < projection.Mask.Length; pixel++)
                            if (projection.Mask[pixel] && targetProjection.Mask[pixel])
                            {
                                directTargetOverlap++;
                                if (targetProjection.Depth[pixel] <= projection.Depth[pixel] + .005f) directTargetInFront++;
                            }
                        bool targetSetup = directTargetInFront == 0 && targetProjection.NearPlaneCrossings == 0;
                        row["centre_world_m"] = A(item.Centre); row["normal_world"] = A(item.Normal);
                        row["reflected_direction_world"] = A(item.Reflected);
                        row["target_board_world_m"] = A(item.Board.transform.position);
                        row["target_placement"] = "4 m along reflected camera axis, shifted to the vehicle-outboard half of its frustum; no bus geometry hidden or moved.";
                        row["direct_target_exclusion"] = new { overlapping_projected_pixels = directTargetOverlap,
                            target_pixels_in_front_of_mirror = directTargetInFront, target_near_plane_crossings = targetProjection.NearPlaneCrossings,
                            passed = targetSetup, method = "Every projected target triangle (both faces) compared per pixel to the nearest mirror triangle; target may not be in front of the opaque mirror overlay." };
                        row["head_forward_world"] = A(eye.transform.forward);
                        row["projection"] = new { pixels = projection.Pixels, area_pixels = projection.Area, retained = projection.Retained,
                            all_in_front = projection.AllInFront, outline_inside = projection.OutlineInside };
                        var stages = new List<object>(); row["stages"] = stages;

                        // Freeze the optical UV mapping independently of the colour targets.
                        // Glass refraction/post processing can move the visible mirror image
                        // away from the un-refracted triangle projection. No target fitting.
                        var opticalMap = await BusVehicleMirrorUvCalibration.Calibrate(item.Material,
                            item.Material.shader.name == "HDRP/Unlit" ? "_UnlitColorMap" : "_BaseColorMap",
                            item.Texture, target, projection.Mask, async () => { await Settle(pump, () => eyeRenders, item); },
                            directory, Prefix(lod, i, item.Name, "uv"));
                        row["optical_uv_calibration"] = opticalMap.Report;
                        Require(opticalMap.Passed, "Independent optical UV calibration did not produce enough reliable texels.");
                        var luminanceEvidence = new Dictionary<string, object>();
                        row["luminance_calibration"] = luminanceEvidence;
                        luminanceEvidence["result"] = await CalibrateLuminance(item, async () => { await Settle(pump, () => eyeRenders, item); },
                            luminanceEvidence, directory, Prefix(lod, i, item.Name, "neutral"), files);

                        stages.Add(await Settle(pump, () => eyeRenders, item));
                        Color32[] offEye = Read(target), offRt = Read(item.Texture);
                        Save(directory, Prefix(lod, i, item.Name, "off-before-eye"), target, offEye, files);
                        Save(directory, Prefix(lod, i, item.Name, "off-before-reflection"), item.Texture, offRt, files);

                        item.Board.SetActive(true);
                        stages.Add(await Settle(pump, () => eyeRenders, item));
                        Color32[] onEye = Read(target), onRt = Read(item.Texture);
                        Save(directory, Prefix(lod, i, item.Name, "on-eye"), target, onEye, files);
                        Save(directory, Prefix(lod, i, item.Name, "on-reflection"), item.Texture, onRt, files);
                        var reflectionHd = HDCamera.GetOrCreate(item.Camera);
                        var reflectionExposure = reflectionHd.volumeStack.GetComponent<Exposure>();
                        var eyeHd = HDCamera.GetOrCreate(eye);
                        var eyeExposure = eyeHd.volumeStack.GetComponent<Exposure>();
                        row["exposure_evidence"] = new {
                            reflection_mode = reflectionExposure.mode.value.ToString(),
                            reflection_fixed_ev100 = reflectionExposure.fixedExposure.value,
                            reflection_compensation = reflectionExposure.compensation.value,
                            reflection_exposure_control = reflectionHd.frameSettings.IsEnabled(FrameSettingsField.ExposureControl),
                            reflection_postprocess = reflectionHd.frameSettings.IsEnabled(FrameSettingsField.Postprocess),
                            reflection_tonemapping_enabled = reflectionHd.frameSettings.IsEnabled(FrameSettingsField.Tonemapping),
                            reflection_tonemapping = reflectionHd.volumeStack.GetComponent<Tonemapping>().mode.value.ToString(),
                            reflection_colour_grading = reflectionHd.frameSettings.IsEnabled(FrameSettingsField.ColorGrading),
                            reflection_post_exposure = reflectionHd.volumeStack.GetComponent<ColorAdjustments>().postExposure.value,
                            reflection_saturation = reflectionHd.volumeStack.GetComponent<ColorAdjustments>().saturation.value,
                            eye_mode = eyeExposure.mode.value.ToString(), eye_fixed_ev100 = eyeExposure.fixedExposure.value,
                            eye_compensation = eyeExposure.compensation.value,
                            eye_exposure_control = eyeHd.frameSettings.IsEnabled(FrameSettingsField.ExposureControl),
                            eye_postprocess = eyeHd.frameSettings.IsEnabled(FrameSettingsField.Postprocess),
                            eye_tonemapping_enabled = eyeHd.frameSettings.IsEnabled(FrameSettingsField.Tonemapping),
                            eye_tonemapping = eyeHd.volumeStack.GetComponent<Tonemapping>().mode.value.ToString(),
                            eye_colour_grading = eyeHd.frameSettings.IsEnabled(FrameSettingsField.ColorGrading),
                            eye_post_exposure = eyeHd.volumeStack.GetComponent<ColorAdjustments>().postExposure.value,
                            eye_saturation = eyeHd.volumeStack.GetComponent<ColorAdjustments>().saturation.value,
                            eye_refraction = eyeHd.frameSettings.IsEnabled(FrameSettingsField.Refraction),
                            eye_lens_distortion = eyeHd.volumeStack.GetComponent<LensDistortion>().intensity.value,
                            eye_panini_distance = eyeHd.volumeStack.GetComponent<PaniniProjection>().distance.value,
                            eye_projection = Enumerable.Range(0, 16).Select(n => eye.projectionMatrix[n]).ToArray(),
                            eye_nonjittered_projection = Enumerable.Range(0, 16).Select(n => eye.nonJitteredProjectionMatrix[n]).ToArray()
                        };
                        row["target_materials"] = item.Board.GetComponent<Renderer>().sharedMaterials.Select(material => {
                            Color colour = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.GetColor("_UnlitColor");
                            Color emission = material.GetColor("_EmissiveColor");
                            return new { material.name, shader = material.shader.name,
                                base_rgba = new[] { colour.r, colour.g, colour.b, colour.a },
                                emission_rgba = new[] { emission.r, emission.g, emission.b, emission.a },
                                map = material.HasProperty("_BaseColorMap") ? material.GetTexture("_BaseColorMap")?.name : material.GetTexture("_UnlitColorMap")?.name,
                                emissive_exposure_weight = material.GetFloat("_EmissiveExposureWeight"),
                                keywords = material.shaderKeywords };
                        }).ToArray();
                        float directionError = Vector3.Angle(item.Camera.transform.forward, item.Reflected);
                        float positionError = Vector3.Distance(item.Camera.transform.position, item.CameraPosition);
                        row["camera"] = new { world_position_m = A(item.Camera.transform.position), forward_world = A(item.Camera.transform.forward),
                            direction_error_degrees = directionError, position_error_m = positionError,
                            item.Camera.fieldOfView, item.Camera.aspect, texture_width = item.Texture.width, texture_height = item.Texture.height };

                        item.Board.SetActive(false);
                        stages.Add(await Settle(pump, () => eyeRenders, item));
                        Color32[] returnEye = Read(target), returnRt = Read(item.Texture);
                        Save(directory, Prefix(lod, i, item.Name, "off-after-eye"), target, returnEye, files);
                        Save(directory, Prefix(lod, i, item.Name, "off-after-reflection"), item.Texture, returnRt, files);

                        string map = item.Material.shader.name == "HDRP/Unlit" ? "_UnlitColorMap" : "_BaseColorMap";
                        Vector2 scale = item.Material.GetTextureScale(map), offset = item.Material.GetTextureOffset(map);
                        bool flipped = (scale - new Vector2(-1f, 1f)).sqrMagnitude < 1e-8f && (offset - new Vector2(1f, 0f)).sqrMagnitude < 1e-8f;
                        bool opaque = item.Material.HasProperty("_SurfaceType") && item.Material.GetFloat("_SurfaceType") == 0f;
                        row["material"] = new { shader = item.Material.shader.name, map, scale = new[] { scale.x, scale.y },
                            offset = new[] { offset.x, offset.y }, rt_bound = item.Material.GetTexture(map) == item.Texture, production_u_flip = flipped, opaque };
                        var naiveEvaluation = Evaluate(item, i, projection, offEye, onEye, returnEye, offRt, onRt, returnRt, scale, offset);
                        row["unrefracted_projection_comparison"] = naiveEvaluation;
                        var evaluation = Evaluate(item, i, projection, offEye, onEye, returnEye, offRt, onRt, returnRt, scale, offset, opticalMap);
                        row["image_checks"] = evaluation;
                        bool geometry = projection.AllInFront && projection.OutlineInside && projection.Pixels >= 100 && projection.Retained >= .95f &&
                            Vector3.Dot(item.Reflected, item.Normal) > .05f && directionError < .1f && positionError < .003f;
                        bool passed = geometry && targetSetup && opaque && flipped && item.Material.GetTexture(map) == item.Texture && (bool)evaluation["passed"];
                        row["geometry_passed"] = geometry;
                        row["status"] = !targetSetup ? "TARGET_SETUP_BLOCKED" : passed ? "automatic checks passed; manual image review required" : "failed";
                        if (!passed) row["failure_scope"] = "Target setup or reflection/display rendering needs diagnosis; absent target pixels alone do not establish a vehicle geometry defect.";
                        if (!(bool)naiveEvaluation["passed"])
                            row["refraction_control"] = await DiagnoseRefraction(item, i, eye, target, pump,
                                () => eyeRenders, directory, projection, offEye, offRt, scale, offset);
                        if (!passed && i == 0)
                            row["target_diagnostics"] = await DiagnoseTarget(item, eye, target, pump, () => eyeRenders,
                                directory, files, offEye, offRt, projection, scale, offset);
                    }
                    catch (Exception ex) { row["status"] = "failed"; row["error"] = ex.ToString(); }
                    finally { item.Board.SetActive(false); }
                }
                report["automatic_passed"] = rows.Count == mirrorCount && rows.All(r => (string)r["status"] == "automatic checks passed; manual image review required");
                report["status"] = (bool)report["automatic_passed"] ? "automatic checks passed; manual image review required" : "failed";
            }
            catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
            finally
            {
                var cleanupErrors = new List<string>();
                try { if (pump != null) pump.Running = false; } catch (Exception ex) { cleanupErrors.Add("stop mirror pump: " + ex); }
                try { if (eye != null) eye.enabled = false; } catch (Exception ex) { cleanupErrors.Add("disable mirror eye: " + ex); }
                try { mirrors.Update(null, false); } catch (Exception ex) { cleanupErrors.Add("disable production mirrors: " + ex); }
                for (int i = cleanup.Count - 1; i >= 0; i--) try { cleanup[i](); } catch (Exception ex) { cleanupErrors.Add(ex.ToString()); }
                if (cleanupErrors.Count > 0) { report["cleanup_errors"] = cleanupErrors; report["status"] = "failed"; report["automatic_passed"] = false; }
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    File.WriteAllText(Path.Combine(directory, "mirror-probe-lod" + lod + ".json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            return report;
        }

        // Causal control only. Original glass, mirror geometry and materials stay present.
        // Refraction is restored before leaving; these images cannot pass the final checks.
        static async Task<object> DiagnoseRefraction(MirrorInfo item, int index, Camera eye, RenderTexture target,
            BusVehicleMirrorProbePump pump, Func<int> eyeCount, string directory, Projection projection,
            Color32[] offEye, Color32[] offRt, Vector2 scale, Vector2 offset)
        {
            var hd = eye.GetComponent<HDAdditionalCameraData>();
            var savedSettings = hd.renderingPathCustomFrameSettings;
            var savedMask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var evidenceFiles = new List<object>(); var rows = new List<object>();
            string path = Path.Combine(directory, "refraction-control"); Directory.CreateDirectory(path);
            try
            {
                item.Board.SetActive(true);
                foreach (bool enabled in new[] { true, false })
                {
                    var settings = savedSettings; var mask = savedMask;
                    settings.SetEnabled(FrameSettingsField.Refraction, enabled);
                    mask.mask[(uint)FrameSettingsField.Refraction] = true;
                    hd.renderingPathCustomFrameSettings = settings;
                    hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                    object settled = await Settle(pump, eyeCount, item);
                    bool actual = HDCamera.GetOrCreate(eye).frameSettings.IsEnabled(FrameSettingsField.Refraction);
                    Require(actual == enabled, "Refraction control frame setting did not apply.");
                    Color32[] pixels = Read(target), reflected = Read(item.Texture);
                    string name = "mirror" + index + (enabled ? "-refraction-on" : "-refraction-off");
                    Save(path, name + "-eye", target, pixels, evidenceFiles);
                    Save(path, name + "-reflection", item.Texture, reflected, evidenceFiles);
                    rows.Add(new { requested = enabled, actual, settled,
                        comparison = Evaluate(item, index, projection, offEye, pixels, offEye, offRt, reflected, offRt, scale, offset) });
                }
            }
            finally
            {
                hd.renderingPathCustomFrameSettings = savedSettings;
                hd.renderingPathCustomFrameSettingsOverrideMask = savedMask;
                item.Board.SetActive(false);
                await Settle(pump, eyeCount, item);
            }
            return new { diagnostic_only = true, scope = "Only eye Refraction frame setting changed; reused off captures are diagnostic baselines, not a fresh acceptance cycle.",
                restored_actual_refraction = HDCamera.GetOrCreate(eye).frameSettings.IsEnabled(FrameSettingsField.Refraction),
                rows, files = evidenceFiles };
        }

        // Failure-only controls on the disposable fixture. Never touch the bus,
        // mirror cameras, exposure, acceptance thresholds or production materials.
        static async Task<object> DiagnoseTarget(MirrorInfo item, Camera eye, RenderTexture target,
            BusVehicleMirrorProbePump pump, Func<int> eyeCount, string directory, List<object> files,
            Color32[] offEye, Color32[] offRt, Projection projection, Vector2 scale, Vector2 offset)
        {
            var rows = new List<object>();
            var temporaryObjects = new List<GameObject>();
            var temporaryMeshes = new List<Mesh>();
            var temporaryMaterials = new List<Material>();
            Mesh mesh = item.Board.GetComponent<MeshFilter>().sharedMesh;
            Renderer renderer = item.Board.GetComponent<Renderer>();
            int[] backing = mesh.GetTriangles(0);
            Material[] originals = renderer.sharedMaterials;
            try
            {
                item.Board.SetActive(true);
                mesh.SetTriangles(Array.Empty<int>(), 0);
                await Capture("no-backing");
                mesh.SetTriangles(backing, 0);

                renderer.enabled = false;
                for (int p = 1; p < 5; p++)
                {
                    var child = new GameObject("CDLC diagnostic standalone tile " + p) {
                        hideFlags = HideFlags.DontSave, layer = item.Board.layer
                    };
                    temporaryObjects.Add(child); child.transform.SetParent(item.Board.transform, false);
                    var tile = new Mesh { hideFlags = HideFlags.DontSave };
                    temporaryMeshes.Add(tile); tile.vertices = mesh.vertices; tile.uv = mesh.uv;
                    tile.triangles = mesh.GetTriangles(p); tile.RecalculateNormals(); tile.RecalculateBounds();
                    child.AddComponent<MeshFilter>().sharedMesh = tile;
                    var r = child.AddComponent<MeshRenderer>(); r.sharedMaterial = originals[p];
                    r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                }
                await Capture("split-original-no-backing");

                Shader shader = Shader.Find("HDRP/Lit");
                Require(shader != null, "Lit fixture-control shader unavailable.");
                for (int p = 0; p < 4; p++)
                {
                    Color c = Colours[p];
                    var material = new Material(shader) { hideFlags = HideFlags.DontSave };
                    temporaryMaterials.Add(material);
                    material.SetColor("_BaseColor", c); material.SetFloat("_Metallic", 0f);
                    material.SetFloat("_Smoothness", .2f);
                    HDMaterial.ValidateMaterial(material);
                    material.SetVector("_EmissiveColor", new Vector4(c.r * 3000f, c.g * 3000f, c.b * 3000f, 1f));
                    temporaryObjects[p].GetComponent<Renderer>().sharedMaterial = material;
                }
                await Capture("split-lit-3000-no-backing");
            }
            finally
            {
                item.Board.SetActive(false); renderer.enabled = true; mesh.SetTriangles(backing, 0);
                foreach (var obj in temporaryObjects) { obj.SetActive(false); UnityEngine.Object.Destroy(obj); }
                foreach (var tile in temporaryMeshes) UnityEngine.Object.Destroy(tile);
                foreach (var material in temporaryMaterials) UnityEngine.Object.Destroy(material);
            }
            return rows;

            async Task Capture(string label)
            {
                object settled = await Settle(pump, eyeCount, item);
                Color32[] currentEye = Read(target), currentRt = Read(item.Texture);
                Save(directory, "diagnostic-" + label + "-eye", target, currentEye, files);
                Save(directory, "diagnostic-" + label + "-reflection", item.Texture, currentRt, files);
                rows.Add(new { label, settled, diagnostic_only = true,
                    note = "Both comparison baselines reuse the original off capture; this is diagnosis, not an independent off/on/off acceptance cycle.",
                    comparison = Evaluate(item, 0, projection, offEye, currentEye, offEye, offRt, currentRt, offRt, scale, offset) });
            }
        }

        static GameObject MakeBoard(int index, MirrorInfo item, Transform root, MirrorSurface surface, List<Action> cleanup)
        {
            Shader shader = Shader.Find("HDRP/Lit");
            Require(shader != null, "HDRP/Lit is required for calibrated target identities.");
            var board = new GameObject("CDLC mirror " + index + " independent colour target") { hideFlags = HideFlags.DontSave, layer = root.gameObject.layer };
            board.SetActive(false);
            cleanup.Add(() => UnityEngine.Object.Destroy(board));
            Vector3 up = Vector3.ProjectOnPlane(root.TransformDirection(V(surface.Up)), item.Reflected).normalized;
            Quaternion rotation = Quaternion.LookRotation(item.Reflected, up);
            float frustumHalfHeight = TargetDistance * Mathf.Tan(item.Camera.fieldOfView * Mathf.Deg2Rad * .5f);
            float frustumHalfWidth = frustumHalfHeight * item.Camera.aspect;
            Vector3 cameraRight = rotation * Vector3.right;
            Vector3 outboard = root.right * Mathf.Sign(root.InverseTransformPoint(item.Centre).x);
            float outboardDot = Vector3.Dot(cameraRight, outboard);
            Require(Mathf.Abs(outboardDot) > .2f, "Mirror camera has no clear vehicle-outboard horizontal direction.");
            // The normal mirror view legitimately includes the side of the bus. Put
            // this controlled target in the outboard half instead of requiring a
            // board behind the bus body to appear through opaque vehicle geometry.
            Vector3 shift = cameraRight * (Mathf.Sign(outboardDot) * frustumHalfWidth * .52f);
            board.transform.SetPositionAndRotation(item.CameraPosition + item.Reflected * TargetDistance + shift, rotation);
            float halfHeight = frustumHalfHeight * .70f;
            float halfWidth = frustumHalfWidth * .36f;
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var groups = new List<int[]>();
            var materials = new Material[5]; item.TileCentres = new Vector3[4];
            item.TileMaterials = new Material[4]; item.TileColours = new Color[4];
            for (int p = 0; p < 5; p++)
            {
                Vector2 centre = p == 0 ? Vector2.zero : TileOffsets[p - 1];
                // Uniform legibility target for every declared mirror: the previous
                // tiny patches left the convex mirror's independently decoded
                // texels without blue samples. Keep centres and all acceptance
                // thresholds fixed; enlarge separated patches on this test board.
                float size = p == 0 ? 1f : TargetTileHalfSize;
                // Colour tiles lie 2 mm in front of a dark board, without a shared coplanar face.
                float z = p == 0 ? 0f : -.002f;
                int n = verts.Count;
                verts.Add(new Vector3((centre.x * 2f - size) * halfWidth, (centre.y * 2f - size) * halfHeight, z));
                verts.Add(new Vector3((centre.x * 2f + size) * halfWidth, (centre.y * 2f - size) * halfHeight, z));
                verts.Add(new Vector3((centre.x * 2f + size) * halfWidth, (centre.y * 2f + size) * halfHeight, z));
                verts.Add(new Vector3((centre.x * 2f - size) * halfWidth, (centre.y * 2f + size) * halfHeight, z));
                uvs.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up });
                groups.Add(new[] { n, n + 2, n + 1, n, n + 3, n + 2 });
                var material = new Material(shader) { name = "CDLC mirror target " + index + " tile " + p, hideFlags = HideFlags.DontSave };
                cleanup.Add(() => UnityEngine.Object.Destroy(material));
                // The old 15000-nit, coloured-albedo target shifted green toward
                // yellow in the actual mirror RT. Isolate colour from diffuse
                // illumination and keep emission below that saturated range.
                Color colour = p == 0 ? new Color(.015f, .015f, .015f) : Colours[(p - 1 + index) % 4];
                material.SetColor("_BaseColor", p == 0 ? colour : Color.black);
                material.SetFloat("_Metallic", 0f);
                material.SetFloat("_Smoothness", .2f);
                HDMaterial.ValidateMaterial(material);
                // Tile emission is set by CalibrateLuminance before any colour capture.
                material.SetVector("_EmissiveColor", new Vector4(0f, 0f, 0f, 1f));
                materials[p] = material;
                if (p > 0) { item.TileMaterials[p - 1] = material; item.TileColours[p - 1] = colour; }
                if (p > 0) item.TileCentres[p - 1] = board.transform.TransformPoint(new Vector3(centre.x * 2f * halfWidth, centre.y * 2f * halfHeight, z));
            }
            var mesh = new Mesh { name = "CDLC independent mirror target " + index, hideFlags = HideFlags.DontSave };
            cleanup.Add(() => UnityEngine.Object.Destroy(mesh));
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.subMeshCount = groups.Count;
            for (int p = 0; p < groups.Count; p++) mesh.SetTriangles(groups[p], p);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            board.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = board.AddComponent<MeshRenderer>(); renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return board;
        }

        // Only the board's tile emission changes; cameras, exposure, production materials and acceptance thresholds do not.
        static async Task<object> CalibrateLuminance(MirrorInfo item, Func<Task> settle,
            IDictionary<string, object> evidence, string directory, string prefix, List<object> files)
        {
            var attempts = new List<object>();
            evidence["attempts"] = attempts;
            float white = CalibrationNits, multiplier = 0f, colourNits;
            string decision = null;
            var nits = new float[4];
            float[] before, after;
            int passes = 0;
            try
            {
                item.Board.SetActive(true);
                for (int attempt = 0; attempt < CalibrationAttempts; attempt++)
                {
                    foreach (Material material in item.TileMaterials) material.SetVector("_EmissiveColor", new Vector4(white, white, white, 1f));
                    await settle();
                    Color32[] pixels = Read(item.Texture);
                    Save(directory, prefix + "-" + attempt, item.Texture, pixels, files);
                    float[] linear = TileLinear(item, pixels);
                    decision = LuminanceDecision(linear, out float median);
                    attempts.Add(new { emission_nits = white, tile_linear_mean = linear, median_linear = median, decision });
                    if (decision == "accept") { multiplier = median / white; break; }
                    if (decision == "inconsistent") break;
                    white *= decision == "brighter" ? 8f : 1f / 8f;
                }
                Require(multiplier > 0f, "Neutral mirror target luminance calibration failed (" + decision + ").");
                colourNits = TargetLinear / multiplier;
                for (int tile = 0; tile < 4; tile++) { nits[tile] = colourNits; SetTile(item, tile, colourNits); }
                await settle();
                before = after = TileLinear(item, Read(item.Texture), true);
                for (int pass = 0; pass < ColourPasses && after.Any(v => Math.Abs(v - TargetLinear) > ColourTolerance); pass++)
                {
                    for (int tile = 0; tile < 4; tile++)
                    {
                        nits[tile] *= ColourScale(after[tile]);
                        SetTile(item, tile, nits[tile]);
                    }
                    await settle();
                    after = TileLinear(item, Read(item.Texture), true);
                    passes++;
                }
            }
            finally { item.Board.SetActive(false); }
            return new { attempts, exposure_multiplier = multiplier, implied_ev100 = Mathf.Log(1f / (1.2f * multiplier), 2f),
                white_derived_colour_nits = colourNits, colour_channel_linear_before = before, colour_emission_nits = nits,
                colour_channel_linear_after = after, colour_rescale_passes = passes, target_linear_rt = TargetLinear,
                method = "White emission on all four tiles gives the exposure; each colour tile is then rescaled (bounded passes) from its own " +
                    "channel level in the RT (red r, green g, blue b, yellow min(r,g)); 5x5 sRGB-decoded samples at each tile centre. " +
                    "Runs before the off/on/off colour captures; image thresholds unchanged." };
        }

        static void SetTile(MirrorInfo item, int tile, float nits)
        {
            Color c = item.TileColours[tile];
            item.TileMaterials[tile].SetVector("_EmissiveColor", new Vector4(c.r * nits, c.g * nits, c.b * nits, 1f));
        }

        // One rescale towards TargetLinear, bounded so an unreadable tile cannot drive its emission to an extreme.
        internal static float ColourScale(float measured) =>
            measured <= 0f ? 4f : Mathf.Clamp(TargetLinear / measured, .25f, 4f);

        // ownChannels: the tile colour's own channels (yellow: the weaker of r and g); otherwise the mean of r, g and b.
        static float[] TileLinear(MirrorInfo item, Color32[] rt, bool ownChannels = false)
        {
            var result = new float[4];
            for (int tile = 0; tile < 4; tile++)
            {
                Vector3 viewport = item.Camera.WorldToViewportPoint(item.TileCentres[tile]);
                int cx = (int)(viewport.x * item.Texture.width), cy = (int)(viewport.y * item.Texture.height);
                Color c = item.TileColours[tile];
                float sum = 0f; int samples = 0;
                for (int y = Math.Max(0, cy - 2); y <= Math.Min(item.Texture.height - 1, cy + 2); y++)
                    for (int x = Math.Max(0, cx - 2); x <= Math.Min(item.Texture.width - 1, cx + 2); x++)
                    {
                        Color32 p = rt[y * item.Texture.width + x];
                        float r = Mathf.GammaToLinearSpace(p.r / 255f), g = Mathf.GammaToLinearSpace(p.g / 255f), b = Mathf.GammaToLinearSpace(p.b / 255f);
                        sum += ownChannels ? ChannelLevel(c, r, g, b) : (r + g + b) / 3f;
                        samples++;
                    }
                result[tile] = viewport.z > 0f && samples > 0 ? sum / samples : 0f;
            }
            return result;
        }

        internal static float ChannelLevel(Color colour, float r, float g, float b)
        {
            float level = float.MaxValue;
            if (colour.r > .5f) level = Math.Min(level, r);
            if (colour.g > .5f) level = Math.Min(level, g);
            if (colour.b > .5f) level = Math.Min(level, b);
            return level == float.MaxValue ? 0f : level;
        }

        // accept: the median and at least three tiles are readable; brighter/dimmer: rescale the white emission;
        // inconsistent: the median is readable but the tiles disagree (a tile is occluded or off the RT).
        internal static string LuminanceDecision(float[] linear, out float median)
        {
            float[] sorted = linear.OrderBy(v => v).ToArray();
            median = (sorted[1] + sorted[2]) * .5f;
            if (median > CalibrationHigh) return "dimmer";
            if (median < CalibrationLow) return "brighter";
            if (linear.Count(v => v >= CalibrationLow && v <= CalibrationHigh) >= 3) return "accept";
            // Two tiles can straddle one boundary while their median is just inside it.
            // Retry exposure in the available direction; acceptance still needs three readable tiles.
            if (linear.All(v => v <= CalibrationHigh)) return "brighter";
            if (linear.All(v => v >= CalibrationLow)) return "dimmer";
            return "inconsistent";
        }

        static async Task<object> Settle(BusVehicleMirrorProbePump pump, Func<int> eyeCount, MirrorInfo item)
        {
            int frame = Time.frameCount, updates = pump.Updates, e = eyeCount(), m = item == null ? 0 : item.Renders;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (Time.frameCount - frame < MinFrames || pump.Updates - updates < MinFrames || eyeCount() - e < MinRenders || (item != null && item.Renders - m < MinRenders))
            {
                CheckMainMenu();
                if (pump.Error != null) throw new InvalidOperationException("Production mirror frame pump failed.", pump.Error);
                Require(watch.Elapsed.TotalSeconds < 20, "Timed out waiting for actual enabled-camera Unity renders; no synchronous Camera.Render fallback is allowed.");
                await Task.Delay(20);
            }
            return new { unity_frames = Time.frameCount - frame, late_updates = pump.Updates - updates, eye_renders = eyeCount() - e,
                reflection_renders = item == null ? -1 : item.Renders - m, elapsed_seconds = watch.Elapsed.TotalSeconds };
        }

        static Projection Project(MeshFilter glass, Camera eye, bool rejectBackFaces = true)
        {
            var result = new Projection(); Mesh mesh = glass.sharedMesh;
            Vector3[] vertices = mesh.vertices; Vector2[] uv = mesh.uv; int[] triangles = mesh.triangles;
            var world = vertices.Select(glass.transform.TransformPoint).ToArray();
            var screen = world.Select(eye.WorldToViewportPoint).ToArray();
            foreach (Vector3 p in screen)
            {
                result.AllInFront &= p.z > eye.nearClipPlane;
                result.OutlineInside &= p.z > eye.nearClipPlane && p.x >= .01f && p.x <= .99f && p.y >= .01f && p.y <= .99f;
            }
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];
                if (screen[ia].z <= eye.nearClipPlane || screen[ib].z <= eye.nearClipPlane || screen[ic].z <= eye.nearClipPlane)
                {
                    if (screen[ia].z > eye.nearClipPlane || screen[ib].z > eye.nearClipPlane || screen[ic].z > eye.nearClipPlane) result.NearPlaneCrossings++;
                    continue;
                }
                if (rejectBackFaces && Vector3.Dot(Vector3.Cross(world[ib] - world[ia], world[ic] - world[ia]), eye.transform.position - world[ia]) <= 0f) continue;
                Vector2 a = new Vector2(screen[ia].x * Width, screen[ia].y * Height), b = new Vector2(screen[ib].x * Width, screen[ib].y * Height), c = new Vector2(screen[ic].x * Width, screen[ic].y * Height);
                float denominator = Cross(b - a, c - a);
                if (Mathf.Abs(denominator) < 1e-5f) continue;
                result.Area += Mathf.Abs(denominator) * .5f;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)))), x1 = Mathf.Min(Width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)))), y1 = Mathf.Min(Height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                {
                    Vector2 q = new Vector2(x + .5f, y + .5f);
                    float wb = Cross(q - a, c - a) / denominator, wc = Cross(b - a, q - a) / denominator, wa = 1f - wb - wc;
                    if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f) continue;
                    float za = wa / screen[ia].z, zb = wb / screen[ib].z, zc = wc / screen[ic].z;
                    int n = y * Width + x;
                    float depth = 1f / (za + zb + zc);
                    if (result.Mask[n] && depth >= result.Depth[n]) continue;
                    if (!result.Mask[n]) { result.Mask[n] = true; result.Pixels++; }
                    result.Depth[n] = depth;
                    result.Uv[n] = (uv[ia] * za + uv[ib] * zb + uv[ic] * zc) / (za + zb + zc);
                }
            }
            result.Retained = result.Area > 0 ? Mathf.Min(1f, result.Pixels / result.Area) : 0f;
            return result;
        }

        static Dictionary<string, object> Evaluate(MirrorInfo item, int index, Projection projection,
            Color32[] offEye, Color32[] onEye, Color32[] returnEye, Color32[] offRt, Color32[] onRt, Color32[] returnRt, Vector2 scale, Vector2 offset,
            BusVehicleMirrorUvCalibration.Map opticalMap = null)
        {
            int[] rtCounts = new int[4], eyeCounts = new int[4], expectedCounts = new int[4];
            int changedRt = 0, changedEye = 0, matched = 0, compared = 0;
            for (int p = 0; p < onRt.Length; p++)
            {
                int colour = Classify(onRt[p]);
                if (Delta(offRt[p], onRt[p]) < 35 || Delta(returnRt[p], onRt[p]) < 35) continue;
                changedRt++; if (colour >= 0) rtCounts[colour]++;
            }
            for (int p = 0; p < projection.Mask.Length; p++)
            {
                int r;
                if (opticalMap != null)
                {
                    if (!opticalMap.ValidMask[p]) continue;
                    r = opticalMap.TexelIndex[p];
                }
                else
                {
                    if (!projection.Mask[p]) continue;
                    Vector2 uv = Vector2.Scale(projection.Uv[p], scale) + offset;
                    if (uv.x < .035f || uv.x > .965f || uv.y < .035f || uv.y > .965f) continue;
                    r = Mathf.Clamp((int)(uv.y * item.Texture.height), 0, item.Texture.height - 1) * item.Texture.width + Mathf.Clamp((int)(uv.x * item.Texture.width), 0, item.Texture.width - 1);
                }
                int expected = Classify(onRt[r]);
                bool targetPixel = expected >= 0 && Delta(offRt[r], onRt[r]) >= 35 && Delta(returnRt[r], onRt[r]) >= 35;
                bool eyeChanged = Delta(offEye[p], onEye[p]) >= 35 && Delta(returnEye[p], onEye[p]) >= 35;
                if (eyeChanged) changedEye++;
                if (!targetPixel) continue;
                compared++; expectedCounts[expected]++;
                if (eyeChanged && Classify(onEye[p]) == expected) { matched++; eyeCounts[expected]++; }
            }
            var tileRows = new List<object>(); bool positions = true;
            for (int tile = 0; tile < 4; tile++)
            {
                int colour = (tile + index) % 4;
                Vector3 viewport = item.Camera.WorldToViewportPoint(item.TileCentres[tile]);
                int hits = 0, samples = 0;
                var minimum = new[] { 255, 255, 255 };
                var maximum = new[] { 0, 0, 0 };
                var sum = new int[3];
                int cx = (int)(viewport.x * item.Texture.width), cy = (int)(viewport.y * item.Texture.height);
                for (int y = Math.Max(0, cy - 3); y <= Math.Min(item.Texture.height - 1, cy + 3); y++)
                    for (int x = Math.Max(0, cx - 3); x <= Math.Min(item.Texture.width - 1, cx + 3); x++)
                    {
                        int p = y * item.Texture.width + x;
                        Color32 pixel = onRt[p];
                        minimum[0] = Math.Min(minimum[0], pixel.r); minimum[1] = Math.Min(minimum[1], pixel.g); minimum[2] = Math.Min(minimum[2], pixel.b);
                        maximum[0] = Math.Max(maximum[0], pixel.r); maximum[1] = Math.Max(maximum[1], pixel.g); maximum[2] = Math.Max(maximum[2], pixel.b);
                        sum[0] += pixel.r; sum[1] += pixel.g; sum[2] += pixel.b; samples++;
                        if (Classify(onRt[p]) == colour && Delta(offRt[p], onRt[p]) >= 35 && Delta(returnRt[p], onRt[p]) >= 35) hits++;
                    }
                bool passed = viewport.z > 0f && viewport.x > .02f && viewport.x < .98f && viewport.y > .02f && viewport.y < .98f && hits >= 9;
                positions &= passed;
                tileRows.Add(new { tile, colour_id = colour, centre_world_m = A(item.TileCentres[tile]), expected_viewport = A(viewport),
                    target_identity_hits_at_expected_position = hits, sample_count = samples, minimum_rgb = minimum, maximum_rgb = maximum,
                    mean_rgb = sum.Select(n => samples == 0 ? 0f : n / (float)samples).ToArray(), passed });
            }
            float agreement = compared == 0 ? 0f : matched / (float)compared;
            int rtMinimum = Math.Max(12, onRt.Length / 200);
            bool passedAll = positions && rtCounts.All(n => n >= rtMinimum) && eyeCounts.All(n => n >= 3) && compared >= 24 &&
                changedEye >= Math.Max(12, projection.Pixels / 50) && agreement >= .80f;
            return new Dictionary<string, object> { ["passed"] = passedAll, ["colour_ids"] = new[] { "red", "green", "blue", "yellow" },
                ["rt_target_changed_pixels"] = changedRt, ["eye_target_changed_pixels_inside_mirror"] = changedEye,
                ["rt_changed_colour_counts"] = rtCounts, ["eye_matched_colour_counts"] = eyeCounts, ["uv_expected_colour_counts"] = expectedCounts,
                ["uv_compared_pixels"] = compared, ["uv_matched_pixels"] = matched, ["uv_colour_agreement"] = agreement,
                ["tiles_at_expected_positions"] = tileRows,
                ["uv_mapping"] = opticalMap == null ? "unrefracted triangle projection" : "independent pre-target complementary Gray-code optical calibration through complete glass",
                ["interpretation"] = "All four independent target colours must appear at expected reflection-camera positions, then match the production mirror texture coordinates in the complete driver-eye image; off/on/off temporal coincidence alone is insufficient." };
        }

        static int Classify(Color32 p)
        {
            if (p.r > 75 && p.r > p.g * 1.6f && p.r > p.b * 1.6f) return 0;
            if (p.g > 75 && p.g > p.r * 1.6f && p.g > p.b * 1.6f) return 1;
            if (p.b > 75 && p.b > p.r * 1.6f && p.b > p.g * 1.6f) return 2;
            if (p.r > 75 && p.g > 75 && Math.Min(p.r, p.g) > p.b * 1.6f && Math.Max(p.r, p.g) < Math.Min(p.r, p.g) * 1.7f) return 3;
            return -1;
        }
        static int Delta(Color32 a, Color32 b) => Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
        static Color32[] Read(RenderTexture target) => BusGlassProbe.Read(target);
        static void Save(string directory, string name, RenderTexture target, List<object> files) => Save(directory, name, target, Read(target), files);
        static void Save(string directory, string name, RenderTexture target, Color32[] pixels, List<object> files)
        {
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try
            {
                image.SetPixels32(pixels); image.Apply(); byte[] png = image.EncodeToPNG();
                string filename = name + ".png"; File.WriteAllBytes(Path.Combine(directory, filename), png);
                using (var sha = SHA256.Create()) files.Add(new { filename, width = target.width, height = target.height,
                    sha256 = BitConverter.ToString(sha.ComputeHash(png)).Replace("-", "").ToLowerInvariant() });
            }
            finally { UnityEngine.Object.Destroy(image); }
        }
        static string MirrorLabel(float centreX, float driverSide, bool convex)
        {
            float side = centreX * driverSide;
            return (side > 0 ? "driver" : side < 0 ? "door" : "centre") + (convex ? "-convex" : "-main");
        }
        static string Prefix(int lod, int index, string name, string stage) => "lod" + lod + "-mirror" + index + "-" + name + "-" + stage;
        static void CheckMainMenu()
        {
            BusVehicleAssetSystem.RequireTestMenu();
            Require(GameManager.instance != null && GameManager.instance.gameMode == GameMode.MainMenu && !GameManager.instance.isGameLoading,
                "Mirror diagnostic only runs in a settled main menu; a map load or game mode transition aborts it.");
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static Vector3 V(Unity.Mathematics.float3 p) => new Vector3(p.x, p.y, p.z);
        static float[] A(Vector3 p) => new[] { p.x, p.y, p.z };
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }

    /// <summary>Explicit frame pump, never a timer that calls Camera.Render.</summary>
    internal sealed class BusVehicleMirrorProbePump : MonoBehaviour
    {
        internal CabMirrors Mirrors;
        internal Camera Eye;
        internal bool Running;
        internal int Updates;
        internal Exception Error;
        void LateUpdate()
        {
            if (!Running) return;
            try { Mirrors.Update(Eye, true); Updates++; }
            catch (Exception ex) { Error = ex; Running = false; }
        }
    }
}
