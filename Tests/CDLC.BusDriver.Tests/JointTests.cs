using System;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The articulated bus's gangway (BusInterior.Joint.cs): the bellows stay closed and the turntable turns by half the fold.
static class JointTests
{
    public static int Run(Action<bool, string> check)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; check(ok, "joint: " + what); }

        // A Citaro G-like front section: the body end 0.45 m ahead of the joint, a 0.9 m gangway to the rear section's end.
        var spec = BusInteriorSpec.Default;
        spec.FrontZ = 5.2f; spec.RearZ = -5.0f; spec.FrontAxleZ = 2.6f; spec.RearAxleZ = -2.4f; spec.Doors = 2; spec.Section = BusSection.Front;
        spec.JointZ = -5.45f; spec.GangwayLength = 0.9f;
        var src = new BusInterior(spec);
        KitModelData data = KitBuild.BuildAll(src);
        Check(data.Error == null, "the front section builds: " + data.Error);
        if (data.Error != null) return fails;

        Check(data.Parts[BusInterior.JointPlate].Data[0] != null, "the turntable has geometry");
        for (int i = 0; i < BusInterior.BellowsCount; i++)
            Check(data.Parts[BusInterior.BellowsFirst + i].Data[0] != null, $"bellows segment {i} has geometry");

        float s = src.Spec.HalfWidth;
        foreach (float deg in new[] { 0f, 20f, -35f, 53f, -53f })
        {
            float fold = math.radians(deg);
            var pose = BusInterior.BoardPoseOf(0, true);
            pose.JointFold = fold;
            var buffer = new KitPoseBuffer(data.Parts);
            BusInterior.Apply(buffer, pose, src.Spec);

            // The bellows' outer skin along the arc from the front body's end to the rear's, on both sides, at mid height: every
            // point inside some segment (no gap opens on the outside of the bend), the joint on the floor.
            float3 joint = new float3(0f, src.Spec.FloorY, -5.45f);
            float d1 = src.Spec.RearZ - joint.z, length = 0.9f;
            int open = 0;
            for (int side = -1; side <= 1; side += 2)
                for (int k = 0; k <= 40; k++)
                {
                    float t = k / 40f;
                    float3 local = new float3(side * (s - 0.08f), 1.4f, d1 - t * length);
                    float3 p = joint + math.rotate(quaternion.RotateY(t * fold), local);
                    if (!InsideAnySegment(data, buffer, p))
                        open++;
                }
            Check(open == 0, $"folded {deg:0} deg: the bellows are closed ({open} of 82 points on the skin open)");

            // The turntable turns by half the fold.
            float4x4 plate = buffer.PartToModel(BusInterior.JointPlate);
            float3 axis = math.mul(new float3x3(plate), new float3(0f, 0f, 1f));
            float turned = math.degrees(math.atan2(axis.x, axis.z));
            Check(math.abs(turned - 0.5f * deg) < 0.5f, $"folded {deg:0} deg: the turntable turned {turned:0.0} deg");
        }

        // The rear section has no joint parts of its own.
        var rear = BusInteriorSpec.Default;
        rear.FrontZ = 3.6f; rear.RearZ = -4.4f; rear.RearAxleZ = -0.6f; rear.Doors = 2; rear.Section = BusSection.Rear; rear.JointZ = 4.05f;
        KitModelData rearData = KitBuild.BuildAll(new BusInterior(rear));
        bool jointPart = false;
        foreach (KitPartDef part in rearData.Parts)
            jointPart |= part.Name == "JointPlate" || part.Name.StartsWith("Bellows", StringComparison.Ordinal);
        Check(rearData.Error == null && !jointPart, "the rear section draws no joint of its own (no part of it is a turntable or a bellows segment)");
        return fails;
    }

    static bool InsideAnySegment(KitModelData data, KitPoseBuffer buffer, float3 p)
    {
        for (int i = 0; i < BusInterior.BellowsCount; i++)
        {
            int part = BusInterior.BellowsFirst + i;
            KitMeshData d = data.Parts[part].Data[0];
            if (d == null) continue;
            float4x4 toPart = math.inverse(buffer.PartToModel(part));
            float3 q = math.transform(toPart, p);
            float3 min = new float3(float.MaxValue), max = new float3(float.MinValue);
            for (int v = 0; v < d.P.Length; v++)
            {
                min = math.min(min, d.P[v]);
                max = math.max(max, d.P[v]);
            }
            if (math.all(q >= min - 0.01f) && math.all(q <= max + 0.01f))
                return true;
        }
        return false;
    }
}
