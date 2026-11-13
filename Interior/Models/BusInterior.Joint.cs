using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// The articulation of an articulated bus (after the HUBNER HNGK joint and openOMSI's coupled sections): one gangway owned by the
// front section, between its open rear end and the rear section's open front. The turntable is a full disc on the joint's pivot
// that turns by half the fold; the bellows are BellowsCount segments from the front body's end to the rear body's, each turned
// about the joint by its share of the fold (segment k by (k + 0.5) / BellowsCount of it), so the bellows bend in a continuous
// arc: no gap opens on the outside of the bend, nothing of a section pokes through on the inside. Each segment is a U of
// concertina pleats (two side walls and the roof) with its own floor strip; neighbours overlap so the joint stays closed up to
// the stop. The joint point and the gangway's length come from the game's attach points (BusCabSystem.ArticulatedJoint);
// the sections' own ends carry only their portal frames (BusInterior.Ends.cs).
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        public const int JointPlate = 34;                  // the turntable; pivot = the joint on the floor, turns about +Y
        public const int BellowsFirst = 35;                // bellows segments, front to rear; pivot = the joint, turn about +Y
        public const int BellowsCount = 8;
        const float kBellowsOverlap = 0.08f;               // each segment reaches this far into its neighbours (closed up to ~55 deg)
        const float kBellowsEnd = 0.08f;                   // the end segments reach into the portals (behind their 8 cm posts)
        const float kTurntableR = 0.78f;

        bool HasJoint => Spec.Section == BusSection.Front && !IsAuthored;
        // Without the game's joint (a spec made by hand): 0.45 m behind the body end, a 0.9 m gangway.
        float JointAt => Spec.GangwayLength > 0f ? Spec.JointZ : Spec.RearZ - 0.45f;
        float3 JointPivot => new float3(0f, F, JointAt);
        float Gangway => Spec.GangwayLength > 0f ? math.max(0.3f, Spec.GangwayLength) : 0.9f;

        void AddJointParts(KitPartList p)
        {
            if (!HasJoint) return;
            p.Add(JointPlate, "JointPlate", Shell, JointPivot);
            for (int i = 0; i < BellowsCount; i++)
                p.Add(BellowsFirst + i, "Bellows" + i, Shell, JointPivot);
        }

        void DefineJoint(KitModelBuilder b, int lod)
        {
            if (!HasJoint)
                return;
            if (b.Building(JointPlate)) Turntable(b.Part(JointPlate), lod);
            for (int i = 0; i < BellowsCount; i++)
                if (b.Building(BellowsFirst + i)) BellowsSegment(b.Part(BellowsFirst + i), i, lod);
        }

        /// <summary>Segment i of the bellows at the straight pose: from the front body's end (Spec.RearZ) back over the gangway.</summary>
        void BellowsSegment(MeshKit k, int i, int lod)
        {
            float step = Gangway / BellowsCount;
            float z1 = Spec.RearZ - i * step + (i == 0 ? kBellowsEnd : kBellowsOverlap);
            float z0 = Spec.RearZ - (i + 1) * step - (i == BellowsCount - 1 ? kBellowsEnd : kBellowsOverlap);
            float xo = HW - 0.05f, xi = WallIn - 0.10f, yTop = Spec.RoofY - 0.10f, yIn = CeilY - 0.08f;
            int pleats = lod == 0 ? 3 : 1;
            float len = (z1 - z0) / pleats;
            for (int p = 0; p < pleats; p++)
            {
                // concertina: every other pleat stands out a little (the folds), rubber and dark alternating
                float e = ((i * pleats + p) & 1) == 0 ? 0f : 0.03f;
                float zc = z1 - (p + 0.5f) * len;
                using (k.Paint(((i * pleats + p) & 1) == 0 ? Sw.Rubber : Sw.DarkGrey))
                {
                    for (int s = -1; s <= 1; s += 2)
                        k.Box(new float3(s * 0.5f * (xi + xo + e), 0.5f * (F - 0.10f + yTop), zc), new float3(xo + e - xi, yTop - F + 0.10f, len + 0.004f));
                    k.Box(new float3(0f, 0.5f * (yIn + yTop + e), zc), new float3(2f * (xo + e), yTop + e - yIn, len + 0.004f));
                }
            }

            // the floor under the gangway (the turntable lies on it), and the side "harmonica" panels' cable ducts at the floor
            using (k.Paint(Sw.Galvanised))
                k.Box(new float3(0f, F - 0.025f, 0.5f * (z0 + z1)), new float3(2f * xi, 0.03f, z1 - z0), Faces.NoBottom);
            if (lod > 0) return;
            using (k.Paint(Sw.DarkGrey))
                for (int s = -1; s <= 1; s += 2)
                    k.Box(new float3(s * (xi - 0.05f), F + 0.06f, 0.5f * (z0 + z1)), new float3(0.10f, 0.12f, z1 - z0));
        }

        /// <summary>The turntable: a full ribbed disc with its rim on the joint, flush with the floor.</summary>
        void Turntable(MeshKit k, int lod)
        {
            float3 c = JointPivot;
            int n = k.Sides(lod == 0 ? 32 : 16);
            using (k.Paint(Sw.Galvanised))
            {
                var rim = Arc(c + new float3(0f, 0.012f, 0f), new float3(1f, 0f, 0f), new float3(0f, 0f, 1f), kTurntableR, 0f, 2f * math.PI, n);
                k.Sweep(rim, Profile.Rect(0.05f, 0.016f), new float3(0f, 1f, 0f), Caps.Both);
                int ci = k.V(c + new float3(0f, 0.006f, 0f), new float3(0f, 1f, 0f));
                for (int i = 0; i < n; i++)
                {
                    int a = k.V(rim[i] - new float3(0f, 0.006f, 0f), new float3(0f, 1f, 0f)), b2 = k.V(rim[i + 1] - new float3(0f, 0.006f, 0f), new float3(0f, 1f, 0f));
                    k.Tri(ci, a, b2, new float3(0f, 1f, 0f));
                }
            }
            if (lod > 0) return;
            // anti-slip ribs across the plate
            using (k.Paint(Sw.DarkGrey))
                for (int r = -3; r <= 3; r++)
                {
                    float z = r * 0.2f, half = math.sqrt(math.max(0f, kTurntableR * kTurntableR - z * z)) - 0.08f;
                    if (half > 0.1f)
                        k.Box(c + new float3(0f, 0.011f, z), new float3(2f * half, 0.008f, 0.025f));
                }
        }

        /// <summary>The joint's pose from the fold (radians: the rear section's heading turned about +Y from the front's).</summary>
        static void ApplyJoint(IKitPoser t, float fold, in BusInteriorSpec spec)
        {
            if (spec.Section != BusSection.Front || spec.ModelVariant != BusModelVariant.Generic || t.PartCount <= BellowsFirst + BellowsCount - 1)
                return;
            fold = math.clamp(float.IsNaN(fold) ? 0f : fold, -math.radians(60f), math.radians(60f));
            t.Pose(JointPlate, float3.zero, quaternion.RotateY(0.5f * fold));
            for (int i = 0; i < BellowsCount; i++)
                t.Pose(BellowsFirst + i, float3.zero, quaternion.RotateY(fold * (i + 0.5f) / BellowsCount));
        }
    }
}
