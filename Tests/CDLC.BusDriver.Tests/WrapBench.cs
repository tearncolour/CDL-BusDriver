using System;
using System.Collections.Generic;
using System.Diagnostics;
using CDLC.BusDriver;
using Unity.Mathematics;

// Timings of the body wrap's picture work at the real picture sizes (dotnet run -c Release -- wrapbench [repeats]): the pictures are
// synthetic (painted body, window and door cut-outs with garbage colour under them, soft edges) because the user's PNGs are not in the
// repository; the work is the same code the game runs on a wrap's pictures (WrapPackage.Prepare).
static class WrapBench
{
    // The five pictures of the SWB6710EV01G wraps (their sizes are in the game's log of 18 October).
    static readonly (string name, int w, int h)[] kPictures =
    {
        ("left", 2048, 785), ("right", 2048, 785), ("front", 1024, 1194), ("rear", 1024, 1194), ("roof", 2048, 674),
    };

    /// <summary>A picture like a wrap's: opaque paint with see-through windows and doors (garbage colour under them), soft edges.</summary>
    internal static byte[] Picture(int w, int h, int seed, out List<(byte kind, float2x3 uv)> areas)
    {
        var rng = new System.Random(seed);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int x = i % w, y = i / w;
            rgba[4 * i] = (byte)(60 + 100 * x / w); rgba[4 * i + 1] = (byte)(90 + 80 * y / h); rgba[4 * i + 2] = (byte)(200 - 90 * x / w); rgba[4 * i + 3] = 255;
        }
        areas = new List<(byte, float2x3)>();
        int holes = 7;
        for (int k = 0; k < holes; k++)
        {
            int rw = w / 12 + rng.Next(w / 20), rh = h / 4 + rng.Next(h / 10);
            int x0 = (int)((k + 0.4f) * (w - rw) / holes), y0 = h / 3 + rng.Next(h / 6);
            for (int y = y0; y < y0 + rh && y < h; y++)
                for (int x = x0; x < x0 + rw && x < w; x++)
                {
                    int i = y * w + x;
                    int edge = Math.Min(Math.Min(x - x0, x0 + rw - 1 - x), Math.Min(y - y0, y0 + rh - 1 - y));
                    rgba[4 * i + 3] = edge >= 3 ? (byte)0 : (byte)(edge * 85);
                    if (edge >= 3) { rgba[4 * i] = (byte)rng.Next(256); rgba[4 * i + 1] = (byte)rng.Next(256); rgba[4 * i + 2] = (byte)rng.Next(256); }
                }
            float2 a = new float2(x0 / (float)w, y0 / (float)h), b = new float2((x0 + rw) / (float)w, y0 / (float)h);
            float2 c = new float2((x0 + rw) / (float)w, (y0 + rh) / (float)h), d = new float2(x0 / (float)w, (y0 + rh) / (float)h);
            byte kind = k % 4 == 3 ? WrapPackage.AreaDoor : WrapPackage.AreaWindow;
            areas.Add((kind, new float2x3(a, b, c)));
            areas.Add((kind, new float2x3(a, c, d)));
        }
        return rgba;
    }

    public static int Run(string[] args)
    {
        int repeats = args.Length > 1 ? int.Parse(args[1]) : 3;
        Console.WriteLine($"## wrap picture work, {repeats} repeats, Release={!Debugger.IsAttached}");
        double total = 0;
        foreach (var (name, w, h) in kPictures)
        {
            byte[] source = Picture(w, h, 7 + w + h, out var areas);
            var times = new List<double>();
            int outlined = 0, recoloured = 0;
            for (int r = 0; r < repeats + 1; r++)
            {
                byte[] work = (byte[])source.Clone();
                long t0 = Stopwatch.GetTimestamp();
                WrapPackage.Prepare(work, w, h, areas, 25f, 10.5f, out outlined, out recoloured);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (r > 0) times.Add(ms);       // the first run warms the JIT
            }
            times.Sort();
            double median = times[times.Count / 2];
            total += median;
            Console.WriteLine($"  {name,-6} {w}x{h}: Prepare {median,7:0.0} ms (best {times[0]:0.0}), {outlined} outlined, {recoloured} recoloured");
        }
        Console.WriteLine($"  five pictures: {total:0} ms");
        // where one picture's time goes
        {
            byte[] source = Picture(2048, 785, 7 + 2048 + 785, out var areas);
            double fill = 0, outline = 0, decon = 0;
            for (int r = 0; r < repeats + 1; r++)
            {
                byte[] work = (byte[])source.Clone();
                long t0 = Stopwatch.GetTimestamp();
                var area = new byte[2048 * 785];
                foreach ((byte kind, float2x3 uv) in areas) WrapPackage.FillArea(area, 2048, 785, uv.c0, uv.c1, uv.c2, kind);
                long t1 = Stopwatch.GetTimestamp();
                WrapPackage.OutlineAreas(work, 2048, 785, area, WrapPackage.OutlineRadius(25f, 10.5f, 2048));
                long t2 = Stopwatch.GetTimestamp();
                WrapPackage.Decontaminate(work, 2048, 785);
                long t3 = Stopwatch.GetTimestamp();
                if (r == 0) continue;
                fill += (t1 - t0) * 1000.0 / Stopwatch.Frequency; outline += (t2 - t1) * 1000.0 / Stopwatch.Frequency; decon += (t3 - t2) * 1000.0 / Stopwatch.Frequency;
            }
            Console.WriteLine($"  left 2048x785 by step (mean of {repeats}): fill areas {fill / repeats:0.0} ms, outline {outline / repeats:0.0} ms, decontaminate {decon / repeats:0.0} ms");
        }
        return 0;
    }
}

// The real thing: the model's own wrap areas and the wraps in the repository root, through this decoder and WrapPackage.Prepare; the counts
// are the ones the game logs ("N outlined white, M see-through pixels recoloured") so they can be set against the game's own log.
static class WrapReal
{
    public static int Run(string[] args)
    {
        string model = args.Length > 1 ? args[1] : "SWB6710EV01G";
        var profile = TestPacks.Profile(model);
        var bus = new CDLC.BusDriver.Interior.Models.BusInterior(profile.Spec);
        Console.WriteLine($"## {model}: wrap length {bus.WrapLength:0.00} m, outline {profile.WrapOutlineMm} mm");
        var faces = (CDLC.BusDriver.Interior.Models.BusInterior.WrapFace[])Enum.GetValues(typeof(CDLC.BusDriver.Interior.Models.BusInterior.WrapFace));
        var areasOf = new List<(byte kind, float2x3 uv)>[5];
        foreach (var face in faces)
        {
            int f = (int)face;
            if (f < 0 || f > 4) continue;
            long t0 = Stopwatch.GetTimestamp();
            var list = bus.HasWrap(face) ? bus.WrapAreas(face) : new List<(byte, float2x3)>();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            // the section's X: the right side runs from the other end (BusSkins.Vehicle.Areas, one section: share 0..1)
            var mapped = new List<(byte, float2x3)>();
            foreach (var (kind, uv) in list)
                mapped.Add((kind, face == CDLC.BusDriver.Interior.Models.BusInterior.WrapFace.Right
                    ? new float2x3(new float2(1f - uv.c0.x, uv.c0.y), new float2(1f - uv.c1.x, uv.c1.y), new float2(1f - uv.c2.x, uv.c2.y)) : uv));
            areasOf[f] = mapped;
            Console.WriteLine($"  WrapAreas({face}): {list.Count} triangles in {ms:0.0} ms");
        }
        foreach (string zip in System.IO.Directory.GetFiles(Repo.Root, "*.zip"))
        {
            WrapPackage.Package package;
            try { package = WrapPackage.Read(zip, false); } catch (WrapPackage.Error) { continue; }
            Console.WriteLine($"## {System.IO.Path.GetFileName(zip)} (model {package.Manifest.Model}, outline {package.Manifest.OutlineMm} mm)");
            var have = new bool[5];
            for (int f = 0; f < 5; f++) have[f] = package.Faces[f] != null;
            for (int f = 0; f < 5; f++)
            {
                if (package.Faces[f] == null) continue;
                long t0 = Stopwatch.GetTimestamp();
                if (!PngDecoder.TryDecode(package.Faces[f], out int w, out int h, out byte[] rgba)) { Console.WriteLine($"  {WrapPackage.Pictures[f]}: not decoded here"); continue; }
                double decode = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                var areas = new List<(byte, float2x3)>();
                int[] faceList = WrapPackage.FacesOf(f, have);
                foreach (int face in faceList) areas.AddRange(areasOf[face]);
                var first = (CDLC.BusDriver.Interior.Models.BusInterior.WrapFace)faceList[0];
                float metres = (first == CDLC.BusDriver.Interior.Models.BusInterior.WrapFace.Left || first == CDLC.BusDriver.Interior.Models.BusInterior.WrapFace.Right || first == CDLC.BusDriver.Interior.Models.BusInterior.WrapFace.Roof)
                    ? bus.WrapLength : bus.WrapSize(first).x;
                long t1 = Stopwatch.GetTimestamp();
                bool see = WrapPackage.Prepare(rgba, w, h, areas, package.Manifest.OutlineMm, metres, out int outlined, out int recoloured);
                double prepare = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
                Console.WriteLine($"  {WrapPackage.Pictures[f],-10} {w}x{h}: decode {decode:0.0} ms, Prepare {prepare:0.0} ms; {outlined} outlined white, {recoloured} recoloured{(see ? "" : " (nothing see-through)")}");
            }
        }
        return 0;
    }
}

// How long an authored model takes to load and to build (the game's log of 19 October: K18 front 1112 ms, SWB6109EV12G 10281 ms, on the
// prebuild thread): dotnet run -c Release -- buildbench [repeats] (the packs in Packs/ are read with the mod's own loader).
static class BuildBench
{
    public static int Run(string[] args)
    {
        int repeats = args.Length > 1 ? int.Parse(args[1]) : 2;
        foreach (string dir in System.IO.Directory.GetDirectories(System.IO.Path.Combine(Repo.Root, "Packs")))
        {
            string model = System.IO.Path.GetFileName(dir).Substring("CDLC.Bus.".Length);
            long t0 = Stopwatch.GetTimestamp();
            var profile = TestPacks.Profile(model);
            double register = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            long t1 = Stopwatch.GetTimestamp();
            _ = profile.Mesh;
            double mesh = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
            var specs = new List<CDLC.BusDriver.Interior.Models.BusInteriorSpec> { profile.Spec.Snapped() };
            Console.WriteLine($"## {model}: pack register {register:0} ms, mesh {mesh:0} ms");
            foreach (var spec in specs)
            {
                var times = new List<double>();
                long totalTriangles = 0;
                for (int r = 0; r < repeats + 1; r++)
                {
                    long t2 = Stopwatch.GetTimestamp();
                    var data = CDLC.BusDriver.Kit.KitBuild.BuildAll(new CDLC.BusDriver.Interior.Models.BusInterior(spec), new CDLC.BusDriver.Kit.MeshKit());
                    double ms = (Stopwatch.GetTimestamp() - t2) * 1000.0 / Stopwatch.Frequency;
                    if (r > 0) times.Add(ms);
                    if (r == 0) Console.WriteLine($"  section {spec.Section}: first build {ms:0} ms (JIT included), reported BuildMs {data.BuildMs:0}");
                }
                times.Sort();
                Console.WriteLine($"  section {spec.Section}: steady build median {times[times.Count / 2]:0} ms (best {times[0]:0})");
            }
        }
        return 0;
    }
}

// The runtime mesh's file: its size, and the cost of the gunzip alone against a whole load (dotnet run -c Release -- meshbench).
static class MeshBench
{
    public static int Run(string[] args)
    {
        foreach (string dir in System.IO.Directory.GetDirectories(System.IO.Path.Combine(Repo.Root, "Packs"), "CDLC.Bus.*"))
        {
            string model = System.IO.Path.GetFileName(dir).Substring("CDLC.Bus.".Length);
            var profile = TestPacks.Profile(model);
            string file = profile.MeshPath;
            if (file == null) { Console.WriteLine($"## {model}: no mesh path"); continue; }
            byte[] gz = System.IO.File.ReadAllBytes(file);
            double best = 1e9; long raw = 0;
            for (int r = 0; r < 4; r++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var ms = new System.IO.MemoryStream();
                using (var z = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(gz), System.IO.Compression.CompressionMode.Decompress)) z.CopyTo(ms);
                raw = ms.Length;
                best = Math.Min(best, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            Console.WriteLine($"## {model}: {gz.Length / 1048576.0:0.0} MB gzip, {raw / 1048576.0:0.0} MB raw, gunzip alone {best:0} ms");
            // the managed memory the loaded mesh keeps (all three LODs), and the time of the whole load
            long before = GC.GetTotalMemory(true), t3 = Stopwatch.GetTimestamp();
            var loaded = profile.Mesh;
            double loadMs = (Stopwatch.GetTimestamp() - t3) * 1000.0 / Stopwatch.Frequency;
            long kept = GC.GetTotalMemory(true) - before;
            GC.KeepAlive(loaded);
            Console.WriteLine($"   load {loadMs:0} ms, keeps {kept / 1048576.0:0.0} MB");
            // the same mesh as SWB2 (a converted copy of a one-section pack's profile and mesh): its load, or an older loader's refusal
            if (file.EndsWith(".swb1.gz", StringComparison.Ordinal) && profile.Assembly == null)
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cdlc-meshbench-" + model);
                System.IO.Directory.CreateDirectory(temp);
                try
                {
                    var two = SwbCodecTests.Swb2Copy(model, profile, temp);
                    long before2 = GC.GetTotalMemory(true), t4 = Stopwatch.GetTimestamp();
                    var loaded2 = two.Mesh;
                    double load2 = (Stopwatch.GetTimestamp() - t4) * 1000.0 / Stopwatch.Frequency;
                    long kept2 = GC.GetTotalMemory(true) - before2;
                    GC.KeepAlive(loaded2);
                    Console.WriteLine($"   as SWB2 (test copy, fastest gzip) {new System.IO.FileInfo(two.MeshPath).Length / 1048576.0:0.0} MB: load {load2:0} ms, keeps {kept2 / 1048576.0:0.0} MB");
                }
                catch (System.IO.InvalidDataException e) { Console.WriteLine("   as SWB2: refused (" + e.Message + ")"); }
                finally { System.IO.Directory.Delete(temp, true); }
            }
            // a hash of every triangle read (swatch, three positions, three normals, as bits): the same before and after a change of the loader
            // (0.14.0 keeps indexed parts, m_Parts, where 0.13 kept Triangle arrays, m_Triangles: either is walked the same way)
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var type = typeof(CDLC.BusDriver.Interior.Models.VehicleMesh);
                var triangles = (System.Collections.IEnumerable)(type.GetField("m_Parts", flags) ?? type.GetField("m_Triangles", flags)).GetValue(profile.Mesh);
                ulong hash = 14695981039346656037UL; long count = 0;
                foreach (System.Collections.IEnumerable lod in triangles)
                    foreach (System.Collections.IEnumerable part in lod)
                        foreach (object t in part)
                        {
                            count++;
                            foreach (var f in t.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                                hash = MeshHash.Mix(hash, f.GetValue(t));
                        }
                Console.WriteLine($"   mesh hash {hash:x16}, {count} triangles");
            }
        }
        return 0;
    }
}

static class MeshHash
{
    static readonly Dictionary<Type, System.Reflection.FieldInfo[]> s_Fields = new Dictionary<Type, System.Reflection.FieldInfo[]>();
    internal static ulong Mix(ulong h, object v)
    {
        switch (v)
        {
            case float f: return Add(h, (ulong)(uint)BitConverter.SingleToInt32Bits(f));
            case int i: return Add(h, (ulong)(uint)i);
            case byte b: return Add(h, b);
            case Enum e: return Add(h, Convert.ToUInt64(e));
        }
        Type type = v.GetType();
        if (!s_Fields.TryGetValue(type, out var fields))
            s_Fields[type] = fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        foreach (var field in fields) h = Mix(h, field.GetValue(v));
        return h;
    }
    static ulong Add(ulong h, ulong x) { h ^= x; h *= 1099511628211UL; return h; }
}

static class ColumnsBench
{
    public static int Run(string[] args)
    {
        byte[] source = WrapBench.Picture(2048, 785, 3, out _);
        foreach (bool mirrored in new[] { false, true })
        {
            double best = 1e9;
            for (int r = 0; r < 8; r++)
            {
                long t0 = Stopwatch.GetTimestamp();
                WrapPackage.Columns(source, 2048, 785, 0, 2048, mirrored);
                best = Math.Min(best, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            Console.WriteLine($"Columns 2048x785 mirrored={mirrored}: {best:0.0} ms");
        }
        return 0;
    }
}

// What the wraps manager's list costs (dotnet run -c Release -- listbench <folder>): WrapPackage.Read of every zip, as BusSkins.Describe
// does on the main thread the first time the list is asked for.
static class ListBench
{
    public static int Run(string[] args)
    {
        string folder = args[1];
        double total = 0, headers = 0;
        int n = 0;
        foreach (string zip in System.IO.Directory.GetFiles(folder, "*.zip"))
        {
            long t0 = Stopwatch.GetTimestamp();
            try { WrapPackage.Read(zip, System.IO.Path.GetFileNameWithoutExtension(zip) == "template"); } catch (WrapPackage.Error) { }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            total += ms; n++;
            long t1 = Stopwatch.GetTimestamp();
            using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                foreach (var e in z.Entries) if (e.Name == "wrap.json") using (var s = e.Open()) { var b = new byte[e.Length]; s.Read(b, 0, b.Length); }
            headers += (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"  {System.IO.Path.GetFileName(zip),-48} Read {ms,7:0.0} ms");
        }
        Console.WriteLine($"{n} zips: WrapPackage.Read {total:0} ms in all; the manifest alone {headers:0.0} ms in all");
        return 0;
    }
}
