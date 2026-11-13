using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// An authored vehicle's runtime mesh (a vehicle pack's &lt;model&gt;.swb1.gz or .swb2.gz, the build tools' Blender export; the
    /// formats are SwbCodec's). It stores model-space vertices/normals for the fixed first 34 parts and local-space
    /// geometry for appended parts: first the door drives (frame bit DriveBit: the
    /// shafts and arms of outward-swing doors, turned with the profile's linkages),
    /// then the wrap surfaces (SurfaceBit: the window skin under the body wrap, and
    /// one for each door leaf that carries its own piece of the wrap, a child of that
    /// leaf), then the straps. The CDLC poser animates every contract.
    /// </summary>
    internal sealed class VehicleMesh
    {
        private const int MaxParts = 256;
        // A part frame flag beside the PartFlags bits (0..15): the part turns with a door shaft. Only right after the 34
        // contract parts, under the shell, in the body's axes. An older Bus Driver rejects the mesh (its pack is not used).
        internal const int DriveBit = 0x10000;
        // A wrap surface: skin that carries the body wrap where the livery is not (windows, door leaves). Right after the door
        // drives. Its parent is the shell (windows: its triangles join the wrap faces) or a door leaf part (4..9: it moves with the
        // leaf). In the body's axes.
        internal const int SurfaceBit = 0x20000;
        internal const int DoorLeafBit = 0x40000;
        private const int WindowKind = 1, DoorKind = 2;

        private readonly Frame[] m_Frames;
        private readonly PartMesh[][] m_Parts;
        private readonly VehicleProfile m_Model;
        internal float3 Eye { get; }

        private readonly struct Frame
        {
            internal readonly int Parent;
            internal readonly PartFlags Flags;
            internal readonly bool Drive, Surface, DoorLeaf;
            internal readonly float3 Pivot;
            internal readonly quaternion Rotation;
            internal Frame(int parent, PartFlags flags, bool drive, bool surface, bool doorLeaf, float3 pivot, quaternion rotation)
            { Parent = parent; Flags = flags; Drive = drive; Surface = surface; DoorLeaf = doorLeaf; Pivot = pivot; Rotation = rotation; }
        }

        private readonly struct Vertex
        {
            internal readonly float3 Position, Normal;
            internal Vertex(float3 position, float3 normal) { Position = position; Normal = normal; }
        }

        private readonly struct Triangle
        {
            internal readonly Sw Swatch;
            internal readonly Vertex A, B, C;
            internal Triangle(Sw swatch, Vertex a, Vertex b, Vertex c)
            { Swatch = swatch; A = a; B = b; C = c; }
        }

        // A part's triangles at one LOD as they are kept for the session: each vertex once (24 bytes), three indices (two bytes each,
        // four in a part of more than 65,535 vertices) and a swatch a triangle; a triangle of copied corners took 76 bytes. Its
        // triangles are made when they are read.
        private sealed class PartMesh : IReadOnlyList<Triangle>
        {
            private readonly Vertex[] m_Vertices;
            private readonly ushort[] m_Short;
            private readonly int[] m_Long;
            private readonly byte[] m_Swatches;

            internal PartMesh(SwbCodec.Part part)
            {
                foreach (byte swatch in part.Swatches)
                    if (swatch >= (int)Sw.Count)
                        throw new InvalidDataException("The vehicle mesh references an unknown palette swatch");
                float[] p = part.Positions, n = part.Normals;
                m_Vertices = new Vertex[part.VertexCount];
                for (int v = 0; v < m_Vertices.Length; v++)
                    m_Vertices[v] = new Vertex(new float3(p[3 * v], p[3 * v + 1], p[3 * v + 2]), math.normalize(new float3(n[3 * v], n[3 * v + 1], n[3 * v + 2])));
                if (m_Vertices.Length > ushort.MaxValue + 1)
                    m_Long = part.Indices;
                else
                {
                    m_Short = new ushort[part.Indices.Length];
                    for (int i = 0; i < m_Short.Length; i++) m_Short[i] = (ushort)part.Indices[i];
                }
                m_Swatches = part.Swatches;
            }

            public int Count => m_Swatches.Length;

            private Vertex At(int corner) => m_Vertices[m_Short != null ? m_Short[corner] : m_Long[corner]];

            public Triangle this[int i] => new Triangle((Sw)m_Swatches[i], At(3 * i), At(3 * i + 1), At(3 * i + 2));

            public Enumerator GetEnumerator() => new Enumerator(this);
            IEnumerator<Triangle> IEnumerable<Triangle>.GetEnumerator() => GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public struct Enumerator : IEnumerator<Triangle>
            {
                private readonly PartMesh m_Mesh;
                private int m_At;
                internal Enumerator(PartMesh mesh) { m_Mesh = mesh; m_At = -1; }
                public Triangle Current => m_Mesh[m_At];
                object System.Collections.IEnumerator.Current => Current;
                public bool MoveNext() => ++m_At < m_Mesh.Count;
                public void Reset() => m_At = -1;
                public void Dispose() { }
            }
        }

        private VehicleMesh(VehicleProfile model, float3 eye, Frame[] frames, PartMesh[][] parts)
        {
            m_Model = model; Eye = eye; m_Frames = frames; m_Parts = parts;
            foreach (Triangle t in parts[0][BusInterior.Livery])
                if (t.Swatch == Sw.WhiteGloss) { LiveryGloss = true; break; }
            while (BusInterior.PartCount + ExtraDoorLeafCount < frames.Length && frames[BusInterior.PartCount + ExtraDoorLeafCount].DoorLeaf) ExtraDoorLeafCount++;
            while (FirstDrive + DriveCount < frames.Length && frames[FirstDrive + DriveCount].Drive) DriveCount++;
            while (FirstSurface + SurfaceCount < frames.Length && frames[FirstSurface + SurfaceCount].Surface) SurfaceCount++;
            m_SurfaceFaces = new BusInterior.WrapFace[SurfaceCount];
            for (int s = 0; s < SurfaceCount; s++)
                m_SurfaceFaces[s] = SurfaceFaceOf(FirstSurface + s);
        }

        private readonly BusInterior.WrapFace[] m_SurfaceFaces;

        /// <summary>The Livery part is built from WhiteGloss: it is drawn in the line colour with the glossy livery material.</summary>
        internal bool LiveryGloss { get; }

        private static bool IsLiveryWhite(Sw swatch) => swatch == Sw.White || swatch == Sw.WhiteGloss;

        // The side a door surface faces: where most of its area's normals point (the kerb side), by its model-space triangles.
        private BusInterior.WrapFace SurfaceFaceOf(int part)
        {
            float3 sum = float3.zero;
            float3 pivot = m_Frames[part].Pivot;
            foreach (Triangle t in m_Parts[0][part])
                sum += t.A.Normal + t.B.Normal + t.C.Normal;
            return sum.x < 0f ? BusInterior.WrapFace.Left : BusInterior.WrapFace.Right;
        }

        private static readonly string[] s_Names =
        {
            "Shell", "SteeringWheel", "SpeedNeedle", "RpmNeedle", "Door0A", "Door0B", "Door1A", "Door1B", "Door2A", "Door2B",
            "Fittings", "Dash", "Cab", "Poles", "Ceiling", "Outside", "Exterior", "Glazing", "Livery",
            "WheelFL", "WheelFR", "WheelRL", "WheelRR", "HeadLamps", "TailLamps", "BrakeLamps", "IndicatorL", "IndicatorR", "ReverseLamps",
            "WrapLeft", "WrapRight", "WrapFront", "WrapRear", "WrapRoof",
        };

        internal int PartCount => m_Frames.Length;
        internal int ExtraDoorLeafCount { get; }
        internal int FirstDrive => BusInterior.PartCount + ExtraDoorLeafCount;
        /// <summary>Door drive parts follow the optional fourth-door leaves.</summary>
        internal int DriveCount { get; }
        internal bool IsDrive(int part) => part >= FirstDrive && part < FirstDrive + DriveCount;
        /// <summary>The wrap surface parts: FirstSurface .. FirstSurface + SurfaceCount - 1, after the door drives.</summary>
        internal int SurfaceCount { get; }
        internal int FirstSurface => FirstDrive + DriveCount;
        /// <summary>The first strap part (after the contract parts, door drives and wrap surfaces).</summary>
        internal int FirstStrap => FirstSurface + SurfaceCount;
        internal bool IsSurface(int part) => part >= FirstSurface && part < FirstStrap;
        /// <summary>The part a wrap surface hangs from: the shell (a window skin) or a door leaf part.</summary>
        internal int SurfaceParent(int part) => m_Frames[part].Parent;
        /// <summary>The wrap face (Left or Right) a door's surface is drawn from (its picture), by the way it faces.</summary>
        internal BusInterior.WrapFace SurfaceFace(int part) => m_SurfaceFaces[part - FirstSurface];
        internal float3 Pivot(int part) => m_Frames[part].Pivot;
        internal quaternion Rotation(int part) => m_Frames[part].Rotation;
        internal int TriangleCount(int part, int lod) => m_Parts[lod][part].Count;

        /// <summary>
        /// The model-space corners of a part's triangles at a LOD (an appended part's are stored about its pivot): what the native
        /// cab matches the game asset's triangles against (BusNativeCab).
        /// </summary>
        internal void Corners(int part, int lod, List<float3> into)
        {
            Frame frame = m_Frames[part];
            bool local = part >= BusInterior.PartCount;
            foreach (Triangle t in m_Parts[lod][part])
            {
                into.Add(local ? frame.Pivot + math.mul(frame.Rotation, t.A.Position) : t.A.Position);
                into.Add(local ? frame.Pivot + math.mul(frame.Rotation, t.B.Position) : t.B.Position);
                into.Add(local ? frame.Pivot + math.mul(frame.Rotation, t.C.Position) : t.C.Position);
            }
        }

        internal void Parts(KitPartList parts)
        {
            for (int i = 0; i < m_Frames.Length; i++)
            {
                Frame f = m_Frames[i];
                string name = i < s_Names.Length ? s_Names[i] : f.DoorLeaf ? "Door3" + (i == BusInterior.PartCount ? "A" : "B") : f.Drive ? "DoorDrive" + (i - FirstDrive) :
                    f.Surface ? "WrapSurface" + (i - FirstSurface) : "Strap" + (i - FirstStrap);
                parts.Add(i, name, f.Parent, f.Pivot, f.Rotation, f.Flags);
            }
        }

        internal void Define(KitModelBuilder builder, int lod)
        {
            int part = builder.Active;
            if ((uint)part >= (uint)m_Frames.Length || (uint)lod >= (uint)m_Parts.Length)
                throw new InvalidDataException("Vehicle mesh part/LOD index is outside the export");
            MeshKit kit = builder.Part(part);
            // Fixed parts store model-space vertices. Appended door drives and
            // straps store vertices relative to their pivot (shaft axis, header
            // attachment). MeshKit's Bake always converts model -> part, so
            // place them in model space first; otherwise their pivot would be
            // subtracted twice.
            if (part >= BusInterior.PartCount)
            {
                Frame frame = m_Frames[part];
                if (frame.Surface)
                {
                    DefineSurface(kit, part, lod);
                    return;
                }
                using (kit.At(frame.Pivot, frame.Rotation)) Emit();
            }
            else Emit();

            // Only the livery's white (White, or WhiteGloss for a glossy line colour) takes the line colour (the Livery part is drawn in it): the native asset's rule, where only the
            // livery-controlled white is tinted. The livery's other paints are drawn in their own colours with the exterior paint; they
            // stay wrap body (the wrap is cut from the whole Livery part).
            void Emit()
            {
                using (kit.Shade(ShadeRule.Flat))
                {
                    foreach (Triangle t in m_Parts[lod][part])
                        if (part != BusInterior.Livery || IsLiveryWhite(t.Swatch))
                            Put(t);
                    if (part == BusInterior.Exterior)
                        foreach (Triangle t in m_Parts[lod][BusInterior.Livery])
                            if (!IsLiveryWhite(t.Swatch))
                                Put(t);
                }
            }

            void Put(Triangle t)
            {
                using (kit.Paint(t.Swatch))
                {
                    int a = kit.V(t.A.Position, t.A.Normal);
                    int b = kit.V(t.B.Position, t.B.Normal);
                    int c = kit.V(t.C.Position, t.C.Normal);
                    float3 normal = t.A.Normal + t.B.Normal + t.C.Normal;
                    if (math.lengthsq(normal) < 1e-8f)
                        normal = math.cross(t.B.Position - t.A.Position, t.C.Position - t.A.Position);
                    kit.Tri(a, b, c, normal, 1e-18f);
                }
            }
        }

        // A door's wrap surface: drawn from the part's own frame (a child of the leaf, so it moves with it), its picture UVs from
        // the leaf's closed place. The window skin (parent: the shell) is drawn by the wrap faces, not here.
        private void DefineSurface(MeshKit kit, int part, int lod)
        {
            Frame frame = m_Frames[part];
            if (frame.Parent == BusInterior.Shell)
                return;
            BusInterior.WrapFace face = SurfaceFace(part);
            using (kit.At(frame.Pivot, frame.Rotation))
            using (kit.Paint(Sw.White))
            using (kit.Shade(ShadeRule.Flat))
                foreach (Triangle t in m_Parts[lod][part])
                {
                    float2 uvA = WrapUV(new Vertex(t.A.Position + frame.Pivot, t.A.Normal), face), uvB = WrapUV(new Vertex(t.B.Position + frame.Pivot, t.B.Normal), face),
                        uvC = WrapUV(new Vertex(t.C.Position + frame.Pivot, t.C.Normal), face);
                    float2 ab = uvB - uvA, ac = uvC - uvA;
                    if (math.abs(ab.x * ac.y - ab.y * ac.x) < 1e-12f)
                        continue;
                    float3 n = math.normalizesafe(t.A.Normal + t.B.Normal + t.C.Normal);
                    int a = kit.VUV(t.A.Position, t.A.Normal, uvA), b = kit.VUV(t.B.Position, t.B.Normal, uvB), c = kit.VUV(t.C.Position, t.C.Normal, uvC);
                    kit.Tri(a, b, c, n, 1e-18f);
                }
        }

        /// <summary>
        /// Image wraps reuse the actual painted surface, including its curved
        /// nose and all wheel/door apertures. The normal selects the image face;
        /// the shader receives ordinary normalized model-space picture UVs.
        /// </summary>
        internal void DefineWrap(KitModelBuilder builder, int lod, BusInterior.WrapFace face)
        {
            MeshKit kit = builder.Part(BusInterior.WrapLeft + (int)face);
            using (kit.Paint(Sw.White))
            using (kit.Shade(ShadeRule.Flat))
                foreach (Triangle t in WrapPieces(WrapBody(lod, face), face))
                {
                    float3 n = math.normalizesafe(t.A.Normal + t.B.Normal + t.C.Normal);
                    float2 ab = WrapUV(t.B, face) - WrapUV(t.A, face), ac = WrapUV(t.C, face) - WrapUV(t.A, face);
                    if (math.abs(ab.x * ac.y - ab.y * ac.x) < 1e-12f)
                        continue;
                    int a = WrapVertex(kit, t.A, face), b = WrapVertex(kit, t.B, face), c = WrapVertex(kit, t.C, face);
                    kit.Tri(a, b, c, n, 1e-18f);
                }
            // the window skins, placed where they are (no extra offset: the build puts them out from the glass)
            using (kit.Paint(Sw.White))
            using (kit.Shade(ShadeRule.Flat))
                foreach (int part in WindowSurfaces())
                {
                    float3 pivot = m_Frames[part].Pivot;
                    foreach (Triangle local in m_Parts[lod][part])
                    {
                        var t = new Triangle(local.Swatch, new Vertex(local.A.Position + pivot, local.A.Normal), new Vertex(local.B.Position + pivot, local.B.Normal),
                            new Vertex(local.C.Position + pivot, local.C.Normal));
                        float3 middle = (t.A.Position + t.B.Position + t.C.Position) / 3f;
                        float3 n = math.normalizesafe(t.A.Normal + t.B.Normal + t.C.Normal);
                        if (!WrapFaceOf(middle, n, out BusInterior.WrapFace actual) || actual != face)
                            continue;
                        float2 uvA = WrapUV(t.A, face), uvB = WrapUV(t.B, face), uvC = WrapUV(t.C, face);
                        float2 ab = uvB - uvA, ac = uvC - uvA;
                        if (math.abs(ab.x * ac.y - ab.y * ac.x) < 1e-12f)
                            continue;
                        int a = kit.VUV(t.A.Position, t.A.Normal, uvA), b = kit.VUV(t.B.Position, t.B.Normal, uvB), c = kit.VUV(t.C.Position, t.C.Normal, uvC);
                        kit.Tri(a, b, c, n, 1e-18f);
                    }
                }
        }

        private IEnumerable<int> WindowSurfaces()
        {
            for (int part = FirstSurface; part < FirstStrap; part++)
                if (m_Frames[part].Parent == BusInterior.Shell) yield return part;
        }

        /// <summary>
        /// The picture-space triangles of the wrap's areas that are not body paint, for a face: the windows (<see cref="WindowKind"/>
        /// 1) and the door leaves (2), where a see-through part of the picture gets its white outline. Same UVs as the wrap uses.
        /// </summary>
        internal List<(byte kind, float2x3 uv)> WrapAreas(BusInterior.WrapFace face)
        {
            var result = new List<(byte, float2x3)>();
            for (int part = FirstSurface; part < FirstStrap; part++)
            {
                Frame frame = m_Frames[part];
                bool door = frame.Parent != BusInterior.Shell;
                if (door && SurfaceFace(part) != face)
                    continue;
                foreach (Triangle local in m_Parts[0][part])
                {
                    var t = new Triangle(local.Swatch, new Vertex(local.A.Position + frame.Pivot, local.A.Normal), new Vertex(local.B.Position + frame.Pivot, local.B.Normal),
                        new Vertex(local.C.Position + frame.Pivot, local.C.Normal));
                    float3 middle = (t.A.Position + t.B.Position + t.C.Position) / 3f;
                    float3 n = math.normalizesafe(t.A.Normal + t.B.Normal + t.C.Normal);
                    if (!door && (!WrapFaceOf(middle, n, out BusInterior.WrapFace actual) || actual != face))
                        continue;
                    float2 a = WrapUV(t.A, face), b = WrapUV(t.B, face), c = WrapUV(t.C, face);
                    float2 ab = b - a, ac = c - a;
                    if (math.abs(ab.x * ac.y - ab.y * ac.x) < 1e-12f)
                        continue;
                    result.Add(((byte)(door ? DoorKind : WindowKind), new float2x3(a, b, c)));
                }
            }
            return result;
        }

        /// <summary>
        /// The parts of a part's triangles that belong to a wrap face. A triangle is cut where the faces' normals meet (the normal's
        /// dominant axis changes: the same line for both neighbours, so they share one seam instead of a zigzag of whole triangles that
        /// flip with the tessellation) and at the picture's extent (the roof edge, the skirts, the ends: nothing is stretched over the
        /// clamped edge). The thresholds and the sign of the normal against the side still decide per piece. The pieces are then cleaned
        /// (specks and thin strips that touch nothing else are dropped), unless clean is false.
        /// </summary>
        private List<Triangle> WrapPieces(IEnumerable<Triangle> source, BusInterior.WrapFace face, bool clean = true)
        {
            BusInteriorSpec spec = m_Model.Spec;
            float bottom = m_Model.WrapBottom;
            var clips = new List<Func<Vertex, float>>();
            foreach (Func<float3, float> test in WrapNormalTests(face))
                clips.Add(v => test(v.Normal));
            switch (face)
            {
                case BusInterior.WrapFace.Left:
                case BusInterior.WrapFace.Right:
                    clips.Add(v => v.Position.y - bottom);
                    clips.Add(v => spec.RoofY - v.Position.y);
                    clips.Add(v => spec.FrontZ - v.Position.z);
                    clips.Add(v => v.Position.z - spec.RearZ);
                    break;
                case BusInterior.WrapFace.Front:
                case BusInterior.WrapFace.Rear:
                    clips.Add(v => v.Position.y - bottom);
                    clips.Add(v => spec.RoofY - v.Position.y);
                    clips.Add(v => v.Position.x + spec.HalfWidth);
                    clips.Add(v => spec.HalfWidth - v.Position.x);
                    break;
                default:
                    clips.Add(v => spec.FrontZ - v.Position.z);
                    clips.Add(v => v.Position.z - spec.RearZ);
                    clips.Add(v => v.Position.x + spec.HalfWidth);
                    clips.Add(v => spec.HalfWidth - v.Position.x);
                    break;
            }

            var result = new List<Triangle>();
            var poly = new List<Vertex>(8);
            foreach (Triangle t in source)
            {
                poly.Clear();
                poly.Add(t.A); poly.Add(t.B); poly.Add(t.C);
                foreach (Func<Vertex, float> clip in clips)
                {
                    poly = ClipPolygon(poly, clip);
                    if (poly.Count < 3)
                        break;
                }
                if (poly.Count < 3)
                    continue;
                float3 middle = float3.zero, normal = float3.zero;
                foreach (Vertex v in poly) { middle += v.Position; normal += v.Normal; }
                middle /= poly.Count;
                normal = math.normalizesafe(normal);
                if (math.abs(middle.x) > spec.HalfWidth + .035f || !WrapPositionOk(face, middle, normal))
                    continue;
                for (int i = 1; i + 1 < poly.Count; i++)
                    result.Add(new Triangle(t.Swatch, Smooth(poly[0]), Smooth(poly[i]), Smooth(poly[i + 1])));
            }
            return clean ? CleanWrapPieces(result, face) : result;
        }

        // The livery's triangles with smoothed normals, per LOD (made once): the wrap's faces are told apart by the surface's normal, so the
        // normal must follow the surface. The model's front is one continuous (C2) surface, but the normals stored in the mesh are the
        // exporter's and carry its tessellation: where a face's seam crosses a curved corner it wavered with it. The normals are averaged
        // over the surface as it is joined (the same point, and its neighbours along the edges), only between normals less than
        // kCreaseDegrees apart, so a real crease (the roof edge, the skirts) stays sharp.
        private readonly Triangle[][] m_WrapLivery = new Triangle[MeshKit.MaxLod + 1][], m_WrapGlazing = new Triangle[MeshKit.MaxLod + 1][];
        private const float kCreaseDegrees = 40f;
        private const int kSmoothPasses = 4;

        // The livery and the glazing's frames are smoothed together (they meet along shared edges), then told apart again. Once per
        // LOD, by whichever thread asks first (the model's build and the wrap template run on worker threads): the arrays are filled
        // before they are published, under the lock, so no caller sees a half-copied one.
        private void SmoothWrapBody(int lod)
        {
            lock (m_WrapLivery)
            {
                if (m_WrapLivery[lod] != null)
                    return;
                PartMesh livery = m_Parts[lod][BusInterior.Livery], glazing = m_Parts[lod][BusInterior.Glazing];
                var all = new Triangle[livery.Count + glazing.Count];
                for (int i = 0; i < livery.Count; i++) all[i] = livery[i];
                for (int i = 0; i < glazing.Count; i++) all[livery.Count + i] = glazing[i];
                Triangle[] smooth = SmoothNormals(all);
                var smoothLivery = new Triangle[livery.Count];
                var smoothGlazing = new Triangle[glazing.Count];
                Array.Copy(smooth, 0, smoothLivery, 0, livery.Count);
                Array.Copy(smooth, livery.Count, smoothGlazing, 0, glazing.Count);
                m_WrapGlazing[lod] = smoothGlazing;
                m_WrapLivery[lod] = smoothLivery;      // last: the "done" mark
            }
        }

        private Triangle[] WrapLivery(int lod)
        {
            SmoothWrapBody(lod);
            return m_WrapLivery[lod];
        }

        /// <summary>
        /// Whether the wrap covers the glazing's frames (the pillars between the windows, the window band above them, the rear frame) on a
        /// face: on a model whose wrap covers the windows, everywhere except the front (the windscreen is not wrapped).
        /// </summary>
        private bool WrapCoversGlazing(BusInterior.WrapFace face) => m_Model.WrapWindows && face != BusInterior.WrapFace.Front && face != BusInterior.WrapFace.Roof;

        // The triangles a face's wrap is cut from, smoothed: the livery, and the glazing's frames where WrapCoversGlazing, or in a
        // wrap pillar on any face.
        private IEnumerable<Triangle> WrapBody(int lod, BusInterior.WrapFace face)
        {
            SmoothWrapBody(lod);
            bool all = WrapCoversGlazing(face);
            if (!all && m_Model.WrapPillars.Length == 0)
                return m_WrapLivery[lod];
            var list = new List<Triangle>(m_WrapLivery[lod].Length + m_WrapGlazing[lod].Length);
            list.AddRange(m_WrapLivery[lod]);
            if (all) list.AddRange(m_WrapGlazing[lod]);
            else list.AddRange(PillarPieces(m_WrapGlazing[lod]));
            return list;
        }

        // The parts of the glazing inside the wrap pillars' boxes: each triangle clipped to each box (its six planes), so the picture
        // ends on the box's edge, not on the edges of whichever whole triangles had their centre inside.
        private IEnumerable<Triangle> PillarPieces(Triangle[] glazing)
        {
            var polygon = new List<Vertex>(9);
            var next = new List<Vertex>(9);
            foreach (VehicleProfile.WrapPillar pillar in m_Model.WrapPillars)
                foreach (Triangle t in glazing)
                {
                    float3 lo = math.min(t.A.Position, math.min(t.B.Position, t.C.Position)), hi = math.max(t.A.Position, math.max(t.B.Position, t.C.Position));
                    if (math.any(hi < pillar.Min | lo > pillar.Max))
                        continue;
                    polygon.Clear(); polygon.Add(t.A); polygon.Add(t.B); polygon.Add(t.C);
                    for (int plane = 0; plane < 6 && polygon.Count >= 3; plane++)
                    {
                        int axis = plane >> 1;
                        bool upper = (plane & 1) == 1;
                        float bound = upper ? pillar.Max[axis] : pillar.Min[axis];
                        next.Clear();
                        for (int k = 0; k < polygon.Count; k++)
                        {
                            Vertex p = polygon[k], q = polygon[(k + 1) % polygon.Count];
                            float dp = upper ? bound - p.Position[axis] : p.Position[axis] - bound;
                            float dq = upper ? bound - q.Position[axis] : q.Position[axis] - bound;
                            if (dp >= 0f) next.Add(p);
                            if ((dp >= 0f) != (dq >= 0f))
                            {
                                float s = dp / (dp - dq);
                                next.Add(new Vertex(math.lerp(p.Position, q.Position, s), math.normalizesafe(math.lerp(p.Normal, q.Normal, s))));
                            }
                        }
                        (polygon, next) = (next, polygon);
                    }
                    for (int k = 1; k + 1 < polygon.Count; k++)
                        yield return new Triangle(t.Swatch, polygon[0], polygon[k], polygon[k + 1]);
                }
        }

        /// <summary>A model-space box's picture rectangle on a face (u0, v0, u1, v1; the bounds of its corners, not limited to 0..1).</summary>
        internal float4 WrapRect(BusInterior.WrapFace face, float3 min, float3 max)
        {
            var rect = new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (int k = 0; k < 8; k++)
            {
                float2 uv = WrapUVRaw(new float3((k & 1) == 0 ? min.x : max.x, (k & 2) == 0 ? min.y : max.y, (k & 4) == 0 ? min.z : max.z), face);
                rect = new float4(math.min(rect.xy, uv), math.max(rect.zw, uv));
            }
            return rect;
        }

        private static Triangle[] SmoothNormals(Triangle[] source)
        {
            if (source == null || source.Length == 0)
                return source;
            int n = source.Length, corners = 3 * n;
            var position = new float3[corners];
            var normal = new float3[corners];
            var keys = new (int, int, int)[corners];
            var at = new Dictionary<(int, int, int), List<int>>();
            var next = new Dictionary<(int, int, int), HashSet<(int, int, int)>>();
            for (int i = 0; i < n; i++)
            {
                Vertex[] v = { source[i].A, source[i].B, source[i].C };
                for (int k = 0; k < 3; k++)
                {
                    int c = 3 * i + k;
                    position[c] = v[k].Position;
                    normal[c] = math.normalizesafe(v[k].Normal);
                    keys[c] = ((int)math.round(position[c].x * 2000f), (int)math.round(position[c].y * 2000f), (int)math.round(position[c].z * 2000f));
                    if (!at.TryGetValue(keys[c], out List<int> list)) at[keys[c]] = list = new List<int>(6);
                    list.Add(c);
                }
                for (int k = 0; k < 3; k++)
                {
                    var a = keys[3 * i + k];
                    var b = keys[3 * i + (k + 1) % 3];
                    if (a.Equals(b)) continue;
                    if (!next.TryGetValue(a, out HashSet<(int, int, int)> sa)) next[a] = sa = new HashSet<(int, int, int)>();
                    if (!next.TryGetValue(b, out HashSet<(int, int, int)> sb)) next[b] = sb = new HashSet<(int, int, int)>();
                    sa.Add(b);
                    sb.Add(a);
                }
            }

            float cosCrease = math.cos(math.radians(kCreaseDegrees));
            var smooth = new float3[corners];
            for (int pass = 0; pass < kSmoothPasses; pass++)
            {
                for (int c = 0; c < corners; c++)
                {
                    float3 own = normal[c], sum = own;
                    foreach (int d in at[keys[c]])
                        if (d != c && math.dot(own, normal[d]) > cosCrease) sum += normal[d];
                    if (next.TryGetValue(keys[c], out HashSet<(int, int, int)> around))
                        foreach (var k in around)
                            foreach (int d in at[k])
                                if (math.dot(own, normal[d]) > cosCrease) sum += 0.5f * normal[d];
                    smooth[c] = math.normalizesafe(sum, own);
                }
                var swap = normal; normal = smooth; smooth = swap;
            }

            var result = new Triangle[n];
            for (int i = 0; i < n; i++)
                result[i] = new Triangle(source[i].Swatch, new Vertex(source[i].A.Position, normal[3 * i]), new Vertex(source[i].B.Position, normal[3 * i + 1]),
                    new Vertex(source[i].C.Position, normal[3 * i + 2]));
            return result;
        }

        /// <summary>
        /// The rules a surface's normal must meet to belong to a wrap face, each as a value that is >= 0 when it does: the face's axis is
        /// the normal's dominant one, it points to the outside of that side, and (sides, front, rear) it does not face down more than
        /// kWrapUnderside. One definition for cutting the wrap's mesh (WrapPieces) and for the projected template (ProjectWrap).
        /// </summary>
        private static List<Func<float3, float>> WrapNormalTests(BusInterior.WrapFace face)
        {
            switch (face)
            {
                case BusInterior.WrapFace.Left:
                case BusInterior.WrapFace.Right:
                    return new List<Func<float3, float>>
                    {
                        n => math.abs(n.x) - math.abs(n.y), n => math.abs(n.x) - math.abs(n.z),
                        face == BusInterior.WrapFace.Left ? (Func<float3, float>)(n => -n.x) : n => n.x, n => n.y + kWrapUnderside,
                    };
                case BusInterior.WrapFace.Front:
                case BusInterior.WrapFace.Rear:
                    return new List<Func<float3, float>>
                    {
                        n => math.abs(n.z) - math.abs(n.x), n => math.abs(n.z) - (n.y < 0f ? kWrapBevel : 1f) * math.abs(n.y),
                        face == BusInterior.WrapFace.Front ? (Func<float3, float>)(n => n.z) : n => -n.z, n => n.y + kWrapUnderside,
                    };
                default:
                    return new List<Func<float3, float>> { n => math.abs(n.y) - math.abs(n.x), n => math.abs(n.y) - math.abs(n.z), n => n.y };
            }
        }

        internal const byte ClassNone = 0, ClassBody = 1, ClassWindow = 2, ClassDoor = 3, ClassBehind = 4, ClassBus = 5;

        // The parts of the bus's outer shape (what the viewer sees of the bus as a silhouette): the body parts, the wheels, the lamps, the
        // mirrors and the wrap surfaces; not the interior seen through an opening.
        private static bool IsOuterShape(int part) =>
            part == BusInterior.Exterior || part == BusInterior.Glazing || part == BusInterior.Livery || part == BusInterior.Outside ||
            (part >= BusInterior.WheelFL && part <= BusInterior.ReverseLamps);

        // A face's picture coordinates of a model point, not limited to the picture (WrapUV limits them).
        private float2 WrapUVRaw(float3 p, BusInterior.WrapFace face)
        {
            BusInteriorSpec spec = m_Model.Spec;
            float width = spec.HalfWidth * 2f, length = spec.FrontZ - spec.RearZ;
            float bottom = m_Model.WrapBottom, height = spec.RoofY - bottom;
            switch (face)
            {
                case BusInterior.WrapFace.Front: return new float2((spec.HalfWidth - p.x) / width, (p.y - bottom) / height);
                case BusInterior.WrapFace.Rear: return new float2((p.x + spec.HalfWidth) / width, (p.y - bottom) / height);
                case BusInterior.WrapFace.Roof: return new float2((spec.FrontZ - p.z) / length, (p.x + spec.HalfWidth) / width);
                default: return new float2((spec.FrontZ - p.z) / length, (p.y - bottom) / height);
            }
        }

        // How far a point is from the viewer looking at the face from outside (less = nearer).
        private static float WrapDepth(float3 p, BusInterior.WrapFace face)
        {
            switch (face)
            {
                case BusInterior.WrapFace.Left: return p.x;
                case BusInterior.WrapFace.Right: return -p.x;
                case BusInterior.WrapFace.Front: return -p.z;
                case BusInterior.WrapFace.Rear: return p.z;
                default: return -p.y;
            }
        }

        /// <summary>
        /// The template of a face by projection: the whole model is drawn orthographically from outside, as the picture is mapped (the same
        /// planar projection as the wrap's UVs), with a depth buffer. A pixel's class is what the viewer sees there: the body paint
        /// (<see cref="ClassBody"/>: the nearest surface is livery whose normal belongs to the face, interpolated across the triangle, so
        /// the edge between faces runs smoothly through the pixels), a window skin (2) or a door piece (3) of the wrap surfaces, or nothing
        /// (0: outside the bus; <see cref="ClassBus"/> (5): the bus there but not painted by this picture: the windscreen and its frame, wheel
        /// arches, lamps, and the surface that turns to face the neighbouring picture). Body paint, a window skin or a door piece
        /// with something in front of it (lamps, plates, grilles, window bars, trim: the picture is there but covered) is
        /// <see cref="ClassBehind"/> (4). The face's picture is
        /// w x h pixels, rows from the bottom; a section covers the share a .. a+s of its width (mirrored for the right side), the
        /// others are drawn by their own calls into the same arrays (depth: w x h floats, start with +infinity).
        /// </summary>
        internal void ProjectWrap(BusInterior.WrapFace face, byte[] classes, float[] depth, int w, int h, float a, float s, bool mirror)
        {
            var tests = WrapNormalTests(face);
            float x0 = mirror ? 1f - (a + s) : a, x1 = mirror ? 1f - a : a + s;
            int px0 = Math.Max(0, (int)Math.Ceiling(x0 * w - .5f - 1e-4f)), px1 = Math.Min(w - 1, (int)Math.Floor(x1 * w - .5f + 1e-4f));
            var skin = new bool[w * h];             // a surface the picture is drawn on is here (nearest or not)
            var outer = new bool[w * h];            // some part of the bus's outer shape is here
            var nearest = new byte[w * h];
            for (int part = 0; part < m_Frames.Length; part++)
            {
                Frame frame = m_Frames[part];
                bool windowSkin = frame.Surface && frame.Parent == BusInterior.Shell;
                bool doorSkin = frame.Surface && !windowSkin && SurfaceFace(part) == face;
                if (BusInterior.WrapLeft <= part && part <= BusInterior.WrapRoof)
                    continue;
                // body paint: the livery, and the glazing's frames and bands on a model whose wrap covers the windows (on the other
                // faces the glazing in a wrap pillar, triangle by triangle)
                bool livery = part == BusInterior.Livery || (part == BusInterior.Glazing && WrapCoversGlazing(face));
                bool pillars = part == BusInterior.Glazing && !livery && m_Model.WrapPillars.Length > 0;
                SmoothWrapBody(0);
                IReadOnlyList<Triangle> triangles = part == BusInterior.Livery ? m_WrapLivery[0] : part == BusInterior.Glazing && (livery || pillars) ? m_WrapGlazing[0] : (IReadOnlyList<Triangle>)m_Parts[0][part];
                // the pillar pieces (body paint) first, then the whole glazing: at equal depth the first drawn stays nearest
                int painted = 0;
                if (pillars)
                {
                    Triangle[] pieces = PillarPieces(m_WrapGlazing[0]).ToArray();
                    painted = pieces.Length;
                    triangles = pieces.Concat(m_WrapGlazing[0]).ToArray();
                }
                bool shape = frame.Surface || IsOuterShape(part);
                // The contract parts store model-space vertices (see Define); only the parts appended after them (door drives, wrap surfaces,
                // straps) are stored about their pivot. Adding the pivot to a contract part moved its geometry by the pivot: a hinged door's
                // leaves came out of their hinges' distance away, over the neighbouring window, as parts "in front of" the picture
                // (SWB6109EV12G's guide, 18 October); wheels, the steering wheel and the needles were displaced the same way.
                float3 pivot = part >= BusInterior.PartCount ? frame.Pivot : float3.zero;
                for (int ti = 0; ti < triangles.Count; ti++)
                {
                    Triangle local = triangles[ti];
                    bool paint = livery || ti < painted;
                    float3 pa = local.A.Position + pivot, pb = local.B.Position + pivot, pc = local.C.Position + pivot;
                    float2 ua = WrapUVRaw(pa, face), ub = WrapUVRaw(pb, face), uc = WrapUVRaw(pc, face);
                    float2 sa = new float2((mirror ? 1f - (a + s * ua.x) : a + s * ua.x) * w, ua.y * h);
                    float2 sb = new float2((mirror ? 1f - (a + s * ub.x) : a + s * ub.x) * w, ub.y * h);
                    float2 sc = new float2((mirror ? 1f - (a + s * uc.x) : a + s * uc.x) * w, uc.y * h);
                    float twice = (sb.x - sa.x) * (sc.y - sa.y) - (sb.y - sa.y) * (sc.x - sa.x);
                    if (math.abs(twice) < 1e-9f)
                        continue;
                    int bx0 = Math.Max(px0, (int)Math.Floor(math.min(sa.x, math.min(sb.x, sc.x)))), bx1 = Math.Min(px1, (int)Math.Ceiling(math.max(sa.x, math.max(sb.x, sc.x))));
                    int by0 = Math.Max(0, (int)Math.Floor(math.min(sa.y, math.min(sb.y, sc.y)))), by1 = Math.Min(h - 1, (int)Math.Ceiling(math.max(sa.y, math.max(sb.y, sc.y))));
                    if (bx0 > bx1 || by0 > by1)
                        continue;
                    float da = WrapDepth(pa, face), db = WrapDepth(pb, face), dc = WrapDepth(pc, face);
                    for (int y = by0; y <= by1; y++)
                        for (int x = bx0; x <= bx1; x++)
                        {
                            float2 p = new float2(x + .5f, y + .5f);
                            float w0 = ((sb.x - p.x) * (sc.y - p.y) - (sb.y - p.y) * (sc.x - p.x)) / twice;
                            float w1 = ((sc.x - p.x) * (sa.y - p.y) - (sc.y - p.y) * (sa.x - p.x)) / twice;
                            float w2 = 1f - w0 - w1;
                            const float e = -0.002f;
                            if (w0 < e || w1 < e || w2 < e)
                                continue;
                            int i = y * w + x;
                            if (shape)
                                outer[i] = true;
                            // a pillar piece lies on the glazing it was cut from: 0.1 mm nearer, so it wins against its own copy
                            float d = w0 * da + w1 * db + w2 * dc - (ti < painted ? 1e-4f : 0f);
                            byte kind = ClassNone;
                            if (paint || windowSkin || doorSkin)
                            {
                                float3 n = math.normalizesafe(w0 * local.A.Normal + w1 * local.B.Normal + w2 * local.C.Normal);
                                bool ok = true;
                                foreach (Func<float3, float> test in tests)
                                    if (test(n) < 0f) { ok = false; break; }
                                if (ok && paint)
                                {
                                    float3 at = w0 * pa + w1 * pb + w2 * pc;
                                    ok = math.abs(at.x) <= m_Model.Spec.HalfWidth + .035f && WrapPositionOk(face, at, n);
                                }
                                if (ok)
                                {
                                    kind = paint ? ClassBody : windowSkin ? ClassWindow : ClassDoor;
                                    skin[i] = true;
                                }
                            }
                            if (d < depth[i])
                            {
                                depth[i] = d;
                                nearest[i] = kind;
                            }
                        }
                }
            }
            for (int y = 0; y < h; y++)
                for (int x = px0; x <= px1; x++)
                {
                    int i = y * w + x;
                    classes[i] = nearest[i] != ClassNone ? nearest[i] : skin[i] ? ClassBehind : outer[i] ? ClassBus : ClassNone;
                }
        }

        // The wrap does not go under a surface that faces down more than this (the bumper's and the roof gutter's curve under the body).
        private const float kWrapUnderside = 0.85f;
        // The front and rear take a surface whose normal tilts down until its vertical part is this many times its horizontal part
        // along the bus (the bevels round the plate's recess and the lamps' housings face 45 degrees down); upwards the roof's face takes over.
        private const float kWrapBevel = 0.6f;
        // Cleaning of a face's pieces: a loose piece (triangles that touch) smaller than this area (m2) is a speck (a screw head, a rubber
        // stop), and one narrower than this width (m) a thin strip (a trim line along an edge, a line the clipping left on a seam): none
        // of them is worth a picture and each shows as fur on the outline. (Single thin triangles are not dropped: the big panels are
        // triangulated with them.)
        private const float kWrapSpeck = 0.01f, kWrapStrip = 0.02f;

        private List<Triangle> CleanWrapPieces(List<Triangle> pieces, BusInterior.WrapFace face)
        {
            float2 size = WrapExtent(face);
            List<Triangle> solid = pieces;

            var result = new List<Triangle>(solid.Count);
            foreach (List<int> comp in WrapComponents(solid))
            {
                float2 lo = new float2(9e9f), hi = new float2(-9e9f);
                float area = 0f;
                foreach (int i in comp)
                {
                    Triangle t = solid[i];
                    float2 a = WrapUV(t.A, face) * size, b = WrapUV(t.B, face) * size, c = WrapUV(t.C, face) * size;
                    lo = math.min(lo, math.min(a, math.min(b, c))); hi = math.max(hi, math.max(a, math.max(b, c)));
                    area += math.abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) * .5f;
                }
                if (area < kWrapSpeck || math.cmin(hi - lo) < kWrapStrip)
                    continue;
                foreach (int i in comp)
                    result.Add(solid[i]);
            }
            return result;
        }

        // The face's size in metres along u and v (what WrapUV normalises by).
        private float2 WrapExtent(BusInterior.WrapFace face)
        {
            BusInteriorSpec spec = m_Model.Spec;
            float width = spec.HalfWidth * 2f, length = spec.FrontZ - spec.RearZ, height = spec.RoofY - m_Model.WrapBottom;
            switch (face)
            {
                case BusInterior.WrapFace.Front:
                case BusInterior.WrapFace.Rear: return new float2(width, height);
                case BusInterior.WrapFace.Roof: return new float2(length, width);
                default: return new float2(length, height);
            }
        }

        private static ((int, int, int), (int, int, int)) Order((int, int, int) a, (int, int, int) b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);

        // Pieces that touch along an edge (to a millimetre) as lists of indices.
        private static List<List<int>> WrapComponents(List<Triangle> pieces)
        {
            var parent = new int[pieces.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            var seen = new Dictionary<((int, int, int), (int, int, int)), int>();
            (int, int, int) Key(Vertex v) => ((int)math.round(v.Position.x * 1000f), (int)math.round(v.Position.y * 1000f), (int)math.round(v.Position.z * 1000f));
            for (int i = 0; i < pieces.Count; i++)
            {
                (int, int, int) a = Key(pieces[i].A), b = Key(pieces[i].B), c = Key(pieces[i].C);
                foreach (((int, int, int), (int, int, int)) edge in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = Order(edge.Item1, edge.Item2);
                    if (seen.TryGetValue(key, out int other)) parent[Find(i)] = Find(other);
                    else seen[key] = i;
                }
            }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                int root = Find(i);
                if (!groups.TryGetValue(root, out List<int> list)) groups[root] = list = new List<int>();
                list.Add(i);
            }
            return new List<List<int>>(groups.Values);
        }

        private static Vertex Smooth(Vertex v) => new Vertex(v.Position, math.normalizesafe(v.Normal));

        // Sutherland-Hodgman against the half space f >= 0 (positions and normals interpolated).
        private static List<Vertex> ClipPolygon(List<Vertex> poly, Func<Vertex, float> f)
        {
            var result = new List<Vertex>(poly.Count + 1);
            for (int i = 0; i < poly.Count; i++)
            {
                Vertex cur = poly[i], next = poly[(i + 1) % poly.Count];
                float fc = f(cur), fn = f(next);
                if (fc >= 0f) result.Add(cur);
                if ((fc >= 0f) != (fn >= 0f))
                {
                    float s = fc / (fc - fn);
                    result.Add(new Vertex(math.lerp(cur.Position, next.Position, s), math.lerp(cur.Normal, next.Normal, s)));
                }
            }
            return result;
        }

        // The position rules of a wrap face (WrapFaceOf's, apart from the dominant axis, which the clipping decided).
        private bool WrapPositionOk(BusInterior.WrapFace face, float3 centre, float3 n)
        {
            switch (face)
            {
                case BusInterior.WrapFace.Roof: return n.y > 0f && centre.y > m_Model.WrapThresholds.y;
                case BusInterior.WrapFace.Left:
                case BusInterior.WrapFace.Right: return math.abs(centre.x) > m_Model.WrapThresholds.x && n.x * centre.x > 0f;
                default: return math.abs(centre.z) > m_Model.WrapThresholds.z && n.z * centre.z > 0f;
            }
        }

        private bool WrapFaceOf(float3 centre, float3 n, out BusInterior.WrapFace face)
        {
            float3 a = math.abs(n);
            if (a.y >= math.max(a.x, a.z))
            {
                face = BusInterior.WrapFace.Roof;
                return n.y > 0f && centre.y > m_Model.WrapThresholds.y;
            }
            if (a.x >= a.z)
            {
                face = n.x < 0f ? BusInterior.WrapFace.Left : BusInterior.WrapFace.Right;
                return math.abs(centre.x) > m_Model.WrapThresholds.x && n.x * centre.x > 0f;
            }
            face = n.z > 0f ? BusInterior.WrapFace.Front : BusInterior.WrapFace.Rear;
            return math.abs(centre.z) > m_Model.WrapThresholds.z && n.z * centre.z > 0f;
        }

        private int WrapVertex(MeshKit kit, Vertex v, BusInterior.WrapFace face) =>
            kit.VUV(v.Position + v.Normal * 0.008f, v.Normal, WrapUV(v, face));

        private float2 WrapUV(Vertex v, BusInterior.WrapFace face)
        {
            BusInteriorSpec spec = m_Model.Spec;
            float width = spec.HalfWidth * 2f, length = spec.FrontZ - spec.RearZ;
            float bottom = m_Model.WrapBottom, height = spec.RoofY - bottom;
            float u, w;
            switch (face)
            {
                case BusInterior.WrapFace.Front:
                    u = (spec.HalfWidth - v.Position.x) / width;
                    w = (v.Position.y - bottom) / height;
                    break;
                case BusInterior.WrapFace.Rear:
                    u = (v.Position.x + spec.HalfWidth) / width;
                    w = (v.Position.y - bottom) / height;
                    break;
                case BusInterior.WrapFace.Roof:
                    u = (spec.FrontZ - v.Position.z) / length;
                    w = (v.Position.x + spec.HalfWidth) / width;
                    break;
                default:
                    u = (spec.FrontZ - v.Position.z) / length;
                    w = (v.Position.y - bottom) / height;
                    break;
            }
            return math.saturate(new float2(u, w));
        }

        /// <summary>The mesh of a profile from its gzip-compressed SWB1 or SWB2 stream (left open).</summary>
        internal static VehicleMesh Load(VehicleProfile model, Stream stream)
        {
            if (stream == null)
                throw new InvalidDataException("No vehicle mesh stream for " + model.Name);
            using (var gzip = new GZipStream(stream, CompressionMode.Decompress, true))
            {
                var reader = new SwbCodec.Reader(gzip);
                uint format = reader.ReadUInt32();
                if (format != SwbCodec.Swb1 && format != SwbCodec.Swb2)
                    throw new InvalidDataException("The vehicle mesh has an unsupported format");
                int lods = reader.ReadInt32(), count = reader.ReadInt32();
                if (lods != MeshKit.MaxLod + 1 || count < BusInterior.PartCount || count > MaxParts)
                    throw new InvalidDataException("The vehicle mesh has an invalid part/LOD count");
                float3 eye = ReadPosition(reader);
                var frames = new Frame[count];
                bool strapsBegun = false, surfacesBegun = false, drivesBegun = false;
                for (int i = 0; i < count; i++)
                {
                    int parent = reader.ReadInt32(), bits = reader.ReadInt32();
                    bool drive = (bits & DriveBit) != 0, surface = (bits & SurfaceBit) != 0, doorLeaf = (bits & DoorLeafBit) != 0;
                    if (i >= BusInterior.PartCount && !drive && !surface && !doorLeaf) strapsBegun = true;
                    // After fixed parts: fourth-door leaves, drives, wrap surfaces, then straps; the flag gates older readers.
                    if (parent < -1 || parent >= i || (i == 0 && parent != -1) || (bits & ~(15 | DriveBit | SurfaceBit | DoorLeafBit)) != 0 ||
                        doorLeaf && (i < BusInterior.PartCount || i >= BusInterior.PartCount + model.ExtraDoorLeafCount ||
                            strapsBegun || surfacesBegun || drivesBegun || drive || surface || parent != 0) ||
                        drive && (i < model.FirstDoorDrive || strapsBegun || surfacesBegun || surface || parent != 0) ||
                        surface && (i < model.FirstDoorDrive || strapsBegun || parent != 0 && !model.IsDoorPart(parent)))
                        throw new InvalidDataException("The vehicle mesh has an invalid part frame " + i);
                    if (surface) surfacesBegun = true;
                    if (drive) drivesBegun = true;
                    float3 pivot = ReadPosition(reader);
                    var q = new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (!math.all(math.isfinite(q)) || math.lengthsq(q) < 0.5f || math.lengthsq(q) > 2f)
                        throw new InvalidDataException("The vehicle mesh has an invalid part rotation " + i);
                    if (surface && math.abs(math.dot(math.normalize(new quaternion(q)).value, quaternion.identity.value)) < .99999f)
                        throw new InvalidDataException("A wrap surface keeps the body's axes: part " + i);
                    frames[i] = new Frame(parent, (PartFlags)(bits & 15), drive, surface, doorLeaf, pivot, math.normalize(new quaternion(q)));
                }
                int budget = SwbCodec.MaxTriangles;
                var merge = format == SwbCodec.Swb1 ? new SwbCodec.CornerMerge() : null;
                var parts = new PartMesh[lods][];
                for (int lod = 0; lod < lods; lod++)
                {
                    parts[lod] = new PartMesh[count];
                    for (int part = 0; part < count; part++)
                        parts[lod][part] = new PartMesh(SwbCodec.ReadPart(reader, format, ref budget, merge));
                }
                if (budget == SwbCodec.MaxTriangles || !reader.AtEnd())
                    throw new InvalidDataException("The vehicle mesh is empty or has trailing data");
                return new VehicleMesh(model, eye, frames, parts);
            }
        }

        private static float3 ReadPosition(SwbCodec.Reader reader)
        {
            var v = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!math.all(math.isfinite(v)) || math.cmax(math.abs(v)) > 100f)
                throw new InvalidDataException("The vehicle mesh contains an invalid position");
            return v;
        }
    }
}
