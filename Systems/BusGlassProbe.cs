using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CDLC.Asset.Rendering;
using Game.Prefabs;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Developer probe (a "probe=glass" request, names=...): the window glass of our own bus assets as BusGlass lays it over the cab
    /// model, checked on the main menu. A red box stands in the bus behind its side windows; a private camera renders the side
    /// with and without the glass (glass-probe.png: per bus, without glass, with glass, then glass without the box). The
    /// result says what was found (surfaces, shader, vertices, whether the mesh is readable) and how the box's pixels changed:
    /// unchanged = no glass drawn, black or the background = opaque, tinted red = glass.
    /// </summary>
    internal static class BusGlassProbe
    {
        internal const string kImage = "glass-probe.png";
        private const int kW = 480, kH = 300;
        private const float WitnessEv100 = 15f;

        static BusGlassWitnessGeometry ReadGeometry(Transform root, MeshFilter[] filters = null)
        {
            var parts = new List<BusGlassWitnessGeometry.Part>();
            foreach (MeshFilter filter in filters ?? root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || !mesh.isReadable) throw new InvalidOperationException("Glass witness requires readable actual pane geometry");
                Vector3[] positions = mesh.vertices;
                for (int i = 0; i < positions.Length; i++)
                    positions[i] = root.InverseTransformPoint(filter.transform.TransformPoint(positions[i]));
                var indices = new List<int>();
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) indices.AddRange(mesh.GetTriangles(submesh));
                parts.Add(new BusGlassWitnessGeometry.Part(positions, indices.ToArray()));
            }
            return new BusGlassWitnessGeometry(parts.ToArray());
        }

        internal static async Task<string> Run(Func<string, PrefabBase> findBus, IEnumerable<string> names, string outputDirectory = null)
        {
            var log = new StringBuilder();
            var cleanup = new List<Action>();
            var shots = new List<Color32[]>();
            var fixtures = new List<object>();
            string directory = outputDirectory ?? Mod.ModDirectory;
            bool ok = true;
            string result = "failed glass probe did not complete";
            try
            {
                BusVehicleAssetSystem.RequireTestMenu();
                Directory.CreateDirectory(directory);
                var targetChecks = new List<object>();
                var targets = await CheckTargets(findBus, names, targetChecks, log);
                File.WriteAllText(Path.Combine(directory, "glass-target-assets.json"), JsonConvert.SerializeObject(new {
                    scope = "Requested three-LOD assets with private NoVT material readers; unrelated global VT queues are not a readiness gate",
                    targets = targetChecks }, Formatting.Indented));

                Vector3 origin = new Vector3(0f, 3000f, 0f);
                var camObject = new GameObject("CDLC glass probe camera") { hideFlags = HideFlags.DontSave };
                cleanup.Add(() => UnityEngine.Object.Destroy(camObject));
                Camera cam = camObject.AddComponent<Camera>();
                cam.enabled = false;
                var hd = camObject.AddComponent<HDAdditionalCameraData>();
                Plain(hd);
                var cameraSettings = hd.renderingPathCustomFrameSettings;
                var cameraMask = hd.renderingPathCustomFrameSettingsOverrideMask;
                cameraSettings.SetEnabled(FrameSettingsField.ExposureControl, true);
                cameraMask.mask[(uint)FrameSettingsField.ExposureControl] = true;
                hd.renderingPathCustomFrameSettings = cameraSettings;
                hd.renderingPathCustomFrameSettingsOverrideMask = cameraMask;
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 100f;
                var rt = new RenderTexture(kW, kH, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                cleanup.Add(() => UnityEngine.Object.Destroy(rt));
                cleanup.Add(rt.Release);
                rt.Create();
                if (!rt.IsCreated()) throw new InvalidOperationException("Glass witness render target was not created");
                cam.targetTexture = rt;
                cleanup.Add(() => cam.targetTexture = null);
                var sunObject = new GameObject("CDLC glass probe sun") { hideFlags = HideFlags.DontSave };
                cleanup.Add(() => { sunObject.SetActive(false); UnityEngine.Object.Destroy(sunObject); });
                Light sun = sunObject.AddComponent<Light>();
                sun.type = UnityEngine.LightType.Directional;
                sun.shadows = LightShadows.None;
                sunObject.AddComponent<HDAdditionalLightData>().SetIntensity(30000f, LightUnit.Lux);
                sun.enabled = true;

                Shader redShader = Shader.Find("HDRP/Lit");
                if (redShader == null) throw new InvalidOperationException("HDRP/Lit probe shader is unavailable");
                Material red = new Material(redShader) { hideFlags = HideFlags.DontSave };
                cleanup.Add(() => UnityEngine.Object.Destroy(red));
                red.SetColor("_BaseColor", new Color(0.9f, 0.05f, 0.05f));
                red.SetFloat("_Metallic", 0f); red.SetFloat("_Smoothness", 0f);
                HDMaterial.ValidateMaterial(red);
                Material grey = new Material(red) { hideFlags = HideFlags.DontSave, name = "Glass witness 18 percent diffuse grey" };
                cleanup.Add(() => UnityEngine.Object.Destroy(grey));
                // SetColor decodes sRGB in this linear project; the report declares a linear 18 percent albedo.
                grey.SetColor("_BaseColor", new Color(.18f, .18f, .18f).gamma);
                HDMaterial.ValidateMaterial(grey);

                foreach (var target in targets)
                {
                    BusVehicleAssetSystem.RequireTestMenu();
                    string name = target.Name;
                    PrefabBase prefab = target.Prefab;

                    var root = new GameObject("CDLC glass probe " + name) { hideFlags = HideFlags.DontSave };
                    root.transform.position = origin;
                    cleanup.Add(() => { root.SetActive(false); UnityEngine.Object.Destroy(root); });
                    var glass = new BusGlass();
                    cleanup.Add(glass.Dispose);
                    bool built = glass.Build(prefab, root.transform, 0);
                    log.Append($"{name}: {glass.Describe}; ");
                    ok &= built;
                    if (!built) throw new InvalidOperationException("Complete source glass was not built");

                    MeshFilter[] paneFilters = root.GetComponentsInChildren<MeshFilter>(true);
                    var geometry = ReadGeometry(root.transform);
                    cam.transform.position = origin + geometry.Eye;
                    cam.transform.LookAt(origin + geometry.Target);
                    sunObject.transform.rotation = Quaternion.LookRotation((cam.transform.forward + Vector3.down).normalized, Vector3.up);
                    Matrix4x4 localToClip = cam.projectionMatrix * cam.worldToCameraMatrix * root.transform.localToWorldMatrix;
                    bool[] geometryMask = geometry.FreezeMask(kW, kH, (x, y) => {
                        Ray ray = cam.ScreenPointToRay(new Vector3(x + .5f, y + .5f, 0));
                        return new Ray(root.transform.InverseTransformPoint(ray.origin), root.transform.InverseTransformDirection(ray.direction));
                    }, out int selectedPixels, point => BusGlassWitnessGeometry.Screen(localToClip, point, kW, kH));
                    var row = new Dictionary<string, object> {
                        ["name"] = name, ["lod_rendered"] = 0,
                        ["bus_mvid"] = typeof(BusGlassProbe).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                        ["assets_mvid"] = typeof(CDLC.Asset.Rendering.GlassMaterial).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                        ["native_package"] = prefab.asset.path, ["native_package_sha256"] = PackageHash(prefab.asset.path),
                        ["scope"] = "Isolated complete imported glass witness; native mesh/texture preflight covers all three LODs. Full vehicle assembly and road lighting are separate checks.",
                        ["geometry_positions_sha256"] = geometry.PositionHash, ["geometry_indices_sha256"] = geometry.IndexHash,
                        ["geometry_triangles"] = geometry.Triangles, ["frozen_ray_mask_sha256"] = BusGlassWitnessGeometry.MaskHash(geometryMask),
                        ["geometry_pixels"] = selectedPixels, ["mask_frozen_before_colour_capture"] = true,
                        ["eye_model_m"] = Components(geometry.Eye), ["target_model_m"] = Components(geometry.Target),
                        ["fov_degrees"] = cam.fieldOfView, ["resolution"] = new[] { kW, kH }, ["configured_ev100"] = WitnessEv100,
                        ["glass_materials"] = paneFilters.Select(filter => BusVehicleMaterialDiagnostics.ShaderEvidence(filter.GetComponent<MeshRenderer>().sharedMaterial)).ToArray(),
                        ["inspection_light"] = new { lux = sunObject.GetComponent<HDAdditionalLightData>().intensity,
                            unit = sunObject.GetComponent<HDAdditionalLightData>().lightUnit.ToString(), enabled = sun.enabled,
                            active = sunObject.activeInHierarchy, rotation = Components(sunObject.transform.eulerAngles),
                            forward = Components(sunObject.transform.forward), incident_direction = Components(-sunObject.transform.forward),
                            scope = "Owned directional inspection light follows the frozen witness view downward at 45 degrees; this is not road/night lighting" }
                    };
                    fixtures.Add(row);
                    var acceptanceErrors = new List<string>(); row["acceptance_errors"] = acceptanceErrors;

                    // Both the view and the target follow the exported side-window geometry.
                    GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cleanup.Add(() => { if (box != null) { box.SetActive(false); UnityEngine.Object.Destroy(box); } });
                    box.hideFlags = HideFlags.DontSave;
                    UnityEngine.Object.Destroy(box.GetComponent<Collider>());
                    box.transform.SetParent(root.transform, false);
                    box.transform.localPosition = geometry.Target;
                    box.transform.localScale = geometry.BoxSize;
                    box.GetComponent<MeshRenderer>().sharedMaterial = red;
                    box.GetComponent<MeshRenderer>().renderingLayerMask = uint.MaxValue;
                    var card = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cleanup.Add(() => { if (card != null) { card.SetActive(false); UnityEngine.Object.Destroy(card); } });
                    card.hideFlags = HideFlags.DontSave;
                    UnityEngine.Object.Destroy(card.GetComponent<Collider>());
                    card.transform.SetParent(root.transform, false);
                    card.transform.localPosition = new Vector3(geometry.Eye.x - 4.5f, geometry.Target.y, geometry.Target.z + 2f);
                    card.transform.localScale = new Vector3(.04f, .3f, .3f);
                    card.GetComponent<MeshRenderer>().sharedMaterial = grey;
                    card.GetComponent<MeshRenderer>().renderingLayerMask = uint.MaxValue;
                    var exposure = new BusGlassOpticsExposure(); cleanup.Add(exposure.Dispose);
                    exposure.Activate(cam, WitnessEv100);
                    // HDRP retires a disabled camera's exposure history between manual renders.
                    var rendering = new Dictionary<string, object>(); row["rendering"] = rendering;
                    var cameraFrames = new BusRoadGlassCameraFrames(cam, rt, BusVehicleAssetSystem.RequireTestMenu,
                        () => cam != null && cam.targetTexture == rt, rendering);
                    cleanup.Add(cameraFrames.Dispose);
                    cameraFrames.Start();
                    var frames = new List<object>(); row["captures"] = frames;
                    var exposureReads = new List<object>(); row["exposure_reads"] = exposureReads;
                    BusGlassOpticsExposure.GpuExposure? firstExposure = null;
                    Quaternion viewRotation = cam.transform.rotation;

                    Color32[] without = null, with = null, empty = null;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        glass.Visible = pass != 0;
                        box.SetActive(pass != 2);
                        await WaitForRenderedWindow(cameraFrames, BusVehicleAssetSystem.RequireTestMenu);
                        int renderedFrame = Time.frameCount;
                        var currentGeometry = ReadGeometry(root.transform, paneFilters);
                        if (currentGeometry.PositionHash != geometry.PositionHash || currentGeometry.IndexHash != geometry.IndexHash ||
                            Vector3.Distance(cam.transform.position, origin + geometry.Eye) > .0001f ||
                            Quaternion.Angle(cam.transform.rotation, viewRotation) > .001f || Math.Abs(cam.fieldOfView - 40f) > .001f)
                            throw new InvalidOperationException("Frozen glass geometry or witness view changed during capture");
                        Color32[] shot = Read(rt);
                        string imageName = name + "-glass-pass-" + pass + ".png";
                        string imagePath = Path.Combine(directory, imageName);
                        SaveFrame(shot, imagePath);
                        var capture = new Dictionary<string, object> {
                            ["pass"] = pass, ["render_frame"] = renderedFrame, ["pixel_read_frame"] = Time.frameCount,
                            ["png"] = imageName, ["png_sha256"] = PackageHash(imagePath),
                            ["frozen_inputs_unchanged"] = true,
                            ["camera"] = new { position = Components(cam.transform.position), rotation = Components(cam.transform.eulerAngles),
                                view_matrix = Components(cam.worldToCameraMatrix), projection_matrix = Components(cam.projectionMatrix),
                                matrix_order = "Unity Matrix4x4 index order (column-major)", aspect = cam.aspect, fov_degrees = cam.fieldOfView,
                                pixel_rect = new[] { cam.pixelRect.x, cam.pixelRect.y, cam.pixelRect.width, cam.pixelRect.height },
                                culling_mask = cam.cullingMask, target_id = rt.GetInstanceID(), enabled = cam.enabled, allow_hdr = cam.allowHDR }
                        };
                        frames.Add(capture);
                        Vector3 cardPixel = cam.WorldToScreenPoint(card.transform.position + Vector3.right * .02f);
                        capture["grey_card_geometry"] = GreyRayEvidence(cam, root.transform, card, sun, (int)cardPixel.x, (int)cardPixel.y, out bool cardVisible);
                        var greySample = new Dictionary<string, object>();
                        capture["grey_card"] = greySample;
                        var exposureRead = new Dictionary<string, object> { ["pass"] = pass };
                        exposureReads.Add(exposureRead);
                        var actualExposure = await BusGlassOpticsExposure.Read(cam, BusVehicleAssetSystem.RequireTestMenu, exposureRead);
                        capture["gpu_exposure"] = actualExposure.Evidence();
                        capture["readback_frame"] = Time.frameCount;
                        var resolved = HDCamera.GetOrCreate(cam);
                        var resolvedExposure = resolved.volumeStack.GetComponent<Exposure>();
                        if (resolvedExposure.mode.value != ExposureMode.Fixed || Math.Abs(resolvedExposure.fixedExposure.value - WitnessEv100) > .001f)
                            throw new InvalidOperationException("Fixed witness exposure was not applied to the actual camera");
                        if (firstExposure.HasValue && (Math.Abs(actualExposure.Ev100 - firstExposure.Value.Ev100) >= .005f ||
                            Math.Abs(actualExposure.Multiplier / firstExposure.Value.Multiplier - 1f) >= .002f))
                            throw new InvalidOperationException("Actual GPU exposure changed between witness states");
                        firstExposure = firstExposure ?? actualExposure;
                        capture["exposure_control"] = resolved.frameSettings.IsEnabled(FrameSettingsField.ExposureControl);
                        capture["tonemapping"] = resolved.volumeStack.GetComponent<Tonemapping>().mode.value.ToString();
                        capture["post_exposure"] = resolved.volumeStack.GetComponent<ColorAdjustments>().postExposure.value;
                        capture["active_lights"] = Resources.FindObjectsOfTypeAll<HDAdditionalLightData>().Where(light => light.enabled && light.gameObject.activeInHierarchy)
                                .Select(light => new { light.name, intensity = light.intensity, unit = light.lightUnit.ToString(),
                                    type = light.GetComponent<Light>().type.ToString(), enabled = light.GetComponent<Light>().enabled,
                                    rotation = Components(light.transform.eulerAngles), forward = Components(light.transform.forward) }).ToArray();
                        try
                        {
                            GreyEvidence(shot, (int)cardPixel.x, (int)cardPixel.y, cardPixel.z, greySample);
                            if (!cardVisible) throw new InvalidOperationException("Grey reference sample rays do not all reach the actual owned card first");
                        }
                        catch (InvalidOperationException error)
                        {
                            capture["acceptance_failure"] = error.Message;
                            acceptanceErrors.Add("pass " + pass + ": " + error.Message); ok = false;
                        }
                        if (pass == 0) without = shot;
                        else if (pass == 1) with = shot;
                        else empty = shot;
                    }
                    shots.Add(without);
                    shots.Add(with);
                    shots.Add(empty);
                    bool pixelsPassed = EvaluatePixels(without, with, empty, out string pixelReport, geometryMask);
                    ok &= pixelsPassed;
                    row["pixels_passed"] = pixelsPassed; row["pixel_report"] = pixelReport;
                    log.Append($"box without glass {Describe(without)}, with glass {Describe(with)}, glass without box {Describe(empty)}; pixels {(pixelsPassed ? "PASS" : "FAIL")} {pixelReport}; ");
                    if (acceptanceErrors.Count != 0) log.Append("grey acceptance FAIL: " + string.Join("; ", acceptanceErrors) + "; ");
                    glass.Visible = false;
                    Vector3 diagnosticCardPixel = cam.WorldToScreenPoint(card.transform.position + Vector3.right * .02f);
                    Vector3 diagnosticBoxPixel = cam.WorldToScreenPoint(box.transform.position + Vector3.right * geometry.BoxSize.x * .5f);
                    var diagnosticReport = new Dictionary<string, object>(); row["input_diagnostics"] = diagnosticReport;
                    var diagnostics = new BusGlassWitnessDiagnostics(cam, card, box, (int)diagnosticCardPixel.x, (int)diagnosticCardPixel.y,
                        (int)diagnosticBoxPixel.x, (int)diagnosticBoxPixel.y, BusVehicleAssetSystem.RequireTestMenu, diagnosticReport);
                    cleanup.Add(diagnostics.Dispose);
                    Exception diagnosticFailure = null;
                    try { await diagnostics.Run(cameraFrames, directory, name); }
                    catch (Exception error)
                    {
                        diagnosticFailure = error; diagnosticReport["status"] = "FAIL"; diagnosticReport["error"] = error.ToString(); throw;
                    }
                    finally
                    {
                        try { diagnostics.Dispose(); }
                        catch (Exception error)
                        {
                            if (diagnosticFailure != null) throw new AggregateException("Witness diagnostics and cleanup failed", diagnosticFailure, error);
                            throw;
                        }
                    }
                    // Every model uses the same test position: hide it before testing the next model.
                    glass.Visible = false;
                    root.SetActive(false);
                    UnityEngine.Object.Destroy(box);
                    cameraFrames.Dispose();
                    exposure.Dispose();
                }

                BusVehicleAssetSystem.RequireTestMenu();
                SavePng(shots, Path.Combine(directory, kImage));
                ok &= shots.Count > 0;
                result = (ok ? "ok " : "failed ") + log;
            }
            catch (Exception ex)
            {
                result = $"failed {ex.GetType().Name}: {ex.Message} {ex.StackTrace?.Split('\n')[0]} after {log}";
            }
            finally
            {
                var cleanupErrors = new List<string>();
                for (int i = cleanup.Count - 1; i >= 0; i--)
                {
                    try { cleanup[i](); } catch (Exception ex) { cleanupErrors.Add($"cleanup[{i}]: {ex}"); }
                }
                if (cleanupErrors.Count > 0)
                    result = "failed glass probe cleanup: " + string.Join("; ", cleanupErrors) + "; result before cleanup: " + result;
                if (Directory.Exists(directory)) File.WriteAllText(Path.Combine(directory, "glass-fixture.json"), JsonConvert.SerializeObject(new {
                    result, fixtures, cleanup_errors = cleanupErrors, source_materials_modified = false,
                    interpretation = "Geometry mask and view are fixed before colours. Original optical thresholds are unchanged; clipped red samples are invalid." }, Formatting.Indented));
            }
            return result;
        }

        internal static async Task WaitForRenderedWindow(BusRoadGlassCameraFrames frames, Action check)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            frames.BeginWindow();
            while (!frames.WindowReady)
            {
                check();
                if (Time.realtimeSinceStartupAsDouble >= deadline)
                    throw new TimeoutException("Glass witness camera did not complete its actual HDRP render window");
                await Task.Delay(15);
                check();
            }
            frames.EndWindow();
            check();
        }

        static float[] Components(Vector3 value) => new[] { value.x, value.y, value.z };

        static float[] Components(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (int i = 0; i < values.Length; i++) values[i] = matrix[i];
            return values;
        }

        internal static string PackageHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        static object GreyEvidence(Color32[] pixels, int x, int y, float depth, IDictionary<string, object> evidence = null)
        {
            evidence = evidence ?? new Dictionary<string, object>();
            evidence["pixel"] = new[] { x, y }; evidence["depth_m"] = depth;
            evidence["linear_albedo"] = .18f;
            if (depth <= 0 || x < 3 || y < 3 || x >= kW - 3 || y >= kH - 3)
            {
                evidence["status"] = "OUTSIDE_CAMERA";
                throw new InvalidOperationException("The geometry-placed grey reference is outside the witness camera");
            }
            long r = 0, g = 0, b = 0;
            var samples = new List<int[]>();
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
            {
                Color32 pixel = pixels[(y + dy) * kW + x + dx]; r += pixel.r; g += pixel.g; b += pixel.b;
                samples.Add(new[] { x + dx, y + dy, (int)pixel.r, pixel.g, pixel.b });
            }
            double[] mean = { r / 25d, g / 25d, b / 25d };
            evidence["samples_xy_rgb_8bit"] = samples; evidence["mean_rgb_8bit"] = mean;
            evidence["scope"] = "Diffuse reference under recorded inspection lighting; not a spectral transmittance calibration";
            if (mean.Any(channel => channel < 16 || channel >= 240))
            {
                evidence["status"] = "TOO_DARK_OR_CLIPPED";
                throw new InvalidOperationException("18 percent diffuse grey reference is too dark or clipped for glass comparison");
            }
            evidence["status"] = "PASS";
            return evidence;
        }

        static object GreyRayEvidence(Camera camera, Transform root, GameObject card, Light light, int x, int y, out bool visible)
        {
            var meshes = new List<(MeshFilter Filter, Vector3[] World, int[] Indices)>();
            MeshFilter cardFilter = card.GetComponent<MeshFilter>();
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    (camera.cullingMask & 1 << renderer.gameObject.layer) == 0) continue;
                Mesh mesh = filter.sharedMesh;
                Vector3[] positions = mesh.vertices;
                for (int i = 0; i < positions.Length; i++) positions[i] = filter.transform.TransformPoint(positions[i]);
                var indices = new List<int>();
                for (int i = 0; i < mesh.subMeshCount; i++) indices.AddRange(mesh.GetTriangles(i));
                meshes.Add((filter, positions, indices.ToArray()));
            }
            visible = true;
            Matrix4x4 worldToClip = camera.projectionMatrix * camera.worldToCameraMatrix;
            var candidates = meshes.Select(mesh => BusGlassWitnessGeometry.Candidates(mesh.World, mesh.Indices,
                point => BusGlassWitnessGeometry.Screen(worldToClip, point, kW, kH), x - 2, y - 2, x + 3, y + 3)).ToArray();
            var samples = new List<object>();
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
            {
                Ray ray = camera.ScreenPointToRay(new Vector3(x + dx + .5f, y + dy + .5f));
                float distance = float.MaxValue;
                MeshFilter closest = null;
                Vector3 normal = Vector3.zero;
                int triangle = -1;
                for (int m = 0; m < meshes.Count; m++)
                    foreach (int i in candidates[m])
                    {
                        var mesh = meshes[m];
                        Vector3 a = mesh.World[mesh.Indices[i]], b = mesh.World[mesh.Indices[i + 1]], c = mesh.World[mesh.Indices[i + 2]];
                        float hit = BusGlassWitnessGeometry.TriangleDistance(ray, a, b, c);
                        if (hit <= 0 || hit >= distance) continue;
                        distance = hit; closest = mesh.Filter; triangle = i / 3;
                        normal = Vector3.Cross(b - a, c - a).normalized;
                    }
                bool hitCard = ReferenceEquals(closest, cardFilter);
                visible &= hitCard;
                samples.Add(new { pixel = new[] { x + dx, y + dy }, origin = Components(ray.origin), direction = Components(ray.direction),
                    hit_card = hitCard, mesh_filter_id = closest == null ? (int?)null : closest.GetInstanceID(),
                    mesh_name = closest == null ? null : closest.sharedMesh.name, triangle,
                    distance_m = closest == null ? (float?)null : distance, world_normal = Components(normal),
                    normal_dot_incident_light = closest == null ? (float?)null : Vector3.Dot(normal, -light.transform.forward) });
            }
            Mesh cardMesh = cardFilter.sharedMesh;
            var cardPart = new[] { new BusGlassWitnessGeometry.Part(cardMesh.vertices, cardMesh.triangles) };
            return new { all_samples_hit_card_first = visible, card_id = card.GetInstanceID(), mesh_filter_id = cardFilter.GetInstanceID(),
                mesh_id = cardMesh.GetInstanceID(), positions_sha256 = BusGlassWitnessGeometry.Hash(cardPart, true),
                indices_sha256 = BusGlassWitnessGeometry.Hash(cardPart, false), local_to_world = Components(card.transform.localToWorldMatrix),
                light_incident_direction = Components(-light.transform.forward), samples,
                scope = "Nearest actual triangle among active owned grey card, red target and imported glass; this is a geometry witness, not a GPU object-ID/depth readback" };
        }

        internal static async Task<List<(string Name, PrefabBase Prefab)>> CheckTargets(Func<string, PrefabBase> findBus,
            IEnumerable<string> names, List<object> checks, StringBuilder log)
        {
            var targets = new List<(string Name, PrefabBase Prefab)>();
            foreach (string name in names)
            {
                BusVehicleAssetSystem.RequireTestMenu();
                BusVehicleAssetSystem.Model model = BusVehicleAssetSystem.ModelNamed(name);
                PrefabBase prefab = findBus(model.Name);
                if (prefab == null) throw new InvalidOperationException("Glass target vehicle is not loaded: " + name);
                await BusVehicleProbe.CheckTargetAssets(prefab, model.Name, checks);
                if (!CheckGlassAssets(prefab, log)) throw new InvalidOperationException("Glass target asset check failed: " + log);
                targets.Add((name, prefab));
            }
            if (targets.Count == 0) throw new InvalidOperationException("Glass probe requires at least one target vehicle");
            return targets;
        }

        internal static bool CheckGlassAssets(PrefabBase prefab, StringBuilder log)
        {
            if (!(prefab is ObjectGeometryPrefab geometry) || geometry.m_Meshes == null || geometry.m_Meshes.Length == 0 ||
                !(geometry.m_Meshes[0].m_Mesh is RenderPrefab mesh))
            {
                log.Append("missing vehicle render mesh; ");
                return false;
            }
            var meshes = new List<RenderPrefab> { mesh };
            if (mesh.TryGet(out LodProperties lods) && lods.m_LodMeshes != null)
                meshes.AddRange(lods.m_LodMeshes);
            bool valid = true;
            foreach (RenderPrefab level in meshes)
            {
                if (level == null) { log.Append("missing LOD render mesh; "); valid = false; continue; }
                var lease = new BusNativeMeshLease(level);
                Exception failure = null;
                try
                {
                    int glassCount = 0;
                    for (int i = 0; i < level.materialCount; i++)
                    {
                        var surface = level.GetSurfaceAsset(i);
                        if (surface == null || (surface.name ?? "").IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        glassCount++;
                        NativeMaterialLease native = null;
                        Exception materialFailure = null;
                        try
                        {
                            native = NativeMaterialLease.Acquire(surface);
                            Material material = native.Material;
                            Mesh source = lease.Get(i, out int subMesh);
                            bool shaderOk = material != null && material.shader != null && material.shader.name == "BH/GlsShader";
                            bool readable = source != null && source.isReadable && subMesh >= 0 && subMesh < source.subMeshCount && source.GetIndexCount(subMesh) >= 3;
                            valid &= shaderOk && readable;
                            log.Append($"{level.name}/{surface.name}: shader={material?.shader?.name ?? "missing"}, readable nonempty={readable}; ");
                        }
                        catch (Exception error) { materialFailure = error; throw; }
                        finally
                        {
                            try { native?.Dispose(); }
                            catch (Exception cleanup)
                            {
                                if (materialFailure != null) throw new AggregateException("Glass material check and release failed.", materialFailure, cleanup);
                                throw;
                            }
                        }
                    }
                    if (glassCount == 0) { log.Append($"{level.name}: no _Gls surface; "); valid = false; }
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    try { lease.Dispose(); }
                    catch (Exception cleanup)
                    {
                        if (failure != null) throw new AggregateException("Glass asset check and native mesh release failed.", failure, cleanup);
                        throw;
                    }
                }
            }
            return valid;
        }

        // Display-space acceptance, not an optical transmission measurement. The baseline selects red box pixels in the
        // central window. Require a visible, darker red box and prove that removing the box changes those same pixels;
        // the third render prevents an opaque red material from masquerading as transparent glass.
        internal static bool EvaluatePixels(Color32[] without, Color32[] with, Color32[] empty, out string detail, bool[] geometryMask = null)
        {
            if (without == null || with == null || empty == null || without.Length != kW * kH || with.Length != without.Length || empty.Length != without.Length ||
                geometryMask != null && geometryMask.Length != without.Length)
            {
                detail = "invalid render dimensions";
                return false;
            }
            int samples = 0, region = 0, redVisible = 0, boxVisible = 0;
            double beforeR = 0, afterR = 0, effect = 0;
            for (int y = kH * 2 / 5; y < kH * 3 / 5; y++)
                for (int x = kW * 2 / 5; x < kW * 3 / 5; x++)
                {
                    region++;
                    int index = y * kW + x;
                    Color32 a = without[index], b = with[index], c = empty[index];
                    if (geometryMask != null && !geometryMask[index]) continue;
                    if (a.r < 32 || a.r < a.g + 12 || a.r < a.b + 12) continue;
                    if (geometryMask != null && (a.r >= 254 || b.r >= 254))
                    {
                        detail = "clipped red sample in the frozen geometry mask; optical comparison is invalid";
                        return false;
                    }
                    samples++;
                    beforeR += a.r;
                    afterR += b.r;
                    if (b.r >= 8 && b.r >= b.g + 2 && b.r >= b.b + 2) redVisible++;
                    int contrastGain = (b.r - Math.Max(b.g, b.b)) - (c.r - Math.Max(c.g, c.b));
                    effect += contrastGain;
                    if (b.r >= c.r + 2 && contrastGain >= 2) boxVisible++;
                }
            if (samples < 256 || samples < region / 4)
            {
                detail = $"baseline red box covers only {samples}/{region} central pixels (need 256 and 25%)";
                return false;
            }
            double ratio = afterR / beforeR, reduction = (beforeR - afterR) / samples, contrast = effect / samples;
            bool passed = ratio >= 0.05 && ratio <= 0.98 && reduction >= 2 && contrast >= 2 &&
                redVisible >= samples * 0.6 && boxVisible >= samples * 0.6;
            detail = FormattableString.Invariant($"samples={samples}/{region}, red ratio={ratio:F3} [0.05..0.98], darkening={reduction:F2} >=2/255, box contrast gain={contrast:F2} >=2/255, red visible={redVisible}/{samples}, responds to box={boxVisible}/{samples} (both >=60%)");
            return passed;
        }

        // The mean colour of the middle of the view (where the box is).
        private static string Describe(Color32[] pixels)
        {
            long r = 0, g = 0, b = 0, n = 0;
            for (int y = kH * 2 / 5; y < kH * 3 / 5; y++)
                for (int x = kW * 2 / 5; x < kW * 3 / 5; x++)
                {
                    Color32 p = pixels[y * kW + x];
                    r += p.r; g += p.g; b += p.b; n++;
                }
            return $"rgb({r / n}, {g / n}, {b / n})";
        }

        internal static void Plain(HDAdditionalCameraData hd)
        {
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = new Color(0.3f, 0.42f, 0.58f, 1f);
            hd.customRenderingSettings = true;
            FrameSettings fs = hd.renderingPathCustomFrameSettings;
            FrameSettingsOverrideMask mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            foreach (FrameSettingsField f in new[] { FrameSettingsField.MotionBlur, FrameSettingsField.DepthOfField, FrameSettingsField.Volumetrics, FrameSettingsField.VolumetricClouds })
            {
                mask.mask[(uint)f] = true;
                fs.SetEnabled(f, false);
            }
            hd.renderingPathCustomFrameSettings = fs;
            hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
        }

        internal static Color32[] Read(RenderTexture rt)
        {
            RenderTexture was = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.GetPixels32();
            }
            finally
            {
                try { RenderTexture.active = was; }
                finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
            }
        }

        internal static void SaveFrame(Color32[] pixels, string path)
        {
            var texture = new Texture2D(kW, kH, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }

        // Three shots per bus side by side (without glass, with glass, glass without box), a row per bus.
        private static void SavePng(List<Color32[]> shots, string path)
        {
            if (shots.Count == 0)
                return;
            int rows = (shots.Count + 2) / 3;
            var tex = new Texture2D(kW * 3, kH * rows, TextureFormat.RGBA32, false);
            try
            {
                for (int i = 0; i < shots.Count; i++)
                    tex.SetPixels32(i % 3 * kW, (rows - 1 - i / 3) * kH, kW, kH, shots[i]);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }
    }

    // This selector consumes geometry only; it runs before any target-colour capture.
    internal sealed class BusGlassWitnessGeometry
    {
        internal readonly struct Part
        {
            internal readonly Vector3[] Positions;
            internal readonly int[] Indices;
            internal Part(Vector3[] positions, int[] indices) { Positions = positions; Indices = indices; }
        }
        readonly Part[] parts;
        internal readonly Vector3 Target, Eye, BoxSize = new Vector3(.6f, .9f, 3f);
        internal readonly string PositionHash, IndexHash;
        internal readonly int Triangles;

        internal BusGlassWitnessGeometry(Part[] parts)
        {
            if (parts == null || parts.Length == 0) throw new InvalidOperationException("No actual glass geometry");
            this.parts = parts;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            foreach (Part part in parts)
            {
                if (part.Positions == null || part.Indices == null || part.Indices.Length % 3 != 0)
                    throw new InvalidOperationException("Invalid glass geometry arrays");
                foreach (Vector3 v in part.Positions)
                {
                    if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z)) throw new InvalidOperationException("Non-finite glass position");
                    min = Vector3.Min(min, v); max = Vector3.Max(max, v);
                }
                foreach (int index in part.Indices)
                    if (index < 0 || index >= part.Positions.Length) throw new InvalidOperationException("Glass index outside actual vertex buffer");
                Triangles += part.Indices.Length / 3;
            }
            Vector3 centre = (min + max) * .5f, sum = Vector3.zero;
            double area = 0;
            foreach (Part part in parts)
                for (int i = 0; i < part.Indices.Length; i += 3)
                {
                    Vector3 a = part.Positions[part.Indices[i]], b = part.Positions[part.Indices[i + 1]], c = part.Positions[part.Indices[i + 2]];
                    Vector3 normal = Vector3.Cross(b - a, c - a), point = (a + b + c) / 3f;
                    float weight = normal.magnitude;
                    if (weight <= 1e-8f || Math.Abs(normal.x) < weight * .8f ||
                        point.x <= centre.x + (max.x - min.x) * .25f || Math.Abs(point.z - centre.z) > (max.z - min.z) * .2f) continue;
                    sum += point * weight; area += weight;
                }
            if (area <= 1e-8) throw new InvalidOperationException("No central side-glass patch in actual geometry");
            Vector3 selected = sum / (float)area;
            Target = new Vector3(centre.x, selected.y, selected.z);
            Eye = new Vector3(max.x + 5.75f, selected.y, selected.z);
            PositionHash = Hash(parts, true); IndexHash = Hash(parts, false);
        }

        // project maps a geometry point to (pixel x, pixel y, view depth) for the same camera as rayAt. It only
        // skips triangles whose projected bounds cannot contain a pixel centre; the hit test itself is unchanged.
        internal bool[] FreezeMask(int width, int height, Func<int, int, Ray> rayAt, out int selected, Func<Vector3, Vector3> project = null)
        {
            var mask = new bool[width * height]; selected = 0;
            int x0 = width * 2 / 5, x1 = width * 3 / 5, y0 = height * 2 / 5, y1 = height * 3 / 5;
            List<int>[][] cells = null;
            if (project != null)
            {
                cells = new List<int>[parts.Length][];
                for (int p = 0; p < parts.Length; p++)
                    cells[p] = PixelCandidates(parts[p].Positions, parts[p].Indices, project, x0, y0, x1, y1);
            }
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    Ray ray = rayAt(x, y);
                    float box = BoxDistance(ray, Target - BoxSize * .5f, Target + BoxSize * .5f);
                    if (box <= 0) continue;
                    for (int p = 0; p < parts.Length && !mask[y * width + x]; p++)
                    {
                        Part part = parts[p];
                        IEnumerable<int> triangles = cells == null ? AllTriangles(part.Indices.Length) : cells[p][(y - y0) * (x1 - x0) + x - x0];
                        foreach (int i in triangles)
                        {
                            float glass = TriangleDistance(ray, part.Positions[part.Indices[i]], part.Positions[part.Indices[i + 1]], part.Positions[part.Indices[i + 2]]);
                            if (glass > 0 && glass < box - .001f) { mask[y * width + x] = true; selected++; break; }
                        }
                    }
                }
            int region = (x1 - x0) * (y1 - y0);
            if (selected < 256 || selected < region / 4) throw new InvalidOperationException("Actual through-glass target coverage is below 256 pixels and 25% of the fixed central region");
            return mask;
        }

        const float kMarginPixels = 2f;

        static IEnumerable<int> AllTriangles(int indexCount)
        {
            for (int i = 0; i < indexCount; i += 3) yield return i;
        }

        internal static Vector3 Screen(Matrix4x4 toClip, Vector3 point, int width, int height)
        {
            Vector4 clip = toClip * new Vector4(point.x, point.y, point.z, 1f);
            if (clip.w <= 1e-4f) return new Vector3(float.NaN, float.NaN, clip.w);
            return new Vector3((clip.x / clip.w * .5f + .5f) * width, (clip.y / clip.w * .5f + .5f) * height, clip.w);
        }

        // Triangles near or behind the camera have no bounded projection and stay candidates everywhere.
        static bool Bounds(Vector3[] positions, int[] indices, int i, Func<Vector3, Vector3> project, out Vector2 min, out Vector2 max)
        {
            min = new Vector2(float.MaxValue, float.MaxValue); max = -min;
            for (int k = 0; k < 3; k++)
            {
                Vector3 s = project(positions[indices[i + k]]);
                if (!(s.z > 1e-4f) || !Finite(s.x) || !Finite(s.y)) return false;
                min = Vector2.Min(min, s); max = Vector2.Max(max, s);
            }
            min -= Vector2.one * kMarginPixels; max += Vector2.one * kMarginPixels;
            return true;
        }

        // Candidate triangle index offsets for each pixel centre of [x0, x1) x [y0, y1), row-major.
        static List<int>[] PixelCandidates(Vector3[] positions, int[] indices, Func<Vector3, Vector3> project, int x0, int y0, int x1, int y1)
        {
            int columns = x1 - x0;
            var cells = new List<int>[columns * (y1 - y0)];
            for (int c = 0; c < cells.Length; c++) cells[c] = new List<int>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                int left = x0, right = x1 - 1, bottom = y0, top = y1 - 1;
                if (Bounds(positions, indices, i, project, out Vector2 min, out Vector2 max))
                {
                    left = Math.Max(x0, (int)Math.Ceiling(min.x - .5f)); right = Math.Min(x1 - 1, (int)Math.Floor(max.x - .5f));
                    bottom = Math.Max(y0, (int)Math.Ceiling(min.y - .5f)); top = Math.Min(y1 - 1, (int)Math.Floor(max.y - .5f));
                }
                for (int y = bottom; y <= top; y++)
                    for (int x = left; x <= right; x++) cells[(y - y0) * columns + x - x0].Add(i);
            }
            return cells;
        }

        // Candidate triangle index offsets that may be hit by any pixel-centre ray of [x0, x1) x [y0, y1).
        internal static List<int> Candidates(Vector3[] positions, int[] indices, Func<Vector3, Vector3> project, int x0, int y0, int x1, int y1)
        {
            var result = new List<int>();
            for (int i = 0; i < indices.Length; i += 3)
                if (!Bounds(positions, indices, i, project, out Vector2 min, out Vector2 max) ||
                    (max.x >= x0 + .5f && min.x <= x1 - .5f && max.y >= y0 + .5f && min.y <= y1 - .5f)) result.Add(i);
            return result;
        }

        internal static float TriangleDistance(Ray ray, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 edge = b - a, other = c - a, cross = Vector3.Cross(ray.direction, other);
            float determinant = Vector3.Dot(edge, cross);
            if (Math.Abs(determinant) < 1e-8f) return -1;
            Vector3 offset = ray.origin - a;
            float u = Vector3.Dot(offset, cross) / determinant;
            if (u < 0 || u > 1) return -1;
            Vector3 q = Vector3.Cross(offset, edge);
            float v = Vector3.Dot(ray.direction, q) / determinant;
            if (v < 0 || u + v > 1) return -1;
            return Vector3.Dot(other, q) / determinant;
        }

        static float BoxDistance(Ray ray, Vector3 min, Vector3 max)
        {
            float near = 0, far = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                float direction = ray.direction[axis], origin = ray.origin[axis];
                if (Math.Abs(direction) < 1e-8f) { if (origin < min[axis] || origin > max[axis]) return -1; continue; }
                float a = (min[axis] - origin) / direction, b = (max[axis] - origin) / direction;
                near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
                if (near > far) return -1;
            }
            return near;
        }

        internal static string MaskHash(bool[] mask)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(mask.Select(value => value ? (byte)1 : (byte)0).ToArray()));
        }
        internal static string Hash(Part[] parts, bool positions)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            using (var sha = SHA256.Create())
            {
                writer.Write(parts.Length);
                foreach (Part part in parts)
                    if (positions) { writer.Write(part.Positions.Length); foreach (Vector3 v in part.Positions) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); } }
                    else { writer.Write(part.Indices.Length); foreach (int index in part.Indices) writer.Write(index); }
                writer.Flush(); return Hex(sha.ComputeHash(stream.ToArray()));
            }
        }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
