using System;
using CDLC.BusDriver;

// Line timing bookkeeping (BusTiming): the departure frame the game samples when the player's bus begins boarding.
static class TimingTests
{
    static int s_Fails;
    static void Check(bool ok, string what) { if (!ok) { s_Fails++; Console.WriteLine("  FAIL timing: " + what); } }

    public static int Run(Action<bool, string> check)
    {
        s_Fails = 0;
        // The 0.4.0 bug: a departure frame 2 s in the future sampled by the game is a 71-million-second travel time.
        uint now = 1_000_000u;
        uint future = now + 120u;
        float wrapped = (float)(now - future) / 60f;
        check(wrapped > 1e6f, $"a future departure frame wraps to {wrapped:0} s (the bug)");
        check(BusTiming.IsPoisoned(wrapped, 60f), "the wrapped sample counts as poisoned");
        check(BusTiming.IsPoisoned(35_000_000f, 60f), "a half-wrapped average (one lerp) counts as poisoned");
        check(!BusTiming.IsPoisoned(90f, 60f) && !BusTiming.IsPoisoned(1500f, 60f), "slow but possible averages are kept");
        check(BusTiming.IsPoisoned(-1f, 60f) && BusTiming.IsPoisoned(float.NaN, 60f), "negative and NaN averages are poisoned");

        // Expected time: the stop's sane average, else the path, else a default; never below the floor.
        check(BusTiming.ExpectedSeconds(75f, 40f) == 75f, "a sane average is the expectation");
        check(BusTiming.ExpectedSeconds(35_000_000f, 40f) == 40f, "a poisoned average falls back to the path time");
        check(BusTiming.ExpectedSeconds(0f, 0f) == BusTiming.kDefaultExpectedSeconds, "no data: the default");
        check(BusTiming.ExpectedSeconds(0f, 4f) == BusTiming.kMinExpectedSeconds, "a tiny path time is floored");

        // The sample: the real time within bounds when the bus came straight from the previous stop.
        check(BusTiming.SampleSeconds(70f, 60f, true) == 70f, "a real time near the expectation is sampled as is");
        check(BusTiming.SampleSeconds(600f, 60f, true) == 90f, "a sightseeing detour is capped at 1.5x");
        check(BusTiming.SampleSeconds(5f, 60f, true) == 30f, "an impossibly quick run is floored at 0.5x");
        check(BusTiming.SampleSeconds(600f, 60f, false) == 60f, "not straight from the previous stop: the expectation");
        check(BusTiming.SampleSeconds(float.NaN, 60f, true) == 60f, "NaN actual: the expectation");

        // The written frame is in the past by the sample, and never wraps below zero.
        check(BusTiming.DepartureFrame(now, 70f) == now - 4200u, "departure frame = now - sample");
        check(BusTiming.DepartureFrame(now, 70f) < now, "the departure frame is in the past");
        check(BusTiming.DepartureFrame(100u, 70f) == 1u, "early in a game the frame clamps to 1, never wraps");
        float sampledBack = (float)(now - BusTiming.DepartureFrame(now, 70f)) / 60f;
        check(Math.Abs(sampledBack - 70f) < 0.02f, $"the game would sample {sampledBack:0.00} s back");
        return s_Fails;
    }
}
