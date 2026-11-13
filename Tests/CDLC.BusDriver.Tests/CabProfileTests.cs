using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// The driver's cab a vehicle declares (the profile's optional "cab" block, handed to Core with SetCab, API 10), the camera's eye
// against the model's formal one, and the shift started by taking a bus directly (BusShiftRules).
static class CabProfileTests
{
    const string Prefix = "cab: ";

    internal static void Run(Action<bool, string> check)
    {
        string dir = TestPacks.Folder("SWB6710EV01G");
        // the real pack profile without its own cab block (the pack has one since 1.2.4) and cameras (since 1.3.3): the variants below
        // each add theirs
        var root = JsonNode.Parse(File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G"))).AsObject();
        root.Remove("cab");
        root.Remove("cameras");
        string good = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        check(!good.Contains("\"cab\"") && !good.Contains("\"cameras\""), Prefix + "the test profile starts without a cab block and cameras");
        string WithCab(string cab) => good.Replace("\"format\": \"cdlb-vehicle\",", "\"format\": \"cdlb-vehicle\", \"cab\": " + cab + ",");

        // no "cab" block: nothing said, the model's eye and Core's own rules (as before)
        VehicleProfile plain = VehicleProfileJson.Parse(good, dir, "test");
        check(math.all(plain.Cab.EyeOffset == float3.zero) && float.IsNaN(plain.Cab.LookDownDeg) && float.IsNaN(plain.Cab.SeatSpring) && float.IsNaN(plain.Cab.Insulation),
            Prefix + "a profile without a cab block says nothing (the mesh's eye, Core decides the rest)");

        // a full block
        string full = WithCab("{ \"eyeOffset\": [0, -0.06, 0.02], \"lookDownDeg\": 6.5, \"seat\": \"air\", \"insulation\": 0.8 }");
        check(full != good, Prefix + "the test profile has the block");
        VehicleProfile cab = VehicleProfileJson.Parse(full, dir, "test");
        check(math.all(cab.Cab.EyeOffset == new float3(0f, -0.06f, 0.02f)) && cab.Cab.LookDownDeg == 6.5f && cab.Cab.SeatSpring == 1f && cab.Cab.Insulation == 0.8f,
            Prefix + "the block is read: eye offset, look-down, an air-sprung seat, the insulation");
        VehicleProfile fixedSeat = VehicleProfileJson.Parse(WithCab("{ \"seat\": \"fixed\" }"), dir, "test");
        check(fixedSeat.Cab.SeatSpring == 0f && math.all(fixedSeat.Cab.EyeOffset == float3.zero) && float.IsNaN(fixedSeat.Cab.Insulation),
            Prefix + "every field is optional (a fixed seat alone: no eye offset, insulation left to Core)");

        // "cabRender": the driver's view drawn from the pack's own game asset (native) or, as before, from the runtime mesh
        check(!plain.NativeCab, Prefix + "no cabRender: the runtime mesh draws the cab");
        string WithRender(string value) => good.Replace("\"format\": \"cdlb-vehicle\",", "\"format\": \"cdlb-vehicle\", \"cabRender\": " + value + ",");
        check(VehicleProfileJson.Parse(WithRender("\"native\""), dir, "test").NativeCab, Prefix + "cabRender native is read");
        check(!VehicleProfileJson.Parse(WithRender("\"runtime\""), dir, "test").NativeCab, Prefix + "cabRender runtime is the default drawn out");
        bool rejected = false;
        try { VehicleProfileJson.Parse(WithRender("\"baked\""), dir, "test"); } catch (VehicleProfileJson.Error) { rejected = true; }
        check(rejected, Prefix + "an unknown cabRender rejects the profile");

        // refused: the reader rejects a bad block whole, as every other part of a profile
        foreach ((string what, string block) in new[]
        {
            ("a cab that is not an object", "5"),
            ("an eye offset past half a metre", "{ \"eyeOffset\": [0, -0.8, 0] }"),
            ("an eye offset with two numbers", "{ \"eyeOffset\": [0, -0.06] }"),
            ("a look-down past 30 degrees", "{ \"lookDownDeg\": 45 }"),
            ("an insulation over 1", "{ \"insulation\": 1.5 }"),
            ("an unknown seat", "{ \"seat\": \"soft\" }"),
        })
            check(!Accepts(WithCab(block), dir), Prefix + "refuses " + what);

        // the camera's eye: the model's formal eye (the mirrors were aimed from it, the review probes look from it) moved by the cab
        VehicleProfile pack = TestPacks.Profile("SWB6710EV01G");
        float3 formal = BusInterior.Eye(pack.Spec);
        check(math.all(formal == pack.Mesh.Eye), Prefix + "the formal eye is the runtime mesh's eye");
        check(math.distance(BusInterior.CameraEye(pack.Spec), formal + pack.Cab.EyeOffset) < 1e-6f, Prefix + "the camera's eye is the formal eye moved by the pack's cab.eyeOffset");
        // the packs (SWB6710EV01G 1.2.4, SWB6109EV12G 1.0.1) carry the cab block: the camera 6 cm under the formal eye, as Core 0.13.26
        // had every bus
        foreach (string model in new[] { "SWB6710EV01G", "SWB6109EV12G" })
        {
            VehicleProfile p = TestPacks.Profile(model);
            check(math.all(p.Cab.EyeOffset == new float3(0f, -0.06f, 0f)) && p.Cab.SeatSpring == 1f && p.Cab.Insulation == 1f &&
                  math.distance(BusInterior.CameraEye(p.Spec), BusInterior.Eye(p.Spec) + new float3(0f, -0.06f, 0f)) < 1e-6f,
                Prefix + "the " + model + " pack's cab: the camera 6 cm under the formal eye, an air-sprung seat, a fully insulated saloon");
        }
        var generic = new BusInteriorSpec { FrontZ = 6f, RearZ = -6f, HalfWidth = 1.275f, RoofY = 3.2f, FloorY = 0.34f, FrontAxleZ = 3f, RearAxleZ = -3f, Doors = 2, DriverSide = -1f };
        float3 genericEye = BusInterior.Eye(generic), genericCamera = BusInterior.CameraEye(generic);
        check(math.abs(genericCamera.y - (genericEye.y - 0.06f)) < 1e-6f && genericCamera.x == genericEye.x && genericCamera.z == genericEye.z,
            Prefix + "the procedural bus's camera eye is 6 cm under its model's eye (as Core 0.13.26 had it)");
        VehicleProfile.CabData genericCab = BusInterior.DriverCab(generic);
        check(genericCab.SeatSpring == 1f && genericCab.Insulation == 1f && float.IsNaN(genericCab.LookDownDeg),
            Prefix + "the procedural bus: an air-sprung seat in a fully insulated saloon, the look-down left to Core");

        Cameras(check, good, dir);
        ShiftOnTake(check);
    }

    // The camera positions a vehicle declares besides the driver's seat (the profile's optional root "cameras", Core API 14).
    static void Cameras(Action<bool, string> check, string good, string dir)
    {
        string WithCameras(string cameras) => good.Replace("\"format\": \"cdlb-vehicle\",", "\"format\": \"cdlb-vehicle\", \"cameras\": " + cameras + ",");

        // the driver's seat alone (the cab of API 10, as before) on WG6980A; eight more, four seats and four mounted, on SWB6109EV12G
        // (pack 1.1.3) and SWB6710EV01G (pack 1.3.3; Tools/<model>/cameras.py writes them into the tool profile)
        check(TestPacks.Profile("WG6980A").Cameras.Length == 0, Prefix + "the WG6980A pack has no cameras besides the driver's seat");
        string[] setNames = { "frontPassenger", "standing", "door", "rearSeat", "front", "roof", "side", "rear" };
        bool EightCameras(VehicleProfile.CameraData[] set)
        {
            bool ok = set.Length == setNames.Length;
            for (int i = 0; ok && i < set.Length; i++)
                ok = set[i].Name == setNames[i] && set[i].Mounted == (i >= 4);
            return ok;
        }
        check(EightCameras(TestPacks.Profile("SWB6109EV12G").Cameras), Prefix + "the SWB6109EV12G pack declares its eight cameras: four seats, then four mounted");
        check(EightCameras(TestPacks.Profile("SWB6710EV01G").Cameras), Prefix + "the SWB6710EV01G pack declares its eight cameras: four seats, then four mounted");
        string k18Path = TestPacks.ProfilePath("CRRC_K18");
        string k18Text = File.ReadAllText(k18Path);
        VehicleProfile k18 = VehicleProfileJson.Parse(k18Text, TestPacks.Folder("CRRC_K18"), "test", false);
        check(k18.Cameras.Length == 0 && k18.Assembly.Rear.Profile.Cameras.Length == 0, Prefix + "the K18 pack has none on either section");
        check(VehicleProfileJson.Parse(WithCameras("[]"), dir, "test").Cameras.Length == 0 && VehicleProfileJson.Parse(WithCameras("null"), dir, "test").Cameras.Length == 0,
            Prefix + "an empty or null cameras list is none");

        string two = "[ { \"name\": \"frontPassenger\", \"eye\": [0.6, 1.95, 4.1], \"yawDeg\": -20, \"lookDownDeg\": 8, \"fovDeg\": 70, \"seat\": \"fixed\", \"insulation\": 0.7 }," +
                     "  { \"name\": \" rear \", \"kind\": \"mounted\", \"eye\": [0, 2.9, -5.4], \"yawDeg\": 180, \"lookDownDeg\": 35 } ]";
        VehicleProfile p = VehicleProfileJson.Parse(WithCameras(two), dir, "test");
        check(p.Cameras.Length == 2, Prefix + "two cameras read");
        VehicleProfile.CameraData a = p.Cameras[0], b = p.Cameras[1];
        check(a.Name == "frontPassenger" && !a.Mounted && math.all(a.Eye == new float3(0.6f, 1.95f, 4.1f)) && a.YawDeg == -20f && a.LookDownDeg == 8f &&
              a.FovDeg == 70f && a.SeatSpring == 0f && a.Insulation == 0.7f, Prefix + "a seat: its name, eye, yaw, look-down, field of view, fixed seat and insulation");
        check(b.Name == "rear" && b.Mounted && b.YawDeg == 180f && b.LookDownDeg == 35f && float.IsNaN(b.FovDeg) && float.IsNaN(b.SeatSpring) && float.IsNaN(b.Insulation),
            Prefix + "a mounted camera: the name trimmed, a steeper look-down than a seat may have, the rest left to Core");
        VehicleProfile.CameraData bare = VehicleProfileJson.Parse(WithCameras("[ { \"eye\": [0, 1.7, 0] } ]"), dir, "test").Cameras[0];
        check(bare.Name == "" && !bare.Mounted && bare.YawDeg == 0f && float.IsNaN(bare.LookDownDeg) && float.IsNaN(bare.FovDeg),
            Prefix + "only the eye is needed: no name (Core shows \"Camera N\"), a seat, forward, the player's field of view");
        check(Accepts(WithCameras("[" + string.Join(",", System.Linq.Enumerable.Repeat("{ \"eye\": [0, 1.7, 0] }", VehicleProfileJson.MaxCameras)) + "]"), dir),
            Prefix + "15 cameras besides the driver's seat (Core takes 16)");

        foreach ((string what, string cameras) in new[]
        {
            ("cameras that are not a list", "{ \"eye\": [0, 1.7, 0] }"),
            ("a camera that is not an object", "[ 5 ]"),
            ("a camera without an eye", "[ { \"name\": \"door\" } ]"),
            ("an eye with two numbers", "[ { \"eye\": [0, 1.7] } ]"),
            ("an eye past 30 m", "[ { \"eye\": [0, 1.7, 31] } ]"),
            ("an unknown kind", "[ { \"eye\": [0, 1.7, 0], \"kind\": \"drone\" } ]"),
            ("a seat looking down past 30 degrees", "[ { \"eye\": [0, 1.7, 0], \"lookDownDeg\": 35 } ]"),
            ("a mounted camera looking down past 60 degrees", "[ { \"eye\": [0, 3, 0], \"kind\": \"mounted\", \"lookDownDeg\": 70 } ]"),
            ("a field of view under 20 degrees", "[ { \"eye\": [0, 1.7, 0], \"fovDeg\": 10 } ]"),
            ("a yaw past 180 degrees", "[ { \"eye\": [0, 1.7, 0], \"yawDeg\": 200 } ]"),
            ("a seat on a mounted camera", "[ { \"eye\": [0, 3, 0], \"kind\": \"mounted\", \"seat\": \"air\" } ]"),
            ("an unknown seat", "[ { \"eye\": [0, 1.7, 0], \"seat\": \"soft\" } ]"),
            ("an insulation over 1", "[ { \"eye\": [0, 1.7, 0], \"insulation\": 2 } ]"),
            ("a name over 40 characters", "[ { \"eye\": [0, 1.7, 0], \"name\": \"" + new string('x', 41) + "\" } ]"),
            ("16 cameras besides the driver's seat", "[" + string.Join(",", System.Linq.Enumerable.Repeat("{ \"eye\": [0, 1.7, 0] }", VehicleProfileJson.MaxCameras + 1)) + "]"),
        })
            check(!Accepts(WithCameras(cameras), dir), Prefix + "cameras: refuses " + what);

        // an articulated bus: each section's own cameras (the rear's go to Core on section 1), at most 15 over the two
        string InSection(string section, string cameras) =>
            k18Text.Replace("\"model\": \"" + section + "\",", "\"model\": \"" + section + "\", \"cameras\": " + cameras + ",");
        string rearSeat = "[ { \"name\": \"rearSeat\", \"eye\": [0.5, 1.9, -6] } ]";
        string withRear = InSection("CRRCK18Rear", rearSeat);
        check(withRear != k18Text, Prefix + "the K18 test profile has a camera on its rear section");
        VehicleProfile articulated = VehicleProfileJson.Parse(withRear, TestPacks.Folder("CRRC_K18"), "test", false);
        check(articulated.Cameras.Length == 0 && articulated.Assembly.Rear.Profile.Cameras.Length == 1 && articulated.Assembly.Rear.Profile.Cameras[0].Name == "rearSeat",
            Prefix + "the K18: a camera on the rear section is the rear section's");
        string eight = "[" + string.Join(",", System.Linq.Enumerable.Repeat("{ \"eye\": [0, 1.7, -6] }", 8)) + "]";
        bool refused = false;
        try { VehicleProfileJson.Parse(InSection("CRRCK18", eight).Replace("\"model\": \"CRRCK18Rear\",", "\"model\": \"CRRCK18Rear\", \"cameras\": " + eight + ","), TestPacks.Folder("CRRC_K18"), "test", false); }
        catch (VehicleProfileJson.Error) { refused = true; }
        check(refused, Prefix + "the K18: 8 + 8 cameras over the two sections are refused (15 besides the driver's seat)");
    }

    // The shift started by taking a bus directly: once per vehicle taken, only for a line's bus, only with the option, never over a shift.
    static void ShiftOnTake(Action<bool, string> check)
    {
        check(BusShiftRules.StartOnTake(true, true, false, true, true), Prefix + "a line's bus taken directly with no shift running starts the shift");
        check(!BusShiftRules.StartOnTake(false, true, false, true, true), Prefix + "the option off: no shift");
        check(!BusShiftRules.StartOnTake(true, false, false, true, true), Prefix + "a shift already running (or taking over): left alone");
        check(!BusShiftRules.StartOnTake(true, true, true, true, true), Prefix + "the panel asked for something this frame: its request wins");
        check(!BusShiftRules.StartOnTake(true, true, false, false, true), Prefix + "not just taken (the same vehicle a frame later): no new start");
        check(!BusShiftRules.StartOnTake(true, true, false, true, false), Prefix + "a bus on no line (parked, in its depot) is driven freely");

        long a = BusShiftRules.Key(12, 1), b = BusShiftRules.Key(13, 1), a2 = BusShiftRules.Key(12, 2);
        check(a != 0L && a != b && a != a2 && BusShiftRules.Key(0, 0) == 0L, Prefix + "a vehicle's key tells index and version apart, none is 0");
        var watch = new TakeWatch();
        bool first = watch.Taken(true, a), again = watch.Taken(true, a);
        check(first && !again, Prefix + "taking a vehicle counts once, not every frame");
        bool other = watch.Taken(true, b);
        check(other, Prefix + "taking another vehicle straight away counts");
        bool released = watch.Taken(false, 0L), retaken = watch.Taken(true, b);
        check(!released && retaken, Prefix + "letting go and taking the same vehicle again counts again");
        check(!watch.Taken(true, 0L) && watch.Taken(true, b), Prefix + "no vehicle (driving but none known) is no take and forgets the last one");
    }

    static bool Accepts(string text, string dir)
    {
        try { VehicleProfileJson.Parse(text, dir, "test"); return true; }
        catch (VehicleProfileJson.Error) { return false; }
    }
}
