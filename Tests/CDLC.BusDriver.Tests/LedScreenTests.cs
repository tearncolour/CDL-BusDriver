using System;
using System.Collections.Generic;
using CDLC.BusDriver;

// The next-stop screen rules (Systems/LedScreenRules.cs): its three states, when its text is made again and the text, in both languages of the mod. The old inline code of
// BusCabSystem is the oracle for the shift: whatever it made, the rules make.
static class LedScreenTests
{
    const string Prefix = "next-stop screen: ";

    internal static void Run(Action<bool, string> check)
    {
        States(check);
        Keys(check);
        Texts(check);
        Shift(check);
        Ride(check);
    }

    static void States(Action<bool, string> check)
    {
        check(LedScreenRules.Phase(false, false) == LedScreenRules.Next && LedScreenRules.Phase(false, true) == LedScreenRules.Arriving &&
              LedScreenRules.Phase(true, false) == LedScreenRules.AtStop && LedScreenRules.Phase(true, true) == LedScreenRules.AtStop,
            Prefix + "on the way, close to the stop, at the stop (the open doors win over the distance)");
        check(LedScreenRules.Next == 0 && LedScreenRules.Arriving == 1 && LedScreenRules.AtStop == 2, Prefix + "the states keep their numbers (the key is built from them)");
    }

    // The key tells the text to be made again; two different situations never share one.
    static void Keys(Action<bool, string> check)
    {
        check(LedScreenRules.Key(3, LedScreenRules.Arriving, false, true) == 4 * 16 + 4 + 0 + 1 && LedScreenRules.Key(-1, LedScreenRules.Next, false, false) == 0,
            Prefix + "the key is the old one: (ordinal + 1) x 16 + state x 4 + skipped x 2 + known");
        var seen = new HashSet<int>();
        bool distinct = true;
        for (int ordinal = -1; ordinal <= 60; ordinal++)
            for (int phase = 0; phase <= 2; phase++)
                foreach (bool skipped in new[] { false, true })
                    foreach (bool valid in new[] { false, true })
                        distinct &= seen.Add(LedScreenRules.Key(ordinal, phase, skipped, valid));

        check(distinct, Prefix + "no two stops, states, skip flags or known-flags share a key (62 stops x 3 x 2 x 2)");
    }

    static void Texts(Action<bool, string> check)
    {
        foreach (string locale in new[] { "en-US", "zh-HANS" })
        {
            Func<string, string> text = LangPack.Text(locale);
            string stop = locale == "en-US" ? "Central Station" : "中央车站", terminus = locale == "en-US" ? "Harbour" : "海港";
            string next = LedScreenRules.Message(true, stop, terminus, LedScreenRules.Next, false, text);
            string arriving = LedScreenRules.Message(true, stop, terminus, LedScreenRules.Arriving, false, text);
            string skipped = LedScreenRules.Message(true, stop, terminus, LedScreenRules.Arriving, true, text);
            string at = LedScreenRules.Message(true, stop, terminus, LedScreenRules.AtStop, false, text);
            string towards = " · " + string.Format(text("LED.Towards"), terminus);
            check(next == string.Format(text("LED.Next"), stop) + towards && arriving == string.Format(text("LED.Arriving"), stop) + towards &&
                  skipped == string.Format(text("LED.Skip"), stop) + towards && at == string.Format(text("LED.AtStop"), stop),
                Prefix + locale + ": next stop, approaching and not stopping end with the terminus, now at does not");
            check(next.Contains(stop) && arriving.Contains(stop) && skipped.Contains(stop) && at.Contains(stop) && next != arriving && arriving != skipped && next != at,
                Prefix + locale + ": the four messages name the stop and differ");
            check(LedScreenRules.Message(true, stop, "", LedScreenRules.Next, false, text) == string.Format(text("LED.Next"), stop) &&
                  LedScreenRules.Message(true, stop, null, LedScreenRules.Arriving, false, text) == string.Format(text("LED.Arriving"), stop),
                Prefix + locale + ": a line without a terminus has none on the screen");
            check(LedScreenRules.Message(false, stop, terminus, LedScreenRules.Next, false, text) == "" && LedScreenRules.Message(true, "", terminus, LedScreenRules.Next, false, text) == "" &&
                  LedScreenRules.Message(true, null, terminus, LedScreenRules.AtStop, false, text) == "",
                Prefix + locale + ": no stop known, or one without a name, is no message");
            check(LedScreenRules.Message(true, stop, terminus, LedScreenRules.Next, true, text) == next, Prefix + locale + ": a skipped stop is announced as skipped only when close");
        }
    }

    // The code BusCabSystem had before the screen served the ride too, kept here as the oracle of the shift.
    static string OldMessage(bool valid, string name, string terminus, int phase, bool skipped, Func<string, string> text)
    {
        if (!valid || string.IsNullOrEmpty(name)) return "";
        string s;
        if (phase == 2) s = string.Format(text("LED.AtStop"), name);
        else if (phase == 1 && skipped) s = string.Format(text("LED.Skip"), name);
        else if (phase == 1) s = string.Format(text("LED.Arriving"), name);
        else s = string.Format(text("LED.Next"), name);
        if (phase != 2 && !string.IsNullOrEmpty(terminus)) s += " · " + string.Format(text("LED.Towards"), terminus);
        return s;
    }

    static void Shift(Action<bool, string> check)
    {
        int compared = 0, differing = 0;
        foreach (string locale in new[] { "en-US", "zh-HANS" })
        {
            Func<string, string> text = LangPack.Text(locale);
            foreach (bool valid in new[] { false, true })
                foreach (string name in new[] { null, "", "A", "Central Station", "中央车站", "Rue Eglise {1}" })
                    foreach (string terminus in new[] { null, "", "Harbour", "海港" })
                        for (int phase = 0; phase <= 2; phase++)
                            foreach (bool skipped in new[] { false, true })
                            {
                                compared++;
                                if (OldMessage(valid, name, terminus, phase, skipped, text) != LedScreenRules.Message(valid, name, terminus, phase, skipped, text)) differing++;
                            }
        }

        check(differing == 0, Prefix + "the shift message is what it was, for " + compared + " combinations of stop, terminus, state and skip in both languages (" + differing + " differ)");
    }

    // A ride has no skipped stop and reads its facts from the ride: what the screen shows follows the doors and the distance.
    static void Ride(Action<bool, string> check)
    {
        Func<string, string> text = LangPack.Text("en-US");
        int onTheWay = LedScreenRules.Phase(false, false), close = LedScreenRules.Phase(false, true), boarding = LedScreenRules.Phase(true, true);
        check(LedScreenRules.Message(true, "Pier", "Harbour", onTheWay, false, text).StartsWith(string.Format(text("LED.Next"), "Pier")) &&
              LedScreenRules.Message(true, "Pier", "Harbour", close, false, text).StartsWith(string.Format(text("LED.Arriving"), "Pier")) &&
              LedScreenRules.Message(true, "Pier", "Harbour", boarding, false, text) == string.Format(text("LED.AtStop"), "Pier"),
            Prefix + "a ridden bus: next stop on the way, approaching close to it, now at while it boards");
        check(LedScreenRules.Key(2, onTheWay, false, true) != LedScreenRules.Key(2, close, false, true) && LedScreenRules.Key(2, close, false, true) != LedScreenRules.Key(2, boarding, false, true) &&
              LedScreenRules.Key(2, boarding, false, true) != LedScreenRules.Key(3, onTheWay, false, true),
            Prefix + "a ridden bus: its text is made again as the bus nears the stop, boards, and heads for the next");
    }
}
