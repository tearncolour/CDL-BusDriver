using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// The station packs' live departure boards (the owner, 21 October 2026: "你这电子灯牌没用啊", then "实时信息，由 Bus Driver 绘制"): the station-sign file
// is read and its mistakes refused with the reason; the stops before a bay are counted round the line; a bay's lines are ordered soonest first; the
// three layouts put their texts where they belong, in their inks, a page at a time, scrolling only where they may; "stationpreview <dir>" writes them
// as PNG to look at.
static class StationBoardTests
{
    const string P = "station boards: ";

    internal const string Sample = @"{
 ""format"": ""cdlc-station-signs"", ""version"": 1, ""prefab"": ""CDLCBusInterchange"",
 ""stops"": [ { ""bay"": 1, ""position"": [21.0, 0, -1.5] }, { ""bay"": 2, ""position"": [11.84, 0, -1.5] } ],
 ""screens"": [
  { ""id"": ""hall"", ""layout"": ""departures"", ""bays"": [1, 2], ""centre"": [1.15, 5.5, 30.08], ""normal"": [0, 0, -1], ""size"": [8.8, 1.96], ""rows"": 154 },
  { ""id"": ""bay1"", ""layout"": ""bay"", ""bays"": [1], ""centre"": [17.88, 4.225, 3.46], ""normal"": [0, 0, -1], ""size"": [3.54, 0.45], ""rows"": 32 },
  { ""id"": ""pylon1"", ""layout"": ""pylon"", ""bays"": [1], ""centre"": [16.33, 1.65, 5.26], ""normal"": [0, 0, 1], ""size"": [0.46, 0.62], ""rows"": 88 }
 ] }";

    static string Broken(string from, string to) => Sample.Replace(from, to);

    static bool Refused(string text, string reason, out string got)
    {
        try
        {
            StationSignProfile.Parse(text, "test");
            got = "accepted";
            return false;
        }
        catch (StationSignProfile.Error e)
        {
            got = e.Message;
            return e.Message.Contains(reason);
        }
    }

    internal static BoardTexts Chinese() => new BoardTexts
    {
        AtStop = "到站", Arriving = "进站", StopsOne = "{0}站", StopsMany = "{0}站", NoBus = "暂无车辆", NoLines = "暂无线路", NoLinesEnglish = "No lines",
        AtStopEnglish = "At stop", ArrivingEnglish = "Arriving", NoBusEnglish = "No bus",
        HeadBay = "站台", HeadLine = "线路", HeadTo = "开往", HeadWaiting = "候车", HeadStatus = "状态",
    };

    internal static BoardTexts English() => new BoardTexts
    {
        AtStop = "At stop", Arriving = "Arriving", StopsOne = "{0} stop", StopsMany = "{0} stops", NoBus = "No bus", NoLines = "No lines", NoLinesEnglish = "",
        AtStopEnglish = "", ArrivingEnglish = "", NoBusEnglish = "",
        HeadBay = "Bay", HeadLine = "Line", HeadTo = "To", HeadWaiting = "Waiting", HeadStatus = "Status",
    };

    static BoardRoute Route(string label, string terminus, string english, BoardStatus status, int stops, byte ink = BoardInk.Line0, int waiting = 0) =>
        new BoardRoute { Label = label, Suffix = "路", LineInk = ink, Terminus = terminus, English = english, Status = status, Stops = stops, Waiting = waiting };

    internal static void Run(Action<bool, string> check)
    {
        // ---- the file
        StationSignProfile profile = StationSignProfile.Parse(Sample, "test");
        check(profile.Prefab == "CDLCBusInterchange" && profile.Stops.Length == 2 && profile.Screens.Length == 3 && profile.Screens[0].Layout == StationSignProfile.Layout.Departures,
            P + "a station-sign file is read (prefab, stops, screens, layouts)");
        check(profile.Screens[0].Columns == 691 && profile.Screens[1].Columns == 252 && profile.Screens[2].Columns == 65,
            P + $"square dots: the hall board {profile.Screens[0].Columns} x 154, a bay board {profile.Screens[1].Columns} x 32, a pylon {profile.Screens[2].Columns} x 88");
        var refusals = new (string text, string reason)[]
        {
            (Broken("cdlc-station-signs", "cdlb-vehicle"), "not a cdlc-station-signs"),
            (Broken(@"""version"": 1", @"""version"": 2"), "version 2"),
            (Broken(@"""bay"": 2,", @"""bay"": 1,"), "bay 1 twice"),
            (Broken(@"""bays"": [1, 2]", @"""bays"": [1, 3]"), "bay 3 has no stop"),
            (Broken(@"""normal"": [0, 0, 1]", @"""normal"": [0, 1, 0]"), "horizontal unit vector"),
            (Broken(@"""layout"": ""bay"", ""bays"": [1]", @"""layout"": ""bay"", ""bays"": [1, 2]"), "shows one bay"),
            (Broken(@"""size"": [8.8, 1.96]", @"""size"": [29.0, 1.96]"), "dots"),
            (Broken(@"""layout"": ""pylon""", @"""layout"": ""tower"""), "bay, pylon or departures"),
            (Broken(@"""prefab"": ""CDLCBusInterchange""", @"""prefab"": ""Bus Interchange"""), "prefab"),
            (Broken(@"""centre"": [1.15, 5.5, 30.08]", @"""centre"": [1.15, 5.5]"), "[x, y, z]"),
        };
        foreach (var (text, reason) in refusals)
            check(Refused(text, reason, out string got), P + $"refused with the reason \"{reason}\" (got \"{got}\")");

        // ---- the stops before the bay, round the line (waypoints: stop, control point, stop, stop, control point, the bay)
        var line = new List<bool> { true, false, true, true, false, true };
        check(StationBoardRules.StopsBetween(line, 2, 5) == 2 && StationBoardRules.StopsBetween(line, 4, 5) == 0 && StationBoardRules.StopsBetween(line, 5, 5) == 0 &&
              StationBoardRules.StopsBetween(line, 0, 5) == 3 && StationBoardRules.StopsBetween(line, 0, 2) == 1 && StationBoardRules.StopsBetween(line, 3, 0) == 2 &&
              StationBoardRules.StopsBetween(line, 6, 5) == -1 && StationBoardRules.StopsBetween(new List<bool>(), 0, 0) == -1,
            P + "the stops between a bus's next waypoint and the bay are counted round the line (its next stop counted, the bay not; not a waypoint: -1)");
        check(StationBoardRules.OfBus(true, 3).status == BoardStatus.AtStop && StationBoardRules.OfBus(false, 0).status == BoardStatus.Arriving &&
              StationBoardRules.OfBus(false, 2) == (BoardStatus.Stops, 2) && StationBoardRules.OfBus(false, -1).status == BoardStatus.NoBus,
            P + "a bus boarding at the bay is at the stop, one whose next stop is the bay arriving, else n stops away, off the line none");
        check(StationBoardRules.Nearer((BoardStatus.Stops, 3), (BoardStatus.Arriving, 0)).status == BoardStatus.Arriving &&
              StationBoardRules.Nearer((BoardStatus.Stops, 1), (BoardStatus.Stops, 4)).stops == 1 && StationBoardRules.Nearer((BoardStatus.NoBus, 0), (BoardStatus.Stops, 9)).stops == 9,
            P + "the nearer of two buses wins");
        var routes = new List<BoardRoute> { Route("10", "a", "", BoardStatus.Stops, 2), Route("2", "b", "", BoardStatus.Stops, 2), Route("K3", "c", "", BoardStatus.NoBus, 0),
            Route("52", "d", "", BoardStatus.Arriving, 0), Route("9", "e", "", BoardStatus.AtStop, 0) };
        StationBoardRules.Sort(routes);
        check(string.Join(",", routes.Select(r => r.Label)) == "9,52,2,10,K3", P + $"a bay's lines soonest first, then 2 before 10 ({string.Join(",", routes.Select(r => r.Label))})");
        check(math.all(math.abs(StationBoardRules.LedColour(new float3(0.1f, 0.2f, 0.5f)) - new float3(0.2f, 0.4f, 1f)) < 1e-4f) &&
              math.all(StationBoardRules.LedColour(new float3(0f, 0f, 0f)) == new float3(1f, 1f, 1f)),
            P + "a dark line colour is shown at full brightness with its hue; black as white");
        check(StationBoardRules.Page(0, 6) == 0 && StationBoardRules.Page(13, 6) == 2 && StationBoardRules.Page(-5, 6) == 0, P + "a page every six seconds");

        // ---- the layouts
        if (!HdSignTests.Load(out DotFont font, out Pinyin pinyin, out LedFont led))
        {
            check(false, P + "the fonts load");
            return;
        }

        BoardTexts zh = Chinese(), en = English();
        var bay = new BoardBay { Bay = 1, Waiting = 12 };
        bay.Routes.Add(Route("52", "龙华中路", StopEnglish.Of("龙华中路", pinyin), BoardStatus.Stops, 2, BoardInk.Line0));
        bay.Routes.Add(Route("K3", "虹桥火车站东交通中心", StopEnglish.Of("虹桥火车站东交通中心", pinyin), BoardStatus.Arriving, 0, BoardInk.Line0 + 1));
        var board = new StationBoardComposer(font, led, StationSignProfile.Layout.Bay, 252, 32);
        board.Compose(new[] { bay }, zh, 0);
        board.Render(0);
        check(board.Pages == 2 && board.Texts.Contains("52") && board.Texts.Contains("龙华中路") && board.Texts.Contains("2站") && board.Texts.Contains("2 stops"),
            P + $"a bay board: a line a page, its label, terminus, English and status ({string.Join(" | ", board.Texts)})");
        int labelEnd = StationBoardComposer.Margin + board.Measure("52", DotFont.Label) - 1;
        check(InkOnly(board, BoardInk.Line0, 0, labelEnd) && Lit(board, BoardInk.Text) > 0 && Lit(board, BoardInk.Accent) > 0,
            P + $"the label in its line's colour (columns 0..{labelEnd}), the text and the English in theirs");
        board.Compose(new[] { bay }, zh, 1);
        board.Render(0);
        bool[,] first = (bool[,])board.Buffer.Clone();
        board.Render(80);
        check(board.Texts.Contains("K3") && board.Texts.Contains("进站") && board.Scrolls && !Same(first, board.Buffer) && Lit(board, BoardInk.Good) > 0,
            P + "its second page: the next line, arriving (green), its long terminus scrolling");
        var empty = new BoardBay { Bay = 3 };
        board.Compose(new[] { empty }, zh, 0);
        board.Render(0);
        check(board.Texts.Contains("暂无线路") && board.Texts.Contains("No lines") && Lit(board, BoardInk.Dim) > 0, P + "a bay without lines says so");
        board.Compose(new[] { bay }, en, 0);
        check(board.Texts.Contains("2 stops") && !board.Texts.Contains("2站"), P + "in English the status is one line");

        var pylon = new StationBoardComposer(font, led, StationSignProfile.Layout.Pylon, 65, 88);
        bay.Routes.Add(Route("夜12", "莘庄", "", BoardStatus.NoBus, 0, BoardInk.Line0 + 2));
        pylon.Compose(new[] { bay }, zh, 0);
        pylon.Render(0);
        check(pylon.PerPage == 2 && pylon.Pages == 2 && pylon.Texts.Count(t => t == "52" || t == "K3") == 2 && !pylon.Texts.Contains("夜12") && NothingOutside(pylon),
            P + $"a pylon: two lines a page ({string.Join(" | ", pylon.Texts)})");
        pylon.Compose(new[] { bay }, zh, 1);
        check(pylon.Texts.Contains("夜12") && pylon.Texts.Contains("暂无车辆"), P + "its second page: the third line, no bus");

        var hall = new StationBoardComposer(font, led, StationSignProfile.Layout.Departures, 691, 154);
        var bays = new List<BoardBay>();
        for (int b = 1; b <= 6; b++)
        {
            var bb = new BoardBay { Bay = b, Waiting = b * 3 };
            bb.Routes.Add(Route((b * 11).ToString(), "滨江" + b + "路", "", BoardStatus.Stops, b, (byte)(BoardInk.Line0 + b), b * 3));
            if (b <= 2)
                bb.Routes.Add(Route("K" + b, "人民广场", "", BoardStatus.Arriving, 0, BoardInk.Line0));
            bays.Add(bb);
        }

        hall.Compose(bays, zh, 0);
        hall.Render(0);
        check(hall.PerPage == 6 && hall.Pages == 2 && hall.Texts.Contains("站台") && hall.Texts.Contains("开往") && hall.Texts.Contains("1/2") && hall.Texts.Contains("11") && hall.Texts.Contains("6") &&
              !hall.Scrolls && NothingOutside(hall),
            P + $"the hall board: a header, six rows a page, 8 rows in two pages, nothing scrolling ({hall.Texts.Count()} texts)");
        hall.Compose(bays, zh, 1);
        check(hall.Texts.Contains("2/2") && hall.Texts.Contains("66") && hall.Texts.Contains("滨江6路"), P + "its second page: the last rows");
        hall.Compose(new List<BoardBay>(), zh, 0);
        check(hall.Texts.Contains("暂无线路") && hall.Pages == 1, P + "a station without lines says so");

        // a line's name in the line column of a smaller departures board: the column widens to it (a quarter of the board at most)
        var small = new StationBoardComposer(font, led, StationSignProfile.Layout.Departures, 400, 66);
        var named = new BoardBay { Bay = 1 };
        named.Routes.Add(new BoardRoute { Label = "高港专线", Suffix = "", LineInk = BoardInk.Line0 + 3, Terminus = "泰州火车站", Status = BoardStatus.Stops, Stops = 2 });
        small.Compose(new[] { named }, zh, 0);
        small.Render(0);
        int lo = int.MaxValue, hi = -1;
        for (int x = 0; x < small.Width; x++)
            for (int y = StationBoardComposer.DepartureRow; y < small.Height; y++)
                if (small.Buffer[x, y] && small.Ink[x, y] == BoardInk.Line0 + 3) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
        int want = small.Measure("高港专线", DotFont.Medium);
        check(hi >= lo && hi - lo + 1 >= want - 2 && NothingOutside(small),
            P + $"a 400-dot departures board draws the whole \"高港专线\" ({hi - lo + 1} of {want} dots)");
    }

    static int Lit(StationBoardComposer c, byte ink)
    {
        int n = 0;
        for (int x = 0; x < c.Width; x++)
            for (int y = 0; y < c.Height; y++)
                if (c.Buffer[x, y] && c.Ink[x, y] == ink)
                    n++;
        return n;
    }

    // the lit dots of columns x0..x1 are all of an ink (and some are lit)
    static bool InkOnly(StationBoardComposer c, byte ink, int x0, int x1)
    {
        int n = 0;
        for (int x = x0; x <= x1; x++)
            for (int y = 0; y < c.Height; y++)
                if (c.Buffer[x, y])
                {
                    if (c.Ink[x, y] != ink) return false;
                    n++;
                }
        return n > 0;
    }

    static bool NothingOutside(StationBoardComposer c)
    {
        for (int y = 0; y < c.Height; y++)
            if (c.Buffer[0, y] || c.Buffer[c.Width - 1, y])
                return false;
        return true;
    }

    static bool Same(bool[,] a, bool[,] b)
    {
        for (int x = 0; x < a.GetLength(0); x++)
            for (int y = 0; y < a.GetLength(1); y++)
                if (a[x, y] != b[x, y]) return false;
        return true;
    }

    /// <summary>"stationcheck &lt;file&gt;": a station pack's .cdls.json read as the mod reads it (its screens and their dots, or the reason it is refused).</summary>
    internal static int CheckFile(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: stationcheck <file.cdls.json>"); return 2; }
        try
        {
            StationSignProfile p = StationSignProfile.Load(args[1]);
            Console.WriteLine($"STATION_SIGNS_OK {p.Prefab}: {p.Stops.Length} bays, {p.Screens.Length} screens");
            foreach (StationSignProfile.Screen sc in p.Screens)
                Console.WriteLine($"  {sc.Id}: {sc.Layout} bays {string.Join(",", sc.Bays)}, {sc.Columns} x {sc.Rows} dots, {sc.Size.x:0.00} x {sc.Size.y:0.00} m");
            return 0;
        }
        catch (StationSignProfile.Error e)
        {
            Console.WriteLine("STATION_SIGNS_REFUSED " + e.Message);
            return 1;
        }
    }

    /// <summary>"stationpreview [dir]": the boards as PNG (a dot 4 px, the inks' colours; the lines' colours a red, a blue, a green and a purple).</summary>
    internal static int Preview(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : "out/stationpreview";
        Directory.CreateDirectory(dir);
        if (!HdSignTests.Load(out DotFont font, out Pinyin pinyin, out LedFont led)) { Console.WriteLine("no fonts"); return 1; }
        var palette = new byte[BoardInk.Count][];
        palette[BoardInk.Text] = new byte[] { 255, 158, 31 };
        palette[BoardInk.Accent] = new byte[] { 26, 217, 255 };
        palette[BoardInk.Good] = new byte[] { 40, 255, 90 };
        palette[BoardInk.Dim] = new byte[] { 150, 100, 30 };
        var lines = new[] { new byte[] { 255, 60, 50 }, new byte[] { 60, 120, 255 }, new byte[] { 60, 230, 90 }, new byte[] { 200, 80, 255 } };
        for (int i = BoardInk.Line0; i < BoardInk.Count; i++)
            palette[i] = lines[(i - BoardInk.Line0) % lines.Length];
        var bay = new BoardBay { Bay = 3, Waiting = 14 };
        bay.Routes.Add(Route("52", "龙华中路", StopEnglish.Of("龙华中路", pinyin), BoardStatus.Stops, 2, BoardInk.Line0));
        bay.Routes.Add(Route("K3", "虹桥火车站", StopEnglish.Of("虹桥火车站", pinyin), BoardStatus.Arriving, 0, BoardInk.Line0 + 1));
        bay.Routes.Add(Route("夜12", "莘庄", StopEnglish.Of("莘庄", pinyin), BoardStatus.AtStop, 0, BoardInk.Line0 + 2));
        var bays = new List<BoardBay>();
        string[] towns = { "人民广场", "上海南站", "龙华中路东安路", "世纪大道", "徐家汇", "中山公园" };
        for (int b = 1; b <= 6; b++)
        {
            var bb = new BoardBay { Bay = b, Waiting = b * 4 };
            bb.Routes.Add(Route((b * 11).ToString(), towns[b - 1], "", b == 2 ? BoardStatus.AtStop : b == 4 ? BoardStatus.NoBus : BoardStatus.Stops, b, (byte)(BoardInk.Line0 + b % 4), b * 4));
            if (b == 3)
                bb.Routes.Add(Route("K3", "虹桥火车站", "", BoardStatus.Arriving, 0, BoardInk.Line0 + 1));
            bays.Add(bb);
        }

        var cases = new (string name, StationSignProfile.Layout layout, int w, int h, List<BoardBay> bays, BoardTexts texts, int page)[]
        {
            ("bay-page1", StationSignProfile.Layout.Bay, 252, 32, new List<BoardBay> { bay }, Chinese(), 0),
            ("bay-page2", StationSignProfile.Layout.Bay, 252, 32, new List<BoardBay> { bay }, Chinese(), 1),
            ("bay-page3", StationSignProfile.Layout.Bay, 252, 32, new List<BoardBay> { bay }, Chinese(), 2),
            ("bay-english", StationSignProfile.Layout.Bay, 252, 32, new List<BoardBay> { bay }, English(), 0),
            ("bay-empty", StationSignProfile.Layout.Bay, 252, 32, new List<BoardBay> { new BoardBay { Bay = 5 } }, Chinese(), 0),
            ("pylon-page1", StationSignProfile.Layout.Pylon, 65, 88, new List<BoardBay> { bay }, Chinese(), 0),
            ("pylon-page2", StationSignProfile.Layout.Pylon, 65, 88, new List<BoardBay> { bay }, Chinese(), 1),
            ("pylon-english", StationSignProfile.Layout.Pylon, 65, 88, new List<BoardBay> { bay }, English(), 0),
            ("hall", StationSignProfile.Layout.Departures, 691, 154, bays, Chinese(), 0),
            ("hall-english", StationSignProfile.Layout.Departures, 691, 154, bays, English(), 0),
        };
        foreach (var k in cases)
        {
            var c = new StationBoardComposer(font, led, k.layout, k.w, k.h);
            c.Compose(k.bays, k.texts, k.page);
            c.Render(0);
            int s = k.w > 300 ? 3 : 6;
            int w = k.w * s + 2 * s, h = k.h * s + 2 * s;
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = 14; rgb[i + 1] = 14; rgb[i + 2] = 16; }
            for (int x = 0; x < k.w; x++)
                for (int y = 0; y < k.h; y++)
                {
                    byte[] col = c.Buffer[x, y] ? palette[c.Ink[x, y]] : new byte[] { 34, 30, 28 };
                    for (int dy = 1; dy < s - 1; dy++)
                        for (int dx = 1; dx < s - 1; dx++)
                        {
                            int o = ((s + y * s + dy) * w + s + x * s + dx) * 3;
                            rgb[o] = col[0]; rgb[o + 1] = col[1]; rgb[o + 2] = col[2];
                        }
                }

            string file = Path.Combine(dir, "station-" + k.name + ".png");
            CardSignTests.Png(file, w, h, rgb);
            Console.WriteLine($"{file} {k.w}x{k.h} pages {c.Pages} scrolls {c.Scrolls}: {string.Join(" | ", c.Texts)}");
        }

        return 0;
    }
}
