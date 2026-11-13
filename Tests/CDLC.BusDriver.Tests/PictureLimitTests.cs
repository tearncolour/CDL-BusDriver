using System;
using System.Linq;
using CDLC.BusDriver;

// A wrap picture's size is read from its header and held to PngDecoder.MaxPixels before any decoding, on the worker and on the main
// thread's LoadImage path alike. Before, a picture the worker refused for its size went to LoadImage, which has no limit: an 8192 x 4096
// PNG of 127 KiB passed the zip checks and decoded to 128 MiB on the main thread (the audit of 10 November).
static class PictureLimitTests
{
    const string Prefix = "picture limit: ";

    static byte[] PngHeader(int width, int height)
    {
        var b = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BigEndian(width).CopyTo(b, 16);
        BigEndian(height).CopyTo(b, 20);
        b[24] = 8; b[25] = 6;
        return b;
    }

    static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    // SOI, an APP0 segment, a DHT segment (C4: not a frame), then SOF2 (progressive) with the size
    static byte[] Jpeg(int width, int height) => new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x06, (byte)'J', (byte)'F', (byte)'I', (byte)'F',
        0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00,
        0xFF, 0xC2, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
    };

    internal static void Run(Action<bool, string> check)
    {
        check(PngDecoder.Refusal(PngHeader(4096, 4096)) == null && PngDecoder.Refusal(PngHeader(2048, 785)) == null,
            Prefix + "a PNG of 4096 x 4096 or less is taken (a wrap side is 2048 x 785)");
        string big = PngDecoder.Refusal(PngHeader(8192, 4096));
        check(big != null && big.Contains("8192 x 4096"), Prefix + "the audit's 8192 x 4096 PNG is refused, with its size in the reason: " + big);
        check(PngDecoder.Refusal(PngHeader(16384, 1)) == null && PngDecoder.Refusal(PngHeader(int.MaxValue, 2)) != null,
            Prefix + "the limit is on the pixels (16384 x 1 is taken), and a size that overflows is refused");
        check(PngDecoder.Refusal(PngHeader(-5, 10)) != null && PngDecoder.Refusal(PngHeader(0, 10)) != null, Prefix + "a negative or empty size is refused");

        check(PngDecoder.TryReadSize(Jpeg(3000, 1200), out int jw, out int jh) && jw == 3000 && jh == 1200,
            Prefix + "a JPEG's size is read from its frame header, past the segments before it");
        check(PngDecoder.Refusal(Jpeg(9000, 9000)) != null && PngDecoder.Refusal(Jpeg(4000, 4000)) == null, Prefix + "a JPEG is held to the same limit");
        check(PngDecoder.Refusal(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }) != null && PngDecoder.Refusal(new byte[0]) != null && PngDecoder.Refusal(null) != null,
            Prefix + "a file whose size cannot be read (a GIF, nothing) is refused: LoadImage takes PNG and JPEG only");
        byte[] cut = Jpeg(100, 100).Take(12).ToArray();
        check(PngDecoder.Refusal(cut) != null, Prefix + "a JPEG cut before its frame header is refused");

        byte[] real = WrapTemplate.Png(64, 32, new byte[64 * 32 * 4]);
        check(PngDecoder.TryReadSize(real, out int pw, out int ph) && pw == 64 && ph == 32 && PngDecoder.Refusal(real) == null,
            Prefix + "a real PNG (the template writer's) is read and taken");
    }
}
