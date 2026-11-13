using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDLC.Asset.Rendering;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Game.Prefabs;
using UnityEngine;

namespace CDLC.BusDriver
{
    internal static class BusOpaqueAtlasDiagnostic
    {
        internal static async Task<object> Run(Camera camera, RenderTexture target, string directory, string name,
            PrefabBase prefab, CabInstance instance, VehicleProfile profile, List<object> shots)
        {
            Transform root = instance.Root;
            var native = new BusNativeCab();
            var hidden = new List<(Renderer renderer, bool enabled)>();
            var bindings = new List<(MeshRenderer renderer, Material[] originals, bool shadows)>();
            var materials = new Dictionary<Material, Material>();
            var maps = new Dictionary<Texture2D, Texture2D>();
            var evidence = new List<object>();
            Texture2D flat = null;
            Exception failure = null;
            try
            {
                if (!native.Build(prefab, profile, root, root.gameObject.layer))
                    throw new InvalidOperationException("Native cab diagnostic construction failed");
                foreach (int part in NativeCabOwnership.ReplacedParts(profile.Mesh))
                {
                    Renderer renderer = instance.Part(part)?.GetComponent<Renderer>();
                    if (renderer == null) continue;
                    hidden.Add((renderer, renderer.enabled));
                    renderer.enabled = false;
                }
                flat = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true)
                    { name = "Opaque diagnostic exact neutral normal", hideFlags = HideFlags.DontSave };
                flat.SetPixel(0, 0, new Color(.5f, .5f, 1f, 1f)); flat.Apply();
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var originals = renderer.sharedMaterials;
                    var replacements = (Material[])originals.Clone();
                    bool changed = false;
                    for (int slot = 0; slot < originals.Length; slot++)
                    {
                        Material source = originals[slot];
                        if (source?.shader?.name != "BH/SG_DefaultShader") continue;
                        if (!materials.TryGetValue(source, out var clone))
                        {
                            materials.Add(source, clone = new Material(source)
                                { name = source.name + " opaque atlas diagnostic", hideFlags = HideFlags.DontSave });
                            foreach (string property in new[] { "_BaseColorMap", "_MaskMap", "_NormalMap", "_ControlMask" })
                            {
                                if (!source.HasProperty(property) || !(source.GetTexture(property) is Texture2D texture)) continue;
                                if (maps.ContainsKey(texture)) continue;
                                var copy = new Texture2D(texture.width, texture.height, texture.graphicsFormat,
                                    UnityEngine.Experimental.Rendering.TextureCreationFlags.None)
                                { name = texture.name + " opaque mip zero", hideFlags = HideFlags.DontSave,
                                    filterMode = texture.filterMode, wrapModeU = texture.wrapModeU,
                                    wrapModeV = texture.wrapModeV, wrapModeW = texture.wrapModeW,
                                    anisoLevel = texture.anisoLevel, mipMapBias = texture.mipMapBias };
                                maps.Add(texture, copy);
                                Graphics.CopyTexture(texture, 0, 0, copy, 0, 0);
                            }
                        }
                        replacements[slot] = clone; changed = true;
                    }
                    if (!changed) continue;
                    bindings.Add((renderer, originals, renderer.receiveShadows));
                    renderer.sharedMaterials = replacements;
                }
                if (materials.Count == 0) throw new InvalidOperationException("No native opaque material in diagnostic root");
                foreach (string phase in new[] { "baseline", "exact-neutral-normal", "mip-zero", "receive-shadows-off", "restored" })
                {
                    foreach (var pair in materials)
                    {
                        Material clone = pair.Value;
                        clone.CopyPropertiesFromMaterial(pair.Key); clone.shaderKeywords = pair.Key.shaderKeywords;
                        if (phase == "exact-neutral-normal") clone.SetTexture("_NormalMap", flat);
                        if (phase == "mip-zero")
                            foreach (string property in new[] { "_BaseColorMap", "_MaskMap", "_NormalMap", "_ControlMask" })
                                if (pair.Key.HasProperty(property) && pair.Key.GetTexture(property) is Texture2D texture && maps.ContainsKey(texture))
                                    clone.SetTexture(property, maps[texture]);
                    }
                    foreach (var binding in bindings)
                        binding.renderer.receiveShadows = phase == "receive-shadows-off" ? false : binding.shadows;
                    var frame = BusInterior.Eye(profile.Spec);
                    Vector3 eye = new Vector3(frame.x, frame.y, frame.z);
                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, 0, "opaque-atlas-" + phase + "-cab",
                        eye, eye + new Vector3(.18f, -.14f, 1.35f), 80f, shots);
                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, 0, "opaque-atlas-" + phase + "-outside",
                        new Vector3(10f, 6f, 13f), new Vector3(0f, 1.7f, 0f), 55f, shots);
                    evidence.Add(new { phase, materials = materials.Values.Select(BusVehicleMaterialDiagnostics.ShaderEvidence).ToArray() });
                }
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                var errors = new List<Exception>();
                foreach (var binding in bindings)
                    CabCleanup.Try(() => { binding.renderer.sharedMaterials = binding.originals; binding.renderer.receiveShadows = binding.shadows; }, errors);
                foreach (var material in materials.Values) CabCleanup.DestroyOrRetain(material, errors);
                foreach (var map in maps.Values) CabCleanup.DestroyOrRetain(map, errors);
                if (flat != null) CabCleanup.DestroyOrRetain(flat, errors);
                foreach (var part in hidden) CabCleanup.Try(() => part.renderer.enabled = part.enabled, errors);
                CabCleanup.Try(native.Dispose, errors);
                if (errors.Count != 0)
                {
                    if (failure != null) errors.Insert(0, failure);
                    throw new AggregateException("Opaque atlas diagnostic cleanup failed", errors);
                }
            }
            return new { phases = evidence, native_cab = native.Describe, original_materials_restored = true,
                scope = "Private material copies; original vertex streams and camera, independent normal/mip/shadow controls. Diagnostic only; native AI road renderer still requires separate confirmation." };
        }
    }
}
