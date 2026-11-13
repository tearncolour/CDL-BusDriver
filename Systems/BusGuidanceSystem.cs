using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Game.Rendering;
using Game.Simulation;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Paints the guidance on the road during a shift, only for the line being driven: a boarding zone on the lane of every
    /// stop (the next stop bright and pulsing, or pale and still when nobody gets on or off there and it may be skipped;
    /// the zone the bus stands in green, solid while the doors are open), a marker
    /// at the stop point, and the line's own path to the next stop as a dashed line. Redrawn every frame into the game's
    /// overlay buffer. On the ground it is projected onto the road like the game's own overlays; on bridges, elevated roads,
    /// ramps and in tunnels it is drawn at the road's own height instead, because projected overlays only reach the terrain.
    /// </summary>
    public sealed partial class BusGuidanceSystem : GameSystemBase
    {
        private const float kZoneWidth = 3.0f;

        private static readonly Color kTarget = new Color(1f, 0.83f, 0.3f, 1f);
        private static readonly Color kInside = new Color(0.55f, 0.86f, 0.27f, 1f);
        private static readonly Color kGuide = new Color(0.29f, 0.76f, 0.95f, 1f);
        private static readonly Color kSkip = new Color(0.78f, 0.84f, 0.88f, 1f);

        private OverlayRenderSystem m_Overlay;
        private TerrainSystem m_TerrainSystem;
        private TerrainHeightData m_Heights;
        private bool m_HasHeights;
        private const float kElevatedMeters = 1.5f;
        private const float kLift = 0.15f;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
        }

        /// <summary>Projected on ground roads; on a deck or in a tunnel drawn just above the road at its own height.</summary>
        private OverlayRenderSystem.StyleFlags Style(ref Bezier4x3 curve)
        {
            if (!m_HasHeights)
                return OverlayRenderSystem.StyleFlags.Projected;

            float off = math.max(OffGround(curve.a), math.max(OffGround(MathUtils.Position(curve, 0.5f)), OffGround(curve.d)));
            if (off < kElevatedMeters)
                return OverlayRenderSystem.StyleFlags.Projected;

            float3 lift = new float3(0f, kLift, 0f);
            curve = new Bezier4x3(curve.a + lift, curve.b + lift, curve.c + lift, curve.d + lift);
            return 0;
        }

        private OverlayRenderSystem.StyleFlags Style(ref float3 point)
        {
            if (!m_HasHeights || OffGround(point) < kElevatedMeters)
                return OverlayRenderSystem.StyleFlags.Projected;

            point.y += kLift;
            return 0;
        }

        /// <summary>
        /// The zone the bus stands in (and the next stop's as the bus pulls in) is drawn at the road's own height instead of
        /// projected: a projected overlay paints everything below it, the bus and its interior included (the whole cab turned
        /// green at a stop), while one drawn at road height stays under the floor like the road markings.
        /// </summary>
        private static OverlayRenderSystem.StyleFlags OnRoad(ref Bezier4x3 curve)
        {
            float3 lift = new float3(0f, kRoadLift, 0f);
            curve = new Bezier4x3(curve.a + lift, curve.b + lift, curve.c + lift, curve.d + lift);
            return 0;
        }

        private static OverlayRenderSystem.StyleFlags OnRoad(ref float3 point)
        {
            point.y += kRoadLift;
            return 0;
        }

        private const float kRoadLift = 0.06f;

        // ------------------------------------------------------------------ the bus's footprint
        // The game draws its overlays over everything below them - projected ones onto any surface, road-height ones without
        // being hidden by what stands on them - so nothing is drawn where the bus (each section) stands, plus a margin.

        private struct Footprint
        {
            internal float2 Centre;
            internal float2 Forward;
            internal float HalfWidth;
            internal float HalfLength;
            internal float Radius;
        }

        private const float kFootprintMargin = 0.5f;
        private const float kClipStep = 0.6f;
        private readonly List<Footprint> m_Footprints = new List<Footprint>(4);
        private readonly List<float2> m_Runs = new List<float2>(8);

        // The bus's sections and their prefabs' bounds, read once per bus and again every 2 s. Their poses each frame are the ones the
        // driving mod drew them at (Core), so the overlay reads no vehicle transform on the main thread: that read waited for the game's
        // jobs writing transforms (this system was 0.29 to 0.52 ms a frame in its own log of 22 October, for one or two curves).
        private const float kSectionsSeconds = 2f;
        private Entity m_SectionsBus = Entity.Null;
        private float m_SectionsRead;
        private readonly List<(Entity vehicle, Bounds3 bounds)> m_Sections = new List<(Entity, Bounds3)>(4);

        private void CollectFootprints()
        {
            m_Footprints.Clear();
            Entity bus = BusState.Bus;
            if (bus == Entity.Null || !EntityManager.Exists(bus))
                return;

            float now = UnityEngine.Time.unscaledTime;
            if (bus != m_SectionsBus || now >= m_SectionsRead + kSectionsSeconds)
                ReadSections(bus, now);
            bool driven = Core.HasCab && Core.IsDriving() && Core.Vehicle() == bus;
            for (int i = 0; i < m_Sections.Count; i++)
            {
                (Entity vehicle, Bounds3 bounds) = m_Sections[i];
                if (SectionPose(vehicle, bus, driven, out float3 position, out quaternion rotation))
                    AddFootprint(position, rotation, bounds);
            }
        }

        private void ReadSections(Entity bus, float now)
        {
            m_SectionsBus = bus;
            m_SectionsRead = now;
            m_Sections.Clear();
            if (EntityManager.TryGetBuffer(bus, true, out DynamicBuffer<LayoutElement> layout) && layout.Length > 0)
            {
                for (int i = 0; i < layout.Length; i++)
                    AddSection(layout[i].m_Vehicle);
            }
            else
            {
                AddSection(bus);
            }
        }

        private void AddSection(Entity vehicle)
        {
            if (vehicle != Entity.Null && EntityManager.TryGetComponent(vehicle, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out ObjectGeometryData geometry))
                m_Sections.Add((vehicle, geometry.m_Bounds));
        }

        /// <summary>Where a section is drawn this frame: the driven bus's body pose (a towed section's own) from the driving mod; without
        /// one (not driven, or no pose this frame) its transform, as before.</summary>
        private bool SectionPose(Entity vehicle, Entity bus, bool driven, out float3 position, out quaternion rotation)
        {
            if (driven)
            {
                bool posed = vehicle == bus ? Core.TryGetBodyPose(out Vector3 p, out Quaternion r) : Core.TryGetRenderPose(vehicle, out p, out r);
                if (posed)
                {
                    position = p;
                    rotation = r;
                    return true;
                }
            }

            if (EntityManager.Exists(vehicle) && EntityManager.TryGetComponent(vehicle, out Game.Objects.Transform transform))
            {
                position = transform.m_Position;
                rotation = transform.m_Rotation;
                return true;
            }

            position = default;
            rotation = quaternion.identity;
            return false;
        }

        private void AddFootprint(float3 position, quaternion rotation, Bounds3 b)
        {
            float3 forward = math.mul(rotation, new float3(0f, 0f, 1f));
            float2 f = math.normalizesafe(forward.xz, new float2(0f, 1f));
            float2 r = new float2(f.y, -f.x);
            float3 centre = MathUtils.Center(b);
            float halfWidth = 0.5f * (b.max.x - b.min.x) + kFootprintMargin;
            float halfLength = 0.5f * (b.max.z - b.min.z) + kFootprintMargin;
            m_Footprints.Add(new Footprint
            {
                Centre = position.xz + r * centre.x + f * centre.z,
                Forward = f,
                HalfWidth = halfWidth,
                HalfLength = halfLength,
                Radius = math.sqrt(halfWidth * halfWidth + halfLength * halfLength),
            });
        }

        private bool Covered(float3 point)
        {
            for (int i = 0; i < m_Footprints.Count; i++)
            {
                Footprint fp = m_Footprints[i];
                float2 d = point.xz - fp.Centre;
                float along = math.dot(d, fp.Forward), side = math.dot(d, new float2(fp.Forward.y, -fp.Forward.x));
                if (math.abs(along) <= fp.HalfLength && math.abs(side) <= fp.HalfWidth)
                    return true;
            }

            return false;
        }

        /// <summary>The parts of a curve (as t ranges) that lie outside the bus, sampled every 0.6 m (with half the width as margin).</summary>
        private List<float2> Uncovered(Bezier4x3 curve, float halfWidth)
        {
            m_Runs.Clear();
            if (m_Footprints.Count == 0)
            {
                m_Runs.Add(new float2(0f, 1f));
                return m_Runs;
            }

            // Far from every part of the bus (the box of the curve's control points, widened by the strip, against each part's
            // circumscribed circle): nothing to cut out, no need to sample it.
            float2 lo = math.min(math.min(curve.a.xz, curve.b.xz), math.min(curve.c.xz, curve.d.xz)) - halfWidth;
            float2 hi = math.max(math.max(curve.a.xz, curve.b.xz), math.max(curve.c.xz, curve.d.xz)) + halfWidth;
            bool near = false;
            for (int i = 0; i < m_Footprints.Count && !near; i++)
            {
                Footprint fp = m_Footprints[i];
                float2 outside = math.max(math.max(lo - fp.Centre, fp.Centre - hi), 0f);
                near = math.lengthsq(outside) <= fp.Radius * fp.Radius;
            }

            if (!near)
            {
                m_Runs.Add(new float2(0f, 1f));
                return m_Runs;
            }

            float length = math.distance(curve.a, curve.b) + math.distance(curve.b, curve.c) + math.distance(curve.c, curve.d);
            int steps = math.clamp((int)math.ceil(length / kClipStep), 1, 400);
            float start = -1f;
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                float3 p = MathUtils.Position(curve, t);
                bool covered = Covered(p);
                if (!covered && halfWidth > 0f)
                {
                    // the strip's edges too: a wide zone must not reach under the bus with its side
                    float3 tangent = MathUtils.Tangent(curve, t);
                    float2 side = math.normalizesafe(new float2(tangent.z, -tangent.x)) * halfWidth;
                    covered = Covered(p + new float3(side.x, 0f, side.y)) || Covered(p - new float3(side.x, 0f, side.y));
                }

                if (!covered && start < 0f)
                    start = t;
                else if (covered && start >= 0f)
                {
                    if (t > start + 0.001f)
                        m_Runs.Add(new float2(start, math.max(start, t - 1f / steps)));
                    start = -1f;
                }
            }

            if (start >= 0f && start < 1f)
                m_Runs.Add(new float2(start, 1f));
            return m_Runs;
        }
        private const float kNearBusMeters = 60f;
        private const float kGuideAheadMeters = 500f;

        private static readonly ProfilerMarker s_GuideMarker = new ProfilerMarker("CDLC.Guidance.GuideLine");
        private static readonly ProfilerMarker s_ZonesMarker = new ProfilerMarker("CDLC.Guidance.Zones");

        private float OffGround(float3 point)
        {
            float ground = TerrainUtils.SampleHeight(ref m_Heights, point);
            return math.isfinite(ground) ? math.abs(point.y - ground) : 0f;
        }

        private static readonly Bezier4x3[] s_NoCurves = new Bezier4x3[0];

        protected override void OnUpdate()
        {
            BusSettings settings = Mod.Settings;
            if (!BusState.InShift || settings == null || (!settings.ShowZones && !settings.ShowRoute && !settings.Immersive) || BusState.Stops.Count == 0)
                return;

            try
            {
                Draw(settings);
            }
            catch (System.Exception ex)
            {
                // A fault here repeats every frame: logged with a count, at most every 30 s.
                m_Faults++;
                float now = UnityEngine.Time.unscaledTime;
                if (m_Faults == 1 || now >= m_NextFaultLog)
                {
                    m_NextFaultLog = now + 30f;
                    Mod.log.Warn($"Bus guidance overlay failed ({m_Faults}x): {ex}");
                }
            }
        }

        private int m_Faults;
        private float m_NextFaultLog;

        // The frame's drawing as commands, written into the game's overlay buffer by a job after the game's other overlay writers
        // (as the game's own overlay systems do): drawing straight into the buffer on the main thread had to wait for those jobs
        // first, every frame (17 October, the user's measurement: this system 0.26 ms a frame with a K18).
        private struct DrawCommand
        {
            internal bool Dashed;
            internal Color Color;
            internal OverlayRenderSystem.StyleFlags Style;
            internal Bezier4x3 Curve;
            internal float Width, Dash, Gap;
        }

        private struct DrawJob : IJob
        {
            public OverlayRenderSystem.Buffer Buffer;
            [Unity.Collections.ReadOnly] public NativeArray<DrawCommand> Commands;

            public void Execute()
            {
                for (int i = 0; i < Commands.Length; i++)
                {
                    DrawCommand c = Commands[i];
                    if (c.Dashed)
                        Buffer.DrawDashedCurve(c.Color, c.Color, 0f, c.Style, c.Curve, c.Width, c.Dash, c.Gap);
                    else
                        Buffer.DrawCurve(c.Color, c.Color, 0f, c.Style, c.Curve, c.Width);
                }
            }
        }

        private NativeList<DrawCommand> m_Commands;
        private JobHandle m_DrawJob;

        // The update's own time while in a shift, logged every 60 s.
        private long m_PerfTicks, m_PerfWorst;
        private int m_PerfFrames, m_PerfCommands;
        private float m_PerfNext;

        protected override void OnDestroy()
        {
            m_DrawJob.Complete();
            if (m_Commands.IsCreated)
                m_Commands.Dispose();
            base.OnDestroy();
        }

        private void DrawCurve(Color color, OverlayRenderSystem.StyleFlags style, Bezier4x3 curve, float width) =>
            m_Commands.Add(new DrawCommand { Color = color, Style = style, Curve = curve, Width = width });

        private void DrawDashedCurve(Color color, OverlayRenderSystem.StyleFlags style, Bezier4x3 curve, float width, float dash, float gap) =>
            m_Commands.Add(new DrawCommand { Dashed = true, Color = color, Style = style, Curve = curve, Width = width, Dash = dash, Gap = gap });

        private void Draw(BusSettings settings)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            // last frame's job read the commands; it has long finished
            m_DrawJob.Complete();
            if (!m_Commands.IsCreated)
                m_Commands = new NativeList<DrawCommand>(64, Allocator.Persistent);
            m_Commands.Clear();
            Collect(settings);
            if (m_Commands.Length > 0)
            {
                OverlayRenderSystem.Buffer buffer = m_Overlay.GetBuffer(out JobHandle dependencies);
                m_DrawJob = new DrawJob { Buffer = buffer, Commands = m_Commands.AsArray() }.Schedule(dependencies);
                m_Overlay.AddBufferWriter(m_DrawJob);
            }

            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            m_PerfTicks += ticks;
            m_PerfWorst = System.Math.Max(m_PerfWorst, ticks);
            m_PerfFrames++;
            m_PerfCommands += m_Commands.Length;
            float now = UnityEngine.Time.unscaledTime;
            if (now < m_PerfNext)
                return;
            if (m_PerfNext > 0f)
            {
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Mod.log.Info($"Bus guidance: {m_PerfTicks * ms / System.Math.Max(1, m_PerfFrames):0.000} ms a frame on average, {m_PerfWorst * ms:0.0} ms at most, over {m_PerfFrames} frames; " +
                    $"{m_PerfCommands / (float)System.Math.Max(1, m_PerfFrames):0.0} curves a frame, drawn by a job.");
            }
            m_PerfNext = now + 60f;
            m_PerfTicks = m_PerfWorst = 0;
            m_PerfFrames = m_PerfCommands = 0;
        }

        private void Collect(BusSettings settings)
        {
            m_HasHeights = false;
            if (m_TerrainSystem != null)
            {
                m_Heights = m_TerrainSystem.GetHeightData();
                m_HasHeights = m_Heights.isCreated;
            }

            CollectFootprints();
            float pulse = 0.55f + 0.45f * Mathf.Sin(UnityEngine.Time.unscaledTime * 4f);
            // Immersive mode: no guide line and no other stops' zones: the next stop's zone only, fainter (the shift still tracks it all)
            bool immersive = settings.Immersive;
            if (settings.ShowRoute && !immersive && BusState.GuideCurves.Count > 0)
            {
                using var guideScope = s_GuideMarker.Auto();
                // A thin, soft dashed line on the road, only ahead of the bus (the game's own line overlay stays off).
                Color guide = kGuide;
                guide.a = 0.42f;
                int from = BusState.OnGuide ? BusState.GuideFrom : 0;
                float drawn = 0f;
                for (int i = from; i < BusState.GuideCurves.Count; i++)
                {
                    // Only the stretch ahead that can be made out; the rest of a long path is not drawn until the bus gets near.
                    if (BusState.OnGuide && drawn > kGuideAheadMeters)
                        break;

                    Bezier4x3 curve = BusState.GuideCurves[i];
                    if (BusState.OnGuide && i == from)
                    {
                        drawn += BusState.GuideLengths[i] * (1f - BusState.GuideFromT);
                        if (BusState.GuideFromT > 0.98f)
                            continue;
                        curve = MathUtils.Cut(curve, new float2(BusState.GuideFromT, 1f));
                    }
                    else
                    {
                        drawn += BusState.GuideLengths[i];
                    }

                    // At road height like the near stop zones (projected, the line painted every vehicle ahead on it), and
                    // never under the bus itself.
                    OverlayRenderSystem.StyleFlags style = OnRoad(ref curve);
                    foreach (float2 run in Uncovered(curve, 0.25f))
                        DrawDashedCurve(guide, style, run.x <= 0f && run.y >= 1f ? curve : MathUtils.Cut(curve, run), 0.5f, 2.5f, 3f);
                }
            }

            if (!settings.ShowZones && !immersive)
                return;

            using var zonesScope = s_ZonesMarker.Auto();
            Color line = BusState.LineColor;
            for (int i = 0; i < BusState.Stops.Count; i++)
            {
                StopInfo stop = BusState.Stops[i];
                if (!stop.Valid)
                    continue;

                bool target = i == BusState.TargetIndex;
                bool inside = i == BusState.ZoneIndex;
                bool skip = target && BusState.TargetSkip != SkipReason.None;
                if (!target && !inside && (!settings.ShowAllStops || immersive))
                    continue;
                if (immersive && !target && !(inside && BusState.ZoneIndex == BusState.TargetIndex))
                    continue;

                Color fill;
                if (inside)
                {
                    fill = kInside;
                    fill.a = BusState.DoorsOpen ? 0.5f : (BusState.Stopped ? 0.4f : 0.28f);
                }
                else if (skip)
                {
                    fill = kSkip;
                    fill.a = 0.14f;
                }
                else if (target)
                {
                    fill = kTarget;
                    fill.a = 0.16f + 0.16f * pulse;
                }
                else
                {
                    fill = line;
                    fill.a = 0.16f;
                }

                if (immersive)
                    fill.a *= 0.6f;

                // Fill only (no outline), so the pieces of a curved zone join without seams. No stop line and no marker: the
                // bus only has to stand in the zone (BusStopZone), wherever along it.
                bool underBus = inside || (target && BusState.DistanceToTarget < kNearBusMeters);
                foreach (Bezier4x3 zonePiece in stop.ZoneCurves ?? s_NoCurves)
                {
                    Bezier4x3 piece = zonePiece;
                    OverlayRenderSystem.StyleFlags pieceStyle = underBus ? OnRoad(ref piece) : Style(ref piece);
                    foreach (float2 run in Uncovered(piece, 0.5f * kZoneWidth))
                        DrawCurve(fill, pieceStyle, run.x <= 0f && run.y >= 1f ? piece : MathUtils.Cut(piece, run), kZoneWidth);
                }
            }
        }
    }
}
