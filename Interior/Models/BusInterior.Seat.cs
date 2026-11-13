using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// One passenger seat (Citaro "City Star" / Yutong style): a moulded plastic shell (side profile across the seat width), moquette
// inserts on the pan and the back, a yellow grab handle on the aisle seat's back, and the cantilever frame to the wall seat rail.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        // Shell side outline (z forward, y up) for a seat facing +z, origin on the floor under the pan centre.
        static readonly float2[] s_ShellPts =
        {
            new float2(0.235f, 0.428f), new float2(0.245f, 0.405f), new float2(0.205f, 0.388f), new float2(-0.16f, 0.392f),
            new float2(-0.215f, 0.43f), new float2(-0.265f, 0.80f), new float2(-0.285f, 0.975f), new float2(-0.268f, 1.0f),
            new float2(-0.245f, 0.982f), new float2(-0.228f, 0.80f), new float2(-0.19f, 0.47f), new float2(-0.15f, 0.446f),
            new float2(0.195f, 0.446f),
        };
        static readonly bool[] s_ShellSmooth = { true, true, false, false, true, false, true, true, true, false, true, true, true };
        static readonly float2[] s_ShellFar =
        {
            new float2(0.24f, 0.44f), new float2(0.24f, 0.39f), new float2(-0.2f, 0.39f), new float2(-0.28f, 1.0f), new float2(-0.23f, 1.0f), new float2(-0.17f, 0.44f),
        };

        static readonly Profile s_Shell = new Profile(s_ShellPts, s_ShellSmooth), s_ShellLo = new Profile(s_ShellFar);

        // Seat row at (x of the wall-side seat centre ... ), facing +z (back = false) or -z; n seats from the wall towards the aisle.
        // xs = x sign of the wall; base = floor height under the row; accent = priority seats.
        void SeatRow(MeshKit k, float xs, float z, float baseY, int n, bool back, bool accent, bool cantilever, int lod)
        {
            const float w = 0.44f;
            float xw = xs * (WallIn - 0.03f);                       // wall-side edge of the row
            if (m_SeatSink != null)                                 // RiderSlots: the cushion point of every seat
                for (int i = 0; i < n; i++)
                    m_SeatSink.Add(new float4(xw - xs * (w * (i + 0.5f)), baseY + 0.473f, z, back ? math.PI : 0f));
            if (k.Skipping) return;
            quaternion q = quaternion.RotateY(back ? math.PI : 0f);
            for (int i = 0; i < n; i++)
            {
                float xc = xw - xs * (w * (i + 0.5f));
                using (k.At(new float3(xc, baseY, z), q))
                {
                    using (k.Paint(Sw.Grey)) k.ExtrudeX(lod == 0 ? s_Shell : s_ShellLo, -0.5f * w + 0.012f, 0.5f * w - 0.012f, Caps.Both);
                    if (lod > 1) continue;
                    using (k.Paint(accent ? Sw.SeatPrintYellow : Sw.SeatFabricGrey))
                    {
                        k.BevelBox(new float3(0f, 0.458f, 0.02f), new float3(w - 0.07f, 0.03f, 0.33f), 0.012f);
                        k.BevelBox(new float3(0f, 0.70f, -0.226f), new float3(w - 0.08f, 0.40f, 0.022f), quaternion.RotateX(math.radians(-6.5f)), 0.01f);
                    }
                    if (lod > 0) continue;
                    // aisle seat: grab handle across the top of the back
                    if (i == n - 1)
                    {
                        float hx = -xs * (back ? -1f : 1f) * 0.06f;
                        using (k.Paint(Sw.Yellow))
                            Pipe(k, new[] { new float3(hx - 0.09f, 0.975f, -0.27f), new float3(hx - 0.09f, 1.05f, -0.275f), new float3(hx + 0.09f, 1.05f, -0.275f), new float3(hx + 0.09f, 0.975f, -0.27f) }, 0.013f, 10);
                    }
                }
            }
            // frame: a steel beam under the pans from the wall bracket to the aisle end; a leg or a cantilever strut there
            float xa = xw - xs * (w * n - 0.04f);
            float zf = back ? z + 0.02f : z - 0.02f;
            // over a wheel arch the pans rest on the housing (the wheel turns below it): a leg at the aisle end and a beam from it to
            // the housing's side, nothing reaching into the wheel well
            if (baseY > F + 0.01f)
                foreach (float3 a in Arches())
                {
                    if (math.abs(zf - a.x) >= kArchHalf + 0.04f) continue;
                    float plate = WallIn - a.y;
                    if (math.abs(xa) < plate - 0.05f)
                        using (k.Paint(Sw.Steel))
                        {
                            k.BoxAlong(new float3(xs * (plate + 0.01f), baseY + 0.36f, zf), new float3(xa, baseY + 0.36f, zf), new float2(0.05f, 0.05f), new float3(0f, 1f, 0f));
                            k.Cylinder(new float3(xa + xs * 0.06f, baseY, zf), new float3(xa + xs * 0.06f, baseY + 0.36f, zf), 0.025f, k.Sides(10));
                            k.BevelBox(new float3(xa + xs * 0.06f, baseY + 0.006f, zf), new float3(0.12f, 0.012f, 0.12f), 0.004f);
                        }
                    return;
                }
            using (k.Paint(Sw.Steel))
            {
                k.BoxAlong(new float3(xw + xs * 0.02f, baseY + 0.36f, zf), new float3(xa, baseY + 0.36f, zf), new float2(0.05f, 0.05f), new float3(0f, 1f, 0f));
                if (cantilever && n <= 2)
                {
                    k.BoxAlong(new float3(xw + xs * 0.02f, baseY + 0.12f, zf), new float3(xw - xs * 0.40f, baseY + 0.34f, zf), new float2(0.04f, 0.04f), new float3(0f, 0f, 1f));
                    k.BevelBox(new float3(xw + xs * 0.01f, baseY + 0.25f, zf), new float3(0.03f, 0.32f, 0.08f), 0.008f);
                }
                else
                {
                    k.Cylinder(new float3(xa + xs * 0.06f, baseY, zf), new float3(xa + xs * 0.06f, baseY + 0.36f, zf), 0.025f, k.Sides(10));
                    k.BevelBox(new float3(xa + xs * 0.06f, baseY + 0.006f, zf), new float3(0.12f, 0.012f, 0.12f), 0.004f);
                }
            }
        }
    }
}
