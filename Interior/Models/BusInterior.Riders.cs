using System.Collections.Generic;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// The people's places in the bus and the swinging hand straps.
//  * Strap parts (after the contract parts, one per strap, all drawing the first one's mesh): pivot = where the strap hangs from
//    the rail; Apply swings them by Pose.StrapSwing (the cab system's pendulum: angle about X = fore and aft, about Z = sideways),
//    each strap a little more or less (their lengths and stiffness differ).
//  * RiderSlots: every seat (the cushion point under the hips, facing the way the seat faces), the standing places (the door
//    areas under the straps, the aisle under the rails, the wheelchair bay) and the driver's seat. The cab system fills them in
//    proportion to the passengers on board (BusCabSystem.Riders.cs).
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        public enum RiderKind : byte { Seated, Holding, Standing, Driver }

        public struct RiderSlot
        {
            public float3 Position;       // the figure's origin (model space): the cushion point of a seat, the floor for standing
            public float Yaw;             // radians about +Y: 0 = facing +Z (forward)
            public RiderKind Kind;
        }

        // ---------------------------------------------------------------- straps

        /// <summary>The first strap part; the straps run from here to the model's last part.</summary>
        public const int StrapFirst = BellowsFirst + BellowsCount;     // after the joint (BusInterior.Joint.cs)
        internal const float StrapSwingLimit = .7f;
        int FirstGenericStrap => GenericDoorRig.For(Spec).FirstStrap;

        void AddStrapParts(KitPartList p)
        {
            List<float3> tops = StrapTops();
            for (int i = 0; i < tops.Count; i++)
                p.Add(FirstGenericStrap + i, "Strap" + i, Shell, tops[i], default, PartFlags.Details, i == 0 ? -1 : FirstGenericStrap);
        }

        void DefineStraps(KitModelBuilder b)
        {
            if (b.PartCount > FirstGenericStrap && b.Building(FirstGenericStrap)) Strap(b.Part(FirstGenericStrap), StrapTops()[0]);
        }

        // Each strap swings by the pendulum's angles times its own factor (0.8 .. 1.2): they do not move in lockstep.
        static void ApplyStraps(IKitPoser t, float2 swing, int first, int end)
        {
            for (int part = first; part < end; part++)
            {
                float f = 0.8f + 0.4f * math.frac((part - first) * 0.618034f);
                t.Pose(part, float3.zero, math.mul(quaternion.RotateZ(swing.y * f), quaternion.RotateX(swing.x * f)));
            }
        }

        // ---------------------------------------------------------------- rider slots

        List<float4> m_SeatSink;          // SeatRow adds every seat here while RiderSlots collects them (x, y, z, yaw)

        /// <summary>Every place for a person in this bus section: the seats, the standing places, the driver's seat (with a cab).</summary>
        public List<RiderSlot> RiderSlots()
        {
            if (IsAuthored) return AuthoredModel.RiderSlots();
            var slots = new List<RiderSlot>(64);
            m_SeatSink = new List<float4>(48);
            MeshKit skip = new MeshKit().SkipTwin(0);
            for (int s = -1; s <= 1; s += 2) SeatSide(skip, s, 0);
            if (HasRearPlatform) RearBench(skip, 0);
            foreach (float4 seat in m_SeatSink)
                slots.Add(new RiderSlot { Position = seat.xyz, Yaw = seat.w, Kind = RiderKind.Seated });
            m_SeatSink = null;

            float kerb = -Side;
            var stands = new List<float3>(32);                      // x, z, yaw
            // the door areas: in front of each kerb-side door under the straps, clear of the leaves' swing
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                if (HasCab && d == 0) continue;                     // the front door: the entrance by the driver stays clear
                for (int i = -1; i <= 1; i++)
                    stands.Add(new float3(kerb * 0.30f, door.x + 0.42f * i, i == 0 ? kerb * -0.5f * math.PI : (i < 0 ? 0f : math.PI)));
            }
            // the wheelchair bay: two places by the wall rail
            stands.Add(new float3(Side * 0.55f, BayZ - 0.35f, 0f));
            stands.Add(new float3(Side * 0.55f, BayZ + 0.25f, math.PI));
            // the aisle: in single file under the rails, out of the door areas and the bay
            float a0 = HasRearPlatform ? RearPlatZ + 0.45f : Spec.RearZ + 0.8f, a1 = HasCab ? CabRearZ - 0.45f : Spec.FrontZ - 0.8f;
            int n = 0;
            for (float z = a1; z >= a0; z -= 0.72f)
            {
                bool clear = math.abs(z - BayZ) > 0.7f;
                for (int d = 0; d < DoorCount && clear; d++)
                {
                    float2 door = Door(d);
                    if (math.abs(z - door.x) < 0.5f * door.y + 0.25f) clear = false;
                }
                if (!clear) continue;
                stands.Add(new float3((n & 1) == 0 ? 0.05f : -0.05f, z, (n & 1) == 0 ? 0f : math.PI));
                n++;
            }
            foreach (float3 st in stands)
            {
                // not on top of a seat (the rear bench by the rear door) or of another standing place
                bool free = true;
                foreach (RiderSlot other in slots)
                    free &= math.distance(other.Position.xz, st.xy) >= 0.45f;
                if (!free) continue;
                // holding: the right hand up to the rail on that side, or to a strap above (door areas); hands down in the bay
                bool bay = math.abs(st.y - BayZ) < 0.7f && math.sign(st.x) == math.sign(Side);
                slots.Add(new RiderSlot { Position = new float3(st.x, F, st.y), Yaw = st.z, Kind = bay ? RiderKind.Standing : RiderKind.Holding });
            }
            if (HasCab)
                slots.Add(new RiderSlot { Position = DriverSeatPoint, Yaw = 0f, Kind = RiderKind.Driver });
            return slots;
        }

        // The driver's cushion point: on the air-suspended seat (DriverSeat), under the hips.
        float3 DriverSeatPoint => new float3(CabX, PlatY + 0.56f, Spec.FrontZ - 1.48f + 0.22f);

        /// <summary>The rider slots of a bus section (a new model of the spec; main thread or any).</summary>
        public static List<RiderSlot> RiderSlotsOf(in BusInteriorSpec spec) => new BusInterior(spec).RiderSlots();
    }
}
