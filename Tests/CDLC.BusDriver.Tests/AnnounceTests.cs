using System;
using System.Collections.Generic;
using CDLC.BusDriver;

// When a stop announcement is due (Systems/AnnounceRules.cs): for a shift what BusAnnouncementSystem always did, the old code kept here as the oracle and run over thousands of
// random frames; for a ride (the owner, 9 November: "乘客模式下公交内电子屏和报站", the screen was 0.17.1, the voice is this) the same rules over what the ride system reads.
static class AnnounceTests
{
    const string Prefix = "announcements: ";

    internal static void Run(Action<bool, string> check)
    {
        Shifts(check);
        Rides(check);
        Begin(check);
        Switch(check);
    }

    // The audit of 10 November: changing to another bus of the same line heading for the same stop kept the old bus's state (its queue,
    // and "next stop" and "approaching" already said), so the new bus announced nothing. The source now includes the vehicle.
    static void Switch(Action<bool, string> check)
    {
        var first = new AnnounceSource(true, true, line: 7, bus: 100);
        var same = new AnnounceSource(true, true, line: 7, bus: 100);
        var other = new AnnounceSource(true, true, line: 7, bus: 101);
        check(!AnnounceRules.Switched(same, first), "announce: the same bus on the same line is not a new source");
        check(AnnounceRules.Switched(other, first), "announce: another bus of the same line is a new source (its queue dropped, the rules begun again)");
        check(AnnounceRules.Switched(new AnnounceSource(true, false, 7, 100), first), "announce: the same bus driven instead of ridden is a new source");
        check(AnnounceRules.Switched(default, first) && AnnounceRules.Switched(default, default), "announce: no source is always begun again (as before)");
        check(new AnnounceSource(false, true, 7, 100).Equals(default(AnnounceSource)), "announce: an inactive source carries nothing");

        // the scenario: bus A rode to the stop's range (next stop and approaching said), the player changes to bus B, same line, same target
        AnnounceFacts under = AnnounceRules.RideFacts(3, true, "Stop C", doorsOpen: false, approaching: true, shownDistance: 90);
        var rules = new AnnounceState();
        AnnounceRules.Begin(ref rules, under);
        AnnounceDue a = AnnounceRules.Step(ref rules, under, settled: true);
        check(a == (AnnounceDue.Next | AnnounceDue.Arriving), "announce: bus A says the next stop and the approach once");
        check(AnnounceRules.Step(ref rules, under, settled: true) == AnnounceDue.None, "announce: and not again");
        AnnounceSource followed = first;
        if (AnnounceRules.Switched(other, followed))
        {
            followed = other;
            AnnounceRules.Begin(ref rules, under);
        }
        AnnounceDue b = AnnounceRules.Step(ref rules, under, settled: true);
        check(followed.Equals(other) && b == (AnnounceDue.Next | AnnounceDue.Arriving),
            "announce: after the change to bus B, B's next stop and approach are said (they were not: the state was A's)");
    }

    // The shift states the old code distinguished.
    enum Shift { Idle, Driving, DoorsOpening, DoorsOpen, DoorsClosing }

    // BusAnnouncementSystem.Follow as it was before the rules were taken out of it, member for member.
    sealed class OldFollow
    {
        Shift lastState;
        int lastTarget = -1;
        bool arrivalDone, nextPending;

        internal OldFollow(Shift state) { lastState = state; }

        internal List<AnnounceKind> Step(Shift state, int target, bool valid, float distance, bool skip, bool settled)
        {
            var said = new List<AnnounceKind>();
            if (target != lastTarget)
            {
                lastTarget = target;
                arrivalDone = false;
                nextPending = target >= 0 && valid;
            }

            if (nextPending && state == Shift.Driving && settled)
            {
                nextPending = false;
                said.Add(AnnounceKind.Next);
            }

            if (!arrivalDone && state == Shift.Driving && valid && settled && distance > 0f && distance < 150f)
            {
                arrivalDone = true;
                said.Add(skip ? AnnounceKind.Skip : AnnounceKind.Arriving);
            }

            if (state == Shift.DoorsOpening && lastState != Shift.DoorsOpening && lastState != Shift.DoorsOpen)
                said.Add(AnnounceKind.AtStop);
            lastState = state;
            return said;
        }
    }

    static List<AnnounceKind> Kinds(AnnounceDue due, bool skip)
    {
        var said = new List<AnnounceKind>();
        if ((due & AnnounceDue.Next) != 0) said.Add(AnnounceKind.Next);
        if ((due & AnnounceDue.Arriving) != 0) said.Add(skip ? AnnounceKind.Skip : AnnounceKind.Arriving);
        if ((due & AnnounceDue.AtStop) != 0) said.Add(AnnounceKind.AtStop);
        return said;
    }

    static bool Same(List<AnnounceKind> a, List<AnnounceKind> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static void Shifts(Action<bool, string> check)
    {
        var random = new Random(20261110);
        float[] distances = { -1f, 0f, 20f, 149.9f, 150f, 150.1f, 400f };
        int steps = 0, differing = 0, said = 0;
        for (int run = 0; run < 60; run++)
        {
            Shift state = (Shift)random.Next(5);
            var old = new OldFollow(state);
            var s = new AnnounceState();
            var first = AnnounceRules.ShiftFacts(-1, false, state == Shift.Driving, state == Shift.DoorsOpening, state == Shift.DoorsOpening || state == Shift.DoorsOpen, 0f);
            AnnounceRules.Begin(ref s, first);
            int target = -1;
            bool valid = false;
            for (int i = 0; i < 400; i++)
            {
                if (random.Next(12) == 0) state = (Shift)random.Next(5);
                if (random.Next(25) == 0) target = random.Next(-1, 6);
                if (random.Next(20) == 0) valid = !valid;
                float distance = distances[random.Next(distances.Length)];
                bool skip = random.Next(7) == 0;
                bool settled = i > 20 || random.Next(3) == 0;
                List<AnnounceKind> expected = old.Step(state, target, valid, distance, skip, settled);
                AnnounceFacts facts = AnnounceRules.ShiftFacts(target, valid, state == Shift.Driving, state == Shift.DoorsOpening, state == Shift.DoorsOpening || state == Shift.DoorsOpen, distance);
                List<AnnounceKind> got = Kinds(AnnounceRules.Step(ref s, facts, settled), skip);
                steps++;
                said += expected.Count;
                if (!Same(expected, got)) differing++;
            }
        }

        check(differing == 0 && said > 500, Prefix + "a shift is announced as before: " + steps + " random frames of states, targets, distances and skips, " + said + " announcements, " + differing + " differ");
        check(AnnounceRules.ShiftFacts(0, true, true, false, false, 149.9f).InRange && !AnnounceRules.ShiftFacts(0, true, true, false, false, 150f).InRange &&
              !AnnounceRules.ShiftFacts(0, true, true, false, false, 0f).InRange && !AnnounceRules.ShiftFacts(0, true, true, false, false, -5f).InRange,
            Prefix + "a shift is in range from 150 m down to just above 0, as before");
    }

    // What one stop looks like to a passenger: the bus heads for stop 3, comes within 150 m, stops and opens its doors, closes them and heads for stop 4.
    sealed class Trip
    {
        internal AnnounceState State;
        internal readonly List<string> Said = new List<string>();
        int frame;

        internal Trip(AnnounceFacts first) { AnnounceRules.Begin(ref State, first); }

        internal void Frame(AnnounceFacts facts, string note)
        {
            AnnounceDue due = AnnounceRules.Step(ref State, facts, ++frame > 10);
            if ((due & AnnounceDue.Next) != 0) Said.Add("next@" + note);
            if ((due & AnnounceDue.Arriving) != 0) Said.Add("arriving@" + note);
            if ((due & AnnounceDue.AtStop) != 0) Said.Add("at@" + note);
        }
    }

    static void Rides(Action<bool, string> check)
    {
        AnnounceFacts Ride(int target, bool doorsOpen = false, bool approaching = false, int distance = 400, bool route = true, string next = "X") =>
            AnnounceRules.RideFacts(target, route, next, doorsOpen, approaching, distance);

        var trip = new Trip(Ride(3));
        for (int i = 0; i < 12; i++) trip.Frame(Ride(3), "start");                  // under way, not yet near: the target at the start is said once, after the first moments
        string afterStart = string.Join(",", trip.Said);
        for (int i = 0; i < 30; i++) trip.Frame(Ride(3), "far");                    // nothing more while far
        trip.Frame(Ride(3, approaching: true, distance: 145), "145m");              // within 150 m: approaching
        for (int i = 0; i < 20; i++) trip.Frame(Ride(3, approaching: true, distance: 100 - 5 * i), "near" + i);
        trip.Frame(Ride(3, doorsOpen: true, approaching: true, distance: 5), "doors"); // the doors open at stop 3
        for (int i = 0; i < 60; i++) trip.Frame(Ride(3, doorsOpen: true), "open");  // standing with the doors open: nothing
        trip.Frame(Ride(4), "closed");                                              // doors shut and the target moved on: "next"
        for (int i = 0; i < 30; i++) trip.Frame(Ride(4), "away");
        string all = string.Join(",", trip.Said);
        check(afterStart == "next@start" && all == "next@start,arriving@145m,at@doors,next@closed",
            Prefix + "a ride is announced once for each: next stop when under way, approaching within 150 m, the stop at the doors, next stop again after it (" + all + ")");

        // the target moves on while the doors are still open: "next" waits for the doors to shut
        var late = new Trip(Ride(3, doorsOpen: true));
        for (int i = 0; i < 12; i++) late.Frame(Ride(3, doorsOpen: true), "open");
        for (int i = 0; i < 5; i++) late.Frame(Ride(4, doorsOpen: true), "moved");
        string heldBack = string.Join(",", late.Said);
        for (int i = 0; i < 5; i++) late.Frame(Ride(4), "closed");
        check(heldBack == "" && string.Join(",", late.Said) == "next@closed", Prefix + "a ride: the next stop is not said while the doors are open, and is said once they shut (" + string.Join(",", late.Said) + ")");

        // a ride that begins with the doors open does not announce the stop it stands at as an arrival
        var standing = new Trip(Ride(2, doorsOpen: true));
        for (int i = 0; i < 20; i++) standing.Frame(Ride(2, doorsOpen: true), "open");
        check(standing.Said.Count == 0, Prefix + "a ride that begins at a stop with its doors open does not announce that arrival");

        // no route known (a detour, the depot): nothing is said for it, and the same target again after it is not said twice
        var detour = new Trip(Ride(3));
        for (int i = 0; i < 12; i++) detour.Frame(Ride(3), "a");
        string before = string.Join(",", detour.Said);
        for (int i = 0; i < 10; i++) detour.Frame(Ride(3, route: false, next: ""), "lost");
        for (int i = 0; i < 10; i++) detour.Frame(Ride(3), "back");
        check(before == "next@a" && string.Join(",", detour.Said) == before, Prefix + "a ride that loses its route for a while and finds the same stop again does not say it twice");

        // the facts of a ride
        AnnounceFacts open = AnnounceRules.RideFacts(2, true, "X", true, false, 0), shut = AnnounceRules.RideFacts(2, true, "X", false, true, 120), nameless = AnnounceRules.RideFacts(2, true, "", false, true, 120);
        check(open.Opening && open.AtStop && !open.Driving && !open.InRange && shut.Driving && !shut.Opening && !shut.AtStop && shut.InRange && shut.StopValid,
            Prefix + "a ride: open doors are the arrival, shut doors are under way, a distance under 150 m is in range");
        check(!nameless.StopValid && !AnnounceRules.RideFacts(2, false, "X", false, true, 120).StopValid && !AnnounceRules.RideFacts(2, true, "X", false, true, 0).InRange && !AnnounceRules.RideFacts(2, true, "X", false, false, 120).InRange,
            Prefix + "a ride: no stop name, no route, no distance or not approaching is no announcement");
    }

    static void Begin(Action<bool, string> check)
    {
        var s = new AnnounceState { LastTarget = 7, ArrivalDone = true, NextPending = true, LastAtStop = false };
        AnnounceRules.Begin(ref s, AnnounceRules.RideFacts(3, true, "X", true, false, 0));
        check(s.LastTarget == -1 && !s.ArrivalDone && !s.NextPending && s.LastAtStop, Prefix + "a new shift or ride starts clean and remembers whether its doors are open");
        AnnounceRules.Begin(ref s, AnnounceRules.ShiftFacts(3, true, true, false, false, 100f));
        check(!s.LastAtStop, Prefix + "and with its doors shut remembers that");
    }
}
