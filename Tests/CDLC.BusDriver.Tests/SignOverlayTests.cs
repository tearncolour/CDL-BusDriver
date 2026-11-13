using System;
using System.IO;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using LedGeometry = CDLC.Asset.Led.LedGeometry;

// Sign overlays (format "cdlb-signs"): our signs on a bus asset by someone else, read as the mod reads them, and the frames they are
// drawn in. The real file is the SWB6115 overlay (Tools/SWB6115), measured on that asset's own body.
static class SignOverlayTests
{
    const string Prefix = "sign overlay: ";

    internal static void Run(Action<bool, string> check)
    {
        string path = Path.Combine(Repo.Root, "Tools", "SWB6115", "SWB6115.cdlb-signs.json");
        string text = File.ReadAllText(path);
        SignOverlay o = VehicleProfileJson.ParseOverlay(text, path);
        check(o.Name == "SWB6115" && o.Source == path, Prefix + "the SWB6115 overlay is read");
        check(o.FrontSign.Matrix.x == 104 && o.SideSign.Matrix.x == 68 && o.RearSign.Matrix.x == 96 && !o.HasCards, Prefix + "three LED signs");
        foreach ((string what, VehicleProfile.Sign s, SignOverlay.Backing b) in new[]
            { ("front", o.FrontSign, o.FrontBacking), ("side", o.SideSign, o.SideBacking), ("rear", o.RearSign, o.RearBacking) })
        {
            check(math.abs(s.Size.x / s.Matrix.x - s.Size.y / s.Matrix.y) < 1e-4f, Prefix + what + ": the classic dots are square");
            int2 hd = LedGeometry.HdMatrix(s.Size);
            check(math.abs(s.Size.x / hd.x - s.Size.y / hd.y) < .02f * s.Size.y / hd.y, Prefix + what + ": the high-density dots are square");
            check(b.Present && b.Size.x >= s.Size.x && b.Size.y >= s.Size.y && math.all(b.Srgb == new float3(14f)), Prefix + what + ": a dark board covers the sign");
        }

        // the frames: dots face -Z of the frame (LedDots); the board lies behind them, towards the body
        float3 Facing(quaternion r) => math.mul(r, new float3(0f, 0f, -1f));
        float3 Up(quaternion r) => math.mul(r, new float3(0f, 1f, 0f));
        check(math.dot(Facing(o.FrontRotation), new float3(0f, 0f, 1f)) > .99f && Up(o.FrontRotation).z < 0f, Prefix + "the front sign faces forward, its top leaning back");
        check(math.abs(math.asin(-Up(o.FrontRotation).z) - .107f) < 1e-4f, Prefix + "the front sign leans by its tilt");
        check(math.dot(Facing(o.RearRotation), new float3(0f, 0f, -1f)) > .99f && Up(o.RearRotation).z > 0f, Prefix + "the rear sign faces backward, its top leaning forward");
        check(o.Kerb == 1f && math.dot(Facing(o.SideRotation), new float3(1f, 0f, 0f)) > .99f && Up(o.SideRotation).x < 0f,
            Prefix + "the side sign faces its own side (+X, the doors), its top leaning in");
        check(math.all(BusInterior.SideSignRotation(1f, .028f).value == BusInterior.SideSignRotation(1f, o.SideSign.Tilt).value),
            Prefix + "the side frame is the one our models' side signs use");
        float3 board = SignOverlay.BackingCentre(o.FrontSign.Centre, o.FrontRotation);
        check(board.z < o.FrontSign.Centre.z && math.abs(math.distance(board, o.FrontSign.Centre) - SignOverlay.BackingDepth) < 1e-5f,
            Prefix + "the front board lies just behind the dots");
        check(SignOverlay.BackingCentre(o.RearSign.Centre, o.RearRotation).z > o.RearSign.Centre.z &&
              SignOverlay.BackingCentre(o.SideSign.Centre, o.SideRotation).x < o.SideSign.Centre.x, Prefix + "the rear and side boards lie behind their dots");

        // refused whole, with the reason
        string Swap(string from, string to)
        {
            check(text.Contains(from), Prefix + "the test file has " + from);
            return text.Replace(from, to);
        }
        foreach ((string what, string bad) in new[]
        {
            ("another format", Swap("\"cdlb-signs\"", "\"cdlb-vehicle\"")),
            ("a newer version", Swap("\"version\": 1", "\"version\": 2")),
            ("a bad model name", Swap("\"SWB6115\"", "\"SWB 6115\"")),
            ("a side sign on the middle line", Swap("[1.256, 1.52, 3.16]", "[0.2, 1.52, 3.16]")),
            ("a board smaller than its sign", Swap("\"width\": 1.4,", "\"width\": 1.2,")),
            ("a board without a colour", Swap("\"colour\": [14, 14, 14], \"width\": 0.92", "\"width\": 0.92")),
            ("no sign at all", "{ \"format\": \"cdlb-signs\", \"version\": 1, \"model\": \"X\", \"signs\": {} }"),
            ("not JSON", "{"),
        })
            check(!Accepts(bad), Prefix + "refuses " + what);

        // an overlay is not a vehicle profile
        check(!ProfileAccepts(text), Prefix + "the vehicle profile reader refuses an overlay");
    }

    static bool Accepts(string text)
    {
        try { VehicleProfileJson.ParseOverlay(text, "test"); return true; }
        catch (VehicleProfileJson.Error) { return false; }
    }

    static bool ProfileAccepts(string text)
    {
        try { VehicleProfileJson.Parse(text, ".", "test"); return true; }
        catch (VehicleProfileJson.Error) { return false; }
    }
}
