using System;
using CDLC.BusDriver;

// When the body watch keeps the game body of the bus hidden (Systems/NativeBodyRules.cs). The report that led to it (the owner, 9 November): driving a bus that is not
// one of ours, the bus is gone in the chase view. The log had the driving mod show the body at 14:30:38.001 and the watch put Hidden back at 14:30:38.005, and the
// same pair at 14:31:14.619.
static class NativeBodyTests
{
    const string Prefix = "native body watch: ";

    internal static void Run(Action<bool, string> check)
    {
        // the case of the report: a bus of someone else, our cab model asks for the body to be hidden in the driver view, the player is in the chase view
        check(!NativeBodyRules.ShouldBeHidden(viewing: true, wholeHidden: false, cabBodyHidden: true, cabView: false),
            Prefix + "in the chase view the body of a bus that is not ours is left to the driving mod, which shows it");
        check(NativeBodyRules.ShouldBeHidden(viewing: true, wholeHidden: false, cabBodyHidden: true, cabView: true),
            Prefix + "in the driver view with our cab model the body stays hidden (the ridden bus whose closed native door showed in the doorway)");
        check(NativeBodyRules.ShouldBeHidden(viewing: true, wholeHidden: true, cabBodyHidden: true, cabView: false) &&
              NativeBodyRules.ShouldBeHidden(viewing: true, wholeHidden: true, cabBodyHidden: false, cabView: false),
            Prefix + "a bus whose whole body we draw stays hidden in every view, the chase view and a camera on the outside included");
        check(!NativeBodyRules.ShouldBeHidden(viewing: true, wholeHidden: false, cabBodyHidden: false, cabView: true),
            Prefix + "nothing asked, nothing kept hidden");

        bool never = true;
        foreach (bool whole in new[] { false, true })
            foreach (bool cabHidden in new[] { false, true })
                foreach (bool cabView in new[] { false, true })
                    never &= !NativeBodyRules.ShouldBeHidden(viewing: false, whole, cabHidden, cabView);
        check(never, Prefix + "not driving or riding it: never (8 combinations)");

        // the whole table against the rule written out: 16 rows
        int rows = 0, wrong = 0;
        foreach (bool viewing in new[] { false, true })
            foreach (bool whole in new[] { false, true })
                foreach (bool cabHidden in new[] { false, true })
                    foreach (bool cabView in new[] { false, true })
                    {
                        rows++;
                        bool expected = viewing && (whole || (cabHidden && cabView));
                        if (NativeBodyRules.ShouldBeHidden(viewing, whole, cabHidden, cabView) != expected) wrong++;
                    }

        check(rows == 16 && wrong == 0, Prefix + "all 16 combinations of viewing, whole, cab, cab view follow the rule (" + wrong + " wrong)");
    }
}
