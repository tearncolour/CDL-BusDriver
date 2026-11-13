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
    /// <summary>Static imported geometry and original BH materials, separate from the SWB1 runtime cab.</summary>
    internal static class BusNativeVehicleProbe
    {
        internal static async Task Run(PrefabBase prefab, string name, Camera camera, RenderTexture target,
            string directory, Vector3 origin, List<object> shots, List<object> reports)
        {
            if (!(prefab is ObjectGeometryPrefab geometry) || geometry.m_Meshes == null || geometry.m_Meshes.Length == 0 ||
                !(geometry.m_Meshes[0].m_Mesh is RenderPrefab root))
                throw new InvalidOperationException("Native probe has no imported vehicle geometry");
            var levels = new List<RenderPrefab> { root };
            if (root.TryGet(out LodProperties properties) && properties.m_LodMeshes != null)
                levels.AddRange(properties.m_LodMeshes);
            Require(levels.Count == 3 && levels.All(level => level != null), "Native probe requires three imported LODs");
            for (int lod = 0; lod < levels.Count; lod++)
            {
                RenderPrefab level = levels[lod];
                BusNativeRendererControl native = null;
                BusNativeMeshLease meshes = null;
                Exception failure = null;
                var materialRows = new List<object>();
                var levelReport = new Dictionary<string, object>
                {
                    ["name"] = name, ["lod"] = lod, ["render_prefab"] = level.name,
                    ["scope"] = "Actual game RenderPrefabRenderer with native geometry, original ObtainMaterial(false) BH materials and its procedural light/animation buffers. Menu inspection lighting; not an AI vehicle, road scene, VT renderer, or animated native door test.",
                    ["materials"] = materialRows,
                };
                reports.Add(levelReport);
                try
                {
                    native = new BusNativeRendererControl(root, origin, lod);
                    await native.WaitForUpdate();
                    meshes = new BusNativeMeshLease(level);
                    var grouped = new Dictionary<Mesh, Material[]>();
                    long triangles = 0; int glass = 0;
                    for (int surfaceIndex = 0; surfaceIndex < level.materialCount; surfaceIndex++)
                    {
                        Mesh mesh = meshes.Get(surfaceIndex, out int sub);
                        Material material = level.ObtainMaterial(surfaceIndex, false);
                        Require(mesh != null && sub >= 0 && sub < mesh.subMeshCount, "Native mesh/submesh missing");
                        Require(material != null && material.shader != null && material.shader.name.StartsWith("BH/", StringComparison.Ordinal),
                            "Native material is missing or did not load a BH shader");
                        Require(material.HasProperty("_BaseColorMap") && material.GetTexture("_BaseColorMap") != null,
                            "Native material base-colour texture is missing");
                        Require(material.HasProperty("_MaskMap") && material.GetTexture("_MaskMap") != null,
                            "Native material mask texture is missing");
                        if (!grouped.TryGetValue(mesh, out Material[] slots)) grouped[mesh] = slots = new Material[mesh.subMeshCount];
                        Require(slots[sub] == null, "Native surface is mapped to a duplicate submesh");
                        slots[sub] = material;
                        long count = mesh.GetIndexCount(sub) / 3; triangles += count;
                        var surface = level.GetSurfaceAsset(surfaceIndex);
                        string surfaceName = surface?.name ?? "missing";
                        if (surfaceName.IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            glass++;
                            Require(material.shader.name == "BH/GlsShader", "Native glass has the wrong original shader");
                            if (BusVehicleAssetSystem.HasSharedGlass(name))
                            {
                                Require(material.HasProperty("_CoatMask") && Mathf.Abs(material.GetFloat("_CoatMask") - BusVehicleAssetSystem.ModelNamed(name).GlassCoatMask) < .0001f,
                                    "Native glass did not preserve the model-specific independent coat property");
                                Require(material.GetFloat("_ReceivesSSRTransparent") == 0f && material.GetFloat("_TransparentDepthPrepassEnable") == 0f &&
                                    !material.GetShaderPassEnabled("TransparentDepthPrepass"), "Native glass lost its persisted transparent-depth compatibility settings");
                            }
                        }
                        materialRows.Add(new
                        {
                            surface_index = surfaceIndex, surface_name = surfaceName, mesh = mesh.name, submesh = sub,
                            vertices = mesh.vertexCount, triangles = count, material = material.name,
                            shader = material.shader.name, keywords = material.shaderKeywords, queue = material.renderQueue,
                            transparent_depth_prepass = material.GetShaderPassEnabled("TransparentDepthPrepass"),
                            textures = TextureBindings(material), scalar_properties = ScalarProperties(material),
                            base_colour_property = Colour(material, "_BaseColor"),
                        });
                    }
                    Require(glass == 1 && triangles > 0, "Native LOD requires one glass surface and nonempty geometry");
                    foreach (var pair in grouped)
                    {
                        Require(pair.Value.All(value => value != null), "Native mesh has an unassigned submesh slot");
                    }
                    levelReport["triangles"] = triangles;
                    levelReport["game_renderer_before"] = native.Evidence();
                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, lod, "native-outside",
                        new Vector3(9.7f, 4.3f, 10.8f), new Vector3(0f, 1.45f, .2f), 35f, shots);
                    levelReport["reflection_environment_after_render"] = EnvironmentEvidence();
                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, lod, "native-saloon",
                        new Vector3(0f, 1.9f, 3.25f), new Vector3(0f, 1.85f, -4.25f), 86f, shots);
                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, lod, "native-rear-seat-access",
                        new Vector3(-.20f, 1.60f, .50f), new Vector3(.74f, 1.05f, -1.42f), 70f, shots);
                    levelReport["game_renderer_after"] = native.Evidence();
                    levelReport["status"] = "captured; manual appearance review required";
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    var cleanup = new List<Exception>();
                    if (native != null) CabCleanup.Try(native.Dispose, cleanup);
                    if (meshes != null) CabCleanup.Try(meshes.Dispose, cleanup);
                    if (cleanup.Count != 0)
                    {
                        if (failure != null) cleanup.Insert(0, failure);
                        throw new AggregateException("Native vehicle check cleanup failed.", cleanup);
                    }
                }
            }
        }

        static object TextureBindings(Material material)
        {
            var result = new Dictionary<string, object>();
            foreach (string property in material.GetTexturePropertyNames())
                result[property] = TextureInfo(material.GetTexture(property));
            return result;
        }

        static object TextureInfo(Texture texture) => texture == null ? null : new
        {
            name = texture.name, width = texture.width, height = texture.height,
            dimension = texture.dimension.ToString(), filter = texture.filterMode.ToString(),
            instance_id = texture.GetInstanceID(),
        };

        static object ScalarProperties(Material material)
        {
            var result = new Dictionary<string, float>();
            foreach (string property in new[] { "_Metallic", "_Smoothness", "_CoatStrength", "_CoatMask", "_ReceivesSSRTransparent", "_TransparentDepthPrepassEnable", "_RefractionDistance", "_RefractionModel", "_AlphaCutoffEnable", "_SurfaceType" })
                if (material.HasProperty(property)) result[property] = material.GetFloat(property);
            return result;
        }

        static object Colour(Material material, string property)
        {
            if (!material.HasProperty(property)) return null;
            Color colour = material.GetColor(property);
            return new[] { colour.r, colour.g, colour.b, colour.a };
        }

        internal static object EnvironmentEvidence()
        {
            var sphericalHarmonics = new float[27];
            var ambient = RenderSettings.ambientProbe;
            for (int channel = 0; channel < 3; channel++)
                for (int coefficient = 0; coefficient < 9; coefficient++)
                    sphericalHarmonics[channel * 9 + coefficient] = ambient[channel, coefficient];
            var volumes = VolumeManager.instance?.stack;
            VisualEnvironment environment = volumes?.GetComponent<VisualEnvironment>();
            HDRISky hdri = volumes?.GetComponent<HDRISky>();
            PhysicallyBasedSky physical = volumes?.GetComponent<PhysicallyBasedSky>();
            return new
            {
                caution = "Metadata alone does not prove useful specular radiance. If metal renders black, inspect the real sky/reflection state and screenshots before changing material metallic or colour.",
                active_colour_space = QualitySettings.activeColorSpace.ToString(),
                ambient_mode = RenderSettings.ambientMode.ToString(), ambient_intensity = RenderSettings.ambientIntensity,
                ambient_sh = sphericalHarmonics, reflection_mode = RenderSettings.defaultReflectionMode.ToString(),
                reflection_intensity = RenderSettings.reflectionIntensity, reflection_resolution = RenderSettings.defaultReflectionResolution,
                skybox_material = RenderSettings.skybox?.name, custom_reflection = TextureInfo(RenderSettings.customReflectionTexture),
                hdrp_sky_type = environment?.skyType.value, hdri_active = hdri?.active,
                hdri_cubemap = TextureInfo(hdri?.hdriSky.value), physically_based_sky_active = physical?.active,
                shader_global_sky_texture = TextureInfo(Shader.GetGlobalTexture("_SkyTexture")),
                active_reflection_probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Where(p => p.enabled && p.gameObject.activeInHierarchy)
                    .Select(p => new { name = p.name, mode = p.mode.ToString(), intensity = p.intensity,
                        baked = TextureInfo(p.bakedTexture), realtime = TextureInfo(p.realtimeTexture) }).ToArray(),
            };
        }

        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
