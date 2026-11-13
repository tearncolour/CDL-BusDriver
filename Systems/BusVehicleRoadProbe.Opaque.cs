#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CDLC.Asset.Rendering;
using CDLC.BusDriver.Interior;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    internal static partial class BusVehicleRoadProbe
    {
        static async Task RunNativeOpaqueComparison(SystemBase host, Entity[] sections, Action check,
            Func<Task> settle, Func<string, Task> capture, List<object> results)
        {
            var expected = new HashSet<SurfaceAsset>();
            var prefabs = host.World.GetExistingSystemManaged<PrefabSystem>();
            foreach (Entity entity in sections)
            {
                Require(prefabs.TryGetPrefab(host.EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab, out PrefabBase prefab)
                    && prefab is ObjectGeometryPrefab, "Opaque comparison prefab missing");
                foreach (var item in ((ObjectGeometryPrefab)prefab).m_Meshes)
                    if (item.m_Mesh is RenderPrefab root)
                    {
                        var levels = new List<RenderPrefab> { root };
                        if (root.TryGet(out LodProperties lod) && lod.m_LodMeshes != null) levels.AddRange(lod.m_LodMeshes);
                        foreach (var level in levels)
                            for (int slot = 0; slot < level.materialCount; slot++)
                            {
                                var surface = level.GetSurfaceAsset(slot);
                                if (surface != null && !BusGlass.IsGlass(surface)) expected.Add(surface);
                            }
                    }
            }
            var managed = host.World.GetExistingSystemManaged<BatchManagerSystem>().GetManagedBatches();
            var targets = new Dictionary<Material, SurfaceAsset>();
            for (int i = 0; i < managed.BatchCount; i++)
                if (managed.GetBatch(i) is CustomBatch batch && expected.Contains(batch.sourceSurface) && batch.material != null)
                    targets[batch.material] = batch.sourceSurface;
            Require(targets.Count > 0, "Opaque comparison has no exact native target batches");
            var snapshots = targets.Keys.ToDictionary(m => m, m => new Material(m));
            var readers = new Dictionary<Material, NativeMaterialLease>();
            var rows = new List<object>();
            var report = new Dictionary<string, object> { ["scope"] = "Diagnostic only: exact pack native batch materials in owned temporary World; shared template/files unchanged; all properties restored",
                ["captures"] = rows, ["status"] = "RUNNING" };
            results.Add(report);
            Exception failure = null;
            try
            {
                foreach (var pair in targets) readers[pair.Key] = NativeMaterialLease.Acquire(pair.Value);
                foreach (string phase in new[] { "baseline", "animation-off", "no-vt", "restored" })
                {
                    check();
                    foreach (var pair in snapshots)
                    {
                        pair.Key.CopyPropertiesFromMaterial(pair.Value);
                        if (phase == "animation-off")
                        {
                            pair.Key.DisableKeyword("_GPU_ANIMATION_PROCEDURAL");
                            pair.Key.DisableKeyword("_GPU_ANIMATION_SHAPE");
                        }
                        if (phase == "no-vt")
                        {
                            Material source = readers[pair.Key].Material;
                            foreach (string name in source.GetTexturePropertyNames())
                                if (pair.Key.HasProperty(name))
                                {
                                    pair.Key.SetTexture(name, source.GetTexture(name));
                                    pair.Key.SetTextureScale(name, source.GetTextureScale(name));
                                    pair.Key.SetTextureOffset(name, source.GetTextureOffset(name));
                                }
                            pair.Key.DisableKeyword("ENABLE_VT");
                        }
                    }
                    await settle(); check();
                    string filename = "opaque-native-12-" + phase + ".png";
                    await capture(filename);
                    rows.Add(new { phase, filename, materials = targets.Select(p => new { surface = p.Value.id.ToString(),
                        material = BusVehicleMaterialDiagnostics.ShaderEvidence(p.Key) }).ToArray() });
                }
                report["status"] = "CAPTURED_REQUIRES_IMAGE_REVIEW";
            }
            catch (Exception error) { failure = error; report["status"] = "FAIL"; throw; }
            finally
            {
                var errors = new List<Exception>();
                foreach (var pair in snapshots)
                {
                    CabCleanup.Try(() => pair.Key.CopyPropertiesFromMaterial(pair.Value), errors);
                    CabCleanup.Try(() => UnityEngine.Object.Destroy(pair.Value), errors);
                }
                foreach (var lease in readers.Values) CabCleanup.Try(lease.Dispose, errors);
                report["cleanup_errors"] = errors.Select(e => e.ToString()).ToArray();
                if (errors.Count > 0)
                {
                    if (failure != null) errors.Insert(0, failure);
                    throw new AggregateException("Native opaque comparison restoration failed", errors);
                }
            }
        }
    }
}
#endif
