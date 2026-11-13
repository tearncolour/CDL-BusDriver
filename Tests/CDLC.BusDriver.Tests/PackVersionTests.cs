using System;
using System.IO;
using CDLC.BusDriver.Interior.Models;

// The packer read pack profiles of versions 1 to 3 only, while the profile reader takes 1 to VehicleProfileJson.Version (4 since the
// parallel four-bar doors): the ZK6128H's version 4 profile ran in the game but could not be packed (the audit of 10 November). Both now
// ask VehicleProfileJson.Supported and IsAssembly.
static class PackVersionTests
{
    const string Prefix = "pack version: ";

    static string Solo(string version) => "{" + (version != null ? "\"version\":" + version + "," : "") + "\"model\":\"Fixture\",\"pack\":{\"template\":\"Bus01\"}}";

    static bool Packs(string json)
    {
        try { PackSettings.Parse(json, "Fixture", "fixture"); return true; }
        catch (ArgumentException) { return false; }
    }

    internal static void Run(Action<bool, string> check)
    {
        bool every = true;
        for (int v = 1; v <= VehicleProfileJson.Version; v++)
            every &= v == 2 || Packs(Solo(v.ToString()));     // 2 is always an assembly: its sections are checked elsewhere
        check(every && Packs(Solo(null)), Prefix + $"every single-section profile version the reader takes (1, 3 to {VehicleProfileJson.Version}, and none written) is packed");
        check(!Packs(Solo((VehicleProfileJson.Version + 1).ToString())) && !VehicleProfileJson.Supported(VehicleProfileJson.Version + 1),
            Prefix + "a version above the reader's is refused by both");
        check(!Packs(Solo("0")) && !Packs(Solo("3.5")) && !Packs(Solo("\"4\"")), Prefix + "version 0, a fraction or a string is refused");
        check(VehicleProfileJson.IsAssembly(2, false) && VehicleProfileJson.IsAssembly(4, true) && !VehicleProfileJson.IsAssembly(4, false) && !VehicleProfileJson.IsAssembly(1, true),
            Prefix + "an assembly is version 2, or 3 and later with sections");

        // the profile of the audit, where this machine has it
        string zk = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "Desktop", "BusDriverMod", "Tools", "ZK6128H", "vehicle.json");
        if (File.Exists(zk))
        {
            string text = File.ReadAllText(zk);
            bool packed;
            string why = "";
            try { packed = PackSettings.Parse(text, "ZK6128H", zk) != null; }
            catch (ArgumentException e) { packed = false; why = e.Message; }
            check(packed, Prefix + "the ZK6128H's version 4 profile is packed " + why);
        }
    }
}
