using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static class BusGlassSurfaceDiagnostic
    {
        internal static async Task<List<object>> Run(Camera camera, RenderTexture target, string directory, string name,
            Vector3 origin, Transform model, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>();
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var aa = hd.antialiasing; bool history = hd.hasPersistentHistory;
            var intensities = lights.Select(light => light.intensity).ToArray(); var units = lights.Select(light => light.lightUnit).ToArray();
            var renderers = new List<(MeshRenderer renderer, Material[] materials)>();
            var clones = new Dictionary<Material, Material>();
            var constants = new Dictionary<Material, (Texture2D color, Texture2D mask)>();
            var textureEvidence = new List<object>();
            using (var exposure = new BusVehicleMaterialDiagnostics.PrivateExposure(camera, origin, 13f))
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing; hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(intensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var before = renderer.sharedMaterials; var after = (Material[])before.Clone(); bool changed = false;
                    for (int slot = 0; slot < after.Length; slot++)
                    {
                        var material = after[slot]; if (material?.shader?.name != "BH/GlsShader") continue;
                        if (!clones.TryGetValue(material, out var clone))
                        {
                            clone = new Material(material) { name = material.name + " surface diagnostic clone", hideFlags = HideFlags.DontSave };
                            clones[material] = clone;
                            Texture colorMap = material.GetTexture("_BaseColorMap"), maskMap = material.GetTexture("_MaskMap");
                            if (colorMap.width != 64 || maskMap.width != 64 || colorMap.height != 64 || maskMap.height != 64)
                                throw new InvalidOperationException("Uniform swatch diagnostic expects this model's 64x64 atlas");
                            // The single-glass atlas is tile 0, a padded 4x4 region; sample its interior pixel.
                            Color color = BusVehicleProbe.SampleLinear(colorMap, 2, 2), mask = BusVehicleProbe.SampleLinear(maskMap, 2, 2);
                            if (Mathf.Abs(color.a - .28f) > .02f || mask.g < .98f || Mathf.Abs(mask.a - .96f) > .03f)
                                throw new InvalidOperationException("Sampled texel is not the expected glass swatch");
                            var uniformColor = Constant(color, "uniform glass decoded linear BaseColor");
                            var uniformMask = Constant(mask, "uniform glass linear MaskMap");
                            constants[material] = (uniformColor, uniformMask);
                            Color checkColor = BusVehicleProbe.SampleLinear(uniformColor, 32, 32), checkMask = BusVehicleProbe.SampleLinear(uniformMask, 32, 32);
                            if (Difference(color, checkColor) > .00001f || Difference(mask, checkMask) > .00001f)
                                throw new InvalidOperationException("Uniform diagnostic texture changed shader sample values");
                            textureEvidence.Add(new { source_color = colorMap.name, source_mask = maskMap.name, sample_pixel = new[] { 2, 2 },
                                sampled_linear_rgba = Values(color), sampled_mask_rgba = Values(mask), uniform_color_check = Values(checkColor), uniform_mask_check = Values(checkMask),
                                new_format = uniformColor.graphicsFormat.ToString(), new_mip_count = uniformColor.mipmapCount,
                                highest_mip_color = Values(uniformColor.GetPixel(0, 0, uniformColor.mipmapCount - 1)),
                                explanation = "Original sRGB texture decoded by GPU sampling; constant RGBAFloat linear texture prevents double decode and fills all mips with exactly that same shader input." });
                        }
                        after[slot] = clone; changed = true;
                    }
                    if (changed) { renderers.Add((renderer, before)); renderer.sharedMaterials = after; }
                }
                if (clones.Count != 1) throw new InvalidOperationException("This diagnostic requires exactly one shared glass material");
                foreach (string group in new[] { "baseline", "depth-offset-off", "uniform-glass-atlas" })
                {
                    var validation = new List<object>();
                    foreach (var pair in clones)
                    {
                        var clone = pair.Value; clone.CopyPropertiesFromMaterial(pair.Key); clone.shaderKeywords = pair.Key.shaderKeywords;
                        if (group == "depth-offset-off")
                        {
                            clone.SetFloat("_DepthOffsetEnable", 0f); clone.DisableKeyword("_DEPTHOFFSET_ON");
                            bool supported = HDMaterial.ValidateMaterial(clone);
                            if (clone.IsKeywordEnabled("_DEPTHOFFSET_ON") || clone.GetFloat("_DepthOffsetEnable") != 0f)
                                throw new InvalidOperationException("Depth-offset keyword remained enabled after validation");
                            validation.Add(new { validator_supported = supported, depth_offset_keyword = clone.IsKeywordEnabled("_DEPTHOFFSET_ON"), property = clone.GetFloat("_DepthOffsetEnable") });
                        }
                        if (group == "uniform-glass-atlas")
                        {
                            clone.SetTexture("_BaseColorMap", constants[pair.Key].color);
                            clone.SetTexture("_MaskMap", constants[pair.Key].mask);
                        }
                    }
                    await BusVehicleProbe.Capture(camera, target, directory, name, 0, "surface-" + group + "-handrail-material",
                        new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true);
                    result.Add(new { group, scope = "Temporary private material diagnostic; not approved production settings.", validation,
                        textures = textureEvidence, materials = clones.Values.Select(BusVehicleMaterialDiagnostics.ShaderEvidence).ToArray() });
                }
            }
            finally
            {
                foreach (var renderer in renderers) renderer.renderer.sharedMaterials = renderer.materials;
                foreach (var clone in clones.Values) UnityEngine.Object.Destroy(clone);
                foreach (var maps in constants.Values) { UnityEngine.Object.Destroy(maps.color); UnityEngine.Object.Destroy(maps.mask); }
                hd.antialiasing = aa; hd.hasPersistentHistory = history;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(intensities[i], units[i]);
            }
            return result;
        }
        static Texture2D Constant(Color value, string name)
        {
            var texture = new Texture2D(64, 64, TextureFormat.RGBAFloat, true, true) { name = name, hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels(Enumerable.Repeat(value, 4096).ToArray()); texture.Apply(true, false); return texture;
        }
        static float Difference(Color a, Color b) => Mathf.Max(Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g)), Mathf.Max(Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a)));
        static float[] Values(Color c) => new[] { c.r, c.g, c.b, c.a };
    }
}
