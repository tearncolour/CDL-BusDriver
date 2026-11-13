using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Ceiling: continuous light strips (LampWhite: lit at night) in the channels of the roof profile, poster frames on the slanted
// coves, linear air-con diffusers in the duct soffits, roof hatches, ceiling panel joints, speakers, CCTV domes, the next-stop LED
// display behind the cab (always lit), a TFT pair mid-bus, the STOP request signs (LampRed: lit on a stop request) and the amber
// warning lamps above the doors (flash while the doors move), the cab's reading light.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        float CeilFront => HasCab ? Spec.FrontZ - 0.85f : Spec.FrontZ - 0.12f;
        float CeilRear => OpenRear ? Spec.RearZ + 0.12f : Spec.RearZ + 0.14f;

        void DefineCeiling(MeshKit k, int lod)
        {
            float z0 = CeilRear, z1 = CeilFront, c = CeilY, xi = WallIn;
            for (int s = -1; s <= 1; s += 2)
                using (k.MirrorX(s < 0))
                {
                    // light strip in the channel: diffuser between two trims, in 2.4 m segments
                    int segs = math.max(1, (int)math.round((z1 - z0) / 2.4f));
                    for (int i = 0; i < segs; i++)
                    {
                        float a = math.lerp(z0, z1, i / (float)segs) + 0.03f, b = math.lerp(z0, z1, (i + 1) / (float)segs) - 0.03f;
                        using (k.Paint(Sw.LampWhite)) k.Box(new float3(xi - 0.33f, c - 0.108f, 0.5f * (a + b)), new float3(0.10f, 0.016f, b - a), Faces.NoTop);
                    }
                    if (lod > 0) continue;
                    using (k.Paint(Sw.Galvanised))
                        for (int e = -1; e <= 1; e += 2) k.Box(new float3(xi - 0.33f + e * 0.058f, c - 0.11f, 0.5f * (z0 + z1)), new float3(0.016f, 0.02f, z1 - z0), Faces.NoTop);
                    // linear air diffuser in the duct soffit
                    using (k.Paint(Sw.Black, 0.5f)) k.Box(new float3(xi - 0.55f, c - 0.078f, 0.5f * (z0 + z1)), new float3(0.07f, 0.006f, z1 - z0 - 0.2f), Faces.YNeg);
                    using (k.Paint(Sw.DarkGrey))
                        for (float z = z0 + 0.15f; z < z1 - 0.1f; z += 0.12f) k.Box(new float3(xi - 0.55f, c - 0.0815f, z), new float3(0.074f, 0.004f, 0.012f), Faces.YNeg);
                    PosterFrames(k, z0, z1);
                }
            if (lod == 0) CeilingDetails(k, z0, z1);
            Displays(k, lod);
            // amber warning lamps above the doors, on the cant rail
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                k.Lamp(new float3(-Side * (WallIn - 0.206f), WinTop + 0.07f, door.x), new float3(Side, -0.15f, 0f), Sw.Amber, new float2(0.16f, 0.05f));
            }
        }

        // Poster frames on the slanted cove (right side; mirrored by the caller).
        void PosterFrames(MeshKit k, float z0, float z1)
        {
            float xi = WallIn;
            float3 a = new float3(xi, WinTop + 0.07f, 0f), b = new float3(xi - 0.24f, CeilY - 0.13f, 0f);
            float3 up = math.normalize(b - a), n = math.normalize(new float3(-up.y, up.x, 0f));
            if (n.x > 0f) n = -n;
            float3 mid = 0.5f * (a + b);
            int count = math.max(1, (int)math.floor((z1 - z0) / 1.1f));
            for (int i = 0; i < count; i++)
            {
                float z = math.lerp(z0, z1, (i + 0.5f) / count);
                Sw poster = (i % 3) == 0 ? Sw.White : (i % 3) == 1 ? Sw.Blue : Sw.Orange;
                using (k.Frame(new float3(mid.x, mid.y, z) + n * 0.003f, n, up))
                {
                    using (k.Paint(poster)) k.Box(new float3(0f, 0f, 0.002f), new float3(0.62f, 0.2f, 0.004f), Faces.ZPos);
                    using (k.Paint(Sw.Galvanised)) Frame(k, new float3(0f, 0f, 0.006f), new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), new float2(0.32f, 0.11f), 0.01f, 0.018f, 0.01f, 0);
                }
            }
        }

        void CeilingDetails(MeshKit k, float z0, float z1)
        {
            float c = CeilY, w = WallIn - 0.72f;
            // panel joints across the centre ceiling
            for (float z = z0 + 1.2f; z < z1 - 0.3f; z += 1.2f)
                k.PanelLine(new float3(-w, c - 0.001f, z), new float3(w, c - 0.001f, z), new float3(0f, -1f, 0f), 0.008f);
            // roof hatches (two on a long body, one on a short one): recessed panel in a frame, red emergency handle
            int hatches = z1 - z0 > 7f ? 2 : 1;
            for (int i = 0; i < hatches; i++)
            {
                float hz = hatches == 1 ? 0.5f * (z0 + z1) : math.lerp(z0, z1, i == 0 ? 0.28f : 0.7f);
                using (k.Paint(Sw.DarkGrey))
                    Frame(k, new float3(0f, c - 0.012f, hz), new float3(1f, 0f, 0f), new float3(0f, 0f, 1f), new float2(0.36f, 0.42f), 0.05f, 0.05f, 0.024f, 2);
                using (k.Paint(Sw.Grey)) k.Box(new float3(0f, c - 0.004f, hz), new float3(0.68f, 0.008f, 0.8f), Faces.YNeg);
                using (k.Paint(Sw.Red)) k.BevelBox(new float3(0f, c - 0.03f, hz - 0.3f), new float3(0.16f, 0.025f, 0.04f), 0.008f);
                using (k.Paint(Sw.Yellow)) k.Box(new float3(0.2f, c - 0.009f, hz - 0.3f), new float3(0.12f, 0.002f, 0.06f), Faces.YNeg);
            }
            // speakers and CCTV domes along the centre line
            int n = math.max(2, (int)math.round((z1 - z0) / 2.6f));
            for (int i = 0; i < n; i++)
            {
                float z = math.lerp(z0, z1, (i + 0.5f) / n) + 0.6f;
                if (z > z1 - 0.2f) continue;
                using (k.Paint(Sw.DarkGrey)) k.Tube(new float3(0f, c, z), new float3(0f, c - 0.01f, z), 0.085f, 0.07f, 16, Caps.B);
                using (k.Paint(Sw.Black, 0.6f)) k.Disc(new float3(0f, c - 0.004f, z), new float3(0f, -1f, 0f), 0.071f, 16);
            }
            for (int i = 0; i < DoorCount; i++)
            {
                float3 dc = new float3(-Side * 0.15f, c, Door(i).x + 0.9f);
                if (dc.z > z1 - 0.2f) dc.z = Door(i).x - 0.9f;
                using (k.Paint(Sw.White)) k.Cylinder(dc, dc - new float3(0f, 0.02f, 0f), 0.07f, 16);
                using (k.AxisY(dc - new float3(0f, 0.02f, 0f), new float3(0f, -1f, 0f)))
                using (k.Paint(Sw.Screen)) k.Lathe(new[] { new LP(0.055f, 0f, true), new LP(0.045f, 0.035f), new LP(0f, 0.05f) }, 16);
            }
        }
    }
}
