using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using CDLC.BusDriver;

// The thread-safe PNG decoder behind the body wraps: every colour type and depth it takes, every row filter, split IDAT chunks, tRNS,
// and the refusals (it must say no, never guess: the caller then uses the game's LoadImage as before).
static class PngDecoderTests
{
    const string Prefix = "png decoder: ";

    internal static void Run(Action<bool, string> check)
    {
        RoundTrips(check);
        Refusals(check);
        Fuzz(check);
        RealWraps(check);
    }

    // ---------------------------------------------------------------- a small encoder (the reference for the decoder's inverse)

    /// <summary>
    /// A PNG of packed rows (top row first, each exactly the row's bytes for the colour type and depth), one filter type per row
    /// (<paramref name="filterOf"/> of the row), the data split into IDAT chunks of <paramref name="idatSize"/> bytes.
    /// </summary>
    internal static byte[] Encode(int w, int h, int colour, int depth, byte[] rows, byte[] palette = null, byte[] trns = null,
        Func<int, int> filterOf = null, int idatSize = 1 << 20, int interlace = 0)
    {
        int channels = colour == 0 || colour == 3 ? 1 : colour == 2 ? 3 : colour == 4 ? 2 : 4;
        int bits = depth * channels, stride = (w * bits + 7) / 8, bpp = Math.Max(1, bits / 8);
        var filtered = new byte[h * (stride + 1)];
        for (int y = 0; y < h; y++)
        {
            int f = filterOf != null ? filterOf(y) : y % 5;
            filtered[y * (stride + 1)] = (byte)f;
            for (int i = 0; i < stride; i++)
            {
                int cur = rows[y * stride + i];
                int a = i >= bpp ? rows[y * stride + i - bpp] : 0, b = y > 0 ? rows[(y - 1) * stride + i] : 0, c = y > 0 && i >= bpp ? rows[(y - 1) * stride + i - bpp] : 0;
                int predictor = 0;
                switch (f)
                {
                    case 1: predictor = a; break;
                    case 2: predictor = b; break;
                    case 3: predictor = (a + b) / 2; break;
                    case 4:
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        break;
                }
                filtered[y * (stride + 1) + 1 + i] = (byte)(cur - predictor);
            }
        }
        byte[] zlib = Zlib(filtered);
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        var header = new byte[13];
        Put(header, 0, w); Put(header, 4, h);
        header[8] = (byte)depth; header[9] = (byte)colour; header[12] = (byte)interlace;
        Chunk(png, "IHDR", header);
        if (palette != null) Chunk(png, "PLTE", palette);
        if (trns != null) Chunk(png, "tRNS", trns);
        for (int at = 0; at < zlib.Length; at += idatSize)
        {
            var part = new byte[Math.Min(idatSize, zlib.Length - at)];
            Array.Copy(zlib, at, part, 0, part.Length);
            Chunk(png, "IDAT", part);
        }
        Chunk(png, "IEND", new byte[0]);
        return png.ToArray();
    }

    static byte[] Zlib(byte[] data)
    {
        var output = new MemoryStream();
        output.WriteByte(0x78); output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(data, 0, data.Length);
        uint a = 1, b = 0;
        foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        uint adler = b << 16 | a;
        output.WriteByte((byte)(adler >> 24)); output.WriteByte((byte)(adler >> 16)); output.WriteByte((byte)(adler >> 8)); output.WriteByte((byte)adler);
        return output.ToArray();
    }

    static void Put(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        Put(length, 0, data.Length);
        s.Write(length, 0, 4);
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        Array.Copy(data, 0, body, 4, data.Length);
        s.Write(body, 0, body.Length);
        uint crc = 0xFFFFFFFFu;
        foreach (byte x in body)
        {
            crc ^= x;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        crc ^= 0xFFFFFFFFu;
        var tail = new byte[4];
        Put(tail, 0, (int)crc);
        s.Write(tail, 0, 4);
    }

    static byte[] RandomBytes(System.Random rng, int n, int mask = 255)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(rng.Next(256) & mask);
        return b;
    }

    // The expected RGBA (rows from the bottom) of packed top-down rows.
    static byte[] Expect(int w, int h, int colour, int depth, byte[] rows, byte[] palette, byte[] trns)
    {
        int channels = colour == 0 || colour == 3 ? 1 : colour == 2 ? 3 : colour == 4 ? 2 : 4;
        int stride = (w * depth * channels + 7) / 8;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = ((h - 1 - y) * w + x) * 4, r = y * stride;
                byte R, G, B, A = 255;
                switch (colour)
                {
                    case 6: R = rows[r + 4 * x]; G = rows[r + 4 * x + 1]; B = rows[r + 4 * x + 2]; A = rows[r + 4 * x + 3]; break;
                    case 2:
                        R = rows[r + 3 * x]; G = rows[r + 3 * x + 1]; B = rows[r + 3 * x + 2];
                        if (trns != null && R == trns[1] && G == trns[3] && B == trns[5]) A = 0;
                        break;
                    case 4: R = G = B = rows[r + 2 * x]; A = rows[r + 2 * x + 1]; break;
                    default:
                    {
                        int bit = x * depth, s = depth == 8 ? rows[r + x] : (rows[r + bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                        if (colour == 3)
                        {
                            R = palette[3 * s]; G = palette[3 * s + 1]; B = palette[3 * s + 2];
                            if (trns != null && s < trns.Length) A = trns[s];
                        }
                        else
                        {
                            R = G = B = (byte)(depth == 8 ? s : s * 255 / ((1 << depth) - 1));
                            if (trns != null && s == trns[1]) A = 0;
                        }
                        break;
                    }
                }
                rgba[o] = R; rgba[o + 1] = G; rgba[o + 2] = B; rgba[o + 3] = A;
            }
        return rgba;
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ---------------------------------------------------------------- the checks

    static void RoundTrips(Action<bool, string> check)
    {
        var rng = new System.Random(20261019);
        // (colour, depth, name)
        var kinds = new[] { (6, 8, "RGBA 8"), (2, 8, "RGB 8"), (4, 8, "grey + alpha 8"), (0, 8, "grey 8"), (0, 4, "grey 4"), (0, 2, "grey 2"), (0, 1, "grey 1"),
                            (3, 8, "palette 8"), (3, 4, "palette 4"), (3, 2, "palette 2"), (3, 1, "palette 1") };
        foreach (var (colour, depth, name) in kinds)
        {
            bool ok = true;
            foreach (int w in new[] { 1, 2, 3, 7, 16, 37, 129 })
                foreach (int h in new[] { 1, 2, 5, 23 })
                {
                    int channels = colour == 0 || colour == 3 ? 1 : colour == 2 ? 3 : colour == 4 ? 2 : 4;
                    int stride = (w * depth * channels + 7) / 8;
                    byte[] rows = RandomBytes(rng, stride * h);
                    byte[] palette = null, trns = null;
                    if (colour == 3)
                    {
                        int entries = Math.Min(256, 1 << depth);
                        palette = RandomBytes(rng, 3 * entries);
                        trns = RandomBytes(rng, Math.Max(1, entries / 2));
                        // every index must be inside the palette
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int bit = x * depth, at = y * stride + bit / 8;
                                int idx = rng.Next(entries), shift = depth == 8 ? 0 : 8 - depth - bit % 8, mask = ((1 << depth) - 1) << shift;
                                rows[at] = (byte)((rows[at] & ~mask) | (idx << shift));
                            }
                    }
                    else if (colour == 0)
                        trns = new byte[] { 0, (byte)rng.Next(1 << depth) };
                    else if (colour == 2)
                    {
                        // the transparent colour is one that occurs
                        int px = rng.Next(w * h);
                        trns = new byte[] { 0, rows[3 * px], 0, rows[3 * px + 1], 0, rows[3 * px + 2] };
                        int y = px / w, x = px % w;
                        trns = new byte[] { 0, rows[y * stride + 3 * x], 0, rows[y * stride + 3 * x + 1], 0, rows[y * stride + 3 * x + 2] };
                    }
                    byte[] png = Encode(w, h, colour, depth, rows, palette, trns, idatSize: rng.Next(2) == 0 ? 1 << 20 : 7);
                    bool decoded = PngDecoder.TryDecode(png, out int dw, out int dh, out byte[] rgba);
                    ok &= decoded && dw == w && dh == h && Same(rgba, Expect(w, h, colour, depth, rows, palette, trns));
                }
            check(ok, Prefix + name + ": every size and row filter decodes to the picture, rows from the bottom, tRNS applied");
        }

        // all five filters on a picture whose rows reward the filters (smooth ramps, repeated rows, noise)
        {
            int w = 64, h = 40;
            var rows = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w * 4; x++)
                    rows[y * w * 4 + x] = (byte)(y % 7 == 0 ? rng.Next(256) : y % 3 == 0 ? rows[Math.Max(0, y - 1) * w * 4 + x] : x * 3 + y * 5);
            bool ok = true;
            for (int f = 0; f < 5; f++)
            {
                int fixedFilter = f;
                byte[] png = Encode(w, h, 6, 8, rows, filterOf: y => fixedFilter);
                ok &= PngDecoder.TryDecode(png, out int dw, out int dh, out byte[] rgba) && Same(rgba, Expect(w, h, 6, 8, rows, null, null));
            }
            check(ok, Prefix + "None, Sub, Up, Average and Paeth on every row each give the same picture");
        }

        // the picture is not touched by the decoder's input: a decoded picture's rows run from the bottom
        {
            var rows = new byte[2 * 2 * 4] { 1, 1, 1, 255, 2, 2, 2, 255, 3, 3, 3, 255, 4, 4, 4, 255 };
            PngDecoder.TryDecode(Encode(2, 2, 6, 8, rows), out _, out _, out byte[] rgba);
            check(rgba != null && rgba[0] == 3 && rgba[4] == 4 && rgba[8] == 1 && rgba[12] == 2, Prefix + "the top row of the file is the last row of the result");
        }
    }

    static void Refusals(Action<bool, string> check)
    {
        var rng = new System.Random(5);
        byte[] rows = RandomBytes(rng, 8 * 8 * 4);
        byte[] good = Encode(8, 8, 6, 8, rows);
        check(PngDecoder.TryDecode(good, out _, out _, out _), Prefix + "the plain picture used below decodes");

        Func<byte[], bool> refused = png => !PngDecoder.TryDecode(png, out int w, out int h, out byte[] rgba) && w == 0 && h == 0 && rgba == null;
        check(refused(null) && refused(new byte[0]) && refused(new byte[40]) && refused(new byte[] { 137, 80, 78, 71 }), Prefix + "nothing, short data and zeros are refused");

        byte[] jpg = new byte[200]; jpg[0] = 0xFF; jpg[1] = 0xD8;
        check(refused(jpg), Prefix + "a JPG is refused (the game's LoadImage reads it)");

        byte[] sixteen = Encode(4, 4, 6, 16, RandomBytes(rng, 4 * 4 * 8));
        check(refused(sixteen), Prefix + "a 16-bit picture is refused (the game rounds it its own way)");
        byte[] twoBitRgb = Encode(4, 4, 2, 4, RandomBytes(rng, 24));
        check(refused(twoBitRgb), Prefix + "a colour type with a depth the standard does not allow is refused");
        check(refused(Encode(8, 8, 6, 8, rows, interlace: 1)), Prefix + "an interlaced picture is refused");

        byte[] badCrc = (byte[])good.Clone();
        int idat = IndexOf(badCrc, "IDAT");
        badCrc[idat + 6] ^= 0x55;
        check(refused(badCrc), Prefix + "damaged picture data (checksum) is refused");

        byte[] cut = new byte[good.Length - 30];
        Array.Copy(good, cut, cut.Length);
        check(refused(cut), Prefix + "a cut-off file is refused");

        byte[] shortData = Encode(8, 8, 6, 8, rows);
        // the header claims more rows than the data holds
        Put(shortData, IndexOf(shortData, "IHDR") + 8, 400);
        FixCrc(shortData, IndexOf(shortData, "IHDR") - 4, 13);
        check(refused(shortData), Prefix + "a header that promises more rows than there are is refused");

        byte[] huge = Encode(8, 8, 6, 8, rows);
        Put(huge, IndexOf(huge, "IHDR") + 4, 20000); Put(huge, IndexOf(huge, "IHDR") + 8, 20000);
        FixCrc(huge, IndexOf(huge, "IHDR") - 4, 13);
        check(refused(huge), Prefix + "a picture over " + PngDecoder.MaxPixels / (1024 * 1024) + " megapixels is refused without allocating it");

        byte[] zero = Encode(8, 8, 6, 8, rows);
        Put(zero, IndexOf(zero, "IHDR") + 4, 0);
        FixCrc(zero, IndexOf(zero, "IHDR") - 4, 13);
        check(refused(zero), Prefix + "a zero-width picture is refused");

        byte[] badFilter = Encode(8, 8, 6, 8, rows, filterOf: y => 0);
        // a filter byte of 9 in the first row: rebuild the stream by hand
        {
            var filtered = new byte[8 * (8 * 4 + 1)];
            for (int y = 0; y < 8; y++) filtered[y * 33] = (byte)(y == 0 ? 9 : 0);
            var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var header = new byte[13]; Put(header, 0, 8); Put(header, 4, 8); header[8] = 8; header[9] = 6;
            Chunk(png, "IHDR", header); Chunk(png, "IDAT", Zlib(filtered)); Chunk(png, "IEND", new byte[0]);
            badFilter = png.ToArray();
        }
        check(refused(badFilter), Prefix + "a row filter that does not exist is refused");

        // a palette index past the palette
        {
            var indices = new byte[4 * 4];
            indices[5] = 3;
            var palette = new byte[] { 1, 2, 3, 4, 5, 6 };
            check(refused(Encode(4, 4, 3, 8, indices, palette)), Prefix + "a palette index past the palette is refused");
            check(refused(Encode(4, 4, 3, 8, new byte[16])), Prefix + "an indexed picture with no palette is refused");
        }

        // a damaged tRNS is left out (as libpng does), the picture is still read, opaque
        {
            var rgb = RandomBytes(rng, 4 * 4 * 3);
            byte[] png = Encode(4, 4, 2, 8, rgb, trns: new byte[] { 0, rgb[0], 0, rgb[1], 0, rgb[2] });
            int at = IndexOf(png, "tRNS");
            png[at + 5] ^= 0x33;
            bool ok = PngDecoder.TryDecode(png, out _, out _, out byte[] rgba);
            bool opaque = ok;
            for (int i = 3; ok && i < rgba.Length; i += 4) opaque &= rgba[i] == 255;
            check(ok && opaque, Prefix + "a damaged tRNS chunk is ignored and the picture reads opaque");
        }
    }

    static void Fuzz(Action<bool, string> check)
    {
        var rng = new System.Random(77);
        byte[] seedPicture = Encode(13, 9, 6, 8, RandomBytes(rng, 13 * 9 * 4));
        bool never = true, sized = true;
        int decodedAny = 0;
        for (int round = 0; round < 3000; round++)
        {
            byte[] png = (byte[])seedPicture.Clone();
            int edits = 1 + rng.Next(4);
            for (int e = 0; e < edits; e++) png[rng.Next(png.Length)] = (byte)rng.Next(256);
            if (rng.Next(5) == 0) Array.Resize(ref png, rng.Next(png.Length));
            try
            {
                if (PngDecoder.TryDecode(png, out int w, out int h, out byte[] rgba))
                {
                    decodedAny++;
                    sized &= rgba != null && rgba.Length == w * h * 4 && w > 0 && h > 0;
                }
            }
            catch (Exception)
            {
                never = false;
            }
        }
        check(never && sized, Prefix + "3000 randomly damaged pictures: no exception, and a result is always the size it says");
    }

    static void RealWraps(Action<bool, string> check)
    {
        // the wraps in the repository's root (artwork from other games, not tracked): every picture of them decodes to its header's size
        int pictures = 0;
        bool ok = true;
        foreach (string zip in Directory.GetFiles(Repo.Root, "*.zip"))
        {
            WrapPackage.Package package;
            try { package = WrapPackage.Read(zip, false); }
            catch (WrapPackage.Error) { continue; }
            foreach (byte[] bytes in package.Faces)
            {
                if (bytes == null) continue;
                pictures++;
                int w = bytes[16] << 24 | bytes[17] << 16 | bytes[18] << 8 | bytes[19], h = bytes[20] << 24 | bytes[21] << 16 | bytes[22] << 8 | bytes[23];
                ok &= PngDecoder.TryDecode(bytes, out int dw, out int dh, out byte[] rgba) && dw == w && dh == h && rgba.Length == w * h * 4;
            }
        }
        if (pictures > 0)
            check(ok, Prefix + $"the {pictures} pictures of the real wraps in the repository root decode to their header's size");
    }

    static int IndexOf(byte[] data, string type)
    {
        for (int i = 4; i + 4 <= data.Length; i++)
            if (data[i] == type[0] && data[i + 1] == type[1] && data[i + 2] == type[2] && data[i + 3] == type[3]) return i;
        throw new InvalidOperationException(type);
    }

    // Writes the right checksum after a chunk body was changed by hand (at = the chunk's length field).
    static void FixCrc(byte[] png, int at, int length)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = at + 4; i < at + 8 + length; i++)
        {
            crc ^= png[i];
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        Put(png, at + 8 + length, (int)(crc ^ 0xFFFFFFFFu));
    }
}
