using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver.Kit;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver.Interior
{
    internal static class GenericCabDetail
    {
        static readonly Dictionary<(Material, bool), Material> s_Materials = new Dictionary<(Material, bool), Material>();
        static readonly Dictionary<string, Texture2D> s_Maps = new Dictionary<string, Texture2D>();

        internal static Material Material(Material source, Fin finish, bool steering)
        {
            string tile = Tile(finish, steering);
            // the game's shader has no detail map: a material made on it keeps its plain look (the owner accepted the loss of the relief, 7 November)
            if (tile == null || source == null || GameShaderMove.OnGameShader(source)) return source;
            var key = (source, steering);
            if (s_Materials.TryGetValue(key, out Material cached)) return cached;
            Material copy = null;
            try
            {
                copy = new Material(source) { name = source.name + " baked relief", hideFlags = HideFlags.DontSave };
                copy.SetTexture("_DetailMap", Map(tile));
                if (tile == "leather") copy.SetFloat("_Smoothness", .44f);
                copy.SetFloat("_UVDetail", 1f); copy.SetVector("_UVDetailsMappingMask", new Vector4(0, 1, 0, 0));
                copy.SetFloat("_LinkDetailsWithBase", 0f); copy.SetFloat("_DetailNormalScale", 1f);
                copy.SetFloat("_DetailAlbedoScale", 0f); copy.SetFloat("_DetailSmoothnessScale", 0f);
                HDMaterial.ValidateMaterial(copy);
                // ValidateMaterial puts an opaque material back in queue 2000, outside the depth prepass the road's decals are cut against: the road's markings and the stop's
                // paving then showed through the body, the roof and the cab (the owner, 6-7 November). The cab's other materials are set after their validation (CabMaterials.MakeLit).
                copy.renderQueue = CabMaterials.kQueueOpaqueDecal;
                if (!copy.IsKeywordEnabled("_DETAIL_MAP") || !copy.IsKeywordEnabled("_NORMALMAP"))
                    throw new InvalidOperationException("HDRP rejected the generic cab detail normal map.");
                s_Materials.Add(key, copy);
                return copy;
            }
            catch (Exception error) { CabCleanup.FailedAllocation(copy, error); throw; }
        }

        internal static string Tile(Fin finish, bool steering)
        {
            if (finish == Fin.Matte || finish == Fin.Satin || finish == Fin.MouldedPlastic) return steering ? "leather" : "polymer";
            return finish == Fin.Metal || finish == Fin.BrushedMetal ? "brushed" : finish == Fin.Textile ? "woven" : null;
        }

        static Texture2D Map(string tile)
        {
            if (s_Maps.TryGetValue(tile, out Texture2D existing)) return existing;
            Texture2D texture = null;
            try
            {
                using (Stream input = typeof(GenericCabDetail).Assembly.GetManifestResourceStream("CDLC.BusDriver.Detail." + tile + ".png"))
                {
                    if (input == null) throw new FileNotFoundException("Missing baked cab normal tile: " + tile);
                    using (var data = new MemoryStream())
                    {
                        input.CopyTo(data);
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true) { name = "Cab baked " + tile, hideFlags = HideFlags.DontSave,
                            wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
                        if (!ImageConversion.LoadImage(texture, data.ToArray(), false)) throw new InvalidDataException("Invalid detail map " + tile);
                        texture.Apply(true, true);
                    }
                }
                s_Maps.Add(tile, texture); return texture;
            }
            catch (Exception error) { CabCleanup.FailedAllocation(texture, error); throw; }
        }

        internal static void TakeResources(HashSet<UnityEngine.Object> resources)
        {
            foreach (Material material in s_Materials.Values) resources.Add(material);
            foreach (Texture2D texture in s_Maps.Values) resources.Add(texture);
            s_Materials.Clear(); s_Maps.Clear();
        }

        internal static Mesh Upload(KitMeshData d, int part, string name, SubMeshDescriptor[] subs, Bounds bounds)
        {
            Mesh mesh = null;
            try
            {
                int count = d.P.Length;
                var positions = new Vector3[count]; var normals = new Vector3[count]; var baseUv = new Vector2[count];
                var detailUv = new Vector2[count]; var tangents = new Vector4[count];
                for (int i = 0; i < count; i++) { positions[i] = d.P[i]; normals[i] = d.N[i]; baseUv[i] = d.UV[i]; }
                for (int t = 0; t < d.Idx.Length; t += 3)
                {
                    int ia = d.Idx[t], ib = d.Idx[t + 1], ic = d.Idx[t + 2];
                    GenericDetailUv.Triangle(part, d.P[ia], d.P[ib], d.P[ic], d.N[ia] + d.N[ib] + d.N[ic], out float2 ua, out float2 ub, out float2 uc);
                    detailUv[ia] = ua; detailUv[ib] = ub; detailUv[ic] = uc;
                    GenericDetailUv.Basis(d.P[ia], d.P[ib], d.P[ic], ua, ub, uc, out float3 u, out float3 v);
                    if (!math.all(math.isfinite(u)) || !math.all(math.isfinite(v))) throw new InvalidDataException("Degenerate cab detail UV: " + name);
                    for (int k = 0; k < 3; k++)
                    {
                        int i = d.Idx[t + k]; float3 n = d.N[i], tangent = math.normalizesafe(u - n * math.dot(u, n));
                        tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, math.dot(math.cross(n, tangent), v) < 0 ? -1f : 1f);
                    }
                }
                mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.vertices = positions; mesh.normals = normals; mesh.uv = baseUv; mesh.uv2 = detailUv; mesh.tangents = tangents;
                mesh.SetIndexBufferParams(d.Idx.Length, IndexFormat.UInt32); mesh.SetIndexBufferData(d.Idx, 0, 0, d.Idx.Length);
                mesh.subMeshCount = subs.Length;
                for (int i = 0; i < subs.Length; i++) mesh.SetSubMesh(i, subs[i], MeshUpdateFlags.DontRecalculateBounds);
                mesh.bounds = bounds; mesh.UploadMeshData(true); return mesh;
            }
            catch (Exception error) { CabCleanup.FailedAllocation(mesh, error); throw; }
        }
    }
}
