using System.Collections.Generic;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Poles: the yellow grab system (35 mm tube). Two ceiling handrails along the aisle edges on hangers, vertical poles from the floor to
// the ceiling (at the aisle edges every two seat rows, at the door edges and the draught screens' free ends) with clamps and flanges,
// red stop-request push buttons on the poles, hand straps over the standing areas (doors, wheelchair bay), card validators at the
// middle and rear doors, emergency hammers on the window pillars.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        const float kPoleR = 0.0175f, kRailX = 0.33f;
        float RailY => math.min(Spec.FloorY + 1.88f, CeilY - 0.22f);
        float RailFront(float side) => HasCab ? (side == Side ? CabRearZ - 0.05f : Spec.FrontZ - 0.95f) : Spec.FrontZ - 0.40f;
        float RailRear => HasRearPlatform ? RearPlatZ + 0.05f : Spec.RearZ + 0.40f;

        void DefinePoles(MeshKit k, int lod)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                float x = s * kRailX, z0 = RailRear, z1 = RailFront(s);
                using (k.Paint(Sw.Yellow))
                    Pipe(k, new[] { new float3(x, CeilY + 0.01f, z1), new float3(x, RailY + 0.08f, z1), new float3(x, RailY, z1 - 0.08f),
                                    new float3(x, RailY, z0 + 0.08f), new float3(x, RailY + 0.08f, z0), new float3(x, CeilY + 0.01f, z0) }, kPoleR, 12, Caps.None);
                int hangers = math.max(1, (int)math.round((z1 - z0) / 1.3f));
                for (int i = 1; i < hangers; i++)
                {
                    float z = math.lerp(z0, z1, i / (float)hangers);
                    using (k.Paint(Sw.Galvanised))
                    {
                        k.Cylinder(new float3(x, RailY, z), new float3(x, CeilY, z), 0.009f, k.Sides(8), Caps.None);
                        if (lod > 0) continue;
                        k.Cylinder(new float3(x, RailY - 0.025f, z), new float3(x, RailY + 0.025f, z), kPoleR + 0.006f, k.Sides(10));
                        k.Cylinder(new float3(x, CeilY - 0.012f, z), new float3(x, CeilY + 0.002f, z), 0.035f, k.Sides(10));
                    }
                }
                foreach (float z in PoleStations(s))
                    Pole(k, new float3(x, F, z), lod);
            }
            // poles at the free ends of the draught screens
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                for (int e = 0; e < 2; e++)
                {
                    if (e == 1 && d == 0 && HasCab) continue;
                    float z = e == 0 ? door.x - .5f * door.y - DoorScreenSetback : door.x + .5f * door.y + DoorScreenSetback;
                    if (z < RearIn + 0.3f || z > Spec.FrontZ - 0.3f) continue;
                    DoorEntryRail(k, z, lod);
                }
                if (d > 0 || !HasCab) Validator(k, new float3(DoorEntryX + Side*.055f, F + 1.28f, door.x + .5f * door.y + DoorScreenSetback), lod);
            }
        }

        void DoorEntryRail(MeshKit k, float z, int lod)
        {
            float x = DoorEntryX;
            var path = new List<float3> { new float3(x + Side*.10f, F + .01f, z), new float3(x + Side*.10f, F + .62f, z) };
            int count = lod == 0 ? 8 : 4;
            for (int i = 1; i <= count; i++)
            {
                float t = i/(float)count, ease = t*t*(3-2*t);
                path.Add(new float3(x + Side*.10f*(1-ease), F + .62f + .42f*t, z));
            }
            path.Add(new float3(x, F + 1.62f, z));
            using (k.Paint(Sw.BrushedStainless))
            {
                Pipe(k, path.ToArray(), kPoleR, lod == 0 ? 12 : 8);
                if (lod > 0) return;
                float3 foot = path[0];
                k.Cylinder(foot - new float3(0,.01f,0), foot + new float3(0,.005f,0), .042f, k.Sides(14));
                k.Cylinder(new float3(x,F+1.595f,z),new float3(x,F+1.645f,z),.024f,k.Sides(12));
            }
        }

        // Pole positions along one aisle edge: every ~1.5 m, plus the door edges on the kerb side; none in the wheelchair bay.
        List<float> PoleStations(float side)
        {
            var list = new List<float>(16);
            bool kerb = side == -Side;
            float z0 = RailRear + 0.25f, z1 = RailFront(side) - 0.2f;
            float2 bay = new float2(BayZ, 1.2f);
            int n = math.max(1, (int)math.round((z1 - z0) / 1.52f));
            for (int i = 0; i <= n; i++)
            {
                float z = math.lerp(z0, z1, i / (float)n);
                if (!kerb && math.abs(z - bay.x) < 0.95f) continue;
                if (kerb && InDoor(z, 0.25f)) continue;
                list.Add(z);
            }
            if (kerb)
                for (int d = 0; d < DoorCount; d++)
                {
                    float2 door = Door(d);
                    for (int e = -1; e <= 1; e += 2)
                    {
                        float z = door.x + e * (0.5f * door.y + 0.2f);
                        if (z > z0 - 0.2f && z < z1 + 0.2f) list.Add(z);
                    }
                }
            return list;
        }

        void Pole(MeshKit k, float3 foot, int lod)
        {
            using (k.Paint(Sw.Yellow)) k.Cylinder(foot, new float3(foot.x, CeilY + 0.01f, foot.z), kPoleR, k.Sides(12), Caps.None);
            if (lod > 0) return;
            using (k.Paint(Sw.Galvanised))
            {
                k.Cylinder(foot, foot + new float3(0f, 0.012f, 0f), 0.04f, k.Sides(12), Caps.B);
                k.Cylinder(new float3(foot.x, CeilY - 0.012f, foot.z), new float3(foot.x, CeilY + 0.002f, foot.z), 0.04f, k.Sides(12), Caps.A);
                if (math.abs(math.abs(foot.x) - kRailX) < 0.01f)
                {
                    // T-clamp where the pole crosses the ceiling handrail: a sleeve on the pole and one on the rail each side, so
                    // the two tubes read as joined, not as one stuck through the other.
                    k.Cylinder(new float3(foot.x, RailY - 0.05f, foot.z), new float3(foot.x, RailY + 0.05f, foot.z), kPoleR + 0.009f, k.Sides(12));
                    k.Cylinder(new float3(foot.x, RailY, foot.z - 0.055f), new float3(foot.x, RailY, foot.z + 0.055f), kPoleR + 0.007f, k.Sides(12));
                    k.Box(new float3(foot.x, RailY, foot.z), new float3(2f * kPoleR + 0.022f, 0.012f, 0.03f));
                }
            }
            // stop-request push button, facing the aisle centre
            float3 c = new float3(foot.x, F + 1.32f, foot.z);
            float3 f = new float3(-math.sign(foot.x), 0f, 0f);
            using (k.Frame(c, f, new float3(0f, 1f, 0f)))
            {
                using (k.Paint(Sw.Red)) k.BevelBox(new float3(0f, 0f, 0.012f), new float3(0.05f, 0.085f, 0.04f), 0.01f);
                using (k.Paint(Sw.White)) k.Cylinder(new float3(0f, 0.005f, 0.03f), new float3(0f, 0.005f, 0.038f), 0.016f, 12);
                using (k.Paint(Sw.Red)) k.Cylinder(new float3(0f, 0.005f, 0.038f), new float3(0f, 0.005f, 0.043f), 0.012f, 12);
            }
        }

        // Hand straps over the standing areas along the rails (the door areas on the kerb side, the wheelchair bay): where they hang.
        // They are parts of their own (BusInterior.Riders.cs) and swing with the bus.
        System.Collections.Generic.List<float3> StrapTops()
        {
            var tops = new System.Collections.Generic.List<float3>(32);
            // Full pendulum travel, including the 1.2 per-strap factor, grip radius and pole collar.
            float angle = StrapSwingLimit * 1.2f;
            float clearance = .33f * math.sin(angle) + .07f * math.cos(angle) + kPoleR + .009f + .02f;
            for (int s = -1; s <= 1; s += 2)
            {
                bool kerb = s == -Side;
                float x = s * kRailX;
                var zones = new List<float2>();
                for (int d = 0; d < DoorCount; d++)
                {
                    if (!kerb && d != RampDoor) continue;
                    float2 door = Door(d);
                    float zc = kerb ? door.x : BayZ;
                    float lo = math.max(zc - .8f, RailRear + .1f), hi = math.min(zc + .8f, RailFront(s) - .1f);
                    if (hi > lo) zones.Add(new float2(lo, hi));
                }
                zones.Sort((a,b) => a.x.CompareTo(b.x));
                var free = new List<float2>();
                foreach (float2 zone in zones)
                {
                    if (free.Count > 0 && zone.x <= free[free.Count-1].y)
                    {
                        float2 previous = free[free.Count-1]; previous.y = math.max(previous.y, zone.y); free[free.Count-1] = previous;
                    }
                    else free.Add(zone);
                }
                foreach (float pole in PoleStations(s))
                {
                    var remaining = new List<float2>();
                    foreach (float2 zone in free)
                    {
                        if (pole + clearance <= zone.x || pole - clearance >= zone.y) { remaining.Add(zone); continue; }
                        if (pole - clearance > zone.x) remaining.Add(new float2(zone.x, pole - clearance));
                        if (pole + clearance < zone.y) remaining.Add(new float2(pole + clearance, zone.y));
                    }
                    free = remaining;
                }
                foreach (float2 zone in free)
                {
                    if (zone.y-zone.x < .06f) continue;
                    int count = 1 + (int)math.floor((zone.y-zone.x)/.34f);
                    float centre = (zone.x+zone.y)*.5f;
                    for (int i = 0; i < count; i++) tops.Add(new float3(x, RailY, centre + (i-(count-1)*.5f)*.34f));
                }
            }
            return tops;
        }

        // One strap hanging from its top on the rail (model space): the webbing and the yellow triangular handle.
        void Strap(MeshKit k, float3 top)
        {
            float3 bot = top - new float3(0f, 0.2f, 0f);
            using (k.Paint(Sw.DarkGrey)) k.Box(0.5f * (top + bot) - new float3(0f, 0.02f, 0f), new float3(0.004f, 0.2f, 0.028f));
            using (k.Paint(Sw.Yellow))
                k.Sweep(new[] { bot + new float3(0f, 0f, 0.01f), bot + new float3(0f, -0.12f, 0.06f), bot + new float3(0f, -0.12f, -0.06f), bot + new float3(0f, 0f, -0.01f) },
                    Profile.Circle(0.01f, 6), new float3(1f, 0f, 0f), Caps.None, null, true);
        }

        // The card reader at a door (the same one as on the fare box), on the pole, facing the door opening behind it.
        void Validator(MeshKit k, float3 c, int lod)
        {
            using (k.Frame(c, new float3(0f, 0f, -1f), new float3(0f, 1f, 0f)))
            using (k.At(new float3(0f, 0f, 0.05f)))
                CardReader(k, lod);
        }
    }
}
