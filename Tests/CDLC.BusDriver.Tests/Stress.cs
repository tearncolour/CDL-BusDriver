using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// Robustness and speed checks (dotnet run -c Release -- stress [count] [seed]): random bus shapes built by the kit (every part, every
// level of detail, the body wrap's layout, the riders' slots), random and hostile strings through the text code (line names,
// announcements, the LED composer), and timings of the heavy builds. Prints a report block (also written to out/stress.txt).
static class Stress
{
    static readonly StringBuilder s_Out = new StringBuilder();
    static int s_Fails, s_Checks;

    static void Line(string text) { Console.WriteLine(text); s_Out.AppendLine(text); }
    static void Check(bool ok, string what)
    {
        s_Checks++;
        if (!ok) { s_Fails++; Line("  FAIL " + what); }
    }

    public static int Run(string[] args)
    {
        int count = args.Length > 1 ? int.Parse(args[1]) : 200;
        int seed = args.Length > 2 ? int.Parse(args[2]) : 20261014;
        var rng = new System.Random(seed);
        Line($"## stress: {count} random bus shapes, seed {seed}");
        Shapes(rng, count);
        Strings(rng, count * 20);
        Timings();
        Line($"## {s_Checks} checks, {s_Fails} failed");
        Directory.CreateDirectory("out");
        File.WriteAllText(Path.Combine("out", "stress.txt"), s_Out.ToString());
        return s_Fails == 0 ? 0 : 1;
    }

    static float R(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

    // A random but plausible bus (and some implausible ones: the spec is snapped and clamped, so every input must still build).
    static BusInteriorSpec Random(System.Random rng, bool wild)
    {
        float front = R(rng, 3.0f, 8.0f), rear = -R(rng, 3.0f, 12.0f);
        var s = new BusInteriorSpec
        {
            FrontZ = front, RearZ = rear,
            HalfWidth = wild ? R(rng, 0.5f, 2.0f) : R(rng, 1.0f, 1.35f),
            RoofY = wild ? R(rng, 1.5f, 4.5f) : R(rng, 2.5f, 3.6f),
            FloorY = wild ? R(rng, 0.0f, 1.5f) : R(rng, 0.25f, 0.9f),
            FrontAxleZ = front - R(rng, wild ? 0.2f : 1.6f, 3.5f), RearAxleZ = rear + R(rng, wild ? 0.2f : 1.2f, 4.5f),
            Doors = wild ? rng.Next(-1, 6) : rng.Next(1, 4),
            DriverSide = rng.Next(2) == 0 ? -1f : 1f,
            Section = (BusSection)rng.Next(3),
        };
        return s;
    }

    static void Shapes(System.Random rng, int count)
    {
        int built = 0, errors = 0, wild = 0, triMin = int.MaxValue, triMax = 0, parts = 0, overBudget = 0, wrapBad = 0, slotBad = 0;
        double msTotal = 0, msWorst = 0;
        var long_ = new List<string>();
        for (int i = 0; i < count; i++)
        {
            bool isWild = i % 5 == 4;
            if (isWild) wild++;
            BusInteriorSpec spec = Random(rng, isWild);
            BusInterior src;
            KitModelData data;
            var sw = Stopwatch.StartNew();
            try
            {
                src = new BusInterior(spec);
                data = KitBuild.BuildAll(src);
            }
            catch (Exception e)
            {
                errors++;
                Check(false, $"shape {i} threw {e.GetType().Name}: {e.Message} (spec {Describe(spec)})");
                continue;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            msTotal += ms;
            msWorst = Math.Max(msWorst, ms);
            if (data.Error != null)
            {
                errors++;
                Check(false, $"shape {i}: build error {data.Error} (spec {Describe(spec)})");
                continue;
            }
            built++;
            parts += data.Parts.Length;
            int tris = data.Tris[0];
            triMin = Math.Min(triMin, tris);
            triMax = Math.Max(triMax, tris);
            // sanity: finite geometry, indices in range, no NaN in any part of any level
            bool finite = true, indices = true;
            for (int p = 0; p < data.Parts.Length; p++)
                for (int l = 0; l <= MeshKit.MaxLod; l++)
                {
                    var d = data.Parts[p].Data[l];
                    if (d == null) continue;
                    foreach (var v in d.P) finite &= math.all(math.isfinite(v));
                    foreach (var v in d.N) finite &= math.all(math.isfinite(v));
                    foreach (var v in d.UV) finite &= math.all(math.isfinite(v));
                    foreach (int idx in d.Idx) indices &= idx >= 0 && idx < d.P.Length;
                }
            Check(finite, $"shape {i}: every vertex, normal and UV finite ({Describe(spec)})");
            Check(indices, $"shape {i}: every index inside its vertex array ({Describe(spec)})");
            if (data.Tris[0] > 300000) overBudget++;
            // the wrap: layouts inside the picture, sizes positive
            foreach (BusInterior.WrapFace face in Enum.GetValues(typeof(BusInterior.WrapFace)))
            {
                var layout = src.WrapLayout(face);
                bool ok = true;
                foreach (var (uv, solid) in layout)
                    ok &= math.all(math.isfinite(uv)) && (!solid || (uv.z > uv.x && uv.w > uv.y));
                var size = src.WrapSize(face);
                ok &= !src.HasWrap(face) || (size.x > 0f && size.y > 0f && math.all(math.isfinite(size)));
                if (!ok) { wrapBad++; Check(false, $"shape {i}: wrap {face} layout ({Describe(spec)})"); }
            }
            // the riders' places: finite, inside the body's length, at least one seat on a solo bus
            var slots = BusInterior.RiderSlotsOf(src.Spec);
            bool slotsOk = slots.All(s => math.all(math.isfinite(s.Position)) && s.Position.z <= src.Spec.FrontZ + 0.5f && s.Position.z >= src.Spec.RearZ - 0.5f);
            if (!slotsOk) { slotBad++; Check(false, $"shape {i}: rider slots inside the body ({Describe(spec)})"); }
            if (ms > 4000) long_.Add($"{ms:0} ms for {Describe(spec)}");
        }
        Line($"shapes: {built} built, {errors} failed ({wild} of them wild: sizes outside the design range, 0..5 doors, odd axles)");
        Line($"  build time: mean {msTotal / Math.Max(1, built):0} ms, worst {msWorst:0} ms; triangles at LOD0: {triMin} .. {triMax}; parts per bus {parts / Math.Max(1, built)}");
        Line($"  over the 300k-triangle budget: {overBudget}; wrap layouts wrong: {wrapBad}; rider slots wrong: {slotBad}");
        foreach (string l in long_.Take(5)) Line("  slow: " + l);
    }

    static string Describe(BusInteriorSpec s) =>
        $"front {s.FrontZ:0.0} rear {s.RearZ:0.0} half-width {s.HalfWidth:0.00} roof {s.RoofY:0.00} floor {s.FloorY:0.00} axles {s.FrontAxleZ:0.0}/{s.RearAxleZ:0.0} doors {s.Doors} side {s.DriverSide:0} {s.Section}";

    // Hostile and odd strings through the text code: nothing throws, results are bounded.
    static void Strings(System.Random rng, int count)
    {
        var odd = new List<string>
        {
            null, "", " ", "\0", "\t\n", "0", "-1", "007", "K", "K3", "BRT12", "夜12路", "N-12", "12A", "A12B34", "〇一二", "😀3", "😀", "K\uD800", "￿",
            new string('9', 5000), new string('夜', 3000), "%s%d{0}{1}", "<b>bold</b>", "\"quoted\"", "line\r\nbreak", "K 3 路 ", "ＫＢ１２", "Ⅻ", "٣٤", "K​3",
        };
        for (int i = 0; i < count; i++)
        {
            var chars = new char[rng.Next(0, 24)];
            for (int c = 0; c < chars.Length; c++) chars[c] = (char)rng.Next(0, rng.Next(2) == 0 ? 128 : 0x9FFF);
            odd.Add(new string(chars));
        }

        int parsed = 0, thrown = 0;
        Func<string, string> text = LangPack.Text("en-US");
        foreach (string s in odd)
        {
            try
            {
                LineLabel.Parse(s, rng.Next(-5, 100000), out string prefix, out string number);
                parsed++;
                Check(prefix != null && number != null, "LineLabel.Parse never returns null parts");
                Check((prefix ?? "").Length <= (s ?? "").Length + 8 && (number ?? "").Length <= Math.Max(8, (s ?? "").Length + 8), "LineLabel.Parse output is bounded by its input");
                float3 c = LineLabel.ColorFor(prefix, s);
                Check(math.all(math.isfinite(c)) && math.all(c >= 0f) && math.all(c <= 1f), "LineLabel.ColorFor stays a colour for any option text");
                LineLabel.Hex(c);
                foreach (AnnounceKind kind in Enum.GetValues(typeof(AnnounceKind)))
                {
                    string said = TtsText.Sentence(kind, s, s, true, text, s);
                    Check(said != null, "TtsText.Sentence never returns null");
                }
            }
            catch (Exception e)
            {
                thrown++;
                Check(false, $"text code threw {e.GetType().Name} on \"{Escape(s)}\": {e.Message}");
            }
        }
        // the LED composer on the same strings: never writes outside its buffer, never throws
        string fontPath = FontPath();
        if (fontPath != null)
        {
            var font = new LedFont();
            Check(font.Load(fontPath), "the LED font loads");
            var composer = new LedComposer(font, 160, 16);
            int signs = 0;
            foreach (string s in odd)
            {
                try
                {
                    composer.Clear();
                    composer.RenderSign(s ?? "", (s ?? "") + "·" + (s ?? ""), rng.Next(0, 10000));
                    composer.RenderSign("", s ?? "", rng.Next(0, 10000));
                    composer.MeasureWidth(s ?? "");
                    signs++;
                }
                catch (Exception e)
                {
                    thrown++;
                    Check(false, $"LED composer threw {e.GetType().Name} on \"{Escape(s)}\": {e.Message}");
                }
            }
            Line($"strings: {odd.Count} inputs, {parsed} through the line label and the announcements, {signs} through the LED composer, {thrown} exceptions");
        }
        else
            Line($"strings: {odd.Count} inputs, {parsed} through the line label and the announcements, {thrown} exceptions (LED font not found: composer skipped)");
    }

    static string Escape(string s) => s == null ? "null" : s.Length > 40 ? s.Substring(0, 40).Replace("\0", "\\0") + "…" : s.Replace("\0", "\\0");

    static string FontPath()
    {
        for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            string p = Path.Combine(dir, "Fonts", "led-font.bin.gz");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // Timings of the heavy builds a driver pays at the first frame of a shift (the mod builds on a worker thread, so they cost
    // load time, not frame time) and of the per-frame text code.
    static void Timings()
    {
        Line("timings:");
        foreach (string name in new[] { "default 12 m", "midi", "long 18 m", "front (articulated)" })
        {
            BusInteriorSpec spec = BusInteriorSpec.Default;
            if (name == "midi") { spec.FrontZ = 4.6f; spec.RearZ = -4.6f; spec.FrontAxleZ = 2.2f; spec.RearAxleZ = -2.0f; spec.Doors = 2; }
            if (name == "long 18 m") { spec.FrontZ = 6.8f; spec.RearZ = -11.2f; spec.FrontAxleZ = 3.3f; spec.RearAxleZ = -8.7f; spec.RoofY = 3.3f; }
            if (name == "front (articulated)") { spec.FrontZ = 5.2f; spec.RearZ = -5.0f; spec.FrontAxleZ = 2.6f; spec.RearAxleZ = -2.4f; spec.Doors = 2; spec.Section = BusSection.Front; }
            var times = new List<double>();
            KitModelData data = null;
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                data = KitBuild.BuildAll(new BusInterior(spec));
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Line($"  bus model {name}: median {times[2]:0} ms (best {times[0]:0}, worst {times[4]:0}); triangles {data.Tris[0]} / {data.Tris[1]} / {data.Tris[2]} over LOD0 / 1 / 2; renderers {data.Renderers[0]}");
            Check(times[2] < 5000, $"bus model {name} builds in under 5 s ({times[2]:0} ms)");
        }

        {
            var times = new List<double>();
            KitModelData data = null;
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                data = KitBuild.BuildAll(new BusRiders(), new MeshKit());
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            int max = 0, min = int.MaxValue;
            foreach (var part in data.Parts) { max = Math.Max(max, part.Data[0].Tris); min = Math.Min(min, part.Data[0].Tris); }
            Line($"  simple riders ({data.Parts.Length} figures): median {times[2]:0} ms; triangles per figure {min} .. {max} at LOD0, total {data.Tris[0]} / {data.Tris[1]} / {data.Tris[2]}");
            Check(times[2] < 8000, $"the riders build in under 8 s ({times[2]:0} ms)");
        }

        // the per-frame text code
        var rng = new System.Random(1);
        Func<string, string> text = LangPack.Text("en-US");
        int n = 200000;
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            LineLabel.Parse("夜12路", 12, out string p, out string num);
            LineLabel.ColorFor(p, null);
        }
        double parseNs = timer.Elapsed.TotalMilliseconds * 1e6 / n;
        timer.Restart();
        for (int i = 0; i < n / 10; i++)
            TtsText.Sentence(AnnounceKind.Next, "人民广场", "码头路", true, text, "K3");
        double sentenceNs = timer.Elapsed.TotalMilliseconds * 1e6 / (n / 10);
        Line($"  per call: LineLabel.Parse + ColorFor {parseNs:0} ns; an announcement sentence {sentenceNs:0} ns");
        Check(parseNs < 20000 && sentenceNs < 200000, "the text code is cheap enough to call every few frames");

        string fontPath = FontPath();
        if (fontPath != null)
        {
            var font = new LedFont();
            font.Load(fontPath);
            var composer = new LedComposer(font, 160, 16);
            timer.Restart();
            int signs = 2000;
            for (int i = 0; i < signs; i++)
                composer.RenderSign("K3", "开往人民广场南门方向", i);
            double signUs = timer.Elapsed.TotalMilliseconds * 1000.0 / signs;
            Line($"  per call: one LED sign frame (scrolling destination) {signUs:0.0} us");
            Check(signUs < 2000, "an LED sign frame is cheap (under 2 ms)");
        }
    }
}
