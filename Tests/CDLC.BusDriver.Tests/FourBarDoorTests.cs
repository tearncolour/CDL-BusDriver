using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The parallel four-bar door linkage (doors[].linkage[].kind "parallel_four_bar", cdlb-vehicle version 4): the solver against the trajectory of
// Tools/ZK6128H/BUSDRIVER-MOTION-INTERFACE.md, the rules of the numbers, the profile contract, and a profile carried through its mesh into the real part
// matrices of a pose. The numbers are that document's photo estimates for two single-leaf doors; they are test data, not a statement about a real coach.
static class FourBarDoorTests
{
    const string Prefix = "four-bar doors: ";
    const float Metres = 1e-5f;                  // the document asks 1 mm; floats of this size are good to a few micrometres

    static readonly float2 Arm = new float2(-.05f, .5253030303f);

    // The front door of the document: main shaft S, main joint J, balance shaft B, balance joint K (closed positions, x then z).
    static DoorLinkage Front(int drive = 34, int balance = 35) => DoorLinkage.ParallelFourBar(new float2(1.13f, 4.905f), new float2(1.08f, 5.4303030303f),
        new float2(1.17f, 4.985f), new float2(1.12f, 5.5103030303f), 157.15762745f, drive, balance);

    static DoorLinkage Middle(int drive = 36, int balance = 37) => DoorLinkage.ParallelFourBar(new float2(1.13f, -1.50f), new float2(1.08f, -.9746969697f),
        new float2(1.13f, -1.42f), new float2(1.08f, -.8946969697f), 157.15762745f, drive, balance);

    internal static void Run(Action<bool, string> check)
    {
        Trajectory(check);
        Sweep(check);
        Rejects(check);
        Profiles(check);
        Pose(check);
    }

    static bool Near(float a, float b, float tolerance) => math.abs(a - b) <= tolerance;
    static bool Near(float2 a, float2 b, float tolerance) => math.all(math.abs(a - b) <= tolerance);
    static bool Near(float3 a, float3 b, float tolerance) => math.all(math.abs(a - b) <= tolerance);

    // The samples of the document's section 6, computed there from the source model's own formula; the two doors move alike.
    static void Trajectory(Action<bool, string> check)
    {
        (float open, float degrees, float dx, float dz)[] table =
        {
            (0f, 0f, 0f, 0f), (.25f, 24.55587929f, .22282788f, -.02673127f), (.5f, 78.57881373f, .55500000f, -.37227273f),
            (.75f, 132.60174816f, .47050810f, -.84407600f), (1f, 157.15762745f, .30000000f, -.99000000f),
        };
        foreach ((string door, DoorLinkage linkage) in new[] { ("front", Front()), ("middle", Middle()) })
        {
            check(linkage.Check() == null && linkage.Kind == DoorLinkageKind.ParallelFourBar, Prefix + door + " door: its numbers make a parallelogram: " + linkage.Check());
            bool ok = true;
            foreach (var row in table)
            {
                linkage.Solve(row.open, out float shaft, out float2 joint, out float yaw, out _);
                float2 step = joint - linkage.Joint;
                ok &= Near(math.degrees(shaft), row.degrees, 1e-4f) && Near(step, new float2(row.dx, row.dz), Metres) && yaw == 0f;
            }

            check(ok, Prefix + door + " door: the source model's trajectory at 0, .25, .5, .75 and 1 open (turn, step in x and z, no yaw)");
        }
    }

    // 129 openings: the balance joint takes the same step as the main joint, both arms keep their length, the leaf never turns; the outermost point
    // of the leaf's path is the document's (0.5777 m out, not the 0.30 m of the fully open leaf); a pose depends on the opening only.
    static void Sweep(Action<bool, string> check)
    {
        DoorLinkage link = Front();
        float worstParallel = 0f, worstArm = 0f, yawSum = 0f, maxOut = float.NegativeInfinity, maxAt = 0f;
        float2 arm = link.Joint - link.Shaft, balanceArm = link.BalanceJoint - link.BalanceShaft;
        for (int i = 0; i <= 4000; i++)
        {
            float open = i / 4000f;
            link.Solve(open, out float shaft, out float2 joint, out float yaw, out float slack);
            float2 balance = link.BalanceJointAt(shaft);
            if (i % 31 == 0 || i == 4000)
            {
                worstParallel = math.max(worstParallel, math.length((balance - link.BalanceJoint) - (joint - link.Joint)));
                worstArm = math.max(worstArm, math.abs(math.length(joint - link.Shaft) - math.length(arm)) + math.abs(math.length(balance - link.BalanceShaft) - math.length(balanceArm)));
                yawSum += math.abs(yaw) + math.abs(slack);
            }

            if (joint.x - link.Joint.x > maxOut) { maxOut = joint.x - link.Joint.x; maxAt = open; }
        }

        check(worstParallel <= Metres && worstArm <= Metres, Prefix + "at 130 openings the two joints take one step and both arms keep their length (" + worstParallel + ", " + worstArm + " m)");
        check(yawSum == 0f, Prefix + "the leaf never turns and has no roller slack");
        check(Near(maxOut, .57767724f, Metres) && maxAt > .570f && maxAt < .575f, Prefix + "the outermost point is 0.5777 m out at about .572 open (" + maxOut + " m at " + maxAt + ")");

        link.Solve(.9f, out float s1, out float2 j1, out _, out _);
        link.Solve(.2f, out _, out _, out _, out _);
        link.Solve(.9f, out float s2, out float2 j2, out _, out _);
        check(s1 == s2 && j1.Equals(j2), Prefix + "a pose follows the opening alone: .9, then .2, then .9 again gives the same");
        link.Solve(-3f, out float lo, out float2 jl, out _, out _);
        link.Solve(0f, out float lo0, out float2 j0, out _, out _);
        link.Solve(7f, out float hi, out float2 jh, out _, out _);
        link.Solve(1f, out float hi1, out float2 j1x, out _, out _);
        check(lo == lo0 && jl.Equals(j0) && hi == hi1 && jh.Equals(j1x), Prefix + "an opening outside 0..1 is the nearest end (the ease is applied once)");
    }

    // The rules of the numbers: a parallelogram within 1 mm, two shafts apart, an arm long enough, a turn of 5..170 degrees, two different drive parts.
    static void Rejects(Action<bool, string> check)
    {
        DoorLinkage f = Front();
        DoorLinkage Make(float2? shaft = null, float2? joint = null, float2? balanceShaft = null, float2? balanceJoint = null, float degrees = 157.15762745f,
            int drive = 34, int balance = 35) =>
            DoorLinkage.ParallelFourBar(shaft ?? f.Shaft, joint ?? f.Joint, balanceShaft ?? f.BalanceShaft, balanceJoint ?? f.BalanceJoint, degrees, drive, balance);
        void Refused(string what, DoorLinkage linkage, string word)
        {
            string why = linkage.Check();
            check(why != null && why.Contains(word), Prefix + what + " is refused (" + why + ")");
        }

        check(Make().Check() == null, Prefix + "the document's front door is accepted");
        check(Make(balanceJoint: f.BalanceJoint + new float2(.0005f, 0f)).Check() == null, Prefix + "a balance joint 0.5 mm off the parallelogram is accepted");
        Refused("a balance joint 2 mm off the parallelogram", Make(balanceJoint: f.BalanceJoint + new float2(.002f, 0f)), "balance arm");
        Refused("an arbitrary quadrilateral (the leaf would be forced not to turn)", Make(balanceJoint: f.BalanceJoint + new float2(.05f, -.03f)), "balance arm");
        Refused("a balance arm of another length", Make(balanceJoint: f.BalanceShaft + (f.BalanceJoint - f.BalanceShaft) * 1.2f), "balance arm");
        float2 arm = f.Joint - f.Shaft;
        Refused("two shafts on the same spot", Make(balanceShaft: f.Shaft, balanceJoint: f.Joint), "2 cm apart");
        Refused("two shafts 1.9 cm apart", Make(balanceShaft: f.Shaft + new float2(.019f, 0f), balanceJoint: f.Joint + new float2(.019f, 0f)), "2 cm apart");
        check(Make(balanceShaft: f.Shaft + new float2(.021f, 0f), balanceJoint: f.Joint + new float2(.021f, 0f)).Check() == null, Prefix + "two shafts 2.1 cm apart are accepted");
        Refused("a main arm of 2 cm", Make(joint: f.Shaft + new float2(.02f, 0f), balanceJoint: f.BalanceShaft + new float2(.02f, 0f)), "3 cm");
        Refused("a joint that is not a number", Make(joint: new float2(float.NaN, 1f)), "finite");
        Refused("a shaft at infinity", Make(shaft: new float2(float.PositiveInfinity, 0f)), "finite");
        Refused("an angle that is not a number", Make(degrees: float.NaN), "finite");
        Refused("a turn of 4.9 degrees", Make(degrees: 4.9f), "5..170");
        Refused("a turn of 170.1 degrees", Make(degrees: 170.1f), "5..170");
        check(Make(degrees: 170f).Check() == null && Make(degrees: -157.15762745f).Check() == null, Prefix + "170 degrees and a negative turn (the other hand) are accepted");
        Refused("one part for both drives", Make(drive: 34, balance: 34), "two different parts");

        var roller = new DoorLinkage(new float2(1.1f, 1.7f), new float2(1.1f, 1.57f), new float2(1.1f, 1.44f), 80f, 34);
        check(roller.Kind == DoorLinkageKind.GuideRail && roller.BalanceDrivePart == -1 && roller.Check() == null,
            Prefix + "a roller linkage stays a guide-rail kind, with no balance part");
        check(math.length(arm - (f.BalanceJoint - f.BalanceShaft)) < 1e-6f, Prefix + "(the fixtures' arms are equal)");
    }

    // ---------------------------------------------------------------- the profile contract

    static readonly float2 FixtureArm = new float2(-.05f, .40f), FixtureGap = new float2(.04f, .09f);

    static JsonArray Pair(float2 v) => new JsonArray((double)Math.Round(v.x, 5), (double)Math.Round(v.y, 5));
    static float2 Read(JsonNode pair) => new float2((float)pair.AsArray()[0].GetValue<double>(), (float)pair.AsArray()[1].GetValue<double>());

    // The pack's linkage of a leaf as a four-bar on the same main shaft: the balance shaft a little aside, both arms (-0.05, 0.40) long.
    static JsonObject FourBar(JsonObject roller, int balancePart)
    {
        float2 shaft = Read(roller["shaft"]), balance = shaft + FixtureGap;
        return new JsonObject
        {
            ["kind"] = "parallel_four_bar", ["shaft"] = Pair(shaft), ["joint"] = Pair(shaft + FixtureArm), ["shaftDegrees"] = roller["shaftDegrees"].GetValue<double>(),
            ["drivePart"] = roller["drivePart"].GetValue<int>(), ["balanceShaft"] = Pair(balance), ["balanceJoint"] = Pair(balance + FixtureArm), ["balanceDrivePart"] = balancePart,
        };
    }

    // The pack's profile as a version 4 one whose every leaf is a four-bar: the drive parts 34, 35, 36 stay the main ones, 37, 38, 39 are the balance ones.
    static JsonObject Version4(string good)
    {
        JsonObject root = JsonNode.Parse(good).AsObject();
        root["version"] = 4;
        int balance = 37;
        foreach (JsonNode door in root["doors"].AsArray())
        {
            JsonArray leaves = door["linkage"].AsArray();
            for (int i = 0; i < leaves.Count; i++) leaves[i] = FourBar(leaves[i].AsObject(), balance++);
        }

        return root;
    }

    static JsonObject Leaf(JsonObject root, int door, int leaf) => root["doors"][door]["linkage"][leaf].AsObject();

    static string Refusal(string text, string dir)
    {
        try { VehicleProfileJson.Parse(text, dir, "test"); return null; }
        catch (VehicleProfileJson.Error e) { return e.Message; }
    }

    static void Profiles(Action<bool, string> check)
    {
        string good = File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G")), dir = TestPacks.Folder("SWB6710EV01G");
        check(VehicleProfileJson.Version == 4, Prefix + "this Bus Driver reads profile version 4");

        // an assembly file (sections) is an assembly at version 4 as it is at 2, and its sections read their linkages by the same version
        string k18Dir = TestPacks.Folder("CRRC_K18"), k18 = File.ReadAllText(Path.Combine(k18Dir, "CRRCK18.cdlb.json"));
        VehicleProfile two = null, four = null;
        string assemblyWhy = null;
        try
        {
            two = VehicleProfileJson.Parse(k18, k18Dir, "test");
            four = VehicleProfileJson.Parse(k18.Replace("\"version\": 2,", "\"version\": 4,"), k18Dir, "test");
        }
        catch (VehicleProfileJson.Error e) { assemblyWhy = e.Message; }
        check(assemblyWhy == null && two.Assembly != null && four.Assembly != null && four.Assembly.Front.Prefab == two.Assembly.Front.Prefab &&
              four.Assembly.Rear.Prefab == two.Assembly.Rear.Prefab, Prefix + "an assembly file of version 4 is read as an assembly, like version 2 (" + (assemblyWhy ?? "ok") + ")");

        VehicleProfile profile = VehicleProfileJson.Parse(Version4(good).ToJsonString(), dir, "test");
        bool kinds = profile.DoorLinkages != null && profile.DoorLinkages.Length == 2 && profile.DoorLinkages[0].Length == 2 && profile.DoorLinkages[1].Length == 1;
        int balanced = 0;
        for (int d = 0; kinds && d < profile.DoorLinkages.Length; d++)
            foreach (DoorLinkage l in profile.DoorLinkages[d])
                if (l.Kind == DoorLinkageKind.ParallelFourBar && l.BalanceDrivePart == 37 + balanced) balanced++;
        check(kinds && balanced == 3 && profile.DoorDriveCount == 6, Prefix + "a version 4 profile with three four-bar leaves reads, with six drive parts (" + balanced + " balanced, " + profile.DoorDriveCount + " drives)");

        // the old data, in a version 4 file: without a kind it is the roller, and its name may be written
        JsonObject old = JsonNode.Parse(good).AsObject();
        old["version"] = 4;
        VehicleProfile legacy = VehicleProfileJson.Parse(old.ToJsonString(), dir, "test");
        check(legacy.DoorLinkages[0][0].Kind == DoorLinkageKind.GuideRail && legacy.DoorDriveCount == 3, Prefix + "a roller linkage in a version 4 file is still a roller");
        foreach (JsonNode door in old["doors"].AsArray())
            foreach (JsonNode leaf in door["linkage"].AsArray()) leaf["kind"] = "guide_rail";
        check(Refusal(old.ToJsonString(), dir) == null, Prefix + "kind guide_rail may be written in version 4");

        void Refuses(string what, string text, string word)
        {
            string why = Refusal(text, dir);
            check(why != null && why.Contains(word), Prefix + "refuses " + what + " (" + (why ?? "accepted") + ")");
        }

        JsonObject v3 = Version4(good);
        v3["version"] = 3;
        Refuses("a kind in a version 3 profile", v3.ToJsonString(), "version 4");
        JsonObject v5 = Version4(good);
        v5["version"] = 5;
        Refuses("version 5", v5.ToJsonString(), "newer Bus Driver");
        JsonObject root = Version4(good);
        Leaf(root, 0, 0)["kind"] = "swing";
        Refuses("an unknown kind", root.ToJsonString(), "unknown linkage kind");
        root = Version4(good);
        Leaf(root, 0, 0)["kind"] = 5;
        Refuses("a kind that is not text", root.ToJsonString(), "unknown linkage kind");
        root = Version4(good);
        Leaf(root, 1, 0)["guide"] = Pair(new float2(1.1f, -1.2f));
        Refuses("a guide on a four-bar", root.ToJsonString(), "no guide");
        foreach (string key in new[] { "balanceShaft", "balanceJoint", "balanceDrivePart" })
        {
            root = Version4(good);
            Leaf(root, 0, 1).Remove(key);
            Refuses("a four-bar without " + key, root.ToJsonString(), key);
        }

        foreach (string key in new[] { "balanceShaft", "balanceJoint", "balanceDrivePart" })
        {
            old = JsonNode.Parse(good).AsObject();
            old["version"] = 4;
            Leaf(old, 0, 0)[key] = key == "balanceDrivePart" ? JsonValue.Create(37) : Pair(new float2(1f, 1f));
            Refuses("a roller linkage with " + key, old.ToJsonString(), key);
        }

        root = Version4(good);
        Leaf(root, 0, 0)["balanceDrivePart"] = 34;
        Refuses("one part for the main and the balance drive", root.ToJsonString(), "used twice");
        root = Version4(good);
        Leaf(root, 0, 1)["balanceDrivePart"] = 36;
        Refuses("a balance drive part another leaf uses", root.ToJsonString(), "used twice");
        root = Version4(good);
        Leaf(root, 1, 0)["balanceDrivePart"] = 99;
        Refuses("drive parts that leave a gap", root.ToJsonString(), "drive parts must be");
        root = Version4(good);
        Leaf(root, 0, 0)["balanceJoint"] = Pair(Read(Leaf(root, 0, 0)["balanceJoint"]) + new float2(.05f, .03f));
        Refuses("a quadrilateral that is not a parallelogram", root.ToJsonString(), "balance arm");
        root = Version4(good);
        Leaf(root, 0, 0)["balanceShaft"] = Leaf(root, 0, 0)["shaft"].DeepClone();
        Leaf(root, 0, 0)["balanceJoint"] = Leaf(root, 0, 0)["joint"].DeepClone();
        Refuses("two shafts on one spot", root.ToJsonString(), "2 cm apart");

        // the mesh has to agree: the pack's own mesh has three drive parts, not six
        bool refused = false;
        try { var _ = VehicleProfileJson.Parse(Version4(good).ToJsonString(), dir, "test").Mesh; }
        catch (InvalidDataException) { refused = true; }
        check(refused, Prefix + "a four-bar profile is refused with a mesh that has fewer drive parts");
    }

    // ---------------------------------------------------------------- a profile through its mesh into the part matrices of a pose

    // The pack SWB1 as an SWB2 with one more drive part for each balance shaft, after its own drive parts (the balance parts of a four-bar profile): the frame
    // of the last drive part moved to the shaft, a one-triangle mesh at every LOD. The frames after the new ones keep their parents.
    static void WithBalanceDrives(string swb1, string swb2, float2[] balanceShafts)
    {
        var reader = new SwbCodec.Reader(new GZipStream(File.OpenRead(swb1), CompressionMode.Decompress));
        if (reader.ReadUInt32() != SwbCodec.Swb1) throw new InvalidDataException(swb1 + " is not SWB1");
        int lods = reader.ReadInt32(), count = reader.ReadInt32();
        var head = new int[3 + 9 * count];
        for (int i = 0; i < head.Length; i++) head[i] = reader.ReadInt32();
        int last = -1;
        for (int i = 0; i < count; i++)
            if ((head[3 + 9 * i + 1] & VehicleMesh.DriveBit) != 0) last = i;
        int at = last + 1, extra = balanceShafts.Length;
        var frames = new List<int[]>();
        for (int i = 0; i < count; i++)
        {
            var frame = new int[9];
            Array.Copy(head, 3 + 9 * i, frame, 0, 9);
            if (frame[0] >= at) frame[0] += extra;
            frames.Add(frame);
        }

        var added = new List<int[]>();
        foreach (float2 shaft in balanceShafts)
        {
            int[] frame = (int[])frames[last].Clone();      // parent, flags, pivot x y z, rotation: only the x and the z of the shaft differ
            frame[2] = math.asint(shaft.x);
            frame[4] = math.asint(shaft.y);
            added.Add(frame);
        }

        frames.InsertRange(at, added);
        int budget = SwbCodec.MaxTriangles;
        var merge = new SwbCodec.CornerMerge();
        using (var gzip = new GZipStream(File.Create(swb2), CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(SwbCodec.Swb2); writer.Write(lods); writer.Write(count + extra);
            for (int i = 0; i < 3; i++) writer.Write(head[i]);
            foreach (int[] frame in frames)
                foreach (int value in frame) writer.Write(value);
            for (int lod = 0; lod < lods; lod++)
                for (int i = 0; i < count; i++)
                {
                    SwbCodec.Part part = SwbCodec.ReadPart(reader, SwbCodec.Swb1, ref budget, merge);
                    SwbCodec.WriteSwb2(writer, part);
                    for (int k = 0; i == last && k < extra; k++)
                        SwbCodec.WriteSwb2(writer, new SwbCodec.Part(new[] { 0f, 0f, 0f, .01f, 0f, 0f, 0f, .01f, 0f }, new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f },
                            new[] { 0, 1, 2 }, new[] { part.Swatches[0] }));
                }
        }
    }

    static float2 Turn(float2 v, double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new float2((float)(v.x * c + v.y * s), (float)(-v.x * s + v.y * c));
    }

    static double Eased(float open) { double x = Math.Min(1, Math.Max(0, open)); return x * x * (3 - 2 * x); }

    static void Pose(Action<bool, string> check)
    {
        string good = File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G"));
        VehicleProfile pack = TestPacks.Profile("SWB6710EV01G");
        string temp = Path.Combine(Path.GetTempPath(), "cdlc-fourbar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try { PoseIn(check, good, pack, temp); }
        finally { Directory.Delete(temp, true); }
    }

    static void PoseIn(Action<bool, string> check, string good, VehicleProfile pack, string temp)
    {
        JsonObject doc = Version4(good);
        var balanceShafts = new List<float2>();
        foreach (JsonNode door in doc["doors"].AsArray())
            foreach (JsonNode leaf in door["linkage"].AsArray()) balanceShafts.Add(Read(leaf["balanceShaft"]));
        string meshPath = Path.Combine(temp, "FourBarFixture.swb2.gz");
        WithBalanceDrives(pack.MeshPath, meshPath, balanceShafts.ToArray());
        doc["model"] = "FourBarFixture";
        doc["mesh"] = "FourBarFixture.swb2.gz";
        doc["meshSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(meshPath))).ToLowerInvariant();
        string profilePath = Path.Combine(temp, "FourBarFixture.cdlb.json");
        File.WriteAllText(profilePath, doc.ToJsonString());
        VehicleProfile profile = VehicleModels.Register(VehicleProfileJson.Load(profilePath));
        VehicleMesh mesh;
        try { mesh = profile.Mesh; }
        catch (Exception e) { check(false, Prefix + "the four-bar profile mesh loads and agrees with it: " + e.Message); return; }
        bool pivots = mesh.DriveCount == 6 && profile.DoorDriveCount == 6 && mesh.IsDrive(37) && mesh.IsDrive(39) && !mesh.IsDrive(40);
        for (int k = 0; k < balanceShafts.Count; k++) pivots &= Near(mesh.Pivot(37 + k).xz, balanceShafts[k], 1e-4f);
        check(pivots, Prefix + "the mesh has six drive parts, the balance ones pivoted on their shafts, and agrees with the profile");

        // a balance shaft where the mesh has none is refused
        var moved = (JsonObject)doc.DeepClone();
        Leaf(moved, 0, 0)["balanceShaft"] = Pair(balanceShafts[0] + new float2(.05f, 0f));
        Leaf(moved, 0, 0)["balanceJoint"] = Pair(Read(Leaf(moved, 0, 0)["balanceJoint"]) + new float2(.05f, 0f));
        string refusal = null;
        try { var _ = VehicleProfileJson.Parse(moved.ToJsonString(), temp, "test").Mesh; }
        catch (InvalidDataException e) { refusal = e.Message; }
        check(refusal != null && refusal.Contains("not on its shaft"), Prefix + "a balance shaft 5 cm off the pivot of its part is refused (" + (refusal ?? "accepted") + ")");

        BusInteriorSpec spec = profile.Spec;
        KitModelData data = KitBuild.BuildAll(new BusInterior(spec));
        if (data.Error != null) { check(false, Prefix + "the model builds: " + data.Error); return; }
        PoseChecks(check, profile, data, spec);
    }

    static KitPoseBuffer Posed(KitModelData data, BusInteriorSpec spec, float first, float second)
    {
        var buffer = new KitPoseBuffer(data.Parts);
        BusInterior.Pose pose = BusInterior.BoardPoseOf(0, true);
        pose.DoorOpen = new float3(first, second, 0f);
        BusInterior.Apply(buffer, pose, spec);
        return buffer;
    }

    static float3 At(KitModelData data, KitPoseBuffer buffer, int part, float3 restPoint) =>
        math.transform(buffer.PartToModel(part), math.transform(data.Parts[part].ModelToPart, restPoint));

    static bool Same(KitPoseBuffer a, KitPoseBuffer b, int part, bool exact)
    {
        float4x4 x = a.PartToModel(part), y = b.PartToModel(part);
        if (exact) return x.c0.Equals(y.c0) && x.c1.Equals(y.c1) && x.c2.Equals(y.c2) && x.c3.Equals(y.c3);
        return math.cmax(math.abs(x.c0 - y.c0)) < 1e-5f && math.cmax(math.abs(x.c1 - y.c1)) < 1e-5f && math.cmax(math.abs(x.c2 - y.c2)) < 1e-5f && math.cmax(math.abs(x.c3 - y.c3)) < 1e-5f;
    }

    // The part matrices a pose gives, against positions worked out here (own turn and ease, double precision): the leaf, both arms, the shafts.
    static void PoseChecks(Action<bool, string> check, VehicleProfile profile, KitModelData data, BusInteriorSpec spec)
    {
        float[] openings = { 0f, .1f, .25f, .5f, .5720117f, .75f, .9f, 1f };
        float worstLeaf = 0f, worstMain = 0f, worstBalance = 0f, worstJoin = 0f, worstTurn = 0f, worstPivot = 0f;
        foreach (float opening in openings)
            for (int door = 0; door < profile.DoorLinkages.Length; door++)
            {
                KitPoseBuffer buffer = door == 0 ? Posed(data, spec, opening, 0f) : Posed(data, spec, 0f, opening);
                for (int leaf = 0; leaf < profile.DoorLinkages[door].Length; leaf++)
                {
                    DoorLinkage l = profile.DoorLinkages[door][leaf];
                    int part = profile.DoorPart(door, leaf);
                    double theta = l.ShaftRadians * Eased(opening);
                    float2 mainJoint = l.Shaft + Turn(l.Joint - l.Shaft, theta), balanceJoint = l.BalanceShaft + Turn(l.BalanceJoint - l.BalanceShaft, theta);
                    float2 step = mainJoint - l.Joint;            // the step of the joint: that of every point of the leaf, and of the balance joint too
                    float3 shift = new float3(step.x, 0f, step.y), pivot = data.Parts[part].Pivot;
                    foreach (float3 witness in new[] { pivot, pivot + new float3(0f, .8f, 0f), pivot + new float3(.1f, .4f, .6f) })
                        worstLeaf = math.max(worstLeaf, math.distance(At(data, buffer, part, witness), witness + shift));
                    float y = data.Parts[l.DrivePart].Pivot.y;
                    float3 pin = new float3(l.Joint.x, y, l.Joint.y), balancePin = new float3(l.BalanceJoint.x, y, l.BalanceJoint.y);
                    float3 mainTip = At(data, buffer, l.DrivePart, pin), balanceTip = At(data, buffer, l.BalanceDrivePart, balancePin);
                    worstMain = math.max(worstMain, math.distance(mainTip, new float3(mainJoint.x, y, mainJoint.y)));
                    worstBalance = math.max(worstBalance, math.distance(balanceTip, new float3(balanceJoint.x, y, balanceJoint.y)));
                    worstJoin = math.max(worstJoin, math.max(math.distance(mainTip, At(data, buffer, part, pin)), math.distance(balanceTip, At(data, buffer, part, balancePin))));
                    float3 along = At(data, buffer, part, pivot + new float3(0f, 0f, 1f)) - At(data, buffer, part, pivot);
                    worstTurn = math.max(worstTurn, math.distance(along, new float3(0f, 0f, 1f)));
                    float3 mainShaft = math.transform(buffer.PartToModel(l.DrivePart), float3.zero), balanceShaft = math.transform(buffer.PartToModel(l.BalanceDrivePart), float3.zero);
                    worstPivot = math.max(worstPivot, math.max(math.distance(mainShaft.xz, l.Shaft), math.distance(balanceShaft.xz, l.BalanceShaft)));
                }
            }

        const float tolerance = Metres * 3f;
        check(worstLeaf <= tolerance, Prefix + "posed, three witness points of every leaf move by the step of the joint, at 8 openings (worst " + worstLeaf + " m)");
        check(worstMain <= tolerance && worstBalance <= tolerance, Prefix + "the pin of each arm is where its turned arm puts it (" + worstMain + ", " + worstBalance + " m)");
        check(worstJoin <= tolerance, Prefix + "the arms and the leaf stay joined at every opening (" + worstJoin + " m)");
        check(worstTurn <= tolerance, Prefix + "the leaf keeps its closed heading (" + worstTurn + ")");
        check(worstPivot <= tolerance, Prefix + "the fixed shafts do not move (" + worstPivot + " m)");

        var rest = new KitPoseBuffer(data.Parts);
        KitPoseBuffer shut = Posed(data, spec, 0f, 0f);
        bool atRest = true;
        foreach (int part in new[] { 4, 5, 6, 34, 35, 36, 37, 38, 39 }) atRest &= Same(shut, rest, part, false);
        check(atRest, Prefix + "shut is exactly the rest pose (the leaves and all six drive parts)");

        // each door on its own opening, and a pose carries nothing from the one before: open, shut, half, shut again
        KitPoseBuffer second = Posed(data, spec, 0f, .6f);
        check(Same(second, rest, 4, false) && !Same(second, rest, 6, false), Prefix + "the second door opens alone");
        var chained = new KitPoseBuffer(data.Parts);
        foreach (float opening in new[] { 1f, 0f, .5f, 0f })
        {
            BusInterior.Pose pose = BusInterior.BoardPoseOf(0, true);
            pose.DoorOpen = new float3(opening, opening, 0f);
            BusInterior.Apply(chained, pose, spec);
        }

        bool again = true;
        foreach (int part in new[] { 4, 5, 6, 34, 35, 36, 37, 38, 39 }) again &= Same(chained, shut, part, true);
        check(again, Prefix + "after open, shut, half and shut again the doors are exactly where a fresh shut pose has them");
    }
}
