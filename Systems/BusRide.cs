using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>Requests from the panel, taken by the ride system on its next update.</summary>
    internal enum RideRequest
    {
        None,
        Ride,
        Next,
        Leave
    }

    /// <summary>
    /// The passenger mode: the player rides a bus the game keeps driving (CSL Driving Life Core API 17) and sees it from inside, with the
    /// bus's camera positions, its doors and wheels moving and the passengers on board. What the panel asks for and what is known about the
    /// ride, shared by the ride system (BusRideSystem, which owns it), the bus model (BusCabSystem) and the panel (BusUiSystem); main thread only.
    /// Not part of a shift: a ride has no stops to serve, no fares and no doors of its own.
    /// </summary>
    internal static class BusRide
    {
        internal static RideRequest Request;
        internal static Entity RequestedLine = Entity.Null;

        /// <summary>The bus ridden: its front unit, the vehicle the driving mod follows. Entity.Null when not riding.</summary>
        internal static Entity Bus = Entity.Null;
        internal static Entity Line = Entity.Null;
        internal static string LineName = "";
        internal static string LinePrefix = "";
        internal static string LineNumber = "";
        internal static Color32 LineColor = new Color32(75, 195, 241, 255);
        internal static int Passengers;
        internal static int Capacity;

        /// <summary>The bus is boarding at a stop: its doors open, the doors' sounds play and the people come and go one by one.</summary>
        internal static bool DoorsOpen;

        // The route sign at the foot of the screen (the shift's sign and line strip, read from the bus without touching it): the stop it
        // heads for and how far it is, by the road. HasRoute is false while the bus is not heading for one of its line's stops.
        internal static bool HasRoute, Approaching;
        internal static string Next = "", Terminus = "", StopsJson = "[]", PrefixColour = "";
        internal static int NextIndex, Target = -1, StopCount, Progress, Distance;

        /// <summary>The names of the line's stops in its order (the stops only, so an index of it is a <see cref="Target"/>): the announcements make their voices ahead from them.</summary>
        internal static string[] StopNames = System.Array.Empty<string>();

        /// <summary>A ride has just begun: the bus model puts the camera at a passenger's seat once it knows the bus's camera positions.</summary>
        internal static bool NeedsSeat;

        internal static bool Riding => Bus != Entity.Null;

        internal static void Clear()
        {
            Bus = Entity.Null;
            Line = Entity.Null;
            LineName = LinePrefix = LineNumber = "";
            Passengers = Capacity = 0;
            DoorsOpen = false;
            NeedsSeat = false;
            HasRoute = Approaching = false;
            Next = Terminus = PrefixColour = "";
            StopsJson = "[]";
            StopNames = System.Array.Empty<string>();
            NextIndex = StopCount = Progress = Distance = 0;
            Target = -1;
        }
    }
}
