using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Colossal.AssetPipeline.Native;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The game's geometry file (Colossal.IO.AssetDatabase.GeometryAsset, format 1) as written by GeometryAssetSave: a 10-byte prefix
    /// (version, mesh count, file flags), one 88-byte header per mesh, the 40-byte submesh records, every mesh's index data, then each
    /// vertex attribute of every mesh in turn (attribute-major), each block compressed by zstd and/or meshopt per the file flags.
    /// The mod reads it itself instead of RenderPrefab.ObtainMeshes, which reads, decompresses and uploads the whole model on the calling
    /// frame (150 to 900 ms for a pack bus, the log of 10 November) to give the few glass submeshes.
    /// </summary>
    internal sealed class NativeGeometryFile
    {
        internal const ushort kFormatVersion = 1;
        internal const int kPrefixBytes = 10, kMeshInfoBytes = 88, kSubMeshBytes = 40, kAttributes = 14;
        internal const uint kFlagMeshopt = 1, kFlagZstd = 2;
        internal const int kPosition = 0, kBlendWeight = 12, kBlendIndices = 13;
        private const int kAbsent = 15, kUInt8 = 6, kUInt32 = 10;

        internal sealed class MeshInfo
        {
            internal int VertexCount, IndexCount, SubMeshCount, IndexBytes;
            internal bool WideIndices;
            internal readonly byte[] Format = new byte[kAttributes];      // VertexAttributeFormat as Unity's mesh gets it; kAbsent: none
            internal readonly byte[] Dimension = new byte[kAttributes];
            internal readonly int[] StoredBytes = new int[kAttributes];   // the attribute's block in the file (compressed size)
            internal readonly int[] Stream = new int[kAttributes];
            internal readonly int[] Offset = new int[kAttributes];        // within its stream's vertex
            internal readonly int[] Stride = new int[3];
            internal bool Has(int attribute) => Format[attribute] != kAbsent;
            internal int ElementBytes(int attribute) => Has(attribute) ? FormatBytes(Format[attribute]) * Dimension[attribute] : 0;
        }

        // GeometryAsset.SubMeshInfo: index start and count, first vertex and vertex count, bounds (centre, extents)
        internal struct SubMesh
        {
            internal int Mesh, IndexStart, IndexCount, FirstVertex, VertexCount;
            internal float3 Centre, Extents;
        }

        internal readonly uint Flags;
        internal readonly MeshInfo[] Meshes;
        internal readonly SubMesh[] SubMeshes;          // flattened: material i of the render prefab is SubMeshes[i]
        // Regions of the file, from the asset's own offset in it.
        internal readonly long SubMeshesAt, IndicesAt, BodyEnd;
        internal readonly long[] AttributesAt = new long[kAttributes];
        // The decoded buffer this reader fills: per mesh its indices, then its attributes in order, each uncompressed.
        internal readonly long[] DecodedIndicesAt;
        internal readonly long[,] DecodedAttributeAt;
        internal readonly long DecodedBytes;

        /// <summary>The meshes' count and the file flags from the prefix (version first); throws on another version or a corrupt count.</summary>
        internal static (int meshes, uint flags) ReadPrefix(byte[] prefix)
        {
            if (prefix == null || prefix.Length < kPrefixBytes) throw new InvalidOperationException("Geometry prefix is truncated.");
            ushort version = BitConverter.ToUInt16(prefix, 0);
            if (version != kFormatVersion) throw new NotSupportedException($"Geometry format {version} (this reader knows {kFormatVersion}).");
            int meshes = BitConverter.ToInt32(prefix, 2);
            if (meshes < 1 || meshes > 1000) throw new InvalidOperationException($"Invalid geometry mesh count {meshes}.");
            return (meshes, BitConverter.ToUInt32(prefix, 6));
        }

        internal static int InfosBytes(int meshes) => meshes * kMeshInfoBytes;

        /// <summary>The headers (<see cref="InfosBytes"/> bytes after the prefix) and the regions they give.</summary>
        internal NativeGeometryFile(int meshes, uint flags, byte[] infos)
        {
            if (infos == null || infos.Length < InfosBytes(meshes)) throw new InvalidOperationException("Geometry mesh headers are truncated.");
            Flags = flags;
            Meshes = new MeshInfo[meshes];
            int subMeshes = 0;
            long indexBytes = 0;
            var attributeBytes = new long[kAttributes];
            for (int m = 0; m < meshes; m++)
            {
                int at = m * kMeshInfoBytes;
                ulong formats = BitConverter.ToUInt64(infos, at);
                uint dims = BitConverter.ToUInt32(infos, at + 8);
                var info = new MeshInfo
                {
                    IndexBytes = BitConverter.ToInt32(infos, at + 68),
                    WideIndices = (BitConverter.ToUInt32(infos, at + 72) & 1) != 0,
                    VertexCount = BitConverter.ToInt32(infos, at + 76),
                    IndexCount = BitConverter.ToInt32(infos, at + 80),
                    SubMeshCount = BitConverter.ToInt32(infos, at + 84),
                };
                if (info.VertexCount <= 0 || info.VertexCount > 100000000 || info.IndexCount <= 0 || info.IndexCount > 100000000 ||
                    info.SubMeshCount <= 0 || info.SubMeshCount > 1000 || info.IndexBytes <= 0)
                    throw new InvalidOperationException($"Invalid geometry header of mesh {m}.");
                for (int a = 0; a < kAttributes; a++)
                {
                    int format = (int)((formats >> (a * 4)) & 0xF);
                    info.StoredBytes[a] = BitConverter.ToInt32(infos, at + 12 + 4 * a);
                    attributeBytes[a] += info.StoredBytes[a];
                    if (format == kAbsent)
                    {
                        info.Format[a] = kAbsent;
                        continue;
                    }
                    int dimension = (int)((dims >> (a * 2)) & 3) + 1;
                    // GeometryAsset.CreateMeshes hands four UInt8s to Unity as one UInt32 (the same bytes)
                    if (format == kUInt8 && dimension == 4) { format = kUInt32; dimension = 1; }
                    if (FormatBytes(format) == 0) throw new NotSupportedException($"Vertex format {format} of attribute {a} in mesh {m}.");
                    info.Format[a] = (byte)format;
                    info.Dimension[a] = (byte)dimension;
                }
                Classify(info);
                subMeshes += info.SubMeshCount;
                indexBytes += info.IndexBytes;
                Meshes[m] = info;
            }

            SubMeshes = new SubMesh[subMeshes];
            SubMeshesAt = kPrefixBytes + (long)InfosBytes(meshes);
            IndicesAt = SubMeshesAt + (long)subMeshes * kSubMeshBytes;
            long at2 = IndicesAt + indexBytes;
            for (int a = 0; a < kAttributes; a++)
            {
                AttributesAt[a] = at2;
                at2 += attributeBytes[a];
            }
            BodyEnd = at2;

            DecodedIndicesAt = new long[meshes];
            DecodedAttributeAt = new long[meshes, kAttributes];
            long decoded = 0;
            for (int m = 0; m < meshes; m++)
            {
                MeshInfo info = Meshes[m];
                DecodedIndicesAt[m] = decoded;
                decoded += (long)info.IndexCount * (info.WideIndices ? 4 : 2);
                for (int a = 0; a < kAttributes; a++)
                {
                    DecodedAttributeAt[m, a] = info.Has(a) ? decoded : -1;
                    decoded += (long)info.VertexCount * info.ElementBytes(a);
                }
            }
            DecodedBytes = decoded;
        }

        /// <summary>Reads the submesh records (the body's first <see cref="SubMeshes"/>.Length x 40 bytes).</summary>
        internal unsafe void ReadSubMeshes(byte* records)
        {
            int flat = 0;
            for (int m = 0; m < Meshes.Length; m++)
                for (int s = 0; s < Meshes[m].SubMeshCount; s++, flat++)
                {
                    int* r = (int*)(records + (long)flat * kSubMeshBytes);
                    float* b = (float*)(r + 4);
                    var sub = new SubMesh
                    {
                        Mesh = m, IndexStart = r[0], IndexCount = r[1], FirstVertex = r[2], VertexCount = r[3],
                        Centre = new float3(b[0], b[1], b[2]), Extents = new float3(b[3], b[4], b[5]),
                    };
                    if (sub.IndexStart < 0 || sub.IndexCount < 0 || sub.IndexCount % 3 != 0 || (long)sub.IndexStart + sub.IndexCount > Meshes[m].IndexCount ||
                        sub.FirstVertex < 0 || sub.VertexCount < 0 || (long)sub.FirstVertex + sub.VertexCount > Meshes[m].VertexCount)
                        throw new InvalidOperationException($"Invalid submesh {flat} of the geometry.");
                    SubMeshes[flat] = sub;
                }
        }

        /// <summary>The vertex streams as GeometryUtils.ClassifyVertexAttribute lays them out (attributes in their enum order in each stream).</summary>
        private static void Classify(MeshInfo info)
        {
            bool skinned = info.Has(kBlendIndices);
            for (int a = 0; a < kAttributes; a++)
            {
                if (!info.Has(a)) { info.Stream[a] = -1; continue; }
                int stream = !skinned ? (a == kPosition ? 0 : 1) : a <= 2 ? 0 : a == kBlendWeight || a == kBlendIndices ? 2 : 1;
                info.Stream[a] = stream;
                info.Offset[a] = info.Stride[stream];
                info.Stride[stream] += info.ElementBytes(a);
            }
        }

        /// <summary>Bytes of one component of a VertexAttributeFormat (0: unknown).</summary>
        internal static int FormatBytes(int format) => format switch
        {
            0 or 10 or 11 => 4,
            1 or 4 or 5 or 8 or 9 => 2,
            2 or 3 or 6 or 7 => 1,
            _ => 0,
        };
    }

    /// <summary>Decodes a geometry body (the file from the submesh records to the end of the attributes) and cuts submeshes of it.</summary>
    internal static class NativeGeometryDecode
    {
        /// <summary>The body decoded into one buffer laid out as <see cref="NativeGeometryFile.DecodedIndicesAt"/> and <see cref="NativeGeometryFile.DecodedAttributeAt"/> say.</summary>
        internal static unsafe NativeBlock Decode(NativeGeometryFile format, NativeBlock body)
        {
            format.ReadSubMeshes(body.Bytes);
            var decoded = new NativeBlock(format.DecodedBytes);
            try
            {
                long at = format.IndicesAt - format.SubMeshesAt;
                for (int m = 0; m < format.Meshes.Length; m++)
                {
                    NativeGeometryFile.MeshInfo mesh = format.Meshes[m];
                    int size = mesh.WideIndices ? 4 : 2;
                    Block(format.Flags, body.Bytes + at, mesh.IndexBytes, decoded.Bytes + format.DecodedIndicesAt[m], mesh.IndexCount, size, true);
                    at += mesh.IndexBytes;
                }
                for (int a = 0; a < NativeGeometryFile.kAttributes; a++)
                {
                    at = format.AttributesAt[a] - format.SubMeshesAt;
                    for (int m = 0; m < format.Meshes.Length; m++)
                    {
                        NativeGeometryFile.MeshInfo mesh = format.Meshes[m];
                        if (mesh.Has(a) && mesh.StoredBytes[a] > 0)
                            Block(format.Flags, body.Bytes + at, mesh.StoredBytes[a], decoded.Bytes + format.DecodedAttributeAt[m, a], mesh.VertexCount, mesh.ElementBytes(a), false);
                        at += mesh.StoredBytes[a];
                    }
                }
                return decoded;
            }
            catch
            {
                decoded.Dispose();
                throw;
            }
        }

        internal static unsafe List<NativePiece> Cut(NativeGeometryFile format, NativeBlock body, bool[] wanted, NativeMeshCut.Grouping grouping, int maxTriangles)
        {
            using NativeBlock decoded = Decode(format, body);
            var subMeshes = new List<int>();
            for (int i = 0; i < wanted.Length; i++)
                if (wanted[i]) subMeshes.Add(i);
            return NativeMeshCut.Cut(format, decoded.Bytes, subMeshes, grouping, maxTriangles);
        }

        // As GeometryAsset.DecompressIndexData / DecompressAttributeData: zstd first, then meshopt, else the block as stored.
        private static unsafe void Block(uint flags, byte* source, long size, byte* target, int count, int elementBytes, bool indices)
        {
            long expected = (long)count * elementBytes;
            NativeBlock unpacked = null;
            try
            {
                if ((flags & NativeGeometryFile.kFlagZstd) != 0)
                {
                    long bound = NativeCompression.DecompressCalcBound((IntPtr)source, size, CompressionFormat.ZSTD);
                    if (bound <= 0) throw new InvalidOperationException("A geometry block's zstd size cannot be read.");
                    unpacked = new NativeBlock(bound);
                    long length = NativeCompression.DecompressData((IntPtr)source, size, unpacked.Pointer, bound, CompressionFormat.ZSTD);
                    if (length < 0) throw new InvalidOperationException("A geometry block failed to decompress (zstd).");
                    source = unpacked.Bytes;
                    size = length;
                }
                if ((flags & NativeGeometryFile.kFlagMeshopt) != 0)
                {
                    int result = indices
                        ? NativeCompression.DecompressMeshoptIndexBuffer((IntPtr)source, size, count, elementBytes, (IntPtr)target)
                        : NativeCompression.DecompressMeshoptVertexAttr((IntPtr)source, size, count, elementBytes, (IntPtr)target);
                    if (result < 0) throw new InvalidOperationException($"A geometry block failed to decompress (meshopt {result}).");
                }
                else
                {
                    if (size != expected) throw new InvalidOperationException($"A geometry block holds {size} bytes for {expected}.");
                    Buffer.MemoryCopy(source, target, expected, expected);
                }
            }
            finally { unpacked?.Dispose(); }
        }
    }

    /// <summary>Unmanaged memory (no garbage collector heap: a pack bus's geometry is tens of megabytes), freed by Dispose.</summary>
    internal sealed class NativeBlock : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        internal readonly long Length;

        internal NativeBlock(long length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Length = length;
            Pointer = Marshal.AllocHGlobal((IntPtr)Math.Max(1, length));
        }

        internal unsafe byte* Bytes => (byte*)Pointer;

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    /// <summary>A piece of a submesh: the vertices its triangles use, in the mesh's own vertex streams (packed formats kept byte for byte).</summary>
    internal sealed class NativePiece : IDisposable
    {
        internal int SubMesh, Group, VertexCount, IndexCount, Mesh;
        internal readonly NativeBlock[] Streams = new NativeBlock[3];
        internal NativeBlock Indices;                 // uint32
        internal float3 Min, Max;

        public void Dispose()
        {
            foreach (NativeBlock stream in Streams) stream?.Dispose();
            Indices?.Dispose();
        }
    }

    /// <summary>
    /// Cuts submeshes of decoded geometry into pieces: each triangle goes to the group its corners give (a negative group drops it), each
    /// group of a submesh becomes pieces of at most a given number of triangles, each with only the vertices it uses.
    /// </summary>
    internal static class NativeMeshCut
    {
        internal delegate int Grouping(float3 a, float3 b, float3 c);

        internal static unsafe List<NativePiece> Cut(NativeGeometryFile file, byte* decoded, IReadOnlyList<int> subMeshes, Grouping grouping, int maxTriangles)
        {
            if (maxTriangles < 1) throw new ArgumentOutOfRangeException(nameof(maxTriangles));
            var pieces = new List<NativePiece>();
            int maxVertices = 0, maxTriangleCount = 0;
            foreach (int s in subMeshes)
            {
                maxVertices = Math.Max(maxVertices, file.Meshes[file.SubMeshes[s].Mesh].VertexCount);
                maxTriangleCount = Math.Max(maxTriangleCount, file.SubMeshes[s].IndexCount / 3);
            }
            using var stamp = new NativeBlock(4L * maxVertices);
            using var remap = new NativeBlock(4L * maxVertices);
            using var groups = new NativeBlock(4L * Math.Max(1, maxTriangleCount));
            using var used = new NativeBlock(4L * Math.Min(Math.Min((long)maxVertices, 3L * maxTriangles), MaxVertices));
            int* stamps = (int*)stamp.Pointer, map = (int*)remap.Pointer, groupOf = (int*)groups.Pointer, usedVertices = (int*)used.Pointer;
            for (int v = 0; v < maxVertices; v++) stamps[v] = 0;
            int generation = 0;
            try
            {
                foreach (int s in subMeshes)
                {
                    NativeGeometryFile.SubMesh sub = file.SubMeshes[s];
                    NativeGeometryFile.MeshInfo mesh = file.Meshes[sub.Mesh];
                    byte* indices = decoded + file.DecodedIndicesAt[sub.Mesh];
                    byte* positions = decoded + file.DecodedAttributeAt[sub.Mesh, NativeGeometryFile.kPosition];
                    int format = mesh.Format[NativeGeometryFile.kPosition], dimension = mesh.Dimension[NativeGeometryFile.kPosition];
                    if (!mesh.Has(NativeGeometryFile.kPosition) || dimension < 3 || format != 0 && format != 1)
                        throw new NotSupportedException($"Position format {format} x {dimension} in mesh {sub.Mesh}.");
                    int triangles = sub.IndexCount / 3;
                    var groupIds = new SortedSet<int>();
                    for (int t = 0; t < triangles; t++)
                    {
                        int i0 = Index(indices, mesh, sub.IndexStart + 3 * t), i1 = Index(indices, mesh, sub.IndexStart + 3 * t + 1), i2 = Index(indices, mesh, sub.IndexStart + 3 * t + 2);
                        if ((uint)i0 >= (uint)mesh.VertexCount || (uint)i1 >= (uint)mesh.VertexCount || (uint)i2 >= (uint)mesh.VertexCount)
                            throw new InvalidOperationException($"Submesh {s} indexes past its mesh's {mesh.VertexCount} vertices.");
                        int g = grouping(Position(positions, format, dimension, i0), Position(positions, format, dimension, i1), Position(positions, format, dimension, i2));
                        groupOf[t] = g;
                        if (g >= Body) groupIds.Add(g);
                    }

                    foreach (int group in groupIds)
                    {
                        int t = 0;
                        while (true)
                        {
                            while (t < triangles && groupOf[t] != group) t++;
                            if (t >= triangles) break;
                            generation++;
                            var piece = new NativePiece { SubMesh = s, Group = group, Mesh = sub.Mesh };
                            pieces.Add(piece);
                            piece.Indices = new NativeBlock(4L * 3 * Math.Min(maxTriangles, triangles));
                            uint* output = (uint*)piece.Indices.Pointer;
                            int count = 0, vertices = 0;
                            for (; t < triangles && count < 3 * maxTriangles; t++)
                            {
                                if (groupOf[t] != group) continue;
                                if (vertices + 3 > MaxVertices) break;
                                for (int k = 0; k < 3; k++)
                                {
                                    int v = Index(indices, mesh, sub.IndexStart + 3 * t + k);
                                    if (stamps[v] != generation)
                                    {
                                        stamps[v] = generation;
                                        map[v] = vertices;
                                        usedVertices[vertices++] = v;
                                    }
                                    output[count++] = (uint)map[v];
                                }
                            }
                            piece.IndexCount = count;
                            piece.VertexCount = vertices;
                            Fill(piece, mesh, file, decoded, usedVertices, positions, format, dimension);
                        }
                    }
                }
                return pieces;
            }
            catch
            {
                foreach (NativePiece piece in pieces) piece.Dispose();
                throw;
            }
        }

        /// <summary>The group of the triangles that ride on no part (the body); any group below it drops the triangle.</summary>
        internal const int Body = -1, Drop = -2;
        /// <summary>A piece's vertices at most: its indices fit 16 bits.</summary>
        internal const int MaxVertices = ushort.MaxValue;

        private static unsafe void Fill(NativePiece piece, NativeGeometryFile.MeshInfo mesh, NativeGeometryFile file, byte* decoded, int* used,
            byte* positions, int format, int dimension)
        {
            for (int stream = 0; stream < 3; stream++)
                if (mesh.Stride[stream] > 0)
                    piece.Streams[stream] = new NativeBlock((long)mesh.Stride[stream] * piece.VertexCount);
            for (int a = 0; a < NativeGeometryFile.kAttributes; a++)
            {
                if (!mesh.Has(a)) continue;
                int size = mesh.ElementBytes(a), stride = mesh.Stride[mesh.Stream[a]];
                byte* source = decoded + file.DecodedAttributeAt[piece.Mesh, a];
                byte* target = piece.Streams[mesh.Stream[a]].Bytes + mesh.Offset[a];
                for (int i = 0; i < piece.VertexCount; i++)
                    Buffer.MemoryCopy(source + (long)used[i] * size, target + (long)i * stride, size, size);
            }
            float3 min = new float3(float.MaxValue), max = new float3(float.MinValue);
            for (int i = 0; i < piece.VertexCount; i++)
            {
                float3 p = Position(positions, format, dimension, used[i]);
                min = math.min(min, p);
                max = math.max(max, p);
            }
            piece.Min = piece.VertexCount > 0 ? min : float3.zero;
            piece.Max = piece.VertexCount > 0 ? max : float3.zero;
        }

        private static unsafe int Index(byte* indices, NativeGeometryFile.MeshInfo mesh, int i) =>
            mesh.WideIndices ? ((int*)indices)[i] : ((ushort*)indices)[i];

        internal static unsafe float3 Position(byte* positions, int format, int dimension, int vertex)
        {
            if (format == 0)
            {
                float* p = (float*)(positions + (long)vertex * 4 * dimension);
                return new float3(p[0], p[1], p[2]);
            }
            ushort* h = (ushort*)(positions + (long)vertex * 2 * dimension);
            return new float3(math.f16tof32(h[0]), math.f16tof32(h[1]), math.f16tof32(h[2]));
        }
    }
}
