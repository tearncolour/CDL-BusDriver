using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior;
using Unity.Mathematics;
using LedGeometry = CDLC.Asset.Led.LedGeometry;

// The high-density LED signs (the user, 21 October, with a photo of a Shanghai bus sign): the dot faces and the readings load, a stop
// name gets its English line as that sign writes it, a sign is laid out as the photo (label, chevron, Chinese above English, each in
// its ink, nothing outside its cell), a narrow rear sign falls back to the medium face, a warning takes one big line and an arrow
// when there is room, the scroll runs, the rear sign's messages follow the brake and the indicators, the mesh holds exactly the lit
// dots in their inks. "hdpreview <dir>" writes the signs of the packs as PNG.
static class HdSignTests
{
    const string P = "hd signs: ";

    internal static string FontPath(string name)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "Fonts", name);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    internal static bool Load(out DotFont font, out Pinyin pinyin, out LedFont led)
    {
        font = new DotFont();
        pinyin = new Pinyin();
        led = new LedFont();
        string f = FontPath("led-hd-font.bin.gz"), p = FontPath("pinyin.bin.gz"), l = LedFontTests.FontPath();
        bool ok = f != null && font.Load(f) && p != null && pinyin.Load(p);
        if (l == null || !led.Load(l))
            led = null;
        return ok;
    }

    // The signs of the packs (their profiles' sizes, metres) and the built-in bus's generic rear, for the line names.
    static readonly (string name, float w, float h)[] s_PackSigns =
    {
        ("6710 front", 1.5f, 0.2f), ("6710 side", 0.83f, 0.158f), ("6710 rear", 1.16f, 0.178f),
        ("6109 front", 1.68f, 0.24f), ("6109 side", 0.96f, 0.192f), ("6109 rear", 1.54f, 0.22f),
        ("K18 front", 1.72f, 0.287f), ("K18 side", 1f, 0.2f), ("K18 rear side", 1.2f, 0.2f), ("K18 rear", 1.28f, 0.213f),
        ("6115 front", 1.3f, 0.2f), ("6115 side", 0.85f, 0.2f), ("6115 rear", 1.14f, 0.19f), ("generic rear", 0.42f, 0.16f),
    };

    /// <summary>Dots lit in the sign's outer columns: the first, and the last unless a line scrolls (it comes in over the right edge).</summary>
    static bool Edges(HdComposer c)
    {
        for (int y = 0; y < c.Height; y++)
            if (c.Buffer[0, y] || !c.Scrolls && c.Buffer[c.Width - 1, y])
                return true;
        return false;
    }

    // A line named in Chinese ("高港专线") as the label: no 路 after it (LineLabel.HasNumber), beside the stop on every pack's front and
    // side sign but the narrowest, alone on the rear; a name that leaves the stop less than a third of the sign is shown alone, and one
    // that does not fit even alone scrolls.
    static void NamedLines(Action<bool, string> check, DotFont font, LedFont led, Pinyin pinyin)
    {
        NamedLine(check, font, led, pinyin, "高港专线");
        NamedLine(check, font, led, pinyin, "高港2号专线");
        Narrow(check, font, led, pinyin);
        check(LineLabel.IsRouteNumber("12") && LineLabel.IsRouteNumber("3A") && !LineLabel.IsRouteNumber("高港2号专线") && !LineLabel.IsRouteNumber("3线"),
            P + "路 only after a route number");
    }

    static void NamedLine(Action<bool, string> check, DotFont font, LedFont led, Pinyin pinyin, string name)
    {
        const string stop = "泰州火车站";
        string english = StopEnglish.Of(stop, pinyin);
        foreach (var (sign, w, h) in s_PackSigns)
        {
            int width = LedGeometry.HdMatrix(new float2(w, h)).x;
            var c = new HdComposer(font, led, width);
            c.Compose(HdContent.Route("", name, "", stop, english));
            c.Render(0);
            bool beside = c.HasChevron && width - c.TextStart - HdComposer.Margin >= width / 3;
            bool alone = !c.HasChevron && (c.Scrolls ? c.LabelWidth == 0 : c.LabelWidth > 0 && c.LabelWidth <= width - 2 * HdComposer.Margin);
            // "高港专线": the 136-column side sign of the SWB6115 and the generic rear leave the stop too little room, the name alone;
            // a longer name may go alone on more signs, never off the sign
            bool narrowSign = sign == "6115 side" || sign == "generic rear";
            check((name != "高港专线" ? beside || alone : narrowSign ? alone : beside) && !Edges(c),
                P + $"{sign} ({width} columns): \"{name}\" {(beside ? "beside the stop" : "alone")}, {c.LabelWidth} dots{(c.LabelMedium ? " (medium)" : "")}, the stop from column {c.TextStart}, chevron {c.HasChevron}");
            c.Compose(HdContent.Label("", name, ""));
            c.Render(0);
            check(!Edges(c) && (c.Scrolls ? c.LabelWidth == 0 : c.LabelWidth > 0 && c.LabelWidth <= width - 2 * HdComposer.Margin),
                P + $"{sign}: \"{name}\" alone {(c.Scrolls ? "scrolls" : "fits, centred")} ({c.LabelWidth} of {width} dots{(c.LabelMedium ? ", medium" : "")})");
        }
    }

    static void Narrow(Action<bool, string> check, DotFont font, LedFont led, Pinyin pinyin)
    {
        const string stop = "泰州火车站";
        string english = StopEnglish.Of(stop, pinyin);
        int side = LedGeometry.HdMatrix(new float2(0.85f, 0.2f)).x;
        var narrow = new HdComposer(font, led, side);
        narrow.Compose(HdContent.Route("", "高港泰州专线", "", stop, english));
        narrow.Render(0);
        check(!narrow.HasChevron && !narrow.Scrolls && narrow.LabelWidth > side / 2 && !Edges(narrow),
            P + $"a six-character name on the {side}-column side sign leaves the stop no room: the name alone ({narrow.LabelWidth} dots)");

        int rear = LedGeometry.HdMatrix(new float2(0.42f, 0.16f)).x;
        var tiny = new HdComposer(font, led, rear);
        tiny.Compose(HdContent.Label("", "一二三四五六七八", ""));
        tiny.Render(0);
        bool[,] standing = (bool[,])tiny.Buffer.Clone();
        tiny.Render(HdComposer.HoldDots + 40);
        bool moved = false;
        for (int x = 0; x < rear && !moved; x++)
            for (int y = 0; y < HdComposer.Rows && !moved; y++)
                moved = standing[x, y] != tiny.Buffer[x, y];
        check(tiny.Scrolls && tiny.LabelWidth == 0 && tiny.Cycle > HdComposer.HoldDots && moved,
            P + $"an eight-character name on the {rear}-column rear scrolls as one medium line (cycle {tiny.Cycle})");
    }

    internal static void Run(Action<bool, string> check)
    {
        bool loaded = Load(out DotFont font, out Pinyin pinyin, out LedFont led);
        check(loaded, P + "Fonts/led-hd-font.bin.gz and Fonts/pinyin.bin.gz load");
        if (!loaded)
            return;
        NamedLines(check, font, led, pinyin);
        check(font.FaceCount == 4 && font[DotFont.Big].Rows == 32 && font[DotFont.Small].Rows == 13 && font[DotFont.Medium].Rows == 19 && font[DotFont.Label].Rows == 32,
            P + $"four faces: 32, 13, 19 and 32 rows ({font.Count} glyphs)");
        bool cjk = true;
        foreach (char c in "滨江路刹车注意辆左右转暂停营运龙华中东安夜")
            cjk &= font[DotFont.Big].Has(c) && font[DotFont.Medium].Has(c);
        check(cjk && font[DotFont.Small].Has('M') && font[DotFont.Small].Has('&') && font[DotFont.Label].Has('7') && font[DotFont.Label].Has('K') && !font[DotFont.Label].Has('路'),
            P + "the CJK faces have the signs' characters; the small face the English; the label face digits and capitals only");
        check(pinyin.Count > 20000 && pinyin.Of('龙') == "long" && pinyin.Of('绿') == "lü", P + $"readings of {pinyin.Count} characters (龙 long, 绿 lü)");

        // the English line
        var english = new (string zh, string en)[]
        {
            ("龙华中路", "Middle Long Hua Road"), ("南京东路", "East Nan Jing Road"), ("上海南站", "Shang Hai South Station"),
            ("龙华中路东安路", "Middle Long Hua Road & Dong An Road"), ("莘庄", "Xin Zhuang"), ("闵行", "Min Hang"),
            ("龙华中路站", "Middle Long Hua Road Station"), ("人民广场", "Ren Min Plaza"), ("虹桥火车站", "Hong Qiao Railway Station"),
            ("世纪大道", "Shi Ji Avenue"), ("龙华中路公交站", "Middle Long Hua Road Bus Stop"), ("中山路", "Zhong Shan Road"),
            ("Harbour Station", ""), ("さくら駅", ""), ("滨江2路", "Bin Jiang 2 Road"),
        };
        foreach (var (zh, en) in english)
        {
            string got = StopEnglish.Of(zh, pinyin);
            check(got == en, P + $"English of {zh}: \"{got}\" (want \"{en}\")");
        }

        // the matrix: square dots, 32 rows
        int2 m6710 = LedGeometry.HdMatrix(new float2(1.5f, 0.2f)), mGeneric = LedGeometry.HdMatrix(new float2(0.42f, 0.16f));
        check(m6710.Equals(new int2(240, 32)) && mGeneric.Equals(new int2(84, 32)) && LedGeometry.HdMatrix(new float2(0f, 0.1f)).y == 32,
            P + $"grids of square dots: SWB6710 front {m6710.x}x{m6710.y}, the generic rear {mGeneric.x}x{mGeneric.y}");

        // the Shanghai layout: label, chevron, Chinese above, English under
        var front = new HdComposer(font, led, 240);
        front.Compose(HdContent.Route("", "52", "路", "龙华中路", StopEnglish.Of("龙华中路", pinyin)));
        front.Render(0);
        check(front.HasChevron && front.TwoLines && !front.Scrolls && !front.LabelMedium, P + "52路 › 龙华中路 / Middle Long Hua Road: label, chevron, two lines, nothing scrolls on 240 columns");
        int areaStart = front.TextStart;
        Inks(front, 0, areaStart, out int[] labelInks, out _, out _);
        Inks(front, areaStart, front.Width, out _, out int[] top, out int[] bottom);
        check(labelInks[HdInk.Sign] > 200 && labelInks[HdInk.English] == 0 && top[HdInk.Sign] > 100 && top[HdInk.English] == 0 && bottom[HdInk.English] > 50 && bottom[HdInk.Sign] == 0,
            P + $"inks: the label and the Chinese line amber ({labelInks[HdInk.Sign]}, {top[HdInk.Sign]} dots), the English line cyan ({bottom[HdInk.English]} dots), in their own rows");
        check(Lit(front, areaStart, front.Width, 0, 18) && Lit(front, areaStart, front.Width, 19, 31), P + "the Chinese line in rows 0..18, the English one in 19..31");
        int digits = LabelRows(front, HdComposer.Margin, HdComposer.Margin + front.Measure("52", DotFont.Label));
        check(digits >= 26, P + $"the route number is as tall as the sign ({digits} of 32 rows)");

        var prefixed = new HdComposer(font, led, 240);
        prefixed.Compose(HdContent.Route("K", "12", "路", "上海南站", StopEnglish.Of("上海南站", pinyin)));
        prefixed.Render(0);
        Inks(prefixed, 0, prefixed.Width, out int[] all, out _, out _);
        check(all[HdInk.Prefix] > 50 && all[HdInk.Sign] > 200, P + $"a prefix in its own ink (its own colour): K {all[HdInk.Prefix]} dots");
        var night = new HdComposer(font, led, 240);
        night.Compose(HdContent.Route("夜", "3", "路", "上海南站", ""));
        night.Render(0);
        Inks(night, 0, night.LabelWidth + HdComposer.Margin, out int[] nightInks, out _, out _);
        check(nightInks[HdInk.Prefix] > 100 && !night.TwoLines && night.FirstFace == DotFont.Big, P + "a Chinese prefix (夜) in its ink too; a text without English in one big line");

        // a long stop scrolls, both lines starting again together
        var longStop = new HdComposer(font, led, 240);
        longStop.Compose(HdContent.Route("", "滨江2", "路", "龙华中路东安路", StopEnglish.Of("龙华中路东安路", pinyin)));
        areaStart = longStop.TextStart;
        bool scrolls = longStop.Render(0);
        // standing at the left of the area at first (and through the hold), moved on after it
        bool startsLeft = Lit(longStop, areaStart, areaStart + 4, 0, 18) && Lit(longStop, areaStart, areaStart + 6, 19, 31);
        bool held = Shifted(longStop, 0, HdComposer.HoldDots, 0, areaStart);
        bool moves = Shifted(longStop, HdComposer.HoldDots, HdComposer.HoldDots + 40, 40, areaStart);
        // after the longest has gone out on the left, both come in again from the right edge together
        longStop.Render(longStop.Cycle - (longStop.Width - areaStart) + 3);
        bool entering = !Lit(longStop, areaStart, longStop.Width - 4, 0, 31) && Lit(longStop, longStop.Width - 4, longStop.Width, 0, 18) &&
            Lit(longStop, longStop.Width - 4, longStop.Width, 19, 31);
        longStop.Render(HdComposer.HoldDots + 10);
        bool drawnLater = Lit(longStop, areaStart, longStop.Width, 0, 18) && Lit(longStop, areaStart, longStop.Width, 19, 31);
        bool clipped = true;
        for (int o = 0; o < longStop.Cycle; o += 7)
        {
            longStop.Render(o);
            clipped &= !LitInk(longStop, 0, areaStart, HdInk.English);
        }

        check(scrolls && longStop.FirstScrolls && longStop.SecondScrolls && drawnLater && clipped && longStop.Cycle > longStop.Width,
            P + $"龙华中路东安路 and its English scroll in the area (cycle {longStop.Cycle} dots), never over the label");
        check(startsLeft && held && moves && entering, P + $"a scrolling text stands at the left of its area {HdComposer.HoldDots} dots, moves left, comes in again from the right");

        // the rear sign: the label alone, the medium face on a narrow sign
        var rearWide = new HdComposer(font, led, 208);
        rearWide.Compose(HdContent.Label("", "滨江2", "路"));
        var rearNarrow = new HdComposer(font, led, 84);
        rearNarrow.Compose(HdContent.Label("", "滨江2", "路"));
        rearNarrow.Render(0);
        check(!rearWide.LabelMedium && rearNarrow.LabelMedium && rearNarrow.LabelWidth <= 80 && !Lit(rearNarrow, 0, 84, 0, 5) && !Lit(rearNarrow, 0, 84, 26, 31),
            P + $"rear label: big on 208 columns, the medium face centred on 84 ({rearNarrow.LabelWidth} dots wide)");

        // warnings: one big line and its arrow on a wide sign, two lines on a narrow one
        var brake = new HdComposer(font, led, 208);
        brake.Compose(HdContent.Warning("刹车注意", "Braking", HdInk.Warning, 0));
        brake.Render(0);
        Inks(brake, 0, 208, out int[] brakeInks, out _, out _);
        check(!brake.TwoLines && brake.FirstFace == DotFont.Big && brakeInks[HdInk.Warning] > 300 && brakeInks[HdInk.Sign] == 0,
            P + "刹车注意 on a 208-column rear sign: one big red line");
        var left = new HdComposer(font, led, 208);
        left.Compose(HdContent.Warning("车辆左转", "Turning Left", HdInk.Sign, -1));
        left.Render(0);
        // the arrow does not reach rows 0..3: the leftmost dot there is the text's
        int textLeft = FirstColumn(left, 0, 3), tip = FirstColumn(left, 16, 16);
        check(left.HasArrow && !left.TwoLines && tip >= 0 && tip + HdComposer.ArrowWidth <= textLeft, P + $"车辆左转 with its arrow on the left (tip {tip}, text from {textLeft})");
        var right = new HdComposer(font, led, 208);
        right.Compose(HdContent.Warning("车辆右转", "Turning Right", HdInk.Sign, 1));
        right.Render(0);
        int textRight = LastColumn(right, 0, 3), tipRight = LastColumn(right, 16, 16);
        check(right.HasArrow && tipRight - HdComposer.ArrowWidth >= textRight, P + $"车辆右转 with its arrow on the right (text to {textRight}, tip {tipRight})");
        var narrowBrake = new HdComposer(font, led, 84);
        narrowBrake.Compose(HdContent.Warning("刹车注意", "Braking", HdInk.Warning, 0));
        narrowBrake.Render(0);
        check(narrowBrake.TwoLines && !narrowBrake.Scrolls, P + "刹车注意 / Braking on 84 columns: two lines, no scroll");
        var english1 = new HdComposer(font, led, 208);
        english1.Compose(HdContent.Warning("Braking", "", HdInk.Warning, 0));
        english1.Render(0);
        // on the CJK baseline the k of Braking would start on row 2 and its g be cut; on the Latin one the k starts on row 0
        check(!english1.TwoLines && !english1.Scrolls && Lit(english1, 0, 208, 0, 0), P + "an English warning: one big line on the Latin baseline");
        var notInService = new HdComposer(font, led, 240);
        notInService.Compose(HdContent.Route("", "", "", "暂停营运", "Not in Service"));
        check(!notInService.HasChevron && notInService.TwoLines && !notInService.Scrolls, P + "暂停营运 / Not in Service: no label, two lines centred");

        // a glyph neither face has comes from Unifont (doubled in the big face)
        var fallback = new HdComposer(font, led, 120);
        fallback.Compose(HdContent.Route("", "", "", "乄", ""));
        fallback.Render(0);
        check(font[DotFont.Big].Has(0x4E44) || Lit(fallback, 0, 120, 0, 31), P + "a character outside GB 2312 is drawn from the fallback font");

        // the rear sign's messages
        var st = new RearSignState();
        RearMessage m0 = RearSignRules.Next(ref st, false, 0, 10f, 0.0);
        RearMessage m1 = RearSignRules.Next(ref st, true, 0, 10f, 1.0);
        RearMessage m2 = RearSignRules.Next(ref st, false, 0, 8f, 1.2);
        RearMessage m3 = RearSignRules.Next(ref st, false, 0, 8f, 2.6);
        check(m0 == RearMessage.Route && m1 == RearMessage.Braking && m2 == RearMessage.Braking && m3 == RearMessage.Route,
            P + "braking moving: \"刹车注意\" at once, held 1.5 s after the brake is let go");
        st = new RearSignState();
        RearMessage s0 = RearSignRules.Next(ref st, true, 0, 0f, 10.0);
        check(s0 == RearMessage.Route, P + "standing on the brake: the route");
        st = new RearSignState();
        RearMessage t0 = RearSignRules.Next(ref st, true, -1, 10f, 0.0);
        RearMessage t1 = RearSignRules.Next(ref st, true, 0, 10f, 0.3);
        RearMessage t2 = RearSignRules.Next(ref st, true, 0, 10f, 1.1);
        RearMessage t3 = RearSignRules.Next(ref st, false, 1, 10f, 1.2);
        check(t0 == RearMessage.TurnLeft && t1 == RearMessage.TurnLeft && t2 == RearMessage.Braking && t3 == RearMessage.TurnRight,
            P + "a turn before braking; a message stays 1 s before a lower one; a turn comes at once");

        // the mesh in inks
        var corners = new List<float3>();
        var tris = new[] { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
        int quads = LedGeometry.LitQuadsByInk(prefixed.Buffer, prefixed.Ink, prefixed.Width, 32, new float2(1.5f, 0.2f), corners, tris);
        int lit = 0;
        foreach (bool b in prefixed.Buffer) if (b) lit++;
        check(quads == lit && corners.Count == 4 * lit && tris[HdInk.Prefix].Count == 6 * all[HdInk.Prefix] && tris[HdInk.English].Count == 6 * all[HdInk.English],
            P + $"the mesh: one quad a lit dot ({quads}), each in its ink's submesh");
        float pitch = 1.5f / 240f;
        check(math.abs(corners[1].x - corners[0].x - LedGeometry.DotFill * pitch) < 1e-5f && math.abs(corners[2].y - corners[1].y - LedGeometry.DotFill * 0.2f / 32f) < 1e-5f,
            P + $"square dots, {pitch * 1000f:0.0} mm pitch on the SWB6710 front sign");
    }

    static void Inks(HdComposer c, int x0, int x1, out int[] all, out int[] top, out int[] bottom)
    {
        all = new int[HdInk.Count];
        top = new int[HdInk.Count];
        bottom = new int[HdInk.Count];
        for (int x = Math.Max(0, x0); x < Math.Min(c.Width, x1); x++)
            for (int y = 0; y < 32; y++)
            {
                if (!c.Buffer[x, y]) continue;
                all[c.Ink[x, y]]++;
                if (y < 19) top[c.Ink[x, y]]++; else bottom[c.Ink[x, y]]++;
            }
    }

    static bool Lit(HdComposer c, int x0, int x1, int y0, int y1)
    {
        for (int x = Math.Max(0, x0); x < Math.Min(c.Width, x1); x++)
            for (int y = Math.Max(0, y0); y <= Math.Min(31, y1); y++)
                if (c.Buffer[x, y]) return true;
        return false;
    }

    static bool LitInk(HdComposer c, int x0, int x1, byte ink)
    {
        for (int x = Math.Max(0, x0); x < Math.Min(c.Width, x1); x++)
            for (int y = 0; y < 32; y++)
                if (c.Buffer[x, y] && c.Ink[x, y] == ink) return true;
        return false;
    }

    static int LabelRows(HdComposer c, int x0, int x1)
    {
        int lo = 99, hi = -1;
        for (int x = x0; x < x1; x++)
            for (int y = 0; y < 32; y++)
                if (c.Buffer[x, y]) { lo = Math.Min(lo, y); hi = Math.Max(hi, y); }
        return hi - lo + 1;
    }

    /// <summary>Is the sign at offset b the sign at offset a moved left by shift dots (in the area from x0)?</summary>
    static bool Shifted(HdComposer c, int a, int b, int shift, int x0)
    {
        c.Render(a);
        var before = (bool[,])c.Buffer.Clone();
        c.Render(b);
        int lit = 0;
        for (int x = x0; x + shift < c.Width; x++)
            for (int y = 0; y < 32; y++)
            {
                if (c.Buffer[x, y] != before[x + shift, y]) return false;
                if (c.Buffer[x, y]) lit++;
            }
        return lit > 50;
    }

    static int FirstColumn(HdComposer c, int y0, int y1, int from = 0)
    {
        for (int x = from; x < c.Width; x++)
            for (int y = y0; y <= y1; y++)
                if (c.Buffer[x, y]) return x;
        return -1;
    }

    static int LastColumn(HdComposer c, int y0, int y1)
    {
        for (int x = c.Width - 1; x >= 0; x--)
            for (int y = y0; y <= y1; y++)
                if (c.Buffer[x, y]) return x;
        return -1;
    }

    /// <summary>hdpreview DIR: the packs' signs (Shanghai style) as PNG, a dot a 6-pixel square in its ink.</summary>
    internal static int Preview(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : "out/hdpreview";
        Directory.CreateDirectory(dir);
        if (!Load(out DotFont font, out Pinyin pinyin, out LedFont led)) { Console.WriteLine("no fonts"); return 1; }
        var cases = new (string name, float w, float h, HdContent content, int offset)[]
        {
            ("6710-front", 1.5f, 0.2f, HdContent.Route("", "滨江2", "路", "龙华中路东安路", StopEnglish.Of("龙华中路东安路", pinyin)), 60),
            ("6710-front-short", 1.5f, 0.2f, HdContent.Route("", "52", "路", "上海南站", StopEnglish.Of("上海南站", pinyin)), 0),
            ("6710-front-prefix", 1.5f, 0.2f, HdContent.Route("K", "12", "路", "人民广场", StopEnglish.Of("人民广场", pinyin)), 0),
            ("6710-side", 0.83f, 0.1581f, HdContent.Route("", "52", "路", "上海南站", StopEnglish.Of("上海南站", pinyin)), 0),
            ("6710-rear", 1.16f, 0.1785f, HdContent.Label("", "52", "路"), 0),
            ("6710-rear-brake", 1.16f, 0.1785f, HdContent.Warning("刹车注意", "Braking", HdInk.Warning, 0), 0),
            ("6710-rear-left", 1.16f, 0.1785f, HdContent.Warning("车辆左转", "Turning Left", HdInk.Sign, -1), 0),
            ("6710-rear-right", 1.16f, 0.1785f, HdContent.Warning("车辆右转", "Turning Right", HdInk.Sign, 1), 0),
            ("6109-front", 1.68f, 0.24f, HdContent.Route("夜", "3", "路", "虹桥火车站", StopEnglish.Of("虹桥火车站", pinyin)), 0),
            ("generic-rear", 0.42f, 0.16f, HdContent.Label("", "滨江2", "路"), 0),
            ("generic-rear-brake", 0.42f, 0.16f, HdContent.Warning("刹车注意", "Braking", HdInk.Warning, 0), 0),
            ("front-notinservice", 1.5f, 0.2f, HdContent.Route("", "", "", "暂停营运", "Not in Service"), 0),
            ("front-english", 1.5f, 0.2f, HdContent.Route("", "52", "", "Harbour Station", ""), 0),
            ("named-6710-front", 1.5f, 0.2f, HdContent.Route("", "高港专线", "", "泰州火车站", StopEnglish.Of("泰州火车站", pinyin)), 0),
            ("named-6710-side", 0.83f, 0.158f, HdContent.Route("", "高港专线", "", "泰州火车站", StopEnglish.Of("泰州火车站", pinyin)), 0),
            ("named-6710-rear", 1.16f, 0.178f, HdContent.Label("", "高港专线", ""), 0),
            ("named-6115-side", 0.85f, 0.2f, HdContent.Route("", "高港专线", "", "泰州火车站", StopEnglish.Of("泰州火车站", pinyin)), 0),
            ("named-6115-side-long", 0.85f, 0.2f, HdContent.Route("", "高港泰州专线", "", "泰州火车站", StopEnglish.Of("泰州火车站", pinyin)), 0),
            ("named-generic-rear", 0.42f, 0.16f, HdContent.Label("", "高港专线", ""), 0),
            ("named-generic-rear-long", 0.42f, 0.16f, HdContent.Label("", "一二三四五六七八", ""), 60),
            ("named-digits-6710-front", 1.5f, 0.2f, HdContent.Route("", "高港2号专线", "", "泰州火车站", StopEnglish.Of("泰州火车站", pinyin)), 0),
            ("named-digits-6109-side", 0.96f, 0.192f, HdContent.Route("", "高港2号专线", "", "泰州火车站", StopEnglish.Of("泰州火车站", pinyin)), 0),
            ("named-digits-6710-rear", 1.16f, 0.178f, HdContent.Label("", "高港2号专线", ""), 0),
            ("named-own-word-6710-front", 1.5f, 0.2f, HdContent.Route("快", "3线", "", "泰州火车站", StopEnglish.Of("泰州火车站", pinyin)), 0),
        };
        var colours = new[] { new byte[] { 255, 158, 31 }, new byte[] { 38, 255, 64 }, new byte[] { 26, 217, 255 }, new byte[] { 255, 26, 15 } };
        foreach (var k in cases)
        {
            int2 m = LedGeometry.HdMatrix(new float2(k.w, k.h));
            var c = new HdComposer(font, led, m.x);
            c.Compose(k.content);
            c.Render(k.offset);
            const int s = 6;
            int w = m.x * s + 2 * s, h = m.y * s + 2 * s;
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = 14; rgb[i + 1] = 14; rgb[i + 2] = 16; }
            for (int x = 0; x < m.x; x++)
                for (int y = 0; y < m.y; y++)
                {
                    byte[] col = c.Buffer[x, y] ? colours[c.Ink[x, y]] : new byte[] { 34, 30, 28 };
                    for (int dy = 1; dy < s - 1; dy++)
                        for (int dx = 1; dx < s - 1; dx++)
                        {
                            int o = ((s + y * s + dy) * w + s + x * s + dx) * 3;
                            rgb[o] = col[0]; rgb[o + 1] = col[1]; rgb[o + 2] = col[2];
                        }
                }
            string file = Path.Combine(dir, "hd-" + k.name + ".png");
            CardSignTests.Png(file, w, h, rgb);
            Console.WriteLine($"{file} {m.x}x{m.y} two lines {c.TwoLines} scrolls {c.Scrolls} cycle {c.Cycle} label medium {c.LabelMedium} arrow {c.HasArrow} english \"{k.content.English}\"");
        }

        return 0;
    }
}
