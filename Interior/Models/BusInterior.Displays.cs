using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Ceiling displays: the next-stop LED display (dot-matrix text in LedAmber on a black face) with the STOP request panel (LampRed),
// hanging over the aisle behind the cab (or at the front end of a rear section), a double-sided TFT pair mid-bus, the STOP sign at
// the ramp door, and the driver's reading light.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        void Displays(MeshKit k, int lod)
        {
            float c = CeilY;
            float dz = HasCab ? CabRearZ - 0.12f : Spec.FrontZ - 0.40f;
            LedDisplay(k, new float3(0f, c - 0.17f, dz), lod);
            // TFT pair (double-sided) between the front and the ramp door
            float2 ramp = Door(RampDoor);
            float tz = ramp.x + 1.45f;
            if (tz < dz - 0.8f)
            {
                float3 tc = new float3(0f, c - 0.22f, tz);
                using (k.Paint(Sw.Galvanised))
                    for (int s = -1; s <= 1; s += 2) k.Cylinder(new float3(s * 0.2f, c, tz), new float3(s * 0.2f, c - 0.06f, tz), 0.012f, 8, Caps.None);
                using (k.Paint(Sw.DashBlack)) k.BevelBox(tc, new float3(0.58f, 0.34f, 0.08f), 0.015f);
                for (int f = -1; f <= 1; f += 2)
                {
                    using (k.Paint(Sw.Screen)) k.Box(tc + new float3(0f, 0f, f * 0.041f), new float3(0.52f, 0.29f, 0.002f), f > 0 ? Faces.ZPos : Faces.ZNeg);
                    if (lod > 0) continue;
                    using (k.Paint(Sw.Blue)) k.Box(tc + new float3(0f, 0.115f, f * 0.0425f), new float3(0.52f, 0.06f, 0.001f), f > 0 ? Faces.ZPos : Faces.ZNeg);
                    using (k.Paint(Sw.White))
                        for (int r = 0; r < 4; r++)
                            k.Box(tc + new float3(-0.08f + 0.02f * (r % 2), 0.05f - 0.055f * r, f * 0.0425f), new float3(0.3f - 0.04f * r, 0.018f, 0.001f), f > 0 ? Faces.ZPos : Faces.ZNeg);
                }
            }
            // STOP sign over the ramp door's standing area, lit both ways
            float3 sc = new float3(-Side * 0.55f, c - 0.13f, ramp.x);
            using (k.Paint(Sw.Galvanised)) k.Cylinder(new float3(sc.x, c, sc.z), sc, 0.01f, 8, Caps.None);
            using (k.Paint(Sw.DashBlack)) k.BevelBox(sc - new float3(0f, 0.04f, 0f), new float3(0.38f, 0.11f, 0.06f), 0.012f);
            using (k.Paint(Sw.LampRed))
                for (int f = -1; f <= 1; f += 2) k.Box(sc + new float3(0f, -0.04f, f * 0.031f), new float3(0.32f, 0.07f, 0.004f), f > 0 ? Faces.ZPos : Faces.ZNeg);
            // reading light over the driver
            if (HasCab) k.Lamp(new float3(CabX, c - 0.004f, Spec.FrontZ - 1.25f), new float3(0f, -1f, 0f), Sw.LampWhite, new float2(0.2f, 0.08f));
        }

        void LedDisplay(MeshKit k, float3 c, int lod)
        {
            const float w = 1.30f, h = 0.20f, d = 0.09f;
            using (k.Paint(Sw.Galvanised))
                for (int s = -1; s <= 1; s += 2) k.Cylinder(new float3(s * 0.45f, CeilY, c.z), new float3(s * 0.45f, c.y + 0.5f * h - 0.01f, c.z), 0.012f, 8, Caps.None);
            using (k.Paint(Sw.DashBlack)) k.BevelBox(c, new float3(w, h, d), 0.02f);
            float fz = c.z - 0.5f * d - 0.001f;
            using (k.Paint(Sw.Screen)) k.Box(new float3(c.x - 0.12f, c.y, fz), new float3(1.0f, 0.15f, 0.002f), Faces.ZNeg);
            using (k.Paint(Sw.LampRed)) k.Box(new float3(c.x + 0.52f, c.y, fz - 0.001f), new float3(0.2f, 0.13f, 0.002f), Faces.ZNeg);
            if (lod > 0) return;
            // dot-matrix text: route number, then the next stop's name (blocks of "letters")
            using (k.Paint(Sw.LedAmber))
            {
                float x = c.x - 0.6f;
                for (int i = 0; i < 17; i++)
                {
                    bool gap = i == 3 || i == 9;
                    float cw = i < 3 ? 0.045f : 0.036f;
                    if (!gap) k.Box(new float3(x + 0.5f * cw, c.y + (i < 3 ? 0f : 0.008f), fz - 0.0015f), new float3(cw, i < 3 ? 0.1f : 0.075f, 0.001f), Faces.ZNeg);
                    x += cw + 0.016f;
                }
                k.Box(new float3(c.x + 0.05f, c.y - 0.06f, fz - 0.0015f), new float3(0.58f, 0.012f, 0.001f), Faces.ZNeg);
            }
        }
    }
}
