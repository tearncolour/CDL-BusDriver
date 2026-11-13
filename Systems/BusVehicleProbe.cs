using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CDLC.Asset.Rendering;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Game.Prefabs;
using Newtonsoft.Json;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Real game-renderer diagnostic, requested with probe=vehicle (or mode=vehicle).
    /// Uses the production cab upload/instance/poser and imported BusGlass. Forced
    /// mesh LODs inspect exported geometry; this is not a test of distance-based LOD
    /// switching, a driven game entity, mirror rendering, passengers or road lighting.
    /// </summary>
    internal static class BusVehicleProbe
    {
        const int Width = 960, Height = 640;
        const float Tolerance = .002f; // includes float precision at the isolated 3000 m test origin
        static readonly Vector3 Origin = new Vector3(0f, 3000f, 0f);

        sealed class Pane
        {
            internal Transform Transform;
            internal int Part, Surface, Triangles;
            internal Vector3[] Local, Rest;
        }

        internal static async Task<string> Run(Func<string, PrefabBase> findBus, IEnumerable<string> requestedNames, bool materialDiagnostic = false, bool refractionDiagnostic = false, bool shaderDiagnostic = false, bool inspectionDiagnostic = false, bool shadowDiagnostic = false, bool rendererDiagnostic = false, bool surfaceDiagnostic = false, bool contextDiagnostic = false, bool reflectionDiagnostic = false, bool pipelineDiagnostic = false, bool transmissionDiagnostic = false, bool reviewSample = false, bool reviewExterior = false, bool reviewMips = false, bool mirrorProbe = false, string sourceSha256 = null, Func<string, string> verifyLoaded = null, bool refractionCycle = false, bool glassAtlas = false, int? mirrorLod = null)
        {
            if (mirrorLod.HasValue && (!mirrorProbe || mirrorLod < 0 || mirrorLod > MeshKit.MaxLod))
                throw new ArgumentException("mirrorLod is only valid for mirror checks and must name an exported LOD");
            int[] requestedLods = mirrorLod.HasValue ? new[] { mirrorLod.Value } : Enumerable.Range(0, MeshKit.MaxLod + 1).ToArray();
            string directory = Path.Combine(Mod.ModDirectory, "vehicle-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(directory);
            var checks = new List<object>();
            var shots = new List<object>();
            var models = new List<object>();
            var materialChecks = new List<object>();
            var nativeChecks = new List<object>();
            var glassBufferChecks = new List<object>();
            var signChecks = new List<object>();
            var assetLoadChecks = new List<object>();
            var targetAssetChecks = new List<object>();
            var mirrorLodChecks = new List<object>();
            var refractionChecks = new List<object>();
            var cleanup = new List<Action>();
            var cleanupErrors = new List<string>();
            string result = null;
            var probeLights = new List<HDAdditionalLightData>();
            BusVehicleInspectionLighting standardLighting = null;
            var report = new Dictionary<string, object>
            {
                ["status"] = "running", ["utc"] = DateTime.UtcNow.ToString("o"),
                ["scope"] = "Game main-menu renderer; real CabModel/CabInstance/BusInterior.Apply/BusGlass, forced exported LODs. Supplemental to driving a vehicle in a loaded game.",
                ["glass_lod"] = "BusGlass uses the imported root LOD0 glass at every cab LOD, exactly as in production. CheckGlassAssets also validates the three imported LOD assets; these screenshots do not render Gls LOD1/2.",
                ["not_tested"] = mirrorProbe ? new[] { "distance-based LOD selection", "game entity entry and driving", "passengers", "road-scene mirror traffic", "all user camera settings" }
                    : new[] { "distance-based LOD selection", "mirror render cameras", "game entity entry and driving", "passengers", "road-scene lighting", "continuous collision-free motion" },
                ["manual_review_required"] = new[] { "all saved images", "door glass remains aligned during opening", "cab glazing/frit/ceiling are closed", "shared card reader visible", "wheel arch clearance", "straps and poles match the chosen interior", "normal materials and no missing faces" },
                ["lighting"] = "Dedicated daylight plus two inspection fill lights, no geometry hidden or moved for interior shots.",
                ["position_tolerance_m"] = Tolerance,
                ["models"] = models, ["pose_checks"] = checks, ["screenshots"] = shots, ["material_checks"] = materialChecks,
                ["native_checks"] = nativeChecks,
                ["material_diagnostic"] = materialDiagnostic,
                ["refraction_diagnostic"] = refractionDiagnostic,
                ["refraction_cycle"] = refractionCycle,
                ["refraction_checks"] = refractionChecks,
                ["shader_diagnostic"] = shaderDiagnostic,
                ["inspection_diagnostic"] = inspectionDiagnostic,
                ["shadow_diagnostic"] = shadowDiagnostic,
                ["renderer_diagnostic"] = rendererDiagnostic,
                ["surface_diagnostic"] = surfaceDiagnostic,
                ["context_diagnostic"] = contextDiagnostic,
                ["reflection_diagnostic"] = reflectionDiagnostic,
                ["pipeline_diagnostic"] = pipelineDiagnostic,
                ["transmission_diagnostic"] = transmissionDiagnostic,
                ["review_sample"] = reviewSample,
                ["review_exterior"] = reviewExterior,
                ["review_mips"] = reviewMips,
                ["mirror_probe"] = mirrorProbe, ["source_sha256"] = sourceSha256,
                ["mirror_lod_checks"] = mirrorLodChecks, ["requested_lods"] = requestedLods,
                ["glass_buffer_checks"] = glassBufferChecks,
                ["sign_checks"] = signChecks,
                ["asset_load_checks"] = assetLoadChecks,
                ["target_asset_preflight"] = targetAssetChecks,
            };
            try
            {
                // The game's executable loader can use Assembly.Load(byte[]),
                // whose Location is empty. Bind the deployed file plus the
                // running module ID; the launch script verifies both before
                // accepting this report as evidence for its selected build.
                var assembly = typeof(BusVehicleProbe).Assembly;
                string assemblyPath = assembly.Location;
                if (string.IsNullOrWhiteSpace(assemblyPath))
                {
                    Require(!string.IsNullOrWhiteSpace(Mod.ModDirectory), "game executable asset directory is unavailable");
                    assemblyPath = Path.Combine(Mod.ModDirectory, "CDLC.BusDriver.dll");
                }
                report["assembly_path"] = assemblyPath;
                report["assembly_sha256"] = FileHash(assemblyPath);
                report["assembly_mvid"] = assembly.ManifestModule.ModuleVersionId.ToString("D");
                report["assembly_hash_scope"] = "Deployed executable file; the running module MVID is independently checked by the launch script.";
                // Reject invalid loaded assets before allocating any inspection
                // camera, render target or lights. A failed preflight needs no
                // rendering resources or their cleanup lifecycle.
                string[] names = requestedNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                Require(names.Length > 0, "no vehicle names requested");
                foreach (string name in names)
                {
                    Require(verifyLoaded != null, "No production asset-loading verifier supplied");
                    string loaded = verifyLoaded(name);
                    assetLoadChecks.Add(new { name, result = loaded, scope = "Production Verify on this cold-loaded package before rendering" });
                    Require(loaded.StartsWith("ok ", StringComparison.Ordinal), "Loaded asset verification failed: " + loaded);
                    await CheckTargetAssets(findBus(name), name, targetAssetChecks);
                }
                var target = new RenderTexture(reviewExterior ? 1920 : Width, reviewExterior ? 1280 : Height, 24, RenderTextureFormat.ARGB32);
                cleanup.Add(() => UnityEngine.Object.Destroy(target));
                cleanup.Add(target.Release);
                target.hideFlags = HideFlags.DontSave;
                var cameraObject = new GameObject("CDLC vehicle probe camera");
                cleanup.Add(() => UnityEngine.Object.Destroy(cameraObject));
                cameraObject.hideFlags = HideFlags.DontSave;
                var camera = cameraObject.AddComponent<Camera>();
                cleanup.Add(() => camera.targetTexture = null);
                camera.enabled = false; camera.nearClipPlane = .025f; camera.farClipPlane = 100f;
                BusGlassProbe.Plain(cameraObject.AddComponent<HDAdditionalCameraData>());
                camera.targetTexture = target;
                cleanup.Add(MakeLight("daylight", UnityEngine.LightType.Directional, Origin + Vector3.up * 12f, 30000f, probeLights));
                cleanup.Add(MakeLight("cab inspection fill", UnityEngine.LightType.Point, Origin + new Vector3(0f, 2.3f, 3.8f), 700f, probeLights));
                cleanup.Add(MakeLight("saloon inspection fill", UnityEngine.LightType.Point, Origin + new Vector3(0f, 2.3f, -.8f), 1000f, probeLights));
                if (!materialDiagnostic)
                {
                    standardLighting = new BusVehicleInspectionLighting(Origin, probeLights);
                    cleanup.Add(standardLighting.Dispose);
                    report["lighting"] = standardLighting.Evidence();
                }

                foreach (string name in names)
                {
                    PrefabBase prefab = findBus(name);
                    Require(prefab != null, "vehicle prefab not loaded: " + name);
                    var profile = GenericBusReview.Profile(prefab);
                    Require(profile != null, "vehicle probe needs a bound vehicle profile or the built-in bus: " + name);
                    var assetLog = new StringBuilder();
                    Require(BusGlassProbe.CheckGlassAssets(prefab, assetLog), "imported glass asset check failed: " + assetLog);
                    var source = new BusInterior(profile.Spec);
                    KitModelData data = await Task.Run(() => KitBuild.BuildAll(source));
                    BusVehicleAssetSystem.RequireTestMenu();
                    Require(data.Error == null, "runtime model failed: " + data.Error);
                    Require(data.Parts.Length > BusInterior.PartCount, "authored vehicle has no independent hand straps");
                    models.Add(new { name, resource = GenericBusReview.Resource(profile), resource_sha256 = MeshHash(profile), uv_companion_sha256 = GenericBusReview.UvHash(profile),
                        generic_frame_rounding = source.IsAuthored ? null : GenericBusMesh.For(source.Spec).FrameDifferences,
                        package_sha256 = PackageHash(prefab), parts = data.Parts.Length, source_triangles_by_lod = data.Tris,
                        imported_glass = assetLog.ToString(), driver_eye_model_m = A(BusInterior.CameraEye(profile.Spec)) });

                    foreach (int lod in requestedLods)
                    {
                        // A private part table directs CabModel's normal upload to one real source LOD.
                        // Never mutate the source/cached model or replace its material recipe.
                        KitModelData selected = SelectLod(data, lod);
                        var model = new CabModel(selected);
                        CabInstance instance = null;
                        BusVehicleSignProbe signs = null;
                        var glass = new BusGlass();
                        try
                        {
                            instance = new CabInstance(model, "CDLC vehicle probe " + name + " LOD" + lod);
                            instance.OverrideMaterial(BusInterior.Livery, CabMaterials.Livery(new Color32(
                                (byte)profile.DefaultBodyColor.x, (byte)profile.DefaultBodyColor.y, (byte)profile.DefaultBodyColor.z, 255)));
                            // Exactly the production order: glass binding at rest, before the first posed root/door update.
                            Require(glass.Build(prefab, instance.Root, 0, BusCabSystem.DoorLeafOf(profile.Spec), instance.Part), "BusGlass.Build failed");
                            List<Pane> panes = InspectGlass(prefab, instance, profile.Spec, name, lod, glassBufferChecks);
                            var basePose = new BusInterior.Pose { Position = Origin, Rotation = quaternion.identity,
                                Kneel = quaternion.identity, Outside = true, Lights = new KitLights { Lamps = true } };
                            if (glassAtlas)
                            {
                                ApplyAndCheck(instance, selected, panes, profile.Spec, basePose, name, lod, "glass-atlas-closed", checks);
                                signs = new BusVehicleSignProbe(instance.Root, profile.Spec);
                                signs.SetVisible(true, "glass-atlas-outside");
                                report["glass_atlas_diagnostic"] = await BusGlassAtlasDiagnostic.Run(camera, target, directory, name, instance.Root, profile, shots);
                                report["opaque_atlas_diagnostic"] = await BusOpaqueAtlasDiagnostic.Run(camera, target, directory, name, prefab, instance, profile, shots);
                                break;
                            }
                            if (refractionCycle)
                            {
                                ApplyAndCheck(instance, selected, panes, profile.Spec, basePose, name, lod, "refraction-saloon-closed", checks);
                                InspectMaterials(instance, selected, profile, name, lod, materialChecks);
                                refractionChecks.Add(await BusVehicleMaterialDiagnostics.RunRefraction(camera, target, directory,
                                    name, lod, instance.Root, () => standardLighting.Evidence(), shots));
                                continue;
                            }
                            if (mirrorProbe)
                            {
                                basePose.Outside = false;
                                ApplyAndCheck(instance, selected, panes, profile.Spec, basePose, name, lod, "mirrors-closed-cab", checks);
                                InspectMaterials(instance, selected, profile, name, lod, materialChecks);
                                using (var inspectionLighting = new BusVehicleInspectionLighting(Origin, probeLights))
                                {
                                    report["lighting"] = inspectionLighting.Evidence();
                                    var bindings = new Dictionary<string, object> { ["source_sha256"] = sourceSha256,
                                        ["source_binding_scope"] = "Launcher-supplied source hash; launcher checks before/after. Runtime resource is independently hashed in game.",
                                        ["package_sha256"] = PackageHash(prefab), ["runtime_sha256"] = MeshHash(profile),
                                        ["model"] = name, ["lod"] = lod,
                                        ["assembly_sha256"] = report["assembly_sha256"], ["assembly_mvid"] = report["assembly_mvid"] };
                                    var mirrorReport = await BusVehicleMirrorProbe.Run(instance, profile.Spec,
                                        Path.Combine(directory, "mirrors", name, "lod" + lod), lod, 58f, bindings);
                                    mirrorLodChecks.Add(mirrorReport);
                                    if (lod == 0) report["mirror_checks"] = mirrorReport;
                                    Require((bool)mirrorReport["automatic_passed"], "Production mirror diagnostic failed; inspect mirror report and saved images");
                                }
                                continue;
                            }
                            if (inspectionDiagnostic)
                            {
                                CheckDiagnosticPoses(instance, selected, panes, profile.Spec, basePose, name, lod, checks);
                                InspectMaterials(instance, selected, profile, name, lod, materialChecks);
                                if (lod == 0)
                                    report["diagnostic_groups"] = await BusVehicleMaterialDiagnostics.RunInspection(camera, target, directory, name, Origin, probeLights, shots);
                                continue;
                            }
                            if (materialDiagnostic)
                            {
                                ApplyAndCheck(instance, selected, panes, profile.Spec, basePose, name, lod, "diagnostic-closed", checks);
                                InspectMaterials(instance, selected, profile, name, lod, materialChecks);
                                report["diagnostic_groups"] = reviewSample ?
                                    await BusVehicleReviewSample.Run(prefab, camera, target, directory, name, Origin, instance.Root, probeLights, shots, reviewExterior, reviewMips) : transmissionDiagnostic ?
                                    await BusGlassTransmissionDiagnostic.Run(camera, target, directory, name, Origin, instance.Root, probeLights, shots) : pipelineDiagnostic ?
                                    await BusGlassPipelineDiagnostic.Run(camera, target, directory, name, Origin, instance.Root, probeLights, shots) : reflectionDiagnostic ?
                                    await BusGlassReflectionDiagnostic.Run(camera, target, directory, name, Origin, instance.Root, probeLights, shots) : contextDiagnostic ?
                                    await BusGlassContextDiagnostic.Run(camera, target, directory, name, Origin, instance.Root, probeLights, shots) : surfaceDiagnostic ?
                                    await BusGlassSurfaceDiagnostic.Run(camera, target, directory, name, Origin, instance.Root, probeLights, shots) : rendererDiagnostic ?
                                    await BusRendererContractDiagnostic.Run(prefab, camera, target, directory, name, Origin, instance.Root, probeLights, shots) : shadowDiagnostic ?
                                    await BusVehicleMaterialDiagnostics.RunShadows(camera, target, directory, name, Origin, probeLights, shots) : shaderDiagnostic ?
                                    await BusVehicleMaterialDiagnostics.RunShader(camera, target, directory, name, Origin, instance.Root, probeLights, shots) : refractionDiagnostic ?
                                    await BusVehicleMaterialDiagnostics.RunIsolation(camera, target, directory, name, Origin, instance.Root, glass, probeLights, shots) :
                                    await BusVehicleMaterialDiagnostics.Run(camera, target, directory, name, Origin, instance.Root, probeLights, shots);
                                break;
                            }
                            await BusVehicleSignProbe.WaitForCardFont();
                            signs = new BusVehicleSignProbe(instance.Root, profile.Spec);
                            signChecks.Add(signs.Evidence(name, lod));
                            foreach (float opening in new[] { 0f, .5f, 1f })
                            {
                                var pose = basePose; pose.DoorOpen = new float3(opening); pose.FourthDoorOpen = opening;
                                string state = opening == 0f ? "doors-closed" : opening == 1f ? "doors-open" : "doors-half";
                                ApplyAndCheck(instance, selected, panes, profile.Spec, pose, name, lod, state, checks);
                                signs.SetVisible(true, state + "-outside");
                                await CaptureReviewed(camera, target, directory, name, lod, state + "-outside", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), 35f, shots);
                                signs.SetVisible(false, state + "-interior");
                                // The generic cab rear crossbar intersects the shared
                                // inspection camera's near plane; use its actual driver eye.
                                bool generic = profile.Spec.ModelVariant == BusModelVariant.Generic;
                                await CaptureReviewed(camera, target, directory, name, lod, state + "-front-door-inside",
                                    generic ? V(BusInterior.CameraEye(profile.Spec)) : new Vector3(-.35f, 1.80f, 4.10f),
                                    new Vector3(1.10f, 1.42f, generic ? profile.Doors[0].x : 4.15f), 86f, shots);
                                await CaptureReviewed(camera, target, directory, name, lod, state + "-middle-door-inside",
                                    new Vector3(-.10f, 1.60f, 1.00f), new Vector3(1.12f, 1.40f, -.30f), 78f, shots);
                                pose.Outside = false;
                                ApplyAndCheck(instance, selected, panes, profile.Spec, pose, name, lod, state + "-cab", checks);
                                Vector3 eye = V(profile.Spec.ModelVariant == BusModelVariant.Generic ? BusInterior.CameraEye(profile.Spec) : BusInterior.Eye(profile.Spec));
                                await CaptureReviewed(camera, target, directory, name, lod, state + "-cab", eye, eye + new Vector3(.18f, -.20f, 1.35f), 88f, shots);
                            }
                            for (int door = 0; door < profile.Spec.Doors; door++)
                            {
                                var pose = basePose; pose.DoorOpen = float3.zero; pose.FourthDoorOpen = 0f;
                                if (door < 3) pose.DoorOpen[door] = 1f; else pose.FourthDoorOpen = 1f;
                                string state = "door-" + door + "-only";
                                ApplyAndCheck(instance, selected, panes, profile.Spec, pose, name, lod, state, checks);
                                signs.SetVisible(true, state);
                                await CaptureReviewed(camera, target, directory, name, lod, state, new Vector3(9.7f, 3.5f, 8.5f), new Vector3(0f, 1.5f, .5f), 52f, shots);
                            }
                            ApplyAndCheck(instance, selected, panes, profile.Spec, basePose, name, lod, "saloon-closed", checks);
                            signs.SetVisible(true, "LED exterior closeups");
                            await signs.Capture(camera, target, directory, name, lod, shots);
                            signs.SetVisible(false, "saloon and material interior views");
                            await CaptureReviewed(camera, target, directory, name, lod, "saloon-rear-to-front", new Vector3(0f, 2.15f, profile.Spec.RearZ + 1.15f), new Vector3(0f, 2.05f, 3.4f), 85f, shots);
                            await CaptureReviewed(camera, target, directory, name, lod, "saloon-front-to-rear", new Vector3(0f, 1.9f, 3.25f), new Vector3(0f, 1.85f, profile.Spec.RearZ + 1f), 86f, shots);
                            InspectMaterials(instance, selected, profile, name, lod, materialChecks);
                            report["runtime_reflection_environment"] = BusNativeVehicleProbe.EnvironmentEvidence();
                            await CaptureReviewed(camera, target, directory, name, lod, "rear-seat-access", new Vector3(-.20f, 1.60f, .50f), new Vector3(.74f, 1.05f, -1.42f), 70f, shots);
                            await CaptureReviewed(camera, target, directory, name, lod, "handrail-material", new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots);
                            foreach (float steering in new[] { -35f, 35f })
                            {
                                var pose = basePose; pose.WheelSteerDeg = steering; pose.SteerDeg = steering * 4f;
                                string state = steering < 0 ? "steer-minus35" : "steer-plus35";
                                ApplyAndCheck(instance, selected, panes, profile.Spec, pose, name, lod, state, checks);
                                signs.SetVisible(true, state);
                                foreach (int side in new[] { -1, 1 })
                                    await CaptureReviewed(camera, target, directory, name, lod, state + (side < 0 ? "-left" : "-right"),
                                        new Vector3(side * 3.1f, 1.05f, profile.Spec.FrontAxleZ + 1.2f),
                                        new Vector3(side * .99f, profile.WheelRadius, profile.Spec.FrontAxleZ), 54f, shots);
                            }
                            foreach (int axis in new[] { 0, 1 })
                            {
                                var pose = basePose; pose.StrapSwing = axis == 0 ? new float2(.25f, 0f) : new float2(0f, .2f);
                                string state = axis == 0 ? "straps-fore-aft" : "straps-sideways";
                                ApplyAndCheck(instance, selected, panes, profile.Spec, pose, name, lod, state, checks);
                                signs.SetVisible(false, state);
                                await CaptureReviewed(camera, target, directory, name, lod, state, new Vector3(0f, 1.85f, 3.15f), new Vector3(0f, 2.55f, -2f), 90f, shots);
                            }
                        }
                        finally
                        {
                            try { signs?.Dispose(); } catch (Exception ex) { cleanupErrors.Add("Signs: " + ex); }
                            try { if (instance != null) instance.Visible = false; } catch (Exception ex) { cleanupErrors.Add("Visibility: " + ex); }
                            try { glass.Dispose(); } catch (Exception ex) { cleanupErrors.Add("Glass: " + ex); }
                            try { instance?.Destroy(); } catch (Exception ex) { cleanupErrors.Add("Cab instance: " + ex); }
                            try { model.Destroy(); } catch (Exception ex) { cleanupErrors.Add("Cab model: " + ex); }
                        }
                    }
                    if (!materialDiagnostic && !refractionCycle && !glassAtlas)
                        await BusNativeVehicleProbe.Run(prefab, name, camera, target, directory, Origin, shots, nativeChecks);
                }
                report["status"] = "automatic checks passed; manual image review required";
                result = "ok vehicle supplemental probe: " + (mirrorProbe ? mirrorLodChecks.Count + " mirror LOD checks" : shots.Count + " screenshots") +
                    ", " + checks.Count + " pose checks; manual review required; " + directory;
            }
            catch (Exception ex)
            {
                report["status"] = "failed"; report["error"] = ex.ToString();
                result = "failed vehicle probe: " + ex.GetType().Name + ": " + ex.Message + "; " + directory;
            }
            finally
            {
                for (int i = cleanup.Count - 1; i >= 0; i--)
                    try { cleanup[i](); } catch (Exception ex) { cleanupErrors.Add(ex.ToString()); }
                report["cleanup"] = new { passed = cleanupErrors.Count == 0, errors = cleanupErrors };
                if (cleanupErrors.Count != 0)
                {
                    report["status"] = "failed";
                    result = "failed vehicle probe cleanup; " + directory;
                }
                File.WriteAllText(Path.Combine(directory, "report.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            return result;
        }

        internal static KitModelData SelectLod(KitModelData source, int lod)
        {
            if (lod < 0 || lod > MeshKit.MaxLod) throw new ArgumentOutOfRangeException(nameof(lod));
            var result = new KitModelData { Key = source.Key, Variant = source.Variant, Lod = source.Lod,
                Bounds = source.Bounds, Center = source.Center, Radius = source.Radius, Parts = new KitPartDef[source.Parts.Length] };
            for (int i = 0; i < source.Parts.Length; i++)
            {
                var p = source.Parts[i];
                var q = new KitPartDef { Name = p.Name, Index = p.Index, Parent = p.Parent, Pivot = p.Pivot, Rot = p.Rot,
                    Flags = p.Flags, MeshOf = p.MeshOf, LocalPos = p.LocalPos, LocalRot = p.LocalRot, ModelToPart = p.ModelToPart };
                q.Data[0] = p.Data[lod]; q.Tris[0] = p.Tris[lod]; result.Parts[i] = q;
            }
            result.Tris[0] = source.Tris[lod]; result.Renderers[0] = source.Renderers[lod];
            return result;
        }

        // The palette swatches each model's LOD0 mesh uses; a lower LOD may drop swatches but never add one.
        static readonly Dictionary<string, HashSet<Sw>> s_Lod0Swatches = new Dictionary<string, HashSet<Sw>>();

        static void InspectMaterials(CabInstance instance, KitModelData data, VehicleProfile profile, string name, int lod, List<object> checks)
        {
            var sampled = new HashSet<Sw>();
            var authored = new HashSet<Sw>();
            int firstSurface = profile.FirstDoorDrive + profile.DoorDriveCount;
            for (int part = 0; part < data.Parts.Length; part++)
            {
                if (part >= BusInterior.WrapLeft && part < BusInterior.PartCount) continue;
                // Appended wrap surfaces use picture coordinates, not palette row coordinates.
                if (part >= firstSurface && part < firstSurface + profile.WrapSurfaceCount) continue;
                var definition = data.Parts[part];
                var mesh = definition.Data[0] ?? (definition.MeshOf >= 0 ? data.Parts[definition.MeshOf].Data[0] : null);
                if (mesh == null) continue;
                foreach (int index in mesh.Idx)
                {
                    Sw used = (Sw)(int)math.floor(mesh.UV[index].y * KitPalette.Rows);
                    if (used >= Sw.BrushedStainless && used < Sw.Count) authored.Add(used);
                }
                var renderer = instance.Part(part).GetComponent<MeshRenderer>();
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                Require(materials.Length == mesh.SubFin.Length, "material/submesh count differs: " + part);
                // The line-colour band uses the production Livery override,
                // rather than a cab plastic/metal detail material.
                if (data.Key.Name == "bus-interior" && part != BusInterior.Livery)
                {
                    Mesh uploaded = instance.Part(part).GetComponent<MeshFilter>().sharedMesh;
                    Require(uploaded.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent) &&
                        uploaded.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1), "generic cab has no detail UV/tangent stream");
                    for (int sub = 0; sub < mesh.SubFin.Length; sub++)
                    {
                        string tile = GenericCabDetail.Tile(mesh.SubFin[sub], part == BusInterior.SteeringWheel);
                        if (tile == null) continue;
                        Material material = materials[sub];
                        if (GameShaderMove.OnGameShader(material))
                        {
                            // made on the game's shader (no detail map there): the decal settings are what matter, the colour and gloss are checked below
                            Require(MaterialAuditRules.InDecalQueue(material.renderQueue) && material.IsKeywordEnabled("_DISABLE_DECALS") &&
                                math.asint(material.GetFloat("colossal_DecalLayerMask")) == (int)Game.Rendering.DecalLayers.Vehicles,
                                "generic cab material " + material.name + " on the game's shader has not the decal-receiving queue, the keyword and the vehicle layer");
                            checks.Add(new { name, lod, part, sub, baked_tile = "none: game shader", shader = material.shader.name, queue = material.renderQueue,
                                scope = "Actual material of the uploaded mesh; the road's decals leave the game's shader's vehicle layer alone (seen by the owner 7 November)" });
                            continue;
                        }

                        Texture texture = material.GetTexture("_DetailMap");
                        Require(texture != null && texture.width == 512 && texture.height == 512 && texture.wrapMode == TextureWrapMode.Repeat &&
                            !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat), "generic cab detail texture contract failed");
                        Require(MaterialAuditRules.InDecalQueue(material.renderQueue), "generic cab material " + material.name + " is in render queue " + material.renderQueue + ", outside the depth prepass the road's decals are cut against");
                        Require(material.IsKeywordEnabled("_DETAIL_MAP") && material.IsKeywordEnabled("_NORMALMAP") &&
                            material.GetFloat("_UVDetail") == 1f && material.GetFloat("_DetailNormalScale") == 1f &&
                            material.GetFloat("_DetailAlbedoScale") == 0f && material.GetFloat("_DetailSmoothnessScale") == 0f,
                            "generic cab baked normal is not enabled on its metric UV stream");
                        checks.Add(new { name, lod, part, sub, baked_tile = tile, texture = texture.name, format = texture.graphicsFormat.ToString(),
                            width = texture.width, height = texture.height, uv_channel = 1, normal_scale = material.GetFloat("_DetailNormalScale"),
                            smoothness = material.GetFloat("_Smoothness"), shader = material.shader.name,
                            scope = "Actual uploaded mesh and HDRP material inputs; rendered appearance is assessed separately." });
                    }
                }
                for (int sub = 0; sub < mesh.SubFin.Length; sub++)
                {
                    for (int i = mesh.SubStart[sub]; i < mesh.SubStart[sub] + mesh.SubCount[sub]; i++)
                    {
                        Sw sw = (Sw)(int)math.floor(mesh.UV[mesh.Idx[i]].y * KitPalette.Rows);
                        if (sw < Sw.BrushedStainless || sw >= Sw.Count || !sampled.Add(sw)) continue;
                        Fin finish = KitPalette.FinOf(sw);
                        Material material = materials[sub];
                        bool onGame = GameShaderMove.OnGameShader(material);
                        Require(material != null && material.shader != null && (onGame || material.shader.name == "HDRP/Lit"),
                            "authored material shader unavailable: " + sw);
                        Require(mesh.SubFin[sub] == finish, "wrong authored material finish: " + sw);
                        foreach (string property in new[] { "_BaseColorMap", "_BaseColor", "_EmissiveColor" })
                            Require(material.HasProperty(property), "material property unavailable: " + property);
                        float metallic, smoothness, coat = 0f, tolerance = .0001f;
                        if (onGame)
                        {
                            // the game's shader reads metallic and smoothness from the mask map (R and A of a 4 x 4 map), as the baked details of the game do
                            var maskMap = material.GetTexture("_MaskMap") as Texture2D;
                            Require(maskMap != null, "material on the game's shader has no mask map: " + sw);
                            Color texel = maskMap.GetPixel(0, 0);
                            metallic = texel.r; smoothness = texel.a; tolerance = .003f;
                            if (maskMap.width == KitPalette.Width)
                            {
                                // a coated palette finish: the clear coat is on the paint rows and on no other
                                float coatTexel = maskMap.GetPixel(KitPalette.Width / 2, (int)sw * KitPalette.RowHeight + KitPalette.RowHeight / 2).g;
                                Require(Mathf.Abs(coatTexel - (KitPalette.ClearCoat(sw) ? 1f : 0f)) < .003f, "clear coat of the mask map at the row of " + sw + " is " + coatTexel);
                            }

                            Require(MaterialAuditRules.InDecalQueue(material.renderQueue) && material.IsKeywordEnabled("_DISABLE_DECALS") &&
                                math.asint(material.GetFloat("colossal_DecalLayerMask")) == (int)Game.Rendering.DecalLayers.Vehicles,
                                "material on the game's shader has not the decal-receiving queue, the keyword and the vehicle layer: " + sw);
                        }
                        else
                        {
                            foreach (string property in new[] { "_Metallic", "_Smoothness", "_CoatMask" })
                                Require(material.HasProperty(property), "material property unavailable: " + property);
                            metallic = material.GetFloat("_Metallic"); smoothness = material.GetFloat("_Smoothness"); coat = material.GetFloat("_CoatMask");
                        }

                        Vector4 emission = material.GetVector("_EmissiveColor");
                        Require(Mathf.Abs(metallic - KitPalette.Metallic(finish)) < tolerance &&
                                Mathf.Abs(smoothness - KitPalette.Smoothness(finish)) < tolerance && coat < .0001f &&
                                new Vector3(emission.x, emission.y, emission.z).sqrMagnitude < 1e-8f,
                            "authored material physical parameters differ: " + sw);
                        Texture map = material.GetTexture("_BaseColorMap");
                        Require(map != null && map.width == KitPalette.Width && map.height == KitPalette.Height,
                            "authored palette map missing/wrong size: " + sw);
                        int x = map.width / 2, y = ((int)sw * KitPalette.RowHeight) + KitPalette.RowHeight / 2;
                        Color actual = SampleLinear(map, x, y);
                        float3 expected = KitPalette.ToLinear(KitPalette.RampSrgb(finish, sw, (x + .5f) / map.width));
                        float error = math.cmax(math.abs(new float3(actual.r, actual.g, actual.b) - expected));
                        Color baseColor = material.GetColor("_BaseColor");
                        Require(error < .006f && Mathf.Abs(baseColor.r - 1f) < .0001f &&
                                Mathf.Abs(baseColor.g - 1f) < .0001f && Mathf.Abs(baseColor.b - 1f) < .0001f,
                            "authored palette GPU sample differs from linear colour: " + sw + " error=" + error);
                        checks.Add(new { name, lod, part, swatch = sw.ToString(), finish = finish.ToString(),
                            shader = material.shader.name, metallic, smoothness, coat,
                            palette = map.name, palette_size = new[] { map.width, map.height },
                            sampled_linear_rgb = new[] { actual.r, actual.g, actual.b }, expected_linear_rgb = A(expected),
                            maximum_colour_error = error, nonemissive = true });
                    }
                }
            }
            // Per model: every swatch its mesh uses must render with its checked material, and a bus has stainless handrails.
            var unrendered = authored.Where(sw => !sampled.Contains(sw)).Select(sw => sw.ToString()).ToArray();
            Require(unrendered.Length == 0, "authored swatches without a rendered material: " + string.Join(", ", unrendered));
            Require(sampled.Contains(Sw.BrushedStainless), "stainless handrail material absent from actual model");
            if (lod == 0) s_Lod0Swatches[name] = new HashSet<Sw>(authored);
            else if (s_Lod0Swatches.TryGetValue(name, out HashSet<Sw> lod0))
            {
                var added = authored.Where(sw => !lod0.Contains(sw)).Select(sw => sw.ToString()).ToArray();
                Require(added.Length == 0, "LOD" + lod + " uses swatches absent at LOD0: " + string.Join(", ", added));
            }
            checks.Add(new { name, lod, authored_swatches = authored.Select(sw => sw.ToString()).OrderBy(x => x).ToArray(),
                rendered_and_checked = sampled.Count, scope = "Per-model fixture: the swatches this LOD's own mesh uses; stainless handrails required" });
        }

        internal static Color SampleLinear(Texture texture, int x, int y)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture buffer = RenderTexture.GetTemporary(texture.width, texture.height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                // Actual GPU texture sampling decodes the sRGB palette. A linear
                // float target/readback avoids a second colour-space conversion.
                Graphics.Blit(texture, buffer);
                RenderTexture.active = buffer;
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply();
                return copy.GetPixel(x, y);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(buffer);
                UnityEngine.Object.Destroy(copy);
            }
        }

        static List<Pane> InspectGlass(PrefabBase prefab, CabInstance instance, BusInteriorSpec spec, string vehicleName, int lod, List<object> bufferChecks)
        {
            var geometry = (ObjectGeometryPrefab)prefab;
            var render = (RenderPrefab)geometry.m_Meshes[0].m_Mesh;
            var meshes = new BusNativeMeshLease(render);
            Exception failure = null;
            try
            {
                var classify = BusCabSystem.DoorLeafOf(spec);
                var expected = new Dictionary<(int surface, int part), List<int>>();
                var sourceVertices = new Dictionary<int, Vector3[]>();
                var sourceMeshes = new Dictionary<int, Mesh>();
                for (int s = 0; s < render.materialCount; s++)
                {
                    string name = render.GetSurfaceAsset(s)?.name ?? "";
                    if (name.IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("_Glf", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Mesh mesh = meshes.Get(s, out int sub);
                    sourceMeshes[s] = mesh;
                    Vector3[] vertices = mesh.vertices; sourceVertices[s] = vertices;
                    int[] triangles = mesh.GetTriangles(sub);
                    for (int t = 0; t < triangles.Length; t += 3)
                    {
                        int part = classify((vertices[triangles[t]] + vertices[triangles[t + 1]] + vertices[triangles[t + 2]]) / 3f);
                        var key = (s, part);
                        if (!expected.TryGetValue(key, out var list)) expected[key] = list = new List<int>();
                        list.Add(triangles[t]); list.Add(triangles[t + 1]); list.Add(triangles[t + 2]);
                    }
                }
                var result = new List<Pane>();
                foreach (MeshFilter filter in instance.Root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (!filter.name.StartsWith("Glass ", StringComparison.Ordinal)) continue;
                    int surface = int.Parse(filter.name.Split(' ')[1]);
                    int part = -1;
                    if (filter.transform.parent != instance.Root)
                    {
                        part = Enumerable.Range(0, instance.PartCount).FirstOrDefault(i => instance.Part(i) == filter.transform.parent);
                        Require(instance.Part(part) == filter.transform.parent, "glass attached outside the cab part hierarchy");
                    }
                    var key = (surface, part);
                    Require(expected.TryGetValue(key, out var wanted), "unexpected glass group " + key);
                    Mesh mesh = filter.sharedMesh; int[] indices = mesh.GetTriangles(0);
                    bufferChecks.Add(BusGlassBufferAudit.Compare(sourceMeshes[surface], mesh, vehicleName, lod, surface, part));
                    Require(wanted.SequenceEqual(indices), "glass split lost/reordered source triangles for " + key);
                    expected.Remove(key);
                    int[] used = indices.Distinct().ToArray(); Vector3[] local = mesh.vertices;
                    var pane = new Pane { Transform = filter.transform, Surface = surface, Part = part, Triangles = indices.Length / 3,
                        Local = used.Select(i => local[i]).ToArray(), Rest = used.Select(i => sourceVertices[surface][i]).ToArray() };
                    for (int i = 0; i < used.Length; i++)
                        Require(Vector3.Distance(instance.Root.InverseTransformPoint(pane.Transform.TransformPoint(pane.Local[i])), pane.Rest[i]) < Tolerance,
                            "rest glass differs from the imported model surface");
                    Require(filter.GetComponent<MeshRenderer>()?.sharedMaterial?.shader?.name == "BH/GlsShader", "glass renderer does not use BH/GlsShader");
                    result.Add(pane);
                }
                Require(expected.Count == 0, "one or more imported glass groups are missing");
                Require(result.Any(p => p.Part == -1), "static windscreen/side glass group missing");
                var profile = VehicleModels.Find(spec.ModelVariant) ?? GenericBusReview.Profile(spec);
                for (int door = 0; door < spec.Doors; door++)
                    for (int leaf = 0; leaf < profile.DoorLeaves[door]; leaf++)
                        Require(result.Any(p => p.Part == profile.DoorPart(door, leaf) && p.Triangles > 0), "door leaf has no imported glass");
                return result;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try { meshes.Dispose(); }
                catch (Exception cleanup)
                {
                    if (failure != null) throw new AggregateException("Glass inspection and native mesh release failed.", failure, cleanup);
                    throw;
                }
            }
        }

        static void CheckDiagnosticPoses(CabInstance instance, KitModelData data, List<Pane> panes, BusInteriorSpec spec,
            BusInterior.Pose rest, string name, int lod, List<object> checks)
        {
            foreach (float opening in new[] { 0f, .5f, 1f })
            {
                var pose = rest; pose.DoorOpen = new float3(opening); pose.FourthDoorOpen = opening;
                ApplyAndCheck(instance, data, panes, spec, pose, name, lod, "inspection-doors-" + opening + "-outside", checks);
                pose.Outside = false;
                ApplyAndCheck(instance, data, panes, spec, pose, name, lod, "inspection-doors-" + opening + "-cab", checks);
            }
            for (int door = 0; door < spec.Doors; door++)
            {
                var pose = rest; pose.DoorOpen = float3.zero; pose.FourthDoorOpen = 0f;
                if (door < 3) pose.DoorOpen[door] = 1f; else pose.FourthDoorOpen = 1f;
                ApplyAndCheck(instance, data, panes, spec, pose, name, lod, "inspection-single-door-" + door, checks);
            }
            foreach (float steer in new[] { -35f, 35f })
            {
                var pose = rest; pose.WheelSteerDeg = steer; pose.SteerDeg = steer * 4f;
                ApplyAndCheck(instance, data, panes, spec, pose, name, lod, "inspection-steer-" + steer, checks);
            }
            foreach (int axis in new[] { 0, 1 })
            {
                var pose = rest; pose.StrapSwing = axis == 0 ? new float2(.25f, 0f) : new float2(0f, .2f);
                ApplyAndCheck(instance, data, panes, spec, pose, name, lod, "inspection-straps-" + axis, checks);
            }
            ApplyAndCheck(instance, data, panes, spec, rest, name, lod, "inspection-restored-closed", checks);
        }

        static void ApplyAndCheck(CabInstance instance, KitModelData data, List<Pane> panes, BusInteriorSpec spec,
            BusInterior.Pose pose, string name, int lod, string state, List<object> checks)
        {
            BusInterior.Apply(instance, pose, spec);
            var expected = new KitPoseBuffer(data.Parts); BusInterior.Apply(expected, pose, spec);
            float transformError = 0f, glassError = 0f;
            var axes = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
            for (int part = 0; part < data.Parts.Length; part++)
            {
                foreach (Vector3 axis in axes)
                {
                    Vector3 actual = instance.Root.InverseTransformPoint(instance.Part(part).TransformPoint(axis));
                    transformError = Mathf.Max(transformError, Vector3.Distance(actual, V(math.transform(expected.PartToModel(part), (float3)axis))));
                }
                MeshRenderer renderer = instance.Part(part).GetComponent<MeshRenderer>();
                if (renderer != null) Require(renderer.enabled == expected.Visible[part], "part visibility differs from production poser: " + part);
            }
            var glassRows = new List<object>();
            var doorProfile = VehicleModels.Find(spec.ModelVariant) ?? GenericBusReview.Profile(spec);
            foreach (Pane pane in panes)
            {
                Require(pane.Transform.parent == (pane.Part < 0 ? instance.Root : instance.Part(pane.Part)), "glass parent changed during posing");
                float movement = 0f;
                for (int i = 0; i < pane.Local.Length; i++)
                {
                    Vector3 actual = instance.Root.InverseTransformPoint(pane.Transform.TransformPoint(pane.Local[i]));
                    Vector3 wanted = pane.Part < 0 ? pane.Rest[i] : V(expected.RestPointToModel(pane.Part, pane.Rest[i]));
                    glassError = Mathf.Max(glassError, Vector3.Distance(actual, wanted));
                    movement = Mathf.Max(movement, Vector3.Distance(actual, pane.Rest[i]));
                }
                if (doorProfile != null && doorProfile.IsDoorPart(pane.Part))
                {
                    int door = pane.Part < BusInterior.PartCount ? (pane.Part - BusInterior.DoorFrontA) / 2 : 3;
                    float opening = door < 3 ? pose.DoorOpen[door] : pose.FourthDoorOpen;
                    Require(opening == 0f ? movement < Tolerance : movement > .05f, "door glass did not follow its requested open/closed state");
                }
                else Require(movement < Tolerance, "static glass moved with an unrelated door");
                glassRows.Add(new { part = pane.Part, surface = pane.Surface, triangles = pane.Triangles, maximum_movement_m = movement });
            }
            Require(transformError < Tolerance && glassError < Tolerance, "Unity part/glass transforms differ from the pure pose reference");
            var wheelAngles = new List<float>();
            for (int part = BusInterior.WheelFL; part <= BusInterior.WheelFR; part++)
            {
                Vector3 forward = instance.Root.InverseTransformDirection(instance.Part(part).forward);
                float angle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                Require(Mathf.Abs(Mathf.DeltaAngle(angle, pose.WheelSteerDeg)) < .1f, "front wheel does not reach the requested steering angle");
                wheelAngles.Add(angle);
            }
            float minSwing = float.MaxValue, maxPivotDrift = 0f;
            var authored = VehicleModels.Find(spec.ModelVariant);
            GenericDoorRig genericRig = authored == null ? GenericDoorRig.For(spec) : null;
            int drives = authored?.DoorDriveCount ?? genericRig.DriveCount;
            int firstDrive = authored?.FirstDoorDrive ?? genericRig.FirstDrive;
            int straps = 0, firstStrap = authored == null ? genericRig.FirstStrap :
                firstDrive + drives + authored.WrapSurfaceCount;
            for (int part = firstDrive; part < firstDrive + drives; part++)
                Require(Vector3.Distance(instance.Root.InverseTransformPoint(instance.Part(part).position), V(data.Parts[part].Pivot)) < Tolerance,
                    "door drive shaft moved off its axis: " + part);
            // (a native-cab model's card readers are a part after the mesh's: not a strap)
            int strapEnd = authored?.Mesh.PartCount ?? data.Parts.Length;
            for (int part = firstStrap; part < strapEnd; part++)
            {
                var definition = data.Parts[part];
                var mesh = definition.Data[0] ?? (definition.MeshOf >= 0 ? data.Parts[definition.MeshOf].Data[0] : null);
                Require(mesh != null && mesh.P.Length > 0, "hand strap has no geometry at selected LOD: " + part);
                float3 tip = mesh.P.OrderByDescending(p => math.lengthsq(p)).First();
                Vector3 restTip = V(math.transform(new float4x4(definition.Rot, definition.Pivot), tip));
                Vector3 actualTip = instance.Root.InverseTransformPoint(instance.Part(part).TransformPoint(V(tip)));
                minSwing = Mathf.Min(minSwing, Vector3.Distance(restTip, actualTip));
                maxPivotDrift = Mathf.Max(maxPivotDrift, Vector3.Distance(instance.Root.InverseTransformPoint(instance.Part(part).position), V(definition.Pivot)));
                straps++;
            }
            Require(maxPivotDrift < Tolerance, "hand strap suspension point moved");
            if (math.lengthsq(pose.StrapSwing) > 0) Require(minSwing > .005f, "one or more hand straps did not swing");
            checks.Add(new { name, lod, state, outside = pose.Outside, doors = A(pose.DoorOpen), requested_steer_deg = pose.WheelSteerDeg,
                actual_front_wheel_degrees = wheelAngles, strap_swing_radians = new[] { pose.StrapSwing.x, pose.StrapSwing.y },
                strap_count = straps, minimum_strap_tip_motion_m = minSwing, maximum_strap_pivot_drift_m = maxPivotDrift,
                maximum_part_transform_error_m = transformError, maximum_glass_error_m = glassError, glass = glassRows });
        }

        internal static async Task CaptureReviewed(Camera camera, RenderTexture target, string directory, string model, int lod,
            string state, Vector3 eye, Vector3 lookAt, float fieldOfView, List<object> shots, Action afterLastRender = null)
        {
            bool outside = state.Contains("outside") || state.StartsWith("steer-") || state.EndsWith("door-only");
            float ev = outside ? 13f : 12f;
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
            hd.hasPersistentHistory = true;
            var settings = new Dictionary<string, object> { ["requested_ev100"] = ev, ["warmup_frames"] = 64,
                ["history_reset"] = true, ["lighting_scope"] = "Dedicated inspection lights; not road-scene lighting" };
            using (var exposure = new BusVehicleMaterialDiagnostics.PrivateExposure(camera, Origin, ev))
                await Capture(camera, target, directory, model, lod, state, eye, lookAt, fieldOfView, shots, 64, true,
                    () => {
                        var cameraData = HDCamera.GetOrCreate(camera);
                        float actual = cameraData.volumeStack.GetComponent<Exposure>().fixedExposure.value;
                        bool aa = cameraData.frameSettings.IsEnabled(FrameSettingsField.Antialiasing);
                        settings["actual_ev100"] = actual; settings["antialiasing_enabled"] = aa;
                        settings["taa_frame_index"] = typeof(HDCamera).GetField("taaFrameIndex", System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(cameraData);
                        settings["taa_jitter"] = new[] { cameraData.taaJitter.x, cameraData.taaJitter.y, cameraData.taaJitter.z, cameraData.taaJitter.w };
                        settings["transparent_prepass_frame_setting"] = cameraData.frameSettings.IsEnabled(FrameSettingsField.TransparentPrepass);
                        Require(Mathf.Abs(actual - ev) < .001f && aa, "Inspection exposure or antialiasing did not take effect");
                        afterLastRender?.Invoke();
                    }, captureSettings: settings);
        }

        internal static async Task Capture(Camera camera, RenderTexture target, string directory, string model, int lod,
            string state, Vector3 eye, Vector3 lookAt, float fieldOfView, List<object> shots, int warmupFrames = 8, bool resetHistory = false, Action afterLastRender = null, Action<int> afterRender = null, Vector3? originOverride = null, object captureSettings = null)
        {
            BusVehicleAssetSystem.RequireTestMenu();
            Vector3 captureOrigin = originOverride ?? Origin;
            camera.transform.position = captureOrigin + eye; camera.transform.LookAt(captureOrigin + lookAt); camera.fieldOfView = fieldOfView;
            if (resetHistory) HDCamera.GetOrCreate(camera).Reset();
            for (int i = 0; i < warmupFrames; i++)
            {
                BusVehicleAssetSystem.RequireTestMenu();
                camera.Render();
                afterRender?.Invoke(i);
                if (i == warmupFrames - 1) afterLastRender?.Invoke();
                await Task.Delay(15);
                BusVehicleAssetSystem.RequireTestMenu();
            }
            Color32[] pixels = BusGlassProbe.Read(target);
            long sum = 0, squared = 0; int low = 255, high = 0;
            foreach (Color32 p in pixels) { int y = (p.r * 54 + p.g * 183 + p.b * 19) >> 8; sum += y; squared += y * y; low = Math.Min(low, y); high = Math.Max(high, y); }
            double mean = sum / (double)pixels.Length, variance = squared / (double)pixels.Length - mean * mean;
            bool contrastPassed = high - low >= 12 && variance >= 6;
            string filename = model + "-lod" + lod + "-" + state + ".png";
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try { image.SetPixels32(pixels); image.Apply(); File.WriteAllBytes(Path.Combine(directory, filename), image.EncodeToPNG()); }
            finally { UnityEngine.Object.Destroy(image); }
            shots.Add(new { filename, model, lod, state, camera_model_m = A(eye), look_at_model_m = A(lookAt),
                vertical_fov_degrees = fieldOfView, mean_luminance = mean, luminance_variance = variance,
                contrast_check_passed = contrastPassed, capture_settings = captureSettings, manual_review = "pending" });
            Require(contrastPassed, "capture is blank or lacks usable image contrast (image saved): " + state);
        }

        static Action MakeLight(string name, UnityEngine.LightType type, Vector3 position, float intensity, List<HDAdditionalLightData> lights)
        {
            var obj = new GameObject("CDLC vehicle probe " + name) { hideFlags = HideFlags.DontSave };
            obj.transform.position = position; obj.transform.rotation = Quaternion.Euler(48f, 25f, 0f);
            Light light = obj.AddComponent<Light>(); light.type = type; light.range = 13f; light.shadows = LightShadows.None;
            var hd = obj.AddComponent<HDAdditionalLightData>();
            hd.SetIntensity(intensity, type == UnityEngine.LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
            lights.Add(hd);
            return () => UnityEngine.Object.Destroy(obj);
        }

        internal static async Task CheckTargetAssets(PrefabBase prefab, string name, List<object> checks)
        {
            BusVehicleAssetSystem.RequireTestMenu();
            Require(prefab is ObjectGeometryPrefab, "vehicle prefab not loaded: " + name);
            var geometry = (ObjectGeometryPrefab)prefab;
            Require(geometry.m_Meshes != null && geometry.m_Meshes.Length > 0 && geometry.m_Meshes[0].m_Mesh is RenderPrefab,
                "vehicle has no native render mesh: " + name);
            var root = (RenderPrefab)geometry.m_Meshes[0].m_Mesh;
            var levels = new List<RenderPrefab> { root };
            if (root.TryGet(out LodProperties properties) && properties.m_LodMeshes != null) levels.AddRange(properties.m_LodMeshes);
            Require(levels.Count == 3 && levels.All(level => level != null && level.geometryAsset != null),
                "vehicle requires three native geometry LODs: " + name);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (levels.Any(level => level.geometryAsset.loading.m_AsyncLoadingStarted || level.geometryAsset.loading.m_AsyncLoadingScheduled))
            {
                Require(watch.Elapsed.TotalSeconds < 300, "target vehicle geometry did not finish streaming: " + name);
                await Task.Delay(500);
                BusVehicleAssetSystem.RequireTestMenu();
            }
            BusVehicleAssetSystem.RequireTestMenu();
            for (int lod = 0; lod < levels.Count; lod++)
            {
                RenderPrefab level = levels[lod];
                var meshes = new BusNativeMeshLease(level);
                var materials = new List<NativeMaterialLease>();
                Exception failure = null;
                try
                {
                    for (int surfaceIndex = 0; surfaceIndex < level.materialCount; surfaceIndex++)
                    {
                        var surface = level.GetSurfaceAsset(surfaceIndex);
                        Require(surface != null, "target surface is missing: " + name + " LOD" + lod + " surface " + surfaceIndex);
                        // Cached SurfaceAsset data can preserve VT mode even when a later caller requests NoVT.
                        bool cachedVT = (surface.isDataLoaded || surface.isObjectLoaded) && surface.isCurrentlyUsingVT;
                        Require(!cachedVT, "target surface is cached in VT mode; NoVT inspection cannot reuse it: " + surface.id);
                        Mesh mesh = meshes.Get(surfaceIndex, out int submesh);
                        Require(mesh != null && mesh.isReadable && mesh.vertexCount > 0 && submesh >= 0 &&
                            submesh < mesh.subMeshCount && mesh.GetIndexCount(submesh) >= 3,
                            "target native mesh is unreadable or empty: " + name + " LOD" + lod + " surface " + surfaceIndex);
                        var materialLease = NativeMaterialLease.Acquire(surface);
                        materials.Add(materialLease);
                        Material material = materialLease.Material;
                        Require(material != null && material.shader != null && material.shader.isSupported && material.shader.name.StartsWith("BH/", StringComparison.Ordinal) &&
                            !material.IsKeywordEnabled("ENABLE_VT"), "target material is not usable by NoVT inspection: " + surface.id);
                        var textures = new List<object>();
                        string[] slots = material.shader.name == "BH/GlsShader" && material.HasProperty("_NormalMap") ?
                            new[] { "_BaseColorMap", "_MaskMap", "_NormalMap" } : new[] { "_BaseColorMap", "_MaskMap" };
                        foreach (string slot in slots)
                        {
                            Texture texture = material.HasProperty(slot) ? material.GetTexture(slot) : null;
                            Require(texture != null && texture.width > 0 && texture.height > 0,
                                "target material texture is missing: " + surface.id + " " + slot);
                            textures.Add(new { slot, texture.name, texture.width, texture.height });
                        }
                        checks.Add(new { name, lod, surface = surface.id.ToString(), shader = material.shader.name,
                            use_vt = false, triangles = mesh.GetIndexCount(submesh) / 3, textures });
                    }
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    var errors = new List<Exception>();
                    for (int i = materials.Count - 1; i >= 0; i--) CabCleanup.Try(materials[i].Dispose, errors);
                    CabCleanup.Try(meshes.Dispose, errors);
                    if (errors.Count != 0)
                    {
                        if (failure != null) errors.Insert(0, failure);
                        throw new AggregateException("Target asset preflight cleanup failed.", errors);
                    }
                }
            }
        }

        // the pack's runtime mesh file and the bus's package, as deployed
        static string MeshHash(VehicleProfile profile)
        {
            using (Stream stream = GenericBusReview.Open(profile)) return GenericBusReview.Hash(stream);
        }
        static string PackageHash(PrefabBase prefab)
        {
            string path = prefab.asset?.path;
            Require(!string.IsNullOrEmpty(path) && File.Exists(path), "the package of " + prefab.name + " is not a file: " + path);
            return FileHash(path);
        }
        static string FileHash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream)); }
        static string Hex(byte[] data) => BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static Vector3 V(float3 value) => new Vector3(value.x, value.y, value.z);
        static float[] A(float3 value) => new[] { value.x, value.y, value.z };
        static float[] A(Vector3 value) => new[] { value.x, value.y, value.z };
    }
}
