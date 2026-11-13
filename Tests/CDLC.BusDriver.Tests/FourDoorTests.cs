using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

static class FourDoorTests
{
    internal static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "cdlc-four-doors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string text = Profile();
            VehicleProfile model = VehicleProfileJson.Parse(text, folder, "four-door fixture", requireMesh: false);
            check(model.Doors.Length == 4 && model.DoorPart(3, 0) == 34 && model.DoorPart(3, 1) == 35 && model.FirstDoorDrive == 36,
                "four doors: extra leaves preserve fixed parts 0..33 and precede drives");
            check(VehicleBoarding.Points(model).Length == 8, "four doors: native thresholds include both vehicle sides");
            var motion = new DoorMotion();
            motion.Step(true, .5f, model);
            check(motion.Channels.x == .5f && motion.Channels.y == .5f && motion.Channels.z == 0f && motion.Fourth == 0f,
                "four doors: default half-open moves only the two right doors");
            motion.Step(true, .5f, model);
            check(motion.Channels.x == 1f && motion.Channels.y == 1f && motion.Channels.z == 0f && motion.Fourth == 0f,
                "four doors: default full-open keeps both left channels shut");
            motion.Step(true, 1f, model, -1f);
            check(motion.Channels.x == 1f && motion.Channels.z == 0f && motion.Fourth == 0f,
                "four doors: changing target while open cannot switch platform sides");
            motion.Step(false, 1f, model, -1f);
            motion.Step(true, .5f, model, -1f);
            check(motion.Channels.x == 0f && motion.Channels.y == 0f && motion.Channels.z == .5f && motion.Fourth == .5f && motion.Moving,
                "four doors: left side including the fourth channel opens after all doors close");
            motion.Step(false, 1f, model);
            motion.Step(true, 1f, model);
            check(motion.Channels.x == 1f && motion.Channels.y == 1f && motion.Channels.z == 0f && motion.Fourth == 0f,
                "four doors: default command returns to right side after a complete close");
            WriteMesh(folder, model, 0);
            check(model.Mesh.ExtraDoorLeafCount == 2 && model.Mesh.FirstStrap == 36 && model.Mesh.TriangleCount(35, 2) == 1,
                "four doors: final LOD carries fourth-door geometry without treating it as a strap");
            VehicleModels.Register(model);
            var parts = new KitPartList(); model.Mesh.Parts(parts);
            var posed = new KitPoseBuffer(parts.ToArray());
            var pose = BusInterior.BoardPoseOf(0, true);
            pose.FourthDoorOpen = 1f; pose.StrapSwing = new float2(.35f, -.20f);
            BusInterior.Apply(posed, pose, model.Spec);
            float3 fourthPoint = model.Mesh.Pivot(35) + new float3(0f, 0f, .25f);
            float3 moved = posed.RestPointToModel(35, fourthPoint);
            check(moved.x > fourthPoint.x + .20f && math.abs(moved.y-fourthPoint.y) < 1e-5f,
                "four doors: fourth left leaf moves inward, unaffected by strap swing");
            float3 rightPoint = model.Mesh.Pivot(5) + new float3(0f, 0f, .25f);
            check(math.distance(posed.RestPointToModel(5, rightPoint), rightPoint) < 1e-5f,
                "four doors: fourth channel does not move a closed right leaf");
            foreach (int failure in new[] { 1, 2, 3, 4 })
            {
                WriteMesh(folder, model, failure);
                bool rejected = false;
                try { var mesh = VehicleProfileJson.Parse(text, folder, "invalid four-door fixture", requireMesh: false).Mesh; }
                catch (InvalidDataException) { rejected = true; }
                check(rejected, "four doors: rejects malformed extra-leaf frame case " + failure);
            }
            foreach (string bad in new[] { text.Replace("\"left\"", "\"both\""), text.Replace("\"doors\":4", "\"doors\":5"),
                text.Replace("\"version\":3", "\"version\":1") })
            {
                bool rejected = false;
                try { VehicleProfileJson.Parse(bad, folder, "invalid side/count", requireMesh: false); }
                catch (VehicleProfileJson.Error) { rejected = true; }
                check(bad != text && rejected, "four doors: rejects invalid side or unsupported count");
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    static string Profile()
    {
        var root = JsonNode.Parse(File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G"))).AsObject();
        root.Remove("meshSha256");
        root["version"] = 3;
        root["spec"]["doors"] = 4;
        root["wrap"]["windows"] = false; root["wrap"]["doors"] = false;
        var doors = new JsonArray();
        float half = root["spec"]["halfWidth"].GetValue<float>(), floor = root["spec"]["floorY"].GetValue<float>();
        for (int d = 0; d < 4; d++)
        {
            float side = d < 2 ? 1f : -1f;
            doors.Add(new JsonObject {
                ["centreZ"] = 2f-d, ["width"] = .8f, ["leaves"] = 2, ["side"] = side > 0f ? "right" : "left",
                ["openSeconds"] = 1f, ["closeSeconds"] = 1f,
                ["boardingPosition"] = new JsonArray(side*half, floor, 2f-d),
                ["boardingRotation"] = new JsonArray(0f, -side*.70710678f, 0f, .70710678f),
                ["boarding"] = true, ["alighting"] = true
            });
        }
        root["doors"] = doors;
        return root.ToJsonString();
    }

    static void WriteMesh(string folder, VehicleProfile model, int failure)
    {
        using var file = File.Create(Path.Combine(folder, Path.GetFileName(model.MeshPath)));
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var w = new BinaryWriter(gzip);
        w.Write(0x31425753u); w.Write(3); w.Write(36);
        Vec(w, 0f, 2f, 2f);
        for (int i = 0; i < 36; i++)
        {
            w.Write(i == 0 ? -1 : 0);
            int flags = i >= 34 ? VehicleMesh.DoorLeafBit : 0;
            if (failure == 1 && i == 34) flags = 0;
            if (failure == 2 && i == 34) flags |= VehicleMesh.DriveBit;
            w.Write(flags);
            int d = i >= 34 ? 3 : (i-4)/2;
            float x = i >= 4 && i <= 9 || i >= 34 ? (d < 2 ? 1.2f : -1.2f) : 0f;
            if (failure == 3 && i == 35) x = 1.2f;
            Vec(w, x, .4f, 0f); Vec(w, 0f, 0f, 0f); w.Write(1f);
        }
        for (int lod = 0; lod < 3; lod++)
            for (int part = 0; part < 36; part++)
            {
                bool geometry = part >= 34 && !(failure == 4 && part == 35 && lod == 2);
                w.Write(geometry ? 1 : 0);
                if (!geometry) continue;
                w.Write((byte)0);
                Vec(w, 0f, 0f, 0f); Vec(w, 0f, 0f, 1f);
                Vec(w, .1f, 0f, 0f); Vec(w, 0f, 0f, 1f);
                Vec(w, 0f, .1f, 0f); Vec(w, 0f, 0f, 1f);
            }
    }

    static void Vec(BinaryWriter w, float x, float y, float z) { w.Write(x); w.Write(y); w.Write(z); }
}
