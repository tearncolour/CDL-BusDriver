using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Dash: the VDV-style cockpit wrapped round the driver. Main dash (side profile across the driver's area: defroster strip on top,
// lip, switch face, knee recess, pedal box), the low instrument cluster on its neck behind the wheel (two round dials with chrome
// bezels and tick marks, a central display, the warning-light row, a sun hood), the column shroud with the stalks, the switch
// wings (D / N / R buttons, rocker switches, door / kneeling / hazard buttons), the angled side console and the cabinet in front
// of the entrance. Authored for the driver on the left (x < 0) and mirrored for Side = +1; the dial faces are driver-relative.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        float LCabX => -(Spec.HalfWidth - 0.66f);         // the driver's centre line in the left-hand authoring frame

        void DefineDash(MeshKit k, int lod)
        {
            using (k.MirrorX(Side > 0f)) { DashBody(k, lod); DashExtras(k, lod); }
            for (int i = 0; i < 2; i++) DialFace(k, i, lod);
            Cluster(k, lod);
        }

        // Side outline (z, y) of the main dash in front of the driver.
        float2[] DashOutline(float fz, float top)
        {
            return new[]
            {
                new float2(fz - 0.11f, PlatY - 0.02f), new float2(fz - 0.11f, WsBotY + 0.01f), new float2(fz - 0.24f, top - 0.035f),
                new float2(fz - 0.42f, top), new float2(fz - 0.48f, top - 0.015f), new float2(fz - 0.52f, top - 0.075f),
                new float2(fz - 0.58f, WsBotY - 0.20f), new float2(fz - 0.50f, PlatY + 0.18f), new float2(fz - 0.30f, PlatY + 0.10f),
                new float2(fz - 0.30f, PlatY - 0.02f),
            };
        }
        static readonly bool[] s_DashSmooth = { false, false, true, true, true, true, false, false, false, false };

        void DashBody(MeshKit k, int lod)
        {
            float fz = Spec.FrontZ, cx = LCabX, top = WsBotY + 0.075f;
            float xr = cx + 0.33f;                         // right end of the main dash (the side console starts there)
            using (k.Paint(Sw.DashBlack)) k.ExtrudeX(new Profile(DashOutline(fz, top), s_DashSmooth), -WallIn - 0.02f, xr, Caps.Both);
            // side console towards the door, turned 28 degrees to face the driver
            using (k.At(new float3(xr - 0.02f, 0f, fz - 0.50f), quaternion.RotateY(math.radians(28f))))
            {
                var o = DashOutline(0.39f, top - 0.03f);
                using (k.Paint(Sw.DashBlack)) k.ExtrudeX(new Profile(o, s_DashSmooth), 0f, 0.46f, Caps.Both);
                if (lod == 0) ConsoleButtons(k, top - 0.03f);
            }
            // cabinet in front of the entrance (electrics, the ticket printer shelf on top), up to the door-side wall
            float c0 = cx + 0.70f, c1 = math.min(0.48f, WallIn - 0.62f);
            using (k.Paint(Sw.Panel)) k.BevelBox(new float3(0.5f * (c0 + c1), 0.5f * (F + WsBotY), fz - 0.27f), new float3(c1 - c0, WsBotY - F, 0.32f), 0.02f);
            using (k.Paint(Sw.DashBlack)) k.BevelBox(new float3(0.5f * (c0 + c1), WsBotY + 0.015f, fz - 0.27f), new float3(c1 - c0 + 0.02f, 0.05f, 0.36f), 0.015f);
            if (lod > 0) return;
            k.PanelLine(new float3(c0 + 0.05f, F + 0.08f, fz - 0.432f), new float3(c0 + 0.05f, WsBotY - 0.06f, fz - 0.432f), new float3(0f, 0f, -1f));
            k.PanelLine(new float3(c1 - 0.05f, F + 0.08f, fz - 0.432f), new float3(c1 - 0.05f, WsBotY - 0.06f, fz - 0.432f), new float3(0f, 0f, -1f));
            using (k.Paint(Sw.Steel)) k.BevelBox(new float3(c0 + 0.12f, WsBotY - 0.18f, fz - 0.435f), new float3(0.02f, 0.06f, 0.02f), 0.005f);   // cabinet lock
            // defroster outlets along the windscreen base
            for (int i = 0; i < 4; i++)
            {
                float x0 = math.lerp(-WallIn + 0.12f, c1 - 0.15f, i / 4f) + 0.04f, x1 = math.lerp(-WallIn + 0.12f, c1 - 0.15f, (i + 1) / 4f) - 0.04f;
                float y = i < 2 ? WsBotY + 0.045f : WsBotY + 0.042f;
                using (k.Paint(Sw.DarkGrey)) k.Louvre(new float3(0.5f * (x0 + x1), y, fz - 0.19f), new float2(x1 - x0, 0.06f), 3, new float3(0f, 1f, 0.12f));
            }
            // round face vents left of the binnacle and on the console
            DashVent(k, new float3(cx - 0.42f, top - 0.09f, fz - 0.54f));
            DashVent(k, new float3(cx + 0.40f, top - 0.10f, fz - 0.50f));
            // left switch wing: two rows of rocker switches, the D / N / R push buttons, the parking brake valve
            using (k.At(new float3(cx - 0.40f, WsBotY - 0.07f, fz - 0.555f), math.mul(quaternion.RotateY(math.radians(-14f)), quaternion.RotateX(math.radians(18f)))))
            {
                using (k.Paint(Sw.Black)) k.BevelBox(new float3(0f, 0f, -0.005f), new float3(0.30f, 0.15f, 0.012f), 0.004f);
                for (int r = 0; r < 2; r++)
                    for (int i = 0; i < 6; i++) Rocker(k, new float3(-0.12f + 0.045f * i, 0.035f - 0.065f * r, -0.012f), (r * 6 + i) % 4);
            }
            using (k.At(new float3(cx - 0.36f, WsBotY + 0.035f, fz - 0.46f), quaternion.RotateX(math.radians(62f))))
                for (int i = 0; i < 3; i++)
                {
                    using (k.Paint(Sw.Black)) k.BevelBox(new float3(-0.05f + 0.05f * i, 0f, 0f), new float3(0.044f, 0.044f, 0.014f), 0.004f);
                    using (k.Paint(i == 0 ? Sw.Green : i == 1 ? Sw.White : Sw.Red)) k.BevelBox(new float3(-0.05f + 0.05f * i, 0f, -0.01f), new float3(0.034f, 0.034f, 0.01f), 0.004f);
                }
            using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(-WallIn + 0.1f, top - 0.06f, fz - 0.62f), new float3(0.08f, 0.06f, 0.1f), 0.01f);
            using (k.Paint(Sw.Yellow)) k.BoxAlong(new float3(-WallIn + 0.1f, top - 0.04f, fz - 0.64f), new float3(-WallIn + 0.1f, top + 0.03f, fz - 0.68f), new float2(0.03f, 0.03f), new float3(1f, 0f, 0f), true);
        }

        // Rocker switch with a coloured indicator window (variant 0..3).
        void Rocker(MeshKit k, float3 c, int v)
        {
            using (k.Paint(Sw.DarkGrey)) k.BevelBox(c, new float3(0.028f, 0.045f, 0.012f), 0.004f);
            using (k.Paint(Sw.Black)) k.Box(c + new float3(0f, -0.006f, -0.008f), new float3(0.022f, 0.022f, 0.006f), quaternion.RotateX(math.radians(12f)));
            Sw ind = v == 0 ? Sw.Green : v == 1 ? Sw.Orange : v == 2 ? Sw.White : Sw.Blue;
            using (k.Paint(ind)) k.Box(c + new float3(0f, 0.013f, -0.0065f), new float3(0.012f, 0.005f, 0.002f), Faces.ZNeg);
        }

        void DashVent(MeshKit k, float3 c)
        {
            float3 n = new float3(0f, 0.25f, -1f);
            using (k.Paint(Sw.DarkGrey)) k.Tube(c, c + 0.02f * math.normalize(n), 0.05f, 0.036f, k.Sides(16), Caps.Both);
            using (k.Paint(Sw.Black, 0.4f)) k.Disc(c + 0.004f * math.normalize(n), n, 0.037f, k.Sides(16));
            using (k.Paint(Sw.DarkGrey))
                for (int i = -1; i <= 1; i++) k.Box(c + new float3(0f, 0.022f * i, -0.008f), new float3(0.066f, 0.006f, 0.012f));
        }
    }
}
