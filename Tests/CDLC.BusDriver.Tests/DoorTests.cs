using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// Profile-driven doors: per-door timing (DoorMotion), outward-swing linkages (DoorLinkage, doors[].linkage) and their door
// drive parts in the SWB1 mesh (frame bit VehicleMesh.DriveBit, after the 34 contract parts, before the straps).
static class DoorTests
{
    const string Prefix = "doors: ";

    internal static void Run(Action<bool, string> check)
    {
        Timing(check);
        Linkages(check);
        Rejects(check);
        MeshRules(check);
        FourDoorTests.Run(check);
    }

    static void Timing(Action<bool, string> check)
    {
        var defaults = new DoorMotion();
        defaults.Step(true, .7f, null);
        check(Near(defaults.Channels.x, .5f, 1e-4f) && Near(defaults.Channels.z, .5f, 1e-4f), Prefix + "without a profile every door opens in the default 1.4 s");

        VehicleProfile sunwin = TestPacks.Profile("SWB6710EV01G");
        var doors = new DoorMotion();
        for (int i = 0; i < 60; i++) doors.Step(true, 1f / 60f, sunwin);
        check(Near(doors.Channels.x, 1f) && Near(doors.Channels.y, 1f), Prefix + "both doors open together in their 1.0 s");
        check(!doors.Moving, Prefix + "open doors are not moving (the warning beacon goes out)");
        float rearShut = -1f, frontStart = -1f, frontShut = -1f;
        for (int i = 1; i <= 240; i++)
        {
            doors.Step(false, 1f / 60f, sunwin);
            float t = i / 60f;
            if (rearShut < 0f && doors.Channels.y == 0f) rearShut = t;
            if (frontStart < 0f && doors.Channels.x < .999f) frontStart = t;
            if (frontShut < 0f && doors.Channels.x == 0f) frontShut = t;
        }
        check(Near(rearShut, 1.0f, .02f), Prefix + "the rear door closes first, in 1.0 s (" + rearShut + " s)");
        check(Near(frontStart, 1.2f + 1f / 60f, .02f) && Near(frontShut, 2.2f, .025f) && doors.Channels.z == 0f,
            Prefix + "the front pair starts 1.2 s after the command and is shut at 2.2 s (" + frontStart + ", " + frontShut + " s)");

        var reopen = new DoorMotion();
        for (int i = 0; i < 60; i++) reopen.Step(true, 1f / 60f, sunwin);
        for (int i = 0; i < 30; i++) reopen.Step(false, 1f / 60f, sunwin);
        float front = reopen.Channels.x, rear = reopen.Channels.y;
        reopen.Step(true, 1f / 60f, sunwin);
        check(Near(front, 1f) && rear < .6f && reopen.Channels.y > rear && Near(reopen.Channels.x, 1f),
            Prefix + "opening again during the close delay turns the closing rear door round at once, the waiting front pair stays open");
    }

    static void Linkages(Action<bool, string> check)
    {
        // the plan turn matches quaternion.RotateY
        float2 v = new float2(.3f, -.7f);
        float3 r = math.rotate(quaternion.RotateY(.6f), new float3(v.x, 0f, v.y));
        check(Near(DoorLinkage.Turn(v, .6f), r.xz), Prefix + "the linkage's plan turn is quaternion.RotateY's");

        VehicleProfile sunwin = TestPacks.Profile("SWB6710EV01G");
        check(sunwin.DoorLinkages != null && sunwin.DoorLinkages.Length == 2 && sunwin.DoorLinkages[0]?.Length == 2 && sunwin.DoorLinkages[1]?.Length == 1 &&
              sunwin.DoorDriveCount == 3, Prefix + "the SWB6710EV01G pack declares three linkage leaves with three door drives");
        if (sunwin.DoorLinkages == null) return;
        BusInteriorSpec spec = sunwin.Spec;
        float kerb = -spec.DriverSide;
        for (int d = 0; d < 2; d++)
            for (int leaf = 0; leaf < sunwin.DoorLinkages[d].Length; leaf++)
            {
                DoorLinkage l = sunwin.DoorLinkages[d][leaf];
                string name = "door " + d + " leaf " + leaf + ": ";
                check(l.Check() == null, Prefix + name + "the linkage moves all the way without a dead point (" + l.Check() + ")");
                check(l.Solve(0f, out float s0, out float2 j0, out float y0, out _) && s0 == 0f && y0 == 0f && Near(j0, l.Joint),
                    Prefix + name + "shut is exactly the rest pose");
                float open = d == 0 ? 80f : 82f;
                check(l.Solve(1f, out float s1, out float2 j1, out float y1, out float slack) && Near(math.degrees(math.abs(y1)), open, .05f) && slack > .02f,
                    Prefix + name + "open, the leaf has turned " + open + " degrees, the roller 20 mm or more from its dead point");
                check(Near(math.distance(j1, l.Shaft), math.distance(l.Joint, l.Shaft)) && Near(math.abs(s1), math.abs(l.ShaftRadians)),
                    Prefix + name + "the joint stays on its arm's circle about the shaft");
                // the Scott Russell drive: the shaft above the leaf's jamb end (shut), the crank pin halfway to the roller, which runs
                // along the body; so the jamb end runs straight into the bus
                check(Near(math.distance(l.Shaft, l.Joint), math.distance(l.Joint, l.Guide)) && Near(l.Shaft.x, l.Guide.x) &&
                      l.Shaft.x * kerb < spec.HalfWidth - .04f && l.Shaft.x * kerb > spec.HalfWidth - .08f,
                    Prefix + name + "the shaft stands above the leaf's jamb end, the crank half the guide span");
                float2 jamb = l.Shaft;            // the leaf's jamb-end point, under the shaft when shut
                bool straight = true;
                for (int i = 0; i <= 20; i++)
                {
                    l.Solve(i / 20f, out _, out float2 j, out float y, out _);
                    float2 now = j + DoorLinkage.Turn(jamb - l.Joint, y);
                    straight &= Near(now.y, jamb.y, 1e-4f) && now.x * kerb <= jamb.x * kerb + 1e-4f;
                }
                check(straight, Prefix + name + "the leaf's jamb end runs straight into the bus");
                float2 guide = l.Joint + DoorLinkage.Turn(l.Guide - l.Joint, y1) + (j1 - l.Joint);
                check(Near(guide.x, l.Guide.x), Prefix + name + "the roller is still on its rail when open");
            }
    }

    static void Rejects(Action<bool, string> check)
    {
        string good = File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G"));
        string dir = TestPacks.Folder("SWB6710EV01G");
        (string what, string text)[] bad =
        {
            ("a door drive used twice", good.Replace("\"drivePart\": 35", "\"drivePart\": 34")),
            ("door drives with a gap", good.Replace("\"drivePart\": 36", "\"drivePart\": 37")),
            ("fewer linkages than leaves", Regex.Replace(good, "\"leaves\": 1,", "\"leaves\": 2,")),
            ("a linkage that reaches a dead point", good.Replace("\"shaftDegrees\": -82.0", "\"shaftDegrees\": -90")),
            ("a linkage that passes its dead point and turns back", good.Replace("\"shaftDegrees\": -82.0", "\"shaftDegrees\": -120")),
            ("a negative close delay", Regex.Replace(good, "\"closeDelay\": 1\\.2", "\"closeDelay\": -1")),
        };
        foreach ((string what, string text) in bad)
            check(text != good && !Accepts(text, dir), Prefix + "refuses " + what);

        // a profile without linkages over a mesh with door drives: the mesh is refused when the model is built
        // (the pack is written with a two-space indent: a door's keys at six spaces, the linkage's closing bracket too)
        string noLinks = Regex.Replace(good, ",\\s*\"linkage\": \\[[\\s\\S]*?\\n {6}\\]", "");
        check(noLinks != good && !noLinks.Contains("\"linkage\""), Prefix + "(test) the linkages are removed from a copy of the profile");
        bool refused = false;
        string rejection = "no rejection";
        try { var _ = VehicleProfileJson.Parse(noLinks, dir, "test").Mesh; }
        catch (InvalidDataException e) { rejection = e.Message; refused = e.Message.Contains("door drive"); }
        check(refused, Prefix + "refuses a mesh whose door drives the profile's linkages do not declare (" + rejection + ")");
    }

    static void MeshRules(Action<bool, string> check)
    {
        string dir = TestPacks.Folder("SWB6710EV01G");
        byte[] raw;
        using (var gz = new GZipStream(File.OpenRead(Path.Combine(dir, "SWB6710EV01G.swb1.gz")), CompressionMode.Decompress))
        using (var ms = new MemoryStream()) { gz.CopyTo(ms); raw = ms.ToArray(); }
        int parts = BitConverter.ToInt32(raw, 8);
        int Flags(int part) => BitConverter.ToInt32(raw, 24 + 36 * part + 4);
        check(parts == 34 + 3 + 4 + 12 && Flags(34) == VehicleMesh.DriveBit && Flags(36) == VehicleMesh.DriveBit && Flags(37) == VehicleMesh.SurfaceBit &&
              Flags(40) == VehicleMesh.SurfaceBit && Flags(41) == 0,
            Prefix + "the pack's mesh has the three door drives (34..36), the four wrap surfaces (37..40), then its twelve straps");

        string good = File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G"));
        string text = Regex.Replace(good, ",\\s*\"meshSha256\": \"[0-9a-f]{64}\"", "");
        (string what, int part, int flags)[] cases =
        {
            ("a door drive after a strap", 45, VehicleMesh.DriveBit),
            ("a door drive after a wrap surface", 41, VehicleMesh.DriveBit),
            ("a wrap surface after a strap", 45, VehicleMesh.SurfaceBit),
            ("a drive flagged as a wrap surface too", 34, VehicleMesh.DriveBit | VehicleMesh.SurfaceBit),
            ("a gap in the door drives", 35, 0),
            ("an unknown frame flag", 34, VehicleMesh.DriveBit | 0x20000),
            ("a door drive among the contract parts", 12, VehicleMesh.DriveBit),
        };
        foreach ((string what, int part, int flags) in cases)
        {
            byte[] copy = (byte[])raw.Clone();
            BitConverter.GetBytes(flags).CopyTo(copy, 24 + 36 * part + 4);
            string folder = Path.Combine(Path.GetTempPath(), "cdlc-door-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                using (var file = File.Create(Path.Combine(folder, "SWB6710EV01G.swb1.gz")))
                using (var gz = new GZipStream(file, CompressionLevel.Fastest))
                    gz.Write(copy, 0, copy.Length);
                bool refused = false;
                try { var _ = VehicleProfileJson.Parse(text, folder, "test", requireMesh: false).Mesh; }
                catch (InvalidDataException) { refused = true; }
                check(refused, Prefix + "refuses a mesh with " + what);
            }
            finally { Directory.Delete(folder, true); }
        }
    }

    static bool Accepts(string text, string dir)
    {
        try { VehicleProfileJson.Parse(text, dir, "test"); return true; }
        catch (VehicleProfileJson.Error) { return false; }
    }

    static bool Near(float a, float b, float tolerance = 1e-4f) => math.abs(a - b) <= tolerance;
    static bool Near(float2 a, float2 b, float tolerance = 1e-4f) => math.cmax(math.abs(a - b)) <= tolerance;
}
