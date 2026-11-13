using System;
using System.IO;
using System.IO.Compression;

namespace CDLC.BusDriver
{
    /// <summary>
    /// A PNG decoder that runs on any thread (the game's LoadImage only runs on the main thread, and decoding a wrap's 2048 x 785
    /// pictures there was a measurable part of the half second the wrap's load froze the game for). It reads what wrap pictures are in
    /// practice: not interlaced, 8 bits a sample (grey, grey + alpha, RGB, RGBA) or 1 to 8 bits an index or grey sample, with tRNS.
    /// Anything else (16 bits, interlaced, damaged data, a picture over <see cref="MaxPixels"/>) is not decoded here, so the caller
    /// takes the game's LoadImage as before: the result of this decoder is only used when it is certain.
    /// Output: width x height x 4 bytes, RGBA, rows from the BOTTOM (the order Unity's GetPixels32 gives).
    /// </summary>
    internal static class PngDecoder
    {
        /// <summary>The largest picture taken at all (4096 x 4096), by this decoder and by the game's LoadImage alike: its pixels are 64 MiB.</summary>
        internal const int MaxPixels = 16 * 1024 * 1024;

        /// <summary>
        /// Why a picture file is not taken (null: it is): its size from its header (a PNG's IHDR, a JPEG's frame header) over
        /// <see cref="MaxPixels"/>, or no size to read (LoadImage takes only PNG and JPEG). Asked before any decoding, on any thread: a
        /// small file can hold a huge picture (8192 x 4096 compresses to 127 KiB), and the main thread's LoadImage has no limit of its own.
        /// </summary>
        internal static string Refusal(byte[] file)
        {
            if (!TryReadSize(file, out int width, out int height))
                return "is not a PNG or JPEG picture whose size can be read";
            if (width <= 0 || height <= 0)
                return $"has an empty size ({width} x {height})";
            if ((long)width * height > MaxPixels)
                return $"is {width} x {height}, over the {MaxPixels / (1024 * 1024)} million pixels taken (4096 x 4096)";
            return null;
        }

        /// <summary>The picture's size from its header alone.</summary>
        internal static bool TryReadSize(byte[] file, out int width, out int height)
        {
            width = height = 0;
            if (file == null) return false;
            if (file.Length >= 24 && StartsWith(file, kSignature) && file[12] == 'I' && file[13] == 'H' && file[14] == 'D' && file[15] == 'R')
            {
                width = ReadInt(file, 16);
                height = ReadInt(file, 20);
                return true;
            }
            if (file.Length < 4 || file[0] != 0xFF || file[1] != 0xD8)
                return false;
            // JPEG: markers until a start of frame (SOF0..SOF15 but DHT C4, JPG C8 and DAC CC), whose payload is precision, height, width
            int pos = 2;
            while (pos + 4 <= file.Length)
            {
                if (file[pos] != 0xFF) return false;
                byte marker = file[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                if (marker == 0xD8 || marker == 0x01 || marker >= 0xD0 && marker <= 0xD7) { pos += 2; continue; }
                if (marker == 0xD9 || marker == 0xDA) return false;
                int length = (file[pos + 2] << 8) | file[pos + 3];
                if (length < 2) return false;
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    if (pos + 9 > file.Length || length < 7) return false;
                    height = (file[pos + 5] << 8) | file[pos + 6];
                    width = (file[pos + 7] << 8) | file[pos + 8];
                    return true;
                }
                pos += 2 + length;
            }
            return false;
        }

        private static bool StartsWith(byte[] file, byte[] prefix)
        {
            for (int i = 0; i < prefix.Length; i++)
                if (file[i] != prefix[i]) return false;
            return true;
        }

        private static readonly byte[] kSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static uint[] s_Crc;

        /// <summary>Decodes the picture; false (nothing set) when it is not a PNG this decoder is sure of.</summary>
        internal static bool TryDecode(byte[] png, out int width, out int height, out byte[] rgba)
        {
            width = height = 0;
            rgba = null;
            try
            {
                if (Decode(png, out width, out height, out rgba))
                    return true;
            }
            catch (Exception)
            {
                // damaged or hostile data (a deflate error, a short chunk, no memory): the caller's LoadImage decides
            }
            width = height = 0;
            rgba = null;
            return false;
        }

        private static bool Decode(byte[] png, out int width, out int height, out byte[] rgba)
        {
            width = height = 0;
            rgba = null;
            if (png == null || png.Length < 8 + 12 + 13 + 12)
                return false;
            for (int i = 0; i < 8; i++)
                if (png[i] != kSignature[i]) return false;

            int depth = 0, colour = -1, channels = 0;
            int pos = 8;
            bool header = false, ended = false;
            byte[] palette = null;
            int paletteCount = 0;
            byte[] paletteAlpha = null;
            int transparentGrey = -1, transparentR = -1, transparentG = -1, transparentB = -1;
            var idat = new MemoryStream();
            while (pos + 12 <= png.Length && !ended)
            {
                long length = ReadInt(png, pos);
                if (length < 0 || pos + 12L + length > png.Length) return false;
                int data = pos + 8, len = (int)length;
                string type = new string(new[] { (char)png[pos + 4], (char)png[pos + 5], (char)png[pos + 6], (char)png[pos + 7] });
                bool crcOk = Crc(png, pos + 4, len + 4) == (uint)ReadInt(png, data + len);
                switch (type)
                {
                    case "IHDR":
                        if (header || len != 13 || !crcOk) return false;
                        width = ReadInt(png, data);
                        height = ReadInt(png, data + 4);
                        depth = png[data + 8];
                        colour = png[data + 9];
                        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return false;
                        if (png[data + 10] != 0 || png[data + 11] != 0 || png[data + 12] != 0) return false;   // compression, filter method, not interlaced
                        switch (colour)
                        {
                            case 0: channels = 1; if (depth != 1 && depth != 2 && depth != 4 && depth != 8) return false; break;
                            case 2: channels = 3; if (depth != 8) return false; break;
                            case 3: channels = 1; if (depth != 1 && depth != 2 && depth != 4 && depth != 8) return false; break;
                            case 4: channels = 2; if (depth != 8) return false; break;
                            case 6: channels = 4; if (depth != 8) return false; break;
                            default: return false;
                        }
                        header = true;
                        break;
                    case "PLTE":
                        if (!header || !crcOk || len % 3 != 0 || len == 0 || len > 768) return false;
                        palette = new byte[len];
                        Buffer.BlockCopy(png, data, palette, 0, len);
                        paletteCount = len / 3;
                        break;
                    case "tRNS":
                        // ancillary: a damaged one is left out (as libpng does), not a reason to refuse the picture
                        if (!header || !crcOk) break;
                        if (colour == 3)
                        {
                            paletteAlpha = new byte[Math.Min(len, 256)];
                            Buffer.BlockCopy(png, data, paletteAlpha, 0, paletteAlpha.Length);
                        }
                        else if (colour == 0 && len == 2)
                            transparentGrey = ReadShort(png, data);
                        else if (colour == 2 && len == 6)
                        {
                            transparentR = ReadShort(png, data);
                            transparentG = ReadShort(png, data + 2);
                            transparentB = ReadShort(png, data + 4);
                        }
                        break;
                    case "IDAT":
                        if (!header || !crcOk) return false;
                        idat.Write(png, data, len);
                        break;
                    case "IEND":
                        ended = true;
                        break;
                }
                pos = data + len + 4;
            }
            if (!header || idat.Length < 6 || (colour == 3 && palette == null)) return false;

            int bits = depth * channels;
            int stride = (int)(((long)width * bits + 7) / 8);
            long rawLength = (long)height * (stride + 1);
            if (rawLength > int.MaxValue / 2) return false;
            byte[] raw = Inflate(idat.GetBuffer(), (int)idat.Length, (int)rawLength);
            if (raw == null) return false;
            if (!Unfilter(raw, width, height, stride, Math.Max(1, bits / 8))) return false;

            rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int src = y * (stride + 1) + 1, dst = (height - 1 - y) * width * 4;      // PNG rows run top to bottom, Unity's bottom to top
                switch (colour)
                {
                    case 6:
                        Buffer.BlockCopy(raw, src, rgba, dst, width * 4);
                        break;
                    case 2:
                        for (int x = 0; x < width; x++, src += 3, dst += 4)
                        {
                            byte r = raw[src], g = raw[src + 1], b = raw[src + 2];
                            rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b;
                            rgba[dst + 3] = (byte)(r == transparentR && g == transparentG && b == transparentB ? 0 : 255);
                        }
                        break;
                    case 4:
                        for (int x = 0; x < width; x++, src += 2, dst += 4)
                        {
                            byte g = raw[src];
                            rgba[dst] = g; rgba[dst + 1] = g; rgba[dst + 2] = g; rgba[dst + 3] = raw[src + 1];
                        }
                        break;
                    case 0:
                        for (int x = 0; x < width; x++, dst += 4)
                        {
                            int sample = Sample(raw, src, x, depth);
                            byte v = depth == 8 ? (byte)sample : (byte)(sample * 255 / ((1 << depth) - 1));
                            rgba[dst] = v; rgba[dst + 1] = v; rgba[dst + 2] = v;
                            rgba[dst + 3] = (byte)(sample == transparentGrey ? 0 : 255);
                        }
                        break;
                    default:
                        for (int x = 0; x < width; x++, dst += 4)
                        {
                            int index = Sample(raw, src, x, depth);
                            if (index >= paletteCount) return FailClear(out width, out height, out rgba);
                            rgba[dst] = palette[3 * index]; rgba[dst + 1] = palette[3 * index + 1]; rgba[dst + 2] = palette[3 * index + 2];
                            rgba[dst + 3] = paletteAlpha != null && index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                        }
                        break;
                }
            }
            return true;
        }

        private static bool FailClear(out int width, out int height, out byte[] rgba)
        {
            width = height = 0;
            rgba = null;
            return false;
        }

        // The sample x of a row (1, 2, 4 or 8 bits, the leftmost in the high bits).
        private static int Sample(byte[] raw, int row, int x, int depth)
        {
            if (depth == 8) return raw[row + x];
            int perByte = 8 / depth, shift = 8 - depth * (x % perByte + 1);
            return (raw[row + x / perByte] >> shift) & ((1 << depth) - 1);
        }

        // zlib: a 2-byte header, the deflate stream (the 4-byte checksum after it is not read); exactly <paramref name="size"/> bytes out.
        private static byte[] Inflate(byte[] data, int length, int size)
        {
            if ((data[0] & 0x0F) != 8 || ((data[0] << 8) | data[1]) % 31 != 0 || (data[1] & 0x20) != 0) return null;
            var raw = new byte[size];
            using (var input = new MemoryStream(data, 2, length - 2, false))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < size)
                {
                    int n = inflate.Read(raw, got, size - got);
                    if (n <= 0) return null;          // fewer rows than the header says
                    got += n;
                }
            }
            return raw;
        }

        // Undoes the row filters in place (rows of 1 filter byte and stride data bytes); false for a filter type that does not exist.
        private static bool Unfilter(byte[] raw, int width, int height, int stride, int bpp)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * (stride + 1), cur = row + 1, prev = cur - (stride + 1);
                byte filter = raw[row];
                switch (filter)
                {
                    case 0:
                        break;
                    case 1:
                        for (int i = bpp; i < stride; i++) raw[cur + i] = (byte)(raw[cur + i] + raw[cur + i - bpp]);
                        break;
                    case 2:
                        if (y > 0)
                            for (int i = 0; i < stride; i++) raw[cur + i] = (byte)(raw[cur + i] + raw[prev + i]);
                        break;
                    case 3:
                        for (int i = 0; i < stride; i++)
                        {
                            int left = i >= bpp ? raw[cur + i - bpp] : 0, up = y > 0 ? raw[prev + i] : 0;
                            raw[cur + i] = (byte)(raw[cur + i] + ((left + up) >> 1));
                        }
                        break;
                    case 4:
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? raw[cur + i - bpp] : 0, b = y > 0 ? raw[prev + i] : 0, c = y > 0 && i >= bpp ? raw[prev + i - bpp] : 0;
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            raw[cur + i] = (byte)(raw[cur + i] + predictor);
                        }
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private static int ReadInt(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

        private static int ReadShort(byte[] b, int at) => b[at] << 8 | b[at + 1];

        private static uint Crc(byte[] data, int at, int count)
        {
            uint[] table = s_Crc;
            if (table == null)
            {
                table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                s_Crc = table;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = at; i < at + count; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
