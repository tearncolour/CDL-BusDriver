#if CDLC_AUTHORING
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CDLC.Asset.Rendering;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    internal sealed class BusRoadBindingsSnapshot : IDisposable
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        readonly World world;
        readonly ComponentSystemBase target;
        readonly object observer;
        readonly Type session;
        readonly MethodInfo snapshot;
        readonly string assetsMvid, busMvid;
        readonly HashSet<string> surfaces = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, object> evidence = new Dictionary<string, object>();
        readonly List<int> ownedIds = new List<int>();
        WeakReference oldScope, oldBackend;
        long generation;
        int rendered, lastRenderedFrame = -1;
        bool subscribed, disposed;
        sealed class BindingWitness
        {
            internal int Index, MaterialId;
            internal string SurfaceId;
        }
        internal bool LiveCaptured { get; private set; }
        internal object Evidence => evidence;

        internal static void RequireObserver(World world, string expectedAssets, string expectedObserver)
        {
            Assembly assets = ExactAssembly("CDLC.Asset", expectedAssets);
            Assembly probe = ExactAssembly("CDLC.GlassLifecycleProbe", expectedObserver);
            object reader = FindObserver(world, probe);
            SnapshotMethod(reader.GetType());
            int targets = 0;
            foreach (ComponentSystemBase system in world.Systems)
                if (system.GetType().Assembly == assets && system.GetType().FullName == "CDLC.Asset.Rendering.NativeGlassSystem") targets++;
            if (targets != 1) throw new InvalidOperationException("Expected one production native glass system in this World");
        }

        internal BusRoadBindingsSnapshot(SystemBase host, PrefabBase front, PrefabBase rear,
            string expectedAssets, string expectedObserver)
        {
            world = host.World;
            Assembly assets = ExactAssembly("CDLC.Asset", expectedAssets);
            Assembly probe = ExactAssembly("CDLC.GlassLifecycleProbe", expectedObserver);
            observer = FindObserver(world, probe); snapshot = SnapshotMethod(observer.GetType());
            foreach (ComponentSystemBase system in world.Systems)
                if (system.GetType().Assembly == assets && system.GetType().FullName == "CDLC.Asset.Rendering.NativeGlassSystem")
                {
                    if (target != null) throw new InvalidOperationException("Duplicate production native glass systems");
                    target = system;
                }
            if (target == null) throw new InvalidOperationException("Production native glass is not present in this World");
            session = assets.GetType("CDLC.Asset.Runtime.RuntimeSession", true);
            assetsMvid = assets.ManifestModule.ModuleVersionId.ToString("D");
            busMvid = typeof(BusRoadBindingsSnapshot).Assembly.ManifestModule.ModuleVersionId.ToString("D");
            AddSurfaces(front); AddSurfaces(rear);
            evidence["assets_mvid"] = assetsMvid; evidence["bus_mvid"] = busMvid;
            evidence["observer_mvid"] = probe.ManifestModule.ModuleVersionId.ToString("D");
            evidence["target_surfaces"] = surfaces.ToArray();
            evidence["native_defaults_before_after_bytes_verified"] = false;
            evidence["scope"] = "Every declared target surface must have a production-committed native batch. Entity generation and active viewer evidence are recorded separately in lifecycle_fixture. The independent byte observer covers only its selected episode, not every target surface. Snapshot work is outside a formal performance window.";
            try { RenderPipelineManager.endFrameRendering += OnRendered; subscribed = true; }
            catch { Dispose(); throw; }
        }

        void AddSurfaces(PrefabBase prefab)
        {
            if (prefab?.asset == null) throw new InvalidOperationException("Binding target has no native asset");
            string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(prefab.asset.path), prefab.name + ".cdlg.json");
            NativeGlassManifest manifest = NativeGlassManifest.Parse(System.IO.File.ReadAllText(path));
            manifest.RequireIdentity(prefab.name, prefab.asset.id.guid.ToString());
            foreach (string surface in manifest.Surfaces) surfaces.Add(surface);
        }

        void OnRendered(ScriptableRenderContext context, Camera[] cameras)
        {
            if (!disposed && cameras != null && cameras.Length > 0 && Time.frameCount != lastRenderedFrame)
            { lastRenderedFrame = Time.frameCount; rendered++; }
        }

        internal async Task CaptureLive(Action checkWorld, int timeoutSeconds = 180)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                checkWorld();
                if (!world.IsCreated || !ReferenceEquals(target.World, world) || !target.Enabled || (bool)Read(target, "m_Failed"))
                    throw new InvalidOperationException("Production native glass stopped or left the owned World");
                if (!(bool)ReadStatic(session, "IsInitialized")) throw new InvalidOperationException("Assets runtime is not initialized");
                object backend = Read(target, "m_Batches"), scope = Read(target, "m_Scope");
                var bindings = backend == null ? null : Read(backend, "m_Bindings") as IDictionary;
                var matches = new List<BindingWitness>(); var ids = new List<int>();
                var matchedSurfaces = new HashSet<string>(StringComparer.Ordinal);
                if (bindings != null && scope != null && ReferenceEquals(scope, Read(backend, "m_Scope")) &&
                    (bool)Read(scope, "IsAlive") && ((IList)Read(scope, "m_Resources")).Contains(backend))
                    foreach (DictionaryEntry pair in bindings)
                    {
                        object binding = pair.Value;
                        var surface = Read(binding, "Surface") as SurfaceAsset;
                        if (surface == null || !surfaces.Contains(surface.name)) continue;
                        if ((bool)Read(binding, "Retiring") || (bool)Read(binding, "MaterialWritePending") || !(bool)Read(binding, "OwnsReplacement")) continue;
                        var replacement = Read(binding, "Replacement") as Material;
                        object owner = Read(binding, "Owner"), batch = Read(binding, "Batch");
                        if (owner == null || !(bool)Read(owner, "m_OwnsMaterial") || replacement == null ||
                            !ReferenceEquals(Read(owner, "m_Material"), replacement) || !ReferenceEquals(Read(batch, "material"), replacement))
                            throw new InvalidOperationException("Committed target has no exact owned private material");
                        matches.Add(new BindingWitness { Index = Convert.ToInt32(Read(binding, "Index")),
                            MaterialId = replacement.GetInstanceID(), SurfaceId = surface.id.ToString() });
                        matchedSurfaces.Add(surface.name);
                        ids.Add(replacement.GetInstanceID());
                        var mask = Read(owner, "m_Mask") as Texture;
                        if (mask != null) ids.Add(mask.GetInstanceID());
                    }
                string[] missingSurfaces = surfaces.Where(surface => !matchedSurfaces.Contains(surface)).ToArray();
                evidence["matching_committed_surfaces"] = matchedSurfaces.ToArray();
                evidence["missing_target_surfaces"] = missingSurfaces;
                if (missingSurfaces.Length == 0)
                {
                    generation = Convert.ToInt64(ReadStatic(session, "Generation"));
                    if (Convert.ToInt64(Read(scope, "Generation")) != generation)
                        throw new InvalidOperationException("Committed binding scope belongs to another generation");
                    oldScope = new WeakReference(scope); oldBackend = new WeakReference(backend);
                    evidence["generation"] = generation; evidence["live_frame"] = Time.frameCount;
                    JObject live = InvokeSnapshot(true, "live");
                    evidence["live_snapshot"] = live;
                    RequireLiveRows(live, matches);
                    checkWorld();
                    ownedIds.AddRange(ids.Distinct());
                    evidence["recorded_private_ids"] = ownedIds.ToArray();
                    evidence["matching_committed_indices"] = matches.Select(match => match.Index).ToArray();
                    evidence["all_target_surfaces_committed"] = true;
                    LiveCaptured = true;
                    return;
                }
                if (watch.Elapsed.TotalSeconds >= timeoutSeconds)
                    throw new TimeoutException("Missing production-committed target native glass surfaces: " + string.Join(", ", missingSurfaces));
                await Task.Delay(100);
            }
        }

        static void RequireLiveRows(JObject live, List<BindingWitness> witnesses)
        {
            var rows = live["active_bindings"] as JArray;
            foreach (BindingWitness witness in witnesses)
            {
                JObject[] same = rows?.OfType<JObject>().Where(row => row["index"]?.Value<int>() == witness.Index).ToArray();
                if (same == null || same.Length != 1 || same[0]["owns_replacement"]?.Value<bool>() != true ||
                    same[0]["bound_to_replacement"]?.Value<bool>() != true || same[0]["same_batch_identity"]?.Value<bool>() != true ||
                    same[0]["retiring"]?.Value<bool>() != false || same[0]["native_index"]?.Value<int?>() == null ||
                    same[0]["native_index"].Value<int>() < 0 || same[0]["surface"]?.Value<string>() != witness.SurfaceId ||
                    same[0]["replacement"]?["id"]?.Value<int?>() != witness.MaterialId ||
                    same[0]["actual"]?["id"]?.Value<int?>() != witness.MaterialId ||
                    same[0]["native_material_id"]?.Value<uint?>() == null ||
                    same[0]["native_material_id"]?.Value<uint?>() != same[0]["managed_material_id"]?.Value<uint?>())
                    throw new InvalidOperationException("Explicit snapshot does not verify this target's committed native identity");
                JToken plan = same[0]["native_defaults"];
                var properties = plan?["properties"] as JArray;
                if (plan?["metadata_available"]?.Value<bool>() != true || plan?["controlled_count"]?.Value<int?>() == null ||
                    plan["name_source"]?.Value<string>() != "ActualRegisteredNameOnly" || properties == null || properties.Count == 0 ||
                    properties.Any(property => property["name_id"]?.Value<int?>() == null || property["offset"]?.Value<int?>() == null ||
                        property["offset"].Value<int>() < 0 || property["size"]?.Value<int?>() == null || property["size"].Value<int>() <= 0 ||
                        property["builtin"]?.Value<bool?>() == null || property["global"]?.Value<bool?>() == null ||
                        property["controlled"]?.Value<bool?>() == null || string.IsNullOrEmpty(property["registered_name"]?.Value<string>()) ||
                        property["size"]?.Value<int?>() != property["registered_size"]?.Value<int?>() ||
                        property["builtin"]?.Value<bool?>() != property["registered_builtin"]?.Value<bool?>() ||
                        property["global"]?.Value<bool?>() != property["registered_global"]?.Value<bool?>()) ||
                    plan["controlled_count"].Value<int>() != properties.Count(property => property["controlled"]?.Value<bool>() == true))
                    throw new InvalidOperationException("Target native-default metadata or actual registry identity is incomplete");
            }
        }

        JObject InvokeSnapshot(bool requireBindings, string label)
        {
            var errors = new List<string>();
            try
            {
                object value = snapshot.Invoke(observer, new object[] { requireBindings, assetsMvid, busMvid, errors, false, false });
                JObject document = JObject.FromObject(value);
                if (errors.Count > 0) { evidence[label + "_snapshot"] = document; throw new InvalidOperationException(string.Join("; ", errors)); }
                return document;
            }
            catch (Exception error) { evidence[label + "_snapshot_exception"] = error.ToString(); throw; }
            finally { evidence[label + "_snapshot_errors"] = errors.ToArray(); }
        }

        internal async Task SnapshotAfterCleanup(Action checkOwnership, int timeoutSeconds = 180)
        {
            if (!LiveCaptured) throw new InvalidOperationException("No successful live binding snapshot precedes cleanup");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int retiredFrame = -1, retiredRenders = -1;
            while (true)
            {
                checkOwnership();
                if (!world.IsCreated || GameManager.instance.gameMode != GameMode.MainMenu ||
                    GameManager.instance.state != GameManager.State.WorldReady || GameManager.instance.isGameLoading)
                    throw new OperationCanceledException("Binding snapshot no longer owns its returned main menu");
                if (!target.Enabled || (bool)Read(target, "m_Failed")) throw new InvalidOperationException("Native glass failed before release evidence");
                long current = Convert.ToInt64(ReadStatic(session, "Generation"));
                object next = Read(target, "m_Scope"), scope = oldScope.Target, backend = oldBackend.Target;
                bool retired = (scope == null || !(bool)Read(scope, "IsAlive")) &&
                    (backend == null || ((IDictionary)Read(backend, "m_Bindings")).Count == 0);
                bool ready = current > generation && next != null && (bool)Read(next, "IsAlive") &&
                    Convert.ToInt64(Read(next, "Generation")) == current && Read(target, "m_Batches") != null;
                if (retired && ready)
                {
                    if (retiredFrame < 0) { retiredFrame = Time.frameCount; retiredRenders = rendered; }
                    if (Time.frameCount - retiredFrame >= 2 && rendered - retiredRenders >= 2) break;
                }
                if (watch.Elapsed.TotalSeconds >= timeoutSeconds)
                    throw new TimeoutException("Committed binding retirement did not reach a normal new scope and two later render completions");
                await Task.Delay(100);
            }
            checkOwnership();
            evidence["retired_frame"] = retiredFrame; evidence["release_snapshot_frame"] = Time.frameCount;
            evidence["two_later_render_completions"] = true;
            JObject final = InvokeSnapshot(false, "release"); evidence["release_snapshot"] = final;
            var queries = final["native_instance_ids"]?["queries"] as JArray;
            foreach (int id in ownedIds)
            {
                JObject[] same = queries?.OfType<JObject>().Where(row => row["instance_id"]?.Value<int>() == id).ToArray();
                if (same == null || same.Length != 1 || same[0]["exists"]?.Value<bool?>() != false)
                    throw new InvalidOperationException("Recorded private native ID absence is not confirmed after cleanup: " + id);
            }
            evidence["exact_resource_release_verified"] = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            if (subscribed) { RenderPipelineManager.endFrameRendering -= OnRendered; subscribed = false; }
            disposed = true;
        }

        static object FindObserver(World world, Assembly probe)
        {
            object reader = null;
            foreach (ComponentSystemBase system in world.Systems)
                if (system.GetType().Assembly == probe && system.GetType().FullName == "CDLC.GlassLifecycleProbe.ProbeSystem")
                {
                    if (reader != null) throw new InvalidOperationException("Duplicate lifecycle observer systems");
                    reader = Read(system, "observer");
                }
            return reader ?? throw new InvalidOperationException("Lifecycle observer has not initialized in this World");
        }

        static MethodInfo SnapshotMethod(Type type) => type.GetMethod("Snapshot", Instance, null,
            new[] { typeof(bool), typeof(string), typeof(string), typeof(List<string>), typeof(bool), typeof(bool) }, null) ??
            throw new NotSupportedException("Lifecycle observer has no exact explicit snapshot API");

        static Assembly ExactAssembly(string name, string expected)
        {
            if (!Guid.TryParse(expected, out Guid id) || id == Guid.Empty) throw new InvalidOperationException("Exact " + name + " MVID is required");
            Assembly[] matches = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == name).ToArray();
            if (matches.Length != 1 || matches[0].ManifestModule.ModuleVersionId != id)
                throw new InvalidOperationException("Loaded " + name + " does not match the expected unique assembly");
            return matches[0];
        }

        static object ReadStatic(Type type, string name) => type.GetProperty(name, Static)?.GetValue(null) ??
            throw new MissingMemberException(type.FullName, name);

        static object Read(object value, string name)
        {
            if (value == null) throw new InvalidOperationException("Missing reflection owner for " + name);
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(value);
                PropertyInfo property = type.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(value);
            }
            throw new MissingFieldException(value.GetType().FullName, name);
        }
    }
}
#endif
