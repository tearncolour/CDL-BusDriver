using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

// The printed destination boards ("card" signs, a vehicle profile's signs.*.style "card"): the 1990s white board with the route
// number in a bold sans and the termini in a Song-style face, both red, both fixed (no scrolling). The glyphs are rasterised
// offline (Tools/CardFont/make_card_font.py, Fonts/card-font.bin.gz: Noto Sans/Serif CJK SC Bold, SIL OFL 1.1); a code point
// neither face has is drawn from the LED font's Unifont glyph, scaled. Pure: no Unity types; built and tested offline.
namespace CDLC.BusDriver.Interior
{
    /// <summary>The baked faces of Fonts/card-font.bin.gz (format: Tools/CardFont/make_card_font.py). Read-only after Load.</summary>
    internal sealed class CardFont
    {
        internal const int Label = 0, Text = 1;

        internal sealed class Glyph
        {
            internal int Advance, Left, Top, Width, Height;
            internal byte[] Coverage;      // Width * Height values 0..15, row-major from the top row
        }

        internal sealed class Face
        {
            internal int Em, Ascent, Descent;
            internal readonly Dictionary<uint, Glyph> Glyphs = new Dictionary<uint, Glyph>();
        }

        readonly List<Face> m_Faces = new List<Face>();

        internal int FaceCount => m_Faces.Count;
        internal Face this[int face] => m_Faces[face];

        internal int Count
        {
            get
            {
                int n = 0;
                foreach (Face f in m_Faces) n += f.Glyphs.Count;
                return n;
            }
        }

        /// <summary>Loads the packed faces; false (and nothing loaded) when the file is missing, damaged or of another version.</summary>
        internal bool Load(string path)
        {
            m_Faces.Clear();
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new BinaryReader(gz))
                {
                    if (reader.ReadByte() != 'C' || reader.ReadByte() != 'D' || reader.ReadByte() != 'C' || reader.ReadByte() != 'F' || reader.ReadByte() != 1)
                        return false;
                    int faces = reader.ReadByte();
                    if (faces < 2 || faces > 8)
                        return false;
                    for (int f = 0; f < faces; f++)
                    {
                        var face = new Face { Em = reader.ReadUInt16(), Ascent = reader.ReadInt16(), Descent = reader.ReadInt16() };
                        uint count = reader.ReadUInt32();
                        if (face.Em < 8 || face.Em > 255 || count > 100000)
                            return Fail();
                        for (uint i = 0; i < count; i++)
                        {
                            uint cp = reader.ReadUInt32();
                            var g = new Glyph { Advance = reader.ReadByte(), Left = reader.ReadSByte(), Top = reader.ReadSByte(), Width = reader.ReadByte(), Height = reader.ReadByte() };
                            int n = g.Width * g.Height;
                            byte[] packed = reader.ReadBytes((n + 1) / 2);
                            if (packed.Length != (n + 1) / 2)
                                return Fail();
                            g.Coverage = new byte[n];
                            for (int k = 0; k < n; k++)
                                g.Coverage[k] = (byte)((k & 1) == 0 ? packed[k >> 1] >> 4 : packed[k >> 1] & 15);
                            face.Glyphs[cp] = g;
                        }

                        m_Faces.Add(face);
                    }
                }

                return m_Faces.Count >= 2 && m_Faces[Label].Glyphs.Count > 0 && m_Faces[Text].Glyphs.Count > 0;
            }
            catch (Exception)
            {
                return Fail();
            }
        }

        bool Fail()
        {
            m_Faces.Clear();
            return false;
        }

        internal bool Has(int face, uint cp) => face < m_Faces.Count && m_Faces[face].Glyphs.ContainsKey(cp);

        /// <summary>The glyph of a code point in the preferred face, else the other face; null when neither has it.</summary>
        internal Glyph Find(int preferred, uint cp, out int face)
        {
            face = preferred;
            if (preferred < m_Faces.Count && m_Faces[preferred].Glyphs.TryGetValue(cp, out Glyph g))
                return g;
            for (int f = 0; f < m_Faces.Count; f++)
            {
                if (f != preferred && m_Faces[f].Glyphs.TryGetValue(cp, out g))
                {
                    face = f;
                    return g;
                }
            }

            return null;
        }
    }

    /// <summary>A colour of the board (sRGB bytes).</summary>
    internal readonly struct CardRgb : IEquatable<CardRgb>
    {
        internal readonly byte R, G, B;
        internal CardRgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public bool Equals(CardRgb o) => R == o.R && G == o.G && B == o.B;
        public override bool Equals(object obj) => obj is CardRgb o && Equals(o);
        public override int GetHashCode() => (R << 16) | (G << 8) | B;

        /// <summary>The boards of the 1990s: a warm white board, red paint (photo xhs_6).</summary>
        internal static readonly CardRgb DefaultBoard = new CardRgb(242, 240, 233), DefaultInk = new CardRgb(200, 30, 38);
    }

    /// <summary>
    /// Lays a printed board out in an RGBA picture (row 0 the top): the route label (the prefix in its own colour, then the number;
    /// the label face, big) and the text (the text face), the two centred together; a text too long for the board is drawn narrower
    /// first (down to <see cref="MinSqueeze"/> of its width, as a sign painter squeezes the characters), then the whole is drawn
    /// smaller. Without a text the label alone is centred and bigger; without a label the text alone. The buffer is reused.
    /// </summary>
    internal sealed class CardComposer
    {
        internal const float LabelHeight = 0.74f, LabelAloneHeight = 0.80f, TextHeight = 0.58f, TextAloneHeight = 0.62f, Gap = 0.30f;
        internal const float MinSqueeze = 0.55f;
        const uint kHeightProbe = 0x56FD;  // 国: the CJK ink height of the text face
        const uint kDigitProbe = '0';
        /// <summary>↔ is drawn as the boards' bold double arrow (photo xhs_6), not from the font: its advance, length, height and shaft (em).</summary>
        internal const uint ArrowCodePoint = 0x2194;
        const float kArrowAdvance = 1.15f, kArrowLength = 1.0f, kArrowHeight = 0.46f, kArrowShaft = 0.17f, kArrowHead = 0.32f;

        readonly CardFont m_Font;
        readonly LedFont m_Fallback;
        internal readonly int Width, Height;
        internal readonly byte[] Pixels;

        /// <summary>Of the last layout: the label's and the text's scale (picture px per glyph px) and the text's squeeze.</summary>
        internal float LabelScale, TextScale, Squeeze;

        /// <summary>Of the last layout: the drawn ink's left and right picture columns (-1 when nothing was drawn).</summary>
        internal int InkLeft, InkRight;

        /// <summary>Code points drawn from the LED font's Unifont in the last layout (neither face had them).</summary>
        internal int FallbackGlyphs;

        internal CardComposer(CardFont font, LedFont fallback, int width, int height)
        {
            m_Font = font ?? throw new ArgumentNullException(nameof(font));
            m_Fallback = fallback;
            Width = Math.Max(8, width);
            Height = Math.Max(8, height);
            Pixels = new byte[Width * Height * 4];
        }

        struct Run
        {
            internal string Text;
            internal int Face;
            internal CardRgb Ink;
        }

        /// <summary>Draws the board. Returns false when there is nothing to show (all three texts empty): the board is left blank.</summary>
        internal bool Render(string prefix, string number, string text, CardRgb board, CardRgb ink, CardRgb prefixInk)
        {
            prefix = prefix ?? "";
            number = number ?? "";
            text = text ?? "";
            for (int i = 0; i < Pixels.Length; i += 4)
            {
                Pixels[i] = board.R;
                Pixels[i + 1] = board.G;
                Pixels[i + 2] = board.B;
                Pixels[i + 3] = 255;
            }

            InkLeft = InkRight = -1;
            FallbackGlyphs = 0;
            LabelScale = TextScale = 0f;
            Squeeze = 1f;
            bool hasLabel = prefix.Length + number.Length > 0, hasText = text.Trim().Length > 0;
            if (!hasLabel && !hasText)
                return false;

            CardFont.Face labelFace = m_Font[CardFont.Label], textFace = m_Font[CardFont.Text];
            float digitH = ProbeHeight(labelFace, kDigitProbe, 0.72f);
            float cjkH = ProbeHeight(textFace, kHeightProbe, 0.88f);
            float H = Height;
            float labelScale = (hasText ? LabelHeight : LabelAloneHeight) * H / digitH;
            float textScale = (hasLabel ? TextHeight : TextAloneHeight) * H / cjkH;
            var labelRuns = new List<Run>(2);
            if (prefix.Length > 0) labelRuns.Add(new Run { Text = prefix, Face = CardFont.Label, Ink = prefixInk });
            if (number.Length > 0) labelRuns.Add(new Run { Text = number, Face = CardFont.Label, Ink = ink });
            var textRun = new Run { Text = text.Trim(), Face = CardFont.Text, Ink = ink };

            float labelW = 0f;
            foreach (Run r in labelRuns) labelW += Measure(r.Text, r.Face) * labelScale;
            float textW = hasText ? Measure(textRun.Text, CardFont.Text) * textScale : 0f;
            float gap = hasLabel && hasText ? Gap * H : 0f;
            float margin = Math.Max(0.03f * Width, 0.22f * H);
            float avail = Width - 2f * margin;
            float squeeze = 1f, uniform = 1f;
            if (labelW + gap + textW > avail && textW > 0f)
                squeeze = Math.Max(MinSqueeze, (avail - labelW - gap) / textW);
            float total = labelW + gap + textW * squeeze;
            if (total > avail)
                uniform = avail / total;
            labelScale *= uniform;
            textScale *= uniform;
            gap *= uniform;
            labelW *= uniform;
            textW *= uniform;
            LabelScale = labelScale;
            TextScale = textScale;
            Squeeze = squeeze;

            float x = 0.5f * (Width - (labelW + gap + textW * squeeze));
            float labelBaseline = 0.5f * H + labelScale * 0.5f * digitH;
            foreach (Run r in labelRuns)
                x = DrawRun(r, x, labelBaseline, labelScale, 1f);
            x += gap;
            if (hasText)
            {
                CardFont.Glyph probe = textFace.Glyphs.TryGetValue(kHeightProbe, out CardFont.Glyph p) ? p : null;
                float centreAbove = probe != null ? probe.Top - 0.5f * probe.Height : 0.5f * cjkH - 0.07f * textFace.Em;
                float textBaseline = 0.5f * H + textScale * centreAbove;
                DrawRun(textRun, x, textBaseline, textScale, squeeze);
            }

            return true;
        }

        static float ProbeHeight(CardFont.Face face, uint cp, float emShare) =>
            face.Glyphs.TryGetValue(cp, out CardFont.Glyph g) && g.Height > 0 ? g.Height : emShare * face.Em;

        /// <summary>A text's advance in glyph px of the face (the fallback's glyphs scaled to the face's em).</summary>
        internal float Measure(string text, int face)
        {
            float w = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                uint cp = CodePoint(text, ref i);
                if (cp == ArrowCodePoint)
                {
                    w += kArrowAdvance * m_Font[face].Em;
                    continue;
                }

                CardFont.Glyph g = m_Font.Find(face, cp, out int found);
                if (g != null)
                    w += g.Advance * (float)m_Font[face].Em / m_Font[found].Em;
                else
                    w += FallbackAdvance(cp) * m_Font[face].Em / 16f;
            }

            return w;
        }

        float FallbackAdvance(uint cp) => m_Fallback != null ? m_Fallback.Width(cp) : 8f;

        static uint CodePoint(string s, ref int i)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                uint cp = (uint)char.ConvertToUtf32(c, s[i + 1]);
                i++;
                return cp;
            }

            return c;
        }

        float DrawRun(Run run, float x, float baseline, float scale, float squeeze)
        {
            int em = m_Font[run.Face].Em;
            for (int i = 0; i < run.Text.Length; i++)
            {
                uint cp = CodePoint(run.Text, ref i);
                if (cp == ArrowCodePoint)
                {
                    DrawArrow(x, baseline, scale * em, squeeze, run.Ink);
                    x += kArrowAdvance * em * scale * squeeze;
                    continue;
                }

                CardFont.Glyph g = m_Font.Find(run.Face, cp, out int found);
                if (g == null)
                {
                    g = FallbackGlyph(cp);
                    if (g == null)
                    {
                        x += 8f * em / 16f * scale * squeeze;
                        continue;
                    }

                    FallbackGlyphs++;
                    float k = em / 16f;            // Unifont's 16 dots to the face's em
                    Blit(g, x, baseline, scale * k, squeeze, run.Ink);
                    x += g.Advance * scale * k * squeeze;
                    continue;
                }

                float fk = (float)em / m_Font[found].Em;
                Blit(g, x, baseline, scale * fk, squeeze, run.Ink);
                x += g.Advance * scale * fk * squeeze;
            }

            return x;
        }

        /// <summary>A Unifont glyph as a 16-row coverage glyph (baseline 2 rows above its bottom), or null without the LED font.</summary>
        CardFont.Glyph FallbackGlyph(uint cp)
        {
            if (m_Fallback == null)
                return null;
            int w = m_Fallback.Width(cp);
            var bits = new bool[w, LedFont.Rows];
            m_Fallback.Render(cp, bits, 0, 0);
            var g = new CardFont.Glyph { Advance = w, Left = 0, Top = LedFont.Rows - 2, Width = w, Height = LedFont.Rows, Coverage = new byte[w * LedFont.Rows] };
            for (int y = 0; y < LedFont.Rows; y++)
                for (int xx = 0; xx < w; xx++)
                    g.Coverage[y * w + xx] = bits[xx, y] ? (byte)15 : (byte)0;
            return g;
        }

        /// <summary>
        /// Draws one glyph scaled: its pen at x, its baseline at baseline (picture px), sx = scale * squeeze across, scale down. Each
        /// picture pixel samples the coverage bilinearly (n x n samples when the glyph is drawn smaller); drawn bigger, the coverage
        /// is sharpened (a smoothstep across its middle), so the upscaled edges stay crisp instead of blurred.
        /// </summary>
        void Blit(CardFont.Glyph g, float x, float baseline, float scale, float squeeze, CardRgb ink)
        {
            if (g.Width == 0 || g.Height == 0)
                return;
            float sx = scale * squeeze, sy = scale;
            float left = x + g.Left * sx, top = baseline - g.Top * sy;
            int x0 = Math.Max(0, (int)Math.Floor(left)), x1 = Math.Min(Width - 1, (int)Math.Ceiling(left + g.Width * sx));
            int y0 = Math.Max(0, (int)Math.Floor(top)), y1 = Math.Min(Height - 1, (int)Math.Ceiling(top + g.Height * sy));
            int nx = Math.Max(1, Math.Min(4, (int)Math.Ceiling(1f / sx))), ny = Math.Max(1, Math.Min(4, (int)Math.Ceiling(1f / sy)));
            bool sharpen = Math.Min(sx, sy) > 1.3f;
            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    float sum = 0f;
                    for (int j = 0; j < ny; j++)
                    {
                        float v = (py + (j + 0.5f) / ny - top) / sy - 0.5f;
                        for (int i = 0; i < nx; i++)
                        {
                            float u = (px + (i + 0.5f) / nx - left) / sx - 0.5f;
                            sum += Sample(g, u, v);
                        }
                    }

                    float a = sum / (nx * ny);
                    if (sharpen)
                    {
                        float t = Math.Max(0f, Math.Min(1f, (a - 0.22f) / 0.56f));
                        a = t * t * (3f - 2f * t);
                    }

                    if (a <= 0.004f)
                        continue;
                    int o = (py * Width + px) * 4;
                    // the darker of what is there and the ink at this coverage (overlapping glyphs keep their ink)
                    Pixels[o] = Math.Min(Pixels[o], Mix(Pixels[o], ink.R, a));
                    Pixels[o + 1] = Math.Min(Pixels[o + 1], Mix(Pixels[o + 1], ink.G, a));
                    Pixels[o + 2] = Math.Min(Pixels[o + 2], Mix(Pixels[o + 2], ink.B, a));
                    if (InkLeft < 0 || px < InkLeft) InkLeft = px;
                    if (px > InkRight) InkRight = px;
                }
            }
        }

        /// <summary>
        /// The double arrow: a shaft and two triangular heads, centred on the text's ink centre (0.37 em above the baseline), drawn
        /// with 4 x 4 samples a pixel. emPx: picture px per em; squeeze narrows it with the text.
        /// </summary>
        void DrawArrow(float x, float baseline, float emPx, float squeeze, CardRgb ink)
        {
            float ex = emPx * squeeze;
            float x0 = x + 0.5f * (kArrowAdvance - kArrowLength) * ex, x1 = x0 + kArrowLength * ex;
            float cy = baseline - 0.37f * emPx, hh = 0.5f * kArrowHeight * emPx, hs = 0.5f * kArrowShaft * emPx, head = kArrowHead * ex;
            int px0 = Math.Max(0, (int)Math.Floor(x0)), px1 = Math.Min(Width - 1, (int)Math.Ceiling(x1));
            int py0 = Math.Max(0, (int)Math.Floor(cy - hh)), py1 = Math.Min(Height - 1, (int)Math.Ceiling(cy + hh));
            for (int py = py0; py <= py1; py++)
            {
                for (int px = px0; px <= px1; px++)
                {
                    int hit = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        float yy = Math.Abs(py + (j + 0.5f) / 4f - cy);
                        for (int i = 0; i < 4; i++)
                        {
                            float xx = px + (i + 0.5f) / 4f;
                            float d = Math.Min(xx - x0, x1 - xx);               // distance from the nearer tip
                            if (d < 0f) continue;
                            bool inHead = d <= head && yy <= hh * d / head;
                            bool inShaft = d > 0.6f * head && yy <= hs;
                            if (inHead || inShaft) hit++;
                        }
                    }

                    if (hit == 0) continue;
                    float a = hit / 16f;
                    int o = (py * Width + px) * 4;
                    Pixels[o] = Math.Min(Pixels[o], Mix(Pixels[o], ink.R, a));
                    Pixels[o + 1] = Math.Min(Pixels[o + 1], Mix(Pixels[o + 1], ink.G, a));
                    Pixels[o + 2] = Math.Min(Pixels[o + 2], Mix(Pixels[o + 2], ink.B, a));
                    if (InkLeft < 0 || px < InkLeft) InkLeft = px;
                    if (px > InkRight) InkRight = px;
                }
            }
        }

        static byte Mix(byte under, byte ink, float a) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(under + (ink - under) * a)));

        static float Sample(CardFont.Glyph g, float u, float v)
        {
            int iu = (int)Math.Floor(u), iv = (int)Math.Floor(v);
            float fu = u - iu, fv = v - iv;
            float c00 = At(g, iu, iv), c10 = At(g, iu + 1, iv), c01 = At(g, iu, iv + 1), c11 = At(g, iu + 1, iv + 1);
            return ((c00 * (1f - fu) + c10 * fu) * (1f - fv) + (c01 * (1f - fu) + c11 * fu) * fv) / 15f;
        }

        static float At(CardFont.Glyph g, int x, int y) => x < 0 || y < 0 || x >= g.Width || y >= g.Height ? 0f : g.Coverage[y * g.Width + x];
    }

    /// <summary>What a printed board says (pure): the line's two ends, the way the boards named a line ("A ⇄ B").</summary>
    internal static class CardText
    {
        /// <summary>Between the two ends: the bold double arrow of the painted boards (drawn as a shape, CardComposer.Arrow).</summary>
        internal const string Arrow = " ↔ ";

        /// <summary>
        /// The board's text for a line: its two ends joined by the arrow when both are known and differ; one of them when only one
        /// is; else the line's name (the label alone when the name is only the number).
        /// </summary>
        internal static string Route(string origin, string terminus, string lineName, string number)
        {
            origin = (origin ?? "").Trim();
            terminus = (terminus ?? "").Trim();
            if (origin.Length > 0 && terminus.Length > 0 && !string.Equals(origin, terminus, StringComparison.Ordinal))
                return origin + Arrow + terminus;
            if (terminus.Length > 0) return terminus;
            if (origin.Length > 0) return origin;
            string name = (lineName ?? "").Trim();
            return string.Equals(name, (number ?? "").Trim(), StringComparison.Ordinal) ? "" : name;
        }

        /// <summary>The picture of a board of this size: 128 px high (more for a tall board), the width by its proportions, at most 2048 px.</summary>
        internal static void PictureSize(float widthMeters, float heightMeters, out int width, out int height)
        {
            if (!(widthMeters > 0f) || !(heightMeters > 0f))
            {
                width = 512;
                height = 64;
                return;
            }

            height = 128;
            width = (int)Math.Round(height * widthMeters / heightMeters);
            if (width > 2048)
            {
                height = Math.Max(16, (int)Math.Round(height * 2048f / width));
                width = 2048;
            }

            width = Math.Max(16, width);
        }

        /// <summary>The key of a shared board picture: its picture size, colours and texts.</summary>
        internal static string Key(int width, int height, CardRgb board, CardRgb ink, CardRgb prefixInk, string prefix, string number, string text) =>
            width + "x" + height + "/" + board.GetHashCode().ToString("x6") + ink.GetHashCode().ToString("x6") + prefixInk.GetHashCode().ToString("x6") + "/" +
            (prefix ?? "") + "\u0001" + (number ?? "") + "\u0001" + (text ?? "");
    }
}
