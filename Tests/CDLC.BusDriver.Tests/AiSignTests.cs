using System;
using System.Collections.Generic;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using LedGeometry = CDLC.Asset.Led.LedGeometry;

// The AI buses' LED signs (BusAiSignSystem; the user, 18 October: "the AI buses' electronic signs, as an option that can be switched
// off to save performance"): what they say, how far they are drawn, the shared scroll, and that the lit-dots mesh is exactly the dots
// the driven bus's LedDots lights, on the real font and the packs' own sign grids.
static class AiSignTests
{
    const string Prefix = "ai signs: ";

    internal static void Run(Action<bool, string> check)
    {
        // what a bus says
        check(AiSignRules.State(true, false, false, false) == AiSignState.InService, Prefix + "a bus on a passenger line shows the line");
        check(AiSignRules.State(true, true, false, false) == AiSignState.NotInService && AiSignRules.State(false, false, false, false) == AiSignState.NotInService,
            Prefix + "returning to the depot, or on the road without a line: \"Not in service\"");
        check(AiSignRules.State(true, false, true, false) == AiSignState.Off && AiSignRules.State(true, false, false, true) == AiSignState.Off,
            Prefix + "parked, or an evacuation / test / dummy traffic: dark");

        // the distance, with a tenth's hysteresis for a sign already drawn
        check(AiSignRules.Drawn(119f, 120f, false) && !AiSignRules.Drawn(121f, 120f, false) && AiSignRules.Drawn(131f, 120f, true) && !AiSignRules.Drawn(133f, 120f, true),
            Prefix + "drawn within the option's distance, one already drawn up to a tenth farther");
        check(!AiSignRules.Drawn(float.NaN, 120f, true) && !AiSignRules.Drawn(10f, 0f, true), Prefix + "bad numbers draw nothing");

        // the shared scroll: the same time gives every bus the same offset; it wraps at the cycle; no scroll, no offset
        int a = AiSignRules.ScrollOffset(12.34, 267, true), b = AiSignRules.ScrollOffset(12.34, 267, true);
        check(a == b && a == (int)(Math.Floor(12.34 * 20.0) % 267), Prefix + $"the scroll position is the same for every bus at a time ({a} dots)");
        check(AiSignRules.ScrollOffset(5.0, 267, false) == 0 && AiSignRules.ScrollOffset(double.NaN, 267, true) == 0 && AiSignRules.ScrollOffset(-3.0, 267, true) == 0,
            Prefix + "a sign that does not scroll, or a bad time, stays at 0");

        // the mesh: exactly the lit dots, as LedDots lays them out, on the real font and the packs' grids
        string fontPath = LedFontTests.FontPath();
        var font = new LedFont();
        if (fontPath == null || !font.Load(fontPath))
        {
            check(false, Prefix + "the LED font is needed for the mesh checks");
            return;
        }

        var corners = new List<float3>();
        var triangles = new List<int>();
        foreach (string model in new[] { "SWB6710EV01G", "SWB6109EV12G" })
        {
            VehicleProfile profile = TestPacks.Profile(model);
            VehicleProfile.Sign sign = profile.FrontSign;
            var composer = new LedComposer(font, sign.Matrix.x, sign.Matrix.y);
            composer.RenderSign("", "12", "上海火车站", 0);
            int lit = 0;
            for (int y = 0; y < sign.Matrix.y; y++)
                for (int x = 0; x < sign.Matrix.x; x++)
                    if (composer.Buffer[x, y] && composer.Ink[x, y] == 0)
                        lit++;
            int quads = LedGeometry.LitQuads(composer.Buffer, composer.Ink, 0, sign.Matrix.x, sign.Matrix.y, sign.Size, corners, triangles);
            float2 pitch = sign.Size / new float2(sign.Matrix.x, sign.Matrix.y);
            float side = corners.Count >= 4 ? corners[1].x - corners[0].x : 0f, high = corners.Count >= 4 ? corners[2].y - corners[1].y : 0f;
            check(quads == lit && quads > 0 && corners.Count == 4 * lit && triangles.Count == 6 * lit,
                Prefix + $"{model} front sign: {quads} quads for the {lit} lit dots of \"12 上海火车站\" ({100f * lit / (sign.Matrix.x * sign.Matrix.y):0}% of the {sign.Matrix.x}x{sign.Matrix.y} grid)");
            check(Math.Abs(side - LedGeometry.DotFill * pitch.x) < 1e-5f && Math.Abs(high - LedGeometry.DotFill * pitch.y) < 1e-5f && Math.Abs(pitch.x - pitch.y) < 1e-5f,
                Prefix + $"{model}: the dots are square, {LedGeometry.DotFill:0.00} of the {pitch.x * 1000f:0.0} mm pitch, as LedDots");
            float maxX = 0f, maxY = 0f;
            foreach (float3 c in corners) { maxX = Math.Max(maxX, Math.Abs(c.x)); maxY = Math.Max(maxY, Math.Abs(c.y)); }
            check(maxX <= 0.5f * sign.Size.x + 1e-5f && maxY <= 0.5f * sign.Size.y + 1e-5f, Prefix + $"{model}: every dot inside the sign's field");
        }

        // the prefix in its own ink: the quads of ink 1 are exactly the prefix's dots
        var two = new LedComposer(font, 112, 16);
        two.RenderSign("K", "12", "Central", 0);
        int prefixLit = 0, mainLit = 0;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 112; x++)
                if (two.Buffer[x, y]) { if (two.Ink[x, y] == 1) prefixLit++; else mainLit++; }
        int prefixQuads = LedGeometry.LitQuads(two.Buffer, two.Ink, 1, 112, 16, new float2(1.68f, 0.24f), corners, triangles);
        int mainQuads = LedGeometry.LitQuads(two.Buffer, two.Ink, 0, 112, 16, new float2(1.68f, 0.24f), corners, triangles);
        check(prefixQuads == prefixLit && mainQuads == mainLit && prefixLit > 0, Prefix + $"the prefix K in its own ink ({prefixQuads} dots) and the rest ({mainQuads})");
        check(LedGeometry.LitQuads(null, null, 0, 112, 16, new float2(1f, 0.2f), corners, triangles) == 0 && corners.Count == 0, Prefix + "no buffer, no quads");

        // the shared mesh's key tells signs and texts apart
        string k1 = LedGeometry.MeshKey(120, 16, new float2(1.5f, 0.2f), "", "12", "A"), k2 = LedGeometry.MeshKey(120, 16, new float2(1.5f, 0.2f), "", "12", "B");
        string k3 = LedGeometry.MeshKey(84, 16, new float2(1.5f, 0.2f), "", "12", "A"), k4 = LedGeometry.MeshKey(120, 16, new float2(1.5f, 0.2f), "1", "2", "A");
        check(k1 != k2 && k1 != k3 && k1 != k4 && k1 == LedGeometry.MeshKey(120, 16, new float2(1.5f, 0.2f), "", "12", "A"),
            Prefix + "one shared mesh per grid, size and text (prefix, number and message kept apart)");
    }
}
