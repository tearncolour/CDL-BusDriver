using System;
using CDLC.BusDriver;
using Unity.Mathematics;

// The boarding zone (BusStopZone): one long box, the bus in it with its middle and front door, no stop line, no heading
// test; and the riders still to board shown on the sign while the doors are open.
static class StopZoneTests
{
    // A stop at the origin, the road running +z, the kerb on the right (+x).
    static readonly float2 s_Stop = float2.zero, s_Direction = new float2(0f, 1f), s_Right = new float2(1f, 0f);

    // A 12 m bus whose front is 6 m ahead of its middle, standing at (x, middle z) and heading at the given angle from +z
    // (positive towards +x).
    static bool Inside(float x, float middleZ, float headingDegrees, float kerbSide = 1f, float behind = 24f)
    {
        float a = math.radians(headingDegrees);
        float2 heading = new float2(math.sin(a), math.cos(a));
        float2 middle = new float2(x, middleZ);
        float2 frontDoor = middle + heading * (6f - BusStopZone.kFrontDoorBehindFront);
        return BusStopZone.Inside(s_Stop, s_Direction, s_Right, kerbSide, behind, middle, frontDoor);
    }

    public static void Run(Action<bool, string> check)
    {
        // How far the box reaches behind the stop point.
        check(BusStopZone.Behind(12f) == 24f, "a 12 m bus: the box reaches 24 m behind the stop point (the minimum)");
        check(math.abs(BusStopZone.Behind(18.3f) - 28.3f) < 1e-4f, "an 18.3 m articulated bus: 10 m more than the bus behind the stop point");
        check(BusStopZone.Behind(60f) == BusStopZone.kBehindMaxMeters, "a very long rig is capped");
        check(BusStopZone.Behind(0f) == 24f && BusStopZone.Behind(float.NaN) == 24f && BusStopZone.Behind(float.PositiveInfinity) == 24f, "no length: the minimum");
        check(BusStopZone.kBehindMinMeters + BusStopZone.kAheadMeters > 3f + 16f, "the box is longer than the 0.9.12 one (16 m behind, 3 m ahead)");

        // In the box lengthwise, wherever along it: no stop line.
        check(Inside(0f, -6f, 0f), "front at the stop point: in");
        check(Inside(0f, -18f, 0f), "front 12 m short of the stop point (0.9.12 wanted it within 7 m): in");
        check(Inside(0f, -23.5f, 0f), "the middle near the box's rear end: in");
        check(!Inside(0f, -24.5f, 0f), "the middle behind the box: out");
        check(Inside(0f, 1.5f, 0f), "front 7.5 m past the stop point, the front door at the box's far end: in");
        check(!Inside(0f, 2.5f, 0f), "the front door past the box: out");

        // No heading test: either way round, and at an angle that keeps both points in the box.
        check(Inside(0f, -10f, 180f), "reversed into the box: in");
        check(Inside(0f, -19f, 180f) && !Inside(0f, -20f, 180f), "reversed: the front door, now towards the box's rear end, has to be in it");
        check(Inside(-0.5f, -10f, 32f), "32 degrees to the road (0.9.12's heading test refused it), both points in the box: in");
        check(!Inside(0f, -10f, 40f), "40 degrees: the front door is 2.9 m off the axis, out of the box (the box is the limit, not a heading test)");
        check(!Inside(0f, -10f, 90f), "across the road: out");

        // Across the road: more room towards the kerb than towards the traffic, the front door 0.4 m more.
        check(Inside(1.7f, -10f, 0f) && !Inside(1.9f, -10f, 0f), "towards the kerb: up to 1.8 m");
        check(Inside(-1.1f, -10f, 0f) && !Inside(-1.3f, -10f, 0f), "towards the traffic: up to 1.2 m");
        check(Inside(-1.1f, -10f, 0f, -1f) && !Inside(-1.9f, -10f, 0f, -1f), "the kerb on the left: the same, mirrored");
        check(Inside(1.3f, -10f, 0f, 0f) && Inside(-1.3f, -10f, 0f, 0f) && !Inside(1.5f, -10f, 0f, 0f), "kerb side unknown: 1.4 m either way");
        check(BusStopZone.Across(2.1f, 1f, BusStopZone.kFrontDoorSlack) && !BusStopZone.Across(2.1f, 1f, 0f), "the front door has 0.4 m more");

        // How far to move into the box lengthwise, in the box's and in the bus's own terms.
        float behind = 24f;
        check(BusStopZone.Shift(-30f, -25.5f, behind) == 6f, "the middle 6 m behind the box: 6 m on");
        check(BusStopZone.Shift(3f, 7.5f, behind) == -1.5f, "the front door 1.5 m past the box: 1.5 m back");
        check(BusStopZone.Shift(-10f, -5.5f, behind) == 0f, "in the box lengthwise: no move");
        check(BusStopZone.Shift(-25.5f, -30f, behind) == 6f, "reversed, the front door 6 m behind the box: 6 m on along the box");
        check(BusStopZone.Move(6f, 1f) == 6f && BusStopZone.Move(6f, -1f) == -6f, "the move in the bus's terms: reversed, on along the box is backing up");
        bool landed = true;
        for (float middle = -60f; middle <= 30f; middle += 0.5f)
        {
            foreach (float door in new[] { middle + 4.5f, middle - 4.5f })
            {
                float shift = BusStopZone.Shift(middle, door, behind);
                landed &= BusStopZone.Along(middle + shift, behind) && BusStopZone.Along(door + shift, behind);
                landed &= shift == 0f || !(BusStopZone.Along(middle, behind) && BusStopZone.Along(door, behind));
            }
        }
        check(landed, "every move lands both points in the box, and a bus already in it is not moved");

        // Driving past: a bus that can still open is never counted as passed.
        check(BusStopZone.kPassedSkipMeters > BusStopZone.kAheadMeters + BusStopZone.kFrontDoorBehindFront, "skipped only once the front door is past the box");
        check(BusStopZone.kPassedMissMeters > BusStopZone.kPassedSkipMeters, "missed later than skipped");

        // Riders still to board.
        check(BusStopZone.ToBoard(2, 5, 40, 80, out int left) == 7 && left == 0, "2 walking to the doors and 5 waiting with room: 7 to board");
        check(BusStopZone.ToBoard(1, 5, 78, 80, out left) == 3 && left == 3, "room for 2 of the 5 waiting: 1 + 2 to board, 3 left behind");
        check(BusStopZone.ToBoard(0, 4, 80, 80, out left) == 0 && left == 4, "full: nobody to board, 4 left behind");
        check(BusStopZone.ToBoard(0, 6, 10, 0, out left) == 6 && left == 0, "capacity unknown: all waiting count");
        check(BusStopZone.ToBoard(-1, -3, -5, 80, out left) == 0 && left == 0, "negative inputs count as none");
        check(BusStopZone.StillWaiting(5, 7) == 0 && BusStopZone.StillWaiting(5, -2) == 5, "the waiting count never goes below 0 or above the game's count");

        // The game's waiting count is refreshed about every 4 s: riders who joined since are taken off it.
        var waiting = new BusStopZone.WaitingCount();
        check(waiting.Observe(10, 500, 0) == 10, "doors open: the game's count");
        check(waiting.Observe(10, 500, 4) == 6, "4 joined the bus since the count: 6 still waiting");
        check(waiting.Observe(6, 700, 4) == 6, "the next count (6) already leaves out the 4 who joined");
        check(waiting.Observe(6, 700, 6) == 4, "2 more joined: 4");
        check(waiting.Observe(6, 700, 12) == 0, "more joined than were counted (new arrivals boarding at once): 0, not below");
        check(waiting.Observe(9, 900, 12) == 9, "new arrivals: the next count holds them");
        check(waiting.Observe(9, 960, 13) == 9, "the same count with a grown waiting-time sum is a new count too");
        waiting.Reset();
        check(waiting.Observe(3, 960, 13) == 3, "after a reset the next look is the base again");
    }
}
