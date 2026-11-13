using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver;
using Unity.Mathematics;

// The picture work of a wrap, after it was made cache-friendly, against the code it replaced (WrapReference, copied from before): the same
// pixels and the same counts, bit for bit, on random pictures of every awkward size, on a wrap-sized picture and on the real wraps.
static class WrapSpeedTests
{
    const string Prefix = "wrap speed: ";

    internal static void Run(Action<bool, string> check)
    {
        Thumbnails(check);
        var rng = new System.Random(20261019);
        bool outlines = true, cleans = true;
        int pictures = 0;
        foreach (int w in new[] { 1, 2, 5, 15, 16, 17, 31, 32, 33, 47, 64, 100 })
            foreach (int h in new[] { 1, 2, 3, 16, 17, 41 })
            {
                for (int round = 0; round < 3; round++)
                {
                    var pic = new byte[w * h * 4];
                    rng.NextBytes(pic);
                    // alpha: mostly 0 or 255 so there are painted and see-through regions, with soft values between
                    for (int i = 0; i < w * h; i++)
                    {
                        int r = rng.Next(10);
                        pic[4 * i + 3] = (byte)(round == 2 ? rng.Next(256) : r < 4 ? 0 : r < 8 ? 255 : rng.Next(256));
                    }
                    var area = new byte[w * h];
                    for (int i = 0; i < area.Length; i++) area[i] = (byte)(rng.Next(3) == 0 ? 0 : rng.Next(1, 3));
                    float radius = 0.5f + (float)rng.NextDouble() * 6f;

                    byte[] a = (byte[])pic.Clone(), b = (byte[])pic.Clone();
                    int ca = WrapReference.OutlineAreas(a, w, h, area, radius), cb = WrapPackage.OutlineAreas(b, w, h, area, radius);
                    outlines &= ca == cb && Same(a, b);
                    int da = WrapReference.Decontaminate(a, w, h), db = WrapPackage.Decontaminate(b, w, h);
                    cleans &= da == db && Same(a, b);
                    pictures++;
                }
            }
        check(outlines, Prefix + $"the outline gives the same pixels and count on {pictures} random pictures (widths 1 to 100 round the block of 16)");
        check(cleans, Prefix + $"the colour under see-through pixels is the same on {pictures} random pictures");

        // wrap-sized, with the windows and doors of a wrap
        {
            bool same = true;
            foreach (var (w, h) in new[] { (2048, 785), (1024, 1194), (2048, 674) })
            {
                byte[] pic = WrapBench.Picture(w, h, w + h, out var areas);
                byte[] a = (byte[])pic.Clone(), b = (byte[])pic.Clone();
                var area = new byte[w * h];
                foreach ((byte kind, float2x3 uv) in areas) WrapPackage.FillArea(area, w, h, uv.c0, uv.c1, uv.c2, kind);
                float radius = WrapPackage.OutlineRadius(25f, 10.5f, w);
                same &= WrapReference.OutlineAreas(a, w, h, area, radius) == WrapPackage.OutlineAreas(b, w, h, area, radius) && Same(a, b);
                same &= WrapReference.Decontaminate(a, w, h) == WrapPackage.Decontaminate(b, w, h) && Same(a, b);
            }
            check(same, Prefix + "a wrap-sized picture (2048 x 785, 1024 x 1194, 2048 x 674) comes out the same");
        }

        // the real wraps in the repository root, with the model's own windows and doors
        {
            int count = 0;
            bool same = true;
            foreach (string zip in Directory.GetFiles(Repo.Root, "*.zip"))
            {
                WrapPackage.Package package;
                try { package = WrapPackage.Read(zip, false); }
                catch (WrapPackage.Error) { continue; }
                foreach (byte[] bytes in package.Faces)
                {
                    if (bytes == null || !PngDecoder.TryDecode(bytes, out int w, out int h, out byte[] pic)) continue;
                    byte[] a = (byte[])pic.Clone(), b = (byte[])pic.Clone();
                    var area = new byte[w * h];
                    for (int i = 0; i < area.Length; i += 3) area[i] = WrapPackage.AreaWindow;       // a pattern: the model's own areas are in wrapreal
                    same &= WrapReference.OutlineAreas(a, w, h, area, 2.4f) == WrapPackage.OutlineAreas(b, w, h, area, 2.4f) && Same(a, b);
                    same &= WrapReference.Decontaminate(a, w, h) == WrapPackage.Decontaminate(b, w, h) && Same(a, b);
                    count++;
                }
            }
            if (count > 0)
                check(same, Prefix + $"the {count} pictures of the real wraps come out the same");
        }
    }

    // The wraps manager's thumbnails, made on a worker since 0.12.7 (WrapPackage.ScaleDown, WrapTemplate.Png) instead of the graphics card.
    static void Thumbnails(Action<bool, string> check)
    {
        // 4 x 2 to 2 x 1: each pixel the mean of the 2 x 2 block it covers
        var four = new byte[]
        {
            0, 0, 0, 0,  100, 100, 100, 100,  10, 20, 30, 40,  50, 60, 70, 80,
            200, 200, 200, 200,  100, 100, 100, 100,  30, 40, 50, 60,  70, 80, 90, 100,
        };
        byte[] two = WrapPackage.ScaleDown(four, 4, 2, 2, 1);
        check(two[0] == 100 && two[3] == 100 && two[4] == 40 && two[5] == 50 && two[6] == 60 && two[7] == 70, "thumbnail: a pixel is the mean of the pixels it covers");

        var uniform = new byte[2048 * 785 * 4];
        for (int i = 0; i < uniform.Length; i += 4) { uniform[i] = 12; uniform[i + 1] = 200; uniform[i + 2] = 99; uniform[i + 3] = 255; }
        byte[] small = WrapPackage.ScaleDown(uniform, 2048, 785, 240, 92);
        bool same = small.Length == 240 * 92 * 4;
        for (int i = 0; same && i < small.Length; i += 4) same = small[i] == 12 && small[i + 1] == 200 && small[i + 2] == 99 && small[i + 3] == 255;
        check(same, "thumbnail: a one-colour 2048 x 785 picture scales to the same colour at 240 x 92");

        // bottom rows stay at the bottom: a picture red below, blue above
        var halves = new byte[64 * 32 * 4];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 64; x++) { int i = (y * 64 + x) * 4; halves[i] = (byte)(y < 16 ? 255 : 0); halves[i + 2] = (byte)(y < 16 ? 0 : 255); halves[i + 3] = 255; }
        byte[] halvesSmall = WrapPackage.ScaleDown(halves, 64, 32, 16, 8);
        check(halvesSmall[0] == 255 && halvesSmall[2] == 0 && halvesSmall[(7 * 16) * 4 + 2] == 255, "thumbnail: the picture's bottom stays its bottom");

        // the real wraps: decoded, scaled, encoded and decoded again at the thumbnail's size, the colours of a block kept
        int count = 0;
        bool ok = true;
        foreach (string zip in Directory.GetFiles(Repo.Root, "*.zip"))
        {
            WrapPackage.Package package;
            try { package = WrapPackage.Read(zip, false); }
            catch (WrapPackage.Error) { continue; }
            byte[] left = package.Faces[0];
            if (left == null || !PngDecoder.TryDecode(left, out int w, out int h, out byte[] rgba)) continue;
            int th = Math.Min(120, Math.Max(16, (int)Math.Round(240 * h / (double)w)));
            byte[] thumb = WrapPackage.ScaleDown(rgba, w, h, 240, th);
            byte[] png = WrapTemplate.Png(240, th, thumb);
            ok &= PngDecoder.TryDecode(png, out int tw, out int tth, out byte[] back) && tw == 240 && tth == th && Same(back, thumb);
            count++;
        }
        if (count > 0)
            check(ok, $"thumbnail: the left side of the {count} real wraps makes a 240-wide PNG that reads back pixel for pixel");
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
