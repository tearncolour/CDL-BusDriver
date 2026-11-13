using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using Game.Prefabs;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static class BusVehicleReviewSample
    {
        sealed class NativeSample : IDisposable
        {
            internal GameObject Root;
            internal BusNativeMeshLease Meshes;

            internal NativeSample(RenderPrefab prefab, Vector3 origin)
            {
                try
                {
                    Meshes = new BusNativeMeshLease(prefab);
                    Root = new GameObject("Native LOD0 review sample");
                    Root.hideFlags = HideFlags.DontSave;
                    Root.transform.position = origin;
                }
                catch (Exception failure)
                {
                    try { Dispose(); }
                    catch (Exception cleanup) { throw new AggregateException("Native sample construction and cleanup failed.", failure, cleanup); }
                    throw;
                }
            }

            public void Dispose()
            {
                var failures = new List<Exception>();
                bool detached = true;
                if (Root != null)
                {
                    detached &= CabCleanup.Try(() => Root.SetActive(false), failures);
                    try
                    {
                        foreach (MeshRenderer renderer in Root.GetComponentsInChildren<MeshRenderer>(true))
                            detached &= CabCleanup.Try(() => renderer.enabled = false, failures);
                        foreach (MeshFilter filter in Root.GetComponentsInChildren<MeshFilter>(true))
                            detached &= CabCleanup.Try(() => filter.sharedMesh = null, failures);
                    }
                    catch (Exception error) { detached = false; failures.Add(error); }
                    if (detached && CabCleanup.Try(() => UnityEngine.Object.Destroy(Root), failures)) Root = null;
                }
                if (detached && Meshes != null && CabCleanup.Try(Meshes.Dispose, failures)) Meshes = null;
                if (failures.Count != 0)
                {
                    CabCleanup.Retain(this);
                    throw new AggregateException("Native review sample cleanup failed.", failures);
                }
            }
        }

        internal static async Task<List<object>> Run(PrefabBase prefab, Camera camera, RenderTexture target, string directory,
            string name, Vector3 origin, Transform runtime, List<HDAdditionalLightData> lights, List<object> shots, bool exteriorOnly = false, bool mipBiasDiagnostic = false)
        {
            var result = new List<object>(); var hd = camera.GetComponent<HDAdditionalCameraData>();
            var aa = hd.antialiasing; bool history = hd.hasPersistentHistory;
            var intensities = lights.Select(light => light.intensity).ToArray(); var units = lights.Select(light => light.lightUnit).ToArray();
            var added = new List<HDAdditionalLightData>(); GameObject native = null;
            NativeSample nativeOwner = null;
            Exception failure = null;
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing; hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(intensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                Add("front inspection key", LightType.Directional, new Vector3(0f, 10f, 10f), 10000f, LightUnit.Lux);
                foreach (float z in new[] { -4f, -2f, 0f, 2f, 4f }) Add("interior inspection " + z, LightType.Point, new Vector3(0f, 1.65f, z), 16000f, LightUnit.Lumen);
                CheckGlass(runtime.gameObject);
                await Capture("runtime-outside", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), exteriorOnly ? 35f : 49f, 13f, runtime.gameObject);
                if (!exteriorOnly) {
                await Capture("runtime-handrail", new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, 12f, runtime.gameObject);
                await Capture("runtime-rear-seat-access", new Vector3(-.20f, 1.60f, .50f), new Vector3(.74f, 1.05f, -1.42f), 70f, 12f, runtime.gameObject);
                await Capture("runtime-front-door-inside", new Vector3(-.35f, 1.80f, 4.10f), new Vector3(1.10f, 1.42f, 4.15f), 86f, 12f, runtime.gameObject);
                await Capture("runtime-middle-door-inside", new Vector3(-.10f, 1.60f, 1.00f), new Vector3(1.12f, 1.40f, -.30f), 78f, 12f, runtime.gameObject);
                }
                runtime.gameObject.SetActive(false);
                var render = (RenderPrefab)((ObjectGeometryPrefab)prefab).m_Meshes[0].m_Mesh;
                nativeOwner = new NativeSample(render, origin);
                native = nativeOwner.Root;
                var groups = new Dictionary<Mesh, Material[]>();
                for (int i = 0; i < render.materialCount; i++)
                {
                    var mesh = nativeOwner.Meshes.Get(i, out int sub);
                    if (!groups.TryGetValue(mesh, out var slots)) groups[mesh] = slots = new Material[mesh.subMeshCount];
                    if (slots[sub] != null) throw new InvalidOperationException("Duplicate native sample submesh");
                    slots[sub] = render.ObtainMaterial(i, false);
                }
                foreach (var group in groups)
                {
                    if (group.Value.Any(material => material == null)) throw new InvalidOperationException("Missing native sample material");
                    var child = new GameObject(group.Key.name) { hideFlags = HideFlags.DontSave }; child.transform.SetParent(native.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = group.Key; var renderer = child.AddComponent<MeshRenderer>(); renderer.sharedMaterials = group.Value;
                    renderer.shadowCastingMode = ShadowCastingMode.On; renderer.receiveShadows = true;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes; renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                }
                CheckGlass(native);
                await Capture("native-outside", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), exteriorOnly ? 35f : 49f, 13f, native);
                if (mipBiasDiagnostic)
                {
                    using (var bias = new NativePaletteBias(native, name))
                    {
                        bias.SetBias(-10f);
                        await Capture("native-outside-original-texture-mip-bias", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), 35f, 13f, native);
                        bias.VerifyBias(-10f);
                        result.Add(new { diagnostic = "Only model-specific original texture objects' GPU sampling mipMapBias is changed in memory, then restored. No texture pixels, material, importer or package changed.", textures = bias.Evidence });
                    }
                }
                if (exteriorOnly && !mipBiasDiagnostic)
                {
                    using (var emission = new BusNativeEmissionControl(native, name))
                    {
                        await Capture("native-outside-black-emissive-control", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), 35f, 13f, native);
                        result.Add(new { diagnostic = "Private body emissive map replaced by GPU-verified black solely for attribution; restored afterwards", emission = emission.Evidence });
                    }
                    native.SetActive(false);
                    using (var official = new BusNativeRendererControl(render, origin))
                    {
                        await official.WaitForUpdate();
                        object beforeCapture = official.Evidence();
                        await Capture("native-outside-game-renderer", new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), 35f, 13f, official.Root);
                        result.Add(new { diagnostic = "Actual game RenderPrefabRenderer, including its procedural lighting buffers", before = beforeCapture, after = official.Evidence() });
                    }
                }
                if (!exteriorOnly) await Capture("native-saloon", new Vector3(0f, 1.9f, 3.25f), new Vector3(0f, 1.85f, -4.25f), 86f, 12f, native);

                void Add(string label, LightType type, Vector3 position, float intensity, LightUnit unit)
                {
                    var obj = new GameObject("CDLC " + label) { hideFlags = HideFlags.DontSave };
                    obj.transform.position = origin + position; obj.transform.rotation = Quaternion.Euler(48f, 205f, 0f);
                    var light = obj.AddComponent<Light>(); light.type = type; light.range = 5.5f; light.shadows = LightShadows.None;
                    var data = obj.AddComponent<HDAdditionalLightData>(); data.SetIntensity(intensity, unit); added.Add(data);
                }
                async Task Capture(string view, Vector3 eye, Vector3 look, float fov, float ev, GameObject visible)
                {
                    object immediate = null;
                    using (var exposure = new BusVehicleMaterialDiagnostics.PrivateExposure(camera, origin, ev))
                        await BusVehicleProbe.Capture(camera, target, directory, name, 0, "review-sample-" + view, eye, look, fov, shots, 64, true,
                            () => immediate = new { fixed_ev = HDCamera.GetOrCreate(camera).volumeStack.GetComponent<Exposure>().fixedExposure.value,
                                frame_prepass = HDCamera.GetOrCreate(camera).frameSettings.IsEnabled(FrameSettingsField.TransparentPrepass),
                                glass = CheckGlass(visible), environment = BusNativeVehicleProbe.EnvironmentEvidence() });
                    result.Add(new { group = view, scope = view.Contains("black-emissive") ? "Attribution only: private native body material copies use a black emissive texture; geometry and other materials unchanged."
                            : "Complete original materials/geometry under explicit inspection lights; not road-scene lighting.", immediate,
                        procedural_lighting = BusNativeRendererControl.PropertyEvidence(visible),
                        lights = lights.Concat(added).Select(light => new { light.name, intensity = light.intensity, unit = light.lightUnit.ToString(),
                            type = light.type.ToString(), position = V(light.transform.position - origin) }).ToArray() });
                }
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                var cleanup = new List<Exception>();
                if (nativeOwner != null) CabCleanup.Try(nativeOwner.Dispose, cleanup);
                CabCleanup.Try(() => runtime.gameObject.SetActive(true), cleanup);
                foreach (var light in added)
                {
                    CabCleanup.Try(() => light.gameObject.SetActive(false), cleanup);
                    CabCleanup.DestroyOrRetain(light.gameObject, cleanup);
                }
                CabCleanup.Try(() => { hd.antialiasing = aa; hd.hasPersistentHistory = history; }, cleanup);
                for (int i = 0; i < lights.Count; i++)
                {
                    int at = i;
                    CabCleanup.Try(() => lights[at].SetIntensity(intensities[at], units[at]), cleanup);
                }
                if (cleanup.Count != 0)
                {
                    if (failure != null) cleanup.Insert(0, failure);
                    throw new AggregateException("Vehicle review sample cleanup failed.", cleanup);
                }
            }
            return result;
        }
        static object CheckGlass(GameObject root)
        {
            var materials = root.GetComponentsInChildren<MeshRenderer>(true).SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null && material.shader.name == "BH/GlsShader").Distinct().ToArray();
            if (materials.Length == 0) throw new InvalidOperationException("No original glass in review sample");
            foreach (var material in materials)
                if (material.GetFloat("_ReceivesSSRTransparent") != 0f || material.GetFloat("_TransparentDepthPrepassEnable") != 0f || material.GetShaderPassEnabled("TransparentDepthPrepass"))
                    throw new InvalidOperationException("Review sample must use the new persisted glass compatibility contract");
            return materials.Select(material => new { material.name, shader = material.shader.name, coat = material.GetFloat("_CoatMask"),
                transparent_ssr = material.GetFloat("_ReceivesSSRTransparent"), prepass_enabled = material.GetShaderPassEnabled("TransparentDepthPrepass"),
                keywords = material.shaderKeywords }).ToArray();
        }
        static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };

        sealed class NativePaletteBias : IDisposable
        {
            readonly Dictionary<Texture2D, float> originals = new Dictionary<Texture2D, float>();
            internal readonly List<object> Evidence = new List<object>();
            internal NativePaletteBias(GameObject root, string model)
            {
                try
                {
                    foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
                    {
                        foreach (var material in renderer.sharedMaterials)
                        {
                            if (material.shader.name == "BH/GlsShader") continue;
                            foreach (string property in material.GetTexturePropertyNames())
                            {
                                if (!(material.GetTexture(property) is Texture2D texture) || texture.width != 64 || texture.height != 64) continue;
                                if (!texture.name.StartsWith(model + "_", StringComparison.Ordinal))
                                    throw new InvalidOperationException("Refusing to modify sampling on a shared/default texture");
                                if (!originals.ContainsKey(texture)) originals.Add(texture, texture.mipMapBias);
                                Evidence.Add(new { property, source = texture.name, texture.width, texture.height, texture.mipmapCount,
                                    texture_instance_id = texture.GetInstanceID(), original_sampling_bias = texture.mipMapBias });
                            }
                        }
                    }
                    if (Evidence.Count == 0) throw new InvalidOperationException("No native 64-pixel body palette textures for diagnostic");
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                foreach (var entry in originals)
                {
                    entry.Key.mipMapBias = entry.Value;
                    Evidence.Add(new { texture_instance_id = entry.Key.GetInstanceID(), restored_sampling_bias = entry.Key.mipMapBias,
                        restored = entry.Key.mipMapBias == entry.Value });
                }
                originals.Clear();
            }
            internal void SetBias(float bias)
            {
                foreach (Texture2D texture in originals.Keys)
                {
                    texture.mipMapBias = bias;
                    if (texture.mipMapBias != bias) throw new InvalidOperationException("Requested texture sampling bias did not take effect");
                    Evidence.Add(new { texture = texture.name, requested_sampling_bias = bias, actual_sampling_bias = texture.mipMapBias });
                }
            }
            internal void VerifyBias(float expected)
            {
                foreach (var texture in originals.Keys)
                {
                    Evidence.Add(new { texture_instance_id = texture.GetInstanceID(), sampling_bias_after_capture = texture.mipMapBias });
                    if (texture.mipMapBias != expected) throw new InvalidOperationException("Texture sampling bias changed during capture");
                }
            }
        }
    }
}
