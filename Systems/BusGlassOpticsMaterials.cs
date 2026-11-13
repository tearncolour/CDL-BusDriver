#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Linq;
using CDLC.BusDriver.Interior;
using UnityEngine;

namespace CDLC.BusDriver
{
    internal sealed class BusGlassOpticsMaterials : IDisposable
    {
        sealed class Entry
        {
            internal Material Material;
            internal float Thickness, Smoothness;
            internal Texture Mask;
            internal bool NeutralMask, ThicknessTouched, SmoothnessTouched, MaskTouched;
        }

        readonly List<Entry> entries = new List<Entry>();
        Texture2D mask;
        bool closing;

        internal BusGlassOpticsMaterials(IEnumerable<Material> materials, ISet<Texture> verifiedNeutralMasks)
        {
            foreach (Material material in materials.Distinct())
            {
                if (material == null || material.shader?.name != "BH/GlsShader" ||
                    !material.name.EndsWith("(CDL glass)", StringComparison.Ordinal) || !material.HasProperty("_Thickness"))
                    throw new InvalidOperationException("Optics accepts only verified CDL private glass materials");
                float thickness = material.GetFloat("_Thickness");
                if (float.IsNaN(thickness) || float.IsInfinity(thickness) || thickness <= 0)
                    throw new InvalidOperationException("Optics requires a finite positive baseline thickness");
                Texture originalMask = material.GetTexture("_MaskMap");
                entries.Add(new Entry { Material = material, Thickness = thickness, Smoothness = material.GetFloat("_Smoothness"), Mask = originalMask,
                    NeutralMask = originalMask != null && verifiedNeutralMasks.Contains(originalMask) });
            }
            if (entries.Count == 0) throw new InvalidOperationException("Optics found no private glass materials");
        }

        internal bool HasNeutralMasks => entries.Any(e => e.NeutralMask);

        internal void SetThicknessHalf()
        {
            EnsureOpen(); Restore();
            foreach (Entry entry in entries)
            {
                entry.ThicknessTouched = true;
                entry.Material.SetFloat("_Thickness", entry.Thickness * .5f);
                if (entry.Material.GetFloat("_Thickness") != entry.Thickness * .5f)
                    throw new InvalidOperationException("Optics thickness change did not apply");
            }
        }

        internal void SetNeutralMask230()
        {
            EnsureOpen(); Restore();
            if (!HasNeutralMasks) throw new InvalidOperationException("No verified existing neutral mask can be varied");
            mask = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            mask.name = "CDL optics temporary neutral mask A230"; mask.hideFlags = HideFlags.DontSave;
            mask.wrapMode = TextureWrapMode.Repeat; mask.filterMode = FilterMode.Bilinear;
            mask.LoadRawTextureData(new byte[] { 0, 255, 0, 230 }); mask.Apply(false, true);
            foreach (Entry entry in entries)
            {
                if (!entry.NeutralMask) continue;
                entry.MaskTouched = true;
                entry.Material.SetTexture("_MaskMap", mask);
                if (entry.Material.GetTexture("_MaskMap") != mask)
                    throw new InvalidOperationException("Optics neutral mask did not apply");
            }
        }

        internal void SetSmoothness95()
        {
            EnsureOpen(); Restore();
            foreach (Entry entry in entries)
            {
                entry.SmoothnessTouched = true;
                entry.Material.SetFloat("_Smoothness", entry.Smoothness * .95f);
                if (entry.Material.GetFloat("_Smoothness") != entry.Smoothness * .95f)
                    throw new InvalidOperationException("Optics smoothness change did not apply");
            }
        }

        internal void Restore()
        {
            var failures = new List<Exception>();
            foreach (Entry entry in entries)
            {
                if (entry.Material == null) { entry.MaskTouched = false; entry.ThicknessTouched = false; entry.SmoothnessTouched = false; continue; }
                if (entry.ThicknessTouched)
                    CabCleanup.Try(() => {
                        entry.Material.SetFloat("_Thickness", entry.Thickness);
                        if (entry.Material.GetFloat("_Thickness") != entry.Thickness)
                            throw new InvalidOperationException("Optics thickness restoration did not persist");
                        entry.ThicknessTouched = false;
                    }, failures);
                if (entry.SmoothnessTouched)
                    CabCleanup.Try(() => {
                        entry.Material.SetFloat("_Smoothness", entry.Smoothness);
                        if (entry.Material.GetFloat("_Smoothness") != entry.Smoothness)
                            throw new InvalidOperationException("Optics smoothness restoration did not persist");
                        entry.SmoothnessTouched = false;
                    }, failures);
                if (entry.MaskTouched)
                    CabCleanup.Try(() => {
                        entry.Material.SetTexture("_MaskMap", entry.Mask);
                        if (entry.Material.GetTexture("_MaskMap") != entry.Mask)
                            throw new InvalidOperationException("Optics mask restoration did not persist");
                        entry.MaskTouched = false;
                    }, failures);
            }
            if (!entries.Any(e => e.MaskTouched) && mask != null &&
                CabCleanup.Try(() => UnityEngine.Object.Destroy(mask), failures)) mask = null;
            CabCleanup.ThrowIfAny(failures, "Optics material restoration failed");
        }

        void EnsureOpen()
        {
            if (closing) throw new ObjectDisposedException(nameof(BusGlassOpticsMaterials));
        }

        public void Dispose()
        {
            closing = true;
            try { Restore(); }
            catch { CabCleanup.Retain(this); throw; }
        }
    }
}
#endif
