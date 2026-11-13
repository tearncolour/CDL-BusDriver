using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using CDLC.BusDriver;
using Colossal.AssetPipeline.Native;
using Unity.Mathematics;

// The glass and the native cab read the game's geometry files themselves, off the main thread (Systems/NativeGeometryFile.cs), instead
// of RenderPrefab.ObtainMeshes on the frame of the take-over (183 ms for the SWB6109EV12G's glass, 152 ms for the ZK6128H's, the log of
// 10 November). Checked here: the format against files written as GeometryAssetSave writes them, the cut (groups, dropped triangles, piece
// limits, only the vertices a piece uses, the triangles unchanged), and the decoder on the real packs' geometry against what their
// exporter wrote beside it (each submesh's vertex range and bounds). The failure gate that bounds the rebuilds, and the prebuild queue's
// promotion, are checked too.
static unsafe class GeometryReadTests
{
    const string Prefix = "geometry read: ";

    internal static void Run(Action<bool, string> check)
    {
        Plugin();
        Synthetic(check);
        Cut(check);
        RealPacks(check);
        Gate(check);
        Queue(check);
    }

    // The game's decompressor (zstd, meshopt) from the installed game, as the game loads it.
    internal static void Plugin()
    {
        string managed = Environment.GetEnvironmentVariable("CSII_MANAGEDPATH");
        if (string.IsNullOrEmpty(managed)) return;
        string plugin = Path.Combine(managed, "..", "Plugins", "x86_64", "PipelinePlugin.dll");
        NativeLibrary.SetDllImportResolver(typeof(NativeCompression).Assembly, (name, assembly, path) =>
            name == "PipelinePlugin" && File.Exists(plugin) ? NativeLibrary.Load(plugin) : IntPtr.Zero);
    }

    sealed class Attr
    {
        internal int Index, Format, Dimension;
        internal byte[] Data;      // vertexCount x element bytes
    }

    sealed class SynthMesh
    {
        internal int Vertices;
        internal bool Wide;
        internal int[] Indices;
        internal (int start, int count)[] SubMeshes;
        internal List<Attr> Attributes = new List<Attr>();
    }

    // A geometry file as GeometryAssetSave lays it out; compressed (meshopt then zstd per block, the writer's flags 1 | 2) or stored.
    static byte[] Write(SynthMesh[] meshes, bool compressed)
    {
        var blocksIndex = new byte[meshes.Length][];
        var blocksAttr = new byte[meshes.Length, 14][];
        for (int m = 0; m < meshes.Length; m++)
        {
            SynthMesh s = meshes[m];
            byte[] raw = s.Wide ? s.Indices.SelectMany(i => BitConverter.GetBytes((uint)i)).ToArray() : s.Indices.SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray();
            blocksIndex[m] = compressed ? CompressIndices(s.Indices, s.Vertices) : raw;
            foreach (Attr a in s.Attributes)
                blocksAttr[m, a.Index] = compressed ? CompressAttribute(a.Data, s.Vertices) : a.Data;
        }
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)1);
        w.Write(meshes.Length);
        w.Write(compressed ? 3u : 0u);
        for (int m = 0; m < meshes.Length; m++)
        {
            SynthMesh s = meshes[m];
            ulong formats = ulong.MaxValue;
            uint dims = 0;
            var sizes = new int[14];
            foreach (Attr a in s.Attributes)
            {
                formats &= ~(0xFUL << (a.Index * 4));
                formats |= (ulong)a.Format << (a.Index * 4);
                dims |= (uint)(a.Dimension - 1) << (a.Index * 2);
            }
            for (int a = 0; a < 14; a++) sizes[a] = blocksAttr[m, a]?.Length ?? 0;
            w.Write(formats);
            w.Write(dims);
            foreach (int size in sizes) w.Write(size);
            w.Write(blocksIndex[m].Length);
            w.Write(s.Wide ? 1u : 0u);
            w.Write(s.Vertices);
            w.Write(s.Indices.Length);
            w.Write(s.SubMeshes.Length);
        }
        for (int m = 0; m < meshes.Length; m++)
        {
            SynthMesh s = meshes[m];
            foreach ((int start, int count) in s.SubMeshes)
            {
                w.Write(start); w.Write(count); w.Write(0); w.Write(s.Vertices);
                for (int k = 0; k < 6; k++) w.Write(0f);
            }
        }
        for (int m = 0; m < meshes.Length; m++) w.Write(blocksIndex[m]);
        for (int a = 0; a < 14; a++)
            for (int m = 0; m < meshes.Length; m++)
                if (blocksAttr[m, a] != null) w.Write(blocksAttr[m, a]);
        w.Flush();
        return stream.ToArray();
    }

    static byte[] CompressIndices(int[] indices, int vertices)
    {
        uint[] wide = indices.Select(i => (uint)i).ToArray();
        long bound = NativeCompression.CompressMeshoptIndexBufferBound(indices.Length, vertices);
        var meshopt = new byte[bound];
        long size;
        fixed (uint* src = wide) fixed (byte* dst = meshopt)
            size = NativeCompression.CompressMeshoptIndexBuffer((IntPtr)src, indices.Length, (IntPtr)dst, bound);
        return Zstd(meshopt, size);
    }

    static byte[] CompressAttribute(byte[] data, int vertices)
    {
        int vertexSize = data.Length / vertices;
        long bound = NativeCompression.CompressMeshoptVertexAttrBound(vertices, vertexSize);
        var meshopt = new byte[bound];
        long size;
        fixed (byte* src = data) fixed (byte* dst = meshopt)
            size = NativeCompression.CompressMeshoptVertexAttr((IntPtr)src, vertices, vertexSize, (IntPtr)dst, bound);
        return Zstd(meshopt, size);
    }

    static byte[] Zstd(byte[] data, long size)
    {
        long bound = NativeCompression.CompressCalcBound(size, CompressionFormat.ZSTD);
        var packed = new byte[bound];
        long length;
        fixed (byte* src = data) fixed (byte* dst = packed)
            length = NativeCompression.CompressData((IntPtr)src, size, (IntPtr)dst, bound, CompressionFormat.ZSTD, 3);
        Array.Resize(ref packed, (int)length);
        return packed;
    }

    // Reads a file the way NativeGeometryRead does (prefix, headers, body) and decodes it.
    static (NativeGeometryFile format, NativeBlock decoded) Read(byte[] file)
    {
        (int meshes, uint flags) = NativeGeometryFile.ReadPrefix(file);
        var infos = new byte[NativeGeometryFile.InfosBytes(meshes)];
        Array.Copy(file, NativeGeometryFile.kPrefixBytes, infos, 0, infos.Length);
        var format = new NativeGeometryFile(meshes, flags, infos);
        using var body = new NativeBlock(format.BodyEnd - format.SubMeshesAt);
        Marshal.Copy(file, (int)format.SubMeshesAt, body.Pointer, (int)body.Length);
        return (format, NativeGeometryDecode.Decode(format, body));
    }

    static List<NativePiece> CutFile(byte[] file, bool[] wanted, NativeMeshCut.Grouping grouping, int maxTriangles, out NativeGeometryFile format)
    {
        (int meshes, uint flags) = NativeGeometryFile.ReadPrefix(file);
        var infos = new byte[NativeGeometryFile.InfosBytes(meshes)];
        Array.Copy(file, NativeGeometryFile.kPrefixBytes, infos, 0, infos.Length);
        format = new NativeGeometryFile(meshes, flags, infos);
        using var body = new NativeBlock(format.BodyEnd - format.SubMeshesAt);
        Marshal.Copy(file, (int)format.SubMeshesAt, body.Pointer, (int)body.Length);
        return NativeGeometryDecode.Cut(format, body, wanted, grouping, maxTriangles);
    }

    static byte[] Floats(params float[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    // Two meshes: a plain one (float positions, SNorm16 x2 normals, Float16 x2 UVs, 16-bit indices, two submeshes) and a skinned one
    // (UInt8 x4 blend indices, which the game hands to Unity as one UInt32, UNorm8 x4 weights, 32-bit indices).
    static SynthMesh[] Sample()
    {
        var plain = new SynthMesh
        {
            Vertices = 7, Wide = false,
            Indices = new[] { 0, 1, 2, 3, 4, 5, 1, 3, 6 },
            SubMeshes = new[] { (0, 6), (6, 3) },
        };
        plain.Attributes.Add(new Attr { Index = 0, Format = 0, Dimension = 3, Data = Floats(-1, 0, 0, -1, 1, 0, -2, 0, 0, 1, 1, 0, 1, 0, 0, 2, 0, 0, 0, 3, 0) });
        plain.Attributes.Add(new Attr { Index = 1, Format = 5, Dimension = 2, Data = Enumerable.Range(0, 28).Select(i => (byte)(i * 7)).ToArray() });
        plain.Attributes.Add(new Attr { Index = 4, Format = 1, Dimension = 2, Data = Enumerable.Range(0, 28).Select(i => (byte)(200 - i)).ToArray() });
        var skinned = new SynthMesh
        {
            Vertices = 4, Wide = true,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            SubMeshes = new[] { (0, 6) },
        };
        skinned.Attributes.Add(new Attr { Index = 0, Format = 0, Dimension = 3, Data = Floats(0, 0, 5, 0, 1, 5, 1, 1, 5, 1, 0, 5) });
        skinned.Attributes.Add(new Attr { Index = 2, Format = 0, Dimension = 1, Data = Floats(1, 2, 3, 4) });
        skinned.Attributes.Add(new Attr { Index = 12, Format = 2, Dimension = 4, Data = Enumerable.Range(0, 16).Select(i => (byte)(255 - i)).ToArray() });
        skinned.Attributes.Add(new Attr { Index = 13, Format = 6, Dimension = 4, Data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray() });
        return new[] { plain, skinned };
    }

    static bool SameTriangles(Func<int, int> decoded, int[] written)
    {
        for (int t = 0; t < written.Length; t += 3)
        {
            bool any = false;
            for (int r = 0; r < 3; r++)
                any |= decoded(t) == written[t + r] && decoded(t + 1) == written[t + (r + 1) % 3] && decoded(t + 2) == written[t + (r + 2) % 3];
            if (!any) return false;
        }
        return true;
    }

    static bool Same(byte* a, byte[] b) { for (int i = 0; i < b.Length; i++) if (a[i] != b[i]) return false; return true; }

    static void Synthetic(Action<bool, string> check)
    {
        SynthMesh[] sample = Sample();
        foreach (bool compressed in new[] { false, true })
        {
            string how = compressed ? "compressed (meshopt + zstd, as the game writes)" : "stored";
            byte[] file;
            try { file = Write(sample, compressed); }
            catch (DllNotFoundException) { check(!compressed, Prefix + "the game's PipelinePlugin.dll is found (CSII_MANAGEDPATH)"); continue; }
            (NativeGeometryFile format, NativeBlock decoded) = Read(file);
            using (decoded)
            {
                check(format.Meshes.Length == 2 && format.SubMeshes.Length == 3, Prefix + how + ": two meshes, three submeshes, flattened in order");
                check(format.SubMeshes[1].Mesh == 0 && format.SubMeshes[1].IndexStart == 6 && format.SubMeshes[2].Mesh == 1, Prefix + how + ": a submesh knows its mesh and its first index");
                bool bytes = true;
                for (int m = 0; m < 2; m++)
                    foreach (Attr a in sample[m].Attributes)
                        bytes &= Same(decoded.Bytes + format.DecodedAttributeAt[m, a.Index], a.Data);
                check(bytes, Prefix + how + ": every attribute decodes to its bytes (packed normals, half UVs and blend data untouched)");
                ushort* narrow = (ushort*)(decoded.Bytes + format.DecodedIndicesAt[0]);
                uint* wide = (uint*)(decoded.Bytes + format.DecodedIndicesAt[1]);
                // meshopt's index codec keeps each triangle and its winding, not which corner comes first
                check(SameTriangles(i => narrow[i], sample[0].Indices) && SameTriangles(i => (int)wide[i], sample[1].Indices),
                    Prefix + how + ": 16-bit and 32-bit indices decode to the written triangles, in order and winding");
            }
            NativeGeometryFile.MeshInfo plain = format.Meshes[0], skinned = format.Meshes[1];
            check(plain.Stream[0] == 0 && plain.Stream[1] == 1 && plain.Stream[4] == 1 && plain.Stride[0] == 12 && plain.Stride[1] == 8 && plain.Offset[4] == 4,
                Prefix + how + ": a plain mesh has the position alone in stream 0, the rest in stream 1 in attribute order (GeometryUtils)");
            check(skinned.Stream[0] == 0 && skinned.Stream[2] == 0 && skinned.Stride[0] == 16 && skinned.Stream[12] == 2 && skinned.Stream[13] == 2 && skinned.Stride[2] == 8,
                Prefix + how + ": a skinned mesh has position, normal and tangent in stream 0, the blend weights and indices in stream 2");
            check(skinned.Format[13] == 10 && skinned.Dimension[13] == 1, Prefix + how + ": UInt8 x4 blend indices are one UInt32, as the game gives them to Unity");
        }

        byte[] stored = Write(sample, false);
        stored[0] = 2;
        bool versionRejected = false;
        try { NativeGeometryFile.ReadPrefix(stored); } catch (NotSupportedException) { versionRejected = true; }
        check(versionRejected, Prefix + "another format version is refused, not misread");
        stored = Write(sample, false);
        BitConverter.GetBytes(9999).CopyTo(stored, NativeGeometryFile.kPrefixBytes + 76);   // the first mesh's vertex count
        check(Refused(stored), Prefix + "a header whose vertex count does not match its blocks is refused");
        stored = Write(sample, false);
        int firstSubMesh = NativeGeometryFile.kPrefixBytes + NativeGeometryFile.InfosBytes(2);
        BitConverter.GetBytes(30).CopyTo(stored, firstSubMesh + 4);                           // the first submesh's index count
        check(Refused(stored), Prefix + "a submesh past its mesh's indices is refused");
        stored = Write(sample, false);
        BitConverter.GetBytes(5).CopyTo(stored, firstSubMesh + 12);                           // the first submesh's vertex count past the mesh's 7
        BitConverter.GetBytes(3).CopyTo(stored, firstSubMesh + 8);
        check(Refused(stored), Prefix + "a submesh past its mesh's vertices is refused");
    }

    static bool Refused(byte[] file)
    {
        try { Read(file).decoded.Dispose(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    static void Cut(Action<bool, string> check)
    {
        SynthMesh[] sample = Sample();
        byte[] file = Write(sample, false);
        // by the triangle's centre: high up dropped (submesh 1's only triangle), else x < 0 the body, x > 0 part 7
        NativeMeshCut.Grouping bySide = (a, b, c) =>
        {
            float3 centre = (a + b + c) / 3f;
            if (centre.y > 1.2f) return NativeMeshCut.Drop;
            return centre.x < 0f ? NativeMeshCut.Body : 7;
        };
        List<NativePiece> pieces = CutFile(file, new[] { true, true, true }, bySide, 1000, out NativeGeometryFile format);
        try
        {
            int body0 = pieces.Count(p => p.SubMesh == 0 && p.Group == NativeMeshCut.Body), part0 = pieces.Count(p => p.SubMesh == 0 && p.Group == 7);
            check(body0 == 1 && part0 == 1 && !pieces.Any(p => p.SubMesh == 1),
                Prefix + "submesh 0 becomes a body piece and a part piece; submesh 1, all dropped, no piece");
            NativePiece body = pieces.First(p => p.SubMesh == 0 && p.Group == NativeMeshCut.Body);
            check(body.VertexCount == 3 && body.IndexCount == 3, Prefix + "a piece holds only the vertices its triangles use");
            float* positions = (float*)body.Streams[0].Bytes;
            uint* indices = (uint*)body.Indices.Bytes;
            bool same = true;
            for (int k = 0; k < 3; k++)
            {
                int original = sample[0].Indices[k];
                float3 want = new float3(BitConverter.ToSingle(sample[0].Attributes[0].Data, original * 12), BitConverter.ToSingle(sample[0].Attributes[0].Data, original * 12 + 4),
                    BitConverter.ToSingle(sample[0].Attributes[0].Data, original * 12 + 8));
                float* p = positions + 3 * indices[k];
                same &= p[0] == want.x && p[1] == want.y && p[2] == want.z;
            }
            check(same, Prefix + "the piece's triangle has the original corners in the original order (the winding kept)");
            NativePiece part = pieces.First(p => p.SubMesh == 0 && p.Group == 7);
            float* partPositions = (float*)part.Streams[0].Bytes;
            uint* partIndices = (uint*)part.Indices.Bytes;
            bool remapped = part.VertexCount == 3;
            for (int k = 0; k < 3; k++)
            {
                int original = sample[0].Indices[3 + k];
                float* p = partPositions + 3 * partIndices[k];
                remapped &= partIndices[k] < part.VertexCount && p[0] == BitConverter.ToSingle(sample[0].Attributes[0].Data, original * 12) &&
                    p[1] == BitConverter.ToSingle(sample[0].Attributes[0].Data, original * 12 + 4);
            }
            check(remapped, Prefix + "a piece's indices are its own (vertices 3, 4, 5 of the mesh are 0, 1, 2 of the piece) and still give the original corners");
            byte* stream1 = body.Streams[1].Bytes;
            int v0 = sample[0].Indices[0];
            check(Same(stream1 + 8 * indices[0], sample[0].Attributes[1].Data.Skip(4 * v0).Take(4).ToArray()) &&
                  Same(stream1 + 8 * indices[0] + 4, sample[0].Attributes[2].Data.Skip(4 * v0).Take(4).ToArray()),
                Prefix + "the other attributes of a vertex follow it into the piece, interleaved in the mesh's stream layout");
            check(body.Min.x == -2f && body.Max.x == -1f && body.Max.y == 1f && body.Min.y == 0f, Prefix + "a piece's bounds are its vertices'");
            NativePiece skinned = pieces.First(p => p.SubMesh == 2);
            check(skinned.Group == 7 && skinned.IndexCount == 6 && skinned.Streams[2] != null && skinned.Streams[2].Length == 8 * 4,
                Prefix + "the skinned mesh's piece keeps its blend stream");
        }
        finally { foreach (NativePiece p in pieces) p.Dispose(); }

        // many triangles: pieces of at most the given count, every triangle in one of them
        var strip = new SynthMesh { Vertices = 1002, Wide = true, SubMeshes = new[] { (0, 3000) } };
        strip.Indices = Enumerable.Range(0, 1000).SelectMany(t => new[] { t, t + 1, t + 2 }).ToArray();
        strip.Attributes.Add(new Attr { Index = 0, Format = 0, Dimension = 3, Data = Floats(Enumerable.Range(0, 1002).SelectMany(i => new[] { (float)i, i % 2, 0f }).ToArray()) });
        pieces = CutFile(Write(new[] { strip }, false), new[] { true }, (a, b, c) => NativeMeshCut.Body, 300, out _);
        try
        {
            check(pieces.Count == 4 && pieces.All(p => p.IndexCount <= 900) && pieces.Sum(p => p.IndexCount) == 3000,
                Prefix + "1000 triangles at most 300 a piece: four pieces holding all of them");
            // triangle t of the strip is (t, t+1, t+2): its corners' x are t, t+1, t+2, whichever piece holds it
            bool corners = true;
            int next = 0;
            foreach (NativePiece p in pieces)
            {
                float* xyz = (float*)p.Streams[0].Bytes;
                uint* ix = (uint*)p.Indices.Bytes;
                for (int i = 0; i < p.IndexCount; i++)
                    corners &= ix[i] < p.VertexCount && xyz[3 * ix[i]] == next + i / 3 + i % 3;
                next += p.IndexCount / 3;
            }
            check(corners, Prefix + "every piece of the strip gives back its triangles' corners, in order");
        }
        finally { foreach (NativePiece p in pieces) p.Dispose(); }
        check(NativeMeshCut.MaxVertices == ushort.MaxValue, Prefix + "a piece has at most 65535 vertices (its indices fit 16 bits)");
    }

    // The packs' geometry as the game loads it (Stored entries of the .cok zip, the asset's bytes at their offset).
    static void RealPacks(Action<bool, string> check)
    {
        string mods = Environment.GetEnvironmentVariable("CSII_LOCALMODSPATH");
        var packages = new List<string>();
        if (!string.IsNullOrEmpty(mods) && Directory.Exists(mods))
        {
            packages.AddRange(Directory.GetDirectories(mods, "CDLC.Bus.*").SelectMany(d => Directory.GetFiles(d, "*.cok")));
            string pdx = Path.Combine(mods, "..", ".cache", "mods", "pdx_mods");
            if (Directory.Exists(pdx)) packages.AddRange(Directory.GetFiles(pdx, "*.cok", SearchOption.AllDirectories).Where(p => Path.GetFileName(p).StartsWith("SWB")));
        }
        if (packages.Count == 0)
        {
            Console.WriteLine("  (geometry read: no installed vehicle packs to read)");
            return;
        }

        int files = 0, triangles = 0, outside = 0, outOfRange = 0, unspanned = 0, failed = 0;
        double worstSpan = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (string package in packages)
            using (ZipArchive zip = ZipFile.OpenRead(package))
                foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName.EndsWith(".Geometry", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] bytes;
                    using (Stream s = entry.Open()) using (var copy = new MemoryStream()) { s.CopyTo(copy); bytes = copy.ToArray(); }
                    files++;
                    try
                    {
                        (NativeGeometryFile format, NativeBlock decoded) = Read(bytes);
                        using (decoded)
                            foreach (NativeGeometryFile.SubMesh sub in format.SubMeshes)
                            {
                                NativeGeometryFile.MeshInfo mesh = format.Meshes[sub.Mesh];
                                byte* idx = decoded.Bytes + format.DecodedIndicesAt[sub.Mesh];
                                byte* pos = decoded.Bytes + format.DecodedAttributeAt[sub.Mesh, NativeGeometryFile.kPosition];
                                int fmt = mesh.Format[NativeGeometryFile.kPosition], dim = mesh.Dimension[NativeGeometryFile.kPosition];
                                float3 min = new float3(float.MaxValue), max = new float3(float.MinValue);
                                float tolerance = 1e-3f + 1e-3f * math.cmax(sub.Extents);
                                for (int i = sub.IndexStart; i < sub.IndexStart + sub.IndexCount; i++)
                                {
                                    int v = mesh.WideIndices ? ((int*)idx)[i] : ((ushort*)idx)[i];
                                    if (v < sub.FirstVertex || v >= sub.FirstVertex + sub.VertexCount) { outOfRange++; continue; }
                                    float3 p = NativeMeshCut.Position(pos, fmt, dim, v);
                                    if (math.any(math.abs(p - sub.Centre) > sub.Extents + tolerance)) outside++;
                                    min = math.min(min, p);
                                    max = math.max(max, p);
                                }
                                triangles += sub.IndexCount / 3;
                                if (sub.IndexCount > 0)
                                {
                                    double span = math.cmax(math.abs(max - (sub.Centre + sub.Extents)) + math.abs(min - (sub.Centre - sub.Extents)));
                                    worstSpan = Math.Max(worstSpan, span);
                                    if (span > tolerance) unspanned++;
                                }
                            }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Console.WriteLine($"  geometry read: {Path.GetFileName(package)}:{entry.FullName}: {e.GetType().Name}: {e.Message}");
                    }
                }
        Console.WriteLine($"  geometry read: {files} geometry files of {packages.Count} packages, {triangles} triangles, {stopwatch.ElapsedMilliseconds} ms; " +
            $"worst bounds difference {worstSpan * 1000:0.000} mm");
        check(files > 0 && failed == 0, Prefix + $"every geometry file of the installed packs decodes ({files} files, {failed} failed)");
        check(outOfRange == 0, Prefix + $"every index lies in its submesh's vertex range written by the exporter ({outOfRange} outside)");
        check(outside == 0 && unspanned == 0,
            Prefix + $"the decoded corners of every submesh fill exactly the bounds written beside them ({outside} corners outside, {unspanned} submeshes not spanning theirs)");
    }

    static void Gate(Action<bool, string> check)
    {
        var gate = new FailureGate<string>();
        check(gate.Allowed("bus", 0f), Prefix + "gate: a bus that never failed may be taken over");
        float first = gate.Fail("bus", 10f);
        check(first == 1f && !gate.Allowed("bus", 10.5f) && gate.Allowed("bus", 11f), Prefix + "gate: after a failure, not again before 1 s");
        check(gate.Fail("bus", 11f) == 2f && gate.Fail("bus", 13f) == 4f && gate.Fail("bus", 17f) == 8f, Prefix + "gate: the pause doubles: 2, 4, 8 s");
        float last = gate.Fail("bus", 25f);
        check(float.IsPositiveInfinity(last) && gate.GaveUp("bus") && !gate.Allowed("bus", 1e9f), Prefix + "gate: after five failures in a row, never again");
        check(gate.Allowed("other", 25f), Prefix + "gate: another key is not held back");
        gate.Succeeded("bus");
        check(gate.Allowed("bus", 25f) && gate.Failures("bus") == 0, Prefix + "gate: a success starts the count again");
        check(FailureGate<string>.Delay(20) == FailureGate<string>.kMaxDelaySeconds, Prefix + "gate: the pause is held at 60 s");

        // the storm of 9 November: a bus failing on every frame for 107 s at 60 frames a second
        var storm = new FailureGate<int>();
        int rebuilds = 0;
        for (int frame = 0; frame < 107 * 60; frame++)
        {
            float now = frame / 60f;
            if (!storm.Allowed(1, now)) continue;
            rebuilds++;
            storm.Fail(1, now);
        }
        check(rebuilds == FailureGate<int>.kGiveUpAfter, Prefix + $"gate: a bus that fails every frame is built {FailureGate<int>.kGiveUpAfter} times, not 112 ({rebuilds})");
    }

    static void Queue(Action<bool, string> check)
    {
        var order = new List<string>();
        var gate = new System.Threading.ManualResetEventSlim(false);
        var blocker = CabPrebuild.Enqueue(() => { gate.Wait(); return 0; }, "blocker");
        var jobs = new List<System.Threading.Tasks.Task<int>>();
        foreach (string name in new[] { "a", "b", "selected", "c" })
        {
            string tag = name;
            jobs.Add(CabPrebuild.Enqueue(() => { lock (order) order.Add(tag); return 0; }, tag));
        }
        bool promoted = CabPrebuild.Promote("selected");
        gate.Set();
        System.Threading.Tasks.Task.WaitAll(jobs.ToArray(), 10000);
        check(promoted && string.Join(",", order) == "selected,a,b,c", Prefix + $"queue: the selected bus's job runs before the ones queued ahead of it ({string.Join(",", order)})");
        check(!CabPrebuild.Promote("missing"), Prefix + "queue: promoting a tag that is not queued does nothing");
        blocker.Wait(1000);
    }
}
