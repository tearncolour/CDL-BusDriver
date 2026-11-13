#if CDLC_AUTHORING
using System;

namespace CDLC.BusDriver
{
    internal static class BusRoadGlassClock
    {
        // DebugAdvanceTime casts to uint before division; negative minutes cannot restore an earlier clock.
        internal static int AdvanceMinutes(float normalizedTime, int hour)
        {
            if (float.IsNaN(normalizedTime) || float.IsInfinity(normalizedTime) || normalizedTime < 0 || normalizedTime > 1)
                throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            if (hour < 0 || hour > 23) throw new ArgumentOutOfRangeException(nameof(hour));
            int current = (int)Math.Floor(normalizedTime * 1440f) % 1440;
            return (hour * 60 - current + 1440) % 1440;
        }
    }
}
#endif
