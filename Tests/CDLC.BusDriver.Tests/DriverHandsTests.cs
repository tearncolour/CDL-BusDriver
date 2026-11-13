using System;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// Where the driver's hands are on the wheel's rim as the wheel turns (DriverHands): the hand-over-hand regrip, the slew, the baked pose
// picked for the pair of angles, and the rim geometry the poses are baked at. The poses and the figure's mesh are not checked here (that
// is the game's skinning, in game).
static class DriverHandsTests
{
    const string Prefix = "driver hands: ";

    internal static void Run(Action<bool, string> check)
    {
        Targets(check);
        Slew(check);
        Indices(check);
        Rim(check);
    }

    static void Targets(Action<bool, string> check)
    {
        check(DriverHands.Target(0f, true) == 0f && DriverHands.Target(0f, false) == 0f, Prefix + "wheel straight: both hands at 9 and 3 o'clock");
        check(Math.Abs(DriverHands.Target(30f, true) - 30f) < 1e-4f && Math.Abs(DriverHands.Target(30f, false) - 30f) < 1e-4f,
            Prefix + "a small turn: both hands ride the rim with the wheel");

        // turning clockwise the right hand is the lower one: it lets go first (at +45) and takes the rim again 120 degrees back
        check(Math.Abs(DriverHands.Target(44f, false) - 44f) < 1e-3f && Math.Abs(DriverHands.Target(46f, false) - (-74f)) < 1e-3f,
            Prefix + "clockwise: the right hand regrips at +45, 120 degrees back");
        check(Math.Abs(DriverHands.Target(74f, true) - 74f) < 1e-3f && Math.Abs(DriverHands.Target(76f, true) - (-44f)) < 1e-3f,
            Prefix + "clockwise: the left hand holds on to +75");
        // the other way round the left one is lower
        check(Math.Abs(DriverHands.Target(-44f, true) - (-44f)) < 1e-3f && Math.Abs(DriverHands.Target(-46f, true) - 74f) < 1e-3f,
            Prefix + "anticlockwise: the left hand regrips at -45");

        bool inRange = true, lower = true;
        for (float w = -1000f; w <= 1000f; w += 0.5f)
        {
            float l = DriverHands.Target(w, true), r = DriverHands.Target(w, false);
            inRange &= l >= -75f - 1e-3f && l <= 75f + 1e-3f && r >= -75f - 1e-3f && r <= 75f + 1e-3f;
            // the hands never regrip together: when one jumps the other holds
            float dl = DriverHands.Target(w + 0.5f, true) - l, dr = DriverHands.Target(w + 0.5f, false) - r;
            lower &= !(Math.Abs(dl) > 1f && Math.Abs(dr) > 1f);
        }
        check(inRange, Prefix + "a hand is never more than 75 degrees from its place at rest, whatever the turn (two and a half turns each way)");
        check(lower, Prefix + "the two hands never let go at the same moment");

        // the hands stay put while the wheel moves a little (no continuous crawling along the rim beyond the wheel's own turn)
        float held = 0f;
        for (float w = -40f; w <= 40f; w += 0.25f)
            held = Math.Max(held, Math.Abs(DriverHands.Target(w, true) - w));
        check(held < 1e-3f, Prefix + "within 40 degrees of centre the hands are on the rim where they were, turning with it");

        check(DriverHands.Target(float.NaN, true) == 0f && DriverHands.Target(float.PositiveInfinity, false) == 0f, Prefix + "a bad angle: rest, never NaN");
    }

    static void Slew(Action<bool, string> check)
    {
        var s = new DriverHands.State();
        DriverHands.Step(ref s, 100f, 0.016f);
        check(s.Set && s.Left == DriverHands.Target(100f, true) && s.Right == DriverHands.Target(100f, false), Prefix + "the first frame puts the hands where they belong (no sweep from nowhere)");

        // a regrip: the target jumps 120 degrees, a hand needs 120/900 s
        var t = new DriverHands.State();
        DriverHands.Step(ref t, 44f, 0.016f);
        float before = t.Right;
        DriverHands.Step(ref t, 46f, 0.016f);
        float step = Math.Abs(t.Right - before);
        check(step > 0f && step <= DriverHands.SlewDegPerSecond * 0.016f + 1e-3f, Prefix + "a regrip moves the hand no faster than the slew");
        int frames = 0;
        while (Math.Abs(t.Right - DriverHands.Target(46f, false)) > 1e-3f && frames < 100)
        {
            DriverHands.Step(ref t, 46f, 0.016f);
            frames++;
        }
        check(frames >= 5 && frames <= 12, Prefix + $"a regrip of 120 degrees takes about a seventh of a second ({frames} frames)");

        var big = new DriverHands.State();
        DriverHands.Step(ref big, 0f, 0.016f);
        DriverHands.Step(ref big, 400f, 5f);                        // a stall: a long frame
        check(Math.Abs(big.Left) <= DriverHands.SlewDegPerSecond * 0.1f + 75f, Prefix + "a long frame does not fling the hand further than 0.1 s of slew");

        var paused = new DriverHands.State();
        DriverHands.Step(ref paused, 10f, 0.016f);
        DriverHands.Step(ref paused, 60f, 0f);
        check(paused.Left == DriverHands.Target(60f, true), Prefix + "no time passing (paused): the hands go straight to their place");
    }

    static void Indices(Action<bool, string> check)
    {
        check(DriverHands.Index(0f) == DriverHands.Half && DriverHands.Angle(DriverHands.Half) == 0f, Prefix + "angle 0 is the middle baked pose");
        bool roundTrip = true;
        for (int i = 0; i < DriverHands.Steps; i++)
            roundTrip &= DriverHands.Index(DriverHands.Angle(i)) == i;
        check(roundTrip, Prefix + "every baked angle maps back to its index");
        check(DriverHands.Index(-1000f) == 0 && DriverHands.Index(1000f) == DriverHands.Steps - 1, Prefix + "angles outside the baked range clamp to the ends");
        check(DriverHands.Angle(0) <= -75f && DriverHands.Angle(DriverHands.Steps - 1) >= 75f, Prefix + "the baked angles cover the whole range a hand can be at (-75..75)");

        var seen = new bool[DriverHands.PoseCount];
        for (int l = 0; l < DriverHands.Steps; l++)
            for (int r = 0; r < DriverHands.Steps; r++)
                seen[DriverHands.PoseOffset(l, r)] = true;
        bool all = true;
        foreach (bool b in seen) all &= b;
        check(all, Prefix + "every pair of hand angles has its own pose, none past the baked count");
        check(DriverHands.RestOffset == DriverHands.PoseOffset(DriverHands.Half, DriverHands.Half) && DriverHands.RestOffset == DriverHands.PoseCount / 2, Prefix + "the rest pose is the middle of the baked poses");

        // what the figure picks for a driver driving straight, then through a full lock and back
        var s = new DriverHands.State();
        DriverHands.Step(ref s, 0f, 0.016f);
        check(DriverHands.PoseOffset(DriverHands.Index(s.Left), DriverHands.Index(s.Right)) == DriverHands.RestOffset, Prefix + "driving straight: the rest pose");
        bool within = true;
        for (float w = -900f; w <= 900f; w += 3f)
        {
            DriverHands.Step(ref s, w, 0.016f);
            int pose = DriverHands.PoseOffset(DriverHands.Index(s.Left), DriverHands.Index(s.Right));
            within &= pose >= 0 && pose < DriverHands.PoseCount;
        }
        check(within, Prefix + "a sweep through two and a half turns each way always picks a baked pose");
    }

    static void Rim(Action<bool, string> check)
    {
        float3 l0 = DriverHands.Rim(true, 0f), r0 = DriverHands.Rim(false, 0f);
        check(Math.Abs(l0.x + r0.x - 2f * DriverHands.RimCentre.x) < 1e-5f && l0.x < DriverHands.RimCentre.x && r0.x > DriverHands.RimCentre.x,
            Prefix + "at rest the hands are on either side of the wheel's centre, symmetrically");
        check(Math.Abs(l0.y - r0.y) < 1e-5f && Math.Abs(l0.z - r0.z) < 1e-5f, Prefix + "at rest both hands are at the same height and distance");
        check(Math.Abs(math.distance(r0, DriverHands.RimCentre) - DriverHands.RimRadius) < 1e-4f, Prefix + "a hand is on the rim (the wheel's radius from its centre)");

        bool onRim = true, plane = true;
        float3 up = new float3(0f, math.cos(math.radians(DriverHands.TiltDeg)), math.sin(math.radians(DriverHands.TiltDeg)));
        for (float a = -75f; a <= 75f; a += 5f)
            foreach (bool left in new[] { true, false })
            {
                float3 h = DriverHands.Rim(left, a);
                onRim &= Math.Abs(math.distance(h, DriverHands.RimCentre) - DriverHands.RimRadius) < 1e-4f;
                // in the wheel's plane: no component along the wheel's axis (x cross up)
                float3 axis = math.cross(new float3(1f, 0f, 0f), up);
                plane &= Math.Abs(math.dot(h - DriverHands.RimCentre, axis)) < 1e-4f;
            }
        check(onRim, Prefix + "every angle is on the rim");
        check(plane, Prefix + "every angle is in the plane of the wheel (tilted " + DriverHands.TiltDeg + " degrees)");

        // clockwise as the driver sees it: the right hand goes down the rim (towards the driver), the left one up it
        float3 rDown = DriverHands.Rim(false, 45f), lUp = DriverHands.Rim(true, 45f);
        check(rDown.y < r0.y && lUp.y > l0.y, Prefix + "a clockwise turn takes the right hand down the rim and the left one up");
        check(rDown.z < r0.z, Prefix + "the right hand coming down moves towards the driver (less forward)");
    }
}
