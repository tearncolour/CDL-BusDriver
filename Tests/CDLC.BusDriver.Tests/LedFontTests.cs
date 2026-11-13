using System;
using System.IO;
using CDLC.BusDriver.Interior;

// The LED screen's font and composer (pure): the packed Unifont loads, Latin and CJK glyphs have their widths and dots, a short
// message is centred, a long one scrolls and clips to the message area, and nothing is written outside the buffer.
static class LedFontTests
{
    internal static void Run(Action<bool, string> check)
    {
        string path = FontPath();
        check(path != null, "led-font.bin.gz found in the repo (Fonts/)");
        var font = new LedFont();
        bool loaded = path != null && font.Load(path);
        check(loaded && font.Count > 20000, $"LED font loads ({font.Count} glyphs)");
        check(font.Width('A') == 8 && font.Width('站') == 16 && font.Width('下') == 16, "glyph widths: A 8 dots, 站 / 下 16 dots");
        check(font.Has('·') && font.Has('，') && font.Has('。'), "the separators (·, fullwidth comma and full stop) have glyphs");

        var buffer = new bool[32, 16];
        font.Render('A', buffer, 0, 0);
        font.Render('站', buffer, 8, 0);
        check(Count(buffer, 0, 8) > 10 && Count(buffer, 8, 24) > 30, "A and 站 render dots");
        check(Count(buffer, 24, 32) == 0, "nothing past the glyphs");
        Array.Clear(buffer, 0, buffer.Length);
        font.Render((uint)0x1F68C, buffer, 0, 0);
        check(Count(buffer, 0, 8) > 0, "a missing glyph (emoji) draws the fallback box");

        // Clipping: a glyph half off the left edge and one off the right edge write only inside the buffer.
        Array.Clear(buffer, 0, buffer.Length);
        font.Render('站', buffer, -8, 0);
        font.Render('站', buffer, 28, 0);
        check(Count(buffer, 0, 8) > 0 && Count(buffer, 28, 32) > 0, "glyphs half off either edge write only inside the buffer");
        Array.Clear(buffer, 0, buffer.Length);
        font.Render('A', buffer, 4, 0, 6);
        check(Count(buffer, 4, 6) == 0 && Count(buffer, 6, 12) > 0, "a glyph clips at minX");

        var composer = new LedComposer(font, 128, 16);
        check(composer.MeasureWidth("12") == 17 && composer.MeasureWidth("下一站") == 50, $"widths: 12 = {composer.MeasureWidth("12")}, 下一站 = {composer.MeasureWidth("下一站")}");
        // "12" is 17 dots, the area starts at 21 and is 107 wide; "下一站 北门" is 93 dots and fits.
        bool scrolls = composer.RenderSign("12", "下一站 北门", 0);
        check(!scrolls, "a short message does not scroll");
        int first = FirstLit(composer.Buffer, 21), last = LastLit(composer.Buffer);
        check(first >= 28 && last <= 120 && Math.Abs((first - 21) - (127 - last)) <= 3, $"the short message is centred in the area (dots {first}..{last})");
        check(composer.Scrolls("12", "下一站 人民广场"), "下一站 人民广场 (127 dots) scrolls next to a route number");
        check(Count(composer.Buffer, 0, 17) > 0 && Count(composer.Buffer, 17, 21) == 0, "the route number sits at the left with a gap");

        string longText = "下一站 人民广场 · 开往 码头路客运枢纽站 · 换乘 M1";
        check(composer.Scrolls("12", longText), "a long message scrolls");
        int cycle = composer.Cycle("12", longText);
        check(cycle == composer.MeasureWidth(longText) + 128 - 21, $"cycle = message + area ({cycle})");
        composer.RenderSign("12", longText, 0);
        check(Count(composer.Buffer, 21, 128) == 0, "at offset 0 the message is still outside the area on the right");
        composer.RenderSign("12", longText, 40);
        check(Count(composer.Buffer, 21, 128) > 0 && Count(composer.Buffer, 17, 21) == 0 && Count(composer.Buffer, 0, 17) > 0, "scrolled in: lit in the area, the block gap stays dark, the number stays");
        composer.RenderSign("12", longText, cycle - 1);
        check(Count(composer.Buffer, 21, 128) == 0, "at the end of the cycle the message has left the area");
        composer.RenderSign("12", longText, -5);
        composer.RenderSign("12", longText, cycle * 3 + 7);
        check(true, "negative and wrapped offsets do not throw");
        composer.RenderSign("", "Next stop", 0);
        check(Count(composer.Buffer, 0, 128) > 0, "no block: the message alone");

        // A route prefix: its dots in ink 1, the number's and the message's in ink 0, the message after the whole label.
        composer.RenderSign("BRT", "1", "开往 北站", 0);
        int pw = composer.MeasureWidth("BRT"), lw = composer.LabelWidth("BRT", "1");
        int ink1 = 0, ink1Outside = 0, ink0InPrefix = 0;
        for (int x = 0; x < 128; x++)
            for (int y = 0; y < 16; y++)
            {
                if (!composer.Buffer[x, y]) continue;
                if (composer.Ink[x, y] == 1) { ink1++; if (x >= pw) ink1Outside++; }
                else if (x < pw) ink0InPrefix++;
            }
        check(ink1 > 20 && ink1Outside == 0 && ink0InPrefix == 0, $"the prefix BRT is all in ink 1 ({ink1} dots), nothing else is ({ink1Outside} outside, {ink0InPrefix} of ink 0 in it)");
        check(Count(composer.Buffer, pw + 1, lw) > 0 && Count(composer.Buffer, lw, lw + LedComposer.BlockGap) == 0, "the number follows the prefix, then the gap before the message");
        check(composer.Cycle("K", "3", "x") == composer.MeasureWidth("x") + 128 - composer.LabelWidth("K", "3") - LedComposer.BlockGap, "the cycle counts the whole label");
        var rear = new LedComposer(font, 48, 16);
        rear.RenderSign("K", "3", "", 0);
        int rf = FirstLit(rear.Buffer, 0), rl = LastLit(rear.Buffer);
        check(Math.Abs(rf - (47 - rl)) <= 2, $"the label alone is centred (dots {rf}..{rl} of 48)");

        // LedSign writes the prefix's layer once per text, not on every scroll step: its dots must not depend on the offset.
        check(composer.Scrolls("BRT", "1", longText), "a long message scrolls next to a prefixed label");
        bool[,] prefixAt0 = PrefixDots(composer, "BRT", "1", longText, 0);
        int prefixCycle = composer.Cycle("BRT", "1", longText);
        bool steady = Count(prefixAt0, 0, 128) > 20;
        foreach (int offset in new[] { 1, 17, 40, prefixCycle / 2, prefixCycle - 1 })
            steady &= Same(prefixAt0, PrefixDots(composer, "BRT", "1", longText, offset));
        check(steady, "the prefix's dots stay the same at every scroll offset");
        check(Count(PrefixDots(composer, "", "12", longText, 40), 0, 128) == 0, "no prefix: no dots in the prefix's ink");

        LabelTests(check);

        // The dots' plane sits inside the destination box, below the ceiling and behind the box's front.
        var spec = CDLC.BusDriver.Interior.Models.BusInteriorSpec.Default;
        CDLC.BusDriver.Interior.Models.BusInterior.LedScreenFrame(spec, out var centre, out var size);
        check(size.x > 0.6f && size.x < 0.8f && size.y > 0.07f && size.y < 0.1f, $"LED screen size {size.x:0.00} x {size.y:0.00} m");
        check(centre.x == 0f && centre.y > spec.RoofY - 0.42f && centre.y < spec.RoofY - 0.27f && centre.z < spec.FrontZ - 0.40f && centre.z > spec.FrontZ - 0.50f, $"LED screen at ({centre.x:0.00}, {centre.y:0.00}, {centre.z:0.00}) for roof {spec.RoofY:0.00}, front {spec.FrontZ:0.00}");
    }

    // The route label from a line's name: the prefix, the number, the prefix colours.
    static void LabelTests(Action<bool, string> check)
    {
        void Case(string name, int route, string prefix, string number)
        {
            CDLC.BusDriver.LineLabel.Parse(name, route, out string p, out string n);
            check(p == prefix && n == number, $"line '{name}' ({route}): prefix '{p}' number '{n}' (expected '{prefix}' '{number}')");
        }
        Case("K3", 7, "K", "3");
        Case("k 12", 7, "K", "12");
        Case("BRT-1", 7, "BRT", "1");
        Case("BRT1路", 7, "BRT", "1");
        Case("夜12路", 7, "夜", "12");
        Case("快3线", 7, "快", "3");
        Case("12", 7, "", "12");
        Case("36路", 7, "", "36");
        Case("12A", 7, "", "12A");
        Case("Bus Line 12", 12, "", "12");
        Case("公交12", 12, "", "12");
        Case("人民路", 5, "", "5");
        Case("高港专线", 5, "", "5");
        Case("", 0, "", "");
        // a name the player gave the line (NameSystem's custom name): its own words stay, digits or not
        void Named(string name, string prefix, string number)
        {
            CDLC.BusDriver.LineLabel.Parse(name, 5, true, out string p, out string n);
            check(p == prefix && n == number, $"player's line '{name}': prefix '{p}' number '{n}' (expected '{prefix}' '{number}')");
        }
        Named("高港专线", "", "高港专线");
        Named(" 机场大巴 ", "", "机场大巴");
        Named("高港2号专线", "", "高港2号专线");
        Named("1号专线", "", "1号专线");
        Named("高港专线2", "", "高港专线2");
        Named("旅游1线", "旅游", "1线");
        Named("K3高港快线", "", "K3高港快线");
        Named("306路区间", "", "306路区间");
        Named("快3线", "快", "3线");
        Named("机场1号线", "机场", "1号线");
        Named("BRT1线", "BRT", "1线");
        Named("12号线", "", "12号线");
        Named("夜12路", "夜", "12");
        Named("12路", "", "12");
        Named("K3", "K", "3");
        Named("公交12", "", "公交12");
        Named("Bus Line 12", "", "5");
        Named("Harbour Express", "", "5");
        Named("一二三四五六七八九十一二", "", "一二三四五六七八九十一二");
        Named("一二三四五六七八九十一二三", "", "5");
        check(CDLC.BusDriver.LineLabel.IsRouteNumber("12") && CDLC.BusDriver.LineLabel.IsRouteNumber("3A") && !CDLC.BusDriver.LineLabel.IsRouteNumber("3线") &&
              !CDLC.BusDriver.LineLabel.IsRouteNumber("高港2号专线") && !CDLC.BusDriver.LineLabel.IsRouteNumber("") && !CDLC.BusDriver.LineLabel.IsRouteNumber(null),
            "only a route number takes the locale's 路: not a line's name, not a number with its own word");
        string spec = "K=red; BRT = #00ff00 ;夜=蓝,*=white";
        check(CDLC.BusDriver.LineLabel.ColorFor("K", spec).x > 0.9f && CDLC.BusDriver.LineLabel.ColorFor("K", spec).y < 0.3f, "K is red");
        check(CDLC.BusDriver.LineLabel.Hex(CDLC.BusDriver.LineLabel.ColorFor("brt", spec)) == "#00ff00", "BRT by #RRGGBB, any case");
        check(CDLC.BusDriver.LineLabel.ColorFor("夜", spec).z > 0.9f, "夜 by a Chinese colour name");
        check(CDLC.BusDriver.LineLabel.ColorFor("X", spec).y > 0.9f, "an unknown prefix takes *");
        check(CDLC.BusDriver.LineLabel.ColorFor("X", "K=blue").x > 0.9f, "no * : red");
        check(CDLC.BusDriver.LineLabel.ColorFor("K", "").y > 0.9f && CDLC.BusDriver.LineLabel.ColorFor("K", "").x < 0.5f, "an empty option: the defaults (K green)");
    }

    internal static string FontPath()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "Fonts", "led-font.bin.gz");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    // The dots LedSign's prefix layer lights for a sign at a scroll offset (lit and in ink 1).
    static bool[,] PrefixDots(LedComposer composer, string prefix, string block, string message, int offset)
    {
        composer.RenderSign(prefix, block, message, offset);
        var dots = new bool[composer.Width, composer.Height];
        for (int x = 0; x < composer.Width; x++)
            for (int y = 0; y < composer.Height; y++)
                dots[x, y] = composer.Buffer[x, y] && composer.Ink[x, y] == 1;
        return dots;
    }

    static bool Same(bool[,] a, bool[,] b)
    {
        for (int x = 0; x < a.GetLength(0); x++)
            for (int y = 0; y < a.GetLength(1); y++)
                if (a[x, y] != b[x, y]) return false;
        return true;
    }

    static int Count(bool[,] b, int x0, int x1)
    {
        int n = 0;
        for (int x = Math.Max(0, x0); x < Math.Min(x1, b.GetLength(0)); x++)
            for (int y = 0; y < b.GetLength(1); y++)
                if (b[x, y]) n++;
        return n;
    }

    static int FirstLit(bool[,] b, int from)
    {
        for (int x = from; x < b.GetLength(0); x++)
            for (int y = 0; y < b.GetLength(1); y++)
                if (b[x, y]) return x;
        return -1;
    }

    static int LastLit(bool[,] b)
    {
        for (int x = b.GetLength(0) - 1; x >= 0; x--)
            for (int y = 0; y < b.GetLength(1); y++)
                if (b[x, y]) return x;
        return -1;
    }
}
