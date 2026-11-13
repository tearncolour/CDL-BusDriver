using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver.Interior.Models;

/// <summary>The repository's root (the folder with CDLC.BusDriver.csproj): CDLC_BUSDRIVER_REPO if set (a host that loads a copy
/// of the test assembly, Tools/SWB6109EV12G/render_runtime_review.ps1), else found above the test binary.</summary>
static class Repo
{
    static string s_Root;
    internal static string Root
    {
        get
        {
            if (s_Root != null) return s_Root;
            string set = Environment.GetEnvironmentVariable("CDLC_BUSDRIVER_REPO");
            if (!string.IsNullOrEmpty(set) && File.Exists(Path.Combine(set, "CDLC.BusDriver.csproj"))) return s_Root = set;
            foreach (string start in new[] { Path.GetDirectoryName(typeof(Repo).Assembly.Location), AppContext.BaseDirectory })
                for (string dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                    if (File.Exists(Path.Combine(dir, "CDLC.BusDriver.csproj"))) return s_Root = dir;
            throw new DirectoryNotFoundException("Cannot find the repository root (CDLC.BusDriver.csproj) above " + AppContext.BaseDirectory +
                "; set CDLC_BUSDRIVER_REPO");
        }
    }
}

/// <summary>
/// The authored vehicles as the mod meets them: their vehicle packs (Packs/CDLC.Bus.&lt;model&gt;/, made by
/// Tools/VehiclePack/make_pack.py from the build), read with the mod's own loader and registered once per run.
/// </summary>
static class TestPacks
{
    static readonly Dictionary<string, VehicleProfile> s_Loaded = new Dictionary<string, VehicleProfile>(StringComparer.OrdinalIgnoreCase);

    internal static string Folder(string model) => Path.Combine(Repo.Root, "Packs", "CDLC.Bus." + model);
    internal static string ProfilePath(string model) => Path.Combine(Folder(model), model + ".cdlb.json");

    internal static VehicleProfile Profile(string model)
    {
        if (s_Loaded.TryGetValue(model, out VehicleProfile p)) return p;
        string path = ProfilePath(model);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No vehicle pack for {model} at {path}: run python Tools/VehiclePack/make_pack.py {model} first");
        p = VehicleModels.Register(VehicleProfileJson.Load(path));
        s_Loaded[model] = p;
        return p;
    }

    internal static BusInteriorSpec Spec(string model) => Profile(model).Spec;
}
