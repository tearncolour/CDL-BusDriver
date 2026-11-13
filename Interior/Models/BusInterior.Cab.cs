using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Cab: the driver's raised platform (+0.34 m, yellow nosings), floor pedals, the air-suspended driver's seat (rubber bellows skirt,
// bolstered cushion and backrest, headrest on chrome posts, folding armrest, levers), the cab partition with its door (solid lower
// panel, framed open upper part, grab rail), the fare box with the card validator, the roller sun visors, the saloon and door
// mirrors, the overhead console and a fire extinguisher. Authored for the driver on the left and mirrored for Side = +1.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        float LPartX => -(Spec.HalfWidth - 1.10f);        // partition line in the left-hand authoring frame

        void DefineCab(MeshKit k, int lod)
        {
            using (k.MirrorX(Side > 0f))
            {
                Platform(k, lod);
                DriverSeat(k, lod);
                Partition(k, lod);
                FareBox(k, lod);
                CabTop(k, lod);
            }
        }

        void Platform(MeshKit k, int lod)
        {
            float fz = Spec.FrontZ, x0 = -WallIn, x1 = LPartX, z0 = CabRearZ, z1 = fz - 0.14f;
            using (k.Paint(Sw.FloorVinyl)) k.Box(new float3(0.5f * (x0 + x1), 0.5f * (F + PlatY), 0.5f * (z0 + z1)), new float3(x1 - x0, PlatY - F, z1 - z0));
            using (k.Paint(Sw.Yellow))
            {
                k.Box(new float3(x1 - 0.03f, PlatY + 0.002f, 0.5f * (z0 + z1)), new float3(0.06f, 0.006f, z1 - z0), Faces.NoBottom);
                k.Box(new float3(0.5f * (x0 + x1), PlatY + 0.002f, z0 + 0.03f), new float3(x1 - x0, 0.006f, 0.06f), Faces.NoBottom);
            }
            using (k.Paint(Sw.Galvanised)) k.Box(new float3(x1 + 0.002f, F + 0.17f, 0.5f * (z0 + z1)), new float3(0.004f, 0.3f, z1 - z0 - 0.02f));
            // floor pedals (organ type): brake (wide), accelerator (long), a footrest on the left
            float cx = LCabX, pz = fz - 0.73f;
            using (k.Paint(Sw.Rubber)) k.Box(new float3(cx, PlatY + .002f, fz - .97f), new float3(.40f, .004f, .30f));
            for (int i = 0; i < 2; i++)
            {
                float x = cx + (i == 0 ? -0.02f : 0.15f);
                float2 size = i == 0 ? new float2(0.13f, 0.19f) : new float2(0.09f, 0.27f);
                using (k.At(new float3(x, PlatY + 0.03f + 0.5f * size.y * 0.819152f, pz), quaternion.RotateX(math.radians(35f))))
                {
                    using (k.Paint(Sw.Black)) k.BevelBox(float3.zero, new float3(size.x, size.y, 0.025f), 0.006f);
                    using (k.Paint(Sw.Rubber))
                        for (int r = 0; r < 4; r++) k.Box(new float3(0f, -0.4f * size.y + r * 0.27f * size.y, -0.015f), new float3(size.x - 0.02f, 0.012f, 0.006f), Faces.All & ~Faces.ZPos);
                }
                float hingeZ = pz - 0.5f * size.y * 0.573576f;
                using (k.Paint(Sw.Steel)) k.Cylinder(new float3(x - 0.05f, PlatY + 0.025f, hingeZ), new float3(x + 0.05f, PlatY + 0.025f, hingeZ), 0.025f, k.Sides(12));
            }
            using (k.Paint(Sw.DarkGrey)) k.Box(new float3(cx - 0.23f, PlatY + 0.045f, pz - 0.03f), new float3(0.14f, 0.045f, 0.24f), quaternion.RotateX(math.radians(12f)));
        }

        void DriverSeat(MeshKit k, int lod)
        {
            float cx = LCabX, zb = Spec.FrontZ - 1.48f, yc = PlatY + 0.56f;      // zb = foot of the backrest, yc = cushion top
            // air-suspension base: pedestal, concertina skirt, the seat frame
            using (k.Paint(Sw.Black)) k.BevelBox(new float3(cx, PlatY + 0.04f, zb + 0.24f), new float3(0.44f, 0.08f, 0.5f), 0.02f);
            int folds = lod == 0 ? 6 : 1;
            using (k.Paint(Sw.Rubber))
                for (int i = 0; i < folds; i++)
                {
                    float y0 = PlatY + 0.08f + i * 0.28f / folds, e = (i & 1) == 0 ? 0f : 0.025f;
                    k.BevelBox(new float3(cx, y0 + 0.14f / folds, zb + 0.24f), new float3(0.40f - e, 0.28f / folds + 0.004f, 0.46f - e), 0.01f);
                }
            using (k.Paint(Sw.Black)) k.BevelBox(new float3(cx, PlatY + 0.40f, zb + 0.25f), new float3(0.50f, 0.06f, 0.52f), 0.02f);
            // cushion: centre pad and raised side bolsters
            using (k.Paint(Sw.SeatFabricGrey)) k.BevelBox(new float3(cx, yc - 0.06f, zb + 0.27f), new float3(0.34f, 0.12f, 0.48f), 0.04f);
            using (k.Paint(Sw.DarkGrey))
                for (int s = -1; s <= 1; s += 2) k.BevelBox(new float3(cx + s * 0.21f, yc - 0.04f, zb + 0.27f), new float3(0.09f, 0.15f, 0.5f), 0.04f);
            // backrest, leaning back 12 degrees: pad, bolsters, back shell; headrest on two posts
            using (k.At(new float3(cx, PlatY + 0.46f, zb), quaternion.RotateX(math.radians(-12f))))
            {
                using (k.Paint(Sw.SeatFabricGrey)) k.BevelBox(new float3(0f, 0.36f, 0.0f), new float3(0.36f, 0.62f, 0.10f), 0.04f);
                using (k.Paint(Sw.DarkGrey))
                    for (int s = -1; s <= 1; s += 2) k.BevelBox(new float3(s * 0.22f, 0.34f, 0.01f), new float3(0.1f, 0.62f, 0.14f), 0.045f);
                using (k.Paint(Sw.Black)) k.BevelBox(new float3(0f, 0.36f, -0.07f), new float3(0.52f, 0.66f, 0.06f), 0.03f);
                using (k.Paint(Sw.Chrome))
                    for (int s = -1; s <= 1; s += 2) k.Cylinder(new float3(s * 0.08f, 0.66f, -0.02f), new float3(s * 0.08f, 0.78f, -0.02f), 0.007f, k.Sides(8));
                using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(0f, 0.86f, -0.01f), new float3(0.30f, 0.18f, 0.11f), 0.045f);
                // folding armrest on the aisle side
                using (k.Paint(Sw.Black)) k.BevelBox(new float3(0.31f, 0.2f, 0.17f), new float3(0.07f, 0.07f, 0.34f), quaternion.RotateX(math.radians(12f)), 0.025f);
            }
            if (lod > 0) return;
            using (k.Paint(Sw.Black))                     // height / recline levers
            {
                k.BoxAlong(new float3(cx - 0.26f, PlatY + 0.40f, zb + 0.30f), new float3(cx - 0.28f, PlatY + 0.42f, zb + 0.46f), new float2(0.015f, 0.015f), new float3(0f, 1f, 0f));
                k.BoxAlong(new float3(cx - 0.26f, PlatY + 0.48f, zb + 0.02f), new float3(cx - 0.29f, PlatY + 0.6f, zb + 0.06f), new float2(0.015f, 0.015f), new float3(0f, 0f, 1f));
            }
        }

        void Partition(MeshKit k, int lod)
        {
            float px = LPartX, z0 = CabRearZ, z1 = Spec.FrontZ - 0.86f, yl = PlatY + 0.70f, yu = PlatY + 1.12f, t = 0.04f;
            // side panel (the cab door) and the rear panel behind the seat
            using (k.Paint(Sw.Panel))
            {
                k.BevelBox(new float3(px + 0.5f * t, 0.5f * (F + yl), 0.5f * (z0 + z1)), new float3(t, yl - F, z1 - z0), 0.012f);
                k.BevelBox(new float3(0.5f * (px - WallIn), 0.5f * (F + yl), z0 - 0.5f * t), new float3(WallIn + px, yl - F, t), 0.012f);
            }
            // aluminium frames of the open upper part
            using (k.Paint(Sw.Galvanised))
            {
                for (int i = 0; i < 3; i++)
                {
                    float z = i == 0 ? z0 : i == 1 ? 0.5f * (z0 + z1) : z1;
                    k.BevelBox(new float3(px + 0.02f, 0.5f * (yl + yu), z), new float3(0.035f, yu - yl, 0.035f), 0.008f);
                }
                k.BevelBox(new float3(px + 0.02f, yu, 0.5f * (z0 + z1)), new float3(0.035f, 0.035f, z1 - z0 + 0.035f), 0.008f);
                k.BevelBox(new float3(0.5f * (px - WallIn), yu, z0 - 0.02f), new float3(WallIn + px, 0.035f, 0.035f), 0.008f);
                k.BevelBox(new float3(-WallIn + 0.03f, 0.5f * (yl + yu), z0 - 0.02f), new float3(0.035f, yu - yl, 0.035f), 0.008f);
                k.BevelBox(new float3(px + 0.02f, yl + 0.015f, 0.5f * (z0 + z1)), new float3(0.06f, 0.03f, z1 - z0), 0.01f);
                k.BevelBox(new float3(0.5f * (px - WallIn), yl + 0.015f, z0 - 0.02f), new float3(WallIn + px, 0.03f, 0.06f), 0.01f);
            }
            if (lod > 0) return;
            // door hinges and handle, the yellow grab rail along the aisle side, the fire extinguisher behind the seat
            using (k.Paint(Sw.Steel))
                for (int i = 0; i < 2; i++) k.Cylinder(new float3(px + 0.045f, F + 0.25f + 0.8f * i, z0 + 0.03f), new float3(px + 0.045f, F + 0.37f + 0.8f * i, z0 + 0.03f), 0.012f, 8);
            using (k.Paint(Sw.Galvanised)) k.BoxAlong(new float3(px + 0.06f, yl - 0.12f, z1 - 0.12f), new float3(px + 0.06f, yl - 0.12f, z1 - 0.28f), new float2(0.025f, 0.02f), new float3(0f, 1f, 0f), true);
            using (k.Paint(Sw.Yellow))
                Pipe(k, new[] { new float3(px + 0.07f, yl - 0.25f, z1 - 0.06f), new float3(px + 0.07f, yl - 0.05f, z1 - 0.06f), new float3(px + 0.07f, yl - 0.05f, z0 + 0.12f), new float3(px + 0.07f, yl - 0.25f, z0 + 0.12f) }, 0.016f, 12);
            float3 fe = new float3(-WallIn + 0.18f, PlatY + 0.30f, z0 + 0.09f);
            using (k.At(fe))
            {
                using (k.Paint(Sw.Red)) k.Lathe(new[] { new LP(0f, 0f), new LP(0.06f, 0f, true), new LP(0.06f, 0.36f), new LP(0.04f, 0.42f), new LP(0f, 0.43f) }, k.Sides(14));
                using (k.Paint(Sw.Black)) k.BevelBox(new float3(0f, 0.46f, 0f), new float3(0.05f, 0.06f, 0.12f), 0.01f);
                using (k.Paint(Sw.Galvanised)) k.Box(new float3(0f, 0.25f, -0.065f), new float3(0.13f, 0.03f, 0.01f));
            }
        }
    }
}
