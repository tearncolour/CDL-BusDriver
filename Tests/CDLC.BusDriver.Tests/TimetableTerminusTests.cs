using System;
using System.Collections.Generic;
using CDLC.BusDriver;

// The terminus taken from TransitTimetables: its pick (TimetableDispatchSystem.FindTerminus, 0.5.4) over the line's waypoints,
// and the sentences of a first stop out of the depot that is the terminus. Stops are numbers here, 0 a plain waypoint.
static class TimetableTerminusTests
{
    internal static void Run(Action<bool, string> check)
    {
        // waypoints: a plain one, stops 11, 12 (no boarding slot), 13, 14
        var stops = new List<int> { 0, 11, 12, 13, 14 };
        var boarding = new List<bool> { false, true, false, true, true };

        check(TimetableTerminus.Pick(stops, boarding, 13, 0, out bool chosen) == 3 && chosen, "the chosen stop on the route is the terminus");
        check(TimetableTerminus.Pick(stops, boarding, 0, 0, out chosen) == 1 && !chosen, "none chosen: the first stop with a boarding slot (not the plain waypoint before it)");
        check(TimetableTerminus.Pick(stops, boarding, 99, 0, out chosen) == 1 && !chosen, "chosen stop no longer on the route: the first stop");
        check(TimetableTerminus.Pick(stops, boarding, 12, 0, out chosen) == 1 && !chosen, "chosen stop without a boarding slot: the first stop");
        check(TimetableTerminus.Pick(new List<int> { 0, 0 }, new List<bool> { false, false }, 0, 0, out chosen) == -1 && !chosen, "no stop at all: none");
        check(TimetableTerminus.Pick(new List<int> { 11, 13, 11 }, new List<bool> { true, true, true }, 11, 0, out chosen) == 0 && chosen, "a stop on the route twice: its first waypoint");
        check(TimetableTerminus.Pick(stops, new List<bool> { false, true }, 13, 0, out chosen) == 1 && !chosen, "lists of different lengths: only the common part is read");

        // The player's line: drawn from the stop after the wanted terminus, so the wanted one is last. Without a choice the
        // timetable runs to the first stop, the one after it; with the choice, to the wanted one.
        var drawn = new List<int> { 21, 22, 23, 24 };
        var all = new List<bool> { true, true, true, true };
        check(TimetableTerminus.Pick(drawn, all, 0, 0, out _) == 0 && TimetableTerminus.Pick(drawn, all, 24, 0, out _) == 3, "first stop without a choice, the chosen last stop with one");

        check(TtsText.IsTerminus("码头路", "码头路") && TtsText.IsTerminus(" 码头路", "码头路 "), "the terminus by name, spaces ignored");
        check(!TtsText.IsTerminus("码头路", "") && !TtsText.IsTerminus("", "") && !TtsText.IsTerminus("码头", "码头路"), "no terminus, no name, another name: not the terminus");
        Func<string, string> En = LangPack.Text("en-US");
        check(TtsText.Sentence(AnnounceKind.AtStop, "Central", "", true, En) == "This is Central. Please mind your step." &&
              TtsText.Sentence(AnnounceKind.Next, "Central", "", true, En, "12") == "The bus is moving off, please hold on. The next stop is Central.",
            "out of the depot to the terminus (no terminus passed): an ordinary stop, no direction");
    }
}
