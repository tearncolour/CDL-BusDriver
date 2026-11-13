using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLaneFlags = Game.Vehicles.CarLaneFlags;
using PathElement = Game.Pathfind.PathElement;
using PathFlags = Game.Pathfind.PathFlags;
using PathInformation = Game.Pathfind.PathInformation;
using PathOwner = Game.Pathfind.PathOwner;
using ObjectTransform = Game.Objects.Transform;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Runs a bus shift on top of CSL Driving Life Core: takes a bus out of the depot (or over from service) for the chosen
    /// line, keeps it on the line while the player drives, finds which boarding zone it stands in, and opens and closes the
    /// doors through the game's own stop logic: with the lane marked "arrived" and the stop as target, the bus AI starts
    /// boarding, residents board and alight and pay the line's fare into the city budget, and the departure time is held
    /// while the doors are open. Runs before the bus AI each simulation step.
    /// </summary>
    public sealed partial class BusShiftSystem : GameSystemBase
    {
        private const float kDispatchTimeoutSeconds = 90f;
        private const float kPossessTimeoutSeconds = 4f;
        private const uint kOpenTimeoutFrames = 96U;
        private const uint kSoftCloseLimitFrames = 600U;
        private const uint kHoldDepartureFrames = 120U;
        private const uint kTerminusFrames = 256U;   // the terminus is read again this often (the timetable's can be changed)

        private SimulationSystem m_SimulationSystem;
        private NameSystem m_NameSystem;
        private readonly HashSet<Entity> m_KnownVehicles = new HashSet<Entity>();
        private readonly HashSet<Entity> m_OnBoard = new HashSet<Entity>();
        private readonly HashSet<Entity> m_NowAboard = new HashSet<Entity>();
        private readonly List<Entity> m_Gone = new List<Entity>();
        private readonly List<Entity> m_Cars = new List<Entity>();

        // While the doors are open: riders on the bus's list still walking to the doors, and the stop's waiting count less
        // the riders who joined since the game last counted it (BusStopZone.WaitingCount).
        private int m_Walking;
        private BusStopZone.WaitingCount m_StopWaiting;
        private float m_StateTime;
        private float m_LastDriveRequest;
        private Entity m_PendingBus = Entity.Null;
        private uint m_DoorFrame;
        private int m_GuideIndex = -1;
        private int m_LogCooldown;

        // The stop the doors were last opened at (never counted as passed), and the stop last driven past (undone when the
        // bus reverses into its box after all).
        private int m_ServedIndex = -1;
        private int m_LastPassed = -1;
        private bool m_LastPassedSkipped;

        // Where the terminus came from (logged when it changes) and when it was last read; the first stop driven past on the
        // way out of the depot (the depot leg is back on when the bus reverses into it after all).
        private string m_TerminusSource;
        private uint m_TerminusFrame;
        private int m_DepotStop = -1;

        // The bus kept on its line this step (KeepOnLine): its flags are put right by KeepOnLineJob at the end of the update.
        private ComponentLookup<Game.Vehicles.PublicTransport> m_Transports;
        private Entity m_KeepOnLineBus = Entity.Null;

        // The rendered frame of the last step that tracked the bus while driving (UpdateShift) and of the last depot check (UpdateDispatch):
        // that work runs in a frame's first simulation step only.
        private int m_TrackedFrame = -1;
        private int m_DispatchFrame = -1;

        // The update's time on the main thread while on a shift (waits for the game's jobs included, as the game's profilers count it),
        // per rendered frame, logged every 60 s.
        private long m_PerfTicks, m_PerfFrameTicks, m_PerfWorst;
        private int m_PerfFrames, m_PerfSteps, m_PerfDoorSteps, m_PerfFrame = -1;
        private float m_PerfNext;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_Transports = GetComponentLookup<Game.Vehicles.PublicTransport>(false);
        }

        protected override void OnDestroy()
        {
            EndShift("Msg.ShiftEnded");
            base.OnDestroy();
        }

        private uint Frame => m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0U;

        /// <summary>Another save or the main menu: the shift and everything learned about stops belong to the old city.</summary>
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            s_SeenStops.Clear();
            m_KnownVehicles.Clear();
            m_PendingBus = Entity.Null;
            m_GuideIndex = -1;
            ResetStopTracking();
            if (BusState.State != ShiftState.Idle)
            {
                Mod.log.Info("Bus shift ended: game loading.");
                BusState.Reset();
                if (Core.Available)
                    Core.SetKeyHints(new List<(string, string, string, string, bool)>());
            }
        }

        protected override void OnUpdate()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool onShift = BusState.State != ShiftState.Idle;
            try
            {
                if (!Core.Available)
                {
                    if (BusState.State != ShiftState.Idle)
                        EndShift("Msg.NeedDrivingMod");
                    BusState.Request = ShiftRequest.None;
                    return;
                }

                HandleRequest();
                ShiftOnDirectTake();
                switch (BusState.State)
                {
                    case ShiftState.Dispatching:
                        if (FirstStepOfFrame(ref m_DispatchFrame))
                            UpdateDispatch();
                        break;
                    case ShiftState.TakingOver:
                        UpdatePossession();
                        break;
                    case ShiftState.Driving:
                    case ShiftState.DoorsOpening:
                    case ShiftState.DoorsOpen:
                    case ShiftState.DoorsClosing:
                        UpdateShift();
                        break;
                }
            }
            catch (Exception ex)
            {
                if (m_LogCooldown-- <= 0)
                {
                    Mod.log.Warn($"Bus shift update failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    m_LogCooldown = 300;
                }
            }
            finally
            {
                // Last, after every main-thread read and write of the update: a job scheduled here is the system's dependency only once
                // the update has returned, so the main thread must not touch PublicTransport after it.
                ScheduleKeepOnLine();
                if (onShift || BusState.State != ShiftState.Idle)
                    CountTime(start);
            }
        }

        /// <summary>True in the first simulation step of a rendered frame (the game runs one or more steps a frame).</summary>
        private static bool FirstStepOfFrame(ref int lastFrame)
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == lastFrame)
                return false;
            lastFrame = frame;
            return true;
        }

        private void CountTime(long start)
        {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            int frame = UnityEngine.Time.frameCount;
            if (frame != m_PerfFrame)
            {
                if (m_PerfFrame >= 0)
                {
                    m_PerfWorst = Math.Max(m_PerfWorst, m_PerfFrameTicks);
                    m_PerfFrames++;
                }
                m_PerfFrame = frame;
                m_PerfFrameTicks = 0;
            }

            m_PerfFrameTicks += ticks;
            m_PerfTicks += ticks;
            m_PerfSteps++;
            if (BusState.DoorsOpen)
                m_PerfDoorSteps++;
            float now = UnityEngine.Time.unscaledTime;
            if (now < m_PerfNext)
                return;
            if (m_PerfNext > 0f && m_PerfFrames > 0)
            {
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Mod.log.Info($"Bus shift: {m_PerfTicks * ms / m_PerfFrames:0.000} ms a frame on average, {m_PerfWorst * ms:0.0} ms at most, over {m_PerfFrames} frames " +
                    $"({m_PerfSteps / (float)m_PerfFrames:0.0} simulation steps a frame, {100f * m_PerfDoorSteps / Math.Max(1, m_PerfSteps):0}% of them with the doors open).");
            }
            m_PerfNext = now + 60f;
            m_PerfTicks = m_PerfWorst = 0;
            m_PerfFrames = m_PerfSteps = m_PerfDoorSteps = 0;
        }

        // ------------------------------------------------------------------ requests

        private void HandleRequest()
        {
            ShiftRequest request = BusState.Request;
            BusState.Request = ShiftRequest.None;
            switch (request)
            {
                case ShiftRequest.Dispatch:
                case ShiftRequest.TakeOver:
                    StartShift(BusState.RequestedLine, request == ShiftRequest.Dispatch);
                    break;
                case ShiftRequest.End:
                    if (BusState.State != ShiftState.Idle)
                        EndShift("Msg.ShiftEnded");
                    break;
                case ShiftRequest.ToggleDoors:
                    ToggleDoors(false);
                    break;
                case ShiftRequest.ForceClose:
                    ToggleDoors(true);
                    break;
            }
        }

        private void StartShift(Entity line, bool dispatch)
        {
            if (BusState.State != ShiftState.Idle)
                EndShift(null);

            if (!PrepareShift(line))
                return;

            m_StateTime = 0f;
            if (dispatch)
            {
                m_KnownVehicles.Clear();
                if (EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteVehicle> vehicles))
                {
                    for (int i = 0; i < vehicles.Length; i++)
                        m_KnownVehicles.Add(vehicles[i].m_Vehicle);
                }

                // The same request a line makes when it lacks vehicles; a depot with a free bus answers it.
                Entity request = EntityManager.CreateEntity();
                EntityManager.AddComponentData(request, default(ServiceRequest));
                EntityManager.AddComponentData(request, new TransportVehicleRequest(line, 1f));
                EntityManager.AddComponentData(request, new RequestGroup(8U));
                BusState.DepotLeg = true;
                BusState.State = ShiftState.Dispatching;
                BusState.SetMessage("Msg.Dispatching");
                Mod.log.Info($"Bus shift: requested a bus from the depot for line {line} ({BusState.LineName}).");
                return;
            }

            Entity bus = FindBusToTakeOver(line);
            if (bus == Entity.Null)
            {
                BusState.SetMessage("Msg.NoBusOnLine");
                BusState.Reset();
                return;
            }

            BeginPossession(bus);
        }

        /// <summary>The line's data for a shift (its name, stops, timing); false with the message when there is no shift to run on it.</summary>
        private bool PrepareShift(Entity line)
        {
            if (!IsBusLine(line))
            {
                BusState.SetMessage("Msg.NoLine");
                return false;
            }

            BusState.Reset();
            BusState.Line = line;
            ReadLineInfo(line);
            BuildStops(line);
            HealLineTiming(line);
            if (BusState.Stops.Count < 2)
            {
                BusState.SetMessage("Msg.LineTooShort");
                BusState.Reset();
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ a bus taken directly

        private TakeWatch m_TakeWatch;

        /// <summary>
        /// The player took a vehicle with the driving mod's key or its info panel: a bus running on a passenger line starts the shift on
        /// that line with this bus (option ShiftOnDrive), the same as the panel's take-over from there on (UpdatePossession finds the bus
        /// already driven and starts at once). Any other vehicle is driven freely. Looked at once per vehicle taken.
        /// </summary>
        private void ShiftOnDirectTake()
        {
            bool driving = Core.IsDriving();
            Entity vehicle = driving ? Core.Vehicle() : Entity.Null;
            if (!m_TakeWatch.Taken(driving, BusShiftRules.Key(vehicle.Index, vehicle.Version)))
                return;

            bool option = Mod.Settings == null || Mod.Settings.ShiftOnDrive;
            Entity line = LineOfBus(vehicle);
            if (!BusShiftRules.StartOnTake(option, BusState.State == ShiftState.Idle, BusState.Request != ShiftRequest.None, true, line != Entity.Null))
                return;

            Mod.log.Info($"Bus shift: the player took bus {vehicle} of line {line} directly; starting the shift on its line.");
            if (!PrepareShift(line))
            {
                Mod.log.Info($"Bus shift: line {line} has no shift to run (message {BusState.Message}); the bus is driven freely.");
                return;
            }

            m_StateTime = 0f;
            BeginPossession(vehicle);
        }

        /// <summary>The passenger bus line this vehicle runs on (its route, which lists it among its vehicles), else Entity.Null.</summary>
        private Entity LineOfBus(Entity vehicle)
        {
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) || !EntityManager.TryGetComponent(vehicle, out CurrentRoute route) ||
                !IsBusLine(route.m_Route) || !EntityManager.TryGetBuffer(route.m_Route, true, out DynamicBuffer<RouteVehicle> vehicles))
                return Entity.Null;
            for (int i = 0; i < vehicles.Length; i++)
            {
                if (vehicles[i].m_Vehicle == vehicle)
                    return route.m_Route;
            }

            return Entity.Null;
        }

        // ------------------------------------------------------------------ getting a bus

        private void UpdateDispatch()
        {
            m_StateTime += UnityEngine.Time.deltaTime;
            if (!EntityManager.Exists(BusState.Line) || !EntityManager.TryGetBuffer(BusState.Line, true, out DynamicBuffer<RouteVehicle> vehicles))
            {
                EndShift("Msg.NoLine");
                return;
            }

            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = vehicles[i].m_Vehicle;
                if (m_KnownVehicles.Contains(vehicle))
                    continue;

                // A new bus of this line: wait until it has left the depot and is out on the road.
                if (IsOnRoad(vehicle))
                {
                    Mod.log.Info($"Bus shift: depot bus {vehicle} is out on the road.");
                    BeginPossession(vehicle);
                    return;
                }
            }

            if (m_StateTime > kDispatchTimeoutSeconds)
            {
                EndShift("Msg.DispatchTimeout");
            }
        }

        private void BeginPossession(Entity bus)
        {
            m_PendingBus = bus;
            m_StateTime = 0f;
            m_LastDriveRequest = -10f;
            BusState.State = ShiftState.TakingOver;
            BusState.SetMessage("Msg.TakingOver");
        }

        private void UpdatePossession()
        {
            m_StateTime += UnityEngine.Time.deltaTime;
            if (m_PendingBus == Entity.Null || !EntityManager.Exists(m_PendingBus) || EntityManager.HasComponent<Deleted>(m_PendingBus))
            {
                EndShift("Msg.BusLost");
                return;
            }

            if (Core.IsDriving() && Core.Vehicle() == m_PendingBus)
            {
                BusState.Bus = m_PendingBus;
                BusState.FrontOffset = 6f;
                if (EntityManager.TryGetComponent(BusState.Bus, out PrefabRef busPrefab) && EntityManager.TryGetComponent(busPrefab.m_Prefab, out ObjectGeometryData geometry) &&
                    geometry.m_Bounds.max.z > 1f && geometry.m_Bounds.max.z < 20f)
                {
                    BusState.FrontOffset = geometry.m_Bounds.max.z;
                }

                // The painted box is long enough for the whole bus and 10 m more (an articulated bus is 18 m and more).
                BusState.ZoneBehind = BusStopZone.Behind(RigLength(BusState.Bus));
                ResizeZones();

                m_PendingBus = Entity.Null;
                BusState.State = ShiftState.Driving;
                m_GuideIndex = -1;
                ResetStopTracking();
                if (Mod.Settings != null && Mod.Settings.CabViewOnShift && Core.HasCab)
                    Core.SetCabView(true);
                SetHints();
                BusState.SetMessage("Msg.ShiftStarted", BusState.LineName);
                Mod.log.Info($"Bus shift started on line {BusState.Line} ({BusState.LineName}) with bus {BusState.Bus}.");
                return;
            }

            if (m_StateTime - m_LastDriveRequest > 1f)
            {
                m_LastDriveRequest = m_StateTime;
                Core.Drive(m_PendingBus);
            }

            if (m_StateTime > kPossessTimeoutSeconds)
                EndShift("Msg.CannotDrive");
        }

        private Entity FindBusToTakeOver(Entity line)
        {
            if (!EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteVehicle> vehicles))
                return Entity.Null;

            UnityEngine.Camera camera = UnityEngine.Camera.main;
            float3 reference = camera != null ? (float3)camera.transform.position : float3.zero;
            Entity best = Entity.Null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = vehicles[i].m_Vehicle;
                if (!IsOnRoad(vehicle) || !EntityManager.TryGetComponent(vehicle, out ObjectTransform transform))
                    continue;

                float distance = math.distancesq(transform.m_Position, reference);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = vehicle;
                }
            }

            return best;
        }

        private bool IsOnRoad(Entity vehicle)
        {
            return vehicle != Entity.Null && EntityManager.Exists(vehicle) &&
                !EntityManager.HasComponent<Deleted>(vehicle) && !EntityManager.HasComponent<Unspawned>(vehicle) &&
                !EntityManager.HasComponent<TripSource>(vehicle) && !EntityManager.HasComponent<ParkedCar>(vehicle) &&
                EntityManager.HasComponent<Moving>(vehicle) && EntityManager.HasComponent<Car>(vehicle) &&
                EntityManager.TryGetComponent(vehicle, out CarCurrentLane lane) && lane.m_Lane != Entity.Null;
        }

        // ------------------------------------------------------------------ the shift

        private void UpdateShift()
        {
            Entity bus = BusState.Bus;
            if (!EntityManager.Exists(bus) || EntityManager.HasComponent<Deleted>(bus) || !EntityManager.Exists(BusState.Line))
            {
                EndShift("Msg.BusLost");
                return;
            }

            if (!Core.IsDriving() || Core.Vehicle() != bus)
            {
                // The player left the bus (driving mod key or panel): the shift ends and the bus carries on as usual.
                EndShift("Msg.ShiftEnded");
                return;
            }

            KeepOnLine(bus);
            // Driving, the doors shut: where the bus is, its target, the zone, the guide and the counts are for what is drawn and shown
            // once a frame (and the stop passed, a frame late at most), so they are found in a frame's first simulation step only. Each
            // main-thread read of the game's components waits for the jobs that write them; in a frame's second step those are the
            // jobs the first step has just scheduled (the shift's update was 0.89 ms a frame, its own work 0.07 ms a step: the user's
            // measurements, 22 October). With the doors open every step does all of it, as the game's boarding needs.
            bool exact = BusState.DoorsOpen;
            if (!FirstStepOfFrame(ref m_TrackedFrame) && !exact)
                return;

            if (Frame - m_TerminusFrame >= kTerminusFrames)
                RefreshTerminus(BusState.Line);
            Pose(bus, exact, out float3 position, out float3 forward);
            float speed = math.abs(Core.SpeedMps() * Core.TimeScale());
            BusState.Stopped = speed < BusState.kStoppedMps;
            LearnFromBoardingBuses();
            UpdateTarget(bus, position, forward);
            BusState.ZoneIndex = FindZone(position, forward);
            BusState.CanOpen = BusState.ZoneIndex >= 0 && BusState.Stopped && BusState.State == ShiftState.Driving;
            UpdatePassengers(bus);
            UpdateDemand(bus);
            if (BusState.State == ShiftState.Driving)
                CheckPassedStop(bus, position, forward);

            uint frame = Frame;
            switch (BusState.State)
            {
                case ShiftState.DoorsOpening:
                    HoldStop(bus, BusState.ZoneIndex >= 0 ? BusState.ZoneIndex : BusState.TargetIndex);
                    HoldDeparture(bus, frame);
                    if (IsBoarding(bus))
                    {
                        BusState.State = ShiftState.DoorsOpen;
                        BusState.SetMessage("Msg.DoorsOpen");
                    }
                    else if (frame - m_DoorFrame > kOpenTimeoutFrames)
                    {
                        FinishStop(bus, false);
                        BusState.SetMessage("Msg.NoBoardingHere");
                    }
                    break;
                case ShiftState.DoorsOpen:
                    HoldStop(bus, BusState.ZoneIndex >= 0 ? BusState.ZoneIndex : BusState.TargetIndex);
                    HoldDeparture(bus, frame);
                    TrackFares(bus);
                    if (!IsBoarding(bus))
                        FinishStop(bus, true);
                    break;
                case ShiftState.DoorsClosing:
                    HoldStop(bus, BusState.ZoneIndex >= 0 ? BusState.ZoneIndex : BusState.TargetIndex);
                    TrackFares(bus);
                    if (!IsBoarding(bus))
                    {
                        FinishStop(bus, true);
                    }
                    else if (frame - m_DoorFrame > kSoftCloseLimitFrames)
                    {
                        // Somebody is still walking up: close for good.
                        SetDeparture(bus, BusShiftRules.ForcedDeparture(frame));
                    }
                    break;
            }

            UpdateBoardingCount();
        }

        /// <summary>
        /// Keeps the bus on the line: the line may try to retire an extra bus or send it for maintenance. Every step, as before; the flags
        /// are put right by KeepOnLineJob, scheduled at the end of the update ahead of the bus AI's jobs (which read them), instead of
        /// reading PublicTransport on the main thread, which waited for the bus AI's jobs of the step before.
        /// </summary>
        private void KeepOnLine(Entity bus)
        {
            m_KeepOnLineBus = bus;
            if (!EntityManager.HasComponent<CurrentRoute>(bus))
                EntityManager.AddComponentData(bus, new CurrentRoute(BusState.Line));
        }

        private struct KeepOnLineJob : IJob
        {
            public Entity Bus;
            public ComponentLookup<Game.Vehicles.PublicTransport> Transports;

            public void Execute()
            {
                if (!Transports.TryGetComponent(Bus, out Game.Vehicles.PublicTransport transport))
                    return;

                const PublicTransportFlags kLeave = PublicTransportFlags.AbandonRoute | PublicTransportFlags.RequiresMaintenance | PublicTransportFlags.Returning;
                if ((transport.m_State & kLeave) != 0 || (transport.m_State & PublicTransportFlags.EnRoute) == 0)
                {
                    transport.m_State &= ~kLeave;
                    transport.m_State |= PublicTransportFlags.EnRoute;
                    Transports[Bus] = transport;
                }
            }
        }

        private void ScheduleKeepOnLine()
        {
            Entity bus = m_KeepOnLineBus;
            m_KeepOnLineBus = Entity.Null;
            if (bus == Entity.Null || BusState.Bus != bus || !BusState.InShift)
                return;
            try
            {
                m_Transports.Update(this);
                Dependency = new KeepOnLineJob { Bus = bus, Transports = m_Transports }.Schedule(Dependency);
            }
            catch (Exception ex)
            {
                if (m_LogCooldown-- <= 0)
                {
                    Mod.log.Warn($"Bus shift: keeping the bus on its line failed: {ex.GetType().Name}: {ex.Message}");
                    m_LogCooldown = 300;
                }
            }
        }

        /// <summary>
        /// Where the bus is and which way it faces. Driving: the pose the driving mod drew the body at this frame (no wait for the game's
        /// jobs; a step behind the simulation at most). With the doors open, or without that pose: the bus's own transform.
        /// </summary>
        private void Pose(Entity bus, bool exact, out float3 position, out float3 forward)
        {
            if (!exact && Core.HasCab && Core.TryGetBodyPose(out UnityEngine.Vector3 bodyPosition, out UnityEngine.Quaternion bodyRotation))
            {
                position = bodyPosition;
                forward = math.forward(bodyRotation);
                return;
            }

            ObjectTransform transform = EntityManager.GetComponentData<ObjectTransform>(bus);
            position = transform.m_Position;
            forward = math.forward(transform.m_Rotation);
        }

        private void UpdateTarget(Entity bus, float3 position, float3 forward)
        {
            int index = -1;
            if (EntityManager.TryGetComponent(bus, out Target target))
                index = IndexOfWaypoint(target.m_Target);

            if (index < 0)
            {
                // Not heading for one of the line's stops (just taken over from the depot path): aim for the nearest stop
                // ahead of the bus.
                index = NearestStop(position, forward);
                if (index >= 0)
                    SetTarget(bus, index);
            }
            else if (!BusState.Stops[index].IsStop)
            {
                // A plain route waypoint only shapes the line: the guide runs through it, the target is the next real stop.
                int next = NextStop(index);
                if (next >= 0)
                {
                    SetTarget(bus, next);
                    index = next;
                }
            }

            BusState.TargetIndex = index;
            if (index < 0)
                return;

            if (index != m_GuideIndex)
            {
                m_GuideIndex = index;
                BuildGuide(index);
            }

            if (index != m_ServedIndex)
                m_ServedIndex = -1;

            StopInfo stop = BusState.Stops[index];
            float3 delta = stop.Position - position;
            float heading = math.atan2(forward.x, forward.z);
            float bearing = math.atan2(delta.x, delta.z);
            BusState.BearingToTarget = math.degrees(Wrap(bearing - heading));
            BusState.Waiting = EntityManager.TryGetComponent(stop.Waypoint, out WaitingPassengers waiting) ? waiting.m_Count : 0;
            TrackGuide(position, heading, math.length(delta.xz));
        }

        /// <summary>A new target the way the game sets one (VehicleUtils.SetTarget): the bus's own path is redone for it.</summary>
        private void SetTarget(Entity bus, int index)
        {
            if (!EntityManager.HasComponent<Target>(bus))
                return;

            EntityManager.SetComponentData(bus, new Target(BusState.Stops[index].Waypoint));
            if (EntityManager.TryGetComponent(bus, out PathOwner owner))
            {
                owner.m_State &= ~PathFlags.Failed;
                owner.m_State |= PathFlags.Obsolete;
                EntityManager.SetComponentData(bus, owner);
            }
        }

        // The stretch of the guide the bus was on last, so a path running close past itself does not make it jump.
        private static int s_GuideHint;
        private static readonly float3[] s_Ahead = new float3[64];

        // The turn last found (TurnDirection) and where its bend is in the world; when it was last found (unscaled time).
        private static int s_HeldTurn;
        private static float3 s_HeldTurnAt;
        private static float s_HeldTurnSeen;
        private const float kTurnHoldSeconds = 4f, kTurnSameMeters = 25f, kTurnPassedMeters = 3f;

        /// <summary>
        /// Where the bus is on the line's path to the next stop: the distance still to drive along the road (not as the crow
        /// flies), the share of the way done (for the line strip in the HUD), where the dashed guide starts (ahead only),
        /// whether the bus has left the route, where the arrow points and the next turn.
        /// </summary>
        private static void TrackGuide(float3 position, float heading, float straightDistance)
        {
            BusState.OnGuide = false;
            BusState.OffRoute = false;
            BusState.OffRouteDistance = 0f;
            BusState.GuideFrom = 0;
            BusState.GuideFromT = 0f;
            BusState.GuideBearing = BusState.BearingToTarget;
            BusState.TurnDirection = 0;
            BusState.TurnDistance = 0f;
            int count = BusState.GuideCurves.Count;
            int hint = math.clamp(s_GuideHint, 0, math.max(0, count - 1));
            float best = float.MaxValue;
            float bestNear = float.MaxValue;
            int bestCurve = -1;
            int nearCurve = -1;
            float bestT = 0f;
            float nearT = 0f;
            // The stretch the bus was on first (nearly always the nearest), then the other curves, skipping those whose
            // control-point box is already farther away than the best found: the result is the same as measuring them all.
            int windowStart = math.max(0, hint - 1), windowEnd = math.min(count - 1, hint + 3);
            for (int i = windowStart; i <= windowEnd; i++)
            {
                float distance = GuideDistance(i, position, out float t);
                if (distance < best)
                {
                    best = distance;
                    bestCurve = i;
                    bestT = t;
                }

                if (distance < bestNear)
                {
                    bestNear = distance;
                    nearCurve = i;
                    nearT = t;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (i >= windowStart && i <= windowEnd)
                    continue;

                float4 box = BusState.GuideBounds[i];
                float2 outside = math.max(math.max(box.xy - position.xz, position.xz - box.zw), 0f);
                if (math.length(outside) > best)
                    continue;

                float distance = GuideDistance(i, position, out float t);
                if (distance < best || (distance == best && i < bestCurve))
                {
                    best = distance;
                    bestCurve = i;
                    bestT = t;
                }
            }

            // Stay on the stretch the bus was on unless another part of the path is clearly nearer.
            if (nearCurve >= 0 && bestNear <= best + 4f)
            {
                best = bestNear;
                bestCurve = nearCurve;
                bestT = nearT;
            }

            if (bestCurve < 0 || BusState.GuideTotal <= 0.1f)
            {
                BusState.DistanceToTarget = straightDistance;
                return;
            }

            if (best > BusState.kOffRouteMeters)
            {
                // Off the line's route: the arrow points back to it.
                float3 back = MathUtils.Position(BusState.GuideCurves[bestCurve], bestT) - position;
                BusState.OffRoute = true;
                BusState.OffRouteDistance = best;
                BusState.GuideBearing = math.degrees(Wrap(math.atan2(back.x, back.z) - heading));
            }

            if (best > 30f)
            {
                BusState.DistanceToTarget = straightDistance;
                return;
            }

            s_GuideHint = bestCurve;
            float done = 0f;
            for (int i = 0; i < bestCurve; i++)
                done += BusState.GuideLengths[i];
            done += MathUtils.Length(BusState.GuideCurves[bestCurve], new Bounds1(0f, bestT));
            BusState.OnGuide = true;
            BusState.GuideFrom = bestCurve;
            BusState.GuideFromT = bestT;
            BusState.DistanceToTarget = math.max(0f, BusState.GuideTotal - done);
            BusState.Progress = math.saturate(done / BusState.GuideTotal);
            LookAhead(bestCurve, bestT, position, heading);
        }

        /// <summary>Distance from the bus to a guide curve; a road above or below it (a bridge, a tunnel) is not the one the bus is on.</summary>
        private static float GuideDistance(int index, float3 position, out float t)
        {
            Bezier4x3 curve = BusState.GuideCurves[index];
            float distance = MathUtils.Distance(curve.xz, position.xz, out t);
            return distance + math.max(0f, math.abs(MathUtils.Position(curve, t).y - position.y) - 2f) * 3f;
        }

        /// <summary>
        /// Samples the path ahead every 5 m (up to 315 m, the stop line at most). The arrow points at the path 35 m ahead,
        /// which follows the road round corners instead of pointing at the stop through the houses. The next turn is
        /// where the path's heading changes by more than 35° within 30 m (a U-turn: more than 140° within 60 m). A turn found
        /// is held by where its bend is (HoldTurn), so it does not come and go as the samples move along with the bus.
        /// </summary>
        private static void LookAhead(int fromCurve, float fromT, float3 position, float heading)
        {
            const float kStep = 5f;
            int n = 0;
            s_Ahead[n++] = MathUtils.Position(BusState.GuideCurves[fromCurve], fromT);
            float carry = 0f;
            for (int i = fromCurve; i < BusState.GuideCurves.Count && n < s_Ahead.Length; i++)
            {
                float length = BusState.GuideLengths[i];
                if (length < 0.01f)
                    continue;

                float start = i == fromCurve ? fromT : 0f;
                float remaining = length * (1f - start);
                float s = kStep - carry;
                while (s <= remaining && n < s_Ahead.Length)
                {
                    s_Ahead[n++] = MathUtils.Position(BusState.GuideCurves[i], math.min(1f, start + s / length));
                    s += kStep;
                }

                carry = remaining - (s - kStep);
            }

            if (!BusState.OffRoute && n >= 2)
            {
                float3 ahead = s_Ahead[math.min(7, n - 1)] - position;
                if (math.lengthsq(ahead.xz) > 1f)
                    BusState.GuideBearing = math.degrees(Wrap(math.atan2(ahead.x, ahead.z) - heading));
            }

            const int kWindow = 6;
            float3 bend = position;
            for (int k = 0; k + kWindow + 1 < n; k++)
            {
                float h0 = SegmentHeading(k);
                float change = Wrap(SegmentHeading(k + kWindow) - h0);
                if (math.abs(change) < math.radians(35f))
                    continue;

                float total = Wrap(SegmentHeading(math.min(k + 2 * kWindow, n - 2)) - h0);
                BusState.TurnDirection = math.abs(total) > math.radians(140f) ? 2 : (change > 0f ? 1 : -1);
                BusState.TurnDistance = k * kStep + kWindow * kStep * 0.5f;
                bend = s_Ahead[math.min(k + kWindow / 2, n - 1)];
                break;
            }

            HoldTurn(bend, position, heading);
        }

        /// <summary>
        /// Keeps the turn steady: a turn found again near where it was is the same one; when none is found this frame, the held
        /// one stays while its bend is still ahead of the bus (its distance as the crow flies) for up to kTurnHoldSeconds.
        /// </summary>
        private static void HoldTurn(float3 bend, float3 position, float heading)
        {
            float now = UnityEngine.Time.unscaledTime;
            if (BusState.TurnDirection != 0)
            {
                s_HeldTurn = BusState.TurnDirection;
                s_HeldTurnAt = bend;
                s_HeldTurnSeen = now;
                return;
            }

            if (s_HeldTurn == 0)
                return;
            float2 forward = new float2(math.sin(heading), math.cos(heading));
            float along = math.dot((s_HeldTurnAt - position).xz, forward);
            if (along < kTurnPassedMeters || now - s_HeldTurnSeen > kTurnHoldSeconds || math.distance(s_HeldTurnAt.xz, position.xz) > 320f)
            {
                s_HeldTurn = 0;
                return;
            }

            BusState.TurnDirection = s_HeldTurn;
            BusState.TurnDistance = along;
        }

        private static float SegmentHeading(int k)
        {
            float3 d = s_Ahead[k + 1] - s_Ahead[k];
            return math.atan2(d.x, d.z);
        }

        private static float Wrap(float radians)
        {
            while (radians > math.PI)
                radians -= 2f * math.PI;
            while (radians < -math.PI)
                radians += 2f * math.PI;
            return radians;
        }

        /// <summary>
        /// The stop whose box the bus stands in: its middle and its front door in the box, whichever way round the bus stands
        /// (BusStopZone.Inside). The next stop is checked first. Also keeps, for the prompts and the passed-stop check, how far
        /// the bus front is past the next stop's point, how far the bus is from the box axis towards the kerb, and how far it
        /// has to move to be in the box lengthwise.
        /// </summary>
        private static int FindZone(float3 position, float3 forward)
        {
            float2 heading = math.normalizesafe(forward.xz);
            float2 front = position.xz + heading * BusState.FrontOffset;
            float2 frontDoor = front - heading * BusStopZone.kFrontDoorBehindFront;
            int target = BusState.TargetIndex;
            BusState.KerbGap = 0f;
            BusState.TargetZoneMove = 0f;
            BusState.TargetZonePast = false;
            if (target >= 0 && target < BusState.Stops.Count && BusState.Stops[target].Valid)
            {
                StopInfo stop = BusState.Stops[target];
                float2 point = stop.Position.xz, direction = stop.Direction.xz;
                BusState.TargetFrontAlong = math.dot(front - point, direction);
                BusState.TargetLateral = math.dot(position.xz - point, stop.Right.xz);
                BusState.KerbGap = stop.KerbSide != 0f ? -BusState.TargetLateral * stop.KerbSide : 0f;
                float shift = BusStopZone.Shift(math.dot(position.xz - point, direction), math.dot(frontDoor - point, direction), BusState.ZoneBehind);
                BusState.TargetZoneMove = BusStopZone.Move(shift, math.dot(heading, direction));
                BusState.TargetZonePast = shift < 0f;
                if (InBox(stop, position, frontDoor))
                    return target;
            }

            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                if (i != target && BusState.Stops[i].Valid && InBox(BusState.Stops[i], position, frontDoor))
                    return i;
            }

            return -1;
        }

        private static bool InBox(StopInfo stop, float3 position, float2 frontDoor)
        {
            return math.abs(position.y - stop.Position.y) <= BusStopZone.kLevelMeters &&
                BusStopZone.Inside(stop.Position.xz, stop.Direction.xz, stop.Right.xz, stop.KerbSide, BusState.ZoneBehind, position.xz, frontDoor);
        }

        /// <summary>The nearest real stop, preferring stops ahead of the bus over ones it has just passed.</summary>
        private int NearestStop(float3 position, float3 forward)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                StopInfo stop = BusState.Stops[i];
                if (!stop.Valid || !stop.IsStop)
                    continue;

                float3 delta = stop.Position - position;
                float score = math.length(delta.xz) + (math.dot(delta.xz, forward.xz) < 0f ? 150f : 0f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>The next real stop after <paramref name="index"/> in line order (plain route waypoints left out).</summary>
        private static int NextStop(int index)
        {
            int count = BusState.Stops.Count;
            for (int k = 1; k <= count; k++)
            {
                int i = (index + k) % count;
                if (BusState.Stops[i].IsStop)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Driving past the next stop without opening the doors: with nobody to pick up or set down (or nobody getting off
        /// a full bus) the stop is skipped as soon as the front door is a few metres past the box, else it is missed once
        /// the bus is clearly gone (25 m past the stop point). Either way the next stop becomes the target, so the guide, the
        /// arrow and the line strip point ahead again. Reversing into the box can still open the doors there.
        /// </summary>
        private void CheckPassedStop(Entity bus, float3 position, float3 forward)
        {
            int target = BusState.TargetIndex;
            if (target < 0 || target >= BusState.Stops.Count || BusState.ZoneIndex == target)
                return;

            StopInfo stop = BusState.Stops[target];
            if (!stop.Valid || !stop.IsStop || math.abs(position.y - stop.Position.y) > 4f ||
                math.abs(BusState.TargetLateral) > BusState.kPassedLateral ||
                math.dot(math.normalizesafe(forward.xz), stop.Direction.xz) < 0.5f)
                return;

            // The stop the doors were just open at: the game moves the target on itself, but never count it as passed.
            bool served = target == m_ServedIndex;
            bool skippable = served || BusState.TargetSkip != SkipReason.None;
            if (BusState.TargetFrontAlong < (skippable ? BusStopZone.kPassedSkipMeters : BusStopZone.kPassedMissMeters))
                return;

            int next = NextStop(target);
            if (next < 0 || next == target)
                return;

            if (!served)
            {
                m_LastPassed = target;
                m_LastPassedSkipped = skippable;
                if (skippable)
                {
                    BusState.Skipped++;
                    BusState.SetMessage("Msg.SkippedStop", stop.Name);
                }
                else
                {
                    BusState.Missed++;
                    BusState.SetMessage("Msg.MissedStop", stop.Name + "|" + BusState.Waiting + "|" + BusState.Alighting);
                }

                Mod.log.Info($"Bus stop {stop.Name} {(skippable ? "skipped" : "missed")} ({BusState.Waiting} waiting, {BusState.Alighting} to get off).");
            }

            if (BusState.DepotLeg)
            {
                // Driven past the first stop out of the depot: the trip has begun.
                BusState.DepotLeg = false;
                m_DepotStop = target;
            }

            SetTarget(bus, next);
            m_ServedIndex = -1;
            m_NextDemandFrame = 0U;
        }

        private int IndexOfWaypoint(Entity waypoint)
        {
            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                if (BusState.Stops[i].Waypoint == waypoint)
                    return i;
            }

            return -1;
        }

        // ------------------------------------------------------------------ doors

        private void ToggleDoors(bool force)
        {
            Entity bus = BusState.Bus;
            switch (BusState.State)
            {
                case ShiftState.Driving:
                    if (force)
                        return;
                    if (BusState.ZoneIndex < 0)
                    {
                        BusState.SetMessage("Msg.NotInZone");
                        return;
                    }
                    if (!BusState.Stopped)
                    {
                        BusState.SetMessage("Msg.StopFirst");
                        return;
                    }
                    OpenDoors(bus, BusState.ZoneIndex);
                    break;
                case ShiftState.DoorsOpening:
                case ShiftState.DoorsOpen:
                    m_DoorFrame = Frame;
                    BusState.State = ShiftState.DoorsClosing;
                    SetDeparture(bus, force ? BusShiftRules.ForcedDeparture(Frame) : Frame);
                    BusState.SetMessage(force ? "Msg.DoorsForceClosed" : "Msg.DoorsClosing");
                    break;
                case ShiftState.DoorsClosing:
                    if (force)
                        SetDeparture(bus, BusShiftRules.ForcedDeparture(Frame));
                    break;
            }
        }

        private void OpenDoors(Entity bus, int stopIndex)
        {
            StopInfo stop = BusState.Stops[stopIndex];
            int target = BusState.TargetIndex;
            if (target >= 0 && stopIndex != target && stop.Ordinal >= 0 && BusState.Stops[target].Ordinal >= 0 && BusState.RealStops > 0)
            {
                int real = BusState.RealStops;
                int ahead = ((stop.Ordinal - BusState.Stops[target].Ordinal) % real + real) % real;
                if (ahead <= real / 2)
                {
                    // Further along the line (the bus took another road past the stops in between): those were passed.
                    for (int i = target; i != stopIndex && i >= 0; i = NextStop(i))
                    {
                        if (i < BusState.Demand.Count && BusState.Demand[i].Skip != SkipReason.None)
                            BusState.Skipped++;
                        else
                            BusState.Missed++;
                    }

                    BusState.DepotLeg = false;
                    m_DepotStop = -1;
                }
                else if (stopIndex == m_LastPassed)
                {
                    // Back at the stop just driven past (stopped late and reversed into the box): it is served after all.
                    if (m_LastPassedSkipped)
                        BusState.Skipped = math.max(0, BusState.Skipped - 1);
                    else
                        BusState.Missed = math.max(0, BusState.Missed - 1);
                    if (stopIndex == m_DepotStop)
                        BusState.DepotLeg = true;
                }
            }

            m_LastPassed = -1;
            m_ServedIndex = stopIndex;
            EntityManager.SetComponentData(bus, new Target(stop.Waypoint));
            if (EntityManager.HasBuffer<CarNavigationLane>(bus))
                EntityManager.GetBuffer<CarNavigationLane>(bus).Clear();

            // The game samples this stop's travel time from the departure frame the moment boarding begins (see BusTiming):
            // it has to be a sane past value now, before TransportCarAISystem runs.
            WriteArrivalTiming(bus, stopIndex);
            Core.SetParkingBrake(true);
            Core.SetHoldStopFlags(true);
            HoldStop(bus, stopIndex);
            m_OnBoard.Clear();
            foreach (Entity car in Cars(bus))
            {
                if (!EntityManager.TryGetBuffer(car, true, out DynamicBuffer<Passenger> passengers))
                    continue;
                for (int i = 0; i < passengers.Length; i++)
                {
                    if (IsPerson(passengers[i].m_Passenger))
                        m_OnBoard.Add(passengers[i].m_Passenger);
                }
            }

            BusState.StopBoarded = 0;
            BusState.StopAlighted = 0;
            m_StopWaiting.Reset();
            m_DoorFrame = Frame;
            BusState.State = ShiftState.DoorsOpening;
            BusState.SetMessage("Msg.DoorsOpening", stop.Name);
        }

        /// <summary>The lane says "arrived at the end of the path" and the target is the stop: the bus AI starts boarding.</summary>
        private void HoldStop(Entity bus, int stopIndex)
        {
            if (!EntityManager.TryGetComponent(bus, out CarCurrentLane lane))
                return;

            const CarLaneFlags kArrived = CarLaneFlags.EndOfPath | CarLaneFlags.EndReached;
            const CarLaneFlags kClear = CarLaneFlags.ParkingSpace | CarLaneFlags.Waypoint | CarLaneFlags.Connection | CarLaneFlags.ResetSpeed;
            CarLaneFlags flags = (lane.m_LaneFlags | kArrived) & ~kClear;
            if (flags == lane.m_LaneFlags)
                return;

            lane.m_LaneFlags = flags;
            if (stopIndex >= 0 && stopIndex < BusState.Stops.Count && BusState.Stops[stopIndex].Lane != Entity.Null && lane.m_Lane == Entity.Null)
                lane.m_Lane = BusState.Stops[stopIndex].Lane;
            EntityManager.SetComponentData(bus, lane);
        }

        /// <summary>
        /// Keeps the doors open: once the bus AI has begun boarding it closes them when the simulation frame passes the
        /// departure frame, so that frame is pushed ahead while the doors are open. Never before boarding has begun: until
        /// then the departure frame is the previous stop's departure, which the game is about to sample as the travel time
        /// (a future frame there wraps to 70 million seconds and kills the line's ridership: the 0.4.0 bug).
        /// </summary>
        private void HoldDeparture(Entity bus, uint frame)
        {
            if (!EntityManager.TryGetComponent(bus, out Game.Vehicles.PublicTransport transport))
                return;

            if ((transport.m_State & PublicTransportFlags.Boarding) == 0)
                return;

            if (transport.m_DepartureFrame < frame + kHoldDepartureFrames / 2U)
            {
                transport.m_DepartureFrame = frame + kHoldDepartureFrames;
                EntityManager.SetComponentData(bus, transport);
            }
        }

        // ------------------------------------------------------------------ line timing (see BusTiming)

        // The simulation frame the bus left its last served stop, and which stop that was (-1: none this shift).
        private uint m_LeftStopFrame;
        private int m_LeftStopIndex = -1;

        /// <summary>
        /// Before boarding begins: the departure frame the game will sample as "travel time from the previous stop". The
        /// player's real time when the bus came straight from the previous stop of the line (bounded to half .. one and a
        /// half of what the stop expects), else the expected time, so a detour or a skipped stop never distorts the line.
        /// </summary>
        private void WriteArrivalTiming(Entity bus, int stopIndex)
        {
            if (!EntityManager.TryGetComponent(bus, out Game.Vehicles.PublicTransport transport))
                return;

            uint now = Frame;
            float expected = ExpectedTravelSeconds(stopIndex);
            bool straight = m_LeftStopIndex >= 0 && NextStop(m_LeftStopIndex) == stopIndex && now > m_LeftStopFrame;
            float actual = straight ? (now - m_LeftStopFrame) / 60f : 0f;
            float sample = BusTiming.SampleSeconds(actual, expected, straight);
            transport.m_DepartureFrame = BusTiming.DepartureFrame(now, sample);
            EntityManager.SetComponentData(bus, transport);
            Mod.log.Info($"Bus stop {BusState.Stops[stopIndex].Name}: travel time sample {sample:0} s (actual {actual:0} s, expected {expected:0} s, straight {straight}).");
        }

        /// <summary>What the stop expects from the previous stop: its saved average when sane, else the path's duration.</summary>
        private float ExpectedTravelSeconds(int stopIndex)
        {
            Entity waypoint = BusState.Stops[stopIndex].Waypoint;
            float average = EntityManager.TryGetComponent(waypoint, out VehicleTiming timing) ? timing.m_AverageTravelTime : 0f;
            return BusTiming.ExpectedSeconds(average, PathSeconds(stopIndex));
        }

        /// <summary>The planned duration of the line's path into the stop (the segment ending at that waypoint).</summary>
        private float PathSeconds(int stopIndex)
        {
            if (!EntityManager.TryGetBuffer(BusState.Line, true, out DynamicBuffer<RouteSegment> segments) || segments.Length == 0)
                return 0f;

            int count = segments.Length;
            int index = ((stopIndex - 1) % count + count) % count;
            return EntityManager.TryGetComponent(segments[index].m_Segment, out PathInformation info) ? info.m_Duration : 0f;
        }

        /// <summary>
        /// Repairs what earlier versions did to this line: a waypoint whose saved average travel time no bus could produce
        /// (the departure-frame wrap) gets the path's duration back, so residents take the line again. Run when a shift starts.
        /// </summary>
        private void HealLineTiming(Entity line)
        {
            int healed = 0;
            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                Entity waypoint = BusState.Stops[i].Waypoint;
                if (!EntityManager.TryGetComponent(waypoint, out VehicleTiming timing))
                    continue;

                float path = PathSeconds(i);
                if (!BusTiming.IsPoisoned(timing.m_AverageTravelTime, path))
                    continue;

                Mod.log.Info($"Bus stop {BusState.Stops[i].Name}: saved average travel time {timing.m_AverageTravelTime:0} s is impossible; reset to the path's {path:0} s.");
                timing.m_AverageTravelTime = math.max(0f, path);
                EntityManager.SetComponentData(waypoint, timing);
                healed++;
            }

            if (healed > 0)
                Mod.log.Info($"Bus line {SafeName(line)}: {healed} stop timing(s) repaired.");
        }

        private void SetDeparture(Entity bus, uint frame)
        {
            if (!EntityManager.TryGetComponent(bus, out Game.Vehicles.PublicTransport transport))
                return;

            transport.m_DepartureFrame = frame;
            EntityManager.SetComponentData(bus, transport);
        }

        private bool IsBoarding(Entity bus)
        {
            return EntityManager.TryGetComponent(bus, out Game.Vehicles.PublicTransport transport) &&
                (transport.m_State & PublicTransportFlags.Boarding) != 0;
        }

        /// <summary>Doors closed: drop the stop marks so the bus can drive, and count the stop.</summary>
        private void FinishStop(Entity bus, bool served)
        {
            if (EntityManager.TryGetComponent(bus, out CarCurrentLane lane) && (lane.m_LaneFlags & (CarLaneFlags.EndOfPath | CarLaneFlags.EndReached)) != 0)
            {
                lane.m_LaneFlags &= ~(CarLaneFlags.EndOfPath | CarLaneFlags.EndReached);
                EntityManager.SetComponentData(bus, lane);
            }

            Core.SetHoldStopFlags(false);
            Core.SetParkingBrake(false);
            if (served)
            {
                BusState.Served++;
                BusState.SetMessage("Msg.Departing", BusState.StopBoarded + "|" + BusState.StopAlighted);
            }

            // Leaving this stop now: the next stop's travel time counts from here, and the trip has begun.
            m_LeftStopIndex = m_ServedIndex;
            m_LeftStopFrame = Frame;
            BusState.DepotLeg = false;
            m_DepotStop = -1;
            BusState.State = ShiftState.Driving;
            m_GuideIndex = -1;
            m_NextDemandFrame = 0U;
        }

        /// <summary>
        /// The bus's cars: the front and, on an articulated bus, the sections behind it. Each car has its own riders and
        /// capacity: the game seats a boarding rider in the nearest car with room (ResidentAISystem.TryFindVehicle), so on a
        /// K18 the riders of the rear section are on the rear section's list, not the front's.
        /// </summary>
        private List<Entity> Cars(Entity bus)
        {
            m_Cars.Clear();
            m_Cars.Add(bus);
            if (EntityManager.TryGetBuffer(bus, true, out DynamicBuffer<LayoutElement> layout))
            {
                for (int i = 0; i < layout.Length; i++)
                {
                    Entity car = layout[i].m_Vehicle;
                    if (car != Entity.Null && car != bus && EntityManager.Exists(car))
                        m_Cars.Add(car);
                }
            }

            return m_Cars;
        }

        /// <summary>A dog is not a person: it pays no fare and takes no place (the game's free space counts residents only).</summary>
        private bool IsPerson(Entity passenger) => !EntityManager.HasComponent<Game.Creatures.Pet>(passenger);

        private uint m_NextPassengerFrame;

        /// <summary>
        /// The people on the bus (all its cars) and its capacity; also how many of them are still walking to the doors. Every step
        /// while the doors are open (the sign counts the riders still to board); with the doors shut the count cannot change
        /// but at a stop, so it is taken twice a game second (each rider is two lookups, a K18 carries 150).
        /// </summary>
        private void UpdatePassengers(Entity bus)
        {
            uint frame = Frame;
            if (!BusState.DoorsOpen && BusState.Capacity > 0 && frame < m_NextPassengerFrame && m_NextPassengerFrame - frame <= 30U)
                return;
            m_NextPassengerFrame = frame + 30U;

            int people = 0, walking = 0;
            bool measure = BusState.Capacity == 0;
            foreach (Entity car in Cars(bus))
            {
                if (EntityManager.TryGetBuffer(car, true, out DynamicBuffer<Passenger> passengers))
                {
                    for (int i = 0; i < passengers.Length; i++)
                    {
                        Entity passenger = passengers[i].m_Passenger;
                        if (!IsPerson(passenger))
                            continue;
                        people++;
                        // On the bus's list from the moment the game lets a rider board; inside once Ready is set.
                        if (EntityManager.TryGetComponent(passenger, out Game.Creatures.CurrentVehicle current) && (current.m_Flags & Game.Creatures.CreatureVehicleFlags.Ready) == 0)
                            walking++;
                    }
                }

                if (measure && EntityManager.TryGetComponent(car, out PrefabRef prefabRef) && EntityManager.TryGetComponent(prefabRef.m_Prefab, out PublicTransportVehicleData data))
                    BusState.Capacity += data.m_PassengerCapacity;
            }

            BusState.Passengers = people;
            m_Walking = walking;
        }

        /// <summary>While the doors are open: the riders still to board at the stop and those the full bus leaves there, for the sign.</summary>
        private void UpdateBoardingCount()
        {
            if (!BusState.DoorsOpen || m_ServedIndex < 0 || m_ServedIndex >= BusState.Stops.Count)
            {
                BusState.ToBoard = 0;
                BusState.LeftBehind = 0;
                return;
            }

            int stillWaiting = EntityManager.TryGetComponent(BusState.Stops[m_ServedIndex].Waypoint, out WaitingPassengers waiting)
                ? m_StopWaiting.Observe(waiting.m_Count, waiting.m_OngoingAccumulation, BusState.StopBoarded)
                : 0;
            BusState.ToBoard = BusStopZone.ToBoard(m_Walking, stillWaiting, BusState.Passengers, BusState.Capacity, out BusState.LeftBehind);
        }

        private uint m_NextDemandFrame;
        private int[] m_Alighting = new int[0];
        private readonly Dictionary<Entity, int> m_ExitCounts = new Dictionary<Entity, int>();

        /// <summary>
        /// Twice a game second: how many wait at each stop (the game's own count of residents waiting there for this line)
        /// and how many riders get off where. The game lets a rider off at the stop of the next waypoint in the rider's path
        /// (ResidentAISystem / RouteUtils.ShouldExitVehicle), so that is the stop counted. A stop where nobody gets on or
        /// off, or nobody gets off a full bus, may be skipped.
        /// </summary>
        private void UpdateDemand(Entity bus)
        {
            int count = BusState.Stops.Count;
            uint frame = Frame;
            bool due = BusState.Demand.Count != count || frame >= m_NextDemandFrame || m_NextDemandFrame - frame > 30U;
            if (due)
            {
                m_NextDemandFrame = frame + 30U;
                RefreshDemand(bus, count);
            }

            int target = BusState.TargetIndex;
            StopDemand demand = target >= 0 && target < BusState.Demand.Count ? BusState.Demand[target] : default;
            BusState.Alighting = demand.Alighting;
            BusState.TargetSkip = target >= 0 && target < count && BusState.Stops[target].IsStop ? demand.Skip : SkipReason.None;
        }

        private void RefreshDemand(Entity bus, int count)
        {
            if (m_Alighting.Length != count)
                m_Alighting = new int[count];
            Array.Clear(m_Alighting, 0, count);
            m_ExitCounts.Clear();
            foreach (Entity car in Cars(bus))
            {
                if (!EntityManager.TryGetBuffer(car, true, out DynamicBuffer<Passenger> passengers))
                    continue;
                for (int i = 0; i < passengers.Length; i++)
                {
                    if (!IsPerson(passengers[i].m_Passenger))
                        continue;
                    Entity exit = ExitWaypoint(passengers[i].m_Passenger);
                    if (exit != Entity.Null)
                        m_ExitCounts[exit] = (m_ExitCounts.TryGetValue(exit, out int n) ? n : 0) + 1;
                }
            }

            // Each exit waypoint belongs to the first stop from the next one on that is it (or the same stop object, for a
            // rider routed through another line's waypoint there).
            int start = math.max(0, BusState.TargetIndex);
            foreach (KeyValuePair<Entity, int> exit in m_ExitCounts)
            {
                Entity exitStop = EntityManager.TryGetComponent(exit.Key, out Connected connected) ? connected.m_Connected : Entity.Null;
                for (int k = 0; k < count; k++)
                {
                    int i = (start + k) % count;
                    StopInfo stop = BusState.Stops[i];
                    if (stop.IsStop && (stop.Waypoint == exit.Key || (exitStop != Entity.Null && stop.Stop == exitStop)))
                    {
                        m_Alighting[i] += exit.Value;
                        break;
                    }
                }
            }

            bool full = BusState.Capacity > 0 && BusState.Passengers >= BusState.Capacity;
            BusState.Demand.Clear();
            var json = new System.Text.StringBuilder("[");
            for (int i = 0; i < count; i++)
            {
                StopInfo stop = BusState.Stops[i];
                var demand = new StopDemand
                {
                    Waiting = stop.IsStop && EntityManager.TryGetComponent(stop.Waypoint, out WaitingPassengers waiting) ? waiting.m_Count : 0,
                    Alighting = m_Alighting[i]
                };
                if (stop.IsStop && demand.Alighting == 0)
                    demand.Skip = demand.Waiting == 0 ? SkipReason.Empty : (full ? SkipReason.Full : SkipReason.None);
                BusState.Demand.Add(demand);
                if (!stop.IsStop)
                    continue;

                if (json.Length > 1)
                    json.Append(',');
                json.Append('[').Append(demand.Waiting).Append(',').Append(demand.Alighting).Append(',').Append((int)demand.Skip).Append(']');
            }

            BusState.DemandJson = json.Append(']').ToString();
        }

        /// <summary>The waypoint a rider gets off at: the next element of the rider's path, as the resident AI tests it.</summary>
        private Entity ExitWaypoint(Entity passenger)
        {
            if (!EntityManager.TryGetComponent(passenger, out PathOwner owner) || !EntityManager.TryGetBuffer(passenger, true, out DynamicBuffer<PathElement> path))
                return Entity.Null;

            for (int k = owner.m_ElementIndex + 1; k < path.Length && k <= owner.m_ElementIndex + 3; k++)
            {
                Entity target = path[k].m_Target;
                if (target != Entity.Null && EntityManager.HasComponent<Game.Routes.Waypoint>(target))
                    return target;
            }

            return Entity.Null;
        }

        /// <summary>The whole bus from front to back: all its sections for an articulated bus.</summary>
        private float RigLength(Entity bus)
        {
            float length = 0f;
            if (EntityManager.TryGetBuffer(bus, true, out DynamicBuffer<LayoutElement> layout) && layout.Length > 0)
            {
                for (int i = 0; i < layout.Length; i++)
                    length += SectionLength(layout[i].m_Vehicle);
            }

            return length > 1f ? length : SectionLength(bus);
        }

        private float SectionLength(Entity vehicle)
        {
            return vehicle != Entity.Null && EntityManager.TryGetComponent(vehicle, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out ObjectGeometryData geometry)
                ? math.max(0f, geometry.m_Bounds.max.z - geometry.m_Bounds.min.z)
                : 0f;
        }

        /// <summary>
        /// Counts who got on and off while the doors are open. The fare itself is charged by the game when a resident
        /// boards (into the city budget as public transport fees); this only adds it up for the shift summary.
        /// </summary>
        private void TrackFares(Entity bus)
        {
            bool listed = false;
            m_NowAboard.Clear();
            foreach (Entity car in Cars(bus))
            {
                if (!EntityManager.TryGetBuffer(car, true, out DynamicBuffer<Passenger> passengers))
                    continue;
                listed = true;
                for (int i = 0; i < passengers.Length; i++)
                {
                    Entity passenger = passengers[i].m_Passenger;
                    if (!IsPerson(passenger))
                        continue;
                    m_NowAboard.Add(passenger);
                    if (m_OnBoard.Add(passenger))
                    {
                        BusState.Boarded++;
                        BusState.StopBoarded++;
                        BusState.Fares += BusState.TicketPrice;
                    }
                }
            }

            if (!listed)
                return;

            m_Gone.Clear();
            foreach (Entity passenger in m_OnBoard)
            {
                if (!m_NowAboard.Contains(passenger))
                    m_Gone.Add(passenger);
            }

            foreach (Entity passenger in m_Gone)
            {
                m_OnBoard.Remove(passenger);
                BusState.Alighted++;
                BusState.StopAlighted++;
            }
        }

        // ------------------------------------------------------------------ line data

        private bool IsBusLine(Entity line)
        {
            return line != Entity.Null && EntityManager.Exists(line) && EntityManager.HasComponent<TransportLine>(line) &&
                EntityManager.TryGetComponent(line, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out TransportLineData data) &&
                data.m_TransportType == TransportType.Bus && data.m_PassengerTransport;
        }

        private void ReadLineInfo(Entity line)
        {
            BusState.LineName = SafeName(line);
            LineLabel.Parse(BusState.LineName, EntityManager.TryGetComponent(line, out RouteNumber number) ? number.m_Number : 0, EntityNames.PlayerNamed(m_NameSystem, line), out BusState.LinePrefix, out BusState.LineNumber);
            BusState.TicketPrice = EntityManager.TryGetComponent(line, out TransportLine transportLine) ? transportLine.m_TicketPrice : 0;
            if (EntityManager.TryGetComponent(line, out Game.Routes.Color color))
                BusState.LineColor = color.m_Color;
        }

        internal string SafeName(Entity entity) => EntityNames.Text(m_NameSystem, entity);

        internal bool PlayerNamed(Entity entity) => EntityNames.PlayerNamed(m_NameSystem, entity);

        /// <summary>
        /// Stops in line order, each with a straight stop box. The stop line is where the game's own buses stop: first
        /// where a bus of this line was seen boarding at that stop (exact for every stop and road type), else the stop's
        /// end lane at its end position (what the bus AI drives to: a bus bay, or the kerb lane), else the end of the line's
        /// path. The box runs straight along the road at the stop, so it does not follow a bay's curved entry back into
        /// the traffic lane.
        /// </summary>
        private void BuildStops(Entity line)
        {
            BusState.Stops.Clear();
            if (!EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteWaypoint> waypoints))
                return;

            EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteSegment> segments);
            int count = waypoints.Length;
            int real = 0;
            var names = new System.Text.StringBuilder("[");
            var log = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                var stop = new StopInfo { Waypoint = waypoint, Ordinal = -1 };
                if (EntityManager.TryGetComponent(waypoint, out Connected connected) && connected.m_Connected != Entity.Null)
                {
                    stop.Stop = connected.m_Connected;
                    stop.Ordinal = real++;
                    stop.Name = SafeName(connected.m_Connected);
                    float3 position = default;
                    float3 direction = default;
                    string source = null;
                    if (EntityManager.TryGetComponent(waypoint, out RouteLane routeLane) && routeLane.m_EndLane != Entity.Null &&
                        EntityManager.TryGetComponent(routeLane.m_EndLane, out Curve laneCurve) && laneCurve.m_Length > 0.5f)
                    {
                        // Where TransportCarAISystem ends the bus's navigation: the end lane at the end position.
                        stop.Lane = routeLane.m_EndLane;
                        float t = math.saturate(routeLane.m_EndCurvePos);
                        position = MathUtils.Position(laneCurve.m_Bezier, t);
                        direction = MathUtils.Tangent(laneCurve.m_Bezier, t);
                        source = "stop lane";
                        if (TryBayLane(stop.Stop, routeLane.m_EndLane, position, direction, out float3 bayLanePosition, out float3 bayLaneDirection))
                        {
                            position = bayLanePosition;
                            direction = bayLaneDirection;
                            source = "bay lane";
                        }
                    }
                    else if (segments.IsCreated && segments.Length == count &&
                             PathEndPoint(segments[(i - 1 + count) % count].m_Segment, out position, out direction))
                    {
                        source = "line path";
                    }

                    if (s_SeenStops.TryGetValue(waypoint, out SeenStop seen) && (source == null || math.distance(seen.Front.xz, position.xz) < 30f))
                    {
                        position = seen.Front;
                        direction = seen.Forward;
                        source = "seen bus";
                    }

                    // Best of all: the painted bus bay the game draws on the road at the stop (the green area in the line
                    // colour), when the stop asset has one.
                    if (source != "seen bus" && TryPaintedBay(stop.Stop, source != null ? position : float3.zero, source != null ? direction : float3.zero, source != null, out float3 bayFront, out float3 bayDirection))
                    {
                        position = bayFront;
                        direction = bayDirection;
                        source = "painted bay";
                    }

                    if (source != null)
                    {
                        SetStopBox(ref stop, position, direction, source == "stop lane" || source == "line path");
                        log.Append($"\n  {i} {stop.Name}: {source} at ({stop.Position.x:0.0}, {stop.Position.z:0.0}); {DescribeLane(routeLane.m_StartLane)} -> {DescribeLane(routeLane.m_EndLane)} @ {routeLane.m_EndCurvePos:0.00}");
                    }
                }

                BusState.Stops.Add(stop);
                if (!stop.IsStop)
                {
                    // A plain route waypoint (it only shapes the line): not a stop on the strip or in the count.
                    log.Append($"\n  {i} waypoint");
                    continue;
                }

                if (names.Length > 1)
                    names.Append(',');
                names.Append('"').Append((stop.Name ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }

            BusState.RealStops = real;
            BusState.StopNamesJson = names.Append(']').ToString();
            BusState.TransfersJson = BuildTransfersJson(line);
            m_TerminusSource = null;
            RefreshTerminus(line);
            Mod.log.Info($"Bus line {SafeName(line)}: {real} stops, {count - real} other waypoints" + log);
        }

        /// <summary>
        /// The other lines calling at each stop (for the transfer badges on the strip): every route whose waypoint is connected
        /// to the same stop object, by that route's number, colour and transport type. Lines sharing a stop object are what the
        /// game itself counts as a transfer (ConnectedRoute on the stop).
        /// </summary>
        private string BuildTransfersJson(Entity line)
        {
            var json = new System.Text.StringBuilder("[");
            bool firstStop = true;
            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                StopInfo stop = BusState.Stops[i];
                if (!stop.IsStop)
                    continue;

                if (!firstStop)
                    json.Append(',');
                firstStop = false;
                json.Append('[');
                bool firstLine = true;
                if (EntityManager.TryGetBuffer(stop.Stop, true, out DynamicBuffer<ConnectedRoute> routes))
                {
                    var seen = new HashSet<Entity>();
                    for (int r = 0; r < routes.Length; r++)
                    {
                        if (!EntityManager.TryGetComponent(routes[r].m_Waypoint, out Owner owner) || owner.m_Owner == line || !seen.Add(owner.m_Owner))
                            continue;
                        if (!EntityManager.TryGetComponent(owner.m_Owner, out PrefabRef prefabRef) ||
                            !EntityManager.TryGetComponent(prefabRef.m_Prefab, out TransportLineData data) || !data.m_PassengerTransport)
                            continue;

                        string number = EntityManager.TryGetComponent(owner.m_Owner, out RouteNumber routeNumber) ? routeNumber.m_Number.ToString() : "";
                        string color = EntityManager.TryGetComponent(owner.m_Owner, out Game.Routes.Color lineColor)
                            ? $"#{lineColor.m_Color.r:x2}{lineColor.m_Color.g:x2}{lineColor.m_Color.b:x2}"
                            : "#4bc3f1";
                        if (!firstLine)
                            json.Append(',');
                        firstLine = false;
                        json.Append("[\"").Append(number).Append("\",\"").Append(color).Append("\",\"").Append(data.m_TransportType).Append("\"]");
                    }
                }

                json.Append(']');
            }

            return json.Append(']').ToString();
        }

        /// <summary>The terminus for the signs and the announcements, read again; logged when it or its source changes.</summary>
        private void RefreshTerminus(Entity line)
        {
            m_TerminusFrame = Frame;
            string name = Terminus(line, out string source);
            BusState.Origin = Origin(name);
            if (name == BusState.Terminus && source == m_TerminusSource)
                return;

            BusState.Terminus = name;
            m_TerminusSource = source;
            Mod.log.Info($"Bus line {BusState.LineName}: terminus {name} ({source}).");
        }

        /// <summary>
        /// The direction sign's stop: the terminus TransitTimetables runs the line to when it times the line (the stop chosen
        /// there, or its first-stop fallback); else, as before, the last real stop of the line in its waypoint order.
        /// </summary>
        private string Terminus(Entity line, out string source)
        {
            if (TransitTimetablesLink.TryTerminus(EntityManager, line, out Entity timetableStop, out bool chosen))
            {
                for (int i = 0; i < BusState.Stops.Count; i++)
                {
                    if (BusState.Stops[i].IsStop && BusState.Stops[i].Stop == timetableStop)
                    {
                        source = chosen ? "TransitTimetables, the chosen stop" : "TransitTimetables, first stop: none chosen or no longer on the route";
                        return BusState.Stops[i].Name ?? "";
                    }
                }
            }

            source = "the line's last stop";
            for (int i = BusState.Stops.Count - 1; i >= 0; i--)
            {
                if (BusState.Stops[i].IsStop)
                    return BusState.Stops[i].Name ?? "";
            }

            return "";
        }

        /// <summary>The line's other end for a printed board: its first real stop, or its last when the terminus is the first.</summary>
        private static string Origin(string terminus)
        {
            string first = null, last = null;
            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                if (!BusState.Stops[i].IsStop) continue;
                first = first ?? BusState.Stops[i].Name;
                last = BusState.Stops[i].Name;
            }

            return string.Equals(first, terminus, System.StringComparison.Ordinal) ? last ?? "" : first ?? "";
        }

        /// <summary>The straight stop box ending a few metres past the stop point, along the road (or the seen bus's heading).</summary>
        private void SetStopBox(ref StopInfo stop, float3 position, float3 direction, bool alongRoad)
        {
            if (alongRoad && EntityManager.TryGetComponent(stop.Stop, out Attached attached) && EntityManager.TryGetComponent(attached.m_Parent, out Curve road))
            {
                // The road's direction at the stop, pointing the way the bus drives.
                float3 along = MathUtils.Tangent(road.m_Bezier, math.saturate(attached.m_CurvePosition));
                if (math.lengthsq(along.xz) > 0.0001f)
                    direction = math.dot(along.xz, direction.xz) >= 0f ? along : -along;
            }

            float2 flat = math.normalizesafe(direction.xz, new float2(0f, 1f));
            stop.Position = position;
            stop.Direction = new float3(flat.x, 0f, flat.y);
            stop.Right = new float3(flat.y, 0f, -flat.x);

            // The kerb is on the side of the stop object (the pavement, or the island of a median stop).
            stop.KerbSide = 0f;
            if (EntityManager.TryGetComponent(stop.Stop, out ObjectTransform stopTransform))
            {
                float side = math.dot(stopTransform.m_Position.xz - position.xz, stop.Right.xz);
                if (math.abs(side) > 0.75f)
                    stop.KerbSide = math.sign(side);
            }

            SetZoneCurve(ref stop);
            stop.Valid = true;
        }

        /// <summary>The painted box: from well behind the whole bus to a few metres past the stop point.</summary>
        private static void SetZoneCurve(ref StopInfo stop)
        {
            float3 rear = stop.Position - stop.Direction * BusState.ZoneBehind;
            float3 ahead = stop.Position + stop.Direction * BusStopZone.kAheadMeters;
            stop.ZoneCurves = new[] { new Bezier4x3(rear, math.lerp(rear, ahead, 1f / 3f), math.lerp(rear, ahead, 2f / 3f), ahead) };
        }

        private static void ResizeZones()
        {
            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                StopInfo stop = BusState.Stops[i];
                if (!stop.Valid)
                    continue;

                SetZoneCurve(ref stop);
                BusState.Stops[i] = stop;
            }
        }

        /// <summary>
        /// Roads with a built-in stop island or bus bay: the bus stops on a lane of the same road next to the island, not on
        /// the traffic lane the stop is attached to. A car lane of the same road piece running the same way and clearly
        /// closer to the stop object (more than 1.5 m) is that bay.
        /// </summary>
        private bool TryBayLane(Entity stopEntity, Entity endLane, float3 lanePoint, float3 laneDirection, out float3 position, out float3 direction)
        {
            position = default;
            direction = default;
            if (stopEntity == Entity.Null || !EntityManager.TryGetComponent(stopEntity, out ObjectTransform stopTransform) ||
                !EntityManager.TryGetComponent(endLane, out Owner owner) || !EntityManager.TryGetBuffer(owner.m_Owner, true, out DynamicBuffer<Game.Net.SubLane> lanes))
                return false;

            float2 stopXZ = stopTransform.m_Position.xz;
            float2 laneFlat = math.normalizesafe(laneDirection.xz);
            float bestDistance = math.distance(lanePoint.xz, stopXZ) - 1.5f;
            bool found = false;
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (lane == endLane || !EntityManager.HasComponent<Game.Net.CarLane>(lane) || EntityManager.HasComponent<Game.Net.ConnectionLane>(lane) ||
                    EntityManager.HasComponent<Game.Net.ParkingLane>(lane) || !EntityManager.TryGetComponent(lane, out Curve curve) || curve.m_Length < 5f)
                    continue;

                MathUtils.Distance(curve.m_Bezier.xz, stopXZ, out float t);
                float3 point = MathUtils.Position(curve.m_Bezier, t);
                float2 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, t).xz);
                if (math.dot(tangent, laneFlat) < 0.7f || math.abs(point.y - lanePoint.y) > 2f)
                    continue;

                float distance = math.distance(point.xz, stopXZ);
                if (distance >= bestDistance)
                    continue;

                // Same stop line along the road as the end lane (projected across), on the bay lane.
                float along = math.dot(lanePoint.xz - point.xz, tangent);
                MathUtils.Distance(curve.m_Bezier.xz, point.xz + tangent * along, out float tStop);
                bestDistance = distance;
                position = MathUtils.Position(curve.m_Bezier, tStop);
                direction = new float3(tangent.x, 0f, tangent.y);
                found = true;
            }

            return found;
        }

        /// <summary>
        /// The painted stop area of a stop asset: a decal sub-object lying flat on the road (several metres long, about a
        /// lane wide). Its front end along the road is the stop line; with a known lane point the decal nearest to it wins.
        /// </summary>
        private bool TryPaintedBay(Entity stopEntity, float3 near, float3 laneDirection, bool hasNear, out float3 front, out float3 direction)
        {
            front = default;
            direction = default;
            if (stopEntity == Entity.Null || !EntityManager.TryGetBuffer(stopEntity, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
                return false;

            float best = float.MaxValue;
            for (int i = 0; i < subObjects.Length; i++)
            {
                Entity sub = subObjects[i].m_SubObject;
                if (!EntityManager.TryGetComponent(sub, out ObjectTransform transform) || !EntityManager.TryGetComponent(sub, out PrefabRef prefabRef) ||
                    !EntityManager.TryGetBuffer(prefabRef.m_Prefab, true, out DynamicBuffer<Game.Prefabs.SubMesh> meshes))
                    continue;

                for (int m = 0; m < meshes.Length; m++)
                {
                    if (!EntityManager.TryGetComponent(meshes[m].m_SubMesh, out MeshData mesh) || (mesh.m_State & MeshFlags.Decal) == 0)
                        continue;

                    float3 size = MathUtils.Size(mesh.m_Bounds);
                    float length = math.max(size.x, size.z);
                    float width = math.min(size.x, size.z);
                    if (length < 6f || length > 40f || width < 2f || width > 5f)
                        continue;

                    // The decal's long axis in the world; it points the way the bus drives when it agrees with the road.
                    quaternion rotation = math.mul(transform.m_Rotation, meshes[m].m_Rotation);
                    float3 axis = math.mul(rotation, size.z >= size.x ? new float3(0f, 0f, 1f) : new float3(1f, 0f, 0f));
                    float3 center = transform.m_Position + math.mul(transform.m_Rotation, meshes[m].m_Position + MathUtils.Center(mesh.m_Bounds));
                    float2 flat = math.normalizesafe(axis.xz);
                    if (math.lengthsq(flat) < 0.5f)
                        continue;

                    float2 laneFlat = math.normalizesafe(laneDirection.xz);
                    if (math.lengthsq(laneFlat) > 0.5f)
                    {
                        // The lane the bus stops on runs the way the bus drives.
                        if (math.dot(flat, laneFlat) < 0f)
                            flat = -flat;
                    }
                    else if (EntityManager.TryGetComponent(stopEntity, out Attached attached) && EntityManager.TryGetComponent(attached.m_Parent, out Curve road) &&
                        EntityManager.TryGetComponent(attached.m_Parent, out Game.Net.Edge _))
                    {
                        // Bus traffic on the stop's side drives with the kerb on its right: the lane's travel direction.
                        float2 tangent = math.normalizesafe(MathUtils.Tangent(road.m_Bezier, math.saturate(attached.m_CurvePosition)).xz);
                        float2 toStop = transform.m_Position.xz - MathUtils.Position(road.m_Bezier, math.saturate(attached.m_CurvePosition)).xz;
                        float side = tangent.x * toStop.y - tangent.y * toStop.x;
                        float2 travel = side < 0f ? tangent : -tangent;
                        if (math.dot(flat, travel) < 0f)
                            flat = -flat;
                    }

                    float distance = hasNear ? math.distance(center.xz, near.xz) : 0f;
                    if (hasNear && distance > 25f)
                        continue;

                    if (distance >= best)
                        continue;

                    best = distance;
                    direction = new float3(flat.x, 0f, flat.y);
                    front = center + direction * (length * 0.5f);
                    front.y = center.y;
                }
            }

            return best < float.MaxValue;
        }

        /// <summary>For the log: what kind of lane the stop uses and who owns it (road edge or the stop object's own bay).</summary>
        private string DescribeLane(Entity lane)
        {
            if (lane == Entity.Null || !EntityManager.Exists(lane))
                return "none";

            string kind = EntityManager.HasComponent<Game.Net.ConnectionLane>(lane) ? "connection" :
                EntityManager.HasComponent<Game.Net.CarLane>(lane) ? "car" :
                EntityManager.HasComponent<Game.Net.PedestrianLane>(lane) ? "pedestrian" : "other";
            string owner = "";
            if (EntityManager.TryGetComponent(lane, out Owner laneOwner))
            {
                owner = EntityManager.HasComponent<Edge>(laneOwner.m_Owner) ? " of road" :
                    EntityManager.HasComponent<Game.Routes.TransportStop>(laneOwner.m_Owner) ? " of stop" : " of " + laneOwner.m_Owner;
            }

            return $"{kind}{owner} {lane.Index}";
        }

        private struct SeenStop
        {
            internal float3 Front;
            internal float3 Forward;
        }

        // Stop lines seen from the game's own buses boarding there, kept for the session (waypoint -> bus front).
        private static readonly Dictionary<Entity, SeenStop> s_SeenStops = new Dictionary<Entity, SeenStop>();
        private uint m_NextSeenFrame;

        /// <summary>
        /// Every half second: a bus of this line boarding at one of its stops shows exactly where buses stop there (its
        /// front and heading). That replaces the computed stop line, so the box is right for any stop, bay or road mod.
        /// </summary>
        private void LearnFromBoardingBuses()
        {
            uint frame = Frame;
            if (frame < m_NextSeenFrame)
                return;

            m_NextSeenFrame = frame + 30U;
            if (!EntityManager.TryGetBuffer(BusState.Line, true, out DynamicBuffer<RouteVehicle> vehicles))
                return;

            for (int v = 0; v < vehicles.Length; v++)
            {
                Entity other = vehicles[v].m_Vehicle;
                if (other == BusState.Bus || !EntityManager.TryGetComponent(other, out Game.Vehicles.PublicTransport transport) ||
                    (transport.m_State & PublicTransportFlags.Boarding) == 0 || !EntityManager.TryGetComponent(other, out Target target))
                    continue;

                int index = -1;
                for (int i = 0; i < BusState.Stops.Count; i++)
                {
                    if (BusState.Stops[i].Waypoint == target.m_Target)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0 || !EntityManager.TryGetComponent(other, out ObjectTransform transform))
                    continue;

                float3 forward = math.forward(transform.m_Rotation);
                forward.y = 0f;
                forward = math.normalizesafe(forward, new float3(0f, 0f, 1f));
                float front = 6f;
                if (EntityManager.TryGetComponent(other, out PrefabRef prefabRef) && EntityManager.TryGetComponent(prefabRef.m_Prefab, out ObjectGeometryData geometry))
                    front = geometry.m_Bounds.max.z;
                float3 frontPoint = transform.m_Position + forward * front;
                StopInfo stop = BusState.Stops[index];
                if (s_SeenStops.TryGetValue(stop.Waypoint, out SeenStop known) && math.distance(known.Front.xz, frontPoint.xz) < 0.5f)
                    continue;

                float moved = stop.Valid ? math.distance(stop.Position.xz, frontPoint.xz) : 0f;
                if (stop.Valid && moved > 30f)
                    continue;

                s_SeenStops[stop.Waypoint] = new SeenStop { Front = frontPoint, Forward = forward };
                SetStopBox(ref stop, frontPoint, forward, false);
                BusState.Stops[index] = stop;
                if (index == m_GuideIndex)
                    m_GuideIndex = -1;
                Mod.log.Info($"Bus stop {stop.Name}: stop line taken from a boarding bus of the line ({moved:0.0} m from the computed one).");
            }
        }

        /// <summary>
        /// Where a route segment's path stops the bus (the stop line) and its direction there: the point on the last car
        /// lane of the path (a bus bay or the kerb lane, lane centre). The drawn route line itself runs on to the stop
        /// object on the pavement, so its end is only the fallback.
        /// </summary>
        private bool PathEndPoint(Entity segment, out float3 end, out float3 direction)
        {
            end = default;
            direction = new float3(0f, 0f, 1f);
            if (EntityManager.TryGetBuffer(segment, true, out DynamicBuffer<PathElement> path))
            {
                for (int i = path.Length - 1; i >= 0; i--)
                {
                    PathElement element = path[i];
                    Entity lane = element.m_Target;
                    if (lane == Entity.Null || !EntityManager.HasComponent<Game.Net.CarLane>(lane) ||
                        EntityManager.HasComponent<Game.Net.ConnectionLane>(lane) ||
                        !EntityManager.TryGetComponent(lane, out Curve laneCurve) || laneCurve.m_Length < 0.5f)
                        continue;

                    float t = math.saturate(element.m_TargetDelta.y);
                    float3 tangent = MathUtils.Tangent(laneCurve.m_Bezier, t);
                    if (element.m_TargetDelta.y < element.m_TargetDelta.x)
                        tangent = -tangent;
                    end = MathUtils.Position(laneCurve.m_Bezier, t);
                    direction = math.normalizesafe(tangent, direction);
                    return true;
                }
            }

            if (!EntityManager.TryGetBuffer(segment, true, out DynamicBuffer<CurveElement> curves) || curves.Length == 0)
                return false;

            Bezier4x3 last = curves[curves.Length - 1].m_Curve;
            end = last.d;
            direction = math.normalizesafe(MathUtils.Tangent(last, 1f), direction);
            return true;
        }

        /// <summary>
        /// The line's own path from the previous stop to the target stop (drawn on the road ahead of the bus), through any
        /// plain route waypoints in between. It ends at the stop line on the lane; the route line's last bit onto the
        /// pavement (to the stop object) is cut off.
        /// </summary>
        private void BuildGuide(int targetIndex)
        {
            BusState.GuideCurves.Clear();
            BusState.GuideLengths.Clear();
            BusState.GuideBounds.Clear();
            BusState.GuideTotal = 0f;
            s_GuideHint = 0;
            if (!EntityManager.TryGetBuffer(BusState.Line, true, out DynamicBuffer<RouteSegment> segments) || segments.Length == 0)
                return;

            // Segment k runs from waypoint k to waypoint k + 1: take those from the previous real stop on.
            int count = segments.Length;
            int last = ((targetIndex - 1) % count + count) % count;
            int first = last;
            if (count == BusState.Stops.Count)
            {
                for (int k = 0; k < count - 1 && !BusState.Stops[first].IsStop; k++)
                    first = (first - 1 + count) % count;
            }

            for (int k = first; k != last; k = (k + 1) % count)
            {
                if (!EntityManager.TryGetBuffer(segments[k].m_Segment, true, out DynamicBuffer<CurveElement> through))
                    continue;

                for (int i = 0; i < through.Length; i++)
                    AddGuideCurve(through[i].m_Curve);
            }

            Entity segment = segments[last].m_Segment;
            if (!EntityManager.TryGetBuffer(segment, true, out DynamicBuffer<CurveElement> curves))
                return;

            // The piece of the route line closest to the stop line, searched from the end.
            int cutIndex = curves.Length;
            float cutT = 1f;
            if (targetIndex >= 0 && targetIndex < BusState.Stops.Count && BusState.Stops[targetIndex].Valid)
            {
                float2 stopLine = BusState.Stops[targetIndex].Position.xz;
                float best = 4f;
                for (int i = curves.Length - 1; i >= 0 && i >= curves.Length - 6; i--)
                {
                    float distance = MathUtils.Distance(curves[i].m_Curve.xz, stopLine, out float t);
                    if (distance < best)
                    {
                        best = distance;
                        cutIndex = i;
                        cutT = t;
                    }
                }
            }

            for (int i = 0; i < curves.Length && i <= cutIndex; i++)
            {
                Bezier4x3 curve = curves[i].m_Curve;
                if (i == cutIndex)
                {
                    if (cutT < 0.01f)
                        break;
                    curve = MathUtils.Cut(curve, new float2(0f, cutT));
                }

                AddGuideCurve(curve);
            }
        }

        private static void AddGuideCurve(Bezier4x3 curve)
        {
            float length = MathUtils.Length(curve);
            BusState.GuideCurves.Add(curve);
            BusState.GuideLengths.Add(length);
            float2 lo = math.min(math.min(curve.a.xz, curve.b.xz), math.min(curve.c.xz, curve.d.xz));
            float2 hi = math.max(math.max(curve.a.xz, curve.b.xz), math.max(curve.c.xz, curve.d.xz));
            BusState.GuideBounds.Add(new float4(lo, hi));
            BusState.GuideTotal += length;
        }

        // ------------------------------------------------------------------ end

        private void ResetStopTracking()
        {
            m_LeftStopIndex = -1;
            m_LeftStopFrame = 0U;
            m_ServedIndex = -1;
            m_LastPassed = -1;
            m_LastPassedSkipped = false;
            m_NextDemandFrame = 0U;
            m_DepotStop = -1;
        }

        private void SetHints()
        {
            // The keyboard keys, and the pad's controls for the rows when the player uses a pad (the driving mod names them for the
            // pad in hand and renames them when the pad changes; a row without a pad button is left out in pad mode).
            Core.SetKeyHints(new List<(string, string, string, string, bool)>
            {
                (BusInput.DisplayKeyboardKey(BusInput.kDoors), BusInput.PadControls(BusInput.kDoors), BusLocale.kPrefix + "Hint.Doors", null, true),
                (BusInput.Pair(BusInput.DisplayKeyboardKey(BusInput.kIndicateLeft), BusInput.DisplayKeyboardKey(BusInput.kIndicateRight)),
                    BusInput.Pair(BusInput.PadControls(BusInput.kIndicateLeft), BusInput.PadControls(BusInput.kIndicateRight)),
                    BusLocale.kPrefix + "Hint.Indicators", null, false),
                (BusInput.DisplayKeyboardKey(BusInput.kHazard), BusInput.HazardPadControls(), BusLocale.kPrefix + "Hint.Hazard", null, false),
                (BusInput.DisplayKeyboardKey(BusInput.kLights), BusInput.PadControls(BusInput.kLights), BusLocale.kPrefix + "Hint.Lights", null, false),
                (BusInput.DisplayKeyboardKey(BusInput.kFlash), BusInput.PadControls(BusInput.kFlash), BusLocale.kPrefix + "Hint.Flash", null, false),
                (BusInput.DisplayKeyboardKey(BusInput.kImmersive), BusInput.PadControls(BusInput.kImmersive), BusLocale.kPrefix + "Hint.Immersive", null, false),
                (BusInput.DisplayKeyboardKey(BusInput.kPanel), BusInput.PadControls(BusInput.kPanel), BusLocale.kPrefix + "Hint.Panel", null, false)
            });
        }

        internal void EndShift(string message)
        {
            Entity bus = BusState.Bus != Entity.Null ? BusState.Bus : m_PendingBus;
            try
            {
                if (bus != Entity.Null && EntityManager.Exists(bus) && BusState.DoorsOpen)
                {
                    SetDeparture(bus, BusShiftRules.ForcedDeparture(Frame));
                    if (EntityManager.TryGetComponent(bus, out CarCurrentLane lane))
                    {
                        lane.m_LaneFlags &= ~(CarLaneFlags.EndOfPath | CarLaneFlags.EndReached);
                        EntityManager.SetComponentData(bus, lane);
                    }
                }

                if (Core.Available)
                {
                    Core.ClearAll();
                    if (BusState.Bus != Entity.Null && Core.IsDriving() && Core.Vehicle() == BusState.Bus)
                        Core.Release();
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Bus shift cleanup: {ex.GetType().Name}: {ex.Message}");
            }

            if (BusState.State != ShiftState.Idle)
                Mod.log.Info($"Bus shift ended: {BusState.Served} stops, {BusState.Skipped} skipped, {BusState.Missed} missed, {BusState.Boarded} boarded, {BusState.Alighted} alighted, fares {BusState.Fares}.");

            BusSummary.Capture();
            BusState.Reset();
            m_PendingBus = Entity.Null;
            m_OnBoard.Clear();
            m_KnownVehicles.Clear();
            m_GuideIndex = -1;
            ResetStopTracking();
            if (message != null)
                BusState.SetMessage(message);
        }
    }

    /// <summary>What the last shift achieved, shown in the panel after it ends.</summary>
    internal static class BusSummary
    {
        internal static bool Has;
        internal static string LineName = "";
        internal static int Served;
        internal static int Boarded;
        internal static int Alighted;
        internal static int Skipped;
        internal static int Missed;
        internal static long Fares;

        internal static void Capture()
        {
            if (BusState.Line == Entity.Null || (BusState.Served == 0 && BusState.Boarded == 0 && BusState.Alighted == 0))
                return;

            Has = true;
            LineName = BusState.LineName;
            Served = BusState.Served;
            Boarded = BusState.Boarded;
            Alighted = BusState.Alighted;
            Skipped = BusState.Skipped;
            Missed = BusState.Missed;
            Fares = BusState.Fares;
        }
    }
}
