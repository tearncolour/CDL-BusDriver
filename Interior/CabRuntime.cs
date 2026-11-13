using System;
using System.Collections.Generic;
using System.Threading;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Bus material recipes and posed instances. Shared upload and cleanup run on the main thread.
namespace CDLC.BusDriver.Interior
{
    internal static class CabCleanup
    {
        static readonly HashSet<IDisposable> s_Pending = new HashSet<IDisposable>();

        internal static void Retain(IDisposable resource) => s_Pending.Add(resource);
        internal static void Retain(UnityEngine.Object resource) => Retain(new OwnedObject(resource));

        sealed class OwnedObject : IDisposable
        {
            UnityEngine.Object m_Value;
            internal OwnedObject(UnityEngine.Object value) { m_Value = value; }
            public void Dispose()
            {
                if (m_Value == null) return;
                UnityEngine.Object.Destroy(m_Value);
                m_Value = null;
            }
        }

        internal static void RetryPending()
        {
            var failures = new List<Exception>();
            foreach (IDisposable resource in new List<IDisposable>(s_Pending))
                if (Try(resource.Dispose, failures)) s_Pending.Remove(resource);
            Try(CDLC.Asset.Led.LedPanel.RetryRetained, failures);
            ThrowIfAny(failures, "Retained runtime resource cleanup failed.");
        }

        internal static bool Try(Action action, List<Exception> failures)
        {
            try { action(); return true; }
            catch (Exception error) { failures.Add(error); return false; }
        }

        internal static void ThrowIfAny(List<Exception> failures, string message)
        {
            if (failures.Count != 0) throw new AggregateException(message, failures);
        }

        internal static void DestroyOrRetain(UnityEngine.Object value, List<Exception> failures)
        {
            if (!Try(() => UnityEngine.Object.Destroy(value), failures)) Retain(value);
        }

        internal static void FailedAllocation(UnityEngine.Object value, Exception failure)
        {
            if (value == null) return;
            try { UnityEngine.Object.Destroy(value); }
            catch (Exception cleanup) { Retain(value); throw new AggregateException("Allocation and cleanup failed.", failure, cleanup); }
        }
    }

    internal static class CabMaterials
    {
        // Same values as the kit's roller-verified recipe: lamps 300 nits, tail 150, beacon 3000 (flashing), glow (LED) 60.
        const float kLampNits = 300f, kTailNits = 150f, kBeaconNits = 1500f, kGlowNits = 60f;
        internal const int kQueueOpaqueDecal = 2225;       // HDRenderQueue.Priority.OpaqueDecal: the first queue the depth prepass takes (see MaterialAuditRules.InDecalQueue)
        static Material[] s_Base;                 // per Fin (lights: off state)
        static Material s_LampOn, s_TailOn, s_BeaconOn;
        static readonly Texture2D[] s_Palette = new Texture2D[(int)Fin.Count];
        static readonly Dictionary<int, Material[]> s_Sets = new Dictionary<int, Material[]>();
        static float s_BeaconLevel = -1f;

        static int s_ResourceUsers;
        static bool s_ClearRequested;
        static readonly HashSet<UnityEngine.Object> s_PendingDestroy = new HashSet<UnityEngine.Object>();
        static int s_ResourceThreadId;

        static void CheckResourceAccess()
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            if (s_ResourceThreadId == 0) s_ResourceThreadId = thread;
            else if (thread != s_ResourceThreadId)
                throw new InvalidOperationException("Shared cab resources must be managed on their owning main thread.");
        }

        sealed class Resources : IDisposable
        {
            bool m_Disposed;
            public void Dispose()
            {
                CheckResourceAccess();
                if (m_Disposed)
                {
                    if (s_ResourceUsers == 0 && s_ClearRequested) DestroyAllCore();
                    return;
                }
                m_Disposed = true;
                s_ResourceUsers--;
                if (s_ResourceUsers == 0 && s_ClearRequested) DestroyAllCore();
            }
        }

        /// <summary>Retains this Bus material pool; a requested clear waits for its last main-thread lease.</summary>
        internal static IDisposable AcquireResources()
        {
            CheckResourceAccess();
            var resources = new Resources();
            checked { s_ResourceUsers++; }
            return resources;
        }

        /// <summary>
        /// A material on HDRP/Lit. gameShader: moved to the game's shader where it is made (GameShaderMove.Take: the game's decals decide what they land on by a property HDRP/Lit has not), for
        /// everything that does not glow; the lamps, the LED glow and the card signs stay on HDRP/Lit (the game's graph reads an emission in nits differently; the cards were not looked at).
        /// </summary>
        static Material MakeLit(string name, float3 srgb, Texture2D map, float metallic, float smooth, bool gameShader = false)
        {
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null)
                return null;

            Material m = null;
            try
            {
                m = new Material(shader);
                m.name = name;
                m.hideFlags = HideFlags.DontSave;
                m.enableInstancing = true;
                var c = new Color(srgb.x, srgb.y, srgb.z, 1f);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                if (m.HasProperty("_Color")) m.SetColor("_Color", c);
                if (map != null && m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", map);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
                if (m.HasProperty("_SupportDecals")) m.SetFloat("_SupportDecals", 0f);
                m.SetFloat("colossal_DecalLayerMask", math.asfloat((int)Game.Rendering.DecalLayers.Vehicles));
                if (m.HasProperty("_EmissiveColor")) m.SetVector("_EmissiveColor", Vector4.zero);
                try { HDMaterial.ValidateMaterial(m); }
                catch (Exception ex) { Mod.log.Warn($"Cab interior: ValidateMaterial failed: {ex.Message}"); }
                // This depth queue prevents the road's decals being projected through the bus.
                m.renderQueue = kQueueOpaqueDecal;
                if (gameShader) GameShaderMove.Take(m);
                return m;
            }
            catch (Exception failure) { CabCleanup.FailedAllocation(m, failure); throw; }
        }

        static void SetEmission(Material m, Fin f, float nits)
        {
            if (m == null || !m.HasProperty("_EmissiveColor")) return;
            float3 lin = KitPalette.ToLinear(KitPalette.EmissiveSrgb(f)) * nits;
            m.SetVector("_EmissiveColor", new Vector4(lin.x, lin.y, lin.z, 1f));
        }

        static Texture2D Palette(Fin f)
        {
            if (s_Palette[(int)f] != null) return s_Palette[(int)f];
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(KitPalette.Width, KitPalette.Height, TextureFormat.RGBA32, false, false);
                tex.name = "Bus Driver cab palette " + f;
                tex.hideFlags = HideFlags.DontSave;
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.LoadRawTextureData(KitPalette.PaletteBytes(f));
                tex.Apply(false, true);
                return s_Palette[(int)f] = tex;
            }
            catch (Exception failure) { CabCleanup.FailedAllocation(tex, failure); throw; }
        }

        static void Ensure()
        {
            if (s_Base != null) return;
            var created = new Material[(int)Fin.Count];
            var previousPalette = (Texture2D[])s_Palette.Clone();
            Material lampOn = null, tailOn = null, beaconOn = null;
            try
            {
                for (int f = 0; f < (int)Fin.Count; f++)
                {
                    var fin = (Fin)f;
                    Texture2D map = KitPalette.UsesPalette(fin) ? Palette(fin) : null;
                    bool light = KitPalette.IsLight(fin);
                    created[f] = MakeLit("Bus Driver cab " + fin, KitPalette.FlatSrgb(fin), map, light ? 0f : KitPalette.Metallic(fin), light ? 0.85f : KitPalette.Smoothness(fin), gameShader: fin != Fin.Glow);
                }
                SetEmission(created[(int)Fin.Glow], Fin.Glow, kGlowNits);          // LED displays are always lit
                lampOn = MakeLit("Bus Driver cab Lamp On", KitPalette.FlatSrgb(Fin.Lamp), null, 0f, 0.85f);
                tailOn = MakeLit("Bus Driver cab Tail On", KitPalette.FlatSrgb(Fin.Tail), null, 0f, 0.85f);
                beaconOn = MakeLit("Bus Driver cab Beacon", KitPalette.FlatSrgb(Fin.Beacon), null, 0f, 0.85f);
                SetEmission(lampOn, Fin.Lamp, kLampNits);
                SetEmission(tailOn, Fin.Tail, kTailNits);
                s_Base = created;
                s_LampOn = lampOn;
                s_TailOn = tailOn;
                s_BeaconOn = beaconOn;
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                foreach (Material material in created)
                    if (material != null) CabCleanup.DestroyOrRetain(material, failures);
                foreach (Material material in new[] { lampOn, tailOn, beaconOn })
                    if (material != null) CabCleanup.DestroyOrRetain(material, failures);
                for (int i = 0; i < s_Palette.Length; i++)
                {
                    Texture2D texture = s_Palette[i];
                    if (previousPalette[i] != null || texture == null) continue;
                    s_Palette[i] = null;
                    CabCleanup.DestroyOrRetain(texture, failures);
                }
                if (failures.Count > 1) throw new AggregateException("Shared material creation and cleanup failed.", failures);
                throw;
            }
        }

        static readonly Dictionary<long, Material> s_Lamps = new Dictionary<long, Material>();
        static readonly Dictionary<int, Material> s_Liveries = new Dictionary<int, Material>();

        /// <summary>
        /// A lit lamp in its own colour (sRGB) and brightness (nits): the bus's outside lamps, the LED signs outdoors, the warning
        /// lamps of the cluster. Cached per colour and brightness.
        /// </summary>
        internal static Material Lamp(float3 srgb, float nits)
        {
            long key = ((long)math.round(srgb.x * 255f) << 40) | ((long)math.round(srgb.y * 255f) << 32) | ((long)math.round(srgb.z * 255f) << 24) | (long)math.round(math.max(0f, nits));
            if (s_Lamps.TryGetValue(key, out Material m) && m != null) return m;
            m = MakeLit("Bus Driver lamp " + key.ToString("x"), srgb, null, 0f, 0.85f);
            try
            {
                if (m != null && m.HasProperty("_EmissiveColor"))
                {
                    float3 lin = KitPalette.ToLinear(srgb) * nits;
                    m.SetVector("_EmissiveColor", new Vector4(lin.x, lin.y, lin.z, 1f));
                }
                return s_Lamps[key] = m;
            }
            catch (Exception failure) { CabCleanup.FailedAllocation(m, failure); throw; }
        }

        sealed class Dimmed
        {
            internal Material Material;
            internal float3 Srgb;
            internal float DayNits, NightNits;
        }

        static readonly Dictionary<long, Dimmed> s_Dimmed = new Dictionary<long, Dimmed>();
        static float s_DimLevel = -1f;

        /// <summary>
        /// A lamp whose brightness follows the daylight like a real LED's auto-dimming (SetDaylight): dayNits in sunshine,
        /// nightNits in the dark. Without it the outside signs and lamps bloomed into blobs at night, when the game's exposure is
        /// opened up. Cached per colour and levels.
        /// </summary>
        internal static Material DimmedLamp(float3 srgb, float dayNits, float nightNits)
        {
            long key = ((long)math.round(srgb.x * 255f) << 48) | ((long)math.round(srgb.y * 255f) << 40) | ((long)math.round(srgb.z * 255f) << 32) |
                       ((long)math.round(math.max(0f, dayNits)) << 16) | (long)math.round(math.clamp(nightNits, 0f, 65535f));
            if (s_Dimmed.TryGetValue(key, out Dimmed d) && d.Material != null) return d.Material;
            Material m = MakeLit("Bus Driver dimmed lamp " + key.ToString("x"), srgb, null, 0f, 0.85f);
            if (m == null) return null;
            try
            {
                d = new Dimmed { Material = m, Srgb = srgb, DayNits = dayNits, NightNits = nightNits };
                Dim(d, s_DimLevel < 0f ? 1f : s_DimLevel);
                s_Dimmed[key] = d;
                return m;
            }
            catch (Exception failure) { CabCleanup.FailedAllocation(m, failure); throw; }
        }

        /// <summary>
        /// The daylight (the game's LightingSystem.dayLightBrightness: 0 night .. 1 midday sun) for the dimmed lamps: full
        /// brightness from a third of the midday sun, the night level below dusk, log-linear between (the eye's scale). Only a
        /// changed level is written.
        /// </summary>
        internal static void SetDaylight(float daylight)
        {
            float level = math.round(math.smoothstep(0f, 0.3f, math.saturate(daylight)) * 64f) / 64f;
            if (level == s_DimLevel) return;
            s_DimLevel = -1f;
            foreach (Dimmed d in s_Dimmed.Values) Dim(d, level);
            s_DimLevel = level;
        }

        static void Dim(Dimmed d, float level)
        {
            if (d.Material == null || !d.Material.HasProperty("_EmissiveColor")) return;
            float nits = d.NightNits > 0f && d.DayNits > 0f ? d.NightNits * math.pow(d.DayNits / d.NightNits, level) : math.lerp(d.NightNits, d.DayNits, level);
            float3 lin = KitPalette.ToLinear(d.Srgb) * nits;
            d.Material.SetVector("_EmissiveColor", new Vector4(lin.x, lin.y, lin.z, 1f));
        }

        /// <summary>
        /// A printed board's material (a card sign): its picture as the colour, lit by the scene only. It has no light of its own: the
        /// game's HDRP/Lit has no emissive-map variant, so a light through the picture lit the whole board plain white at night (the owner,
        /// 26 October). One per picture; the caller gives it back with ReleaseCard.
        /// </summary>
        internal static Material Card(Texture2D picture) => MakeLit("Bus Driver card sign", new float3(1f, 1f, 1f), picture, 0f, 0.3f);

        /// <summary>A card sign's material destroyed.</summary>
        internal static void ReleaseCard(Material m)
        {
            if (m != null) UnityEngine.Object.Destroy(m);
        }

        /// <summary>
        /// Paint cached by RGB and finish; another line never recolours a previously returned material. A glossy livery (WhiteGloss)
        /// has the smoothness of its row's finish, so it matches the glossy exterior paints beside it.
        /// </summary>
        internal static Material Livery(Color32 colour, bool gloss = false)
        {
            int key = (colour.r << 16) | (colour.g << 8) | colour.b | (gloss ? 1 << 24 : 0);
            if (s_Liveries.TryGetValue(key, out Material cached) && cached != null) return cached;
            float smoothness = gloss ? KitPalette.Smoothness(KitPalette.FinOf(Sw.WhiteGloss)) : 0.55f;
            return s_Liveries[key] = MakeLit((gloss ? "Bus Driver livery gloss " : "Bus Driver livery ") + (key & 0xffffff).ToString("x6"), new float3(colour.r, colour.g, colour.b) / 255f, null, 0f, smoothness, gameShader: true);
        }

        // The game's own material the body wraps are made from: its cut-out ("DefaultCutout", BH/SG_DefaultShader, queue 2475). The
        // HDRP/Lit of the game's build has no alpha variants: a cut-out or blended Lit material is drawn opaque, the colour under the
        // picture's see-through pixels and all (measured in the game 18 October: the see-through half of a test picture came out magenta in
        // every Lit setting and was cut out in the game's own cut-out materials). The hard cut-out (alpha .5) is steady: the alpha-hashed one
        // ("DefaultHashedCutout") dithers its soft edges into dots that only the game's temporal anti-aliasing smooths. Found by name in the
        // game's material library once.
        const string kWrapTemplateName = "DefaultCutout", kWrapTemplateFallback = "DefaultHashedCutout";
        static Material s_WrapTemplate;
        static bool s_WrapLogged;

        static Material WrapTemplate()
        {
            if (s_WrapTemplate != null) return s_WrapTemplate;
            Material fallback = null;
            for (int index = 0; index < 128; index++)
            {
                Material candidate;
                try { candidate = Colossal.IO.AssetDatabase.AssetDatabase.global.resources.materialLibrary.GetMaterial(index); }
                catch (Exception) { break; }
                if (candidate == null) break;
                if (candidate.name == kWrapTemplateName) return s_WrapTemplate = candidate;
                if (candidate.name == kWrapTemplateFallback) fallback = candidate;
            }
            if (fallback != null)
                Mod.log.Warn($"Wrap material: the game's \"{kWrapTemplateName}\" is not in its material library; using \"{kWrapTemplateFallback}\" (dithered edges).");
            else
                Mod.log.Warn($"Wrap material: neither \"{kWrapTemplateName}\" nor \"{kWrapTemplateFallback}\" is in the game's material library: the wrap is drawn with HDRP/Lit, which ignores the picture's alpha.");
            return s_WrapTemplate = fallback;
        }

        /// <summary>
        /// A body wrap's material: its picture, cut out by its alpha with the game's own alpha-hashed cut-out material (a new material: the
        /// caller destroys it). Without the game's material (not found in its library) the old HDRP/Lit one, which draws the picture opaque.
        /// </summary>
        internal static Material Wrap(Texture2D picture)
        {
            Material template = WrapTemplate();
            if (template == null)
            {
                Material lit = MakeLit("Bus Driver wrap (Lit)", new float3(1f, 1f, 1f), picture, 0f, 0.5f);
                if (lit != null && lit.HasProperty("_BaseColorMap")) lit.SetTexture("_BaseColorMap", picture);
                return lit;
            }
            Material m = null;
            try
            {
                m = new Material(template);
                m.name = "Bus Driver wrap";
                m.hideFlags = HideFlags.DontSave;
                if (m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", picture);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.5f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                if (m.HasProperty("_AlphaCutoff")) m.SetFloat("_AlphaCutoff", 0.5f);
                // fully in: the batch system feeds the fade per instance, a plain renderer gets none and the shader would dither the skin as if half faded out
                m.SetFloat("colossal_LodFade", 1f);
                // What the library's material had before the decal settings below (logged once): a copy keeps them.
                string decalFrom = s_WrapLogged ? null : $"queue {m.renderQueue}, _SupportDecals {(m.HasProperty("_SupportDecals") ? m.GetFloat("_SupportDecals").ToString("0.##") : "absent")}, colossal_DecalLayerMask {(m.HasProperty("colossal_DecalLayerMask") ? math.asint(m.GetFloat("colossal_DecalLayerMask")).ToString() : "absent")}";
                // The road's decals (lane lines, paving) are drawn into the decal buffer against the depth of the decal-receiving queue (see MakeLit):
                // the library's cut-out sits in the alpha-test queue (2475), outside that depth, so the paving under the bus was drawn onto the wrap
                // (seen from above: the roof and the sides in the ground's cobbles). Same as the cab's materials: the decal-receiving queue, no decals
                // taken, the vehicle layer. Whether the cut-out still cuts out in that queue is measured by the wrap probe (variants=1), not assumed.
                if (m.HasProperty("_SupportDecals")) m.SetFloat("_SupportDecals", 0f);
                if (m.HasProperty("colossal_DecalLayerMask")) m.SetFloat("colossal_DecalLayerMask", math.asfloat((int)Game.Rendering.DecalLayers.Vehicles));
                // SetFloat alone left the shader's _DISABLE_DECALS keyword off (wrap probe, 18 October: only ValidateMaterial turned it on, and with it
                // the cut-out and the red half's brightness stayed the same), so the keyword follows the float here, then the queue as MakeLit does.
                try { HDMaterial.ValidateMaterial(m); }
                catch (Exception ex) { Mod.log.Warn($"Wrap material: ValidateMaterial failed: {ex.Message}"); }
                m.renderQueue = kQueueOpaqueDecal;
                if (!s_WrapLogged)
                {
                    s_WrapLogged = true;
                    var passes = new System.Text.StringBuilder();
                    for (int i = 0; i < m.passCount; i++)
                        passes.Append(' ').Append(m.GetPassName(i)).Append(m.GetShaderPassEnabled(m.GetPassName(i)) ? "" : "(off)");
                    var names = new System.Text.StringBuilder();
                    Shader shader = m.shader;
                    for (int i = 0; i < shader.GetPropertyCount() && i < 80; i++)
                        names.Append(' ').Append(shader.GetPropertyName(i));
                    // what the library's material brings with it (a copy keeps its textures and values): the textures the shader reads besides the picture
                    // (a white default mask or an emissive map would make the wrap glossy or glowing) and the values of the lighting inputs
                    var inputs = new System.Text.StringBuilder();
                    foreach (string texture in new[] { "_MaskMap", "_NormalMap", "_EmissiveColorMap", "_ControlMask" })
                        inputs.Append(' ').Append(texture).Append(m.HasProperty(texture) ? "=" + (m.GetTexture(texture) != null ? m.GetTexture(texture).name : "none") : "(absent)");
                    foreach (string value in new[] { "_Metallic", "_Smoothness", "_EmissiveIntensity", "_AlbedoAffectEmissive", "_AlphaCutoff", "_CoatStrength", "_Wetness", "_Snow" })
                        inputs.Append(' ').Append(value).Append(m.HasProperty(value) ? "=" + m.GetFloat(value).ToString("0.##") : "(absent)");
                    if (m.HasProperty("_EmissiveColor")) { Color e = m.GetColor("_EmissiveColor"); inputs.Append($" _EmissiveColor=({e.r:0.##},{e.g:0.##},{e.b:0.##})"); }
                    // what the shader says about the picture's tiling and offset (the right side came out mirrored in the game, so the share and the
                    // mirroring are baked into the texture, see BusSkins.Material): the flags and type of the properties that could carry them
                    foreach (string uv in new[] { "_BaseColorMap", "_UV0Scale", "_UV0Offset", "_TileOffset" })
                    {
                        int index = shader.FindPropertyIndex(uv);
                        if (index < 0) { inputs.Append(' ').Append(uv).Append("(no property)"); continue; }
                        inputs.Append(' ').Append(uv).Append('[').Append(shader.GetPropertyType(index)).Append(' ').Append(shader.GetPropertyFlags(index));
                        if (shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Vector) { Vector4 value = m.GetVector(uv); inputs.Append($" ={value.x:0.##},{value.y:0.##},{value.z:0.##},{value.w:0.##}"); }
                        inputs.Append(']');
                    }
                    Mod.log.Info($"Wrap material: {template.name} [{shader.name}], decals from the library: {decalFrom}; now queue {m.renderQueue}, _DISABLE_DECALS {(m.IsKeywordEnabled("_DISABLE_DECALS") ? "on" : "OFF")}, _SupportDecals {(m.HasProperty("_SupportDecals") ? m.GetFloat("_SupportDecals").ToString("0.##") : "absent")}, colossal_DecalLayerMask {(m.HasProperty("colossal_DecalLayerMask") ? math.asint(m.GetFloat("colossal_DecalLayerMask")).ToString() : "absent")}; passes:{passes}; texture {(m.HasProperty("_BaseColorMap") ? "_BaseColorMap" : "NONE")}; inputs:{inputs}; properties:{names}");
                }
                return m;
            }
            catch (Exception failure) { CabCleanup.FailedAllocation(m, failure); throw; }
        }

        /// <summary>The always-lit LED material (the dashboard and display LEDs); the LED screen's dots use it too.</summary>
        internal static Material Glow()
        {
            Ensure();
            return s_Base != null ? s_Base[(int)Fin.Glow] : null;
        }

        /// <summary>Material array for a submesh layout (bit per finish, ascending) and light state; cached.</summary>
        internal static Material[] Set(ushort finMask, in KitLights lights, bool detail = false, bool steering = false)
        {
            Ensure();
            int code = (lights.Lamps ? 1 : 0) | (lights.Tail ? 2 : 0) | (lights.Beacon != 0 ? 4 : 0);
            int key = (finMask << 3) | code | (detail ? 1 << 20 : 0) | (detail && steering ? 1 << 21 : 0);
            if (s_Sets.TryGetValue(key, out var set)) return set;
            set = new Material[math.countbits((int)finMask)];
            int k = 0;
            for (int f = 0; f < (int)Fin.Count; f++)
            {
                if ((finMask & (1 << f)) == 0) continue;
                Material m = s_Base[f];
                if ((Fin)f == Fin.Lamp && lights.Lamps) m = s_LampOn;
                else if ((Fin)f == Fin.Tail && lights.Tail) m = s_TailOn;
                else if ((Fin)f == Fin.Beacon && lights.Beacon != 0) m = s_BeaconOn;
                set[k++] = detail ? GenericCabDetail.Material(m, (Fin)f, steering) : m;
            }
            return s_Sets[key] = set;
        }

        /// <summary>Door warning lamps: a double flash at about 1.2 Hz on the render clock (only a changed level is written).</summary>
        internal static void UpdateFlash(double time)
        {
            if (s_BeaconOn == null) return;
            double ph = time * 1.2;
            float p = (float)(ph - Math.Floor(ph));
            float level = p < 0.12f || (p > 0.22f && p < 0.34f) ? kBeaconNits : kBeaconNits * 0.03f;
            if (level == s_BeaconLevel) return;
            SetEmission(s_BeaconOn, Fin.Beacon, level);
            s_BeaconLevel = level;
        }

        internal static void DestroyAll()
        {
            CheckResourceAccess();
            s_ClearRequested = true;
            var failures = new List<Exception>();
            CabCleanup.Try(CabCleanup.RetryPending, failures);
            if (s_ResourceUsers == 0) CabCleanup.Try(DestroyAllCore, failures);
            CabCleanup.ThrowIfAny(failures, "Shared cab resource cleanup failed.");
        }

        static void DestroyAllCore()
        {
            var resources = new HashSet<UnityEngine.Object>(s_PendingDestroy);
            GenericCabDetail.TakeResources(resources);
            if (s_Base != null) foreach (Material m in s_Base) if (m != null) resources.Add(m);
            foreach (Material m in new[] { s_LampOn, s_TailOn, s_BeaconOn }) if (m != null) resources.Add(m);
            foreach (Material m in s_Lamps.Values) if (m != null) resources.Add(m);
            foreach (Material m in s_Liveries.Values) if (m != null) resources.Add(m);
            foreach (Dimmed d in s_Dimmed.Values) if (d.Material != null) resources.Add(d.Material);
            foreach (Texture2D texture in s_Palette) if (texture != null) resources.Add(texture);
            s_Lamps.Clear();
            s_Liveries.Clear();
            s_Dimmed.Clear();
            s_DimLevel = -1f;
            Array.Clear(s_Palette, 0, s_Palette.Length);
            s_Base = null;
            s_LampOn = s_TailOn = s_BeaconOn = null;
            s_Sets.Clear();
            s_BeaconLevel = -1f;
            var failures = new List<Exception>();
            foreach (UnityEngine.Object resource in resources)
            {
                if (CabCleanup.Try(() => UnityEngine.Object.Destroy(resource), failures)) s_PendingDestroy.Remove(resource);
                else s_PendingDestroy.Add(resource);
            }
            s_ClearRequested = s_PendingDestroy.Count != 0;
            CabCleanup.ThrowIfAny(failures, "One or more shared cab resources failed to destroy.");
        }
    }

    /// <summary>LOD0 meshes of a built model (one per part with geometry).</summary>
    internal sealed class CabModel : IDisposable
    {
        internal readonly KitModelData Data;
        internal readonly Mesh[] Meshes;
        internal readonly ushort[] Masks;
        internal int Triangles;
        // The lighter levels of detail (level 1 .. ), per part, when asked for (the riders): null otherwise.
        internal readonly Mesh[][] Levels;
        internal readonly ushort[][] LevelMasks;

        internal CabModel(KitModelData data, int levels = 1) : this(data, levels, false) { }

        /// <summary>A model whose meshes are uploaded a few at a time (<see cref="Step"/>), for a model prepared before it is needed:
        /// the upload is main-thread work, and all at once it is a stall of a hundred milliseconds or more.</summary>
        internal CabModel(KitModelData data, bool deferred, int levels = 1) : this(data, levels, deferred) { }

        private bool m_Destroyed;
        private bool m_DisposeComplete;
        private int m_Next;            // the next step of the upload: parts of LOD0 first, then those of each lighter level in turn
        private readonly int m_Levels; // the levels this model uploads: 1 = LOD0 only
        internal bool Complete => m_Next >= Data.Parts.Length * m_Levels;

        /// <summary>Uploads the next meshes until the time budget (Stopwatch ticks) is used; true when the model is complete.</summary>
        internal bool Step(long budgetTicks)
        {
            if (m_Destroyed) throw new ObjectDisposedException(nameof(CabModel));
            try
            {
                long end = budgetTicks == long.MaxValue ? long.MaxValue : System.Diagnostics.Stopwatch.GetTimestamp() + budgetTicks;
                int n = Data.Parts.Length;
                while (m_Next < n * m_Levels)
                {
                    int l = m_Next / n, i = m_Next % n;
                    m_Next++;
                    var part = Data.Parts[i];
                    var d = part.Data[l] ?? (part.MeshOf >= 0 ? Data.Parts[part.MeshOf].Data[l] : null);
                    if (l == 0)
                    {
                        if (d != null)
                        {
                            Meshes[i] = Upload(d, i, "Bus Driver cab " + part.Name);
                            Masks[i] = d.FinMask;
                            Triangles += d.Tris;
                        }
                    }
                    else if (d != null && d.Tris != 0)
                    {
                        Levels[l - 1][i] = Upload(d, i, "Bus Driver cab " + part.Name + " LOD" + l);
                        LevelMasks[l - 1][i] = d.FinMask;
                    }

                    if (System.Diagnostics.Stopwatch.GetTimestamp() >= end)
                        break;
                }

                return Complete;
            }
            catch (Exception uploadError)
            {
                try { Destroy(); }
                catch (Exception cleanupError) { throw new AggregateException("Cab model upload and cleanup failed.", uploadError, cleanupError); }
                throw;
            }
        }

        private CabModel(KitModelData data, int levels, bool deferred)
        {
            Data = data;
            int n = data.Parts.Length;
            Meshes = new Mesh[n];
            Masks = new ushort[n];
            m_Levels = Math.Max(1, Math.Min(levels, MeshKit.MaxLod + 1));
            if (m_Levels > 1)
            {
                Levels = new Mesh[m_Levels - 1][];
                LevelMasks = new ushort[m_Levels - 1][];
                for (int l = 1; l < m_Levels; l++)
                {
                    Levels[l - 1] = new Mesh[n];
                    LevelMasks[l - 1] = new ushort[n];
                }
            }

            if (!deferred)
            {
                try { Step(long.MaxValue); }
                catch
                {
                    if (!m_DisposeComplete) CabCleanup.Retain(this);
                    throw;
                }
            }
        }

        Mesh Upload(KitMeshData d, int part, string name)
        {
            var subMeshes = new SubMeshDescriptor[d.SubStart.Length];
            for (int i = 0; i < subMeshes.Length; i++) subMeshes[i] = new SubMeshDescriptor(d.SubStart[i], d.SubCount[i]);
            var b = d.Bounds;
            var bounds = new Bounds(new Vector3(b.Center.x, b.Center.y, b.Center.z), new Vector3(b.Size.x, b.Size.y, b.Size.z) + Vector3.one * 0.1f);
            return Data.Key.Name == "bus-interior" ? GenericCabDetail.Upload(d, part, name, subMeshes, bounds) :
                CDLC.Asset.Cab.MeshUpload.Upload(name, d.P, d.N, d.UV, d.Idx, subMeshes, bounds);
        }

        internal void Destroy() => Dispose();

        public void Dispose()
        {
            if (m_DisposeComplete) return;
            m_Destroyed = true;
            var failures = new List<Exception>();
            for (int i = 0; i < Meshes.Length; i++)
            {
                Mesh mesh = Meshes[i];
                if (mesh != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(mesh), failures)) Meshes[i] = null;
            }
            if (Levels != null)
                foreach (Mesh[] level in Levels)
                    if (level != null)
                        for (int i = 0; i < level.Length; i++)
                        {
                            Mesh mesh = level[i];
                            if (mesh != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(mesh), failures)) level[i] = null;
                        }
            Triangles = 0;
            if (failures.Count == 0) m_DisposeComplete = true;
            CabCleanup.ThrowIfAny(failures, "One or more cab meshes failed to destroy.");
        }
    }

    /// <summary>A placed model: one GameObject per part (the model's part tree), posed through IKitPoser.</summary>
    internal sealed class CabInstance : IKitPoser, IDisposable
    {
        readonly CabModel m_Model;
        GameObject m_Root;
        IDisposable m_MaterialLease;
        readonly MeshFilter[] m_Filter;
        bool m_Disposed;
        bool m_DisposeStarted;
        readonly Transform[] m_T;
        readonly MeshRenderer[] m_R;
        readonly float3[] m_Pos, m_Scale;
        readonly quaternion[] m_Rot;
        readonly bool[] m_Vis;
        readonly bool[] m_Own;              // parts drawn with their own material (OverrideMaterial): the lights leave them alone
        readonly Material[] m_Last;         // the material OverrideMaterial last gave each part (the same again writes nothing)
        KitLights m_Lights;
        bool m_LightsSet, m_Visible = true, m_Counted;

        /// <summary>Instances made and not yet fully disposed (the native body check logs it: a second one on a ridden bus would be a stale model).</summary>
        internal static int Live { get; private set; }

        internal CabInstance(CabModel model, string name)
        {
            m_Model = model;
            var parts = model.Data.Parts;
            int n = parts.Length;
            m_T = new Transform[n];
            m_R = new MeshRenderer[n];
            m_Filter = new MeshFilter[n];
            m_Pos = new float3[n];
            m_Scale = new float3[n];
            m_Rot = new quaternion[n];
            m_Vis = new bool[n];
            m_Own = new bool[n];
            m_Last = new Material[n];
            // a part that fails half way: the objects made so far go with the root (the caller never gets the instance)
            GameObject pendingPart = null;
            try
            {
                m_MaterialLease = CabMaterials.AcquireResources();
                m_Root = new GameObject(name);
                m_Root.hideFlags = HideFlags.DontSave;
                m_Root.layer = 0;
                UnityEngine.Object.DontDestroyOnLoad(m_Root);
                for (int i = 0; i < n; i++)
                {
                    var p = parts[i];
                    pendingPart = new GameObject(p.Name);
                    pendingPart.transform.SetParent(p.Parent < 0 ? m_Root.transform : m_T[p.Parent], false);
                    var go = pendingPart;
                    pendingPart = null;
                    go.hideFlags = HideFlags.DontSave;
                    go.layer = 0;
                    go.transform.SetLocalPositionAndRotation(V(p.LocalPos), Q(p.LocalRot));
                    m_T[i] = go.transform;
                    m_Pos[i] = p.LocalPos;
                    m_Rot[i] = p.LocalRot;
                    m_Scale[i] = new float3(1f);
                    m_Vis[i] = true;
                    if (model.Meshes[i] == null) continue;
                    m_Filter[i] = go.AddComponent<MeshFilter>();
                    m_Filter[i].sharedMesh = model.Meshes[i];
                    var r = go.AddComponent<MeshRenderer>();
                    m_R[i] = r;
                    r.shadowCastingMode = (p.Flags & PartFlags.Details) != 0 ? ShadowCastingMode.Off : ShadowCastingMode.On;
                    r.receiveShadows = true;
                    r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                    r.renderingLayerMask = uint.MaxValue;
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                    r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    r.allowOcclusionWhenDynamic = false;
                    r.sharedMaterials = CabMaterials.Set(model.Masks[i], KitLights.Off, model.Data.Key.Name == "bus-interior", i == 1);
                }

                Live++;
                m_Counted = true;
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                if (pendingPart != null && !CabCleanup.Try(() => UnityEngine.Object.Destroy(pendingPart), failures)) CabCleanup.Retain(pendingPart);
                CabCleanup.Try(Dispose, failures);
                if (failures.Count > 1) { CabCleanup.Retain(this); throw new AggregateException("Cab instance construction and cleanup failed.", failures); }
                throw;
            }
        }

        static Vector3 V(float3 v) => new Vector3(v.x, v.y, v.z);
        static Quaternion Q(quaternion q) => new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);

        internal bool Alive => !m_DisposeStarted && m_Root != null;
        /// <summary>The model's frame (model space = this transform's local space), for extra parts the model does not own.</summary>
        internal Transform Root => m_Root != null ? m_Root.transform : null;
        internal Transform Part(int part) => m_Root != null && (uint)part < (uint)m_T.Length ? m_T[part] : null;

        internal bool Visible
        {
            get => m_Visible;
            set
            {
                if (m_DisposeStarted || m_Visible == value || m_Root == null) return;
                m_Root.SetActive(value);
                m_Visible = value;
            }
        }

        public void SetRoot(float3 position, quaternion rotation) => m_Root.transform.SetPositionAndRotation(V(position), Q(KitPose.Safe(rotation)));

        public void Pose(int part, float3 offset, quaternion delta)
        {
            var p = m_Model.Data.Parts[part];
            PoseLocal(part, p.LocalPos + offset, math.mul(p.LocalRot, KitPose.Safe(delta)));
        }

        public void PoseLocal(int part, float3 localPos, quaternion localRot)
        {
            localRot = KitPose.Safe(localRot);
            if (math.all(localPos == m_Pos[part]) && math.all(localRot.value == m_Rot[part].value)) return;
            m_T[part].SetLocalPositionAndRotation(V(localPos), Q(localRot));
            m_Pos[part] = localPos;
            m_Rot[part] = localRot;
        }

        public void Scale(int part, float3 scale)
        {
            if (math.all(scale == m_Scale[part])) return;
            m_T[part].localScale = V(scale);
            m_Scale[part] = scale;
        }

        /// <summary>
        /// Draws a part with one material of its own (lamps lit in their colour, the livery in the line's colour). Called every
        /// frame: the material it already has returns at once (reading sharedMaterials allocated an array each time). Nothing
        /// else writes these parts' materials (SetLights leaves them alone).
        /// </summary>
        internal void OverrideMaterial(int part, Material material)
        {
            if (m_DisposeStarted || part < 0 || part >= m_R.Length || m_R[part] == null || material == null) return;
            if (ReferenceEquals(m_Last[part], material)) return;
            var mats = m_R[part].sharedMaterials;
            bool same = true;
            for (int i = 0; i < mats.Length; i++) same &= mats[i] == material;
            if (!same)
            {
                m_Last[part] = null;
                for (int i = 0; i < mats.Length; i++) mats[i] = material;
                m_R[part].sharedMaterials = mats;
            }
            m_Own[part] = true;
            m_Last[part] = material;
        }

        public void Show(int part, bool visible)
        {
            if (m_DisposeStarted || m_Vis[part] == visible) return;
            if (m_R[part] != null) m_R[part].enabled = visible;
            m_Vis[part] = visible;
        }

        public void SetLights(in KitLights lights)
        {
            if (m_DisposeStarted) return;
            int code = (lights.Lamps ? 1 : 0) | (lights.Tail ? 2 : 0) | (lights.Beacon != 0 ? 4 : 0);
            int old = (m_Lights.Lamps ? 1 : 0) | (m_Lights.Tail ? 2 : 0) | (m_Lights.Beacon != 0 ? 4 : 0);
            if (m_LightsSet && code == old) return;
            m_LightsSet = false;
            for (int i = 0; i < m_R.Length; i++)
                if (m_R[i] != null && !m_Own[i]) m_R[i].sharedMaterials = CabMaterials.Set(m_Model.Masks[i], lights, m_Model.Data.Key.Name == "bus-interior", i == 1);
            m_Lights = lights;
            m_LightsSet = true;
        }

        public KitPartDef Def(int part) => m_Model.Data.Parts[part];
        public int PartCount => m_T.Length;

        public float4x4 PartToModel(int part)
        {
            var parts = m_Model.Data.Parts;
            var m = KitPose.Local(m_Pos[part], m_Rot[part], m_Scale[part]);
            for (int p = parts[part].Parent; p >= 0; p = parts[p].Parent) m = math.mul(KitPose.Local(m_Pos[p], m_Rot[p], m_Scale[p]), m);
            return m;
        }

        internal void Destroy() => Dispose();

        public void Dispose()
        {
            if (m_Disposed) return;
            m_DisposeStarted = true;
            var failures = new List<Exception>();
            bool detached = true;
            foreach (MeshRenderer renderer in m_R)
                if (renderer != null) CabCleanup.Try(() => renderer.enabled = false, failures);
            if (m_Root != null) CabCleanup.Try(() => m_Root.SetActive(false), failures);
            for (int i = 0; i < m_R.Length; i++)
            {
                MeshRenderer renderer = m_R[i];
                MeshFilter filter = m_Filter[i];
                if (renderer != null) detached &= CabCleanup.Try(() => renderer.sharedMaterials = Array.Empty<Material>(), failures);
                if (filter != null) detached &= CabCleanup.Try(() => filter.sharedMesh = null, failures);
            }
            if (detached && m_Root != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(m_Root), failures)) m_Root = null;
            if (detached && m_Root == null)
            {
                Array.Clear(m_R, 0, m_R.Length);
                Array.Clear(m_Filter, 0, m_Filter.Length);
                Array.Clear(m_T, 0, m_T.Length);
                Array.Clear(m_Last, 0, m_Last.Length);
                if (m_MaterialLease != null && CabCleanup.Try(m_MaterialLease.Dispose, failures)) m_MaterialLease = null;
            }
            if (m_Root == null && m_MaterialLease == null) m_Disposed = true;
            if (m_Disposed && m_Counted)
            {
                Live--;
                m_Counted = false;
            }
            CabCleanup.ThrowIfAny(failures, "Cab instance cleanup failed.");
        }
    }
}
