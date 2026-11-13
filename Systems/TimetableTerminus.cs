using System;
using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The stop TransitTimetables anchors a line at, picked the way its dispatch picks it (TimetableDispatchSystem.FindTerminus in
    /// TransitTimetables 0.5.4): the stop the player chose, when one of the line's waypoints is connected to it and it has a
    /// boarding slot; else the first waypoint's stop that has one. Pure, so the rule is checked offline (TransitTimetablesLink
    /// feeds it the line's waypoints).
    /// </summary>
    internal static class TimetableTerminus
    {
        /// <summary>
        /// The waypoint index of the terminus, or -1 when no stop qualifies. <paramref name="stops"/>: each waypoint's connected
        /// stop (<paramref name="none"/> for a plain waypoint); <paramref name="boarding"/>: whether that stop has a boarding slot.
        /// <paramref name="usedChosen"/>: the chosen stop was taken, not the first-stop fallback.
        /// </summary>
        internal static int Pick<T>(IReadOnlyList<T> stops, IReadOnlyList<bool> boarding, T chosen, T none, out bool usedChosen) where T : IEquatable<T>
        {
            usedChosen = false;
            int count = Math.Min(stops.Count, boarding.Count);
            if (!chosen.Equals(none))
            {
                for (int i = 0; i < count; i++)
                {
                    if (boarding[i] && stops[i].Equals(chosen))
                    {
                        usedChosen = true;
                        return i;
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (boarding[i] && !stops[i].Equals(none))
                    return i;
            }

            return -1;
        }
    }
}
