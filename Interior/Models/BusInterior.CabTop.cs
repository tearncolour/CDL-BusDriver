using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Cab, continued: the stainless fare box (coins through a clear window) with the card reader at the head of the aisle, the roller
// sun visors, the saloon mirror, the convex door mirror and the overhead console (radio, switches, destination-sign keypad,
// speaker). Left-hand authoring frame. The card reader (CardReader) is the Chinese kind: a colour screen, the tap zone for the IC
// card and NFC phones, the QR code scanner window under it; the same reader stands at the other doors (Poles.cs).
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        void FareBox(MeshKit k, int lod)
        {
            float x = LPartX + 0.24f, z = Spec.FrontZ - 1.08f;
            using (k.Paint(Sw.Galvanised))
            {
                k.BevelBox(new float3(x, F + 0.40f, z), new float3(0.28f, 0.80f, 0.26f), 0.02f);
                k.BevelBox(new float3(x, F + 0.03f, z), new float3(0.34f, 0.06f, 0.32f), 0.01f);
            }
            // the sloping top: a dark frame round the clear coin window (the coins lie on the glass floor inside), the slot
            quaternion slope = quaternion.RotateZ(math.radians(-10f));
            using (k.Paint(Sw.Black)) k.BevelBox(new float3(x, F + 0.84f, z), new float3(0.30f, 0.08f, 0.28f), slope, 0.02f);
            if (lod == 0)
            {
                using (k.Paint(Sw.Glass)) k.Box(new float3(x + 0.02f, F + 0.885f, z), new float3(0.2f, 0.01f, 0.2f), slope);
                using (k.Paint(Sw.Steel)) k.Box(new float3(x - 0.1f, F + 0.892f, z), new float3(0.03f, 0.01f, 0.12f), slope);   // coin slot plate
                using (k.Paint(Sw.Black)) k.Box(new float3(x - 0.1f, F + 0.894f, z), new float3(0.006f, 0.01f, 0.08f), slope);  // the slot
                using (k.Paint(Sw.Steel)) k.Box(new float3(x + 0.141f, F + 0.6f, z), new float3(0.004f, 0.12f, 0.16f), Faces.XPos);   // cash drawer
            }
            // the card reader on a short post, turned towards the entrance
            using (k.Paint(Sw.Galvanised)) k.Cylinder(new float3(x - 0.08f, F + 0.86f, z - 0.06f), new float3(x - 0.08f, F + 0.94f, z - 0.06f), 0.018f, k.Sides(10));
            using (k.At(new float3(x - 0.06f, F + 1.07f, z - 0.04f), math.mul(quaternion.RotateY(math.radians(60f)), quaternion.RotateX(math.radians(15f)))))
                CardReader(k, lod);
        }

        /// <summary>
        /// Every card reader of the bus (model space): its origin (the CardReader frame's origin), the frame's rotation (local +Z =
        /// out of the face, +Y = up the face) and the x sign of the authored layout in that frame (-1 where the cab is mirrored).
        /// The cab system lights them (CardReaderLights). The fare box's reader first.
        /// </summary>
        public static System.Collections.Generic.List<(float3 origin, quaternion frame, float xSign)> CardReaders(in BusInteriorSpec s)
        {
            var bus = new BusInterior(s.Snapped());
            var list = new System.Collections.Generic.List<(float3, quaternion, float)>();
            if (bus.IsAuthored)
            {
                list.AddRange(bus.AuthoredModel.CardReaders);
                return list;
            }
            if (bus.HasCab)
            {
                float m = bus.Side > 0f ? -1f : 1f;                 // DefineCab mirrors the left-hand authoring for Side = +1
                float x = bus.LPartX + 0.24f - 0.06f, z = bus.Spec.FrontZ - 1.08f - 0.04f;
                quaternion r = math.mul(quaternion.RotateY(math.radians(60f * m)), quaternion.RotateX(math.radians(15f)));
                list.Add((new float3(m * x, bus.F + 1.07f, z), r, m));
            }
            for (int d = 0; d < bus.DoorCount; d++)
            {
                if (d == 0 && bus.HasCab) continue;
                float2 door = bus.Door(d);
                float3 c = new float3(bus.DoorEntryX + bus.Side*.055f, bus.F + 1.28f, door.x + .5f * door.y + DoorScreenSetback);
                list.Add((c + new float3(0f, 0f, -0.05f), quaternion.RotateY(math.PI), 1f));      // Validator: facing -Z
            }
            return list;
        }

        /// <summary>
        /// The card reader: a white housing, the face (local +Z, 4.5 cm in front of the origin) with a colour screen on top (a
        /// blue title bar, the fare and the balance), the tap zone for the IC card and NFC phones (a card with the contactless
        /// waves), the QR code scanner's dark window with its red aiming light, the green / red result lamps and the speaker.
        /// </summary>
        internal static void CardReader(MeshKit k, int lod)
        {
            using (k.Paint(Sw.SafetyWhite)) k.BevelBox(float3.zero, new float3(0.17f, 0.27f, 0.09f), 0.02f);
            using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(0f, 0f, 0.042f), new float3(0.15f, 0.25f, 0.008f), 0.008f);
            if (lod > 0) return;
            // screen
            // Keep the original front planes, but close each thin faceplate so
            // grazing views beside the rounded housing see its real edge/back.
            using (k.Paint(Sw.Screen)) k.Box(new float3(0f, 0.075f, 0.0465f), new float3(0.12f, 0.08f, 0.002f));
            using (k.Paint(Sw.Blue)) k.Box(new float3(0f, 0.105f, 0.048f), new float3(0.12f, 0.018f, 0.001f));
            using (k.Paint(Sw.White))
            {
                k.Box(new float3(-0.01f, 0.077f, 0.048f), new float3(0.08f, 0.012f, 0.001f));
                k.Box(new float3(-0.02f, 0.054f, 0.048f), new float3(0.06f, 0.008f, 0.001f));
            }
            // tap zone: the card and the waves
            using (k.Paint(Sw.Grey)) k.BevelBox(new float3(0f, -0.025f, 0.0465f), new float3(0.12f, 0.08f, 0.003f), 0.01f);
            using (k.Paint(Sw.Blue)) k.Box(new float3(-0.022f, -0.025f, 0.0485f), new float3(0.045f, 0.03f, 0.001f));
            using (k.Paint(Sw.White))
                for (int i = 0; i < 3; i++)
                    k.Box(new float3(0.012f + 0.012f * i, -0.025f, 0.0485f), new float3(0.004f, 0.016f + 0.01f * i, 0.001f));
            // QR code scanner window with the red aiming line, the result lamps, the speaker slots
            using (k.Paint(Sw.Black)) k.Box(new float3(0f, -0.09f, 0.0465f), new float3(0.09f, 0.035f, 0.002f));
            using (k.Paint(Sw.Red)) k.Box(new float3(0f, -0.09f, 0.048f), new float3(0.06f, 0.003f, 0.001f));
            using (k.Paint(Sw.Green)) k.Box(new float3(0.058f, -0.09f, 0.0475f), new float3(0.01f, 0.01f, 0.001f));
            using (k.Paint(Sw.Red)) k.Box(new float3(0.058f, -0.075f, 0.0475f), new float3(0.01f, 0.01f, 0.001f));
            using (k.Paint(Sw.Black))
                for (int i = 0; i < 3; i++)
                    k.Box(new float3(-0.058f, -0.08f - 0.008f * i, 0.0475f), new float3(0.016f, 0.003f, 0.001f));
        }

        void CabTop(MeshKit k, int lod)
        {
            float fz = Spec.FrontZ, cx = LCabX;
            // roller sun visors: the driver's blind pulled down a little, the door-side one rolled up
            float vy = WsTopY - 0.035f, vz = WsZ(WsTopY) - 0.075f;
            for (int i = 0; i < 2; i++)
            {
                float x0 = i == 0 ? -WsHalf + 0.03f : cx + 0.52f, x1 = i == 0 ? cx + 0.46f : WsHalf - 0.03f;
                using (k.Paint(Sw.DashBlack)) k.Cylinder(new float3(x0, vy, vz), new float3(x1, vy, vz), 0.032f, k.Sides(12));
                float drop = i == 0 ? 0.10f : 0.01f;
                using (k.Paint(Sw.DarkGrey)) k.Box(new float3(0.5f * (x0 + x1), vy - 0.5f * drop - 0.02f, vz + 0.012f), new float3(x1 - x0 - 0.06f, drop, 0.004f));
                using (k.Paint(Sw.Galvanised)) k.BevelBox(new float3(0.5f * (x0 + x1), vy - drop - 0.03f, vz + 0.012f), new float3(x1 - x0 - 0.05f, 0.02f, 0.016f), 0.004f);
                if (lod == 0)
                    using (k.Paint(Sw.Black)) k.BevelBox(new float3(0.5f * (x0 + x1), vy - drop - 0.055f, vz + 0.01f), new float3(0.06f, 0.03f, 0.014f), 0.005f);
            }
            // overhead console above the driver: sloped underside facing the driver
            float cz0 = fz - 0.40f, cz1 = fz - 0.82f, cy0 = WsTopY - 0.02f, cy1 = CeilY - 0.11f;
            var outline = new[] { new float2(cz0, CeilY + 0.02f), new float2(cz0, cy0), new float2(cz1, cy1), new float2(cz1, CeilY + 0.02f) };
            using (k.Paint(Sw.DashBlack)) k.ExtrudeX(new Profile(outline), -WallIn - 0.02f, cx + 0.48f, Caps.Both);
            float3 a = new float3(0f, cy0, cz0), b = new float3(0f, cy1, cz1), up = math.normalize(a - b);
            float3 nrm = math.normalize(math.cross(new float3(1f, 0f, 0f), up));
            if (nrm.y > 0f) nrm = -nrm;
            if (lod == 0)
                using (k.Frame(new float3(cx, 0.5f * (cy0 + cy1), 0.5f * (cz0 + cz1)) + nrm * 0.002f, nrm, up))
                {
                    using (k.Paint(Sw.Screen)) k.BevelBox(new float3(0f, 0.04f, 0.006f), new float3(0.19f, 0.06f, 0.012f), 0.004f);         // radio
                    using (k.Paint(Sw.LedAmber)) k.Box(new float3(0.02f, 0.045f, 0.0125f), new float3(0.08f, 0.015f, 0.001f), Faces.ZPos);
                    using (k.Paint(Sw.DarkGrey))
                        for (int i = 0; i < 4; i++) k.Cylinder(new float3(-0.075f + 0.05f * i, -0.04f, 0f), new float3(-0.075f + 0.05f * i, -0.04f, 0.014f), 0.009f, 10);
                    using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(0.30f, 0.0f, 0.008f), new float3(0.16f, 0.12f, 0.016f), 0.004f);  // sign keypad
                    using (k.Paint(Sw.Grey))
                        for (int i = 0; i < 12; i++) k.Box(new float3(0.255f + 0.03f * (i % 4), -0.035f + 0.025f * (i / 4), 0.017f), new float3(0.02f, 0.016f, 0.004f), Faces.ZPos);
                    using (k.Paint(Sw.LedAmber)) k.Box(new float3(0.30f, 0.045f, 0.017f), new float3(0.12f, 0.018f, 0.001f), Faces.ZPos);
                    using (k.Paint(Sw.Black, 0.5f)) k.Disc(new float3(-0.3f, 0f, 0.002f), new float3(0f, 0f, 1f), 0.055f, k.Sides(16));       // speaker
                    using (k.Paint(Sw.DarkGrey)) k.Tube(new float3(-0.3f, 0f, 0f), new float3(-0.3f, 0f, 0.008f), 0.062f, 0.055f, k.Sides(16));
                    for (int i = 0; i < 6; i++) Rocker(k, new float3(0.07f + 0.035f * (i % 3) - 0.24f, 0.04f - 0.06f * (i / 3), 0.006f), i % 4);
                }
            // saloon mirror (wide convex) on an arm under the destination box, looking back into the saloon
            // The arm runs from inside the mirror housing into the destination box (which starts at fz - 0.40, WsTopY + 0.02):
            // both ends buried, so the joints never show a gap.
            float3 mc = new float3(cx + 0.95f, WsTopY - 0.20f, fz - 0.55f);
            using (k.Paint(Sw.Black)) k.BoxAlong(mc + new float3(0f, 0.02f, 0f), new float3(mc.x, WsTopY + 0.06f, fz - 0.37f), new float2(0.025f, 0.02f), new float3(1f, 0f, 0f));
            using (k.Paint(Sw.Black)) k.Cylinder(mc + new float3(0f, 0.045f, 0.005f), mc + new float3(0f, 0.075f, 0.012f), 0.022f, k.Sides(10));
            using (k.At(mc, quaternion.RotateX(math.radians(10f))))
            {
                using (k.Paint(Sw.Black)) k.BevelBox(float3.zero, new float3(0.40f, 0.13f, 0.035f), 0.012f);
                using (k.Paint(Sw.Chrome)) k.Box(new float3(0f, 0f, -0.018f), new float3(0.37f, 0.105f, 0.004f), Faces.ZNeg);
            }
            // convex door mirror above the front entrance
            float3 dm = new float3(WallIn - .18f, WinTop - .04f, fz - .22f);
            using (k.Paint(Sw.Black)) k.BoxAlong(dm + new float3(.03f, .03f, 0f), new float3(WallIn + .03f, WinTop + .06f, fz - .22f), new float2(.02f, .02f), new float3(0f, 0f, 1f));
            using (k.AxisY(dm, new float3(-0.5f, -0.6f, -0.6f)))
            {
                using (k.Paint(Sw.Black)) k.Cylinder(new float3(0f, -0.02f, 0f), new float3(0f, 0.012f, 0f), 0.1f, k.Sides(16));
                using (k.Paint(Sw.Chrome)) k.Lathe(new[] { new LP(0.088f, 0.01f, true), new LP(0.06f, 0.022f), new LP(0f, 0.028f) }, k.Sides(16));
            }
        }
    }
}
