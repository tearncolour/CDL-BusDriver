using System.Collections.Generic;
using System.Text;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The route sign of a ride: the stop the bus heads for, how far it is by the road and how much of the way from the stop before is done, as
    /// the shift's HUD shows them. Only read from the game: the shift moves the bus's target and path to the stop the player is at; a bus the
    /// game drives keeps its own, so nothing here writes to it.
    /// </summary>
    internal sealed partial class BusRideSystem
    {
        // One entry a waypoint of the line, in the line's order (the segments of its route are indexed the same way).
        private Entity m_RouteLine = Entity.Null;
        private Entity[] m_Waypoints = System.Array.Empty<Entity>();
        private Entity[] m_StopObjects = System.Array.Empty<Entity>();
        private bool[] m_IsStop = System.Array.Empty<bool>();
        private int[] m_Ordinal = System.Array.Empty<int>();
        private string[] m_StopNames = System.Array.Empty<string>();
        private readonly Dictionary<Entity, int> m_WaypointIndex = new Dictionary<Entity, int>();

        // The route line's curves to the stop the bus heads for (waypoint index; -1 none), their lengths and the sum.
        private int m_GuideStop = -1;
        private readonly List<Bezier4x3> m_GuideCurves = new List<Bezier4x3>();
        private readonly List<float> m_GuideLengths = new List<float>();
        private float m_GuideTotal;

        /// <summary>The ride is over: the next one reads its line again (a stop may have been renamed since).</summary>
        private void ForgetRoute()
        {
            m_RouteLine = Entity.Null;
            m_GuideStop = -1;
        }

        /// <summary>The line's waypoints, which of them are stops and the stops' names, once for each line a ride is on.</summary>
        private void ReadRoute(Entity line)
        {
            if (line == m_RouteLine)
                return;

            m_RouteLine = line;
            m_GuideStop = -1;
            m_WaypointIndex.Clear();
            var names = new StringBuilder("[");
            int count = EntityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteWaypoint> waypoints) ? waypoints.Length : 0;
            m_Waypoints = new Entity[count];
            m_StopObjects = new Entity[count];
            m_IsStop = new bool[count];
            m_Ordinal = new int[count];
            m_StopNames = new string[count];
            int real = 0;
            string last = "";
            var stopNames = new List<string>();
            for (int i = 0; i < count; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                m_Waypoints[i] = waypoint;
                m_WaypointIndex[waypoint] = i;
                m_Ordinal[i] = -1;
                if (!EntityManager.TryGetComponent(waypoint, out Connected connected) || connected.m_Connected == Entity.Null)
                    continue;

                string name = EntityNames.Text(m_NameSystem, connected.m_Connected) ?? "";
                m_IsStop[i] = true;
                m_StopObjects[i] = connected.m_Connected;
                m_StopNames[i] = name;
                m_Ordinal[i] = real++;
                stopNames.Add(name);
                last = name;
                if (names.Length > 1)
                    names.Append(',');
                names.Append('"').Append(name.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }

            BusRide.StopsJson = names.Append(']').ToString();
            BusRide.StopNames = stopNames.ToArray();
            BusRide.StopCount = real;
            BusRide.Terminus = TerminusOf(line, last);
        }

        /// <summary>The direction sign\x27s stop, as the shift gives it: the one TransitTimetables runs the line to, else the line\x27s last stop.</summary>
        private string TerminusOf(Entity line, string lastStop)
        {
            if (TransitTimetablesLink.TryTerminus(EntityManager, line, out Entity timetableStop, out _))
                for (int i = 0; i < m_IsStop.Length; i++)
                    if (m_IsStop[i] && m_StopObjects[i] == timetableStop)
                        return m_StopNames[i];
            return lastStop;
        }

        /// <summary>The stop the bus heads for and its distance by the road, four times a second with the rest of the ride\x27s readouts.</summary>
        private void UpdateRoute(Entity bus)
        {
            BusRide.HasRoute = false;
            BusRide.PrefixColour = LineLabel.Hex(LineLabel.ColorFor(BusRide.LinePrefix, Mod.Settings?.RoutePrefixColors));
            if (m_RouteLine != BusRide.Line)
                ReadRoute(BusRide.Line);

            if (!EntityManager.TryGetComponent(bus, out Target target) || !m_WaypointIndex.TryGetValue(target.m_Target, out int heading))
                return;
            int stop = BusRideRules.NextStop(m_IsStop, heading);
            if (stop < 0)
                return;

            if (stop != m_GuideStop)
                BuildGuide(stop);

            BusRide.HasRoute = true;
            BusRide.Target = m_Ordinal[stop];
            BusRide.NextIndex = m_Ordinal[stop] + 1;
            BusRide.Next = m_StopNames[stop] ?? "";
            float metres = RemainingTo(stop, out float done);
            BusRide.Distance = BusRideRules.ShownDistance(metres);
            BusRide.Approaching = BusRideRules.Approaching(metres);
            BusRide.Progress = (int)System.Math.Round(100f * BusRideRules.Progress(done, m_GuideTotal));
        }

        /// <summary>The route line from the stop before to this one, as the shift\x27s guide has it (without the cut at the stop line: a few metres).</summary>
        private void BuildGuide(int stop)
        {
            m_GuideStop = stop;
            m_GuideCurves.Clear();
            m_GuideLengths.Clear();
            m_GuideTotal = 0f;
            if (!EntityManager.TryGetBuffer(m_RouteLine, true, out DynamicBuffer<RouteSegment> segments) || segments.Length != m_IsStop.Length ||
                !BusRideRules.SegmentsTo(m_IsStop, stop, out int first, out int last))
                return;

            for (int k = first; ; k = (k + 1) % segments.Length)
            {
                if (EntityManager.TryGetBuffer(segments[k].m_Segment, true, out DynamicBuffer<CurveElement> curves))
                    for (int i = 0; i < curves.Length; i++)
                    {
                        float length = MathUtils.Length(curves[i].m_Curve);
                        m_GuideCurves.Add(curves[i].m_Curve);
                        m_GuideLengths.Add(length);
                        m_GuideTotal += length;
                    }

                if (k == last)
                    break;
            }
        }

        /// <summary>
        /// The distance from the bus along the guide to the stop, and how far along it the bus is. A bus more than 30 m from the guide (a detour, a
        /// bus lane the line does not run on) is measured as the crow flies to the stop.
        /// </summary>
        private float RemainingTo(int stop, out float done)
        {
            done = 0f;
            if (!Core.TryGetBodyPose(out UnityEngine.Vector3 body, out _))
                return 0f;

            float3 position = new float3(body.x, body.y, body.z);
            int best = -1;
            float bestDistance = float.MaxValue, bestT = 0f;
            for (int i = 0; i < m_GuideCurves.Count; i++)
            {
                Bezier4x3 curve = m_GuideCurves[i];
                float distance = MathUtils.Distance(curve.xz, position.xz, out float t);
                // a road above or below is not the one the bus is on (a bridge, a tunnel)
                distance += math.max(0f, math.abs(MathUtils.Position(curve, t).y - position.y) - 2f) * 3f;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    bestT = t;
                }
            }

            if (best >= 0 && bestDistance <= 30f && m_GuideTotal > 0.1f)
            {
                for (int i = 0; i < best; i++)
                    done += m_GuideLengths[i];
                done += MathUtils.Length(m_GuideCurves[best], new Bounds1(0f, bestT));
                return math.max(0f, m_GuideTotal - done);
            }

            done = 0f;
            return EntityManager.TryGetComponent(m_Waypoints[stop], out Position at) ? math.distance(at.m_Position.xz, position.xz) : 0f;
        }
    }
}
