using System.Collections.Generic;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Wrap: a body wrap (option "Body wrap", the wraps manager in the panel: BusSkins) on the bus's five faces, a picture each, drawn
// as skins just outside the body where it is solid; the windows, doors, lamps, plates and grilles stay as they are.
//   Left, Right  the side from the skirts (WrapBottom) to the roof edge (WrapTop): the lower panels round the wheel arches and
//                between the doors, and the black band above the windows. u: from the front (0) to the rear (1) of the section.
//   Front        the lower front round its corners, from the bumper lip up to the black glass under the windscreen (the lamp
//                clusters, the air intake, the maker's bar and the plate in front of it). u: from the bus's right corner (0,
//                the viewer's left standing in front of the bus) round to its left corner (1).
//   Rear         the rear below the window, from the bumper up, and the strips beside the window (the grille, the plate and the
//                lamps in front of it). u: from the bus's left (0, the viewer's left standing behind the bus) to its right.
//   Roof         over the top from the left side's roof edge to the right side's (under the roof pods). u: from the front (0) to
//                the rear (1) of the section; v: from the left edge (0) over to the right edge (1).
// v runs up (Roof: across). The cab system maps the section's u onto the pictures (an articulated bus: one side picture over
// both sections). WrapLayout gives each face's solid pieces and the parts in front of it, for the templates the mod writes.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        public enum WrapFace : byte { Left, Right, Front, Rear, Roof }
        public const int WrapFaceCount = 5;

        [System.Flags]
        public enum WrapFaces : byte { None = 0, Left = 1, Right = 2, Front = 4, Rear = 8, Roof = 16, All = 31 }

        public const float WrapBottom = kBumperTop;          // the skirts' top (model space y)
        const float kWrapOut = 0.009f;                        // outside the walls, the glazing band and the skirts
        const float kWrapNose = 0.0105f;                      // outside the livery's skin round the front
        const float kWrapRearZ = 0.0095f;                     // in front of the rear panel (behind the grille's, lamps' and plate's faces)
        const float kWrapColumn = 0.12f;                      // column width round the arches

        /// <summary>The top of the side wrap: the roof edge where the side glazing band ends.</summary>
        public float WrapTop => IsAuthored ? Spec.RoofY : BlackBandTop;
        /// <summary>The side wrap's front end (where the side meets the front's rounded corner; the section's front when it has none).</summary>
        public float WrapFrontZ => IsAuthored ? Spec.FrontZ : HasCab ? Spec.FrontZ - kNoseR - 0.005f : Spec.FrontZ;
        public float WrapRearZ => Spec.RearZ;
        public float WrapLength => WrapFrontZ - WrapRearZ;

        float FrontWrapBottom => F - 0.03f;
        float FrontWrapTop => WsBotY - 0.07f;
        float RearWrapTop => RearWinTop;

        /// <summary>Whether this section has the face (the front on the section with the cab, the rear on the last).</summary>
        public bool HasWrap(WrapFace face) => face == WrapFace.Front ? HasCab : face == WrapFace.Rear ? !OpenRear : true;

        /// <summary>
        /// The picture-space triangles of a face that are windows (kind 1) or door leaves (2) on an authored section whose profile has wrap
        /// surfaces: where the wrap's see-through parts get their white outline. Empty for a generated section.
        /// </summary>
        public List<(byte kind, float2x3 uv)> WrapAreas(WrapFace face) =>
            IsAuthored && HasWrap(face) ? AuthoredModel.Mesh.WrapAreas(face) : new List<(byte, float2x3)>();

        /// <summary>
        /// The template of a face by projection (VehicleMesh.ProjectWrap): into classes (w x h, rows from the bottom) what the viewer sees
        /// at each pixel, for an authored section whose picture share is a .. a+s of the width. False for a generated section.
        /// </summary>
        public bool ProjectWrap(WrapFace face, byte[] classes, float[] depth, int w, int h, float a, float s, bool mirror)
        {
            if (!IsAuthored || !HasWrap(face)) return false;
            AuthoredModel.Mesh.ProjectWrap(face, classes, depth, w, h, a, s, mirror);
            return true;
        }

        /// <summary>The picture rectangle of a model-space box on a face (VehicleMesh.WrapRect); null for a generated section.</summary>
        public float4? WrapRect(WrapFace face, float3 min, float3 max) =>
            IsAuthored && HasWrap(face) ? AuthoredModel.Mesh.WrapRect(face, min, max) : (float4?)null;

        /// <summary>The face's size in metres (u, v): a picture in these proportions fits it.</summary>
        public float2 WrapSize(WrapFace face)
        {
            if (IsAuthored)
                return face == WrapFace.Roof ? new float2(WrapLength, 2f * HW) :
                    new float2(face == WrapFace.Front || face == WrapFace.Rear ? 2f * HW : WrapLength, Spec.RoofY - AuthoredModel.WrapBottom);
            switch (face)
            {
                case WrapFace.Front: return new float2(Length(NoseLine(kWrapNose, 6)), FrontWrapTop - FrontWrapBottom);
                case WrapFace.Rear: return new float2(2f * HW, RearWrapTop - WrapBottom);
                case WrapFace.Roof: return new float2(WrapLength, Length(RoofLine(6)));
                default: return new float2(WrapLength, WrapTop - WrapBottom);
            }
        }

        /// <summary>
        /// The visible pieces of the side wrap (-1 left, +1 right) as (z0, y0, z1, y1) in model space: the lower panels in columns
        /// round the wheel arches and between the doors, and the band above the windows.
        /// </summary>
        public List<float4> WrapRects(float side)
        {
            var rects = new List<float4>();
            float lowTop = SillY - 0.035f, bandBottom = WinTop - 0.035f, top = WrapTop;
            foreach (float2 run in SideRuns(side))
            {
                float z = run.x;
                while (z < run.y - 1e-4f)
                {
                    float z1 = math.min(run.y, z + kWrapColumn);
                    float bottom = math.max(WrapBottom, math.max(ArchTopAt(z), ArchTopAt(z1)) + 0.03f);
                    if (lowTop - bottom > 0.02f)
                    {
                        // merge with the column before when it has the same height (a long straight panel: one quad)
                        int last = rects.Count - 1;
                        if (last >= 0 && math.abs(rects[last].z - z) < 1e-4f && math.abs(rects[last].y - bottom) < 1e-4f && math.abs(rects[last].w - lowTop) < 1e-4f)
                            rects[last] = new float4(rects[last].x, bottom, z1, lowTop);
                        else
                            rects.Add(new float4(z, bottom, z1, lowTop));
                    }
                    z = z1;
                }
            }
            rects.Add(new float4(WrapRearZ, bandBottom, WrapFrontZ, top));
            return rects;
        }

        /// <summary>
        /// A face's layout in its (u, v) for a template: the pieces where the picture shows (solid) and, after them, what stands
        /// in front of it (lamps, plates, grilles, roof pods: solid = false). Sides and roof: u over this section.
        /// </summary>
        public List<(float4 uv, bool solid)> WrapLayout(WrapFace face)
        {
            var list = new List<(float4, bool)>();
            if (!HasWrap(face))
                return list;
            if (IsAuthored)
            {
                // The template covers the normalized face; the actual wrap
                // mesh, cut from Livery's triangles, masks glass and openings.
                list.Add((new float4(0f, 0f, 1f, 1f), true));
                return list;
            }
            switch (face)
            {
                case WrapFace.Left:
                case WrapFace.Right:
                {
                    float len = math.max(0.1f, WrapLength), h = WrapTop - WrapBottom;
                    foreach (float4 r in WrapRects(face == WrapFace.Left ? -1f : 1f))
                        list.Add((new float4((WrapFrontZ - r.z) / len, (r.y - WrapBottom) / h, (WrapFrontZ - r.x) / len, (r.w - WrapBottom) / h), true));
                    break;
                }
                case WrapFace.Front:
                {
                    float y0 = FrontWrapBottom, h = FrontWrapTop - y0;
                    List<float2> line = NoseLine(kWrapNose, 6);
                    float total = Length(line), flatStart = 0f;
                    for (int i = 1; i < line.Count && line[i - 1].x > HW - kNoseR + 1e-4f; i++)
                        flatStart += math.distance(line[i - 1], line[i]);
                    float U(float x) => (flatStart + (HW - kNoseR - x)) / total;
                    void Box(float cx, float cy, float w, float hh) => list.Add((new float4(U(cx + 0.5f * w), (cy - 0.5f * hh - y0) / h, U(cx - 0.5f * w), (cy + 0.5f * hh - y0) / h), false));
                    list.Add((new float4(0f, 0f, 1f, 1f), true));
                    for (int s = -1; s <= 1; s += 2)
                        Box(s * HeadX, 0.81f, 0.44f, 0.12f);
                    Box(0f, 0.60f, 2f * (HW - 0.58f), 0.05f);
                    Box(0f, 0.97f, 0.36f, 0.028f);
                    Box(0f, 0.36f, 0.46f, 0.15f);
                    break;
                }
                case WrapFace.Rear:
                {
                    float h = RearWrapTop - WrapBottom;
                    float U(float x) => (x + HW) / (2f * HW);
                    float V(float y) => (y - WrapBottom) / h;
                    foreach (float4 r in RearRects())
                        list.Add((new float4(U(r.x), V(r.y), U(r.z), V(r.w)), true));
                    void Box(float cx, float cy, float w, float hh) => list.Add((new float4(U(cx - 0.5f * w), V(cy - 0.5f * hh), U(cx + 0.5f * w), V(cy + 0.5f * hh)), false));
                    Box(Side * 0.2f, 1.0f, 1.2f, 0.46f);
                    Box(-Side * 0.62f, 0.63f, 0.46f, 0.15f);
                    for (int s = -1; s <= 1; s += 2)
                        Box(s * (HW - 0.07f), 1.0f, 0.09f, 0.62f);
                    break;
                }
                case WrapFace.Roof:
                {
                    List<float2> line = RoofLine(6);
                    float total = Length(line), len = math.max(0.1f, WrapLength);
                    float V(float x)
                    {
                        float at = 0f;
                        for (int i = 1; i < line.Count; i++)
                        {
                            float2 a = line[i - 1], b = line[i];
                            if (b.x > a.x + 1e-5f && x >= a.x && x <= b.x)
                                return (at + math.distance(a, b) * (x - a.x) / (b.x - a.x)) / total;
                            at += math.distance(a, b);
                        }
                        return x < 0f ? 0f : 1f;
                    }
                    void Box(float cx, float cz, float w, float l) => list.Add((new float4((WrapFrontZ - cz - 0.5f * l) / len, V(cx - 0.5f * w), (WrapFrontZ - cz + 0.5f * l) / len, V(cx + 0.5f * w)), false));
                    list.Add((new float4(0f, 0f, 1f, 1f), true));
                    foreach (float3 pod in RoofPodBoxes())
                        Box(0f, pod.y, pod.x, pod.z);
                    break;
                }
            }
            return list;
        }

        // The rear's solid pieces (x0, y0, x1, y1): below the window from the bumper up, and beside the window.
        List<float4> RearRects()
        {
            // a low roof over a high floor leaves no room for the rear window (its top under its bottom): the window is then as tall
            // as the room allows, and the strips beside it only when there is a hand's width of them
            float wy1 = RearWrapTop, wy0 = math.max(WrapBottom + 0.05f, math.min(RearWinBottom, wy1 - 0.05f)), wh = RearWinHalf, x = HW - 0.01f;
            var rects = new List<float4> { new float4(-x, WrapBottom, x, wy0) };
            if (wy1 - wy0 > 0.04f)
            {
                rects.Add(new float4(-x, wy0, -wh, wy1));
                rects.Add(new float4(wh, wy0, x, wy1));
            }
            return rects;
        }

        // The roof pods (as RoofPods places them) for the template: (width, centre z, length).
        List<float3> RoofPodBoxes()
        {
            float len = Spec.FrontZ - Spec.RearZ;
            float ac = math.min(3.4f, 0.3f * len), zc = HasCab ? math.lerp(Spec.RearAxleZ, Spec.FrontAxleZ, 0.62f) : Spec.RearZ + 0.6f * len;
            var pods = new List<float3> { new float3(math.min(2.0f, 2f * HW - 0.5f), zc, ac) };
            bool engine = !OpenRear;
            float ez = Spec.RearZ + 0.85f;
            if (engine)
                pods.Add(new float3(2f * HW - 0.4f, ez, 1.3f));
            foreach (float hz in new[] { HasCab ? Spec.FrontZ - 2.3f : Spec.FrontZ - 1.2f, Spec.RearZ + 2.6f })
                if (math.abs(hz - zc) > 0.5f * ac + 0.6f && (!engine || hz > ez + 1.2f))
                    pods.Add(new float3(0.9f, hz, 0.9f));
            return pods;
        }

        // The highest point of the wheel arch openings (with their rounded corners) at z; below the wrap where there is none.
        float ArchTopAt(float z)
        {
            float best = -1f;
            foreach (float3 a in Arches())
            {
                float dz = math.abs(z - a.x);
                if (dz >= kArchHalf)
                    continue;
                float top = a.z, r = math.min(0.32f, (top - F) * 0.6f), straight = kArchHalf - r;
                float y = dz <= straight ? top : top - r + math.sqrt(math.max(0f, r * r - (dz - straight) * (dz - straight)));
                best = math.max(best, y);
            }
            return best;
        }

        // The front's plan line (x, z), off metres outside the body's rounded front: from the right side at the side wrap's end
        // round the right corner, across the front, round the left corner back to the left side.
        List<float2> NoseLine(float off, int arc)
        {
            float fz = Spec.FrontZ, cx = HW - kNoseR, cz = fz - kNoseR, r = kNoseR + off, back = WrapFrontZ;
            var pts = new List<float2> { new float2(HW + off, back) };
            for (int i = 0; i <= arc; i++)
            {
                float a = 0.5f * math.PI * i / arc;
                pts.Add(new float2(cx + r * math.cos(a), cz + r * math.sin(a)));
            }
            for (int i = arc; i >= 0; i--)
            {
                float a = 0.5f * math.PI * i / arc;
                pts.Add(new float2(-(cx + r * math.cos(a)), cz + r * math.sin(a)));
            }
            pts.Add(new float2(-(HW + off), back));
            return pts;
        }

        // The roof's cross-section (x, y) from the left side's roof edge (the glazing band's top) up over the cant rail, across the
        // top and down to the right side's.
        List<float2> RoofLine(int n)
        {
            float edge = RoofOuterY(HW), flat = HW - kCantR;
            var pts = new List<float2> { new float2(-HW, BlackBandTop), new float2(-HW, edge) };
            for (int i = 1; i <= n; i++)
            {
                float x = -HW + kCantR * i / n;
                pts.Add(new float2(x, RoofOuterY(x)));
            }
            for (int i = 0; i < n; i++)
            {
                float x = flat + kCantR * i / n;
                pts.Add(new float2(x, RoofOuterY(x)));
            }
            pts.Add(new float2(HW, edge));
            pts.Add(new float2(HW, BlackBandTop));
            return pts;
        }

        static float Length(List<float2> line)
        {
            float l = 0f;
            for (int i = 1; i < line.Count; i++) l += math.distance(line[i - 1], line[i]);
            return math.max(1e-3f, l);
        }

        // The outward normals of a plan or section line (perpendicular to it, away from \`inside\`).
        static float2[] LineNormals(List<float2> line, float2 inside)
        {
            var n = new float2[line.Count];
            for (int i = 0; i < line.Count; i++)
            {
                float2 a = line[math.max(0, i - 1)], b = line[math.min(line.Count - 1, i + 1)], d = math.normalizesafe(b - a);
                float2 p = new float2(d.y, -d.x);
                if (math.dot(p, line[i] - inside) < 0f) p = -p;
                n[i] = p;
            }
            return n;
        }

        void DefineWrap(MeshKit k, WrapFace face, int lod)
        {
            if (!HasWrap(face))
                return;
            using (k.Paint(Sw.White))
            {
                switch (face)
                {
                    case WrapFace.Left:
                    case WrapFace.Right:
                    {
                        float side = face == WrapFace.Left ? -1f : 1f;
                        float len = math.max(0.1f, WrapLength), h = WrapTop - WrapBottom, x = side * (HW + kWrapOut);
                        float3 n = new float3(side, 0f, 0f);
                        float2 Uv(float z, float y) => new float2((WrapFrontZ - z) / len, (y - WrapBottom) / h);
                        foreach (float4 r in WrapRects(side))
                            Quad(k, new float3(x, r.y, r.x), new float3(x, r.y, r.z), new float3(x, r.w, r.z), new float3(x, r.w, r.x),
                                Uv(r.x, r.y), Uv(r.z, r.y), Uv(r.z, r.w), Uv(r.x, r.w), n);
                        break;
                    }
                    case WrapFace.Front:
                    {
                        List<float2> line = NoseLine(kWrapNose, lod == 0 ? 6 : 3);
                        float2[] normals = LineNormals(line, new float2(0f, Spec.FrontZ - 1f));
                        float total = Length(line), y0 = FrontWrapBottom, y1 = FrontWrapTop, at = 0f;
                        int[] bottom = new int[line.Count], top = new int[line.Count];
                        for (int i = 0; i < line.Count; i++)
                        {
                            if (i > 0) at += math.distance(line[i - 1], line[i]);
                            float3 nn = new float3(normals[i].x, 0f, normals[i].y);
                            bottom[i] = k.VUV(new float3(line[i].x, y0, line[i].y), nn, new float2(at / total, 0f));
                            top[i] = k.VUV(new float3(line[i].x, y1, line[i].y), nn, new float2(at / total, 1f));
                        }
                        for (int i = 0; i + 1 < line.Count; i++)
                        {
                            float2 m = math.normalizesafe(normals[i] + normals[i + 1]);
                            k.Quad(bottom[i], bottom[i + 1], top[i + 1], top[i], new float3(m.x, 0f, m.y));
                        }
                        break;
                    }
                    case WrapFace.Rear:
                    {
                        float z = Spec.RearZ - kWrapRearZ, h = RearWrapTop - WrapBottom;
                        float3 n = new float3(0f, 0f, -1f);
                        float2 Uv(float x, float y) => new float2((x + HW) / (2f * HW), (y - WrapBottom) / h);
                        foreach (float4 r in RearRects())
                            Quad(k, new float3(r.x, r.y, z), new float3(r.z, r.y, z), new float3(r.z, r.w, z), new float3(r.x, r.w, z),
                                Uv(r.x, r.y), Uv(r.z, r.y), Uv(r.z, r.w), Uv(r.x, r.w), n);
                        break;
                    }
                    case WrapFace.Roof:
                    {
                        List<float2> line = RoofLine(lod == 0 ? 6 : 3);
                        float2[] normals = LineNormals(line, new float2(0f, Spec.RoofY - 1.2f));
                        float total = Length(line), len = math.max(0.1f, WrapLength), at = 0f;
                        int[] front = new int[line.Count], rear = new int[line.Count];
                        for (int i = 0; i < line.Count; i++)
                        {
                            if (i > 0) at += math.distance(line[i - 1], line[i]);
                            float2 p = line[i] + normals[i] * kWrapOut;
                            float3 nn = new float3(normals[i].x, normals[i].y, 0f);
                            front[i] = k.VUV(new float3(p.x, p.y, WrapFrontZ), nn, new float2(0f, at / total));
                            rear[i] = k.VUV(new float3(p.x, p.y, WrapRearZ), nn, new float2(1f, at / total));
                        }
                        for (int i = 0; i + 1 < line.Count; i++)
                        {
                            float2 m = math.normalizesafe(normals[i] + normals[i + 1]);
                            k.Quad(front[i], front[i + 1], rear[i + 1], rear[i], new float3(m.x, m.y, 0f));
                        }
                        break;
                    }
                }
            }
        }

        static void Quad(MeshKit k, float3 a, float3 b, float3 c, float3 d, float2 ua, float2 ub, float2 uc, float2 ud, float3 n) =>
            k.Quad(k.VUV(a, n, ua), k.VUV(b, n, ub), k.VUV(c, n, uc), k.VUV(d, n, ud), n);
    }
}
