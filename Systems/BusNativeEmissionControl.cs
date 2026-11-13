using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CDLC.BusDriver
{
    // Diagnostic only: keep geometry, material parameters and keywords; replace
    // the actual native emissive map on private material copies, then restore.
    internal sealed class BusNativeEmissionControl : IDisposable
    {
        const string Property = "_EmissiveColorMap";
        readonly Dictionary<MeshRenderer, Material[]> m_Originals = new Dictionary<MeshRenderer, Material[]>();
        readonly Dictionary<Material, Material> m_Copies = new Dictionary<Material, Material>();
        readonly Texture2D m_Black;
        internal readonly List<object> Evidence = new List<object>();

        // model: the bus's name; its native textures are named "<model>_..." (the check below refuses a shared/default map)
        internal BusNativeEmissionControl(GameObject root, string model)
        {
            m_Black = new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
            { name = "CDLC private black emissive diagnostic", hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            m_Black.SetPixels(Enumerable.Repeat(new Color(0f, 0f, 0f, 1f), 16).ToArray());
            m_Black.Apply(false, false);
            try
            {
                Color sample = BusVehicleProbe.SampleLinear(m_Black, 2, 2);
                if (sample.r != 0f || sample.g != 0f || sample.b != 0f)
                    throw new InvalidOperationException("Private emission texture failed GPU black readback");
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Material[] original = renderer.sharedMaterials;
                    Material[] replacement = (Material[])original.Clone();
                    bool changed = false;
                    for (int i = 0; i < original.Length; i++)
                    {
                        Material source = original[i];
                        if (source.shader.name == "BH/GlsShader") continue;
                        if (!source.HasProperty(Property) || !(source.GetTexture(Property) is Texture2D map) ||
                            !map.name.StartsWith(model + "_", StringComparison.Ordinal))
                            throw new InvalidOperationException("Missing model-specific native emissive texture binding");
                        if (!m_Copies.TryGetValue(source, out Material copy))
                        {
                            copy = new Material(source) { name = source.name + " private black emission control", hideFlags = HideFlags.DontSave };
                            m_Copies.Add(source, copy);
                            copy.SetTexture(Property, m_Black);
                            if (copy.GetTexture(Property) != m_Black) throw new InvalidOperationException("Emissive replacement not bound");
                            Evidence.Add(new { source_material = source.name, source_shader = source.shader.name, property = Property,
                                original_texture = map.name, original_mips = map.mipmapCount, replacement = m_Black.name,
                                replacement_linear_rgb = new[] { sample.r, sample.g, sample.b }, keywords = copy.shaderKeywords });
                        }
                        replacement[i] = copy; changed = true;
                    }
                    if (changed) { m_Originals.Add(renderer, original); renderer.sharedMaterials = replacement; }
                }
                if (m_Copies.Count == 0) throw new InvalidOperationException("No native emission control material found");
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (var pair in m_Originals) if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            Evidence.Add(new { restored_native_material_bindings = m_Originals.All(x => x.Key == null || x.Key.sharedMaterials.SequenceEqual(x.Value)) });
            m_Originals.Clear();
            foreach (var material in m_Copies.Values) UnityEngine.Object.Destroy(material);
            m_Copies.Clear();
            if (m_Black != null) UnityEngine.Object.Destroy(m_Black);
        }
    }
}
