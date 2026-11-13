using System;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

static class AssemblyPackSettingsTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string json = "{\"version\":2,\"model\":\"FixtureAssembly\",\"sections\":[" +
            "{\"id\":\"front\",\"prefab\":\"FixtureFront\"},{\"id\":\"rear\",\"prefab\":\"FixtureRear\"}]," +
            "\"pack\":{\"template\":\"Bus02\",\"capacity\":151,\"sections\":{" +
            "\"front\":{\"capacity\":85,\"guid\":\"11111111111111111111111111111111\"}," +
            "\"rear\":{\"capacity\":66,\"guid\":\"22222222222222222222222222222222\"}}}}";
        PackSettings settings = PackSettings.Parse(json, "FixtureAssembly", "fixture");
        check(settings.AssemblyModel == "FixtureAssembly" && settings.Sections.Length == 2 &&
            settings.Sections[0].Capacity + settings.Sections[1].Capacity == settings.Capacity,
            "assembly pack: explicit independent section capacities sum to the total");
        check(PackSettings.Parse(json, "FixtureRear", "fixture").AssemblyModel == "FixtureAssembly",
            "assembly pack: exact rear prefab entrance retains its assembly settings");
        foreach (string bad in new[]
        {
            json.Replace("\"capacity\":66", "\"capacity\":65"),
            json.Replace("\"capacity\":66", "\"capacity\":66.5"),
            json.Replace("\"guid\":\"22222222222222222222222222222222\"", "\"guid\":\"11111111111111111111111111111111\""),
            json.Replace("\"guid\":\"22222222222222222222222222222222\"", "\"guid\":\"invalid\""),
            json.Replace("FixtureRear", "FixtureFront"),
            json.Replace("FixtureRear", "Fixture_Rear"),
            json.Replace("\"id\":\"rear\"", "\"id\":\"front\""),
            json.Replace("\"id\":\"rear\"", "\"id\":\"../rear\""),
            json.Replace("\"version\":2", "\"version\":" + (VehicleProfileJson.Version + 1)),   // above what the profile reader takes
            json.Replace("\"capacity\":66,", ""),
        })
        {
            bool rejected = false;
            try { PackSettings.Parse(bad, "FixtureAssembly", "bad fixture"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "assembly pack: rejects invalid native identity/capacity/section/version");
        }
        bool unknown = false;
        try { PackSettings.Parse(json, "GuessedRearSuffix", "fixture"); }
        catch (ArgumentException) { unknown = true; }
        check(unknown, "assembly pack: never guesses a section from a name suffix");
        check(PackSettings.NativeMainName("FixtureFront") && !PackSettings.NativeMainName("Fixture_Front"),
            "assembly pack: native main names reserve underscores for importer suffixes");
        check(PackSettings.Parse("{\"version\":1,\"model\":\"Legacy_WithUnderscore\",\"pack\":{\"template\":\"Bus02\"}}", "Legacy_WithUnderscore", "v1 fixture").AssemblyModel == null,
            "assembly pack: v1 name acceptance remains unchanged");
        // pack.icon: the asset menu icon, a PNG file name beside the profile; none keeps the game's rendered icon
        const string single = "{\"version\":1,\"model\":\"FixtureBus\",\"pack\":{\"template\":\"Bus02\"}}";
        check(!PackSettings.Parse(single, "FixtureBus", "normal fixture").GlassNormalStrength.HasValue,
            "pack glass normal: omission preserves the imported material setting");
        foreach ((string text, float want) in new[] { ("0", 0f), ("0.5", .5f), ("1", 1f) })
            check(PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"glassNormalStrength\":" + text + "}"), "FixtureBus", "normal fixture").GlassNormalStrength == want,
                "pack glass normal: explicit bounded strength is retained");
        foreach (string invalid in new[] { "-0.01", "1.01", "null", "\"0\"", "true", "[0]" })
        {
            bool rejected = false;
            try { PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"glassNormalStrength\":" + invalid + "}"), "FixtureBus", "normal fixture"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "pack glass normal: rejects invalid or untyped strength");
        }
        check(PackSettings.Parse(single, "FixtureBus", "lights").LightLayers.Length == 9,
            "pack lights: omitted contract preserves all nine legacy layers");
        var explicitLights = PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"lightLayers\":[8,2,3]}"), "FixtureBus", "lights").LightLayers;
        check(explicitLights.Length == 3 && explicitLights[0] == 2 && explicitLights[2] == 8,
            "pack lights: authored sparse IDs retain purpose numbers and canonical ordering");
        foreach (string invalid in new[] { "[]", "[2,2]", "[0]", "[10]", "[1.5]", "[\"2\"]", "null" })
        {
            bool rejected = false;
            try { PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"lightLayers\":" + invalid + "}"), "FixtureBus", "lights"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "pack lights: rejects empty, duplicate and invalid light IDs");
        }
        check(PackSettings.Parse(single, "FixtureBus", "icon fixture").Icon == null, "pack icon: none without pack.icon");
        check(PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"icon\":\"icon.png\"}"), "FixtureBus", "icon fixture").Icon == "icon.png",
            "pack icon: a PNG name beside the profile is read");
        foreach (string badIcon in new[] { "\"../icon.png\"", "\"sub/icon.png\"", "\"C:icon.png\"", "\"icon.jpg\"", "\".png\"", "\"\"", "5" })
        {
            bool rejected = false;
            try { PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"icon\":" + badIcon + "}"), "FixtureBus", "bad icon fixture"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "pack icon: rejects " + badIcon + " (only a plain .png name beside the profile)");
        }
        // pack.glassCoatMask: the strict pipeline's glass coat, the model's own; absent keeps the audited value
        string strict = single.Replace("\"Bus02\"}", "\"Bus02\",\"strict\":true}");
        check(PackSettings.Parse(strict, "FixtureBus", "coat fixture").GlassCoatMask == PackSettings.AuditedGlassCoatMask && PackSettings.AuditedGlassCoatMask == .65f,
            "pack glass coat: a strict model without pack.glassCoatMask keeps the audited 0.65");
        check(PackSettings.Parse(single, "FixtureBus", "coat fixture").GlassCoatMask == .65f, "pack glass coat: a model that is not strict reads the same default");
        foreach ((string text, float want) in new[] { ("0.4", .4f), ("0", 0f), ("1", 1f), ("0.65", .65f) })
            check(PackSettings.Parse(strict.Replace("\"strict\":true", "\"strict\":true,\"glassCoatMask\":" + text), "FixtureBus", "coat fixture").GlassCoatMask == want,
                "pack glass coat: pack.glassCoatMask " + text + " is read as the model's own");
        foreach (string badCoat in new[] { "-0.1", "1.5", "\"0.5\"", "true", "[0.5]" })
        {
            bool rejected = false;
            try { PackSettings.Parse(strict.Replace("\"strict\":true", "\"strict\":true,\"glassCoatMask\":" + badCoat), "FixtureBus", "bad coat fixture"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "pack glass coat: rejects " + badCoat + " (a number 0..1)");
        }
        bool coatWithoutStrict = false;
        try { PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"glassCoatMask\":0.5}"), "FixtureBus", "coat fixture"); }
        catch (ArgumentException) { coatWithoutStrict = true; }
        check(coatWithoutStrict, "pack glass coat: pack.glassCoatMask without a shared glass surface does nothing, so it is refused");
        // pack.sharedGlass: the shared, saved glass surface without the strict palette import; strict implies it
        string shared = single.Replace("\"Bus02\"}", "\"Bus02\",\"sharedGlass\":true}");
        PackSettings sharedSettings = PackSettings.Parse(shared, "FixtureBus", "shared glass fixture");
        check(sharedSettings.SharedGlass && !sharedSettings.Strict, "pack shared glass: pack.sharedGlass alone is not the strict pipeline");
        check(PackSettings.Parse(strict, "FixtureBus", "shared glass fixture").SharedGlass, "pack shared glass: pack.strict implies it");
        check(!PackSettings.Parse(single, "FixtureBus", "shared glass fixture").SharedGlass &&
            !PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"sharedGlass\":false}"), "FixtureBus", "shared glass fixture").SharedGlass,
            "pack shared glass: absent or false keeps the importer's per-LOD glass");
        check(PackSettings.Parse(shared.Replace("\"sharedGlass\":true", "\"sharedGlass\":true,\"glassCoatMask\":0.4"), "FixtureBus", "coat fixture").GlassCoatMask == .4f,
            "pack shared glass: pack.glassCoatMask is read with pack.sharedGlass");
        foreach (string badShared in new[] { "1", "\"true\"", "[true]" })
        {
            bool rejected = false;
            try { PackSettings.Parse(single.Replace("\"Bus02\"}", "\"Bus02\",\"sharedGlass\":" + badShared + "}"), "FixtureBus", "bad shared glass fixture"); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "pack shared glass: rejects " + badShared + " (true or false)");
        }
        float3 frontAnchor = new float3(.1f, .8f, -1.2f), rearAnchor = new float3(-.1f, .8f, -.2f);
        float3 tractor = PackSettings.TractorOffset(frontAnchor, -1.1f);
        float3 trailer = PackSettings.TrailerOffset(rearAnchor, -.8f);
        check(math.distance(new float3(tractor.xy, -1.1f + tractor.z), frontAnchor) < 1e-6f,
            "assembly pack: tractor offset inverts native minimum-bound anchor formula");
        check(math.distance(new float3(trailer.xy, -.8f - trailer.z), rearAnchor) < 1e-6f,
            "assembly pack: trailer offset handles negative local front bounds without recentering");
    }
}
