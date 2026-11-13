using System.Collections.Generic;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Exterior: what the bus looks like from outside, drawn instead of the game's body in the chase view (seen from the cab only the
// mirrors and wipers of the Outside part show). A modern Chinese low-floor city bus (after a Yutong in Nanjing): white body, a
// black flush-glazing band from the sills up to the roof edge, a black glass face across the front from the bumper up to the
// windscreen with the headlamp clusters in its lower corners, white corner pillars over the whole front, the destination sign
// behind black glass under a roof cap in the line's colour, the bumpers and the side skirts in the line's colour too, slim
// vertical LED lamps at the rear under a black upper rear, a smooth roof fairing over the air conditioning, LED destination
// signs on the front, the kerb side and the rear (the cab system draws their dots). The windows are clear: the game's shader
// build has no transparent glass (an opaque pane would hide the saloon), so the openings stay open inside black frit frames and
// the lit saloon is seen from outside.
// The shell is already painted outside (white walls, roof). Here:
//   Exterior   the front's black face and white pillars, the maker's bar, lamp housings and unlit lenses, plates, the black upper
//              rear with the engine grille below, the sign boxes, the roof fairings and hatches, the underbody
//   Glazing    the glazing's black frames: the band above the windows up to the roof edge, sill strips, pillar and door-post
//              covers, the windscreen's and the rear window's frit
//   Livery     in the line's colour (the cab system paints it): the bumpers, the side skirts, the roof cap over the front sign
//   Wheels     275/70 R22.5 steering wheels, a wide single on the drive axle (single on an articulated bus's middle axle)
//   Lamps      the lit lenses: head (with the daytime strip), tail, brake (with the high third light), indicators, reversing
// Lamp lenses are laid out once (LampLenses) and drawn twice: unlit in the Exterior part, lit 1.5 mm in front in the lamp parts.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        // Wheel centre (model space): the front axle or the rear one, side -1 left / +1 right, on the road.
        float3 WheelCentre(bool front, float side) =>
            new float3(side * (front ? FrontWheelX : RearWheelX), WheelRadius, front ? Spec.FrontAxleZ : Spec.RearAxleZ);

        void DefineExteriorParts(KitModelBuilder b, int lod)
        {
            if (b.Building(Exterior)) DefineExterior(b.Part(Exterior), lod);
            if (b.Building(Glazing)) DefineGlazing(b.Part(Glazing), lod);
            if (b.Building(Livery)) DefineLivery(b.Part(Livery), lod);
            for (int w = 0; w < 4; w++)
                if (b.Building(WheelFL + w) && (w >= 2 || HasCab)) DefineRoadWheel(b.Part(WheelFL + w), w, lod);
            for (int part = HeadLamps; part <= ReverseLamps; part++)
                if (b.Building(part)) LampLenses(b.Part(part), part, true, lod);
            for (int f = 0; f < WrapFaceCount; f++)
                if (b.Building(WrapLeft + f)) DefineWrap(b.Part(WrapLeft + f), (WrapFace)f, lod);
        }

        // ---------------------------------------------------------------- body

        const float kBumperTop = 0.50f;
        float BlackBandTop => Spec.RoofY - 0.26f;              // the side glazing band ends at the roof edge's rounding

        void DefineExterior(MeshKit k, int lod)
        {
            Underbody(k);
            if (HasCab) FrontMask(k, lod);
            if (!OpenRear) RearPanel(k, lod);
            SideSignBox(k);
            RoofPods(k, lod);
            for (int part = HeadLamps; part <= ReverseLamps; part++) LampLenses(k, part, false, lod);
        }

        // Under the floor: a dark slab over the floor's footprint, open under the wheel arches (the wheels turn in the wells).
        void Underbody(MeshKit k)
        {
            float z0 = Spec.RearZ + 0.02f, z1 = Spec.FrontZ - 0.02f, y = F - 0.14f, h = 0.04f;
            float xc = WallIn - math.max(kFrontReach, kRearReach);
            Faces f = Faces.All;
            using (k.Paint(Sw.Black))
            {
                k.Box(new float3(0f, y, 0.5f * (z0 + z1)), new float3(2f * xc, h, z1 - z0), f);
                for (int s = -1; s <= 1; s += 2)
                {
                    foreach (float2 seg in OutsideArches(z0, z1))
                        if (seg.y - seg.x > 0.005f)
                            k.Box(new float3(s * 0.5f * (xc + HW - 0.004f), y, 0.5f * (seg.x + seg.y)), new float3(HW - 0.004f - xc, h, seg.y - seg.x), f);
                    foreach (float3 a in Arches())
                    {
                        float xi = WallIn - a.y;
                        if (xi - xc > 0.005f)
                            k.Box(new float3(s * 0.5f * (xc + xi), y, a.x), new float3(xi - xc, h, 2f * kArchHalf), f);
                    }
                }
            }
        }

        // Stretches of the body side between the ends and the door openings (kerb side) as (rear, front) pairs.
        float2[] SideRuns(float side)
        {
            float z0 = Spec.RearZ, z1 = Spec.FrontZ - (HasCab ? kNoseR + 0.005f : 0f);
            if (side != -Side) return new[] { new float2(z0, z1) };
            var runs = new System.Collections.Generic.List<float2>();
            var cuts = DoorCuts(0f);
            float top = z1;
            for (int i = 0; i < cuts.Length; i++)
            {
                if (top - cuts[i].y > 0.05f) runs.Add(new float2(cuts[i].y, top));
                top = cuts[i].x;
            }
            if (top - z0 > 0.05f) runs.Add(new float2(z0, top));
            return runs.ToArray();
        }

        // The front (the lower front's colour is the Livery part): one black glass face from below the windscreen up to the
        // roof edge, a band under the windscreen and over the corners up to the roof (wrapping round them into the side glazing),
        // the destination sign behind it; under the roof's rounded front edge, no cap on it. The headlamp clusters' black
        // housings in the lower corners, the maker's bar, the plate, the black bumper lip at the foot.
        void FrontMask(MeshKit k, int lod)
        {
            float fz = Spec.FrontZ;
            int arc = lod == 0 ? 6 : 3;
            FrontNose(k, lod, arc);
            // the destination sign's dark window in the glass (the LED dots 3 mm in front of it: FrontSignFrame)
            FrontSignFrame(Spec, out float3 sc, out float2 ss);
            using (k.Paint(Sw.Screen)) k.Box(new float3(sc.x, sc.y, fz + kNoseSkin + 0.0015f), new float3(ss.x + 0.04f, ss.y + 0.03f, 0.001f), Faces.ZPos);
            using (k.Paint(Sw.Black))
            {
                for (int s = -1; s <= 1; s += 2)
                    k.BevelBox(new float3(s * HeadX, 0.81f, fz + 0.016f), new float3(0.44f, 0.12f, 0.006f), 0.04f);     // slim, rounded ends
                k.BevelBox(new float3(0f, 0.60f, fz + 0.0135f), new float3(2f * (HW - 0.58f), 0.05f, 0.006f), 0.02f);          // air intake
                k.ExtrudeY(NoseSkin(0.02f, 0.02f, fz - kNoseR - 0.12f, true, 1f, 0f, arc), F - 0.12f, F - 0.03f);   // bumper lip
            }
            if (lod == 0)
                using (k.Paint(Sw.Chrome)) k.Box(new float3(0f, 0.97f, fz + 0.016f), new float3(0.36f, 0.028f, 0.001f), Faces.ZPos);   // the maker's bar
            NumberPlate(k, new float3(0f, 0.36f, fz + 0.016f), 1f);
        }

        // Headlamp clusters in the lower corners: centre x (each side), just inside the corner's round.
        float HeadX => HW - kNoseR - 0.215f;

        const float kNoseSkin = 0.014f;            // the front's glass skin: in front of the body's rounded front
        const float kLipDrop = 0.036f;             // the black glass stops this far under the roof (the side band's top: BlackBandTop)

        // The front's skin round the plan-rounded nose, from the side band's end round the corner, across the front and back:
        // black glass from under the windscreen up to just under the roof (open over the windscreen), its top edge following the
        // roof's round, a white lip up to the roof, and the roof carried forward over the nose to it. Flat quads along the plan
        // outline, so the corners are round in plan and in section.
        void FrontNose(MeshKit k, int lod, int arc)
        {
            float fz = Spec.FrontZ, off = kNoseSkin, xs = HW - kNoseR, xw = WsHalf - 0.02f, xc = HW - kCantR;
            float gy0 = WsBotY - 0.08f, yb = WsBotY + 0.05f, yt = WsTopY, back = BodyFront - 0.02f, sideZ = fz - kNoseR - 0.05f;
            var pts = new System.Collections.Generic.List<float2> { new float2(HW + off, sideZ) };
            for (int i = 0; i <= arc; i++)
            {
                float a = 0.5f * math.PI * i / arc;
                pts.Add(new float2(xs + (kNoseR + off) * math.cos(a), fz - kNoseR + (kNoseR + off) * math.sin(a)));
            }
            var front = new System.Collections.Generic.List<float> { xw };
            int cn = lod == 0 ? 4 : 2;
            float t0 = math.acos(math.saturate((xw - xc) / kCantR));
            for (int i = 1; i < cn; i++) front.Add(xc + kCantR * math.cos(math.lerp(t0, 0.5f * math.PI, i / (float)cn)));
            front.Add(xc);
            foreach (float x in front) pts.Add(new float2(x, fz + off));
            for (int i = front.Count - 1; i >= 0; i--) pts.Add(new float2(-front[i], fz + off));
            for (int i = arc; i >= 0; i--)
            {
                float a = 0.5f * math.PI * i / arc;
                pts.Add(new float2(-(xs + (kNoseR + off) * math.cos(a)), fz - kNoseR + (kNoseR + off) * math.sin(a)));
            }
            pts.Add(new float2(-(HW + off), sideZ));

            float3 P(float2 p, float y) => new float3(p.x, y, p.y);
            // The 3 mm skin has real boundary faces at the window aperture and panel edges.
            void Skin(float3 p0, float3 p1, float3 p2, float3 p3, float3 n)
            {
                ClosedSkin(k, p0, p1, p2, p3, n, .003f);
            }
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float2 a = pts[i], b = pts[i + 1], d = b - a;
                if (math.lengthsq(d) < 1e-8f) continue;
                float2 n = math.normalize(new float2(d.y, -d.x));
                if (math.dot(n, 0.5f * (a + b) - new float2(0f, fz - 1.5f)) < 0f) n = -n;
                float3 nn = new float3(n.x, 0f, n.y);
                float ra = RoofOuterY(a.x), rb = RoofOuterY(b.x), ta = ra - kLipDrop, tb = rb - kLipDrop;
                bool screen = a.y > fz && b.y > fz && math.abs(a.x) <= xw + 1e-4f && math.abs(b.x) <= xw + 1e-4f;
                using (k.Paint(Sw.Black))
                {
                    if (screen)
                    {
                        Skin(P(a, gy0), P(b, gy0), P(b, yb), P(a, yb), nn);
                        Skin(P(a, yt), P(b, yt), P(b, tb), P(a, ta), nn);
                    }
                    else
                        Skin(P(a, gy0), P(b, gy0), P(b, tb), P(a, ta), nn);
                }
                using (k.Paint(Sw.White))
                {
                    Skin(P(a, ta), P(b, tb), P(b, rb), P(a, ra), nn);
                    if (math.abs(b.x - a.x) > 1e-4f)
                    {
                        float3 c0 = new float3(a.x, ra + 0.003f, back), c1 = new float3(b.x, rb + 0.003f, back), c2 = P(b, rb + 0.003f), c3 = P(a, ra + 0.003f);
                        float3 up = math.normalizesafe(math.cross(c1 - c0, c3 - c0));
                        if (up.y < 0f) up = -up;
                        ClosedSkin(k, c0, c1, c2, c3, up, .003f);
                    }
                }
            }
            // ledges closing the skin to the windscreen: under its top (above the glass) and over the dash (below it)
            using (k.Paint(Sw.Black))
            {
                ClosedSkin(k, new float3(-xw, yt, WsZ(yt) - 0.01f), new float3(xw, yt, WsZ(yt) - 0.01f), new float3(xw, yt, fz + off), new float3(-xw, yt, fz + off), new float3(0f, -1f, 0f), .003f);
                ClosedSkin(k, new float3(-xw, yb, fz - 0.10f), new float3(xw, yb, fz - 0.10f), new float3(xw, yb, fz + off), new float3(-xw, yb, fz + off), new float3(0f, 1f, 0f), .003f);
            }
            // the sides between the side band's top and the roof's edge, from the roof's end to the nose
            using (k.Paint(Sw.White))
                for (int s = -1; s <= 1; s += 2)
                {
                    float y0 = BlackBandTop - 0.005f, y1 = RoofOuterY(HW) + 0.004f, z0 = back, z1 = sideZ + 0.005f;
                    k.Box(new float3(s * (HW + 0.006f), 0.5f * (y0 + y1), 0.5f * (z0 + z1)), new float3(0.004f, y1 - y0, z1 - z0));
                }
        }

        // A Chinese bus plate: yellow (large vehicles), black rim.
        void NumberPlate(MeshKit k, float3 c, float facing)
        {
            using (k.Paint(Sw.Black)) k.Box(c, new float3(0.46f, 0.15f, 0.006f));
            using (k.Paint(Sw.Yellow)) k.Box(c + new float3(0f, 0f, facing * 0.0035f), new float3(0.44f, 0.13f, 0.001f), facing > 0f ? Faces.ZPos : Faces.ZNeg);
        }

        // The rear: white below with the engine grille and the plate, black above (round the rear window up to the roof) with the
        // route-number sign, slim vertical lamp housings at the corners, the bumper.
        float RearWinBottom => RearPlatY + 0.80f;
        float RearWinTop => WinTop - 0.04f;
        float RearWinHalf => HW - 0.32f;
        // The rear liner overlaps its window posts by 10 mm. Thin outer frames must share both curved edges.
        internal float[] RearShellBands => OpenRear ? System.Array.Empty<float>() : new[] { RearWinBottom - .01f, RearWinBottom, RearWinTop, RearWinTop + .01f };

        void RearPanel(MeshKit k, int lod)
        {
            float rz = Spec.RearZ, wy0 = RearWinBottom, wy1 = RearWinTop, wh = RearWinHalf, black = wy0 - 0.08f;
            using (k.Paint(Sw.White))
                k.Box(new float3(0f, 0.5f * (kBumperTop + black), rz - 0.004f), new float3(2f * HW - 0.02f, black - kBumperTop, 0.008f));
            using (k.Paint(Sw.Black))
            {
                k.Box(new float3(0f, 0.5f * (black + wy0), rz - 0.004f), new float3(2f * HW - 0.02f, wy0 - black, 0.008f));
                for (int s = -1; s <= 1; s += 2)
                    k.Box(new float3(s * 0.5f * (wh + HW - 0.01f), 0.5f * (wy0 + wy1), rz - 0.004f), new float3(HW - 0.01f - wh, wy1 - wy0, 0.008f));
                k.ExtrudeZ(EndSection(wy1, 0.01f, kLipDrop, lod == 0 ? 5 : 2), rz - 0.008f, rz);   // up to the roof's round, as at the front
            }
            // engine compartment grille (the engine tower is on the driver side), louvres
            float gx = Side * 0.2f;
            using (k.Paint(Sw.Black)) k.BevelBox(new float3(gx, 1.0f, rz - 0.012f), new float3(1.2f, 0.46f, 0.012f), 0.006f);
            if (lod == 0) using (k.Paint(Sw.DarkGrey)) k.Louvre(new float3(gx, 1.0f, rz - 0.019f), new float2(1.14f, 0.40f), 9, new float3(0f, 0f, -1f));
            NumberPlate(k, new float3(-Side * 0.62f, 0.63f, rz - 0.012f), -1f);
            for (int s = -1; s <= 1; s += 2)
                using (k.Paint(Sw.Black)) k.BevelBox(new float3(s * (HW - 0.07f), 1.0f, rz - 0.016f), new float3(0.09f, 0.62f, 0.024f), 0.01f);
            // route-number box (the cab system draws the LED dots on it)
            RearRouteFrame(Spec, out float3 rc, out float2 rs);
            using (k.Paint(Sw.DashBlack)) k.BevelBox(new float3(rc.x, rc.y, rz - 0.01f), new float3(rs.x + 0.06f, rs.y + 0.05f, 0.02f), 0.006f);
            using (k.Paint(Sw.Screen)) k.Box(new float3(rc.x, rc.y, rz - 0.0205f), new float3(rs.x + 0.02f, rs.y + 0.01f, 0.001f), Faces.ZNeg);
        }

        /// <summary>The rear route-number LED field (its dots face -Z), model space: centre of the dot plane and its size.</summary>
        public static void RearRouteFrame(in BusInteriorSpec s, out float3 centre, out float2 size)
        {
            VehicleProfile model = VehicleModels.Find(s.ModelVariant);
            if (model != null)
            {
                centre = model.RearSign.Centre;
                size = model.RearSign.Size;
                return;
            }
            float wy1 = s.RoofY - 0.64f - 0.04f, top = s.RoofY - 0.05f;
            centre = new float3(0f, 0.5f * (wy1 + top) - 0.03f, s.RearZ - 0.0215f);
            size = new float2(0.42f, 0.16f);
        }

        public static int2 RearRouteMatrix(in BusInteriorSpec s) =>
            VehicleModels.Find(s.ModelVariant)?.RearSign.Matrix ?? new int2(48, 16);

        /// <summary>The front destination sign's LED field (its dots face +Z), model space: centre of the dot plane and its size.</summary>
        public static void FrontSignFrame(in BusInteriorSpec s, out float3 centre, out float2 size)
        {
            VehicleProfile model = VehicleModels.Find(s.ModelVariant);
            if (model != null)
            {
                centre = model.FrontSign.Centre;
                size = model.FrontSign.Size;
                return;
            }
            float dy0 = s.RoofY - 0.42f + 0.02f;
            centre = new float3(0f, 0.5f * (dy0 + s.RoofY - 0.12f), s.FrontZ + 0.0185f);   // 3 mm in front of the nose's glass
            size = new float2(1.62f, 0.16f);
        }

        public static int2 FrontSignMatrix(in BusInteriorSpec s) =>
            VehicleModels.Find(s.ModelVariant)?.FrontSign.Matrix ?? new int2(160, 16);

        public static int2 SideSignMatrix(in BusInteriorSpec s) =>
            VehicleModels.Find(s.ModelVariant)?.SideSign.Matrix ?? new int2(112, 16);

        /// <summary>
        /// The side sign's frame for LedDots (whose dots face -Z): facing out on the kerb side, leaning in at the top by the model's
        /// tilt (an authored body that narrows towards the roof; 0 for the generic bus).
        /// </summary>
        public static quaternion SideSignRotation(in BusInteriorSpec s, float kerb)
        {
            VehicleProfile model = VehicleModels.Find(s.ModelVariant);
            float tilt = model?.SideSign.Tilt ?? 0f;
            if (model == null && new BusInterior(s, true).SideSign(out float3 c, out _, out _))
            {
                float3 n = BusBarrel.Normal(s, c, new float3(kerb, 0, 0));
                tilt = math.atan2(n.y, math.abs(n.x));
            }
            return SideSignRotation(kerb, tilt);
        }

        public static quaternion SideSignRotation(float kerb, float tilt) =>
            math.mul(quaternion.RotateZ(kerb * tilt), quaternion.RotateY(-kerb * 0.5f * math.PI));

        /// <summary>The frame for LedDots of a sign facing the front (+Z) or the rear (-Z), its top leaning back into the body by tilt.</summary>
        public static quaternion EndSignRotation(bool front, float tilt) =>
            front ? math.mul(quaternion.RotateX(-tilt), quaternion.RotateY(math.PI)) : quaternion.RotateX(tilt);

        /// <summary>
        /// The side destination sign (kerb side, at the top of the first window behind the front door; its dots face the kerb):
        /// centre of the dot plane, size, the kerb's x sign. False when the bus has no front door with a window behind it.
        /// </summary>
        public static bool SideSignFrame(in BusInteriorSpec s, out float3 centre, out float2 size, out float kerb)
        {
            var bus = new BusInterior(s);
            return bus.SideSign(out centre, out size, out kerb);
        }

        bool SideSign(out float3 centre, out float2 size, out float kerb)
        {
            kerb = -Side;
            if (IsAuthored)
            {
                centre = AuthoredModel.SideSign.Centre;
                size = AuthoredModel.SideSign.Size;
                return AuthoredModel.SideSign.Matrix.x > 0;
            }
            size = new float2(0.86f, 0.13f);
            centre = default;
            if (!HasCab || DoorCount < 1) return false;
            float2 door = Door(0);
            float z1 = door.x - 0.5f * door.y - kPillarW - 0.03f, z0 = z1 - 0.95f;
            foreach (float3 run in WallRuns(kerb))
                if (run.x <= z0 && run.y >= z1 + kPillarW)
                {
                    centre = new float3(kerb * (HW - 0.0125f), WinTop - 0.13f, 0.5f * (z0 + z1));
                    if (!RawSource) centre = BusBarrel.Point(Spec, centre);
                    return true;
                }
            return false;
        }

        // The side sign's housing hangs in the top of the window (seen through the clear opening, as on a real bus).
        void SideSignBox(MeshKit k)
        {
            if (!SideSign(out float3 c, out float2 size, out float kerb)) return;
            float3 box = new float3(c.x - kerb * 0.0175f, c.y, c.z);
            using (k.Paint(Sw.DashBlack)) k.BevelBox(box, new float3(0.03f, size.y + 0.05f, size.x + 0.06f), 0.006f);
            using (k.Paint(Sw.Screen)) k.Box(new float3(c.x - kerb * 0.001f, c.y, c.z), new float3(0.001f, size.y + 0.01f, size.x + 0.02f), kerb > 0f ? Faces.XPos : Faces.XNeg);
        }

        // Roof: a smooth fairing over the air conditioning (front half of the saloon) with its fan grilles, the engine's cooling
        // fairing at the rear, two hatches.
        void RoofPods(MeshKit k, int lod)
        {
            float ry = Spec.RoofY, len = Spec.FrontZ - Spec.RearZ;
            float ac = math.min(3.4f, 0.3f * len), zc = HasCab ? math.lerp(Spec.RearAxleZ, Spec.FrontAxleZ, 0.62f) : Spec.RearZ + 0.6f * len;
            using (k.Paint(Sw.White)) k.BevelBox(new float3(0f, ry + 0.1f, zc), new float3(math.min(2.0f, 2f * HW - 0.5f), 0.22f, ac), 0.08f);
            if (lod == 0)
                for (int i = -1; i <= 1; i += 2)
                    using (k.Paint(Sw.DarkGrey)) k.Disc(new float3(0.42f * i, ry + 0.211f, zc), new float3(0f, 1f, 0f), 0.22f, k.Sides(20));
            bool engine = !OpenRear;
            float ez = Spec.RearZ + 0.85f;
            if (engine)
            {
                using (k.Paint(Sw.White)) k.BevelBox(new float3(0f, ry + 0.07f, ez), new float3(2f * HW - 0.4f, 0.16f, 1.3f), 0.06f);
                if (lod == 0) using (k.Paint(Sw.DarkGrey)) k.Louvre(new float3(0f, ry + 0.151f, ez), new float2(1.4f, 0.8f), 7, new float3(0f, 1f, 0f));
            }
            foreach (float hz in new[] { HasCab ? Spec.FrontZ - 2.3f : Spec.FrontZ - 1.2f, Spec.RearZ + 2.6f })
                if (math.abs(hz - zc) > 0.5f * ac + 0.6f && (!engine || hz > ez + 1.2f))
                    using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(0f, ry + 0.02f, hz), new float3(0.9f, 0.05f, 0.9f), 0.015f);
        }

        // ---------------------------------------------------------------- livery

        // In the line's colour: the side skirts (the foot of both sides up to the bumper line, round the arches, not across the
        // doors), the front and rear bumpers with their corners, the roof cap over the front destination sign.
        void DefineLivery(MeshKit k, int lod)
        {
            float y0 = F - 0.12f, y1 = kBumperTop;
            float fz = Spec.FrontZ, rz = Spec.RearZ, by = 0.5f * (F - 0.11f + kBumperTop), bh = kBumperTop - F + 0.11f;
            using (k.Paint(Sw.Blue))
            {
                for (int s = -1; s <= 1; s += 2)
                    foreach (float2 run in SideRuns(s))
                        foreach (float2 seg in OutsideArches(run.x, run.y, 0.01f))
                            if (seg.y - seg.x > 0.05f)
                            {
                                float zStart = seg.x <= rz + 1e-4f ? seg.x + kEndRecess : seg.x;      // the white skin ends at RearZ: not in its plane (BusInterior.Walls.cs)
                                k.Box(new float3(s * (HW + 0.0045f), 0.5f * (y0 + y1), 0.5f * (zStart + seg.y)), new float3(0.003f, y1 - y0, seg.y - zStart));
                            }
                // the whole lower front round its corners, from the bumper lip up to the black glass
                if (HasCab)
                    k.ExtrudeY(NoseSkin(0.008f, 0.008f, fz - kNoseR - 0.01f, true, 1f, 0f, lod == 0 ? 6 : 3), F - 0.04f, WsBotY - 0.07f);
                if (!OpenRear)
                    k.BevelBox(new float3(0f, by, rz + 0.03f), new float3(2f * HW - 0.03f, bh, 0.10f), 0.035f);
            }
        }

        // ---------------------------------------------------------------- glass (the vehicle asset only)

        /// <summary>
        /// The glass of every window as quads (model space, corners in order round the pane), for the vehicle asset, whose game
        /// material has real transparent glass (the "_Gls" material); the mod's own model keeps the openings clear. Side windows
        /// between the pillars (the driver's sliding window included), the door leaves' panes (doors shut), the windscreen in its
        /// raked plane and the rear window.
        /// </summary>
        public System.Collections.Generic.List<float3[]> WindowPanes(System.Collections.Generic.List<int> partIndices = null)
        {
            var panes = new System.Collections.Generic.List<float3[]>();
            void Pane(float3 a, float3 b, float3 c, float3 d, int part = Glazing)
            {
                int count = RawSource ? 1 : math.max(1, (int)math.ceil(math.abs(d.y - a.y) / .12f));
                for (int i = 0; i < count; i++)
                {
                    float t0 = i / (float)count, t1 = (i + 1) / (float)count;
                    float3[] quad = { math.lerp(a, d, t0), math.lerp(b, c, t0), math.lerp(b, c, t1), math.lerp(a, d, t1) };
                    if (!RawSource) for (int j = 0; j < quad.Length; j++) quad[j] = BusBarrel.Point(Spec, quad[j]);
                    panes.Add(quad);
                    partIndices?.Add(part);
                }
            }
            for (int s = -1; s <= 1; s += 2)
            {
                float x = s * (HW - 0.03f);
                foreach (float3 run in WallRuns(s))
                {
                    int n = Panes(run.x, run.y, run.z < 0f ? 1 : (int)run.z);
                    for (int i = 0; i < n; i++)
                    {
                        float a = PillarZ(run.x, run.y, n, i) + 0.5f * kPillarW, b = PillarZ(run.x, run.y, n, i + 1) - 0.5f * kPillarW;
                        Pane(new float3(x, SillY, a), new float3(x, SillY, b), new float3(x, WinTop, b), new float3(x, WinTop, a));
                    }
                }
            }
            float kerb = -Side;
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                for (int leaf = 0; leaf < 2; leaf++)
                {
                    float hinge = leaf == 0 ? door.x + door.y * 0.5f : door.x - door.y * 0.5f, dir = leaf == 0 ? -1f : 1f;
                    float w = door.y * .5f - .010f, x = LeafX + kerb * .014f;
                    float z0 = hinge + dir * (DoorJambGap + .041f), z1 = hinge + dir * (w - .041f);
                    foreach (float2 span in new[] { new float2(F + .342f, F + .948f), new float2(F + .992f, LeafTop - .049f) })
                        Pane(new float3(x, span.x, z0), new float3(x, span.x, z1), new float3(x, span.y, z1), new float3(x, span.y, z0), DoorFrontA + 2 * d + leaf);
                }
            }
            if (HasCab)
            {
                float ya = WsBotY - 0.02f, yb = WsTopY + 0.02f, hx = WsHalf + 0.03f;
                Pane(new float3(-hx, ya, WsZ(ya)), new float3(hx, ya, WsZ(ya)), new float3(hx, yb, WsZ(yb)), new float3(-hx, yb, WsZ(yb)));
            }
            if (!OpenRear)
            {
                float z = Spec.RearZ + 0.03f, h = RearWinHalf;
                Pane(new float3(-h, RearWinBottom, z), new float3(h, RearWinBottom, z), new float3(h, RearWinTop, z), new float3(-h, RearWinTop, z));
            }
            return panes;
        }

        // ---------------------------------------------------------------- glazing frames

        // Flush glazing without the glass: the black band from the window tops up to the roof edge along the whole side, black
        // strips along the sills, covers over the window pillars and the door posts (the windows read as one black band with clear
        // panes), the windscreen's black frit border and the rear window's frame.
        void DefineGlazing(MeshKit k, int lod)
        {
            const float t = 0.006f;
            float top = BlackBandTop, zf = WallFrontZ, zr = WallRearZ;
            using (k.Paint(Sw.Black))
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    float x = s * (HW + 0.004f);
                    k.Box(new float3(x, 0.5f * (WinTop - 0.035f + top), 0.5f * (zr + zf)), new float3(t, top - WinTop + 0.035f, zf - zr));
                    foreach (float3 run in WallRuns(s))
                    {
                        float z0 = run.x, z1 = run.y;
                        int n = Panes(z0, z1, run.z < 0f ? 1 : (int)run.z);
                        k.Box(new float3(x, SillY, 0.5f * (z0 + z1)), new float3(t, 0.07f, z1 - z0));
                        for (int i = 0; i <= n; i++)
                            k.Box(new float3(x, 0.5f * (SillY + WinTop), PillarZ(z0, z1, n, i)), new float3(t, WinTop - SillY, kPillarW + 0.06f));
                    }
                    if (s != -Side) continue;
                    for (int d = 0; d < DoorCount; d++)
                    {
                        float2 door = Door(d);
                        for (int e = -1; e <= 1; e += 2)
                        {
                            // over the post (it stands 1 cm proud); not into the front's round
                            float c = door.x + e * (0.5f * door.y + 0.045f), z0 = c - 0.06f, z1 = math.min(c + 0.06f, HasCab ? Spec.FrontZ - kNoseR : float.MaxValue);
                            k.Box(new float3(s * (HW + 0.014f), 0.5f * (F + WinTop), 0.5f * (z0 + z1)), new float3(t, WinTop - F, z1 - z0));
                        }
                    }
                }
                if (HasCab)
                {
                    // the windscreen's frit, in its raked plane just in front of the gasket: wide at the bottom over the dash
                    float3 v = math.normalize(new float3(0f, WsTopY - WsBotY, WsTopZ - WsBotZ));
                    float3 n = math.normalize(math.cross(new float3(1f, 0f, 0f), v));
                    float hv = 0.5f * math.distance(new float2(WsBotY, WsBotZ), new float2(WsTopY, WsTopZ));
                    float3 o = new float3(0f, 0.5f * (WsBotY + WsTopY), 0.5f * (WsBotZ + WsTopZ)) + n * 0.02f;
                    float w = 2f * WsHalf + 0.06f;
                    k.BoxAlong(o - v * (hv - 0.05f) - new float3(0.5f * w, 0f, 0f), o - v * (hv - 0.05f) + new float3(0.5f * w, 0f, 0f), new float2(0.14f, t), n);
                    k.BoxAlong(o + v * (hv - 0.035f) - new float3(0.5f * w, 0f, 0f), o + v * (hv - 0.035f) + new float3(0.5f * w, 0f, 0f), new float2(0.09f, t), n);
                    for (int s = -1; s <= 1; s += 2)
                        k.BoxAlong(o - v * hv + new float3(s * (WsHalf - 0.01f), 0f, 0f), o + v * hv + new float3(s * (WsHalf - 0.01f), 0f, 0f), new float2(0.08f, t), n);
                }
                if (!OpenRear)
                {
                    float wy0 = RearWinBottom, wy1 = RearWinTop, wh = RearWinHalf, z = Spec.RearZ - 0.011f;
                    k.Box(new float3(0f, wy0 + 0.02f, z), new float3(2f * wh + 0.04f, 0.06f, t));
                    k.Box(new float3(0f, wy1 - 0.02f, z), new float3(2f * wh + 0.04f, 0.06f, t));
                    for (int s = -1; s <= 1; s += 2)
                        k.Box(new float3(s * (wh - 0.01f), 0.5f * (wy0 + wy1), z), new float3(0.06f, wy1 - wy0, t));
                }
            }
        }

        // ---------------------------------------------------------------- wheels

        // w: 0 front left, 1 front right, 2 rear left, 3 rear right.
        void DefineRoadWheel(MeshKit k, int w, int lod)
        {
            bool front = w < 2;
            float side = (w & 1) == 0 ? -1f : 1f;
            var spec = new WheelSpec
            {
                Radius = WheelRadius, Width = front || !DriveAxleWide ? kFrontTyre : kDriveTyre, RimRadius = 0.29f, Bolts = 10,
                Tyre = Sw.Rubber, Rim = Sw.Galvanised,
            };
            float3 c = WheelCentre(front, side);
            k.Wheel(c, spec, side);
            if (lod > 0) return;
            // brake drum and hub behind the rim (seen through the wheel's dish), a hub cap
            using (k.Paint(Sw.DarkGrey)) k.Cylinder(c - new float3(side * 0.2f, 0f, 0f), c - new float3(side * 0.04f, 0f, 0f), 0.22f, k.Sides(16));
            using (k.Paint(Sw.Steel)) k.Cylinder(c + new float3(side * 0.02f, 0f, 0f), c + new float3(side * 0.07f, 0f, 0f), 0.09f, k.Sides(12));
        }

        // ---------------------------------------------------------------- lamps

        // The lenses of one lamp group (a lamp part index): unlit (lit = false: plain lenses in the Exterior part) or lit (1.5 mm in
        // front, in the lamp swatch: the cab system draws them with emissive materials). Front: in the black band on each side a
        // daytime-running strip over three LED projectors, the indicator strip under them, a side repeater behind the front arch.
        // Rear: a slim vertical strip at each corner (indicator, brake, tail, reversing from the top), the high brake light.
        void LampLenses(MeshKit k, int group, bool lit, int lod)
        {
            float fz = Spec.FrontZ, rz = Spec.RearZ, o = lit ? 0.0015f : 0f;
            bool front = HasCab, rear = !OpenRear;
            float zf = fz + 0.0195f + o, hx = HeadX;
            for (int s = -1; s <= 1; s += 2)
            {
                bool ind = group == (s < 0 ? IndicatorL : IndicatorR);
                if (front && group == HeadLamps)
                    using (k.Paint(lit ? Sw.LampWhite : Sw.Chrome))
                    {
                        // the daytime strip along the top, three LED projectors in a row (the asset's exporter tells the strip from
                        // the low beam by height: above 0.85 m)
                        k.Box(new float3(s * hx, 0.858f, zf), new float3(0.40f, 0.014f, 0.001f), Faces.ZPos);
                        for (int j = -1; j <= 1; j++)
                            k.Box(new float3(s * (hx + 0.11f * j), 0.808f, zf), new float3(0.08f, 0.045f, 0.001f), Faces.ZPos);
                    }
                if (front && ind)
                    using (k.Paint(lit ? Sw.Amber : Sw.Orange))
                    {
                        k.Box(new float3(s * hx, 0.765f, zf), new float3(0.32f, 0.014f, 0.001f), Faces.ZPos);
                        k.Box(new float3(s * (HW + 0.004f + o), F + 0.26f, Spec.FrontAxleZ + kArchHalf + 0.14f), new float3(0.004f, 0.03f, 0.12f), s > 0 ? Faces.XPos : Faces.XNeg);
                    }
                if (!rear) continue;
                float x = s * (HW - 0.07f), z = rz - 0.0285f - o;
                Sw sw = Sw.Count;
                float y = 0f, h = 0.13f;
                if (ind) { sw = lit ? Sw.Amber : Sw.Orange; y = 1.235f; }
                else if (group == BrakeLamps) { sw = lit ? Sw.LampRed : Sw.Red; y = 1.09f; }
                else if (group == TailLamps) { sw = lit ? Sw.LampRed : Sw.Red; y = 0.945f; }
                else if (group == ReverseLamps) { sw = lit ? Sw.LampWhite : Sw.Chrome; y = 0.80f; h = 0.10f; }
                if (sw == Sw.Count) continue;
                using (k.Paint(sw)) k.Box(new float3(x, y, z), new float3(0.06f, h, 0.001f), Faces.ZNeg);
            }
            if (rear && group == BrakeLamps)                    // the high third brake light under the roof edge
                using (k.Paint(lit ? Sw.LampRed : Sw.Red)) k.Box(new float3(0f, Spec.RoofY - 0.12f, rz - 0.009f - o), new float3(0.40f, 0.03f, 0.001f), Faces.ZNeg);
        }

        // ---------------------------------------------------------------- pose

        // The exterior's pose (Apply): parts seen from outside only, wheels turned and rolled and kept on the road while the body
        // kneels, the lamps.
        static void ApplyExterior(IKitPoser t, in Pose p, in BusInteriorSpec spec)
        {
            // An authored model is one closed body, inside and out: its outer skin faces away from the cab (culled), and what of
            // it faces in - the destination board's frame, the frit's backing, the wheels through the door glass - is really
            // there from the driver's seat too. The generic model hides its exterior parts in the cab view (its glazing frames
            // would crowd the view out).
            bool authored = spec.ModelVariant != BusModelVariant.Generic;
            bool o = p.Outside || authored, cab = spec.Section != BusSection.Rear, rearEnd = spec.Section != BusSection.Front;
            t.Show(Exterior, o);
            t.Show(Glazing, o);
            t.Show(Livery, o);
            for (int f = 0; f < WrapFaceCount; f++)
                t.Show(WrapLeft + f, o && ((int)p.Wrap & (1 << f)) != 0);
            // the wrap's door pieces (children of their leaves) show with their side's picture; the window skin is drawn by the faces
            VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
            if (model != null && model.WrapDoors)
            {
                int first = model.FirstDoorDrive + model.DoorDriveCount + (model.WrapWindows ? 1 : 0);
                for (int part = first, d = 0; d < model.DoorLeaves.Length; d++)
                    for (int leaf = 0; leaf < model.DoorLeaves[d]; leaf++, part++)
                    {
                        WrapFace face = model.Mesh.SurfaceFace(part);
                        t.Show(part, o && ((int)p.Wrap & (1 << (int)face)) != 0 && model.HasDoorLeaf(d, leaf != 0));
                    }
            }
            ExteriorLampsAndWheels(t, p, spec, o, cab, rearEnd);
        }

        /// <summary>The door leaves' wrap pieces of a section (their part and the picture face they wear); none when the model's wrap does not cover the doors.</summary>
        public static List<(int part, WrapFace face)> WrapDoorPieces(BusInteriorSpec spec)
        {
            var list = new List<(int, WrapFace)>();
            VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
            if (model == null || !model.WrapDoors) return list;
            int first = model.FirstDoorDrive + model.DoorDriveCount + (model.WrapWindows ? 1 : 0);
            for (int part = first, d = 0; d < model.DoorLeaves.Length; d++)
                for (int leaf = 0; leaf < model.DoorLeaves[d]; leaf++, part++)
                    list.Add((part, model.Mesh.SurfaceFace(part)));
            return list;
        }

        static void ExteriorLampsAndWheels(IKitPoser t, in Pose p, in BusInteriorSpec spec, bool o, bool cab, bool rearEnd)
        {
            quaternion undo = math.inverse(math.normalize(KitPose.Safe(p.Kneel)));
            quaternion roll = quaternion.RotateX(p.WheelRoll), steer = quaternion.RotateY(math.radians(p.WheelSteerDeg));
            for (int w = 0; w < 4; w++)
            {
                int part = WheelFL + w;
                bool front = w < 2;
                t.Show(part, o && (!front || cab));
                float3 pivot = t.Def(part).Pivot;
                t.PoseLocal(part, math.mul(undo, pivot) - p.KneelOffset, math.mul(undo, front ? math.mul(steer, roll) : roll));
            }
            int lit = LitLampParts(p, spec);
            for (int part = HeadLamps; part <= ReverseLamps; part++)
                t.Show(part, (lit & (1 << (part - HeadLamps))) != 0);
        }

        /// <summary>
        /// The lamp parts shown lit for a pose, one bit each from HeadLamps (bit 0) to ReverseLamps (bit 5): the lamps that are on,
        /// on the end of the bus that has them (head lamps on a section with a cab, tail, brake and reverse lamps on one with a rear
        /// end), where the exterior parts are drawn at all. The native cab hides its unlit copy of each of these faces (BusNativeCab.LitLamps).
        /// </summary>
        public static int LitLampParts(in Pose p, in BusInteriorSpec spec)
        {
            bool o = p.Outside || spec.ModelVariant != BusModelVariant.Generic;
            if (!o) return 0;
            bool cab = spec.Section != BusSection.Rear, rearEnd = spec.Section != BusSection.Front;
            ExteriorLamps l = p.Lamps;
            int lit = 0;
            if (cab && (l & ExteriorLamps.Head) != 0) lit |= 1 << (HeadLamps - HeadLamps);
            if (rearEnd && (l & ExteriorLamps.Tail) != 0) lit |= 1 << (TailLamps - HeadLamps);
            if (rearEnd && (l & ExteriorLamps.Brake) != 0) lit |= 1 << (BrakeLamps - HeadLamps);
            if ((l & ExteriorLamps.Left) != 0) lit |= 1 << (IndicatorL - HeadLamps);
            if ((l & ExteriorLamps.Right) != 0) lit |= 1 << (IndicatorR - HeadLamps);
            if (rearEnd && (l & ExteriorLamps.Reverse) != 0) lit |= 1 << (ReverseLamps - HeadLamps);
            return lit;
        }
    }
}
