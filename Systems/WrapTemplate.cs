using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using WrapFace = CDLC.BusDriver.Interior.Models.BusInterior.WrapFace;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The template zip of a vehicle model (Wraps/&lt;model&gt;/template.zip), pure (no Unity), so the game (BusSkins) and the offline tools
    /// write the same file: for each face the empty transparent picture to paint on, guide/&lt;face&gt;.png (what each area is, by projection
    /// of the model: VehicleMesh.ProjectWrap), a README in English and Chinese, and wrap.json with the model's numbers.
    /// </summary>
    internal static class WrapTemplate
    {
        internal const int Revision = 7;       // 7: an articulated bus's joints are marked on the sides and the roof; doorLeaves of every section
        private const float kMinCovered = 0.02f;           // an authored bus's face covered less than this has no wrap picture

        // Guide colours (RGB)
        private static readonly byte[] kOutside = { 30, 34, 40 }, kHidden = { 58, 63, 69 }, kSolid = { 236, 238, 240 }, kCovered = { 150, 156, 164 };
        private static readonly byte[] kGrid = { 170, 178, 188 }, kFront = { 230, 60, 40 }, kJoint = { 255, 40, 160 };
        private static readonly byte[] kWindowA = { 150, 200, 235 }, kWindowB = { 112, 168, 214 }, kDoor = { 120, 200, 140 };
        // the wrap pillars' outlines, by the pillar's place in the profile's list
        private static readonly byte[][] kPillar = { new byte[] { 255, 150, 0 }, new byte[] { 230, 0, 200 }, new byte[] { 240, 220, 0 }, new byte[] { 0, 200, 220 } };

        internal sealed class Input
        {
            internal string Model;
            internal int Layout;
            /// <summary>The texts of the language files (the keys without the mod's prefix), in English and in Chinese: the template holds its guide in both.</summary>
            internal Func<string, string> English, Chinese;
            internal float OutlineMm = WrapPackage.DefaultOutlineMm;
            /// <summary>The bus's sections, front first.</summary>
            internal IList<BusInterior> Sections;
            /// <summary>The model's profile when the bus is authored (null: the generated bus).</summary>
            internal VehicleProfile Profile;

            internal float Total => Sections.Sum(m => m.WrapLength);
        }

        internal static bool Along(WrapFace face) => face == WrapFace.Left || face == WrapFace.Right || face == WrapFace.Roof;

        internal static string Title(Input v) => $"template r{Revision} {v.Total:0.0}m".Replace(',', '.');

        /// <summary>
        /// Builds the template's manifest and entries (name -> bytes: the five pictures, guide/..., the READMEs). False when no face has a picture.
        /// </summary>
        internal static bool Build(Input v, out WrapPackage.Manifest manifest, out List<KeyValuePair<string, byte[]>> entries)
        {
            entries = new List<KeyValuePair<string, byte[]>>();
            manifest = null;
            var info = new StringBuilder("{\n    \"alpha\": \"").Append(v.English("Wrap.Template.InfoAlpha")).Append("\",\n    \"outlineRule\": \"")
                .Append(v.English("Wrap.Template.InfoOutlineRule")).Append("\",\n    \"faces\": {");
            bool firstFace = true;
            for (int f = 0; f < BusInterior.WrapFaceCount; f++)
            {
                byte[] guide = Guide(v, (WrapFace)f, out int w, out int h, out float2 metres);
                if (guide == null) continue;
                entries.Add(new KeyValuePair<string, byte[]>("guide/" + WrapPackage.Pictures[f], Png(w, h, guide)));
                entries.Add(new KeyValuePair<string, byte[]>(WrapPackage.Pictures[f], Png(w, h, new byte[w * h * 4])));      // the empty picture to paint on
                if (!firstFace) info.Append(',');
                firstFace = false;
                info.Append("\n      \"").Append(WrapPackage.Faces[f]).Append("\": { \"metres\": [")
                    .Append(metres.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(", ").Append(metres.y.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("], \"pixels\": [").Append(w).Append(", ").Append(h).Append("], \"mmPerPixel\": ")
                    .Append((metres.x * 1000f / w).ToString("0.##", CultureInfo.InvariantCulture)).Append(" }");
            }
            if (entries.Count == 0)
                return false;
            info.Append("\n    },\n    \"windows\": ").Append(v.Profile != null && v.Profile.WrapWindows ? "true" : "false")
                .Append(",\n    \"doors\": ").Append(v.Profile != null && v.Profile.WrapDoors ? "true" : "false");
            // the doors' leaves from the front, over every section
            byte[] leaves = v.Sections.Where(m => m.IsAuthored).SelectMany(m => m.AuthoredModel.DoorLeaves).ToArray();
            if (leaves.Length > 0)
                info.Append(",\n    \"doorLeaves\": [").Append(string.Join(", ", leaves.Select(l => l.ToString(CultureInfo.InvariantCulture)))).Append("]");
            info.Append(",\n    \"sections\": ").Append(v.Sections.Count);
            if (v.Sections.Count > 1)
                info.Append(",\n    \"sectionMetres\": [").Append(string.Join(", ", v.Sections.Select(m => m.WrapLength.ToString("0.###", CultureInfo.InvariantCulture)))).Append("]");
            info.Append("\n  }");
            entries.Add(new KeyValuePair<string, byte[]>("README.txt", Encoding.UTF8.GetBytes(Readme(v, v.English))));
            entries.Add(new KeyValuePair<string, byte[]>("README-zh.txt", Encoding.UTF8.GetBytes(Readme(v, v.Chinese))));
            manifest = new WrapPackage.Manifest { Model = v.Model, Layout = v.Layout, OutlineMm = v.OutlineMm, Title = Title(v), Info = info.ToString() };
            return true;
        }

        /// <summary>The guide of the template in one language: <paramref name="text"/> reads the language file's texts (the keys "Wrap.Template.*").</summary>
        internal static string Readme(Input v, Func<string, string> text)
        {
            bool windows = v.Profile != null && v.Profile.WrapWindows, doors = v.Profile != null && v.Profile.WrapDoors;
            string[] pillars = PillarNames(v);
            var b = new StringBuilder();
            b.Append(string.Format(text("Wrap.Template.Title"), v.Model, v.Layout)).Append("\n\n")
                .Append(text("Wrap.Template.Intro")).Append('\n')
                .Append(text("Wrap.Template.Paint")).Append('\n');
            if (windows) b.Append(text("Wrap.Template.PaintWindows")).Append('\n');
            if (windows) b.Append(text("Wrap.Template.Glass")).Append('\n');
            if (doors) b.Append(text("Wrap.Template.Door")).Append('\n');
            for (int k = 0; k < pillars.Length; k++)
                b.Append(string.Format(text("Wrap.Template.Pillar"), text("Wrap.Template.Colour" + k % kPillar.Length), pillars[k])).Append('\n');
            if (v.Sections.Count > 1)
                b.Append(string.Format(text("Wrap.Template.Joint"), v.Sections.Count, SectionMetres(v))).Append('\n');
            string hidden = windows || doors ? FrontPillars(v) ? "HiddenFront" : "HiddenWindscreen" : "HiddenAll";
            b.Append(text("Wrap.Template.Trim")).Append('\n')
                .Append(string.Format(text("Wrap.Template.Hidden"), text("Wrap.Template." + hidden))).Append('\n')
                .Append(text("Wrap.Template.Outside")).Append('\n')
                .Append(text("Wrap.Template.Grid")).Append("\n\n")
                .Append(string.Format(text("Wrap.Template.Rule"), v.Model)).Append('\n');
            return b.ToString().Replace("\n", "\r\n");
        }

        private static string SectionMetres(Input v) => string.Join(" + ", v.Sections.Select(m => m.WrapLength.ToString("0.00", CultureInfo.InvariantCulture)));

        private static bool FrontPillars(Input v) => v.Profile != null && v.Profile.WrapPillars.Any(p => Array.IndexOf(p.Faces, WrapFace.Front) >= 0);

        // the pillars' names in their first order: a name's place picks its outline colour
        private static string[] PillarNames(Input v) => (v.Profile?.WrapPillars ?? new VehicleProfile.WrapPillar[0]).Select(p => p.Name).Distinct().ToArray();

        // A pillar's outline, 3 px inside its rectangle (pixels, rows from the bottom), clipped to the picture.
        private static void Outline(byte[] px, int w, int h, float x0, float y0, float x1, float y1, byte[] rgb)
        {
            const int t = 3;
            int ix0 = (int)Math.Round(x0), ix1 = (int)Math.Round(x1) - 1, iy0 = (int)Math.Round(y0), iy1 = (int)Math.Round(y1) - 1;
            for (int y = Math.Max(0, iy0); y <= Math.Min(h - 1, iy1); y++)
                for (int x = Math.Max(0, ix0); x <= Math.Min(w - 1, ix1); x++)
                    if (x < ix0 + t || x > ix1 - t || y < iy0 + t || y > iy1 - t)
                        Put(px, y * w + x, rgb);
        }

        private static void Put(byte[] px, int i, byte[] rgb)
        {
            px[i * 4] = rgb[0]; px[i * 4 + 1] = rgb[1]; px[i * 4 + 2] = rgb[2]; px[i * 4 + 3] = 255;
        }

        private static bool Is(byte[] px, int i, byte[] rgb) => px[i * 4] == rgb[0] && px[i * 4 + 1] == rgb[1] && px[i * 4 + 2] == rgb[2];

        /// <summary>
        /// A face's guide picture as RGBA (rows from the bottom): for an authored bus the projection of the model (VehicleMesh.ProjectWrap:
        /// body paint light, windows blue stripes, door pieces green, paint with something in front of it mid grey, the rest of the bus dark,
        /// outside it darker), for a generated one its layout rectangles; a line every metre, the front edge red (sides, roof). Null when no
        /// section has the face. metres: the face's size.
        /// </summary>
        internal static byte[] Guide(Input v, WrapFace face, out int w, out int h, out float2 metres)
        {
            IList<BusInterior> models = v.Sections;
            bool along = Along(face);
            List<BusInterior> parts = along ? models.ToList() : models.Where(m => m.HasWrap(face)).Take(1).ToList();
            w = h = 0;
            metres = float2.zero;
            if (parts.Count == 0)
                return null;
            float total = along ? v.Total : parts[0].WrapSize(face).x;
            float height = parts[0].WrapSize(face).y;
            metres = new float2(total, height);
            w = along ? 2048 : 1024;
            h = Math.Min(4096, Math.Max(32, (int)Math.Round(w * height / total)));
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) Put(px, i, kHidden);
            byte[] classes = null;
            float[] depth = null;
            float start = 0f;
            foreach (BusInterior m in parts)
            {
                float a = along ? start / total : 0f, s = along ? m.WrapLength / total : 1f;
                float X(float u) => face == WrapFace.Right ? 1f - (a + s * u) : a + s * u;
                if (m.IsAuthored)
                {
                    if (classes == null)
                    {
                        classes = new byte[w * h];
                        depth = new float[w * h];
                        for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;
                    }
                    m.ProjectWrap(face, classes, depth, w, h, a, s, face == WrapFace.Right);
                    start += m.WrapLength;
                    continue;
                }
                foreach (bool solid in new[] { true, false })
                    foreach (var (uv, isSolid) in m.WrapLayout(face))
                    {
                        if (isSolid != solid) continue;
                        float xa = X(uv.x), xb = X(uv.z);
                        int x0 = Math.Min(w, Math.Max(0, (int)Math.Round(Math.Min(xa, xb) * w))), x1 = Math.Min(w, Math.Max(0, (int)Math.Round(Math.Max(xa, xb) * w)));
                        int y0 = Math.Min(h, Math.Max(0, (int)Math.Round(uv.y * h))), y1 = Math.Min(h, Math.Max(0, (int)Math.Round(uv.w * h)));
                        byte[] c = solid ? kSolid : kCovered;
                        for (int y = y0; y < y1; y++)
                            for (int x = x0; x < x1; x++)
                                if (solid || Is(px, y * w + x, kSolid))
                                    Put(px, y * w + x, c);
                    }
                start += m.WrapLength;
            }
            if (classes != null)
                for (int i = 0; i < w * h; i++)
                {
                    int x = i % w, y = i / w;
                    switch (classes[i])
                    {
                        case VehicleMesh.ClassNone: Put(px, i, kOutside); break;
                        case VehicleMesh.ClassBody: Put(px, i, kSolid); break;
                        case VehicleMesh.ClassBehind: Put(px, i, kCovered); break;
                        case VehicleMesh.ClassWindow: Put(px, i, ((x + y) >> 3 & 1) == 0 ? kWindowA : kWindowB); break;
                        case VehicleMesh.ClassDoor: Put(px, i, kDoor); break;
                    }
                }
            if (parts.Any(m => m.IsAuthored))
            {
                int solid = 0;
                for (int i = 0; i < w * h; i++)
                    if (Is(px, i, kSolid)) solid++;
                if (solid < w * h * kMinCovered)
                    return null;
            }

            for (float d = 1f; d < metres.x; d += 1f)
            {
                int x = Math.Min(w - 1, Math.Max(0, (int)Math.Round(d / metres.x * w)));
                for (int y = 0; y < h; y++) Put(px, y * w + x, kGrid);
            }
            for (float d = 1f; d < metres.y; d += 1f)
            {
                int row = Math.Min(h - 1, Math.Max(0, (int)Math.Round(d / metres.y * h)));
                for (int x = 0; x < w; x++) Put(px, row * w + x, kGrid);
            }
            if (along)
            {
                int front = face == WrapFace.Right ? w - 6 : 0;
                for (int y = 0; y < h; y++)
                    for (int x = front; x < front + 6; x++)
                        Put(px, y * w + x, kFront);
                // the joints: dashes 4 px wide where a section ends and the next begins
                float joint = 0f;
                for (int k = 0; k + 1 < parts.Count; k++)
                {
                    joint += parts[k].WrapLength;
                    float u = joint / total;
                    int x0 = (int)Math.Round((face == WrapFace.Right ? 1f - u : u) * w) - 2;
                    for (int y = 0; y < h; y++)
                        if (y / 12 % 2 == 0)
                            for (int x = Math.Max(0, x0); x < Math.Min(w, x0 + 4); x++)
                                Put(px, y * w + x, kJoint);
                }
            }
            float at = 0f;
            foreach (BusInterior m in parts)
            {
                float a = along ? at / total : 0f, s = along ? m.WrapLength / total : 1f;
                at += m.WrapLength;
                VehicleProfile.WrapPillar[] pillars = m.IsAuthored ? m.AuthoredModel.WrapPillars : new VehicleProfile.WrapPillar[0];
                for (int k = 0; k < pillars.Length; k++)
                {
                    if (Array.IndexOf(pillars[k].Faces, face) < 0 || !(m.WrapRect(face, pillars[k].Min, pillars[k].Max) is float4 r))
                        continue;
                    float xa = face == WrapFace.Right ? 1f - (a + s * r.x) : a + s * r.x, xb = face == WrapFace.Right ? 1f - (a + s * r.z) : a + s * r.z;
                    int colour = Array.IndexOf(PillarNames(v), pillars[k].Name);
                    Outline(px, w, h, Math.Min(xa, xb) * w, r.y * h, Math.Max(xa, xb) * w, r.w * h, kPillar[Math.Max(0, colour) % kPillar.Length]);
                }
            }
            return px;
        }

        // ---------------------------------------------------------------- PNG

        /// <summary>An 8-bit RGBA PNG from pixels w x h x 4, rows from the bottom (as Unity keeps them).</summary>
        internal static byte[] Png(int w, int h, byte[] rgba)
        {
            var raw = new byte[(w * 4 + 1) * h];
            for (int y = 0; y < h; y++)
            {
                int src = (h - 1 - y) * w * 4, dst = y * (w * 4 + 1);
                raw[dst] = 0;
                Buffer.BlockCopy(rgba, src, raw, dst + 1, w * 4);
            }
            byte[] z;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    deflate.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (byte value in raw)
                {
                    a = (a + value) % 65521;
                    b = (b + a) % 65521;
                }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                z = ms.ToArray();
            }
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                BigEndian(ihdr, 0, (uint)w); BigEndian(ihdr, 4, (uint)h);
                ihdr[8] = 8; ihdr[9] = 6;
                Chunk(png, "IHDR", ihdr);
                Chunk(png, "IDAT", z);
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        private static void BigEndian(byte[] b, int offset, uint value)
        {
            b[offset] = (byte)(value >> 24); b[offset + 1] = (byte)(value >> 16); b[offset + 2] = (byte)(value >> 8); b[offset + 3] = (byte)value;
        }

        private static uint[] s_Crc;

        private static void Chunk(Stream s, string type, byte[] data)
        {
            if (s_Crc == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                s_Crc = table;
            }
            var length = new byte[4];
            BigEndian(length, 0, (uint)data.Length);
            s.Write(length, 0, 4);
            var body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            s.Write(body, 0, body.Length);
            uint crc = 0xFFFFFFFFu;
            foreach (byte value in body) crc = s_Crc[(crc ^ value) & 0xFF] ^ (crc >> 8);
            var end = new byte[4];
            BigEndian(end, 0, crc ^ 0xFFFFFFFFu);
            s.Write(end, 0, 4);
        }
    }
}
