using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// How an authored bus is packed (the "pack" block of its Tools/&lt;model&gt;/vehicle.json, kept in the pack's profile too): the
    /// vanilla bus it copies, its capacity, its body colour without a line, the strict pipeline flag and the vehicle prefab's
    /// identity in saves, its asset menu icon. Pure data, read by the in-game packer (BusVehicleAssetSystem) and checked by the tests.
    /// </summary>
    internal sealed class PackSettings
    {
        internal string Template;                 // Bus01 (diesel) or Bus02 (electric)
        internal int Capacity;                    // 0: the template's
        internal float3 Livery = new float3(1f);  // RGB 0..1
        internal bool Strict;
        // Omitted keeps the legacy nine-layer contract. Explicit lists describe authored native lights, not runtime LED overlays.
        internal int[] LightLayers = Enumerable.Range(1, 9).ToArray();
        // pack.sharedGlass (implied by pack.strict): the three LODs share one glass surface, saved with its coat and with transparent
        // SSR and the transparent depth prepass off (the dot pattern over BH/GlsShader glass); the palette import stays strict-only
        internal bool SharedGlass;
        /// <summary>The glass coat (_CoatMask) the strict pipeline gives this model's shared glass surface when its pack block does not say:
        /// the value audited in game on the first strict bus (SWB6109EV12G). Gls MaskMap G is AO, so the coat is a separate float.</summary>
        internal const float AuditedGlassCoatMask = .65f;
        // pack.glassCoatMask (0..1, only with a shared glass surface): this model's own glass coat; absent: AuditedGlassCoatMask
        internal float GlassCoatMask = AuditedGlassCoatMask;
        internal float? GlassNormalStrength;
        // pack.icon: the file name of a PNG beside the profile, the bus's icon in the game's asset menus; null: the game renders one
        internal string Icon;
        // The vehicle prefab's GUID (PrefabID = type + name + GUID in a save), frozen at the value already in use so a repack keeps
        // it; null: whatever the import path gives. ObsoleteGuids: every earlier GUID saves may still hold, they resolve to this
        // prefab. Both 32 lower case hex digits.
        internal string Guid;
        internal string[] ObsoleteGuids = new string[0];
        internal string AssemblyModel;
        internal Section[] Sections = new Section[0];
        internal sealed class Section
        {
            internal string Id, Prefab, Guid;
            internal int Capacity;
            internal string[] ObsoleteGuids = new string[0];
        }

        internal static PackSettings Parse(string profileText, string model, string source)
        {
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(profileText) as Dictionary<string, object>; }
            catch (MiniJson.FormatError e) { throw new ArgumentException($"{source}: not valid JSON: {e.Message}"); }
            if (root == null || !(root.TryGetValue("model", out object m) && m is string name))
                throw new ArgumentException($"{source} is not the vehicle profile of {model}");
            // the version as the profile reader takes it (absent: 1), so the packer packs every profile Bus Driver runs and refuses the same ones
            int profileVersion = 1;
            if (root.TryGetValue("version", out object version))
            {
                if (!(version is MiniJson.Number number) || number.Double != Math.Floor(number.Double) || !VehicleProfileJson.Supported((int)number.Double))
                    throw new ArgumentException($"{source}: unsupported pack profile version (this Bus Driver reads 1 to {VehicleProfileJson.Version})");
                profileVersion = (int)number.Double;
            }
            bool assembly = VehicleProfileJson.IsAssembly(profileVersion, root.ContainsKey("sections"));
            if (!assembly && !string.Equals(name, model, StringComparison.Ordinal))
                throw new ArgumentException($"{source} is not the vehicle profile of {model}");
            if (!(root.TryGetValue("pack", out object p) && p is Dictionary<string, object> pack))
                throw new ArgumentException($"{source} has no \"pack\" block");

            var s = new PackSettings();
            if (pack.TryGetValue("lightLayers", out object layers))
            {
                if (assembly || !(layers is List<object> ids) || ids.Count == 0 || ids.Count > 9 ||
                    ids.Any(x => !(x is MiniJson.Number n) || n.Double != Math.Floor(n.Double) || n.Double < 1 || n.Double > 9))
                    throw new ArgumentException($"{source}: pack.lightLayers must be a nonempty list of unique integer IDs 1..9 for a single-section vehicle");
                s.LightLayers = ids.Select(x => (int)((MiniJson.Number)x).Double).OrderBy(x => x).ToArray();
                if (s.LightLayers.Distinct().Count() != s.LightLayers.Length)
                    throw new ArgumentException($"{source}: pack.lightLayers contains duplicate IDs");
            }
            s.Template = pack.TryGetValue("template", out object t) && t is string ts ? ts : throw new ArgumentException($"{source}: pack.template");
            if (s.Template != "Bus01" && s.Template != "Bus02")
                throw new ArgumentException($"{source}: pack.template must be Bus01 or Bus02");
            if (pack.TryGetValue("capacity", out object c))
            {
                if (!(c is MiniJson.Number cn) || cn.Double != Math.Floor(cn.Double) || cn.Double < 0 || cn.Double > 500)
                    throw new ArgumentException($"{source}: pack.capacity must be a whole number 0..500");
                s.Capacity = (int)cn.Double;
            }
            if (pack.TryGetValue("livery", out object l))
            {
                if (!(l is List<object> lc) || lc.Count != 3 || !lc.TrueForAll(x => x is MiniJson.Number))
                    throw new ArgumentException($"{source}: pack.livery must be three numbers");
                float3 rgb = new float3(((MiniJson.Number)lc[0]).Float, ((MiniJson.Number)lc[1]).Float, ((MiniJson.Number)lc[2]).Float);
                if (!math.all(rgb >= 0f & rgb <= 1f)) throw new ArgumentException($"{source}: pack.livery must be 0..1");
                s.Livery = rgb;
            }
            s.Strict = pack.TryGetValue("strict", out object st) && st is bool b && b;
            if (pack.TryGetValue("sharedGlass", out object sg) && sg != null && !(sg is bool))
                throw new ArgumentException($"{source}: pack.sharedGlass must be true or false");
            s.SharedGlass = s.Strict || sg is bool shared && shared;
            if (pack.TryGetValue("glassNormalStrength", out object normalStrength))
            {
                if (!(normalStrength is MiniJson.Number strength) || !(strength.Double >= 0 && strength.Double <= 1))
                    throw new ArgumentException($"{source}: pack.glassNormalStrength must be a number 0..1");
                s.GlassNormalStrength = strength.Float;
            }
            if (pack.TryGetValue("glassCoatMask", out object gcm) && gcm != null)
            {
                if (!s.SharedGlass)
                    throw new ArgumentException($"{source}: pack.glassCoatMask belongs to a shared glass surface (pack.sharedGlass or pack.strict) and does nothing without it");
                if (!(gcm is MiniJson.Number gn) || !(gn.Double >= 0 && gn.Double <= 1))
                    throw new ArgumentException($"{source}: pack.glassCoatMask must be a number 0..1");
                s.GlassCoatMask = gn.Float;
            }
            if (pack.TryGetValue("icon", out object ic) && ic != null)
            {
                if (!(ic is string icon) || !IconName(icon))
                    throw new ArgumentException($"{source}: pack.icon must be the name of a .png file beside the profile (letters, digits, - _ .)");
                s.Icon = icon;
            }

            if (pack.TryGetValue("guid", out object g) && g != null)
                s.Guid = RequireGuid(g as string, source + " pack.guid");
            var obsolete = new List<string>();
            if (pack.TryGetValue("obsoleteGuids", out object og) && og != null)
            {
                if (!(og is List<object> list)) throw new ArgumentException($"{source}: pack.obsoleteGuids must be a list");
                foreach (object o in list)
                {
                    string old = RequireGuid(o as string, source + " pack.obsoleteGuids");
                    if (old == s.Guid) throw new ArgumentException($"{source}: pack.obsoleteGuids lists the current guid (the package adds its own alias)");
                    if (!obsolete.Contains(old)) obsolete.Add(old);
                }
            }
            if (obsolete.Count > 0 && s.Guid == null)
                throw new ArgumentException($"{source}: pack.obsoleteGuids needs pack.guid (aliases belong to a prefab with a fixed identity)");
            s.ObsoleteGuids = obsolete.ToArray();
            if (assembly)
            {
                if (!(root.TryGetValue("sections", out object sections) && sections is List<object> entries) || entries.Count != 2 ||
                    !(pack.TryGetValue("sections", out object packing) && packing is Dictionary<string, object> byId))
                    throw new ArgumentException($"{source}: v2 requires two sections and explicit pack.sections settings");
                var native = new List<Section>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var prefabs = new HashSet<string>(StringComparer.Ordinal);
                foreach (object entry in entries)
                {
                    if (!(entry is Dictionary<string, object> section) ||
                        !(section.TryGetValue("id", out object idValue) && idValue is string id) || !VehicleProfileJson.ModelName(id) || !ids.Add(id) ||
                        !(section.TryGetValue("prefab", out object prefabValue) && prefabValue is string prefab) ||
                        !VehicleProfileJson.ModelName(prefab) || !prefabs.Add(prefab) ||
                        !(byId.TryGetValue(id, out object settingsValue) && settingsValue is Dictionary<string, object> settings))
                        throw new ArgumentException($"{source}: invalid or missing pack section identity");
                    if (!NativeMainName(prefab))
                        throw new ArgumentException($"{source}: section {id} native prefab '{prefab}' must be a single ASCII alphanumeric main name; underscores are reserved by the native asset importer");
                    if (!(settings.TryGetValue("capacity", out object capacity) && capacity is MiniJson.Number n) ||
                        n.Double != Math.Floor(n.Double) || n.Double < 0 || n.Double > 500)
                        throw new ArgumentException($"{source}: pack.sections.{id}.capacity must be a whole number 0..500");
                    if (!(settings.TryGetValue("guid", out object guid) && guid is string guidText))
                        throw new ArgumentException($"{source}: pack.sections.{id}.guid must explicitly preserve native identity");
                    var item = new Section { Id = id, Prefab = prefab, Capacity = (int)n.Double,
                        Guid = RequireGuid(guidText, source + " pack.sections." + id + ".guid") };
                    if (settings.TryGetValue("obsoleteGuids", out object aliases))
                    {
                        if (!(aliases is List<object> aliasList)) throw new ArgumentException($"{source}: pack.sections.{id}.obsoleteGuids");
                        item.ObsoleteGuids = aliasList.Select(a => RequireGuid(a as string, source + " section alias")).Distinct().ToArray();
                        if (item.ObsoleteGuids.Contains(item.Guid)) throw new ArgumentException($"{source}: section aliases its current GUID");
                    }
                    native.Add(item);
                }
                if (byId.Count != entries.Count || native.Sum(x => x.Capacity) != s.Capacity || s.Capacity <= 0 ||
                    native.Select(x => x.Guid).Distinct().Count() != native.Count)
                    throw new ArgumentException($"{source}: section capacities must sum to positive pack.capacity; native GUIDs must differ");
                var allIdentities = new HashSet<string>(StringComparer.Ordinal);
                foreach (Section item in native)
                    foreach (string identity in new[] { item.Guid }.Concat(item.ObsoleteGuids))
                        if (!allIdentities.Add(identity)) throw new ArgumentException($"{source}: native section identities overlap");
                if (!string.Equals(name, model, StringComparison.Ordinal) && !prefabs.Contains(model))
                    throw new ArgumentException($"{source} does not declare prefab {model}");
                s.AssemblyModel = name;
                s.Sections = native.ToArray();
            }
            return s;
        }

        /// <summary>A plain file name of a PNG in the profile's own folder: no folders, no path tricks.</summary>
        internal static bool IconName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= 64 && name[0] != '.' && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            name.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.');

        // VehicleInitializeSystem derives local anchors from the imported geometry bounds.
        // Keep the inverse in pure code so asymmetric section origins are covered offline.
        internal static float3 TractorOffset(float3 anchor, float boundsMinZ) =>
            new float3(anchor.x, anchor.y, anchor.z - boundsMinZ);
        internal static float3 TrailerOffset(float3 anchor, float boundsMaxZ) =>
            new float3(anchor.x, anchor.y, boundsMaxZ - anchor.z);

        // Native AssetUtils.ParseName treats underscore tokens as LOD, material and other suffixes.
        // The assembly/model ID may contain underscores; its explicitly declared native main names may not.
        internal static bool NativeMainName(string name) => VehicleProfileJson.ModelName(name) && name.IndexOf('_') < 0;

        /// <summary>A GUID as the game writes it (32 hex digits, either case), as lower case.</summary>
        internal static string RequireGuid(string text, string what)
        {
            if (text == null || text.Length != 32 || !text.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'))
                throw new ArgumentException($"{what}: '{text}' is not a 32 digit hex GUID");
            return text.ToLowerInvariant();
        }
    }
}
