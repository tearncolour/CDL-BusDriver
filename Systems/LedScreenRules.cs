using System;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The decisions of the passengers' next-stop screen on the destination box that need no game (tested offline): which of its three states it is in, when its text
    /// has to be made again, and the text. The screen shows one of two sources, the shift's state of the bus the player drives (BusState) or the ride's of the bus the
    /// player rides (BusRide); both are turned into the same few facts before they get here.
    /// </summary>
    internal static class LedScreenRules
    {
        /// <summary>"Next stop X", "Approaching X" (or "Not stopping at X"), "Now at X".</summary>
        internal const int Next = 0, Arriving = 1, AtStop = 2;

        /// <summary>At the stop with the doors open, else close to it (within 150 m, the shift's rule), else on the way.</summary>
        internal static int Phase(bool atStop, bool approaching) => atStop ? AtStop : approaching ? Arriving : Next;

        /// <summary>
        /// Changes when the screen's text must be made again: the stop (its place among the line's), the state, whether it is skipped, whether a stop is known at all.
        /// A rename or a language switch is not in it: the caller makes the text again every few seconds.
        /// </summary>
        internal static int Key(int ordinal, int phase, bool skipped, bool valid) => (ordinal + 1) * 16 + phase * 4 + (skipped ? 2 : 0) + (valid ? 1 : 0);

        /// <summary>
        /// The screen's message (the texts are the LED.* keys of the language files): none without a stop; "Now at X" at the stop; "Not stopping at X" close to a
        /// skipped one; "Approaching X" close to any other; else "Next stop X". Each but "Now at X" ends with " · to Y" (the terminus) when the line has one.
        /// </summary>
        internal static string Message(bool valid, string stop, string terminus, int phase, bool skipped, Func<string, string> text)
        {
            if (!valid || string.IsNullOrEmpty(stop))
                return "";

            string message;
            if (phase == AtStop)
                message = string.Format(text("LED.AtStop"), stop);
            else if (phase == Arriving && skipped)
                message = string.Format(text("LED.Skip"), stop);
            else if (phase == Arriving)
                message = string.Format(text("LED.Arriving"), stop);
            else
                message = string.Format(text("LED.Next"), stop);

            if (phase != AtStop && !string.IsNullOrEmpty(terminus))
                message += " · " + string.Format(text("LED.Towards"), terminus);
            return message;
        }
    }
}
