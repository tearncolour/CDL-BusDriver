#if CDLC_AUTHORING
using System;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.Rendering;
using Game.Rendering;
using UnityEngine;

namespace CDLC.BusDriver
{
    internal sealed class BusGlassOpticsBinding : IDisposable
    {
        internal SurfaceAsset Surface;
        internal Material Material;
        internal MeshRenderer Renderer;
        internal string Section;
        internal CustomBatch Batch;
        internal int Index;
        internal BatchManagerSystem Manager;

        internal static BusGlassOpticsBinding ForPane(SurfaceAsset surface, MeshRenderer renderer, string section)
        {
            if (surface == null || renderer == null) throw new InvalidOperationException("Missing exact driving-glass binding");
            return new BusGlassOpticsBinding { Surface = surface, Renderer = renderer, Material = renderer.sharedMaterial, Section = section };
        }

        internal void Validate()
        {
            if (Material == null || Material.shader?.name != "BH/GlsShader" ||
                !Material.name.EndsWith("(CDL glass)", StringComparison.Ordinal))
                throw new InvalidOperationException("Optics target is not a live CDL private glass material");
            if (Renderer != null)
            {
                if (Renderer.sharedMaterial != Material || !Renderer.enabled || !Renderer.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("Driving glass binding changed during optics comparison");
                return;
            }
            if (Batch == null || Manager == null || !Manager.World.IsCreated) throw new InvalidOperationException("Native glass World is unavailable");
            var managed = Manager.GetManagedBatches();
            if (Index < 0 || Index >= managed.BatchCount || !ReferenceEquals(managed.GetBatch(Index), Batch) ||
                !ReferenceEquals(Batch.sourceSurface, Surface) || Batch.material != Material)
                throw new InvalidOperationException("Exact native glass binding changed during optics comparison");
        }

        internal object WriteFloatDefault(string property, float value)
        {
            if (Batch == null) return null;
            Validate();
            var native = Manager.GetNativeBatchGroups(true, out var dependencies); dependencies.Complete();
            try
            {
                RequireNativeIndex(native);
                var properties = Manager.GetManagedBatches().GetBatchProperties(Material.shader, new OptionalProperties(Batch.sourceFlags, Batch.sourceType));
                FieldInfo listField = typeof(NativeBatchProperties).GetField("m_Properties", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new NotSupportedException("Native property metadata contract changed");
                object list = listField.GetValue(properties);
                PropertyInfo lengthProperty = list.GetType().GetProperty("Length");
                if (lengthProperty?.PropertyType != typeof(int)) throw new NotSupportedException("Native property length contract changed");
                int count = (int)lengthProperty.GetValue(list);
                if (count < 0 || count > 4096) throw new NotSupportedException("Native property count is invalid");
                int propertyIndex = -1, name = Shader.PropertyToID(property);
                for (int i = 0; i < count; i++) if (properties.GetNameID(i) == name) { propertyIndex = i; break; }
                if (propertyIndex < 0) return new { property, present_in_native_defaults = false, material_value = value };
                // Registered values need an owned original-byte/CAS lease; material readback cannot substitute for it.
                throw new NotSupportedException("Native optics property is registered in batch defaults: " + property +
                    "; this diagnostic cannot safely capture and restore its native bytes, so no native default write was attempted");
            }
            finally { Manager.AddNativeBatchGroupsReader(default); }
        }

        int RequireNativeIndex(NativeBatchGroups<CullingData, GroupData, BatchData, InstanceData> native)
        {
            if (!native.IsCreated || Batch.groupIndex < 0 || Batch.groupIndex >= native.GetGroupCount() || !native.IsValidGroup(Batch.groupIndex))
                throw new InvalidOperationException("Native glass group disappeared");
            int index = native.GetBatchIndex(Batch.groupIndex, Index);
            if (index < 0 || index >= native.GetBatchCount(Batch.groupIndex) || native.GetManagedBatchIndex(Batch.groupIndex, index) != Index ||
                !native.GetBatchMaterialID(Batch.groupIndex, index).Equals(Batch.materialID) ||
                !native.GetBatchMeshID(Batch.groupIndex, index).Equals(Batch.meshID))
                throw new InvalidOperationException("Native glass batch mapping or render IDs changed");
            return index;
        }

        internal void RestoreDefaults()
        {
            // Registered defaults are rejected before any write, so existing callers have no native lease to restore.
        }

        public void Dispose()
        {
            RestoreDefaults();
        }
    }
}
#endif
