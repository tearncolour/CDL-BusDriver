using System;
using System.Collections.Generic;
using CDLC.Asset.Rendering;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The pack's own game asset drawn in the driver's view (profile "cabRender": "native"): its LOD0 opaque meshes with private copies
    /// of their native materials (the vehicle shader with its normal and mask maps), under the cab instance's root, in place of the
    /// runtime mesh's static parts. What the runtime mesh moves (steering wheel, needles, door leaves, wheels, door drives, straps)
    /// and the card reader the mod draws are left out: the asset's triangles whose corners all lie on the runtime mesh's moving
    /// corners, or inside a reader's housing, are dropped, so the instance's own parts stand there. Glass stays BusGlass's.
    /// The per-renderer properties the game's own renderer sets (RenderPrefabRenderer) are set here too: the procedural light
    /// buffer with every lamp off (the instance's lamp parts light the lenses), its parameters, the window state. Virtual texturing
    /// is switched off on the private materials (no VT buffer is bound here); a mesh with bones keeps the procedural animation and is
    /// skinned by the vehicle's own bones from a buffer of the cab's (Pose).
    /// </summary>
    internal sealed class BusNativeCab : IDisposable
    {
        private sealed class Piece
        {
            internal GameObject Object;
            internal Mesh Mesh;
            internal int Lamps;     // the lamp parts whose unlit face this piece is (NativeCabOwnership.LampBit of each); 0: body
        }

        private static readonly string[] s_OffKeywords = { "_GPU_ANIMATION_PROCEDURAL", "_GPU_ANIMATION_SHAPE", "ENABLE_VT" };
        private static readonly int s_DecalLayerMask = Shader.PropertyToID("colossal_DecalLayerMask");

        private readonly List<Piece> m_Pieces = new List<Piece>();
        private readonly List<NativeMaterialLease> m_Leases = new List<NativeMaterialLease>();
        private readonly List<Material> m_Materials = new List<Material>();
        // Each native render prefab owns its palette, materials and GPU buffers.
        private readonly List<BusNativeCab> m_AdditionalMeshes = new List<BusNativeCab>();
        private int m_MeshSlot;
        private ComputeBuffer m_Lights;
        // The skin matrices the vertex shader reads as the game's procedural animation does (_BoneTransforms: one per bone by bind index,
        // the previous frame's after them): null for a mesh without bones.
        private ComputeBuffer m_Bones;
        private float4x4[] m_Skins, m_Models;
        private RenderPrefab m_Mesh;
        private Unity.Entities.Entity m_MeshEntity;
        private bool m_PoseFailed;
        private bool m_RetainedForCleanup;

        internal bool Ready { get; private set; }
        internal string Describe { get; private set; } = "none";

        // a piece's triangles at most: one piece is one step's upload
        private const int kPieceTriangles = 32768;

        // The build in steps (Begin, Step): the opaque submeshes read and cut on a worker, then a material or a piece at a time.
        private NativeGeometryRead m_Cut;
        private int m_Next;
        private Transform m_Parent;
        private int m_Layer, m_LightCount, m_BoneCount;
        private EmissiveProperties m_Emissive;
        private readonly Dictionary<int, Material> m_BySubMesh = new Dictionary<int, Material>();
        private readonly Dictionary<int, int> m_Kept = new Dictionary<int, int>();
        private NativeCabOwnership.Split m_Split;        // made on the worker; read once the cut is done
        private bool m_Finished;
        private bool m_Visible = true;
        private int m_LitLamps;                          // NativeCabOwnership.LampBit of the lamp parts the runtime mesh shows lit

        /// <summary>
        /// Starts the pieces under <paramref name="parent"/> (the cab instance's root, model frame): every palette's opaque submeshes are read
        /// and cut off the main thread. False when the vehicle has no native geometry.
        /// </summary>
        internal bool Begin(PrefabBase vehicle, VehicleProfile profile, Transform parent, int layer)
        {
            if (!(vehicle is ObjectGeometryPrefab geometry) || geometry.m_Meshes == null || geometry.m_Meshes.Length == 0 ||
                parent == null || profile == null)
                return false;
            foreach (ObjectMeshInfo info in geometry.m_Meshes)
            {
                if (!(info?.m_Mesh is RenderPrefab))
                    throw new InvalidOperationException("Native cab requires render prefabs.");
                // This path replaces runtime corners in the common vehicle frame; conditional or offset meshes need a different contract.
                if (geometry.m_Meshes.Length > 1 && (math.any(info.m_Position != float3.zero) ||
                    !info.m_Rotation.Equals(quaternion.identity) || info.m_RequireState != 0))
                    throw new InvalidOperationException("Native cab palettes must share the unconditional vehicle frame.");
            }
            for (int slot = 0; slot < geometry.m_Meshes.Length; slot++)
            {
                BusNativeCab part = slot == 0 ? this : new BusNativeCab();
                if (slot != 0) m_AdditionalMeshes.Add(part);
                part.BeginMesh((RenderPrefab)geometry.m_Meshes[slot].m_Mesh, profile, parent, layer, slot);
            }
            Describe = "reading";
            return true;
        }

        private void BeginMesh(RenderPrefab mesh, VehicleProfile profile, Transform parent, int layer, int slot)
        {
            m_MeshSlot = slot;
            m_Mesh = mesh;
            m_Parent = parent;
            m_Layer = layer;
            m_Emissive = mesh.GetComponent<EmissiveProperties>();
            m_LightCount = m_Emissive != null && m_Emissive.active ? m_Emissive.lightsCount + 1 : 0;
            m_BoneCount = mesh.GetComponent<ProceduralAnimationProperties>()?.m_Bones?.Length ?? 0;
            var wanted = new bool[mesh.materialCount];
            for (int i = 0; i < wanted.Length; i++)
                wanted[i] = !BusGlass.IsGlass(mesh.GetSurfaceAsset(i));
            m_Cut = new NativeGeometryRead(mesh, wanted, () =>
            {
                var split = new NativeCabOwnership.Split(profile);
                m_Split = split;
                return split.GroupOf;
            }, kPieceTriangles);
        }

        /// <summary>
        /// Advances every palette within <paramref name="budgetTicks"/> (at least one piece of each palette whose cut is there); true when
        /// all are complete. A failure is thrown; Dispose then releases what was made.
        /// </summary>
        internal bool Step(long budgetTicks)
        {
            if (m_Finished) return true;
            long end = budgetTicks == long.MaxValue ? long.MaxValue : System.Diagnostics.Stopwatch.GetTimestamp() + budgetTicks;
            bool done = StepMesh(end);
            foreach (BusNativeCab part in m_AdditionalMeshes)
                done &= part.StepMesh(end);
            if (!done) return false;
            m_Finished = true;
            var descriptions = new List<string> { $"palette 0: {MeshDescription}" };
            for (int i = 0; i < m_AdditionalMeshes.Count; i++)
                descriptions.Add($"palette {i + 1}: {m_AdditionalMeshes[i].MeshDescription}");
            Describe = string.Join("; ", descriptions);
            bool ready = m_Pieces.Count > 0;
            foreach (BusNativeCab part in m_AdditionalMeshes) ready |= part.m_Pieces.Count > 0;
            Ready = ready;
            return true;
        }

        /// <summary>The whole build in one call (the authoring diagnostics): Begin, wait for the cuts, every piece.</summary>
        internal bool Build(PrefabBase vehicle, VehicleProfile profile, Transform parent, int layer)
        {
            if (!Begin(vehicle, profile, parent, layer)) return false;
            m_Cut?.Finish();
            foreach (BusNativeCab part in m_AdditionalMeshes) part.m_Cut?.Finish();
            Step(long.MaxValue);
            return Ready;
        }

        private string MeshDescription { get; set; } = "none";

        private bool StepMesh(long end)
        {
            if (m_Cut == null) return true;
            if (!m_Cut.Poll()) return false;
            List<NativePiece> pieces = m_Cut.Pieces;
            if (m_Next == 0 && pieces.Count > 0)
                CreateBuffers();
            while (m_Next < pieces.Count)
            {
                MakePiece(pieces[m_Next++]);
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= end) break;
            }
            if (m_Next < pieces.Count) return false;

            var found = new List<string>();
            int kept = 0, lampFaces = 0;
            foreach (Piece piece in m_Pieces)
                if (piece.Lamps != 0) lampFaces += piece.Mesh.GetSubMesh(0).indexCount / 3;
            foreach (KeyValuePair<int, int> entry in m_Kept)
            {
                kept += entry.Value;
                found.Add($"{m_Mesh.GetSurfaceAsset(entry.Key)?.name} ({m_BySubMesh[entry.Key].shader?.name}, {entry.Value} triangles)");
            }
            int total = 0;
            for (int i = 0; i < m_Cut.Format.SubMeshes.Length; i++)
                if (!BusGlass.IsGlass(m_Mesh.GetSurfaceAsset(i))) total += m_Cut.Format.SubMeshes[i].IndexCount / 3;
            MeshDescription = found.Count > 0
                ? $"{string.Join(", ", found)}; {total - kept} moving / reader triangles left to the runtime parts, {m_Split?.MovingCorners ?? 0} runtime corners, " +
                  $"{lampFaces} lamp-face triangles hidden while their lamp is lit, " +
                  $"{m_LightCount} light entries{(m_Bones != null ? $", skinned by {m_BoneCount} bones" : "")}; read and cut off the main thread in {m_Cut.WorkerMs} ms, {m_Pieces.Count} piece(s)"
                : $"no opaque surface in {m_Mesh.name} ({m_Mesh.materialCount} surfaces)";
            Describe = MeshDescription;
            if (m_BoneCount == 0) m_Mesh = null;      // kept only for skinning (Pose)
            m_Cut.Dispose();
            m_Cut = null;
            return true;
        }

        private void CreateBuffers()
        {
            if (m_LightCount > 0)
            {
                // the game's renderer: index 0 is the no-lamp entry, then one RGBA (colour, intensity) per declared lamp; all off here
                m_Lights = new ComputeBuffer(m_LightCount, 16, ComputeBufferType.Raw);
                m_Lights.SetData(new Color[m_LightCount]);
            }
            if (m_BoneCount > 0)
            {
                m_Bones = new ComputeBuffer(m_BoneCount * 2, 64, ComputeBufferType.Raw) { name = "CDL cab bones" };
                m_Skins = new float4x4[m_BoneCount];
                for (int b = 0; b < m_BoneCount; b++) m_Skins[b] = float4x4.identity;
                m_Bones.SetData(m_Skins, 0, 0, m_BoneCount);
                m_Bones.SetData(m_Skins, 0, m_BoneCount, m_BoneCount);
            }
        }

        private void MakePiece(NativePiece cut)
        {
            if (!m_BySubMesh.TryGetValue(cut.SubMesh, out Material material))
            {
                NativeMaterialLease lease = NativeMaterialLease.Acquire(m_Mesh.GetSurfaceAsset(cut.SubMesh));
                m_Leases.Add(lease);
                material = PrivateMaterial(lease.Material, m_Bones != null);
                m_Materials.Add(material);
                m_BySubMesh[cut.SubMesh] = material;
                m_Kept[cut.SubMesh] = 0;
            }

            int lamps = NativeCabOwnership.LampsOf(cut.Group);
            var piece = new Piece { Object = new GameObject("Native cab " + cut.SubMesh + (lamps != 0 ? " lamps " + lamps : "")), Lamps = lamps };
            m_Pieces.Add(piece);
            piece.Object.hideFlags = HideFlags.DontSave;
            piece.Object.layer = m_Layer;
            // the imported vertex streams as they are (packed normals and tangents): the model frame is the instance's root
            BusGlass.AttachInModelFrame(piece.Object.transform, m_Parent, m_Parent, null);
            piece.Mesh = NativeGeometryRead.MakeMesh(cut, m_Cut.Format.Meshes[cut.Mesh], m_Mesh.name + " cab");
            piece.Object.AddComponent<MeshFilter>().sharedMesh = piece.Mesh;
            var renderer = piece.Object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            renderer.renderingLayerMask = uint.MaxValue;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            renderer.allowOcclusionWhenDynamic = false;
            var block = new MaterialPropertyBlock();
            if (m_Lights != null)
            {
                block.SetBuffer("_LightInfo", m_Lights);
                block.SetVector("colossal_LightParameters", new Vector4(0f, m_LightCount, 0f, 0f));
                block.SetFloat("colossal_SingleLightsOffset", m_Emissive.GetSingleLightOffset(cut.Mesh));
            }
            block.SetVector("colossal_BuildingState", Vector4.zero);
            if (m_Bones != null)
            {
                block.SetBuffer("_BoneTransforms", m_Bones);
                block.SetVector("colossal_BoneParameters", new Vector4(math.asfloat(0u), math.asfloat((uint)(m_BoneCount - 1)), 0f, 0f));
                block.SetInt("_BonePreviousTransformsByteOffset", m_BoneCount * 64);
                // the bones carry parts beyond the rest mesh's bounds (a trolley pole raised to its wire)
                Bounds bounds = piece.Mesh.bounds;
                bounds.Expand(8f);
                renderer.localBounds = bounds;
            }
            renderer.SetPropertyBlock(block);
            if (!Shows(piece)) piece.Object.SetActive(false);
            m_Kept[cut.SubMesh] += cut.IndexCount / 3;
        }

        /// <summary>
        /// A private copy of the native material: LOD fade full, no VT; procedural animation only when the cab skins the mesh (its buffer is bound then);
        /// the vehicle decal layer, which the game's batches give a vehicle's material (a copy keeps the template's 0, and the pictures the owner sent on
        /// 2 November, the ground's and the buildings' textures on the LCK5120A, were of a copy with 0: the material audit showed these as the only
        /// materials of the bus that take decals).
        /// </summary>
        private static Material PrivateMaterial(Material native, bool skinned)
        {
            var material = new Material(native) { name = native.name + " (CDL cab)", hideFlags = HideFlags.DontSave };
            try
            {
                if (material.HasProperty("colossal_LodFade")) material.SetFloat("colossal_LodFade", 1f);
                if (material.HasProperty(s_DecalLayerMask)) material.SetFloat(s_DecalLayerMask, math.asfloat((int)Game.Rendering.DecalLayers.Vehicles));
                foreach (string name in s_OffKeywords)
                {
                    LocalKeyword keyword = material.shader.keywordSpace.FindKeyword(name);
                    if (keyword.isValid) material.SetKeyword(in keyword, skinned && name == "_GPU_ANIMATION_PROCEDURAL");
                }
                HDMaterial.ValidateMaterial(material);
                // as the game's own RenderPrefabRenderer: a skinned mesh draws its motion vectors (after the validation, which sets the passes)
                if (skinned) material.SetShaderPassEnabled("MOTIONVECTORS", true);
                return material;
            }
            catch (Exception failure)
            {
                CabCleanup.FailedAllocation(material, failure);
                throw;
            }
        }

        /// <summary>
        /// Skins the pieces by the vehicle's own bones this frame (whatever the game or a mod wrote in its Bone buffer): the game's skin
        /// matrices (ProceduralSkeletonSystem.GetSkinMatrices: each bone's model matrix from its parents' and its local transform, times its
        /// bind pose, by bind index), taken relative to the root, so that a bone at rest leaves its triangles where the static cab had them.
        /// Nothing is uploaded when no bone moved; the previous frame's matrices are the same (no motion of the bones in the motion vectors).
        /// A failure is logged once and the pieces stay at rest.
        /// </summary>
        internal void Pose(Unity.Entities.EntityManager entities, Unity.Entities.World world, Unity.Entities.Entity vehicle)
        {
            foreach (BusNativeCab part in m_AdditionalMeshes) part.Pose(entities, world, vehicle);
            if (m_Bones == null || m_PoseFailed) return;
            try
            {
                if (m_MeshEntity == Unity.Entities.Entity.Null)
                    m_MeshEntity = world.GetExistingSystemManaged<PrefabSystem>().GetEntity(m_Mesh);
                if (!entities.HasComponent<PrefabRef>(vehicle) || !entities.HasBuffer<Game.Rendering.Skeleton>(vehicle) || !entities.HasBuffer<Game.Rendering.Bone>(vehicle))
                    return;
                Unity.Entities.DynamicBuffer<SubMesh> subMeshes = entities.GetBuffer<SubMesh>(entities.GetComponentData<PrefabRef>(vehicle).m_Prefab, true);
                Unity.Entities.DynamicBuffer<Game.Rendering.Skeleton> skeletons = entities.GetBuffer<Game.Rendering.Skeleton>(vehicle, true);
                int j = m_MeshSlot;
                if (j >= subMeshes.Length || j >= skeletons.Length || skeletons[j].m_BufferAllocation.Empty) return;
                if (subMeshes[j].m_SubMesh != m_MeshEntity)
                    throw new InvalidOperationException("Native cab render prefab no longer matches its skeleton slot.");
                Game.Rendering.Skeleton skeleton = skeletons[j];
                Unity.Entities.DynamicBuffer<ProceduralBone> procedural = entities.GetBuffer<ProceduralBone>(m_MeshEntity, true);
                Unity.Entities.DynamicBuffer<Game.Rendering.Bone> bones = entities.GetBuffer<Game.Rendering.Bone>(vehicle, true);
                int n = procedural.Length;
                if (n != m_Skins.Length || skeleton.m_BoneOffset < 0 || skeleton.m_BoneOffset + n > bones.Length)
                    throw new InvalidOperationException($"the mesh has {n} procedural bones, the cab {m_Skins.Length}, the vehicle's buffer {bones.Length} from {skeleton.m_BoneOffset}");
                if (m_Models == null) m_Models = new float4x4[n];
                float4x4 rootInverse = float4x4.identity;
                bool moved = false;
                for (int i = 0; i < n; i++)
                {
                    ProceduralBone p = procedural[i];
                    Game.Rendering.Bone bone = bones[skeleton.m_BoneOffset + i];
                    float4x4 local = float4x4.TRS(bone.m_Position, bone.m_Rotation, bone.m_Scale);
                    m_Models[i] = p.m_ParentIndex >= 0 ? math.mul(m_Models[p.m_ParentIndex], local) : local;
                    if (i == 0 && p.m_ParentIndex < 0) rootInverse = math.inverse(m_Models[0]);
                    float4x4 skin = math.mul(rootInverse, math.mul(m_Models[i], p.m_BindPose));
                    if (!moved && !skin.Equals(m_Skins[p.m_BindIndex])) moved = true;
                    m_Skins[p.m_BindIndex] = skin;
                }
                if (!moved) return;
                m_Bones.SetData(m_Skins, 0, 0, n);
                m_Bones.SetData(m_Skins, 0, n, n);
            }
            catch (Exception error)
            {
                m_PoseFailed = true;
                Mod.log.Warn($"Native cab skinning stopped, the pieces stay at rest: {error.GetType().Name}: {error.Message}");
            }
        }

        internal bool Visible
        {
            set
            {
                foreach (BusNativeCab part in m_AdditionalMeshes) part.Visible = value;
                if (m_Visible == value) return;
                m_Visible = value;
                ApplyVisibility();
            }
        }

        /// <summary>
        /// The lamp parts the runtime mesh shows lit this frame (bits of NativeCabOwnership.LampBit): the asset's unlit copy of a
        /// face is hidden while any lamp part it copies is lit, so a lit face is not drawn over its unlit copy at the same depth.
        /// </summary>
        internal int LitLamps
        {
            set
            {
                foreach (BusNativeCab part in m_AdditionalMeshes) part.LitLamps = value;
                if (m_LitLamps == value) return;
                m_LitLamps = value;
                ApplyVisibility();
            }
        }

        private bool Shows(Piece piece) => m_Visible && (piece.Lamps & m_LitLamps) == 0;

        private void ApplyVisibility()
        {
            foreach (Piece piece in m_Pieces)
                if (piece.Object != null && piece.Object.activeSelf != Shows(piece))
                    piece.Object.SetActive(Shows(piece));
        }

        public void Dispose()
        {
            Ready = false;
            var failures = new List<Exception>();
            if (m_Cut != null && CabCleanup.Try(m_Cut.Dispose, failures)) m_Cut = null;
            for (int i = m_AdditionalMeshes.Count - 1; i >= 0; i--)
                if (CabCleanup.Try(m_AdditionalMeshes[i].Dispose, failures)) m_AdditionalMeshes.RemoveAt(i);
            for (int i = m_Pieces.Count - 1; i >= 0; i--)
            {
                Piece piece = m_Pieces[i];
                try
                {
                    bool detached = true;
                    if (piece.Object != null)
                    {
                        detached &= CabCleanup.Try(() => piece.Object.SetActive(false), failures);
                        MeshRenderer renderer = piece.Object.GetComponent<MeshRenderer>();
                        if (renderer != null)
                        {
                            detached &= CabCleanup.Try(() => renderer.enabled = false, failures);
                            detached &= CabCleanup.Try(() => renderer.sharedMaterials = Array.Empty<Material>(), failures);
                        }
                        MeshFilter filter = piece.Object.GetComponent<MeshFilter>();
                        if (filter != null) detached &= CabCleanup.Try(() => filter.sharedMesh = null, failures);
                    }
                    if (!detached) continue;
                    if (piece.Mesh != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(piece.Mesh), failures)) piece.Mesh = null;
                    if (piece.Object != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(piece.Object), failures)) piece.Object = null;
                    if (piece.Mesh == null && piece.Object == null) m_Pieces.RemoveAt(i);
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (m_Pieces.Count == 0)
            {
                for (int i = m_Materials.Count - 1; i >= 0; i--)
                    if (CabCleanup.Try(() => UnityEngine.Object.Destroy(m_Materials[i]), failures)) m_Materials.RemoveAt(i);
                for (int i = m_Leases.Count - 1; i >= 0; i--)
                    if (CabCleanup.Try(m_Leases[i].Dispose, failures)) m_Leases.RemoveAt(i);
                if (m_Lights != null && CabCleanup.Try(m_Lights.Dispose, failures)) m_Lights = null;
                if (m_Bones != null && CabCleanup.Try(m_Bones.Dispose, failures)) m_Bones = null;
            }
            if (failures.Count != 0)
            {
                if (!m_RetainedForCleanup) { m_RetainedForCleanup = true; CabCleanup.Retain(this); }
                CabCleanup.ThrowIfAny(failures, "Native cab cleanup failed.");
            }
            Describe = "none";
        }
    }
}
