using System;
using System.IO;
using System.Text.RegularExpressions;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// A mirror's optional "cornerRadius": the reflection follows a flat glass with rounded corners instead of the default rounded,
// tapered outline, so it covers the glass to its corners (WG6980A: a rectangular glass showed bright native corners).
static class MirrorOutlineTests
{
    const string Prefix = "mirror outline: ";

    internal static void Run(Action<bool, string> check)
    {
        string dir = TestPacks.Folder("SWB6710EV01G");
        string good = File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G"));
        // the first mirror (driver side, flat) gets the key; its size stays the pack's
        var first = new Regex(@"(""mirrors""\s*:\s*\[\s*\{)");
        string With(string json) => first.Replace(good, "$1 " + json + ",", 1);

        VehicleProfile plain = VehicleProfileJson.Parse(good, dir, "test");
        MirrorSurface m0 = plain.Mirror(0);
        check(m0.Segments == 96, Prefix + "without the key the default outline is kept (96 points)");

        VehicleProfile rounded = VehicleProfileJson.Parse(With("\"cornerRadius\": 0.02"), dir, "test");
        MirrorSurface r0 = rounded.Mirror(0);
        float2 half = r0.Size * .5f;
        float maxX = 0f, maxY = 0f, area = 0f;
        for (int i = 0; i < r0.Segments; i++)
        {
            float2 a = r0.Outline(i), b = r0.Outline((i + 1) % r0.Segments);
            maxX = math.max(maxX, math.abs(a.x)); maxY = math.max(maxY, math.abs(a.y));
            area += a.x * b.y - b.x * a.y;
        }
        area *= .5f;
        float expected = r0.Size.x * r0.Size.y - (4f - math.PI) * .02f * .02f;
        check(r0.Segments == 28 && math.abs(maxX - half.x) < 1e-6f && math.abs(maxY - half.y) < 1e-6f,
            Prefix + "a rounded rectangle reaches the glass edges (4 arcs of 7 points)");
        check(area > 0f && math.abs(area - expected) < .01f * expected, Prefix + "counter-clockwise and the glass's area less the corner rounding");

        MirrorSurface square = VehicleProfileJson.Parse(With("\"cornerRadius\": 0"), dir, "test").Mirror(0);
        check(square.Segments == 4 && math.all(math.abs(square.Outline(0)) == half), Prefix + "radius 0: the four exact corners");

        check(!Accepts(With("\"cornerRadius\": -0.01"), dir), Prefix + "refuses a negative radius");
        check(!Accepts(With("\"cornerRadius\": 5"), dir), Prefix + "refuses a radius past the glass");
        check(!Accepts(With("\"cornerRadius\": 0.01, \"convexDepth\": 0.01"), dir), Prefix + "refuses a radius on a convex (oval) mirror");
    }

    static bool Accepts(string text, string dir)
    {
        try { VehicleProfileJson.Parse(text, dir, "test"); return true; }
        catch (VehicleProfileJson.Error) { return false; }
    }
}
