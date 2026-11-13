using System;
using CDLC.BusDriver;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// The body-wrap template pictures of an authored bus, rasterised offline from the same data the game uses (BusInterior.WrapCoverage:
// the livery triangles the wrap covers, in picture coordinates), to look at them without the game and to check them.
// Usage: dotnet run -c Release --project Tests/CDLC.BusDriver.Tests -- wraptemplate <pack .cdlb.json> <out dir>
static class WrapTemplatePreview
{
    static readonly byte[] Hidden = { 58, 63, 69 }, Solid = { 236, 238, 240 }, Grid = { 170, 178, 188 }, Front = { 230, 60, 40 };
    static readonly byte[] Outside = { 30, 34, 40 }, Behind = { 150, 156, 164 }, WindowA = { 150, 200, 235 }, WindowB = { 112, 168, 214 }, Door = { 120, 200, 140 };

    public static int Run(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("wraptemplate <pack .cdlb.json> <out dir>"); return 2; }
        string path = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(args[2]);
        VehicleProfile profile = VehicleProfileJson.Load(path);
        var sections = new List<BusInterior>();
        if (profile.Assembly != null)
        {
            VehicleModels.RegisterAssembly(profile.Assembly);
            sections.Add(new BusInterior(profile.Assembly.Front.Profile.Spec));
            sections.Add(new BusInterior(profile.Assembly.Rear.Profile.Spec));
        }
        else
        {
            VehicleModels.Register(profile);
            sections.Add(new BusInterior(profile.Spec));
        }

        string[] names = { "left", "right", "front", "rear", "roof" };
        int failed = 0;
        for (int f = 0; f < BusInterior.WrapFaceCount; f++)
        {
            var face = (BusInterior.WrapFace)f;
            bool along = face == BusInterior.WrapFace.Left || face == BusInterior.WrapFace.Right || face == BusInterior.WrapFace.Roof;
            var parts = along ? sections : sections.FindAll(s => s.HasWrap(face)).GetRange(0, Math.Min(1, sections.FindAll(s => s.HasWrap(face)).Count));
            if (parts.Count == 0) continue;
            float total = 0f;
            if (along) foreach (var s in parts) total += s.WrapLength; else total = parts[0].WrapSize(face).x;
            float height = parts[0].WrapSize(face).y;
            int w = along ? 2048 : 1024, h = Math.Clamp((int)Math.Round(w * height / total), 32, 4096);
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++) { rgb[i * 3] = Hidden[0]; rgb[i * 3 + 1] = Hidden[1]; rgb[i * 3 + 2] = Hidden[2]; }
            float start = 0f;
            var classes = new byte[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;
            foreach (var m in parts)
            {
                float a = along ? start / total : 0f, sShare = along ? m.WrapLength / total : 1f;
                m.ProjectWrap(face, classes, depth, w, h, a, sShare, face == BusInterior.WrapFace.Right);
                start += m.WrapLength;
            }
            int triangles = 0, windows = 0, doors = 0;
            var area = new byte[w * h];
            for (int i = 0; i < classes.Length; i++)
            {
                int x = i % w, y = i / w;
                switch (classes[i])
                {
                    case 0: Put(rgb, w, x, y, Outside); break;
                    case 1: Put(rgb, w, x, y, Solid); triangles++; break;
                    case 4: Put(rgb, w, x, y, Behind); break;
                    case 2: Put(rgb, w, x, y, ((x + y) >> 3 & 1) == 0 ? WindowA : WindowB); area[i] = WrapPackage.AreaWindow; windows++; break;
                    case 3: Put(rgb, w, x, y, Door); area[i] = WrapPackage.AreaDoor; doors++; break;
                }
            }
            if (windows + doors > 0) TestWrap(args[2], names[f], w, h, area, total);

            // the metre grid and the front edge, as the in-game template has them
            for (float d = 1f; d < total; d += 1f)
            {
                int x = Math.Clamp((int)Math.Round(d / total * w), 0, w - 1);
                for (int y = 0; y < h; y++) Put(rgb, w, x, y, Grid);
            }
            for (float d = 1f; d < height; d += 1f)
            {
                int row = Math.Clamp((int)Math.Round(d / height * h), 0, h - 1);
                for (int x = 0; x < w; x++) Put(rgb, w, x, row, Grid);
            }
            if (along)
            {
                int front = face == BusInterior.WrapFace.Right ? w - 6 : 0;
                for (int y = 0; y < h; y++) for (int x = front; x < front + 6; x++) Put(rgb, w, x, y, Front);
            }

            int coveredPixels = 0;
            for (int i = 0; i < w * h; i++) if (rgb[i * 3] == Solid[0] && rgb[i * 3 + 1] == Solid[1] && rgb[i * 3 + 2] == Solid[2]) coveredPixels++;
            if (coveredPixels < w * h * 0.02)
            {
                Console.WriteLine($"{names[f],-5} {w}x{h}  {triangles} body pixels, {100.0 * coveredPixels / (w * h):0.0} % covered: no picture written (the bus has no wrap here)");
                continue;
            }

            // the picture is stored top row first; v runs up
            var flipped = new byte[rgb.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgb, y * w * 3, flipped, (h - 1 - y) * w * 3, w * 3);
            WritePng(Path.Combine(args[2], names[f] + ".png"), w, h, flipped);
            int covered = 0;
            for (int i = 0; i < w * h; i++) if (rgb[i * 3] == Solid[0] && rgb[i * 3 + 1] == Solid[1]) covered++;
            Console.WriteLine($"{names[f],-5} {w}x{h}  {triangles} body, {windows} window and {doors} door pixels, {100.0 * covered / (w * h):0.0} % of the picture covered");
            if (triangles == 0) failed++;
        }

        return failed == 0 ? 0 : 1;
    }

    // A test picture (opaque orange with see-through holes every metre and a band) put through the outline the game applies, shown over
    // dark grey (the see-through), so the white outlines can be checked against the areas: <face>-outline.png.
    static void TestWrap(string dir, string name, int w, int h, byte[] area, float metres)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = 230; rgba[i + 1] = 120; rgba[i + 2] = 30; rgba[i + 3] = 255;
                float u = x / (float)w * metres, v = y / (float)h;
                bool hole = (v > 0.45f && v < 0.9f && (u % 1f) > 0.15f && (u % 1f) < 0.8f) || (v > 0.2f && v < 0.28f);
                if (hole) rgba[i + 3] = 0;
            }
        float radius = WrapPackage.OutlineRadius(10f, metres, w);
        int changed = WrapPackage.OutlineAreas(rgba, w, h, area, radius);
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, o = ((h - 1 - y) * w + x) * 3;
                bool cut = rgba[i + 3] < 128;
                rgb[o] = cut ? (byte)40 : rgba[i]; rgb[o + 1] = cut ? (byte)44 : rgba[i + 1]; rgb[o + 2] = cut ? (byte)50 : rgba[i + 2];
            }
        WritePng(Path.Combine(dir, name + "-outline.png"), w, h, rgb);
        Console.WriteLine($"{name,-5} outline radius {radius} px, {changed} pixels turned white");
    }

    // Writes the model's template zip exactly as the game does (WrapTemplate.Build): Wraps/<model>/template.zip
    // Usage: dotnet run -c Release --project Tests/CDLC.BusDriver.Tests -- wraptemplatezip <pack .cdlb.json> <out zip>
    public static int RunZip(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("wraptemplatezip <pack .cdlb.json> <out zip>"); return 2; }
        VehicleProfile profile = VehicleProfileJson.Load(Path.GetFullPath(args[1]));
        var sections = new List<BusInterior>();
        if (profile.Assembly != null)
        {
            // as the game's BusSkins.SetVehicle: every section, front first, the front's profile names the model and its layout
            VehicleModels.RegisterAssembly(profile.Assembly);
            sections.Add(new BusInterior(profile.Assembly.Front.Profile.Spec));
            sections.Add(new BusInterior(profile.Assembly.Rear.Profile.Spec));
            profile = profile.Assembly.Front.Profile;
        }
        else
        {
            VehicleModels.Register(profile);
            sections.Add(new BusInterior(profile.Spec));
        }
        var input = new WrapTemplate.Input { English = LangPack.Text("en-US"), Chinese = LangPack.Text("zh-HANS"), Model = profile.Name, Layout = profile.WrapLayout, OutlineMm = profile.WrapOutlineMm, Profile = profile, Sections = sections };
        if (!WrapTemplate.Build(input, out WrapPackage.Manifest manifest, out List<KeyValuePair<string, byte[]>> entries)) { Console.Error.WriteLine("no face has a picture"); return 1; }
        WrapPackage.Write(Path.GetFullPath(args[2]), manifest, entries);
        Console.WriteLine($"TEMPLATE_ZIP_OK {args[2]}: {entries.Count} entries, layout {manifest.Layout}, outline {manifest.OutlineMm} mm, {manifest.Title}");
        return 0;
    }

    // A demo wrap for the model (zip) and what the game makes of it: every face its own colour and letter, the windows and the door pieces
    // cut out (alpha 0) where the model has them, a see-through disc in the body. The previews apply the game's own outline
    // (WrapPackage.OutlineAreas, the model's outlineMm) and show the see-through parts over dark glass (windows, doors) or the line colour (body).
    // Usage: dotnet run -c Release --project Tests/CDLC.BusDriver.Tests -- wrapdemo <pack .cdlb.json> <out dir>
    static readonly string[][] Letters =
    {
        new[] { "X....", "X....", "X....", "X....", "X....", "X....", "XXXXX" },
        new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X.X..", "X..X.", "X...X" },
        new[] { "XXXXX", "X....", "X....", "XXXX.", "X....", "X....", "X...." },
        new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X...X", "X...X", "XXXX." },
        new[] { "XXXXX", "..X..", "..X..", "..X..", "..X..", "..X..", "..X.." },
    };

    public static int RunDemo(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("wrapdemo <pack .cdlb.json> <out dir>"); return 2; }
        VehicleProfile profile = VehicleProfileJson.Load(Path.GetFullPath(args[1]));
        if (profile.Assembly != null) { Console.Error.WriteLine("articulated packs are not supported by this tool"); return 2; }
        VehicleModels.Register(profile);
        var bus = new BusInterior(profile.Spec);
        string dir = Path.Combine(Path.GetFullPath(args[2]), profile.Name);
        Directory.CreateDirectory(dir);
        string previews = Path.Combine(Path.GetFullPath(args[2]), "preview-" + profile.Name);
        Directory.CreateDirectory(previews);
        byte[][] dark = { new byte[] { 36, 44, 56 }, new byte[] { 200, 235, 232 } };           // see-through: glass / line colour
        byte[][] a = { new byte[] { 230, 70, 60 }, new byte[] { 60, 110, 230 }, new byte[] { 50, 170, 90 }, new byte[] { 240, 150, 40 }, new byte[] { 150, 80, 200 } };
        string[] names = { "left", "right", "front", "rear", "roof" };
        var entries = new List<KeyValuePair<string, byte[]>>();
        for (int f = 0; f < BusInterior.WrapFaceCount; f++)
        {
            var face = (BusInterior.WrapFace)f;
            if (!bus.HasWrap(face)) continue;
            float2 size = bus.WrapSize(face);
            int w = WrapTemplate.Along(face) ? 2048 : 1024, h = Math.Clamp((int)Math.Round(w * size.y / size.x), 32, 4096);
            var classes = new byte[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;
            bus.ProjectWrap(face, classes, depth, w, h, 0f, 1f, face == BusInterior.WrapFace.Right);
            var rgba = new byte[w * h * 4];
            int period = Math.Max(8, w / 64);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    bool light = ((x + (f % 2 == 0 ? y : -y) + 4 * w) / period & 1) == 0;
                    byte[] c = a[f];
                    rgba[i * 4] = (byte)(light ? Math.Min(255, c[0] + 40) : c[0]); rgba[i * 4 + 1] = (byte)(light ? Math.Min(255, c[1] + 40) : c[1]); rgba[i * 4 + 2] = (byte)(light ? Math.Min(255, c[2] + 40) : c[2]);
                    rgba[i * 4 + 3] = 255;
                    if (classes[i] == VehicleMesh.ClassWindow || classes[i] == VehicleMesh.ClassDoor) rgba[i * 4 + 3] = 0;         // the model's windows and door pieces are cut out
                }
            // a see-through disc in the body and the face's letter
            int cx = (int)(w * (WrapTemplate.Along(face) ? 0.42f : 0.2f)), cy = (int)(h * 0.16f), r = (int)(h * 0.07f);
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                    if (x >= 0 && y >= 0 && x < w && y < h && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) rgba[(y * w + x) * 4 + 3] = 0;
            int scale = Math.Max(3, (int)(h * 0.2f / 7f)), x0 = w / 2 - 5 * scale / 2, y0 = (int)(h * 0.08f);
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < 5; col++)
                    if (Letters[f][row][col] == 'X')
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int x = x0 + col * scale + dx, y = y0 + (6 - row) * scale + dy;
                                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                                int i = (y * w + x) * 4;
                                rgba[i] = rgba[i + 1] = rgba[i + 2] = 20; rgba[i + 3] = 255;
                            }
            entries.Add(new KeyValuePair<string, byte[]>(names[f] + ".png", WrapTemplate.Png(w, h, rgba)));

            // what the game makes of it: the outline in the window and door areas, shown over what shows through
            var area = new byte[w * h];
            for (int i = 0; i < area.Length; i++) area[i] = classes[i] == VehicleMesh.ClassWindow ? WrapPackage.AreaWindow : classes[i] == VehicleMesh.ClassDoor ? WrapPackage.AreaDoor : WrapPackage.AreaNone;
            var processed = (byte[])rgba.Clone();
            float radius = WrapPackage.OutlineRadius(profile.WrapOutlineMm, size.x, w);
            int changed = WrapPackage.OutlineAreas(processed, w, h, area, radius);
            // what the game draws: the picture only on the body paint, the window skins and the door pieces (the wrap's mesh); paint with a
            // part in front of it shows that part (mid grey), the rest of the bus stays as the model has it (dark grey), outside the bus darkest
            var shown = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                bool cut = processed[i * 4 + 3] < 128;
                byte[] picture = cut ? dark[area[i] != 0 ? 0 : 1] : new[] { processed[i * 4], processed[i * 4 + 1], processed[i * 4 + 2] };
                byte[] c;
                switch (classes[i])
                {
                    case VehicleMesh.ClassBody:
                    case VehicleMesh.ClassWindow:
                    case VehicleMesh.ClassDoor: c = picture; break;
                    case VehicleMesh.ClassBehind: c = new byte[] { 150, 156, 164 }; break;
                    case VehicleMesh.ClassBus: c = new byte[] { 58, 63, 69 }; break;
                    default: c = new byte[] { 20, 24, 30 }; break;
                }
                shown[i * 4] = c[0]; shown[i * 4 + 1] = c[1]; shown[i * 4 + 2] = c[2]; shown[i * 4 + 3] = 255;
            }
            File.WriteAllBytes(Path.Combine(previews, names[f] + "-as-in-game.png"), WrapTemplate.Png(w, h, shown));
            Console.WriteLine($"{names[f],-5} {w}x{h}  outline {profile.WrapOutlineMm} mm = {radius} px, {changed} pixels turned white");
        }

        var manifest = new WrapPackage.Manifest { Model = profile.Name, Layout = profile.WrapLayout, OutlineMm = profile.WrapOutlineMm, Title = "demo faces" };
        WrapPackage.Write(Path.Combine(dir, "Demo faces.zip"), manifest, entries);
        Console.WriteLine($"WRAP_DEMO_OK {Path.Combine(dir, "Demo faces.zip")}");
        return 0;
    }

    static void Put(byte[] rgb, int w, int x, int y, byte[] c) { int i = (y * w + x) * 3; rgb[i] = c[0]; rgb[i + 1] = c[1]; rgb[i + 2] = c[2]; }

    static void Fill(byte[] rgb, int w, int h, float2 a, float2 b, float2 c)
    {
        float2 pa = new float2(a.x * w, a.y * h), pb = new float2(b.x * w, b.y * h), pc = new float2(c.x * w, c.y * h);
        float area = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
        if (Math.Abs(area) < 1e-9f) return;
        int x0 = Math.Clamp((int)Math.Floor(Math.Min(pa.x, Math.Min(pb.x, pc.x))), 0, w - 1), x1 = Math.Clamp((int)Math.Ceiling(Math.Max(pa.x, Math.Max(pb.x, pc.x))), 0, w - 1);
        int y0 = Math.Clamp((int)Math.Floor(Math.Min(pa.y, Math.Min(pb.y, pc.y))), 0, h - 1), y1 = Math.Clamp((int)Math.Ceiling(Math.Max(pa.y, Math.Max(pb.y, pc.y))), 0, h - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + .5f, py = y + .5f;
                float w0 = ((pb.x - px) * (pc.y - py) - (pb.y - py) * (pc.x - px)) / area;
                float w1 = ((pc.x - px) * (pa.y - py) - (pc.y - py) * (pa.x - px)) / area;
                float w2 = 1f - w0 - w1;
                if (w0 >= -0.002f && w1 >= -0.002f && w2 >= -0.002f) Put(rgb, w, x, y, Solid);
            }
    }

    static void WritePng(string path, int w, int h, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++) { raw[y * (w * 3 + 1)] = 0; Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3); }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", new byte[0]);
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        uint crc = Crc(td);
        var c = new byte[4]; BE(c, 0, (int)crc); s.Write(c);
    }

    static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return ~c;
    }
}
