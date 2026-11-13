using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// A body wrap as a file (BusSkins loads it): Wraps/&lt;model&gt;/&lt;name&gt;.zip with the pictures of the faces it covers (left, right,
    /// front, rear, roof .png) and wrap.json, which names the vehicle model it was made for and the layout version of that
    /// model's wrap surfaces. A wrap of one model is never used on another (its pictures are drawn to the model's own faces).
    /// Everything here is pure (no Unity), so the tests run it: the zip reading with its limits, the manifest checks, and the
    /// picture work the game does once per load (the white outline round see-through windows and doors, the colour of the nearest painted pixel put
    /// transparent pixels).
    /// </summary>
    internal static class WrapPackage
    {
        internal const string Format = "cdlb-wrap";
        internal const int Version = 1;
        internal const string ManifestName = "wrap.json";
        internal static readonly string[] Faces = { "left", "right", "front", "rear", "roof" };
        internal static readonly string[] Pictures = { "left.png", "right.png", "front.png", "rear.png", "roof.png" };
        internal const float DefaultOutlineMm = VehicleProfile.DefaultWrapOutlineMm;
        internal const int MaxEntries = 32, MaxEntryBytes = 96 << 20, MaxTotalBytes = 320 << 20, MaxOutlinePixels = 24;

        internal sealed class Manifest
        {
            internal string Model;
            internal int Layout;
            internal float OutlineMm = DefaultOutlineMm;
            internal string Title;
            /// <summary>A JSON object written as "info": the template's notes for the painter (face sizes, doors, the alpha rule); ignored when read.</summary>
            internal string Info;
        }

        internal sealed class Package
        {
            internal Manifest Manifest;
            internal readonly byte[][] Faces = new byte[WrapPackage.Faces.Length][];
            internal readonly byte[][] Guides = new byte[WrapPackage.Faces.Length][];
            internal byte[] Thumb;
            internal DateTime Time;
            internal bool Any { get { foreach (byte[] f in Faces) if (f != null) return true; return false; } }
        }

        internal sealed class Error : Exception
        {
            internal Error(string message) : base(message) { }
        }

        // ---------------------------------------------------------------- names and folders

        /// <summary>A model or wrap name that is safe as a file name (letters, digits, - _ . and space; not a path).</summary>
        internal static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name == "." || name == ".." || name[0] == ' ' || name[name.Length - 1] == ' ' || name[name.Length - 1] == '.')
                return false;
            foreach (char c in name)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.' || c == ' ' || c > 127 && char.IsLetter(c)))
                    return false;
            return true;
        }

        /// <summary>The folder of a model's wraps under the wraps root (null for a name that is not a valid model).</summary>
        internal static string ModelFolder(string root, string model) =>
            JsonModel(model) ? Path.Combine(root, model) : null;

        internal static string ZipPath(string root, string model, string name) =>
            JsonModel(model) && ValidName(name) ? Path.Combine(root, model, name + ".zip") : null;

        private static bool JsonModel(string model) => VehicleProfileJson.ModelName(model);

        // ---------------------------------------------------------------- the manifest

        internal static string ManifestJson(Manifest m)
        {
            var b = new StringBuilder("{\n");
            b.Append("  \"format\": \"").Append(Format).Append("\",\n");
            b.Append("  \"version\": ").Append(Version).Append(",\n");
            b.Append("  \"model\": ").Append(Quote(m.Model)).Append(",\n");
            b.Append("  \"layout\": ").Append(m.Layout).Append(",\n");
            b.Append("  \"outlineMm\": ").Append(m.OutlineMm.ToString("0.###", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(m.Title)) b.Append(",\n  \"title\": ").Append(Quote(m.Title));
            if (!string.IsNullOrEmpty(m.Info)) b.Append(",\n  \"info\": ").Append(m.Info);
            return b.Append("\n}\n").ToString();
        }

        internal static Manifest ParseManifest(string text)
        {
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(text) as Dictionary<string, object>; }
            catch (MiniJson.FormatError e) { throw new Error("wrap.json is not valid JSON: " + e.Message); }
            if (root == null) throw new Error("wrap.json is not a JSON object");
            if (!(root.TryGetValue("format", out object f) && f as string == Format)) throw new Error("wrap.json is not a Bus Driver wrap (format)");
            int version = Int(root, "version", 1, 1000);
            if (version > Version) throw new Error($"the wrap is version {version}; this Bus Driver reads {Version}: it needs a newer Bus Driver");
            string model = root.TryGetValue("model", out object m) ? m as string : null;
            if (!JsonModel(model)) throw new Error("wrap.json names no valid vehicle model");
            var manifest = new Manifest { Model = model, Layout = Int(root, "layout", 0, 100000) };
            if (root.TryGetValue("outlineMm", out object o) && o is MiniJson.Number n)
            {
                float mm = n.Float;
                if (float.IsNaN(mm) || mm < 0f || mm > 60f) throw new Error("wrap.json outlineMm must be 0..60");
                manifest.OutlineMm = mm;
            }
            if (root.TryGetValue("title", out object t) && t is string title && title.Length <= 80) manifest.Title = title;
            return manifest;
        }

        private static int Int(Dictionary<string, object> o, string key, int min, int max)
        {
            if (!o.TryGetValue(key, out object v) || !(v is MiniJson.Number n)) throw new Error("wrap.json: missing number " + key);
            double d = n.Double;
            if (d != Math.Floor(d) || d < min || d > max) throw new Error($"wrap.json: {key} must be an integer {min}..{max}");
            return (int)d;
        }

        private static string Quote(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        // ---------------------------------------------------------------- the zip

        /// <summary>
        /// Reads a wrap zip. Only the known top-level entries are used (the five pictures, wrap.json, thumb.png; the guide/ folder
        /// and the READMEs are for the painter); anything else is ignored. Entry names with a path, a drive or "..", too many or too
        /// big entries, or a zip bomb (an entry bigger than it says) are refused. Throws Error with the reason.
        /// </summary>
        internal static Package Read(string path, bool withGuides = false)
        {
            try
            {
                var package = new Package { Time = File.GetLastWriteTimeUtc(path) };
                using (FileStream file = File.OpenRead(path))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count > MaxEntries) throw new Error($"the zip has {zip.Entries.Count} entries (at most {MaxEntries})");
                    long total = 0;
                    byte[] manifest = null;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('\\', '/');
                        if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains("..") || name.Contains(":"))
                            throw new Error("the zip has an unsafe entry name: " + entry.FullName);
                        string lower = name.ToLowerInvariant();
                        if (name.Contains("/"))
                        {
                            // guide/<face>.png: the template's colour map, read only when asked for (the template is shown by it); the rest is ignored
                            int guide = withGuides && lower.StartsWith("guide/", StringComparison.Ordinal) ? Array.IndexOf(Pictures, lower.Substring(6)) : -1;
                            if (guide < 0) continue;
                            if (entry.Length > MaxEntryBytes) throw new Error($"{name} is {entry.Length >> 20} MB (at most {MaxEntryBytes >> 20})");
                            total += entry.Length;
                            if (total > MaxTotalBytes) throw new Error("the zip is too big");
                            package.Guides[guide] = ReadEntry(entry);
                            continue;
                        }
                        int face = Array.IndexOf(Pictures, lower);
                        bool wanted = face >= 0 || lower == ManifestName || lower == "thumb.png";
                        if (!wanted) continue;
                        if (entry.Length > MaxEntryBytes) throw new Error($"{name} is {entry.Length >> 20} MB (at most {MaxEntryBytes >> 20})");
                        total += entry.Length;
                        if (total > MaxTotalBytes) throw new Error("the zip is too big");
                        byte[] bytes = ReadEntry(entry);
                        if (face >= 0) package.Faces[face] = bytes;
                        else if (lower == "thumb.png") package.Thumb = bytes;
                        else manifest = bytes;
                    }
                    if (manifest == null) throw new Error("the zip has no wrap.json");
                    package.Manifest = ParseManifest(Encoding.UTF8.GetString(manifest));
                }
                return package;
            }
            catch (Error) { throw; }
            catch (InvalidDataException e) { throw new Error("not a valid zip: " + e.Message); }
            catch (IOException e) { throw new Error("cannot read the zip: " + e.Message); }
        }

        private static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            using (Stream s = entry.Open())
            using (var m = new MemoryStream((int)Math.Min(entry.Length, MaxEntryBytes)))
            {
                var buffer = new byte[81920];
                int n;
                long size = 0;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    size += n;
                    if (size > MaxEntryBytes || size > entry.Length) throw new Error(entry.FullName + " is bigger than it says");
                    m.Write(buffer, 0, n);
                }
                return m.ToArray();
            }
        }

        /// <summary>Writes a wrap zip (the manifest first, then every entry given: name -> bytes), replacing a file of that name.</summary>
        internal static void Write(string path, Manifest manifest, IEnumerable<KeyValuePair<string, byte[]>> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            using (FileStream file = File.Create(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add(zip, ManifestName, Encoding.UTF8.GetBytes(ManifestJson(manifest)));
                foreach (KeyValuePair<string, byte[]> e in entries)
                {
                    if (string.Equals(e.Key, ManifestName, StringComparison.OrdinalIgnoreCase) || e.Key.Contains("..") || e.Key.StartsWith("/", StringComparison.Ordinal))
                        throw new Error("bad entry name " + e.Key);
                    Add(zip, e.Key, e.Value);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream s = entry.Open()) s.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Checks a read wrap against the model driven: its model and its layout version. Null when it fits, else why it does not.
        /// </summary>
        internal static string Mismatch(Manifest wrap, string model, int layout)
        {
            if (!string.Equals(wrap.Model, model, StringComparison.Ordinal))
                return "Wrap.Err.OtherModel|" + wrap.Model + "|" + model;
            if (wrap.Layout != layout)
                return "Wrap.Err.OtherLayout|" + wrap.Layout + "|" + model + "|" + layout;
            return null;
        }

        // ---------------------------------------------------------------- the picture work

        /// <summary>The pixels (RGBA, 4 bytes each) of an area class 0 (none) / 1 (window) / 2 (door), one byte a pixel.</summary>
        internal const byte AreaNone = 0, AreaWindow = 1, AreaDoor = 2;

        // How many columns of a picture the distance passes handle together (a cache line holds 16 pixels of a row).
        private const int ColumnBlock = 16;

        /// <summary>
        /// Marks the pixels whose centre lies in a triangle given in picture coordinates (x, y in 0..1, y up) with an area class
        /// (a hair of overlap closes the seams between neighbouring triangles). area: width x height.
        /// </summary>
        internal static void FillArea(byte[] area, int width, int height, float2 a, float2 b, float2 c, byte kind)
        {
            float2 pa = new float2(a.x * width, a.y * height), pb = new float2(b.x * width, b.y * height), pc = new float2(c.x * width, c.y * height);
            float twice = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
            if (math.abs(twice) < 1e-9f) return;
            int x0 = math.clamp((int)math.floor(math.min(pa.x, math.min(pb.x, pc.x))), 0, width - 1), x1 = math.clamp((int)math.ceil(math.max(pa.x, math.max(pb.x, pc.x))), 0, width - 1);
            int y0 = math.clamp((int)math.floor(math.min(pa.y, math.min(pb.y, pc.y))), 0, height - 1), y1 = math.clamp((int)math.ceil(math.max(pa.y, math.max(pb.y, pc.y))), 0, height - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float2 p = new float2(x + .5f, y + .5f);
                    float w0 = ((pb.x - p.x) * (pc.y - p.y) - (pb.y - p.y) * (pc.x - p.x)) / twice;
                    float w1 = ((pc.x - p.x) * (pa.y - p.y) - (pc.y - p.y) * (pa.x - p.x)) / twice;
                    float w2 = 1f - w0 - w1;
                    const float e = -0.002f;
                    if (w0 >= e && w1 >= e && w2 >= e) area[y * width + x] = kind;
                }
        }

        /// <summary>
        /// The faces (WrapFace values: 0 left, 1 right, 2 front, 3 rear, 4 roof) a picture is drawn on: a side without its picture takes
        /// the other side's. have: which of the five pictures the wrap has.
        /// </summary>
        internal static int[] FacesOf(int picture, bool[] have)
        {
            switch (picture)
            {
                case 0: return have[1] ? new[] { 0 } : new[] { 0, 1 };
                case 1: return have[0] ? new[] { 1 } : new[] { 1, 0 };
                default: return new[] { picture };
            }
        }

        /// <summary>
        /// What the game does to a decoded picture once, before it is drawn (the user's PNG is not changed): where the picture has see-through
        /// pixels, the white outline round them inside the window and door areas it covers (areas: picture-space triangles with their
        /// class, from the model), then the colour of the nearest painted pixel put under the see-through ones (Decontaminate). rgba: width x height x 4, rows from the bottom.
        /// metres: the face's width in metres (for the outline's width in pixels). Returns whether the picture had see-through pixels.
        /// </summary>
        internal static bool Prepare(byte[] rgba, int width, int height, IEnumerable<(byte kind, float2x3 uv)> areas, float outlineMm, float metres,
            out int outlined, out int recoloured)
        {
            outlined = recoloured = 0;
            bool transparent = false;
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] < 255) { transparent = true; break; }
            if (!transparent)
                return false;
            var area = new byte[width * height];
            bool any = false;
            foreach ((byte kind, float2x3 uv) in areas)
            {
                FillArea(area, width, height, uv.c0, uv.c1, uv.c2, kind);
                any = true;
            }
            if (any)
                outlined = OutlineAreas(rgba, width, height, area, OutlineRadius(outlineMm, metres, width));
            recoloured = Decontaminate(rgba, width, height);
            return true;
        }

        /// <summary>The outline's width in pixels: <paramref name="mm"/> on a face <paramref name="metres"/> wide that is <paramref name="width"/> pixels across.</summary>
        internal static int OutlinePixels(float mm, float metres, int width)
        {
            if (mm <= 0f || metres <= 0f || width <= 0) return 0;
            return Math.Min(MaxOutlinePixels, Math.Max(1, (int)Math.Round(mm / 1000f * width / metres)));
        }

        /// <summary>The outline's width in pixels, not rounded (0 for no outline): <paramref name="mm"/> on a face <paramref name="metres"/> wide that is <paramref name="width"/> pixels across.</summary>
        internal static float OutlineRadius(float mm, float metres, int width)
        {
            if (mm <= 0f || metres <= 0f || width <= 0) return 0f;
            return Math.Min(MaxOutlinePixels, mm / 1000f * width / metres);
        }

        /// <summary>
        /// The white edge of a see-through hole in a wrap film. Inside a window or door area, the pixels that are not painted (alpha under
        /// half) within <paramref name="radius"/> pixels of the painted ones get a white outline: the distance to the painted pixels is the
        /// exact Euclidean one (so the outline is round and as smooth as the picture's own edge, not a stair-step of squares), the outline's
        /// outer edge is anti-aliased over one pixel, and it lies under whatever the picture already has there (the picture's own soft edge
        /// stays, over the white). Painted pixels and everything outside the areas are not touched. rgba: width x height x 4 bytes, rows
        /// bottom to top as Unity reads them; area: width x height. Returns the number of pixels changed.
        /// </summary>
        internal static int OutlineAreas(byte[] rgba, int width, int height, byte[] area, float radius)
        {
            if (radius <= 0.25f || rgba == null || area == null || rgba.Length != width * height * 4 || area.Length != width * height) return 0;
            const float Far = 1e12f;
            int n = width * height;
            bool anyArea = false;
            for (int i = 0; i < n && !anyArea; i++) anyArea = area[i] != AreaNone;
            if (!anyArea) return 0;
            // squared distance to the nearest painted pixel (Felzenszwalb-Huttenlocher, exact), columns then rows
            var d2 = new float[n];
            int longest = Math.Max(width, height);
            var f = new float[longest];
            var g = new float[longest];
            var v = new int[longest];
            var z = new float[longest + 1];
            // The columns, ColumnBlock at a time: a column read one pixel a row down the picture touched a new cache line each time
            // (the stride is a whole row), so the rows of a block of columns are read and written together. Each column's own
            // arithmetic is unchanged, so the result is the same bit for bit.
            var inBlock = new float[ColumnBlock][];
            var outBlock = new float[ColumnBlock][];
            for (int b = 0; b < ColumnBlock; b++) { inBlock[b] = new float[height]; outBlock[b] = new float[height]; }
            for (int x0 = 0; x0 < width; x0 += ColumnBlock)
            {
                int columns = Math.Min(ColumnBlock, width - x0);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width + x0;
                    for (int b = 0; b < columns; b++) inBlock[b][y] = rgba[(row + b) * 4 + 3] >= 128 ? 0f : Far;
                }
                for (int b = 0; b < columns; b++) DistanceLine(inBlock[b], height, v, z, outBlock[b]);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width + x0;
                    for (int b = 0; b < columns; b++) d2[row + b] = outBlock[b][y];
                }
            }
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) f[x] = d2[y * width + x];
                DistanceLine(f, width, v, z, g);
                for (int x = 0; x < width; x++) d2[y * width + x] = g[x];
            }
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                if (area[i] == AreaNone || rgba[i * 4 + 3] >= 128) continue;
                // a painted pixel's centre is 0 away, the painted region's edge half a pixel beyond it: the outline reaches radius pixels past the edge
                float cover = Math.Min(1f, Math.Max(0f, radius + 1f - (float)Math.Sqrt(d2[i])));
                if (cover <= 0f) continue;
                float own = rgba[i * 4 + 3] / 255f;              // the picture's own soft edge lies over the white
                float outA = own + cover * (1f - own);
                for (int c = 0; c < 3; c++)
                    rgba[i * 4 + c] = (byte)Math.Round((rgba[i * 4 + c] * own + 255f * cover * (1f - own)) / outA);
                rgba[i * 4 + 3] = (byte)Math.Round(outA * 255f);
                changed++;
            }
            return changed;
        }

        // The squared distance transform of one line (f: 0 on the painted pixels, a large value elsewhere); v and z are scratch.
        private static void DistanceLine(float[] f, int n, int[] v, float[] z, float[] outD)
        {
            int k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                float dq = q - v[k];
                outD[q] = dq * dq + f[v[k]];
            }
        }

        /// <summary>
        /// Cleans the colour under the see-through pixels of a picture (alpha unchanged). Every pixel with alpha under <paramref name="clearAlpha"/>
        /// takes the colour of the nearest pixel with alpha of <paramref name="sourceAlpha"/> or more (the exact nearest one, at any distance).
        /// A cut-out picture keeps garbage under its transparent pixels (a third-party picture: 91 % black, a few per cent every colour of the
        /// rainbow, measured on two wraps), and the graphics card mixes the texels' colours without looking at their alpha: where the picture's edge
        /// is filtered or its scaled-down levels (mips) are made, that garbage runs into the visible edge as dark and coloured fringes and specks.
        /// With the nearest painted colour there instead, the mix is the picture's own colour. Pixels from clearAlpha up to sourceAlpha (the soft
        /// edge, glows) keep their colour. A picture without a painted pixel is left as it is. rgba: width x height x 4. Returns the pixels changed.
        /// </summary>
        internal static int Decontaminate(byte[] rgba, int width, int height, int sourceAlpha = 192, int clearAlpha = 64)
        {
            if (rgba == null || rgba.Length != width * height * 4 || width <= 0 || height <= 0) return 0;
            const float Far = 1e12f;
            int n = width * height, longest = Math.Max(width, height);
            bool anySource = false, anyClear = false;
            for (int i = 0; i < n; i++)
            {
                int a = rgba[i * 4 + 3];
                anySource |= a >= sourceAlpha;
                anyClear |= a < clearAlpha;
            }
            if (!anySource || !anyClear) return 0;
            var f = new float[longest];
            var outD = new float[longest];
            var outI = new int[longest];
            var v = new int[longest];
            var z = new float[longest + 1];
            var vertical = new float[n];         // squared distance to the nearest source in the pixel's column
            var sourceRow = new int[n];          // that source's row
            // the columns ColumnBlock at a time, as in OutlineAreas (the same arithmetic per column, so the same result)
            var inBlock = new float[ColumnBlock][];
            var distBlock = new float[ColumnBlock][];
            var indexBlock = new int[ColumnBlock][];
            for (int b = 0; b < ColumnBlock; b++) { inBlock[b] = new float[height]; distBlock[b] = new float[height]; indexBlock[b] = new int[height]; }
            for (int x0 = 0; x0 < width; x0 += ColumnBlock)
            {
                int columns = Math.Min(ColumnBlock, width - x0);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width + x0;
                    for (int b = 0; b < columns; b++) inBlock[b][y] = rgba[(row + b) * 4 + 3] >= sourceAlpha ? 0f : Far;
                }
                for (int b = 0; b < columns; b++) DistanceLineNearest(inBlock[b], height, v, z, distBlock[b], indexBlock[b]);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width + x0;
                    for (int b = 0; b < columns; b++)
                    {
                        vertical[row + b] = distBlock[b][y];
                        sourceRow[row + b] = indexBlock[b][y];
                    }
                }
            }
            int changed = 0;
            var pick = new int[n];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) f[x] = vertical[y * width + x];
                DistanceLineNearest(f, width, v, z, outD, outI);
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    pick[i] = -1;
                    if (rgba[i * 4 + 3] >= clearAlpha || outD[x] >= Far * 0.5f) continue;
                    int sx = outI[x];
                    pick[i] = sourceRow[y * width + sx] * width + sx;
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (pick[i] < 0) continue;
                int s = pick[i] * 4;
                if (rgba[i * 4] != rgba[s] || rgba[i * 4 + 1] != rgba[s + 1] || rgba[i * 4 + 2] != rgba[s + 2]) changed++;
                rgba[i * 4] = rgba[s]; rgba[i * 4 + 1] = rgba[s + 1]; rgba[i * 4 + 2] = rgba[s + 2];
            }
            return changed;
        }

        /// <summary>A picture (w x h x 4, rows from the bottom) scaled to tw x th: each pixel the mean of the source pixels it covers.</summary>
        internal static byte[] ScaleDown(byte[] rgba, int w, int h, int tw, int th)
        {
            var result = new byte[tw * th * 4];
            for (int ty = 0; ty < th; ty++)
            {
                int y0 = (int)((long)ty * h / th), y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * h / th));
                for (int tx = 0; tx < tw; tx++)
                {
                    int x0 = (int)((long)tx * w / tw), x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * w / tw));
                    long r = 0, g = 0, b = 0, a = 0;
                    for (int y = y0; y < y1 && y < h; y++)
                        for (int x = x0, i = (y * w + x0) * 4; x < x1 && x < w; x++, i += 4)
                        {
                            r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; a += rgba[i + 3];
                        }
                    long count = Math.Max(1, (long)(Math.Min(y1, h) - y0) * (Math.Min(x1, w) - x0));
                    int o = (ty * tw + tx) * 4;
                    result[o] = (byte)((r + count / 2) / count); result[o + 1] = (byte)((g + count / 2) / count);
                    result[o + 2] = (byte)((b + count / 2) / count); result[o + 3] = (byte)((a + count / 2) / count);
                }
            }
            return result;
        }

        /// <summary>
        /// The columns of a face's picture one section of the bus shows, and the direction they run in, as the texture is baked (the game's
        /// body-wrap shader does not take a texture's tiling and offset, so the share and the mirroring are done on the pixels: measured by
        /// the user on 18 October, the right side of a wrapped bus came out mirrored). The picture's u is the distance from the bus's front,
        /// in 0..1 over the whole bus; the section starts at <paramref name="start"/> of it and covers <paramref name="share"/>. A picture
        /// that has its front at the left edge (the left side, the roof) shows columns [start, start + share) as they are; the right side's
        /// picture has its front at the right edge, so it shows columns [1 - start - share, 1 - start) and runs them backwards.
        /// x0 inclusive, x1 exclusive, at least one column wide and inside the picture.
        /// </summary>
        internal static void ShareColumns(int width, float start, float share, bool frontAtRight, out int x0, out int x1, out bool mirrored)
        {
            float lo = frontAtRight ? 1f - start - share : start, hi = lo + share;
            x0 = (int)Math.Round(Math.Max(0f, lo) * width);
            x1 = (int)Math.Round(Math.Min(1f, hi) * width);
            x0 = Math.Min(Math.Max(0, x0), Math.Max(0, width - 1));
            x1 = Math.Min(Math.Max(x0 + 1, x1), width);
            mirrored = frontAtRight;
        }

        /// <summary>
        /// The columns [x0, x1) of a picture (width x height RGBA bytes), as a new picture x1 - x0 wide; with <paramref name="mirrored"/> the
        /// last column of the range comes first. Rows stay as they are.
        /// </summary>
        internal static byte[] Columns(byte[] rgba, int width, int height, int x0, int x1, bool mirrored)
        {
            if (rgba == null || width <= 0 || height <= 0 || rgba.Length != width * height * 4 || x0 < 0 || x1 > width || x1 <= x0)
                throw new ArgumentException("columns outside the picture");
            int w = x1 - x0;
            var result = new byte[w * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int from = (y * width + (mirrored ? x1 - 1 - x : x0 + x)) * 4, to = (y * w + x) * 4;
                    result[to] = rgba[from]; result[to + 1] = rgba[from + 1]; result[to + 2] = rgba[from + 2]; result[to + 3] = rgba[from + 3];
                }
            }
            return result;
        }

        // DistanceLine that also reports, for every position, which input position is the nearest one (the argmin of the parabolas).
        private static void DistanceLineNearest(float[] f, int n, int[] v, float[] z, float[] outD, int[] outIndex)
        {
            int k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + (float)q * q) - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                float dq = q - v[k];
                outD[q] = dq * dq + f[v[k]];
                outIndex[q] = v[k];
            }
        }
    }
}
