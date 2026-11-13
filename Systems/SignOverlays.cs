using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Game.Prefabs;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Sign overlays: our destination signs for a bus asset by someone else, from a &lt;prefab name&gt;.cdlb-signs.json put beside its
    /// .cok (SignOverlay). The asset keeps its own body; the AI buses and the driven bus wear the signs. Found on demand from the
    /// prefab's own file, once per package path, as VehiclePacks; a vehicle pack's profile comes first (the callers ask it first).
    /// A bad file is logged once and the bus stays without signs. Main thread.
    /// </summary>
    internal static class SignOverlays
    {
        internal const string Extension = ".cdlb-signs.json";

        private static readonly Dictionary<string, SignOverlay> s_ByPackage = new Dictionary<string, SignOverlay>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<PrefabBase, SignOverlay> s_ByPrefab = new Dictionary<PrefabBase, SignOverlay>(VehiclePacks.SamePrefab.Instance);

        internal static SignOverlay ForPrefab(PrefabBase prefab)
        {
            if (prefab is object && s_ByPrefab.TryGetValue(prefab, out SignOverlay cached))
                return cached;
            string package = prefab?.asset?.path;
            if (string.IsNullOrEmpty(package) || prefab.name == null)
                return null;
            string key = package.Replace('\\', '/') + "|" + prefab.name;
            if (!s_ByPackage.TryGetValue(key, out SignOverlay overlay))
            {
                overlay = Load(package, prefab.name);
                s_ByPackage[key] = overlay;
            }
            s_ByPrefab[prefab] = overlay;
            return overlay;
        }

        /// <summary>A board's paint, cached by colour (the livery materials).</summary>
        internal static Material BoardMaterial(in SignOverlay.Backing backing)
        {
            float3 c = math.round(backing.Srgb);
            return CabMaterials.Livery(new Color32((byte)c.x, (byte)c.y, (byte)c.z, 255));
        }

        private static SignOverlay Load(string package, string prefabName)
        {
            string path = VehiclePacks.ProfilePath(package, prefabName);
            if (path == null) return null;
            path = path.Substring(0, path.Length - VehiclePacks.ProfileExtension.Length) + Extension;
            if (!File.Exists(path)) return null;
            try
            {
                SignOverlay overlay = VehicleProfileJson.ParseOverlay(File.ReadAllText(path, Encoding.UTF8), path);
                if (!string.Equals(overlay.Name, prefabName, StringComparison.Ordinal))
                    throw new VehicleProfileJson.Error($"the overlay is for {overlay.Name}, the asset is {prefabName}");
                Mod.log.Info($"Sign overlay {prefabName}: {path} (front {overlay.FrontSign.Matrix.x > 0}, side {overlay.SideSign.Matrix.x > 0}, rear {overlay.RearSign.Matrix.x > 0})");
                return overlay;
            }
            catch (Exception e) when (e is VehicleProfileJson.Error || e is IOException || e is UnauthorizedAccessException)
            {
                Mod.log.Warn($"Sign overlay {prefabName} not used ({path}): {e.Message}. The bus has no signs of ours.");
                return null;
            }
        }
    }
}
