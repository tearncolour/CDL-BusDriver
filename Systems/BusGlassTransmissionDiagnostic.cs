using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static class BusGlassTransmissionDiagnostic
    {
        internal static async Task<List<object>> Run(Camera camera, RenderTexture target, string directory, string name,
            Vector3 origin, Transform model, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>(); var hd = camera.GetComponent<HDAdditionalCameraData>();
            var aa = hd.antialiasing; bool history = hd.hasPersistentHistory; var clear = hd.clearColorMode;
            var settings = hd.renderingPathCustomFrameSettings; var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var intensities = lights.Select(light => light.intensity).ToArray(); var units = lights.Select(light => light.lightUnit).ToArray();
            var glazing = model.GetComponentsInChildren<MeshRenderer>(true).Where(renderer => renderer.name.StartsWith("Glass ", StringComparison.Ordinal)).ToArray();
            var visibility = glazing.Select(renderer => renderer.enabled).ToArray();
            var slots = glazing.Select(renderer => renderer.sharedMaterials).ToArray();
            GameObject plane = null, checker = null, lamp = null; Mesh packed = null, ordinary = null;
            var materials = new List<Material>(); var centres = new List<Vector3>();
            using (var exposure = new BusVehicleMaterialDiagnostics.PrivateExposure(camera, origin, 13f))
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing; hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(intensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                object selected = BusGlassContextDiagnostic.SelectPlane(model, model.gameObject.layer, out plane, out packed);
                var positions = packed.vertices; var uvs = packed.uv; var indices = packed.GetTriangles(0);
                ordinary = new Mesh { name = "Float32 two triangle source-UV transmission control", hideFlags = HideFlags.DontSave };
                ordinary.vertices = indices.Select(index => positions[index]).ToArray(); ordinary.uv = indices.Select(index => uvs[index]).ToArray();
                ordinary.triangles = Enumerable.Range(0, 6).ToArray(); ordinary.RecalculateNormals(); ordinary.RecalculateTangents(); ordinary.RecalculateBounds();
                plane.GetComponent<MeshFilter>().sharedMesh = ordinary;
                var source = glazing[0].sharedMaterial;
                bool originalPrepass = source.GetShaderPassEnabled("TransparentDepthPrepass");
                var noPrepass = new Material(source) { name = "Actual glass prepass-disabled control", hideFlags = HideFlags.DontSave }; materials.Add(noPrepass);
                noPrepass.SetShaderPassEnabled("TransparentDepthPrepass", false);
                var gls = new Material(source) { name = "Actual glass transmission Float32 control", hideFlags = HideFlags.DontSave }; materials.Add(gls);
                gls.DisableKeyword("_TANGENTSPACE_OCTO"); HDMaterial.ValidateMaterial(gls);
                if (gls.IsKeywordEnabled("_TANGENTSPACE_OCTO")) throw new InvalidOperationException("Float32 transmission quad still uses packed normals");
                var lit = new Material(Shader.Find("HDRP/Lit")) { name = "Lit transmission control", hideFlags = HideFlags.DontSave }; materials.Add(lit);
                lit.SetFloat("_SurfaceType", 1f); lit.SetFloat("_BlendMode", 0f); lit.SetFloat("_AlphaCutoffEnable", 0f); lit.SetFloat("_DoubleSidedEnable", 1f);
                lit.SetFloat("_Metallic", 0f); lit.SetFloat("_Smoothness", .96f); lit.SetFloat("_RefractionModel", 0f);
                lit.SetColor("_BaseColor", BusVehicleProbe.SampleLinear(source.GetTexture("_BaseColorMap"), 2, 2)); HDMaterial.ValidateMaterial(lit);
                checker = new GameObject("Temporary external red green checker") { hideFlags = HideFlags.DontSave };
                checker.transform.position = origin;
                for (int i = 0; i < 4; i++)
                {
                    var material = new Material(Shader.Find("HDRP/Lit")) { name = "Lit checker " + i, hideFlags = HideFlags.DontSave }; materials.Add(material);
                    material.SetColor("_BaseColor", i % 2 == 0 ? new Color(.9f, .025f, .025f) : new Color(.025f, .9f, .025f));
                    material.SetFloat("_Metallic", 0f); material.SetFloat("_Smoothness", .15f); HDMaterial.ValidateMaterial(material);
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = "External checker " + i; box.transform.SetParent(checker.transform, false);
                    box.transform.localPosition = new Vector3(1.48f, 2.03f + (i / 2) * .30f, .8f + (i % 2) * .30f);
                    box.transform.localScale = new Vector3(.08f, .26f, .26f); box.GetComponent<MeshRenderer>().sharedMaterial = material;
                    UnityEngine.Object.Destroy(box.GetComponent<Collider>());
                    centres.Add(box.transform.position - new Vector3(.04f, 0f, 0f));
                }
                lamp = new GameObject("Transmission checker inspection light") { hideFlags = HideFlags.DontSave };
                lamp.transform.position = origin + new Vector3(1.34f, 2.18f, .95f);
                var light = lamp.AddComponent<Light>(); light.type = LightType.Point; light.range = 2f; light.shadows = LightShadows.None;
                var hdLight = lamp.AddComponent<HDAdditionalLightData>(); hdLight.SetIntensity(1200f, LightUnit.Lumen);
                foreach (string group in new[] { "actual-gls-checker", "lit-checker", "no-pane-checker", "full-vehicle-baseline", "full-vehicle-material-prepass-off", "full-vehicle-frame-prepass-off", "full-vehicle-sky-clear" })
                {
                    bool sky = group == "full-vehicle-sky-clear";
                    bool full = group.StartsWith("full-vehicle", StringComparison.Ordinal);
                    for (int i = 0; i < glazing.Length; i++) { glazing[i].enabled = full && visibility[i]; glazing[i].sharedMaterials = slots[i]; }
                    checker.SetActive(!full); lamp.SetActive(!full); plane.SetActive(!full && group != "no-pane-checker");
                    plane.GetComponent<MeshRenderer>().sharedMaterial = group == "lit-checker" ? lit : gls;
                    if (group == "full-vehicle-material-prepass-off") foreach (var renderer in glazing) renderer.sharedMaterial = noPrepass;
                    var current = settings; var currentMask = mask;
                    if (group == "full-vehicle-frame-prepass-off") { current.SetEnabled(FrameSettingsField.TransparentPrepass, false); currentMask.mask[(uint)FrameSettingsField.TransparentPrepass] = true; }
                    hd.renderingPathCustomFrameSettings = current; hd.renderingPathCustomFrameSettingsOverrideMask = currentMask;
                    hd.clearColorMode = sky ? HDAdditionalCameraData.ClearColorMode.Sky : clear;
                    object immediate = null;
                    await BusVehicleProbe.Capture(camera, target, directory, name, 0, "transmission-" + group + "-handrail-material",
                        new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true, () =>
                        immediate = new { clear_mode = hd.clearColorMode.ToString(), fixed_ev = HDCamera.GetOrCreate(camera).volumeStack.GetComponent<Exposure>().fixedExposure.value,
                            frame_transparent_prepass = HDCamera.GetOrCreate(camera).frameSettings.IsEnabled(FrameSettingsField.TransparentPrepass),
                            original_material_prepass = originalPrepass, current_material_prepass = group == "full-vehicle-material-prepass-off" ? noPrepass.GetShaderPassEnabled("TransparentDepthPrepass") : source.GetShaderPassEnabled("TransparentDepthPrepass") });
                    var pixels = BusGlassProbe.Read(target); var samples = new List<object>();
                    if (!full)
                    {
                        for (int i = 0; i < centres.Count; i++)
                        {
                            Vector3 v = camera.WorldToViewportPoint(centres[i]);
                            if (v.z <= 0 || v.x < .02f || v.x > .98f || v.y < .02f || v.y > .98f) throw new InvalidOperationException("Checker centre outside camera");
                            int x = (int)(v.x * target.width), y = (int)(v.y * target.height); double r = 0, g = 0, b = 0;
                            for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++) { var p = pixels[(y + dy) * target.width + x + dx]; r += p.r; g += p.g; b += p.b; }
                            samples.Add(new { index = i, expected = i % 2 == 0 ? "red" : "green", pixel = new[] { x, y }, mean_rgb_8bit = new[] { r / 49d, g / 49d, b / 49d },
                                dominant_channel_detected = i % 2 == 0 ? r > g * 1.4 + 490 : g > r * 1.4 + 490 });
                        }
                    }
                    result.Add(new { group, scope = "External illuminated checker is a temporary transmission witness, not a model addition; no-pane and Lit groups are diagnostic only.",
                        selected_plane = selected, immediate, samples, source_uv_preserved = true,
                        checker_bounds = "x=1.44..1.52, y=1.90..2.46, z=.67..1.23 metres in model space; outside body side x<=1.26.",
                        checker_light_lumen = 1200f, material = full || group == "no-pane-checker" ? null : BusVehicleMaterialDiagnostics.ShaderEvidence(plane.GetComponent<MeshRenderer>().sharedMaterial) });
                }
            }
            finally
            {
                foreach (var obj in new[] { plane, checker, lamp }) if (obj != null) { obj.SetActive(false); UnityEngine.Object.Destroy(obj); }
                if (packed != null) UnityEngine.Object.Destroy(packed); if (ordinary != null) UnityEngine.Object.Destroy(ordinary);
                foreach (var material in materials) UnityEngine.Object.Destroy(material);
                for (int i = 0; i < glazing.Length; i++) { glazing[i].enabled = visibility[i]; glazing[i].sharedMaterials = slots[i]; }
                hd.antialiasing = aa; hd.hasPersistentHistory = history; hd.clearColorMode = clear;
                hd.renderingPathCustomFrameSettings = settings; hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(intensities[i], units[i]);
            }
            return result;
        }
    }
}
