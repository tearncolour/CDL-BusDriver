using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// The instrument cluster (binnacle, dial faces, display, telltales, column shroud, stalks), the side-console buttons, and the
// animated parts: the steering wheel (500 mm padded rim, two dished spokes with brushed trims, padded hub with the horn pad) and
// the two needles. Wheel and needles are flat shaded (they turn). Dial frames: local +X = the driver's left, +Y = up the face,
// +Z = towards the driver; a point at clockwise angle a from the top (as the driver sees it) is (-sin a, cos a).
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        const float kDialR = 0.08f;

        /// <summary>Lamps in the cluster's lower telltale row.</summary>
        public const int TelltaleSlots = 12;

        /// <summary>
        /// A telltale lamp of the lower row (slot 0 .. 11, left to right as the driver sees it): centre, the frame its lamp faces -Z in,
        /// size. The dial's frame has its local +X on the driver's LEFT (see DialRot), so slot 0, the left indicator's, is at the
        /// +X end of the row: the row used to run the other way and the left indicator lit on the driver's right (the user's report
        /// from the driver's view, 17 October). The housings drawn in Cluster are the same twelve places, so they do not move.
        /// </summary>
        public static void TelltaleFrame(in BusInteriorSpec s, int slot, out float3 centre, out quaternion rotation, out float2 size)
        {
            var bus = new BusInterior(s);
            float3 m = 0.5f * (bus.DialCentre(0) + bus.DialCentre(1));
            centre = m + math.mul(bus.DialRot, new float3(0.209f - 0.038f * slot, -0.091f, 0.0015f));
            rotation = math.mul(bus.DialRot, quaternion.RotateY(math.PI));
            size = new float2(0.014f, 0.010f);
        }

        /// <summary>The cluster's central display (its dots face the driver: -Z of the frame): centre, frame, size.</summary>
        public static void ClusterDisplayFrame(in BusInteriorSpec s, out float3 centre, out quaternion rotation, out float2 size)
        {
            var bus = new BusInterior(s);
            float3 m = 0.5f * (bus.DialCentre(0) + bus.DialCentre(1));
            centre = m + math.mul(bus.DialRot, new float3(0f, -0.005f, 0.0045f));
            rotation = math.mul(bus.DialRot, quaternion.RotateY(math.PI));
            size = new float2(0.07f, 0.06f);
        }

        void Cluster(MeshKit k, int lod)
        {
            float3 m = 0.5f * (DialCentre(0) + DialCentre(1));
            using (k.At(m, DialRot))
            {
                using (k.Paint(Sw.DashBlack))
                {
                    k.BevelBox(new float3(0f, -0.008f, -0.07f), new float3(0.55f, 0.205f, 0.12f), 0.03f);      // housing
                    k.BevelBox(new float3(0f, 0.1f, 0.025f), new float3(0.58f, 0.02f, 0.1f), 0.008f);           // sun hood
                    for (int s = -1; s <= 1; s += 2)
                        k.BevelBox(new float3(s * 0.28f, 0.0f, 0.02f), new float3(0.02f, 0.2f, 0.08f), 0.006f);
                    // neck down to the dash, round the column
                    k.BevelBox(new float3(0f, -0.2f, -0.12f), new float3(0.3f, 0.2f, 0.16f), quaternion.RotateX(math.radians(-20f)), 0.03f);
                }
                using (k.Paint(Sw.Black)) k.Box(new float3(0f, -0.008f, -0.0095f), new float3(0.54f, 0.19f, 0.011f));
                // central display (the cab system shows the gear on it) and the telltale rows (the lower row's lamps are lit by the cab
                // system: TelltaleFrame)
                using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(0f, -0.005f, -0.002f), new float3(0.1f, 0.092f, 0.008f), 0.004f);
                using (k.Paint(Sw.Screen)) k.Box(new float3(0f, -0.005f, 0.0025f), new float3(0.086f, 0.074f, 0.002f), Faces.ZPos);
                if (lod > 0) return;
                for (int r = 0; r < 2; r++)
                    for (int i = 0; i < TelltaleSlots; i++)
                    {
                        int v = (i * 7 + r * 5) % 9;
                        Sw sw = r == 1 ? Sw.Screen : v == 0 ? Sw.Red : v == 3 ? Sw.Green : v == 5 ? Sw.LedAmber : v == 7 ? Sw.Blue : Sw.Screen;
                        float y = r == 0 ? 0.081f : -0.091f;
                        using (k.Paint(sw)) k.Box(new float3(-0.209f + 0.038f * i, y, -0.0025f), new float3(0.014f, 0.01f, 0.004f), Faces.ZPos);
                    }
            }
            // column shroud from under the hub into the dash, the stalks on its left
            float3 hub = WheelHub, col = math.mul(WheelRot, new float3(0f, 0f, 1f));
            using (k.Paint(Sw.DashBlack))
                k.BoxAlong(hub - 0.07f * col, hub - 0.45f * col, new float2(0.13f, 0.12f), new float3(0f, 0f, 1f), true);
            if (lod > 0) return;
            for (int i = 0; i < 2; i++)
            {
                float3 a = hub - (0.13f + 0.05f * i) * col + new float3(-0.06f, 0f, 0f), b = a + new float3(-0.17f, -0.02f + 0.03f * i, -0.03f);
                using (k.Paint(Sw.Black)) k.BoxAlong(a, b, new float2(0.016f, 0.012f), new float3(0f, 1f, 0f), true);
                using (k.Paint(Sw.DarkGrey)) k.Cylinder(b, b + new float3(-0.05f, -0.004f, -0.01f), 0.011f, k.Sides(8));
            }
            using (k.Paint(Sw.Black)) k.Cylinder(hub - 0.2f * col + new float3(0.065f, 0f, 0f), hub - 0.2f * col + new float3(0.1f, 0f, 0f), 0.022f, k.Sides(12));   // ignition switch
        }

        void DialFace(MeshKit k, int which, int lod)
        {
            using (k.At(DialCentre(which), DialRot))
            {
                using (k.Paint(Sw.Screen)) k.Disc(float3.zero, new float3(0f, 0f, 1f), kDialR, k.Sides(32));
                using (k.Paint(Sw.Chrome)) k.Tube(new float3(0f, 0f, -0.004f), new float3(0f, 0f, 0.012f), kDialR + 0.009f, kDialR, k.Sides(32), Caps.B);
                if (lod > 0) return;
                // scale: 0 at -120 degrees .. full at +120; speed 0..120 km/h (major 10, minor 5), rpm 0..3000 (major 500, minor 100, red from 2500)
                int steps = which == 0 ? 24 : 30, major = which == 0 ? 2 : 5;
                for (int i = 0; i <= steps; i++)
                {
                    float a = math.radians(-120f + 240f * i / steps);
                    bool big = i % major == 0;
                    float len = big ? 0.016f : 0.008f, w = big ? 0.0045f : 0.0022f, r = kDialR - 0.008f - 0.5f * len;
                    bool red = which == 1 && i >= 25;
                    using (k.Paint(red ? Sw.Red : Sw.White))
                        k.Box(new float3(-math.sin(a) * r, math.cos(a) * r, 0.0008f), new float3(w, len, 0.001f), quaternion.RotateZ(a), Faces.ZPos);
                    if (big && !red)                  // number plate stand-ins: a short white bar inside each major mark
                        using (k.Paint(Sw.Grey)) k.Box(new float3(-math.sin(a) * (r - 0.02f), math.cos(a) * (r - 0.02f), 0.0008f), new float3(0.008f, 0.004f, 0.001f), Faces.ZPos);
                }
                using (k.Paint(Sw.White)) k.Box(new float3(0f, -0.042f, 0.0008f), new float3(0.022f, 0.005f, 0.001f), Faces.ZPos);     // unit label
                if (which == 0)
                    using (k.Paint(Sw.LedAmber)) k.Box(new float3(0f, -0.06f, 0.0008f), new float3(0.03f, 0.008f, 0.001f), Faces.ZPos); // odometer
            }
        }

        // Buttons on the top of the side console (console frame, top surface around y = top): door buttons with guards, kneeling,
        // ramp, hazard, the stop-brake button, the ticket-printer keypad.
        void ConsoleButtons(MeshKit k, float top)
        {
            for (int i = 0; i < 3; i++)
            {
                float3 c = new float3(0.09f + 0.085f * i, top + 0.003f, 0.02f);
                using (k.Paint(Sw.Yellow)) k.Cylinder(c, c + new float3(0f, 0.012f, 0f), 0.027f, k.Sides(16));
                using (k.Paint(i == 1 ? Sw.Red : Sw.Green)) k.Cylinder(c + new float3(0f, 0.012f, 0f), c + new float3(0f, 0.022f, 0f), 0.019f, k.Sides(16));
            }
            for (int i = 0; i < 4; i++)
            {
                float3 c = new float3(0.07f + 0.08f * i, top - 0.005f, 0.11f);
                Sw sw = i == 0 ? Sw.Blue : i == 1 ? Sw.White : i == 2 ? Sw.Red : Sw.Orange;
                using (k.Paint(Sw.Black)) k.BevelBox(c, new float3(0.05f, 0.016f, 0.05f), 0.005f);
                using (k.Paint(sw)) k.BevelBox(c + new float3(0f, 0.01f, 0f), new float3(0.034f, 0.008f, 0.034f), 0.004f);
            }
            using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(0.34f, top + 0.01f, -0.06f), new float3(0.14f, 0.03f, 0.1f), 0.008f);   // keypad
            using (k.Paint(Sw.Grey))
                for (int i = 0; i < 9; i++) k.Box(new float3(0.305f + 0.035f * (i % 3), top + 0.027f, -0.09f + 0.03f * (i / 3)), new float3(0.024f, 0.006f, 0.022f), Faces.NoBottom);
        }

        // ---------------------------------------------------------------- animated parts

        void DefineNeedle(MeshKit k, float3 centre)
        {
            using (k.At(centre, DialRot))
            using (k.Shade(ShadeRule.Flat))
            {
                using (k.Paint(Sw.Orange)) k.Prism(new[] { new float2(-0.0028f, -0.018f), new float2(0.0028f, -0.018f), new float2(0.0009f, 0.074f), new float2(-0.0009f, 0.074f) },
                    new float3(0f, 0f, 0.004f), new float3(0f, 0f, 0.007f), new float3(0f, 1f, 0f));
                using (k.Paint(Sw.Black)) k.Cylinder(new float3(0f, 0f, 0.003f), new float3(0f, 0f, 0.012f), 0.011f, k.Sides(12));
            }
        }

        void DefineWheel(MeshKit k, int lod)
        {
            const float R = 0.235f;
            using (k.At(WheelHub, WheelRot))
            using (k.Shade(ShadeRule.Flat))
            {
                int n = k.Sides(40), m = k.Sides(12);
                var ring = Arc(float3.zero, new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), R, 0f, 2f * math.PI * (n - 1) / n, n - 1);
                var rim = new float2[m];
                for (int i = 0; i < m; i++) { float a = 2f * math.PI * i / m; rim[i] = new float2(0.017f * math.cos(a), 0.021f * math.sin(a)); }
                var sm = new bool[m];
                for (int i = 0; i < m; i++) sm[i] = true;
                using (k.Paint(Sw.DashBlack)) k.Sweep(ring, new Profile(rim, sm), new float3(0f, 0f, 1f), Caps.None, null, true);
                // two dished spokes slightly below the horizontal, brushed trims on their faces
                for (int s = 0; s < 2; s++)
                {
                    float a = math.radians(s == 0 ? 100f : 260f);
                    float3 d = new float3(-math.sin(a), math.cos(a), 0f);
                    float3 p0 = d * 0.05f + new float3(0f, 0f, -0.035f), p1 = d * (R - 0.01f);
                    using (k.Paint(Sw.DashBlack)) k.BoxAlong(p0, p1, new float2(0.06f, 0.018f), new float3(0f, 0f, 1f), true);
                    using (k.Paint(Sw.Galvanised)) k.BoxAlong(p0 + d * 0.03f + new float3(0f, 0f, 0.01f), p1 - d * 0.03f + new float3(0f, 0f, 0.0095f), new float2(0.026f, 0.003f), new float3(0f, 0f, 1f));
                }
                // padded hub with the horn pad and a badge
                using (k.AxisY(float3.zero, new float3(0f, 0f, 1f)))
                {
                    using (k.Paint(Sw.DashBlack))
                        k.Lathe(new[] { new LP(0f, -0.09f), new LP(0.04f, -0.09f, true), new LP(0.045f, -0.06f), new LP(0.078f, -0.045f), new LP(0.082f, -0.02f),
                                        new LP(0.074f, 0.004f), new LP(0.05f, 0.016f), new LP(0f, 0.019f) }, k.Sides(24));
                    using (k.Paint(Sw.Galvanised)) k.Cylinder(new float3(0f, 0.018f, 0f), new float3(0f, 0.021f, 0f), 0.018f, k.Sides(16));
                }
            }
        }
    }
}
