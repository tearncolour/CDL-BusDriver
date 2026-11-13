using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CDLC.Asset.Articulation;
using CDLC.BusDriver.Interior.Models;
using Colossal.Mathematics;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.SceneFlow;
using Game.Settings;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using ObjectTransform = Game.Objects.Transform;

namespace CDLC.BusDriver
{
    /// <summary>A disposable NEW official-map road smoke test. No saved city is read or written.</summary>
    internal static partial class BusVehicleRoadProbe
    {
        internal struct OwnedVehicle : IComponentData { public int Token; }
        sealed class Donor
        {
            internal Entity Entity;
            internal ObjectTransform Transform;
            internal Owner Owner;
            internal Target Target;
            internal CarCurrentLane Lane;
            internal PathOwner PathOwner;
            internal PathInformation Information;
            internal PathElement[] Path;
            internal CarNavigationLane[] Navigation;
            internal float Turn;
            internal RouteMeasure Route;
        }

        sealed class RouteMeasure
        {
            public bool sufficient;
            public string reason;
            public float remaining_curve_length_m, required_remaining_m, maximum_speed_m_s,
                prefix_distance_m, largest_join_gap_m, turn_degrees, turn_distance_m, turn_window_limit_m;
            public int measured_curves;
            public List<object> curves = new List<object>();
        }

        sealed class RouteInsufficientException : InvalidOperationException
        {
            internal RouteInsufficientException(string message) : base(message) { }
        }

        internal static async Task<string> Run(SystemBase host, PrefabSystem prefabs, string name,
            Func<string, PrefabBase> find, Func<string, string> verify, string mapId = null, string directory = null, int seconds = 90,
            bool glassCycle = false, bool glassRoadLights = false, bool glassOptics = false, bool stationaryGlass = false,
            bool preparedCancel = false, string expectedAssetsMvid = null, string expectedObserverMvid = null, bool bindingsSnapshot = false)
        {
            directory = directory ?? Path.Combine(Mod.ModDirectory, "road-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                directory = Path.Combine(directory, "attempt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(directory);
            var samples = new List<object>(); var shots = new List<object>(); var errors = new List<string>();
            var report = new Dictionary<string, object> { ["status"] = "running", ["scope"] = "Native road AI smoke in an unsaved official new map; not passenger service acceptance",
                ["utc"] = DateTime.UtcNow.ToString("o"), ["samples"] = samples, ["screenshots"] = shots,
                ["saved_city_read"] = false, ["save_written"] = false, ["auto_quit"] = false,
                ["not_tested"] = new[] { "passenger boarding/alighting", "route service and capacity under load", "manual driving", "save/reload" },
                ["assembly_mvid"] = typeof(BusVehicleRoadProbe).Assembly.ManifestModule.ModuleVersionId.ToString("D") };
            report["request_mode"] = bindingsSnapshot ? "bindings-snapshot" : preparedCancel ? "prepared-cancel" : stationaryGlass ? "glass-road" : "assembly-road";
            report["road_travel_tested"] = !stationaryGlass && !preparedCancel && !bindingsSnapshot;
            if (stationaryGlass || preparedCancel || bindingsSnapshot)
            {
                report["scope"] = bindingsSnapshot ? "Explicit committed native-glass metadata and resource-release snapshots for a native-factory vehicle in an unsaved official map" :
                    preparedCancel ? "Observed production native-glass preparation cancelled by normal Cleanup in an unsaved official map" :
                    "Stationary native glass fixture in an unsaved official map; no road travel acceptance";
                report["not_tested"] = new[] { "road travel", "AI turning and lane following", "passenger service", "save/reload" };
            }
            GameManager game = GameManager.instance;
            EntityManager em = host.EntityManager;
            bool oldAutoSave = SharedSettings.instance.general.autoSave;
            // A fresh map opens with blocking unlock popups (signature buildings, milestones); while one is open the game's
            // TimeUISystem holds the simulation paused every frame through its UI pause barrier. Like autosave, the game's own
            // setting is switched off for the disposable world only (no Apply/Save) and restored when the probe leaves.
            bool oldBlockingPopups = SharedSettings.instance.userInterface.blockingPopupsEnabled;
            bool ownsWorld = false, interrupted = false, expectingLoad = false, success = false;
            bool takingControl = false, tookTestVehicle = false;
            const string cabViewOwner = Core.kOwner + ".RoadProbe";
            bool cabViewRequested = false;
            bool? originalCabPreference = null;
            Purpose expectedPurpose = Purpose.NewGame; GameMode expectedMode = GameMode.Game;
            Entity frontEntity = Entity.Null, rearEntity = Entity.Null;
            GameObject cameraObject = null; RenderTexture target = null;
            Game.Rendering.CameraUpdateSystem cameras = null;
            Camera oldCamera = null, inspectionCamera = null;
            Game.Rendering.IGameCameraController oldController = null;
            string failure = null;
            string failureKind = null;
            BusRoadPreparedCancel cancellation = null;
            BusRoadBindingsSnapshot bindingWitness = null;
            Task<bool> cancellationCleanup = null;
            // The game's TimeUISystem pauses the simulation every frame while the platform reports the app as not focused
            // (AppState.Constrained: another window in front, or minimized). A review runs with the game in the background, so the
            // probe treats its own disposable world as focused and restores the real state when it leaves; see "focus_override".
            Game.UI.InGame.TimeUISystem timeUi = null;
            System.Reflection.FieldInfo focusField = null;
            bool focusOverridden = false;
            int focusOverrides = 0;
            List<object> externalPauses = null;
            SimulationSystem runningSimulation = null;
            void KeepRunning(double seconds)
            {
                if (timeUi != null && focusField != null && focusField.FieldType == typeof(bool) && !(bool)focusField.GetValue(timeUi))
                { focusField.SetValue(timeUi, true); focusOverridden = true; focusOverrides++; }
                if (runningSimulation != null && runningSimulation.selectedSpeed <= 0f)
                {
                    externalPauses?.Add(new { seconds, simulation_frame = runningSimulation.frameIndex });
                    runningSimulation.selectedSpeed = 1f;
                }
            }
            void Preload(Purpose purpose, GameMode mode)
            {
                if (expectingLoad && purpose == expectedPurpose && mode == expectedMode) { expectingLoad = false; return; }
                Interrupt("Unexpected world preload");
            }
            void Interrupt(string reason)
            {
                if (!interrupted) report["interruption"] = new { reason, unity_frame = Time.frameCount };
                interrupted = true; ownsWorld = false;
            }
            void CheckWorld()
            {
                if (interrupted || !ownsWorld || game.gameMode != GameMode.Game || game.isGameLoading || game.state != GameManager.State.WorldReady)
                { Interrupt("Road probe no longer owns its new-map session"); throw new OperationCanceledException("Road probe no longer owns its new-map session; return/exit cancelled"); }
                if (takingControl && Core.IsDriving() && Core.Vehicle() != frontEntity)
                { Interrupt("Player selected another vehicle"); throw new OperationCanceledException("Player selected another vehicle; return/exit cancelled"); }
                // While the probe itself hands the view to the driven cab, the active camera is legitimately not the inspection one.
                if (!takingControl && inspectionCamera != null && cameras != null && cameras.activeCamera != inspectionCamera)
                { Interrupt("Active viewer changed during road test"); throw new OperationCanceledException("Active viewer changed during road test; return/exit cancelled"); }
            }
            try
            {
                BusVehicleAssetSystem.RequireTestMenu();
                if (preparedCancel) BusRoadPreparedCancel.RequireObserver(host.World, expectedAssetsMvid, expectedObserverMvid);
                if (bindingsSnapshot) BusRoadBindingsSnapshot.RequireObserver(host.World, expectedAssetsMvid, expectedObserverMvid);
                if (!preparedCancel && !bindingsSnapshot) Require(Core.HasApi9, "Driving Core API 9 is required for a temporary cab view without changing the player's saved preference");
                string checkedAsset = verify(name);
                Require(checkedAsset.StartsWith("ok ", StringComparison.Ordinal), "Native pack preflight failed: " + checkedAsset);
                report["native_preflight"] = checkedAsset;
                PrefabBase frontPrefab = find(name);
                VehicleProfile profile = VehiclePacks.ForPrefab(frontPrefab);
                VehicleAssembly assembly = profile?.Assembly;
                // Only the stationary glass cycle takes a single-section pack bus; road travel, prepared cancellation and the
                // bindings snapshot are articulation fixtures.
                if (assembly == null)
                    Require(stationaryGlass && profile != null && !string.IsNullOrEmpty(profile.MeshPath),
                        "Name must be the explicit front prefab of a loaded v2 assembly, or a single-section pack bus for glass-road");
                else Require(assembly.Front.Prefab == name, "Name must be the explicit front prefab of a loaded v2 assembly");
                Entity frontPrefabEntity = prefabs.GetEntity(frontPrefab);
                Entity rearPrefabEntity = assembly == null ? Entity.Null : prefabs.GetEntity(find(assembly.Rear.Prefab));
                report["assembly_identity"] = assembly?.Identity;
                report["single_section"] = assembly == null;
                report["native_package"] = frontPrefab.asset.path;
                report["native_package_sha256"] = Hash(frontPrefab.asset.path);
                report["runtime_sha256"] = assembly == null ? new[] { Hash(profile.MeshPath) }
                    : new[] { Hash(assembly.Front.Profile.MeshPath), Hash(assembly.Rear.Profile.MeshPath), Hash(assembly.Joint.MeshPath) };
                report["assembly_sha256"] = Hash(Path.Combine(Mod.ModDirectory, "CDLC.BusDriver.dll"));
                var maps = AssetDatabase.game.GetAssets<MapMetadata>().OrderBy(m => m.name, StringComparer.Ordinal).ToArray();
                // Same rule as the game's own new-game list (MenuUISystem.FilterMaps): an official map is offered when its
                // content prerequisites are met. Its metadata's mapData is not filled before the map is loaded, so it is no criterion.
                bool Offered(MapMetadata m) => m.target != null && game.ArePrerequisitesMet(m);
                report["official_maps"] = maps.Select(m => new { name = m.name, id = m.id.guid.ToString(), path = m.path,
                    offered = Offered(m), map_data_in_metadata = m.target?.mapData != null }).ToArray();
                MapMetadata map = maps.FirstOrDefault(m => (string.IsNullOrEmpty(mapId) || mapId == "auto" || m.id.guid.ToString() == mapId || m.name == mapId) && Offered(m));
                Require(map != null, "No matching playable official map; map= accepts an AssetDatabase.game MapMetadata GUID or exact name");
                report["map"] = new { name = map.name, id = map.id.guid.ToString(), path = map.path, database = map.database.name };
                report["auto_save_original"] = oldAutoSave;
                SharedSettings.instance.general.autoSave = false; // no Apply/Save: never prunes or rewrites user settings/saves
                SharedSettings.instance.userInterface.blockingPopupsEnabled = false;
                report["blocking_popups_original"] = oldBlockingPopups;
                game.onGamePreload += Preload;
                expectingLoad = true;
                Require(await game.Load(GameMode.Game, Purpose.NewGame, map), "Official new map failed to load");
                ownsWorld = !interrupted; CheckWorld();
                if (bindingsSnapshot || preparedCancel)
                {
                    bool CancellationStarted() => cancellation?.Triggered.IsCompleted == true;
                    runningSimulation = host.World.GetExistingSystemManaged<SimulationSystem>();
                    Require(runningSimulation != null, "Loaded native fixture World has no simulation");
                    host.World.GetExistingSystemManaged<Game.UI.InGame.SignatureBuildingUISystem>()?.ClearUnlockedSignature();
                    timeUi = host.World.GetExistingSystemManaged<Game.UI.InGame.TimeUISystem>();
                    focusField = typeof(Game.UI.InGame.TimeUISystem).GetField("m_HasFocus",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    externalPauses = new List<object>(); report["external_pauses"] = externalPauses;
                    var fixture = new Dictionary<string, object> { ["road_travel_tested"] = false,
                        ["native_factory"] = "TransportVehicleSelectData.CreateVehicle", ["temporary_vehicle_created"] = false,
                        ["native_entity_presence_verified"] = false };
                    report["lifecycle_fixture"] = fixture;
                    var fixtureWait = System.Diagnostics.Stopwatch.StartNew();
                    Donor fixtureDonor = null;
                    while (fixtureDonor == null)
                    {
                        CheckWorld(); KeepRunning(-fixtureWait.Elapsed.TotalSeconds); em.CompleteAllTrackedJobs();
                        fixtureDonor = FindDonor(host, em, 0, 0, false, out object donorSearch, false);
                        fixture["donor_search"] = donorSearch;
                        if (fixtureDonor != null) break;
                        if (fixtureWait.Elapsed.TotalSeconds >= 30) throw new TimeoutException("No usable native traffic donor for the lifecycle fixture within 30 seconds");
                        await Task.Delay(100);
                    }
                    if (!CancellationStarted())
                    {
                        if (preparedCancel)
                        {
                            cancellation = new BusRoadPreparedCancel(host, frontPrefab, find(assembly.Rear.Prefab),
                                expectedAssetsMvid, expectedObserverMvid, CheckWorld, () =>
                                {
                                    CheckWorld();
                                    expectingLoad = true; expectedPurpose = Purpose.Cleanup; expectedMode = GameMode.MainMenu;
                                    cancellationCleanup = game.Load(GameMode.MainMenu, Purpose.Cleanup);
                                    return cancellationCleanup;
                                });
                            report["prepared_cancellation"] = cancellation.Evidence;
                        }
                        int token = Environment.TickCount;
                        Spawn(host, em, fixtureDonor, frontPrefabEntity, token);
                        using (EntityQuery own = em.CreateEntityQuery(ComponentType.ReadOnly<OwnedVehicle>()))
                        using (NativeArray<Entity> created = own.ToEntityArray(Allocator.Temp))
                            foreach (Entity entity in created) if (em.GetComponentData<OwnedVehicle>(entity).Token == token)
                            { Require(frontEntity == Entity.Null, "Native fixture token matched multiple vehicles"); frontEntity = entity; }
                        Require(frontEntity != Entity.Null && em.GetComponentData<PrefabRef>(frontEntity).m_Prefab == frontPrefabEntity,
                            "Native factory did not create the requested fixture prefab");
                        fixture["temporary_vehicle_created"] = true; fixture["front_entity"] = frontEntity.ToString();
                        fixture["token"] = token; fixture["donor_entity"] = fixtureDonor.Entity.ToString();
                        if (em.Exists(fixtureDonor.Entity)) em.AddComponent<Deleted>(fixtureDonor.Entity);
                        cameraObject = new GameObject("CDLC native lifecycle fixture camera") { hideFlags = HideFlags.DontSave };
                        inspectionCamera = cameraObject.AddComponent<Camera>(); inspectionCamera.enabled = false;
                        inspectionCamera.nearClipPlane = .1f; inspectionCamera.farClipPlane = 700;
                        BusGlassProbe.Plain(cameraObject.AddComponent<HDAdditionalCameraData>());
                        target = new RenderTexture(640, 400, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                        inspectionCamera.targetTexture = target;
                        PlaceCamera(inspectionCamera, fixtureDonor.Transform, fixtureDonor.Transform);
                        cameras = host.World.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
                        Require(cameras != null, "Loaded native fixture World has no camera update system");
                        oldCamera = cameras.activeCamera; oldController = cameras.activeCameraController;
                        cameras.activeCameraController = null; cameras.activeCamera = inspectionCamera;
                        fixture["camera_contract"] = "Owned actual activeCamera/activeViewer; native entity rendering, without manual batch registration";
                        fixture["fixture_timeout_seconds"] = 30;
                        fixture["preparation_timeout_seconds"] = preparedCancel ? (int?)60 : null;
                        fixtureWait.Restart();
                        bool ready = false;
                        double fixtureReadyAt = -1;
                        while (!CancellationStarted())
                        {
                            CheckWorld(); if (!ready) KeepRunning(-fixtureWait.Elapsed.TotalSeconds); em.CompleteAllTrackedJobs();
                            Require(em.Exists(frontEntity) && !em.HasComponent<Deleted>(frontEntity), "Owned native fixture front disappeared");
                            if (em.HasBuffer<LayoutElement>(frontEntity))
                            {
                                var layout = em.GetBuffer<LayoutElement>(frontEntity);
                                if (layout.Length == 2 && layout[0].m_Vehicle == frontEntity)
                                {
                                    Entity candidate = layout[1].m_Vehicle;
                                    Require(candidate != frontEntity && candidate != Entity.Null && em.Exists(candidate) &&
                                        em.HasComponent<Controller>(candidate) && em.GetComponentData<Controller>(candidate).m_Controller == frontEntity &&
                                        em.HasComponent<PrefabRef>(candidate) && em.GetComponentData<PrefabRef>(candidate).m_Prefab == rearPrefabEntity,
                                        "Native fixture rear does not match the owned front and requested assembly");
                                    rearEntity = candidate;
                                    if (!em.HasComponent<Deleted>(rearEntity) && em.HasComponent<UpdateFrame>(frontEntity) && em.HasComponent<UpdateFrame>(rearEntity) &&
                                        em.HasBuffer<TransformFrame>(frontEntity) && em.GetBuffer<TransformFrame>(frontEntity).Length >= 4 &&
                                        em.HasBuffer<TransformFrame>(rearEntity) && em.GetBuffer<TransformFrame>(rearEntity).Length >= 4 &&
                                        em.HasComponent<Game.Rendering.InterpolatedTransform>(frontEntity) && em.HasComponent<Game.Rendering.InterpolatedTransform>(rearEntity))
                                    {
                                        var f = em.GetComponentData<Game.Rendering.InterpolatedTransform>(frontEntity); var r = em.GetComponentData<Game.Rendering.InterpolatedTransform>(rearEntity);
                                        if (ValidStationaryPose(f) && ValidStationaryPose(r))
                                        {
                                            PlaceCamera(inspectionCamera, new ObjectTransform(f.m_Position, f.m_Rotation), new ObjectTransform(r.m_Position, r.m_Rotation));
                                            if (!ready) fixture["pose_ready_frame"] = Time.frameCount;
                                            runningSimulation.selectedSpeed = 0; ready = true;
                                            fixture["rear_entity"] = rearEntity.ToString(); fixture["native_entity_presence_verified"] = true;
                                        }
                                    }
                                }
                            }
                            inspectionCamera.Render();
                            bool viewerReady = ready && cameras.activeViewer?.camera == inspectionCamera &&
                                math.distance(cameras.position, (float3)inspectionCamera.transform.position) <= .01f;
                            double fixtureElapsed = fixtureWait.Elapsed.TotalSeconds;
                            fixture["viewer_ready"] = viewerReady;
                            fixture["elapsed_seconds"] = fixtureElapsed;
                            if (viewerReady && fixtureReadyAt < 0)
                            {
                                fixtureReadyAt = fixtureElapsed;
                                fixture["ready_frame"] = Time.frameCount;
                                fixture["ready_elapsed_seconds"] = fixtureReadyAt;
                            }
                            if (viewerReady && bindingsSnapshot)
                            {
                                bindingWitness = new BusRoadBindingsSnapshot(host, frontPrefab, find(assembly.Rear.Prefab), expectedAssetsMvid, expectedObserverMvid);
                                report["bindings_snapshot"] = bindingWitness.Evidence;
                                await bindingWitness.CaptureLive(() => { CheckWorld(); inspectionCamera.Render(); }, 60);
                                break;
                            }
                            if (fixtureReadyAt < 0 && fixtureElapsed >= 30)
                                throw new TimeoutException("Native lifecycle entities and active viewer were not ready within 30 seconds");
                            if (preparedCancel && fixtureReadyAt >= 0)
                            {
                                double preparationElapsed = fixtureElapsed - fixtureReadyAt;
                                fixture["preparation_wait_seconds"] = preparationElapsed;
                                if (preparationElapsed >= 60)
                                    throw new TimeoutException("No target private native preparation was observed within 60 seconds after fixture readiness");
                            }
                            await Task.Delay(20);
                        }
                    }
                    if (preparedCancel) await cancellation.Triggered;
                    success = true;
                }
                else
                {
                var simulation = host.World.GetOrCreateSystemManaged<SimulationSystem>();
                simulation.selectedSpeed = 1f;
                runningSimulation = simulation;
                host.World.GetExistingSystemManaged<Game.UI.InGame.SignatureBuildingUISystem>()?.ClearUnlockedSignature();
                timeUi = host.World.GetExistingSystemManaged<Game.UI.InGame.TimeUISystem>();
                focusField = typeof(Game.UI.InGame.TimeUISystem).GetField("m_HasFocus",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                report["focus_override_available"] = timeUi != null && focusField != null && focusField.FieldType == typeof(bool);
                var setupWatch = System.Diagnostics.Stopwatch.StartNew();
                // Pauses seen while waiting for a route are recorded with negative seconds (time before the observation).
                externalPauses = new List<object>();
                report["external_pauses"] = externalPauses;
                Donor donor = null;
                object routeSearch = null;
                // The spawned bus drives at its own top speed (lane limits on official-map highways are far higher), so the
                // route window is what this bus covers during the observation, not what the donor car could.
                float busSpeed = em.HasComponent<CarData>(frontPrefabEntity) ? em.GetComponentData<CarData>(frontPrefabEntity).m_MaxSpeed : 0f;
                Require(math.isfinite(busSpeed) && busSpeed > 1f, "Requested bus prefab has no usable CarData max speed");
                report["bus_max_speed_m_s"] = busSpeed;
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (donor == null && wait.Elapsed.TotalSeconds < 180)
                {
                    CheckWorld(); KeepRunning(-setupWatch.Elapsed.TotalSeconds); em.CompleteAllTrackedJobs();
                    donor = FindDonor(host, em, seconds, busSpeed, stationaryGlass, out routeSearch);
                    report["route_search"] = routeSearch;
                    if (donor == null) await Task.Delay(500);
                }
                if (donor == null) throw new RouteInsufficientException(stationaryGlass
                    ? "No ground-supported native traffic donor satisfying the route constraints appeared within 180 seconds; inspect route_search road-light rejections"
                    : "No native traffic route with enough measured remaining length and an upcoming turn appeared within 180 seconds");
                report["native_path_donor"] = new { entity = donor.Entity.ToString(), lane = donor.Lane.m_Lane.ToString(), owner = donor.Owner.m_Owner.ToString(),
                    target = donor.Target.m_Target.ToString(), position = A(donor.Transform.m_Position), path_length = donor.Path.Length, native_path_turn_degrees = donor.Turn,
                    replaced_in_disposable_world = true };
                report["selected_route_geometry"] = donor.Route;
                int token = Environment.TickCount;
                // Simulation-progress evidence: a stalled simulation (pending pathfind results hold SimulationSystem at
                // pendingSimulationFrame-1) must not be read as a vehicle defect, nor hidden.
                var pathResults = host.World.GetOrCreateSystemManaged<PathfindResultSystem>();
                object SimState() => new { frame = simulation.frameIndex, selected_speed = simulation.selectedSpeed,
                    pending_pathfind_frame = pathResults.pendingSimulationFrame == uint.MaxValue ? -1L : (long)pathResults.pendingSimulationFrame,
                    pending_pathfind_requests = pathResults.pendingRequestCount };
                report["simulation_before_spawn"] = SimState();
                Spawn(host, em, donor, frontPrefabEntity, token);
                using (EntityQuery own = em.CreateEntityQuery(ComponentType.ReadOnly<OwnedVehicle>()))
                using (NativeArray<Entity> created = own.ToEntityArray(Allocator.Temp))
                    foreach (Entity entity in created) if (em.GetComponentData<OwnedVehicle>(entity).Token == token) frontEntity = entity;
                Require(frontEntity != Entity.Null, "Native vehicle factory produced no tagged bus");
                Require(em.GetComponentData<PrefabRef>(frontEntity).m_Prefab == frontPrefabEntity, "Native selector did not choose the requested bus");
                // Replace only this donor in our disposable world, so the bus is not spawned on top of a moving car.
                if (em.Exists(donor.Entity)) em.AddComponent<Deleted>(donor.Entity);
                cameraObject = new GameObject("CDLC native road probe camera") { hideFlags = HideFlags.DontSave };
                Camera camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.nearClipPlane = .1f; camera.farClipPlane = 700;
                BusGlassProbe.Plain(cameraObject.AddComponent<HDAdditionalCameraData>());
                target = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                camera.targetTexture = target;
                cameras = host.World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
                oldCamera = cameras.activeCamera; oldController = cameras.activeCameraController;
                cameras.activeCameraController = null; cameras.activeCamera = camera; inspectionCamera = camera;
                report["camera_contract"] = "The inspection camera is the real CameraUpdateSystem.activeCamera/activeViewer; native culling and the production AI joint share it. No fixture joint is created.";
                if (stationaryGlass)
                {
                    rearEntity = await WaitStationaryBus(host, frontEntity, frontPrefabEntity, rearPrefabEntity,
                        assembly, camera, cameras, report, CheckWorld, () => KeepRunning(-setupWatch.Elapsed.TotalSeconds));
                    takingControl = true;
                    tookTestVehicle = true;
                    try
                    {
                        success = await RunGlassCycle(host, frontEntity, rearEntity, assembly, camera, target, cameras,
                            directory, report, CheckWorld, Interrupt, () => ownsWorld && !interrupted && game.gameMode == GameMode.Game &&
                                game.state == GameManager.State.WorldReady && !game.isGameLoading, glassRoadLights, glassOptics);
                    }
                    finally { takingControl = false; }
                    tookTestVehicle = Core.IsDriving() && Core.Vehicle() == frontEntity;
                }
                else
                {
                float3 start = em.GetComponentData<ObjectTransform>(frontEntity).m_Position;
                uint firstFrame = simulation.frameIndex; float distance = 0, maxBend = 0, maxGap = 0, rearTravel = 0;
                float3 last = start, firstRear = 0; bool haveRear = false; int frameChanges = 0; uint lastFrame = firstFrame;
                // Lane health. The spawned bus follows its copied donor path for about ten seconds and then (every run so far, on
                // two maps) stops advancing along a lane: its lane position stays at the start of a short piece and it coasts straight
                // on, off the road, at its top speed (confirmed against the terrain). Whatever is taken over or
                // released after that is not on the road, so the probe records when the lane stalls and takes over earlier, on the lane.
                Entity laneSeen = Entity.Null; float lanePositionSeen = -1f; double laneMovedAt = 0, leftLaneAt = -1; bool onLane = true, early = false;
                const float kLaneMovedPosition = .01f, kLaneStalledSeconds = 1.0f, kEarlyTakeoverSpeed = 20f;
                var joints = host.World.GetOrCreateSystemManaged<ArticulationSystem>();
                // This disposable world is the probe's own. Third-party mods subscribed by the player (e.g. ones that set the
                // simulation speed) or a game error dialog can pause it; that is no vehicle result, so the probe resumes it and
                // records every pause it saw.


                int visibleJointSamples = 0, jointVertices = 0;
                double nextShot = 3;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < seconds)
                {
                    await Task.Delay(250); CheckWorld(); em.CompleteAllTrackedJobs();
                    KeepRunning(watch.Elapsed.TotalSeconds);
                    RequireRoadVehicle(em, frontEntity, "road sample");
                    if (!em.HasBuffer<LayoutElement>(frontEntity) || em.GetBuffer<LayoutElement>(frontEntity).Length != 2)
                    { Require(watch.Elapsed.TotalSeconds < 8, "Normal Created-vehicle integration did not produce two real layout entities"); continue; }
                    rearEntity = em.GetBuffer<LayoutElement>(frontEntity)[1].m_Vehicle;
                    Require(rearEntity != frontEntity && em.Exists(rearEntity) && em.GetComponentData<PrefabRef>(rearEntity).m_Prefab == rearPrefabEntity,
                        "Rear entity/prefab differs from the explicit native assembly");
                    Require(em.GetComponentData<Controller>(rearEntity).m_Controller == frontEntity, "Rear native Controller does not point to front");
                    ObjectTransform f = em.GetComponentData<ObjectTransform>(frontEntity), r = em.GetComponentData<ObjectTransform>(rearEntity);
                    PlaceCamera(camera, f, r);
                    if (!haveRear) { firstRear = r.m_Position; haveRear = true; }
                    distance += math.distance(last, f.m_Position); last = f.m_Position;
                    rearTravel = math.distance(firstRear, r.m_Position);
                    float gap = math.distance(f.m_Position + math.rotate(f.m_Rotation, assembly.FrontAnchor), r.m_Position + math.rotate(r.m_Rotation, assembly.RearAnchor));
                    float angle = math.degrees(math.acos(math.clamp(math.dot(math.forward(f.m_Rotation), math.forward(r.m_Rotation)), -1f, 1f)));
                    maxGap = math.max(maxGap, gap); maxBend = math.max(maxBend, angle);
                    uint frame = simulation.frameIndex; if (frame != lastFrame) frameChanges++; lastFrame = frame;
                    bool jointVisible = joints.HasVisibleJoint(frontEntity, out int vertexCount);
                    if (jointVisible) visibleJointSamples++;
                    jointVertices = math.max(jointVertices, vertexCount);
                    if (watch.Elapsed.TotalSeconds >= 10 && !jointVisible)
                        report["joint_failure_state"] = NativeJointProbeState(host, frontEntity, rearEntity, assembly, cameras);
                    Require(watch.Elapsed.TotalSeconds < 10 || jointVisible, "Production AI joint is not visible from the actual active viewer");
                    Require(math.all(math.isfinite(f.m_Position)) && math.all(math.isfinite(r.m_Position)) && gap <= assembly.MaxSeparation + .015f, "Live native attachment separated or became nonfinite");
                    Require(em.HasBuffer<Game.Objects.TransformFrame>(rearEntity) && em.GetBuffer<Game.Objects.TransformFrame>(rearEntity).Length > 0,
                        "Rear lacks native interpolation frames");
                    var lane = em.GetComponentData<CarCurrentLane>(frontEntity);
                    float speedNow = math.length(em.GetComponentData<Moving>(frontEntity).m_Velocity);
                    if (lane.m_Lane != laneSeen || math.abs(lane.m_CurvePosition.x - lanePositionSeen) > kLaneMovedPosition)
                    { laneSeen = lane.m_Lane; lanePositionSeen = lane.m_CurvePosition.x; laneMovedAt = watch.Elapsed.TotalSeconds; }
                    double laneStalled = watch.Elapsed.TotalSeconds - laneMovedAt;
                    onLane = !(speedNow > 3f && laneStalled > kLaneStalledSeconds);
                    if (!onLane && leftLaneAt < 0) leftLaneAt = laneMovedAt;
                    var frontPath = em.GetComponentData<PathOwner>(frontEntity);
                    samples.Add(new { seconds = watch.Elapsed.TotalSeconds, simulation_frame = frame, simulation = SimState(),
                        front_path_state = frontPath.m_State.ToString(), front_path_index = frontPath.m_ElementIndex,
                        front_lane_flags = lane.m_LaneFlags.ToString(), rear_has_path_owner = em.HasComponent<PathOwner>(rearEntity),
                        front = A(f.m_Position), rear = A(r.m_Position),
                        front_rotation = A(f.m_Rotation.value), rear_rotation = A(r.m_Rotation.value), attachment_gap_m = gap, bend_degrees = angle,
                        lane = lane.m_Lane.ToString(), lane_position = A(lane.m_CurvePosition), speed_m_s = speedNow, lane_stalled_seconds = laneStalled, on_lane = onLane,
                        rear_speed_m_s = math.length(em.GetComponentData<Moving>(rearEntity).m_Velocity), production_joint_visible = jointVisible,
                        production_joint_vertices = vertexCount });
                    if (watch.Elapsed.TotalSeconds >= nextShot)
                    {
                        CheckWorld(); await Capture(camera, target, directory, shots, f, r, frame, angle, CheckWorld,
                            () => joints.HasVisibleJoint(frontEntity)); nextShot += 20;
                    }
                    if (watch.Elapsed.TotalSeconds >= 25 && distance > 60 && rearTravel > 40 && maxBend > 5 && frameChanges > 30) break;
                    // still on its lane, at speed, with the joint drawn: take over now rather than after it has left the road
                    if (onLane && speedNow >= kEarlyTakeoverSpeed && distance > 60 && rearTravel > 40 && frameChanges > 30 && visibleJointSamples > 20)
                    { early = true; break; }
                }
                report["metrics"] = new { front_distance_m = distance, rear_displacement_m = rearTravel, max_bend_degrees = maxBend,
                    max_attachment_gap_m = maxGap, changed_simulation_samples = frameChanges, simulation_frames = simulation.frameIndex - firstFrame,
                    front_entity = frontEntity.ToString(), rear_entity = rearEntity.ToString(), production_joint_visible_samples = visibleJointSamples, production_joint_vertices = jointVertices,
                    left_lane_at_seconds = leftLaneAt >= 0 ? (double?)leftLaneAt : null, takeover_on_lane = onLane, takeover_early_on_lane = early,
                    bend_requirement = early ? "waived: the take-over was kept on its lane at speed; bends are covered by the Assembly stage, not by this run" : "required (max bend above 3 degrees)" };
                Require(distance > 30 && rearTravel > 20 && (early || maxBend > 3) && frameChanges > 20 && visibleJointSamples > 20,
                    "Insufficient real road travel/turning/joint samples; this is not a road PASS");
                Require(!Core.IsDriving(), "A player vehicle is already controlled; cannot acquire a different vehicle for inspection");
                takingControl = true;
                cameras.activeCamera = oldCamera; cameras.activeCameraController = oldController;
                Core.Drive(frontEntity); tookTestVehicle = true;
                var cab = host.World.GetOrCreateSystemManaged<BusCabSystem>();
                object cabEvidence = null;
                var takeover = System.Diagnostics.Stopwatch.StartNew();
                while (takeover.Elapsed.TotalSeconds < 20)
                {
                    await Task.Delay(250); CheckWorld(); KeepRunning(watch.Elapsed.TotalSeconds);
                    if (Core.IsDriving() && Core.Vehicle() == frontEntity && !cabViewRequested)
                    {
                        originalCabPreference = Core.CabViewPreference();
                        cabViewRequested = true; Core.SetCabViewRequested(cabViewOwner, true);
                        Core.SelectDriverSeat();     // the driver's eye, whatever camera position a pack may add (Core API 14)
                    }
                    if (cabViewRequested && Core.CabViewActive() &&
                        cab.TryGetAssemblyProbeState(frontEntity, rearEntity, out cabEvidence)) break;
                }
                Require(Core.IsDriving() && Core.Vehicle() == frontEntity && Core.CabViewActive() &&
                    cab.TryGetAssemblyProbeState(frontEntity, rearEntity, out cabEvidence),
                    "Driver takeover did not produce an active driver-eye view of the same two-section cab, glass and production joint");
                report["takeover"] = cabEvidence;
                // Where a cab attachment gap comes from (render layer, not the attachment): sway of each section, Core's kneel
                // applied to the front, both speeds, and the gap between the two interpolated transforms without sway.
                {
                    em.CompleteAllTrackedJobs();
                    object Sway(Entity e) => em.HasComponent<Game.Rendering.Swaying>(e) ? (object)A(em.GetComponentData<Game.Rendering.Swaying>(e).m_SwayPosition) : null;
                    // roll at the joint: the turntable locks it, so the two sections' roll sway should agree (Core 0.13.13 couples it)
                    float? rollDifference = em.HasComponent<Game.Rendering.Swaying>(frontEntity) && em.HasComponent<Game.Rendering.Swaying>(rearEntity)
                        ? math.degrees(em.GetComponentData<Game.Rendering.Swaying>(frontEntity).m_SwayPosition.x - em.GetComponentData<Game.Rendering.Swaying>(rearEntity).m_SwayPosition.x)
                        : (float?)null;
                    float? interpolatedGap = null;
                    if (em.HasComponent<Game.Rendering.InterpolatedTransform>(frontEntity) && em.HasComponent<Game.Rendering.InterpolatedTransform>(rearEntity))
                    {
                        var fi = em.GetComponentData<Game.Rendering.InterpolatedTransform>(frontEntity);
                        var ri = em.GetComponentData<Game.Rendering.InterpolatedTransform>(rearEntity);
                        interpolatedGap = math.distance(fi.m_Position + math.rotate(fi.m_Rotation, assembly.FrontAnchor),
                            ri.m_Position + math.rotate(ri.m_Rotation, assembly.RearAnchor));
                    }
                    var ft = em.GetComponentData<ObjectTransform>(frontEntity); var rt = em.GetComponentData<ObjectTransform>(rearEntity);
                    report["takeover_gap_sources"] = new {
                        front_sway = Sway(frontEntity), rear_sway = Sway(rearEntity), roll_difference_deg = rollDifference, kneel_level = Core.KneelLevel(),
                        front_speed_m_s = math.length(em.GetComponentData<Moving>(frontEntity).m_Velocity),
                        rear_speed_m_s = math.length(em.GetComponentData<Moving>(rearEntity).m_Velocity),
                        simulation_transform_gap_m = math.distance(ft.m_Position + math.rotate(ft.m_Rotation, assembly.FrontAnchor),
                            rt.m_Position + math.rotate(rt.m_Rotation, assembly.RearAnchor)),
                        interpolated_transform_gap_m = interpolatedGap,
                        scope = "Sampled in the frame after the cab reported its takeover state; cab gap is in takeover.attachment_gap_m" };
                }
                report["driver_camera"] = new { cab_view_active = Core.CabViewActive(), temporary_owner = cabViewOwner,
                    saved_cab_preference = originalCabPreference, preference_unchanged = Core.CabViewPreference() == originalCabPreference,
                    persistent_view_setter_used = false };
                Require(Core.CabViewPreference() == originalCabPreference, "Cab preference changed during the temporary camera request");
                // The roll the turntable locks, over time rather than at one instant: 1.5 s of samples after the cab took over (Core's
                // roll coupling follows the front's roll with a time constant of about 0.12 s, so one sample taken while the front's
                // roll is still building shows the lag, not the coupling). The settled value is the last 0.5 s.
                {
                    CabBlocks.Reset();      // the cab's own time per block over the same window (frames, ms a frame): report.cab_blocks
                    var rolls = new List<(double t, float diff, float front, float rear, float speed)>();
                    var rollWatch = System.Diagnostics.Stopwatch.StartNew();
                    while (rollWatch.Elapsed.TotalSeconds < 1.5)
                    {
                        await Task.Delay(50); CheckWorld(); em.CompleteAllTrackedJobs();
                        Require(Core.IsDriving() && Core.Vehicle() == frontEntity, "Driving ended during the roll sampling window");
                        if (!em.HasComponent<Game.Rendering.Swaying>(frontEntity) || !em.HasComponent<Game.Rendering.Swaying>(rearEntity)) continue;
                        float fr = em.GetComponentData<Game.Rendering.Swaying>(frontEntity).m_SwayPosition.x, rr = em.GetComponentData<Game.Rendering.Swaying>(rearEntity).m_SwayPosition.x;
                        rolls.Add((rollWatch.Elapsed.TotalSeconds, math.degrees(fr - rr), math.degrees(fr), math.degrees(rr), math.length(em.GetComponentData<Moving>(frontEntity).m_Velocity)));
                    }
                    report["cab_blocks"] = new { table = CabBlocks.Table(), described = CabBlocks.Describe(), window_seconds = 1.5, scope = "BusCabSystem.OnUpdate per block, the road probe's own take-over of a two-section K18 (menu-free real game, driver view), ms a frame over the frames of the roll window" };
                    var settled = rolls.Where(r => r.t >= 1.0).ToList();
                    report["roll_window"] = new {
                        samples = rolls.Count, window_seconds = 1.5,
                        max_abs_difference_deg = rolls.Count > 0 ? rolls.Max(r => Math.Abs(r.diff)) : (float?)null,
                        settled_samples = settled.Count,
                        settled_max_abs_difference_deg = settled.Count > 0 ? settled.Max(r => Math.Abs(r.diff)) : (float?)null,
                        max_abs_front_roll_deg = rolls.Count > 0 ? rolls.Max(r => Math.Abs(r.front)) : (float?)null,
                        max_abs_rear_roll_deg = rolls.Count > 0 ? rolls.Max(r => Math.Abs(r.rear)) : (float?)null,
                        speed_m_s = rolls.Count > 0 ? rolls[rolls.Count - 1].speed : (float?)null,
                        series = rolls.Select(r => new { t = Math.Round(r.t, 2), diff_deg = r.diff, front_deg = r.front, rear_deg = r.rear }).ToArray(),
                        scope = "Roll sway.x of the two native sections after the cab took over, straight or gently curved road, no kneel; target: settled difference below 0.5 degree" };
                }
                string cabShot = Path.Combine(directory, "road-driver-takeover.png");
                CheckWorld(); Require(Core.CabViewActive(), "Driver eye became inactive before its screenshot");
                ScreenCapture.CaptureScreenshot(cabShot);
                for (int i = 0; i < 20 && !File.Exists(cabShot); i++)
                { await Task.Delay(100); CheckWorld(); Require(Core.CabViewActive(), "Driver eye became inactive while capturing its screenshot"); }
                Require(File.Exists(cabShot), "Actual driver-view screenshot was not written");
                shots.Add(new { filename = Path.GetFileName(cabShot), actual_driver_view = Core.CabViewActive(), synthetic_vehicle_renderers = false });
                Core.SetCabViewRequested(cabViewOwner, false); cabViewRequested = false;
                Require(Core.CabViewPreference() == originalCabPreference, "Removing the temporary camera request changed the cab preference");
                report["temporary_camera_request_released"] = true;
                // Stay driving until the cab's delayed work has had its turn (the wrap template is written 4 s after a take-over), then
                // record what it wrote: the files of the model's template folder, their sizes and write times.
                {
                    var hold = System.Diagnostics.Stopwatch.StartNew();
                    while (hold.Elapsed.TotalSeconds < 6)
                    {
                        await Task.Delay(250); CheckWorld(); KeepRunning(watch.Elapsed.TotalSeconds);
                        Require(Core.IsDriving() && Core.Vehicle() == frontEntity, "Driving ended while waiting for the cab's delayed work");
                    }
                    string folder = WrapPackage.ModelFolder(BusSkins.Root, BusSkins.Model) ?? BusSkins.Root;
                    string template = Path.Combine(folder, BusSkins.kTemplate + ".zip");
                    report["wrap_template"] = new {
                        folder, model = BusSkins.Model, exists = File.Exists(template),
                        files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.zip").Select(f => new { name = Path.GetFileName(f), bytes = new FileInfo(f).Length,
                            written_utc = File.GetLastWriteTimeUtc(f).ToString("o") }).ToArray() : null,
                        scope = "Written by the cab 4 s after the take-over (BusCabSystem.m_TemplateAt) into the driven model's wraps folder; this run stayed 6 s more in the driver's seat" };
                }
                Core.Release();
                for (int i = 0; i < 20 && Core.IsDriving(); i++) { await Task.Delay(100); CheckWorld(); }
                Require(!Core.IsDriving(), "Driving Core did not release the test vehicle");
                tookTestVehicle = false;
                cameras.activeCameraController = null; cameras.activeCamera = camera; takingControl = false;
                em.CompleteAllTrackedJobs();
                float3 releasedAt = em.GetComponentData<ObjectTransform>(frontEntity).m_Position;
                bool continued = false;
                var releaseWait = System.Diagnostics.Stopwatch.StartNew();
                while (releaseWait.Elapsed.TotalSeconds < 20)
                {
                    await Task.Delay(250); CheckWorld(); KeepRunning(watch.Elapsed.TotalSeconds); em.CompleteAllTrackedJobs();
                    RequireRoadVehicle(em, frontEntity, "post-release sample");
                    Require(em.Exists(rearEntity), "Test rear entity disappeared after release");
                    var layout = em.GetBuffer<LayoutElement>(frontEntity);
                    Require(layout.Length == 2 && layout[0].m_Vehicle == frontEntity && layout[1].m_Vehicle == rearEntity, "Release changed the native two-entity assembly");
                    ObjectTransform fp = em.GetComponentData<ObjectTransform>(frontEntity), rp = em.GetComponentData<ObjectTransform>(rearEntity);
                    PlaceCamera(camera, fp, rp);
                    if (math.distance(releasedAt, fp.m_Position) > 3 && joints.HasVisibleJoint(frontEntity)) { continued = true; break; }
                }
                Require(continued, "Same native assembly did not resume motion with its AI joint after release");
                report["release"] = new { same_front = frontEntity.ToString(), same_rear = rearEntity.ToString(), native_motion_resumed = continued, core_released = !Core.IsDriving() };
                if (glassCycle)
                {
                    takingControl = true;
                    tookTestVehicle = true;
                    try
                    {
                        success = await RunGlassCycle(host, frontEntity, rearEntity, assembly, camera, target, cameras,
                            directory, report, CheckWorld, Interrupt, () => ownsWorld && !interrupted && game.gameMode == GameMode.Game &&
                                game.state == GameManager.State.WorldReady && !game.isGameLoading, glassRoadLights, glassOptics);
                    }
                    finally { takingControl = false; }
                    tookTestVehicle = Core.IsDriving() && Core.Vehicle() == frontEntity;
                }
                else success = true;
                }
                }
            }
            catch (Exception error)
            {
                failure = error.ToString(); report["error"] = failure;
                failureKind = error is RouteInsufficientException ? "ROUTE_INSUFFICIENT" : error is OperationCanceledException ? "INTERRUPTED" : "PROBE_FAILURE";
            }
            finally
            {
                if (cancellation != null && cancellationCleanup == null)
                {
                    try { cancellation.Dispose(); }
                    catch (Exception e) { errors.Add("Disarm prepared checkpoint before ordinary cleanup: " + e); }
                }
                if (focusOverridden)
                {
                    try { focusField.SetValue(timeUi, Application.isFocused); report["focus_restored_to"] = Application.isFocused; }
                    catch (Exception e) { errors.Add("Restore TimeUISystem focus: " + e); }
                }
                report["focus_override"] = new { overridden = focusOverridden, times = focusOverrides,
                    scope = "Disposable world only: TimeUISystem.m_HasFocus set true while the probe observed/drove; restored to Application.isFocused" };
                if (cabViewRequested)
                {
                    try
                    {
                        Core.SetCabViewRequested(cabViewOwner, false); cabViewRequested = false;
                        report["temporary_camera_request_released"] = true;
                        report["cab_preference_unchanged_before_cleanup_release"] = Core.CabViewPreference() == originalCabPreference;
                    }
                    catch (Exception e) { errors.Add("Release temporary cab view: " + e); }
                }
                try
                {
                    if (tookTestVehicle && !interrupted && Core.IsDriving() && Core.Vehicle() == frontEntity)
                        Core.Release();
                }
                catch (Exception e) { errors.Add("Release owned test vehicle: " + e); }
                if (cancellationCleanup != null)
                {
                    try
                    {
                        Require(await cancellationCleanup, "Prepared cancellation's normal Cleanup failed to load the main menu");
                        if (interrupted) throw new OperationCanceledException("User took over the prepared cancellation transition");
                        ownsWorld = false;
                        report["returned_to_main_menu"] = true;
                        await cancellation.SnapshotAfterCleanup(() =>
                        {
                            if (interrupted) throw new OperationCanceledException("User took over after prepared cancellation returned to the main menu");
                        });
                    }
                    catch (Exception e) { errors.Add("Prepared cancellation cleanup/evidence: " + e); }
                    finally { ownsWorld = false; }
                }
                try
                {
                    if (ownsWorld && !interrupted && !game.isGameLoading && game.gameMode == GameMode.Game &&
                        game.state == GameManager.State.WorldReady && cameras != null && cameras.activeCamera == inspectionCamera)
                    {
                        cameras.activeCamera = oldCamera;
                        cameras.activeCameraController = oldController;
                    }
                }
                catch (Exception e) { errors.Add("Restore owned test camera: " + e); }
                if ((interrupted || cancellationCleanup != null) && cameraObject != null && cameras != null &&
                    (cameras.activeCamera == inspectionCamera || cameras.activeViewer?.camera == inspectionCamera))
                {
                    var retirementReport = new Dictionary<string, object>();
                    report["camera_retirement"] = retirementReport;
                    var retirement = new BusRoadGlassCameraRetirement(host.World, cameras, cameraObject, target,
                        Path.Combine(directory, "camera-retirement.json"), retirementReport);
                    cameraObject = null; target = null;
                    try { retirement.Dispose(); }
                    catch (Exception e) { errors.Add("Retire active test camera: " + e); }
                }
                inspectionCamera = null;
                try
                {
                    if (cameraObject != null)
                    {
                        var c = cameraObject.GetComponent<Camera>();
                        if (c != null) c.targetTexture = null;
                    }
                }
                catch (Exception e) { errors.Add("Detach test camera target: " + e); }
                try { if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject); }
                catch (Exception e) { errors.Add("Destroy test camera: " + e); }
                try { if (target != null) target.Release(); }
                catch (Exception e) { errors.Add("Release test render texture: " + e); }
                try { if (target != null) UnityEngine.Object.Destroy(target); }
                catch (Exception e) { errors.Add("Destroy test render texture: " + e); }
                if (cancellationCleanup == null && ownsWorld && !interrupted)
                {
                    try
                    {
                        em.CompleteAllTrackedJobs();
                        foreach (Entity e in new[] { rearEntity, frontEntity }) if (e != Entity.Null && em.Exists(e) && !em.HasComponent<Deleted>(e)) em.AddComponent<Deleted>(e);
                    }
                    catch (Exception e) { errors.Add("Delete owned test vehicles: " + e); }
                    try
                    {
                        await Task.Delay(100); CheckWorld();
                        expectingLoad = true; expectedPurpose = Purpose.Cleanup; expectedMode = GameMode.MainMenu;
                        Require(await game.Load(GameMode.MainMenu, Purpose.Cleanup), "Could not return from disposable test world");
                        ownsWorld = false;
                        report["returned_to_main_menu"] = true;
                        if (bindingWitness?.LiveCaptured == true)
                            await bindingWitness.SnapshotAfterCleanup(() =>
                            {
                                if (interrupted) throw new OperationCanceledException("User took over after binding snapshot returned to the main menu");
                            });
                    }
                    catch (Exception e) { errors.Add("Return from owned test world: " + e); }
                }
                try { cancellation?.Dispose(); }
                catch (Exception e) { errors.Add("Disarm prepared cancellation checkpoint: " + e); }
                try { bindingWitness?.Dispose(); }
                catch (Exception e) { errors.Add("Remove binding-snapshot render observer: " + e); }
                if (report.TryGetValue("camera_retirement", out object retirementEvidence) &&
                    ((Dictionary<string, object>)retirementEvidence).TryGetValue("pending", out object pendingRetirement) && (bool)pendingRetirement)
                    errors.Add("Owned test camera retirement is still pending; resource cleanup is incomplete");
                try { game.onGamePreload -= Preload; }
                catch (Exception e) { errors.Add("Remove test preload handler: " + e); }
                bool? autoSaveRestored = null, blockingPopupsRestored = null;
                try
                {
                    SharedSettings.instance.general.autoSave = oldAutoSave;
                    autoSaveRestored = SharedSettings.instance.general.autoSave == oldAutoSave;
                }
                catch (Exception e) { errors.Add("Restore auto save: " + e); }
                try
                {
                    SharedSettings.instance.userInterface.blockingPopupsEnabled = oldBlockingPopups;
                    blockingPopupsRestored = SharedSettings.instance.userInterface.blockingPopupsEnabled == oldBlockingPopups;
                }
                catch (Exception e) { errors.Add("Restore blocking popups: " + e); }
                report["blocking_popups_restored"] = blockingPopupsRestored;
                report["user_interrupted"] = interrupted; report["cleanup_errors"] = errors;
                report["failure_kind"] = failureKind;
                report["auto_save_restored"] = autoSaveRestored;
                report["status"] = interrupted ? "INTERRUPTED" : success && errors.Count == 0 ? "PASS" : "FAIL";
                File.WriteAllText(Path.Combine(directory, "road-report.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            return (success && !interrupted && errors.Count == 0 ? "ok " : "failed ") +
                (bindingsSnapshot ? "native glass binding snapshot " : preparedCancel ? "prepared native glass cancellation " : stationaryGlass ? "stationary native glass fixture " : "native road smoke ") + directory + (failure == null ? "" : ": " + failure.Split('\n')[0]);
        }

        static Donor FindDonor(SystemBase host, EntityManager em, int seconds, float busSpeed, bool requireRoadLighting, out object search, bool requireTravelRoute = true)
        {
            using (EntityQuery query = em.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<CarCurrentLane>(), ComponentType.ReadOnly<PathOwner>(), ComponentType.ReadOnly<PathInformation>(),
                    ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Target>(), ComponentType.ReadOnly<ObjectTransform>(), ComponentType.ReadOnly<Moving>(), ComponentType.ReadOnly<PathElement>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<TripSource>(), ComponentType.ReadOnly<Unspawned>(), ComponentType.ReadOnly<Controller>() } }))
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                Donor best = null;
                int eligible = 0, insufficient = 0, lightingTested = 0, lightingRejected = 0;
                var lightingRejections = new List<object>();
                object selectedLighting = null;
                RouteMeasure longestRejected = null;
                foreach (Entity entity in entities)
                {
                    // This fixture replaces one native traffic entity, not an unrelated articulated
                    // assembly whose trailer would otherwise remain on the copied lane.
                    if (em.HasBuffer<LayoutElement>(entity) && em.GetBuffer<LayoutElement>(entity).Length > 1) continue;
                    var lane = em.GetComponentData<CarCurrentLane>(entity); var pathOwner = em.GetComponentData<PathOwner>(entity);
                    var target = em.GetComponentData<Target>(entity); var owner = em.GetComponentData<Owner>(entity);
                    var path = em.GetBuffer<PathElement>(entity);
                    if (lane.m_Lane == Entity.Null || !em.Exists(target.m_Target) || !em.Exists(owner.m_Owner) ||
                        !em.HasComponent<Game.Buildings.TrafficSpawner>(owner.m_Owner) ||
                        (pathOwner.m_State & (PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Stuck)) != 0 ||
                        pathOwner.m_ElementIndex < 0 || pathOwner.m_ElementIndex > path.Length ||
                        math.length(em.GetComponentData<Moving>(entity).m_Velocity) < 2) continue;
                    eligible++;
                    var transform = em.GetComponentData<ObjectTransform>(entity);
                    CarNavigationLane[] navigation = em.HasBuffer<CarNavigationLane>(entity)
                        ? em.GetBuffer<CarNavigationLane>(entity).AsNativeArray().ToArray() : Array.Empty<CarNavigationLane>();
                    RouteMeasure route = requireTravelRoute ? MeasureRoute(em, lane, navigation, path, pathOwner.m_ElementIndex, transform.m_Position,
                        math.length(em.GetComponentData<Moving>(entity).m_Velocity), busSpeed, seconds) : null;
                    if (requireTravelRoute && !route.sufficient)
                    {
                        insufficient++;
                        if (longestRejected == null || route.remaining_curve_length_m > longestRejected.remaining_curve_length_m) longestRejected = route;
                        continue;
                    }
                    object lighting = null;
                    if (requireRoadLighting)
                    {
                        lightingTested++;
                        if (!RoadGlassLights.CanPrepare(host, transform.m_Position, out lighting))
                        {
                            lightingRejected++;
                            if (lightingRejections.Count < 24) lightingRejections.Add(new { entity = entity.ToString(), lane = lane.m_Lane.ToString(),
                                position = A(transform.m_Position), road_light_preflight = lighting });
                            continue;
                        }
                    }
                    // Prefer the earliest usable bend, not a distant maximum angle near the route end.
                    if (best != null && (!requireTravelRoute || best.Route.turn_distance_m <= route.turn_distance_m)) continue;
                    selectedLighting = lighting;
                    best = new Donor { Entity = entity, Transform = em.GetComponentData<ObjectTransform>(entity), Owner = owner, Target = target, Lane = lane,
                        PathOwner = pathOwner, Information = em.GetComponentData<PathInformation>(entity), Path = path.AsNativeArray().ToArray(),
                        Navigation = navigation,
                        Turn = route?.turn_degrees ?? 0, Route = route };
                }
                search = new { eligible_moving_donors = eligible, insufficient_routes = insufficient,
                    longest_rejected_route = longestRejected, selected = best != null,
                    reason = best != null ? null : eligible == 0 ? "No eligible moving native traffic donor"
                        : requireRoadLighting && lightingRejected > 0 ? "No route-qualified donor has a ground-supported road-light site"
                        : "No donor satisfies the measured route length and turn constraints",
                    travel_route_required = requireTravelRoute,
                    road_lighting_required = requireRoadLighting, road_lighting_tested = lightingTested, road_lighting_rejected = lightingRejected,
                    road_lighting_rejections = lightingRejections, road_lighting_rejections_dropped = lightingRejected - lightingRejections.Count,
                    selected_road_lighting = selectedLighting,
                    scope = requireTravelRoute ? "Conservative curve-only remaining length; gaps are not counted. Native route expiry is not a model defect."
                        : "Native placement donor only; no route length, turning, lighting or road travel acceptance." };
                return best;
            }
        }

        static RouteMeasure MeasureRoute(EntityManager em, CarCurrentLane current, CarNavigationLane[] navigation,
            DynamicBuffer<PathElement> path, int first, float3 position, float speed, float busSpeed, int seconds)
        {
            var result = new RouteMeasure { maximum_speed_m_s = speed, turn_distance_m = -1 };
            var stations = new List<float>(); var directions = new List<float2>();
            var remaining = new List<(PathElement element, string source, int index)>();
            remaining.Add((new PathElement(current.m_Lane, current.m_CurvePosition.xz, 0), "current_lane", -1));
            for (int i = 0; i < navigation.Length; i++)
                remaining.Add((new PathElement(navigation[i].m_Lane, navigation[i].m_CurvePosition, 0), "navigation", i));
            // Native CarNavigationSystem consumes up to twelve PathElements into Navigation.
            // The current lane, navigation buffer and unconsumed path are successive pieces,
            // not three alternative representations of the complete remaining route.
            for (int i = first; i < path.Length; i++) remaining.Add((path[i], "path", i));
            float3 previousEnd = position;
            float station = 0;
            foreach (var entry in remaining)
            {
                PathElement element = entry.element;
                if (!em.HasComponent<Game.Net.Curve>(element.m_Target) ||
                    (element.m_Flags & (PathElementFlags.Action | PathElementFlags.Reverse)) != 0) break;
                float a = element.m_TargetDelta.x, b = element.m_TargetDelta.y;
                if (!math.isfinite(a) || !math.isfinite(b) || a < 0 || a > 1 || b < 0 || b > 1) break;
                var curve = em.GetComponentData<Game.Net.Curve>(element.m_Target);
                if (math.abs(a-b) < 1e-5f) continue;
                float3 start = MathUtils.Position(curve.m_Bezier, a), end = MathUtils.Position(curve.m_Bezier, b);
                if (!math.all(math.isfinite(start)) || !math.all(math.isfinite(end)))
                { result.reason = "Nonfinite route curve"; break; }
                float gap = math.distance(previousEnd, start);
                if (result.measured_curves == 0)
                {
                    result.prefix_distance_m = gap;
                    if (gap > 20) { result.reason = "Current lane geometry is too far from the actual vehicle"; break; }
                }
                else if (gap > 12) { result.reason = "Upcoming curve chain has an unmeasured discontinuity"; break; }
                result.largest_join_gap_m = math.max(result.largest_join_gap_m, gap);
                float length = MathUtils.Length(curve.m_Bezier, new Bounds1(math.min(a,b),math.max(a,b)));
                if (!math.isfinite(length) || length <= 0) break;
                if (em.HasComponent<Game.Net.CarLane>(element.m_Target))
                    result.maximum_speed_m_s = math.max(result.maximum_speed_m_s, em.GetComponentData<Game.Net.CarLane>(element.m_Target).m_SpeedLimit);
                result.curves.Add(new { source = entry.source, source_index = entry.index, lane = element.m_Target.ToString(), from = a, to = b,
                    start = A(start), end = A(end), measured_length_m = length, preceding_gap_m = gap });
                // Independent tangents over short arclength windows catch bends within one Bezier,
                // which comparing only successive lane chords misses. Never count the join gap as road length.
                int steps = math.clamp((int)math.ceil(length / 2), 4, 256);
                for (int j = 0; j <= steps; j++)
                {
                    float t = math.lerp(a,b,j/(float)steps);
                    float2 direction = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier,t).xz * math.sign(b-a));
                    float at = station + MathUtils.Length(curve.m_Bezier,new Bounds1(math.min(a,t),math.max(a,t)));
                    if (math.lengthsq(direction) < .5f) continue;
                    stations.Add(at); directions.Add(direction);
                }
                station += length; previousEnd = end; result.measured_curves++;
            }
            result.remaining_curve_length_m = station;
            // Scenario suitability, not vehicle geometry acceptance: retain road beyond the observation,
            // takeover and release windows using the greatest measured native speed limit.
            // Remaining road for observation + takeover + release at the bus's speed (cannot exceed the fastest lane), and a
            // bend the bus reaches in the first 65 % of the observation (official-map through traffic is mostly highway).
            float travelSpeed = math.min(busSpeed, result.maximum_speed_m_s);
            result.required_remaining_m = math.max(300, travelSpeed * (seconds + 45) + 80);
            result.turn_window_limit_m = math.max(60, travelSpeed * seconds * .65f);
            for (int i = 0; i < stations.Count; i++)
            {
                float fromVehicle = result.prefix_distance_m + stations[i];
                if (fromVehicle < 15 || fromVehicle > result.turn_window_limit_m) continue;
                for (int j = i-1; j >= 0 && stations[i]-stations[j] <= 30; j--)
                {
                    float angle = math.degrees(math.acos(math.clamp(math.dot(directions[i],directions[j]),-1,1)));
                    // A right-angle junction turn is the most telling articulation case; only reversals are excluded.
                    if (angle >= 8 && angle <= 120)
                    { result.turn_degrees = angle; result.turn_distance_m = fromVehicle; break; }
                }
                if (result.turn_distance_m >= 0) break;
            }
            result.sufficient = station >= result.required_remaining_m && result.turn_distance_m >= 0;
            if (!result.sufficient && result.reason == null)
                result.reason = station < result.required_remaining_m ? "Measured remaining curve length does not cover observation/takeover/release budget" : "No measured 8..120 degree bend over 30m inside the observation window";
            return result;
        }

        static void Spawn(SystemBase host, EntityManager em, Donor donor, Entity requestedPrefab, int token)
        {
            var selector = new TransportVehicleSelectData(host);
            using (EntityQuery query = em.CreateEntityQuery(TransportVehicleSelectData.GetEntityQueryDesc()))
            using (var models = new NativeList<VehicleModel>(1, Allocator.Temp))
            using (var commands = new EntityCommandBuffer(Allocator.Temp))
            {
                models.Add(new VehicleModel { m_PrimaryPrefab = requestedPrefab });
                selector.PreUpdate(host, host.World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>(), query, Allocator.TempJob, out var handle); handle.Complete();
                try
                {
                    var random = Unity.Mathematics.Random.CreateFromIndex((uint)token); int2 passenger = new int2(1, 500), cargo = 0;
                    Entity vehicle = selector.CreateVehicle(commands.AsParallelWriter(), 0, ref random, donor.Transform, Entity.Null, models,
                        TransportType.Bus, EnergyTypes.FuelAndElectricity, SizeClass.Large, PublicTransportPurpose.TransportLine,
                        Game.Economy.Resource.NoResource, ref passenger, ref cargo, false);
                    Require(vehicle != Entity.Null, "Native transport selector could not create the requested bus");
                    commands.AddComponent(vehicle, new OwnedVehicle { Token = token }); commands.AddComponent(vehicle, donor.Owner);
                    commands.SetComponent(vehicle, donor.Target); commands.SetComponent(vehicle, donor.Lane);
                    commands.SetComponent(vehicle, new PathOwner(donor.PathOwner.m_ElementIndex, PathFlags.Updated));
                    commands.SetComponent(vehicle, donor.Information);
                    commands.SetComponent(vehicle, new Game.Vehicles.PublicTransport { m_State = PublicTransportFlags.DummyTraffic });
                    commands.SetBuffer<PathElement>(vehicle).CopyFrom(donor.Path);
                    commands.SetBuffer<CarNavigationLane>(vehicle).CopyFrom(donor.Navigation);
                    commands.Playback(em);
                }
                finally { selector.PostUpdate(default); }
            }
        }

        static async Task Capture(Camera camera, RenderTexture texture, string directory, List<object> shots, ObjectTransform front, ObjectTransform rear, uint frame, float bend, Action check, Func<bool> jointVisible)
        {
            PlaceCamera(camera, front, rear);
            for (int i = 0; i < 8; i++) { check(); camera.Render(); await Task.Delay(20); }
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            string filename = "road-" + frame + ".png";
            try { image.SetPixels32(BusGlassProbe.Read(texture)); image.Apply(); File.WriteAllBytes(Path.Combine(directory, filename), image.EncodeToPNG()); }
            finally { UnityEngine.Object.Destroy(image); }
            shots.Add(new { filename, simulation_frame = frame, bend_degrees = bend, front = A(front.m_Position), rear = A(rear.m_Position),
                actual_game_lighting = true, synthetic_vehicle_renderers = false, production_joint_visible = jointVisible(), manual_review = "pending" });
            Require(jointVisible(), "Saved road view does not include the active production AI joint");
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void RequireRoadVehicle(EntityManager em, Entity entity, string stage)
        {
            if (em.Exists(entity) && em.HasComponent<CarCurrentLane>(entity) &&
                VehicleUtils.PathEndReached(em.GetComponentData<CarCurrentLane>(entity)))
                throw new RouteInsufficientException("Native test route ended before " + stage + " completed; this does not diagnose a vehicle defect");
            Require(em.Exists(entity) && !em.HasComponent<Deleted>(entity), "Native AI removed the test bus during " + stage + "; cause not established as a model defect");
        }
        static float[] A(float3 v) => new[] { v.x, v.y, v.z };
        static float[] A(float4 v) => new[] { v.x, v.y, v.z, v.w };
        static Vector3 V(float3 v) => new Vector3(v.x, v.y, v.z);
        static void PlaceCamera(Camera camera, ObjectTransform front, ObjectTransform rear)
        {
            Vector3 focus = (V(front.m_Position) + V(rear.m_Position)) * .5f + Vector3.up * 1.5f;
            camera.transform.SetPositionAndRotation(focus + V(math.rotate(front.m_Rotation, new float3(22, 10, 22))), Quaternion.identity);
            camera.transform.LookAt(focus); camera.fieldOfView = 48;
        }
        static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    }
}
