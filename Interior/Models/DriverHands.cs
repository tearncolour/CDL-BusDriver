using System;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// Where the driver's hands are on the steering wheel's rim as the wheel turns (pure, tested). A bus wheel turns two and a half
    /// turns each way, so the hands cannot ride it: each hand holds the rim for a stretch of the turn and then lets go and takes it
    /// again a little back (the hand-over-hand of a driver turning a big wheel), the lower hand first. A hand's place is an angle on the
    /// rim, clockwise as the driver sees it; the figure's poses are baked at <see cref="Steps"/> angles per hand and the pose is picked
    /// from the pair.
    /// </summary>
    public static class DriverHands
    {
        /// <summary>Angles per hand the poses are baked at (an odd count: the middle one is the rest pose, hands at 9 and 3 o'clock).</summary>
        public const int Steps = 15;
        public const int Half = Steps / 2;
        public const float StepDeg = 12f;
        /// <summary>A hand holds the rim for this much of the turn, then regrips.</summary>
        public const float PeriodDeg = 120f;
        /// <summary>How fast a hand moves along the rim (deg/s): a regrip takes about a seventh of a second.</summary>
        public const float SlewDegPerSecond = 900f;

        /// <summary>Poses baked for a driver: every pair of angles.</summary>
        public const int PoseCount = Steps * Steps;

        /// <summary>The rim's centre and radius in the seat frame (x right, y up, z forward), and the wheel's tilt from the vertical (deg).</summary>
        public static readonly float3 RimCentre = new float3(0f, 0.30f, 0.36f);
        public const float RimRadius = 0.19f;
        public const float TiltDeg = 55f;

        /// <summary>The state of the two hands: the angles they are at now (deg, clockwise as the driver sees it).</summary>
        public struct State
        {
            public float Left, Right;
            public bool Set;
        }

        /// <summary>
        /// The angle a hand takes the rim at for a wheel turned by <paramref name="wheelDeg"/> (clockwise positive). Straight, both hands
        /// are at 0. Turning clockwise the right hand (3 o'clock, going down towards the driver) is the lower one: it lets go first, at
        /// +45 degrees, and takes the rim again 120 degrees back; the left hand holds on to +75. Turning the other way the left hand is
        /// the lower one. The result is in [-75, 75].
        /// </summary>
        public static float Target(float wheelDeg, bool left)
        {
            if (float.IsNaN(wheelDeg) || float.IsInfinity(wheelDeg))
                return 0f;
            // the left hand's range [-45, 75), the right hand's [-75, 45): the lower hand's end is the nearer one
            float low = left ? -45f : -75f;
            float x = wheelDeg - low;
            x -= PeriodDeg * (float)Math.Floor(x / PeriodDeg);
            return x + low;
        }

        /// <summary>One frame: the hands move along the rim towards their targets, no faster than a hand can.</summary>
        public static void Step(ref State s, float wheelDeg, float dt)
        {
            float l = Target(wheelDeg, true), r = Target(wheelDeg, false);
            if (!s.Set || dt <= 0f)
            {
                s.Left = l;
                s.Right = r;
                s.Set = true;
                return;
            }

            float max = SlewDegPerSecond * Math.Min(dt, 0.1f);
            s.Left = MoveTowards(s.Left, l, max);
            s.Right = MoveTowards(s.Right, r, max);
        }

        private static float MoveTowards(float current, float target, float maxDelta) =>
            Math.Abs(target - current) <= maxDelta ? target : current + Math.Sign(target - current) * maxDelta;

        /// <summary>The baked angle (0 .. Steps-1) nearest a hand's angle.</summary>
        public static int Index(float angleDeg)
        {
            int i = (int)Math.Round(angleDeg / StepDeg) + Half;
            return i < 0 ? 0 : (i >= Steps ? Steps - 1 : i);
        }

        /// <summary>The angle a baked index stands for.</summary>
        public static float Angle(int index) => (index - Half) * StepDeg;

        /// <summary>The pose, counted from the first of the driver's poses, for the two hands' indices.</summary>
        public static int PoseOffset(int left, int right) => left * Steps + right;

        /// <summary>The rest pose (hands at 9 and 3 o'clock).</summary>
        public static int RestOffset => PoseOffset(Half, Half);

        /// <summary>
        /// A hand's place on the rim (seat frame) at an angle: the right hand starts at 3 o'clock, the left at 9, and a clockwise turn
        /// takes the right one down the rim towards the driver and the left one up it, away.
        /// </summary>
        public static float3 Rim(bool left, float angleDeg)
        {
            float a = math.radians(angleDeg), tilt = math.radians(TiltDeg);
            float3 x = new float3(1f, 0f, 0f);
            float3 up = new float3(0f, math.cos(tilt), math.sin(tilt));      // the way to 12 o'clock: forward and up on a flat-ish wheel
            float3 offset = RimRadius * (math.cos(a) * x - math.sin(a) * up);
            return RimCentre + (left ? -offset : offset);
        }
    }
}
