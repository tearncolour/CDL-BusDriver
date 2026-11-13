using System;
using System.Collections.Generic;
using System.Text;
using CDLC.BusDriver.Interior.Models;

// The live departure boards of a station pack's screens (the owner, 21 October 2026, on the bus interchange's blank glowing board: "你这电子灯牌没用啊";
// then "实时信息，由 Bus Driver 绘制"): square-dot LED screens in the high-density signs' faces (DotFont: 32, 19 and 13 rows; Unifont for a missing
// glyph), laid out by the screen's layout of the station-sign file (StationSignProfile):
//   bay        one bay's lines, a page each: the label in the line's colour, the terminus (Chinese above, English under, scrolling when long), the
//              nearest bus's status at the right (at the stop, arriving, n stops away, no bus);
//   pylon      a narrow column at the bay: two lines a page, each its label over its status;
//   departures every bay's lines in rows (bay, line, terminus, waiting, status) under a header, pages of as many rows as fit; nothing scrolls (a big
//              board is written once a change).
// Pure: no Unity types; built and tested offline. The content comes from BusStationBoardSystem (the game's lines, buses and stops).
namespace CDLC.BusDriver.Interior
{
    /// <summary>The nearest bus of a line to a bay.</summary>
    internal enum BoardStatus : byte { NoBus, Stops, Arriving, AtStop }

    /// <summary>The inks of a station board (a material each): the text, the headers and English, arriving and at the stop, no bus; then the lines' colours.</summary>
    internal static class BoardInk
    {
        internal const byte Text = 0, Accent = 1, Good = 2, Dim = 3, Line0 = 4, Lines = 12, Count = Line0 + Lines;
    }

    /// <summary>A line at a bay as a board shows it.</summary>
    internal struct BoardRoute
    {
        /// <summary>The line's label (its prefix and number: "52", "K3", "夜12") in its colour's ink, then the suffix ("路" in Chinese) in the text's.</summary>
        internal string Label, Suffix;
        internal byte LineInk;
        internal string Terminus, English;
        internal BoardStatus Status;
        /// <summary>Stops between the nearest bus and the bay (Status Stops).</summary>
        internal int Stops;
        /// <summary>The people waiting at the bay for this line (the game counts them per line).</summary>
        internal int Waiting;
    }

    /// <summary>A bay and its lines (in the order the boards show them: StationBoardRules.Sort); Waiting: for all of them.</summary>
    internal sealed class BoardBay
    {
        internal int Bay, Waiting;
        internal readonly List<BoardRoute> Routes = new List<BoardRoute>(4);
    }

    /// <summary>The words of the boards in the game's language (BusLocale): the status texts, the headers, a bay without lines.</summary>
    internal struct BoardTexts
    {
        internal string AtStop, Arriving, StopsOne, StopsMany, NoBus, NoLines, NoLinesEnglish;
        internal string AtStopEnglish, ArrivingEnglish, NoBusEnglish;
        internal string HeadBay, HeadLine, HeadTo, HeadWaiting, HeadStatus;

        /// <summary>The status of a line ("{0}" in the stops' texts: the number).</summary>
        internal string Status(in BoardRoute r)
        {
            switch (r.Status)
            {
                case BoardStatus.AtStop: return AtStop ?? "";
                case BoardStatus.Arriving: return Arriving ?? "";
                case BoardStatus.Stops: return string.Format(r.Stops == 1 ? StopsOne ?? "{0}" : StopsMany ?? "{0}", r.Stops);
                default: return NoBus ?? "";
            }
        }

        /// <summary>The status's English line under a Chinese one ("" when the status is in English already).</summary>
        internal string StatusEnglish(in BoardRoute r)
        {
            string text = Status(r);
            if (!StopEnglish.HasCjk(text))
                return "";
            switch (r.Status)
            {
                case BoardStatus.AtStop: return AtStopEnglish ?? "";
                case BoardStatus.Arriving: return ArrivingEnglish ?? "";
                case BoardStatus.Stops: return r.Stops == 1 ? "1 stop" : r.Stops + " stops";
                default: return NoBusEnglish ?? "";
            }
        }

        internal static byte StatusInk(BoardStatus s) => s == BoardStatus.AtStop || s == BoardStatus.Arriving ? BoardInk.Good : s == BoardStatus.NoBus ? BoardInk.Dim : BoardInk.Text;

        internal string Key()
        {
            var sb = new StringBuilder(128);
            foreach (string s in new[] { AtStop, Arriving, StopsOne, StopsMany, NoBus, NoLines, NoLinesEnglish, AtStopEnglish, ArrivingEnglish, NoBusEnglish, HeadBay, HeadLine,
                         HeadTo, HeadWaiting, HeadStatus })
                sb.Append(s).Append('\u0001');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Lays a station board out in a dot buffer (x right, y down, true = lit, an ink each): Compose decides a page's texts and places, Render draws it
    /// at a scroll position. The buffers are reused; a Compose allocates its texts' list only.
    /// </summary>
    internal sealed class StationBoardComposer
    {
        internal const int Margin = 2, Gap = 4;
        // a scrolling text first stands at the left of its area this many dots of the scroll (as the signs: HdComposer.HoldDots)
        internal const int HoldDots = HdComposer.HoldDots;
        internal const int PylonBlock = 42, DepartureRow = 22;

        readonly DotFont m_Font;
        readonly LedFont m_Fallback;
        readonly StationSignProfile.Layout m_Layout;
        readonly int m_Width, m_Height;
        readonly bool[,] m_Buffer;
        readonly byte[,] m_Ink;

        struct Item
        {
            internal string Text;
            internal int Face, X, Top, MinX, MaxX, Width;
            internal byte Ink;
            internal bool Latin, Scrolls;
        }

        readonly List<Item> m_Items = new List<Item>(64);
        readonly List<(int x0, int x1, int y, byte ink)> m_Rules = new List<(int, int, int, byte)>(2);
        int m_Pages = 1, m_Cycle = 1;
        bool m_Scrolls;

        internal StationBoardComposer(DotFont font, LedFont fallback, StationSignProfile.Layout layout, int width, int height)
        {
            m_Font = font;
            m_Fallback = fallback;
            m_Layout = layout;
            m_Width = Math.Max(8, width);
            m_Height = Math.Max(8, height);
            m_Buffer = new bool[m_Width, m_Height];
            m_Ink = new byte[m_Width, m_Height];
        }

        internal int Width => m_Width;
        internal int Height => m_Height;
        internal bool[,] Buffer => m_Buffer;
        internal byte[,] Ink => m_Ink;
        /// <summary>The pages the composed content needs (the last Compose's).</summary>
        internal int Pages => m_Pages;
        internal bool Scrolls => m_Scrolls;
        internal int Cycle => m_Cycle;
        /// <summary>The texts of the composed page (for the tests).</summary>
        internal IEnumerable<string> Texts
        {
            get
            {
                foreach (Item i in m_Items)
                    yield return i.Text;
            }
        }

        /// <summary>How many lines (rows of the departures board, blocks of a pylon) a page holds.</summary>
        internal int PerPage => m_Layout == StationSignProfile.Layout.Pylon ? Math.Max(1, m_Height / PylonBlock) :
            m_Layout == StationSignProfile.Layout.Departures ? Math.Max(1, (m_Height - DepartureRow) / DepartureRow) : 1;

        // ---------------------------------------------------------------- glyphs (as HdComposer's, on this buffer's size)

        static bool AnyCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (DotFont.IsCjk(DotFont.CodePoint(s, ref i)))
                    return true;
            return false;
        }

        int Glyph(uint cp, int face, bool latin, int x, int top, int minX, int maxX, byte ink, bool draw)
        {
            DotFont.Face f = m_Font[face];
            if (cp == ' ' || cp == 0xA0)
                return Math.Max(f.TryGet(' ', out DotFont.Glyph sp) ? sp.Advance : 0, f.Rows / 3);
            if (f.TryGet(cp, out DotFont.Glyph g))
            {
                if (draw)
                    f.Render(cp, m_Buffer, m_Ink, ink, x, top + (latin ? f.LatinBaseline - f.Baseline : 0), minX, maxX, top, top + f.Rows - 1);
                return g.Advance;
            }

            if (face == DotFont.Label)
                return Glyph(cp, DotFont.Big, latin, x, top, minX, maxX, ink, draw);
            if (m_Fallback == null)
                return f.Rows / 2;
            int scale = f.Rows >= 2 * LedFont.Rows ? 2 : 1;
            int w = m_Fallback.Width(cp);
            if (draw)
            {
                int y0 = top + (f.Rows - LedFont.Rows * scale) / 2;
                int lo = Math.Max(minX, 0), hi = Math.Min(maxX, m_Width - 1), ylo = Math.Max(top, 0), yhi = Math.Min(top + f.Rows - 1, m_Height - 1);
                for (int r = 0; r < LedFont.Rows; r++)
                    for (int c = 0; c < w; c++)
                    {
                        if (!m_Fallback.Dot(cp, c, r))
                            continue;
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int px = x + c * scale + sx, py = y0 + r * scale + sy;
                                if (px < lo || px > hi || py < ylo || py > yhi)
                                    continue;
                                m_Buffer[px, py] = true;
                                m_Ink[px, py] = ink;
                            }
                    }
            }

            return w * scale;
        }

        int Run(string text, int face, bool latin, int x, int top, int minX, int maxX, byte ink, bool draw)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            int start = x, gap = face == DotFont.Small ? 0 : HdComposer.Spacing;
            for (int i = 0; i < text.Length; i++)
            {
                uint cp = DotFont.CodePoint(text, ref i);
                bool visible = draw && x <= maxX && x + 2 * 32 >= minX;
                x += Glyph(cp, face, latin, x, top, minX, maxX, ink, visible) + gap;
            }

            return x - gap - start;
        }

        /// <summary>The width of a text in a face.</summary>
        internal int Measure(string text, int face) => Run(text, face, !AnyCjk(text), 0, 0, 0, -1, 0, false);

        // ---------------------------------------------------------------- the items

        /// <summary>A text in the columns minX..maxX: at x (align -1 left, 0 centred, +1 right of the area), scrolling when it does not fit and scroll.</summary>
        int Add(string text, int face, int top, int minX, int maxX, int align, byte ink, bool scroll)
        {
            if (string.IsNullOrEmpty(text) || maxX < minX)
                return 0;
            bool latin = !AnyCjk(text);
            int width = Run(text, face, latin, 0, 0, 0, -1, 0, false), area = maxX - minX + 1;
            var item = new Item { Text = text, Face = face, Top = top, MinX = minX, MaxX = maxX, Width = width, Ink = ink, Latin = latin };
            if (width > area && scroll)
            {
                item.Scrolls = true;
                item.X = minX;
            }
            else
                item.X = width > area || align < 0 ? minX : align > 0 ? maxX + 1 - width : minX + (area - width) / 2;
            m_Items.Add(item);
            return Math.Min(width, area);
        }

        /// <summary>A text in the medium face, or in the small one (centred on the medium cell) when it is Latin and does not fit the medium.</summary>
        int AddFitting(string text, int top, int minX, int maxX, int align, byte ink, bool scroll)
        {
            if (!AnyCjk(text) && Measure(text, DotFont.Medium) > maxX - minX + 1)
                return Add(text, DotFont.Small, top + (m_Font[DotFont.Medium].Rows - m_Font[DotFont.Small].Rows) / 2, minX, maxX, align, ink, scroll);
            return Add(text, DotFont.Medium, top, minX, maxX, align, ink, scroll);
        }

        /// <summary>The label (in the line's ink) and its suffix (the text's), from x; returns the right edge.</summary>
        int AddLabel(in BoardRoute r, int face, int top, int x, int maxX)
        {
            int w = Add(r.Label, face, top, x, maxX, -1, r.LineInk, false);
            if (w > 0 && !string.IsNullOrEmpty(r.Suffix))
                w += HdComposer.Spacing + Add(r.Suffix, face == DotFont.Label ? DotFont.Big : face, top, x + w + HdComposer.Spacing, maxX, -1, BoardInk.Text, false);
            return x + w;
        }

        // ---------------------------------------------------------------- the layouts

        /// <summary>Decides a page of the content (the page wraps round the pages it needs: Pages).</summary>
        internal void Compose(IReadOnlyList<BoardBay> bays, in BoardTexts texts, int page)
        {
            m_Items.Clear();
            m_Rules.Clear();
            m_Pages = 1;
            switch (m_Layout)
            {
                case StationSignProfile.Layout.Bay: ComposeBay(bays.Count > 0 ? bays[0] : null, texts, page); break;
                case StationSignProfile.Layout.Pylon: ComposePylon(bays.Count > 0 ? bays[0] : null, texts, page); break;
                default: ComposeDepartures(bays, texts, page); break;
            }

            m_Scrolls = false;
            m_Cycle = 1;
            foreach (Item i in m_Items)
            {
                if (!i.Scrolls)
                    continue;
                m_Scrolls = true;
                m_Cycle = Math.Max(m_Cycle, HoldDots + i.Width + (i.MaxX - i.MinX + 1));
            }
        }

        void NoLines(in BoardTexts texts, int top, int bottom)
        {
            int h = m_Font[DotFont.Medium].Rows + (string.IsNullOrEmpty(texts.NoLinesEnglish) ? 0 : 2 + m_Font[DotFont.Small].Rows);
            int y = top + Math.Max(0, (bottom - top - h) / 2);
            Add(texts.NoLines, DotFont.Medium, y, Margin, m_Width - 1 - Margin, 0, BoardInk.Dim, true);
            if (!string.IsNullOrEmpty(texts.NoLinesEnglish) && StopEnglish.HasCjk(texts.NoLines))
                Add(texts.NoLinesEnglish, DotFont.Small, y + m_Font[DotFont.Medium].Rows + 2, Margin, m_Width - 1 - Margin, 0, BoardInk.Accent, true);
        }

        void ComposeBay(BoardBay bay, in BoardTexts texts, int page)
        {
            if (bay == null || bay.Routes.Count == 0)
            {
                NoLines(texts, 0, m_Height);
                return;
            }

            m_Pages = bay.Routes.Count;
            BoardRoute r = bay.Routes[((page % m_Pages) + m_Pages) % m_Pages];
            int right = m_Width - 1 - Margin;
            int medium = m_Font[DotFont.Medium].Rows;
            // the status at the right: its text over its English, both right-aligned, at most a third of the board
            string status = texts.Status(r), english = texts.StatusEnglish(r);
            int sw = Math.Min(m_Width / 3, Math.Max(Measure(status, DotFont.Medium), Measure(english, DotFont.Small)));
            byte ink = BoardTexts.StatusInk(r.Status);
            if (english.Length > 0)
            {
                Add(status, DotFont.Medium, 0, right + 1 - sw, right, 1, ink, true);
                Add(english, DotFont.Small, medium, right + 1 - sw, right, 1, BoardInk.Accent, true);
            }
            else
                AddFitting(status, (m_Height - medium) / 2, right + 1 - sw, right, 1, ink, true);

            // the label at the left: as tall as the board (digits and capitals), in the medium face when that would take more than a third
            int labelFace = DotFont.Label, labelTop = 0;
            string labelText = (r.Label ?? "") + (r.Suffix ?? "");
            if (Measure(labelText, DotFont.Label) > m_Width / 3 || m_Height < m_Font[DotFont.Label].Rows)
            {
                labelFace = DotFont.Medium;
                labelTop = (m_Height - medium) / 2;
            }

            int areaStart = AddLabel(r, labelFace, labelTop, Margin, right - sw - Gap) + Gap * 2;
            int areaEnd = right - sw - Gap * 2;
            string terminus = r.Terminus ?? "";
            if (!string.IsNullOrEmpty(r.English))
            {
                Add(terminus, DotFont.Medium, 0, areaStart, areaEnd, 0, BoardInk.Text, true);
                Add(r.English, DotFont.Small, medium, areaStart, areaEnd, 0, BoardInk.Accent, true);
            }
            else
                AddFitting(terminus, (m_Height - medium) / 2, areaStart, areaEnd, 0, BoardInk.Text, true);
        }

        void ComposePylon(BoardBay bay, in BoardTexts texts, int page)
        {
            if (bay == null || bay.Routes.Count == 0)
            {
                NoLines(texts, 0, m_Height);
                return;
            }

            int per = PerPage;
            m_Pages = (bay.Routes.Count + per - 1) / per;
            int first = (((page % m_Pages) + m_Pages) % m_Pages) * per;
            int medium = m_Font[DotFont.Medium].Rows, right = m_Width - 1 - Margin;
            int top = Math.Max(0, (m_Height - per * PylonBlock) / 2);
            for (int k = 0; k < per && first + k < bay.Routes.Count; k++)
            {
                BoardRoute r = bay.Routes[first + k];
                int y = top + k * PylonBlock;
                AddLabel(r, DotFont.Medium, y, Margin, right);
                AddFitting(texts.Status(r), y + medium + 2, Margin, right, -1, BoardTexts.StatusInk(r.Status), true);
            }
        }

        void ComposeDepartures(IReadOnlyList<BoardBay> bays, in BoardTexts texts, int page)
        {
            int medium = m_Font[DotFont.Medium].Rows, right = m_Width - 1 - Margin;
            // the columns: the bay, the line, the terminus (the rest), the waiting, the status
            int bayW = Math.Max(Measure(texts.HeadBay, DotFont.Medium), Measure("99", DotFont.Medium));
            int widest = 0;
            foreach (BoardBay b in bays)
                foreach (BoardRoute r in b.Routes)
                    widest = Math.Max(widest, Measure(r.Label ?? "", DotFont.Medium) +
                        (string.IsNullOrEmpty(r.Suffix) ? 0 : HdComposer.Spacing + Measure(r.Suffix, DotFont.Medium)));
            int lineW = Math.Max(Math.Max(Measure(texts.HeadLine, DotFont.Medium), m_Width / 7), Math.Min(widest, m_Width / 4));
            int waitW = Math.Max(Measure(texts.HeadWaiting, DotFont.Medium), Measure("999", DotFont.Medium));
            int statusW = Math.Max(Measure(texts.HeadStatus, DotFont.Medium), m_Width / 6);
            int xBay = Margin, xLine = xBay + bayW + Gap * 3, xTo = xLine + lineW + Gap * 3;
            int xStatus = right + 1 - statusW, xWait = xStatus - Gap * 3 - waitW, toEnd = xWait - Gap * 3 - 1;
            if (toEnd - xTo < m_Width / 6)
            {
                // a narrow board: no waiting column
                xWait = -1;
                toEnd = xStatus - Gap * 3 - 1;
            }

            Add(texts.HeadBay, DotFont.Medium, 0, xBay, xBay + bayW - 1, -1, BoardInk.Accent, false);
            Add(texts.HeadLine, DotFont.Medium, 0, xLine, xLine + lineW - 1, -1, BoardInk.Accent, false);
            Add(texts.HeadTo, DotFont.Medium, 0, xTo, toEnd, -1, BoardInk.Accent, false);
            if (xWait >= 0)
                Add(texts.HeadWaiting, DotFont.Medium, 0, xWait, xWait + waitW - 1, 1, BoardInk.Accent, false);
            Add(texts.HeadStatus, DotFont.Medium, 0, xStatus, right, -1, BoardInk.Accent, false);
            m_Rules.Add((Margin, right, medium + 1, BoardInk.Dim));

            var rows = new List<(BoardBay bay, BoardRoute route)>();
            foreach (BoardBay b in bays)
                foreach (BoardRoute r in b.Routes)
                    rows.Add((b, r));
            if (rows.Count == 0)
            {
                NoLines(texts, DepartureRow, m_Height);
                return;
            }

            int per = PerPage;
            m_Pages = (rows.Count + per - 1) / per;
            int p = ((page % m_Pages) + m_Pages) % m_Pages, first = p * per;
            if (m_Pages > 1)
            {
                string pages = (p + 1) + "/" + m_Pages;
                Add(pages, DotFont.Small, (medium - m_Font[DotFont.Small].Rows) / 2, toEnd - Measure(pages, DotFont.Small), toEnd, 1, BoardInk.Dim, false);
            }

            for (int k = 0; k < per && first + k < rows.Count; k++)
            {
                var (b, r) = rows[first + k];
                int y = DepartureRow * (k + 1) + 1;
                Add(b.Bay.ToString(), DotFont.Medium, y, xBay, xBay + bayW - 1, -1, BoardInk.Text, false);
                AddLabel(r, DotFont.Medium, y, xLine, xLine + lineW - 1);
                Add(r.Terminus ?? "", DotFont.Medium, y, xTo, toEnd, -1, BoardInk.Text, false);
                if (xWait >= 0)
                    Add(r.Waiting.ToString(), DotFont.Medium, y, xWait, xWait + waitW - 1, 1, BoardInk.Text, false);
                AddFitting(texts.Status(r), y, xStatus, right, -1, BoardTexts.StatusInk(r.Status), false);
            }
        }

        // ---------------------------------------------------------------- drawing

        internal void Clear()
        {
            Array.Clear(m_Buffer, 0, m_Buffer.Length);
            Array.Clear(m_Ink, 0, m_Ink.Length);
        }

        /// <summary>Draws the composed page; a scrolling text at this offset (standing at the left of its area for HoldDots, then going out on the left
        /// and coming in again from its area's right edge).</summary>
        internal void Render(int offset)
        {
            Clear();
            foreach (var (x0, x1, y, ink) in m_Rules)
                for (int x = x0; x <= x1; x += 2)
                    if (x >= 0 && x < m_Width && y >= 0 && y < m_Height)
                    {
                        m_Buffer[x, y] = true;
                        m_Ink[x, y] = ink;
                    }

            foreach (Item i in m_Items)
            {
                int x = i.X;
                if (i.Scrolls)
                {
                    // out on the left (Width dots), in again from the right edge (the area's width), then standing until the cycle starts again
                    int area = i.MaxX - i.MinX + 1;
                    int moved = Math.Max(0, (((offset % m_Cycle) + m_Cycle) % m_Cycle) - HoldDots);
                    if (moved >= i.Width + area)
                        moved = 0;
                    x = moved <= i.Width ? i.MinX - moved : i.MaxX + 1 - (moved - i.Width);
                }

                Run(i.Text, i.Face, i.Latin, x, i.Top, i.MinX, i.MaxX, i.Ink, true);
            }
        }
    }
}
