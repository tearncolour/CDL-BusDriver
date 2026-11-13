using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CDLC.Asset.Articulation;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.City;
using Game.Prefabs;
using Game.Rendering;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using SignCamera = CDLC.Asset.Led.SignCamera;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The bus the player drives, drawn by the mod: in the driver's view (CSL Driving Life Core's first-person camera) the inside
    /// of the bus around the camera, and with the exterior option the whole bus, outside included, in the chase view instead of
    /// the game's body. Builds the model for the driven bus (its size, axles, doors and the traffic side measured from the game's
    /// bus) on a background thread, and draws it every frame at the body pose the camera used, so model and camera never drift
    /// apart. Animates the steering wheel, the dials, the doors (with the shift's door state), the wheels (turning and rolling,
    /// kept on the road while the bus kneels), the lights (saloon lights at night, the stop-request sign, the door warning lamps,
    /// the outside lamps and indicators), the LED signs and the cluster's warning lamps (BusCabSystem.Exterior.cs). An articulated
    /// bus gets its rear section too. Also tells the camera where the driver's eye is, and the driving mod how heavy the bus is,
    /// when it kneels and how far its doors are open (API 4). PreCulling, after the driving mod's camera system.
    /// </summary>
    public sealed partial class BusCabSystem : GameSystemBase
    {
        // A city bus's steering: about 22 turns of the wheel per turn of the road wheels (ZF Servocom 22-26), five turns lock to
        // lock; the driving mod's own ratio when it has one (API 4), so the wheel turns exactly as the physics steer.
        private const float kSteeringRatio = 22f;
        // Mass of a passenger with luggage (EU 1230/2012: 68 kg per passenger plus a little).
        private const float kPassengerKg = 70f;

        private sealed class Section
        {
            internal Entity Entity;
            internal BusInteriorSpec Spec;
            internal KitModelKey Key;
            internal CabInstance Instance;
            internal bool Dressed;          // the instance's own materials (livery, lamps) are set
            internal bool LiveryGloss;      // its mesh's Livery part is the glossy white (read by Dress)
            internal Vector3 HeldPosition;  // the last pose read, kept a few frames when a read fails (no one-frame blink)
            internal Quaternion HeldRotation;
            internal int HeldFrames = int.MaxValue;
            internal BusGlass Glass;        // imported panes over the replacement body's window openings
            internal float NextGlassAttempt;
            internal int GlassAttempts;     // tries since the instance was made: the wait between them doubles (1 s .. 30 s)
            internal BusNativeCab Native;   // the pack's own game asset in place of the runtime mesh's static parts (cabRender native)
            internal float NextNativeAttempt;
            internal int NativeAttempts;
            internal int HeadBeam = -1;     // the beam whose material the head lamps have (-1: none yet; again after Dress)
            internal readonly DoorMotion Doors = new DoorMotion();  // its doors' openings, each on its profile's timing
        }

        /// <summary>
        /// The driving mod's readouts for one update, read once at its start (nothing in the update changes them: its calls into
        /// the driving mod only set requests). Read under the same conditions as before: the API 4 members only with API 4, the
        /// cab members only with the cab API; otherwise false / zero, as those checks gave.
        /// </summary>
        private struct CoreFrame
        {
            internal bool HasCab, HasApi4, Driving, Riding, CabView, Braking, Reversing, ParkingBrake;
            internal int Camera;                // the camera the driver's view uses (API 14): 0 the driver's seat, n the bus's camera n - 1, past them the driving mod's own
            internal Entity Vehicle;            // the vehicle driven
            internal Entity Subject;            // the vehicle the camera is in: the one driven, or the one ridden as a passenger (API 17)
            internal bool Viewing => Driving || Riding;
            internal float Speed, WheelAngle, SteeringRatio, Rpm;
            internal float TimeScale;           // world seconds a real second (Core API 18; 1 before it): Speed is true, WorldSpeed what moves per real second
            internal float WorldSpeed => Speed * TimeScale;
            internal bool BodyPosed;            // TryGetBodyPose (the driver's view's body pose this frame)
            internal Vector3 BodyPosition;
            internal Quaternion BodyRotation;
        }

        private CoreFrame m_Core;
        private GlassDetail m_GlassDetail;

        private static CoreFrame ReadCore()
        {
            var f = new CoreFrame { HasCab = Core.HasCab, HasApi4 = Core.HasApi4 };
            f.Driving = Core.IsDriving();
            f.Vehicle = Core.Vehicle();
            f.Riding = !f.Driving && Core.IsRiding();
            f.Subject = f.Driving ? f.Vehicle : f.Riding ? Core.RiddenVehicle() : Entity.Null;
            f.Speed = Core.SpeedMps();
            f.TimeScale = Core.TimeScale();
            f.WheelAngle = Core.SteeringWheelAngle();
            f.SteeringRatio = f.HasApi4 ? Core.SteeringRatio() : kSteeringRatio;
            if (f.HasCab)
            {
                f.CabView = Core.CabViewActive();
                f.Camera = Core.ActiveCamera();
                f.Rpm = Core.EngineRpm();
                f.BodyPosed = Core.TryGetBodyPose(out f.BodyPosition, out f.BodyRotation);
            }
            if (f.HasApi4)
            {
                f.Braking = Core.Braking();
                f.Reversing = Core.Reversing();
                f.ParkingBrake = Core.ParkingBrake();
            }
            return f;
        }

        // Profiler sections of the update (a profiling build or the editor; nothing in a release player).
        private static readonly ProfilerMarker s_UpdateMarker = new ProfilerMarker("CDLC.BusCab.Update");
        private static readonly ProfilerMarker s_MaterialsMarker = new ProfilerMarker("CDLC.BusCab.Materials");
        private static readonly ProfilerMarker s_PoseMarker = new ProfilerMarker("CDLC.BusCab.Pose");
        private static readonly ProfilerMarker s_SignsMarker = new ProfilerMarker("CDLC.BusCab.Signs");
        private static readonly ProfilerMarker s_MirrorsMarker = new ProfilerMarker("CDLC.BusCab.Mirrors");
        private static readonly ProfilerMarker s_RidersMarker = new ProfilerMarker("CDLC.BusCab.Riders");

        private const int kHoldPoseFrames = 15;

        // Built models by shape, kept for the session (a few hundred KB each): taking the same bus model again is instant.
        private static readonly Dictionary<KitModelKey, CabModel> s_Models = new Dictionary<KitModelKey, CabModel>();
        private static readonly Dictionary<KitModelKey, Task<KitModelData>> s_Builds = new Dictionary<KitModelKey, Task<KitModelData>>();
        private static readonly HashSet<KitModelKey> s_Failed = new HashSet<KitModelKey>();
        // The head lamps of each built model that has them (model space), for the real lights.
        private static readonly Dictionary<KitModelKey, (float3 first, float3 second)> s_HeadLamps = new Dictionary<KitModelKey, (float3, float3)>();

        private Entity m_Bus = Entity.Null;
        private bool m_OwnAsset;                 // the driven bus is one of ours: our model replaces it outside too
        private readonly List<Section> m_Sections = new List<Section>();
        private float m_Doors;                   // the widest open door of the bus (sounds, cab openness, the telltale)
        private bool m_DoorsMoving;              // a door of the bus is opening or closing (the warning beacon)
        private readonly DoorMotion m_NoSectionDoors = new DoorMotion();
        private bool m_EyeSet;
        // The bus's cameras besides the driver's seat (Core API 14): its profiles' "cameras", the rear section's on section 1; made again
        // with the sections (the key tells the Core wrapper to make its list again)
        private readonly List<(VehicleProfile.CameraData Camera, int Section)> m_OtherCameras = new List<(VehicleProfile.CameraData, int)>();
        private int m_CamerasKey;
        private bool m_BodyHidden;
        private bool m_WholeHidden;
        private float m_WheelRoll;
        private int m_LogCooldown;

        // The update's own time (Stopwatch ticks) while the model is shown, logged every kPerfLogSeconds.
        private const float kPerfLogSeconds = 60f;
        private long m_PerfTicks, m_PerfWorst;
        private int m_PerfFrames;
        private float m_PerfNext = kPerfLogSeconds;

        private LightingSystem m_Lighting;

        // The LED next-stop screen (see UpdateLedScreen).
        private const float kLedDotsPerSecond = 24f;
        private const float kLedTextRefreshSeconds = 2f;
        private const float kCabLedNits = 60f;          // the cab screen's prefix, as bright as its amber (CabMaterials' glow)
        private const float kLedArrivingMeters = 150f;
        private static LedFont s_LedFont;
        private static Task<LedFont> s_LedFontLoad;
        private static bool s_LedFontFailed;
        private LedSign m_LedScreen;
        private string m_LedPrefix = "";
        private string m_LedBlock = "";
        private string m_LedMessage = "";
        private int m_LedKey = int.MinValue;
        private float m_LedNextText;

        private IDisposable m_MaterialResources;
        private readonly string m_CoreOwner = Core.kOwner + ".Cab." + Guid.NewGuid().ToString("N");

        protected override void OnCreate()
        {
            base.OnCreate();
            m_MaterialResources = CabMaterials.AcquireResources();
        }

        protected override void OnUpdate()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            using ProfilerMarker.AutoScope profile = s_UpdateMarker.Auto();
            try
            {
                { long tc = CabBlocks.Now; m_Core = ReadCore(); CabBlocks.Add(CabBlocks.ReadCore, tc); }
                m_GlassDetail = Mod.Settings != null ? Mod.Settings.GlassDetail : GlassDetail.High;
                GlassQualityLink.Sync(m_GlassDetail);
                if (m_Cameras == null)
                    m_Cameras = World.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
                SignCamera.Update(m_Cameras != null && m_Cameras.activeCamera != null ? m_Cameras.activeCamera : Camera.main);
                float dt = Mathf.Min(UnityEngine.Time.deltaTime, 0.1f);
                m_BuildDeadline = CabBlocks.Now + (long)(kBuildBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                m_BuildStepped = false;
                { long t = CabBlocks.Now; UpdateDoors(dt); CabBlocks.Add(CabBlocks.Doors, t); }
                if (ModuleOn(CabModule.DoorSounds))
                    try { long t = CabBlocks.Now; DoorSounds(); CabBlocks.Add(CabBlocks.DoorSounds, t); } catch (Exception ex) { ModuleFailed(CabModule.DoorSounds, ex); }
                if (ModuleOn(CabModule.Signals))
                    try { long t = CabBlocks.Now; UpdateSignals(UnityEngine.Time.unscaledTime); CabBlocks.Add(CabBlocks.Signals, t); } catch (Exception ex) { ModuleFailed(CabModule.Signals, ex); }
                if (ModuleOn(CabModule.Driveline))
                    try { long t = CabBlocks.Now; DrivelineLink(); CabBlocks.Add(CabBlocks.Driveline, t); } catch (Exception ex) { ModuleFailed(CabModule.Driveline, ex); }
                if (ModuleOn(CabModule.Haptics))
                    try { long t = CabBlocks.Now; GamepadHaptics.Tick(UnityEngine.Time.unscaledDeltaTime); CabBlocks.Add(CabBlocks.Haptics, t); } catch (Exception ex) { ModuleFailed(CabModule.Haptics, ex); }
                { long t = CabBlocks.Now; PrepareModelsAhead(); CabBlocks.Add(CabBlocks.Prepare, t); }
                { long t = CabBlocks.Now; Run(dt); CabBlocks.Add(CabBlocks.Run, t); }
                if (ModuleOn(CabModule.Overlay))
                    try { long t = CabBlocks.Now; UpdateOverlay(); CabBlocks.Add(CabBlocks.Outside, t); } catch (Exception ex) { ModuleFailed(CabModule.Overlay, ex); }
            }
            catch (Exception ex)
            {
                BusFailed(ex);
            }
            finally
            {
                MeasureUpdate(start);
            }
        }

        // ---- failures
        // A part of the update the bus can be drawn without fails on its own: it is released and tried again later, the bus stays
        // (FailureGate: 1, 2, 4, 8 s, then off until another bus is taken over). A failure of the bus itself releases it, and the same
        // vehicle model is taken over again only after the gate's pause; after five failures in a row the game's own bus stays for the
        // session. A bus counts as working again once it has been drawn complete for kHealthySeconds.
        private enum CabModule { DoorSounds, Signals, Driveline, Haptics, Overlay, Led, Ride, Skins, Readers, Signs, Cluster, Mirrors, Headlights, Riders }

        private const float kHealthySeconds = 10f;
        private readonly FailureGate<CabModule> m_ModuleGate = new FailureGate<CabModule>();
        private readonly FailureGate<Entity> m_BusGate = new FailureGate<Entity>();
        private readonly HashSet<Entity> m_GaveUpLogged = new HashSet<Entity>();
        private Entity m_BusPrefab = Entity.Null;
        private float m_ReadySince = -1f;

        private bool ModuleOn(CabModule module) => m_ModuleGate.Allowed(module, UnityEngine.Time.unscaledTime);

        private void ModuleFailed(CabModule module, Exception error)
        {
            float delay = m_ModuleGate.Fail(module, UnityEngine.Time.unscaledTime);
            Mod.log.Warn($"Bus {module} failed ({m_ModuleGate.Failures(module)} of {FailureGate<CabModule>.kGiveUpAfter}), " +
                $"{(float.IsInfinity(delay) ? "off until another bus is taken over" : $"tried again in {delay:0} s")}; the rest of the bus goes on: " +
                $"{error.GetType().Name}: {error.Message}\n{error.StackTrace}");
            try { ReleaseModule(module); }
            catch (Exception cleanup) { Mod.log.Warn($"Bus {module}: releasing it after the failure failed too: {cleanup.GetType().Name}: {cleanup.Message}"); }
        }

        private void ReleaseModule(CabModule module)
        {
            switch (module)
            {
                case CabModule.DoorSounds: m_DoorSounds.Reset(); break;
                case CabModule.Overlay: ReleaseOverlay(); break;
                case CabModule.Led: ReleaseLedScreen(); break;
                case CabModule.Skins: m_Skins.Update(""); break;
                case CabModule.Readers: ReleaseReaderLights(); break;
                case CabModule.Signs: ReleaseSigns(); break;
                case CabModule.Cluster: ReleaseCluster(); break;
                case CabModule.Mirrors: m_Mirrors.Destroy(); m_MirrorInstance = null; break;
                case CabModule.Headlights: m_Headlights.Destroy(); m_HeadlightInstance = null; break;
                case CabModule.Riders: ReleaseRiders(); break;
            }
        }

        private void BusFailed(Exception error)
        {
            Entity prefab = m_BusPrefab;
            if (prefab == Entity.Null)
            {
                // not a bus of ours yet (reading the driving mod, the doors): as before, once in 600 updates
                if (m_LogCooldown-- <= 0)
                {
                    Mod.log.Warn($"Bus interior update failed: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
                    m_LogCooldown = 600;
                }
            }
            else
            {
                float delay = m_BusGate.Fail(prefab, UnityEngine.Time.unscaledTime);
                Mod.log.Warn($"Bus model for {m_Bus} failed ({m_BusGate.Failures(prefab)} of {FailureGate<Entity>.kGiveUpAfter}); the game's own bus is shown, " +
                    $"{(float.IsInfinity(delay) ? "and this vehicle model is not taken over again this session" : $"taken over again in {delay:0} s")}: " +
                    $"{error.GetType().Name}: {error.Message}\n{error.StackTrace}");
            }
            try
            {
                ReleaseBus();
                ReleaseOverlay();
            }
            catch (Exception cleanup)
            {
                if (m_LogCooldown-- <= 0)
                {
                    Mod.log.Warn($"Bus release after a failure failed: {cleanup.GetType().Name}: {cleanup.Message}");
                    m_LogCooldown = 600;
                }
            }
        }

        /// <summary>Whether the vehicle model may be taken over now (the bus gate); says once when it was given up.</summary>
        private bool MayTakeOver(Entity vehicle, Entity prefab)
        {
            if (m_BusGate.Allowed(prefab, UnityEngine.Time.unscaledTime))
                return true;
            if (m_BusGate.GaveUp(prefab) && m_GaveUpLogged.Add(prefab))
                Mod.log.Warn($"Bus model for {vehicle}: its vehicle model failed {FailureGate<Entity>.kGiveUpAfter} times in a row; the game's own bus is shown for the rest of the session.");
            return false;
        }

        // ---- construction within the frame
        // The driven bus's model upload, glass and native cab are made a step at a time within this many milliseconds of a frame (at
        // least one step a frame): until they are complete the game's own body stays.
        private const double kBuildBudgetMs = 2.0;
        private long m_BuildDeadline;
        private bool m_BuildStepped;

        /// <summary>What is left of this frame's construction budget (Stopwatch ticks; 1 for the frame's first step when it is spent; 0: none).</summary>
        private long BuildBudget()
        {
            long left = m_BuildDeadline - CabBlocks.Now;
            if (left > 0) { m_BuildStepped = true; return left; }
            if (m_BuildStepped) return 0;
            m_BuildStepped = true;
            return 1;
        }

        private void PrepareModelsAhead()
        {
            try { PrepareAhead(); }
            catch (Exception ex)
            {
                // a failure here only costs the head start: the first drive builds its model as it always did
                m_PrepareFailed = true;
                Mod.log.Warn($"Preparing bus models ahead failed and is off: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void MeasureUpdate(long start)
        {
            if (m_Sections.Count == 0)
                return;
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            m_PerfTicks += ticks;
            m_PerfWorst = Math.Max(m_PerfWorst, ticks);
            m_PerfFrames++;
            CabBlocks.Frame();
            float now = UnityEngine.Time.unscaledTime;
            if (now < m_PerfNext)
                return;
            m_PerfNext = now + kPerfLogSeconds;
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int people = 0;
            foreach (RiderState state in m_Riders.Values)
                foreach (GameObject figure in state.Figures)
                    if (figure != null) people++;
            Mod.log.Info($"Bus interior: {m_PerfTicks * ms / Math.Max(1, m_PerfFrames):0.00} ms a frame on average, {m_PerfWorst * ms:0.0} ms at most, over {m_PerfFrames} frames; " +
                $"{people} people drawn ({(m_CitizensShown ? "game citizens" : "simple figures")}), mirrors {(Mod.Settings != null && Mod.Settings.RealMirrors ? "on" : "off")}.");
            Mod.log.Info($"Bus interior blocks (ms a frame): {CabBlocks.Describe()}. {BusGlass.Spent()}.");
            m_PerfTicks = m_PerfWorst = 0;
            m_PerfFrames = 0;
            CabBlocks.Reset();
        }

        private readonly BusDoorSounds m_DoorSounds = new BusDoorSounds();
        private readonly BusSkins m_Skins = new BusSkins();
        private readonly List<BusInteriorSpec> m_WrapSpecs = new List<BusInteriorSpec>();
        private Entity m_WrapBus = Entity.Null;

        private readonly List<float> m_WrapLengths = new List<float>();
        private readonly BusCabSounds m_CabSounds = new BusCabSounds();

        /// <summary>The doors' hiss and clunk when the shift opens and closes them, or the bus ridden boards (with or without the interior).</summary>
        private void DoorSounds()
        {
            bool riding = m_Core.Riding && BusRide.Riding;
            Entity bus = riding ? BusRide.Bus : BusState.Bus;
            if (!(riding || BusState.InShift) || bus == Entity.Null || !EntityManager.Exists(bus))
            {
                m_DoorSounds.Reset();
                m_CabSounds.Update(Vector3.zero, Quaternion.identity, BusInteriorSpec.Default, false);
                return;
            }

            bool open = riding ? BusRide.DoorsOpen : BusState.State == ShiftState.DoorsOpening || BusState.State == ShiftState.DoorsOpen;
            Vector3 position;
            Quaternion rotation;
            if (m_Core.HasCab && m_Core.BodyPosed)
            {
                position = m_Core.BodyPosition;
                rotation = m_Core.BodyRotation;
            }
            else
            {
                if (!EntityManager.TryGetComponent(bus, out Game.Objects.Transform transform))
                    return;
                position = new Vector3(transform.m_Position.x, transform.m_Position.y, transform.m_Position.z);
                rotation = new Quaternion(transform.m_Rotation.value.x, transform.m_Rotation.value.y, transform.m_Rotation.value.z, transform.m_Rotation.value.w);
            }

            // The front door: on the kerb side, just behind the front of the bus.
            BusInteriorSpec spec = m_Sections.Count > 0 ? m_Sections[0].Spec : BusInteriorSpec.Default;
            var door = new Vector3(-spec.DriverSide * spec.HalfWidth, 1.2f, spec.FrontZ - 0.8f);
            m_DoorSounds.Update(open, position + rotation * door);
            // the cab's own sounds (the stop request, the indicator relay) are the driver's
            if (riding)
                m_CabSounds.Update(Vector3.zero, Quaternion.identity, BusInteriorSpec.Default, false);
            else
                m_CabSounds.Update(position, rotation, spec, true);
        }

        // ---------------------------------------------------------------- the driving mod: load, kneeling, the doors

        private float m_PayloadSet = -1f;
        private bool m_KneelSet;
        private float m_CabOpenSet = -1f;

        /// <summary>
        /// API 4: the passengers on board make the bus heavier (the driveline pulls away slower and stops longer), the bus kneels
        /// while its doors are open (option), and the open doors let the outside's sound into the cab.
        /// </summary>
        private void DrivelineLink()
        {
            if (!m_Core.HasApi4)
                return;

            Entity bus = BusState.Bus;
            bool mine = BusState.InShift && bus != Entity.Null && m_Core.Driving && m_Core.Vehicle == bus;
            float payload = mine ? BusState.Passengers * kPassengerKg : 0f;
            if (Mathf.Abs(payload - m_PayloadSet) > 1f)
            {
                m_PayloadSet = payload;
                Core.SetPayloadMass(payload);
            }

            BusSettings settings = Mod.Settings;
            bool kneel = mine && settings != null && settings.KneelAtStops && (BusState.State == ShiftState.DoorsOpening || BusState.State == ShiftState.DoorsOpen);
            if (kneel != m_KneelSet)
            {
                m_KneelSet = kneel;
                Core.SetKneel(kneel);
            }

            float open = mine ? m_Doors : 0f;
            if (Mathf.Abs(open - m_CabOpenSet) > 0.02f || (open == 0f) != (m_CabOpenSet == 0f))
            {
                m_CabOpenSet = open;
                Core.SetCabOpen(open);
            }
        }

        private void Run(float dt)
        {
            BusSettings settings = Mod.Settings;
            bool interior = settings != null && settings.ShowInterior;
            bool exterior = settings != null && settings.ShowExterior && m_Core.HasApi4;
            if (!(interior || exterior) || !m_Core.HasCab || !m_Core.Viewing)
            {
                ReleaseBus();
                return;
            }

            Entity vehicle = m_Core.Subject;
            if (vehicle != m_Bus)
            {
                { long tr = CabBlocks.Now; ReleaseBus(); CabBlocks.Add(CabBlocks.Release, tr); }
                if (vehicle == Entity.Null || !IsBus(vehicle))
                    return;
                Entity prefab = EntityManager.TryGetComponent(vehicle, out PrefabRef prefabRef) ? prefabRef.m_Prefab : Entity.Null;
                if (!MayTakeOver(vehicle, prefab))
                    return;

                m_Bus = vehicle;
                m_BusPrefab = prefab;
                m_ReadySince = -1f;
                m_ModuleGate.Clear();
                { long tm = CabBlocks.Now; Measure(vehicle); CabBlocks.Add(CabBlocks.Measure, tm); }
                m_OwnAsset = IsOurAsset(m_Bus);
                Mod.log.Info($"Bus model for {vehicle}: {(m_OwnAsset ? "one of our buses, drawn inside and out" : "not one of our buses, our cab inside only, its own body outside")}.");
                m_CabSounds.Replace();
            }

            if (m_Sections.Count == 0)
            {
                if (m_AssemblyPending && UnityEngine.Time.unscaledTime >= m_AssemblyRetryAt)
                    Measure(vehicle);
                return;
            }

            // A bus that is not ours keeps the game's (or its author's) body outside: our model only as the cab around the camera.
            exterior &= m_OwnAsset;
            if (!interior && !exterior)
            {
                ReleaseBus();
                return;
            }

            // The driver's cab for the camera, as soon as the bus is known (the camera needs it before the model is built): the eye, the
            // look-down, the seat and the saloon's insulation from the model's profile (Core API 10; an older Core takes the eye only).
            if (interior)
            {
                long te = CabBlocks.Now;
                VehicleProfile.CabData driverCab = BusInterior.DriverCab(m_Sections[0].Spec);
                // the model eye, moved by what the driver set in the options (the formal eye, what the mirrors are set up from, stays)
                Vector3 eye = ToVector(BusInterior.CameraEye(m_Sections[0].Spec) + CabViewRules.Offset(Mod.Settings?.CabEyeHeight ?? 0, Mod.Settings?.CabEyeSide ?? 0));
                // a bus with cameras besides the driver's seat sends them all (Core API 14); the others only the cab, as before
                if (m_OtherCameras.Count > 0)
                    Core.SetCameras(m_CamerasKey, eye, driverCab.LookDownDeg, driverCab.SeatSpring, driverCab.Insulation, m_OtherCameras, m_CoreOwner);
                else
                    Core.SetCab(eye, driverCab.LookDownDeg, driverCab.SeatSpring, driverCab.Insulation, m_CoreOwner);
                CabBlocks.Add(CabBlocks.Eye, te);
                m_EyeSet = true;
            }
            else if (m_EyeSet)
            {
                Core.ClearCabEye(m_CoreOwner);
                m_EyeSet = false;
            }

            // Our model has the walls, roof and window openings: the game's shell would only shade it and show through. With the
            // exterior the game's body is not drawn in the chase view either, once every section of ours is there.
            // Only for our own bus assets: an official or workshop bus we do not model keeps its own body outside (it may be
            // articulated, and we have no model for it), so the chase view still shows the real vehicle.
            bool cab = m_Core.CabView;
            bool ready = m_Sections[0].Instance != null && m_Sections[0].Instance.Alive &&
                (!m_OwnAsset || m_Sections[0].Glass?.Ready == true) && NativeReady(m_Sections[0]);
            bool allReady = ready;
            for (int i = 1; i < m_Sections.Count; i++)
                allReady &= m_Sections[i].Instance != null && m_Sections[i].Instance.Alive &&
                    (!m_OwnAsset || m_Sections[i].Glass?.Ready == true) && NativeReady(m_Sections[i]);
            if (m_Assembly != null)
            {
                long ta = CabBlocks.Now;
                ReadRearPose();
                allReady &= DebouncedAssemblyReady(AssemblyCanReplace() && AuthoredJointReady());
                CabBlocks.Add(CabBlocks.Assembly, ta);
            }
            bool cabHide = (m_Assembly != null ? allReady : ready) && interior;
            if (cabHide != m_BodyHidden)
            {
                Core.SetCabBodyHidden(cabHide, m_CoreOwner);
                m_BodyHidden = cabHide;
            }

            bool wholeHide = allReady && exterior && (interior || !cab);
            if (wholeHide != m_WholeHidden)
            {
                Core.SetBodyHidden(wholeHide, m_CoreOwner);
                m_WholeHidden = wholeHide;
            }

            WatchNativeBody();
            AuditMaterials();

            bool show = cab ? interior : exterior;
            // Keep the complete native body until every replacement section includes its windows.
            if (m_OwnAsset || m_Assembly != null) show &= allReady;
            if (!allReady) m_ReadySince = -1f;
            else if (m_ReadySince < 0f) m_ReadySince = UnityEngine.Time.unscaledTime;
            else if (UnityEngine.Time.unscaledTime - m_ReadySince >= kHealthySeconds && m_BusGate.Failures(m_BusPrefab) > 0)
                m_BusGate.Succeeded(m_BusPrefab);
            if (ModuleOn(CabModule.Led))
                try { using (s_SignsMarker.Auto()) { long tl = CabBlocks.Now; UpdateLedScreen(); CabBlocks.Add(CabBlocks.Led, tl); } }
                catch (Exception ex) { ModuleFailed(CabModule.Led, ex); }
            CabMaterials.UpdateFlash(UnityEngine.Time.unscaledTimeAsDouble);
            if (ModuleOn(CabModule.Ride))
                try { RideMotion(dt); PickPassengerSeat(); }
                catch (Exception ex) { ModuleFailed(CabModule.Ride, ex); }
            float speed = m_Core.Speed;
            float wheelRadius = VehicleModels.Find(m_Sections[0].Spec.ModelVariant)?.WheelRadius ?? BusInterior.WheelRadius;
            m_WheelRoll = Mathf.Repeat(m_WheelRoll + m_Core.WorldSpeed * dt / wheelRadius, 2f * Mathf.PI);
            KitLights lights = Lights();
            var pose = new BusInterior.Pose
            {
                SteerDeg = m_Core.WheelAngle * Mathf.Rad2Deg * m_Core.SteeringRatio,
                SpeedKmh = Mathf.Abs(speed) * 3.6f,
                Rpm = EngineRpm(),
                DoorOpen = float3.zero,         // per section below
                Lights = lights,
                Outside = !cab,
                WheelSteerDeg = m_Core.WheelAngle * Mathf.Rad2Deg,
                WheelRoll = m_WheelRoll,
                Lamps = exterior ? OutsideLamps(speed, lights) : BusInterior.ExteriorLamps.None
            };

            // the body wrap: its pictures, each section's share of them (an articulated bus: one picture over both)
            if (m_WrapBus != m_Bus || m_WrapSpecs.Count != m_Sections.Count)
            {
                // another bus: its sections' layout, which model's wraps are used, and the template for it in the wraps folder
                m_WrapBus = m_Bus;
                m_WrapSpecs.Clear();
                m_WrapLengths.Clear();
                foreach (Section s in m_Sections)
                {
                    m_WrapSpecs.Add(s.Spec);
                    m_WrapLengths.Add(new BusInterior(s.Spec).WrapLength);
                }
                BusSkins.SetVehicle(m_WrapSpecs);
                if (exterior)
                    m_TemplateAt = UnityEngine.Time.unscaledTime + kTemplateDelaySeconds;
            }
            pose.Wrap = BusInterior.WrapFaces.None;
            if (ModuleOn(CabModule.Skins))
                try
                {
                    long tk = CabBlocks.Now;
                    m_Skins.Update(exterior && settings != null ? settings.BodyWrap : "");
                    CabBlocks.Add(CabBlocks.Skins, tk);
                    pose.Wrap = m_Skins.Faces;
                }
                catch (Exception ex) { ModuleFailed(CabModule.Skins, ex); }
            if (m_TemplateAt > 0f && UnityEngine.Time.unscaledTime >= m_TemplateAt)
            {
                // the template pictures (a reference for wrap authors, PNG files written to the wraps folder; 100 ms for a K18) are no
                // part of drawing the bus: a few seconds after the take-over, not in its first frame
                m_TemplateAt = 0f;
                long tw = CabBlocks.Now;
                BusSkins.WriteTemplate();
                CabBlocks.Add(CabBlocks.WrapWrite, tw);
            }
            float wrapTotal = 0f;
            foreach (float l in m_WrapLengths)
                wrapTotal += l;
            float wrapStart = 0f;

            // The kneel the camera gave the body pose, undone for the wheels (the front section's; an authored rear takes the roll, see
            // ReadRearPose).
            quaternion kneel = quaternion.identity;
            float3 kneelOffset = float3.zero;
            if (m_Core.HasApi4)
            {
                Core.GetKneelTransform(out Vector3 offset, out Quaternion rotation);
                kneel = new quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
                kneelOffset = new float3(offset.x, offset.y, offset.z);
            }

            // The fold of an articulated bus for the bellows (from this frame's poses, else the last ones held).
            float fold = 0f;
            if (m_Sections.Count > 1)
            {
                Quaternion frontRotation = m_Core.BodyPosed ? m_Core.BodyRotation : m_Sections[0].HeldRotation;
                Quaternion rearRotation = m_Assembly != null && m_Rear.Valid ? m_Rear.Rotation :
                    Core.TryGetRenderPose(m_Sections[1].Entity, out _, out Quaternion rr) ? rr : m_Sections[1].HeldRotation;
                fold = JointFold(frontRotation, rearRotation);
            }

            for (int i = 0; i < m_Sections.Count; i++)
            {
                Section section = m_Sections[i];
                if (section.Instance == null || !section.Instance.Alive)
                {
                    long tcr = CabBlocks.Now;
                    section.Instance = TryCreate(section);
                    CabBlocks.Add(CabBlocks.Create, tcr);
                    if (section.Instance == null)
                        continue;
                    // the glass and the native cab hang under the instance: made again for this one
                    ReleaseConstruction(section);
                    section.Dressed = false;
                }

                {
                    long tg = CabBlocks.Now;
                    float now = UnityEngine.Time.unscaledTime;
                    if (m_OwnAsset && section.Glass == null && now >= section.NextGlassAttempt)
                        StartGlass(section);
                    if (section.Glass != null && !section.Glass.Ready)
                        StepGlass(section);
                    if (section.Native == null && WantsNativeCab(section) && now >= section.NextNativeAttempt)
                        StartNativeCab(section);
                    if (section.Native != null && !section.Native.Ready)
                        StepNativeCab(section);
                    CabBlocks.Add(CabBlocks.Glass, tg);
                }

                Vector3 position = default;
                Quaternion rotation = Quaternion.identity;
                bool posed = false;
                if (show && i == 0)
                {
                    position = m_Core.BodyPosition;
                    rotation = m_Core.BodyRotation;
                    posed = m_Core.BodyPosed;
                }
                else if (show && i == 1 && m_Assembly != null)
                {
                    position = m_Rear.Position;
                    rotation = m_Rear.Rotation;
                    posed = m_Rear.Valid;
                }
                else if (show)
                    posed = Core.TryGetRenderPose(section.Entity, out position, out rotation);
                if (posed)
                {
                    section.HeldPosition = position;
                    section.HeldRotation = rotation;
                    section.HeldFrames = 0;
                }
                else if (show && section.HeldFrames < kHoldPoseFrames)
                {
                    // A pose that could not be read for a frame: the last one, rather than hiding the model (it blinked).
                    position = section.HeldPosition;
                    rotation = section.HeldRotation;
                    section.HeldFrames++;
                    posed = true;
                }

                section.Instance.Visible = posed;
                if (section.Glass != null)
                    section.Glass.Visible = posed && !GlassDetailRules.HidesCabGlass(m_GlassDetail, m_Core.CabView);
                if (section.Native != null)
                {
                    section.Native.Visible = posed;
                    if (posed)
                        section.Native.Pose(EntityManager, World, section.Entity);
                }
                if (!posed)
                    continue;

                using (s_MaterialsMarker.Auto())
                using (new CabBlocks.Scope(CabBlocks.Materials))
                {
                    if (!section.Dressed)
                    {
                        Dress(section);
                        section.HeadBeam = -1;
                    }
                    if (section.HeadBeam != (int)m_Beam)
                    {
                        // the beam's lamp material (cached per beam): given again only when the beam changes
                        Material head = HeadMaterial(m_Beam);
                        section.Instance.OverrideMaterial(BusInterior.HeadLamps, head);
                        if (head != null)
                            section.HeadBeam = (int)m_Beam;
                    }
                    // every frame: the one livery material is recoloured in place when the colour differs (CabMaterials.Livery)
                    section.Instance.OverrideMaterial(BusInterior.Livery, CabMaterials.Livery(LiveryColour(), section.LiveryGloss));
                    if (pose.Wrap != BusInterior.WrapFaces.None && i < m_WrapLengths.Count)
                    {
                        float length = m_WrapLengths[i];
                        for (int f = 0; f < BusInterior.WrapFaceCount; f++)
                            if (((int)pose.Wrap & (1 << f)) != 0)
                                section.Instance.OverrideMaterial(BusInterior.WrapLeft + f, m_Skins.Material(i, (BusInterior.WrapFace)f, wrapStart, length, wrapTotal));
                        // the door leaves' pieces of the wrap wear their side's picture (they move with the leaves)
                        foreach ((int part, BusInterior.WrapFace face) in BusInterior.WrapDoorPieces(section.Spec))
                            section.Instance.OverrideMaterial(part, m_Skins.Material(i, face, wrapStart, length, wrapTotal));
                        wrapStart += length;
                    }
                }

                using (s_PoseMarker.Auto())
                {
                    pose.Position = new float3(position.x, position.y, position.z);
                    pose.Rotation = new quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
                    { long ts = CabBlocks.Now; pose.StrapSwing = StrapSwing(section, position, rotation, dt); CabBlocks.Add(CabBlocks.Straps, ts); }
                    bool rearKneel = i == 1 && m_Assembly != null && m_Rear.Valid;
                    pose.Kneel = i == 0 ? kneel : rearKneel ? m_Rear.Kneel : quaternion.identity;
                    pose.KneelOffset = i == 0 ? kneelOffset : rearKneel ? m_Rear.KneelOffset : float3.zero;
                    pose.JointFold = i == 0 ? fold : 0f;
                    pose.DoorOpen = section.Doors.Channels;
                    pose.FourthDoorOpen = section.Doors.Fourth;
                    { long tp = CabBlocks.Now; BusInterior.Apply(section.Instance, pose, section.Spec); CabBlocks.Add(CabBlocks.Apply, tp); }
                    // ApplyExterior updates visibility every frame; the native replacement owns these static surfaces.
                    if (section.Native?.Ready == true)
                    {
                        foreach (int part in NativeCabOwnership.ReplacedParts(VehicleModels.Find(section.Spec.ModelVariant).Mesh))
                            section.Instance.Show(part, false);
                        section.Native.LitLamps = BusInterior.LitLampParts(pose, section.Spec);
                    }
                    if (ModuleOn(CabModule.Readers))
                        try { long tr = CabBlocks.Now; UpdateReaders(section, i == 0); CabBlocks.Add(CabBlocks.Readers, tr); }
                        catch (Exception ex) { ModuleFailed(CabModule.Readers, ex); }
                }
            }

            {
                long tj = CabBlocks.Now, deform0 = JointRenderer.StatDeformTicks, upload0 = JointRenderer.StatUploadTicks;
                UpdateAuthoredJoint(show);
                CabBlocks.Add(CabBlocks.Joint, tj);
                CabBlocks.AddTicks(CabBlocks.JointDeform, JointRenderer.StatDeformTicks - deform0);
                CabBlocks.AddTicks(CabBlocks.JointUpload, JointRenderer.StatUploadTicks - upload0);
            }
            CabMaterials.SetDaylight(Daylight());
            using (s_SignsMarker.Auto())
            {
                if (ModuleOn(CabModule.Signs))
                    try { long to = CabBlocks.Now; UpdateOutside(!cab && exterior && !m_Core.Riding && (m_Assembly == null || allReady)); CabBlocks.Add(CabBlocks.Outside, to); }
                    catch (Exception ex) { ModuleFailed(CabModule.Signs, ex); }
                if (ModuleOn(CabModule.Cluster))
                    try { long tk = CabBlocks.Now; UpdateCluster(cab && interior && (m_Assembly == null || allReady)); CabBlocks.Add(CabBlocks.Cluster, tk); }
                    catch (Exception ex) { ModuleFailed(CabModule.Cluster, ex); }
            }
            if (ModuleOn(CabModule.Mirrors))
                try
                {
                    using (s_MirrorsMarker.Auto())
                    using (new CabBlocks.Scope(CabBlocks.Mirrors))
                        UpdateMirrors(cab && interior && (m_Assembly == null || allReady));
                }
                catch (Exception ex) { ModuleFailed(CabModule.Mirrors, ex); }
            if (ModuleOn(CabModule.Headlights))
                try { long th = CabBlocks.Now; UpdateHeadlights(show); CabBlocks.Add(CabBlocks.Headlights, th); }
                catch (Exception ex) { ModuleFailed(CabModule.Headlights, ex); }
            if (ModuleOn(CabModule.Riders))
                try
                {
                    using (s_RidersMarker.Auto())
                    using (new CabBlocks.Scope(CabBlocks.Riders))
                        UpdateRiders(dt, cab, exterior);
                }
                catch (Exception ex) { ModuleFailed(CabModule.Riders, ex); }
        }

        // ---------------------------------------------------------------- the bus

        private bool IsBus(Entity vehicle)
        {
            return EntityManager.TryGetComponent(vehicle, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out PublicTransportVehicleData data) &&
                data.m_TransportType == TransportType.Bus && EntityManager.HasComponent<Car>(vehicle);
        }

        /// <summary>The interior's spec from the game's bus: body size, axles from the wheel bones, doors, the traffic side.</summary>
        private void Measure(Entity bus)
        {
            bool leftHand = false;
            CityConfigurationSystem city = World.GetExistingSystemManaged<CityConfigurationSystem>();
            if (city != null)
                leftHand = city.leftHandTraffic;

            var sections = new List<Entity> { bus };
            if (EntityManager.TryGetBuffer(bus, true, out DynamicBuffer<LayoutElement> layout))
            {
                for (int i = 0; i < layout.Length; i++)
                {
                    if (layout[i].m_Vehicle != bus && layout[i].m_Vehicle != Entity.Null)
                        sections.Add(layout[i].m_Vehicle);
                }
            }

            bool articulated = sections.Count > 1;
            int count = math.min(sections.Count, 2);
            var specs = new BusInteriorSpec[count];
            VehicleProfile leading = AuthoredProfileOf(bus);
            if (leading?.Assembly != null)
            {
                long tb = CabBlocks.Now;
                bool bound = TryBindAuthoredAssembly(leading, sections, out specs);
                CabBlocks.Add(CabBlocks.Bind, tb);
                if (!bound) return;
                count = specs.Length;
            }
            else
            {
                for (int i = 0; i < count; i++)
                    specs[i] = SpecOf(sections[i], i == 0 ? (articulated ? BusSection.Front : BusSection.Solo) : BusSection.Rear, leftHand);
                if (count == 2)
                    ArticulatedJoint(bus, sections[0], sections[1], ref specs[0], ref specs[1]);
            }

            for (int i = 0; i < count; i++)
            {
                BusInteriorSpec spec = specs[i].Snapped();
                var section = new Section { Entity = sections[i], Spec = spec, Key = new BusInterior(spec).Key };
                m_Sections.Add(section);
                StartBuild(section);
                Mod.log.Info($"Bus model for {bus} section {i}: {spec.Section}, {spec.RearZ:0.0}..{spec.FrontZ:0.0} m, half width {spec.HalfWidth:0.00}, roof {spec.RoofY:0.00}, axles {spec.FrontAxleZ:0.0} / {spec.RearAxleZ:0.0}, {spec.Doors} doors, driver {(spec.DriverSide < 0f ? "left" : "right")}.");
            }

            CollectCameras(bus);
        }

        /// <summary>The cameras the bus's profiles declare besides the driver's seat, each on its section (Core takes 16 with the driver's seat).</summary>
        private void CollectCameras(Entity bus)
        {
            m_OtherCameras.Clear();
            m_CamerasKey++;
            for (int i = 0; i < m_Sections.Count && i < 2; i++)
            {
                VehicleProfile.CameraData[] cameras = VehicleModels.Find(m_Sections[i].Spec.ModelVariant)?.Cameras;
                if (cameras == null)
                    continue;
                foreach (VehicleProfile.CameraData camera in cameras)
                    if (m_OtherCameras.Count < VehicleProfileJson.MaxCameras)
                        m_OtherCameras.Add((camera, i));
            }

            if (m_OtherCameras.Count > 0)
                Mod.log.Info($"Bus model for {bus}: {m_OtherCameras.Count} camera position(s) besides the driver's seat{(Core.HasApi14 ? "" : " (the driving mod is older than API 14: the driver's seat only)")}.");
        }

        // The rigid body ends this far from the joint (a gangway of about 0.9 m, like an 18 m bus's): folded to the stop, the
        // sections' corners stay clear of each other and the bellows span the rest.
        private const float kPortalFromJoint = 0.45f;

        /// <summary>
        /// The articulation from the game: the joint (the turntable's centre) on each section's axis at the attach points the
        /// game and the driving mod turn the sections about, so our bellows bend about the same point the bus folds about. The
        /// body ends are kept clear of the joint, and the front section gets the gangway's length.
        /// </summary>
        private void ArticulatedJoint(Entity bus, Entity front, Entity rear, ref BusInteriorSpec f, ref BusInteriorSpec r)
        {
            float j1 = f.RearZ - kPortalFromJoint, j2 = r.FrontZ + kPortalFromJoint;
            bool found = false;
            if (EntityManager.TryGetComponent(front, out PrefabRef fp) && EntityManager.TryGetComponent(fp.m_Prefab, out CarTractorData tractor) &&
                EntityManager.TryGetComponent(rear, out PrefabRef rp) && EntityManager.TryGetComponent(rp.m_Prefab, out CarTrailerData trailer))
            {
                found = true;
                j1 = tractor.m_AttachPosition.z;
                j2 = trailer.m_AttachPosition.z;
            }
            if (!math.isfinite(j1) || !math.isfinite(j2) || j1 > f.RearAxleZ - 0.5f || j2 < r.RearAxleZ + 1f)
            {
                Mod.log.Warn($"Bus model for {bus}: the joint ({j1:0.00} / {j2:0.00}) lies outside the sections; placed at their ends instead.");
                found = false;
                j1 = f.RearZ - kPortalFromJoint;
                j2 = r.FrontZ + kPortalFromJoint;
            }

            float frontEnd = f.RearZ, rearEnd = r.FrontZ;
            f.RearZ = math.max(f.RearZ, j1 + kPortalFromJoint);
            r.FrontZ = math.min(r.FrontZ, j2 - kPortalFromJoint);
            f.JointZ = j1;
            r.JointZ = j2;
            f.GangwayLength = (f.RearZ - j1) + (j2 - r.FrontZ);
            Mod.log.Info($"Bus model for {bus}: joint at {j1:0.00} m on the front section, {j2:0.00} m on the rear ({(found ? "the game's attach points" : "estimated")}); " +
                $"body ends {frontEnd:0.00} -> {f.RearZ:0.00} and {rearEnd:0.00} -> {r.FrontZ:0.00}, gangway {f.GangwayLength:0.00} m.");
        }

        /// <summary>The fold for the bellows: the rear section's heading turned about the front's up axis (radians, + to the right).</summary>
        private static float JointFold(Quaternion front, Quaternion rear)
        {
            Vector3 forward = Quaternion.Inverse(front) * rear * Vector3.forward;
            return Mathf.Atan2(forward.x, forward.z);
        }

        private BusInteriorSpec SpecOf(Entity vehicle, BusSection kind, bool leftHand)
        {
            var spec = BusInteriorSpec.Default;
            spec.Section = kind;
            spec.DriverSide = leftHand ? 1f : -1f;
            if (!EntityManager.TryGetComponent(vehicle, out PrefabRef prefabRef))
                return spec;

            PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            if (kind == BusSection.Solo && prefabs != null && prefabs.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab))
            {
                // one of our authored buses from a vehicle pack: its exact profile (dimensions, doors, cab), not measured
                VehicleProfile model = VehiclePacks.ForPrefab(prefab);
                if (model != null) return model.Spec;
                // the mod's own bus: its exact spec too (the glass, the signs and the embedded mesh are made for it)
                if (OwnBus.Is(prefab.name)) return OwnBus.Spec(leftHand);
            }
            if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out ObjectGeometryData geometry))
                spec = spec.FromBounds(geometry.m_Bounds.min, geometry.m_Bounds.max);

            float length = spec.FrontZ - spec.RearZ;
            spec.Doors = kind == BusSection.Solo ? (length > 10.5f ? 3 : 2) : 2;
            if (TryAxles(prefabRef.m_Prefab, out float front, out float rear))
            {
                spec.FrontAxleZ = front;
                spec.RearAxleZ = rear;
            }
            else
            {
                spec.FrontAxleZ = spec.FrontZ - 2.6f;
                spec.RearAxleZ = spec.RearZ + (kind == BusSection.Front ? 2.6f : 3.3f);
            }

            return spec;
        }

        /// <summary>
        /// Is this bus one of our own (a model we can draw ourselves inside and out)? The mod's own bus (Asset/CDLCBus12.cok, in
        /// the mod's folder; or by its name, for a prefab whose source cannot be read), or an authored bus from a vehicle pack
        /// (VehiclePacks: a profile beside its package). Only these are replaced outside by our model: an official, DLC or
        /// workshop bus keeps its own body in the chase view (it may be articulated, and we have no model for it).
        /// </summary>
        private bool IsOurAsset(Entity vehicle)
        {
            try
            {
                if (!EntityManager.TryGetComponent(vehicle, out PrefabRef prefabRef) || prefabRef.m_Prefab == Entity.Null)
                    return false;

                PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
                if (prefabs == null || !prefabs.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) || prefab == null)
                    return false;

                string path = prefab.asset?.path;
                string root = Mod.ModDirectory;
                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(root) &&
                    path.Replace('\\', '/').StartsWith(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    return true;

                if (string.Equals(prefab.name, OwnBus.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
                return VehiclePacks.ForPrefab(prefab) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Front and rear axle positions from the wheel bones of the bus mesh.</summary>
        private bool TryAxles(Entity prefab, out float front, out float rear)
        {
            front = float.MinValue;
            rear = float.MaxValue;
            if (!EntityManager.TryGetBuffer(prefab, true, out DynamicBuffer<Game.Prefabs.SubMesh> subMeshes))
                return false;

            for (int j = 0; j < subMeshes.Length; j++)
            {
                if (!EntityManager.TryGetBuffer(subMeshes[j].m_SubMesh, true, out DynamicBuffer<ProceduralBone> bones))
                    continue;

                for (int i = 0; i < bones.Length; i++)
                {
                    BoneType type = bones[i].m_Type;
                    if (type != BoneType.RollingTire && type != BoneType.SteeringTire && type != BoneType.FixedTire)
                        continue;

                    float z = bones[i].m_ObjectPosition.z + subMeshes[j].m_Position.z;
                    front = math.max(front, z);
                    rear = math.min(rear, z);
                }
            }

            return front > rear + 1f;
        }

        // ---------------------------------------------------------------- model

        // ---- models prepared ahead (the first drive of an authored bus should not wait for its model)
        // The model of every registered authored profile is built on a low-priority background thread (CabPrebuild) while the
        // player is not driving, its meshes are uploaded a couple of milliseconds a frame, and TryCreate then finds it ready. The
        // spec (and so the cache key) of an authored section is its profile's, exactly what Measure takes for the same bus.
        private const float kPrepareAfterSeconds = 15f, kPrepareRecheckSeconds = 5f;
        private const double kWarmBudgetMs = 2.0;
        private static readonly Dictionary<KitModelKey, CabModel> s_Warming = new Dictionary<KitModelKey, CabModel>();
        private static readonly Dictionary<KitModelKey, (string name, BusSection kind, long uploadTicks, int frames)> s_Ahead =
            new Dictionary<KitModelKey, (string, BusSection, long, int)>();
        private string m_WrapAhead;
        private int m_PreparedCount = -1;
        private float m_PrepareAt;
        private bool m_PrepareFailed;

        private void PrepareAhead()
        {
            if (m_PrepareFailed || m_Core.Driving)
                return;
            float now = UnityEngine.Time.unscaledTime;
            int count = VehicleModels.Count;
            if (count != m_PreparedCount && now >= kPrepareAfterSeconds && now >= m_PrepareAt)
            {
                m_PreparedCount = count;
                m_PrepareAt = now + kPrepareRecheckSeconds;
                // Every pack's mesh is read first, then the models are built: a mesh is what the first frame of a drive waits for (the
                // driver's eye is in it), and one read behind another bus's 1 to 10 s build was read by the main thread instead.
                var pending = new List<VehicleProfile>();
                foreach (VehicleProfile profile in VehicleModels.Snapshot())
                {
                    KitModelKey key = new BusInterior(profile.Spec.Snapped()).Key;
                    if (s_Models.ContainsKey(key) || s_Builds.ContainsKey(key) || s_Warming.ContainsKey(key) || s_Failed.Contains(key))
                        continue;
                    pending.Add(profile);
                    VehicleProfile meshAhead = profile;
                    CabPrebuild.Enqueue(() => { _ = meshAhead.Mesh; return 0; }, key);
                }

                foreach (VehicleProfile profile in pending)
                {
                    BusInteriorSpec spec = profile.Spec.Snapped();
                    KitModelKey key = new BusInterior(spec).Key;
                    VehicleProfile ahead = profile;
                    s_Ahead[key] = (profile.Name, spec.Section, 0, 0);
                    s_Builds[key] = CabPrebuild.Enqueue(() =>
                    {
                        _ = ahead.Mesh;                    // the pack's mesh, read and checked here instead of in the first frame of the drive
                        return KitBuild.BuildAll(new BusInterior(spec), new MeshKit());
                    }, key);
                }
                m_SelectedSeen = Entity.Null;          // a selection made before these were queued moves its build to the front now
            }

            long budget = (long)(kWarmBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            bool selected = SelectedAhead(out KitModelKey selectedKey, out string selectedName);
            PrepareWrapAhead(now);
            WarmGlassAhead(now, selected ? selectedName : null);   // not held back by the models still being built: the glass of a bus is its own step
            if (s_Ahead.Count == 0)
            {
                // the bus models are done (or none to do): the riders' figures take the frame's budget
                if (now >= kPrepareAfterSeconds && Mod.Settings != null && Mod.Settings.ShowRiders)
                    PrepareRidersAhead(budget);
                return;
            }

            long spent = 0;
            var keys = new List<KitModelKey>(s_Ahead.Keys);
            if (selected && keys.Remove(selectedKey)) keys.Insert(0, selectedKey);
            foreach (KitModelKey key in keys)
            {
                if (spent >= budget)
                    break;
                if (!s_Warming.TryGetValue(key, out CabModel warming))
                {
                    if (!s_Builds.TryGetValue(key, out Task<KitModelData> build) || !build.IsCompleted)
                        continue;
                    if (build.Status != TaskStatus.RanToCompletion || build.Result == null || build.Result.Error != null)
                    {
                        s_Ahead.Remove(key);           // the first real drive reports it (TryCreate)
                        continue;
                    }

                    s_Builds.Remove(key);
                    s_Warming[key] = warming = new CabModel(build.Result, true);
                }

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool done = warming.Step(Math.Max(1, budget - spent));
                long used = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                spent += used;
                var info = s_Ahead[key];
                s_Ahead[key] = (info.name, info.kind, info.uploadTicks + used, info.frames + 1);
                if (done)
                {
                    info = s_Ahead[key];
                    s_Ahead.Remove(key);
                    AdoptModel(key, warming);
                    Mod.log.Info($"Bus model of {info.name} prepared ahead: built on a background thread in {warming.Data.BuildMs:0} ms, " +
                        $"uploaded in {info.uploadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms over {info.frames} frame(s).");
                }
            }
        }

        // The selected body wrap, made while the player is not driving: its zip is read and its pictures decoded and prepared on a worker
        // thread (BusSkins), then uploaded one texture a frame by this call. Nothing is drawn from it before the first drive;
        // BusSkins.Update by the cab finds it loaded (the same name, the same file times).
        private void PrepareWrapAhead(float now)
        {
            BusSettings settings = Mod.Settings;
            string name = settings != null && settings.ShowExterior ? settings.BodyWrap : "";
            // (a wrap belongs to one model: before a bus has been driven there is no model to read it for)
            if (string.IsNullOrEmpty(name) || name == m_WrapAhead || now < kPrepareAfterSeconds || s_Ahead.Count != 0 || !BusSkins.HasVehicle)
                return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            m_Skins.Update(name);
            m_WrapAheadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (m_Skins.Loading)
                return;
            m_WrapAhead = name;
            Mod.log.Info($"Body wrap \"{name}\" prepared ahead: {m_WrapAheadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms on the main thread.");
            m_WrapAheadTicks = 0;
        }

        private long m_WrapAheadTicks;
        // The glass of the registered authored buses, read and cut ahead off the main thread (BusGlass.StartCut), kept for the session by
        // render prefab and model key (the cut follows the model's door leaves): the profile names started, and when to look again for
        // the ones whose prefab is not in the world yet (it is once a city is loaded). One cut runs at a time.
        private static readonly Dictionary<(RenderPrefab mesh, KitModelKey key), NativeGeometryRead> s_GlassCuts =
            new Dictionary<(RenderPrefab, KitModelKey), NativeGeometryRead>();
        private readonly HashSet<string> m_GlassWarmed = new HashSet<string>(), m_GlassNotFound = new HashSet<string>();
        private readonly HashSet<string> m_BodySet = new HashSet<string>();   // profiles whose body the driving mod has (Core.SetVehicleBody)
        private (RenderPrefab mesh, KitModelKey key) m_GlassAheadKey;
        private string m_GlassAheadName;
        private float m_GlassWarmAt;
        private float m_TemplateAt;
        private const float kTemplateDelaySeconds = 4f;

        private void WarmGlassAhead(float now, string first)
        {
            if (m_GlassAheadName != null && !PollGlassAhead())
                return;
            if (now < m_GlassWarmAt || now < kPrepareAfterSeconds)
                return;
            PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null)
                return;
            m_GlassWarmAt = now + 3f;
            Dictionary<string, PrefabBase> loaded = null;
            var profiles = new List<VehicleProfile>(VehicleModels.Snapshot());
            int at = first != null ? profiles.FindIndex(p => p.Name == first) : -1;
            if (at > 0) { VehicleProfile selected = profiles[at]; profiles.RemoveAt(at); profiles.Insert(0, selected); }
            foreach (VehicleProfile profile in profiles)
            {
                if (m_GlassWarmed.Contains(profile.Name))
                    continue;
                // By its name among the loaded car prefabs: an asset prefab's ID carries the asset's hash, so the ID of a type and a
                // name alone (as this asked before) never found one, and the glass of a pack bus was never made ahead nor its body given
                // to the driving mod.
                loaded = loaded ?? CarPrefabsByName(prefabs);
                if (!loaded.TryGetValue(profile.Name, out PrefabBase prefab))
                {
                    // not in the world yet (no city loaded): looked for again; said once, so a bus that is never found shows in the log
                    if (m_GlassNotFound.Add(profile.Name))
                        Mod.log.Info($"Bus glass of {profile.Name}: its prefab is not in the world yet; looking again every 3 s.");
                    continue;
                }
                // the body the driving mod collides with: the profile's ends and half width, no mirrors (Core API 11; once per profile)
                if (m_BodySet.Add(profile.Name) && BusBody.Box(profile.Spec, out float3 bodyMin, out float3 bodyMax))
                    Core.SetVehicleBody(prefabs.GetEntity(prefab), bodyMin, bodyMax);
                m_GlassWarmed.Add(profile.Name);
                try
                {
                    BusInteriorSpec spec = profile.Spec.Snapped();
                    var key = (BusGlass.MeshOf(prefab), new BusInterior(spec).Key);
                    if (key.Item1 == null || s_GlassCuts.ContainsKey(key))
                        continue;
                    NativeGeometryRead cut = BusGlass.StartCut(key.Item1, DoorLeafOf(spec));
                    if (cut == null)
                    {
                        Mod.log.Info($"Bus glass of {profile.Name}: no glass surface.");
                        continue;
                    }
                    s_GlassCuts[key] = cut;
                    m_GlassAheadKey = key;
                    m_GlassAheadName = profile.Name;
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Bus glass of {profile.Name} could not be prepared ahead (the first drive reads it): {ex.GetType().Name}: {ex.Message}");
                }
                return;
            }
        }

        /// <summary>Advances the glass cut running ahead; true once it is done (or failed and was dropped).</summary>
        private bool PollGlassAhead()
        {
            if (!s_GlassCuts.TryGetValue(m_GlassAheadKey, out NativeGeometryRead cut))
            {
                m_GlassAheadName = null;
                return true;
            }
            try
            {
                if (!cut.Poll()) return false;
                Mod.log.Info($"Bus glass of {m_GlassAheadName} prepared ahead: {cut.Pieces.Count} pane(s) read and cut off the main thread in {cut.WorkerMs} ms.");
            }
            catch (Exception ex)
            {
                DropGlassCut(m_GlassAheadKey);
                Mod.log.Warn($"Bus glass of {m_GlassAheadName} could not be prepared ahead (the first drive reads it): {ex.GetType().Name}: {ex.Message}");
            }
            m_GlassAheadName = null;
            return true;
        }

        private static void DropGlassCut((RenderPrefab mesh, KitModelKey key) key)
        {
            if (!s_GlassCuts.TryGetValue(key, out NativeGeometryRead cut)) return;
            s_GlassCuts.Remove(key);
            cut.Dispose();
        }

        // The loaded car and trailer prefabs by name (asked every 3 s while a pack bus's glass is still to be made, at most 40 times).
        private Dictionary<string, PrefabBase> CarPrefabsByName(PrefabSystem prefabs)
        {
            var byName = new Dictionary<string, PrefabBase>(StringComparer.Ordinal);
            EntityQuery query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CarData>());
            using (Unity.Collections.NativeArray<Entity> entities = query.ToEntityArray(Unity.Collections.Allocator.Temp))
                foreach (Entity e in entities)
                    if (prefabs.TryGetPrefab(e, out PrefabBase p) && p is CarBasePrefab && p.name != null && !byName.ContainsKey(p.name))
                        byName[p.name] = p;
            return byName;
        }

        private static void AdoptModel(KitModelKey key, CabModel model)
        {
            s_Models[key] = model;
            s_Warming.Remove(key);
            if (HeadlampFrames.Find(model.Data, BusInterior.HeadLamps, out float3 firstLamp, out float3 secondLamp))
                s_HeadLamps[key] = (firstLamp, secondLamp);
        }

        private static void StartBuild(Section section)
        {
            if (s_Models.ContainsKey(section.Key) || s_Builds.ContainsKey(section.Key) || s_Warming.ContainsKey(section.Key) || s_Failed.Contains(section.Key))
                return;

            BusInteriorSpec spec = section.Spec;
            s_Builds[section.Key] = CabPrebuild.Observe(Task.Run(() => KitBuild.BuildAll(new BusInterior(spec), new MeshKit())));
        }

        /// <summary>
        /// The bus the player has selected (the info panel; the bus about to be driven or ridden) as an authored profile's model key and
        /// mesh: its model build is moved to the front of the background queue and its glass and upload go first.
        /// </summary>
        private bool SelectedAhead(out KitModelKey key, out string name)
        {
            key = default;
            name = null;
            Entity selected = (m_Tools ??= World.GetExistingSystemManaged<Game.Tools.ToolSystem>())?.selected ?? Entity.Null;
            if (selected != m_SelectedSeen)
            {
                m_SelectedSeen = selected;
                m_SelectedProfile = null;
                PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
                if (selected != Entity.Null && prefabs != null && EntityManager.TryGetComponent(selected, out PrefabRef prefabRef) &&
                    prefabs.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab))
                    m_SelectedProfile = VehiclePacks.ForPrefab(prefab);
                if (m_SelectedProfile != null)
                {
                    m_SelectedKey = new BusInterior(m_SelectedProfile.Spec.Snapped()).Key;
                    if (CabPrebuild.Promote(m_SelectedKey))
                        Mod.log.Info($"Bus model of {m_SelectedProfile.Name}: selected, its background build moved to the front.");
                    m_GlassWarmAt = 0f;
                }
            }
            if (m_SelectedProfile == null) return false;
            key = m_SelectedKey;
            name = m_SelectedProfile.Name;
            return true;
        }

        private Game.Tools.ToolSystem m_Tools;
        private Entity m_SelectedSeen = Entity.Null;
        private VehicleProfile m_SelectedProfile;
        private KitModelKey m_SelectedKey;

        /// <summary>
        /// The section's instance once its model is uploaded: a model still uploading (prepared ahead or built for this drive) takes the
        /// frame's construction budget, and the game's body stays until it is complete. Null until then.
        /// </summary>
        private CabInstance TryCreate(Section section)
        {
            if (!s_Models.TryGetValue(section.Key, out CabModel model))
            {
                if (!s_Warming.TryGetValue(section.Key, out CabModel warming))
                {
                    if (!s_Builds.TryGetValue(section.Key, out Task<KitModelData> build) || !build.IsCompleted)
                        return null;

                    s_Builds.Remove(section.Key);
                    KitModelData data = build.Status == TaskStatus.RanToCompletion ? build.Result : null;
                    if (data == null || data.Error != null)
                    {
                        s_Failed.Add(section.Key);
                        s_Ahead.Remove(section.Key);
                        Mod.log.Warn($"Bus model could not be built: {(data != null ? data.Error : build.Exception?.GetBaseException().Message)}");
                        return null;
                    }
                    s_Warming[section.Key] = warming = new CabModel(data, true);
                }

                long budget = BuildBudget();
                if (budget <= 0 || !warming.Step(budget))
                    return null;
                bool ahead = s_Ahead.Remove(section.Key);
                AdoptModel(section.Key, warming);
                model = warming;
                Mod.log.Info($"Bus model {(ahead ? "prepared ahead, its upload finished for the drive" : "built")}: {model.Triangles} triangles, " +
                    $"{model.Data.BuildMs:0} ms on a background thread ({section.Spec.Section}).");
            }

            return new CabInstance(model, "Bus Driver bus " + section.Spec.Section);
        }

        // ---------------------------------------------------------------- doors, dials, lights

        /// <summary>
        /// The doors follow the shift: opening when the doors are opened, closing when they close, each door of each section at its
        /// profile's rates and close delay (the rear door may close first). Without a model yet, one set on the default timing.
        /// </summary>
        private void UpdateDoors(float dt)
        {
            bool open = BusState.InShift && BusState.Bus != Entity.Null && (BusState.State == ShiftState.DoorsOpening || BusState.State == ShiftState.DoorsOpen) ||
                BusRide.Riding && BusRide.DoorsOpen;
            if (m_Sections.Count == 0)
            {
                m_NoSectionDoors.Step(open, dt, null);
                m_Doors = m_NoSectionDoors.Max;
                m_DoorsMoving = m_NoSectionDoors.Moving;
                return;
            }
            m_Doors = 0f;
            m_DoorsMoving = false;
            foreach (Section section in m_Sections)
            {
                // Platform-side selection belongs to the future BRT stop contract.
                section.Doors.Step(open, dt, VehicleModels.Find(section.Spec.ModelVariant));
                m_Doors = Mathf.Max(m_Doors, section.Doors.Max);
                m_DoorsMoving |= section.Doors.Moving;
            }
        }

        private float EngineRpm()
        {
            float rpm = m_Core.Rpm;
            if (rpm > 1f)
                return rpm;

            // Engine sound off: a plain diesel curve for the needle.
            float kmh = Mathf.Abs(m_Core.Speed) * 3.6f;
            return 650f + Mathf.Repeat(kmh * 45f, 1400f);
        }

        private bool Dark() => Daylight() < 0.45f;

        /// <summary>The game's daylight, 0 (night) .. 1 (midday sun); night when it cannot be read.</summary>
        private float Daylight()
        {
            if (m_Lighting == null)
                m_Lighting = World.GetExistingSystemManaged<LightingSystem>();
            return m_Lighting != null ? m_Lighting.dayLightBrightness : 0f;
        }

        private KitLights Lights()
        {
            bool inShift = BusState.Bus == m_Bus && BusState.InShift;
            bool moving = m_DoorsMoving;
            return new KitLights
            {
                Lamps = Dark() || inShift && BusState.DoorsOpen || BusRide.Bus == m_Bus && BusRide.DoorsOpen,
                Tail = inShift && StopRequested(),
                Beacon = (byte)(inShift && moving ? 1 : 0)
            };
        }

        /// <summary>
        /// The door leaf a point of the glass belongs to (model frame): in a door's opening on the kerb side, leaf A from the front
        /// post to the middle (the whole opening for a single leaf), leaf B from the middle to the rear post; -1 elsewhere.
        /// Everything is looked up here: the function is pure (the glass cut calls it on a worker thread).
        /// </summary>
        internal static Func<Vector3, int> DoorLeafOf(BusInteriorSpec spec)
        {
            var bus = new BusInterior(spec);
            VehicleProfile authored = bus.AuthoredModel;
            float kerb = -bus.Spec.DriverSide, half = bus.Spec.HalfWidth, floor = bus.Spec.FloorY, top = bus.Spec.RoofY - 0.3f;
            var doors = new List<(float front, float middle, float rear, float side, int partA, int partB)>();
            for (int d = 0; d < bus.DoorCount; d++)
            {
                float2 door = bus.Door(d);
                bool two = authored == null || authored.HasDoorLeaf(d, true);
                int partA = authored?.DoorPart(d, 0) ?? BusInterior.DoorFrontA + 2 * d;
                int partB = two ? authored?.DoorPart(d, 1) ?? BusInterior.DoorFrontA + 2 * d + 1 : partA;
                doors.Add((door.x + 0.5f * door.y, door.x, door.x - 0.5f * door.y, authored?.DoorSide(d) ?? kerb, partA, partB));
            }
            return p =>
            {
                if (p.y < floor || p.y > top)
                    return -1;
                foreach ((float front, float middle, float rear, float side, int partA, int partB) in doors)
                {
                    if (p.x * side < half - 0.3f) continue;
                    if (p.z > front + 0.03f || p.z < rear - 0.03f)
                        continue;
                    return p.z < middle ? partB : partA;
                }
                return -1;
            };
        }

        /// <summary>The section's pack asks for its own game asset in the driver's view (cabRender native) and the asset is ours.</summary>
        private bool WantsNativeCab(Section section) => m_OwnAsset && VehicleModels.Find(section.Spec.ModelVariant)?.NativeCab == true;

        /// <summary>The game's body is hidden only once the native cab is there: without it the openings would show the shell.</summary>
        private bool NativeReady(Section section) => !WantsNativeCab(section) || section.Native?.Ready == true;

        // A section whose glass or native cab cannot be made is tried less and less often (1 s .. 30 s); only the first tries are logged.
        private static float RetryDelay(int attempt) => Mathf.Min(30f, 1f * (1 << Math.Min(attempt, 5)));

        private PrefabBase SectionPrefab(Section section)
        {
            PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            return prefabs != null && EntityManager.TryGetComponent(section.Entity, out PrefabRef prefabRef) &&
                prefabs.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefab) ? prefab : null;
        }

        /// <summary>The glass and the native cab of a section, with their tries: made again for a new instance.</summary>
        private void ReleaseConstruction(Section section)
        {
            section.GlassAttempts = section.NativeAttempts = 0;
            section.NextGlassAttempt = section.NextNativeAttempt = 0f;
            var failures = new List<Exception>();
            if (section.Glass != null && CabCleanup.Try(section.Glass.Dispose, failures)) section.Glass = null;
            if (section.Native != null && CabCleanup.Try(section.Native.Dispose, failures)) section.Native = null;
            CabCleanup.ThrowIfAny(failures, "Bus section construction cleanup failed.");
        }

        /// <summary>Starts the section's imported panes (read and cut off the main thread, or the cut made ahead); StepGlass makes them.</summary>
        private void StartGlass(Section section)
        {
            int attempt = section.GlassAttempts++;
            section.NextGlassAttempt = UnityEngine.Time.unscaledTime + RetryDelay(attempt);
            PrefabBase prefab = SectionPrefab(section);
            if (prefab == null || section.Instance.Root == null)
                return;
            var glass = new BusGlass();
            try
            {
                CabInstance instance = section.Instance;
                s_GlassCuts.TryGetValue((BusGlass.MeshOf(prefab), section.Key), out NativeGeometryRead ahead);
                if (glass.Begin(prefab, instance.Root, instance.Root.gameObject.layer, DoorLeafOf(section.Spec), instance.Part,
                        part => instance.Def(part).ModelToPart, cut: ahead))
                {
                    section.Glass = glass;
                    glass = null;
                }
                else if (attempt < 3)
                    Mod.log.Info($"Bus glass for {section.Entity}: {glass.Describe}{(attempt == 2 ? " (tried again less often from now on)" : "")}.");
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Bus glass for {section.Entity} skipped: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                glass?.Dispose();
            }
        }

        private void StepGlass(Section section)
        {
            long budget = BuildBudget();
            if (budget <= 0) return;
            BusGlass glass = section.Glass;
            try
            {
                if (!glass.Step(budget)) return;
                if (glass.Ready || section.GlassAttempts <= 3)
                    Mod.log.Info($"Bus glass for {section.Entity}: {glass.Describe}{(!glass.Ready && section.GlassAttempts == 3 ? " (tried again less often from now on)" : "")}.");
                if (glass.Ready) return;
            }
            catch (Exception ex)
            {
                // a cut made ahead that failed here is dropped: the next try reads the file again
                PrefabBase prefab = SectionPrefab(section);
                if (prefab != null) DropGlassCut((BusGlass.MeshOf(prefab), section.Key));
                Mod.log.Warn($"Bus glass for {section.Entity} skipped (tried again in {section.NextGlassAttempt - UnityEngine.Time.unscaledTime:0} s): {ex.GetType().Name}: {ex.Message}");
            }
            section.Glass = null;
            glass.Dispose();
        }

        /// <summary>
        /// Starts the pack's own game asset in place of the runtime mesh's static parts (BusNativeCab): read and cut off the main thread,
        /// then made a piece at a time by StepNativeCab; the game's body stays until it is complete.
        /// </summary>
        private void StartNativeCab(Section section)
        {
            int attempt = section.NativeAttempts++;
            section.NextNativeAttempt = UnityEngine.Time.unscaledTime + RetryDelay(attempt);
            PrefabBase prefab = SectionPrefab(section);
            if (prefab == null || section.Instance.Root == null)
                return;
            var native = new BusNativeCab();
            try
            {
                CabInstance instance = section.Instance;
                if (native.Begin(prefab, VehicleModels.Find(section.Spec.ModelVariant), instance.Root, instance.Root.gameObject.layer))
                {
                    section.Native = native;
                    native = null;
                }
                else if (attempt < 3)
                    Mod.log.Info($"Native cab for {section.Entity}: no native geometry{(attempt == 2 ? " (tried again less often from now on)" : "")}.");
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Native cab for {section.Entity} skipped: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                native?.Dispose();
            }
        }

        private void StepNativeCab(Section section)
        {
            long budget = BuildBudget();
            if (budget <= 0) return;
            BusNativeCab native = section.Native;
            try
            {
                if (!native.Step(budget)) return;
                if (native.Ready || section.NativeAttempts <= 3)
                    Mod.log.Info($"Native cab for {section.Entity}: {native.Describe}{(!native.Ready && section.NativeAttempts == 3 ? " (tried again less often from now on)" : "")}.");
                if (native.Ready) return;
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Native cab for {section.Entity} skipped (tried again in {section.NextNativeAttempt - UnityEngine.Time.unscaledTime:0} s): {ex.GetType().Name}: {ex.Message}");
            }
            section.Native = null;
            native.Dispose();
        }

        // ---------------------------------------------------------------- lifetime

        private void ReleaseRenderers()
        {
            var failures = new List<Exception>();
            CabCleanup.Try(ReleaseAuthoredJoint, failures);
            CabCleanup.Try(ReleaseOutside, failures);
            CabCleanup.Try(ReleaseRiders, failures);
            foreach (Section section in m_Sections.ToArray())
            {
                bool released = true;
                if (section.Instance != null) released &= CabCleanup.Try(() => section.Instance.Visible = false, failures);
                if (section.Glass != null)
                    released &= CabCleanup.Try(() => { section.Glass.Dispose(); section.Glass = null; }, failures);
                if (section.Native != null)
                    released &= CabCleanup.Try(() => { section.Native.Dispose(); section.Native = null; }, failures);
                if (section.Glass == null && section.Native == null && section.Instance != null)
                    released &= CabCleanup.Try(() => { section.Instance.Destroy(); section.Instance = null; }, failures);
                if (released) m_Sections.Remove(section);
            }
            CabCleanup.Try(ReleaseLedScreen, failures);
            CabCleanup.ThrowIfAny(failures, "Bus renderer cleanup failed.");
        }

        private void ReleaseBus()
        {
            var failures = new List<Exception>();
            HideWatchEnd();
            CabCleanup.Try(ReleaseRenderers, failures);
            m_OtherCameras.Clear();
            m_Assembly = null;
            m_Articulated = null;
            ResetAssemblyState();
            m_AssemblyPending = false;
            m_AssemblyBindFailure = null;
            m_Bus = Entity.Null;
            m_BusPrefab = Entity.Null;
            m_ReadySince = -1f;
            m_OwnAsset = false;
            CabCleanup.Try(RestoreCore, failures);
            CabCleanup.ThrowIfAny(failures, "Driven bus cleanup failed.");
        }

        private void RestoreCore()
        {
            var failures = new List<Exception>();
            if (m_EyeSet || m_BodyHidden || m_WholeHidden)
            {
                if (!Core.Available) throw new InvalidOperationException("Core is unavailable while Bus presentation settings still need releasing.");
                if (m_EyeSet) CabCleanup.Try(() => { Core.ClearCabEye(m_CoreOwner); m_EyeSet = false; }, failures);
                if (m_BodyHidden) CabCleanup.Try(() => { Core.SetCabBodyHidden(false, m_CoreOwner); m_BodyHidden = false; }, failures);
                if (m_WholeHidden && Core.HasApi4)
                    CabCleanup.Try(() => { Core.SetBodyHidden(false, m_CoreOwner); m_WholeHidden = false; }, failures);
            }
            CabCleanup.ThrowIfAny(failures, "Bus Core presentation cleanup failed.");
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            var failures = new List<Exception>();
            CabCleanup.Try(ReleaseBus, failures);
            CabCleanup.Try(ReleaseOverlay, failures);
            m_BusGate.Clear();
            m_GaveUpLogged.Clear();
            if (m_Riders.Count == 0) CabCleanup.Try(DestroyCitizens, failures);
            CabCleanup.ThrowIfAny(failures, "Bus city-transition cleanup failed.");
        }

        protected override void OnDestroy()
        {
            var failures = new List<Exception>();
            CabCleanup.Try(CabPrebuild.Shutdown, failures);
            CabCleanup.Try(ReleaseBus, failures);
            CabCleanup.Try(ReleaseOverlay, failures);
            var models = new HashSet<CabModel>(s_Models.Values);
            models.UnionWith(s_Warming.Values);
            if (s_RidersModel != null) models.Add(s_RidersModel);
            if (s_RidersWarming != null) models.Add(s_RidersWarming);
            s_Models.Clear();
            s_Warming.Clear();
            s_RidersModel = s_RidersWarming = null;
            s_Ahead.Clear();
            foreach (Task<KitModelData> build in s_Builds.Values) CabPrebuild.Observe(build);
            s_Builds.Clear();
            s_Failed.Clear();
            s_HeadLamps.Clear();
            foreach (NativeGeometryRead cut in s_GlassCuts.Values) CabCleanup.Try(cut.Dispose, failures);
            s_GlassCuts.Clear();
            if (s_RidersBuild != null) CabPrebuild.Observe(s_RidersBuild);
            s_RidersBuild = null;
            s_RidersFailed = false;
            var retired = new RetiredPresentation(this, models);
            if (!CabCleanup.Try(retired.Dispose, failures)) CabCleanup.Retain(retired);
            CabCleanup.Try(CabMaterials.DestroyAll, failures);
            try { CabCleanup.ThrowIfAny(failures, "Bus presentation cleanup failed."); }
            finally { base.OnDestroy(); }
        }

        private sealed class RetiredPresentation : IDisposable
        {
            private BusCabSystem m_Owner;
            private readonly HashSet<CabModel> m_Models;

            internal RetiredPresentation(BusCabSystem owner, HashSet<CabModel> models)
            { m_Owner = owner; m_Models = models; }

            public void Dispose()
            {
                if (m_Owner == null) return;
                var failures = new List<Exception>();
                CabCleanup.Try(m_Owner.m_DoorSounds.Destroy, failures);
                CabCleanup.Try(m_Owner.m_CabSounds.Destroy, failures);
                CabCleanup.Try(m_Owner.ReleaseRenderers, failures);
                if (failures.Count == 0)
                {
                    foreach (CabModel model in new List<CabModel>(m_Models))
                        CabCleanup.Try(() => { model.Destroy(); m_Models.Remove(model); }, failures);
                    CabCleanup.Try(m_Owner.DestroyCitizens, failures);
                    CabCleanup.Try(m_Owner.m_Skins.Destroy, failures);
                }
                if (failures.Count == 0)
                    CabCleanup.Try(() => { m_Owner.m_MaterialResources?.Dispose(); m_Owner.m_MaterialResources = null; }, failures);
                CabCleanup.Try(m_Owner.RestoreCore, failures);
                CabCleanup.ThrowIfAny(failures, "Retired bus presentation cleanup failed.");
                m_Owner = null;
            }
        }

        // ---------------------------------------------------------------- LED next-stop screen

        /// <summary>
        /// The LED font (the next-stop screen, the outside signs, the printed boards' fallback glyphs, the cluster's gear display):
        /// loaded once per session on a background thread, by whichever of them asks first. True once it is there. It was loaded by
        /// the next-stop screen alone, so on a model without one (nextStop.enabled false: the WG6980A) none of the others were ever
        /// made: the driven bus had no destination boards and no gear display, while the AI buses (their own font) had their boards.
        /// </summary>
        private static bool LedFontReady()
        {
            if (s_LedFont != null)
                return true;
            if (s_LedFontFailed)
                return false;
            if (s_LedFontLoad == null)
            {
                string directory = Mod.ModDirectory;
                if (string.IsNullOrEmpty(directory))
                {
                    s_LedFontFailed = true;
                    return false;
                }

                string path = Path.Combine(directory, "Fonts", "led-font.bin.gz");
                s_LedFontLoad = Task.Run(() =>
                {
                    var font = new LedFont();
                    return font.Load(path) ? font : null;
                });
            }

            if (!s_LedFontLoad.IsCompleted)
                return false;

            s_LedFont = s_LedFontLoad.Status == TaskStatus.RanToCompletion ? s_LedFontLoad.Result : null;
            if (s_LedFont == null)
            {
                s_LedFontFailed = true;
                Mod.log.Warn("LED screen font could not be loaded (Fonts/led-font.bin.gz): the screen and the outside signs stay dark.");
                return false;
            }

            Mod.log.Info($"LED screen font loaded: {s_LedFont.Count} glyphs.");
            return true;
        }

        /// <summary>
        /// What the passengers dot-matrix screen on the destination box is told, and by whom: the shift of the player on this bus (BusState), or the ride of the player
        /// as a passenger of it (BusRide, a bus the game drives): the line label and, if the bus heads for a stop, its name, its place on the line, whether the bus is
        /// close to it or standing at it with the doors open, and the line terminus. Active is false for neither: the screen is dark.
        /// </summary>
        private struct LedFeed
        {
            internal bool Active, Valid, Skipped, AtStop, Approaching;
            internal string Prefix, Block, Stop, Terminus;
            internal int Ordinal;
        }

        private LedFeed ReadLedFeed()
        {
            if (BusState.Bus == m_Bus && BusState.InShift)
            {
                StopInfo target = BusState.Target;
                return new LedFeed
                {
                    Active = true, Valid = target.Valid, Skipped = BusState.TargetSkip != SkipReason.None,
                    AtStop = BusState.State == ShiftState.DoorsOpening || BusState.State == ShiftState.DoorsOpen,
                    Approaching = BusState.DistanceToTarget < kLedArrivingMeters,
                    Prefix = BusState.LinePrefix ?? "", Block = BusState.LineNumber ?? "", Stop = target.Name, Terminus = BusState.Terminus, Ordinal = target.Ordinal,
                };
            }

            // The game does not tell a ridden bus which stop it passes without stopping, so a ride never shows one as skipped. At the stop it boards at, the bus target
            // is that stop until the boarding is over (TransportCarAISystem), so the screen names the right one while the doors are open.
            if (m_Core.Riding && BusRide.Riding && BusRide.Bus == m_Bus)
                return new LedFeed
                {
                    Active = true, Valid = BusRide.HasRoute, AtStop = BusRide.DoorsOpen, Approaching = BusRide.Approaching,
                    Prefix = BusRide.LinePrefix ?? "", Block = BusRide.LineNumber ?? "", Stop = BusRide.Next, Terminus = BusRide.Terminus, Ordinal = BusRide.Target,
                };

            return default;
        }

        /// <summary>
        /// The passengers dot-matrix screen on the destination box, for the player shift on this bus and for the player riding it: the route number on the left, then
        /// "Next stop X · to Y", "X arriving" (within 150 m; "not stopping at X" for a skipped stop of a shift) or "Now at X" (doors open), scrolling when it does not
        /// fit (LedScreenRules makes the text). The font loads once per session on a background thread; the text is made again only when the state changes (and every
        /// 2 s for a language switch); the dots (a mesh under the cab, LedDots) are written only when one changes: once for a static text, 24 times a second while
        /// scrolling. With neither a shift nor a ride on this bus the screen is dark.
        /// </summary>
        private void UpdateLedScreen()
        {
            if (m_Sections.Count > 0 && VehicleModels.Find(m_Sections[0].Spec.ModelVariant)?.NextStopEnabled == false)
            {
                ReleaseLedScreen();
                return;
            }
            if (!LedFontReady())
                return;

            LedFeed feed = ReadLedFeed();
            if (m_LedScreen == null || !m_LedScreen.Alive)
            {
                // The dots live under the cab instance's root (destroyed and made again with it).
                m_LedScreen?.Destroy();
                m_LedScreen = null;
                if (!feed.Active)
                    return;

                Section front = m_Sections.Count > 0 ? m_Sections[0] : null;
                if (front == null || front.Spec.Section == BusSection.Rear || front.Instance == null || !front.Instance.Alive || front.Instance.Root == null)
                    return;

                Material lit = CabMaterials.Glow();
                if (lit == null)
                    return;

                BusInterior.LedScreenFrame(front.Spec, out float3 centre, out float2 size);
                int2 matrix = BusInterior.LedScreenMatrix(front.Spec);
                m_LedScreen = new LedSign(front.Instance.Root, centre, size, matrix.x, matrix.y, lit, s_LedFont, default, kLedDotsPerSecond);
                m_LedKey = int.MinValue;
            }

            if (!feed.Active)
            {
                if (m_LedKey != int.MinValue)
                {
                    m_LedScreen.Show("", "", "", 0);
                    m_LedKey = int.MinValue;
                    m_LedPrefix = m_LedBlock = m_LedMessage = "";
                }

                return;
            }

            float now = UnityEngine.Time.unscaledTime;
            int phase = LedScreenRules.Phase(feed.AtStop, feed.Approaching);
            int key = LedScreenRules.Key(feed.Ordinal, phase, feed.Skipped, feed.Valid);
            if (key != m_LedKey || now >= m_LedNextText || now < m_LedNextText - kLedTextRefreshSeconds - 1f)
            {
                m_LedKey = key;
                m_LedNextText = now + kLedTextRefreshSeconds;
                m_LedPrefix = feed.Prefix;
                m_LedBlock = feed.Block;
                m_LedMessage = LedScreenRules.Message(feed.Valid, feed.Stop, feed.Terminus, phase, feed.Skipped, BusLocale.Text);
                m_LedScreen.PrefixMaterial = CabMaterials.Lamp(PrefixColour(feed.Prefix), kCabLedNits);
            }

            m_LedScreen.Show(m_LedPrefix, m_LedBlock, m_LedMessage, UnityEngine.Time.unscaledTimeAsDouble);
        }

        private void ReleaseLedScreen()
        {
            if (m_LedScreen == null)
                return;

            m_LedScreen.Destroy();
            m_LedScreen = null;
            m_LedPrefix = m_LedMessage = m_LedBlock = "";
            m_LedKey = int.MinValue;
        }

        private static Vector3 ToVector(float3 v) => new Vector3(v.x, v.y, v.z);
    }
}
