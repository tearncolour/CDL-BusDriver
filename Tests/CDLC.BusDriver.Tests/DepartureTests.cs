using System;
using CDLC.BusDriver;

// The departure frame Bus Driver gives a bus to end its boarding (forced door close, a door closing that takes too long, the end of a
// shift), held to the game's own test of it. Before, it was now - 1800 in unsigned frames: in the first 1800 frames of a city that wrapped
// round to a frame about four thousand million away, and the game kept the bus at the stop waiting for it (the audit of 10 November).
static class DepartureTests
{
    const string Prefix = "departure: ";

    // TransportCarAISystem.StopBoarding (not forced), the part the departure frame decides, for a bus at its own stop: true when the bus
    // is let go this frame. walkingUp: a passenger is still on the way (m_MinWaitingDistance finite); notReady: one on board is not seated yet.
    static bool Released(uint now, uint departure, bool walkingUp, bool notReady)
    {
        uint num = Math.Max(0u, departure);
        bool overdue = num != 0 && now >= unchecked(num + 1800);
        float minWaiting = walkingUp ? 12f : float.MaxValue;
        float maxBoarding = minWaiting == float.MaxValue || minWaiting == 0f || overdue ? float.MaxValue : minWaiting + 1f;
        if (now < departure || maxBoarding != float.MaxValue)
            return false;
        if (!overdue && notReady)
            return false;
        return true;
    }

    internal static void Run(Action<bool, string> check)
    {
        uint[] early = { 0, 1, 2, 100, 1000, 1799, 1800 };
        uint[] later = { 1801, 1802, 5000, 3_600_000, uint.MaxValue - 5 };

        bool oldHeld = false;
        foreach (uint now in early)
            oldHeld |= !Released(now, unchecked(now - 1800U), walkingUp: false, notReady: false);
        check(oldHeld, Prefix + "the old frame (now - 1800, wrapped) holds a bus at its stop in a new city even with nobody walking up (the report)");

        bool notZero = true;
        foreach (uint now in early)
            notZero &= BusShiftRules.ForcedDeparture(now) != 0;
        check(notZero && BusShiftRules.ForcedDeparture(0) == 1 && BusShiftRules.ForcedDeparture(1800) == 1,
            Prefix + "up to frame 1800 the forced departure is frame 1 (none earlier is overdue in the game's sense; 0 would mean no departure)");
        bool released = true;
        foreach (uint now in new uint[] { 1, 2, 100, 1000, 1799, 1800 })
            released &= Released(now, BusShiftRules.ForcedDeparture(now), walkingUp: false, notReady: false);
        check(released, Prefix + "early in a city the bus is let go as soon as nobody is walking up and everyone aboard is seated (it is not held for a far frame)");

        bool overdue = true;
        foreach (uint now in later)
            overdue &= Released(now, BusShiftRules.ForcedDeparture(now), walkingUp: true, notReady: true);
        check(overdue, Prefix + "from frame 1801 on the forced departure is overdue: the bus leaves with passengers still walking up and not seated");
        check(BusShiftRules.ForcedDeparture(1801) == 1 && Released(1801, 1, walkingUp: true, notReady: true),
            Prefix + "a bus given frame 1 early is forced off at frame 1801, with no new departure written");
        bool same = true;
        foreach (uint now in later)
            same &= BusShiftRules.ForcedDeparture(now) == now - 1800U;
        check(same, Prefix + "after frame 1800 the forced departure is the old one, now - 1800 (nothing changes for a city that has run half a minute)");
    }
}
