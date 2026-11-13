using System;
using CDLC.BusDriver;

// The option "Glass detail" (Systems/GlassDetailRules.cs): when the glass of the bus the player sits in is not drawn, and the numbers CDL Assets is given.
static class GlassDetailTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(GlassDetailRules.HidesCabGlass(GlassDetail.Fast, true), "glass detail: Fast leaves the cab glass out of a view from a seat");
        check(!GlassDetailRules.HidesCabGlass(GlassDetail.Fast, false), "glass detail: Fast still draws the glass in the chase view and from the cameras on the outside");
        check(!GlassDetailRules.HidesCabGlass(GlassDetail.High, true) && !GlassDetailRules.HidesCabGlass(GlassDetail.High, false), "glass detail: High always draws it");
        check(!GlassDetailRules.HidesCabGlass(GlassDetail.Balanced, true) && !GlassDetailRules.HidesCabGlass(GlassDetail.Balanced, false), "glass detail: Balanced draws it too (only its computing is less)");

        // the numbers are the API of CDL Assets (AssetApi.GlassQualityLevel): 0 High, 1 Balanced, 2 Fast
        check((int)GlassDetail.High == 0 && (int)GlassDetail.Balanced == 1 && (int)GlassDetail.Fast == 2, "glass detail: the numbers are the ones CDL Assets reads (0, 1, 2)");
        check(Enum.GetValues(typeof(GlassDetail)).Length == 3, "glass detail: three levels");
    }
}
