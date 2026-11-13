using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Shell, side walls and roof: lower wall panels with the heater duct and the seat rail, the sill, window pillars with rubber gaskets
// round each opening (no glass: the street is seen through the openings), transoms of the top-hinged vents, the driver's sliding
// window, and the roof as one closed profile (outer skin, cant rail, slanted ad coves, light-strip channels, duct soffits, centre
// ceiling) along the whole body.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        const float kPillarW = 0.11f;      // window pillar (with its trim)
        const float kPitch = 1.38f;        // target window pitch

        float WallFrontZ => HasCab ? Spec.FrontZ - 0.16f : Spec.FrontZ;
        float WallRearZ => Spec.RearZ;
        // The wall's rear end: the pieces' end faces lie at different depths, because two faces of different colour in one plane are drawn by the depth test in turn (the flicker at the
        // rear corner). The white skin ends at RearZ, the panel liner and the first pillar 5 mm inside (one step), the sill and the rails 10 mm (two): each behind the one before.
        const float kEndRecess = 0.005f;
        const float kPillarCapGap = 0.004f;      // the trimmed front pillar's end stays this far behind the A-pillar's front face
        float RecessedStart(float z0, int steps = 1) => z0 <= WallRearZ + 1e-4f ? z0 + steps * kEndRecess : z0;

        void SideWall(MeshKit k, float side, int lod)
        {
            foreach (float3 run in WallRuns(side))
                WallRun(k, side, run.x, run.y, run.z < 0f ? 1 : (int)run.z, run.z < 0f, lod);
        }

        // The stretches of solid side wall (x = rear z, y = front z, z = panes: 0 from the pitch, -1 the driver's sliding window):
        // on the driver's side the sliding window (B-pillar 1.15 m behind the A-pillar) and the saloon windows, on the kerb side
        // the walls between the doors. The glazing frames (Exterior) follow the same runs.
        System.Collections.Generic.List<float3> WallRuns(float side)
        {
            var runs = new System.Collections.Generic.List<float3>(4);
            float z1 = WallFrontZ, z0 = WallRearZ;
            if (side != -Side)
            {
                if (HasCab)
                {
                    float b = Spec.FrontZ - 1.32f;
                    runs.Add(new float3(b, z1, -1f));
                    z1 = b;
                }
                runs.Add(new float3(z0, z1, 0f));
                return runs;
            }
            var cuts = DoorCuts(0f);
            float top = z1;
            for (int i = 0; i < cuts.Length; i++)
            {
                if (top - cuts[i].y > 0.05f) runs.Add(new float3(cuts[i].y, top, 0f));
                top = cuts[i].x;
            }
            if (top - z0 > 0.05f) runs.Add(new float3(z0, top, 0f));
            return runs;
        }

        // Window pillars of a run: count of panes and the centre of pillar i (0 .. n).
        int Panes(float z0, float z1, int panes) => panes > 0 ? panes : math.max(1, (int)math.round((z1 - z0) / kPitch));
        float PillarZ(float z0, float z1, int n, int i) => math.lerp(z0 + 0.5f * kPillarW, z1 - 0.5f * kPillarW, i / (float)n);

        // One solid stretch of side wall from z0 to z1 (rear to front). panes = 0: from the pitch.
        void WallRun(MeshKit k, float side, float z0, float z1, int panes, bool driverWindow, int lod)
        {
            float zRear = RecessedStart(z0, 2), len = z1 - zRear, zc = 0.5f * (zRear + z1), xo = side * HW, xi = side * WallIn, xm = 0.5f * (xo + xi);
            float t = HW - WallIn;
            // lower wall up to the sill (open over the wheel arches), sill ledge
            LowerWall(k, side, z0, z1, lod);
            using (k.Paint(Sw.DarkGrey))
                k.BevelBox(new float3(side * (WallIn - 0.02f + 0.5f * t), SillY - 0.02f, zc), new float3(t + 0.02f, 0.045f, len), 0.012f);
            if (lod == 0)
            {
                // the fittings along the wall foot stop at the wheel arches
                foreach (float2 seg in OutsideArches(zRear, z1, 0.02f))
                {
                    float sl = seg.y - seg.x, sc = 0.5f * (seg.x + seg.y);
                    if (sl < 0.15f) continue;
                    // heater / convector duct along the wall foot: closed box, slotted top grille
                    using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(side * (WallIn - 0.05f), F + 0.09f, sc), new float3(0.10f, 0.18f, sl - 0.02f), 0.01f);
                    using (k.Paint(Sw.Black, 0.5f))
                        k.Box(new float3(side * (WallIn - 0.05f), F + 0.181f, sc), new float3(0.06f, 0.004f, sl - 0.12f), Faces.NoBottom);
                    using (k.Paint(Sw.DarkGrey))
                        for (float z = seg.x + 0.06f; z < seg.y - 0.06f; z += 0.16f)
                            k.Box(new float3(side * (WallIn - 0.05f), F + 0.184f, z), new float3(0.064f, 0.004f, 0.012f), Faces.YPos);
                    // seat rail (aluminium C-rail for the cantilever seats) and panel joints
                    using (k.Paint(Sw.Galvanised)) k.Box(new float3(side * (WallIn - 0.012f), F + 0.36f, sc), new float3(0.024f, 0.045f, sl));
                    using (k.Paint(Sw.Galvanised)) k.Box(new float3(side * (WallIn - 0.008f), SillY - 0.09f, sc), new float3(0.016f, 0.03f, sl));
                }
            }
            // window pillars and openings
            int n = Panes(z0, z1, panes);
            float p0 = z0 + 0.5f * kPillarW, p1 = z1 - 0.5f * kPillarW, wy0 = SillY, wy1 = WinTop;
            // The front-most pillar of a run that reaches the cab's front ends behind the raked A-pillar's front face at every height, its chamfered outer edge included: that face recedes
            // upwards, and a pillar ending at WallFrontZ stood out of it above the beltline, so its light end face showed through the corner of the windscreen (a light strip widening upwards,
            // in the game). Inside the A-pillar's section the cut-off part was hidden anyway.
            float frontCap = HasCab && z1 >= WallFrontZ - 1e-4f ? APillarOuterFrontZ(wy1 + 0.02f) - kPillarCapGap : float.MaxValue;
            for (int i = 0; i <= n; i++)
            {
                float pz = PillarZ(z0, z1, n, i);
                bool rearPillar = i == 0, trimmed = i == n && frontCap < pz + 0.5f * kPillarW;
                using (k.Paint(Sw.Panel))
                    if (rearPillar || trimmed)
                    {
                        float pillarBack = rearPillar ? RecessedStart(z0) : pz - 0.5f * kPillarW, pillarFront = trimmed ? frontCap : pz + 0.5f * kPillarW;
                        k.Box(new float3(xm, 0.5f * (wy0 + wy1), 0.5f * (pillarBack + pillarFront)), new float3(t, wy1 - wy0 + 0.04f, pillarFront - pillarBack));
                    }
                    else k.Box(new float3(xm, 0.5f * (wy0 + wy1), pz), new float3(t, wy1 - wy0 + 0.04f, kPillarW));
                if (lod == 0 && i > 0 && i < n)
                {
                    using (k.Paint(Sw.DarkGrey))                // pillar trim strip on the inner face
                        k.BevelBox(new float3(side * (WallIn - 0.006f), 0.5f * (wy0 + wy1), pz), new float3(0.014f, wy1 - wy0 - 0.06f, 0.07f), 0.005f);
                    if ((i & 1) == 1 && !driverWindow) Hammer(k, new float3(side * (WallIn - 0.013f), wy1 - 0.2f, pz), side);
                }
            }
            for (int i = 0; i < n; i++)
            {
                float a = math.lerp(p0, p1, i / (float)n) + 0.5f * kPillarW, b = math.lerp(p0, p1, (i + 1) / (float)n) - 0.5f * kPillarW;
                float wc = 0.5f * (a + b);
                float3 o = new float3(side * (HW - 0.03f), 0.5f * (wy0 + wy1), wc);
                using (k.Paint(Sw.Rubber))
                    Frame(k, o, new float3(0f, 0f, 1f), new float3(0f, 1f, 0f), new float2(0.5f * (b - a) - 0.012f, 0.5f * (wy1 - wy0) - 0.012f), 0.07f, 0.05f, 0.035f, 3);
                if (lod > 0) continue;
                using (k.Paint(Sw.Black))
                {
                    if (driverWindow)
                    {
                        // sliding window: a fixed rear half, the sliding sash in front (a vertical meeting rail and the sash frame)
                        k.Box(new float3(side * (HW - 0.045f), 0.5f * (wy0 + wy1), wc), new float3(0.03f, wy1 - wy0, 0.05f));
                        k.Box(new float3(side * (HW - 0.06f), wy0 + 0.04f, 0.5f * (wc + b)), new float3(0.03f, 0.03f, b - wc));
                        k.Box(new float3(side * (HW - 0.06f), wy1 - 0.04f, 0.5f * (wc + b)), new float3(0.03f, 0.03f, b - wc));
                        using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(side * (HW - 0.09f), 0.5f * (wy0 + wy1), wc + 0.06f), new float3(0.03f, 0.12f, 0.025f), 0.008f);
                    }
                    else if (((i + (side > 0f ? 1 : 0)) & 1) == 0 && b - a > 0.7f)
                    {
                        // top-hinged vent: transom, the vent's frame
                        float ty = wy1 - 0.30f;
                        k.Box(new float3(side * (HW - 0.04f), ty, wc), new float3(0.035f, 0.035f, b - a));
                        Frame(k, new float3(side * (HW - 0.05f), 0.5f * (ty + wy1), wc), new float3(0f, 0f, 1f), new float3(0f, 1f, 0f),
                            new float2(0.5f * (b - a) - 0.06f, 0.5f * (wy1 - ty) - 0.035f), 0.03f, 0.025f, 0.02f, 2);
                    }
                }
            }
        }

        // Lower side wall from z0 to z1, up to the sill: two skins, the panel inside and the body paint outside (2 mm), down to the
        // skirt; over a wheel arch opening only strips from the opening's outline up (the housing's band closes them below).
        void LowerWall(MeshKit k, float side, float z0, float z1, int lod)
        {
            float xo = side * HW, xi = side * WallIn, xp = side * (HW - 0.002f), y0 = F - 0.12f;
            foreach (float2 seg in OutsideArches(z0, z1))
            {
                float len = seg.y - seg.x, zc = 0.5f * (seg.x + seg.y);
                if (len < 0.002f) continue;
                float pa = math.max(seg.x, RecessedStart(z0));
                if (seg.y - pa > 0.002f)
                    using (k.Paint(Sw.Panel)) k.Box(new float3(0.5f * (xi + xp), 0.5f * (y0 + SillY), 0.5f * (pa + seg.y)), new float3(math.abs(xp - xi), SillY - y0, seg.y - pa));
                using (k.Paint(Sw.White)) k.Box(new float3(0.5f * (xp + xo), 0.5f * (y0 + SillY), zc), new float3(0.002f, SillY - y0, len), Faces.All & ~Faces.XNeg & ~Faces.XPos | (side > 0f ? Faces.XPos : Faces.XNeg));
            }
            foreach (float3 a in Arches())
            {
                float2[] pts = ArchOutline(a.x, a.z, lod);
                for (int i = 0; i + 1 < pts.Length; i++)
                {
                    float2 p = pts[i], q = pts[i + 1];                   // walking over the top towards -z: p.x >= q.x
                    float za = math.max(q.x, z0), zb = math.min(p.x, z1);
                    if (zb - za < 1e-4f) continue;
                    float ya = math.lerp(q.y, p.y, (za - q.x) / math.max(1e-5f, p.x - q.x));
                    float yb = math.lerp(q.y, p.y, (zb - q.x) / math.max(1e-5f, p.x - q.x));
                    using (k.Paint(Sw.White)) k.QuadP(new float3(xo, ya, za), new float3(xo, yb, zb), new float3(xo, SillY, zb), new float3(xo, SillY, za), new float3(side, 0f, 0f));
                    using (k.Paint(Sw.Panel)) k.QuadP(new float3(xi, ya, za), new float3(xi, yb, zb), new float3(xi, SillY, zb), new float3(xi, SillY, za), new float3(-side, 0f, 0f));
                }
            }
        }

        // Emergency hammer in its red holder on a pillar (c = on the pillar face, side = wall x sign).
        void Hammer(MeshKit k, float3 c, float side)
        {
            float3 o = c - new float3(side * 0.02f, 0f, 0f);
            using (k.Paint(Sw.Red))
            {
                k.BevelBox(o, new float3(0.03f, 0.16f, 0.06f), 0.008f);
                k.BevelBox(o - new float3(side * 0.03f, 0.01f, 0f), new float3(0.025f, 0.13f, 0.022f), 0.006f);   // handle
            }
            using (k.Paint(Sw.Steel)) k.BevelBox(o + new float3(-side * 0.03f, 0.07f, 0f), new float3(0.03f, 0.025f, 0.07f), 0.006f);   // head
        }

        // ---------------------------------------------------------------- roof

        // Closed roof profile (x, y) across the whole width: outer skin with rounded cant rails on top, inner side from the window top
        // up the slanted ad cove, the light channel, the duct soffit and the centre ceiling.
        Profile RoofProfile(int lod)
        {
            float wt = WinTop, c = CeilY, xi = WallIn, ro = Spec.RoofY;
            int n = lod == 0 ? 5 : 2;
            var p = new System.Collections.Generic.List<float2>(48);
            var sm = new System.Collections.Generic.List<bool>(48);
            void Add(float x, float y, bool s) { p.Add(new float2(x, y)); sm.Add(s); }
            // right half inner (from the wall up to the centre), then mirrored; outer skin back over the top
            Add(HW, wt, false);
            Add(xi, wt, false);
            Add(xi, wt + 0.07f, false);                         // cant rail trim
            Add(xi - 0.24f, c - 0.13f, false);                  // slanted ad cove
            Add(xi - 0.26f, c - 0.10f, false);
            Add(xi - 0.40f, c - 0.10f, false);                  // light channel (strip sits here)
            Add(xi - 0.40f, c - 0.075f, false);
            Add(xi - 0.70f, c - 0.075f, false);                 // duct soffit (vents)
            Add(xi - 0.72f, c, false);
            Add(-xi + 0.72f, c, false);                         // centre ceiling
            Add(-xi + 0.70f, c - 0.075f, false);
            Add(-xi + 0.40f, c - 0.075f, false);
            Add(-xi + 0.40f, c - 0.10f, false);
            Add(-xi + 0.26f, c - 0.10f, false);
            Add(-xi + 0.24f, c - 0.13f, false);
            Add(-xi, wt + 0.07f, false);
            Add(-xi, wt, false);
            Add(-HW, wt, false);
            float r = 0.28f;
            for (int i = 0; i <= n; i++) { float a = math.PI - 0.5f * math.PI * i / n; Add(-HW + r + r * math.cos(a), ro - r + r * math.sin(a) * 0.8f + 0.2f * r, i > 0 && i < n); }
            for (int i = 0; i <= n; i++) { float a = 0.5f * math.PI - 0.5f * math.PI * i / n; Add(HW - r + r * math.cos(a), ro - r + r * math.sin(a) * 0.8f + 0.2f * r, i > 0 && i < n); }
            p.Reverse(); sm.Reverse();
            return new Profile(p.ToArray(), sm.ToArray());
        }

        void Roof(MeshKit k, int lod)
        {
            float z0 = OpenRear ? Spec.RearZ : Spec.RearZ + 0.02f, z1 = BodyFront;
            using (k.Paint(Sw.White)) using (k.Shade(ShadeRule.Flat)) k.ExtrudeZ(RoofProfile(lod), z0, z1, Caps.Both);
        }
    }
}
