using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The passenger mode's bus: picks a bus of the line the player chose (the one with the most passengers, of equal loads the nearest to the
    /// camera; or the next one of the line), lets CSL Driving Life Core put the camera in it (API 17: the game keeps driving the bus), and keeps
    /// what the bus model and the panel show of the ride (BusRide): the line, the people aboard and whether the bus is boarding. The ride ends by
    /// the player's request, when the bus is gone, or when the player takes a wheel (the driving mod's own key; a shift may start on that bus).
    /// </summary>
    internal sealed partial class BusRideSystem : GameSystemBase
    {
        private const float kSampleSeconds = 0.25f;

        private NameSystem m_NameSystem;
        private float m_NextSample;
        private readonly List<Entity> m_Candidates = new List<Entity>();
        private readonly List<Entity> m_Sections = new List<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
        }

        protected override void OnDestroy()
        {
            if (BusRide.Riding)
                End(null, true);
            base.OnDestroy();
        }

        /// <summary>Another save or the main menu: the bus belongs to the old city (the driving mod lets its ride go itself).</summary>
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            BusRide.Clear();
            BusRide.Request = RideRequest.None;
        }

        protected override void OnUpdate()
        {
            HandleRequest();
            if (!BusRide.Riding)
                return;

            Entity bus = BusRide.Bus;
            if (!Core.IsRiding() || Core.RiddenVehicle() != bus)
            {
                // The player took a wheel (silently: the shift system may start on that bus), or the driving mod let the ride go.
                End(Core.IsDriving() ? null : BusExists(bus) ? "Msg.RideEnded" : "Msg.RideBusLost", false);
                return;
            }

            if (!BusExists(bus))
            {
                End("Msg.RideBusLost", true);
                return;
            }

            float now = UnityEngine.Time.unscaledTime;
            if (now < m_NextSample && now > m_NextSample - 2f)
                return;
            m_NextSample = now + kSampleSeconds;
            Sample(bus);
        }

        // ------------------------------------------------------------------ requests

        private void HandleRequest()
        {
            RideRequest request = BusRide.Request;
            BusRide.Request = RideRequest.None;
            switch (request)
            {
                case RideRequest.Ride:
                    Start(BusRide.RequestedLine, false);
                    break;
                case RideRequest.Next:
                    if (BusRide.Riding)
                        Start(BusRide.Line, true);
                    break;
                case RideRequest.Leave:
                    if (BusRide.Riding)
                        End("Msg.RideEnded", true);
                    break;
            }
        }

        /// <summary>Rides a bus of <paramref name="line"/>: the best one, or with <paramref name="next"/> the one after the bus ridden now.</summary>
        private void Start(Entity line, bool next)
        {
            if (!Core.HasApi17)
            {
                BusState.SetMessage("Msg.RideNeedsCore");
                return;
            }

            if (Core.IsDriving() || BusState.State != ShiftState.Idle)
            {
                BusState.SetMessage("Msg.RideWhileDriving");
                return;
            }

            if (!IsBusLine(line))
            {
                BusState.SetMessage("Msg.NoLine");
                return;
            }

            Entity bus = Choose(line, next);
            if (bus == Entity.Null)
            {
                BusState.SetMessage("Msg.NoBusOnLine");
                return;
            }

            if (bus == BusRide.Bus)
            {
                BusState.SetMessage("Msg.RideOnlyBus");
                return;
            }

            if (!Core.Ride(bus))
            {
                BusState.SetMessage("Msg.RideRefused");
                return;
            }

            BusRide.Bus = bus;
            ReadLine(line);
            BusRide.DoorsOpen = false;
            BusRide.NeedsSeat = true;
            m_NextSample = 0f;
            Sample(bus);
            BusState.SetMessage("Msg.RideStarted", BusRide.LineName);
            Mod.log.Info($"Passenger mode: riding bus {bus} of line {line} ({BusRide.LineName}), {BusRide.Passengers} aboard.");
        }

        private void End(string message, bool stopCore)
        {
            Entity bus = BusRide.Bus;
            if (stopCore)
                Core.StopRiding();
            BusRide.Clear();
            ForgetRoute();
            if (message != null)
                BusState.SetMessage(message);
            Mod.log.Info($"Passenger mode: the ride on bus {bus} ended ({message ?? "the player took a wheel"}).");
        }

        // ------------------------------------------------------------------ the bus

        private bool BusExists(Entity bus) => EntityManager.Exists(bus) && !EntityManager.HasComponent<Deleted>(bus);

        private bool IsBusLine(Entity line)
        {
            return line != Entity.Null && EntityManager.Exists(line) && EntityManager.HasComponent<TransportLine>(line) &&
                EntityManager.TryGetComponent(line, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out TransportLineData data) &&
                data.m_TransportType == TransportType.Bus && data.m_PassengerTransport;
        }

        /// <summary>Out on the road: not in a depot, not parked, not being made.</summary>
        private bool IsOnRoad(Entity vehicle)
        {
            return vehicle != Entity.Null && EntityManager.Exists(vehicle) &&
                !EntityManager.HasComponent<Deleted>(vehicle) && !EntityManager.HasComponent<Unspawned>(vehicle) &&
                !EntityManager.HasComponent<TripSource>(vehicle) && !EntityManager.HasComponent<ParkedCar>(vehicle) &&
                EntityManager.HasComponent<Moving>(vehicle) && EntityManager.HasComponent<Car>(vehicle) &&
                EntityManager.TryGetComponent(vehicle, out CarCurrentLane lane) && lane.m_Lane != Entity.Null;
        }

        private Entity Choose(Entity line, bool next)
        {
            if (!EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteVehicle> vehicles))
                return Entity.Null;

            m_Candidates.Clear();
            for (int i = 0; i < vehicles.Length; i++)
                if (IsOnRoad(vehicles[i].m_Vehicle))
                    m_Candidates.Add(vehicles[i].m_Vehicle);
            if (m_Candidates.Count == 0)
                return Entity.Null;

            if (next)
            {
                var keys = new int[m_Candidates.Count];
                for (int i = 0; i < keys.Length; i++)
                    keys[i] = m_Candidates[i].Index;
                int chosen = BusRideRules.NextByKey(keys, BusRide.Bus.Index);
                return chosen >= 0 ? m_Candidates[chosen] : Entity.Null;
            }

            UnityEngine.Camera camera = UnityEngine.Camera.main;
            float3 reference = camera != null ? (float3)camera.transform.position : float3.zero;
            Entity best = Entity.Null;
            int bestPassengers = -1;
            float bestDistance = float.MaxValue;
            foreach (Entity vehicle in m_Candidates)
            {
                int passengers = EntityManager.TryGetBuffer(vehicle, true, out DynamicBuffer<Passenger> aboard) ? aboard.Length : 0;
                float distance = EntityManager.TryGetComponent(vehicle, out Game.Objects.Transform transform) ? math.distancesq(transform.m_Position, reference) : float.MaxValue;
                if (best == Entity.Null || BusRideRules.Better(passengers, distance, bestPassengers, bestDistance))
                {
                    best = vehicle;
                    bestPassengers = passengers;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>The line's name, label, colour (as a shift reads them) for the panel and the livery.</summary>
        private void ReadLine(Entity line)
        {
            BusRide.Line = line;
            BusRide.LineName = EntityNames.Text(m_NameSystem, line);
            LineLabel.Parse(BusRide.LineName, EntityManager.TryGetComponent(line, out RouteNumber number) ? number.m_Number : 0, EntityNames.PlayerNamed(m_NameSystem, line),
                out BusRide.LinePrefix, out BusRide.LineNumber);
            if (EntityManager.TryGetComponent(line, out Game.Routes.Color color))
                BusRide.LineColor = color.m_Color;
        }

        /// <summary>Four times a second: the bus's line, the people aboard and its capacity, and whether it is boarding at a stop.</summary>
        private void Sample(Entity bus)
        {
            bool boarding = EntityManager.TryGetComponent(bus, out Game.Vehicles.PublicTransport transport) && (transport.m_State & PublicTransportFlags.Boarding) != 0;
            BusRide.DoorsOpen = BusRideRules.DoorsOpen(boarding, Core.SpeedMps() * Core.TimeScale());

            // an articulated bus keeps its riders in every section's own buffer; the capacity is the sections' sum too
            m_Sections.Clear();
            m_Sections.Add(bus);
            if (EntityManager.TryGetBuffer(bus, true, out DynamicBuffer<LayoutElement> layout))
                for (int i = 0; i < layout.Length; i++)
                    if (layout[i].m_Vehicle != bus && layout[i].m_Vehicle != Entity.Null)
                        m_Sections.Add(layout[i].m_Vehicle);

            int aboard = 0, capacity = 0;
            foreach (Entity section in m_Sections)
            {
                if (EntityManager.TryGetBuffer(section, true, out DynamicBuffer<Passenger> passengers))
                    for (int p = 0; p < passengers.Length; p++)
                        if (!EntityManager.HasComponent<Game.Creatures.Pet>(passengers[p].m_Passenger))
                            aboard++;
                if (EntityManager.TryGetComponent(section, out PrefabRef prefabRef) && EntityManager.TryGetComponent(prefabRef.m_Prefab, out PublicTransportVehicleData data))
                    capacity += data.m_PassengerCapacity;
            }

            BusRide.Passengers = aboard;
            BusRide.Capacity = capacity;

            // the bus may be sent to another line
            if (EntityManager.TryGetComponent(bus, out CurrentRoute route) && route.m_Route != BusRide.Line && IsBusLine(route.m_Route))
                ReadLine(route.m_Route);
            UpdateRoute(bus);
        }
    }
}
