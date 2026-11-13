using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>The line's path to the next stop: the dashed guide, where the bus is on it, and the turn ahead.</summary>
    internal static partial class BusState
    {
        internal static readonly List<Bezier4x3> GuideCurves = new List<Bezier4x3>();
        internal static readonly List<float> GuideLengths = new List<float>();
        /// <summary>xz box (min x, min z, max x, max z) of each guide curve; a Bezier stays inside the box of its control points.</summary>
        internal static readonly List<float4> GuideBounds = new List<float4>();
        internal static float GuideTotal;
        internal static int GuideFrom;
        internal static float GuideFromT;
        internal static bool OnGuide;

        // Guidance along the line's path: off the route (and how far), the bearing to a point 35 m ahead on the path (or
        // back to the path when off it), and the next turn on it (-1 left, 1 right, 2 U-turn, 0 none) with its distance.
        internal static bool OffRoute;
        internal static float OffRouteDistance;
        internal static float GuideBearing;
        internal static int TurnDirection;
        internal static float TurnDistance;

        private static void ResetGuidance()
        {
            GuideCurves.Clear();
            GuideLengths.Clear();
            GuideBounds.Clear();
            GuideTotal = 0f;
            GuideFrom = 0;
            GuideFromT = 0f;
            OnGuide = false;
            OffRoute = false;
            OffRouteDistance = 0f;
            GuideBearing = 0f;
            TurnDirection = 0;
            TurnDistance = 0f;
        }
    }
}
