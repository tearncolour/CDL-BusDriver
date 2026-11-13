#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDLC.Asset.Runtime;
using CDLC.BusDriver.Interior;
using Colossal.Rendering;
using Game.Rendering;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static partial class BusVehicleRoadProbe
    {
        internal sealed class GlassVisibility : IDisposable
        {
            sealed class Entry
            {
                internal BusGlassOpticsBinding Binding;
                internal World World;
                internal Mesh Mesh;
                internal int Group, Local = -1;
                internal bool Pending;
                internal string Restoration = "not_changed";
                internal BatchRenderFlags OriginalFlags;
                internal bool Driver;
            }

            sealed class IdentityChanged : InvalidOperationException
            {
                internal IdentityChanged(string message) : base(message) { }
            }

            readonly List<Entry> entries = new List<Entry>();
            readonly long generation = RuntimeSession.Generation;
            bool closed;

            internal GlassVisibility(IEnumerable<BusGlassOpticsBinding> bindings)
            {
                Require(RuntimeSession.IsInitialized, "Visibility runtime session is unavailable");
                var renderers = new HashSet<MeshRenderer>();
                var batches = new HashSet<CustomBatch>();
                foreach (BusGlassOpticsBinding binding in bindings)
                {
                    binding.Validate();
                    var entry = new Entry { Binding = binding, Driver = binding.Renderer != null };
                    if (entry.Driver)
                    {
                        Require(renderers.Add(binding.Renderer), "Visibility contains a duplicate driver renderer");
                        entry.Mesh = binding.Renderer.GetComponent<MeshFilter>()?.sharedMesh;
                        Require(entry.Mesh != null && binding.Renderer.enabled, "Visibility driver pane is not initially enabled");
                    }
                    else
                    {
                        Require(binding.Batch != null && batches.Add(binding.Batch), "Visibility contains a missing or duplicate native batch");
                        entry.World = binding.Manager.World; entry.Group = binding.Batch.groupIndex; entry.Mesh = binding.Batch.mesh;
                        entry.OriginalFlags = Read(entry).m_RenderFlags;
                        Require((entry.OriginalFlags & BatchRenderFlags.IsEnabled) != 0, "Visibility native glass is not initially enabled");
                    }
                    entries.Add(entry);
                }
                Require(entries.Count > 0, "Visibility has no exact glass targets");
            }

            internal void Hide()
            {
                if (closed) throw new ObjectDisposedException(nameof(GlassVisibility));
                Verify(false);
                foreach (Entry entry in entries)
                {
                    entry.Pending = true;
                    entry.Restoration = "pending";
                    if (entry.Driver)
                        entry.Binding.Renderer.enabled = false;
                    else
                        Write(entry, false);
                }
                Verify(true);
            }

            internal void Verify(bool absent)
            {
                foreach (Entry entry in entries)
                {
                    ValidateIdentity(entry);
                    bool enabled = entry.Driver ? entry.Binding.Renderer.enabled :
                        (Read(entry).m_RenderFlags & BatchRenderFlags.IsEnabled) != 0;
                    Require(enabled != absent, "Glass visibility differs from the requested state");
                }
            }

            internal object Evidence()
            {
                return entries.Select(entry => new {
                    section = entry.Binding.Section, surface = entry.Binding.Surface.id.ToString(),
                    material_id = entry.Binding.Material.GetInstanceID(), mesh_id = entry.Mesh.GetInstanceID(),
                    renderer_id = entry.Driver ? (int?)entry.Binding.Renderer.GetInstanceID() : null,
                    renderer_enabled = entry.Driver ? (bool?)entry.Binding.Renderer.enabled : null,
                    managed_index = entry.Binding.Batch != null ? (int?)entry.Binding.Index : null,
                    native_group = entry.Binding.Batch != null ? (int?)entry.Group : null,
                    native_local_index = entry.Binding.Batch != null ? (int?)entry.Local : null,
                    original_flags = entry.Binding.Batch != null ? (int?)(byte)entry.OriginalFlags : null,
                    live_flags = entry.Binding.Batch != null ? (int?)(byte)Read(entry).m_RenderFlags : null,
                    generation, pending_restore = entry.Pending, restoration = entry.Restoration
                }).ToArray();
            }

            internal object CleanupEvidence() => entries.Select(entry => new {
                section = entry.Binding.Section, managed_index = entry.Binding.Batch != null ? (int?)entry.Binding.Index : null,
                native_group = entry.Binding.Batch != null ? (int?)entry.Group : null,
                native_local_index = entry.Binding.Batch != null ? (int?)entry.Local : null,
                pending_restore = entry.Pending, restoration = entry.Restoration
            }).ToArray();
            internal bool HasSkippedRestoration => entries.Any(entry => entry.Restoration == "identity_changed_no_write");

            void ValidateIdentity(Entry entry)
            {
                var binding = entry.Binding;
                Identity(RuntimeSession.Generation == generation, "Visibility runtime generation changed");
                Identity(binding.Material != null && entry.Mesh != null, "Visibility material or mesh was destroyed");
                if (entry.Driver)
                    Identity(binding.Renderer != null && binding.Renderer.sharedMaterial == binding.Material && binding.Renderer.gameObject.activeInHierarchy &&
                        binding.Renderer.GetComponent<MeshFilter>()?.sharedMesh == entry.Mesh,
                        "Visibility driver pane identity changed");
                else
                {
                    Identity(entry.World != null && entry.World.IsCreated && ReferenceEquals(binding.Manager.World, entry.World),
                        "Visibility native World changed");
                    var managed = binding.Manager.GetManagedBatches();
                    Identity(binding.Index >= 0 && binding.Index < managed.BatchCount &&
                        ReferenceEquals(managed.GetBatch(binding.Index), binding.Batch) && binding.Batch.groupIndex == entry.Group &&
                        ReferenceEquals(binding.Batch.sourceSurface, binding.Surface) && binding.Batch.material == binding.Material &&
                        binding.Batch.mesh == entry.Mesh, "Visibility native batch identity changed");
                }
            }

            int NativeIndex(Entry entry, NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> native)
            {
                ValidateIdentity(entry);
                Identity(native.IsCreated && entry.Group >= 0 && entry.Group < native.GetGroupCount() && native.IsValidGroup(entry.Group),
                    "Visibility native group disappeared");
                int local = native.GetBatchIndex(entry.Group, entry.Binding.Index);
                Identity(local >= 0 && local < native.GetBatchCount(entry.Group) && native.GetManagedBatchIndex(entry.Group, local) == entry.Binding.Index &&
                    (entry.Local < 0 || entry.Local == local), "Visibility native batch mapping changed");
                Identity(native.GetBatchMaterialID(entry.Group, local).Equals(entry.Binding.Batch.materialID) &&
                    native.GetBatchMeshID(entry.Group, local).Equals(entry.Binding.Batch.meshID),
                    "Visibility native render IDs differ from the exact managed batch");
                if (entry.Local < 0) entry.Local = local;
                return local;
            }

            BatchData Read(Entry entry)
            {
                ValidateIdentity(entry);
                var manager = entry.Binding.Manager;
                var native = manager.GetNativeBatchGroups(true, out var dependencies); dependencies.Complete();
                try { return native.GetBatchData(entry.Group, NativeIndex(entry, native)); }
                finally { manager.AddNativeBatchGroupsReader(default); }
            }

            void Write(Entry entry, bool enabled)
            {
                ValidateIdentity(entry);
                var manager = entry.Binding.Manager;
                var native = manager.GetNativeBatchGroups(false, out var dependencies); dependencies.Complete();
                try
                {
                    int local = NativeIndex(entry, native);
                    BatchData current = native.GetBatchData(entry.Group, local);
                    // IsEnabled is the culling gate; preserve every other live bit and the entire batch payload.
                    if (enabled) current.m_RenderFlags |= BatchRenderFlags.IsEnabled;
                    else current.m_RenderFlags &= ~BatchRenderFlags.IsEnabled;
                    native.SetBatchData(entry.Group, local, current);
                    Require(((native.GetBatchData(entry.Group, local).m_RenderFlags & BatchRenderFlags.IsEnabled) != 0) == enabled,
                        "Visibility native enabled-bit write did not persist");
                }
                finally { manager.AddNativeBatchGroupsWriter(default); }
            }

            internal void Restore()
            {
                var failures = new List<Exception>();
                foreach (Entry entry in entries)
                {
                    if (!entry.Pending) continue;
                    try
                    {
                        if (!SameOwner(entry))
                        {
                            entry.Pending = false; entry.Restoration = "identity_changed_no_write";
                            continue;
                        }
                        bool enabled = entry.Driver ? entry.Binding.Renderer.enabled :
                            (Read(entry).m_RenderFlags & BatchRenderFlags.IsEnabled) != 0;
                        if (!enabled)
                        {
                            if (entry.Driver)
                            {
                                entry.Binding.Renderer.enabled = true;
                                Require(entry.Binding.Renderer.enabled, "Visibility driver enabled restoration did not persist");
                            }
                            else Write(entry, true);
                        }
                        entry.Pending = false; entry.Restoration = enabled ? "already_enabled_no_write" : "restored_own_enabled_bit";
                    }
                    catch (IdentityChanged) { entry.Pending = false; entry.Restoration = "identity_changed_no_write"; }
                    catch (Exception error) { failures.Add(error); }
                }
                CabCleanup.ThrowIfAny(failures, "Glass visibility restoration failed");
            }

            bool SameOwner(Entry entry)
            {
                if (RuntimeSession.Generation != generation || entry.Binding.Material == null || entry.Mesh == null) return false;
                if (entry.Driver)
                    return entry.Binding.Renderer != null && entry.Binding.Renderer.sharedMaterial == entry.Binding.Material &&
                        entry.Binding.Renderer.GetComponent<MeshFilter>()?.sharedMesh == entry.Mesh;
                var binding = entry.Binding;
                if (entry.World == null || !entry.World.IsCreated || !ReferenceEquals(binding.Manager.World, entry.World)) return false;
                var managed = binding.Manager.GetManagedBatches();
                return binding.Index >= 0 && binding.Index < managed.BatchCount && ReferenceEquals(managed.GetBatch(binding.Index), binding.Batch) &&
                    binding.Batch.groupIndex == entry.Group && ReferenceEquals(binding.Batch.sourceSurface, binding.Surface) &&
                    binding.Batch.material == binding.Material && binding.Batch.mesh == entry.Mesh;
            }

            static void Identity(bool valid, string message)
            {
                if (!valid) throw new IdentityChanged(message);
            }

            public void Dispose()
            {
                closed = true;
                try { Restore(); }
                catch { CabCleanup.Retain(this); throw; }
            }
        }

        static async Task<bool> RunGlassVisibility(List<BusGlassOpticsBinding> bindings, Camera camera, int hour, string mode,
            string directory, Action check, Func<bool> owns, Func<Task> settle, Func<string, Task> capture,
            Func<object> sceneEvidence, List<object> results)
        {
            var report = new Dictionary<string, object> { ["hour"] = hour, ["mode"] = mode, ["status"] = "running",
                ["scope"] = "Exact production glass On/Off/On. Driver disables only the collected pane MeshRenderers; native AI clears only IsEnabled on the exact glass batches, which may serve other instances of the same source in this temporary World.",
                ["mirror_exposure_limit"] = "GPU exposure is locked/read for the eye only; independent mirror-camera GPU exposure is not measured by this comparison." };
            results.Add(report);
            var rows = new List<object>(); report["captures"] = rows;
            var reads = new List<object>(); report["exposure_readbacks"] = reads;
            var originalMaterials = new Dictionary<Material, JObject>();
            var originalSources = new Dictionary<Colossal.IO.AssetDatabase.SurfaceAsset, JObject>();
            GlassVisibility visibility = null;
            var exposure = new BusGlassOpticsExposure();
            Exception failure = null;
            var cleanup = new List<Exception>();
            var position = camera.transform.position; var rotation = camera.transform.rotation; float fov = camera.fieldOfView;
            BusGlassOpticsExposure.GpuExposure? locked = null;
            bool readable = true;
            int restorationFrame = -1, completedFrame = -1;
            try
            {
                check(); Require(owns(), "Glass visibility lost its owned capture");
                Require(mode == "driver" ? bindings.All(binding => binding.Renderer != null) :
                    mode == "native-ai" && bindings.All(binding => binding.Batch != null && binding.Renderer == null),
                    "Glass visibility mode does not match its exact targets");
                visibility = new GlassVisibility(bindings);
                foreach (var binding in bindings)
                {
                    if (!originalMaterials.ContainsKey(binding.Material)) originalMaterials.Add(binding.Material, OpticsMaterial(binding.Material));
                    if (!originalSources.ContainsKey(binding.Surface)) originalSources.Add(binding.Surface, RegisteredOpticsSurface(binding.Surface));
                }
                report["materials_before"] = originalMaterials.Values.ToArray();
                report["sources_before"] = originalSources.Values.ToArray();
                var initial = await ReadExposure(); report["exposure_before_lock"] = initial.Evidence();
                exposure.Activate(camera, initial.Ev100);
                foreach (string stage in new[] { "present", "absent", "present-restored" })
                {
                    CheckView();
                    if (stage == "absent") visibility.Hide(); else visibility.Restore();
                    bool absent = stage == "absent";
                    visibility.Verify(absent);
                    int resetFrame = Time.frameCount;
                    HDCamera.GetOrCreate(camera).Reset();
                    await settle(); CheckView(); visibility.Verify(absent); VerifySources();
                    var gpu = await ReadExposure();
                    var resolved = HDCamera.GetOrCreate(camera).volumeStack.GetComponent<Exposure>();
                    Require(resolved.mode.value == ExposureMode.Fixed && Math.Abs(resolved.fixedExposure.value - initial.Ev100) < .001f,
                        "Glass visibility fixed exposure did not resolve on the actual eye");
                    if (!locked.HasValue) locked = gpu;
                    Require(Math.Abs(gpu.Ev100 - locked.Value.Ev100) < .005f &&
                        Math.Abs(gpu.Multiplier / locked.Value.Multiplier - 1f) < .002f, "Glass visibility actual GPU exposure changed");
                    string filename = "glass-visibility-" + hour + "-" + mode + "-" + stage + ".png";
                    await capture(filename); CheckView(); visibility.Verify(absent); VerifySources();
                    var image = await OpticsImageEvidence(System.IO.Path.Combine(directory, filename), check);
                    readable &= image.usable;
                    rows.Add(new { stage, filename, actual_exposure = gpu.Evidence(), targets = visibility.Evidence(),
                        history_reset_frame = resetFrame, capture_frame = Time.frameCount,
                        camera_position = A(camera.transform.position), camera_rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                        fov, scene = sceneEvidence(), image = image.evidence });
                }
                report["materials_unchanged"] = true; report["source_dictionaries_unchanged"] = true;
            }
            catch (Exception error) { failure = error; report["error"] = error.ToString(); }
            finally
            {
                if (visibility != null) Try(visibility.Dispose);
                if (visibility != null && visibility.HasSkippedRestoration)
                    cleanup.Add(new InvalidOperationException("Glass visibility identity changed; replacement owners were not modified"));
                Try(exposure.Dispose);
                restorationFrame = Time.frameCount;
                report["restoration"] = visibility?.CleanupEvidence();
                try
                {
                    if (owns())
                    {
                        CheckView(); HDCamera.GetOrCreate(camera).Reset();
                        await settle(); CheckView();
                        visibility?.Verify(false);
                        Require(Time.frameCount - restorationFrame >= 2, "Glass visibility cleanup has fewer than two later render frames");
                        completedFrame = Time.frameCount;
                    }
                    else
                    {
                        report["cleanup_render_skipped"] = "Capture ownership lost; no replacement camera is rendered or changed";
                        cleanup.Add(new OperationCanceledException("Glass visibility cleanup lost capture ownership"));
                    }
                }
                catch (Exception error) { cleanup.Add(error); }
                report["cleanup"] = new { passed = cleanup.Count == 0, restoration_frame = restorationFrame,
                    completed_frame = completedFrame, errors = cleanup.Select(error => error.ToString()).ToArray() };
            }
            if (failure != null || cleanup.Count != 0)
            {
                report["status"] = "FAIL";
                if (failure != null) cleanup.Insert(0, failure);
                throw new AggregateException("Glass visibility capture failed", cleanup);
            }
            report["status"] = readable ? "CAPTURED_REQUIRES_ON_OFF_IMAGE_REVIEW" : "CAPTURED_WITH_UNREADABLE_IMAGES";
            return readable;

            void Try(Action action) { try { action(); } catch (Exception error) { cleanup.Add(error); } }
            Task<BusGlassOpticsExposure.GpuExposure> ReadExposure()
            {
                var row = new Dictionary<string, object>(); reads.Add(row);
                return BusGlassOpticsExposure.Read(camera, check, row);
            }
            void CheckView()
            {
                check(); Require(owns(), "Glass visibility capture ownership changed");
                Require(Vector3.Distance(position, camera.transform.position) < .002f && Quaternion.Angle(rotation, camera.transform.rotation) < .01f &&
                    Math.Abs(fov - camera.fieldOfView) < .001f, "Glass visibility camera moved");
            }
            void VerifySources()
            {
                foreach (var original in originalMaterials)
                    Require(JToken.DeepEquals(original.Value, OpticsMaterial(original.Key)), "Visibility changed a glass material");
                foreach (var original in originalSources)
                    Require(JToken.DeepEquals(original.Value, RegisteredOpticsSurface(original.Key)), "Visibility changed source dictionaries");
            }
        }
    }
}
#endif
