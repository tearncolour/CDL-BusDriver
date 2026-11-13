using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CDLC.Asset.Articulation;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using LedGeometry = CDLC.Asset.Led.LedGeometry;
using LedMesh = CDLC.Asset.Led.LedMesh;
using SignCamera = CDLC.Asset.Led.SignCamera;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The LED destination signs of our own buses on the AI buses of the city (the user, 18 October: "the AI buses' electronic signs,
    /// as an option the game can switch off to save performance"). Only buses whose model is ours carry the sign housings: the vehicle
    /// packs' buses (their profile's front, kerb-side and rear signs) and the built-in CDLCBus12 (the generic model's). Each sign shows
    /// what the driven bus's would (AiSignRules, LedComposer, the same font, grids, pitch, materials and dimming): the line's label and
    /// terminus, "Not in service" on the way back to the depot, nothing parked. The bus being driven or ridden is the cab system's.
    /// Cost, kept small (the evaluation of 18 October): the city's buses are looked at twice a second, only ours within the option's
    /// distance get signs; a sign's dots are one mesh per text holding only the lit dots, shared by every bus showing it (a line's buses
    /// share theirs), a scrolling one rewritten when its scroll moves (at most 20 times a second, once for all its buses); each frame
    /// only the signs' roots follow their buses. Options off: nothing is looked at.
    /// </summary>
    public sealed partial class BusAiSignSystem : GameSystemBase
    {
        // as BusCabSystem.Exterior: the signs' amber and brightness by day and by night (CabMaterials.DimmedLamp)
        private const float kSignNits = AiSignRules.SignNits, kSignNight = AiSignRules.SignNight;
        // board pictures drawn a frame (a new line's text: 1 to 3 ms each); the others show a blank board until their turn
        private const int kCardDrawsPerFrame = 2;
        private static readonly float3 s_LedAmber = new float3(1f, 0.62f, 0.12f);

        private const float kScanSeconds = 0.5f, kMinMovedScanSeconds = 0.1f;
        // a bus may move this far between its native frame and the interpolated pose (as CDL Asset's ArticulationSystem)
        private const float kPoseLagMeters = 60f;
        // a sign whose bus left the distance is hidden and kept this long for it, then its objects go to the pool
        private const float kKeepSeconds = 10f;
        private const float kLineTextSeconds = 5f, kUnusedMeshSeconds = 10f;
        private const int kPoolMax = 64;

        private enum Slot { Front, Side, Rear }

        private struct SignDef
        {
            internal Slot Slot;
            internal bool OnRearSection;
            internal float3 Centre;
            internal quaternion Rotation;
            internal float2 Size;
            internal int2 Matrix;
            // a printed board (the profile's style "card") and its colours
            internal bool Card;
            internal CardRgb Board, Ink;
            // a high-density sign (option "LED sign style": Shanghai; Matrix is then AiSignRules.HdMatrix)
            internal bool Hd;
            // a sign overlay's dark board behind the dots (SignOverlay)
            internal SignOverlay.Backing Backing;
        }

        // The signs of a bus model (per prefab): those of its front section and, for an articulated pack bus, the rear section's.
        private sealed class Model
        {
            internal readonly List<SignDef> Signs = new List<SignDef>(3);
            internal bool Articulated;
        }

        private sealed class SharedSign
        {
            internal string Key;
            internal LedComposer Composer;
            internal Mesh Main, Prefix;
            internal string PrefixText, Block, Message;
            internal bool Scrolls;
            internal int Cycle, Offset = -1, Users;
            internal float LastUsed;
            internal Material PrefixMaterial;
            // a printed board: its picture and material, drawn (CardPending) under a per-frame budget
            internal bool Card, CardPending;
            internal Texture2D CardTexture;
            internal Material CardMaterial;
            internal int CardWidth, CardHeight;
            internal CardRgb Board, Ink, PrefixInk;
            // a high-density sign: its composer (the content composed once), its inks' materials, and its solid cells (each lit dot its
            // whole cell, AiSignRules.DrawDots) with their inks; the dots' mesh is written only while a view draws the dots (DotsWanted,
            // this frame), at the scroll position MeshOffset
            internal HdComposer Hd;
            internal Material[] HdMaterials, CellMaterials;
            internal LedMesh HdDots, HdCells;
            internal int MeshOffset = -1;
            internal bool DotsWanted;
            internal float2 Size;
            internal bool Retiring;
        }

        private sealed class SignView
        {
            internal GameObject Main, Prefix, Backing;
            internal MeshFilter MainFilter, PrefixFilter;
            internal MeshRenderer MainRenderer, PrefixRenderer;
            internal SharedSign Shared;
            internal SharedSign PendingShared;
            // a high-density sign: its dot pitch (metres), and whether it draws its dots (else its solid cells)
            internal float Pitch;
            internal bool Dots;
        }

        private sealed class Entry
        {
            internal Entity Vehicle, Rear;
            internal Hitch? Hitch;   // the rear section's native towing; none: drawn on its own frames
            internal Model Model;
            internal GameObject FrontRoot, RearRoot;
            internal readonly List<SignView> Views = new List<SignView>(3);
            internal bool Active = true;
            internal bool Retiring;
            internal float HiddenAt;
            internal Entity Line;
            // the high-density rear sign: its view, the label it shows, its warning (RearSignRules)
            internal int RearView = -1;
            internal string Prefix = "", Block = "";
            internal RearSignState RearState;
            internal RearMessage RearShown;
        }

        private struct LineText
        {
            internal string Prefix, Block, Terminus, Origin, Name, English;
            internal float At;
        }

        private readonly Dictionary<Entity, Entry> m_Entries = new Dictionary<Entity, Entry>();
        private readonly Dictionary<Entity, Model> m_ModelByPrefab = new Dictionary<Entity, Model>();
        private readonly Dictionary<string, SharedSign> m_Shared = new Dictionary<string, SharedSign>();
        private readonly Dictionary<Entity, LineText> m_LineText = new Dictionary<Entity, LineText>();
        private readonly Stack<GameObject> m_Pool = new Stack<GameObject>();
        private readonly HashSet<Entity> m_Keep = new HashSet<Entity>();
        private readonly List<Entity> m_Remove = new List<Entity>();
        private readonly List<string> m_RemoveShared = new List<string>();
        private readonly List<float3> m_Corners = new List<float3>(4096);
        private readonly List<int> m_Triangles = new List<int>(6144);
        private readonly List<Vector3> m_Vertices = new List<Vector3>(4096);
        private readonly List<Vector3> m_Normals = new List<Vector3>(4096);
        private static readonly Material[] s_NoMaterial = new Material[1];
        private int m_StyleKey = -1;

        private EntityQuery m_Query;
        private PrefabSystem m_Prefabs;
        private NameSystem m_Names;
        private CameraUpdateSystem m_Cameras;
        private RenderingSystem m_Rendering;
        private LightingSystem m_Lighting;
        private CityConfigurationSystem m_City;
        private BufferLookup<TransformFrame> m_FramesLookup;
        private ComponentLookup<Swaying> m_SwayLookup;
        private bool m_ReadsReady;
        private int m_ModelGeneration = -1;

        private static LedFont s_Font;
        private static Task<LedFont> s_FontLoad;
        private static bool s_FontFailed;

        private float m_ScanAt, m_LastScanAt;
        private Vector3 m_LastEye;
        private Quaternion m_LastCameraRotation;
        private int m_LogCooldown;

        private IDisposable m_MaterialResources;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Names = World.GetOrCreateSystemManaged<NameSystem>();
            m_Rendering = World.GetOrCreateSystemManaged<RenderingSystem>();
            m_City = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Query = GetEntityQuery(ComponentType.ReadOnly<Game.Vehicles.PublicTransport>(), ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_FramesLookup = GetBufferLookup<TransformFrame>(true);
            m_SwayLookup = GetComponentLookup<Swaying>(true);
            m_MaterialResources = CabMaterials.AcquireResources();
        }

        // What the system costs, for the log (one line every 30 s, six at most, while any sign was drawn): the whole update, the
        // scans, the buses with signs and the meshes written, so the performance question is answered by numbers.
        private const float kStatsSeconds = 30f;
        private const int kStatsLogMax = 6;
        private long m_UpdateTicks, m_ScanTicks, m_Frames, m_Scans, m_Uploads, m_DrawnFrames;
        private float m_NextStats;
        private int m_StatsLogs;
        private static readonly Unity.Profiling.ProfilerMarker s_Marker = new Unity.Profiling.ProfilerMarker("CDLC.AiBusSigns");

        protected override void OnUpdate()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using (s_Marker.Auto())
                    Step();
            }
            catch (Exception e)
            {
                if (m_LogCooldown-- <= 0)
                {
                    Mod.log.Warn($"AI bus signs update failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    m_LogCooldown = 600;
                }

                foreach (Entry entry in m_Entries.Values)
                    Show(entry, false);
            }
            finally
            {
                m_UpdateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                m_Frames++;
                LogStats();
            }
        }

        private void LogStats()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (m_NextStats == 0f)
            {
                m_NextStats = now + kStatsSeconds;
                return;
            }

            if (now < m_NextStats || m_StatsLogs >= kStatsLogMax)
                return;
            m_NextStats = now + kStatsSeconds;
            long frames = Math.Max(1, m_Frames);
            if (m_DrawnFrames > 0)
            {
                m_StatsLogs++;
                double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int active = 0;
                foreach (Entry entry in m_Entries.Values)
                    if (entry.Active)
                        active++;
                Mod.log.Info($"AI bus signs: {m_UpdateTicks * toMs / frames:0.000} ms a frame over {m_Frames} frames (scans {m_ScanTicks * toMs / frames:0.000} in {m_Scans}); " +
                    $"on average {(double)m_DrawnFrames / frames:0.0} bus(es) with signs, now {active} ({m_Entries.Count - active} hidden and kept), {m_Shared.Count} shared text mesh(es), " +
                    $"{m_Uploads} mesh writes; distance {(Mod.Settings != null ? Mod.Settings.AiBusSignDistance : 0)} m.");
            }

            m_UpdateTicks = m_ScanTicks = m_Frames = m_Scans = m_Uploads = m_DrawnFrames = 0;
        }

        private void Step()
        {
            RetryRetiring();
            BusSettings settings = Mod.Settings;
            if (settings == null || !settings.AiBusSigns)
            {
                if (m_Entries.Count != 0 || m_Shared.Count != 0 || m_Pool.Count != 0)
                    ReleaseAll(true);
                return;
            }

            if (!FontReady())
                return;

            m_ReadsReady = false;
            if (m_Cameras == null)
                m_Cameras = World.GetExistingSystemManaged<CameraUpdateSystem>();
            Camera camera = m_Cameras?.activeCamera ?? Camera.main;
            if (camera == null)
                return;

            if (m_ModelGeneration != VehiclePacks.Generation)
            {
                // a pack's answer changed: the models are looked up again
                ReleaseAll(false);
                m_ModelByPrefab.Clear();
                m_ModelGeneration = VehiclePacks.Generation;
            }

            // the option "LED sign style" changed (or the high-density font came): the signs are made again
            int styleKey = HdFonts.Wanted ? HdFonts.Get() != null ? 2 : HdFonts.Failed ? 0 : 1 : 0;
            if (styleKey != m_StyleKey)
            {
                ReleaseAll(false);
                m_ModelByPrefab.Clear();
                m_StyleKey = styleKey;
            }

            if (m_Lighting == null)
                m_Lighting = World.GetExistingSystemManaged<LightingSystem>();
            if (m_Lighting != null)
                CabMaterials.SetDaylight(m_Lighting.dayLightBrightness);

            SignCamera.Update(camera);
            Vector3 eye = camera.transform.position;
            float now = UnityEngine.Time.unscaledTime;
            float maxDistance = Mathf.Clamp(settings.AiBusSignDistance, 20f, 400f);
            bool moved = (eye - m_LastEye).sqrMagnitude > 4f || Quaternion.Angle(camera.transform.rotation, m_LastCameraRotation) > 5f;
            if (now >= m_ScanAt || (moved && now - m_LastScanAt >= kMinMovedScanSeconds))
            {
                m_ScanAt = now + kScanSeconds;
                m_LastScanAt = now;
                m_LastEye = eye;
                m_LastCameraRotation = camera.transform.rotation;
                long scanStart = System.Diagnostics.Stopwatch.GetTimestamp();
                Scan(eye, maxDistance, now);
                m_ScanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - scanStart;
                m_Scans++;
            }

            // every frame: the signs follow their buses, the rear signs their brakes and turns, the scrolling texts move
            Entity driven = CabBus();
            double time0 = UnityEngine.Time.unscaledTimeAsDouble;
            foreach (SharedSign shared in m_Shared.Values)
                shared.DotsWanted = false;
            foreach (Entry entry in m_Entries.Values)
            {
                if (!entry.Active)
                    continue;
                if (entry.Vehicle == driven || !TryGetNativePose(entry.Vehicle, out Vector3 fp, out Quaternion fr))
                {
                    Show(entry, false);
                    continue;
                }

                entry.FrontRoot.transform.SetPositionAndRotation(fp, fr);
                if (entry.RearRoot != null)
                {
                    if (entry.Rear != Entity.Null && TryGetNativePose(entry.Rear, out Vector3 rp, out Quaternion rr, entry.Hitch, fp, fr))
                        entry.RearRoot.transform.SetPositionAndRotation(rp, rr);
                    else
                        entry.RearRoot.SetActive(false);
                }

                Show(entry, true);
                UpdateRear(entry, time0);
                ChooseDots(entry);
                m_DrawnFrames++;
            }

            DrawCards();
            double time = UnityEngine.Time.unscaledTimeAsDouble;
            foreach (SharedSign shared in m_Shared.Values)
            {
                if (shared.Users > 0 && !shared.Card)
                {
                    shared.LastUsed = now;
                    Render(shared, AiSignRules.ScrollOffset(time, shared.Cycle, shared.Scrolls));
                }
            }
        }

        // ---------------------------------------------------------------- the scan

        // The bus the cab system draws with its own model (driven, or ridden as a passenger): no AI signs on it, they would sit at the
        // game's pose inside a model that follows the camera's.
        private static Entity CabBus()
        {
            if (!Core.Available) return Entity.Null;
            if (Core.IsDriving()) return Core.Vehicle();
            return Core.RiddenVehicle();
        }

        private void Scan(Vector3 eye, float maxDistance, float now)
        {
            m_Keep.Clear();
            Entity driven = CabBus();
            using (NativeArray<Entity> vehicles = m_Query.ToEntityArray(Allocator.Temp))
            using (NativeArray<PrefabRef> prefabs = m_Query.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (vehicle == driven)
                        continue;
                    Model model = ModelOf(prefabs[i].m_Prefab);
                    if (model == null)
                        continue;
                    // the leading section only (an articulated bus's rear section is drawn with it)
                    if (EntityManager.TryGetComponent(vehicle, out Controller controller) && controller.m_Controller != Entity.Null && controller.m_Controller != vehicle)
                        continue;

                    EnsureReads();
                    if (!m_FramesLookup.TryGetBuffer(vehicle, out DynamicBuffer<TransformFrame> frames) || frames.Length < 4)
                        continue;
                    bool shown = m_Entries.TryGetValue(vehicle, out Entry known) && known.Active;
                    float rough = Vector3.Distance(frames[0].m_Position, eye);
                    if (rough > maxDistance * AiSignRules.DistanceHysteresis + kPoseLagMeters)
                        continue;
                    if (!TryGetNativePose(vehicle, out Vector3 position, out _) || !AiSignRules.Drawn(Vector3.Distance(position, eye), maxDistance, shown))
                        continue;

                    AiSignState state = StateOf(vehicle, out Entity line);
                    if (state == AiSignState.Off)
                        continue;

                    Entity rear = model.Articulated ? RearSectionOf(vehicle) : Entity.Null;
                    if (model.Articulated && rear == Entity.Null)
                        continue;
                    m_Keep.Add(vehicle);
                    if (!m_Entries.TryGetValue(vehicle, out Entry entry))
                    {
                        entry = Build(vehicle, rear, model);
                    }
                    if (entry.Retiring) continue;

                    entry.Active = true;
                    entry.Rear = rear;
                    entry.Hitch = rear != Entity.Null && EntityManager.TryGetComponent(rear, out PrefabRef rearPrefab) &&
                        ArticulationSystem.TryNativeHitch(EntityManager, prefabs[i].m_Prefab, rearPrefab.m_Prefab, out Hitch hitch)
                        ? hitch : (Hitch?)null;
                    SetText(entry, state, line, now);
                }
            }

            // the buses no longer chosen: hidden now, kept a while, then their objects pooled
            m_Remove.Clear();
            foreach (Entry entry in m_Entries.Values)
            {
                if (m_Keep.Contains(entry.Vehicle))
                    continue;
                if (entry.Active)
                {
                    entry.Active = false;
                    entry.HiddenAt = now;
                    Show(entry, false);
                }

                if (now - entry.HiddenAt >= kKeepSeconds || !EntityManager.Exists(entry.Vehicle) || EntityManager.HasComponent<Deleted>(entry.Vehicle))
                    m_Remove.Add(entry.Vehicle);
            }

            var cleanupFailures = new List<Exception>();
            foreach (Entity vehicle in m_Remove)
            {
                try { Release(m_Entries[vehicle]); m_Entries.Remove(vehicle); }
                catch (Exception error) { cleanupFailures.Add(error); }
            }

            // the texts nobody shows any more
            m_RemoveShared.Clear();
            foreach (SharedSign shared in m_Shared.Values)
                if (shared.Users == 0 && now - shared.LastUsed > kUnusedMeshSeconds)
                    m_RemoveShared.Add(shared.Key);
            foreach (string key in m_RemoveShared)
            {
                SharedSign shared = m_Shared[key];
                shared.Retiring = true;
                try { DestroyShared(shared); m_Shared.Remove(key); }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            CabCleanup.ThrowIfAny(cleanupFailures, "Expired AI sign cleanup failed.");
        }

        private Entity RearSectionOf(Entity vehicle)
        {
            if (!EntityManager.TryGetBuffer(vehicle, true, out DynamicBuffer<LayoutElement> layout))
                return Entity.Null;
            Entity rear = Entity.Null;
            for (int i = 0; i < layout.Length; i++)
            {
                Entity section = layout[i].m_Vehicle;
                if (section != Entity.Null && section != vehicle)
                    rear = section;
            }

            return rear;
        }

        /// <summary>On a passenger line, returning to the depot, parked or something else (AiSignRules.State).</summary>
        private AiSignState StateOf(Entity vehicle, out Entity line)
        {
            line = Entity.Null;
            bool parked = EntityManager.HasComponent<ParkedCar>(vehicle);
            uint flags = EntityManager.TryGetComponent(vehicle, out Game.Vehicles.PublicTransport transport) ? (uint)transport.m_State : 0U;
            bool returning = (flags & (uint)PublicTransportFlags.Returning) != 0U;
            bool special = (flags & (uint)(PublicTransportFlags.Evacuating | PublicTransportFlags.PrisonerTransport | PublicTransportFlags.DummyTraffic |
                PublicTransportFlags.Testing | PublicTransportFlags.Disabled)) != 0U;
            bool onLine = false;
            if (EntityManager.TryGetComponent(vehicle, out CurrentRoute route) && route.m_Route != Entity.Null && EntityManager.Exists(route.m_Route) &&
                EntityManager.TryGetComponent(route.m_Route, out PrefabRef linePrefab) &&
                EntityManager.TryGetComponent(linePrefab.m_Prefab, out TransportLineData lineData) && lineData.m_PassengerTransport)
            {
                onLine = true;
                line = route.m_Route;
            }

            return AiSignRules.State(onLine, returning, parked, special);
        }

        private void SetText(Entry entry, AiSignState state, Entity line, float now)
        {
            string prefix = "", block = "", message, card, english;
            if (state == AiSignState.InService)
            {
                LineText text = LineTextOf(line, now);
                prefix = text.Prefix;
                block = text.Block;
                message = text.Terminus;
                card = CardText.Route(text.Origin, text.Terminus, text.Name, text.Block);
                english = text.English;
            }
            else
            {
                message = card = BusLocale.Text("LED.NotInService");
                english = HdFonts.Fixed(message, BusLocale.English("LED.NotInService"));
            }

            entry.Line = line;
            entry.Prefix = prefix;
            entry.Block = block;
            for (int i = 0; i < entry.Views.Count; i++)
            {
                SignDef def = entry.Model.Signs[i];
                if (def.Hd)
                {
                    // the Shanghai layout; the rear: the label alone, or its warning (UpdateRear)
                    if (def.Slot == Slot.Rear)
                        AssignHd(entry.Views[i], def, HdFonts.Rear(entry.RearShown, prefix, block));
                    else
                        AssignHd(entry.Views[i], def, HdContent.Route(prefix, block, HdFonts.Suffix(block), message, english));
                    continue;
                }

                // the rear sign: the label alone, centred (dark off a line), as the driven bus's
                string m = def.Slot == Slot.Rear ? "" : def.Card ? card : message;
                Assign(entry.Views[i], def, prefix, block, m);
            }
        }

        /// <summary>
        /// A high-density rear sign's warning from the bus's frames (the game's braking and turning flags, its speed): "刹车注意",
        /// "车辆左转 / 右转", else the label (option "Rear sign warnings"; RearSignRules).
        /// </summary>
        private void UpdateRear(Entry entry, double now)
        {
            if (entry.RearView < 0 || entry.RearView >= entry.Views.Count)
                return;
            RearMessage message = RearMessage.Route;
            if (HdFonts.RearWarnings && TryGetFrameFlags(entry.Vehicle, out TransformFlags flags, out float speed))
            {
                bool left = (flags & TransformFlags.TurningLeft) != 0, right = (flags & TransformFlags.TurningRight) != 0;
                int turn = left == right ? 0 : left ? -1 : 1;
                message = RearSignRules.Next(ref entry.RearState, (flags & TransformFlags.Braking) != 0, turn, speed, now);
            }

            if (message == entry.RearShown)
                return;
            entry.RearShown = message;
            AssignHd(entry.Views[entry.RearView], entry.Model.Signs[entry.RearView], HdFonts.Rear(message, entry.Prefix, entry.Block));
        }

        /// <summary>A line's label and terminus, read again every few seconds (as the driven bus's, BusShiftSystem.Terminus).</summary>
        private LineText LineTextOf(Entity line, float now)
        {
            if (m_LineText.TryGetValue(line, out LineText text) && now - text.At < kLineTextSeconds)
                return text;

            string name = EntityNames.Text(m_Names, line);
            LineLabel.Parse(name, EntityManager.TryGetComponent(line, out RouteNumber number) ? number.m_Number : 0, EntityNames.PlayerNamed(m_Names, line), out string prefix, out string block);
            string terminus = TerminusOf(line);
            text = new LineText { Prefix = prefix ?? "", Block = block ?? "", Terminus = terminus, Origin = OriginOf(line, terminus), Name = name ?? "",
                English = HdFonts.English(terminus), At = now };
            m_LineText[line] = text;
            return text;
        }

        private string TerminusOf(Entity line)
        {
            if (TransitTimetablesLink.TryTerminus(EntityManager, line, out Entity stop, out _) && stop != Entity.Null)
                return EntityNames.Text(m_Names, stop) ?? "";
            if (!EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteWaypoint> waypoints))
                return "";
            for (int i = waypoints.Length - 1; i >= 0; i--)
                if (EntityManager.TryGetComponent(waypoints[i].m_Waypoint, out Connected connected) && connected.m_Connected != Entity.Null &&
                    EntityManager.HasComponent<Game.Routes.TransportStop>(connected.m_Connected))
                    return EntityNames.Text(m_Names, connected.m_Connected) ?? "";
            return "";
        }

        /// <summary>A printed board's other end: the line's first real stop in waypoint order, or its last when the terminus is the first.</summary>
        private string OriginOf(Entity line, string terminus)
        {
            if (!EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteWaypoint> waypoints))
                return "";
            string first = null, last = null;
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (!EntityManager.TryGetComponent(waypoints[i].m_Waypoint, out Connected connected) || connected.m_Connected == Entity.Null ||
                    !EntityManager.HasComponent<Game.Routes.TransportStop>(connected.m_Connected))
                    continue;
                string stop = EntityNames.Text(m_Names, connected.m_Connected) ?? "";
                first = first ?? stop;
                last = stop;
            }

            return string.Equals(first, terminus, StringComparison.Ordinal) ? last ?? "" : first ?? "";
        }

        // ---------------------------------------------------------------- the models

        /// <summary>The signs of a bus prefab whose model is ours (a pack bus or CDLCBus12), null for any other.</summary>
        private Model ModelOf(Entity prefab)
        {
            if (m_ModelByPrefab.TryGetValue(prefab, out Model cached))
                return cached;
            if (!m_Prefabs.TryGetPrefab(prefab, out PrefabBase prefabBase) || prefabBase == null)
                return null;   // not there yet: asked again on the next scan

            Model model = null;
            try
            {
                VehicleProfile profile = VehiclePacks.ForPrefab(prefabBase);
                bool own = profile != null || string.Equals(prefabBase.name, OwnBus.Name, StringComparison.OrdinalIgnoreCase);
                SignOverlay overlay = own ? null : SignOverlays.ForPrefab(prefabBase);
                if ((profile != null && HasCards(profile) || overlay != null && overlay.HasCards) && CardFonts.Get() == null && !CardFonts.Failed)
                    return null;   // its printed boards wait for the card font: asked again on the next scan
                if (HdFonts.Wanted && HdFonts.Get() == null && !HdFonts.Failed)
                    return null;   // the high-density signs wait for their font: asked again on the next scan
                if (profile != null)
                    model = PackModel(profile);
                else if (own)
                    model = GenericModel();
                else if (overlay != null)
                    model = OverlayModel(overlay);
            }
            catch (Exception e)
            {
                Mod.log.Warn($"AI bus signs: the model of {prefabBase.name} could not be read ({e.GetType().Name}: {e.Message}); its buses stay dark.");
                model = null;
            }

            m_ModelByPrefab[prefab] = model != null && model.Signs.Count > 0 ? model : null;
            return m_ModelByPrefab[prefab];
        }

        private static bool HasCards(VehicleProfile profile)
        {
            if (profile.FrontSign.IsCard || profile.SideSign.IsCard || profile.RearSign.IsCard || Array.Exists(profile.ExtraSideSigns, s => s.IsCard))
                return true;
            VehicleProfile rear = profile.Assembly?.Rear.Profile;
            return rear != null && (rear.RearSign.IsCard || rear.SideSign.IsCard || Array.Exists(rear.ExtraSideSigns, s => s.IsCard));
        }

        /// <summary>
        /// A pack bus: its section's own signs; an articulated bus's front section with its rear section's rear sign and, when the rear
        /// section's profile declares one, its own side sign.
        /// </summary>
        private static Model PackModel(VehicleProfile profile)
        {
            VehicleAssembly assembly = profile.Assembly;
            if (assembly != null && profile.SectionId != assembly.Front.Id)
                return null;   // a rear section: drawn with its front

            var model = new Model { Articulated = assembly != null };
            AddSigns(model, profile.Spec, false, true, true, assembly == null);
            if (assembly != null)
                AddSigns(model, assembly.Rear.Profile.Spec, true, false, true, true);
            return model;
        }

        /// <summary>A bus asset by someone else with a sign overlay: the overlay's signs on its own body, each with its board.</summary>
        private static Model OverlayModel(SignOverlay overlay)
        {
            var model = new Model();
            if (overlay.FrontSign.Matrix.x > 0)
                model.Signs.Add(Styled(OverlayDef(Slot.Front, overlay.FrontSign, overlay.FrontRotation, overlay.FrontBacking), overlay.FrontSign));
            if (overlay.SideSign.Matrix.x > 0)
                model.Signs.Add(Styled(OverlayDef(Slot.Side, overlay.SideSign, overlay.SideRotation, overlay.SideBacking), overlay.SideSign));
            if (overlay.RearSign.Matrix.x > 0)
                model.Signs.Add(Styled(OverlayDef(Slot.Rear, overlay.RearSign, overlay.RearRotation, overlay.RearBacking), overlay.RearSign));
            return model;
        }

        private static SignDef OverlayDef(Slot slot, in VehicleProfile.Sign sign, quaternion rotation, SignOverlay.Backing backing) =>
            new SignDef { Slot = slot, Centre = sign.Centre, Size = sign.Size, Rotation = rotation, Matrix = sign.Matrix, Backing = backing };

        /// <summary>The built-in bus: the generic model's signs on its exact spec, where its native body has them (BusCabSystem.SpecOf takes the same).</summary>
        private Model GenericModel()
        {
            var model = new Model();
            AddSigns(model, OwnBus.Spec(m_City != null && m_City.leftHandTraffic).Snapped(), false, true, true, true);
            return model;
        }

        private static void AddSigns(Model model, in BusInteriorSpec spec, bool rearSection, bool front, bool side, bool rear)
        {
            VehicleProfile authored = VehicleModels.Find(spec.ModelVariant);
            if (front && (authored == null || authored.FrontSign.Matrix.x > 0))
            {
                BusInterior.FrontSignFrame(spec, out float3 c, out float2 size);
                model.Signs.Add(Styled(new SignDef { Slot = Slot.Front, OnRearSection = rearSection, Centre = c, Size = size, Rotation = quaternion.RotateY(math.PI), Matrix = BusInterior.FrontSignMatrix(spec) },
                    authored?.FrontSign));
            }

            if (side && (authored == null || authored.SideSign.Matrix.x > 0) && BusInterior.SideSignFrame(spec, out float3 sc, out float2 ss, out float kerb))
                model.Signs.Add(Styled(new SignDef { Slot = Slot.Side, OnRearSection = rearSection, Centre = sc, Size = ss, Rotation = BusInterior.SideSignRotation(spec, kerb), Matrix = BusInterior.SideSignMatrix(spec) },
                    authored?.SideSign));

            if (side && authored != null)
                foreach (var sign in authored.ExtraSideSigns)
                    model.Signs.Add(Styled(new SignDef { Slot = Slot.Side, OnRearSection = rearSection, Centre = sign.Centre,
                        Size = sign.Size, Rotation = BusInterior.SideSignRotation(math.sign(sign.Centre.x), sign.Tilt), Matrix = sign.Matrix }, sign));

            if (rear && (authored == null || authored.RearSign.Matrix.x > 0))
            {
                BusInterior.RearRouteFrame(spec, out float3 rc, out float2 rs);
                model.Signs.Add(Styled(new SignDef { Slot = Slot.Rear, OnRearSection = rearSection, Centre = rc, Size = rs, Rotation = quaternion.identity, Matrix = BusInterior.RearRouteMatrix(spec) },
                    authored?.RearSign));
            }
        }

        /// <summary>
        /// A sign drawn as its profile says: a printed board when its style is "card" and the card font is there; else an LED sign, of
        /// high density (32 rows of square dots on its size) when the option asks for it and its font is there.
        /// </summary>
        private static SignDef Styled(SignDef def, VehicleProfile.Sign? source)
        {
            if (source.HasValue && source.Value.IsCard && CardFonts.Get() != null)
            {
                def.Card = true;
                def.Board = CardFonts.Rgb(source.Value.Board, CardRgb.DefaultBoard);
                def.Ink = CardFonts.Rgb(source.Value.Ink, CardRgb.DefaultInk);
            }
            else if (HdFonts.Wanted && HdFonts.Get() != null)
            {
                def.Hd = true;
                def.Matrix = LedGeometry.HdMatrix(def.Size);
            }

            return def;
        }

        // ---------------------------------------------------------------- the objects

        private Entry Build(Entity vehicle, Entity rear, Model model)
        {
            var entry = new Entry { Vehicle = vehicle, Rear = rear, Model = model };
            m_Entries.Add(vehicle, entry);
            try
            {
                entry.FrontRoot = new GameObject("CDLC AI bus signs");
                entry.FrontRoot.hideFlags = HideFlags.HideAndDontSave;
                foreach (SignDef def in model.Signs)
                {
                    if (def.OnRearSection && entry.RearRoot == null)
                    {
                        entry.RearRoot = new GameObject("CDLC AI bus signs (rear section)");
                        entry.RearRoot.hideFlags = HideFlags.HideAndDontSave;
                    }
                    UnityEngine.Transform parent = (def.OnRearSection ? entry.RearRoot : entry.FrontRoot).transform;
                    var view = new SignView();
                    entry.Views.Add(view);
                    if (def.Backing.Present)
                    {
                        Take(view, Part.Backing, parent, SignOverlay.BackingCentre(def.Centre, def.Rotation), def.Rotation, "sign board");
                        view.Backing.transform.localScale = new Vector3(def.Backing.Size.x, def.Backing.Size.y, 1f);
                        view.Backing.GetComponent<MeshFilter>().sharedMesh = CardFonts.Quad();
                        view.Backing.GetComponent<MeshRenderer>().sharedMaterial = SignOverlays.BoardMaterial(def.Backing);
                    }
                    Take(view, Part.Main, parent, def.Centre, def.Rotation, def.Card ? "card sign" : "LED dots");
                    if (def.Hd)
                    {
                        view.Pitch = def.Size.y / def.Matrix.y;
                        view.Dots = false;
                    }
                    if (def.Card)
                    {
                        view.Main.transform.localScale = new Vector3(def.Size.x, def.Size.y, 1f);   // the unit quad to the board's size
                        view.MainFilter.sharedMesh = CardFonts.Quad();
                    }
                    else if (!def.Hd)
                    {
                        Take(view, Part.Prefix, parent, def.Centre, def.Rotation, "LED prefix");
                    }
                    else if (def.Slot == Slot.Rear)
                    {
                        entry.RearView = entry.Views.Count - 1;
                    }
                }
                return entry;
            }
            catch (Exception creationError)
            {
                try { Release(entry); m_Entries.Remove(vehicle); }
                catch (Exception cleanupError) { throw new AggregateException("AI sign construction and cleanup failed.", creationError, cleanupError); }
                throw;
            }
        }

        private enum Part { Main, Prefix, Backing }

        /// <summary>A sign's object from the pool (or new), placed on its section.</summary>
        private void Take(SignView view, Part part, UnityEngine.Transform parent, float3 position, quaternion rotation, string name)
        {
            GameObject go = m_Pool.Count > 0 ? m_Pool.Pop() : null;
            bool create = go == null;
            if (go == null)
                go = new GameObject(name);
            if (part == Part.Prefix) view.Prefix = go; else if (part == Part.Main) view.Main = go; else view.Backing = go;
            if (create)
            {
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<MeshFilter>();
                MeshRenderer r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                r.renderingLayerMask = uint.MaxValue;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                r.allowOcclusionWhenDynamic = false;
            }

            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(position.x, position.y, position.z);
            go.transform.localRotation = new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);
            go.transform.localScale = Vector3.one;
            MeshFilter filter = go.GetComponent<MeshFilter>();
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (part == Part.Prefix) { view.PrefixFilter = filter; view.PrefixRenderer = renderer; }
            else if (part == Part.Main) { view.MainFilter = filter; view.MainRenderer = renderer; }
            filter.sharedMesh = null;
            renderer.enabled = true;
            go.SetActive(true);
        }

        private void Assign(SignView view, in SignDef def, string prefix, string block, string message)
        {
            if (def.Card)
            {
                AssignCard(view, def, prefix, block, message);
                return;
            }

            string key = LedGeometry.MeshKey(def.Matrix.x, def.Matrix.y, def.Size, prefix, block, message);
            if (view.Shared != null && view.Shared.Key == key)
                return;

            if (!m_Shared.TryGetValue(key, out SharedSign shared))
            {
                shared = new SharedSign
                {
                    Key = key,
                    Composer = new LedComposer(s_Font, def.Matrix.x, def.Matrix.y),
                    PrefixText = prefix,
                    Block = block,
                    Message = message,
                };
                m_Shared.Add(key, shared);
                try
                {
                    NewMesh(shared, false, def.Size);
                    NewMesh(shared, true, def.Size);
                    shared.PrefixMaterial = string.IsNullOrEmpty(prefix) ? null :
                        CabMaterials.DimmedLamp(LineLabel.ColorFor(prefix, Mod.Settings != null ? Mod.Settings.RoutePrefixColors : null), kSignNits, kSignNight);
                    shared.Scrolls = shared.Composer.Scrolls(prefix, block, message);
                    shared.Cycle = Math.Max(1, shared.Composer.Cycle(prefix, block, message));
                    Render(shared, AiSignRules.ScrollOffset(UnityEngine.Time.unscaledTimeAsDouble, shared.Cycle, shared.Scrolls));
                }
                catch (Exception error) { RollbackShared(shared, error); throw; }
            }

            BeginBind(view, shared);
            shared.LastUsed = UnityEngine.Time.unscaledTime;
            view.MainFilter.sharedMesh = shared.Main;
            view.MainRenderer.sharedMaterial = CabMaterials.DimmedLamp(s_LedAmber, kSignNits, kSignNight);
            view.PrefixFilter.sharedMesh = shared.Prefix;
            view.PrefixRenderer.sharedMaterial = shared.PrefixMaterial;
            view.Prefix.SetActive(shared.PrefixMaterial != null);
            CommitBind(view, shared);
        }

        /// <summary>A high-density sign: the shared lit-dots mesh of this content, grid and size (a submesh an ink).</summary>
        private void AssignHd(SignView view, in SignDef def, in HdContent content)
        {
            // the prefix keeps its own colour (option "Route prefix colours"), part of the key: a changed option makes new meshes
            float3 prefixColour = LineLabel.ColorFor(content.Prefix, Mod.Settings != null ? Mod.Settings.RoutePrefixColors : null);
            string key = "hd/" + def.Matrix.x + "/" + (int)math.round(def.Size.x * 1000f) + "x" + (int)math.round(def.Size.y * 1000f) + "/" +
                (string.IsNullOrEmpty(content.Prefix) ? "" : ((int)math.round(prefixColour.x * 255f)).ToString("x2") + ((int)math.round(prefixColour.y * 255f)).ToString("x2") +
                ((int)math.round(prefixColour.z * 255f)).ToString("x2")) + "/" + content.Key();
            if (view.Shared != null && view.Shared.Key == key)
                return;

            if (!m_Shared.TryGetValue(key, out SharedSign shared))
            {
                HdFontSet fonts = HdFonts.Get();
                if (fonts == null)
                {
                    view.MainFilter.sharedMesh = null;
                    if (view.Shared != null) view.Shared.Users--;
                    view.Shared = null;
                    return;
                }

                shared = new SharedSign
                {
                    Key = key,
                    Hd = new HdComposer(fonts.Font, s_Font, def.Matrix.x),
                    Size = def.Size,
                };
                m_Shared.Add(key, shared);
                try
                {
                    shared.HdDots = new LedMesh("CDLC AI bus sign dots", def.Matrix.x, def.Matrix.y, def.Size, HdInk.Count);
                    shared.HdCells = new LedMesh("CDLC AI bus sign cells", def.Matrix.x, def.Matrix.y, def.Size, HdInk.Count);
                    shared.HdMaterials = HdFonts.Inks(prefixColour);
                    shared.CellMaterials = HdFonts.Inks(prefixColour, true);
                    shared.Hd.Compose(content);
                    shared.Scrolls = shared.Hd.Scrolls;
                    shared.Cycle = Math.Max(1, shared.Hd.Cycle);
                    Render(shared, AiSignRules.ScrollOffset(UnityEngine.Time.unscaledTimeAsDouble, shared.Cycle, shared.Scrolls));
                }
                catch (Exception error) { RollbackShared(shared, error); throw; }
            }

            BeginBind(view, shared);
            shared.LastUsed = UnityEngine.Time.unscaledTime;
            view.MainFilter.sharedMesh = (view.Dots ? shared.HdDots : shared.HdCells).Mesh;
            Material[] inks = view.Dots ? shared.HdMaterials : shared.CellMaterials;
            if (inks != null)
                view.MainRenderer.sharedMaterials = inks;
            CommitBind(view, shared);
        }

        /// <summary>A printed board: the shared picture of this text and size (made blank now, drawn by DrawCards under the budget).</summary>
        private void AssignCard(SignView view, in SignDef def, string prefix, string block, string message)
        {
            CardText.PictureSize(def.Size.x, def.Size.y, out int width, out int height);
            CardRgb prefixInk = string.IsNullOrEmpty(prefix) ? def.Ink :
                CardFonts.Rgb01(LineLabel.ColorFor(prefix, Mod.Settings != null ? Mod.Settings.RoutePrefixColors : null));
            string key = CardText.Key(width, height, def.Board, def.Ink, prefixInk, prefix, block, message);
            if (view.Shared != null && view.Shared.Key == key)
                return;

            if (!m_Shared.TryGetValue(key, out SharedSign shared))
            {
                shared = new SharedSign
                {
                    Key = key, Card = true, CardPending = true, PrefixText = prefix, Block = block, Message = message,
                    CardWidth = width, CardHeight = height, Board = def.Board, Ink = def.Ink, PrefixInk = prefixInk,
                };
                m_Shared.Add(key, shared);
                try
                {
                    shared.CardTexture = CardFonts.NewPicture(width, height, def.Board);
                    shared.CardMaterial = CabMaterials.Card(shared.CardTexture);
                }
                catch (Exception error) { RollbackShared(shared, error); throw; }
            }

            BeginBind(view, shared);
            shared.LastUsed = UnityEngine.Time.unscaledTime;
            view.MainRenderer.sharedMaterial = shared.CardMaterial;
            CommitBind(view, shared);
        }

        private static void BeginBind(SignView view, SharedSign shared)
        {
            shared.Users++;
            view.PendingShared = shared;
        }

        private static void CommitBind(SignView view, SharedSign shared)
        {
            if (view.Shared != null) view.Shared.Users--;
            view.Shared = shared;
            view.PendingShared = null;
        }

        private CardComposer m_CardComposer;

        /// <summary>Draws the pictures of up to kCardDrawsPerFrame new boards (one composer, reused while the picture size stays).</summary>
        private void DrawCards()
        {
            CardFont font = CardFonts.Get();
            if (font == null)
                return;
            int drawn = 0;
            foreach (SharedSign shared in m_Shared.Values)
            {
                if (!shared.Card || !shared.CardPending || shared.Users <= 0 || shared.CardTexture == null)
                    continue;
                if (m_CardComposer == null || m_CardComposer.Width != shared.CardWidth || m_CardComposer.Height != shared.CardHeight)
                    m_CardComposer = new CardComposer(font, s_Font, shared.CardWidth, shared.CardHeight);
                m_CardComposer.Render(shared.PrefixText, shared.Block, shared.Message, shared.Board, shared.Ink, shared.PrefixInk);
                CardFonts.Upload(shared.CardTexture, m_CardComposer);
                shared.CardPending = false;
                m_Uploads++;
                if (++drawn >= kCardDrawsPerFrame)
                    break;
            }
        }

        private void NewMesh(SharedSign owner, bool prefix, float2 size)
        {
            Mesh mesh = SignMesh(size);
            if (prefix) owner.Prefix = mesh; else owner.Main = mesh;
        }

        private static Mesh SignMesh(float2 size)
        {
            var mesh = new Mesh();
            mesh.name = "CDLC AI bus sign dots";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.MarkDynamic();
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(size.x + 0.02f, size.y + 0.02f, 0.02f));
            return mesh;
        }

        private void RollbackShared(SharedSign shared, Exception creationError)
        {
            shared.Retiring = true;
            try { DestroyShared(shared); m_Shared.Remove(shared.Key); }
            catch (Exception cleanupError) { throw new AggregateException("AI shared sign construction and cleanup failed.", creationError, cleanupError); }
        }

        /// <summary>
        /// Writes a shared sign's lit dots for this scroll position (nothing when it did not move); a high-density sign's into its
        /// solid cells, and into its dots' mesh only while a view draws them (DotsWanted).
        /// </summary>
        private void Render(SharedSign shared, int offset)
        {
            if (shared.Hd != null)
            {
                if (shared.HdDots == null)
                    return;
                if (offset != shared.Offset)
                {
                    shared.Offset = offset;
                    shared.Hd.Render(offset);
                    shared.HdCells.Write(shared.Hd.Buffer, shared.Hd.Ink, true);
                    m_Uploads++;
                }
                if (shared.DotsWanted && shared.MeshOffset != offset)
                {
                    shared.MeshOffset = offset;
                    shared.HdDots.Write(shared.Hd.Buffer, shared.Hd.Ink);
                    m_Uploads++;
                }
                return;
            }

            if (shared.Main == null || offset == shared.Offset)
                return;
            shared.Offset = offset;
            m_Uploads++;

            shared.Composer.RenderSign(shared.PrefixText, shared.Block, shared.Message, offset);
            float2 size = new float2(shared.Main.bounds.size.x - 0.02f, shared.Main.bounds.size.y - 0.02f);
            Upload(shared.Main, shared.Composer, 0, size);
            if (!string.IsNullOrEmpty(shared.PrefixText))
                Upload(shared.Prefix, shared.Composer, 1, size);
        }

        private void Upload(Mesh mesh, LedComposer composer, byte ink, float2 size)
        {
            LedGeometry.LitQuads(composer.Buffer, composer.Ink, ink, composer.Width, composer.Height, size, m_Corners, m_Triangles);
            m_Vertices.Clear();
            m_Normals.Clear();
            for (int i = 0; i < m_Corners.Count; i++)
            {
                float3 c = m_Corners[i];
                m_Vertices.Add(new Vector3(c.x, c.y, c.z));
                m_Normals.Add(new Vector3(0f, 0f, -1f));
            }

            Bounds bounds = mesh.bounds;
            mesh.Clear(true);
            mesh.indexFormat = m_Vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(m_Vertices);
            mesh.SetNormals(m_Normals);
            mesh.SetTriangles(m_Triangles, 0, false);
            mesh.bounds = bounds;
        }

        /// <summary>Each high-density view of a shown bus: its dots or its solid cells for this frame's camera (SignCamera).</summary>
        private static void ChooseDots(Entry entry)
        {
            foreach (SignView view in entry.Views)
            {
                SharedSign shared = view.Shared;
                if (shared == null || shared.Hd == null || shared.CellMaterials == null || shared.HdMaterials == null)
                    continue;
                bool dots = SignCamera.Dots(view.Main.transform, view.Pitch, view.Dots);
                if (dots != view.Dots)
                {
                    view.Dots = dots;
                    view.MainFilter.sharedMesh = (dots ? shared.HdDots : shared.HdCells).Mesh;
                    view.MainRenderer.sharedMaterials = dots ? shared.HdMaterials : shared.CellMaterials;
                }
                if (dots)
                    shared.DotsWanted = true;
            }
        }

        private static void Show(Entry entry, bool show)
        {
            if (entry.FrontRoot != null && entry.FrontRoot.activeSelf != show)
                entry.FrontRoot.SetActive(show);
            if (entry.RearRoot != null && entry.RearRoot.activeSelf != show)
                entry.RearRoot.SetActive(show);
        }

        private void Release(Entry entry)
        {
            entry.Retiring = true;
            entry.Active = false;
            var failures = new List<Exception>();
            if (entry.FrontRoot != null) CabCleanup.Try(() => entry.FrontRoot.SetActive(false), failures);
            if (entry.RearRoot != null) CabCleanup.Try(() => entry.RearRoot.SetActive(false), failures);
            foreach (SignView view in new List<SignView>(entry.Views))
            {
                try { Pool(view.Main); view.Main = null; view.MainFilter = null; view.MainRenderer = null; }
                catch (Exception error) { failures.Add(error); }
                try { Pool(view.Prefix); view.Prefix = null; view.PrefixFilter = null; view.PrefixRenderer = null; }
                catch (Exception error) { failures.Add(error); }
                try { Pool(view.Backing); view.Backing = null; }
                catch (Exception error) { failures.Add(error); }
                if (view.Main != null || view.Prefix != null || view.Backing != null) continue;
                if (view.Shared != null) view.Shared.Users--;
                if (view.PendingShared != null) view.PendingShared.Users--;
                view.Shared = view.PendingShared = null;
                entry.Views.Remove(view);
            }
            if (entry.Views.Count == 0)
            {
                try { if (entry.FrontRoot != null) UnityEngine.Object.Destroy(entry.FrontRoot); entry.FrontRoot = null; }
                catch (Exception error) { failures.Add(error); }
                try { if (entry.RearRoot != null) UnityEngine.Object.Destroy(entry.RearRoot); entry.RearRoot = null; }
                catch (Exception error) { failures.Add(error); }
            }
            CabCleanup.ThrowIfAny(failures, "AI sign owner cleanup failed.");
        }

        private void Pool(GameObject go)
        {
            if (go == null)
                return;
            var failures = new List<Exception>();
            CabCleanup.Try(() => go.SetActive(false), failures);
            MeshFilter filter = null;
            MeshRenderer renderer = null;
            CabCleanup.Try(() => filter = go.GetComponent<MeshFilter>(), failures);
            CabCleanup.Try(() => renderer = go.GetComponent<MeshRenderer>(), failures);
            if (renderer != null)
            {
                CabCleanup.Try(() => renderer.enabled = false, failures);
                CabCleanup.Try(() => renderer.sharedMaterials = s_NoMaterial, failures);
            }
            if (filter != null) CabCleanup.Try(() => filter.sharedMesh = null, failures);
            CabCleanup.ThrowIfAny(failures, "AI sign renderer detach failed.");
            if (filter == null || renderer == null)
            {
                UnityEngine.Object.Destroy(go);
                return;
            }
            go.transform.SetParent(null, false);
            if (m_Pool.Count < kPoolMax)
                m_Pool.Push(go);
            else
                UnityEngine.Object.Destroy(go);
        }

        private static void DestroyShared(SharedSign shared)
        {
            if (shared.Users != 0) throw new InvalidOperationException("AI sign resources still have renderer users.");
            var failures = new List<Exception>();
            try { if (shared.CardMaterial != null) CabMaterials.ReleaseCard(shared.CardMaterial); shared.CardMaterial = null; }
            catch (Exception error) { failures.Add(error); }
            try { if (shared.CardTexture != null) UnityEngine.Object.Destroy(shared.CardTexture); shared.CardTexture = null; }
            catch (Exception error) { failures.Add(error); }
            try { if (shared.Main != null) UnityEngine.Object.Destroy(shared.Main); shared.Main = null; }
            catch (Exception error) { failures.Add(error); }
            try { if (shared.Prefix != null) UnityEngine.Object.Destroy(shared.Prefix); shared.Prefix = null; }
            catch (Exception error) { failures.Add(error); }
            try { if (shared.HdDots != null) shared.HdDots.Dispose(); shared.HdDots = null; }
            catch (Exception error) { failures.Add(error); }
            try { if (shared.HdCells != null) shared.HdCells.Dispose(); shared.HdCells = null; }
            catch (Exception error) { failures.Add(error); }
            CabCleanup.ThrowIfAny(failures, "AI shared sign cleanup failed.");
        }

        private void RetryRetiring()
        {
            var failures = new List<Exception>();
            foreach (Entry entry in new List<Entry>(m_Entries.Values))
            {
                if (!entry.Retiring && !entry.Views.Exists(view => view.PendingShared != null)) continue;
                try { Release(entry); m_Entries.Remove(entry.Vehicle); }
                catch (Exception error) { failures.Add(error); }
            }
            foreach (SharedSign shared in new List<SharedSign>(m_Shared.Values))
            {
                if (!shared.Retiring || shared.Users != 0) continue;
                try { DestroyShared(shared); m_Shared.Remove(shared.Key); }
                catch (Exception error) { failures.Add(error); }
            }
            CabCleanup.ThrowIfAny(failures, "AI retiring sign cleanup failed.");
        }

        /// <summary>Every sign object and mesh gone (the option off, a new city); the pool too when <paramref name="all"/>.</summary>
        private void ReleaseAll(bool all)
        {
            var failures = new List<Exception>();
            foreach (Entry entry in m_Entries.Values) entry.Retiring = true;
            CabCleanup.Try(RetryRetiring, failures);
            foreach (SharedSign shared in new List<SharedSign>(m_Shared.Values))
            {
                if (shared.Users != 0) continue;
                shared.Retiring = true;
                try { DestroyShared(shared); m_Shared.Remove(shared.Key); }
                catch (Exception error) { failures.Add(error); }
            }
            m_LineText.Clear();
            if (all)
            {
                var retained = new List<GameObject>();
                while (m_Pool.Count > 0)
                {
                    GameObject go = m_Pool.Pop();
                    if (go != null)
                    {
                        try { UnityEngine.Object.Destroy(go); }
                        catch (Exception error) { failures.Add(error); retained.Add(go); }
                    }
                }
                foreach (GameObject go in retained) m_Pool.Push(go);
            }
            CabCleanup.ThrowIfAny(failures, "AI bus sign cleanup failed.");
        }

        // ---------------------------------------------------------------- the font and the poses

        /// <summary>The classic LED font (the fallback of the high-density faces), loading on a worker the first time it is asked; null until then.</summary>
        internal static LedFont Font => FontReady() ? s_Font : null;

        private static bool FontReady()
        {
            if (s_Font != null)
                return true;
            if (s_FontFailed)
                return false;
            if (s_FontLoad == null)
            {
                string directory = Mod.ModDirectory;
                if (string.IsNullOrEmpty(directory))
                {
                    s_FontFailed = true;
                    return false;
                }

                string path = Path.Combine(directory, "Fonts", "led-font.bin.gz");
                s_FontLoad = Task.Run(() =>
                {
                    var font = new LedFont();
                    return font.Load(path) ? font : null;
                });
            }

            if (!s_FontLoad.IsCompleted)
                return false;
            s_Font = s_FontLoad.Status == TaskStatus.RanToCompletion ? s_FontLoad.Result : null;
            if (s_Font == null)
            {
                s_FontFailed = true;
                Mod.log.Warn("AI bus signs: the LED font could not be loaded (Fonts/led-font.bin.gz); the AI buses' signs stay dark.");
                return false;
            }

            return true;
        }

        private void EnsureReads()
        {
            if (m_ReadsReady)
                return;
            m_ReadsReady = true;
            EntityManager.CompleteDependencyBeforeRO<TransformFrame>();
            EntityManager.CompleteDependencyBeforeRO<Swaying>();
            m_FramesLookup.Update(this);
            m_SwayLookup.Update(this);
        }

        /// <summary>The game's flags of the bus's frames drawn now (either of the two it is between) and its speed.</summary>
        private bool TryGetFrameFlags(Entity entity, out TransformFlags flags, out float speed)
        {
            flags = 0;
            speed = 0f;
            if (m_Rendering == null)
                return false;
            EnsureReads();
            if (!m_FramesLookup.TryGetBuffer(entity, out DynamicBuffer<TransformFrame> frames) || frames.Length < 4 || !EntityManager.HasComponent<UpdateFrame>(entity))
                return false;
            UpdateFrame update = EntityManager.GetSharedComponent<UpdateFrame>(entity);
            ObjectInterpolateSystem.CalculateUpdateFrames(m_Rendering.frameIndex, m_Rendering.frameTime, update.m_Index, out uint a, out uint b, out float t);
            TransformFrame fa = frames[(int)a], fb = frames[(int)b];
            flags = fa.m_Flags | fb.m_Flags;
            speed = math.length(math.lerp(fa.m_Velocity, fb.m_Velocity, t));
            return math.isfinite(speed);
        }

        /// <summary>The bus where the game draws it this frame (as CDL Asset's ArticulationSystem.TryGetNativePose: the frames, a towed section hung
        /// on its drawn front, and the body's sway).</summary>
        private bool TryGetNativePose(Entity entity, out Vector3 position, out Quaternion rotation,
            Hitch? towedBy = null, Vector3 frontPosition = default, Quaternion frontRotation = default)
        {
            position = default;
            rotation = Quaternion.identity;
            if (m_Rendering == null)
                return false;
            EnsureReads();
            if (!m_FramesLookup.TryGetBuffer(entity, out DynamicBuffer<TransformFrame> frames) || frames.Length < 4 || !EntityManager.HasComponent<UpdateFrame>(entity))
                return false;
            UpdateFrame update = EntityManager.GetSharedComponent<UpdateFrame>(entity);
            ObjectInterpolateSystem.CalculateUpdateFrames(m_Rendering.frameIndex, m_Rendering.frameTime, update.m_Index, out uint a, out uint b, out float t);
            InterpolatedTransform pose = ObjectInterpolateSystem.CalculateTransform(frames[(int)a], frames[(int)b], t);
            if (towedBy.HasValue)
                towedBy.Value.Hang(frontPosition, frontRotation, ref pose.m_Position, ref pose.m_Rotation);
            position = pose.m_Position;
            rotation = pose.m_Rotation;
            if (m_SwayLookup.TryGetComponent(entity, out Swaying sway))
            {
                float2 lean = sway.m_SwayPosition.xz;
                float angle = math.length(lean);
                if (angle > 1e-5f && math.isfinite(angle))
                {
                    lean /= angle;
                    quaternion q = quaternion.AxisAngle(new float3(-lean.y, 0f, lean.x), angle);
                    rotation *= new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
                }

                if (math.isfinite(sway.m_SwayPosition.y))
                    position += rotation * new Vector3(0f, sway.m_SwayPosition.y, 0f);
            }

            return math.isfinite(position.x) && math.isfinite(position.y) && math.isfinite(position.z) &&
                math.isfinite(rotation.x) && math.isfinite(rotation.y) && math.isfinite(rotation.z) && math.isfinite(rotation.w);
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            ReleaseAll(false);
            m_ModelByPrefab.Clear();
        }

        protected override void OnStopRunning()
        {
            foreach (Entry entry in m_Entries.Values)
                Show(entry, false);
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
            ReleaseAll(true);
            m_MaterialResources?.Dispose();
            m_MaterialResources = null;
        }

        private sealed class RetainedCleanup : IDisposable
        {
            readonly BusAiSignSystem m_Owner;
            internal RetainedCleanup(BusAiSignSystem owner) { m_Owner = owner; }
            public void Dispose() => m_Owner.ReleaseResources();
        }
    }
}
