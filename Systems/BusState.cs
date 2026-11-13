using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    internal enum ShiftState
    {
        Idle,
        Dispatching,
        TakingOver,
        Driving,
        DoorsOpening,
        DoorsOpen,
        DoorsClosing
    }

    /// <summary>
    /// One stop of the line being driven. The boarding zone is the last stretch of the line's own path into the stop (and a
    /// little beyond it), so it lies exactly where the game's buses stand, also in bus bays.
    /// </summary>
    internal struct StopInfo
    {
        internal Entity Waypoint;
        internal Entity Stop;
        internal Entity Lane;
        internal Bezier4x3[] ZoneCurves;
        internal float3 Position;
        internal float3 Direction;
        internal float3 Right;
        internal string Name;
        internal bool Valid;

        // Which side of the box axis the stop (the kerb) is on: +1 right, -1 left, 0 unknown.
        internal float KerbSide;

        // Position among the line's real stops (0-based); -1 for a plain route waypoint that only shapes the line.
        internal int Ordinal;

        internal bool IsStop => Stop != Entity.Null;
    }

    /// <summary>Who gets on and off at a stop, as far as the bus knows now.</summary>
    internal struct StopDemand
    {
        internal int Waiting;
        internal int Alighting;

        // Why the stop may be skipped: none, nobody to pick up or set down, or the bus is full and nobody gets off.
        internal SkipReason Skip;
    }

    internal enum SkipReason
    {
        None,
        Empty,
        Full
    }

    /// <summary>Requests from the panel and the keys, handled by the shift system on the next simulation step.</summary>
    internal enum ShiftRequest
    {
        None,
        Dispatch,
        TakeOver,
        End,
        ToggleDoors,
        ForceClose
    }

    /// <summary>
    /// State of the current shift, shared by the simulation, rendering and UI systems (main thread only). One static class in four
    /// files by what the fields are about: the shift itself and its cross-system channels (here), the stops and the boarding zone
    /// (BusState.Stops.cs), who is aboard (BusState.Boarding.cs) and the guidance along the path (BusState.Guidance.cs). Each file
    /// resets its own fields (ResetShift, ResetStops, ResetBoarding, ResetGuidance), so a new field is reset beside its declaration.
    /// </summary>
    internal static partial class BusState
    {
        internal static ShiftState State = ShiftState.Idle;
        internal static Entity Line = Entity.Null;
        internal static Entity Bus = Entity.Null;
        internal static float Progress;
        internal static string LineName = "";
        internal static string LinePrefix = "";      // the route label's prefix (K, BRT, 夜 ...: LineLabel), drawn in its own colour
        internal static string LineNumber = "";      // the route label's number ("3", "12A")
        // Per shift, set when it starts and only read during it (InShift), so Reset leaves it.
        internal static UnityEngine.Color32 LineColor = new UnityEngine.Color32(75, 195, 241, 255);

        internal static long Fares;
        internal static int TicketPrice;
        internal static int Served;
        internal static int Skipped;
        internal static int Missed;
        internal static int RealStops;

        // A bus from the depot on its way to the first stop of the shift: that stop begins the trip, so it is not announced
        // as the final stop even when it is the terminus. Off once the bus leaves a stop or drives past one.
        internal static bool DepotLeg;

        // Per real stop (ordinal order): the other lines calling there, as JSON [[["12","#e8574a","Bus"],...],...], and the
        // line's terminus name for the direction sign.
        internal static string TransfersJson = "[]";
        internal static string Terminus = "";
        // the line's other end for a printed board ("origin <-> terminus"): its first real stop, or its last when the terminus is the first
        internal static string Origin = "";

        // Channels between systems, not part of one shift: Reset leaves them. The panel's and the keys' requests are taken by the shift
        // system on its next step; the message is what the panel shows after the shift ended, set after the Reset; DrivingBus is set
        // by BusCabSystem every frame.
        internal static string Message = "";
        internal static string MessageArg = "";
        internal static uint MessageFrame;

        internal static ShiftRequest Request = ShiftRequest.None;
        internal static Entity RequestedLine = Entity.Null;

        internal static bool InShift => State != ShiftState.Idle && State != ShiftState.Dispatching;

        /// <summary>The player is driving a bus (any: ours, the game's, a workshop's), on a shift or not. BusCabSystem sets it each frame.</summary>
        internal static bool DrivingBus;
        internal static bool DoorsOpen => State == ShiftState.DoorsOpening || State == ShiftState.DoorsOpen || State == ShiftState.DoorsClosing;

        internal static void SetMessage(string id, string arg = "")
        {
            Message = id ?? "";
            MessageArg = arg ?? "";
            MessageFrame = (uint)UnityEngine.Time.frameCount;
        }

        internal static void Reset()
        {
            ResetShift();
            ResetStops();
            ResetBoarding();
            ResetGuidance();
        }

        private static void ResetShift()
        {
            State = ShiftState.Idle;
            Line = Entity.Null;
            Bus = Entity.Null;
            Progress = 0f;
            Fares = 0;
            TicketPrice = 0;
            Served = 0;
            Skipped = 0;
            Missed = 0;
            RealStops = 0;
            DepotLeg = false;
            TransfersJson = "[]";
            Terminus = "";
            Origin = "";
            LineName = "";
            LinePrefix = "";
            LineNumber = "";
        }
    }
}
