using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CDLC.Asset.Articulation;
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
    /// <summary>Explicit two-section main-menu fixture. It exercises the production uploads,
    /// native renderer, glass and joint, but never represents an AI road/driving test.</summary>
    internal static class BusVehicleAssemblyProbe
    {
        static readonly Vector3 Origin = new Vector3(0, 3000, 0);
        static readonly float[] Bends = { 0, -25, 25, -50, 50 };
        static readonly float[] Openings = { 0, .5f, 1 };
        sealed class Pane
        {
            internal Transform Transform;
            internal Vector3 Sample, PartLocal;
            internal int Part;
        }

        internal static async Task<string> Run(Func<string, PrefabBase> find, string name,
            Func<string, string> verifyLoaded, string directory = null, string sourceHash = null)
        {
            directory = directory ?? Path.Combine(Mod.ModDirectory, "assembly-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(directory);
            var shots = new List<object>(); var checks = new List<object>(); var materials = new List<object>(); var signChecks = new List<object>();
            var mirrorChecks = new List<object>();
            var targetChecks = new List<object>();
            var cleanup = new List<Action>(); var cleanupErrors = new List<string>();
            var resourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var report = new Dictionary<string, object>
            {
                ["status"] = "running", ["scope"] = "Main-menu explicit assembly fixture, not a spawned AI bus or road driving test",
                ["utc"] = DateTime.UtcNow.ToString("o"), ["source_sha256"] = sourceHash,
                ["matrix"] = new { lods = new[] { 0, 1, 2 }, door_openings = Openings, bends_degrees = Bends },
                ["screenshots"] = shots, ["pose_checks"] = checks, ["materials"] = materials, ["signs"] = signChecks,
                ["mirrors"] = mirrorChecks,
                ["target_asset_checks"] = targetChecks,
                ["native_scope"] = "Actual RenderPrefabRenderer with native buffers and static closed doors; articulated section transforms are fixture poses",
                ["runtime_scope"] = "Production CabModel/CabInstance/BusInterior.Apply, two separately bound BusGlass objects, CDL Asset's JointRenderer and per-LOD CabMirrors optical checks",
                ["glass_lod"] = "Runtime BusGlass uses root LOD0 glass exactly as production; native controls render the selected complete native LOD including its glass",
                ["not_tested"] = new[] { "road AI following/reversing", "boarding and capacity behavior", "enter/release driving", "save/reload", "distance-selected LOD", "day/night road lighting", "mirror traffic reflection" },
                ["manual_review_required"] = true,
            };
            string result = "failed assembly probe did not complete";
            try
            {
                BusVehicleAssetSystem.RequireTestMenu();
                string verified = verifyLoaded?.Invoke(name);
                Require(verified != null && verified.StartsWith("ok ", StringComparison.Ordinal), "Loaded assembly preflight failed: " + verified);
                report["native_preflight"] = verified;
                PrefabBase frontPrefab = find(name);
                VehicleProfile front = VehiclePacks.ForPrefab(frontPrefab);
                VehicleAssembly assembly = front?.Assembly;
                Require(assembly != null && front.Name == assembly.Front.Prefab, "Request must identify the explicit assembly front prefab");
                PrefabBase rearPrefab = find(assembly.Rear.Prefab);
                VehicleProfile rear = VehiclePacks.ForPrefab(rearPrefab);
                Require(rear?.Assembly?.Identity == assembly.Identity && rear.Name == assembly.Rear.Prefab, "Rear prefab is not from the same complete manifest");
                Require(assembly.Joint.MaxBendDegrees >= 50, "Assembly has not declared the requested 50 degree fixture envelope");
                World world = World.DefaultGameObjectInjectionWorld;
                Entity frontEntity = world.GetOrCreateSystemManaged<PrefabSystem>().GetEntity(frontPrefab);
                Require(world.GetOrCreateSystemManaged<ArticulationSpawnSystem>().TryGetVehicle(frontEntity, out ArticulatedVehicle articulated),
                    "CDL Asset found no articulation manifest for the front prefab");
                Require(articulated.JointSha256 == assembly.Joint.Sha256 && articulated.Rear.Prefab == assembly.Rear.Prefab,
                    "The articulation manifest differs from the profile's assembly");
                var preparing = System.Diagnostics.Stopwatch.StartNew();
                while (!articulated.JointReady && articulated.JointFailure == null && preparing.Elapsed.TotalSeconds < 60) await Task.Delay(50);
                Require(articulated.JointReady, "CDL Asset did not prepare the joint: " + (articulated.JointFailure ?? "timeout"));
                report["articulation_manifest"] = new { articulated.Model, articulated.Folder, articulated.JointPath, articulated.JointSha256,
                    prepared_after_s = preparing.Elapsed.TotalSeconds };
                var profiles = new[] { front, rear }; var prefabs = new[] { frontPrefab, rearPrefab };
                for (int i = 0; i < prefabs.Length; i++)
                {
                    await BusVehicleProbe.CheckTargetAssets(prefabs[i], profiles[i].Name, targetChecks);
                }
                var bindings = new[] { assembly.Front, assembly.Rear };
                var data = new KitModelData[2]; var renders = new RenderPrefab[2];
                var bindingRows = new List<object>();
                for (int i = 0; i < 2; i++)
                {
                    Require(prefabs[i] is ObjectGeometryPrefab geometry && geometry.m_Meshes?.Length == 1 &&
                        geometry.m_Meshes[0].m_Mesh is RenderPrefab, "Missing native section geometry");
                    renders[i] = (RenderPrefab)((ObjectGeometryPrefab)prefabs[i]).m_Meshes[0].m_Mesh;
                    VehicleProfile profile = profiles[i];
                    for (int nativeLod = 0; nativeLod < 3; nativeLod++)
                        materials.Add(new { section = bindings[i].Id, lod = nativeLod, native_material_preflight = NativeMaterials(renders[i], nativeLod, BusVehicleAssetSystem.HasSharedGlass(name)) });
                    resourceHashes[profile.MeshPath] = Hash(profile.MeshPath);
                    resourceHashes[prefabs[i].asset.path] = Hash(prefabs[i].asset.path);
                    data[i] = await Task.Run(() => KitBuild.BuildAll(new BusInterior(profile.Spec)));
                    BusVehicleAssetSystem.RequireTestMenu();
                    Require(data[i].Error == null, "Section mesh build failed: " + data[i].Error);
                    bindingRows.Add(new { id = bindings[i].Id, prefab = profile.Name, resource = profile.MeshPath,
                        resource_sha256 = Hash(profile.MeshPath), native_package = prefabs[i].asset?.path,
                        package_sha256 = Hash(prefabs[i].asset?.path), tris = data[i].Tris,
                        rest_position = A(V(bindings[i].RestPosition)), rest_rotation = A(Q(bindings[i].RestRotation)),
                        native_passenger_points = VehicleBoarding.Points(profile).Select(p => new { door = p.Door,
                            operation = p.Enter ? "Enter" : "Exit", position = A(V(p.Position)), rotation = A(Q(p.Rotation)) }).ToArray(),
                        axles = profile.Axles.Select(a => new { a.Z, a.Steer, a.LeftPart, a.RightPart }).ToArray() });
                }
                report["sections"] = bindingRows;
                report["manifest_identity"] = assembly.Identity;
                report["joint_sha256"] = Hash(assembly.Joint.MeshPath);
                resourceHashes[assembly.Joint.MeshPath] = Hash(assembly.Joint.MeshPath);
                report["assembly_mvid"] = typeof(BusVehicleAssemblyProbe).Assembly.ManifestModule.ModuleVersionId.ToString("D");
                report["assembly_sha256"] = Hash(Path.Combine(Mod.ModDirectory, "CDLC.BusDriver.dll"));
                var cameraObject = new GameObject("CDLC assembly probe camera") { hideFlags = HideFlags.DontSave };
                cleanup.Add(() => UnityEngine.Object.Destroy(cameraObject));
                Camera camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
                camera.nearClipPlane = .025f; camera.farClipPlane = 120;
                camera.aspect = 1440f / 960f;
                BusGlassProbe.Plain(cameraObject.AddComponent<HDAdditionalCameraData>());
                var target = new RenderTexture(1440, 960, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                camera.targetTexture = target;
                cleanup.Add(() => { camera.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target); });
                var baseLights = new List<HDAdditionalLightData>();
                var frontLighting = new BusVehicleInspectionLighting(Origin, baseLights); cleanup.Add(frontLighting.Dispose);
                var rearLighting = new BusVehicleInspectionLighting(Origin + V(assembly.Rear.RestPosition), baseLights); cleanup.Add(rearLighting.Dispose);
                report["lighting"] = new[] { frontLighting.Evidence(), rearLighting.Evidence() };
                for (int lod = 0; lod < 3; lod++)
                {
                    var localCleanup = new List<Action>();
                    try
                    {
                        var models = new CabModel[2]; var instances = new CabInstance[2]; var glasses = new BusGlass[2];
                        var panes = new List<Pane>[2]; var native = new BusNativeRendererControl[2];
                        var signs = new BusVehicleSignProbe[2];
                        for (int i = 0; i < 2; i++)
                        {
                            models[i] = new CabModel(BusVehicleProbe.SelectLod(data[i], lod)); localCleanup.Add(models[i].Destroy);
                            instances[i] = new CabInstance(models[i], "CDLC assembly " + bindings[i].Id); localCleanup.Add(instances[i].Destroy);
                            instances[i].OverrideMaterial(BusInterior.Livery, CabMaterials.Livery(new Color32(
                                (byte)profiles[i].DefaultBodyColor.x, (byte)profiles[i].DefaultBodyColor.y, (byte)profiles[i].DefaultBodyColor.z, 255)));
                            glasses[i] = new BusGlass(); localCleanup.Add(glasses[i].Dispose);
                            Require(glasses[i].Build(prefabs[i], instances[i].Root, 0, BusCabSystem.DoorLeafOf(profiles[i].Spec), instances[i].Part), "Section glass failed");
                            panes[i] = GlassBindings(instances[i], profiles[i]);
                            signs[i] = new BusVehicleSignProbe(instances[i].Root, profiles[i].Spec); localCleanup.Add(signs[i].Dispose);
                            signChecks.Add(signs[i].Evidence(profiles[i].Name, lod));
                            native[i] = new BusNativeRendererControl(renders[i], Origin, lod); localCleanup.Add(native[i].Dispose);
                            await native[i].WaitForUpdate(); BusVehicleAssetSystem.RequireTestMenu();
                            materials.Add(new { section = bindings[i].Id, lod, native = native[i].Evidence(), runtime = MaterialEvidence(instances[i]) });
                            native[i].Root.SetActive(false);
                        }
                        var joint = new JointRenderer(articulated); localCleanup.Add(joint.Dispose);
                        foreach (float bend in Bends)
                        {
                            Vector3 fp = Origin + V(assembly.Front.RestPosition); Quaternion fr = Q(assembly.Front.RestRotation);
                            Quaternion rr = Quaternion.AngleAxis(bend, Vector3.up) * Q(assembly.Rear.RestRotation);
                            Vector3 anchor = fp + fr * V(assembly.FrontAnchor), rp = anchor - rr * V(assembly.RearAnchor);
                            Vector3[] positions = { fp, rp }; Quaternion[] rotations = { fr, rr };
                            ExteriorView(renders, positions, rotations, camera.aspect, out Vector3 outsideEye, out Vector3 outsideLook);
                            Require(Vector3.Distance(anchor, rp + rr * V(assembly.RearAnchor)) < .001f, "Fixture anchors separated");
                            Require(joint.Update(fp, fr, rp, rr, lod, true), "Joint rejected the explicitly anchored pose");
                            CheckJoint(joint, assembly, positions, rotations, lod, bend, checks);
                            foreach (float opening in Openings)
                            {
                                for (int i = 0; i < 2; i++)
                                {
                                    instances[i].Visible = true;
                                    var pose = new BusInterior.Pose { Position = F(positions[i]), Rotation = F(rotations[i]), Kneel = quaternion.identity,
                                        Outside = true, DoorOpen = new float3(opening), WheelSteerDeg = math.sign(bend) * 25,
                                        WheelRoll = .3f, SteerDeg = bend, SpeedKmh = 20, Rpm = 1000,
                                        Lights = new KitLights { Lamps = true }, Lamps = BusInterior.ExteriorLamps.Head | BusInterior.ExteriorLamps.Tail };
                                    BusInterior.Apply(instances[i], pose, profiles[i].Spec);
                                    CheckPose(instances[i], profiles[i], panes[i], positions[i], rotations[i], lod, bend, opening, checks);
                                }
                                string state = "bend" + bend.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture) + "-door" + opening.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                                foreach (var sign in signs) sign.SetVisible(true, state + "-outside");
                                await BusVehicleProbe.CaptureReviewed(camera, target, directory, assembly.Model, lod, state + "-runtime-outside",
                                    outsideEye, outsideLook, 42, shots);
                                foreach (var sign in signs) sign.SetVisible(false, state + "-inside");
                                Vector3 eye = anchor - Origin + fr * new Vector3(0, profiles[0].Spec.FloorY + 1.4f - assembly.FrontAnchor.y, 1.6f);
                                Vector3 look = rp - Origin + rr * new Vector3(0, profiles[1].Spec.FloorY + 1.2f, profiles[1].Spec.FrontZ - 2);
                                await BusVehicleProbe.CaptureReviewed(camera, target, directory, assembly.Model, lod, state + "-runtime-gangway", eye, look, 86, shots);
                                if (bend == 0)
                                {
                                    Vector3 driverEye = fp - Origin + fr * V(BusInterior.Eye(front.Spec));
                                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, assembly.Model, lod, state + "-runtime-driver",
                                        driverEye, driverEye + fr * new Vector3(0, -.15f, 2), 88, shots);
                                    for (int i = 0; i < 2; i++)
                                    {
                                        var p = profiles[i].Spec;
                                        Vector3 cabinEye = positions[i] - Origin + rotations[i] * new Vector3(0, p.FloorY + 1.4f, p.FrontZ - 1.2f);
                                        Vector3 cabinLook = positions[i] - Origin + rotations[i] * new Vector3(0, p.FloorY + 1.2f, p.RearZ + .8f);
                                        await BusVehicleProbe.CaptureReviewed(camera, target, directory, assembly.Model, lod, state + "-runtime-" + bindings[i].Id + "-aisle", cabinEye, cabinLook, 85, shots);
                                        if (opening == 0)
                                        {
                                            float lowZ = p.RearZ + 1;
                                            float localFloor = FloorHeight(data[i], lod, lowZ, p.FloorY);
                                            Vector3 lowEye = positions[i] - Origin + rotations[i] * new Vector3(0, localFloor + .5f, lowZ);
                                            Vector3 lowLook = positions[i] - Origin + rotations[i] * new Vector3(0, p.FloorY + .7f, p.FrontZ - .6f);
                                            await BusVehicleProbe.CaptureReviewed(camera, target, directory, assembly.Model, lod,
                                                state + "-runtime-" + bindings[i].Id + "-low-forward", lowEye, lowLook, 85, shots);
                                        }
                                    }
                                }
                            }
                            for (int i = 0; i < 2; i++)
                            {
                                instances[i].Visible = false;
                                native[i].Root.transform.SetPositionAndRotation(positions[i], rotations[i]); native[i].Root.SetActive(true);
                            }
                            await Task.Delay(50); BusVehicleAssetSystem.RequireTestMenu();
                            await BusVehicleProbe.CaptureReviewed(camera, target, directory, assembly.Model, lod, "bend" + bend + "-native-closed-outside",
                                outsideEye, outsideLook, 42, shots);
                            for (int i = 0; i < 2; i++) native[i].Root.SetActive(false);
                        }
                        // Optical acceptance uses the same complete two-section fixture.
                        // Return from the final bent/open exterior pose before creating
                        // the production mirror cameras. Keep the rear and joint present.
                        var closedPositions = bindings.Select(b => Origin + V(b.RestPosition)).ToArray();
                        var closedRotations = bindings.Select(b => Q(b.RestRotation)).ToArray();
                        for (int i = 0; i < 2; i++)
                        {
                            native[i].Root.SetActive(false);
                            instances[i].Visible = true;
                            signs[i].SetVisible(false, "mirrors-closed-straight-cab");
                            var pose = new BusInterior.Pose {
                                Position = F(closedPositions[i]), Rotation = F(closedRotations[i]),
                                Kneel = quaternion.identity, Outside = false, DoorOpen = float3.zero,
                                WheelSteerDeg = 0, WheelRoll = .3f, SteerDeg = 0,
                                Lights = new KitLights { Lamps = true }
                            };
                            BusInterior.Apply(instances[i], pose, profiles[i].Spec);
                            CheckPose(instances[i], profiles[i], panes[i], closedPositions[i], closedRotations[i], lod, 0, 0, checks);
                        }
                        Require(joint.Update(closedPositions[0], closedRotations[0], closedPositions[1], closedRotations[1], lod, true),
                            "Closed assembly joint failed before mirror acceptance");
                        CheckJoint(joint, assembly, closedPositions, closedRotations, lod, 0, checks);
                        var mirrorBindings = new Dictionary<string, object> {
                            ["scope"] = "Complete straight, closed two-section production fixture; both BusGlass objects and the joint retained",
                            ["source_sha256"] = sourceHash, ["manifest_identity"] = assembly.Identity,
                            ["assembly_sha256"] = report["assembly_sha256"], ["assembly_mvid"] = report["assembly_mvid"],
                            ["joint_sha256"] = Hash(assembly.Joint.MeshPath), ["lod"] = lod,
                            ["sections"] = bindingRows, ["body_and_joint_state_before"] = MirrorAssemblyState(instances, joint)
                        };
                        var mirrorReport = await BusVehicleMirrorProbe.Run(instances[0], front.Spec,
                            Path.Combine(directory, "mirrors", "lod" + lod), lod, 58f, mirrorBindings);
                        mirrorChecks.Add(mirrorReport);
                        BusVehicleAssetSystem.RequireTestMenu();
                        for (int i = 0; i < 2; i++)
                            CheckPose(instances[i], profiles[i], panes[i], closedPositions[i], closedRotations[i], lod, 0, 0, checks);
                        CheckJoint(joint, assembly, closedPositions, closedRotations, lod, 0, checks);
                        mirrorBindings["body_and_joint_state_after"] = MirrorAssemblyState(instances, joint);
                        Require((bool)mirrorReport["automatic_passed"],
                            "Production assembly mirror diagnostic failed; inspect the per-LOD optical report and images");
                    }
                    finally { for (int i = localCleanup.Count - 1; i >= 0; i--) TryCleanup(localCleanup[i], cleanupErrors); }
                }
                foreach (var resource in resourceHashes)
                    Require(Hash(resource.Key) == resource.Value, "Bound resource changed during the assembly fixture: " + resource.Key);
                report["resource_hashes"] = resourceHashes;
                report["status"] = "automatic fixture checks passed; manual image review required";
                result = "ok assembly supplemental probe: " + shots.Count + " screenshots; manual review required; " + directory;
            }
            catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); result = "failed assembly probe: " + error.Message + "; " + directory; }
            finally
            {
                for (int i = cleanup.Count - 1; i >= 0; i--) TryCleanup(cleanup[i], cleanupErrors);
                int frame = UnityEngine.Time.frameCount;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (UnityEngine.Time.frameCount - frame < 3 && timer.Elapsed.TotalSeconds < 10) await Task.Delay(20);
                if (UnityEngine.Time.frameCount - frame < 3) cleanupErrors.Add("Deferred cleanup did not complete three game frames");
                report["cleanup"] = new { passed = cleanupErrors.Count == 0, errors = cleanupErrors };
                if (cleanupErrors.Count > 0) { report["status"] = "failed"; result = "failed assembly probe cleanup; " + directory; }
                File.WriteAllText(Path.Combine(directory, "report.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            return result;
        }

        static object MirrorAssemblyState(CabInstance[] instances, JointRenderer joint)
        {
            Require(instances.All(instance => instance.Alive && instance.Visible), "A section disappeared during mirror acceptance");
            Require(instances.All(instance => instance.Root.GetComponentsInChildren<MeshRenderer>(true)
                .Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy &&
                    renderer.name.StartsWith("Glass ", StringComparison.Ordinal))), "A section lost its complete glass during mirror acceptance");
            var jointRenderer = joint.Root?.GetComponent<MeshRenderer>();
            Require(joint.CurrentMesh != null && joint.CurrentMesh.vertexCount > 0 && jointRenderer != null &&
                jointRenderer.enabled && jointRenderer.gameObject.activeInHierarchy, "Joint is not visible during mirror acceptance");
            return new {
                sections = instances.Select(instance => new {
                    instance.Root.name, position = A(instance.Root.position), rotation = A(instance.Root.rotation),
                    visible_renderers = instance.Root.GetComponentsInChildren<MeshRenderer>(true)
                        .Count(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy),
                    visible_glass = instance.Root.GetComponentsInChildren<MeshRenderer>(true)
                        .Count(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy &&
                            renderer.name.StartsWith("Glass ", StringComparison.Ordinal))
                }).ToArray(), joint_vertices = joint.CurrentMesh.vertexCount, joint_visible = true
            };
        }

        // Fit every transformed native bound with an explicit margin, using the same camera
        // for native/runtime and every door state. A joint-centered fixed distance cropped
        // the longer front section and was not a valid whole-vehicle inspection view.
        static void ExteriorView(RenderPrefab[] renders, Vector3[] positions, Quaternion[] rotations,
            float aspect, out Vector3 eye, out Vector3 look)
        {
            var corners = new List<Vector3>();
            for (int i = 0; i < renders.Length; i++)
            {
                var bounds = renders[i].bounds;
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                    corners.Add(positions[i] - Origin + rotations[i] * new Vector3(
                        x == 0 ? bounds.min.x : bounds.max.x,
                        y == 0 ? bounds.min.y : bounds.max.y,
                        z == 0 ? bounds.min.z : bounds.max.z));
            }
            Vector3 low = corners.Aggregate(Vector3.Min), high = corners.Aggregate(Vector3.Max);
            look = (low + high) * .5f;
            Vector3 away = new Vector3(1, .38f, 1).normalized, forward = -away;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized, up = Vector3.Cross(forward, right);
            float vertical = Mathf.Tan(21f * Mathf.Deg2Rad) * .82f, horizontal = vertical * aspect;
            float distance = 1;
            foreach (Vector3 corner in corners)
            {
                Vector3 delta = corner - look;
                float depth = Vector3.Dot(delta, forward);
                distance = Mathf.Max(distance, Mathf.Abs(Vector3.Dot(delta, right)) / horizontal - depth,
                    Mathf.Abs(Vector3.Dot(delta, up)) / vertical - depth);
            }
            eye = look + away * (distance + .8f);
        }

        static float FloorHeight(KitModelData model, int lod, float z, float baseFloor)
        {
            KitPartDef shell = model.Parts[BusInterior.Shell];
            KitMeshData mesh = shell.Data[lod];
            if (mesh == null) throw new InvalidOperationException("Low-view floor has no shell mesh");
            var origin = new float3(0, baseFloor + 1.1f, z);
            float best = float.PositiveInfinity;
            for (int i = 0; i < mesh.Idx.Length; i += 3)
            {
                float3 a = shell.Pivot + math.rotate(shell.Rot, mesh.P[mesh.Idx[i]]);
                float3 b = shell.Pivot + math.rotate(shell.Rot, mesh.P[mesh.Idx[i+1]]);
                float3 c = shell.Pivot + math.rotate(shell.Rot, mesh.P[mesh.Idx[i+2]]);
                float3 e1 = b-a, e2 = c-a, normal = math.cross(e1,e2);
                if (normal.y <= .8f * math.length(normal)) continue;
                float3 direction = new float3(0,-1,0), h = math.cross(direction,e2);
                float determinant = math.dot(e1,h);
                if (math.abs(determinant) < 1e-10f) continue;
                float3 delta = origin-a;
                float u = math.dot(delta,h)/determinant;
                float3 q = math.cross(delta,e1);
                float v = math.dot(direction,q)/determinant, distance = math.dot(e2,q)/determinant;
                if (u >= -.00001f && v >= -.00001f && u+v <= 1.00001f && distance >= 0 && distance < best)
                    best = distance;
            }
            if (!math.isfinite(best)) throw new InvalidOperationException("No upward floor under the low-view aisle camera");
            return origin.y-best;
        }

        static List<Pane> GlassBindings(CabInstance instance, VehicleProfile profile)
        {
            var result = new List<Pane>();
            Func<Vector3, int> partOf = BusCabSystem.DoorLeafOf(profile.Spec);
            foreach (MeshFilter filter in instance.Root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.name.StartsWith("Glass ", StringComparison.Ordinal) || filter.sharedMesh == null) continue;
                int[] triangles = filter.sharedMesh.GetTriangles(0); Vector3[] vertices = filter.sharedMesh.vertices;
                Require(triangles.Length >= 3, "Empty glass pane");
                Vector3 point = (vertices[triangles[0]] + vertices[triangles[1]] + vertices[triangles[2]]) / 3;
                Vector3 modelPoint = instance.Root.InverseTransformPoint(filter.transform.TransformPoint(point));
                int part = partOf(modelPoint); Transform parent = part >= 0 ? instance.Part(part) : instance.Root;
                Require(filter.transform.parent == parent, "Glass assigned to the wrong door/root transform");
                result.Add(new Pane { Transform = filter.transform, Sample = point, Part = part,
                    PartLocal = parent.InverseTransformPoint(filter.transform.TransformPoint(point)) });
            }
            Require(result.Count > 0, "No glass panes retained");
            return result;
        }

        static void CheckPose(CabInstance instance, VehicleProfile profile, List<Pane> panes, Vector3 position, Quaternion rotation,
            int lod, float bend, float opening, List<object> checks)
        {
            float error = 0;
            foreach (Pane pane in panes)
            {
                Transform parent = pane.Part >= 0 ? instance.Part(pane.Part) : instance.Root;
                error = Mathf.Max(error, Vector3.Distance(pane.Transform.TransformPoint(pane.Sample), parent.TransformPoint(pane.PartLocal)));
            }
            Require(error <= .002f, "Posed glass detached from its own section/door");
            float angleError = 0;
            for (int door = 0; door < profile.Doors.Length; door++)
                for (int leaf = 0; leaf < 2; leaf++)
                {
                    if (!profile.HasDoorLeaf(door, leaf != 0)) continue;
                    int part = profile.DoorPart(door, leaf);
                    DoorLinkage[] linkage = profile.DoorLinkages != null && door < profile.DoorLinkages.Length ? profile.DoorLinkages[door] : null;
                    Quaternion expected;
                    if (linkage != null)
                    {
                        // an outward-swing leaf: the shared solver's turn, in its parent's rest frame (BusInterior.PoseLinkedLeaf)
                        Require(linkage[leaf].Solve(opening, out _, out _, out float yaw, out _), "A door linkage has no solution");
                        KitPartDef def = instance.Def(part);
                        quaternion parent = def.Parent >= 0 ? instance.Def(def.Parent).Rot : quaternion.identity;
                        expected = Q(math.mul(math.inverse(parent), math.mul(quaternion.RotateY(yaw), def.Rot)));
                    }
                    else
                    {
                        float sign = (profile.Spec.DriverSide < 0 ? 1 : -1) * (leaf == 0 ? 1 : -1);
                        float degrees = profile.DoorOpenDegrees != null ? profile.DoorOpenDegrees[door] : BusInterior.DoorOpenDeg;
                        expected = Q(instance.Def(part).LocalRot) * Quaternion.AngleAxis(sign * degrees * BusInterior.DoorEase(opening), Vector3.up);
                    }
                    angleError = Mathf.Max(angleError, Quaternion.Angle(expected, instance.Part(part).localRotation));
                }
            Require(angleError < .05f, "A door leaf failed its declared closed/half/open pose");
            float wheelError = 0;
            foreach (var axle in profile.Axles)
                foreach (int part in new[] { axle.LeftPart, axle.RightPart })
                {
                    Require(Vector3.Distance(instance.Part(part).position, position + rotation * V(instance.Def(part).Pivot)) < .002f,
                        "Independent section wheel pivot moved during pose");
                    Quaternion roll = Quaternion.AngleAxis(.3f * Mathf.Rad2Deg, Vector3.right);
                    Quaternion expected = axle.Steer ? Quaternion.AngleAxis(math.sign(bend) * 25, Vector3.up) * roll : roll;
                    wheelError = Mathf.Max(wheelError, Quaternion.Angle(expected, instance.Part(part).localRotation));
                }
            Require(wheelError < .05f, "Declared steering or independent rear rolling pose differs");
            checks.Add(new { section = profile.SectionId, lod, bend, door_opening = opening, glass_panes = panes.Count, maximum_glass_binding_error_m = error,
                maximum_door_angle_error_degrees = angleError, maximum_wheel_angle_error_degrees = wheelError,
                wheel_parts = profile.Axles.SelectMany(a => new[] { a.LeftPart, a.RightPart }).ToArray() });
        }

        static void CheckJoint(JointRenderer joint, VehicleAssembly assembly, Vector3[] positions, Quaternion[] rotations,
            int lod, float bend, List<object> checks)
        {
            joint.RestVertices(lod, out Vector3[] source, out float[] weights); Vector3[] actual = joint.CurrentMesh.vertices;
            var bindings = new[] { assembly.Front, assembly.Rear }; float error = 0; int endpoints = 0;
            for (int i = 0; i < source.Length; i++)
            {
                float weight = weights[i]; if (weight != 0 && weight != 1) continue;
                int side = weight == 0 ? 0 : 1;
                Vector3 local = Quaternion.Inverse(Q(bindings[side].RestRotation)) * (source[i] - V(bindings[side].RestPosition));
                Vector3 expected = positions[side] + rotations[side] * local;
                error = Mathf.Max(error, Vector3.Distance(expected, joint.Root.TransformPoint(actual[i]))); endpoints++;
            }
            Require(endpoints > 0 && error < .002f, "Rendered joint endpoint vertices separated from their section");
            checks.Add(new { lod, bend, joint_endpoint_vertices = endpoints, maximum_joint_endpoint_error_m = error });
        }

        static object MaterialEvidence(CabInstance instance)
        {
            var rows = new List<object>();
            for (int part = 0; part < instance.PartCount; part++)
            {
                if (part == BusInterior.Livery || (part >= BusInterior.WrapLeft && part < BusInterior.PartCount)) continue;
                KitPartDef definition = instance.Def(part);
                var geometry = definition.Data[0] ?? (definition.MeshOf >= 0 ? instance.Def(definition.MeshOf).Data[0] : null);
                MeshRenderer renderer = instance.Part(part)?.GetComponent<MeshRenderer>();
                if (geometry == null || renderer == null) continue;
                Material[] slots = renderer.sharedMaterials;
                Require(slots.Length == geometry.SubFin.Length, "Part finish/material count differs");
                for (int i = 0; i < slots.Length; i++)
                {
                    Fin finish = geometry.SubFin[i]; Material material = slots[i];
                    if (KitPalette.IsLight(finish)) continue;
                    Require(material != null && material.HasProperty("_Metallic") && material.HasProperty("_Smoothness"), "Missing production finish properties");
                    Require(Mathf.Abs(material.GetFloat("_Metallic") - KitPalette.Metallic(finish)) < .0001f &&
                        Mathf.Abs(material.GetFloat("_Smoothness") - KitPalette.Smoothness(finish)) < .0001f,
                        "Runtime finish differs from its declared metal/plastic/textile recipe");
                }
            }
            foreach (var material in instance.Root.GetComponentsInChildren<MeshRenderer>(true).SelectMany(r => r.sharedMaterials).Distinct())
            {
                Require(material != null && material.shader != null, "Runtime or glass material missing");
                float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0;
                float smoothness = material.HasProperty("_Smoothness") ? material.GetFloat("_Smoothness") : 0;
                Require(!float.IsNaN(metallic) && !float.IsNaN(smoothness), "Nonfinite material state");
                rows.Add(new { material.name, shader = material.shader.name, metallic, smoothness, keywords = material.shaderKeywords });
            }
            return rows;
        }
        static object NativeMaterials(RenderPrefab root, int lod, bool strict)
        {
            Require(root.TryGet(out LodProperties properties) && properties.m_LodMeshes?.Length == 2, "Native assembly requires all three LODs");
            RenderPrefab level = lod == 0 ? root : properties.m_LodMeshes[lod - 1];
            Require(level != null, "Missing native section LOD");
            var rows = new List<object>(); int glass = 0;
            for (int i = 0; i < level.materialCount; i++)
            {
                var surface = level.GetSurfaceAsset(i); Material material = level.ObtainMaterial(i, false);
                Require(surface != null && material != null && material.shader != null && material.shader.isSupported &&
                    material.shader.name.StartsWith("BH/", StringComparison.Ordinal) && !material.IsKeywordEnabled("ENABLE_VT"),
                    "Native material is missing or incompatible with NoVT inspection");
                Require(material.HasProperty("_BaseColorMap") && material.GetTexture("_BaseColorMap") != null &&
                    material.HasProperty("_MaskMap") && material.GetTexture("_MaskMap") != null, "Native palette textures missing");
                bool isGlass = surface.name.IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isGlass)
                {
                    glass++; Require(material.shader.name == "BH/GlsShader", "Native glass uses wrong shader");
                    if (strict) Require(material.GetFloat("_ReceivesSSRTransparent") == 0 && material.GetFloat("_TransparentDepthPrepassEnable") == 0 &&
                        !material.GetShaderPassEnabled("TransparentDepthPrepass"), "Native glass compatibility pass differs");
                }
                rows.Add(new { surface = surface.name, material = material.name, shader = material.shader.name,
                    keywords = material.shaderKeywords, transparent_depth_prepass = material.GetShaderPassEnabled("TransparentDepthPrepass") });
            }
            Require(glass == 1, "Native LOD needs exactly one declared glass surface");
            return rows;
        }
        static void TryCleanup(Action action, List<string> errors) { try { action(); } catch (Exception error) { errors.Add(error.ToString()); } }
        static string Hash(string path)
        {
            Require(!string.IsNullOrEmpty(path) && File.Exists(path), "Missing bound resource: " + path);
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        static Vector3 V(float3 v) => new Vector3(v.x, v.y, v.z);
        static Quaternion Q(quaternion q) => new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
        static float3 F(Vector3 v) => new float3(v.x, v.y, v.z);
        static quaternion F(Quaternion q) => new quaternion(q.x, q.y, q.z, q.w);
        static float[] A(Vector3 v) => new[] { v.x, v.y, v.z };
        static float[] A(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    }
}
