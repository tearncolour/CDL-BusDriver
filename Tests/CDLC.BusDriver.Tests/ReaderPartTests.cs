using System;
using System.IO;
using System.Linq;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The mod's card reader housing on a native-cab bus (the BYD B6 session's report of 13 November,
// Reports/BYD_B6_2025/20261113-cab-corrections): it was built into the runtime Cab part, which the native cab hides, while the cab
// drops the game asset's own triangles inside the housing: neither drew it, and only the readers' lights showed by the door. A
// native-cab model with readers now has the housing in a part of its own after the mesh's parts; every other model is as before.
static class ReaderPartTests
{
    const string Prefix = "reader part: ";

    internal static void Run(Action<bool, string> check)
    {
        RuntimeCab(check);
        NativeCab(check);
    }

    static int Triangles(KitModelData data, int part, int lod) => data.Parts[part].Tris[lod];

    // A pack whose cab the runtime mesh draws: the readers stay in the Cab part and the part table is the mesh's.
    static void RuntimeCab(Action<bool, string> check)
    {
        VehicleProfile runtime = TestPacks.Profile("SWB6710EV01G");
        check(!runtime.NativeCab && runtime.CardReaders.Length > 0 && BusInterior.CardReaderPart(runtime) == BusInterior.Cab,
            Prefix + "a runtime-cab pack with readers builds them into the Cab part");
        KitModelData data = KitBuild.BuildAll(new BusInterior(runtime.Spec.Snapped()), new MeshKit());
        check(data.Error == null && data.Parts.Length == runtime.Mesh.PartCount, Prefix + "and its part table is the mesh's, none added");
        check(BusInterior.CardReaderPart(TestPacks.Profile("WG6980A")) == -1 && BusInterior.CardReaderPart(null) == -1,
            Prefix + "a model without readers (the WG6980A), or no model, has no reader part");
    }

    // The installed native-cab packs: the B6 (one reader), and whichever others are there.
    static void NativeCab(Action<bool, string> check)
    {
        string mods = Environment.GetEnvironmentVariable("CSII_LOCALMODSPATH");
        string b6 = string.IsNullOrEmpty(mods) ? null : Path.Combine(mods, "CDLC.Bus.BYDB62025Rebuild", "BYDB62025Rebuild.cdlb.json");
        if (b6 == null || !File.Exists(b6))
        {
            Console.WriteLine("  (reader part: the BYD B6 pack is not installed: its checks are skipped)");
            return;
        }
        VehicleProfile profile = VehicleModels.Register(VehicleProfileJson.Load(b6));
        VehicleMesh mesh = profile.Mesh;
        int reader = mesh.PartCount;
        check(profile.NativeCab && profile.CardReaders.Length == 1 && BusInterior.CardReaderPart(profile) == reader,
            Prefix + "the B6 (native cab, one reader) has its reader part after the mesh's parts");

        var bus = new BusInterior(profile.Spec.Snapped());
        KitModelData data = KitBuild.BuildAll(bus, new MeshKit());
        check(data.Error == null, Prefix + "the B6's model builds " + data.Error);
        if (data.Error != null) return;
        check(data.Parts.Length == reader + 1 && data.Parts[reader].Name == "CardReaders" && data.Parts[reader].Parent == BusInterior.Shell,
            Prefix + "one part is added, CardReaders, under the shell (the model frame the reader points are in)");
        check(Triangles(data, reader, 0) > 0 && Triangles(data, reader, 1) > 0 && Triangles(data, reader, 2) > 0,
            Prefix + $"the housing is there at all three LODs ({Triangles(data, reader, 0)}, {Triangles(data, reader, 1)}, {Triangles(data, reader, 2)} triangles)");

        // the housing is where the profile puts the reader, and the Cab part no longer holds a second one
        (float3 origin, quaternion frame, float _) = profile.CardReaders[0];
        KitMeshData housing = data.Parts[reader].Data[0];
        float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
        quaternion inverse = math.inverse(frame);
        foreach (float3 p in housing.P)
        {
            float3 local = math.mul(inverse, p - origin);
            lo = math.min(lo, local);
            hi = math.max(hi, local);
        }
        check(math.all(lo >= new float3(-0.095f, -0.145f, -0.055f)) && math.all(hi <= new float3(0.095f, 0.145f, 0.06f)),
            Prefix + $"the housing lies in the reader's box about the profile's reader point (local {lo.x:0.000}..{hi.x:0.000}, {lo.y:0.000}..{hi.y:0.000}, {lo.z:0.000}..{hi.z:0.000} m)");

        // the same model built for a runtime cab: the Cab part is larger by exactly the housing
        profile.NativeCab = false;
        KitModelData fallback;
        try { fallback = KitBuild.BuildAll(new BusInterior(profile.Spec.Snapped()), new MeshKit()); }
        finally { profile.NativeCab = true; }
        check(fallback.Error == null && fallback.Parts.Length == reader, Prefix + "the same pack with a runtime cab has the mesh's part table");
        // Cab loses exactly what it held of the housing: all of it at LOD0 and LOD1. At LOD2 a runtime cab drops the housing with
        // the other small things of the Cab part; in a part of its own it is kept (24 triangles: a box), so it does not vanish with distance.
        int[] left = Enumerable.Range(0, 3).Select(lod => Triangles(fallback, BusInterior.Cab, lod) - Triangles(data, BusInterior.Cab, lod)).ToArray();
        check(left[0] == Triangles(data, reader, 0) && left[1] == Triangles(data, reader, 1),
            Prefix + $"at LOD0 and LOD1 the Cab part is smaller by exactly the reader part's triangles ({left[0]} and {left[1]}): one housing, never two");
        check(left[2] == 0 && Triangles(data, reader, 2) == Triangles(data, reader, 1),
            Prefix + $"at LOD2 the runtime Cab part had no housing to lose ({left[2]}); the reader part keeps its LOD1 box ({Triangles(data, reader, 2)} triangles)");
        bool others = true;
        for (int part = 0; part < reader; part++)
            if (part != BusInterior.Cab)
                for (int lod = 0; lod < 3; lod++)
                    others &= Triangles(fallback, part, lod) == Triangles(data, part, lod);
        check(others, Prefix + "no other part changed");

        // posed: hidden with the parts the native cab replaces, the reader part stays shown and still, the straps swing
        var pose = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(pose, new BusInterior.Pose { Rotation = quaternion.identity, StrapSwing = new float2(0.27f, -0.18f) }, bus.Spec);
        foreach (int part in NativeCabOwnership.ReplacedParts(mesh)) pose.Show(part, false);
        check(!pose.Visible[BusInterior.Cab] && pose.Visible[reader], Prefix + "with the parts the native cab replaces hidden, the reader part is still shown");
        check(pose.Delta[reader].Equals(quaternion.identity) && pose.Offset[reader].Equals(float3.zero), Prefix + "the swing of the hand straps does not move the reader");
        bool swing = mesh.FirstStrap < mesh.PartCount;
        for (int part = mesh.FirstStrap; part < mesh.PartCount; part++) swing &= !pose.Delta[part].Equals(quaternion.identity);
        check(swing, Prefix + $"every hand strap still swings ({mesh.PartCount - mesh.FirstStrap} straps)");

        // the native cab's own lists do not reach the added part: it is neither replaced nor a moving part whose copy is dropped
        check(!NativeCabOwnership.ReplacedParts(mesh).Contains(reader) && !NativeCabOwnership.MovingParts(mesh).Contains(reader),
            Prefix + "the native cab neither hides the reader part nor matches the asset against it (the asset's reader is dropped by its housing box, as before)");
    }
}
