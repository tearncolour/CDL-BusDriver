// The BYD B6 session's check of 13 November (Reports/BYD_B6_2025/20261113-cab-corrections/CabCorrectionCheck.cs), run on this tree.
using System;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

static class ReportCabCorrectionCheck
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    internal static int Run(string[] args)
    {
        VehicleProfile profile = VehicleModels.Register(VehicleProfileJson.Load(args[1]));
        Check(profile.NativeCab && profile.CardReaders.Length == 1, "Expected native B6 and one reader");
        var source = new BusInterior(profile.Spec);
        var data = KitBuild.BuildAll(source, new MeshKit());
        Check(data.Error == null, data.Error);
        int id = profile.Mesh.PartCount;
        Check(data.Parts.Length == id + 1 && data.Parts[id].Name == "CardReaders", "Reader did not get its own part");
        for (int lod = 0; lod < 3; lod++)
            Check(data.Parts[id].Tris[lod] > 0, $"Reader housing missing at LOD {lod}");
        var expectedAxis = new float3(0f, math.cos(.61f), -math.sin(.61f));
        var wheel = data.Parts[BusInterior.SteeringWheel];
        Check(math.distance(math.mul(wheel.Rot, new float3(0,0,1)), expectedAxis) < 1e-6f, "Wheel basis is not aligned");
        foreach (float degrees in new[] { -195f, -120f, -45f, 0f, 45f, 120f, 195f })
        {
            var pose = new KitPoseBuffer(data.Parts);
            BusInterior.Apply(pose, new BusInterior.Pose { Rotation = quaternion.identity, SteerDeg = degrees,
                StrapSwing = new float2(.27f, -.18f) }, source.Spec);
            var mat = pose.PartToModel(BusInterior.SteeringWheel);
            Check(math.distance(mat.c3.xyz, wheel.Pivot) < 1e-6f, "Wheel hub moved");
            Check(math.distance(mat.c2.xyz, expectedAxis) < 1e-6f, "Wheel axis moved during steering");
            var rim = math.transform(mat, new float3(.211f, 0, 0));
            Check(math.abs(math.dot(rim - wheel.Pivot, expectedAxis)) < 1e-6f, "Wheel rim left its rotation plane");
            pose.Show(BusInterior.Cab, false);
            Check(pose.Visible[id] && pose.Delta[id].Equals(quaternion.identity), "Reader hidden or moved with straps");
            Check(!pose.Delta[profile.Mesh.FirstStrap].Equals(quaternion.identity), "Straps stopped moving");
        }
        profile.NativeCab = false;
        var fallback = KitBuild.BuildAll(new BusInterior(profile.Spec), new MeshKit());
        Check(fallback.Error == null && fallback.Parts.Length == id, "Non-native cab changed its part table");
        Console.WriteLine($"PASS: seven steering angles, native reader stationary at all LODs ({string.Join(",", data.Parts[id].Tris)} triangles), runtime cab unaffected");
        return 0;
    }
}

