using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static class BusGlassPipelineDiagnostic
    {
        internal static async Task<List<object>> Run(Camera camera, RenderTexture target, string directory, string name,
            Vector3 origin, Transform model, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>(); var hd = camera.GetComponent<HDAdditionalCameraData>();
            var aa = hd.antialiasing; bool history = hd.hasPersistentHistory;
            var settings = hd.renderingPathCustomFrameSettings; var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var intensities = lights.Select(light => light.intensity).ToArray(); var units = lights.Select(light => light.lightUnit).ToArray();
            var allLights = UnityEngine.Object.FindObjectsOfType<Light>(); var lightStates = allLights.Select(light => light.enabled).ToArray();
            var glazing = model.GetComponentsInChildren<MeshRenderer>(true).Where(renderer => renderer.name.StartsWith("Glass ", StringComparison.Ordinal)).ToArray();
            var visibility = glazing.Select(renderer => renderer.enabled).ToArray();
            var materialSlots = glazing.Select(renderer => renderer.sharedMaterials).ToArray();
            GameObject plane = null; Mesh nativePlane = null, simple = null; Material template = null, lit = null, fogOff = null;
            using (var exposure = new BusVehicleMaterialDiagnostics.PrivateExposure(camera, origin, 13f))
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing; hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(intensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                object originalPlane = BusGlassContextDiagnostic.SelectPlane(model, model.gameObject.layer, out plane, out nativePlane);
                var positions = nativePlane.vertices; var nativeIndices = nativePlane.GetTriangles(0);
                var unique = nativeIndices.Distinct().ToArray();
                if (unique.Length != 6 && unique.Length != 4) throw new InvalidOperationException("Selected native plane is not a two-triangle quad");
                var corners = nativeIndices.Select(index => positions[index]).Distinct().ToArray();
                if (corners.Length != 4) throw new InvalidOperationException("Expected four unique quad positions");
                var indices = nativeIndices.Select(index => Array.IndexOf(corners, positions[index])).ToArray();
                float ymin = corners.Min(v => v.y), ymax = corners.Max(v => v.y), zmin = corners.Min(v => v.z), zmax = corners.Max(v => v.z);
                simple = new Mesh { name = "Diagnostic ordinary Float32 single quad", hideFlags = HideFlags.DontSave };
                simple.vertices = corners; simple.uv = corners.Select(v => new Vector2((v.z - zmin) / (zmax - zmin), (v.y - ymin) / (ymax - ymin))).ToArray();
                simple.triangles = indices; simple.RecalculateNormals(); simple.RecalculateTangents(); simple.RecalculateBounds();
                plane.GetComponent<MeshFilter>().sharedMesh = simple; plane.SetActive(false);
                template = new Material(AssetDatabase.global.resources.materialLibrary.GetMaterial(11)) { name = "DefaultGlassDoubleSided ordinary quad diagnostic", hideFlags = HideFlags.DontSave };
                template.DisableKeyword("_TANGENTSPACE_OCTO"); bool templateValidated = HDMaterial.ValidateMaterial(template);
                if (template.IsKeywordEnabled("_TANGENTSPACE_OCTO")) throw new InvalidOperationException("Ordinary Float32 quad must not use OCTO decoding");
                var originalMaterial = glazing[0].sharedMaterial;
                fogOff = new Material(originalMaterial) { name = "Private transparent fog off diagnostic", hideFlags = HideFlags.DontSave };
                fogOff.SetFloat("_EnableFogOnTransparent", 0f); fogOff.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
                bool fogValidated = HDMaterial.ValidateMaterial(fogOff);
                if (fogOff.IsKeywordEnabled("_ENABLE_FOG_ON_TRANSPARENT")) throw new InvalidOperationException("Transparent fog keyword remained enabled");
                Color rgba = BusVehicleProbe.SampleLinear(originalMaterial.GetTexture("_BaseColorMap"), 2, 2);
                lit = new Material(Shader.Find("HDRP/Lit")) { name = "HDRP Lit transparent ordinary quad diagnostic", hideFlags = HideFlags.DontSave };
                lit.SetFloat("_SurfaceType", 1f); lit.SetFloat("_BlendMode", 0f); lit.SetFloat("_AlphaCutoffEnable", 0f);
                lit.SetFloat("_DoubleSidedEnable", 1f); lit.SetFloat("_Metallic", 0f); lit.SetFloat("_Smoothness", .96f);
                lit.SetFloat("_RefractionModel", 0f); lit.SetColor("_BaseColor", rgba);
                bool litValidated = HDMaterial.ValidateMaterial(lit);
                if (!lit.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")) throw new InvalidOperationException("Standard Lit control is not transparent");
                foreach (string group in new[] { "baseline", "atmosphere-off", "transparent-fog-keyword-off", "ordinary-quad-template", "ordinary-quad-lit", "local-probes-off", "direct-lights-reflections-fog-off" })
                {
                    for (int i = 0; i < glazing.Length; i++) { glazing[i].enabled = visibility[i]; glazing[i].sharedMaterials = materialSlots[i]; }
                    for (int i = 0; i < allLights.Length; i++) allLights[i].enabled = lightStates[i];
                    plane.SetActive(false);
                    var current = settings; var currentMask = mask;
                    bool branchesOff = group == "direct-lights-reflections-fog-off";
                    Set(ref current, ref currentMask, FrameSettingsField.AtmosphericScattering, group != "atmosphere-off" && !branchesOff);
                    Set(ref current, ref currentMask, FrameSettingsField.ReflectionProbe, group != "local-probes-off" && !branchesOff);
                    Set(ref current, ref currentMask, FrameSettingsField.PlanarProbe, group != "local-probes-off" && !branchesOff);
                    if (branchesOff)
                    {
                        foreach (var field in new[] { FrameSettingsField.SkyReflection, FrameSettingsField.DirectSpecularLighting,
                            FrameSettingsField.SSR, FrameSettingsField.TransparentSSR, FrameSettingsField.SSGI, FrameSettingsField.ProbeVolume })
                            Set(ref current, ref currentMask, field, false);
                        foreach (var light in allLights) light.enabled = false;
                    }
                    hd.renderingPathCustomFrameSettings = current; hd.renderingPathCustomFrameSettingsOverrideMask = currentMask;
                    bool quad = group.StartsWith("ordinary-quad", StringComparison.Ordinal);
                    if (group == "transparent-fog-keyword-off")
                        foreach (var renderer in glazing) renderer.sharedMaterial = fogOff;
                    if (quad)
                    {
                        foreach (var renderer in glazing) renderer.enabled = false;
                        plane.GetComponent<MeshRenderer>().sharedMaterial = group == "ordinary-quad-lit" ? lit : template;
                        plane.SetActive(true);
                    }
                    object immediate = null;
                    await BusVehicleProbe.Capture(camera, target, directory, name, 0, "pipeline-" + group + "-handrail-material",
                        new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true, () =>
                        {
                            var hdCamera = HDCamera.GetOrCreate(camera); var fs = hdCamera.frameSettings;
                            var fog = hdCamera.volumeStack.GetComponent<Fog>();
                            immediate = new { atmospheric_scattering = fs.IsEnabled(FrameSettingsField.AtmosphericScattering),
                                volume_fog_enabled = fog.enabled.value, volume_volumetric_fog_enabled = fog.enableVolumetricFog.value,
                                volumetrics = fs.IsEnabled(FrameSettingsField.Volumetrics), reflection_probe = fs.IsEnabled(FrameSettingsField.ReflectionProbe),
                                planar_probe = fs.IsEnabled(FrameSettingsField.PlanarProbe), sky_reflection = fs.IsEnabled(FrameSettingsField.SkyReflection),
                                direct_specular = fs.IsEnabled(FrameSettingsField.DirectSpecularLighting), ssr = fs.IsEnabled(FrameSettingsField.SSR),
                                ssgi = fs.IsEnabled(FrameSettingsField.SSGI), probe_volume = fs.IsEnabled(FrameSettingsField.ProbeVolume),
                                active_scene_lights = allLights.Count(light => light.enabled && light.gameObject.activeInHierarchy) };
                            if ((group == "atmosphere-off" || branchesOff) && fs.IsEnabled(FrameSettingsField.AtmosphericScattering)) throw new InvalidOperationException("Fog control did not apply");
                            if ((group == "local-probes-off" || branchesOff) && (fs.IsEnabled(FrameSettingsField.ReflectionProbe) || fs.IsEnabled(FrameSettingsField.PlanarProbe))) throw new InvalidOperationException("Local probe controls did not apply");
                        });
                    result.Add(new { group, scope = "Attribution only. Quad groups replace all glass with one ordinary quad at the original near window; opaque geometry remains complete.",
                        direct_lights_reflections_fog_off_limit = branchesOff ? "All current Light components disabled; ambient SH and emissive surfaces are not claimed disabled." : null,
                        original_plane = originalPlane, ordinary_quad = new { vertices = 4, triangles = 2, normals = simple.normals.Select(v => new[] { v.x, v.y, v.z }).ToArray(),
                            attributes = simple.GetVertexAttributes().Select(a => new { name = a.attribute.ToString(), format = a.format.ToString(), a.dimension, a.stream }).ToArray() },
                        template_validated = templateValidated, lit_validated = litValidated, fog_validated = fogValidated, immediate,
                        fog_constant_buffer_note = "Resolved frame settings are observed after Camera.Render. ShaderVariablesGlobal is a constant buffer; Shader.GetGlobalFloat is not asserted to read that buffer.",
                        material = quad ? BusVehicleMaterialDiagnostics.ShaderEvidence(plane.GetComponent<MeshRenderer>().sharedMaterial) : group == "transparent-fog-keyword-off" ? BusVehicleMaterialDiagnostics.ShaderEvidence(fogOff) : null });
                }
            }
            finally
            {
                if (plane != null) { plane.SetActive(false); UnityEngine.Object.Destroy(plane); }
                if (nativePlane != null) UnityEngine.Object.Destroy(nativePlane); if (simple != null) UnityEngine.Object.Destroy(simple);
                if (template != null) UnityEngine.Object.Destroy(template); if (lit != null) UnityEngine.Object.Destroy(lit); if (fogOff != null) UnityEngine.Object.Destroy(fogOff);
                for (int i = 0; i < glazing.Length; i++) { glazing[i].enabled = visibility[i]; glazing[i].sharedMaterials = materialSlots[i]; }
                for (int i = 0; i < allLights.Length; i++) if (allLights[i] != null) allLights[i].enabled = lightStates[i];
                hd.antialiasing = aa; hd.hasPersistentHistory = history;
                hd.renderingPathCustomFrameSettings = settings; hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(intensities[i], units[i]);
            }
            return result;
        }
        static void Set(ref FrameSettings settings, ref FrameSettingsOverrideMask mask, FrameSettingsField field, bool value)
        { settings.SetEnabled(field, value); mask.mask[(uint)field] = true; }
    }
}
