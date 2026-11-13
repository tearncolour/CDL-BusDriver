namespace CDLC.BusDriver
{
    /// <summary>
    /// When the game's own body of the bus the player is in is to stay hidden (what the body watch keeps up by putting a lost Hidden back). The driving mod decides which view
    /// shows the body: it hides it in the driver's view on a seat when the cab model asks (SetCabBodyHidden), in every view when an add-on draws the whole bus (SetBodyHidden),
    /// and it shows it again in the chase view and with a camera fixed on the outside. The watch must follow that, not override it.
    /// </summary>
    internal static class NativeBodyRules
    {
        /// <param name="viewing">The player drives the bus or rides it.</param>
        /// <param name="wholeHidden">Bus Driver asked for the body to be hidden in every view (it draws the whole bus itself).</param>
        /// <param name="cabBodyHidden">Bus Driver asked for the body to be hidden in the driver view (its cab model is there).</param>
        /// <param name="cabView">The camera is in the driver view on a seat right now: false in the chase view and with a camera fixed on the outside of the body.</param>
        internal static bool ShouldBeHidden(bool viewing, bool wholeHidden, bool cabBodyHidden, bool cabView) =>
            viewing && (wholeHidden || cabBodyHidden && cabView);
    }
}
