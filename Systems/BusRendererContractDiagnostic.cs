using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Game.Prefabs;
using Game.Rendering.Debug;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static class BusRendererContractDiagnostic
    {
        internal static async Task<List<object>> Run(PrefabBase prefab, Camera camera, RenderTexture target, string directory,
            string name, Vector3 origin, Transform runtime, List<HDAdditionalLightData> lights, List<object> shots)
        {
            var result = new List<object>();
            var render = (RenderPrefab)((ObjectGeometryPrefab)prefab).m_Meshes[0].m_Mesh;
            var hd = camera.GetComponent<HDAdditionalCameraData>();
            var aa = hd.antialiasing; bool history = hd.hasPersistentHistory;
            var intensities = lights.Select(light => light.intensity).ToArray();
            var units = lights.Select(light => light.lightUnit).ToArray();
            var snapshots = new Dictionary<Material, Material>();
            var passStates = new Dictionary<Material, Dictionary<string, bool>>();
            GameObject plain = null, official = null;
            using (var exposure = new BusVehicleMaterialDiagnostics.PrivateExposure(camera, origin, 13f))
            try
            {
                hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                hd.hasPersistentHistory = true;
                for (int i = 0; i < lights.Count; i++)
                    lights[i].SetIntensity(intensities[i], lights[i].GetComponent<Light>().type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen);
                await Capture("runtime", runtime.gameObject, null);
                runtime.gameObject.SetActive(false);

                plain = new GameObject("CDLC native plain comparison") { hideFlags = HideFlags.DontSave };
                plain.transform.position = origin;
                var grouped = new Dictionary<Mesh, Material[]>();
                for (int i = 0; i < render.materialCount; i++)
                {
                    var mesh = render.ObtainMesh(i, out int sub);
                    if (!grouped.TryGetValue(mesh, out var materials)) grouped[mesh] = materials = new Material[mesh.subMeshCount];
                    if (materials[sub] != null) throw new InvalidOperationException("Duplicate native comparison submesh");
                    materials[sub] = render.ObtainMaterial(i, false);
                }
                foreach (var pair in grouped)
                {
                    if (pair.Value.Any(material => material == null)) throw new InvalidOperationException("Missing native comparison material");
                    var child = new GameObject(pair.Key.name) { hideFlags = HideFlags.DontSave };
                    child.transform.SetParent(plain.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = pair.Key;
                    child.AddComponent<MeshRenderer>().sharedMaterials = pair.Value;
                }
                await Capture("native-plain", plain, null);
                plain.SetActive(false);

                // The game component changes cached keywords/pass flags. Save them before its OnEnable,
                // run it last, then restore every imported material instead of polluting another test.
                var levels = new List<RenderPrefab> { render };
                if (render.TryGet(out LodProperties lods) && lods.m_LodMeshes != null) levels.AddRange(lods.m_LodMeshes);
                foreach (var level in levels)
                    for (int i = 0; i < level.materialCount; i++)
                    {
                        var material = level.ObtainMaterial(i, false);
                        if (snapshots.ContainsKey(material)) continue;
                        snapshots[material] = new Material(material) { hideFlags = HideFlags.DontSave };
                        var names = Enumerable.Range(0, material.passCount).Select(material.GetPassName).Concat(new[] { "MOTIONVECTORS" }).Distinct();
                        passStates[material] = names.ToDictionary(pass => pass, material.GetShaderPassEnabled);
                    }
                official = new GameObject("CDLC real RenderPrefabRenderer comparison") { hideFlags = HideFlags.DontSave };
                official.SetActive(false); official.transform.position = origin;
                var component = official.AddComponent<RenderPrefabRenderer>();
                component.m_Prefab = render; component.m_NoVT = true; component.m_Animate = false;
                component.m_LODIndex = 0; component.m_AnimationIndex = -1; component.m_ColorIndex = -1;
                component.m_EmissiveLight = -1; component.m_WindowsLight = 0;
                official.SetActive(true);
                int frame = Time.frameCount;
                for (int attempt = 0; attempt < 100 && Time.frameCount < frame + 2; attempt++) await Task.Delay(20);
                if (Time.frameCount < frame + 2 || component.GetActiveRoot() == null || component.hierarchies.Count(h => h.enabled) != 1)
                    throw new InvalidOperationException("Game RenderPrefabRenderer did not complete its real Update/LOD selection");
                await Capture("game-render-prefab-renderer", official, new { component.m_NoVT, component.m_Animate,
                    component.m_LODIndex, component.m_ColorIndex, component.m_EmissiveLight, component.m_WindowsLight,
                    active_root = component.GetActiveRoot().name, hierarchy_count = component.hierarchies.Count,
                    update_frames_elapsed = Time.frameCount - frame,
                    global_buffer_helper = "Game-owned GlobalBufferRenderer singleton is retained; this component is disabled/disposed and the game's own global buffers are rebound." });
            }
            finally
            {
                if (official != null) { official.SetActive(false); BusNativeRendererControl.RestoreGameGlobalBuffers(); UnityEngine.Object.Destroy(official); }
                if (plain != null) { plain.SetActive(false); UnityEngine.Object.Destroy(plain); }
                foreach (var pair in snapshots)
                {
                    pair.Key.CopyPropertiesFromMaterial(pair.Value);
                    pair.Key.shaderKeywords = pair.Value.shaderKeywords;
                    pair.Key.renderQueue = pair.Value.renderQueue;
                    pair.Key.enableInstancing = pair.Value.enableInstancing;
                    foreach (var pass in passStates[pair.Key]) pair.Key.SetShaderPassEnabled(pass.Key, pass.Value);
                    UnityEngine.Object.Destroy(pair.Value);
                }
                runtime.gameObject.SetActive(true);
                hd.antialiasing = aa; hd.hasPersistentHistory = history;
                for (int i = 0; i < lights.Count; i++) lights[i].SetIntensity(intensities[i], units[i]);
            }
            return result;

            async Task Capture(string group, GameObject holder, object settings)
            {
                object immediate = null;
                var temporalFrames = new List<object>();
                await BusVehicleProbe.Capture(camera, target, directory, name, 0, "contract-" + group + "-handrail-material",
                    new Vector3(-.15f, 1.65f, 1.65f), new Vector3(.45f, 2.15f, .75f), 62f, shots, 64, true, () =>
                    {
                        var hdcam = HDCamera.GetOrCreate(camera);
                        var resolved = hdcam.volumeStack.GetComponent<Exposure>();
                        if (resolved.mode.value != ExposureMode.Fixed || Mathf.Abs(resolved.fixedExposure.value - 13f) > .001f)
                            throw new InvalidOperationException("Renderer comparison fixed exposure did not resolve");
                        immediate = new { fixed_ev100 = resolved.fixedExposure.value,
                            antialiasing = hdcam.frameSettings.IsEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.Antialiasing),
                            environment = BusNativeVehicleProbe.EnvironmentEvidence() };
                    }, frameIndex =>
                    {
                        if (frameIndex < 56) return;
                        var hdcam = HDCamera.GetOrCreate(camera);
                        var taaField = typeof(HDCamera).GetField("taaFrameIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        var resetField = typeof(HDCamera).GetField("didResetPostProcessingHistoryInLastFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var aaProperty = typeof(HDCamera).GetProperty("antialiasing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        Vector4 taaInfo = Shader.GetGlobalVector("_TaaFrameInfo");
                        temporalFrames.Add(new { render_call = frameIndex, unity_frame = Time.frameCount,
                            taa_frame_index = taaField?.GetValue(hdcam), taa_info = new[] { taaInfo.x, taaInfo.y, taaInfo.z, taaInfo.w },
                            taa_jitter = new[] { hdcam.taaJitter.x, hdcam.taaJitter.y, hdcam.taaJitter.z, hdcam.taaJitter.w },
                            hdcam.colorPyramidHistoryIsValid, did_reset_postprocess_history = resetField?.GetValue(hdcam),
                            resolved_aa_mode = aaProperty?.GetValue(hdcam)?.ToString(), camera_type = camera.cameraType.ToString(),
                            hd_projection_matrix = Enumerable.Range(0, 16).Select(i => hdcam.mainViewConstants.projMatrix[i]).ToArray(),
                            projection_matrix = Enumerable.Range(0, 16).Select(i => camera.projectionMatrix[i]).ToArray(),
                            nonjittered_projection_matrix = Enumerable.Range(0, 16).Select(i => camera.nonJitteredProjectionMatrix[i]).ToArray() });
                    });
                result.Add(new { group, scope = "Same camera/lights/exposure; actual game component vs ordinary imported renderer vs runtime cab.", settings, immediate,
                    temporal_frames = temporalFrames,
                    renderers = holder.GetComponentsInChildren<MeshRenderer>().Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy)
                    .Select(renderer => new { renderer.name, renderer_id = renderer.GetInstanceID(),
                        shadow_casting = renderer.shadowCastingMode.ToString(), renderer.receiveShadows,
                        motion_vectors = renderer.motionVectorGenerationMode.ToString(), property_block = BusVehicleMaterialDiagnostics.PropertyBlockEvidence(renderer),
                        materials = renderer.sharedMaterials.Select(material => new { instance_id = material.GetInstanceID(),
                            shader = BusVehicleMaterialDiagnostics.ShaderEvidence(material),
                            pass_enabled = Enumerable.Range(0, material.passCount).Select(i => new { name = material.GetPassName(i), enabled = material.GetShaderPassEnabled(material.GetPassName(i)) }).ToArray(),
                            uppercase_motion_vectors_enabled = material.GetShaderPassEnabled("MOTIONVECTORS") }).ToArray() }).ToArray() });
            }
        }
    }
}
