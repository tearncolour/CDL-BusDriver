using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// Vehicle packs: the profile reader (VehicleProfileJson) and the packs the build makes (Packs/CDLC.Bus.<model>/).
static class VehiclePackTests
{
    const string Prefix = "vehicle packs: ";

    internal static void Run(Action<bool, string> check)
    {
        foreach (string model in new[] { "SWB6710EV01G", "SWB6109EV12G" })
        {
            VehicleProfile pack = TestPacks.Profile(model);
            check(pack.Spec.ModelVariant != BusModelVariant.Generic && VehicleModels.Find(pack.Spec.ModelVariant) == pack,
                Prefix + model + " pack registers with its own variant");
            check(pack.Name == model && pack.MeshSha256 != null && pack.Key.StartsWith("pack-" + model.ToLowerInvariant() + "-"),
                Prefix + model + " pack names its model and mesh hash (cache key " + pack.Key + ")");
            string tool = Path.Combine(Repo.Root, "Tools", model, "vehicle.json");
            VehicleProfile source = VehicleProfileJson.Load(tool, requireMesh: false);
            check(Same(pack, source, out string diff), Prefix + model + " pack profile is the tool's vehicle.json (" + diff + ")");
            var data = KitBuild.BuildAll(new BusInterior(pack.Spec));
            check(data.Error == null && data.Parts.Length >= BusInterior.PartCount, Prefix + model + " pack mesh builds (" + data.Error + ")");
        }
        // (0.7.0: both vehicle.json files were written from the former C# profiles and checked to read back bit for bit; the
        // 255 reference renders of both buses and the generic one were byte-identical before and after the move.)
        Rejects(check);
        VehicleAssemblyTests.Run(check);
        PackIdentity(check);
        check(new BusInterior().AuthoredModel == null && VehicleModels.Find(BusModelVariant.Generic) == null, Prefix + "the generic bus has no profile");
    }

    // Every field bit for bit (floats compared as their bits, so -0 / NaN cannot hide).
    internal static bool Same(VehicleProfile a, VehicleProfile b, out string diff)
    {
        diff = "same";
        BusInteriorSpec x = a.Spec, y = b.Spec;
        if (!Eq(x.FrontZ, y.FrontZ) || !Eq(x.RearZ, y.RearZ) || !Eq(x.HalfWidth, y.HalfWidth) || !Eq(x.RoofY, y.RoofY) || !Eq(x.FloorY, y.FloorY) ||
            !Eq(x.FrontAxleZ, y.FrontAxleZ) || !Eq(x.RearAxleZ, y.RearAxleZ) || x.Doors != y.Doors || !Eq(x.DriverSide, y.DriverSide) ||
            x.Section != y.Section || !Eq(x.JointZ, y.JointZ) || !Eq(x.GangwayLength, y.GangwayLength)) { diff = "spec"; return false; }
        if (a.Name != b.Name) { diff = "name"; return false; }
        if (!Eq(a.WheelRadius, b.WheelRadius) || !Eq(a.WrapBottom, b.WrapBottom) || !Eq(a.WrapThresholds, b.WrapThresholds) ||
            !Eq(a.DefaultBodyColor, b.DefaultBodyColor)) { diff = "wheel/wrap/colour"; return false; }
        if (a.NextStopEnabled != b.NextStopEnabled || !Eq(a.NextStopCentre, b.NextStopCentre) || !Eq(a.NextStopSize, b.NextStopSize) || !a.NextStopMatrix.Equals(b.NextStopMatrix)) { diff = "next stop"; return false; }
        if (a.Doors.Length != b.Doors.Length) { diff = "door count"; return false; }
        for (int i = 0; i < a.Doors.Length; i++)
        {
            if (!Eq(a.Doors[i], b.Doors[i]) || a.DoorLeaves[i] != b.DoorLeaves[i] ||
                !Eq(a.DoorOpenDegrees?[i] ?? BusInterior.DoorOpenDeg,b.DoorOpenDegrees?[i] ?? BusInterior.DoorOpenDeg)) { diff = "door " + i; return false; }
            VehicleProfile.DoorTiming ta = a.Timing(i), tb = b.Timing(i);
            if (!Eq(ta.OpenSeconds, tb.OpenSeconds) || !Eq(ta.CloseSeconds, tb.CloseSeconds) || !Eq(ta.CloseDelay, tb.CloseDelay)) { diff = "door " + i + " timing"; return false; }
            DoorLinkage[] la = a.DoorLinkages?[i], lb = b.DoorLinkages?[i];
            if ((la == null) != (lb == null) || la != null && la.Length != lb.Length) { diff = "door " + i + " linkage"; return false; }
            for (int k = 0; la != null && k < la.Length; k++)
                if (!Eq(la[k].Shaft, lb[k].Shaft) || !Eq(la[k].Joint, lb[k].Joint) || !Eq(la[k].Guide, lb[k].Guide) ||
                    !Eq(la[k].ShaftRadians, lb[k].ShaftRadians) || la[k].DrivePart != lb[k].DrivePart || la[k].Kind != lb[k].Kind ||
                    !Eq(la[k].BalanceShaft, lb[k].BalanceShaft) || !Eq(la[k].BalanceJoint, lb[k].BalanceJoint) || la[k].BalanceDrivePart != lb[k].BalanceDrivePart)
                { diff = "door " + i + " linkage " + k; return false; }
        }
        if (a.DoorDriveCount != b.DoorDriveCount) { diff = "door drives"; return false; }
        if (!Eq(a.FrontSign, b.FrontSign)) { diff = "front sign"; return false; }
        if (!Eq(a.SideSign, b.SideSign)) { diff = "side sign"; return false; }
        if (!Eq(a.RearSign, b.RearSign)) { diff = "rear sign"; return false; }
        if (a.CardReaders.Length != b.CardReaders.Length) { diff = "reader count"; return false; }
        for (int i = 0; i < a.CardReaders.Length; i++)
            if (!Eq(a.CardReaders[i].origin, b.CardReaders[i].origin) || !Eq(a.CardReaders[i].frame.value, b.CardReaders[i].frame.value) ||
                !Eq(a.CardReaders[i].xSign, b.CardReaders[i].xSign)) { diff = "reader " + i; return false; }
        if (a.MirrorCount != b.MirrorCount) { diff = "mirror count"; return false; }
        for (int i = 0; i < a.MirrorCount; i++)
        {
            MirrorSurface m = a.Mirror(i), n = b.Mirror(i);
            if (!Eq(m.Centre, n.Centre) || !Eq(m.Normal, n.Normal) || !Eq(m.Up, n.Up) || !Eq(m.Size, n.Size) || !Eq(m.ConvexDepth, n.ConvexDepth) ||
                m.Segments != n.Segments) { diff = "mirror " + i; return false; }
            for (int k = 0; k < m.Segments; k++)
                if (!Eq(m.Outline(k), n.Outline(k))) { diff = "mirror " + i + " outline"; return false; }
        }
        var ra = a.RiderSlots(); var rb = b.RiderSlots();
        if (ra.Count != rb.Count) { diff = $"rider count {ra.Count}/{rb.Count}"; return false; }
        for (int i = 0; i < ra.Count; i++)
            if (!Eq(ra[i].Position, rb[i].Position) || !Eq(ra[i].Yaw, rb[i].Yaw) || ra[i].Kind != rb[i].Kind) { diff = "rider " + i; return false; }
        return true;
    }

    static bool Eq(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool Eq(float2 a, float2 b) => Eq(a.x, b.x) && Eq(a.y, b.y);
    static bool Eq(float3 a, float3 b) => Eq(a.x, b.x) && Eq(a.y, b.y) && Eq(a.z, b.z);
    static bool Eq(float4 a, float4 b) => Eq(a.x, b.x) && Eq(a.y, b.y) && Eq(a.z, b.z) && Eq(a.w, b.w);
    static bool Eq(VehicleProfile.Sign a, VehicleProfile.Sign b) => Eq(a.Centre, b.Centre) && Eq(a.Size, b.Size) && a.Matrix.Equals(b.Matrix) && Eq(a.Tilt, b.Tilt);

    // A bad profile is refused with a reason, never half used.
    static void Rejects(Action<bool, string> check)
    {
        string good = File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G"));
        string dir = TestPacks.Folder("SWB6710EV01G");
        check(Accepts(good, dir), Prefix + "the pack's own profile is accepted");
        (string what, string text)[] bad =
        {
            ("not JSON", "{ \"format\": "),
            ("another format", good.Replace("\"cdlb-vehicle\"", "\"something-else\"")),
            ("a newer version", good.Replace("\"version\": 1", "\"version\": 5")),
            ("a mesh path outside the pack", good.Replace("\"mesh\": \"SWB6710EV01G.swb1.gz\"", "\"mesh\": \"../SWB6710EV01G.swb1.gz\"")),
            ("a mesh in a subfolder", good.Replace("\"mesh\": \"SWB6710EV01G.swb1.gz\"", "\"mesh\": \"x/SWB6710EV01G.swb1.gz\"")),
            ("a missing mesh file", good.Replace("\"mesh\": \"SWB6710EV01G.swb1.gz\"", "\"mesh\": \"missing.swb1.gz\"")),
            ("more palette rows than this mod has", ReplaceNumber(good, "paletteRows", ((int)Sw.Count + 1).ToString())),
            ("a non-finite size", ReplaceNumber(good, "halfWidth", "1e40")),
            ("a body width out of range", ReplaceNumber(good, "halfWidth", "3")),
            ("a bad model name", good.Replace("\"model\": \"SWB6710EV01G\"", "\"model\": \"../evil\"")),
            ("a duplicate key", good.Replace("\"version\": 1,", "\"version\": 1, \"version\": 1,")),
            ("an unknown rider kind", good.Replace("\"kind\": \"Seated\"", "\"kind\": \"Flying\"")),
        };
        foreach ((string what, string text) in bad)
            check(text != good && !Accepts(text, dir), Prefix + "refuses " + what);
        string meshPath = Path.Combine(dir, "SWB6710EV01G.swb1.gz");
        var wrongHash = VehicleProfileJson.Parse(System.Text.RegularExpressions.Regex.Replace(good, "\"meshSha256\": \"[0-9a-f]{64}\"",
            "\"meshSha256\": \"" + new string('0', 64) + "\""), dir, "test");
        bool refused = false;
        try { var _ = wrongHash.Mesh; }
        catch (InvalidDataException) { refused = true; }
        check(refused && File.Exists(meshPath), Prefix + "refuses a mesh whose SHA-256 is not the profile's");
        WrapPillars(check, good, dir);
    }

    // wrap.pillars: named model-space boxes whose glazing the wrap covers on every face, outlined on the listed faces.
    static void WrapPillars(Action<bool, string> check, string good, string dir)
    {
        // the pack's own pillars (1.3.2: A each side on the front face, B on each side), then the profile without them as the base
        VehicleProfile.WrapPillar[] own = VehicleProfileJson.Parse(good, dir, "test").WrapPillars;
        check(own.Length == 4 && own.Count(p => p.Name == "A") == 2 && own.Count(p => p.Name == "B") == 2,
            Prefix + "the SWB6710EV01G pack declares its A and B pillars");
        var root = JsonNode.Parse(good);
        root["wrap"].AsObject().Remove("pillars");
        string bare = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string With(string pillars)
        {
            var copy = JsonNode.Parse(bare);
            copy["wrap"]["pillars"] = JsonNode.Parse("[" + pillars + "]");
            return copy.ToJsonString();
        }
        const string a = "{ \"name\": \"A\", \"min\": [0.95, 1.15, 3.25], \"max\": [1.2, 2.31, 3.49], \"faces\": [\"front\"] }";
        const string b = "{ \"name\": \"B\", \"min\": [-1.25, 1.11, 1.71], \"max\": [-0.95, 2.7, 1.92], \"faces\": [\"left\", \"left\", \"roof\"] }";
        check(VehicleProfileJson.Parse(bare, dir, "test").WrapPillars.Length == 0, Prefix + "a profile without wrap.pillars has none");
        VehicleProfile.WrapPillar[] read = VehicleProfileJson.Parse(With(a + ", " + b), dir, "test").WrapPillars;
        check(read.Length == 2 && read[0].Name == "A" && read[0].Faces.Length == 1 && read[0].Faces[0] == BusInterior.WrapFace.Front &&
              math.all(read[0].Min == new float3(.95f, 1.15f, 3.25f)) && math.all(read[0].Max == new float3(1.2f, 2.31f, 3.49f)),
            Prefix + "wrap.pillars reads a pillar's name, box and face");
        check(read[1].Faces.Length == 2 && read[1].Faces[0] == BusInterior.WrapFace.Left && read[1].Faces[1] == BusInterior.WrapFace.Roof,
            Prefix + "wrap.pillars keeps a face once");
        check(read[0].Contains(new float3(1f, 2f, 3.3f)) && !read[0].Contains(new float3(.9f, 2f, 3.3f)) && read[0].Contains(read[0].Max),
            Prefix + "a wrap pillar contains the points of its box, its faces included");
        (string what, string pillars)[] bad =
        {
            ("an empty pillar name", a.Replace("\"A\"", "\"\"")),
            ("a pillar name that is not letters or digits", a.Replace("\"A\"", "\"A pillar\"")),
            ("a pillar box with min above max", a.Replace("[0.95, 1.15, 3.25]", "[1.3, 1.15, 3.25]")),
            ("a pillar box with no depth", a.Replace("[0.95, 1.15, 3.25]", "[1.2, 1.15, 3.25]")),
            ("a pillar without faces", a.Replace("[\"front\"]", "[]")),
            ("an unknown pillar face", a.Replace("\"front\"", "\"top\"")),
            ("a pillar box out of range", a.Replace("[1.2, 2.31, 3.49]", "[1.2, 2.31, 99]")),
        };
        foreach ((string what, string pillars) in bad)
            check(!Accepts(With(pillars), dir), Prefix + "refuses " + what);
    }

    // The vehicle prefab's identity in saves: frozen per model (a repack must keep it) and the earlier builds' identities listed as
    // aliases, so a save made with any older build still finds its bus.
    static void PackIdentity(Action<bool, string> check)
    {
        string[] frozen = { "SWB6710EV01G", "e855a3c971f351d204ebdc10da58e9df", "SWB6109EV12G", "2257edf81bccb9c4161ce691c1915b62" };
        for (int i = 0; i < frozen.Length; i += 2)
        {
            string model = frozen[i];
            PackSettings p = PackSettings.Parse(File.ReadAllText(Path.Combine(Repo.Root, "Tools", model, "vehicle.json")), model, model);
            check(p.Guid == frozen[i + 1], Prefix + model + " keeps its frozen prefab GUID (" + p.Guid + ")");
            check(Array.IndexOf(p.ObsoleteGuids, p.Guid) < 0, Prefix + model + " does not alias its current GUID");
            check(p.ObsoleteGuids.Length == new System.Collections.Generic.HashSet<string>(p.ObsoleteGuids).Count, Prefix + model + " lists no GUID twice");
        }
        PackSettings strict = PackSettings.Parse(File.ReadAllText(Path.Combine(Repo.Root, "Tools", "SWB6109EV12G", "vehicle.json")), "SWB6109EV12G", "x");
        check(strict.Strict && strict.ObsoleteGuids.Length == 8 && Array.IndexOf(strict.ObsoleteGuids, "8cae05eedb33787bb0865e63ea22e310") >= 0,
            Prefix + "SWB6109EV12G aliases its eight earlier GUIDs, among them the one the saves hold");
        string good = "{ \"model\": \"M\", \"pack\": { \"template\": \"Bus02\", \"capacity\": 10, \"livery\": [1, 1, 1], \"strict\": false, " +
            "\"guid\": \"2257edf81bccb9c4161ce691c1915b62\", \"obsoleteGuids\": [\"8CAE05EEDB33787BB0865E63EA22E310\"] } }";
        PackSettings upper = PackSettings.Parse(good, "M", "t");
        check(upper.ObsoleteGuids[0] == "8cae05eedb33787bb0865e63ea22e310", Prefix + "GUIDs are read in lower case whatever the file writes");
        (string what, string text)[] bad =
        {
            ("a GUID of the wrong length", good.Replace("2257edf81bccb9c4161ce691c1915b62", "2257edf8")),
            ("a GUID with a non-hex digit", good.Replace("2257edf81bccb9c4161ce691c1915b62", "2257edf81bccb9c4161ce691c1915b6z")),
            ("the current GUID as its own alias", good.Replace("8CAE05EEDB33787BB0865E63EA22E310", "2257edf81bccb9c4161ce691c1915b62")),
            ("aliases without a fixed GUID", good.Replace("\"guid\": \"2257edf81bccb9c4161ce691c1915b62\", ", "")),
            ("an alias that is not text", good.Replace("\"8CAE05EEDB33787BB0865E63EA22E310\"", "7")),
            ("another model's profile", good.Replace("\"model\": \"M\"", "\"model\": \"N\"")),
            ("an unknown template bus", good.Replace("Bus02", "Bus99")),
            ("a livery outside 0..1", good.Replace("[1, 1, 1]", "[1, 2, 1]")),
            ("a capacity that is not whole", good.Replace("\"capacity\": 10", "\"capacity\": 10.5")),
        };
        foreach ((string what, string text) in bad)
        {
            bool refused = false;
            try { PackSettings.Parse(text, "M", "t"); }
            catch (ArgumentException) { refused = true; }
            check(text != good && refused, Prefix + "pack settings refuse " + what);
        }
    }

    static string ReplaceNumber(string json, string key, string value) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"" + key + "\": -?[0-9.eE+-]+", "\"" + key + "\": " + value);

    static bool Accepts(string text, string dir)
    {
        try { VehicleProfileJson.Parse(text, dir, "test"); return true; }
        catch (VehicleProfileJson.Error) { return false; }
    }
}
