using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using ObjectTransform = Game.Objects.Transform;
using RouteColor = Game.Routes.Color;
using SubObject = Game.Objects.SubObject;
using LedPanel = CDLC.Asset.Led.LedPanel;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Live departure boards on the LED screens of station packs (the owner, 21 October 2026, on the bus interchange's blank glowing board: "你这电子
    /// 灯牌没用啊", then "实时信息，由 Bus Driver 绘制"). A station pack's building (a copy of one of the game's bus stations) has dark screen faces and a
    /// &lt;prefab&gt;.cdls.json beside its .cok (StationSignPacks): its bays' stops and its screens' places. Within the boards' distance of the camera,
    /// once a second, each bay's stop is found among the building's own (the game's stops of its "Integrated Bus Stop" markers), its lines read from
    /// the stop (ConnectedRoute: the lines' waypoints there), each line's label (as the buses' signs: LineLabel), colour, terminus, the people waiting
    /// for it there (WaitingPassengers on its waypoint) and its nearest bus (StationBoardRules: boarding here, arriving, n stops away). The screens draw
    /// it (StationBoardComposer, the high-density signs' faces and materials, dimmed at night as the signs); a scrolling text moves every frame, a
    /// board is written again only when its page or content changed. Nothing is drawn for other stations; no station is named here.
    /// <b>Not yet seen in the game.</b>
    /// </summary>
    public sealed partial class BusStationBoardSystem : GameSystemBase
    {
        // the camera within this of a station's building: its screens made and kept; a screen drawn within its layout's distance and from its front
        private const float kStationMeters = 320f, kBayMeters = 160f, kPylonMeters = 70f, kDepartureMeters = 220f, kHysteresis = 1.15f;
        private const float kScanSeconds = 1f, kPageSeconds = 6f, kDotsPerSecond = 20f, kKeepSeconds = 20f;
        // the dots stand this far in front of the screen face (the model's dark face)
        private const float kDotLift = 0.004f;
        private static readonly float3 s_Green = new float3(0.2f, 1f, 0.35f);

        private sealed class Screen
        {
            internal StationSignProfile.Screen Def;
            internal LedPanel Panel;
            internal StationBoardComposer Composer;
            internal Vector3 World, Normal;
            internal readonly List<BoardBay> Bays = new List<BoardBay>(8);
            // the content's key (made each scan), the content and page drawn, the pages it needs
            internal string ContentKey, Key;
            internal int Page = -1, Pages = 1, Offset = -1;
            internal double Start;
            internal bool Shown;
            internal readonly Material[] Inks = new Material[BoardInk.Count];
        }

        private sealed class Station
        {
            internal Entity Building;
            internal StationSignProfile Profile;
            internal GameObject Root;
            internal readonly List<Screen> Screens = new List<Screen>();
            internal readonly Dictionary<int, Entity> StopOfBay = new Dictionary<int, Entity>();
            internal readonly Dictionary<int, BoardBay> Bays = new Dictionary<int, BoardBay>();
            internal float SeenAt;
            internal bool Active;
            internal bool Retiring;
        }

        private struct LineInfo
        {
            internal string Label, Suffix, Terminus, Origin;
            internal float3 Colour;
            internal readonly List<bool> IsStop;
            internal LineInfo(List<bool> isStop) : this() { IsStop = isStop; }
        }

        private readonly Dictionary<Entity, Station> m_Stations = new Dictionary<Entity, Station>();
        private readonly Dictionary<Entity, StationSignProfile> m_ProfileByPrefab = new Dictionary<Entity, StationSignProfile>();
        private readonly Dictionary<Entity, LineInfo> m_Lines = new Dictionary<Entity, LineInfo>();
        private readonly HashSet<Entity> m_Keep = new HashSet<Entity>();
        private readonly List<Entity> m_Remove = new List<Entity>();
        private readonly Dictionary<string, float3> m_LabelColour = new Dictionary<string, float3>(StringComparer.Ordinal);
        private readonly HashSet<Entity> m_BayLines = new HashSet<Entity>();

        private EntityQuery m_Query;
        private PrefabSystem m_Prefabs;
        private NameSystem m_Names;
        private CameraUpdateSystem m_Cameras;
        private LightingSystem m_Lighting;
        private float m_ScanAt;
        private int m_LogCooldown;
        private string m_TextsKey;
        private BoardTexts m_Texts;

        private IDisposable m_MaterialResources;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Names = World.GetOrCreateSystemManaged<NameSystem>();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Game.Buildings.TransportStation>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<ObjectTransform>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_MaterialResources = CabMaterials.AcquireResources();
        }

        protected override void OnUpdate()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                if (m_LogCooldown-- <= 0)
                {
                    Mod.log.Warn($"Station boards update failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    m_LogCooldown = 600;
                }

                foreach (Station s in m_Stations.Values)
                    Show(s, false);
            }
        }

        private void Step()
        {
            RetryRetiring();
            BusSettings settings = Mod.Settings;
            if (settings == null || !settings.StationBoards)
            {
                if (m_Stations.Count != 0)
                    ReleaseAll();
                return;
            }

            HdFontSet fonts = HdFonts.Get();
            if (fonts == null)
                return;   // the faces still loading (or failed: logged by HdFonts)
            if (m_Cameras == null)
                m_Cameras = World.GetExistingSystemManaged<CameraUpdateSystem>();
            Camera camera = m_Cameras?.activeCamera ?? Camera.main;
            if (camera == null)
                return;
            if (m_Lighting == null)
                m_Lighting = World.GetExistingSystemManaged<LightingSystem>();
            if (m_Lighting != null)
                CabMaterials.SetDaylight(m_Lighting.dayLightBrightness);

            Vector3 eye = camera.transform.position;
            float now = UnityEngine.Time.unscaledTime;
            if (now >= m_ScanAt)
            {
                m_ScanAt = now + kScanSeconds;
                Scan(eye, now, fonts.Font);
            }

            double time = UnityEngine.Time.unscaledTimeAsDouble;
            int page = StationBoardRules.Page(time, kPageSeconds);
            foreach (Station station in m_Stations.Values)
            {
                if (!station.Active)
                    continue;
                foreach (Screen screen in station.Screens)
                    Draw(screen, eye, page, time);
            }
        }

        // ---------------------------------------------------------------- the scan: the stations near the camera and their bays

        private void Scan(Vector3 eye, float now, DotFont font)
        {
            Texts();
            m_Keep.Clear();
            m_Lines.Clear();
            using (NativeArray<Entity> buildings = m_Query.ToEntityArray(Allocator.Temp))
            using (NativeArray<PrefabRef> prefabs = m_Query.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (NativeArray<ObjectTransform> transforms = m_Query.ToComponentDataArray<ObjectTransform>(Allocator.Temp))
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    StationSignProfile profile = ProfileOf(prefabs[i].m_Prefab);
                    if (profile == null)
                        continue;
                    Entity building = buildings[i];
                    bool known = m_Stations.TryGetValue(building, out Station station) && station.Active;
                    float d = Vector3.Distance(transforms[i].m_Position, eye);
                    if (d > kStationMeters * (known ? kHysteresis : 1f))
                        continue;
                    m_Keep.Add(building);
                    if (station == null)
                    {
                        station = Build(building, profile, transforms[i], font);
                    }
                    if (station.Retiring) continue;

                    station.Root.transform.SetPositionAndRotation(transforms[i].m_Position, transforms[i].m_Rotation);
                    foreach (Screen screen in station.Screens)
                    {
                        screen.World = station.Root.transform.TransformPoint(new Vector3(screen.Def.Centre.x, screen.Def.Centre.y, screen.Def.Centre.z));
                        screen.Normal = station.Root.transform.TransformDirection(new Vector3(screen.Def.Normal.x, screen.Def.Normal.y, screen.Def.Normal.z));
                    }

                    station.Active = true;
                    station.SeenAt = now;
                    Show(station, true);
                    Read(station, transforms[i]);
                }
            }

            m_Remove.Clear();
            foreach (Station station in m_Stations.Values)
            {
                if (m_Keep.Contains(station.Building))
                    continue;
                if (station.Active)
                {
                    station.Active = false;
                    Show(station, false);
                }

                if (now - station.SeenAt > kKeepSeconds || !EntityManager.Exists(station.Building))
                    m_Remove.Add(station.Building);
            }

            var cleanupFailures = new List<Exception>();
            foreach (Entity e in m_Remove)
            {
                try { Release(m_Stations[e]); m_Stations.Remove(e); }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            CabCleanup.ThrowIfAny(cleanupFailures, "Expired station board cleanup failed.");
        }

        private StationSignProfile ProfileOf(Entity prefab)
        {
            if (m_ProfileByPrefab.TryGetValue(prefab, out StationSignProfile cached))
                return cached;
            StationSignProfile profile = m_Prefabs.TryGetPrefab(prefab, out PrefabBase prefabBase) && prefabBase != null ? StationSignPacks.ForPrefab(prefabBase) : null;
            m_ProfileByPrefab[prefab] = profile;
            return profile;
        }

        /// <summary>The game's stop of each bay: the building's own stops (its sub-objects with ConnectedRoute) nearest the bay's marker (in plan: a
        /// marker on the ground may be put on the terrain).</summary>
        private void FindStops(Station station, in ObjectTransform transform)
        {
            station.StopOfBay.Clear();
            if (!EntityManager.TryGetBuffer(station.Building, true, out DynamicBuffer<SubObject> subs))
                return;
            quaternion inverse = math.inverse(transform.m_Rotation);
            foreach (StationSignProfile.Stop stop in station.Profile.Stops)
            {
                Entity best = Entity.Null;
                float bestD = 3f;
                for (int i = 0; i < subs.Length; i++)
                {
                    Entity e = subs[i].m_SubObject;
                    if (!EntityManager.HasComponent<Game.Routes.TransportStop>(e) || !EntityManager.HasBuffer<ConnectedRoute>(e) ||
                        !EntityManager.TryGetComponent(e, out ObjectTransform t))
                        continue;
                    float3 local = math.mul(inverse, t.m_Position - transform.m_Position);
                    float d = math.distance(local.xz, stop.Position.xz);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = e;
                    }
                }

                if (best != Entity.Null)
                    station.StopOfBay[stop.Bay] = best;
            }
        }

        /// <summary>Each bay's lines and their nearest buses, into the screens' content.</summary>
        private void Read(Station station, in ObjectTransform transform)
        {
            bool stale = station.StopOfBay.Count < station.Profile.Stops.Length;
            foreach (Entity stop in station.StopOfBay.Values)
                stale |= !EntityManager.Exists(stop) || EntityManager.HasComponent<Deleted>(stop);
            if (stale)
                FindStops(station, transform);
            string building = EntityNames.Text(m_Names, station.Building) ?? "";
            m_LabelColour.Clear();

            foreach (StationSignProfile.Stop s in station.Profile.Stops)
            {
                if (!station.Bays.TryGetValue(s.Bay, out BoardBay bay))
                    station.Bays[s.Bay] = bay = new BoardBay { Bay = s.Bay };
                bay.Routes.Clear();
                bay.Waiting = 0;
                if (!station.StopOfBay.TryGetValue(s.Bay, out Entity stop) || !EntityManager.TryGetBuffer(stop, true, out DynamicBuffer<ConnectedRoute> routes))
                    continue;
                Entity boarding = EntityManager.TryGetComponent(stop, out BoardingVehicle bv) ? bv.m_Vehicle : Entity.Null;
                string here = EntityNames.Text(m_Names, stop) ?? building;
                m_BayLines.Clear();
                for (int i = 0; i < routes.Length; i++)
                {
                    Entity waypoint = routes[i].m_Waypoint;
                    if (!EntityManager.TryGetComponent(waypoint, out Owner owner) || !EntityManager.TryGetComponent(waypoint, out Waypoint wp))
                        continue;
                    Entity line = owner.m_Owner;
                    if (!PassengerLine(line) || !m_BayLines.Add(line) || !TryLine(line, out LineInfo info))
                        continue;   // (a line stopping twice at the bay: shown once)
                    var nearest = (status: BoardStatus.NoBus, stops: 0);
                    if (EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteVehicle> vehicles))
                        for (int v = 0; v < vehicles.Length; v++)
                            nearest = StationBoardRules.Nearer(nearest, BusStatus(vehicles[v].m_Vehicle, line, wp.m_Index, boarding, info.IsStop));
                    int waiting = EntityManager.TryGetComponent(waypoint, out WaitingPassengers w) ? Math.Max(0, w.m_Count) : 0;
                    bay.Waiting += waiting;
                    // the terminus; at the terminus itself the line's other end (where the bus goes from here)
                    bool atEnd = string.Equals(info.Terminus, here, StringComparison.Ordinal) || string.Equals(info.Terminus, building, StringComparison.Ordinal);
                    string to = atEnd && info.Origin.Length > 0 ? info.Origin : info.Terminus;
                    bay.Routes.Add(new BoardRoute
                    {
                        Label = info.Label, Suffix = info.Suffix, LineInk = BoardInk.Line0, Terminus = to, English = HdFonts.English(to),
                        Status = nearest.status, Stops = nearest.stops, Waiting = waiting,
                    });
                    if (!m_LabelColour.ContainsKey(info.Label))
                        m_LabelColour[info.Label] = info.Colour;
                }

                StationBoardRules.Sort(bay.Routes);
            }

            // the screens: their bays, each line an ink of its colour (the first twelve lines of a screen; more: the text's ink)
            foreach (Screen screen in station.Screens)
            {
                screen.Bays.Clear();
                var inks = new Dictionary<string, byte>(StringComparer.Ordinal);
                foreach (int b in screen.Def.Bays)
                {
                    if (!station.Bays.TryGetValue(b, out BoardBay bay))
                        continue;
                    var copy = new BoardBay { Bay = bay.Bay, Waiting = bay.Waiting };
                    foreach (BoardRoute r in bay.Routes)
                    {
                        BoardRoute route = r;
                        string key = r.Label;
                        if (!inks.TryGetValue(key, out byte ink))
                        {
                            ink = inks.Count < BoardInk.Lines ? (byte)(BoardInk.Line0 + inks.Count) : BoardInk.Text;
                            inks[key] = ink;
                            if (ink != BoardInk.Text)
                                SetLineInk(screen, ink, m_LabelColour.TryGetValue(key, out float3 c) ? c : AiSignRules.SignAmber);
                        }

                        route.LineInk = ink;
                        copy.Routes.Add(route);
                    }

                    screen.Bays.Add(copy);
                }

                screen.ContentKey = StationBoardRules.Key(screen.Bays) + m_TextsKey;
            }
        }

        private bool PassengerLine(Entity line) =>
            line != Entity.Null && EntityManager.Exists(line) && EntityManager.HasComponent<TransportLine>(line) &&
            EntityManager.TryGetComponent(line, out PrefabRef prefab) && EntityManager.TryGetComponent(prefab.m_Prefab, out TransportLineData data) && data.m_PassengerTransport;

        /// <summary>A line's label, colour, ends and which of its waypoints are at a stop (read once a scan).</summary>
        private bool TryLine(Entity line, out LineInfo info)
        {
            if (m_Lines.TryGetValue(line, out info))
                return true;
            if (!EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteWaypoint> waypoints))
                return false;
            info = new LineInfo(new List<bool>(waypoints.Length));
            string first = null, last = null;
            for (int i = 0; i < waypoints.Length; i++)
            {
                bool stop = EntityManager.TryGetComponent(waypoints[i].m_Waypoint, out Connected c) && c.m_Connected != Entity.Null &&
                    EntityManager.HasComponent<Game.Routes.TransportStop>(c.m_Connected);
                info.IsStop.Add(stop);
                if (!stop)
                    continue;
                string name = EntityNames.Text(m_Names, c.m_Connected) ?? "";
                first = first ?? name;
                last = name;
            }

            string lineName = EntityNames.Text(m_Names, line);
            LineLabel.Parse(lineName, EntityManager.TryGetComponent(line, out RouteNumber number) ? number.m_Number : 0, EntityNames.PlayerNamed(m_Names, line), out string prefix, out string block);
            info.Label = LineLabel.Text(prefix, block);
            info.Suffix = HdFonts.Suffix(block);
            // the terminus as the buses' signs show it (the timetable mod's when it names one, else the last stop), and the other end
            info.Terminus = TransitTimetablesLink.TryTerminus(EntityManager, line, out Entity end, out _) && end != Entity.Null ? EntityNames.Text(m_Names, end) ?? "" : last ?? "";
            info.Origin = string.Equals(first, info.Terminus, StringComparison.Ordinal) ? last ?? "" : first ?? "";
            info.Colour = EntityManager.TryGetComponent(line, out RouteColor colour)
                ? new float3(colour.m_Color.r / 255f, colour.m_Color.g / 255f, colour.m_Color.b / 255f) : AiSignRules.SignAmber;
            m_Lines[line] = info;
            return true;
        }

        /// <summary>One bus of the line: boarding at the bay, or the stops before it (from the waypoint it heads to); not on the line: none.</summary>
        private (BoardStatus, int) BusStatus(Entity vehicle, Entity line, int bayIndex, Entity boarding, List<bool> isStop)
        {
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) || !EntityManager.TryGetComponent(vehicle, out CurrentRoute route) || route.m_Route != line)
                return (BoardStatus.NoBus, 0);
            uint flags = EntityManager.TryGetComponent(vehicle, out Game.Vehicles.PublicTransport transport) ? (uint)transport.m_State : 0U;
            if ((flags & (uint)(PublicTransportFlags.Returning | PublicTransportFlags.AbandonRoute | PublicTransportFlags.Disabled)) != 0U ||
                EntityManager.HasComponent<ParkedCar>(vehicle))
                return (BoardStatus.NoBus, 0);
            bool here = boarding == vehicle && (flags & (uint)PublicTransportFlags.Boarding) != 0U;
            if (!EntityManager.TryGetComponent(vehicle, out Target target) || !EntityManager.TryGetComponent(target.m_Target, out Waypoint wp) ||
                !EntityManager.TryGetComponent(target.m_Target, out Owner owner) || owner.m_Owner != line)
                return here ? (BoardStatus.AtStop, 0) : (BoardStatus.NoBus, 0);
            return StationBoardRules.OfBus(here, StationBoardRules.StopsBetween(isStop, wp.m_Index, bayIndex));
        }

        // ---------------------------------------------------------------- the screens

        private Station Build(Entity building, StationSignProfile profile, in ObjectTransform transform, DotFont font)
        {
            var station = new Station { Building = building, Profile = profile };
            m_Stations.Add(building, station);
            try
            {
                station.Root = new GameObject("CDLC station boards");
                station.Root.hideFlags = HideFlags.HideAndDontSave;
                station.Root.transform.SetPositionAndRotation(transform.m_Position, transform.m_Rotation);
                Material amber = CabMaterials.DimmedLamp(AiSignRules.SignAmber, AiSignRules.SignNits, AiSignRules.SignNight);
                foreach (StationSignProfile.Screen def in profile.Screens)
                {
                    var screen = new Screen { Def = def };
                    screen.Inks[BoardInk.Text] = amber;
                    screen.Inks[BoardInk.Accent] = CabMaterials.DimmedLamp(AiSignRules.SignCyan, AiSignRules.SignNits, AiSignRules.SignNight);
                    screen.Inks[BoardInk.Good] = CabMaterials.DimmedLamp(s_Green, AiSignRules.SignNits, AiSignRules.SignNight);
                    screen.Inks[BoardInk.Dim] = CabMaterials.DimmedLamp(AiSignRules.SignAmber, AiSignRules.SignNits * 0.35f, AiSignRules.SignNight * 0.35f);
                    for (int i = BoardInk.Line0; i < BoardInk.Count; i++)
                        screen.Inks[i] = amber;
                    bool ok = true;
                    foreach (Material m in screen.Inks)
                        ok &= m != null;
                    if (!ok)
                        continue;
                    station.Screens.Add(screen);
                    float3 normal = def.Normal;
                    float3 centre = def.Centre + normal * kDotLift;
                    quaternion rotation = quaternion.LookRotationSafe(-normal, new float3(0f, 1f, 0f));   // the dots face the panel's -Z: the face's normal
                    screen.Panel = new LedPanel(station.Root.transform, centre, def.Size, def.Columns, def.Rows, screen.Inks, rotation, null, CabMaterials.AcquireResources());
                    screen.Composer = new StationBoardComposer(font, BusAiSignSystem.Font, def.Layout, def.Columns, def.Rows);
                    screen.Panel.Visible = false;
                }
                return station;
            }
            catch (Exception creationError)
            {
                try { Release(station); m_Stations.Remove(building); }
                catch (Exception cleanupError) { throw new AggregateException("Station board construction and cleanup failed.", creationError, cleanupError); }
                throw;
            }
        }

        private static void SetLineInk(Screen screen, byte ink, float3 srgb)
        {
            Material m = CabMaterials.DimmedLamp(StationBoardRules.LedColour(srgb), AiSignRules.SignNits, AiSignRules.SignNight);
            if (m == null || screen.Inks[ink] == m)
                return;
            screen.Inks[ink] = m;
            screen.Panel?.SetMaterial(ink, m);
        }

        private float DistanceOf(StationSignProfile.Layout layout) =>
            layout == StationSignProfile.Layout.Pylon ? kPylonMeters : layout == StationSignProfile.Layout.Departures ? kDepartureMeters : kBayMeters;

        /// <summary>A screen this frame: hidden behind its face or beyond its distance; else composed when its page or content changed, its scroll moved.</summary>
        private void Draw(Screen screen, Vector3 eye, int page, double time)
        {
            if (screen.Panel == null || !screen.Panel.Alive)
                return;
            Vector3 toEye = eye - screen.World;
            float limit = DistanceOf(screen.Def.Layout) * (screen.Shown ? kHysteresis : 1f);
            bool visible = toEye.sqrMagnitude < limit * limit && Vector3.Dot(toEye, screen.Normal) > 0f;
            if (!visible)
            {
                if (screen.Shown)
                {
                    screen.Panel.Visible = false;
                    screen.Shown = false;
                }

                return;
            }

            // (one page: composed again only when the content changed)
            int shown = screen.Pages > 1 ? page % screen.Pages : 0;
            if (screen.ContentKey != screen.Key || shown != screen.Page || !screen.Shown)
            {
                if (screen.ContentKey != screen.Key)
                    screen.Start = time;
                screen.Composer.Compose(screen.Bays, m_Texts, page);
                screen.Pages = Math.Max(1, screen.Composer.Pages);
                screen.Key = screen.ContentKey;
                screen.Page = screen.Pages > 1 ? page % screen.Pages : 0;
                screen.Offset = -1;
            }

            int offset = screen.Composer.Scrolls ? (int)((time - screen.Start) * kDotsPerSecond) % Math.Max(1, screen.Composer.Cycle) : 0;
            if (offset != screen.Offset)
            {
                screen.Offset = offset;
                screen.Composer.Render(offset);
                screen.Panel.Apply(screen.Composer.Buffer, screen.Composer.Ink);
            }

            if (!screen.Shown)
            {
                screen.Panel.Visible = true;
                screen.Shown = true;
            }
        }

        /// <summary>The words of the boards in the game's language (read again when they change).</summary>
        private void Texts()
        {
            var texts = new BoardTexts
            {
                AtStop = BusLocale.Text("Board.AtStop"), Arriving = BusLocale.Text("Board.Arriving"),
                StopsOne = BusLocale.Text("Board.StopsOne"), StopsMany = BusLocale.Text("Board.StopsMany"),
                NoBus = BusLocale.Text("Board.NoBus"), NoLines = BusLocale.Text("Board.NoLines"), NoLinesEnglish = BusLocale.English("Board.NoLines"),
                AtStopEnglish = BusLocale.English("Board.AtStop"), ArrivingEnglish = BusLocale.English("Board.Arriving"), NoBusEnglish = BusLocale.English("Board.NoBus"),
                HeadBay = BusLocale.Text("Board.HeadBay"), HeadLine = BusLocale.Text("Board.HeadLine"), HeadTo = BusLocale.Text("Board.HeadTo"),
                HeadWaiting = BusLocale.Text("Board.HeadWaiting"), HeadStatus = BusLocale.Text("Board.HeadStatus"),
            };
            if (!texts.StopsOne.Contains("{0}")) texts.StopsOne = "{0}";
            if (!texts.StopsMany.Contains("{0}")) texts.StopsMany = "{0}";
            m_Texts = texts;
            m_TextsKey = texts.Key();
        }

        private static void Show(Station station, bool show)
        {
            if (station.Root != null && station.Root.activeSelf != show)
                station.Root.SetActive(show);
        }

        private static void Release(Station station)
        {
            station.Retiring = true;
            station.Active = false;
            var failures = new List<Exception>();
            CabCleanup.Try(() => Show(station, false), failures);
            foreach (Screen screen in new List<Screen>(station.Screens))
            {
                try { screen.Panel?.Destroy(); screen.Panel = null; station.Screens.Remove(screen); }
                catch (Exception error) { failures.Add(error); }
            }
            if (station.Screens.Count == 0)
            {
                try { if (station.Root != null) UnityEngine.Object.Destroy(station.Root); station.Root = null; }
                catch (Exception error) { failures.Add(error); }
            }
            CabCleanup.ThrowIfAny(failures, "Station board cleanup failed.");
        }

        private void RetryRetiring()
        {
            var failures = new List<Exception>();
            foreach (Station station in new List<Station>(m_Stations.Values))
            {
                if (!station.Retiring) continue;
                try { Release(station); m_Stations.Remove(station.Building); }
                catch (Exception error) { failures.Add(error); }
            }
            CabCleanup.ThrowIfAny(failures, "Retiring station board cleanup failed.");
        }

        private void ReleaseAll()
        {
            var failures = new List<Exception>();
            foreach (Station station in m_Stations.Values) station.Retiring = true;
            CabCleanup.Try(RetryRetiring, failures);
            CabCleanup.ThrowIfAny(failures, "Station board collection cleanup failed.");
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            ReleaseAll();
            m_ProfileByPrefab.Clear();
            m_ScanAt = 0f;
        }

        protected override void OnStopRunning()
        {
            foreach (Station station in m_Stations.Values)
                Show(station, false);
            base.OnStopRunning();
        }

        protected override void OnDestroy()
        {
            try
            {
                ReleaseResources();
            }
            catch { CabCleanup.Retain(new RetainedCleanup(this)); throw; }
            finally { base.OnDestroy(); }
        }

        private void ReleaseResources()
        {
            ReleaseAll();
            m_MaterialResources?.Dispose();
            m_MaterialResources = null;
        }

        private sealed class RetainedCleanup : IDisposable
        {
            readonly BusStationBoardSystem m_Owner;
            internal RetainedCleanup(BusStationBoardSystem owner) { m_Owner = owner; }
            public void Dispose() => m_Owner.ReleaseResources();
        }
    }
}
