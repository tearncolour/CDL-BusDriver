using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior;
using Colossal.IO.AssetDatabase;
using Colossal.IO.AssetDatabase.VirtualTexturing;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    internal sealed partial class BusCitizens
    {
        private StyleData Upload(CharacterStyle style, CitizenSkeleton skeleton, StylePoses poses, long mainTicks)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var data = new StyleData
            {
                Skeleton = skeleton, Offset = poses.Offset, Seated0 = poses.Seated0, Standing0 = poses.Standing0, Holding = poses.Holding,
                Driver = poses.Driver, DriverFirst = poses.DriverFirst,
            };
            m_Styles.Add(style, data);
            try
            {
                data.Bones = new ComputeBuffer(poses.All.Length, UnsafeUtility.SizeOf<BoneElement>(), ComputeBufferType.Structured);
                data.Bones.SetData(poses.All);
                data.Ready = true;
            }
            catch (Exception failure)
            {
                try { ReleaseStyle(style, data); }
                catch (Exception cleanup) { throw new AggregateException("Citizen style allocation and cleanup failed.", failure, cleanup); }
                throw;
            }
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Mod.log.Info($"Game citizens: style {style.name}, {skeleton.Count} bones, {poses.Count} poses (made on a worker thread in {poses.Ms:0} ms; " +
                $"{(mainTicks + System.Diagnostics.Stopwatch.GetTimestamp() - t0) * ms:0.0} ms on the main thread: the clips and the upload).");
            return data;
        }

        private void ReleaseStyle(CharacterStyle style, StyleData data)
        {
            if (data?.Bones != null) { data.Bones.Release(); data.Bones = null; }
            m_Styles.Remove(style);
        }

        private PartData Part(RenderPrefab prefab)
        {
            if (prefab == null) return null;
            if (m_Parts.TryGetValue(prefab, out PartData known))
            {
                if (known == null || known.Ready) return known;
                ReleasePart(prefab, known);
            }
            // a citizen model the game is streaming in: asking for it now would leave its load stuck (BusGlass.GeometryLoading)
            if (BusGlass.GeometryLoading(prefab)) throw new BusGlass.GeometryBusyException();
            var part = new PartData { Prefab = prefab };
            m_Parts.Add(prefab, part);
            try
            {
                Mesh[] meshes = prefab.ObtainMeshes();
                Material[] materials = prefab.ObtainMaterials(true);
                if (meshes == null || materials == null)
                    throw new InvalidOperationException("Citizen render prefab did not provide meshes and materials.");
                part.Meshes = meshes;
                if (m_Streaming == null)
                    m_Streaming = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<TextureStreamingSystem>();
                var clones = new Material[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;
                    Material m = clones[i] = new Material(materials[i]) { hideFlags = HideFlags.DontSave };
                    part.Clones.Add(m);
                    m.SetFloat("colossal_LodFade", 1f);
                    m.EnableKeyword("_GPU_ANIMATION_SHAPE");
                    SurfaceAsset surface = prefab.GetSurfaceAsset(i);
                    VTAtlassingInfo[] atlas = surface?.VTAtlassingInfos ?? surface?.PreReservedAtlassingInfos;
                    for (int j = 0; j < 2 && atlas != null && m_Streaming != null; j++)
                    {
                        if (atlas.Length <= j || atlas[j].indexInStack < 0) continue;
                        m.EnableKeyword(m.shader.keywordSpace.FindKeyword("ENABLE_VT"));
                        m_Streaming.BindMaterial(m, atlas[j].stackGlobalIndex, j, m_Streaming.GetTextureParamBlock(atlas[j]));
                        part.Pages.Add((atlas[j].stackGlobalIndex, atlas[j].indexInStack));
                    }
                }
                part.Materials = new Material[meshes.Length][];
                int at = 0;
                for (int k = 0; k < meshes.Length; k++)
                {
                    int count = meshes[k] != null ? meshes[k].subMeshCount : 0;
                    part.Materials[k] = new Material[count];
                    for (int s = 0; s < count; s++)
                        part.Materials[k][s] = at + s < clones.Length ? clones[at + s] : null;
                    at += count;
                }
                part.Ready = true;
                return part;
            }
            catch (Exception failure)
            {
                try { ReleasePart(prefab, part); }
                catch (Exception cleanup) { throw new AggregateException("Citizen part allocation and cleanup failed.", failure, cleanup); }
                throw;
            }
        }

        private void ReleasePart(RenderPrefab prefab, PartData part)
        {
            if (part == null) { m_Parts.Remove(prefab); return; }
            var failures = new List<Exception>();
            for (int i = part.Clones.Count - 1; i >= 0; i--)
            {
                Material material = part.Clones[i];
                if (CabCleanup.Try(() => { if (material != null) UnityEngine.Object.Destroy(material); }, failures)) part.Clones.RemoveAt(i);
            }
            if (part.Clones.Count == 0 && part.Prefab != null)
                CabCleanup.Try(() => { part.Prefab.Release(); part.Prefab = null; }, failures);
            if (part.Clones.Count == 0 && part.Prefab == null) m_Parts.Remove(prefab);
            CabCleanup.ThrowIfAny(failures, "Citizen part cleanup failed.");
        }
    }
}
