namespace CDLC.BusDriver
{
    /// <summary>
    /// A shift started by taking a bus directly (the user, 18 October: "when a bus is picked and driven directly, go into its line's
    /// service too"): the driving mod's key or the info panel's Drive button, not the bus panel. Pure, tested.
    /// </summary>
    internal static class BusShiftRules
    {
        /// <summary>
        /// The shift starts on the bus's line when the option is on, no shift is running or asked for, the player has just taken a
        /// vehicle, and that vehicle is one of a passenger bus line's own buses.
        /// </summary>
        internal static bool StartOnTake(bool option, bool idle, bool requestPending, bool justTaken, bool onBusLine) =>
            option && idle && !requestPending && justTaken && onBusLine;

        /// <summary>The frames the game lets a bus overstay its departure before boarding ends whoever is still walking up (TransportCarAISystem.StopBoarding).</summary>
        internal const uint OverdueFrames = 1800;

        /// <summary>
        /// The departure frame that ends the boarding now: one the game takes as overdue (not 0, and now >= it + 1800), so the bus leaves even with
        /// passengers still walking up. Before frame 1801 of a city no departure is overdue in the game's sense; there it is frame 1: not in the
        /// future, so the bus is not held for it (now - 1800 wrapped round to a frame some four thousand million away, which the game waited for),
        /// it leaves once its boarding passengers are on, and from frame 1801 it is overdue like any other.
        /// </summary>
        internal static uint ForcedDeparture(uint now) => now > OverdueFrames ? now - OverdueFrames : 1u;

        /// <summary>A vehicle as a number (Entity index and version), 0 for none: what TakeWatch remembers.</summary>
        internal static long Key(int index, int version) => index <= 0 && version <= 0 ? 0L : ((long)index << 32) | (uint)version;
    }

    /// <summary>
    /// Tells once for each vehicle taken: true the first frame the player drives a vehicle not looked at since the last time nothing
    /// was driven. The same vehicle again after letting it go counts again; taking another one straight away counts as well.
    /// </summary>
    internal struct TakeWatch
    {
        internal long Seen;

        internal bool Taken(bool driving, long vehicle)
        {
            if (!driving || vehicle == 0L)
            {
                Seen = 0L;
                return false;
            }

            if (vehicle == Seen)
                return false;
            Seen = vehicle;
            return true;
        }
    }
}
