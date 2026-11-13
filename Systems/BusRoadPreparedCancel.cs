#if CDLC_AUTHORING
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CDLC.Asset.Rendering;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    [DisableAutoCreation]
    public sealed partial class BusPreparedCancelCheckpoint : GameSystemBase
    {
        internal static BusRoadPreparedCancel Active;
        internal bool Scheduled;
        protected override void OnUpdate() => Active?.Observe();
        protected override void OnDestroy()
        {
            try
            {
                BusRoadPreparedCancel current = Active;
                if (current != null && ReferenceEquals(current.Checkpoint, this))
                    current.Abort("Prepared cancellation checkpoint World was destroyed");
            }
            finally { base.OnDestroy(); }
        }
    }

    internal sealed class BusRoadPreparedCancel : IDisposable
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        readonly World world;
        readonly object target, observation, observer;
        readonly Type session;
        internal readonly BusPreparedCancelCheckpoint Checkpoint;
        readonly Action checkWorld;
        readonly Func<Task<bool>> beginCleanup;
        readonly HashSet<string> surfaces;
        readonly string assetsMvid, busMvid;
        readonly long generation;
        readonly UpdateSystem updates;
        readonly object observerSystem;
        readonly int armedFrame;
        readonly TaskCompletionSource<bool> triggered = new TaskCompletionSource<bool>();
        readonly Dictionary<string, object> evidence = new Dictionary<string, object>();
        object selectedEpisode;
        object scheduledList;
        int scheduledVersion = -1, observerIndex = -1, checkpointIndex = -1;
        int materialId, maskId, index;
        bool active, disposed;
        internal Task Triggered => triggered.Task;
        internal Task<bool> CleanupTask { get; private set; }
        internal object Evidence => evidence;

        internal static void RequireObserver(World world, string expectedAssets, string expectedObserver)
        {
            Assembly assets = ExactAssembly("CDLC.Asset", expectedAssets);
            Assembly probe = ExactAssembly("CDLC.GlassLifecycleProbe", expectedObserver);
            object current = probe.GetType("CDLC.GlassLifecycleProbe.PreparedFrameSystem", true).GetField("Active", Static)?.GetValue(null);
            if (current == null || !(bool)Read(current, "active"))
                throw new InvalidOperationException("Explicit prepared-start must arm the lifecycle observer before this command");
            ComponentSystemBase target = Read(current, "target") as ComponentSystemBase;
            if (target == null || !ReferenceEquals(target.World, world) || target.GetType().Assembly != assets)
                throw new InvalidOperationException("Prepared observer belongs to another Assets assembly or World");
            Type reader = probe.GetType("CDLC.GlassLifecycleProbe.Observer", true);
            if (reader.GetMethod("Snapshot", Instance, null,
                new[] { typeof(bool), typeof(string), typeof(string), typeof(List<string>), typeof(bool), typeof(bool) }, null) == null)
                throw new NotSupportedException("Lifecycle observer has no exact cancellation snapshot API");
        }

        internal BusRoadPreparedCancel(SystemBase host, PrefabBase front, PrefabBase rear,
            string expectedAssets, string expectedObserver, Action checkWorld, Func<Task<bool>> beginCleanup)
        {
            world = host.World;
            RequireObserver(world, expectedAssets, expectedObserver);
            Assembly assets = ExactAssembly("CDLC.Asset", expectedAssets);
            Assembly probe = ExactAssembly("CDLC.GlassLifecycleProbe", expectedObserver);
            assetsMvid = assets.ManifestModule.ModuleVersionId.ToString("D");
            busMvid = typeof(BusRoadPreparedCancel).Assembly.ManifestModule.ModuleVersionId.ToString("D");
            observation = probe.GetType("CDLC.GlassLifecycleProbe.PreparedFrameSystem", true).GetField("Active", Static).GetValue(null);
            target = Read(observation, "target"); session = assets.GetType("CDLC.Asset.Runtime.RuntimeSession", true);
            if (!(bool)session.GetProperty("IsInitialized").GetValue(null)) throw new InvalidOperationException("Assets runtime is not initialized");
            generation = Convert.ToInt64(session.GetProperty("Generation").GetValue(null));
            object probeSystem = null;
            foreach (ComponentSystemBase candidate in world.Systems)
                if (candidate.GetType().FullName == "CDLC.GlassLifecycleProbe.ProbeSystem")
                {
                    if (probeSystem != null) throw new InvalidOperationException("Duplicate lifecycle probe systems");
                    probeSystem = candidate;
                }
            observer = probeSystem == null ? null : Read(probeSystem, "observer");
            if (observer == null || !ReferenceEquals(Read(observer, "prepared"), observation))
                throw new InvalidOperationException("Lifecycle observer and prepared checkpoint do not share an observation");
            surfaces = new HashSet<string>(StringComparer.Ordinal);
            AddSurfaces(front); AddSurfaces(rear);
            this.checkWorld = checkWorld ?? throw new ArgumentNullException(nameof(checkWorld));
            this.beginCleanup = beginCleanup ?? throw new ArgumentNullException(nameof(beginCleanup));
            updates = world.GetExistingSystemManaged<UpdateSystem>();
            observerSystem = Read(observation, "system");
            armedFrame = Time.frameCount;
            if (updates == null || BusPreparedCancelCheckpoint.Active != null)
                throw new InvalidOperationException("Missing update scheduler or another prepared cancellation is active");
            Checkpoint = world.GetOrCreateSystemManaged<BusPreparedCancelCheckpoint>();
            Checkpoint.Enabled = false;
            try
            {
                if (!Checkpoint.Scheduled)
                {
                    MethodInfo after = typeof(UpdateSystem).GetMethods().Single(method => method.Name == "UpdateAfter" &&
                        method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 2 && method.GetParameters().Length == 1);
                    after.MakeGenericMethod(typeof(BusPreparedCancelCheckpoint), probe.GetType("CDLC.GlassLifecycleProbe.PreparedFrameSystem", true))
                        .Invoke(updates, new object[] { SystemUpdatePhase.UIUpdate });
                    Checkpoint.Scheduled = true;
                }
                BusPreparedCancelCheckpoint.Active = this; active = true; Checkpoint.Enabled = true;
                evidence["armed_frame"] = Time.frameCount; evidence["generation"] = generation;
                evidence["assets_mvid"] = assetsMvid; evidence["bus_mvid"] = busMvid;
                evidence["observer_mvid"] = probe.ManifestModule.ModuleVersionId.ToString("D");
                evidence["target_surfaces"] = surfaces.ToArray();
            }
            catch (Exception failure)
            {
                active = false;
                if (ReferenceEquals(BusPreparedCancelCheckpoint.Active, this)) BusPreparedCancelCheckpoint.Active = null;
                try { Checkpoint.Enabled = false; }
                catch (Exception cleanup) { throw new AggregateException("Prepared checkpoint setup and cleanup failed", failure, cleanup); }
                throw;
            }
        }

        void AddSurfaces(PrefabBase prefab)
        {
            if (prefab?.asset == null) throw new InvalidOperationException("Cancellation target has no native asset");
            string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(prefab.asset.path), prefab.name + ".cdlg.json");
            var manifest = NativeGlassManifest.Parse(System.IO.File.ReadAllText(path));
            manifest.RequireIdentity(prefab.name, prefab.asset.id.guid.ToString());
            foreach (string name in manifest.Surfaces) surfaces.Add(name);
        }

        internal void Observe()
        {
            if (!active || triggered.Task.IsCompleted) return;
            try
            {
                checkWorld();
                ValidateSchedule();
                if (!world.IsCreated || !(bool)Read(observation, "active") || Read(Read(observation, "journal"), "Failure") != null)
                    throw new InvalidOperationException("Prepared lifecycle observation stopped or failed");
                if (Convert.ToInt64(session.GetProperty("Generation").GetValue(null)) != generation)
                    throw new OperationCanceledException("Owned generation changed before prepared cancellation");
                var currentTarget = (ComponentSystemBase)target;
                if (!ReferenceEquals(currentTarget.World, world) || !currentTarget.Enabled || (bool)Read(target, "m_Failed"))
                    throw new InvalidOperationException("Production native glass stopped or left the owned World");
                object backend = Read(target, "m_Batches"), scope = backend == null ? null : Read(backend, "m_Scope");
                object prepared = backend == null ? null : Read(backend, "m_Prepared");
                if (prepared == null) return;
                object owner = Read(prepared, "Owner");
                if (owner == null || !(bool)Read(owner, "m_OwnsMaterial")) return;
                var surface = Read(prepared, "Surface") as Colossal.IO.AssetDatabase.SurfaceAsset;
                if (surface == null || !surfaces.Contains(surface.name)) return;
                if (scope == null || !ReferenceEquals(scope, Read(target, "m_Scope")) || !(bool)scope.GetType().GetProperty("IsAlive").GetValue(scope) ||
                    Convert.ToInt64(scope.GetType().GetProperty("Generation").GetValue(scope)) != generation ||
                    !((IList)Read(scope, "m_Resources")).Contains(backend))
                    throw new InvalidOperationException("Prepared native owner is outside its live production scope");
                object batch = Read(prepared, "Batch");
                var map = Read(backend, "m_Bindings") as IDictionary;
                if ((map != null && ReferenceEquals(map[batch], prepared)) || (bool)Read(prepared, "MaterialWritePending") ||
                    (bool)Read(prepared, "DefaultsTouched") || (bool)Read(prepared, "Retiring") ||
                    !ReferenceEquals(Read(batch, "material"), Read(prepared, "Original")))
                    throw new InvalidOperationException("Cancellation target has already committed, retired, or lost original material identity");
                var material = Read(owner, "m_Material") as Material;
                var mask = Read(owner, "m_Mask") as Texture;
                if (material == null || !ReferenceEquals(material, Read(prepared, "Replacement")))
                    throw new InvalidOperationException("Prepared private material identity is unavailable");
                materialId = material.GetInstanceID(); maskId = mask == null ? 0 : mask.GetInstanceID();
                index = Convert.ToInt32(Read(prepared, "Index"));
                foreach (object row in (IEnumerable)Read(Read(observation, "journal"), "Episodes"))
                    if (ReferenceEquals(((WeakReference)Read(row, "Binding")).Target, prepared))
                    {
                        if (selectedEpisode != null) throw new InvalidOperationException("Duplicate observer binding episode");
                        selectedEpisode = row;
                    }
                object episode = Episode();
                if (episode == null || Convert.ToInt32(Read(episode, "CommittedFrame")) >= 0 ||
                    Convert.ToInt64(Read(episode, "Generation")) != generation ||
                    Convert.ToInt32(Read(episode, "FirstFrame")) < armedFrame)
                    throw new InvalidOperationException("Observer did not record the same private pre-commit episode before cancellation");
                evidence["observed_frame"] = Time.frameCount; evidence["index"] = index; evidence["surface"] = surface.name;
                evidence["material_id"] = materialId; evidence["mask_id"] = maskId;
                evidence["committed_before_request"] = false; evidence["scope_owns_backend"] = true;
                // Load enters State.Loading synchronously, before its first GPU-frame await; the next Assets update cannot commit.
                CleanupTask = beginCleanup();
                if (CleanupTask == null || GameManager.instance.state != GameManager.State.Loading)
                    throw new InvalidOperationException("Normal Cleanup did not enter Loading synchronously");
                evidence["loading_entered_synchronously"] = true;
                triggered.TrySetResult(true);
            }
            catch (Exception error) { Abort(error.ToString()); }
        }

        object Episode()
        {
            foreach (object row in (IEnumerable)Read(Read(observation, "journal"), "Episodes"))
                if (ReferenceEquals(row, selectedEpisode)) return row;
            return null;
        }

        void ValidateSchedule()
        {
            if ((bool)Read(updates, "m_IsDirty") || updates.currentPhase != SystemUpdatePhase.UIUpdate)
                throw new InvalidOperationException("Prepared checkpoint schedule is dirty or in another phase");
            var list = (IList)Read(updates, "m_Updates");
            int version = Convert.ToInt32(Read(list, "_version"));
            if (!ReferenceEquals(scheduledList, list) || scheduledVersion != version)
            {
                scheduledList = list; scheduledVersion = version; observerIndex = checkpointIndex = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    object item = Read(list[i], "m_System");
                    if (ReferenceEquals(item, observerSystem)) observerIndex = i;
                    if (ReferenceEquals(item, Checkpoint)) checkpointIndex = i;
                }
            }
            if (observerIndex < 0 || checkpointIndex != observerIndex + 1 ||
                !ReferenceEquals(Read(list[checkpointIndex], "m_System"), Checkpoint) ||
                Convert.ToInt32(Read(list[checkpointIndex], "m_Phase")) != (int)SystemUpdatePhase.UIUpdate ||
                Convert.ToInt32(Read(list[checkpointIndex], "m_Interval")) != 1 ||
                Convert.ToInt32(Read(list[checkpointIndex], "m_Offset")) != 0)
                throw new InvalidOperationException("Prepared checkpoint is not immediately after the lifecycle observer");
        }

        internal async Task SnapshotAfterCleanup(Action checkOwnership, int timeoutSeconds = 180)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                checkOwnership();
                if (evidence.ContainsKey("failure")) throw new InvalidOperationException((string)evidence["failure"]);
                if (!world.IsCreated || GameManager.instance.gameMode != GameMode.MainMenu ||
                    GameManager.instance.state != GameManager.State.WorldReady || GameManager.instance.isGameLoading)
                    throw new OperationCanceledException("Prepared cancellation lost its returned main menu; no further transition is authorized");
                if (!(bool)Read(observation, "active") || Read(Read(observation, "journal"), "Failure") != null)
                    throw new InvalidOperationException("Prepared lifecycle observer stopped or failed before final evidence");
                if ((bool)Read(target, "m_Failed")) throw new InvalidOperationException("Production native glass is in its failure latch");
                object episode = Episode();
                if (episode == null || Convert.ToInt32(Read(episode, "CommittedFrame")) >= 0)
                    throw new InvalidOperationException("The observed private owner committed or its episode is unavailable");
                bool grace = (bool)episode.GetType().GetMethod("GraceObserved", Instance).Invoke(episode,
                    new object[] { Time.frameCount, Convert.ToInt32(Read(observation, "rendered")) });
                bool cancelled = (string)Read(episode, "State") == "cancelled-before-commit";
                object nextScope = Read(target, "m_Scope");
                long currentGeneration = Convert.ToInt64(session.GetProperty("Generation").GetValue(null));
                bool ready = currentGeneration > generation && nextScope != null && Read(target, "m_Batches") != null &&
                    (bool)Read(nextScope, "IsAlive") && Convert.ToInt64(Read(nextScope, "Generation")) == currentGeneration;
                if (cancelled && grace && ready) break;
                if (watch.Elapsed.TotalSeconds >= timeoutSeconds)
                    throw new TimeoutException("Prepared cancellation did not reach cancellation, two later render completions, and a normal new scope");
                await Task.Delay(100);
            }
            var errors = new List<string>();
            checkOwnership();
            MethodInfo snapshot = observer.GetType().GetMethod("Snapshot", Instance, null,
                new[] { typeof(bool), typeof(string), typeof(string), typeof(List<string>), typeof(bool), typeof(bool) }, null);
            object result;
            try { result = snapshot.Invoke(observer, new object[] { false, assetsMvid, busMvid, errors, true, true }); }
            catch (Exception error) { evidence["snapshot_exception"] = error.ToString(); throw; }
            finally { evidence["snapshot_errors"] = errors.ToArray(); evidence["snapshot_frame"] = Time.frameCount; }
            evidence["snapshot"] = result;
            if (errors.Count != 0) throw new InvalidOperationException("Prepared cancellation snapshot failed: " + string.Join("; ", errors));
            JObject document = JObject.FromObject(result);
            var rows = document["prepared_lifecycle"]?["episodes"] as JArray;
            var same = rows?.OfType<JObject>().Where(row => row["generation"]?.Value<long>() == generation &&
                row["index"]?.Value<int>() == index && row["material_id"]?.Value<int>() == materialId &&
                row["first_frame"]?.Value<int>() == Convert.ToInt32(Read(selectedEpisode, "FirstFrame"))).ToArray();
            if (same == null || same.Length != 1 || same[0]["state"]?.Value<string>() != "cancelled-before-commit" ||
                same[0]["committed_frame"]?.Value<int>() != -1 || same[0]["two_later_renders_observed"]?.Value<bool>() != true ||
                same[0]["material_native_id_query"]?["exists"]?.Value<bool?>() != false ||
                same[0]["mask_id"]?.Value<int>() != maskId ||
                (maskId != 0 && same[0]["mask_native_id_query"]?["exists"]?.Value<bool?>() != false))
                throw new InvalidOperationException("This request's exact prepared episode does not prove cancellation and recorded-ID absence");
            evidence["exact_resource_release_verified"] = true;
        }

        internal void Abort(string reason)
        {
            evidence["failure"] = reason;
            triggered.TrySetException(new InvalidOperationException(reason));
            active = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; active = false;
            if (ReferenceEquals(BusPreparedCancelCheckpoint.Active, this)) BusPreparedCancelCheckpoint.Active = null;
            if (world.IsCreated) Checkpoint.Enabled = false;
        }

        static Assembly ExactAssembly(string name, string expected)
        {
            if (!Guid.TryParse(expected, out Guid id) || id == Guid.Empty) throw new InvalidOperationException("Exact " + name + " MVID is required");
            Assembly[] matches = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == name).ToArray();
            if (matches.Length != 1 || matches[0].ManifestModule.ModuleVersionId != id)
                throw new InvalidOperationException("Loaded " + name + " does not match the expected unique assembly");
            return matches[0];
        }

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
