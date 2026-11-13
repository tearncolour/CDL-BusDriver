using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Dash extras (left-hand authoring frame, mirrored with the dash): the driver's ticketing / ITCS terminal on its stalk at the right
// of the cluster, the announcement microphone on a gooseneck, the ticket printer and the change tray on the entrance cabinet, the
// dash top pad seams and the lower trim of the switch face.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        void DashExtras(MeshKit k, int lod)
        {
            float fz = Spec.FrontZ, cx = LCabX, top = WsBotY + 0.075f;
            float3 eye = new float3(cx, Spec.FloorY + kEyeHeight, fz - kEyeBack);
            // terminal: housing, touch screen with a route bar and stop list, three hard keys, the stalk
            float3 tc = new float3(cx + 0.50f, top + 0.14f, fz - 0.50f);
            float3 toEye = math.normalize(eye - tc);
            using (k.Paint(Sw.DashBlack)) k.BoxAlong(new float3(cx + 0.52f, top - 0.04f, fz - 0.44f), tc + new float3(0f, -0.04f, 0.04f), new float2(0.04f, 0.04f), new float3(0f, 0f, 1f), true);
            using (k.Frame(tc, toEye, new float3(0f, 1f, 0f)))
            {
                using (k.Paint(Sw.DashBlack)) k.BevelBox(new float3(0f, 0f, -0.025f), new float3(0.27f, 0.19f, 0.045f), 0.012f);
                using (k.Paint(Sw.Screen)) k.Box(new float3(-0.02f, 0.005f, -0.0015f), new float3(0.2f, 0.15f, 0.003f), Faces.ZPos);
                if (lod == 0)
                {
                    using (k.Paint(Sw.Blue)) k.Box(new float3(-0.02f, 0.064f, 0.0f), new float3(0.2f, 0.028f, 0.001f), Faces.ZPos);
                    using (k.Paint(Sw.White))
                        for (int r = 0; r < 4; r++) k.Box(new float3(-0.04f + 0.01f * (r & 1), 0.03f - 0.03f * r, 0.0f), new float3(0.13f - 0.015f * r, 0.012f, 0.001f), Faces.ZPos);
                    using (k.Paint(Sw.LedAmber)) k.Box(new float3(0.055f, 0.03f, 0.0f), new float3(0.03f, 0.014f, 0.001f), Faces.ZPos);
                    using (k.Paint(Sw.DarkGrey))
                        for (int i = 0; i < 3; i++) k.BevelBox(new float3(0.108f, 0.05f - 0.05f * i, 0.0f), new float3(0.026f, 0.03f, 0.008f), 0.004f);
                }
            }
            if (lod > 0) return;
            // microphone on a gooseneck from the left wing
            using (k.Paint(Sw.Black))
            {
                float3 m0 = new float3(cx - 0.33f, top - 0.02f, fz - 0.56f);
                Pipe(k, new[] { m0, m0 + new float3(0.01f, 0.12f, -0.02f), m0 + new float3(0.05f, 0.2f, -0.07f), m0 + new float3(0.1f, 0.22f, -0.12f) }, 0.006f, 8, Caps.Both);
                using (k.AxisY(m0 + new float3(0.1f, 0.22f, -0.12f), new float3(0.6f, 0.2f, -0.75f)))
                    k.Lathe(new[] { new LP(0f, 0f), new LP(0.016f, 0.005f), new LP(0.02f, 0.03f), new LP(0.016f, 0.055f), new LP(0f, 0.06f) }, k.Sides(10));
            }
            // ticket printer and change tray on the entrance cabinet
            float px = 0.5f * (cx + 0.70f + math.min(0.48f, WallIn - 0.62f)), py = WsBotY + 0.04f, pz = fz - 0.30f;
            using (k.Paint(Sw.Grey)) k.BevelBox(new float3(px, py + 0.07f, pz), new float3(0.26f, 0.14f, 0.2f), 0.02f);
            using (k.Paint(Sw.Black))
            {
                k.Box(new float3(px, py + 0.12f, pz - 0.101f), new float3(0.12f, 0.008f, 0.004f), Faces.ZNeg);          // paper slot
            }
            using (k.Paint(Sw.White)) k.Box(new float3(px, py + 0.14f, pz - 0.13f), new float3(0.08f, 0.002f, 0.06f), quaternion.RotateX(math.radians(-30f)));   // a ticket
            // dash top pad seams and the switch-face trim strip
            k.PanelLine(new float3(-WallIn + 0.05f, top + 0.001f, fz - 0.33f), new float3(cx + 0.3f, top - 0.005f, fz - 0.33f), new float3(0f, 1f, -0.15f));
            using (k.Paint(Sw.DarkGrey))
                k.BoxAlong(new float3(-WallIn + 0.02f, top - 0.07f, fz - 0.522f), new float3(cx + 0.32f, top - 0.07f, fz - 0.522f), new float2(0.03f, 0.008f), new float3(0f, 1f, -0.3f));
        }
    }
}
