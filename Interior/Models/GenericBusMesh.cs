using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>SWB2 geometry for the built-in bus and resolved generic interiors. SWU1 preserves palette ramps and wrap UVs.</summary>
    internal sealed class GenericBusMesh
    {
        internal const string Resource = "CDLC.BusDriver.CDLCBus12";
        static readonly ConcurrentDictionary<KitModelKey, Lazy<GenericBusMesh>> s_Cache = new ConcurrentDictionary<KitModelKey, Lazy<GenericBusMesh>>();
        readonly SwbCodec.Part[][] m_Parts;
        readonly float2[][][] m_Uv;
        internal readonly KitPartDef[] Frames;
        internal readonly FrameDifference[] FrameDifferences;

        internal sealed class FrameDifference
        {
            public int part, component, ulps;
            public float stored, runtime;
            public double delta;
        }

        GenericBusMesh(KitPartDef[] frames, SwbCodec.Part[][] parts, float2[][][] uv, FrameDifference[] differences = null)
        { Frames = frames; m_Parts = parts; m_Uv = uv; FrameDifferences = differences ?? Array.Empty<FrameDifference>(); }

        internal static GenericBusMesh For(BusInteriorSpec spec) => s_Cache.GetOrAdd(new BusInterior(spec, true).Key,
            _ => new Lazy<GenericBusMesh>(() => LoadOrBuild(spec), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        static GenericBusMesh LoadOrBuild(BusInteriorSpec spec)
        {
            var assembly = typeof(GenericBusMesh).Assembly;
            if (new BusInterior(spec, true).Key.Equals(new BusInterior(BusInteriorSpec.Default, true).Key))
            {
                using (Stream mesh = assembly.GetManifestResourceStream(Resource + ".swb2.gz"))
                using (Stream uv = assembly.GetManifestResourceStream(Resource + ".swu1.gz"))
                {
                    if (mesh != null && uv != null) return Read(mesh, uv, KitBuild.PartTable(new BusInterior(spec)));
                    if (mesh != null || uv != null) throw new InvalidDataException("Incomplete built-in bus resource pair.");
                }
            }
            // A resolved third-party shape is built once; the same codec/readback is used as the shipped 12 m mesh.
            var generated = Generate(spec);
            using (var mesh = new MemoryStream())
            using (var uv = new MemoryStream())
            {
                generated.Write(mesh, uv, BusInterior.Eye(spec));
                mesh.Position = uv.Position = 0;
                return Read(mesh, uv, generated.Frames);
            }
        }

        internal static GenericBusMesh Generate(BusInteriorSpec spec)
        {
            var raw = new BusInterior(spec, true);
            spec = raw.Spec;
            var model = KitBuild.BuildAll(raw, new MeshKit());
            if (model.Error != null) throw new InvalidDataException(model.Error);
            var frames = model.Parts;
            float[] rearBands = raw.RearShellBands;
            var parts = new SwbCodec.Part[3][];
            var uvs = new float2[3][][];
            for (int lod = 0; lod < 3; lod++)
            {
                parts[lod] = new SwbCodec.Part[frames.Length]; uvs[lod] = new float2[frames.Length][];
                foreach (KitPartDef frame in frames)
                {
                    KitMeshData data = (frame.MeshOf < 0 ? frame : frames[frame.MeshOf]).Data[lod];
                    var p = new List<float>(); var n = new List<float>(); var ids = new List<int>();
                    var sw = new List<byte>(); var uv = new List<float2>();
                    if (data != null)
                    {
                        float4x4 matrix = new float4x4(frame.Rot, frame.Pivot);
                        float step = lod == 0 ? .18f : lod == 1 ? .32f : .70f;
                        bool skin = frame.Index == 0 || frame.Index >= 4 && frame.Index <= 9 || frame.Index >= 16 && frame.Index <= 18 || frame.Index >= 29 && frame.Index <= 33;
                        for (int sub = 0; sub < data.SubFin.Length; sub++)
                            for (int at = data.SubStart[sub]; at < data.SubStart[sub] + data.SubCount[sub]; at += 3)
                            {
                                int[] polygonIndices = skin ? CoplanarQuad(data, at, data.SubStart[sub] + data.SubCount[sub]) : null;
                                int[] sourceIndices = polygonIndices ?? new[] { data.Idx[at], data.Idx[at + 1], data.Idx[at + 2] };
                                if (polygonIndices != null) at += 3;
                                var poly = new List<Corner>(sourceIndices.Length);
                                foreach (int v in sourceIndices)
                                {
                                    float3 position = math.transform(matrix, data.P[v]);
                                    float bandY = math.round(position.y / step) * step;
                                    if (skin && math.abs(position.y - bandY) < 1e-6f) position.y = bandY;
                                    if (skin) foreach (float edge in rearBands)
                                        if (math.abs(position.y - edge) < 1e-6f) position.y = edge;
                                    poly.Add(new Corner(position, math.mul(frame.Rot, data.N[v]), data.UV[v]));
                                }
                                byte swatch = Swatch(data.SubFin[sub], poly[0].Uv, frame.Index);
                                bool rigid = BusBarrel.MovingRigid(frame.Index);
                                float min = float.MaxValue, max = float.MinValue, extent = 0;
                                foreach (Corner corner in poly) { min = math.min(min, corner.P.y); max = math.max(max, corner.P.y); extent = math.max(extent, math.abs(corner.P.x)); }
                                // Adjacent short panels must use the same bands as tall panels: deforming a T-junction opens a seam.
                                if (rigid || !skin || extent < .9f || max - min < 1e-6f) Emit(poly);
                                else
                                {
                                    int first = (int)math.floor(min / step), last = (int)math.floor(max / step);
                                    for (int band = first; band <= last; band++)
                                    {
                                        float low = band * step, high = (band + 1) * step;
                                        foreach (float edge in rearBands)
                                            if (edge > low + 1e-6f && edge < high - 1e-6f)
                                            {
                                                Emit(Clip(Clip(poly, low, true), edge, false));
                                                low = edge;
                                            }
                                        Emit(Clip(Clip(poly, low, true), high, false));
                                    }
                                }
                                void Emit(List<Corner> polygon)
                                {
                                    for (int i = 1; i + 1 < polygon.Count; i++)
                                    {
                                        Corner a = polygon[0], b = polygon[i], c = polygon[i + 1];
                                        if (math.lengthsq(math.cross(b.P - a.P, c.P - a.P)) < 1e-16f) continue;
                                        sw.Add(swatch); Add(a); Add(b); Add(c);
                                    }
                                }
                                void Add(Corner corner)
                                {
                                    float3 point = rigid ? corner.P : BusBarrel.Point(spec, corner.P);
                                    float3 normal = rigid ? corner.N : BusBarrel.Normal(spec, corner.P, corner.N);
                                    if (frame.Index >= BusInterior.PartCount)
                                    { point = math.mul(math.inverse(frame.Rot), point - frame.Pivot); normal = math.mul(math.inverse(frame.Rot), normal); }
                                    ids.Add(ids.Count); p.Add(point.x); p.Add(point.y); p.Add(point.z);
                                    n.Add(normal.x); n.Add(normal.y); n.Add(normal.z); uv.Add(corner.Uv);
                                }
                            }
                    }
                    parts[lod][frame.Index] = new SwbCodec.Part(p.ToArray(), n.ToArray(), ids.ToArray(), sw.ToArray());
                    uvs[lod][frame.Index] = uv.ToArray();
                }
            }
            return new GenericBusMesh(KitBuild.PartTable(new BusInterior(spec)), parts, uvs);
        }

        static int[] CoplanarQuad(KitMeshData data, int at, int end)
        {
            if (at + 6 > end) return null;
            int[] a = { data.Idx[at], data.Idx[at + 1], data.Idx[at + 2] };
            int[] b = { data.Idx[at + 3], data.Idx[at + 4], data.Idx[at + 5] };
            float3 n = math.normalizesafe(math.cross(data.P[a[1]] - data.P[a[0]], data.P[a[2]] - data.P[a[0]]));
            float3 m = math.normalizesafe(math.cross(data.P[b[1]] - data.P[b[0]], data.P[b[2]] - data.P[b[0]]));
            if (math.dot(n, m) < .999999f) return null;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (a[i] == b[(j + 1) % 3] && a[(i + 1) % 3] == b[j])
                    {
                        int[] q = { a[(i + 1) % 3], a[(i + 2) % 3], a[i], b[(j + 2) % 3] };
                        for (int k = 0; k < 4; k++)
                            if (math.dot(math.cross(data.P[q[(k + 1) % 4]] - data.P[q[k]], data.P[q[(k + 2) % 4]] - data.P[q[(k + 1) % 4]]), n) < -1e-10f) return null;
                        return q;
                    }
            return null;
        }

        readonly struct Corner
        {
            internal readonly float3 P, N; internal readonly float2 Uv;
            internal Corner(float3 p, float3 n, float2 uv) { P = p; N = n; Uv = uv; }
            internal static Corner Lerp(Corner a, Corner b, float t) => new Corner(math.lerp(a.P, b.P, t), math.normalizesafe(math.lerp(a.N, b.N, t)), math.lerp(a.Uv, b.Uv, t));
        }

        static List<Corner> Clip(List<Corner> poly, float y, bool above)
        {
            var result = new List<Corner>(5);
            if (poly.Count == 0) return result;
            Corner a = poly[poly.Count - 1]; bool ai = above ? a.P.y >= y : a.P.y <= y;
            foreach (Corner b in poly)
            {
                bool bi = above ? b.P.y >= y : b.P.y <= y;
                if (ai != bi) Add(Corner.Lerp(a, b, (y - a.P.y) / (b.P.y - a.P.y)));
                if (bi) Add(b);
                a = b; ai = bi;
            }
            if (result.Count > 1 && math.distancesq(result[0].P, result[result.Count - 1].P) < 1e-12f) result.RemoveAt(result.Count - 1);
            return result;
            void Add(Corner corner)
            {
                if (result.Count == 0 || math.distancesq(result[result.Count - 1].P, corner.P) >= 1e-12f) result.Add(corner);
            }
        }

        static byte Swatch(Fin finish, float2 uv, int part)
        {
            if (part >= BusInterior.WrapLeft && part <= BusInterior.WrapRoof) return (byte)Sw.White;
            if (KitPalette.UsesPalette(finish)) return (byte)math.clamp((int)math.floor(uv.y * KitPalette.Rows), 0, KitPalette.Rows - 1);
            switch (finish)
            {
                case Fin.Chrome: return (byte)Sw.Chrome;
                case Fin.Lamp: return (byte)Sw.LampWhite;
                case Fin.Tail: return (byte)Sw.LampRed;
                case Fin.Beacon: return (byte)Sw.Amber;
                case Fin.Glow: return (byte)Sw.LedAmber;
                case Fin.Hazard: return (byte)Sw.Hazard;
                default: throw new InvalidDataException("Unmapped generic finish " + finish);
            }
        }

        internal void Emit(KitModelBuilder builder, int lod)
        {
            int index = builder.Active; var part = m_Parts[lod][index]; var uv = m_Uv[lod][index];
            MeshKit kit = builder.Part(index);
            using (kit.Shade(ShadeRule.Flat))
            using (index >= BusInterior.PartCount ? kit.At(Frames[index].Pivot, Frames[index].Rot) : default(MeshKit.Scope))
                for (int t = 0; t < part.TriangleCount; t++)
                    using (kit.Paint((Sw)part.Swatches[t]))
                    {
                        int a = Add(3 * t), b = Add(3 * t + 1), c = Add(3 * t + 2);
                        float3 normal = float3.zero;
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int v = part.Indices[3 * t + corner] * 3;
                            normal += new float3(part.Normals[v], part.Normals[v + 1], part.Normals[v + 2]);
                        }
                        if (index >= BusInterior.PartCount) normal = math.mul(Frames[index].Rot, normal);
                        kit.Tri(a, b, c, normal, 1e-18f);
                    }
            int Add(int corner)
            {
                int v = part.Indices[corner] * 3;
                return kit.VUV(new float3(part.Positions[v], part.Positions[v + 1], part.Positions[v + 2]),
                    new float3(part.Normals[v], part.Normals[v + 1], part.Normals[v + 2]), uv[corner]);
            }
        }

        internal void Write(Stream mesh, Stream uv, float3 eye)
        {
            using (var gzip = new GZipStream(mesh, CompressionLevel.Optimal, true))
            using (var writer = new BinaryWriter(gzip))
            using (var uvzip = new GZipStream(uv, CompressionLevel.Optimal, true))
            using (var uvWriter = new BinaryWriter(uvzip))
            {
                writer.Write(SwbCodec.Swb2); writer.Write(3); writer.Write(Frames.Length);
                writer.Write(eye.x); writer.Write(eye.y); writer.Write(eye.z);
                foreach (var f in Frames)
                {
                    writer.Write(f.Parent); writer.Write(FrameFlags(f));
                    foreach (float x in new[] { f.Pivot.x, f.Pivot.y, f.Pivot.z, f.Rot.value.x, f.Rot.value.y, f.Rot.value.z, f.Rot.value.w }) writer.Write(x);
                }
                uvWriter.Write(0x31555753u); uvWriter.Write(3); uvWriter.Write(Frames.Length);
                for (int lod = 0; lod < 3; lod++)
                    for (int part = 0; part < Frames.Length; part++)
                    {
                        SwbCodec.WriteSwb2(writer, m_Parts[lod][part]);
                        float2[] coords = m_Uv[lod][part]; uvWriter.Write(coords.Length);
                        foreach (float2 coord in coords) { uvWriter.Write(coord.x); uvWriter.Write(coord.y); }
                    }
            }
        }

        static int FrameFlags(KitPartDef frame) => (int)frame.Flags | (frame.Name.StartsWith("DoorDrive", StringComparison.Ordinal) ? 0x10000 : 0);

        internal static bool FrameComponentMatches(float stored, float expected, int component)
        {
            if (!math.isfinite(stored) || !math.isfinite(expected)) return false;
            if (stored == expected) return true;
            // Subtraction in the door linkage gives a near-zero result whose ULP
            // is smaller than its operands'. Bound positions to 2 micrometres
            // and quaternion components separately across .NET and Unity Mono.
            return Math.Abs((double)stored - expected) <= (component < 3 ? 2e-6 : 5e-7);
        }

        internal static GenericBusMesh Read(Stream mesh, Stream uv, KitPartDef[] expected)
        {
            using (var gzip = new GZipStream(mesh, CompressionMode.Decompress, true))
            using (var uvzip = new GZipStream(uv, CompressionMode.Decompress, true))
            {
                var r = new SwbCodec.Reader(gzip); var u = new SwbCodec.Reader(uvzip);
                if (r.ReadUInt32() != SwbCodec.Swb2 || r.ReadInt32() != 3 || r.ReadInt32() != expected.Length ||
                    u.ReadUInt32() != 0x31555753u || u.ReadInt32() != 3 || u.ReadInt32() != expected.Length)
                    throw new InvalidDataException("Generic SWB2/SWU1 header mismatch.");
                for (int i = 0; i < 3; i++) if (!math.isfinite(r.ReadSingle())) throw new InvalidDataException("Invalid eye.");
                var differences = new List<FrameDifference>();
                foreach (KitPartDef frame in expected)
                {
                    if (r.ReadInt32() != frame.Parent || r.ReadInt32() != FrameFlags(frame)) throw new InvalidDataException("Generic frame mismatch.");
                    int component = 0;
                    foreach (float value in new[] { frame.Pivot.x, frame.Pivot.y, frame.Pivot.z, frame.Rot.value.x, frame.Rot.value.y, frame.Rot.value.z, frame.Rot.value.w })
                    {
                        float stored = r.ReadSingle();
                        if (!FrameComponentMatches(stored, value, component))
                            throw new InvalidDataException($"Generic frame {frame.Index} ({frame.Name}) component {component}: stored={stored:R}, runtime={value:R}, delta={(double)stored - value:R}.");
                        if (stored != value) differences.Add(new FrameDifference { part = frame.Index, component = component,
                            stored = stored, runtime = value, delta = (double)stored - value,
                            ulps = (int)Math.Abs((long)math.asint(stored) - math.asint(value)) });
                        component++;
                    }
                }
                var parts = new SwbCodec.Part[3][]; var coords = new float2[3][][]; int budget = SwbCodec.MaxTriangles;
                for (int lod = 0; lod < 3; lod++)
                {
                    parts[lod] = new SwbCodec.Part[expected.Length]; coords[lod] = new float2[expected.Length][];
                    for (int part = 0; part < expected.Length; part++)
                    {
                        SwbCodec.Part p = parts[lod][part] = SwbCodec.ReadPart(r, SwbCodec.Swb2, ref budget, null);
                        foreach (byte sw in p.Swatches) if (sw >= (int)Sw.Count) throw new InvalidDataException("Unknown generic swatch.");
                        int count = u.ReadInt32(); if (count != p.TriangleCount * 3) throw new InvalidDataException("Generic UV count mismatch.");
                        var values = coords[lod][part] = new float2[count];
                        for (int i = 0; i < count; i++)
                        {
                            values[i] = new float2(u.ReadSingle(), u.ReadSingle());
                            if (!math.all(math.isfinite(values[i])) || math.cmax(math.abs(values[i])) > 1000f) throw new InvalidDataException("Invalid generic UV.");
                        }
                    }
                }
                if (!r.AtEnd() || !u.AtEnd()) throw new InvalidDataException("Generic mesh trailing data.");
                return new GenericBusMesh(expected, parts, coords, differences.ToArray());
            }
        }
    }
}
