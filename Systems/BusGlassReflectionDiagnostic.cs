using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>Attribution controls only: restores every shared material and camera setting.</summary>
    internal static class BusGlassReflectionDiagnostic
    {
        internal static async Task<List<object>> Run(Camera camera, RenderTexture target, string directory, string name,
            Vector3 origin, Transform model, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var results = new List<object>();
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var aa = hd.antialiasing; bool history = hd.hasPersistentHistory;
            var settings = hd.renderingPathCustomFrameSettings; var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var intensities = lights.Select(light => light.intensity).ToArray(); var units = lights.Select(light => light.lightUnit).ToArray();
            var renderers = new List<(MeshRenderer renderer, Material[] materials)>();
            var clones = new Dictionary<Material, Material>();
            Material template = AssetDatabase.global.resources.materialLibrary.GetMaterial(11);
            if (template == null || template.shader.name != "BH/GlsShader") throw new InvalidOperationException("Template 11 is not the expected native glass shader");
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
                            clone = new Material(material) { name = material.name + " reflection diagnostic clone", hideFlags = HideFlags.DontSave };
                            clones[material] = clone;
                        }
                        after[slot] = clone; changed = true;
                    }
                    if (changed) { renderers.Add((renderer, before)); renderer.sharedMaterials = after; }
                }
                if (clones.Count != 1) throw new InvalidOperationException("Expected exactly one source glass material");
                foreach (string group in new[] { "baseline", "sky-reflection-off", "direct-specular-off", "normal-strength-zero", "native-template-11" })
                {
                    var current = settings; var currentMask = mask;
                    current.SetEnabled(FrameSettingsField.SkyReflection, group != "sky-reflection-off");
                    currentMask.mask[(uint)FrameSettingsField.SkyReflection] = true;
                    current.SetEnabled(FrameSettingsField.DirectSpecularLighting, group != "direct-specular-off");
                    currentMask.mask[(uint)FrameSettingsField.DirectSpecularLighting] = true;
                    hd.renderingPathCustomFrameSettings = current; hd.renderingPathCustomFrameSettingsOverrideMask = currentMask;
                    var validation = new List<object>();
                    foreach (var pair in clones)
                    {
                        var source = group == "native-template-11" ? template : pair.Key;
                        var clone = pair.Value; clone.CopyPropertiesFromMaterial(source); clone.shaderKeywords = source.shaderKeywords;
                        if (group == "normal-strength-zero") clone.SetFloat("_NormalStrength", 0f);
                        if (group == "native-template-11") clone.EnableKeyword("_TANGENTSPACE_OCTO");
                        bool validated = HDMaterial.ValidateMaterial(clone);
                        if (!clone.IsKeywordEnabled("_TANGENTSPACE_OCTO")) throw new InvalidOperationException("Packed normals require the native OCTO keyword");
                        validation.Add(new { validator_supported = validated, source_material = source.name, source_instance_id = source.GetInstanceID(),
                            normal_strength = clone.GetFloat("_NormalStrength"), octo = clone.IsKeywordEnabled("_TANGENTSPACE_OCTO") });
                    }
                    object immediate = null, sky = null;
                    await BusVehicleProbe.Capture(camera, target, directory, name, 0, "reflection-" + group + "-handrail-material",
                        new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true, () =>
                        {
                            var hdcam = HDCamera.GetOrCreate(camera);
                            bool actualSky = hdcam.frameSettings.IsEnabled(FrameSettingsField.SkyReflection);
                            bool actualDirect = hdcam.frameSettings.IsEnabled(FrameSettingsField.DirectSpecularLighting);
                            if (actualSky != (group != "sky-reflection-off") || actualDirect != (group != "direct-specular-off"))
                                throw new InvalidOperationException("Reflection diagnostic frame settings were not applied");
                            immediate = new { sky_reflection = actualSky, direct_specular_lighting = actualDirect,
                                exposure_mode = hdcam.volumeStack.GetComponent<Exposure>().mode.value.ToString(),
                                exposure_ev = hdcam.volumeStack.GetComponent<Exposure>().fixedExposure.value,
                                environment = BusNativeVehicleProbe.EnvironmentEvidence(), ambient_probes = AmbientEvidence(camera, model) };
                            if (group == "baseline") sky = BusProbeSkyReadback.CaptureMipChain(directory, "reflection-baseline");
                        });
                    results.Add(new { group, scope = "Attribution only; disabled lighting, flattened normals and template substitution are not production fixes.",
                        validation, immediate, sky_mip_readback = sky, materials = clones.Values.Select(BusVehicleMaterialDiagnostics.ShaderEvidence).ToArray() });
                }
            }
            finally
            {
                foreach (var pair in renderers) pair.renderer.sharedMaterials = pair.materials;
                foreach (var clone in clones.Values) UnityEngine.Object.Destroy(clone);
                hd.antialiasing = aa; hd.hasPersistentHistory = history;
                hd.renderingPathCustomFrameSettings = settings; hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(intensities[i], units[i]);
            }
            return results;
        }
        static object AmbientEvidence(Camera camera, Transform model)
        {
            var renderer = model.GetComponentsInChildren<MeshRenderer>(true).First(item => !item.name.StartsWith("Glass ", StringComparison.Ordinal));
            LightProbes.GetInterpolatedProbe(camera.transform.position, renderer, out var atCamera);
            LightProbes.GetInterpolatedProbe(model.position + Vector3.up, renderer, out var atBody);
            return new { ambient_mode = RenderSettings.ambientMode.ToString(), ambient_intensity = RenderSettings.ambientIntensity,
                render_settings_sh = SH(RenderSettings.ambientProbe), camera_sh = SH(atCamera), body_sh = SH(atBody),
                renderer = renderer.name, light_probe_usage = renderer.lightProbeUsage.ToString(),
                camera_position = new[] { camera.transform.position.x, camera.transform.position.y, camera.transform.position.z },
                body_position = new[] { model.position.x, model.position.y + 1f, model.position.z } };
        }
        static float[][] SH(SphericalHarmonicsL2 sh)
        {
            var channels = new float[3][];
            for (int rgb = 0; rgb < 3; rgb++) { channels[rgb] = new float[9]; for (int term = 0; term < 9; term++) channels[rgb][term] = sh[rgb, term]; }
            return channels;
        }
    }
}
