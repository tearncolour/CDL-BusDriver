using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver.Interior.Models;
using Game.Prefabs;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Station packs: asset-only mods (a folder in the local Mods, or a Paradox Mods subscription) that bring a bus station (a copy of one of the
    /// game's, with its own model; e.g. the CDL Placeable Kit's CDLC.Station.BusInterchange). Next to the station's &lt;prefab&gt;.cok lies the file
    /// only this mod reads: &lt;prefab&gt;.cdls.json (its LED screens, StationSignProfile), which BusStationBoardSystem draws live departures on. The
    /// game loads the .cok by itself (a working station with dark screens, with or without this mod). Found on demand from the prefab's own file
    /// (no folder scanning), once per package path; a bad file is logged once and the station's screens stay dark. Main thread.
    /// </summary>
    internal static class StationSignPacks
    {
        private static readonly Dictionary<string, StationSignProfile> s_ByPackage = new Dictionary<string, StationSignProfile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The screens of a building prefab from a station pack, or null.</summary>
        internal static StationSignProfile ForPrefab(PrefabBase prefab)
        {
            string package = prefab?.asset?.path;
            if (string.IsNullOrEmpty(package) || prefab.name == null || !VehicleProfileJson.ModelName(prefab.name))
                return null;
            string key = package.Replace('\\', '/') + "|" + prefab.name;
            if (s_ByPackage.TryGetValue(key, out StationSignProfile known))
                return known;

            StationSignProfile profile = null;
            string path = ProfilePath(package, prefab.name);
            if (path != null && File.Exists(path))
            {
                try
                {
                    profile = StationSignProfile.Load(path);
                    if (!string.Equals(profile.Prefab, prefab.name, StringComparison.Ordinal))
                        throw new StationSignProfile.Error($"the file is for {profile.Prefab}, the asset is {prefab.name}");
                    Mod.log.Info($"Station pack {prefab.name}: {path} ({profile.Stops.Length} bays, {profile.Screens.Length} screens)");
                }
                catch (Exception e)
                {
                    profile = null;
                    Mod.log.Warn($"Station pack {prefab.name}: its screens are not drawn ({path}): {e.Message}");
                }
            }

            s_ByPackage[key] = profile;
            return profile;
        }

        /// <summary>The station-sign file a prefab's package would have: &lt;the .cok's folder&gt;/&lt;prefab name&gt;.cdls.json.</summary>
        internal static string ProfilePath(string packagePath, string prefabName)
        {
            if (string.IsNullOrEmpty(packagePath))
                return null;
            string folder;
            try { folder = Path.GetDirectoryName(packagePath); }
            catch (ArgumentException) { return null; }
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, prefabName + StationSignProfile.Extension);
        }
    }
}
