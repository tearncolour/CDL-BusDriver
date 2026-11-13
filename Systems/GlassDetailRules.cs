namespace CDLC.BusDriver
{
    /// <summary>
    /// How much the glass of our buses computes (option "Glass detail"; the owner, 4 November 2026: players on weaker PCs stutter with the glass and see it
    /// blurred while driving). High is what it always was. Balanced and Fast ask CDL Assets (0.6.2 and later, <c>AssetApi.GlassQualityLevel</c>, numbers 0
    /// to 2) for glass that computes less; Fast also leaves the glass of the bus you sit in out of the views from a seat.
    /// </summary>
    public enum GlassDetail { High = 0, Balanced = 1, Fast = 2 }

    internal static class GlassDetailRules
    {
        /// <summary>
        /// The glass of the bus the player sits in is not drawn: on Fast, in a view from a seat (the driver's, a passenger's). Its windows are then open
        /// panes: no reflection, no veil, and the large transparent area over the screen is not drawn at all. The chase view and the cameras fixed on the
        /// outside still draw it.
        /// </summary>
        internal static bool HidesCabGlass(GlassDetail detail, bool seatView) => detail == GlassDetail.Fast && seatView;
    }
}
