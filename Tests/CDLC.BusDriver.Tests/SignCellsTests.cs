using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior;
using Unity.Mathematics;
using LedGeometry = CDLC.Asset.Led.LedGeometry;

// The LED signs' dots or their solid cells (the owner, 25 October: "现在一直有摩尔纹很烦"): when a sign is too small on the screen for
// the gaps between its dots, each lit dot is its whole cell, in inks a lit cell's share as bright. The rules (pitch on the screen, the
// choice with its hysteresis), the cells' mesh (whole cells, neighbours on exactly the same lines: no crack), and the beat measured on a
// software raster at pixel centres (no anti-aliasing): a lit field of dots against the same field of cells, and the sign's light either
// way against the ideal (each pixel the mean of the dots under it). "moirepreview <dir>" writes the sign in the three ways as PNG.
static class SignCellsTests
{
    const string P = "sign cells: ";

    internal static void Run(Action<bool, string> check)
    {
        // the pitch on the screen: the SWB6710's front sign (6.25 mm) at 1080p, 60 degrees of height
        float ppr = 1080f / (2f * (float)Math.Tan(Math.PI / 6.0));
        float pitch = 1.5f / 240f;
        float at1 = LedGeometry.PitchPixels(pitch, 1f, 1f, ppr), at10 = LedGeometry.PitchPixels(pitch, 10f, 1f, ppr);
        check(Math.Abs(at1 - 5.85f) < 0.02f && Math.Abs(at10 - 0.585f) < 0.002f, P + $"the SWB6710 front sign's pitch: {at1:0.00} px at 1 m, {at10:0.000} px at 10 m (1080p, 60 deg)");
        check(Math.Abs(LedGeometry.PitchPixels(pitch, 2f, 0.5f, ppr) - 0.5f * LedGeometry.PitchPixels(pitch, 2f, 1f, ppr)) < 1e-4f &&
              LedGeometry.PitchPixels(pitch, 2f, -0.5f, ppr) == LedGeometry.PitchPixels(pitch, 2f, 0.5f, ppr),
            P + "a sign seen obliquely has its pitch shrunk by the cosine, either side");
        check(LedGeometry.PitchPixels(0f, 2f, 1f, ppr) == 0f && LedGeometry.PitchPixels(pitch, float.NaN, 1f, ppr) == 0f && LedGeometry.PitchPixels(pitch, 2f, 1f, 0f) == 0f &&
              LedGeometry.PitchPixels(pitch, 0f, 1f, ppr) > 0f && !float.IsInfinity(LedGeometry.PitchPixels(pitch, 0f, 1f, ppr)),
            P + "bad numbers give no pitch (the cells); at the eye it stays finite");
        check(!LedGeometry.DrawDots(4.9f, false) && LedGeometry.DrawDots(5f, false) && LedGeometry.DrawDots(4.6f, true) && !LedGeometry.DrawDots(4.4f, true) && !LedGeometry.DrawDots(0f, true),
            P + "the dots from 5 px of pitch, back to the cells under 4.5 px (no flicker between)");
        check(Math.Abs(LedGeometry.CellLight - 0.6084f) < 1e-4f, P + $"the cells' inks are {LedGeometry.CellLight:0.000} as bright, a lit dot's share of its cell");
        float3[] colours = AiSignRules.HdInkColours(new float3(1f, 0f, 0f));
        check(colours.Length == HdInk.Count && colours[HdInk.Sign].Equals(AiSignRules.SignAmber) && colours[HdInk.Prefix].Equals(new float3(1f, 0f, 0f)) &&
              colours[HdInk.English].Equals(AiSignRules.SignCyan) && colours[HdInk.Warning].Equals(AiSignRules.SignRed),
            P + "the inks' colours in HdInk order, the prefix's its own");

        // the cells' mesh: whole cells, the same quads as the dots, the edges of neighbours exactly the same numbers
        var buffer = new bool[240, 32];
        var ink = new byte[240, 32];
        for (int x = 10; x < 60; x++) for (int y = 4; y < 28; y++) { buffer[x, y] = true; ink[x, y] = (byte)((x / 7) % 2); }
        buffer[100, 0] = true; ink[100, 0] = HdInk.English;
        buffer[101, 0] = true; ink[101, 0] = 9;            // an ink of no submesh
        var size = new float2(1.5f, 0.2f);
        var corners = new List<float3>();
        var triangles = new[] { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
        int dots = LedGeometry.LitQuadsByInk(buffer, ink, 240, 32, size, corners, triangles);
        var dotCorners = new List<float3>(corners);
        int cells = LedGeometry.LitQuadsByInk(buffer, ink, 240, 32, size, corners, triangles, true);
        check(cells == dots && dots == 50 * 24 + 1 && corners.Count == 4 * cells, P + $"the cells are the same {cells} quads as the dots, a dot of no submesh left out of both");
        float2 cell = size / new float2(240, 32);
        bool whole = true, inside = true;
        for (int q = 0; q < cells; q++)
        {
            float3 a = corners[4 * q], c = corners[4 * q + 2];
            whole &= Math.Abs(c.x - a.x - cell.x) < 1e-6f && Math.Abs(c.y - a.y - cell.y) < 1e-6f;
            inside &= a.x >= -0.5f * size.x - 1e-6f && c.x <= 0.5f * size.x + 1e-6f && a.y >= -0.5f * size.y - 1e-6f && c.y <= 0.5f * size.y + 1e-6f;
        }
        check(whole && inside, P + "each cell is its whole pitch, inside the sign");
        var xs = new HashSet<float>();
        var ys = new HashSet<float>();
        foreach (float3 v in corners) { xs.Add(v.x); ys.Add(v.y); }
        check(xs.Count <= 241 && ys.Count <= 33, P + $"every corner is on one of the grid's 241 x 33 lines ({xs.Count} x {ys.Count} distinct values): neighbours share their edges exactly, no crack");
        bool dotsSmaller = true;
        for (int q = 0; q < dots; q++)
            dotsSmaller &= Math.Abs(dotCorners[4 * q + 2].x - dotCorners[4 * q].x - LedGeometry.DotFill * cell.x) < 1e-6f;
        check(dotsSmaller, P + $"the dots are still {LedGeometry.DotFill} of their pitch");
        bool winding = true;
        for (int s = 0; s < triangles.Length; s++)
            for (int t = 0; t + 2 < triangles[s].Count; t += 3)
            {
                float3 a = corners[triangles[s][t]], b = corners[triangles[s][t + 1]], c = corners[triangles[s][t + 2]];
                winding &= math.cross(b - a, c - a).z < 0f;
            }
        check(winding, P + "the cells face -Z as the dots (the same winding)");

        // the beat on a lit field, and the sign's light against the ideal, at pixel centres
        foreach (float px in new[] { 0.8f, 1.37f, 2.2f, 3.5f })
        {
            float dotsBeat = FieldBeat(px, false), cellsBeat = FieldBeat(px, true);
            check(cellsBeat < 1e-6f && dotsBeat > 0.3f, P + $"at {px} px a dot a lit field's columns vary {dotsBeat:0.00} with the dots, {cellsBeat:0.000} with the cells");
            float light = FieldLight(px, true) * LedGeometry.CellLight, ideal = LedGeometry.CellLight;
            check(Math.Abs(light - ideal) < 0.005f, P + $"at {px} px the cells give the field {light:0.000} of a dot's light, the dots' mean {ideal:0.000}");
        }
        double six = 0;
        double[] columns = Columns(6f, false);
        for (int i = 2; i + 6 < columns.Length - 2; i++) six = Math.Max(six, Math.Abs(columns[i] - columns[i + 6]));
        check(six < 1e-9, P + $"at 6 px a dot the dots' columns repeat every dot exactly (largest difference {six:0.000}): dots, not a beat");
    }

    static bool InDot(double d) { double f = d - Math.Floor(d); return Math.Abs(f - 0.5) <= 0.5 * LedGeometry.DotFill; }

    // a lit block of 60 x 32 dots at px pixels a dot, sampled at pixel centres (the grid off the pixels by a third of a pixel): each column's
    // lit share
    static double[] Columns(float px, bool cells)
    {
        int width = (int)(60 * px), height = (int)(32 * px);
        var columns = new double[width];
        for (int i = 0; i < width; i++)
        {
            double sum = 0;
            for (int j = 0; j < height; j++)
                sum += cells || InDot((i + 0.5 + 0.33) / px) && InDot((j + 0.5 + 0.21) / px) ? 1 : 0;
            columns[i] = sum / height;
        }
        return columns;
    }

    static float FieldBeat(float px, bool cells)
    {
        double[] v = Columns(px, cells);
        double mean = 0, n = 0;
        for (int i = 2; i < v.Length - 2; i++) { mean += v[i]; n++; }
        mean /= n;
        double var = 0;
        for (int i = 2; i < v.Length - 2; i++) var += (v[i] - mean) * (v[i] - mean);
        return (float)(Math.Sqrt(var / n) / Math.Max(1e-9, mean));
    }

    static float FieldLight(float px, bool cells)
    {
        double[] v = Columns(px, cells);
        double sum = 0;
        for (int i = 2; i < v.Length - 2; i++) sum += v[i];
        return (float)(sum / (v.Length - 4));
    }

    /// <summary>moirepreview DIR: the SWB6710's front sign ("K3路 › 柳树街") at several pitches: dots, cells (both at pixel centres), the ideal.</summary>
    internal static int Preview(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : "out/moirepreview";
        Directory.CreateDirectory(dir);
        if (!HdSignTests.Load(out DotFont font, out Pinyin pinyin, out LedFont led)) { Console.WriteLine("no fonts"); return 1; }
        var composer = new HdComposer(font, led, 240);
        composer.Compose(HdContent.Route("K", "3", "路", "柳树街", StopEnglish.Of("柳树街", pinyin)));
        composer.Render(0);
        float3[] inks = AiSignRules.HdInkColours(new float3(0.15f, 1f, 0.25f));
        foreach (float px in new[] { 0.8f, 1.37f, 2.2f, 3.5f, 6f })
        {
            string file = Path.Combine(dir, $"moire-{px:0.00}px.png");
            Write(file, composer, inks, px, 240, 3);
            Write(Path.Combine(dir, $"moire-{px:0.00}px-label.png"), composer, inks, px, 52, 8);
            Console.WriteLine($"{file} (and -label, the label at 8x): top the dots, middle the cells (both at pixel centres), bottom the ideal " +
                $"(16 x 16 samples a pixel of the dots); {px} px a dot; a lit field's columns vary {FieldBeat(px, false):0.000} / {FieldBeat(px, true):0.000}");
        }
        return 0;
    }

    // the first cols dot columns of the sign at px pixels a dot, each screen pixel a zoom x zoom square, the three ways one above the other
    static void Write(string file, HdComposer composer, float3[] inks, float px, int cols, int zoom)
    {
        int w = (int)Math.Ceiling(cols * px) + 2, h = (int)Math.Ceiling(32 * px) + 2, gap = 2;
        int iw = w * zoom, ih = (3 * h + 2 * gap) * zoom;
        var image = new byte[iw * ih * 3];
        for (int i = 0; i < w; i++)
            for (int j = 0; j < h; j++)
            {
                double u = (i + 0.5 + 0.33) / px, v = (j + 0.5 + 0.21) / px;
                float3 dot = Light(composer, inks, u, v, false), cell = Light(composer, inks, u, v, true) * LedGeometry.CellLight, ideal = 0;
                for (int sx = 0; sx < 16; sx++)
                    for (int sy = 0; sy < 16; sy++)
                        ideal += Light(composer, inks, (i + (sx + 0.5) / 16 + 0.33) / px, (j + (sy + 0.5) / 16 + 0.21) / px, false);
                ideal /= 256f;
                float3[] three = { dot, cell, ideal };
                for (int k = 0; k < 3; k++)
                    for (int zx = 0; zx < zoom; zx++)
                        for (int zy = 0; zy < zoom; zy++)
                        {
                            int o = (((k * (h + gap) + j) * zoom + zy) * iw + i * zoom + zx) * 3;
                            for (int c = 0; c < 3; c++) image[o + c] = Srgb(three[k][c]);
                        }
            }
        for (int k = 1; k < 3; k++)
            for (int y = (k * (h + gap) - gap) * zoom; y < k * (h + gap) * zoom; y++)
                for (int x = 0; x < iw; x++) { int o = (y * iw + x) * 3; image[o] = image[o + 1] = image[o + 2] = 90; }
        CardSignTests.Png(file, iw, ih, image);
    }

    // the sign's linear light at (x, y) dots: a lit dot's ink inside the dot (or anywhere in its cell)
    static float3 Light(HdComposer c, float3[] inks, double x, double y, bool cells)
    {
        int dx = (int)Math.Floor(x), dy = (int)Math.Floor(y);
        if (dx < 0 || dy < 0 || dx >= c.Width || dy >= c.Height || !c.Buffer[dx, dy] || c.Ink[dx, dy] >= inks.Length) return 0;
        if (!cells && (!InDot(x) || !InDot(y))) return 0;
        float3 s = inks[c.Ink[dx, dy]];
        return new float3(Linear(s.x), Linear(s.y), Linear(s.z));
    }

    static float Linear(float s) => s <= 0.04045f ? s / 12.92f : (float)Math.Pow((s + 0.055f) / 1.055f, 2.4);
    static byte Srgb(float l) { l = Math.Max(0f, Math.Min(1f, l)); float s = l <= 0.0031308f ? l * 12.92f : 1.055f * (float)Math.Pow(l, 1 / 2.4) - 0.055f; return (byte)Math.Round(s * 255f); }
}
