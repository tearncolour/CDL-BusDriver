using System;
using System.Collections.Generic;
using System.Text;
using CDLC.BusDriver.Interior;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// What a station board says about a line's buses (BusStationBoardSystem), as a Chinese electronic stop board says it ("距本站 N 站"): a bus
    /// boarding at the bay is at the stop; a bus whose next stop is the bay is arriving; else the stops between it and the bay (its next stop
    /// counted, the bay not); no bus on the way: none. Counted on the line's waypoints in their order (a line goes round them), only those at a stop.
    /// The game gives no arrival times worth a clock (its buses' travel seconds are not the clock's minutes): the boards count stops. Pure; tested offline.
    /// </summary>
    internal static class StationBoardRules
    {
        /// <summary>The stops a bus heading to waypoint <paramref name="target"/> passes before waypoint <paramref name="bay"/> (round the line);
        /// -1 when either is not a waypoint of the line.</summary>
        internal static int StopsBetween(IReadOnlyList<bool> isStop, int target, int bay)
        {
            int n = isStop?.Count ?? 0;
            if (n == 0 || target < 0 || target >= n || bay < 0 || bay >= n)
                return -1;
            int count = 0;
            for (int i = target, k = 0; i != bay && k < n; i = (i + 1) % n, k++)
                if (isStop[i])
                    count++;
            return count;
        }

        /// <summary>The status of one bus: boarding at the bay, or the stops before it (StopsBetween; negative: not on the way).</summary>
        internal static (BoardStatus status, int stops) OfBus(bool boardingHere, int stopsBetween)
        {
            if (boardingHere)
                return (BoardStatus.AtStop, 0);
            if (stopsBetween < 0)
                return (BoardStatus.NoBus, 0);
            return stopsBetween == 0 ? (BoardStatus.Arriving, 0) : (BoardStatus.Stops, stopsBetween);
        }

        /// <summary>How soon: at the stop, arriving, then by the stops, no bus last.</summary>
        internal static int Rank(BoardStatus status, int stops)
        {
            switch (status)
            {
                case BoardStatus.AtStop: return 0;
                case BoardStatus.Arriving: return 1;
                case BoardStatus.Stops: return 2 + Math.Max(0, Math.Min(stops, 9999));
                default: return int.MaxValue;
            }
        }

        /// <summary>The nearer of two buses of a line.</summary>
        internal static (BoardStatus status, int stops) Nearer((BoardStatus status, int stops) a, (BoardStatus status, int stops) b) =>
            Rank(b.status, b.stops) < Rank(a.status, a.stops) ? b : a;

        /// <summary>A bay's lines in the boards' order: the soonest first, then by their labels (numbers by value: "2" before "10").</summary>
        internal static void Sort(List<BoardRoute> routes) => routes.Sort((a, b) =>
        {
            int r = Rank(a.Status, a.Stops).CompareTo(Rank(b.Status, b.Stops));
            return r != 0 ? r : CompareLabels(a.Label, b.Label);
        });

        internal static int CompareLabels(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                    continue;
                }

                if (a[i] != b[j])
                    return a[i].CompareTo(b[j]);
                i++;
                j++;
            }

            return (a.Length - i).CompareTo(b.Length - j);
        }

        /// <summary>A line's colour as an LED shows it: the hue kept, its brightest channel full (a dark line colour would be a dark LED; grey becomes
        /// white, black white too); sRGB 0..1.</summary>
        internal static float3 LedColour(float3 srgb)
        {
            srgb = math.saturate(srgb);
            float max = math.cmax(srgb);
            return max < 0.05f ? new float3(1f, 1f, 1f) : math.saturate(srgb / max);
        }

        /// <summary>The key of a bay's content (a board is composed again only when it changed).</summary>
        internal static string Key(IReadOnlyList<BoardBay> bays)
        {
            var sb = new StringBuilder(256);
            foreach (BoardBay b in bays)
            {
                sb.Append(b.Bay).Append(':').Append(b.Waiting).Append('{');
                foreach (BoardRoute r in b.Routes)
                    sb.Append(r.Label).Append('\u0001').Append(r.Suffix).Append('\u0001').Append((int)r.LineInk).Append('\u0001').Append(r.Terminus).Append('\u0001')
                      .Append(r.English).Append('\u0001').Append((int)r.Status).Append('\u0001').Append(r.Stops).Append('\u0001').Append(r.Waiting).Append('|');
                sb.Append('}');
            }

            return sb.ToString();
        }

        /// <summary>The page a board shows now: a page every <paramref name="seconds"/>, from the time.</summary>
        internal static int Page(double time, double seconds) => seconds <= 0 || double.IsNaN(time) ? 0 : (int)(Math.Max(0, time) / seconds % 100000);
    }
}
