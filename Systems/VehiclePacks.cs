using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver.Interior.Models;
using Game.Prefabs;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Vehicle packs: asset-only mods (a folder in the local Mods, or a Paradox Mods subscription) that bring one of our
    /// authored buses. Next to the bus asset &lt;model&gt;.cok lie the files only this mod reads: &lt;model&gt;.cdlb.json (the vehicle
    /// profile, VehicleProfileJson) and the runtime mesh it names (&lt;model&gt;.swb1.gz). The game loads the .cok by itself (an
    /// AI bus, with or without this mod) and ignores the other two; a driven bus is ours when its prefab came from a .cok that
    /// has a profile beside it. Found on demand from the prefab's own file (no folder scanning), once per package path; a bad
    /// profile is logged once and the bus is then driven like any other (our cab inside, its own body outside). Main thread.
    /// </summary>
    internal static class VehiclePacks
    {
        internal const string ProfileExtension = ".cdlb.json";

        private static readonly Dictionary<string, VehicleProfile> s_ByPackage = new Dictionary<string, VehicleProfile>(StringComparer.OrdinalIgnoreCase);
        // The answer for each prefab object already asked about (by reference): the key above costs two strings and a native call
        // for the name on every call, and the AI systems ask per vehicle. Only answers found under a key are kept; cleared (with
        // Generation moved on) when an assembly's profile replaced an answer already given for one of its sections.
        private static readonly Dictionary<PrefabBase, VehicleProfile> s_ByPrefab = new Dictionary<PrefabBase, VehicleProfile>(SamePrefab.Instance);
        // The pack folder of each profile (FolderOf), asked on every scan for each articulated bus.
        private static readonly Dictionary<VehicleProfile, string> s_Folders = new Dictionary<VehicleProfile, string>();

        /// <summary>Changes when an answer ForPrefab gave before may now be another: a cache of those answers (by prefab entity) is
        /// cleared when it changes.</summary>
        internal static int Generation { get; private set; }

        /// <summary>The authored profile of a vehicle prefab from a pack (registered with its variant), or null.</summary>
        internal static VehicleProfile ForPrefab(PrefabBase prefab)
        {
            if (prefab is object && s_ByPrefab.TryGetValue(prefab, out VehicleProfile cached))
                return cached;
            string package = prefab?.asset?.path;
            if (string.IsNullOrEmpty(package) || prefab.name == null)
                return null;
            string key = package.Replace('\\', '/') + "|" + prefab.name;
            if (s_ByPackage.TryGetValue(key, out VehicleProfile known))
            {
                s_ByPrefab[prefab] = known;
                return known;
            }

            VehicleProfile profile = null;
            string path = ProfilePath(package, prefab.name);
            if (path != null && File.Exists(path))
            {
                try
                {
                    profile = VehicleProfileJson.LoadForPrefab(path, prefab.name);
                    if (!string.Equals(profile.Name, prefab.name, StringComparison.Ordinal))
                        throw new VehicleProfileJson.Error($"the profile is for {profile.Name}, the asset is {prefab.name}");
                    if (profile.Assembly != null)
                    {
                        VehicleProfileJson.ValidateAssemblyEntrances(profile.Assembly, Path.GetDirectoryName(path));
                        VehicleModels.RegisterAssembly(profile.Assembly);
                        string prefix = package.Replace('\\', '/') + "|";
                        bool replaced = Answer(prefix+profile.Assembly.Front.Prefab, profile.Assembly.Front.Profile);
                        replaced |= Answer(prefix+profile.Assembly.Rear.Prefab, profile.Assembly.Rear.Profile);
                        if (replaced) { s_ByPrefab.Clear(); Generation++; }
                    }
                    else VehicleModels.Register(profile);
                    Mod.log.Info($"Vehicle pack {prefab.name}: {path} (mesh {profile.MeshSha256?.Substring(0, 12)}, {profile.Spec.FrontZ - profile.Spec.RearZ:0.00} m, {profile.Doors.Length} doors, {profile.MirrorCount} mirrors)");
                }
                catch (Exception e)
                {
                    profile = null;
                    Mod.log.Warn($"Vehicle pack {prefab.name} not used ({path}): {e.Message}. The bus is driven with the generic cab.");
                }
            }
            s_ByPackage[key] = profile;
            s_ByPrefab[prefab] = profile;
            return profile;
        }

        // Sets the answer for a key; true when it replaced another answer given before.
        private static bool Answer(string key, VehicleProfile profile)
        {
            bool replaced = s_ByPackage.TryGetValue(key, out VehicleProfile old) && old != profile;
            s_ByPackage[key] = profile;
            return replaced;
        }

        internal sealed class SamePrefab : IEqualityComparer<PrefabBase>
        {
            internal static readonly SamePrefab Instance = new SamePrefab();
            public bool Equals(PrefabBase a, PrefabBase b) => ReferenceEquals(a, b);
            public int GetHashCode(PrefabBase prefab) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(prefab);
        }

        /// <summary>The profile file a prefab's package would have: &lt;the .cok's folder&gt;/&lt;prefab name&gt;.cdlb.json.</summary>
        internal static string ProfilePath(string packagePath, string prefabName)
        {
            if (string.IsNullOrEmpty(packagePath) || !VehicleProfileJson.ModelName(prefabName))
                return null;
            string folder;
            try { folder = Path.GetDirectoryName(packagePath); }
            catch (ArgumentException) { return null; }
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, prefabName + ProfileExtension);
        }

        /// <summary>The folder of the pack a profile came from (null: not from a pack file).</summary>
        internal static string FolderOf(VehicleProfile profile)
        {
            if (string.IsNullOrEmpty(profile?.Source)) return null;
            if (s_Folders.TryGetValue(profile, out string folder)) return folder;
            folder = Path.GetDirectoryName(Path.GetFullPath(profile.Source));
            s_Folders[profile] = folder;
            return folder;
        }
    }
}
