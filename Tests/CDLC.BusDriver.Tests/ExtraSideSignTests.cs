using System;
using System.IO;
using System.Text.Json.Nodes;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

static class ExtraSideSignTests
{
    internal static void Run(Action<bool, string> check)
    {
        string path = TestPacks.ProfilePath("SWB6710EV01G");
        var root = JsonNode.Parse(File.ReadAllText(path)).AsObject();
        root.Remove("meshSha256");
        VehicleProfile Parse(JsonObject value) => VehicleProfileJson.Parse(value.ToJsonString(), Path.GetDirectoryName(path), "extra-side fixture", requireMesh: false);
        check(Parse(root).ExtraSideSigns.Length == 0, "extra side signs: existing profiles retain no additional screens");
        var sign = new JsonObject { ["centre"] = new JsonArray(-1.2f, 2.7f, 2f), ["width"] = .64f,
            ["height"] = .16f, ["columns"] = 64, ["tilt"] = -.06f };
        root["signs"]["extraSides"] = new JsonArray(sign.DeepClone());
        var parsed = Parse(root).ExtraSideSigns[0];
        var rotation = BusInterior.SideSignRotation(math.sign(parsed.Centre.x), parsed.Tilt);
        float3 normal = math.rotate(rotation, new float3(0, 0, -1));
        check(normal.x < -.99f && math.abs(parsed.Size.x / parsed.Matrix.x - parsed.Size.y / parsed.Matrix.y) < 1e-6f,
            "extra side signs: opposite side faces outward with square dot pitch");
        var card = sign.DeepClone(); card["style"] = "card"; card["centre"][0] = 1.2f;
        root["signs"]["extraSides"] = new JsonArray(sign.DeepClone(), card);
        check(Parse(root).ExtraSideSigns.Length == 2 && Parse(root).ExtraSideSigns[1].IsCard,
            "extra side signs: independent mixed LED and printed boards retain their style");
        foreach (var invalid in new JsonNode[] { JsonValue.Create("left"), new JsonArray((JsonNode)null),
            new JsonArray(sign.DeepClone(), sign.DeepClone(), sign.DeepClone(), sign.DeepClone()) })
        {
            root["signs"]["extraSides"] = invalid;
            bool rejected = false;
            try { Parse(root); } catch (VehicleProfileJson.Error) { rejected = true; }
            check(rejected, "extra side signs: malformed array, missing frame or unsupported count is rejected");
        }
        var ambiguous = sign.DeepClone(); ambiguous["centre"][0] = 0;
        root["signs"]["extraSides"] = new JsonArray(ambiguous);
        bool noSide = false;
        try { Parse(root); } catch (VehicleProfileJson.Error) { noSide = true; }
        check(noSide, "extra side signs: centre cannot imply neither vehicle side");
    }
}
