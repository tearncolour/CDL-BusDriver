using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;
using Random = System.Random;

// The runtime mesh formats (SwbCodec): SWB2 round trips (positions bit for bit, normals within 0.005 degrees, triangles, order and
// swatches kept), SWB1 corners joined without rounding, the refusals of both readers, and the real packs read both ways.
static class SwbCodecTests
{
    const string Prefix = "swb codec: ";
    const double kNormalDegrees = 0.005;

    internal static void Run(Action<bool, string> check)
    {
        Octahedral(check);
        RoundTrips(check);
        Swb1Corners(check);
        Refusals(check);
        RealPacks(check);
    }

    static void Octahedral(Action<bool, string> check)
    {
        var random = new Random(7);
        double worst = 0;
        var directions = new List<float3>
        {
            new float3(0, 0, 1), new float3(0, 0, -1), new float3(1, 0, 0), new float3(-1, 0, 0), new float3(0, 1, 0), new float3(0, -1, 0),
            new float3(1, 1, 1), new float3(-1, -1, -1), new float3(1, -1, -1e-7f), new float3(-0f, -0f, -1), new float3(1e-30f, 0, -1),
        };
        for (int i = 0; i < 200000; i++)
            directions.Add(new float3((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1)));
        foreach (float3 d in directions)
        {
            if (math.lengthsq(d) < 1e-12f) continue;
            SwbCodec.OctEncode(d.x, d.y, d.z, out short u, out short v);
            SwbCodec.OctDecode(u, v, out float x, out float y, out float z);
            worst = Math.Max(worst, Degrees(math.normalize(d), new float3(x, y, z)));
        }
        check(worst <= kNormalDegrees, Prefix + $"octahedral normals within {kNormalDegrees} degrees (worst {worst:0.00000} over {directions.Count})");
        bool unit = true;
        foreach (short u in new short[] { short.MinValue, -32767, -1, 0, 1, short.MaxValue })
            foreach (short v in new short[] { short.MinValue, -32767, -1, 0, 1, short.MaxValue })
            {
                SwbCodec.OctDecode(u, v, out float x, out float y, out float z);
                unit &= math.abs(math.length(new float3(x, y, z)) - 1f) < 1e-5f;
            }
        check(unit, Prefix + "every extreme octahedral code decodes to a unit direction");
    }

    // A part of random triangles: corners drawn from a pool (so vertices are shared), tiny triangles among them, every swatch.
    static SwbCodec.Part RandomPart(Random random, int triangles, int pool, bool tiny)
    {
        var positions = new float[3 * pool];
        var normals = new float[3 * pool];
        for (int v = 0; v < pool; v++)
        {
            for (int c = 0; c < 3; c++)
            {
                double p = (random.NextDouble() * 2 - 1) * 6;
                positions[3 * v + c] = tiny && v > 0 && random.Next(3) == 0 ? positions[3 * (v - 1) + c] + (float)(random.NextDouble() * 1e-5) : (float)p;
                normals[3 * v + c] = (float)(random.NextDouble() * 2 - 1);
            }
            if (random.Next(10) == 0) { normals[3 * v] = 0f; normals[3 * v + 1] = 0f; normals[3 * v + 2] = random.Next(2) == 0 ? -1f : 1f; }
            if (normals[3 * v] == 0 && normals[3 * v + 1] == 0 && normals[3 * v + 2] == 0) normals[3 * v + 2] = 1f;
        }
        var indices = new int[3 * triangles];
        for (int i = 0; i < indices.Length; i++) indices[i] = random.Next(pool);
        var swatches = new byte[triangles];
        for (int t = 0; t < triangles; t++) swatches[t] = (byte)random.Next((int)Sw.Count);
        return new SwbCodec.Part(positions, normals, indices, swatches);
    }

    static SwbCodec.Part Swb2Trip(SwbCodec.Part part)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true)) SwbCodec.WriteSwb2(writer, part);
        stream.Position = 0;
        int budget = SwbCodec.MaxTriangles;
        var reader = new SwbCodec.Reader(stream);
        SwbCodec.Part read = SwbCodec.ReadPart(reader, SwbCodec.Swb2, ref budget, null);
        if (!reader.AtEnd()) throw new InvalidDataException("trailing bytes after the part");
        return read;
    }

    // Whether b is a: every triangle's swatch, its corners' positions bit for bit and normals within the tolerance.
    static bool Same(SwbCodec.Part a, SwbCodec.Part b, out string why)
    {
        why = "";
        if (a.TriangleCount != b.TriangleCount) { why = "triangle count"; return false; }
        double worst = 0;
        for (int t = 0; t < a.TriangleCount; t++)
        {
            if (a.Swatches[t] != b.Swatches[t]) { why = "swatch " + t; return false; }
            for (int k = 0; k < 3; k++)
            {
                int i = a.Indices[3 * t + k], j = b.Indices[3 * t + k];
                for (int c = 0; c < 3; c++)
                    if (SwbCodec.Bits(a.Positions[3 * i + c]) != SwbCodec.Bits(b.Positions[3 * j + c])) { why = "position of triangle " + t; return false; }
                worst = Math.Max(worst, Degrees(math.normalize(new float3(a.Normals[3 * i], a.Normals[3 * i + 1], a.Normals[3 * i + 2])),
                    new float3(b.Normals[3 * j], b.Normals[3 * j + 1], b.Normals[3 * j + 2])));
            }
        }
        why = $"normals within {worst:0.00000} deg";
        return worst <= kNormalDegrees;
    }

    static void RoundTrips(Action<bool, string> check)
    {
        var random = new Random(11);
        bool all = true; string last = "";
        for (int round = 0; round < 40 && all; round++)
        {
            SwbCodec.Part part = RandomPart(random, random.Next(1, 3000), random.Next(1, 4000), round % 2 == 0);
            SwbCodec.Part read = Swb2Trip(part);
            all = Same(part, read, out last) && read.VertexCount <= part.VertexCount;
        }
        check(all, Prefix + "random parts round trip through SWB2 (" + last + ")");

        SwbCodec.Part big = RandomPart(random, 40000, 120000, false);
        SwbCodec.Part bigRead = Swb2Trip(big);
        bool bigSame = Same(big, bigRead, out string bigWhy);
        check(bigRead.VertexCount > ushort.MaxValue && bigSame, Prefix + $"a part of {bigRead.VertexCount} vertices (four-byte indices) round trips ({bigWhy})");

        var empty = new SwbCodec.Part(new float[0], new float[0], new int[0], new byte[0]);
        SwbCodec.Part emptyRead = Swb2Trip(empty);
        check(emptyRead.TriangleCount == 0 && emptyRead.VertexCount == 0, Prefix + "an empty part round trips");

        // corners that differ only below the normal's precision are one vertex in SWB2; positions that differ in one bit are two
        float one = 1f, next = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(1f) + 1);
        var close = new SwbCodec.Part(new[] { 0f, 0f, 0f, one, 0f, 0f, 0f, one, 0f, 0f, 0f, 0f, next, 0f, 0f },
            new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 1e-7f, 0f, 1f, 0f, 0f, 1f }, new[] { 0, 1, 2, 3, 4, 2 }, new byte[] { 1, 2 });
        SwbCodec.Part closeRead = Swb2Trip(close);
        check(closeRead.VertexCount == 4 && Same(close, closeRead, out _), Prefix + $"near-equal normals merge, a one-bit position step does not ({closeRead.VertexCount} vertices)");
    }

    // SWB1 bytes of one part: triangles of (swatch, three corners of position and normal).
    static byte[] Swb1Part(params (byte swatch, float[] corners)[] triangles)
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(triangles.Length);
            foreach ((byte swatch, float[] corners) in triangles)
            {
                w.Write(swatch);
                foreach (float f in corners) w.Write(f);
            }
        }
        return stream.ToArray();
    }

    static SwbCodec.Part ReadSwb1(byte[] bytes, int budget = SwbCodec.MaxTriangles)
    {
        var reader = new SwbCodec.Reader(new MemoryStream(bytes));
        SwbCodec.Part part = SwbCodec.ReadPart(reader, SwbCodec.Swb1, ref budget, new SwbCodec.CornerMerge());
        if (!reader.AtEnd()) throw new InvalidDataException("trailing bytes");
        return part;
    }

    static float[] Corners(params float[] values) => values;

    static void Swb1Corners(Action<bool, string> check)
    {
        float[] a = { 0, 0, 0, 0, 0, 1 }, b = { 1, 0, 0, 0, 0, 1 }, c = { 0, 1, 0, 0, 0, 1 }, d = { 1, 1, 0, 0, 0, 1 };
        float[] cNormal = { 0, 1, 0, 0, 0.6f, 0.8f };
        SwbCodec.Part part = ReadSwb1(Swb1Part((3, a.Concat(b).Concat(c).ToArray()), (4, b.Concat(d).Concat(c).ToArray()),
            (5, a.Concat(b).Concat(cNormal).ToArray())));
        check(part.TriangleCount == 3 && part.VertexCount == 5 && part.Indices.SequenceEqual(new[] { 0, 1, 2, 1, 3, 2, 0, 1, 4 }) &&
              part.Swatches.SequenceEqual(new byte[] { 3, 4, 5 }),
            Prefix + "SWB1 corners equal in all six floats are one vertex, a corner with another normal is its own");
        float minus = -0f;
        SwbCodec.Part signed = ReadSwb1(Swb1Part((1, a.Concat(b).Concat(Corners(minus, 1, 0, 0, 0, 1)).Concat(Corners()).ToArray())));
        check(signed.VertexCount == 3 && SwbCodec.Bits(signed.Positions[6]) == SwbCodec.Bits(-0f),
            Prefix + "SWB1 keeps -0 as it is (bits, not values, are compared)");
    }

    static bool Refuses(Func<object> read)
    {
        try { read(); return false; }
        catch (InvalidDataException) { return true; }
        catch (EndOfStreamException) { return true; }
    }

    // SWB2 bytes of a part written by hand: counts, then raw planes as given.
    static byte[] Swb2Raw(int triangles, int vertices, byte[] body)
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream)) { w.Write(triangles); w.Write(vertices); w.Write(body); }
        return stream.ToArray();
    }

    static SwbCodec.Part ReadSwb2(byte[] bytes, int budget = SwbCodec.MaxTriangles)
    {
        var reader = new SwbCodec.Reader(new MemoryStream(bytes));
        return SwbCodec.ReadPart(reader, SwbCodec.Swb2, ref budget, null);
    }

    static byte[] Planes(float[] positions, short[] octs, int[] distances, byte[] swatches)
    {
        int vertices = positions.Length / 3, corners = distances.Length, width = vertices <= ushort.MaxValue ? 2 : 4;
        var body = new List<byte>();
        for (int b = 0; b < 12; b++)
            for (int v = 0; v < vertices; v++) body.Add((byte)(SwbCodec.Bits(positions[3 * v + b / 4]) >> 8 * (b % 4)));
        for (int b = 0; b < 4; b++)
            for (int v = 0; v < vertices; v++) body.Add((byte)(octs[2 * v + b / 2] >> 8 * (b % 2)));
        for (int b = 0; b < width; b++)
            for (int c = 0; c < corners; c++) body.Add((byte)(distances[c] >> 8 * b));
        body.AddRange(swatches);
        return body.ToArray();
    }

    static void Refusals(Action<bool, string> check)
    {
        float[] a = { 0, 0, 0, 0, 0, 1 }, b = { 1, 0, 0, 0, 0, 1 }, c = { 0, 1, 0, 0, 0, 1 };
        float[] good = a.Concat(b).Concat(c).ToArray();
        float[] Bad(int at, float value) { var x = (float[])good.Clone(); x[at] = value; return x; }
        (string what, byte[] bytes, int budget)[] swb1 =
        {
            ("a NaN position", Swb1Part((1, Bad(0, float.NaN))), SwbCodec.MaxTriangles),
            ("an infinite position", Swb1Part((1, Bad(7, float.PositiveInfinity))), SwbCodec.MaxTriangles),
            ("a position over 100 m", Swb1Part((1, Bad(1, 100.5f))), SwbCodec.MaxTriangles),
            ("a zero normal", Swb1Part((1, Bad(5, 0f))), SwbCodec.MaxTriangles),
            ("a normal component over 2", Swb1Part((1, Bad(3, 2.5f))), SwbCodec.MaxTriangles),
            ("more triangles than the file may hold", Swb1Part((1, good), (1, good)), 1),
            ("a negative triangle count", BitConverter.GetBytes(-1), SwbCodec.MaxTriangles),
            ("a truncated part", Swb1Part((1, good)).Take(30).ToArray(), SwbCodec.MaxTriangles),
        };
        foreach ((string what, byte[] bytes, int budget) in swb1)
            check(Refuses(() => ReadSwb1(bytes, budget)), Prefix + "SWB1 refuses " + what);

        float[] p = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        short[] o = { 0, 32767, 0, 32767, 0, 32767 };
        byte[] body = Planes(p, o, new[] { 0, 0, 0 }, new byte[] { 2 });
        check(!Refuses(() => ReadSwb2(Swb2Raw(1, 3, body))), Prefix + "a hand-written SWB2 part reads");
        float[] nan = { 0, 0, 0, float.NaN, 0, 0, 0, 1, 0 };
        (string what, byte[] bytes, int budget)[] swb2 =
        {
            ("more vertices than corners", Swb2Raw(1, 4, Planes(new float[12], new short[8], new[] { 0, 0, 0 }, new byte[] { 2 })), SwbCodec.MaxTriangles),
            ("vertices without triangles", Swb2Raw(0, 1, new byte[0]), SwbCodec.MaxTriangles),
            ("triangles without vertices", Swb2Raw(1, 0, new byte[0]), SwbCodec.MaxTriangles),
            ("a negative vertex count", Swb2Raw(1, -3, body), SwbCodec.MaxTriangles),
            ("an index to a vertex not yet seen", Swb2Raw(1, 3, Planes(p, o, new[] { 0, 2, 0 }, new byte[] { 2 })), SwbCodec.MaxTriangles),
            ("an index past the vertices", Swb2Raw(1, 2, Planes(new float[6], new short[4], new[] { 0, 0, 0 }, new byte[] { 2 })), SwbCodec.MaxTriangles),
            ("vertices no corner uses", Swb2Raw(1, 3, Planes(p, o, new[] { 0, 1, 1 }, new byte[] { 2 })), SwbCodec.MaxTriangles),
            ("a NaN position", Swb2Raw(1, 3, Planes(nan, o, new[] { 0, 0, 0 }, new byte[] { 2 })), SwbCodec.MaxTriangles),
            ("more triangles than the file may hold", Swb2Raw(2, 3, body), 1),
            ("a truncated part", Swb2Raw(1, 3, body.Take(body.Length - 1).ToArray()), SwbCodec.MaxTriangles),
        };
        foreach ((string what, byte[] bytes, int budget) in swb2)
            check(Refuses(() => ReadSwb2(bytes, budget)), Prefix + "SWB2 refuses " + what);
    }

    // Every pack's SWB1 mesh converted to SWB2 beside a copy of its profile (temporary folder): the same triangles and corners, LOD by
    // LOD and part by part, as the SWB1 mesh, and the models built from both draw the same meshes.
    static void RealPacks(Action<bool, string> check)
    {
        string temp = Path.Combine(Path.GetTempPath(), "cdlc-swb2-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        try
        {
            foreach (string model in new[] { "SWB6710EV01G", "SWB6109EV12G", "WG6980A" })
            {
                VehicleProfile one = TestPacks.Profile(model);
                if (!one.MeshPath.EndsWith(".swb1.gz", StringComparison.Ordinal)) { check(true, Prefix + model + " pack already ships SWB2"); continue; }
                VehicleProfile two = Swb2Copy(model, one, temp);
                VehicleMesh a = one.Mesh, b = two.Mesh;
                bool same = a.PartCount == b.PartCount && a.Eye.Equals(b.Eye);
                var ca = new List<float3>(); var cb = new List<float3>();
                for (int lod = 0; lod <= MeshKit.MaxLod && same; lod++)
                    for (int part = 0; part < a.PartCount && same; part++)
                    {
                        ca.Clear(); cb.Clear();
                        a.Corners(part, lod, ca); b.Corners(part, lod, cb);
                        same = a.TriangleCount(part, lod) == b.TriangleCount(part, lod) && ca.Count == cb.Count;
                        for (int i = 0; i < ca.Count && same; i++)
                            same = SwbCodec.Bits(ca[i].x) == SwbCodec.Bits(cb[i].x) && SwbCodec.Bits(ca[i].y) == SwbCodec.Bits(cb[i].y) && SwbCodec.Bits(ca[i].z) == SwbCodec.Bits(cb[i].z);
                    }
                check(same, Prefix + model + ": its SWB2 mesh has every SWB1 triangle and corner, bit for bit, in every LOD and part");
                check(SameModel(one, two, out string why), Prefix + model + ": the model built from SWB2 draws what the SWB1 one does (" + why + ")");
                Console.WriteLine($"SWB2_REAL {model} {new FileInfo(one.MeshPath).Length} -> {new FileInfo(two.MeshPath).Length} bytes; {why}");
            }
        }
        finally { Directory.Delete(temp, true); }
    }

    // A copy of the pack's profile naming an SWB2 conversion of its mesh, in folder, registered as a model of its own.
    internal static VehicleProfile Swb2Copy(string model, VehicleProfile one, string folder)
    {
        string name = model + ".swb2.gz", path = Path.Combine(folder, name);
        ToSwb2(one.MeshPath, path);
        string hash = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        string text = File.ReadAllText(TestPacks.ProfilePath(model))
            .Replace("\"" + model + ".swb1.gz\"", "\"" + name + "\"")
            .Replace("\"" + one.MeshSha256 + "\"", "\"" + hash + "\"");
        File.WriteAllText(Path.Combine(folder, model + ".cdlb.json"), text);
        return VehicleModels.Register(VehicleProfileJson.Load(Path.Combine(folder, model + ".cdlb.json")));
    }

    // SWB1 to SWB2 with the codec, as Tools/VehiclePack/MeshConvert writes it (without its checks: the comparisons here are the checks).
    internal static void ToSwb2(string swb1, string swb2)
    {
        var reader = new SwbCodec.Reader(new GZipStream(File.OpenRead(swb1), CompressionMode.Decompress));
        if (reader.ReadUInt32() != SwbCodec.Swb1) throw new InvalidDataException(swb1 + " is not SWB1");
        int lods = reader.ReadInt32(), count = reader.ReadInt32();
        var head = new int[3 + 9 * count];
        for (int i = 0; i < head.Length; i++) head[i] = reader.ReadInt32();
        int budget = SwbCodec.MaxTriangles;
        var merge = new SwbCodec.CornerMerge();
        using (var gzip = new GZipStream(File.Create(swb2), CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(SwbCodec.Swb2); writer.Write(lods); writer.Write(count);
            foreach (int bits in head) writer.Write(bits);
            for (int i = 0; i < lods * count; i++) SwbCodec.WriteSwb2(writer, SwbCodec.ReadPart(reader, SwbCodec.Swb1, ref budget, merge));
        }
    }

    // The models built from two profiles. Every part but the wrap faces: the same layout (vertices, triangles, finishes), positions and UVs
    // bit for bit, normals within the codec tolerance. The wrap faces are cut where the surface normal changes its dominant axis, so a
    // normal that moved by thousandths of a degree can move a cut by a fraction of a millimetre and add or drop a sliver: for them the
    // area per face and LOD must agree to kWrapArea, and the outline (the vertices bounding box) to kWrapBounds.
    const double kWrapArea = 1e-4, kWrapBounds = 1e-3;

    internal static bool SameModel(VehicleProfile one, VehicleProfile two, out string why)
    {
        KitModelData a = KitBuild.BuildAll(new BusInterior(one.Spec)), b = KitBuild.BuildAll(new BusInterior(two.Spec));
        why = a.Error ?? b.Error ?? "";
        if (a.Error != null || b.Error != null || a.Parts.Length != b.Parts.Length) { why += " parts"; return false; }
        double worst = 0, area = 0, bounds = 0;
        long vertices = 0;
        int wrapTris = 0, wrapTrisOther = 0;
        for (int part = 0; part < a.Parts.Length; part++)
        {
            bool wrap = part >= BusInterior.WrapLeft && part <= BusInterior.WrapRoof;
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            {
                KitMeshData x = a.Parts[part].Data[lod], y = b.Parts[part].Data[lod];
                if ((x == null) != (y == null)) { why = $"part {part} LOD {lod} missing"; return false; }
                if (x == null) continue;
                if (wrap)
                {
                    double ax = Area(x), ay = Area(y);
                    area = Math.Max(area, Math.Abs(ax - ay) / Math.Max(ax, 1e-9));
                    bounds = Math.Max(bounds, math.cmax(math.abs(x.Bounds.Min - y.Bounds.Min)));
                    bounds = Math.Max(bounds, math.cmax(math.abs(x.Bounds.Max - y.Bounds.Max)));
                    wrapTris += x.Tris; wrapTrisOther += y.Tris;
                    continue;
                }
                if (x.Verts != y.Verts || x.Tris != y.Tris || !x.Idx.SequenceEqual(y.Idx) || x.FinMask != y.FinMask) { why = $"part {part} LOD {lod} layout"; return false; }
                for (int i = 0; i < x.Verts; i++)
                {
                    if (SwbCodec.Bits(x.P[i].x) != SwbCodec.Bits(y.P[i].x) || SwbCodec.Bits(x.P[i].y) != SwbCodec.Bits(y.P[i].y) || SwbCodec.Bits(x.P[i].z) != SwbCodec.Bits(y.P[i].z))
                    { why = $"part {part} LOD {lod} vertex {i} moved"; return false; }
                    if (!x.UV[i].Equals(y.UV[i])) { why = $"part {part} LOD {lod} UV {i}"; return false; }
                    worst = Math.Max(worst, Degrees(x.N[i], y.N[i]));
                }
                vertices += x.Verts;
            }
        }
        why = $"{vertices} vertices, normals within {worst:0.0000} deg; wrap faces {wrapTris} / {wrapTrisOther} triangles, area within {area:0.0e0}, outline within {bounds * 1000:0.000} mm";
        return worst <= kNormalDegrees && area <= kWrapArea && bounds <= kWrapBounds;
    }

    static double Area(KitMeshData d)
    {
        double sum = 0;
        for (int i = 0; i + 2 < d.Idx.Length; i += 3)
            sum += math.length(math.cross(d.P[d.Idx[i + 1]] - d.P[d.Idx[i]], d.P[d.Idx[i + 2]] - d.P[d.Idx[i]])) * .5;
        return sum;
    }

    static double Degrees(float3 a, float3 b)
    {
        double ax = a.x, ay = a.y, az = a.z, bx = b.x, by = b.y, bz = b.z;
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz) * 180.0 / Math.PI;
    }
}
