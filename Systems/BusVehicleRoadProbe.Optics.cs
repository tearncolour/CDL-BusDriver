#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CDLC.Asset.Rendering;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Game.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static partial class BusVehicleRoadProbe
    {
        static List<BusGlassOpticsBinding> NativeOpticsBindings(SystemBase host, Entity[] sections)
        {
            var expected = new Dictionary<SurfaceAsset, string>();
            var prefabs = host.World.GetExistingSystemManaged<PrefabSystem>();
            foreach (Entity entity in sections)
            {
                Require(prefabs.TryGetPrefab(host.EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab, out PrefabBase prefab) &&
                    prefab is ObjectGeometryPrefab, "Optics vehicle prefab is unavailable");
                foreach (ObjectMeshInfo mesh in ((ObjectGeometryPrefab)prefab).m_Meshes)
                    if (mesh.m_Mesh is RenderPrefab root)
                    {
                        var levels = new List<RenderPrefab> { root };
                        if (root.TryGet(out LodProperties lod) && lod.m_LodMeshes != null) levels.AddRange(lod.m_LodMeshes);
                        foreach (RenderPrefab level in levels)
                            for (int i = 0; i < level.materialCount; i++)
                            {
                                SurfaceAsset surface = level.GetSurfaceAsset(i);
                                if (surface != null && surface.name.Contains("_Gls")) expected[surface] = entity.ToString();
                            }
                    }
            }
            var manager = host.World.GetExistingSystemManaged<BatchManagerSystem>();
            var managed = manager.GetManagedBatches();
            var native = manager.GetNativeBatchGroups(true, out var dependencies); dependencies.Complete();
            var result = new List<BusGlassOpticsBinding>();
            for (int i = 0; i < managed.BatchCount; i++)
                if (managed.GetBatch(i) is CustomBatch batch && batch.sourceSurface != null && expected.TryGetValue(batch.sourceSurface, out string section) &&
                    native.IsValidGroup(batch.groupIndex) && native.GetBatchIndex(batch.groupIndex, i) >= 0)
                {
                    var binding = new BusGlassOpticsBinding { Batch = batch, Index = i, Surface = batch.sourceSurface,
                        Material = batch.material, Section = section, Manager = manager };
                    binding.Validate(); result.Add(binding);
                }
            Require(sections.All(s => result.Any(b => b.Section == s.ToString())), "Optics requires current native batches for every test section");
            return result;
        }

        static async Task<bool> RunGlassOptics(List<BusGlassOpticsBinding> bindings, Camera camera, int hour, string mode,
            string directory, Action check, Func<Task> settle, Func<string, Task> capture, Func<object> sceneEvidence, List<object> results)
        {
            var report = new Dictionary<string, object> { ["hour"] = hour, ["mode"] = mode,
                ["status"] = "running", ["assessment"] = "Parameter comparison only; no candidate is declared better" };
            results.Add(report);
            var sources = new Dictionary<SurfaceAsset, NativeMaterialLease>();
            var sourceBefore = new Dictionary<SurfaceAsset, JObject>();
            var registeredBefore = new Dictionary<SurfaceAsset, JObject>();
            var privateBefore = new Dictionary<Material, JObject>();
            var customBefore = new Dictionary<BusGlassOpticsBinding, JObject>();
            var rows = new List<object>(); report["captures"] = rows;
            var maskReads = new List<object>(); report["mask_readbacks"] = maskReads;
            var exposureReads = new List<object>(); report["exposure_readbacks"] = exposureReads;
            var cleanup = new List<Exception>();
            BusGlassOpticsMaterials materials = null;
            var exposure = new BusGlassOpticsExposure();
            Exception failure = null;
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            float fov = camera.fieldOfView;
            BusGlassOpticsExposure.GpuExposure? lockedExposure = null;
            bool readable = true;
            try
            {
                check();
                Require(bindings.Count > 0, "Optics has no exact glass bindings");
                foreach (BusGlassOpticsBinding binding in bindings)
                {
                    binding.Validate();
                    Require(binding.Material.GetFloat("_ReceivesSSRTransparent") == 0 &&
                        binding.Material.GetFloat("_TransparentDepthPrepassEnable") == 0 &&
                        !binding.Material.GetShaderPassEnabled("TransparentDepthPrepass"), "Optics requires the existing SSR/prepass compatibility policy");
                    Require(binding.Material.IsKeywordEnabled("_TANGENTSPACE_OCTO"), "Optics target lost packed-normal decoding");
                    if (!sources.ContainsKey(binding.Surface))
                    {
                        var source = NativeMaterialLease.Acquire(binding.Surface); sources.Add(binding.Surface, source);
                        Require(source.Material != binding.Material, "Optics cannot modify an original native material");
                        sourceBefore.Add(binding.Surface, OpticsMaterial(source.Material));
                        registeredBefore.Add(binding.Surface, RegisteredOpticsSurface(binding.Surface));
                    }
                    Require(sources[binding.Surface].Material.GetFloat("_Thickness") == binding.Material.GetFloat("_Thickness"),
                        "CDL private thickness differs from its native source before comparison");
                    if (binding.Batch?.customProps != null && binding.Batch.customProps.HasProperty(Shader.PropertyToID("_Thickness")))
                        Require(binding.Batch.customProps.GetFloat("_Thickness") == binding.Material.GetFloat("_Thickness"),
                            "Native custom thickness differs from the material baseline");
                    if (!privateBefore.ContainsKey(binding.Material)) privateBefore.Add(binding.Material, OpticsMaterial(binding.Material));
                    customBefore.Add(binding, OpticsPropertyBlock(binding));
                }
                var neutral = new HashSet<Texture>();
                foreach (Material material in privateBefore.Keys)
                {
                    Texture texture = material.GetTexture("_MaskMap");
                    if (texture is Texture2D && texture.width == 1 && texture.height == 1 && texture.name == "CDL neutral glass mask")
                    {
                        var readback = new Dictionary<string, object>(); maskReads.Add(readback);
                        Color32 pixel = await ReadOpticsMask(texture, check, readback);
                        if (pixel.r == 0 && pixel.g == 255 && pixel.b == 0 && pixel.a == 245) neutral.Add(texture);
                    }
                }
                materials = new BusGlassOpticsMaterials(privateBefore.Keys, neutral);
                report["targets"] = bindings.Select(b => new { section = b.Section, surface = b.Surface.name, surface_id = b.Surface.id.ToString(),
                    material_id = b.Material.GetInstanceID(), renderer = b.Renderer != null ? b.Renderer.name : null,
                    rendering_layer_mask = b.Renderer != null ? (uint?)b.Renderer.renderingLayerMask : null,
                    native_batch_index = b.Batch != null ? (int?)b.Index : null, native_group = b.Batch != null ? (int?)b.Batch.groupIndex : null }).ToArray();
                report["baseline_materials"] = privateBefore.Values.ToArray();
                report["isolated_source_prototypes"] = sourceBefore.Values.ToArray();
                report["registered_sources_before"] = registeredBefore.Values.ToArray();
                var originalExposure = await ReadExposure();
                report["exposure_before_lock"] = originalExposure.Evidence();
                exposure.Activate(camera, originalExposure.Ev100);
                foreach (string series in new[] { "thickness", materials.HasNeutralMasks ? "neutral-mask" : "smoothness" })
                {
                    foreach (string stage in new[] { "baseline", "candidate", "baseline-restored" })
                    {
                        check(); materials.Restore();
                        foreach (BusGlassOpticsBinding binding in bindings) binding.RestoreDefaults();
                        var nativeWrites = new List<object>();
                        if (stage == "candidate")
                        {
                            if (series == "thickness")
                            {
                                materials.SetThicknessHalf();
                                foreach (BusGlassOpticsBinding binding in bindings)
                                    nativeWrites.Add(binding.WriteFloatDefault("_Thickness", binding.Material.GetFloat("_Thickness")));
                            }
                            else if (series == "neutral-mask") materials.SetNeutralMask230();
                            else
                            {
                                materials.SetSmoothness95();
                                foreach (BusGlassOpticsBinding binding in bindings)
                                    nativeWrites.Add(binding.WriteFloatDefault("_Smoothness", binding.Material.GetFloat("_Smoothness")));
                            }
                        }
                        HDCamera.GetOrCreate(camera).Reset();
                        await settle();
                        CheckView(); VerifySources(series, stage);
                        var gpu = await ReadExposure();
                        var hd = HDCamera.GetOrCreate(camera); var resolved = hd.volumeStack.GetComponent<Exposure>();
                        Require(resolved.mode.value == ExposureMode.Fixed && Math.Abs(resolved.fixedExposure.value - originalExposure.Ev100) < .001f,
                            "Optics fixed exposure did not reach the active camera");
                        if (!lockedExposure.HasValue) lockedExposure = gpu;
                        Require(Math.Abs(gpu.Ev100 - lockedExposure.Value.Ev100) < .005f &&
                            Math.Abs(gpu.Multiplier / lockedExposure.Value.Multiplier - 1f) < .002f,
                            "Actual GPU exposure changed within an optics comparison");
                        Require(hd.frameSettings.IsEnabled(FrameSettingsField.Refraction) && hd.frameSettings.IsEnabled(FrameSettingsField.SkyReflection) &&
                            hd.frameSettings.IsEnabled(FrameSettingsField.DirectSpecularLighting), "Optics requires complete refraction and reflection lighting");
                        string filename = "glass-optics-" + hour + "-" + mode + "-" + series + "-" + stage + ".png";
                        await capture(filename);
                        CheckView(); VerifySources(series, stage);
                        var pixels = await OpticsImageEvidence(Path.Combine(directory, filename), check);
                        readable &= pixels.usable;
                        rows.Add(new { series, stage, filename, actual_exposure = gpu.Evidence(),
                            exposure_mode = resolved.mode.value.ToString(), fixed_ev100 = resolved.fixedExposure.value,
                            refraction = hd.frameSettings.IsEnabled(FrameSettingsField.Refraction),
                            sky_reflection = hd.frameSettings.IsEnabled(FrameSettingsField.SkyReflection),
                            direct_specular = hd.frameSettings.IsEnabled(FrameSettingsField.DirectSpecularLighting),
                            light_layers = hd.frameSettings.IsEnabled(FrameSettingsField.LightLayers),
                            reflection_probe = hd.frameSettings.IsEnabled(FrameSettingsField.ReflectionProbe),
                            native_default_writes = nativeWrites, materials = privateBefore.Keys.Select(OpticsMaterial).ToArray(), scene = sceneEvidence(),
                            camera_position = A(camera.transform.position), camera_rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w }, fov,
                            image = pixels.evidence });
                    }
                }
                report["isolated_source_prototypes_unchanged"] = true;
                report["registered_sources_after"] = registeredBefore.Keys.Select(RegisteredOpticsSurface).ToArray();
                report["status"] = readable ? "CAPTURED_REQUIRES_IMAGE_COMPARISON" : "CAPTURED_WITH_UNREADABLE_IMAGES";
                return readable;
            }
            catch (Exception error) { failure = error; report["status"] = "FAIL"; report["error"] = error.ToString(); throw; }
            finally
            {
                if (materials != null) Try(materials.Dispose);
                foreach (BusGlassOpticsBinding binding in bindings) Try(binding.Dispose);
                Try(exposure.Dispose);
                foreach (NativeMaterialLease source in sources.Values) Try(source.Dispose);
                report["cleanup"] = new { passed = cleanup.Count == 0, errors = cleanup.Select(e => e.ToString()).ToArray() };
                if (cleanup.Count > 0)
                {
                    if (failure != null) cleanup.Insert(0, failure);
                    throw new AggregateException("Optics comparison cleanup failed", cleanup);
                }
            }

            void Try(Action action) { try { action(); } catch (Exception error) { cleanup.Add(error); } }
            Task<BusGlassOpticsExposure.GpuExposure> ReadExposure()
            {
                var evidence = new Dictionary<string, object>(); exposureReads.Add(evidence);
                return BusGlassOpticsExposure.Read(camera, check, evidence);
            }
            void CheckView()
            {
                check();
                Require(Vector3.Distance(position, camera.transform.position) < .002f && Quaternion.Angle(rotation, camera.transform.rotation) < .01f &&
                    Math.Abs(fov - camera.fieldOfView) < .001f, "Optics camera moved during comparison");
            }
            void VerifySources(string series, string stage)
            {
                foreach (BusGlassOpticsBinding binding in bindings)
                {
                    binding.Validate();
                    Require(JToken.DeepEquals(customBefore[binding], OpticsPropertyBlock(binding)), "Optics changed native custom properties");
                }
                foreach (var source in sources)
                    Require(JToken.DeepEquals(sourceBefore[source.Key], OpticsMaterial(source.Value.Material)), "Optics changed an isolated native prototype");
                foreach (var source in registeredBefore)
                {
                    JObject current = RegisteredOpticsSurface(source.Key);
                    if (source.Value.Value<bool>("observable") && current.Value<bool>("observable"))
                        Require(JToken.DeepEquals(source.Value, current), "Optics changed the loaded registered source dictionaries");
                }
                foreach (var material in privateBefore)
                {
                    JObject before = (JObject)material.Value.DeepClone(), current = OpticsMaterial(material.Key);
                    if (stage == "candidate")
                    {
                        string allowed = series == "thickness" ? "_Thickness" : series == "smoothness" ? "_Smoothness" : "_MaskMap";
                        ((JObject)before["properties"]).Remove(allowed); ((JObject)current["properties"]).Remove(allowed);
                    }
                    Require(JToken.DeepEquals(before, current), "Optics changed properties outside its single test parameter");
                }
            }
        }

        static Task<Color32> ReadOpticsMask(Texture texture, Action check, IDictionary<string, object> evidence)
        {
            return BusGlassOpticsReadback.Read(texture, GraphicsFormat.R8G8B8A8_UNorm, readback =>
            {
                var pixels = readback.GetData<Color32>(); Require(pixels.Length == 1, "Optics mask is not a single texel");
                return pixels[0];
            }, check, evidence);
        }

        static async Task<(bool usable, object evidence)> OpticsImageEvidence(string path, Action check)
        {
            byte[] bytes = null;
            long previous = -1;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (bytes == null)
            {
                check(); Require(watch.Elapsed.TotalSeconds < 5, "Optics screenshot did not finish writing");
                try
                {
                    long length = new FileInfo(path).Length;
                    if (length > 0 && length == previous) bytes = File.ReadAllBytes(path);
                    previous = length;
                }
                catch (IOException) { }
                if (bytes == null) await Task.Delay(50);
            }
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Exception failure = null;
            try
            {
                Require(image.LoadImage(bytes, false), "Optics screenshot is not a complete PNG");
                Color32[] pixels = image.GetPixels32();
                long sum = 0, squared = 0; int black = 0, white = 0, low = 255, high = 0;
                foreach (Color32 pixel in pixels)
                {
                    int value = (pixel.r * 54 + pixel.g * 183 + pixel.b * 19) >> 8;
                    sum += value; squared += value * value; low = Math.Min(low, value); high = Math.Max(high, value);
                    if (value <= 2) black++; if (value >= 253) white++;
                }
                double mean = sum / (double)pixels.Length, variance = squared / (double)pixels.Length - mean * mean;
                bool usable = high - low >= 12 && variance >= 6 && black < pixels.Length * .98 && white < pixels.Length * .98;
                return (usable, new { status = usable ? "MANUAL_REVIEW_REQUIRED" : "UNREADABLE_FOR_OPTICS",
                    image.width, image.height, mean_luminance_8bit = mean, luminance_variance = variance,
                    black_clipped_fraction = black / (double)pixels.Length, white_clipped_fraction = white / (double)pixels.Length,
                    scope = "Whole-image clipping/contrast only; visibility of individual windows requires manual review" });
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try { UnityEngine.Object.Destroy(image); }
                catch (Exception cleanup)
                {
                    Interior.CabCleanup.Retain(image);
                    if (failure != null) throw new AggregateException("Optics image read and cleanup failed", failure, cleanup);
                    throw;
                }
            }
        }

        static JObject OpticsMaterial(Material material)
        {
            var properties = new JObject(); Shader shader = material.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i); ShaderPropertyType kind = shader.GetPropertyType(i);
                object value;
                if (kind == ShaderPropertyType.Texture) value = OpticsTexture(material.GetTexture(name));
                else if (kind == ShaderPropertyType.Color || kind == ShaderPropertyType.Vector)
                { Vector4 v = material.GetVector(name); value = new[] { v.x, v.y, v.z, v.w }; }
                else if (kind == ShaderPropertyType.Int) value = material.GetInt(name);
                else value = material.GetFloat(name);
                properties[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            return JObject.FromObject(new { id = material.GetInstanceID(), material.name, shader = shader.name, shader_id = shader.GetInstanceID(),
                material.renderQueue, material.enableInstancing, keywords = material.shaderKeywords.OrderBy(x => x, StringComparer.Ordinal).ToArray(), properties,
                passes = Enumerable.Range(0, material.passCount).Select(i => new { name = material.GetPassName(i),
                    enabled = material.GetShaderPassEnabled(material.GetPassName(i)) }).ToArray() });
        }

        static object OpticsTexture(Texture texture) => texture == null ? null : new { id = texture.GetInstanceID(), texture.name,
            texture.width, texture.height, format = texture.graphicsFormat.ToString(), filter = texture.filterMode.ToString(), wrap = texture.wrapMode.ToString() };

        static JObject OpticsPropertyBlock(BusGlassOpticsBinding binding)
        {
            if (binding.Batch == null) return new JObject();
            var block = binding.Batch.customProps; var values = new JObject();
            if (block != null)
                for (int i = 0; i < binding.Material.shader.GetPropertyCount(); i++)
                {
                    string name = binding.Material.shader.GetPropertyName(i); int id = Shader.PropertyToID(name);
                    if (!block.HasProperty(id)) continue;
                    ShaderPropertyType kind = binding.Material.shader.GetPropertyType(i);
                    if (kind == ShaderPropertyType.Texture) values[name] = JToken.FromObject(OpticsTexture(block.GetTexture(id)) ?? (object)"null");
                    else if (kind == ShaderPropertyType.Color || kind == ShaderPropertyType.Vector)
                    { Vector4 v = block.GetVector(id); values[name] = JToken.FromObject(new[] { v.x, v.y, v.z, v.w }); }
                    else if (kind == ShaderPropertyType.Int) values[name] = block.GetInt(id);
                    else values[name] = block.GetFloat(id);
                }
            return JObject.FromObject(new { is_null = block == null, empty = block == null || block.isEmpty, declared_shader_properties = values });
        }

        static JObject RegisteredOpticsSurface(SurfaceAsset surface)
        {
            bool observable = surface.floats != null && surface.ints != null && surface.vectors != null &&
                surface.colors != null && surface.textures != null && surface.keywords != null;
            if (!observable) return JObject.FromObject(new { surface = surface.name, id = surface.id.ToString(), observable = false,
                reason = "Registered Surface dictionaries are not loaded; not loaded by this diagnostic merely to claim observation" });
            var vectors = surface.vectors.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key,
                p => new[] { p.Value.x, p.Value.y, p.Value.z, p.Value.w });
            var colors = surface.colors.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key,
                p => new[] { p.Value.r, p.Value.g, p.Value.b, p.Value.a });
            var textures = surface.textures.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key,
                p => p.Value == null ? null : p.Value.id.ToString());
            return JObject.FromObject(new { surface = surface.name, id = surface.id.ToString(), observable = true,
                floats = surface.floats.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
                ints = surface.ints.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value), vectors, colors, textures,
                keywords = surface.keywords.OrderBy(k => k, StringComparer.Ordinal).ToArray() });
        }
    }
}
#endif
