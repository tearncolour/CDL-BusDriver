using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Shell: the closed body around the camera. Floor (vinyl, yellow nosings at doors and steps), podiums over and behind the rear axle,
// the rear platform, wheel arches, side walls (lower panels, heater ducts, sills, window pillars, gaskets), the front (windscreen
// opening, A-pillars, destination box), the rear wall (window opening, engine tower) and the roof / ceiling profile with the ad coves.
// Every piece is a closed solid seen from outside it: no back face shows from the saloon. The wheel arches are hollow housings open
// to the road (the wheels turn inside them): from the saloon they look like the solid humps they were, from outside the arch
// openings in the side walls show the wheels in black wells.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        void DefineShell(MeshKit k, int lod)
        {
            Floor(k, lod);
            Arches(k, lod);
            SideWall(k, Side, lod);
            SideWall(k, -Side, lod);
            DoorPortals(k, lod);
            if (HasCab) FrontEnd(k, lod);
            else if (OpenFront) Bellows(k, Spec.FrontZ, 1f, lod);
            if (OpenRear) Bellows(k, Spec.RearZ, -1f, lod);
            else RearEnd(k, lod);
            Roof(k, lod);
        }

        // ---------------------------------------------------------------- floor

        void Floor(MeshKit k, int lod)
        {
            float z0 = OpenRear ? Spec.RearZ : Spec.RearZ + 0.02f, z1 = OpenFront ? Spec.FrontZ : Spec.FrontZ - 0.02f;
            // the middle between the arch housings over the whole length; at the sides the floor stops at the housings' inner plates
            float xc = WallIn - math.max(kFrontReach, kRearReach);
            using (k.Paint(Sw.FloorVinyl))
            {
                k.Box(new float3(0f, F - 0.06f, 0.5f * (z0 + z1)), new float3(2f * xc, 0.12f, z1 - z0), Faces.NoBottom);
                for (int s = -1; s <= 1; s += 2)
                {
                    foreach (float2 seg in OutsideArches(z0, z1))
                        FloorStrip(k, s, xc, HW - 0.01f, seg.x, seg.y);
                    foreach (float3 a in Arches())
                        FloorStrip(k, s, xc, WallIn - a.y, math.max(z0, a.x - kArchHalf), math.min(z1, a.x + kArchHalf));
                }
            }
            if (lod > 0) return;
            // aisle runner (a lighter vinyl strip 4 mm proud) and the seams of the floor sheets
            float a0 = HasRearPlatform ? RearPlatZ : z0 + 0.3f, a1 = HasCab ? CabRearZ - 0.1f : z1 - 0.3f;
            using (k.Paint(Sw.FloorVinyl, 0.8f))
                k.Box(new float3(0f, F + 0.002f, 0.5f * (a0 + a1)), new float3(0.5f, 0.004f, a1 - a0), Faces.NoBottom);
            for (int s = -1; s <= 1; s += 2)
                k.PanelLine(new float3(s * 0.25f, F + 0.004f, a0), new float3(s * 0.25f, F + 0.004f, a1), new float3(0f, 1f, 0f), 0.008f);
            // door thresholds: aluminium sill and a yellow anti-slip edge
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                float x = -Side * (HW - 0.05f);
                using (k.Paint(Sw.Galvanised)) k.Box(new float3(x, F + 0.006f, door.x), new float3(0.1f, 0.012f, door.y + 0.1f), Faces.NoBottom);
                // The lower guide has a real slot through the anti-slip strip, above the continuous floor.
                float gx = math.abs(BusBarrel.SourcePoint(Spec, new float3(GenericDoorRig.For(Spec).Links[d][0].Guide.x, F + .004f, door.x)).x);
                float edge = math.abs(x) - .09f;
                using (k.Paint(Sw.Yellow))
                    foreach (float2 span in new[] {new float2(edge-.04f,math.min(edge+.04f,gx-.012f)),new float2(math.max(edge-.04f,gx+.012f),edge+.04f)})
                        if (span.y>span.x) k.Box(new float3(-Side*(span.x+span.y)*.5f,F+.004f,door.x),new float3(span.y-span.x,.008f,door.y));
                // ribbed ramp plate of the wheelchair door (middle door)
                if (d == RampDoor)
                    using (k.Paint(Sw.Steel))
                        for (int i = 0; i < 8; i++)
                            k.Box(new float3(x + Side * (0.22f + 0.07f * i), F + 0.004f, door.x), new float3(0.03f, 0.008f, 0.9f), Faces.NoBottom);
            }
        }

        // One side strip of the floor slab: |x| from xa to xb on side s, z from z0 to z1.
        void FloorStrip(MeshKit k, float s, float xa, float xb, float z0, float z1)
        {
            if (xb - xa < 0.005f || z1 - z0 < 0.005f) return;
            k.Box(new float3(s * 0.5f * (xa + xb), F - 0.06f, 0.5f * (z0 + z1)), new float3(xb - xa, 0.12f, z1 - z0), Faces.NoBottom);
        }

        // Step-up podium against a side wall: x from the wall inwards by reach (from an arch housing's inner plate where it passes
        // one), top at y; yellow nosings on the open edges.
        void Podium(MeshKit k, float side, float z0, float z1, float y, float reach, bool noseFront, bool noseRear, int lod)
        {
            if (z1 - z0 < 0.2f) return;
            float zc = 0.5f * (z0 + z1), inner = WallIn - reach;
            using (k.Paint(Sw.FloorVinyl))
            {
                foreach (float2 seg in OutsideArches(z0, z1))
                    PodiumBlock(k, side, inner, WallIn + 0.06f, seg.x, seg.y, y);
                foreach (float3 a in Arches())
                    PodiumBlock(k, side, inner, WallIn - a.y, math.max(z0, a.x - kArchHalf), math.min(z1, a.x + kArchHalf), y);
            }
            if (lod > 0) return;
            float xc = side * (WallIn - 0.5f * reach + 0.03f), xa = side * (inner + 0.03f);
            using (k.Paint(Sw.Yellow))
            {
                k.Box(new float3(xa, y + 0.002f, zc), new float3(0.06f, 0.006f, z1 - z0 - 0.02f), Faces.NoBottom);
                if (noseFront) k.Box(new float3(xc, y + 0.002f, z1 - 0.03f), new float3(reach + 0.06f, 0.006f, 0.06f), Faces.NoBottom);
                if (noseRear) k.Box(new float3(xc, y + 0.002f, z0 + 0.03f), new float3(reach + 0.06f, 0.006f, 0.06f), Faces.NoBottom);
            }
            using (k.Paint(Sw.Galvanised))                                                        // kick plate on the riser
                k.Box(new float3(side * (inner - 0.003f), F + 0.06f, zc), new float3(0.004f, 0.1f, z1 - z0 - 0.04f));
        }

        void PodiumBlock(MeshKit k, float side, float xa, float xb, float z0, float z1, float y)
        {
            if (xb - xa < 0.005f || z1 - z0 < 0.005f) return;
            k.Box(new float3(side * 0.5f * (xa + xb), 0.5f * (F + y), 0.5f * (z0 + z1)), new float3(xb - xa, y - F, z1 - z0), Faces.NoBottom);
        }

        void Arches(MeshKit k, int lod)
        {
            // front wheel arches (rounded housings; the kerb-side one under a seat podium)
            if (HasFrontArch)
                for (int s = -1; s <= 1; s += 2)
                {
                    float z = Spec.FrontAxleZ;
                    ArchHousing(k, s, z, kFrontReach, F + kFrontArchTop, lod);
                    if (s == -Side) Podium(k, s, z - kArchHalf - 0.12f, z + kArchHalf + 0.05f, F + 0.30f, 0.90f, true, true, lod);
                    else Podium(k, s, z - kArchHalf - 0.12f, CabRearZ, F + 0.30f, 0.90f, false, true, lod);
                }
            // rear / drive axle arches and the podiums from the arch to the rear platform (seats on them)
            float zr = Spec.RearAxleZ;
            float pEnd = HasRearPlatform ? RearPlatZ : Spec.RearZ + 0.7f;
            for (int s = -1; s <= 1; s += 2)
            {
                ArchHousing(k, s, zr, kRearReach, F + kRearArchTop, lod);
                float p1 = zr + kArchHalf + 0.10f;
                if (s == Side) { Podium(k, s, pEnd, p1, F + 0.30f, 0.90f, true, false, lod); continue; }
                // kerb side: split around the door openings
                float top = p1;
                var cuts = DoorCuts(0.08f);
                for (int i = 0; i < cuts.Length; i++)
                {
                    if (cuts[i].x >= top || cuts[i].y <= pEnd) continue;
                    Podium(k, s, math.max(cuts[i].y, pEnd), top, F + 0.30f, 0.90f, true, true, lod);
                    top = cuts[i].x;
                }
                if (top > pEnd) Podium(k, s, pEnd, top, F + 0.30f, 0.90f, true, false, lod);
            }
            if (!HasRearPlatform) return;
            // rear platform (the bench over the engine) with an intermediate step
            float zp = RearPlatZ, r0 = RearIn;
            using (k.Paint(Sw.FloorVinyl))
            {
                k.Box(new float3(0f, 0.5f * (F + RearPlatY), 0.5f * (zp + r0 - 0.04f)), new float3(2f * WallIn + 0.1f, RearPlatY - F, zp - r0 + 0.04f), Faces.NoBottom);
                k.Box(new float3(0f, F + 0.105f, zp + 0.13f), new float3(0.62f, 0.21f, 0.26f), Faces.NoBottom);
            }
            if (lod > 0) return;
            using (k.Paint(Sw.Yellow))
            {
                k.Box(new float3(0f, RearPlatY + 0.002f, zp - 0.03f), new float3(2f * WallIn, 0.006f, 0.06f), Faces.NoBottom);
                k.Box(new float3(0f, F + 0.212f, zp + 0.23f), new float3(0.62f, 0.006f, 0.06f), Faces.NoBottom);
            }
        }

        // Wheel arch housing, hollow: an inner plate with the arch's rounded outline (z, y) at the reach (panel towards the aisle,
        // black towards the wheel) and a band along the outline from the plate out to the body side: panel on the saloon side (the
        // hump), black on the wheel side (the well seen through the arch opening). The band lies just inside the outline, so the
        // plate closes its edge and the side wall's strips over the opening (LowerWall) start where it ends.
        void ArchHousing(MeshKit k, float side, float z, float reach, float top, int lod)
        {
            float2[] pts = ArchOutline(z, top, lod);
            float xi = side * (WallIn - reach), xb = xi + side * .024f, xo = side * HW;
            var inner = new float2[pts.Length]; var normals = new float2[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                float2 before = math.normalizesafe(pts[i] - pts[math.max(0, i - 1)]);
                float2 after = math.normalizesafe(pts[math.min(pts.Length - 1, i + 1)] - pts[i]);
                if (i == 0) before = after;
                if (i == pts.Length - 1) after = before;
                float2 n0 = new float2(before.y, -before.x), n1 = new float2(after.y, -after.x);
                normals[i] = math.normalizesafe(n0 + n1);
                inner[i] = pts[i] - normals[i] * (kArchShell / math.dot(normals[i], n0));
            }
            float3 P(float x, float2 p) => new float3(x, p.y, p.x);
            float2 centre = float2.zero, innerCentre = float2.zero;
            foreach (float2 p in pts) centre += p / pts.Length;
            foreach (float2 p in inner) innerCentre += p / inner.Length;
            for (int i = 0; i < pts.Length; i++)
            {
                int j = (i + 1) % pts.Length;
                using (k.Paint(Sw.Panel)) Face(P(xi, centre), P(xi, pts[i]), P(xi, pts[j]), new float3(-side, 0, 0));
                using (k.Paint(Sw.Black)) Face(P(xb, innerCentre), P(xb, inner[i]), P(xb, inner[j]), new float3(side, 0, 0));
            }
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                float2 a = pts[i], b = pts[i + 1];
                float2 d = math.normalizesafe(b - a);
                if (math.lengthsq(d) < 0.5f) continue;
                float2 n = new float2(d.y, -d.x);                       // (z, y): out of the opening, into the body
                float2 ai = inner[i], bi = inner[i + 1];
                float3 nb = new float3(0f, n.y, n.x);
                float3 na = new float3(0, normals[i].y, normals[i].x), nn = new float3(0, normals[i + 1].y, normals[i + 1].x);
                using (k.Paint(Sw.Panel)) Smooth(P(xi, a), P(xo, a), P(xo, b), P(xi, b), na, nn, nb);
                using (k.Paint(Sw.Black))
                {
                    Smooth(P(xb, ai), P(xo, ai), P(xo, bi), P(xb, bi), -na, -nn, -nb);
                    k.QuadP(P(xo, a), P(xo, b), P(xo, bi), P(xo, ai), new float3(side, 0f, 0f));      // the band's edge at the body side
                }
            }
            using (k.Paint(Sw.Black))
            {
                int last = pts.Length - 1;
                foreach (int i in new[] { 0, last })
                    k.QuadP(P(xi, pts[i]), P(xo, pts[i]), P(xo, inner[i]), P(xb, inner[i]), new float3(0, -1, 0));
                k.QuadP(P(xi, pts[0]), P(xb, inner[0]), P(xb, inner[last]), P(xi, pts[last]), new float3(0, -1, 0));
            }
            void Face(float3 a, float3 b, float3 c, float3 normal) => k.Tri(k.V(a, normal), k.V(b, normal), k.V(c, normal), normal);
            void Smooth(float3 a, float3 b, float3 c, float3 d, float3 n0, float3 n1, float3 wanted)
            {
                int va = k.V(a, n0), vb = k.V(b, n0), vc = k.V(c, n1), vd = k.V(d, n1);
                k.Tri(va, vb, vc, wanted); k.Tri(va, vc, vd, wanted);
            }
            if (lod > 0) return;
            using (k.Paint(Sw.Galvanised))                 // stainless kick band round the foot of the housing
                k.Box(new float3(xi - side * 0.002f, F + 0.08f, z), new float3(0.006f, 0.16f, 2f * kArchHalf - 0.02f));
        }
    }
}
