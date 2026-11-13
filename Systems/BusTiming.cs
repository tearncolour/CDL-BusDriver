using System;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Line timing bookkeeping for the player's bus (pure, tested). The game measures a stop's travel time when a bus begins
    /// boarding there: arrival frame minus the bus's departure frame from the previous stop (RouteUtils.UpdateAverageTravelTime,
    /// lerped 50 % into VehicleTiming.m_AverageTravelTime, which is saved with the city and sets how slow the line looks to
    /// the pathfinder). The frames are unsigned: a departure frame in the FUTURE wraps to about 71 million seconds and the
    /// stop's average becomes 35 million seconds in one sample, after which nobody takes the line any more. Bus Driver up to
    /// 0.4.0 held the departure frame 2 s ahead while the doors opened, before the game sampled it: that is what emptied the
    /// buses. Now the sample the game sees is set here: the player's real travel time, bounded to half .. one and a half of
    /// what the stop expects, so a sightseeing detour, a skipped stop or a forced door close never distorts the line.
    /// </summary>
    internal static class BusTiming
    {
        internal const float kMinExpectedSeconds = 15f;
        internal const float kDefaultExpectedSeconds = 60f;
        internal const float kBelow = 0.5f;
        internal const float kAbove = 1.5f;

        /// <summary>A saved average travel time no bus could produce: the wrap bug, or anything beyond ten times the path.</summary>
        internal static bool IsPoisoned(float averageSeconds, float pathSeconds)
        {
            if (float.IsNaN(averageSeconds) || float.IsInfinity(averageSeconds) || averageSeconds < 0f)
                return true;
            float bound = Math.Max(3600f, 10f * (Math.Max(0f, pathSeconds) + 30f));
            return averageSeconds > bound;
        }

        /// <summary>What a stop expects between the previous stop and itself: its own average when sane, else the path time.</summary>
        internal static float ExpectedSeconds(float averageSeconds, float pathSeconds)
        {
            if (averageSeconds > 0f && !IsPoisoned(averageSeconds, pathSeconds))
                return Math.Max(kMinExpectedSeconds, averageSeconds);
            if (pathSeconds > 0f && !float.IsNaN(pathSeconds) && !float.IsInfinity(pathSeconds))
                return Math.Max(kMinExpectedSeconds, pathSeconds);
            return kDefaultExpectedSeconds;
        }

        /// <summary>
        /// The travel time the game should sample for this stop: the player's real time when the bus came straight from the
        /// previous stop, bounded; the expected time when it did not (first stop of the shift, skipped stops, a detour).
        /// </summary>
        internal static float SampleSeconds(float actualSeconds, float expectedSeconds, bool straightFromPreviousStop)
        {
            float expected = Math.Max(kMinExpectedSeconds, expectedSeconds);
            if (!straightFromPreviousStop || float.IsNaN(actualSeconds) || actualSeconds <= 0f)
                return expected;
            return Math.Min(Math.Max(actualSeconds, expected * kBelow), expected * kAbove);
        }

        /// <summary>The departure frame to write so that the game's sample (now - departure) equals the wanted seconds; never in the future.</summary>
        internal static uint DepartureFrame(uint nowFrame, float sampleSeconds)
        {
            uint frames = (uint)Math.Max(1f, Math.Min(sampleSeconds, 36000f) * 60f);
            return nowFrame > frames ? nowFrame - frames : 1u;
        }
    }
}
