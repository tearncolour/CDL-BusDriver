#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using CDLC.Asset.Articulation;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Settings;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using ObjectTransform = Game.Objects.Transform;

namespace CDLC.BusDriver
{
    internal static partial class BusVehicleRoadProbe
    {
        static async Task<bool> RunGlassCycle(SystemBase host, Entity front, Entity rear, VehicleAssembly assembly,
            Camera outside, RenderTexture target, CameraUpdateSystem cameras, string directory,
            Dictionary<string, object> roadReport, Action check, Action<string> interrupt, Func<bool> ownsWorld, bool addRoadLights, bool optics)
        {
            const string owner = Core.kOwner + ".GlassCycle";
            EntityManager em = host.EntityManager;
            // A single-section bus (no rear, no assembly) is posed, parked and bound by its one section.
            Entity[] sections = rear == Entity.Null ? new[] { front } : new[] { front, rear };
            Entity tail = sections[sections.Length - 1];
            string[] sectionNames = sections.Select(s => s.ToString()).ToArray();
            var simulation = host.World.GetExistingSystemManaged<SimulationSystem>();
            var time = host.World.GetExistingSystemManaged<TimeSystem>();
            var planetary = host.World.GetExistingSystemManaged<PlanetarySystem>();
            var lighting = host.World.GetExistingSystemManaged<LightingSystem>();
            var rendering = host.World.GetExistingSystemManaged<RenderingSystem>();
            var prefabSystem = host.World.GetExistingSystemManaged<PrefabSystem>();
            Require(simulation != null && time != null && planetary != null && lighting != null && rendering != null,
                "Glass cycle requires the actual world time and rendering systems");
            using var timeQuery = em.CreateEntityQuery(ComponentType.ReadWrite<TimeData>());
            using var lightQuery = em.CreateEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadOnly<StreetLight>(), ComponentType.ReadOnly<ObjectTransform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            });
            Entity timeEntity = timeQuery.GetSingletonEntity();
            TimeData originalTime = em.GetComponentData<TimeData>(timeEntity);
            float originalSpeed = simulation.selectedSpeed;
            bool originalVisual = SharedSettings.instance.gameplay.dayNightVisual;
            bool originalOverride = planetary.overrideTime;
            float originalMultiplier = planetary.debugTimeMultiplier;
            Camera originalCamera = cameras.activeCamera;
            var originalController = cameras.activeCameraController;
            var rows = new List<object>();
            var opticsRows = new List<object>();
            var visibilityRows = new List<object>();
            var renderTargetCaptures = new List<object>();
            var releases = new List<object>();
            var forwardLooks = new List<object>();
            var mirrorFrameRows = new List<object>();
            var pauseRows = new List<object>();
            var cleanup = new List<Exception>();
            var cycleReport = new Dictionary<string, object> {
                ["scope"] = "Same real bus (both sections of an assembly, or one single-section bus), normal native AI after release and actual driver eye after takeover; optional test-owned native road poles are separately reported.",
                ["sections"] = sectionNames,
                ["frames_per_light_update_window"] = 256, ["captures"] = rows,
                ["render_target_captures"] = renderTargetCaptures,
                ["releases"] = releases,
                ["forward_look_scopes"] = forwardLooks,
                ["driver_mirror_frames"] = mirrorFrameRows,
                ["time_ui_pause_scopes"] = pauseRows,
                ["time_before"] = TimeEvidence(originalTime), ["manual_image_review_required"] = true,
                ["glass_optics_requested"] = optics, ["glass_optics"] = opticsRows,
                ["glass_visibility_requested"] = optics, ["glass_visibility"] = visibilityRows,
                ["not_tested"] = new[] { "door animation matrix", "all distance-selected LODs", "passenger service" }
            };
            roadReport["glass_cycle"] = cycleReport;
            bool drivingOwned = false;
            bool driverEstablished = false;
            BusRoadGlassPause pause = null;
            Action checkWorld = check;
            check = CheckCapture;
            Func<bool> worldOwnership = ownsWorld;
            ownsWorld = OwnsCapturedWorld;
            var requests = new BusRoadGlassRequests(owner);
            BusRoadGlassForwardLook forwardLook = null;
            BusRoadGlassMirrorFrames mirrorFrames = null;
            BusRoadGlassCameraFrames cameraFrames = null;
            RoadGlassLights fixture = null;
            Exception failure = null;
            Vector3 exteriorPosition = default;
            Quaternion exteriorRotation = default;
            bool cameraLocked = false, roadLightsObserved = true;
            bool vehicleLightsObserved = true;
            bool opticsReadable = true;
            Vector3? firstDriverEye = null;
            Quaternion firstDriverRotation = default;
            float firstDriverFov = 0;
            try
            {
                check();
                SharedSettings.instance.gameplay.dayNightVisual = true;
                planetary.overrideTime = false;
                planetary.debugTimeMultiplier = 1f;
                foreach (int hour in new[] { 12, 22 })
                {
                    await TakeOverAndStop();
                    if (hour == 12 && addRoadLights) await StopAtRoadLightSite();
                    check();
                    EnterPause(hour, "native-discovery");
                    em.CompleteAllTrackedJobs();
                    ObjectTransform parkedFront = em.GetComponentData<ObjectTransform>(front);
                    ObjectTransform parkedRear = em.GetComponentData<ObjectTransform>(tail);
                    var lookReport = new Dictionary<string, object> { ["hour"] = hour };
                    forwardLooks.Add(lookReport);
                    forwardLook = new BusRoadGlassForwardLook(host, front, ownsWorld, lookReport);
                    if (hour == 12) await AwaitNativeGlassDiscovery();
                    if (hour == 12 && addRoadLights)
                    {
                        var fixtureReport = new Dictionary<string, object>();
                        cycleReport["native_road_light_fixture"] = fixtureReport;
                        if (NearbyLights(parkedFront.m_Position).count == 0)
                        {
                            fixture = new RoadGlassLights(host, check, ownsWorld, fixtureReport);
                            fixture.Create(parkedFront.m_Position);
                        }
                        else fixtureReport["status"] = "Not needed: existing street lights are within 100 m";
                    }
                    if (!cameraLocked)
                    {
                        PlaceCamera(outside, parkedFront, parkedRear);
                        exteriorPosition = outside.transform.position; exteriorRotation = outside.transform.rotation;
                        cameraLocked = true;
                    }
                    int advance = BusRoadGlassClock.AdvanceMinutes(time.normalizedTime, hour);
                    time.DebugAdvanceTime(advance);
                    uint firstFrame = simulation.frameIndex;
                    int stable = 0, previousOn = -1;
                    float previousBrightness = -1;
                    string previousVehicleLights = null;
                    LeavePause();
                    check();
                    simulation.selectedSpeed = 1;
                    await Wait(() => {
                        var lights = NearbyLights(parkedFront.m_Position);
                        var vehicleLights = VehicleLights();
                        bool expected = hour == 12 ? lighting.dayLightBrightness > .3f : lighting.dayLightBrightness < .03f;
                        bool settled = math.abs(lighting.dayLightBrightness - previousBrightness) < .001f && lights.on == previousOn &&
                            vehicleLights.signature == previousVehicleLights;
                        previousBrightness = lighting.dayLightBrightness; previousOn = lights.on;
                        previousVehicleLights = vehicleLights.signature;
                        stable = expected && settled ? stable + 1 : 0;
                        return simulation.frameIndex - firstFrame >= 256 && stable >= 5;
                    }, 60, "Actual daylight/night lighting did not settle", true);
                    EnterPause(hour, "driver-and-native-captures");
                    await Task.Delay(250); check(); em.CompleteAllTrackedJobs();
                    Require(Speed(front) < .05f && Speed(tail) < .05f, "Parked bus moved during lighting settlement");
                    Require(Core.CabViewActive() && Core.Vehicle() == front, "Glass cycle lost its real driver view");
                    Camera driver = cameras.activeCamera;
                    Require(driver == outside && cameras.activeViewer?.camera == driver && driver.targetTexture == target,
                        "Driver capture is not the owned inspection camera and render target");
                    Require(Core.ActiveCamera() == 0, "Driver capture is not using the actual driver seat");
                    lookReport["driver_eye"] = new { camera = CameraEvidence(driver), kneel_level = Core.KneelLevel(),
                        front = A(em.GetComponentData<ObjectTransform>(front).m_Position),
                        moved_m = firstDriverEye.HasValue ? (float?)Vector3.Distance(firstDriverEye.Value, driver.transform.position) : null,
                        turned_degrees = firstDriverEye.HasValue ? (float?)Quaternion.Angle(firstDriverRotation, driver.transform.rotation) : null,
                        fov_change = firstDriverEye.HasValue ? (float?)(driver.fieldOfView - firstDriverFov) : null };
                    if (firstDriverEye.HasValue)
                        Require(Vector3.Distance(firstDriverEye.Value, driver.transform.position) < .1f &&
                            Quaternion.Angle(firstDriverRotation, driver.transform.rotation) < .5f &&
                            math.abs(firstDriverFov - driver.fieldOfView) < .01f, "Driver viewpoint changed between day and night");
                    else
                    {
                        firstDriverEye = driver.transform.position; firstDriverRotation = driver.transform.rotation;
                        firstDriverFov = driver.fieldOfView;
                    }
                    if (cameraFrames == null)
                    {
                        var cameraReport = new Dictionary<string, object>(); cycleReport["camera_frames"] = cameraReport;
                        cameraFrames = new BusRoadGlassCameraFrames(outside, target, () => {
                            check();
                            Require(cameras.activeCamera == outside && cameras.activeViewer?.camera == outside,
                                "Glass cycle lost the owned active inspection eye");
                        }, () => ownsWorld() && cameras.activeCamera == outside && outside.targetTexture == target &&
                            (!Core.IsDriving() || Core.Vehicle() == front), cameraReport);
                        cameraFrames.Start();
                    }
                    var mirrorReport = new Dictionary<string, object> { ["hour"] = hour };
                    mirrorFrameRows.Add(mirrorReport);
                    mirrorFrames = host.World.GetExistingSystemManaged<BusCabSystem>().WatchGlassMirrors(front, rear, driver, target,
                        () => {
                            check();
                            Require(Core.IsDriving() && Core.Vehicle() == front && Core.CabViewActive() &&
                                cameras.activeCamera == driver && cameras.activeViewer?.camera == driver && driver.targetTexture == target,
                                "Driver mirror observation lost its owned eye");
                        }, () => ownsWorld() && Core.IsDriving() && Core.Vehicle() == front && cameras.activeCamera == driver &&
                            driver.targetTexture == target, mirrorReport);
                    mirrorFrames.Start();
                    await SettleRenders(driver);
                    forwardLook.Verify();
                    Vector3 bodyForward = math.mul(em.GetComponentData<ObjectTransform>(front).m_Rotation, new float3(0, 0, 1));
                    float forwardYaw = Vector3.SignedAngle(Vector3.ProjectOnPlane(bodyForward, Vector3.up),
                        Vector3.ProjectOnPlane(driver.transform.forward, Vector3.up), Vector3.up);
                    lookReport["view"] = new { body_forward = A((float3)bodyForward), camera_forward = A((float3)driver.transform.forward),
                        relative_yaw_degrees = forwardYaw, camera = CameraEvidence(driver) };
                    Require(math.abs(forwardYaw) <= 5f, "Driver camera is not looking forward from its real eye");
                    Require(Core.CabViewActive() && Core.Vehicle() == front && Core.ActiveCamera() == 0,
                        "Driver seat became inactive before capture");
                    ObjectTransform driverFront = em.GetComponentData<ObjectTransform>(front);
                    ObjectTransform driverRear = em.GetComponentData<ObjectTransform>(tail);
                    object driverPose = ReleaseState();
                    string driverFile = "glass-cycle-" + hour + "-driver.png";
                    Save(driverFile);
                    var lampState = NearbyLights(parkedFront.m_Position, true);
                    var actualVehicleLights = VehicleLights(); vehicleLightsObserved &= actualVehicleLights.available;
                    roadLightsObserved &= lampState.count > 0 && (hour == 12 || lampState.on > 0);
                    if (fixture != null) roadLightsObserved &= fixture.Observe(hour, "driver");
                    rows.Add(new { hour, advanced_minutes = advance, mode = "driver", filename = driverFile, output_path = Path.Combine(directory, driverFile),
                        capture_source = "Owned render target of the active Core driver-eye camera", core_active_camera = Core.ActiveCamera(),
                        time = ClockEvidence(), lights = new { lampState.count, lampState.on, lampState.evidence },
                        camera = CameraEvidence(driver), front = A(driverFront.m_Position), rear = A(driverRear.m_Position),
                        paired_view_reference = driverPose,
                        vehicle_lights = actualVehicleLights.evidence,
                        native_entities = sectionNames });
                    if (optics)
                    {
                        Action opticsGuard = OpticsGuard();
                        var bindings = host.World.GetExistingSystemManaged<BusCabSystem>().CollectGlassOpticsBindings(front, rear);
                        opticsReadable &= await RunGlassVisibility(bindings, driver, hour, "driver", directory, opticsGuard, ownsWorld,
                            () => SettleRenders(driver), filename => { Save(filename); return Task.CompletedTask; }, OpticsScene, visibilityRows);
                        opticsReadable &= await RunGlassOptics(bindings, driver, hour, "driver", directory, opticsGuard,
                            () => SettleRenders(driver), filename => {
                                Require(Core.CabViewActive() && Core.Vehicle() == front && Core.ActiveCamera() == 0,
                                    "Driver seat became inactive before optics capture");
                                Save(filename); return Task.CompletedTask;
                            }, OpticsScene, opticsRows);
                    }

                    mirrorFrames.Dispose(); mirrorFrames = null;

                    pause.Validate();
                    em.CompleteAllTrackedJobs();
                    ObjectTransform releaseFront = em.GetComponentData<ObjectTransform>(front);
                    ObjectTransform releaseRear = em.GetComponentData<ObjectTransform>(tail);
                    var release = new Dictionary<string, object> { ["hour"] = hour, ["before"] = ReleaseState(),
                        ["paired_view_reference"] = driverPose };
                    releases.Add(release);
                    forwardLook.Dispose(); forwardLook = null;
                    driverEstablished = false;
                    requests.ClearView();
                    Core.Release();
                    await Wait(() => !Core.IsDriving(), 5, "Glass cycle release timed out", false);
                    drivingOwned = false;
                    release["after_release"] = ReleaseState();
                    pause.Validate();
                    cameras.activeCameraController = null; cameras.activeCamera = outside;
                    outside.transform.SetPositionAndRotation(exteriorPosition, exteriorRotation);
                    await SettleRenders(outside);
                    object nativeJointState = null;
                    if (assembly != null)
                        await Wait(() => {
                            nativeJointState = NativeJointProbeState(host, front, rear, assembly, cameras);
                            cycleReport["native_ai_joint_state"] = nativeJointState;
                            return host.World.GetExistingSystemManaged<ArticulationSystem>()?.HasVisibleJoint(front) == true;
                        }, 5, "Stationary native AI view has no visible production joint", false);
                    em.CompleteAllTrackedJobs();
                    ObjectTransform nativeFront = em.GetComponentData<ObjectTransform>(front);
                    ObjectTransform nativeRear = em.GetComponentData<ObjectTransform>(tail);
                    float displacement = math.distance(releaseFront.m_Position, nativeFront.m_Position);
                    float rearDisplacement = math.distance(releaseRear.m_Position, nativeRear.m_Position);
                    float pairedDisplacement = math.distance(driverFront.m_Position, nativeFront.m_Position);
                    float pairedRearDisplacement = math.distance(driverRear.m_Position, nativeRear.m_Position);
                    release["after_native_settle"] = ReleaseState();
                    release["front_displacement_m"] = displacement;
                    release["rear_displacement_m"] = rearDisplacement;
                    release["paired_front_displacement_m"] = pairedDisplacement;
                    release["paired_rear_displacement_m"] = pairedRearDisplacement;
                    Require(displacement < .1f && rearDisplacement < .1f && pairedDisplacement < .1f && pairedRearDisplacement < .1f,
                        "AI release moved the bus beyond the paired-view tolerance");
                    Require(cameras.activeCamera == outside, "Native AI capture lost its active viewer");
                    object batches = NativeBatchEvidence();
                    string aiFile = "glass-cycle-" + hour + "-ai.png";
                    Save(aiFile);
                    var nativeLamps = NearbyLights(parkedFront.m_Position, true);
                    if (fixture != null) roadLightsObserved &= fixture.Observe(hour, "native-ai");
                    rows.Add(new { hour, mode = "native-ai", filename = aiFile, time = ClockEvidence(), lights = new { nativeLamps.count, nativeLamps.on, nativeLamps.evidence },
                        camera = CameraEvidence(outside), release_displacement_m = displacement, rear_release_displacement_m = rearDisplacement,
                        paired_front_displacement_m = pairedDisplacement, paired_rear_displacement_m = pairedRearDisplacement,
                        batches,
                        production_joint = nativeJointState,
                        vehicle_lights = VehicleLights().evidence,
                        native_entities = sectionNames, core_released = !Core.IsDriving(),
                        front = A(em.GetComponentData<ObjectTransform>(front).m_Position), rear = A(em.GetComponentData<ObjectTransform>(tail).m_Position) });
                    if (optics)
                    {
                        Action opticsGuard = OpticsGuard();
                        var bindings = NativeOpticsBindings(host, sections);
                        if (hour == 12)
                            await RunNativeOpaqueComparison(host, sections, opticsGuard, () => SettleRenders(outside),
                                filename => { Save(filename); return Task.CompletedTask; }, opticsRows);
                        opticsReadable &= await RunGlassVisibility(bindings, outside, hour, "native-ai", directory, opticsGuard, ownsWorld,
                            () => SettleRenders(outside), filename => { Save(filename); return Task.CompletedTask; }, OpticsScene, visibilityRows);
                        opticsReadable &= await RunGlassOptics(bindings, outside, hour, "native-ai", directory, opticsGuard,
                            () => SettleRenders(outside), filename => { Save(filename); return Task.CompletedTask; }, OpticsScene, opticsRows);
                    }
                    LeavePause();
                }
                cycleReport["road_lights_observed"] = roadLightsObserved;
                cycleReport["vehicle_light_buffers_observed"] = vehicleLightsObserved;
                cycleReport["optics_images_readable"] = opticsReadable;
                cycleReport["status"] = !roadLightsObserved || !vehicleLightsObserved ? "PARTIAL_LIGHTING_EVIDENCE" :
                    !opticsReadable ? "PARTIAL_OPTICS_EVIDENCE" : "CAPTURED_REQUIRES_IMAGE_REVIEW";
                return roadLightsObserved && vehicleLightsObserved && opticsReadable;
            }
            catch (Exception error) { failure = error; cycleReport["status"] = "FAIL"; cycleReport["error"] = error.ToString(); throw; }
            finally
            {
                OperationCanceledException cleanupInterruption = null;
                try { check(); }
                catch (OperationCanceledException error)
                {
                    cleanupInterruption = error;
                    cycleReport["cleanup_interruption"] = error.ToString();
                }
                if (mirrorFrames != null)
                    try { mirrorFrames.Dispose(); }
                    catch (Exception error) { CabCleanup.Retain(mirrorFrames); cleanup.Add(error); }
                if (cameraFrames != null)
                    try { cameraFrames.Dispose(); }
                    catch (Exception error) { CabCleanup.Retain(cameraFrames); cleanup.Add(error); }
                if (forwardLook != null)
                    try { forwardLook.Dispose(); }
                    catch (Exception error) { CabCleanup.Retain(forwardLook); cleanup.Add(error); }
                if (pause != null)
                    try { LeavePause(); }
                    catch (Exception error) { cleanup.Add(error); }
                if (fixture != null)
                    try { await fixture.Cleanup(); }
                    catch (Exception error) { cleanup.Add(error); }
                try { requests.Dispose(); }
                catch (Exception error) { CabCleanup.Retain(requests); cleanup.Add(error); }
                try { check(); }
                catch (OperationCanceledException error)
                {
                    cleanupInterruption = error;
                    cycleReport["cleanup_interruption"] = error.ToString();
                }
                if (ownsWorld())
                {
                    Try(() => SharedSettings.instance.gameplay.dayNightVisual = originalVisual);
                    Try(() => simulation.selectedSpeed = 0);
                    Try(() => {
                        em.CompleteAllTrackedJobs(); Require(em.Exists(timeEntity), "Original TimeData entity disappeared"); em.SetComponentData(timeEntity, originalTime);
                        TimeData restored = em.GetComponentData<TimeData>(timeEntity);
                        Require(restored.m_FirstFrame == originalTime.m_FirstFrame && restored.m_StartingYear == originalTime.m_StartingYear &&
                            restored.m_StartingMonth == originalTime.m_StartingMonth && restored.m_StartingHour == originalTime.m_StartingHour &&
                            restored.m_StartingMinutes == originalTime.m_StartingMinutes, "TimeData restoration did not persist");
                    });
                    Try(() => { planetary.overrideTime = originalOverride; planetary.debugTimeMultiplier = originalMultiplier; });
                    if (drivingOwned && Core.IsDriving() && Core.Vehicle() == front)
                        Try(() => { driverEstablished = false; Core.Release(); });
                }
                if (ownsWorld())
                    Try(() => { cameras.activeCamera = originalCamera; cameras.activeCameraController = originalController; simulation.selectedSpeed = originalSpeed; });
                cycleReport["cleanup"] = new { owns_original_world = ownsWorld(), errors = cleanup.Select(e => e.ToString()).ToArray(),
                    restored_time = ownsWorld() && em.Exists(timeEntity) ? TimeEvidence(em.GetComponentData<TimeData>(timeEntity)) : null };
                if (cleanup.Count > 0)
                {
                    if (failure != null) cleanup.Insert(0, failure);
                    throw new AggregateException("Glass cycle cleanup failed", cleanup);
                }
                if (cleanupInterruption != null && failure == null) throw cleanupInterruption;
            }

            void CheckCapture()
            {
                checkWorld();
                string reason = null;
                if (outside == null || cameras.activeCamera != outside || cameras.activeViewer?.camera != outside || outside.targetTexture != target)
                    reason = "Glass capture active eye or render target changed";
                else if (driverEstablished && (!Core.IsDriving() || Core.Vehicle() != front || !Core.CabViewActive() || Core.ActiveCamera() != 0))
                    reason = "Glass capture driver control or viewpoint changed";
                if (reason != null)
                {
                    cycleReport["control_failure_state"] = ControlState();
                    interrupt(reason);
                    throw new OperationCanceledException(reason + "; restore/return/exit cancelled");
                }
                try { pause?.Validate(); }
                catch (OperationCanceledException)
                {
                    cycleReport["control_failure_state"] = ControlState();
                    throw;
                }
            }
            object ControlState() => new { simulation_frame = simulation.frameIndex, unity_frame = Time.frameCount,
                selected_speed = simulation.selectedSpeed, core_driving = Core.IsDriving(), core_vehicle = Core.Vehicle().ToString(),
                expected_vehicle = front.ToString(), cab_view = Core.CabViewActive(), active_seat = Core.ActiveCamera(),
                active_camera = cameras.activeCamera != null ? (int?)cameras.activeCamera.GetInstanceID() : null,
                active_viewer = cameras.activeViewer?.camera != null ? (int?)cameras.activeViewer.camera.GetInstanceID() : null,
                pause = pause?.State() };
            void EnterPause(int hour, string phase)
            {
                Require(pause == null, "Glass capture already holds a TimeUI pause");
                var entry = new Dictionary<string, object> { ["hour"] = hour, ["phase"] = phase };
                pauseRows.Add(entry);
                pause = new BusRoadGlassPause(host.World.GetExistingSystemManaged<Game.UI.InGame.TimeUISystem>(),
                    simulation, ownsWorld, interrupt, entry);
                pause.Enter();
            }
            void LeavePause()
            {
                BusRoadGlassPause released = pause;
                pause = null;
                if (released == null) return;
                try { released.Dispose(); }
                catch { CabCleanup.Retain(released); throw; }
            }
            bool OwnsCapturedWorld()
            {
                try { CheckCapture(); return worldOwnership(); }
                catch (OperationCanceledException) { return false; }
            }
            float Speed(Entity e) => math.length(em.GetComponentData<Moving>(e).m_Velocity);
            object ReleaseState()
            {
                ObjectTransform f = em.GetComponentData<ObjectTransform>(front), r = em.GetComponentData<ObjectTransform>(tail);
                return new { simulation_frame = simulation.frameIndex, rendering_frame = rendering.frameIndex,
                    unity_frame = Time.frameCount, selected_speed = simulation.selectedSpeed, core_driving = Core.IsDriving(),
                    front = new { position = A(f.m_Position), rotation = A(f.m_Rotation.value) },
                    rear = new { position = A(r.m_Position), rotation = A(r.m_Rotation.value) } };
            }
            object OpticsScene() => new { time = ClockEvidence(), camera = CameraEvidence(cameras.activeCamera),
                lights = NearbyLights(em.GetComponentData<ObjectTransform>(front).m_Position, true).evidence,
                vehicle_lights = VehicleLights().evidence };
            Action OpticsGuard()
            {
                uint frame = simulation.frameIndex;
                float brightness = lighting.dayLightBrightness;
                var lampState = NearbyLights(em.GetComponentData<ObjectTransform>(front).m_Position);
                string vehicleLights = VehicleLights().signature;
                return () => {
                    check();
                    if (Core.IsDriving()) forwardLook.Verify();
                    Require(simulation.selectedSpeed == 0 && simulation.frameIndex == frame, "Optics simulation advanced during the paired capture");
                    var current = NearbyLights(em.GetComponentData<ObjectTransform>(front).m_Position);
                    Require(math.abs(lighting.dayLightBrightness - brightness) < .001f && current.count == lampState.count && current.on == lampState.on &&
                        VehicleLights().signature == vehicleLights, "Optics lighting changed during the paired capture");
                };
            }
            async Task TakeOverAndStop()
            {
                check();
                Require(!Core.IsDriving(), "Glass cycle cannot replace a player's controlled vehicle");
                Core.Drive(front); drivingOwned = true;
                await Wait(() => Core.IsDriving() && Core.Vehicle() == front, 20, "Glass cycle takeover timed out", true);
                requests.SetParking();
                requests.SetView(); Core.SelectDriverSeat();
                await Wait(() => Core.CabViewActive() && host.World.GetExistingSystemManaged<BusCabSystem>()
                    .TryGetGlassProbeState(front, rear, out _) && Speed(front) < .05f && Speed(tail) < .05f,
                    30, "Glass cycle vehicle did not stop with a complete cab", true);
                driverEstablished = true;
            }
            // A stop where no test pole pair can stand on the ground (a bridge, an embankment) hands the bus back to its native AI
            // along its own route, and it is stopped again once the place it has reached has a site. Braking can carry it past
            // that site, so every stop is checked again.
            async Task StopAtRoadLightSite()
            {
                const int maxStops = 5;
                var stops = new List<object>();
                cycleReport["road_light_stops"] = stops;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    check(); em.CompleteAllTrackedJobs();
                    float3 stop = em.GetComponentData<ObjectTransform>(front).m_Position;
                    bool ready = RoadGlassLights.CanPrepare(host, stop, out object preflight);
                    stops.Add(new { seconds = watch.Elapsed.TotalSeconds, position = A(stop), road_light_site = ready, preflight });
                    if (ready) return;
                    Require(stops.Count < maxStops, "None of " + maxStops + " stops along the native route had a ground-supported road-light site; see road_light_stops");
                    driverEstablished = false;
                    requests.ClearView();
                    Core.Release();
                    await Wait(() => !Core.IsDriving(), 5, "Road-light stop release timed out", false);
                    drivingOwned = false;
                    cameras.activeCameraController = null; cameras.activeCamera = outside;
                    await Wait(() => {
                        ObjectTransform f = em.GetComponentData<ObjectTransform>(front);
                        PlaceCamera(outside, f, em.GetComponentData<ObjectTransform>(tail));
                        return math.distance(f.m_Position, stop) > 30f && RoadGlassLights.CanPrepare(host, f.m_Position, out _);
                    }, 90, "The native route reached no ground-supported road-light site within 90 s of a stop", true);
                    await TakeOverAndStop();
                }
            }
            async Task Wait(Func<bool> ready, int seconds, string message, bool run)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    check(); if (run) simulation.selectedSpeed = 1;
                    em.CompleteAllTrackedJobs(); if (ready()) return;
                    if (watch.Elapsed.TotalSeconds > seconds) throw new InvalidOperationException(message);
                    await Task.Delay(100);
                }
            }
            async Task AwaitNativeGlassDiscovery()
            {
                long generation = CDLC.Asset.Runtime.RuntimeSession.Generation;
                var reader = new NativeGlassDiscoveryReader();
                var expected = new HashSet<SurfaceAsset>();
                foreach (Entity section in sections)
                {
                    Require(prefabSystem.TryGetPrefab(em.GetComponentData<PrefabRef>(section).m_Prefab, out PrefabBase prefab) &&
                        prefab is ObjectGeometryPrefab, "Native glass discovery has no actual section prefab");
                    int sectionSurfaces = 0;
                    foreach (ObjectMeshInfo mesh in ((ObjectGeometryPrefab)prefab).m_Meshes)
                        if (mesh.m_Mesh is RenderPrefab root)
                        {
                            var levels = new List<RenderPrefab> { root };
                            if (root.TryGet(out LodProperties lod) && lod.m_LodMeshes != null) levels.AddRange(lod.m_LodMeshes);
                            foreach (RenderPrefab level in levels)
                                if (level != null)
                                    for (int i = 0; i < level.materialCount; i++)
                                    {
                                        SurfaceAsset surface = level.GetSurfaceAsset(i);
                                        if (surface != null && surface.name.IndexOf("_Gls", StringComparison.Ordinal) >= 0)
                                        { expected.Add(surface); sectionSurfaces++; }
                                    }
                        }
                    Require(sectionSurfaces > 0, "Native glass discovery has no exact glass surfaces for a test section");
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    check();
                    Require(CDLC.Asset.Runtime.RuntimeSession.IsInitialized && CDLC.Asset.Runtime.RuntimeSession.Generation == generation,
                        "Native glass generation changed while waiting for discovery");
                    bool paused = simulation.selectedSpeed == 0, driving = Core.IsDriving(), sameVehicle = Core.Vehicle() == front,
                        cabView = Core.CabViewActive();
                    cycleReport["native_glass_wait_state"] = new { paused, driving, same_vehicle = sameVehicle,
                        cab_view = cabView, state = ControlState() };
                    Require(paused, "Native glass discovery requires a confirmed TimeUI pause");
                    Require(driving && sameVehicle && cabView, "Native glass discovery lost its established driver view");
                    object system = null;
                    foreach (ComponentSystemBase candidate in host.World.Systems)
                        if (candidate.GetType() == reader.SystemType)
                        {
                            Require(system == null, "Multiple native glass systems exist in the owned World");
                            system = candidate;
                        }
                    var state = reader.Read(system, generation, expected);
                    bool ready = (bool)state["ready"];
                    cycleReport["native_glass_discovery"] = new { generation, elapsed_seconds = watch.Elapsed.TotalSeconds,
                        ready, actual_system = state,
                        scope = "Initial discovery only; actual native bindings of every section remain required by the AI capture" };
                    Require(!(bool)state["terminal_failure"], "Native glass production failed in this generation; discovery cannot complete");
                    if (ready) return;
                    Require(watch.Elapsed.TotalSeconds < 180, "Native glass initial discovery did not complete in the owned World");
                    await Task.Delay(1000);
                }
            }
            async Task SettleRenders(Camera camera)
            {
                Require(cameraFrames != null && camera == outside, "Glass capture has no owned automatic camera");
                int frame = Time.frameCount;
                BusRoadGlassMirrorFrames observed = mirrorFrames;
                observed?.BeginWindow();
                cameraFrames.BeginWindow();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (Time.frameCount - frame < 60 || !cameraFrames.WindowReady || (observed != null && !observed.WindowReady))
                {
                    check();
                    cameraFrames.Validate();
                    observed?.Validate();
                    if (watch.Elapsed.TotalSeconds > 15) throw new InvalidOperationException("World rendering did not settle while paused");
                    await Task.Delay(20);
                }
                cameraFrames.EndWindow();
                observed?.EndWindow();
            }
            void Save(string filename)
            {
                Require(cameras.activeCamera == outside && cameras.activeViewer?.camera == outside && outside.targetTexture == target,
                    "Readback is not the owned active camera's render target");
                RenderTexture previous = RenderTexture.active;
                Texture2D image = null;
                Exception readbackFailure = null;
                try
                {
                    cameraFrames.Validate();
                    Require(cameraFrames.WindowReady, "Glass capture has no completed automatic camera render window");
                    if (mirrorFrames != null)
                    {
                        mirrorFrames.Validate();
                        Require(mirrorFrames.WindowReady, "Driver capture has no completed production mirror render window");
                    }
                    image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                    image.SetPixels32(BusGlassProbe.Read(target)); image.Apply();
                    File.WriteAllBytes(Path.Combine(directory, filename), image.EncodeToPNG());
                }
                catch (Exception error) { readbackFailure = error; throw; }
                finally
                {
                    var errors = new List<Exception>();
                    CabCleanup.Try(() => RenderTexture.active = previous, errors);
                    if (image != null) CabCleanup.Try(() => UnityEngine.Object.Destroy(image), errors);
                    if (errors.Count > 0)
                    {
                        if (readbackFailure != null) errors.Insert(0, readbackFailure);
                        throw new AggregateException("Glass capture readback cleanup failed", errors);
                    }
                }
                renderTargetCaptures.Add(new { filename, output_path = Path.Combine(directory, filename), camera_id = outside.GetInstanceID(),
                    outside.enabled, target_id = target.GetInstanceID(), target.width, target.height, unity_frame = Time.frameCount,
                    previous_active_target_id = previous != null ? (int?)previous.GetInstanceID() : null,
                    active_target_restored = RenderTexture.active == previous });
            }
            (int count, int on, object evidence) NearbyLights(float3 point, bool includeEvidence = false)
            {
                int count = 0, on = 0;
                var observed = includeEvidence ? new List<object>() : null;
                using var entities = lightQuery.ToEntityArray(Unity.Collections.Allocator.Temp);
                foreach (Entity entity in entities)
                {
                    float3 position = em.GetComponentData<ObjectTransform>(entity).m_Position;
                    float distance = math.distance(point, position);
                    if (distance > 100f) continue;
                    count++;
                    StreetLightState state = em.GetComponentData<StreetLight>(entity).m_State;
                    if ((state & StreetLightState.TurnedOff) == 0) on++;
                    if (observed == null) continue;
                    Entity lightOwner = em.HasComponent<Owner>(entity) ? em.GetComponentData<Owner>(entity).m_Owner : Entity.Null;
                    Entity prefabEntity = em.HasComponent<PrefabRef>(entity) ? em.GetComponentData<PrefabRef>(entity).m_Prefab : Entity.Null;
                    string prefabName = prefabSystem != null && prefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefab) ? prefab.name : null;
                    observed.Add(new { entity = entity.ToString(), owner = lightOwner.ToString(), prefab = prefabEntity.ToString(), prefab_name = prefabName,
                        state = state.ToString(), state_bits = (byte)state, position = A(position), distance_m = distance });
                }
                return (count, on, observed);
            }
            (bool available, string signature, object evidence) VehicleLights()
            {
                bool available = true;
                var evidence = new List<object>();
                var state = new List<string>();
                foreach (Entity vehicle in sections)
                {
                    if (!em.HasBuffer<LightState>(vehicle)) { available = false; evidence.Add(new { vehicle = vehicle.ToString(), missing = true }); continue; }
                    var buffer = em.GetBuffer<LightState>(vehicle);
                    if (buffer.Length == 0) available = false;
                    var values = new List<object>();
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        LightState light = buffer[i];
                        values.Add(new { intensity = light.m_Intensity, color = light.m_Color });
                        state.Add(light.m_Intensity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "/" +
                            light.m_Color.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    evidence.Add(new { vehicle = vehicle.ToString(), values });
                }
                return (available, string.Join(";", state), evidence);
            }
            object NativeBatchEvidence()
            {
                var prefabs = host.World.GetExistingSystemManaged<PrefabSystem>();
                var expected = new Dictionary<Colossal.IO.AssetDatabase.SurfaceAsset, string>();
                foreach (Entity vehicle in sections)
                {
                    Require(prefabs.TryGetPrefab(em.GetComponentData<PrefabRef>(vehicle).m_Prefab, out PrefabBase prefab) && prefab is ObjectGeometryPrefab,
                        "Native glass vehicle prefab is unavailable");
                    var geometry = (ObjectGeometryPrefab)prefab;
                    foreach (var mesh in geometry.m_Meshes)
                        if (mesh.m_Mesh is RenderPrefab root)
                        {
                            var levels = new List<RenderPrefab> { root };
                            if (root.TryGet(out LodProperties lod) && lod.m_LodMeshes != null) levels.AddRange(lod.m_LodMeshes);
                            foreach (RenderPrefab level in levels)
                                for (int i = 0; i < level.materialCount; i++)
                                {
                                    var surface = level.GetSurfaceAsset(i);
                                    if (surface != null && surface.name.Contains("_Gls")) expected[surface] = prefab.name;
                                }
                        }
                }
                var managed = host.World.GetExistingSystemManaged<BatchManagerSystem>().GetManagedBatches();
                var found = new List<object>();
                var foundVehicles = new HashSet<string>(StringComparer.Ordinal);
                var sectionPrefabs = new HashSet<string>(expected.Values, StringComparer.Ordinal);
                for (int i = 0; i < managed.BatchCount; i++)
                    if (managed.GetBatch(i) is CustomBatch batch && batch.sourceSurface != null && expected.TryGetValue(batch.sourceSurface, out string vehicle))
                    {
                        Require(batch.material != null && batch.material.shader.name == "BH/GlsShader" &&
                            batch.material.name.EndsWith("(CDL glass)", StringComparison.Ordinal), "Native vehicle glass batch did not receive the CDL material");
                        foundVehicles.Add(vehicle);
                        found.Add(new { vehicle, index = i, batch.groupIndex, surface = batch.sourceSurface.name,
                            material = batch.material.name, material_id = batch.material.GetInstanceID(), shader = batch.material.shader.name });
                    }
                Require(sectionPrefabs.All(foundVehicles.Contains), "Every real section must have native CDL glass batches");
                return found;
            }
            object ClockEvidence() => new { simulation_frame = simulation.frameIndex, rendering_frame = rendering.frameIndex,
                time = TimeEvidence(em.GetComponentData<TimeData>(timeEntity)), normalized_time = time.normalizedTime,
                rendering_time = rendering.timeOfDay, planetary_hour = planetary.hour, planetary_minute = planetary.minute,
                lighting_state = lighting.state.ToString(), daylight = lighting.dayLightBrightness,
                sun_direction = A(planetary.SunLight.transform.forward), sun_intensity = planetary.SunLight.additionalData.intensity,
                sun_unit = planetary.SunLight.additionalData.lightUnit.ToString(), visual_cycle = SharedSettings.instance.gameplay.dayNightVisual,
                planetary_override = planetary.overrideTime, planetary_multiplier = planetary.debugTimeMultiplier };
            void Try(Action action) { try { action(); } catch (Exception error) { cleanup.Add(error); } }
        }

        static object TimeEvidence(TimeData data) => new { data.m_FirstFrame, data.m_StartingYear, data.m_StartingMonth, data.m_StartingHour, data.m_StartingMinutes };
        static object CameraEvidence(Camera camera)
        {
            var hd = HDCamera.GetOrCreate(camera);
            var exposure = hd.volumeStack?.GetComponent<Exposure>();
            return new { id = camera.GetInstanceID(), camera.name, camera.enabled,
                target_texture = camera.targetTexture == null ? null : new { id = camera.targetTexture.GetInstanceID(), camera.targetTexture.name,
                    camera.targetTexture.width, camera.targetTexture.height, created = camera.targetTexture.IsCreated() },
                position = A(camera.transform.position), rotation = new[] { camera.transform.rotation.x,
                camera.transform.rotation.y, camera.transform.rotation.z, camera.transform.rotation.w }, fov = camera.fieldOfView,
                exposure_mode = exposure?.mode.value.ToString(), exposure_fixed = exposure?.fixedExposure.value,
                exposure_min = exposure?.limitMin.value, exposure_max = exposure?.limitMax.value,
                light_layers = hd.frameSettings.IsEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.LightLayers),
                reflection_probe = hd.frameSettings.IsEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.ReflectionProbe),
                exposure_note = "Volume configuration, not GPU exposure readback; images still require visibility and exposure review." };
        }

        internal sealed class NativeGlassDiscoveryReader
        {
            internal readonly Type SystemType;
            readonly FieldInfo scope, catalog, backend, prefabsKnown, inputActive, publishPending, failed, failedGeneration, backendSurfaces;
            readonly PropertyInfo refreshing, hasSurfaces, surfaces, initializing, discovering;

            internal NativeGlassDiscoveryReader()
            {
                var asset = typeof(CDLC.Asset.Rendering.NativeGlassBatches).Assembly;
                SystemType = asset.GetType("CDLC.Asset.Rendering.NativeGlassSystem", true);
                Type catalogType = asset.GetType("CDLC.Asset.Rendering.NativeGlassCatalog", true);
                Type backendType = typeof(CDLC.Asset.Rendering.NativeGlassBatches);
                scope = Field(SystemType, "m_Scope", typeof(CDLC.Asset.Runtime.ResourceScope));
                catalog = Field(SystemType, "m_Catalog", catalogType);
                backend = Field(SystemType, "m_Batches", backendType);
                prefabsKnown = Field(SystemType, "m_PrefabsKnown", typeof(bool));
                inputActive = Field(SystemType, "m_InputActive", typeof(bool));
                publishPending = Field(SystemType, "m_PublishPending", typeof(bool));
                failed = Field(SystemType, "m_Failed", typeof(bool));
                failedGeneration = Field(SystemType, "m_FailedGeneration", typeof(long));
                refreshing = Property(catalogType, "RefreshInProgress", typeof(bool));
                hasSurfaces = Property(catalogType, "HasSurfaces", typeof(bool));
                surfaces = Property(catalogType, "Surfaces", typeof(IEnumerable<SurfaceAsset>));
                initializing = Property(backendType, "InitializationPending", typeof(bool));
                discovering = Property(backendType, "DiscoveryPending", typeof(bool));
                backendSurfaces = Field(backendType, "m_Surfaces", typeof(HashSet<SurfaceAsset>));
            }

            internal Dictionary<string, object> Read(object system, long generation, HashSet<SurfaceAsset> expected)
            {
                var reasons = new List<string>();
                if (system == null) return new Dictionary<string, object> { ["ready"] = false, ["terminal_failure"] = false,
                    ["not_ready"] = new[] { "NativeGlassSystem absent" } };
                if (system.GetType() != SystemType) throw new NotSupportedException("Native glass system identity changed");
                var owner = (CDLC.Asset.Runtime.ResourceScope)scope.GetValue(system);
                object source = catalog.GetValue(system), batches = backend.GetValue(system);
                bool alive = owner != null && owner.IsAlive && owner.Generation == generation;
                bool known = (bool)prefabsKnown.GetValue(system), input = (bool)inputActive.GetValue(system), publishing = (bool)publishPending.GetValue(system);
                bool failure = (bool)failed.GetValue(system);
                long failureGeneration = (long)failedGeneration.GetValue(system);
                bool refresh = source != null && (bool)refreshing.GetValue(source);
                var published = source == null ? new HashSet<SurfaceAsset>() : new HashSet<SurfaceAsset>((IEnumerable<SurfaceAsset>)surfaces.GetValue(source));
                var bound = batches == null ? null : (HashSet<SurfaceAsset>)backendSurfaces.GetValue(batches);
                bool initialization = batches == null || (bool)initializing.GetValue(batches);
                bool discovery = batches == null || (bool)discovering.GetValue(batches);
                string[] missingCatalog = expected.Where(s => !published.Contains(s)).Select(s => s.name).ToArray();
                string[] missingBackend = expected.Where(s => bound == null || !bound.Contains(s)).Select(s => s.name).ToArray();
                if (!alive) reasons.Add("Scope absent, expired or from another generation");
                if (!known) reasons.Add("Initial prefab discovery not committed");
                if (failure) reasons.Add("Native glass system stopped after failure");
                if (publishing) reasons.Add("Surface publication pending");
                if (source == null || !(bool)hasSurfaces.GetValue(source) || published.Count == 0) reasons.Add("Catalog has no published surfaces");
                if (initialization) reasons.Add("Native backend initialization pending");
                if (discovery) reasons.Add("Native backend discovery pending");
                if (expected.Count == 0) reasons.Add("No exact test-section glass surfaces");
                if (missingCatalog.Length != 0) reasons.Add("Exact test surfaces absent from published catalog");
                if (missingBackend.Length != 0) reasons.Add("Exact test surfaces absent from backend whitelist");
                // Periodic refresh may restart on the next frame. Its committed,
                // same-generation target references remain the readiness evidence.
                return new Dictionary<string, object> { ["ready"] = reasons.Count == 0, ["not_ready"] = reasons,
                    ["scope_alive_same_generation"] = alive, ["prefabs_known"] = known, ["failed"] = failure,
                    ["failed_generation"] = failureGeneration, ["terminal_failure"] = failure && failureGeneration == generation,
                    ["input_active"] = input, ["catalog_refresh_in_progress"] = refresh, ["publish_pending"] = publishing,
                    ["backend_initialization_pending"] = initialization, ["backend_discovery_pending"] = discovery,
                    ["expected_surfaces"] = expected.Count, ["published_surfaces"] = published.Count,
                    ["missing_catalog_surfaces"] = missingCatalog, ["missing_backend_surfaces"] = missingBackend,
                    ["readiness_contract"] = "Committed target surfaces and completed backend in the owned generation; ordinary incremental refresh alone does not revoke the committed whitelist" };
            }

            static FieldInfo Field(Type type, string name, Type valueType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null || field.DeclaringType != type || field.FieldType != valueType)
                    throw new NotSupportedException("Native glass field contract changed: " + type.FullName + "." + name);
                return field;
            }

            static PropertyInfo Property(Type type, string name, Type valueType)
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo getter = property?.GetGetMethod(true);
                if (property == null || property.DeclaringType != type || property.PropertyType != valueType ||
                    property.GetIndexParameters().Length != 0 || getter == null || getter.IsStatic)
                    throw new NotSupportedException("Native glass property contract changed: " + type.FullName + "." + name);
                return property;
            }
        }
    }
}
#endif
