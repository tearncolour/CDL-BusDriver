using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>Controlled camera/light comparisons only; never changes a source material or mesh.</summary>
    internal static class BusVehicleMaterialDiagnostics
    {
        internal static async Task<List<object>> RunShadows(Camera camera, RenderTexture target, string directory,
            string name, Vector3 origin, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>();
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var savedAA = hd.antialiasing;
            bool savedHistory = hd.hasPersistentHistory;
            var savedSettings = hd.renderingPathCustomFrameSettings;
            var savedMask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var savedIntensities = lights.Select(light => light.intensity).ToArray();
            var savedUnits = lights.Select(light => light.lightUnit).ToArray();
            using (var exposure = new PrivateExposure(camera, origin, 13f))
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(savedIntensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                foreach (bool shadows in new[] { true, false })
                {
                    string group = shadows ? "baseline" : "no-shadows-diagnostic";
                    var settings = savedSettings; var mask = savedMask;
                    foreach (var field in new[] { FrameSettingsField.ShadowMaps, FrameSettingsField.ContactShadows, FrameSettingsField.ScreenSpaceShadows })
                    {
                        settings.SetEnabled(field, shadows); mask.mask[(uint)field] = true;
                    }
                    hd.renderingPathCustomFrameSettings = settings;
                    hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                    object immediate = null;
                    await BusVehicleProbe.Capture(camera, target, directory, name, 0, "shadow-" + group + "-handrail-material",
                        new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true, () =>
                        {
                            var hdcam = HDCamera.GetOrCreate(camera);
                            var resolved = hdcam.volumeStack.GetComponent<Exposure>();
                            var fs = hdcam.frameSettings;
                            if (resolved.mode.value != ExposureMode.Fixed || Mathf.Abs(resolved.fixedExposure.value - 13f) > .001f)
                                throw new InvalidOperationException("Shadow diagnostic exposure was not applied");
                            if (!shadows && (fs.IsEnabled(FrameSettingsField.ShadowMaps) || fs.IsEnabled(FrameSettingsField.ContactShadows) || fs.IsEnabled(FrameSettingsField.ScreenSpaceShadows)))
                                throw new InvalidOperationException("Shadow disable diagnostic was not applied");
                            immediate = new { timing = "Immediately after last probe Camera.Render, before any await or return to player loop.",
                                fixed_ev100 = resolved.fixedExposure.value,
                                shadow_maps = fs.IsEnabled(FrameSettingsField.ShadowMaps),
                                contact_shadows = fs.IsEnabled(FrameSettingsField.ContactShadows),
                                screen_space_shadows = fs.IsEnabled(FrameSettingsField.ScreenSpaceShadows),
                                antialiasing = fs.IsEnabled(FrameSettingsField.Antialiasing),
                                environment = BusNativeVehicleProbe.EnvironmentEvidence(),
                                sky_gpu_readback = BusProbeSkyReadback.Capture(directory, group),
                            };
                        });
                    result.Add(new { group, scope = "Shadow-disabled group is diagnostic only; production materials are unchanged.",
                        lights = lights.Select(light => LightEvidence(light, origin)).ToArray(), immediate });
                }
            }
            finally
            {
                hd.renderingPathCustomFrameSettings = savedSettings; hd.renderingPathCustomFrameSettingsOverrideMask = savedMask;
                hd.antialiasing = savedAA; hd.hasPersistentHistory = savedHistory;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(savedIntensities[i], savedUnits[i]);
            }
            return result;
        }

        internal static async Task<List<object>> RunInspection(Camera camera, RenderTexture target, string directory,
            string name, Vector3 origin, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>();
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var savedAA = hd.antialiasing;
            bool savedHistory = hd.hasPersistentHistory;
            var savedIntensities = lights.Select(light => light.intensity).ToArray();
            var savedUnits = lights.Select(light => light.lightUnit).ToArray();
            var added = new List<HDAdditionalLightData>();
            using (var exposure = new PrivateExposure(camera, origin, 13f))
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(savedIntensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                await Capture("baseline-handrail-material", new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f);
                // Inspection lights are separate from the bus and deliberately documented. They do not
                // alter material parameters, exposure or the package's production lighting behavior.
                Add("front inspection key", LightType.Directional, new Vector3(0f, 10f, 10f), 10000f, LightUnit.Lux);
                foreach (float z in new[] { -4f, -2f, 0f, 2f, 4f })
                    Add("interior inspection " + z, LightType.Point, new Vector3(0f, 1.65f, z), 16000f, LightUnit.Lumen);
                await Capture("lit-outside", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), 49f);
                await Capture("lit-handrail-material", new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f);
                await Capture("lit-rear-seat-access", new Vector3(-.20f, 1.60f, .50f), new Vector3(.74f, 1.05f, -1.42f), 70f);

                void Add(string label, LightType type, Vector3 position, float intensity, LightUnit unit)
                {
                    var obj = new GameObject("CDLC " + label) { hideFlags = HideFlags.DontSave };
                    obj.transform.position = origin + position; obj.transform.rotation = Quaternion.Euler(48f, 205f, 0f);
                    var light = obj.AddComponent<Light>(); light.type = type; light.range = 5.5f; light.shadows = LightShadows.None;
                    var data = obj.AddComponent<HDAdditionalLightData>(); data.SetIntensity(intensity, unit); added.Add(data);
                }
                async Task Capture(string view, Vector3 eye, Vector3 lookAt, float fov)
                {
                    await BusVehicleProbe.Capture(camera, target, directory, name, 0, "inspection-" + view, eye, lookAt, fov, shots, 64, true);
                    var hdcam = HDCamera.GetOrCreate(camera);
                    var resolved = hdcam.volumeStack.GetComponent<Exposure>();
                    if (resolved.mode.value != ExposureMode.Fixed || Mathf.Abs(resolved.fixedExposure.value - 13f) > .001f)
                        throw new InvalidOperationException("Inspection exposure override was not applied");
                    result.Add(new { group = view, scope = "Complete assembly, original production materials; dedicated inspection lights, not road-scene appearance.",
                        fixed_ev100 = resolved.fixedExposure.value, warmup_render_calls = 64,
                        antialiasing = hdcam.frameSettings.IsEnabled(FrameSettingsField.Antialiasing),
                        postprocess = hdcam.frameSettings.IsEnabled(FrameSettingsField.Postprocess),
                        lights = lights.Concat(added).Select(light => LightEvidence(light, origin)).ToArray(),
                        environment = BusNativeVehicleProbe.EnvironmentEvidence() });
                }
            }
            finally
            {
                foreach (var light in added) UnityEngine.Object.Destroy(light.gameObject);
                hd.antialiasing = savedAA; hd.hasPersistentHistory = savedHistory;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(savedIntensities[i], savedUnits[i]);
            }
            return result;
        }

        internal static async Task<List<object>> RunShader(Camera camera, RenderTexture target, string directory,
            string name, Vector3 origin, Transform model, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>();
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var savedAA = hd.antialiasing;
            bool savedHistory = hd.hasPersistentHistory;
            var savedIntensities = lights.Select(light => light.intensity).ToArray();
            var savedUnits = lights.Select(light => light.lightUnit).ToArray();
            var originals = new List<(MeshRenderer renderer, Material[] materials)>();
            var clones = new Dictionary<Material, Material>();
            using (var exposure = new PrivateExposure(camera, origin, 13f))
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(savedIntensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Material[] before = renderer.sharedMaterials;
                    Material[] after = (Material[])before.Clone();
                    bool changed = false;
                    for (int slot = 0; slot < after.Length; slot++)
                    {
                        Material material = after[slot];
                        if (material == null || material.shader == null || material.shader.name != "BH/GlsShader") continue;
                        if (!clones.TryGetValue(material, out var copy))
                        {
                            copy = new Material(material) { name = material.name + " diagnostic private clone", hideFlags = HideFlags.DontSave };
                            clones[material] = copy;
                        }
                        after[slot] = copy; changed = true;
                    }
                    if (changed) { originals.Add((renderer, before)); renderer.sharedMaterials = after; }
                }
                if (clones.Count == 0) throw new InvalidOperationException("No glass material available for private diagnostic");
                foreach (string group in new[] { "baseline", "coat-zero", "smooth-half", "backface-cull" })
                {
                    foreach (var pair in clones)
                    {
                        var material = pair.Value;
                        material.CopyPropertiesFromMaterial(pair.Key);
                        if (group == "coat-zero") material.SetFloat("_CoatMask", 0f);
                        if (group == "smooth-half") material.SetFloat("_Smoothness", .5f);
                        if (group == "backface-cull")
                        {
                            material.SetFloat("_DoubleSidedEnable", 0f);
                            material.DisableKeyword("_DOUBLESIDED_ON");
                            foreach (string property in new[] { "_CullMode", "_CullModeForward", "_TransparentCullMode" })
                                material.SetFloat(property, (float)CullMode.Back);
                        }
                    }
                    await BusVehicleProbe.Capture(camera, target, directory, name, 0, "shader-" + group + "-handrail-material",
                        new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true);
                    var hdcam = HDCamera.GetOrCreate(camera);
                    var resolvedExposure = hdcam.volumeStack.GetComponent<Exposure>();
                    if (resolvedExposure.mode.value != ExposureMode.Fixed || Mathf.Abs(resolvedExposure.fixedExposure.value - 13f) > .001f)
                        throw new InvalidOperationException("Private shader diagnostic exposure was not applied");
                    result.Add(new
                    {
                        group, scope = "Temporary private material clones only; no candidate appearance is approved by this diagnostic.",
                        fixed_ev100 = resolvedExposure.fixedExposure.value,
                        antialiasing = hdcam.frameSettings.IsEnabled(FrameSettingsField.Antialiasing),
                        postprocess = hdcam.frameSettings.IsEnabled(FrameSettingsField.Postprocess),
                        warmup_render_calls = 64, neutral_reflection_group = "Omitted: no reliable private override of the bound HDRP sky texture established.",
                        lights = lights.Select(light => LightEvidence(light, origin)).ToArray(),
                        original_materials = clones.Keys.Select(ShaderEvidence).ToArray(),
                        private_materials = clones.Values.Select(ShaderEvidence).ToArray(),
                        renderer_property_blocks = originals.Select(original => PropertyBlockEvidence(original.renderer)).ToArray(),
                        environment = BusNativeVehicleProbe.EnvironmentEvidence(),
                    });
                }
            }
            finally
            {
                foreach (var original in originals) original.renderer.sharedMaterials = original.materials;
                foreach (var clone in clones.Values) UnityEngine.Object.Destroy(clone);
                hd.antialiasing = savedAA; hd.hasPersistentHistory = savedHistory;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(savedIntensities[i], savedUnits[i]);
            }
            return result;
        }

        internal sealed class PrivateExposure : IDisposable
        {
            readonly HDAdditionalCameraData cameraData;
            readonly LayerMask layers;
            readonly Transform anchor;
            readonly GameObject volumeObject;
            readonly VolumeProfile profile;
            internal PrivateExposure(Camera camera, Vector3 origin, float ev)
            {
                cameraData = camera.GetComponent<HDAdditionalCameraData>();
                layers = cameraData.volumeLayerMask; anchor = cameraData.volumeAnchorOverride;
                volumeObject = new GameObject("CDLC private exposure only") { hideFlags = HideFlags.DontSave, layer = 31 };
                volumeObject.transform.position = origin;
                var bounds = volumeObject.AddComponent<BoxCollider>(); bounds.isTrigger = true; bounds.size = Vector3.one * 100f;
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                var exposure = profile.Add<Exposure>(true);
                exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(ev); exposure.compensation.Override(0f);
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = false; volume.blendDistance = 0f; volume.weight = 1f; volume.priority = float.MaxValue;
                volume.sharedProfile = profile;
                cameraData.volumeLayerMask = layers.value | (1 << 31); cameraData.volumeAnchorOverride = camera.transform;
            }
            public void Dispose()
            {
                cameraData.volumeLayerMask = layers; cameraData.volumeAnchorOverride = anchor;
                  volumeObject.SetActive(false);
                  UnityEngine.Object.Destroy(volumeObject); UnityEngine.Object.Destroy(profile);
            }
        }

        internal static object ShaderEvidence(Material material)
        {
            var shader = material.shader;
            var properties = new List<object>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string property = shader.GetPropertyName(i);
                ShaderPropertyType type = shader.GetPropertyType(i);
                object value;
                if (type == ShaderPropertyType.Texture)
                {
                    Texture texture = material.GetTexture(property);
                    value = texture == null ? null : (object)new { texture.name, texture.width, texture.height,
                        dimension = texture.dimension.ToString(), format = texture.graphicsFormat.ToString(),
                        wrap = texture.wrapMode.ToString(), filter = texture.filterMode.ToString(), mip_bias = texture.mipMapBias };
                }
                else if (type == ShaderPropertyType.Color || type == ShaderPropertyType.Vector)
                {
                    Vector4 vector = type == ShaderPropertyType.Color ? (Vector4)material.GetColor(property) : material.GetVector(property);
                    value = new[] { vector.x, vector.y, vector.z, vector.w };
                }
                else value = material.GetFloat(property);
                properties.Add(new { index = i, name = property, type = type.ToString(),
                    description = shader.GetPropertyDescription(i), flags = shader.GetPropertyFlags(i).ToString(), value });
            }
            return new { material = material.name, shader = shader.name, shader_instance_id = shader.GetInstanceID(),
                keywords = material.shaderKeywords, queue = material.renderQueue, enable_instancing = material.enableInstancing, properties,
                passes = Enumerable.Range(0, material.passCount).Select(i => new { index = i, name = material.GetPassName(i) }).ToArray() };
        }

        internal static object PropertyBlockEvidence(MeshRenderer renderer)
        {
            var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
            Vector4 unityFade = block.GetVector("unity_LODFade");
            return new { renderer = renderer.name, is_empty = block.isEmpty,
                has_colossal_lod_fade = block.HasFloat("colossal_LodFade"), colossal_lod_fade = block.GetFloat("colossal_LodFade"),
                has_unity_lod_fade = block.HasVector("unity_LODFade"), unity_lod_fade = new[] { unityFade.x, unityFade.y, unityFade.z, unityFade.w } };
        }

        internal static async Task<object> RunRefraction(Camera camera, RenderTexture target, string directory,
            string name, int lod, Transform model, Func<object> lighting, List<object> shots)
        {
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var savedSettings = hd.renderingPathCustomFrameSettings;
            var savedMask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var savedAA = hd.antialiasing;
            bool savedHistory = hd.hasPersistentHistory;
            var views = new List<object>();
            string inputs = RefractionInputs(model, lighting);
            Exception failure = null;
            try
            {
                foreach (string phase in new[] { "on", "off", "on-restored" })
                {
                    BusVehicleAssetSystem.RequireTestMenu();
                    bool enabled = phase != "off";
                    var settings = savedSettings;
                    var mask = savedMask;
                    settings.SetEnabled(FrameSettingsField.Refraction, enabled);
                    mask.mask[(uint)FrameSettingsField.Refraction] = true;
                    hd.renderingPathCustomFrameSettings = settings;
                    hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                    object resolved = null;
                    int firstFrame = Time.frameCount;
                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, lod, "refraction-" + phase + "-handrail-material",
                        new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, () =>
                        {
                            var actual = HDCamera.GetOrCreate(camera);
                            if (actual.frameSettings.IsEnabled(FrameSettingsField.Refraction) != enabled)
                                throw new InvalidOperationException("Refraction cycle did not resolve the requested camera setting: " + phase);
                            resolved = new { frame = Time.frameCount,
                                refraction = actual.frameSettings.IsEnabled(FrameSettingsField.Refraction),
                                ssr = actual.frameSettings.IsEnabled(FrameSettingsField.SSR),
                                transparent_ssr = actual.frameSettings.IsEnabled(FrameSettingsField.TransparentSSR),
                                transparent_prepass = actual.frameSettings.IsEnabled(FrameSettingsField.TransparentPrepass),
                                environment = BusNativeVehicleProbe.EnvironmentEvidence() };
                        });
                    if (resolved == null || RefractionInputs(model, lighting) != inputs)
                        throw new InvalidOperationException("Refraction cycle changed or failed to observe the complete model/material/light inputs: " + phase);
                    views.Add(new { phase, refraction_requested = enabled, first_frame = firstFrame,
                        last_frame = Time.frameCount, resolved, inputs_unchanged = true });
                }
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                var errors = new List<Exception>();
                CabCleanup.Try(() => hd.renderingPathCustomFrameSettings = savedSettings, errors);
                CabCleanup.Try(() => hd.renderingPathCustomFrameSettingsOverrideMask = savedMask, errors);
                CabCleanup.Try(() => hd.antialiasing = savedAA, errors);
                CabCleanup.Try(() => hd.hasPersistentHistory = savedHistory, errors);
                if (errors.Count != 0)
                {
                    if (failure != null) errors.Insert(0, failure);
                    throw new AggregateException("Refraction cycle camera cleanup failed.", errors);
                }
            }
            if (!hd.renderingPathCustomFrameSettings.Equals(savedSettings) ||
                !hd.renderingPathCustomFrameSettingsOverrideMask.Equals(savedMask))
                throw new InvalidOperationException("Refraction cycle did not restore the original camera frame settings and mask");
            return new { name, lod, views, camera_settings_restored = true,
                inputs = Newtonsoft.Json.Linq.JToken.Parse(inputs),
                scope = "Same complete private cab, imported production glass, inspection lights, EV12 and handrail camera; only camera refraction is varied. Off is diagnostic only; On/Off/On differences do not by themselves identify the cause of a visible ripple." };
        }

        static string RefractionInputs(Transform model, Func<object> lighting) =>
            Newtonsoft.Json.JsonConvert.SerializeObject(new {
                renderers = model.GetComponentsInChildren<MeshRenderer>(true).Select(renderer => new {
                    renderer.name, renderer.enabled, active = renderer.gameObject.activeInHierarchy, renderer.renderingLayerMask,
                    materials = renderer.sharedMaterials.Select(material => new {
                        id = material.GetInstanceID(), state = ShaderEvidence(material),
                        passes = Enumerable.Range(0, material.passCount).Select(i => new {
                            name = material.GetPassName(i), enabled = material.GetShaderPassEnabled(material.GetPassName(i)) }) }) }),
                lighting = lighting() });

        internal static async Task<List<object>> RunIsolation(Camera camera, RenderTexture target, string directory,
            string name, Vector3 origin, Transform model, BusGlass glass, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>();
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var savedSettings = hd.renderingPathCustomFrameSettings;
            var savedMask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var savedAA = hd.antialiasing;
            var savedLayers = hd.volumeLayerMask;
            var savedAnchor = hd.volumeAnchorOverride;
            bool savedHistory = hd.hasPersistentHistory;
            var savedIntensities = lights.Select(light => light.intensity).ToArray();
            var savedUnits = lights.Select(light => light.lightUnit).ToArray();
            var volumeObject = new GameObject("CDLC probe local exposure only") { hideFlags = HideFlags.DontSave, layer = 31 };
            volumeObject.transform.position = origin;
            var bounds = volumeObject.AddComponent<BoxCollider>();
            bounds.isTrigger = true; bounds.size = Vector3.one * 100f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var exposureOverride = profile.Add<Exposure>(true);
            exposureOverride.mode.Override(ExposureMode.Fixed);
            exposureOverride.compensation.Override(0f);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = false; volume.blendDistance = 0f; volume.weight = 1f;
            volume.priority = float.MaxValue; volume.sharedProfile = profile;
            try
            {
                hd.volumeLayerMask = savedLayers.value | (1 << 31);
                hd.volumeAnchorOverride = camera.transform;
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(savedIntensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                foreach (string group in new[] { "fixed-ev10", "fixed-ev10-no-refraction-diagnostic", "fixed-ev10-no-glass-diagnostic", "fixed-ev8" })
                {
                    float ev = group == "fixed-ev8" ? 8f : 10f;
                    exposureOverride.fixedExposure.Override(ev);
                    bool visible = group != "fixed-ev10-no-glass-diagnostic";
                    bool refraction = group != "fixed-ev10-no-refraction-diagnostic";
                    glass.Visible = visible;
                    var settings = savedSettings;
                    var mask = savedMask;
                    settings.SetEnabled(FrameSettingsField.Refraction, refraction);
                    mask.mask[(uint)FrameSettingsField.Refraction] = true;
                    hd.renderingPathCustomFrameSettings = settings;
                    hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                    var views = new List<object>();
                    await Capture("outside", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), 49f);
                    await Capture("handrail-material", new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f);
                    await Capture("rear-seat-access", new Vector3(-.20f, 1.60f, .50f), new Vector3(.74f, 1.05f, -1.42f), 70f);
                    result.Add(new
                    {
                        group, scope = "Diagnostic only. Glass hidden/refraction disabled cases are not final appearance checks.",
                        glass_visible = visible, refraction_requested = refraction, fixed_ev100 = ev,
                        local_exposure_volume = new { center_model_m = XYZ(Vector3.zero), box_size_m = XYZ(bounds.size), layer = 31, exposure_only = true },
                        antialiasing_requested = hd.antialiasing.ToString(), warmup_render_calls_per_view = 64,
                        camera_history_reset_per_view = true, lights = lights.Select(light => LightEvidence(light, origin)).ToArray(),
                        glass_materials = GlassEvidence(model), views,
                    });
                    async Task Capture(string view, Vector3 eye, Vector3 lookAt, float fov)
                    {
                        await BusVehicleProbe.Capture(camera, target, directory, name, 0, "isolation-" + group + "-" + view,
                            eye, lookAt, fov, shots, 64, true);
                        var hdcam = HDCamera.GetOrCreate(camera);
                        var fs = hdcam.frameSettings;
                        var exposure = hdcam.volumeStack.GetComponent<Exposure>();
                        if (exposure.mode.value != ExposureMode.Fixed || Mathf.Abs(exposure.fixedExposure.value - ev) > .001f)
                            throw new InvalidOperationException("Private exposure override was not applied");
                        if (fs.IsEnabled(FrameSettingsField.Refraction) != refraction)
                            throw new InvalidOperationException("Refraction diagnostic was not applied");
                        views.Add(new
                        {
                            view, camera_position_model_m = XYZ(eye), look_at_model_m = XYZ(lookAt),
                            resolved_frame_settings = new { antialiasing = fs.IsEnabled(FrameSettingsField.Antialiasing),
                                postprocess = fs.IsEnabled(FrameSettingsField.Postprocess), ssr = fs.IsEnabled(FrameSettingsField.SSR),
                                transparent_ssr = fs.IsEnabled(FrameSettingsField.TransparentSSR), refraction = fs.IsEnabled(FrameSettingsField.Refraction),
                                exposure_control = fs.IsEnabled(FrameSettingsField.ExposureControl) },
                            exposure = new { mode = exposure.mode.value.ToString(), fixed_ev100 = exposure.fixedExposure.value,
                                compensation = exposure.compensation.value },
                            environment = BusNativeVehicleProbe.EnvironmentEvidence(),
                        });
                    }
                }
            }
            finally
            {
                glass.Visible = true;
                hd.renderingPathCustomFrameSettings = savedSettings; hd.renderingPathCustomFrameSettingsOverrideMask = savedMask;
                hd.antialiasing = savedAA; hd.hasPersistentHistory = savedHistory;
                hd.volumeLayerMask = savedLayers; hd.volumeAnchorOverride = savedAnchor;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(savedIntensities[i], savedUnits[i]);
                UnityEngine.Object.Destroy(volumeObject); UnityEngine.Object.Destroy(profile);
            }
            return result;
        }

        internal static async Task<List<object>> Run(Camera camera, RenderTexture target, string directory,
            string name, Vector3 origin, Transform model, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>();
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var savedSettings = hd.renderingPathCustomFrameSettings;
            var savedMask = hd.renderingPathCustomFrameSettingsOverrideMask;
            var savedAA = hd.antialiasing;
            bool savedHistory = hd.hasPersistentHistory;
            var savedIntensities = lights.Select(light => light.intensity).ToArray();
            var savedUnits = lights.Select(light => light.lightUnit).ToArray();
            try
            {
                foreach (string group in new[] { "baseline-none", "taa", "taa-no-ssr-diagnostic", "taa-strong-fill" })
                {
                    hd.renderingPathCustomFrameSettings = savedSettings;
                    hd.renderingPathCustomFrameSettingsOverrideMask = savedMask;
                    hd.antialiasing = group == "baseline-none" ? HDAdditionalCameraData.AntialiasingMode.None :
                        HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                    hd.hasPersistentHistory = true;
                    for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(savedIntensities[i], savedUnits[i]);
                    if (group == "taa-no-ssr-diagnostic")
                    {
                        var settings = hd.renderingPathCustomFrameSettings;
                        var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
                        foreach (var field in new[] { FrameSettingsField.SSR, FrameSettingsField.TransparentSSR })
                        {
                            settings.SetEnabled(field, false);
                            mask.mask[(uint)field] = true;
                        }
                        hd.renderingPathCustomFrameSettings = settings;
                        hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                    }
                    if (group == "taa-strong-fill")
                        foreach (var light in lights)
                            if (light.GetComponent<Light>().type == LightType.Point)
                                light.SetIntensity(40000f, LightUnit.Lumen);

                    var views = new List<object>();
                    await Capture("handrail-material", new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f);
                    await Capture("rear-seat-access", new Vector3(-.20f, 1.60f, .50f), new Vector3(.74f, 1.05f, -1.42f), 70f);
                    result.Add(new
                    {
                        group, scope = "LOD0 camera/light diagnostic; disabled SSR group is not a proposed final material fix",
                        antialiasing_requested = hd.antialiasing.ToString(), persistent_history = hd.hasPersistentHistory,
                        warmup_render_calls_per_view = 64, camera_history_reset_per_view = true,
                        lights = lights.Select(light => LightEvidence(light, origin)).ToArray(),
                        glass_materials = GlassEvidence(model), views,
                    });
                    async Task Capture(string view, Vector3 eye, Vector3 lookAt, float fov)
                    {
                        await BusVehicleProbe.Capture(camera, target, directory, name, 0, "diagnostic-" + group + "-" + view,
                            eye, lookAt, fov, shots, 64, true);
                        var hdcam = HDCamera.GetOrCreate(camera);
                        var fs = hdcam.frameSettings;
                        var exposure = hdcam.volumeStack.GetComponent<Exposure>();
                        views.Add(new
                        {
                            view, camera_position_model_m = XYZ(eye), look_at_model_m = XYZ(lookAt),
                            resolved_frame_settings = new
                            {
                                antialiasing = fs.IsEnabled(FrameSettingsField.Antialiasing),
                                postprocess = fs.IsEnabled(FrameSettingsField.Postprocess),
                                ssr = fs.IsEnabled(FrameSettingsField.SSR), transparent_ssr = fs.IsEnabled(FrameSettingsField.TransparentSSR),
                                refraction = fs.IsEnabled(FrameSettingsField.Refraction),
                                exposure_control = fs.IsEnabled(FrameSettingsField.ExposureControl),
                            },
                            exposure = exposure == null ? null : new
                            {
                                mode = exposure.mode.value.ToString(), fixed_ev100 = exposure.fixedExposure.value,
                                compensation = exposure.compensation.value, limit_min = exposure.limitMin.value,
                                limit_max = exposure.limitMax.value, adaptation = exposure.adaptationMode.value.ToString(),
                            },
                            environment = BusNativeVehicleProbe.EnvironmentEvidence(),
                        });
                    }
                }
            }
            finally
            {
                hd.renderingPathCustomFrameSettings = savedSettings;
                hd.renderingPathCustomFrameSettingsOverrideMask = savedMask;
                hd.antialiasing = savedAA;
                hd.hasPersistentHistory = savedHistory;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(savedIntensities[i], savedUnits[i]);
            }
            return result;
        }

        static object LightEvidence(HDAdditionalLightData light, Vector3 origin)
        {
            var legacy = light.GetComponent<Light>();
            return new
            {
                light.name, type = legacy.type.ToString(), hd_type = light.type.ToString(),
                intensity = light.intensity, unit = light.lightUnit.ToString(), legacy_intensity = legacy.intensity,
                position_model_m = XYZ(light.transform.position - origin), forward = XYZ(light.transform.forward),
                range_m = legacy.range, shadows = legacy.shadows.ToString(),
                colour = new[] { legacy.color.r, legacy.color.g, legacy.color.b },
            };
        }

        static object[] GlassEvidence(Transform model)
        {
            return model.GetComponentsInChildren<MeshRenderer>(true).SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null && material.shader != null && material.shader.name == "BH/GlsShader")
                .Distinct().Select(material =>
                {
                    var properties = new Dictionary<string, float>();
                    foreach (string property in new[] { "_DepthOffset", "_DepthOffsetEnable", "_RefractionDistance", "_Thickness",
                        "_CoatMask", "_Metallic", "_Smoothness", "_AlphaCutoffEnable", "_ReceivesSSR", "_ReceivesSSRTransparent",
                        "_TransparentDepthPrepassEnable", "_TransparentDepthPostpassEnable", "colossal_LodFade" })
                        if (material.HasProperty(property)) properties[property] = material.GetFloat(property);
                    return (object)new { material.name, shader = material.shader.name, keywords = material.shaderKeywords,
                        has_colossal_lod_fade = material.HasProperty("colossal_LodFade"), properties };
                }).ToArray();
        }

        static float[] XYZ(Vector3 vector) => new[] { vector.x, vector.y, vector.z };
    }
}
