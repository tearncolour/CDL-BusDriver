using System;
using System.Collections.Generic;
using System.Text;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The bus's own opaque materials drawn with the game's shader-graph material (BH/SG_DefaultShader) instead of HDRP/Lit. The game's decals (the paving of a lot, a road's markings and its dark
    /// asphalt) decide what they land on by that shader's property colossal_DecalLayerMask; HDRP/Lit has none, and every switch on it that Bus Driver can set was tried in the game
    /// (_SupportDecals 0, the _DISABLE_DECALS keyword, the layer property, the queue, the renderers' rendering layer bits both ways) without stopping the decals landing on the cab and the body
    /// (the owner, 5-7 November; the built-in 12 m and K18 alike). The traffic signs mod met the same on its own HDRP/Lit materials and ended by moving them to this shader with the layer
    /// Vehicles (Render/TsMaterials.GameShader.cs of that mod, whose recipe this is); on 7 November the owner saw the pattern go and the colours and gloss right. What glows stays on HDRP/Lit:
    /// the game's graph does not read an emission in nits as HDRP/Lit does (that mod's LED dots blew out into a bloom). Main thread.
    /// <see cref="Take"/> is what the cab's materials go through when they are made (CabMaterials.MakeLit); the instance is the review session's experiment line gameShader, which moves the
    /// materials of a bus that exist and puts them back from a snapshot.
    /// </summary>
    internal sealed class GameShaderMove
    {
        internal const string ShaderName = "BH/SG_DefaultShader";
        private const string kTemplateName = "Default";
        private const int kLookups = 3;
        private const string kPalettePrefix = "Bus Driver cab palette ", kLiveryPrefix = "Bus Driver livery ";

        private sealed class Moved
        {
            internal Material Material, Before;
            internal Dictionary<string, bool> Passes;
        }

        private static Material s_Template;
        private static int s_Lookups;
        private static bool s_Broken, s_BrokenLogged;
        private static int s_Taken;
        private static Texture2D s_FlatNormal;
        // a few dozen of 4 x 4 pixels, kept for the session
        private static readonly Dictionary<int, Texture2D> s_Colours = new Dictionary<int, Texture2D>(), s_Masks = new Dictionary<int, Texture2D>();
        private static readonly Dictionary<Fin, Texture2D> s_PaletteMasks = new Dictionary<Fin, Texture2D>();

        private readonly Dictionary<int, Moved> m_Moved = new Dictionary<int, Moved>();
        private string m_Last = "";

        /// <summary>Whether a material is drawn with the game's shader (a moved material, or one made on it).</summary>
        internal static bool OnGameShader(Material material) => material != null && material.shader != null && material.shader.name == ShaderName;

        /// <summary>
        /// Moves a material Bus Driver has just made on HDRP/Lit (colour or palette map, metallic, smoothness, no emission) to the game's shader where it stands, for good. False and the
        /// material as it was when the game's material library has no template, or the move fails (the first failure is logged and the rest of the session keeps HDRP/Lit).
        /// </summary>
        internal static bool Take(Material m)
        {
            if (m == null || s_Broken)
                return false;
            Material template = Template();
            if (template == null)
                return false;
            Moved moved = Move(m, template);
            if (moved == null)
            {
                s_Broken = true;
                if (!s_BrokenLogged)
                {
                    s_BrokenLogged = true;
                    Mod.log.Warn("Game shader: a material could not be moved; the rest of this session keeps the cab's materials on HDRP/Lit (the road's decals may then land on the bus).");
                }

                return false;
            }

            UnityEngine.Object.Destroy(moved.Before);
            if (++s_Taken == 1)
                Mod.log.Info($"Game shader: the cab's materials are made on {template.shader.name} (template \"{template.name}\"); \"{m.name}\" was the first.");
            return true;
        }

        /// <summary>How many materials this instance has moved (the experiment's line).</summary>
        internal int Count => m_Moved.Count;

        /// <summary>
        /// The experiment: moves every material of the list that Bus Driver made as HDRP/Lit and that does not glow (MaterialAuditRules.GameShaderMovable); the ones already on the game's shader are
        /// left (the cab's materials are made on it now). The line for the log when the numbers changed since the last call, else null.
        /// </summary>
        internal string Apply(IEnumerable<Material> materials)
        {
            Material template = Template();
            if (template == null)
                return m_Last == "no template" ? null : (m_Last = "no template") + ": the game's material library has no material \"" + kTemplateName + "\" of its default shader: the materials stay on HDRP/Lit";

            int failed = 0, already = 0;
            var stayed = new List<string>();
            foreach (Material material in materials)
            {
                if (material == null)
                    continue;
                float glow = 0f;
                if (material.shader != null && material.HasProperty("_EmissiveColor"))
                {
                    Color emissive = material.GetColor("_EmissiveColor");
                    glow = Mathf.Max(emissive.r, emissive.g, emissive.b);
                }

                if (material.shader != null && material.name.StartsWith("Bus Driver ", StringComparison.Ordinal) && OnGameShader(material))
                {
                    already++;
                    continue;
                }

                if (material.shader == null || !MaterialAuditRules.GameShaderMovable(material.name, material.shader.name, glow))
                {
                    if (material.shader != null && material.shader.name == "HDRP/Lit" && material.name.StartsWith("Bus Driver ", StringComparison.Ordinal))
                        stayed.Add(material.name);
                    continue;
                }

                Moved moved = Move(material, template);
                if (moved == null) failed++;
                else m_Moved[material.GetInstanceID()] = moved;
            }

            string line = $"game shader on: {m_Moved.Count} own material(s) moved by this line, {already} already on {template.shader.name} (template \"{template.name}\"), {failed} could not be moved" +
                          (stayed.Count != 0 ? $", {stayed.Count} stay on HDRP/Lit as they glow: {string.Join(", ", stayed.GetRange(0, Math.Min(5, stayed.Count)))}{(stayed.Count > 5 ? " ..." : "")}" : "");
            if (line == m_Last)
                return null;
            m_Last = line;
            return line;
        }

        /// <summary>Puts every material this instance moved back as it was (its shader, properties, keywords, queue and passes); how many.</summary>
        internal int Restore()
        {
            int back = 0;
            foreach (Moved moved in m_Moved.Values)
            {
                if (moved.Material != null && moved.Before != null)
                {
                    try
                    {
                        Revert(moved);
                        back++;
                    }
                    catch (Exception failure)
                    {
                        Mod.log.Warn($"Game shader: \"{moved.Material.name}\" could not be put back: {failure.GetType().Name}: {failure.Message}");
                    }
                }

                if (moved.Before != null)
                    UnityEngine.Object.Destroy(moved.Before);
            }

            m_Moved.Clear();
            m_Last = "";
            return back;
        }

        // The move, with a snapshot of the whole material taken first: null (and the material put back) when anything throws.
        private static Moved Move(Material m, Material template)
        {
            var moved = new Moved { Material = m, Passes = new Dictionary<string, bool>() };
            try
            {
                moved.Before = new Material(m) { name = m.name + " (before the game shader)", hideFlags = HideFlags.DontSave };
                for (int i = 0; i < m.passCount; i++)
                {
                    string pass = m.GetPassName(i);
                    if (!string.IsNullOrEmpty(pass)) moved.Passes[pass] = m.GetShaderPassEnabled(pass);
                }

                Texture picture = m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") : null;
                Color colour = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.white;
                float metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f, smoothness = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : 0.5f;
                Vector4 emission = m.HasProperty("_EmissiveColor") ? m.GetVector("_EmissiveColor") : Vector4.zero;

                m.shader = template.shader;
                m.CopyPropertiesFromMaterial(template);
                foreach (string keyword in m.shaderKeywords)
                    m.DisableKeyword(keyword);
                foreach (string keyword in template.shaderKeywords)
                    m.EnableKeyword(keyword);
                m.enableInstancing = template.enableInstancing;

                // the colour HDRP/Lit showed: a palette map times the colour (linear light of the Color property), or the colour alone in a map of its own and white
                if (picture != null)
                {
                    m.SetTexture("_BaseColorMap", picture);
                    Color linear = colour.linear;
                    m.SetVector("_BaseColor", new Vector4(linear.r, linear.g, linear.b, 1f));
                }
                else
                {
                    m.SetTexture("_BaseColorMap", ColourMap(colour));
                    m.SetVector("_BaseColor", Vector4.one);
                }

                // the paint's clear coat: a coated palette finish gets a mask laid out as its palette (the paint rows coated), the livery band a flat coated one, the rest a flat plain one
                Fin finish = default;
                bool coatedPalette = picture != null && picture.name.StartsWith(kPalettePrefix, StringComparison.Ordinal) &&
                                     Enum.TryParse(picture.name.Substring(kPalettePrefix.Length), out finish) && KitPalette.HasClearCoat(finish);
                m.SetTexture("_MaskMap", coatedPalette ? PaletteMask(finish) : MaskMap(metallic, smoothness, m.name.StartsWith(kLiveryPrefix, StringComparison.Ordinal)));
                m.SetTexture("_NormalMap", FlatNormal());
                if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", Texture2D.whiteTexture);
                if (m.HasProperty("_EmissiveIntensity")) m.SetFloat("_EmissiveIntensity", 1f);
                if (m.HasProperty("_EmissiveExposureWeight")) m.SetFloat("_EmissiveExposureWeight", 1f);
                if (m.HasProperty("colossal_LodFade")) m.SetFloat("colossal_LodFade", 1f);
                if (m.HasProperty("_SupportDecals")) m.SetFloat("_SupportDecals", 0f);
                if (m.HasProperty("colossal_DecalLayerMask")) m.SetFloat("colossal_DecalLayerMask", math.asfloat((int)Game.Rendering.DecalLayers.Vehicles));
                try { HDMaterial.ValidateMaterial(m); }
                catch (Exception ex) { Mod.log.Warn($"Game shader: ValidateMaterial failed for \"{m.name}\": {ex.Message}"); }
                // ValidateMaterial resets the queue; the decal-receiving one keeps the road's decals from being drawn through the material (Interior.CabMaterials.kQueueOpaqueDecal)
                m.renderQueue = Interior.CabMaterials.kQueueOpaqueDecal;
                // as the shader's keyword for a material that takes no decals
                if (m.shader.keywordSpace.FindKeyword("_DISABLE_DECALS").isValid && !m.IsKeywordEnabled("_DISABLE_DECALS")) m.EnableKeyword("_DISABLE_DECALS");
                // after the validation, which may work the emission out of other properties
                if (m.HasProperty("_EmissiveColor")) m.SetVector("_EmissiveColor", emission);
                return moved;
            }
            catch (Exception failure)
            {
                Mod.log.Warn($"Game shader: \"{m.name}\" could not be moved: {failure.GetType().Name}: {failure.Message}");
                if (moved.Before != null)
                {
                    try { Revert(moved); }
                    catch (Exception again) { Mod.log.Warn($"Game shader: and could not be put back either: {again.Message}"); }
                    UnityEngine.Object.Destroy(moved.Before);
                }

                return null;
            }
        }

        private static void Revert(Moved moved)
        {
            Material m = moved.Material, before = moved.Before;
            m.shader = before.shader;
            m.CopyPropertiesFromMaterial(before);
            foreach (string keyword in m.shaderKeywords)
                m.DisableKeyword(keyword);
            foreach (string keyword in before.shaderKeywords)
                m.EnableKeyword(keyword);
            m.enableInstancing = before.enableInstancing;
            m.renderQueue = before.renderQueue;
            foreach (KeyValuePair<string, bool> pass in moved.Passes)
                m.SetShaderPassEnabled(pass.Key, pass.Value);
        }

        // The game's opaque object material, looked for in its material library (by name, else the first of its default shader that does not cut out). The library is not there before a city is
        // loaded: a miss is asked again a couple of times before it is given up.
        private static Material Template()
        {
            if (s_Template != null)
                return s_Template;
            if (s_Lookups >= kLookups)
                return null;
            s_Lookups++;
            var names = new StringBuilder();
            Material byShader = null;
            for (int index = 0; index < 128; index++)
            {
                Material candidate;
                try { candidate = Colossal.IO.AssetDatabase.AssetDatabase.global.resources.materialLibrary.GetMaterial(index); }
                catch (Exception) { break; }
                if (candidate == null) break;
                names.Append(names.Length > 0 ? ", " : "").Append(candidate.name).Append(" (").Append(candidate.shader != null ? candidate.shader.name : "?").Append(')');
                if (candidate.name == kTemplateName && s_Template == null) s_Template = candidate;
                if (byShader == null && candidate.shader != null && candidate.shader.name.Contains("SG_DefaultShader") && candidate.name.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) < 0 &&
                    candidate.name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) < 0 && candidate.name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) < 0)
                    byShader = candidate;
            }

            if (s_Template == null) s_Template = byShader;
            if (s_Template == null)
            {
                Mod.log.Warn($"Game shader: no opaque material of the game's default shader in its material library (try {s_Lookups} of {kLookups}; found: {names}): the cab's materials stay on HDRP/Lit.");
                return null;
            }

            var has = new StringBuilder();
            foreach (string property in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap", "_BaseColor", "_Metallic", "_Smoothness", "_EmissiveColorMap", "_EmissiveIntensity", "_EmissiveExposureWeight", "colossal_LodFade", "_SupportDecals", "colossal_DecalLayerMask" })
                has.Append(' ').Append(property).Append(s_Template.HasProperty(property) ? "" : "(absent)");
            Mod.log.Info($"Game shader: template \"{s_Template.name}\" ({s_Template.shader.name}), queue {s_Template.renderQueue}, instancing {s_Template.enableInstancing}, keywords [{string.Join(" ", s_Template.shaderKeywords)}];{has}. The library: {names}.");
            return s_Template;
        }

        private static Texture2D FlatNormal() => s_FlatNormal != null ? s_FlatNormal : s_FlatNormal = Solid(new Color32(128, 128, 255, 255), true, "Bus Driver flat normal");

        private static Texture2D ColourMap(Color colour)
        {
            byte r = ToByte(colour.r), g = ToByte(colour.g), b = ToByte(colour.b);
            int key = (r << 16) | (g << 8) | b;
            if (s_Colours.TryGetValue(key, out Texture2D map) && map != null)
                return map;
            return s_Colours[key] = Solid(new Color32(r, g, b, 255), false, "Bus Driver plain colour " + key.ToString("x6"));
        }

        // metallic in R, the clear coat in G (0 or 255), 0 in B, smoothness in A: the layout the game's baked details write
        private static Texture2D MaskMap(float metallic, float smoothness, bool coat)
        {
            byte metal = ToByte(metallic), smooth = ToByte(smoothness), clear = (byte)(coat ? 255 : 0);
            int key = (clear << 16) | (metal << 8) | smooth;
            if (s_Masks.TryGetValue(key, out Texture2D map) && map != null)
                return map;
            return s_Masks[key] = Solid(new Color32(metal, clear, 0, smooth), true, $"Bus Driver plain mask {metal} {clear} {smooth}");
        }

        // the mask of a coated palette finish (KitPalette.MaskBytes), one per finish for the session; point sampled, so that a paint row stays coated to its edge
        private static Texture2D PaletteMask(Fin finish)
        {
            if (s_PaletteMasks.TryGetValue(finish, out Texture2D map) && map != null)
                return map;
            var texture = new Texture2D(KitPalette.Width, KitPalette.Height, TextureFormat.RGBA32, false, true)
                { name = "Bus Driver palette mask " + finish, hideFlags = HideFlags.DontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.LoadRawTextureData(KitPalette.MaskBytes(finish));
            texture.Apply(false, false);
            return s_PaletteMasks[finish] = texture;
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

        private static Texture2D Solid(Color32 colour, bool linear, string name)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, linear) { name = name, hideFlags = HideFlags.DontSave };
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = colour;
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
