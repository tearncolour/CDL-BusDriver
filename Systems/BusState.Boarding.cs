using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>Who is aboard, who boards and alights at the stop, and what the stops are expected to have.</summary>
    internal static partial class BusState
    {
        internal static int Passengers;
        internal static int Capacity;
        internal static int Waiting;
        internal static int Boarded;
        internal static int Alighted;
        internal static int StopBoarded;
        internal static int StopAlighted;

        // While the doors are open: riders still to board (walking to the doors, or waiting with room for them) and riders
        // the full bus leaves at the stop (BusStopZone.ToBoard).
        internal static int ToBoard;
        internal static int LeftBehind;

        internal static readonly List<StopDemand> Demand = new List<StopDemand>();
        internal static string DemandJson = "[]";
        internal static int Alighting;
        internal static SkipReason TargetSkip;

        private static void ResetBoarding()
        {
            Passengers = 0;
            Capacity = 0;
            Waiting = 0;
            Boarded = 0;
            Alighted = 0;
            StopBoarded = 0;
            StopAlighted = 0;
            ToBoard = 0;
            LeftBehind = 0;
            Demand.Clear();
            DemandJson = "[]";
            Alighting = 0;
            TargetSkip = SkipReason.None;
        }
    }
}
