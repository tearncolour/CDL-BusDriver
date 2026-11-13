using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>The stops of the line being driven, the next stop and the boarding zone around it.</summary>
    internal static partial class BusState
    {
        // The boarding zone (its size, what counts as in it, when a stop counts as driven past): BusStopZone.
        internal const float kStoppedMps = 0.4f;

        // Driving past the next stop counts only on the stop's own road: within 8 m of the box axis, the same way, the same level.
        internal const float kPassedLateral = 8f;

        // Off the line's route when the nearest point of its path to the next stop is more than 15 m away.
        internal const float kOffRouteMeters = 15f;

        internal static readonly List<StopInfo> Stops = new List<StopInfo>();
        internal static string StopNamesJson = "[]";
        internal static int TargetIndex = -1;
        // Set again for every bus taken (BusShiftSystem), so Reset leaves it.
        internal static float FrontOffset = 6f;
        internal static float TargetFrontAlong;
        internal static float TargetLateral;

        // Into the next stop's box: how far to drive on (+) or reverse (−) for the middle and the front door to be in it
        // lengthwise (0 when they are), and whether the bus is beyond the box's far end (the way the road runs).
        internal static float TargetZoneMove;
        internal static bool TargetZonePast;
        internal static int ZoneIndex = -1;
        internal static bool CanOpen;
        internal static bool Stopped;
        internal static float DistanceToTarget;
        internal static float BearingToTarget;
        internal static float ZoneBehind = BusStopZone.kBehindMinMeters;

        // Signed distance the bus centre should move towards the kerb to sit on the box axis (negative: away from it).
        internal static float KerbGap;

        internal static StopInfo Target => TargetIndex >= 0 && TargetIndex < Stops.Count ? Stops[TargetIndex] : default;

        private static void ResetStops()
        {
            Stops.Clear();
            StopNamesJson = "[]";
            TargetIndex = -1;
            ZoneIndex = -1;
            CanOpen = false;
            Stopped = false;
            DistanceToTarget = 0f;
            BearingToTarget = 0f;
            TargetFrontAlong = 0f;
            TargetLateral = 0f;
            TargetZoneMove = 0f;
            TargetZonePast = false;
            ZoneBehind = BusStopZone.kBehindMinMeters;
            KerbGap = 0f;
        }
    }
}
