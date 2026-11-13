using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using CDLC.BusDriver;

// Wrap files: the zip reading and its limits, the manifest, the per-model rule, and the picture work done once per load.
static class WrapPackageTests
{
    const string Prefix = "wrap package: ";
    static readonly byte[] Png = { 0x89, 0x50, 0x4e, 0x47 };

    internal static void Run(Action<bool, string> check)
    {
        Names(check);
        Manifests(check);
        Zips(check);
        Pictures(check);
    }

    static void Names(Action<bool, string> check)
    {
        check(WrapPackage.ValidName("Smile 1") && WrapPackage.ValidName("小蓝鲸-v2") && WrapPackage.ValidName("a.b_c-d"), Prefix + "ordinary wrap names are valid");
        check(!WrapPackage.ValidName("") && !WrapPackage.ValidName("..") && !WrapPackage.ValidName("a/b") && !WrapPackage.ValidName("a\\b") &&
              !WrapPackage.ValidName("C:x") && !WrapPackage.ValidName(" x") && !WrapPackage.ValidName("x.") && !WrapPackage.ValidName(new string('a', 65)),
            Prefix + "paths, drives, dots and long names are not names");
        string root = Path.Combine(Path.GetTempPath(), "wraps");
        check(WrapPackage.ZipPath(root, "SWB6710EV01G", "Smile") == Path.Combine(root, "SWB6710EV01G", "Smile.zip"), Prefix + "a wrap lives in its model's folder");
        check(WrapPackage.ZipPath(root, "../x", "Smile") == null && WrapPackage.ZipPath(root, "SWB6710EV01G", "../Smile") == null && WrapPackage.ModelFolder(root, "a b") == null,
            Prefix + "a model or wrap name cannot leave the wraps folder");
    }

    static void Manifests(Action<bool, string> check)
    {
        var m = new WrapPackage.Manifest { Model = "SWB6710EV01G", Layout = 3, OutlineMm = 12.5f, Title = "Smile \"1\"" };
        WrapPackage.Manifest back = WrapPackage.ParseManifest(WrapPackage.ManifestJson(m));
        check(back.Model == m.Model && back.Layout == 3 && back.OutlineMm == 12.5f && back.Title == m.Title, Prefix + "the manifest reads back what was written");
        Func<string, bool> refuses = text => { try { WrapPackage.ParseManifest(text); return false; } catch (WrapPackage.Error) { return true; } };
        string good = WrapPackage.ManifestJson(m);
        check(refuses("{ ") && refuses("[]") && refuses(good.Replace("cdlb-wrap", "x")) && refuses(good.Replace("\"version\": 1", "\"version\": 2")) &&
              refuses(good.Replace("SWB6710EV01G", "../x")) && refuses(good.Replace("\"layout\": 3", "\"layout\": -1")) && refuses(good.Replace("12.5", "99")),
            Prefix + "bad formats, newer versions, bad model names, layouts and outline widths are refused");
        check(WrapPackage.Mismatch(m, "SWB6710EV01G", 3) == null && WrapPackage.Mismatch(m, "CDLCBus12", 3) != null && WrapPackage.Mismatch(m, "SWB6710EV01G", 4) != null,
            Prefix + "a wrap fits only its own model at its own layout");
    }

    static string Temp() { string d = Path.Combine(Path.GetTempPath(), "cdlc-wrap-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    static void Zips(Action<bool, string> check)
    {
        string dir = Temp();
        try
        {
            string path = Path.Combine(dir, "Smile.zip");
            var entries = new List<KeyValuePair<string, byte[]>>
            {
                new KeyValuePair<string, byte[]>("left.png", Png), new KeyValuePair<string, byte[]>("roof.png", new byte[] { 1, 2, 3 }),
                new KeyValuePair<string, byte[]>("guide/left.png", new byte[] { 9 }), new KeyValuePair<string, byte[]>("README.txt", Encoding.UTF8.GetBytes("x")),
            };
            WrapPackage.Write(path, new WrapPackage.Manifest { Model = "SWB6710EV01G", Layout = 1 }, entries);
            WrapPackage.Package p = WrapPackage.Read(path);
            check(p.Manifest.Model == "SWB6710EV01G" && p.Faces[0] != null && p.Faces[0].Length == 4 && p.Faces[4] != null && p.Faces[1] == null && p.Faces[2] == null && p.Any,
                Prefix + "a zip reads back its pictures and manifest; the guide and README entries are not pictures");
            check(!File.Exists(path + ".tmp"), Prefix + "writing leaves no temporary file");
            check(p.Guides[0] == null && WrapPackage.Read(path, true).Guides[0] != null && WrapPackage.Read(path, true).Guides[0].Length == 1 && WrapPackage.Read(path, true).Guides[1] == null,
                Prefix + "the guide pictures are read only when asked for");
            string withInfo = Path.Combine(dir, "Info.zip");
            WrapPackage.Write(withInfo, new WrapPackage.Manifest { Model = "SWB6710EV01G", Layout = 2, Title = "template r1", Info = "{ \"faces\": { \"left\": { \"metres\": [7.1, 2.4] } }, \"doors\": true }" }, entries);
            WrapPackage.Manifest infoBack = WrapPackage.Read(withInfo).Manifest;
            check(infoBack.Model == "SWB6710EV01G" && infoBack.Layout == 2 && infoBack.Title == "template r1", Prefix + "a manifest with the template's info (a nested JSON object) still reads");

            Func<string, Action<ZipArchive>, bool> refuses = (name, fill) =>
            {
                string p2 = Path.Combine(dir, name + ".zip");
                using (var f = File.Create(p2)) using (var z = new ZipArchive(f, ZipArchiveMode.Create)) fill(z);
                try { WrapPackage.Read(p2); return false; } catch (WrapPackage.Error) { return true; }
            };
            Action<ZipArchive, string, byte[]> put = (z, n, b) => { using (var s = z.CreateEntry(n).Open()) s.Write(b, 0, b.Length); };
            string manifest = WrapPackage.ManifestJson(new WrapPackage.Manifest { Model = "SWB6710EV01G", Layout = 1 });
            check(refuses("nomanifest", z => put(z, "left.png", Png)), Prefix + "a zip without wrap.json is refused");
            check(refuses("dots", z => { put(z, "wrap.json", Encoding.UTF8.GetBytes(manifest)); put(z, "a/../left.png", Png); }), Prefix + "an entry with .. is refused");
            check(refuses("abs", z => { put(z, "wrap.json", Encoding.UTF8.GetBytes(manifest)); put(z, "/left.png", Png); }), Prefix + "an absolute entry is refused");
            check(refuses("drive", z => { put(z, "wrap.json", Encoding.UTF8.GetBytes(manifest)); put(z, "C:left.png", Png); }), Prefix + "an entry with a drive is refused");
            check(refuses("many", z => { put(z, "wrap.json", Encoding.UTF8.GetBytes(manifest)); for (int i = 0; i < WrapPackage.MaxEntries + 1; i++) put(z, "e" + i, Png); }), Prefix + "too many entries are refused");
            string garbage = Path.Combine(dir, "garbage.zip");
            File.WriteAllBytes(garbage, new byte[] { 1, 2, 3, 4, 5 });
            bool bad = false;
            try { WrapPackage.Read(garbage); } catch (WrapPackage.Error) { bad = true; }
            check(bad, Prefix + "a file that is not a zip is refused");
            check(refuses("model", z => put(z, "wrap.json", Encoding.UTF8.GetBytes(manifest.Replace("SWB6710EV01G", "x y")))), Prefix + "a manifest naming no valid model is refused");
        }
        finally { Directory.Delete(dir, true); }
    }

    static byte[] Image(int w, int h, Func<int, int, byte[]> pixel)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) Array.Copy(pixel(x, y), 0, rgba, (y * w + x) * 4, 4);
        return rgba;
    }

    static void Pictures(Action<bool, string> check)
    {
        check(WrapPackage.OutlinePixels(10f, 7.145f, 2048) == 3 && WrapPackage.OutlinePixels(0f, 7.145f, 2048) == 0 && WrapPackage.OutlinePixels(10f, 2.35f, 100000) == WrapPackage.MaxOutlinePixels &&
              WrapPackage.OutlinePixels(1f, 7.145f, 64) == 1, Prefix + "the outline width in pixels follows the face size and is capped");

        // painted red on the left half, see-through on the right; the window area is rows 2..7 over the whole width
        const int W = 24, H = 10;
        byte[] red = { 200, 20, 20, 255 }, clear = { 0, 0, 0, 0 };
        byte[] pic = Image(W, H, (x, y) => x < 12 ? red : clear);
        var area = new byte[W * H];
        for (int y = 2; y < 8; y++) for (int x = 0; x < W; x++) area[y * W + x] = WrapPackage.AreaWindow;
        byte[] before = (byte[])pic.Clone();
        int changed = WrapPackage.OutlineAreas(pic, W, H, area, 3);
        bool ok = changed > 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool inside = area[y * W + x] != 0;
                bool white = pic[i] == 255 && pic[i + 1] == 255 && pic[i + 2] == 255 && pic[i + 3] == 255;
                bool wanted = inside && x >= 12 && x < 15;      // 3 pixels beyond the painted edge, inside the area
                if (x >= 12 && white != wanted) ok = false;
                if (x < 12 && (pic[i] != before[i] || pic[i + 3] != before[i + 3])) ok = false;    // painted pixels keep their red
                if (!inside && pic[i + 3] != before[i + 3]) ok = false;
            }
        check(ok && changed == 3 * 6, Prefix + "a see-through hole in a window gets a white edge of the width asked, only inside the window area (" + changed + " pixels)");
        byte[] untouched = Image(W, H, (x, y) => x < 12 ? red : clear);
        check(WrapPackage.OutlineAreas(untouched, W, H, new byte[W * H], 3) == 0 && WrapPackage.OutlineAreas(untouched, W, H, area, 0) == 0,
            Prefix + "outside every area, or with a zero width, nothing changes");
        byte[] hole = Image(W, H, (x, y) => (x >= 8 && x < 16 && y >= 3 && y < 7) ? clear : red);
        int ring = WrapPackage.OutlineAreas(hole, W, H, area, 1);
        check(ring == 2 * (8 + 4) - 4, Prefix + "a window hole inside the paint gets a one pixel white ring just inside its edge (" + ring + " pixels)");

        // the outline is round and smooth: along a diagonal edge its outer boundary is anti-aliased (partial alpha), it stays within the width
        // asked, and the picture's own soft edge lies over the white instead of being replaced by it
        const int D = 40;
        byte[] diagonal = Image(D, D, (x, y) => x + y < D ? red : clear);
        var wholeArea = new byte[D * D];
        for (int i = 0; i < wholeArea.Length; i++) wholeArea[i] = WrapPackage.AreaWindow;
        int smooth = WrapPackage.OutlineAreas(diagonal, D, D, wholeArea, 3.5f);
        int partial = 0, solidWhite = 0, beyond = 0;
        for (int y = 0; y < D; y++)
            for (int x = 0; x < D; x++)
            {
                int i = (y * D + x) * 4;
                if (x + y < D) continue;
                float across = (x + y - (D - 1)) / 1.41421356f;          // distance from the painted edge's pixel centres along the diagonal's normal
                bool white = diagonal[i] == 255 && diagonal[i + 1] == 255 && diagonal[i + 2] == 255;
                if (diagonal[i + 3] > 0 && diagonal[i + 3] < 255) partial++;
                if (white && diagonal[i + 3] == 255) solidWhite++;
                if (across > 3.5f + 1.01f && diagonal[i + 3] != 0) beyond++;
            }
        check(smooth > 0 && partial >= 8 && solidWhite >= 8 && beyond == 0,
            Prefix + "the outline along a diagonal edge is anti-aliased, round and no wider than asked (" + partial + " partly covered, " + solidWhite + " solid, " + beyond + " too far)");
        byte[] ramp = Image(8, 1, (x, y) => x < 3 ? red : x == 3 ? new byte[] { 0, 0, 200, 60 } : clear);
        var rampArea = new byte[8];
        for (int i = 0; i < rampArea.Length; i++) rampArea[i] = WrapPackage.AreaWindow;
        WrapPackage.OutlineAreas(ramp, 8, 1, rampArea, 3f);
        // x = 3: the picture's own pixel (blue, alpha 60) over white: more opaque than it was, still blue-tinted, not replaced by white
        check(ramp[3 * 4 + 3] > 60 && ramp[3 * 4 + 3] < 255 + 1 && ramp[3 * 4 + 2] > ramp[3 * 4] && ramp[3 * 4] < 255 && ramp[3 * 4 + 1] == ramp[3 * 4],
            Prefix + "the picture's own soft edge lies over the white outline (" + ramp[3 * 4] + "," + ramp[3 * 4 + 1] + "," + ramp[3 * 4 + 2] + "," + ramp[3 * 4 + 3] + ")");

        var filled = new byte[10 * 10];
        WrapPackage.FillArea(filled, 10, 10, new Unity.Mathematics.float2(0f, 0f), new Unity.Mathematics.float2(1f, 0f), new Unity.Mathematics.float2(0f, 1f), WrapPackage.AreaDoor);
        int count = 0;
        foreach (byte k in filled) if (k == WrapPackage.AreaDoor) count++;
        check(filled[0] == WrapPackage.AreaDoor && filled[9 * 10] == WrapPackage.AreaDoor && filled[9] == WrapPackage.AreaDoor && filled[9 * 10 + 9] == 0 && count >= 50 && count <= 62,
            Prefix + "an area triangle in picture coordinates fills half the picture (" + count + " pixels)");

        // the colour under the see-through pixels: the nearest painted pixel's, alpha untouched, the soft edge and a picture without paint left alone
        byte[] garbage = Image(12, 1, (x, y) => x < 4 ? new byte[] { 200, 20, 20, 255 } : x == 4 ? new byte[] { 20, 20, 200, 100 } : new byte[] { 0, 255, 0, 0 });
        byte[] garbageBefore = (byte[])garbage.Clone();
        int cleaned = WrapPackage.Decontaminate(garbage, 12, 1);
        bool cleanOk = cleaned == 7;
        for (int x = 0; x < 12; x++)
        {
            int i = x * 4;
            cleanOk &= garbage[i + 3] == garbageBefore[i + 3];                                           // alpha never changes
            if (x < 5) cleanOk &= garbage[i] == garbageBefore[i] && garbage[i + 1] == garbageBefore[i + 1] && garbage[i + 2] == garbageBefore[i + 2];   // painted and soft pixels keep their colour
            else cleanOk &= garbage[i] == 200 && garbage[i + 1] == 20 && garbage[i + 2] == 20;            // see-through ones: the nearest painted colour (pixel 3)
        }
        check(cleanOk, Prefix + "the colour under see-through pixels becomes the nearest painted pixel's, alpha and the soft edge unchanged (" + cleaned + " pixels)");
        byte[] twoSources = Image(10, 8, (x, y) => x == 1 && y == 1 ? new byte[] { 255, 0, 0, 255 } : x == 8 && y == 6 ? new byte[] { 0, 0, 255, 255 } : new byte[] { 0, 0, 0, 0 });
        WrapPackage.Decontaminate(twoSources, 10, 8);
        check(twoSources[(1 * 10 + 3) * 4] == 255 && twoSources[(1 * 10 + 3) * 4 + 2] == 0 && twoSources[(6 * 10 + 6) * 4 + 2] == 255 && twoSources[(6 * 10 + 6) * 4] == 0,
            Prefix + "with two painted pixels each see-through pixel takes the colour of the nearer one, in two dimensions");
        byte[] noPaint = Image(6, 2, (x, y) => new byte[] { 9, 99, 199, 0 });
        byte[] translucent = Image(6, 2, (x, y) => new byte[] { 9, 99, 199, 170 });
        check(WrapPackage.Decontaminate(noPaint, 6, 2) == 0 && noPaint[0] == 9 && WrapPackage.Decontaminate(translucent, 6, 2) == 0 && translucent[0] == 9,
            Prefix + "a picture with no painted pixel, or one that is uniformly translucent, is left as it is");
        Shares(check);
    }

    // A picture 20 columns wide whose column x has red = x (the front of the bus at u = 0 of the whole bus).
    static void Shares(Action<bool, string> check)
    {
        const int W = 20, H = 2;
        byte[] picture = Image(W, H, (x, y) => new byte[] { (byte)x, (byte)(100 + y), 7, 255 });

        // the whole bus: the left side and the roof as they are, the right side mirrored (its front is at its right edge)
        WrapPackage.ShareColumns(W, 0f, 1f, false, out int a0, out int a1, out bool aMirror);
        WrapPackage.ShareColumns(W, 0f, 1f, true, out int b0, out int b1, out bool bMirror);
        byte[] left = WrapPackage.Columns(picture, W, H, a0, a1, aMirror), right = WrapPackage.Columns(picture, W, H, b0, b1, bMirror);
        check(a0 == 0 && a1 == W && !aMirror && left.Length == W * H * 4 && left[0] == 0 && left[(W - 1) * 4] == W - 1,
            Prefix + "the left side of the whole bus is its picture as it is");
        check(b0 == 0 && b1 == W && bMirror && right[0] == W - 1 && right[(W - 1) * 4] == 0 && right[(W + 2) * 4 + 1] == 101,
            Prefix + "the right side of the whole bus is its picture mirrored, rows kept (the front stays at the bus's front)");

        // a front section of 12 / 20 of the bus, then a rear section of 8 / 20 (u measured from the bus's front)
        WrapPackage.ShareColumns(W, 0f, 0.6f, false, out int f0, out int f1, out _);
        WrapPackage.ShareColumns(W, 0.6f, 0.4f, false, out int r0, out int r1, out _);
        check(f0 == 0 && f1 == 12 && r0 == 12 && r1 == W, Prefix + "the sections of the left side and the roof take consecutive columns from the front on");
        WrapPackage.ShareColumns(W, 0f, 0.6f, true, out int g0, out int g1, out bool gMirror);
        WrapPackage.ShareColumns(W, 0.6f, 0.4f, true, out int h0, out int h1, out _);
        byte[] frontRight = WrapPackage.Columns(picture, W, H, g0, g1, gMirror), rearRight = WrapPackage.Columns(picture, W, H, h0, h1, true);
        check(g0 == 8 && g1 == W && h0 == 0 && h1 == 8 && frontRight.Length == 12 * H * 4 && frontRight[0] == W - 1 && frontRight[11 * 4] == 8 && rearRight[0] == 7 && rearRight[7 * 4] == 0,
            Prefix + "the sections of the right side take the picture's right part first, each run backwards, and cover it once");
        check(f1 - f0 + (r1 - r0) == W && (g1 - g0) + (h1 - h0) == W, Prefix + "the shares of the sections add up to the picture, none lost or doubled");

        // a share never leaves the picture or collapses
        WrapPackage.ShareColumns(W, 0.999f, 0.5f, false, out int e0, out int e1, out _);
        WrapPackage.ShareColumns(W, -1f, 0.0001f, true, out int k0, out int k1, out _);
        check(e0 >= 0 && e1 <= W && e1 > e0 && k0 >= 0 && k1 <= W && k1 > k0, Prefix + "a share is at least one column and inside the picture");
        bool threw = false;
        try { WrapPackage.Columns(picture, W, H, 5, 5, false); } catch (ArgumentException) { threw = true; }
        check(threw, Prefix + "columns outside the picture are refused");
    }
}
