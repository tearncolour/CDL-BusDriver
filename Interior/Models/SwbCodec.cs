using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// The runtime mesh files of a vehicle pack (&lt;model&gt;.swb1.gz, &lt;model&gt;.swb2.gz) part by part, without Unity: Bus Driver
    /// reads them (VehicleMesh.Load) and the pack tool converts SWB1 into SWB2 (Tools/VehiclePack/MeshConvert) with this one source.
    /// Both share the header and the part frames, then hold for each LOD and part:
    ///   SWB1: int triangles; per triangle a swatch byte and three corners of six float32 (position, normal).
    ///   SWB2: int triangles, int vertices; the vertices once each (float32 positions, octahedral int16 normals), then per triangle
    ///         three indices into them, then the swatches. Each array is stored in byte planes (all first bytes, then all second
    ///         bytes, ...), which the gzip round the file packs far better than interleaved floats. Vertices are numbered in the order
    ///         the corners first use them, and an index is stored as its distance below the next unused vertex (0: that vertex):
    ///         a new vertex costs a zero, and no index can point past the vertices already seen.
    /// SWB2 keeps SWB1's positions bit for bit, its triangle and corner order and its swatches; a normal moves by at most 0.004 degrees.
    /// </summary>
    internal static class SwbCodec
    {
        internal const uint Swb1 = 0x31425753u, Swb2 = 0x32425753u;     // ASCII "SWB1", "SWB2", little endian
        internal const int MaxTriangles = 2000000;                       // in a whole file
        private const float kPositionLimit = 100f, kNormalLimit = 2f, kOctScale = 32767f;

        /// <summary>A part's triangles at one LOD: its vertices (x, y, z of each position and normal, in the order the corners first use
        /// them), three indices and a swatch a triangle.</summary>
        internal sealed class Part
        {
            internal readonly float[] Positions, Normals;
            internal readonly int[] Indices;
            internal readonly byte[] Swatches;

            internal Part(float[] positions, float[] normals, int[] indices, byte[] swatches)
            { Positions = positions; Normals = normals; Indices = indices; Swatches = swatches; }

            internal int VertexCount => Positions.Length / 3;
            internal int TriangleCount => Swatches.Length;
        }

        /// <summary>One part's triangles at one LOD in <paramref name="format"/>. budget: the triangles the file may still hold (it is
        /// decreased). merge joins SWB1's corners into vertices (one for the whole file; SWB2 needs none).</summary>
        internal static Part ReadPart(Reader reader, uint format, ref int budget, CornerMerge merge)
        {
            int triangles = reader.ReadInt32();
            if (triangles < 0 || triangles > budget)
                throw new InvalidDataException("The vehicle mesh exceeds its triangle limit");
            budget -= triangles;
            return format == Swb1 ? ReadSwb1(reader, triangles, merge) : ReadSwb2(reader, triangles);
        }

        private static Part ReadSwb1(Reader reader, int triangles, CornerMerge merge)
        {
            var swatches = new byte[triangles];
            var indices = new int[3 * triangles];
            for (int t = 0; t < triangles; t++)
            {
                swatches[t] = reader.ReadByte();
                for (int k = 0; k < 3; k++)
                    indices[3 * t + k] = merge.Add(reader);
            }
            return merge.Take(indices, swatches);
        }

        private static Part ReadSwb2(Reader reader, int triangles)
        {
            int vertices = reader.ReadInt32();
            if (vertices < 0 || vertices > 3L * triangles || (vertices == 0) != (triangles == 0))
                throw new InvalidDataException("The vehicle mesh has an invalid vertex count");
            int corners = 3 * triangles, width = IndexWidth(vertices);

            var planes = new byte[Math.Max(12 * vertices, width * corners)];
            reader.ReadBytes(planes, 12 * vertices);
            var bytes = new byte[12 * vertices];
            for (int b = 0; b < 12; b++)                  // plane b: byte b % 4 of coordinate b / 4
                for (int v = 0, at = b; v < vertices; v++, at += 12)
                    bytes[at] = planes[b * vertices + v];
            var positions = new float[3 * vertices];
            Buffer.BlockCopy(bytes, 0, positions, 0, bytes.Length);
            for (int v = 0; v < vertices; v++)
                if (!InRange(positions[3 * v], kPositionLimit) || !InRange(positions[3 * v + 1], kPositionLimit) || !InRange(positions[3 * v + 2], kPositionLimit))
                    throw new InvalidDataException("The vehicle mesh contains an invalid position");

            reader.ReadBytes(planes, 4 * vertices);
            var normals = new float[3 * vertices];
            for (int v = 0; v < vertices; v++)
                OctDecode((short)(planes[v] | planes[vertices + v] << 8), (short)(planes[2 * vertices + v] | planes[3 * vertices + v] << 8),
                    out normals[3 * v], out normals[3 * v + 1], out normals[3 * v + 2]);

            reader.ReadBytes(planes, width * corners);
            var indices = new int[corners];
            int next = 0;
            for (int c = 0; c < corners; c++)
            {
                int distance = 0;
                for (int b = 0; b < width; b++)
                    distance |= planes[b * corners + c] << 8 * b;
                if (distance == 0)
                {
                    if (next == vertices) throw new InvalidDataException("The vehicle mesh has an invalid index");
                    indices[c] = next++;
                }
                else
                {
                    if (distance < 0 || distance > next) throw new InvalidDataException("The vehicle mesh has an invalid index");
                    indices[c] = next - distance;
                }
            }
            if (next != vertices)
                throw new InvalidDataException("The vehicle mesh has unused vertices");

            var swatches = new byte[triangles];
            reader.ReadBytes(swatches, triangles);
            return new Part(positions, normals, indices, swatches);
        }

        /// <summary>
        /// A part in SWB2, its counts first. Vertices equal in their position's bits and their encoded normal are one, numbered in the
        /// order the corners first use them (so an SWB1 part's vertices that differ only below the normal's precision merge).
        /// </summary>
        internal static void WriteSwb2(BinaryWriter writer, Part part)
        {
            int triangles = part.TriangleCount, corners = 3 * triangles;
            float[] p = part.Positions, n = part.Normals;
            var octs = new short[2 * part.VertexCount];
            for (int v = 0; v < part.VertexCount; v++)
                OctEncode(n[3 * v], n[3 * v + 1], n[3 * v + 2], out octs[2 * v], out octs[2 * v + 1]);

            var number = new Dictionary<Key, int>();
            var first = new List<int>();                  // for each written vertex, the part's vertex it is
            var ids = new int[corners];
            for (int c = 0; c < corners; c++)
            {
                int v = part.Indices[c];
                var key = new Key(Bits(p[3 * v]), Bits(p[3 * v + 1]), Bits(p[3 * v + 2]), octs[2 * v], octs[2 * v + 1], 0);
                if (!number.TryGetValue(key, out int id))
                {
                    id = first.Count;
                    number.Add(key, id);
                    first.Add(v);
                }
                ids[c] = id;
            }
            int vertices = first.Count, width = IndexWidth(vertices);
            writer.Write(triangles);
            writer.Write(vertices);

            var plane = new byte[Math.Max(vertices, corners)];
            for (int b = 0; b < 12; b++)
            {
                for (int k = 0; k < vertices; k++)
                    plane[k] = (byte)(Bits(p[3 * first[k] + b / 4]) >> 8 * (b % 4));
                writer.Write(plane, 0, vertices);
            }
            for (int b = 0; b < 4; b++)
            {
                for (int k = 0; k < vertices; k++)
                    plane[k] = (byte)(octs[2 * first[k] + b / 2] >> 8 * (b % 2));
                writer.Write(plane, 0, vertices);
            }
            var distances = new int[corners];
            for (int c = 0, next = 0; c < corners; c++)
                if (ids[c] == next) next++;
                else distances[c] = next - ids[c];
            for (int b = 0; b < width; b++)
            {
                for (int c = 0; c < corners; c++)
                    plane[c] = (byte)(distances[c] >> 8 * b);
                writer.Write(plane, 0, corners);
            }
            writer.Write(part.Swatches, 0, triangles);
        }

        // An index's bytes: two while every distance (at most the vertex count) fits them.
        private static int IndexWidth(int vertices) => vertices <= ushort.MaxValue ? 2 : 4;

        /// <summary>Octahedral encoding of a direction (any length but zero) in two int16.</summary>
        internal static void OctEncode(float x, float y, float z, out short u, out short v)
        {
            float l = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
            float a = x / l, b = y / l;
            if (z < 0f)
            {
                float fa = (1f - Math.Abs(b)) * (a >= 0f ? 1f : -1f), fb = (1f - Math.Abs(a)) * (b >= 0f ? 1f : -1f);
                a = fa; b = fb;
            }
            u = (short)Math.Round(a * kOctScale);
            v = (short)Math.Round(b * kOctScale);
        }

        /// <summary>The unit direction of an octahedral code.</summary>
        internal static void OctDecode(short u, short v, out float x, out float y, out float z)
        {
            float a = Math.Max(u / kOctScale, -1f), b = Math.Max(v / kOctScale, -1f);
            x = a; y = b; z = 1f - Math.Abs(a) - Math.Abs(b);
            if (z < 0f)
            {
                x = (1f - Math.Abs(b)) * (a >= 0f ? 1f : -1f);
                y = (1f - Math.Abs(a)) * (b >= 0f ? 1f : -1f);
            }
            float s = 1f / (float)Math.Sqrt(x * x + y * y + z * z);
            x *= s; y *= s; z *= s;
        }

        // Finite and within the limit (NaN and infinities fail the comparison).
        private static bool InRange(float value, float limit) => Math.Abs(value) <= limit;

        /// <summary>
        /// SWB1's corners joined into vertices: corners equal in all six floats' bits are one vertex (nothing is rounded, so the
        /// triangles are exactly the file's). Checks each corner as Bus Driver always has. One instance serves a file's parts in turn.
        /// </summary>
        internal sealed class CornerMerge
        {
            private readonly Dictionary<Key, int> m_Number = new Dictionary<Key, int>();
            private readonly List<float> m_Positions = new List<float>(), m_Normals = new List<float>();
            private readonly int[] m_Bits = new int[6];

            internal int Add(Reader reader)
            {
                for (int i = 0; i < 6; i++)
                    m_Bits[i] = reader.ReadInt32();
                var key = new Key(m_Bits[0], m_Bits[1], m_Bits[2], m_Bits[3], m_Bits[4], m_Bits[5]);
                if (m_Number.TryGetValue(key, out int id))
                    return id;
                float px = Float(m_Bits[0]), py = Float(m_Bits[1]), pz = Float(m_Bits[2]);
                float nx = Float(m_Bits[3]), ny = Float(m_Bits[4]), nz = Float(m_Bits[5]);
                if (!InRange(px, kPositionLimit) || !InRange(py, kPositionLimit) || !InRange(pz, kPositionLimit))
                    throw new InvalidDataException("The vehicle mesh contains an invalid position");
                if (!InRange(nx, kNormalLimit) || !InRange(ny, kNormalLimit) || !InRange(nz, kNormalLimit) || nx * nx + ny * ny + nz * nz < 1e-8f)
                    throw new InvalidDataException("The vehicle mesh contains an invalid normal");
                id = m_Number.Count;
                m_Number.Add(key, id);
                m_Positions.Add(px); m_Positions.Add(py); m_Positions.Add(pz);
                m_Normals.Add(nx); m_Normals.Add(ny); m_Normals.Add(nz);
                return id;
            }

            internal Part Take(int[] indices, byte[] swatches)
            {
                var part = new Part(m_Positions.ToArray(), m_Normals.ToArray(), indices, swatches);
                m_Number.Clear(); m_Positions.Clear(); m_Normals.Clear();
                return part;
            }
        }

        private readonly struct Key : IEquatable<Key>
        {
            private readonly int m_A, m_B, m_C, m_D, m_E, m_F;
            internal Key(int a, int b, int c, int d, int e, int f) { m_A = a; m_B = b; m_C = c; m_D = d; m_E = e; m_F = f; }
            public bool Equals(Key o) => m_A == o.m_A && m_B == o.m_B && m_C == o.m_C && m_D == o.m_D && m_E == o.m_E && m_F == o.m_F;
            public override bool Equals(object o) => o is Key k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = m_A;
                    h = h * 397 ^ m_B; h = h * 397 ^ m_C; h = h * 397 ^ m_D; h = h * 397 ^ m_E; h = h * 397 ^ m_F;
                    return h;
                }
            }
        }

        // A float's bits and back (BitConverter's SingleToInt32Bits is not in .NET Framework 4.8).
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] internal int Int;
            [FieldOffset(0)] internal float Float;
        }

        internal static int Bits(float value) => new FloatBits { Float = value }.Int;
        private static float Float(int bits) => new FloatBits { Int = bits }.Float;

        /// <summary>
        /// Little-endian primitives from a stream, read in big blocks: BinaryReader asked the gzip stream for four bytes at a time (25
        /// million times for the SWB6109EV12G's 1.7 million triangles), which cost most of a mesh's load.
        /// </summary>
        internal sealed class Reader
        {
            private readonly Stream m_Stream;
            private readonly byte[] m_Buffer = new byte[1 << 17];
            private int m_Pos, m_Count;

            internal Reader(Stream stream) { m_Stream = stream; }

            // At least n bytes in the buffer, or an EndOfStreamException (as BinaryReader gave).
            private void Need(int n)
            {
                if (m_Count - m_Pos >= n) return;
                int left = m_Count - m_Pos;
                if (left > 0) Buffer.BlockCopy(m_Buffer, m_Pos, m_Buffer, 0, left);
                m_Pos = 0;
                m_Count = left;
                while (m_Count < n)
                {
                    int read = m_Stream.Read(m_Buffer, m_Count, m_Buffer.Length - m_Count);
                    if (read <= 0) throw new EndOfStreamException();
                    m_Count += read;
                }
            }

            /// <summary>Whether every byte has been read.</summary>
            internal bool AtEnd()
            {
                if (m_Pos < m_Count) return false;
                m_Pos = m_Count = 0;
                int read = m_Stream.Read(m_Buffer, 0, m_Buffer.Length);
                if (read <= 0) return true;
                m_Count = read;
                return false;
            }

            internal byte ReadByte() { Need(1); return m_Buffer[m_Pos++]; }

            internal int ReadInt32()
            {
                Need(4);
                int v = m_Buffer[m_Pos] | m_Buffer[m_Pos + 1] << 8 | m_Buffer[m_Pos + 2] << 16 | m_Buffer[m_Pos + 3] << 24;
                m_Pos += 4;
                return v;
            }

            internal uint ReadUInt32() => (uint)ReadInt32();

            internal float ReadSingle() => Float(ReadInt32());           // x86 and ARM Windows are little-endian, like the file

            /// <summary>count bytes into the start of into.</summary>
            internal void ReadBytes(byte[] into, int count)
            {
                for (int done = 0; done < count;)
                {
                    if (m_Pos == m_Count) Need(1);
                    int n = Math.Min(count - done, m_Count - m_Pos);
                    Buffer.BlockCopy(m_Buffer, m_Pos, into, done, n);
                    m_Pos += n;
                    done += n;
                }
            }
        }
    }
}
