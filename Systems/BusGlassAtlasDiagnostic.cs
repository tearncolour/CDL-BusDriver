using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal static class BusGlassAtlasDiagnostic
    {
        internal static async Task<object> Run(Camera camera, RenderTexture target, string directory, string name,
            Transform root, VehicleProfile profile, List<object> shots)
        {
            var bindings = new List<(MeshRenderer renderer, Material[] originals)>();
            var materials = new Dictionary<Material, Material>();
            var maps = new Dictionary<Texture2D, Texture2D>();
            var evidence = new List<object>();
            var flat = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true) { name = "Diagnostic exact neutral normal", hideFlags = HideFlags.DontSave };
            flat.SetPixel(0, 0, new Color(.5f, .5f, 1f, 1f)); flat.Apply();
            Exception failure = null;
            try
            {
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var original = renderer.sharedMaterials; var replacement = (Material[])original.Clone(); bool changed = false;
                    for (int slot = 0; slot < replacement.Length; slot++)
                    {
                        var source = original[slot];
                        if (source?.shader?.name != "BH/GlsShader") continue;
                        if (!materials.TryGetValue(source, out var clone))
                        {
                            materials[source] = clone = new Material(source) { name = source.name + " atlas diagnostic", hideFlags = HideFlags.DontSave };
                            foreach (string property in new[] { "_BaseColorMap", "_MaskMap", "_NormalMap" })
                            {
                                if (!(source.GetTexture(property) is Texture2D texture)) throw new InvalidOperationException("Glass diagnostic requires Texture2D: " + property);
                                if (maps.ContainsKey(texture)) continue;
                                var copy = new Texture2D(texture.width, texture.height, texture.graphicsFormat, UnityEngine.Experimental.Rendering.TextureCreationFlags.None)
                                { name = texture.name + " mip zero only", hideFlags = HideFlags.DontSave,
                                    filterMode = texture.filterMode, wrapModeU = texture.wrapModeU, wrapModeV = texture.wrapModeV,
                                    wrapModeW = texture.wrapModeW, anisoLevel = texture.anisoLevel, mipMapBias = texture.mipMapBias };
                                maps.Add(texture, copy);
                                Graphics.CopyTexture(texture, 0, 0, copy, 0, 0);
                            }
                        }
                        replacement[slot] = clone; changed = true;
                    }
                    if (changed) { bindings.Add((renderer, original)); renderer.sharedMaterials = replacement; }
                }
                if (materials.Count == 0) throw new InvalidOperationException("No production glass found");
                foreach (string phase in new[] { "baseline", "mip-zero", "exact-neutral-normal", "transparent-ssr-off", "restored" })
                {
                    foreach (var pair in materials)
                    {
                        var clone = pair.Value; clone.CopyPropertiesFromMaterial(pair.Key); clone.shaderKeywords = pair.Key.shaderKeywords;
                        if (phase == "mip-zero")
                            foreach (string property in new[] { "_BaseColorMap", "_MaskMap", "_NormalMap" })
                                clone.SetTexture(property, maps[(Texture2D)pair.Key.GetTexture(property)]);
                        if (phase == "exact-neutral-normal") clone.SetTexture("_NormalMap", flat);
                        if (phase == "transparent-ssr-off")
                        {
                            clone.SetFloat("_ReceivesSSRTransparent", 0f); clone.SetFloat("_TransparentDepthPrepassEnable", 0f);
                            HDMaterial.ValidateMaterial(clone);
                            if (clone.GetShaderPassEnabled("TransparentDepthPrepass")) throw new InvalidOperationException("Transparent prepass stayed enabled");
                        }
                    }
                    var eyeFrame = BusInterior.Eye(profile.Spec);
                    Vector3 eye = new Vector3(eyeFrame.x, eyeFrame.y, eyeFrame.z);
                    await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, 0, "glass-atlas-" + phase + "-cab",
                        eye, eye + new Vector3(.18f, -.20f, 1.35f), 88f, shots);
                    foreach (var sign in new[] { profile.SideSign }.Concat(profile.ExtraSideSigns))
                    {
                        Vector3 c = new Vector3(sign.Centre.x, sign.Centre.y, sign.Centre.z);
                        Vector3 direction = (Quaternion)BusInterior.SideSignRotation(Mathf.Sign(c.x), sign.Tilt) * Vector3.back;
                        await BusVehicleProbe.CaptureReviewed(camera, target, directory, name, 0,
                            "glass-atlas-" + phase + (c.x > 0 ? "-right-sign" : "-left-sign"), c + direction * 2f, c, 35f, shots);
                    }
                    evidence.Add(new { phase, materials = materials.Values.Select(BusVehicleMaterialDiagnostics.ShaderEvidence).ToArray() });
                }
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                var errors = new List<Exception>();
                foreach (var binding in bindings) CabCleanup.Try(() => binding.renderer.sharedMaterials = binding.originals, errors);
                foreach (var material in materials.Values) CabCleanup.DestroyOrRetain(material, errors);
                foreach (var map in maps.Values) CabCleanup.DestroyOrRetain(map, errors);
                CabCleanup.DestroyOrRetain(flat, errors);
                if (errors.Count != 0)
                {
                    if (failure != null) errors.Insert(0, failure);
                    throw new AggregateException("Glass atlas diagnostic cleanup failed", errors);
                }
            }
            return new { phases = evidence, original_materials_restored = true,
                scope = "Private material copies, exact original geometry and camera; single-variable comparisons are diagnostic only. GPU CopyTexture retains mip-zero format and bytes. No package or shared texture mutation." };
        }
    }
}
