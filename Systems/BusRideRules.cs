using System;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The decisions of the passenger mode that need no game (tested offline in ride-checks): which bus to ride, and what a bus the game
    /// drives does that the driving mod does not tell (its steering, its brake lamps, its doors), read off its motion.
    /// </summary>
    internal static class BusRideRules
    {
        /// <summary>The largest angle the front wheels are drawn at (35 degrees), the steering lock of a bus.</summary>
        internal const float MaxWheelAngle = 0.61f;

        /// <summary>Slower than this (m/s) a bus has no yaw rate worth turning wheels for (it stands, or creeps).</summary>
        internal const float MinSteerSpeed = 0.7f;

        /// <summary>A bus brakes harder than this (m/s²) with its lamps on.</summary>
        internal const float BrakingDeceleration = 0.9f;

        /// <summary>
        /// The road wheels' angle (radians, positive to the right) that gives a bus of this wheelbase this yaw rate (positive to the right)
        /// at this signed speed: the bicycle model. Zero when it stands or the numbers are not finite.
        /// </summary>
        internal static float WheelAngle(float wheelbase, float yawRate, float speed)
        {
            if (!(wheelbase > 0.5f) || !IsFinite(yawRate) || !IsFinite(speed) || Math.Abs(speed) < MinSteerSpeed)
                return 0f;
            float angle = (float)Math.Atan(wheelbase * yawRate / speed);
            return Math.Max(-MaxWheelAngle, Math.Min(MaxWheelAngle, angle));
        }

        /// <summary>One step of an exponential low-pass towards <paramref name="target"/> with the time constant <paramref name="seconds"/>.</summary>
        internal static float Smooth(float current, float target, float dt, float seconds)
        {
            if (!(dt > 0f) || !IsFinite(target))
                return current;
            if (!(seconds > 0f))
                return target;
            return current + (target - current) * (1f - (float)Math.Exp(-dt / seconds));
        }

        /// <summary>The brake lamps: the bus slows down hard, or it stands at a stop with its doors open.</summary>
        internal static bool Braking(float acceleration, float speed, bool doorsOpen) =>
            doorsOpen || acceleration < -BrakingDeceleration && speed > MinSteerSpeed;

        /// <summary>The doors are open: the bus is boarding (the game's flag) and has stopped.</summary>
        internal static bool DoorsOpen(bool boarding, float speed) => boarding && Math.Abs(speed) < MinSteerSpeed;

        /// <summary>
        /// The bus of the line to ride when there is a choice: the one with more passengers (the most to see), and of equal loads the nearer
        /// to the camera. True when the first beats the second.
        /// </summary>
        internal static bool Better(int passengers, float distance, int bestPassengers, float bestDistance) =>
            passengers != bestPassengers ? passengers > bestPassengers : distance < bestDistance;

        /// <summary>
        /// The place after <paramref name="currentKey"/> in the ring of buses with these <paramref name="keys"/> (the
        /// entities' indices), the bus after the current one in key order, the first after the last, the first when the
        /// current one is not among them. -1 for no buses.
        /// </summary>
        internal static int NextByKey(int[] keys, int currentKey)
        {
            if (keys == null || keys.Length == 0)
                return -1;
            int next = -1, first = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] < keys[first])
                    first = i;
                if (keys[i] > currentKey && (next < 0 || keys[i] < keys[next]))
                    next = i;
            }

            return next >= 0 ? next : first;
        }

        /// <summary>
        /// The waypoint of the stop a bus heading for waypoint <paramref name="target"/> serves next: the waypoint itself when it is a stop, else the first
        /// stop after it round the line (a plain waypoint only shapes the route). -1 when the target is not one of the line's waypoints or the line has no stop.
        /// </summary>
        internal static int NextStop(bool[] isStop, int target)
        {
            if (isStop == null || target < 0 || target >= isStop.Length)
                return -1;
            for (int k = 0; k < isStop.Length; k++)
            {
                int i = (target + k) % isStop.Length;
                if (isStop[i])
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// The route line's segments (segment k runs from waypoint k to waypoint k + 1) that lead to the stop at waypoint <paramref name="stop"/>: from the
        /// segment that leaves the previous stop (the one at or before the segment into this stop, plain waypoints between included) to the segment into it.
        /// False when the line has no other waypoint to come from.
        /// </summary>
        internal static bool SegmentsTo(bool[] isStop, int stop, out int first, out int last)
        {
            first = last = -1;
            if (isStop == null || stop < 0 || stop >= isStop.Length || isStop.Length < 2)
                return false;
            int count = isStop.Length;
            last = (stop - 1 + count) % count;
            first = last;
            for (int k = 0; k < count - 1 && !isStop[first]; k++)
                first = (first - 1 + count) % count;
            return true;
        }

        /// <summary>The share of a guide (0..1) behind a bus that is <paramref name="done"/> metres along it; 0 for a guide that is not there.</summary>
        internal static float Progress(float done, float total) =>
            total > 0.1f && IsFinite(done) ? Math.Max(0f, Math.Min(1f, done / total)) : 0f;

        /// <summary>The distance shown for a stop still this far away (metres): to the 5 m, 0 for a bad number.</summary>
        internal static int ShownDistance(float metres) =>
            IsFinite(metres) && metres > 0f ? (int)(Math.Round(metres / 5.0) * 5.0) : 0;

        /// <summary>The stop is close enough to say the bus is arriving at it (the shift's 150 m).</summary>
        internal static bool Approaching(float metres) => IsFinite(metres) && metres < 150f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
