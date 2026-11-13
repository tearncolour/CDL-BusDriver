using System;
using System.IO;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// The body box a pack hands the driving mod (BusBody.Box, Core API 11): the profile's ends and half width, no mirrors.
static class BodyBoxTests
{
    const string Prefix = "body: ";

    internal static void Run(Action<bool, string> check)
    {
        // the real SWB6710EV01G pack: a 7.145 m x 2.35 m body (the game's mesh bounds of it are 7.7 x 2.9 m, the mirrors in)
        VehicleProfile pack = VehicleProfileJson.Parse(File.ReadAllText(TestPacks.ProfilePath("SWB6710EV01G")), TestPacks.Folder("SWB6710EV01G"), "SWB6710EV01G");
        bool ok = BusBody.Box(pack.Spec, out float3 min, out float3 max);
        check(ok, Prefix + "the SWB6710EV01G profile has a body");
        check(math.abs(max.x - min.x - 2.35f) < 1e-4f && math.abs(min.x + 1.175f) < 1e-4f, Prefix + "its width is the body's 2.35 m about the axis");
        check(math.abs(max.z - min.z - 7.145f) < 1e-3f && math.abs(min.z + 3.5725f) < 1e-4f, Prefix + "its length is the body's 7.145 m from the rear face");
        check(min.y == 0f && math.abs(max.y - 2.94f) < 1e-4f, Prefix + "from the ground to the roof skin");

        // an articulated front section whose origin is at the joint end (the K18): the box follows the ends, not the origin
        BusInteriorSpec front = pack.Spec;
        front.FrontZ = 9.01f; front.RearZ = -0.53f; front.HalfWidth = 1.27f;
        check(BusBody.Box(front, out min, out max) && math.abs(min.z + 0.53f) < 1e-4f && math.abs(max.z - 9.01f) < 1e-4f && math.abs(max.x - 1.27f) < 1e-4f, Prefix + "a section off its origin keeps its ends");

        // no usable body
        BusInteriorSpec bad = pack.Spec;
        bad.HalfWidth = 0.2f;
        check(!BusBody.Box(bad, out _, out _), Prefix + "a half width under 0.5 m is no body");
        bad = pack.Spec; bad.FrontZ = bad.RearZ + 0.5f;
        check(!BusBody.Box(bad, out _, out _), Prefix + "a body under 1 m long is no body");
        bad = pack.Spec; bad.RoofY = 0f;
        check(!BusBody.Box(bad, out _, out _), Prefix + "no roof height is no body");
    }
}
