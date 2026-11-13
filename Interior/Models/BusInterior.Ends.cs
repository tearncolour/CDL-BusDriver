using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Shell, the ends: the front (wall under the windscreen, corner posts, raked A-pillars, windscreen gasket, destination-sign box), the
// rear (wall with the rear window opening, engine tower in the driver-side corner) and the articulation (end portal, concertina
// bellows, turntable plate) at the open end of an articulated section.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        void FrontEnd(MeshKit k, int lod)
        {
            float fz = Spec.FrontZ;
            using (k.Paint(Sw.Panel))
            {
                k.Box(new float3(0f, 0.5f * (F - 0.12f + WsBotY), fz - 0.06f), new float3(2f * (HW - kNoseR), WsBotY - F + 0.12f, 0.12f));
                // the rounded corner posts below the windscreen (the side walls end where the round starts)
                for (int s = -1; s <= 1; s += 2)
                {
                    var post = new System.Collections.Generic.List<float2> { new float2(s * (HW - 0.14f), fz - 0.24f), new float2(s * HW, fz - 0.24f) };
                    for (int i = 0; i <= 6; i++)
                    {
                        float a = 0.5f * math.PI * i / 6f;
                        post.Add(new float2(s * (HW - kNoseR + kNoseR * math.cos(a)), fz - kNoseR + kNoseR * math.sin(a)));
                    }
                    post.Add(new float2(s * (HW - 0.14f), fz));
                    k.ExtrudeY(new Profile(post.ToArray()), F - 0.12f, WsBotY - 0.04f);
                }
            }
            // windscreen sill (the glass would sit on it), raked A-pillars
            using (k.Paint(Sw.DarkGrey))
                k.BevelBox(new float3(0f, WsBotY - 0.015f, WsBotZ - 0.02f), new float3(2f * HW - 0.04f, 0.05f, 0.16f), 0.015f);
            using (k.Paint(Sw.DarkGrey))
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    FrontPillar(k, s);
                }
            }
            // rubber gasket round the windscreen opening (in the raked glass plane)
            float3 v = math.normalize(new float3(0f, WsTopY - WsBotY, WsTopZ - WsBotZ));
            float hv = 0.5f * math.distance(new float2(WsBotY, WsBotZ), new float2(WsTopY, WsTopZ));
            float3 o = new float3(0f, 0.5f * (WsBotY + WsTopY), 0.5f * (WsBotZ + WsTopZ));
            using (k.Paint(Sw.Rubber))
                Frame(k, o, new float3(1f, 0f, 0f), v, new float2(WsHalf + 0.01f, hv + 0.01f), 0.14f, 0.035f, 0.026f, 6);
            // destination-sign box above the windscreen: closed housing, service flap on its inner face, LED sign on the front
            float dz0 = fz - 0.40f, dy0 = WsTopY + 0.02f;
            using (k.Paint(Sw.DarkGrey))
            {
                k.ExtrudeZ(EndSection(dy0, 0.02f, 0.02f, lod == 0 ? 5 : 2), dz0, fz - 0.06f);     // its top follows the roof's round
            }
            if (lod == 0)
            {
                using (k.Paint(Sw.Panel))
                    k.BevelBox(new float3(0f, 0.5f * (dy0 + CeilY), dz0 - 0.008f), new float3(2f * WallIn - 0.3f, CeilY - dy0 - 0.06f, 0.016f), 0.006f);
                for (int s = -1; s <= 1; s += 2)
                    k.PanelLine(new float3(s * 0.6f, dy0 + 0.05f, dz0 - 0.017f), new float3(s * 0.6f, CeilY - 0.06f, dz0 - 0.017f), new float3(0f, 0f, -1f), 0.006f);
                // the passengers' next-stop screen in the middle of the inner face
                LedMatrixScreen(k, LedScreenCentre(Spec));
            }
            // the destination sign's dark face (the cab system draws its LED dots when the bus is seen from outside)
            using (k.Paint(Sw.Screen))
                k.Box(new float3(0f, 0.5f * (dy0 + Spec.RoofY) - 0.02f, fz - 0.055f), new float3(1.7f, 0.2f, 0.012f), Faces.ZPos);
        }

        // Front face of the raked A-pillar at height y: the glass plane's z plus 1 cm. It recedes with the rake, 17 cm from the windscreen's foot to its top. Its outer 3.5 cm are chamfered back
        // to the side wall: there the front face is APillarOuterFrontZ.
        float APillarFrontZ(float y) => WsZ(y) + 0.010f;
        float APillarOuterFrontZ(float y) => WsZ(y) - 0.025f;

        void FrontPillar(MeshKit k, float side)
        {
            // One closed corner section joins the raked glass rebate to the side-window post.
            float y0 = SillY - 0.045f, y1 = WsTopY + 0.045f;
            float3[] Section(float y)
            {
                float z = WsZ(y);
                float rear = math.min(Spec.FrontZ - 0.29f, z - 0.11f);
                return new[] {
                    new float3(side * (HW - 0.135f), y, APillarFrontZ(y)),
                    new float3(side * (HW - 0.035f), y, APillarFrontZ(y)),
                    new float3(side * HW, y, APillarOuterFrontZ(y)),
                    new float3(side * HW, y, rear),
                    new float3(side * (HW - 0.075f), y, rear),
                    new float3(side * (HW - 0.135f), y, z - 0.065f),
                };
            }
            float3[] a = Section(y0), b = Section(y1);
            float3 ca = float3.zero, cb = float3.zero;
            for (int i = 0; i < a.Length; i++) { ca += a[i] / a.Length; cb += b[i] / b.Length; }
            for (int i = 0; i < a.Length; i++)
            {
                int j = (i + 1) % a.Length;
                float3 n = math.normalizesafe(math.cross(a[j] - a[i], b[i] - a[i]));
                if (math.dot(n, (a[i] + a[j] - 2f * ca)) < 0f) n = -n;
                k.QuadP(a[i], a[j], b[j], b[i], n);
                int ac = k.V(ca, new float3(0f, -1f, 0f)), bc = k.V(cb, new float3(0f, 1f, 0f));
                k.Tri(ac, k.V(a[i], new float3(0f, -1f, 0f)), k.V(a[j], new float3(0f, -1f, 0f)), new float3(0f, -1f, 0f));
                k.Tri(bc, k.V(b[i], new float3(0f, 1f, 0f)), k.V(b[j], new float3(0f, 1f, 0f)), new float3(0f, 1f, 0f));
            }
        }

        /// <summary>Dots of the LED next-stop screen (LedDots draws them at this count; one Unifont row high).</summary>
        public const int LedScreenDotsX = 128, LedScreenDotsY = 16;
        public static int2 LedScreenMatrix(in BusInteriorSpec spec) =>
            VehicleModels.Find(spec.ModelVariant)?.NextStopMatrix ?? new int2(LedScreenDotsX, LedScreenDotsY);
        const float kLedScreenW = 0.72f, kLedScreenH = 0.09f, kLedBezel = 0.012f;

        /// <summary>Where the destination box's inner-face screen sits in model space (its dark face centre), for a spec.</summary>
        static float3 LedScreenCentre(in BusInteriorSpec spec)
        {
            VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
            if (model != null) return model.NextStopCentre;
            float dy0 = spec.RoofY - 0.42f + 0.02f, ceilY = spec.RoofY - 0.27f, dz0 = spec.FrontZ - 0.40f;
            return new float3(0f, 0.5f * (dy0 + ceilY), dz0 - 0.016f);
        }

        /// <summary>
        /// The plane of the LED dots (model space, facing -Z) and its size: 3 mm in front of the screen's dark face, towards the
        /// saloon (the face is at centre.z - bezel - 0.001 - 0.001; smaller z = nearer the driver). The cab system puts the
        /// LedDots mesh there under the instance root. The first build had the dots 0.3 mm BEHIND the face: a dark screen.
        /// </summary>
        public static void LedScreenFrame(in BusInteriorSpec spec, out float3 centre, out float2 size)
        {
            VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
            if (model != null && model.NextStopSize.x > 0f)
            {
                centre = model.NextStopCentre;
                size = model.NextStopSize;
                return;
            }
            float3 c = LedScreenCentre(spec);
            centre = new float3(c.x, c.y, c.z - kLedBezel - 0.002f - 0.003f);
            size = new float2(kLedScreenW - 0.02f, kLedScreenH - 0.006f);
        }

        // The LED dot-matrix next-stop screen on the destination box's inner face (it faces the saloon, the driver sees it in the
        // mirror or by looking up): a black bezel standing proud of the panel and the screen's dark glass; the dots themselves are
        // a mesh the cab system writes at runtime (LedDots), placed by LedScreenFrame.
        void LedMatrixScreen(MeshKit k, float3 center)
        {
            using (k.At(center))
            {
                using (k.Paint(Sw.DashBlack))
                    k.BevelBox(new float3(0f, 0f, -0.5f * kLedBezel), new float3(kLedScreenW + 0.05f, kLedScreenH + 0.03f, kLedBezel), 0.004f);
                using (k.Paint(Sw.Screen))
                    k.Box(new float3(0f, 0f, -kLedBezel - 0.001f), new float3(kLedScreenW, kLedScreenH, 0.002f), Faces.ZNeg);
            }
        }

        void RearEnd(MeshKit k, int lod)
        {
            float rz = Spec.RearZ + 0.06f, wy0 = RearPlatY + 0.80f, wy1 = WinTop - 0.04f, wh = HW - 0.32f;
            // Two faces of different colour in one plane are drawn by the depth test in turn (the flicker at the right rear corner beside the lamp housing, 8 November). The skin's rear
            // edge, the posts' and the livery band's faces end exactly at RearZ, and the panel's sides lay on the barrel's contour: so its rear faces sit 5 mm inside RearZ and its
            // sides 1 cm inside the skin.
            const float kPanelInset = 0.01f;
            float backZ = Spec.RearZ + 0.005f, frontZ = rz + 0.06f, midZ = 0.5f * (backZ + frontZ), thick = frontZ - backZ;
            using (k.Paint(Sw.Panel))
            {
                k.Box(new float3(0f, 0.5f * (F - 0.12f + wy0), midZ), new float3(2f * (HW - kPanelInset), wy0 - F + 0.12f, thick));
                for (int s = -1; s <= 1; s += 2)
                    k.Box(new float3(s * 0.5f * (HW - kPanelInset + wh), 0.5f * (wy0 + wy1), midZ), new float3(HW - kPanelInset - wh, wy1 - wy0 + 0.02f, thick));
                k.ExtrudeZ(EndSection(wy1, kPanelInset, 0.005f, lod == 0 ? 5 : 2), backZ, frontZ);   // up into the roof's round
            }
            using (k.Paint(Sw.Rubber))
                Frame(k, new float3(0f, 0.5f * (wy0 + wy1), rz + 0.03f), new float3(1f, 0f, 0f), new float3(0f, 1f, 0f),
                    new float2(wh - 0.01f, 0.5f * (wy1 - wy0) - 0.01f), 0.1f, 0.05f, 0.04f, 3);
            // engine tower in the driver-side rear corner: panelled box up to the ceiling, louvred vent, service hatch
            float tx = Side * (WallIn - 0.30f), tz = RearIn + 0.36f, ty0 = RearPlatY, ty1 = CeilY - 0.05f;
            using (k.Paint(Sw.Panel)) k.BevelBox(new float3(tx, 0.5f * (ty0 + ty1), tz), new float3(0.62f, ty1 - ty0, 0.74f), 0.03f);
            if (lod > 0) return;
            using (k.Paint(Sw.DarkGrey)) k.Louvre(new float3(tx, ty0 + 0.45f, tz + 0.37f), new float2(0.42f, 0.36f), 6, new float3(0f, 0f, 1f));
            k.PanelLine(new float3(tx - 0.24f, ty0 + 0.8f, tz + 0.372f), new float3(tx + 0.24f, ty0 + 0.8f, tz + 0.372f), new float3(0f, 0f, 1f));
            k.PanelLine(new float3(tx - 0.24f, ty1 - 0.25f, tz + 0.372f), new float3(tx + 0.24f, ty1 - 0.25f, tz + 0.372f), new float3(0f, 0f, 1f));
            float hx = -Side * 0.31f;
            k.PanelLine(new float3(tx + hx, ty0 + 0.8f, tz + 0.2f), new float3(tx + hx, ty1 - 0.25f, tz + 0.2f), new float3(-Side, 0f, 0f));
            // the panel behind the bench (engine bay cover) with its grille
            using (k.Paint(Sw.DarkGrey)) k.Louvre(new float3(-Side * 0.35f, RearPlatY + 0.45f, RearIn + 0.002f), new float2(0.8f, 0.2f), 4, new float3(0f, 0f, 1f));
        }

        // Articulation at an open end (zEnd, outwards along dir): the end portal and its grab poles. The bellows and the turntable
        // between the sections bend with the joint: the front section's moving parts (BusInterior.Joint.cs).
        void Bellows(MeshKit k, float zEnd, float dir, int lod)
        {
            float t = 0.08f, zp = zEnd - dir * 0.5f * t, ox = WallIn - 0.12f, oy1 = CeilY - 0.12f;
            using (k.Paint(Sw.DarkGrey))
            {
                for (int s = -1; s <= 1; s += 2)
                    k.BevelBox(new float3(s * 0.5f * (ox + HW), 0.5f * (F - 0.12f + Spec.RoofY), zp), new float3(HW - ox, Spec.RoofY - F + 0.12f, t), 0.02f);
                k.BevelBox(new float3(0f, 0.5f * (oy1 + Spec.RoofY), zp), new float3(2f * HW, Spec.RoofY - oy1, t), 0.02f);
            }
            // a vertical grab pole at each side of the portal, floor to ceiling, and the threshold's yellow nosing
            if (lod == 0)
                using (k.Paint(Sw.Yellow))
                    for (int s = -1; s <= 1; s += 2)
                        k.Cylinder(new float3(s * (ox - 0.06f), F, zEnd - dir * 0.10f), new float3(s * (ox - 0.06f), oy1, zEnd - dir * 0.10f), 0.0175f, k.Sides(10));
            using (k.Paint(Sw.Yellow))
                k.Box(new float3(0f, F + 0.003f, zEnd - dir * 0.03f), new float3(2f * ox, 0.006f, 0.05f));
        }
    }
}
