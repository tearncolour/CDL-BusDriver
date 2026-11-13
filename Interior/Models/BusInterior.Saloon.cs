using System.Collections.Generic;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Fittings: the passenger seating (Citaro-like layout). Each side is split into seating bands by floor height (front arch podiums,
// the low floor, the rear podiums), minus the door openings and the wheelchair bay opposite the ramp door; bands are filled with
// forward-facing double seats at 0.76 m pitch (priority seats in red moquette behind the front door, a backward-facing pair on the
// kerb-side arch podium). The rear bench spans the rear platform beside the engine tower. Also the wheelchair bay (backrest board,
// tip-up seat, rail) and the draught screens at the door edges.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        const float kSeatPitch = 0.76f;
        int PerRow => WallIn - 0.91f >= 0.24f ? 2 : 1;           // double seats when the aisle stays at least 0.48 m wide

        void DefineSeats(MeshKit k, int lod)
        {
            for (int s = -1; s <= 1; s += 2) SeatSide(k, s, lod);
            if (HasRearPlatform) RearBench(k, lod);
            WheelchairBay(k, lod);
            DoorScreens(k, lod);
        }

        // The ramp door (wheelchair entrance): the middle door, or the only one.
        int RampDoor => Spec.Section == BusSection.Rear || DoorCount == 1 ? 0 : 1;
        // The wheelchair bay on the driver side: opposite the ramp door, or behind the front arch when the ramp door is the front door.
        float BayZ => HasCab && RampDoor == 0 ? (HasFrontArch ? Spec.FrontAxleZ - kArchHalf - 1.1f : CabRearZ - 1.0f) : Door(RampDoor).x;

        void SeatSide(MeshKit k, float side, int lod)
        {
            bool kerb = side == -Side;
            float front = HasCab ? (kerb ? Spec.FrontAxleZ + kArchHalf + 0.05f : CabRearZ - 0.05f) : Spec.FrontZ - 0.7f;
            float rear = HasRearPlatform ? RearPlatZ : Spec.RearZ + 0.7f;
            float podF = HasCab ? Spec.FrontAxleZ - kArchHalf - 0.12f : front, podR = Spec.RearAxleZ + kArchHalf + 0.10f;
            var bands = new List<float3>(8);
            if (HasCab) bands.Add(new float3(podF, front, F + 0.30f));
            if (podF > podR) bands.Add(new float3(podR, podF, F));
            bands.Add(new float3(rear, math.min(podR, podF), F + 0.30f));
            // exclusions: doors (kerb side), the wheelchair bay (driver side)
            if (kerb)
                for (int d = 0; d < DoorCount; d++) { float2 door = Door(d); Cut(bands, door.x - 0.5f * door.y - 0.12f, door.x + 0.5f * door.y + 0.10f); }
            else Cut(bands, BayZ - 0.95f, BayZ + 0.95f);
            int row = 0;
            foreach (var b in bands)
            {
                if (b.y - b.x < 0.62f) continue;
                float z = b.y - 0.40f;
                bool firstBand = row == 0;
                // single seats behind the ramp door (more standing room in the low-floor middle)
                float2 rd = Door(RampDoor);
                bool singles = kerb && b.z == F && math.abs(b.y - (rd.x - 0.5f * rd.y - 0.12f)) < 0.01f;
                // kerb-side front arch podium: a backward-facing priority pair at its front, facing the next row
                if (kerb && HasCab && b.z > F && firstBand && b.y - b.x > 1.4f)
                {
                    SeatRow(k, side, b.y - 0.32f, b.z, PerRow, true, true, true, lod);
                    z = b.y - 0.32f - 1.12f;
                }
                for (; z - 0.29f >= b.x; z -= kSeatPitch, row++)
                {
                    bool accent = kerb && row < 2 && HasCab;
                    SeatRow(k, side, z, b.z, singles ? 1 : PerRow, false, accent, true, lod);
                }
                row++;
            }
        }

        // Removes [a, b] from every band (bands: x = rear z, y = front z, z = floor height).
        static void Cut(List<float3> bands, float a, float b)
        {
            for (int i = bands.Count - 1; i >= 0; i--)
            {
                float3 s = bands[i];
                if (b <= s.x || a >= s.y) continue;
                bands.RemoveAt(i);
                if (b < s.y) bands.Insert(i, new float3(b, s.y, s.z));
                if (a > s.x) bands.Insert(i + (b < s.y ? 1 : 0), new float3(s.x, a, s.z));
            }
        }

        void RearBench(MeshKit k, int lod)
        {
            float width = 2f * WallIn - 0.64f;
            int n = math.max(2, (int)math.floor(width / 0.44f));
            SeatRow(k, -Side, RearIn + 0.36f, RearPlatY, n, false, false, false, lod);
        }

        void WheelchairBay(MeshKit k, int lod)
        {
            float2 bay = new float2(BayZ, 1.2f);
            float xs = Side, xw = xs * WallIn, zb = bay.x + 0.62f;
            // backrest board at the front of the bay, facing back (the wheelchair parks against it), on two posts
            using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(xw - xs * 0.33f, F + 0.78f, zb), new float3(0.52f, 0.86f, 0.07f), 0.025f);
            using (k.Paint(Sw.Fabric)) k.BevelBox(new float3(xw - xs * 0.33f, F + 0.80f, zb - 0.045f), new float3(0.44f, 0.74f, 0.04f), 0.015f);
            using (k.Paint(Sw.Steel))
                for (int i = 0; i < 2; i++) k.Cylinder(new float3(xw - xs * (0.15f + 0.36f * i), F, zb + 0.05f), new float3(xw - xs * (0.15f + 0.36f * i), F + 0.4f, zb + 0.05f), 0.02f, k.Sides(8));
            // tip-up seat folded against the wall, the wall rail, a blue ramp-request button and the bay marking
            float zt = bay.x - 0.35f;
            using (k.Paint(Sw.Grey)) k.BevelBox(new float3(xw - xs * 0.06f, F + 0.62f, zt), new float3(0.06f, 0.42f, 0.44f), 0.02f);
            using (k.Paint(Sw.Fabric)) k.BevelBox(new float3(xw - xs * 0.095f, F + 0.62f, zt), new float3(0.012f, 0.34f, 0.36f), 0.004f);
            using (k.Paint(Sw.Yellow))
                Pipe(k, new[] { new float3(xw, F + 0.95f, zb - 0.1f), new float3(xw - xs * 0.08f, F + 0.95f, zb - 0.16f), new float3(xw - xs * 0.08f, F + 0.95f, bay.x - 0.85f), new float3(xw, F + 0.95f, bay.x - 0.91f) }, 0.016f, 10);
            if (lod > 0) return;
            using (k.Paint(Sw.Blue))
            {
                k.BevelBox(new float3(xw - xs * 0.09f, F + 1.02f, bay.x), new float3(0.03f, 0.07f, 0.06f), 0.006f);
                k.Box(new float3(xw - xs * 0.5f, F + 0.003f, bay.x - 0.1f), new float3(0.5f, 0.006f, 0.5f), Faces.YPos);
            }
            using (k.Paint(Sw.White)) k.Box(new float3(xw - xs * 0.5f, F + 0.0065f, bay.x - 0.1f), new float3(0.18f, 0.002f, 0.24f), Faces.YPos);
        }

        // Draught screens at the door edges where seats or the cab face the door: lower panel, open aluminium frame above.
        void DoorScreens(MeshKit k, int lod)
        {
            float xs = -Side, xw = xs * WallIn, len = DoorScreenReach;
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                for (int e = 0; e < 2; e++)
                {
                    if (e == 1 && (d == 0 && HasCab)) continue;           // the front door's front edge is the dash
                    float z = e == 0 ? door.x - .5f * door.y - DoorScreenSetback : door.x + .5f * door.y + DoorScreenSetback;
                    if (z < RearIn + 0.3f || z > Spec.FrontZ - 0.3f) continue;
                    float xc = xw - xs * 0.5f * len, y1 = F + 0.82f, y2 = F + 1.62f;
                    using (k.Paint(Sw.Panel)) k.BevelBox(new float3(xc, 0.5f * (F + 0.06f + y1), z), new float3(len, y1 - F - 0.06f, 0.03f), 0.01f);
                    using (k.Paint(Sw.Galvanised))
                    {
                        k.BevelBox(new float3(xc, y2, z), new float3(len, 0.03f, 0.03f), 0.008f);
                        k.BevelBox(new float3(xw - xs * 0.015f, 0.5f * (y1 + y2), z), new float3(0.03f, y2 - y1, 0.03f), 0.008f);
                        k.BevelBox(new float3(xc, y1, z), new float3(len, 0.03f, 0.035f), 0.008f);
                    }
                }
            }
        }
    }
}
