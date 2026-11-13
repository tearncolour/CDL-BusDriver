using System;
using System.IO;
using System.IO.Compression;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;

// The printed boards ("card" signs, pure parts): the baked faces load, the board text joins a line's two ends, a board is laid out
// centred with the label bigger than the text, a long text is squeezed then shrunk but never drawn past the margins, a code point
// neither face has comes from the LED font, and the colours are the profile's. "cardpreview <dir>" writes boards as PNG.
static class CardSignTests
{
    internal static string FontPath()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "Fonts", "card-font.bin.gz");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    internal static void Run(Action<bool, string> check)
    {
        string path = FontPath();
        check(path != null, "card-font.bin.gz found in the repo (Fonts/)");
        var font = new CardFont();
        bool loaded = path != null && font.Load(path);
        check(loaded && font.FaceCount == 2 && font.Count > 7500, $"card font loads ({font.FaceCount} faces, {font.Count} glyphs)");
        if (!loaded)
            return;
        check(font.Has(CardFont.Label, '5') && font.Has(CardFont.Label, 'K') && font.Has(CardFont.Text, '机') && font.Has(CardFont.Text, '场') &&
              font.Has(CardFont.Text, 0x21C4), "glyphs: digits and Latin in the label face; 机 场 and the arrow ⇄ in the text face");
        check(!font.Has(CardFont.Label, '机') && font.Find(CardFont.Label, '机', out int f) != null && f == CardFont.Text,
              "a CJK character asked of the label face is found in the text face");

        check(CardText.Route("大观公园", "飞机场", "52路", "52") == "大观公园 ↔ 飞机场", "two ends: A ⇄ B");
        check(CardText.Route("飞机场", "飞机场", "52路", "52") == "飞机场", "the same stop twice: once");
        check(CardText.Route("", "飞机场", "52路", "52") == "飞机场" && CardText.Route("大观公园", "", "", "52") == "大观公园", "one end known: that end");
        check(CardText.Route("", "", "Harbour Express", "12") == "Harbour Express" && CardText.Route("", "", "12", "12") == "", "no ends: the line's name, nothing when it is only the number");
        CardText.PictureSize(2.13f, 0.25f, out int fw, out int fh);
        CardText.PictureSize(9f, 0.1f, out int ww, out int wh);
        check(fh == 128 && Math.Abs(fw - 1091) <= 1 && ww == 2048 && wh < 128, $"picture sizes: front 2.13 x 0.25 m -> {fw} x {fh}, a very long board capped at {ww} x {wh}");
        check(CardText.Key(10, 10, CardRgb.DefaultBoard, CardRgb.DefaultInk, CardRgb.DefaultInk, "", "52", "a") !=
              CardText.Key(10, 10, CardRgb.DefaultBoard, new CardRgb(0, 0, 0), CardRgb.DefaultInk, "", "52", "a"), "the key changes with the ink");

        var led = new LedFont();
        string ledPath = LedFontTests.FontPath();
        bool ledLoaded = ledPath != null && led.Load(ledPath);
        var c = new CardComposer(font, ledLoaded ? led : null, fw, fh);
        CardRgb board = CardRgb.DefaultBoard, ink = CardRgb.DefaultInk;
        bool drawn = c.Render("", "52", "大观公园 ↔ 飞机场", board, ink, ink);
        int margin = (int)Math.Max(0.03f * fw, 0.22f * fh);
        check(drawn && c.InkLeft >= margin - 2 && c.InkRight <= fw - margin + 2, $"52 大观公园 ⇄ 飞机场: drawn inside the margins ({c.InkLeft}..{c.InkRight} of {fw}, margin {margin})");
        check(Math.Abs((c.InkLeft + c.InkRight) * 0.5f - fw * 0.5f) < 0.04f * fw, "the board's ink is centred");
        check(c.Squeeze == 1f && c.LabelScale > c.TextScale, $"it fits unsqueezed; the label bigger than the text ({c.LabelScale:0.00} vs {c.TextScale:0.00})");
        check(c.FallbackGlyphs == 0, "every glyph of the Kunming board is in the baked faces");
        check(Count(c, ink, 30) > 2000 && Count(c, board, 2) > fw * fh / 2, "red ink on a mostly white board");

        c.Render("", "52", "昆明世界园艺博览会西门停车场 ↔ 长水国际机场航站楼东侧站", board, ink, ink);
        check(c.Squeeze < 1f && c.InkLeft >= margin - 2 && c.InkRight <= fw - margin + 2, $"a long text is squeezed ({c.Squeeze:0.00}) and stays inside");
        c.Render("", "52", new string('站', 60), board, ink, ink);
        check(c.Squeeze == CardComposer.MinSqueeze && c.InkRight <= fw - margin + 2 && c.TextScale < 0.5f * c.LabelScale + 1f, $"a very long text: squeezed to the minimum, then smaller ({c.TextScale:0.00})");

        CardText.PictureSize(1.02f, 0.21f, out int rw, out int rh);
        var rear = new CardComposer(font, ledLoaded ? led : null, rw, rh);
        rear.Render("", "52", "", board, ink, ink);
        check(rear.InkLeft > 0.25f * rw && rear.InkRight < 0.75f * rw, $"the rear board: the number alone, centred ({rear.InkLeft}..{rear.InkRight} of {rw})");
        var prefixInk = new CardRgb(20, 90, 200);
        rear.Render("K", "52", "", board, ink, prefixInk);
        check(Count(rear, prefixInk, 30) > 50 && Count(rear, ink, 30) > 50, "a prefix in its own colour, the number in the ink");
        check(!rear.Render("", "", " ", board, ink, ink) && Count(rear, board, 0) == rw * rh, "nothing to show: a blank board");

        if (ledLoaded)
        {
            c.Render("", "7", "\U0001F68C", board, ink, ink);
            check(c.FallbackGlyphs == 1, "a code point neither face has is drawn from the LED font");
        }

        ProfileChecks(check);
    }

    /// <summary>The profile's sign style: "card" with its colours, "led" the default, the colours refused on an LED sign.</summary>
    static void ProfileChecks(Action<bool, string> check)
    {
        if (File.Exists(TestPacks.ProfilePath("WG6980A")))
        {
            VehicleProfile wg = TestPacks.Profile("WG6980A");
            check(wg.FrontSign.IsCard && wg.SideSign.IsCard && wg.RearSign.IsCard, "WG6980A: all three signs are printed boards");
            check(wg.CardReaders.Length == 0 && BusInterior.CardReaders(wg.Spec).Count == 0, "WG6980A: no card reader (a coins-only 1999 bus; the user, 21 October)");
            check(Math.Abs(wg.FrontSign.Size.y - 0.25f) < 1e-4f && wg.FrontSign.Ink.x == 200f && wg.FrontSign.Board.y == 240f, "WG6980A: the front board's height and colours as written");

            string text = File.ReadAllText(TestPacks.ProfilePath("WG6980A"));
            string dir = TestPacks.Folder("WG6980A");
            bool refusedStyle = false, refusedColours = false;
            try { VehicleProfileJson.Parse(text.Replace("\"style\": \"card\"", "\"style\": \"paper\""), dir, "test", false); }
            catch (VehicleProfileJson.Error) { refusedStyle = true; }
            try { VehicleProfileJson.Parse(text.Replace("\"style\": \"card\"", "\"style\": \"led\""), dir, "test", false); }
            catch (VehicleProfileJson.Error) { refusedColours = true; }
            check(refusedStyle && refusedColours, "an unknown style is refused; ink/board on an LED sign are refused");
        }

        if (File.Exists(TestPacks.ProfilePath("SWB6710EV01G")))
        {
            VehicleProfile led = TestPacks.Profile("SWB6710EV01G");
            check(!led.FrontSign.IsCard && led.FrontSign.Style == VehicleProfile.SignStyle.Led && led.FrontSign.Ink.x < 0f, "SWB6710EV01G: LED signs (no style: the default)");
        }
    }

    /// <summary>Pixels within <paramref name="tolerance"/> of a colour.</summary>
    static int Count(CardComposer c, CardRgb rgb, int tolerance)
    {
        int n = 0;
        for (int i = 0; i < c.Pixels.Length; i += 4)
            if (Math.Abs(c.Pixels[i] - rgb.R) <= tolerance && Math.Abs(c.Pixels[i + 1] - rgb.G) <= tolerance && Math.Abs(c.Pixels[i + 2] - rgb.B) <= tolerance)
                n++;
        return n;
    }

    /// <summary>cardpreview DIR: the boards of the WG6980A and a few hard cases as PNG.</summary>
    internal static int Preview(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : "out/cardpreview";
        Directory.CreateDirectory(dir);
        var font = new CardFont();
        if (!font.Load(FontPath())) { Console.WriteLine("no card font"); return 1; }
        var led = new LedFont();
        led.Load(LedFontTests.FontPath());
        var cases = new (string name, float w, float h, string prefix, string number, string text)[]
        {
            ("front", 2.13f, 0.25f, "", "52", "大观公园 ↔ 飞机场"),
            ("side", 1.21f, 0.211f, "", "52", "大观公园 ↔ 飞机场"),
            ("rear", 1.02f, 0.21f, "", "52", ""),
            ("front-long", 2.13f, 0.25f, "", "52", "昆明世界园艺博览会西门停车场 ↔ 长水国际机场"),
            ("front-latin", 2.13f, 0.25f, "K", "12", "Harbour Station ↔ Airport"),
            ("front-notinservice", 2.13f, 0.25f, "", "", "暂停营运"),
            ("named-front", 2.13f, 0.25f, "", "高港专线", "高港 ↔ 泰州火车站"),
            ("named-side", 1.21f, 0.211f, "", "高港专线", "高港 ↔ 泰州火车站"),
            ("named-rear", 1.02f, 0.21f, "", "高港专线", ""),
            ("named-digits-front", 2.13f, 0.25f, "", "高港2号专线", "高港 ↔ 泰州火车站"),
            ("named-digits-rear", 1.02f, 0.21f, "", "高港2号专线", ""),
        };
        foreach (var k in cases)
        {
            CardText.PictureSize(k.w, k.h, out int w, out int h);
            var c = new CardComposer(font, led, w, h);
            c.Render(k.prefix, k.number, k.text, CardRgb.DefaultBoard, CardRgb.DefaultInk, new CardRgb(20, 90, 200));
            var rgb = new byte[w * h * 3];
            for (int i = 0, j = 0; i < c.Pixels.Length; i += 4, j += 3) { rgb[j] = c.Pixels[i]; rgb[j + 1] = c.Pixels[i + 1]; rgb[j + 2] = c.Pixels[i + 2]; }
            string file = Path.Combine(dir, "card-" + k.name + ".png");
            Png(file, w, h, rgb);
            Console.WriteLine($"{file} {w}x{h} squeeze {c.Squeeze:0.00} label {c.LabelScale:0.00} text {c.TextScale:0.00} fallback {c.FallbackGlyphs}");
        }

        return 0;
    }

    internal static void Png(string path, int w, int h, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
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
        uint r = 0xFFFFFFFFu;
        foreach (byte x in td) { r ^= x; for (int k = 0; k < 8; k++) r = (r & 1) != 0 ? 0xEDB88320u ^ (r >> 1) : r >> 1; }
        var crc = new byte[4]; BE(crc, 0, (int)(r ^ 0xFFFFFFFFu)); s.Write(crc);
    }
}
