using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Colossal.Entities;
using Colossal.IO.AssetDatabase.VirtualTexturing;
using Colossal.Mathematics;
using Game.Citizens;
using Game.Creatures;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using CAnimationClip = Colossal.Animations.AnimationClip;
using ObjectState = Game.Objects.ObjectState;
using BlendWeights = Game.Rendering.BlendWeights;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The game's own citizens as the bus's passengers and driver (option "Game citizens"): each passenger looks as the game made
    /// that resident (the creature's mesh group: its character style, clothes, hair, skin and the mask that hides the skin under the
    /// clothes), or, for the figures that only make up the load and when that is unknown, a character of the right age and gender
    /// from the game's human prefabs (summer or winter clothes by the temperature). The meshes and materials are the game's, drawn
    /// through its GPU skinning with bone and meta buffers of our own, bound per renderer (never the game's global buffers); the
    /// poses are made on the CPU from the game's clips (CitizenSkeleton): seated from the bench-sitting loop, standing from the
    /// idle loop, holding a strap and the driver at the wheel with the arm IK on top (the game has no clips for either). The
    /// virtual textures (the clothes) are asked for every frame for the figures shown: the game streams them for its own cameras'
    /// views only. Anything unexpected switches it off for the session and the simple figures (BusRiders) take over.
    /// Cost: each figure has the game's level-of-detail meshes of its parts under a LODGroup (the full meshes only close to the
    /// camera, the game's lighter ones further away, nothing when it is tiny on screen); one shadow caster per figure, the
    /// body's lightest mesh drawn into the shadow maps only (the parts themselves cast none: a figure's ten parts each casting
    /// into every cascade were most of its draw calls); the mouth (behind closed lips) is not drawn; the culling bounds hold
    /// every pose.
    /// </summary>
    internal sealed partial class BusCitizens : IDisposable
    {
        internal enum Pose { Seated, Standing, Holding, Driver }

        private const int kSeated = 4, kStanding = 3;                 // pose variants: frames of the loops
        private const float kHipOverCushion = 0.11f, kHipBehind = -0.08f;  // the seated pelvis over the slot's cushion point (BusRiders' seated frame)
        private const float kColdBelow = 12f;                           // °C: winter clothes below
        // The full meshes and the game's first lighter level only: its lower ones are built for a figure a few pixels tall walking
        // and tear in these poses (the arms).
        private const int kMaxLevels = 2;
        // LODGroup: the screen height (share of the view) under which the next level shows; under the last one, nothing (a figure
        // about 80 m away).
        private static readonly float[] kLevelHeights = { 0.40f };
        private const float kCullHeight = 0.02f;
        private const int kTinyVertices = 400;                           // eyes, eyelashes: only at the full level
        private const float kRequestSeconds = 0.25f;
        // Every pose fits in this (the body's frame: the rest pose's model space; a seated figure's knees forward, a hand up on a strap).
        private static readonly Bounds kPoseBounds = new Bounds(new Vector3(0f, 0.95f, 0.15f), new Vector3(1.5f, 2.1f, 1.5f));

        // For the developer benchmark (BusCitizenProbe): the levels of detail and the trimmed shadows can be switched off.
        internal static bool s_Levels = true, s_TrimShadows = true;
        // For the developer probe: only the shadow caster, drawn visibly (to see that the whole body casts).
        internal static bool s_ShowShadowCaster;
        // For the developer probe: one level of detail only (no LODGroup); -1: all.
        internal static int s_OnlyLevel = -1;
        private static readonly float3 kPoleRight = new float3(1f, -0.6f, -0.4f), kPoleLeft = new float3(-1f, -0.6f, -0.4f);

        private sealed class StyleData
        {
            internal bool Ready;
            internal CitizenSkeleton Skeleton;
            internal ComputeBuffer Bones;
            internal float3[] Offset;                  // per pose: the root's offset in the slot's frame
            internal int Seated0, Standing0, Holding, Driver;     // Driver: the rest pose, the middle of DriverFirst's DriverHands.PoseCount poses
            internal int DriverFirst = -1;
        }

        private sealed class PartData
        {
            internal bool Ready;
            internal RenderPrefab Prefab;
            internal Mesh[] Meshes;
            internal Material[][] Materials;           // per mesh, its sub-meshes' materials (our clones)
            internal readonly List<(int stack, int texture)> Pages = new List<(int, int)>();
            internal readonly List<Material> Clones = new List<Material>();
        }

        private struct Appearance
        {
            internal CharacterStyle Style;
            internal RenderPrefab[] Parts;
            internal BlendWeights Texture, Mask;
        }

        /// <summary>What a driver's figure needs to change its pose while it is alive: the bone buffer's layout, its meta entries, the renderers.</summary>
        internal sealed class HandsRig
        {
            internal ComputeBuffer Bones;
            internal int BoneCount, First, Pose;
            internal MetaBufferData Meta, ShadowMeta;
            internal List<Renderer> Renderers = new List<Renderer>(), ShadowRenderers = new List<Renderer>();
            internal DriverHands.State State;
            internal bool Failed;
        }

        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_Prefabs;
        private readonly EntityQuery m_Humans;
        private readonly Dictionary<CharacterStyle, StyleData> m_Styles = new Dictionary<CharacterStyle, StyleData>();
        private readonly Dictionary<RenderPrefab, PartData> m_Parts = new Dictionary<RenderPrefab, PartData>();
        private readonly Dictionary<string, HumanPrefab> m_HumanPrefabs = new Dictionary<string, HumanPrefab>();
        private readonly HashSet<(int, int)> m_Requested = new HashSet<(int, int)>();
        private TextureStreamingSystem m_Streaming;
        private float m_NextRequest;
        private ClimateSystem m_Climate;
        private bool m_Failed;

        /// <summary>Developer trace (BusCitizenProbe): the poses' joints as they are made.</summary>
        internal Action<string> Trace;
        // The developer probes make every figure at once: a style is posed on the calling thread (the cab waits for the worker).
        internal bool Synchronous;

        internal BusCitizens(EntityManager entityManager, PrefabSystem prefabs, EntityQuery humans)
        {
            m_EntityManager = entityManager;
            m_Prefabs = prefabs;
            m_Humans = humans;
        }

        /// <summary>Off for the session after repeated failures (the simple figures show instead).</summary>
        internal bool Failed => m_Failed;

        /// <summary>A figure could not be made just now (a model still loading): the caller tries again after this (unscaled time).</summary>
        internal float RetryAt => m_RetryAt;

        private const float kRetrySeconds = 1.5f;
        private const int kMaxErrors = 12;
        private float m_RetryAt;
        private int m_Errors, m_TransientLogged;

        private static bool Transient(Exception ex) => ex is BusGlass.GeometryBusyException ||
            ex.Message != null && ex.Message.IndexOf("currently running", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// A figure of the passenger (Entity.Null: anyone, by the variety number) in a pose at a rider slot, under the section's
        /// root; null when it cannot be made (the caller shows a simple figure).
        /// </summary>
        internal GameObject Make(Transform parent, BusInterior.RiderSlot slot, Pose pose, Entity person, int variety)
        {
            if (m_DisposeStarted || m_Failed || parent == null || Time.unscaledTime < m_RetryAt)
                return null;
            GameObject root = null;
            GameObject pendingChild = null;
            ComputeBuffer meta = null;
            Figure figure = null;
            try
            {
                if (!TryAppearance(person, variety, pose == Pose.Driver, out Appearance look))
                    return null;
                StyleData style = Style(look.Style);
                if (style == null)
                    return null;

                int p = PoseIndex(style, pose, variety);
                root = new GameObject("citizen " + look.Style.name) { hideFlags = HideFlags.DontSave, layer = parent.gameObject.layer };
                root.SetActive(false);
                figure = root.AddComponent<Figure>();
                OwnFigure(figure);
                figure.Pages = new List<(int, int)>();
                root.transform.SetParent(parent, false);
                root.transform.localPosition = new Vector3(slot.Position.x, slot.Position.y, slot.Position.z);
                root.transform.localRotation = Quaternion.Euler(0f, slot.Yaw * Mathf.Rad2Deg, 0f);
                pendingChild = new GameObject("pose") { hideFlags = HideFlags.DontSave, layer = root.layer };
                pendingChild.transform.SetParent(root.transform, false);
                GameObject body = pendingChild;
                pendingChild = null;
                body.transform.localPosition = new Vector3(style.Offset[p].x, style.Offset[p].y, style.Offset[p].z);

                meta = new ComputeBuffer(1, UnsafeUtility.SizeOf<MetaBufferData>(), ComputeBufferType.Structured);
                figure.Meta = meta;
                var metaData = new MetaBufferData
                {
                    m_BoneOffset = p * style.Skeleton.Count, m_BoneCount = style.Skeleton.Count, m_ShapeCount = 0, m_MetaIndexLink = -1, m_BoneLink = -1,
                    m_TextureWeights = look.Texture, m_MaskWeights = look.Mask, m_OverlayColors1 = White(),
                };
                meta.SetData(new[] { metaData });
                var shadowData = new MetaBufferData();
                MaterialPropertyBlock block = Block(style, p, meta);
                // the parts' levels of detail: level l draws each part's l-th LOD mesh (its last one when it has fewer; the tiny
                // ones, eyes and eyelashes, only at the full level)
                var chains = new List<RenderPrefab[]>();
                int levels = 1;
                foreach (RenderPrefab prefab in look.Parts)
                {
                    if (prefab == null)
                        continue;
                    RenderPrefab[] chain = Chain(prefab);
                    chains.Add(chain);
                    levels = Math.Max(levels, chain.Length);
                }
                // the shadow caster: the body's lightest mesh, at every level
                List<Renderer> shadow = null;
                if (s_TrimShadows)
                {
                    // its own meta entry without the clothing mask: the skin shader cuts out the skin under the clothes by it
                    figure.ShadowMeta = new ComputeBuffer(1, UnsafeUtility.SizeOf<MetaBufferData>(), ComputeBufferType.Structured);
                    shadowData = new MetaBufferData
                    {
                        m_BoneOffset = p * style.Skeleton.Count, m_BoneCount = style.Skeleton.Count, m_ShapeCount = 0, m_MetaIndexLink = -1, m_BoneLink = -1,
                        m_TextureWeights = look.Texture, m_OverlayColors1 = White(),
                    };
                    figure.ShadowMeta.SetData(new[] { shadowData });
                    shadow = ShadowCaster(body.transform, look.Parts, Block(style, p, figure.ShadowMeta));
                }
                int from = 0;
                if (s_OnlyLevel >= 0)
                {
                    from = Math.Min(s_OnlyLevel, levels - 1);
                    levels = from + 1;
                }
                var renderers = new List<Renderer>[levels];
                for (int l = 0; l < from; l++)
                    renderers[l] = new List<Renderer>();
                for (int l = from; l < levels; l++)
                {
                    renderers[l] = new List<Renderer>();
                    Transform holder = body.transform;
                    if (levels - from > 1)
                    {
                        var level = new GameObject("lod" + l) { hideFlags = HideFlags.DontSave, layer = root.layer };
                        level.transform.SetParent(body.transform, false);
                        holder = level.transform;
                    }
                    foreach (RenderPrefab[] chain in chains)
                    {
                        bool reused = l >= chain.Length;
                        PartData part = Part(chain[Math.Min(l, chain.Length - 1)]);
                        if (part == null)
                            continue;
                        if (!reused)
                            figure.Pages.AddRange(part.Pages);
                        for (int m = 0; m < part.Meshes.Length; m++)
                        {
                            Mesh mesh = part.Meshes[m];
                            if (mesh == null || s_ShowShadowCaster || (l > 0 && (reused || chain.Length == 1) && mesh.vertexCount < kTinyVertices) || (s_TrimShadows && Mouth(part.Materials[m])))
                                continue;
                            renderers[l].Add(Renderer(holder, mesh, part.Materials[m], block, s_TrimShadows ? ShadowCastingMode.Off : ShadowCastingMode.On));
                            if (Trace != null && s_OnlyLevel >= 0)
                                Trace($"level {l} {part.Prefab.name}/{m}: {mesh.vertexCount} v, {string.Join(",", Array.ConvertAll(mesh.GetVertexAttributes(), a => $"{a.attribute}:{a.format}x{a.dimension}"))}");
                        }
                    }
                }
                // A driver's hands follow the wheel: the figure keeps what it takes to change its pose (the main renderers, before the
                // shadow caster joins every level's list).
                if (pose == Pose.Driver && style.DriverFirst >= 0)
                {
                    figure.Hands = new HandsRig { Bones = style.Bones, BoneCount = style.Skeleton.Count, First = style.DriverFirst, Pose = p, Meta = metaData, ShadowMeta = shadowData };
                    foreach (List<Renderer> level in renderers)
                        if (level != null)
                            foreach (Renderer r in level)
                                if (!figure.Hands.Renderers.Contains(r))
                                    figure.Hands.Renderers.Add(r);
                    if (shadow != null)
                        figure.Hands.ShadowRenderers.AddRange(shadow);
                }
                if (shadow != null)
                    foreach (List<Renderer> level in renderers)
                        level?.AddRange(shadow);
                if (levels - from > 1)
                {
                    var lods = new LOD[levels];
                    for (int l = 0; l < levels; l++)
                        lods[l] = new LOD(l == levels - 1 ? kCullHeight : kLevelHeights[Math.Min(l, kLevelHeights.Length - 1)], renderers[l].ToArray());
                    LODGroup group = body.AddComponent<LODGroup>();
                    group.fadeMode = LODFadeMode.None;
                    group.SetLODs(lods);
                    group.localReferencePoint = kPoseBounds.center;
                    group.size = 1.9f;
                }
                root.SetActive(true);
                return root;
            }
            catch (Exception ex)
            {
                var cleanupFailures = new List<Exception>();
                if (pendingChild != null && !CabCleanup.Try(() => UnityEngine.Object.Destroy(pendingChild), cleanupFailures)) CabCleanup.Retain(pendingChild);
                if (figure != null)
                {
                    if (!CabCleanup.Try(figure.Dispose, cleanupFailures)) CabCleanup.Retain((IDisposable)figure);
                }
                else if (root != null && !CabCleanup.Try(() => UnityEngine.Object.Destroy(root), cleanupFailures)) CabCleanup.Retain(root);
                if (cleanupFailures.Count != 0)
                {
                    cleanupFailures.Insert(0, ex);
                    throw new AggregateException("Citizen allocation and cleanup failed.", cleanupFailures);
                }

                // the game is still loading one of the models (it streams them in): try again shortly; anything else counts,
                // and only errors that keep coming switch the game citizens off
                m_RetryAt = Time.unscaledTime + kRetrySeconds;
                if (!Transient(ex) && ++m_Errors >= kMaxErrors)
                    Fail("making a figure", ex);
                else if (m_Errors <= 3 || Transient(ex) && m_TransientLogged++ < 3)
                    Mod.log.Info($"Game citizens: a figure waits ({ex.GetType().Name}: {ex.Message}); trying again in {kRetrySeconds:0} s.");
                return null;
            }
        }

        /// <summary>
        /// The driver's hands for the wheel's turn (deg, clockwise): the pose with the hands on the rim at the places DriverHands says.
        /// A figure that is not a game citizen with the baked poses is left alone; anything unexpected switches the hands off for
        /// that figure (it stays in its last pose), the rest of the figure carries on.
        /// </summary>
        internal void DriveHands(GameObject figure, float wheelDeg, float dt)
        {
            if (m_DisposeStarted) return;
            Figure f = figure != null ? figure.GetComponent<Figure>() : null;
            HandsRig rig = f != null ? f.Hands : null;
            if (rig == null || rig.Failed || f.Meta == null)
                return;
            try
            {
                DriverHands.Step(ref rig.State, wheelDeg, dt);
                int pose = rig.First + DriverHands.PoseOffset(DriverHands.Index(rig.State.Left), DriverHands.Index(rig.State.Right));
                if (pose == rig.Pose)
                    return;
                rig.Pose = pose;
                rig.Meta.m_BoneOffset = pose * rig.BoneCount;
                rig.ShadowMeta.m_BoneOffset = pose * rig.BoneCount;
                f.Meta.SetData(new[] { rig.Meta });
                MaterialPropertyBlock block = Block(rig.Bones, rig.BoneCount, pose, f.Meta);
                foreach (Renderer r in rig.Renderers)
                    if (r != null)
                        r.SetPropertyBlock(block);
                if (f.ShadowMeta != null)
                {
                    f.ShadowMeta.SetData(new[] { rig.ShadowMeta });
                    MaterialPropertyBlock shadowBlock = Block(rig.Bones, rig.BoneCount, pose, f.ShadowMeta);
                    foreach (Renderer r in rig.ShadowRenderers)
                        if (r != null)
                            r.SetPropertyBlock(shadowBlock);
                }
            }
            catch (Exception ex)
            {
                rig.Failed = true;
                Mod.log.Warn($"Game citizens: the driver's hands could not follow the wheel ({ex.GetType().Name}: {ex.Message}); they stay where they are.");
            }
        }

        /// <summary>A few times a second: asks for the virtual textures of the figures shown (bigger in the driver's view).</summary>
        internal void RequestTextures(float pixels)
        {
            if (m_DisposeStarted || m_Failed || m_Figures.Count == 0)
                return;
            float now = Time.unscaledTime;
            if (now < m_NextRequest)
                return;
            m_NextRequest = now + kRequestSeconds;
            try
            {
                if (m_Streaming == null)
                    m_Streaming = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<TextureStreamingSystem>();
                if (m_Streaming == null)
                    return;
                m_Requested.Clear();
                var whole = new Bounds2(new float2(0f, 0f), new float2(1f, 1f));
                foreach (Figure f in m_Figures)
                {
                    if (f == null || !f.isActiveAndEnabled || f.Pages == null)
                        continue;
                    foreach (var page in f.Pages)
                        if (m_Requested.Add(page))
                            m_Streaming.RequestRegion(page.stack, page.texture, pixels, whole);
                }
            }
            catch (Exception ex)
            {
                Fail("streaming textures", ex);
            }
        }

        // A renderer of a figure's part: the game's material copies with the figure's buffers.
        private static MeshRenderer Renderer(Transform holder, Mesh mesh, Material[] materials, MaterialPropertyBlock block, ShadowCastingMode shadows)
        {
            var go = new GameObject(mesh.name) { hideFlags = HideFlags.DontSave, layer = holder.gameObject.layer };
            go.transform.SetParent(holder, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = materials;
            r.shadowCastingMode = shadows;
            r.receiveShadows = true;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            r.renderingLayerMask = uint.MaxValue;
            r.lightProbeUsage = LightProbeUsage.BlendProbes;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            r.localBounds = kPoseBounds;
            r.SetPropertyBlock(block);
            return r;
        }

        // The figure's buffers and its pose's bones: the vertex shader takes the bone range from colossal_BoneParameters (as the
        // game's batches set it from an Animated's bone allocation: first and last index, the bits in floats), not from the meta entry.
        private static MaterialPropertyBlock Block(StyleData style, int pose, ComputeBuffer meta) => Block(style.Bones, style.Skeleton.Count, pose, meta);

        private static MaterialPropertyBlock Block(ComputeBuffer bones, int boneCount, int pose, ComputeBuffer meta)
        {
            var block = new MaterialPropertyBlock();
            block.SetBuffer("boneBuffer", bones);
            block.SetBuffer("boneHistoryBuffer", bones);
            block.SetBuffer("metaBuffer", meta);
            block.SetFloat("colossal_MetaParameters", 0f);
            uint first = (uint)(pose * boneCount), last = first + (uint)boneCount - 1u;
            block.SetVector("colossal_BoneParameters", new Vector4(math.asfloat(first), math.asfloat(last), 0f, 0f));
            block.SetVector("colossal_ShapeParameters1", Vector4.zero);
            block.SetVector("colossal_ShapeParameters2", Vector4.zero);
            block.SetVector("colossal_OverlayParameters", Vector4.zero);
            block.SetInt("colossal_CullVertices", 0);
            return block;
        }

        private static bool Mouth(Material[] materials)
        {
            foreach (Material m in materials)
                if (m != null && m.shader != null && m.shader.name.Contains("CharacterMouth"))
                    return true;
            return false;
        }

        // The figure's shadow: the body part's lighter level (its body and head, not the eyes), drawn into the shadow maps only.
        // Null when the character has no body part (then no shadow).
        private List<Renderer> ShadowCaster(Transform holder, RenderPrefab[] parts, MaterialPropertyBlock block)
        {
            foreach (RenderPrefab prefab in parts)
            {
                if (prefab == null || prefab.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) < 0)      // Female_body, Male_body ...
                    continue;
                RenderPrefab[] chain = Chain(prefab);
                PartData part = Part(chain[chain.Length - 1]);
                if (part == null)
                    return null;
                var casters = new List<Renderer>();
                for (int m = 0; m < part.Meshes.Length; m++)
                    if (part.Meshes[m] != null && part.Meshes[m].vertexCount >= kTinyVertices)
                        casters.Add(Renderer(holder, part.Meshes[m], part.Materials[m], block, s_ShowShadowCaster ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly));
                return casters;
            }
            return null;
        }

        // A part's levels of detail: itself, then the game's lighter meshes for it (none when switched off for the benchmark).
        private static RenderPrefab[] Chain(RenderPrefab prefab)
        {
            if (!s_Levels || !prefab.TryGet(out LodProperties lod) || lod.m_LodMeshes == null || lod.m_LodMeshes.Length == 0)
                return new[] { prefab };
            var chain = new List<RenderPrefab> { prefab };
            foreach (RenderPrefab level in lod.m_LodMeshes)
                if (level != null && chain.Count < kMaxLevels)
                    chain.Add(level);
            return chain.ToArray();
        }

        // ---------------------------------------------------------------- appearance

        private bool TryAppearance(Entity person, int variety, bool driver, out Appearance look)
        {
            look = default;
            // as the game made this resident: the creature's mesh group in its prefab's characters
            if (person != Entity.Null && m_EntityManager.TryGetComponent(person, out PrefabRef prefabRef) &&
                m_EntityManager.TryGetBuffer(person, true, out DynamicBuffer<MeshGroup> groups) && groups.Length > 0 &&
                m_EntityManager.TryGetBuffer(prefabRef.m_Prefab, true, out DynamicBuffer<SubMeshGroup> subGroups) &&
                m_EntityManager.TryGetBuffer(prefabRef.m_Prefab, true, out DynamicBuffer<CharacterElement> characters) &&
                m_EntityManager.TryGetBuffer(prefabRef.m_Prefab, true, out DynamicBuffer<SubMesh> subMeshes))
            {
                int g = groups[0].m_SubMeshGroup;
                if (g < subGroups.Length && g < characters.Length &&
                    m_Prefabs.TryGetPrefab(characters[g].m_Style, out PrefabBase stylePrefab) && stylePrefab is CharacterStyle style)
                {
                    int2 range = subGroups[g].m_SubMeshRange;
                    var parts = new List<RenderPrefab>();
                    for (int k = range.x; k < range.y && k < subMeshes.Length; k++)
                        if (m_Prefabs.TryGetPrefab(subMeshes[k].m_SubMesh, out PrefabBase mesh) && mesh is RenderPrefab render)
                            parts.Add(render);
                    if (parts.Count > 0)
                    {
                        look = new Appearance { Style = style, Parts = parts.ToArray(), Texture = characters[g].m_TextureWeights, Mask = characters[g].m_MaskWeights };
                        return true;
                    }
                }
            }

            // a character of the right age and gender (anyone, and the driver: an adult, a man or a woman by the variety number)
            CitizenAge age = CitizenAge.Adult;
            bool male = (variety & 1) == 0;
            if (!driver && person != Entity.Null && m_EntityManager.TryGetComponent(person, out Game.Creatures.Resident resident) &&
                m_EntityManager.TryGetComponent(resident.m_Citizen, out Citizen citizen))
            {
                age = citizen.GetAge();
                male = (citizen.m_State & CitizenFlags.Male) != 0;
            }
            CharacterGroup.Character character = Character(age, male, variety);
            if (character?.m_Style == null || character.m_MeshPrefabs == null)
                return false;
            look = new Appearance
            {
                Style = character.m_Style, Parts = character.m_MeshPrefabs,
                Texture = RenderingUtils.GetBlendWeights(character.m_Meta.textureWeights), Mask = RenderingUtils.GetBlendWeights(character.m_Meta.maskWeights),
            };
            return true;
        }

        private CharacterGroup.Character Character(CitizenAge age, bool male, int variety)
        {
            string name = $"{(age == CitizenAge.Child ? "Child" : age == CitizenAge.Teen ? "Teen" : age == CitizenAge.Elderly ? "Elderly" : "Adult")}_{(male ? "Male" : "Female")}";
            HumanPrefab human = Human(name);
            if (human?.m_Meshes == null)
                return null;
            if (m_Climate == null)
                m_Climate = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<ClimateSystem>();
            ObjectState season = m_Climate != null && m_Climate.temperature < kColdBelow ? ObjectState.Cold : ObjectState.Warm;
            GenderMask gender = male ? GenderMask.Male : GenderMask.Female;
            foreach (ObjectMeshInfo info in human.m_Meshes)
            {
                if (info.m_RequireState != season || !(info.m_Mesh is CharacterGroup group) || group.m_Characters == null)
                    continue;
                var fitting = new List<CharacterGroup.Character>();
                foreach (CharacterGroup.Character c in group.m_Characters)
                    if (c?.m_Style != null && (c.m_Style.m_Gender & gender) != 0)
                        fitting.Add(c);
                if (fitting.Count > 0)
                    return fitting[(int)((uint)variety % (uint)fitting.Count)];
            }
            return null;
        }

        private HumanPrefab Human(string name)
        {
            if (m_HumanPrefabs.Count == 0)
            {
                using (NativeArray<Entity> list = m_Humans.ToEntityArray(Allocator.Temp))
                    foreach (Entity e in list)
                        if (m_Prefabs.TryGetPrefab(e, out PrefabBase p) && p is HumanPrefab h)
                            m_HumanPrefabs[h.name] = h;
            }
            return m_HumanPrefabs.TryGetValue(name, out HumanPrefab human) ? human : null;
        }

        // ---------------------------------------------------------------- poses

        private static int PoseIndex(StyleData s, Pose pose, int variety)
        {
            uint v = (uint)variety;
            switch (pose)
            {
                case Pose.Seated: return s.Seated0 + (int)(v % kSeated);
                case Pose.Holding: return s.Holding;
                case Pose.Driver: return s.Driver;
                default: return s.Standing0 + (int)(v % kStanding);
            }
        }

        // A style's poses being made on a worker thread (Poses), with the skeleton they are for and the main thread's share so far.
        private readonly Dictionary<CharacterStyle, (System.Threading.Tasks.Task<StylePoses> task, CitizenSkeleton skeleton, long mainTicks)> m_StyleWork =
            new Dictionary<CharacterStyle, (System.Threading.Tasks.Task<StylePoses>, CitizenSkeleton, long)>();
        private const float kStyleWaitSeconds = 0.1f;

        // A style's poses as plain data (any thread): the bone buffer's elements and where each pose's root goes.
        private sealed class StylePoses
        {
            internal BoneElement[] All;
            internal float3[] Offset;
            internal int Seated0, Standing0, Holding, Driver, DriverFirst, Count;
            internal double Ms;
        }

        /// <summary>
        /// A style's poses into one bone buffer: seated (frames of the bench-sitting loop), standing (frames of the idle loop),
        /// holding a strap (idle, the right hand on the strap) and the driver (seated, the hands on the wheel); and where each
        /// pose's root goes in the slot's frame (seated: the pelvis over the cushion point; standing: the feet on the floor).
        /// The first figure of a style used to make all of them in its frame (the log of 21 October: riders 117 ms at a take-over,
        /// 91 ms while passengers boarded): the clips are taken on the main thread, the poses made on a worker thread, the buffer
        /// uploaded when they are there. Null meanwhile, with RetryAt a moment later (the place waits for its citizen), and for a
        /// style that cannot be used (simple figures for it).
        /// </summary>
        private StyleData Style(CharacterStyle style)
        {
            if (m_Styles.TryGetValue(style, out StyleData known))
            {
                if (known == null || known.Ready) return known;
                ReleaseStyle(style, known);
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (m_StyleWork.TryGetValue(style, out var work))
            {
                if (!work.task.IsCompleted)
                {
                    WaitForStyle();
                    return null;
                }
                m_StyleWork.Remove(style);
                if (work.task.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || work.task.Result == null)
                {
                    m_Styles[style] = null;
                    Mod.log.Warn($"Game citizens: the poses of {style.name} could not be made ({work.task.Exception?.InnerException?.Message ?? "no result"}); simple figures for it.");
                    return null;
                }
                return Upload(style, work.skeleton, work.task.Result, work.mainTicks + (System.Diagnostics.Stopwatch.GetTimestamp() - t0));
            }

            CitizenSkeleton skeleton = CitizenSkeleton.Load(style);
            CAnimationClip bench = CitizenSkeleton.LoadClip(style, "Bench_Sitting");
            CAnimationClip idle = CitizenSkeleton.LoadClip(style, "Idle");
            if (skeleton == null || bench == null || idle == null)
            {
                m_Styles[style] = null;
                Mod.log.Warn($"Game citizens: the style {style.name} has no {(skeleton == null ? "rest pose" : bench == null ? "Bench_Sitting" : "Idle")} clip; simple figures for it.");
                return null;
            }

            if (Trace != null || Synchronous)
            {
                // the developer probes: on this thread (one of them traces the driver's joints as they are made)
                StylePoses now = Poses(skeleton, bench, idle);
                return Upload(style, skeleton, now, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            }

            m_StyleWork[style] = (System.Threading.Tasks.Task.Run(() => Poses(skeleton, bench, idle)), skeleton, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            WaitForStyle();
            return null;
        }

        private void WaitForStyle() => m_RetryAt = Math.Max(m_RetryAt, Time.unscaledTime + kStyleWaitSeconds);

        // The poses themselves (any thread: the skeleton and the clips are only read).
        private StylePoses Poses(CitizenSkeleton skeleton, CAnimationClip bench, CAnimationClip idle)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = skeleton.Count;
            var poses = new List<(BoneElement[] bones, float3 offset)>();
            var pos = new float3[n];
            var rot = new quaternion[n];

            float3 SeatOffset()
            {
                float3 pelvis = skeleton.Globals(pos, rot)[CitizenSkeleton.kPelvis].c3.xyz;
                return new float3(-pelvis.x, kHipOverCushion - pelvis.y, kHipBehind - pelvis.z);
            }

            void Add(float3 offset)
            {
                var bones = new BoneElement[n];
                skeleton.Skin(skeleton.Globals(pos, rot), bones);
                poses.Add((bones, offset));
            }

            var data = new StylePoses();
            int benchFrames = Math.Max(1, bench.m_Animation.frameCount), idleFrames = Math.Max(1, idle.m_Animation.frameCount);
            data.Seated0 = poses.Count;
            for (int i = 0; i < kSeated; i++)
            {
                skeleton.Local(bench, benchFrames * (2 * i + 1) / (2 * kSeated), pos, rot);
                Add(SeatOffset());
            }
            data.Standing0 = poses.Count;
            for (int i = 0; i < kStanding; i++)
            {
                skeleton.Local(idle, idleFrames * (2 * i + 1) / (2 * kStanding), pos, rot);
                Add(float3.zero);
            }
            data.Holding = poses.Count;
            skeleton.Local(idle, 0, pos, rot);
            skeleton.ReachArm(pos, rot, CitizenSkeleton.kRightUpperArm, BusRiders.HoldingHand, kPoleRight);
            Add(float3.zero);
            // The driver: seated, a hand on each side of the rim, one pose for every pair of places the hands can hold it at
            // (DriverHands: they take the rim again a little back as the wheel turns). The pair (rest, rest) is the rest pose.
            data.DriverFirst = poses.Count;
            skeleton.Local(bench, 0, pos, rot);
            float3 seat = SeatOffset();
            for (int left = 0; left < DriverHands.Steps; left++)
                for (int right = 0; right < DriverHands.Steps; right++)
                {
                    skeleton.Local(bench, 0, pos, rot);
                    float3 handL = DriverHands.Rim(true, DriverHands.Angle(left)) - seat, handR = DriverHands.Rim(false, DriverHands.Angle(right)) - seat;
                    bool rest = left == DriverHands.Half && right == DriverHands.Half;
                    if (rest)
                        Joints("driver before", skeleton, pos, rot);
                    skeleton.ReachArm(pos, rot, CitizenSkeleton.kLeftUpperArm, handL, kPoleLeft);
                    skeleton.ReachArm(pos, rot, CitizenSkeleton.kRightUpperArm, handR, kPoleRight);
                    if (rest)
                        Joints($"driver after (targets {handL}, {handR})", skeleton, pos, rot);
                    Add(seat);
                }
            data.Driver = data.DriverFirst + DriverHands.RestOffset;

            data.Offset = new float3[poses.Count];
            data.All = new BoneElement[poses.Count * n];
            for (int i = 0; i < poses.Count; i++)
            {
                Array.Copy(poses[i].bones, 0, data.All, i * n, n);
                data.Offset[i] = poses[i].offset;
            }
            data.Count = poses.Count;
            data.Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return data;
        }

        private void Joints(string what, CitizenSkeleton skeleton, float3[] pos, quaternion[] rot)
        {
            if (Trace == null)
                return;
            float4x4[] g = skeleton.Globals(pos, rot);
            string J(int b) => $"{b}:({g[b].c3.x:0.00},{g[b].c3.y:0.00},{g[b].c3.z:0.00})";
            Trace($"{what}: pelvis {J(1)} spine {J(4)} L {J(89)} {J(90)} {J(91)} {J(92)} {J(96)} R {J(113)} {J(114)} {J(115)} {J(116)} {J(120)}");
        }

        private static BlendColors White() => new BlendColors
        {
            m_Color0 = Color.white, m_Color1 = Color.white, m_Color2 = Color.white, m_Color3 = Color.white,
            m_Color4 = Color.white, m_Color5 = Color.white, m_Color6 = Color.white, m_Color7 = Color.white,
        };

        private void Fail(string what, Exception ex)
        {
            if (m_Failed)
                return;
            m_Failed = true;
            Mod.log.Warn($"Game citizens: {what} failed ({ex.GetType().Name}: {ex.Message}); the simple figures show instead for this session.");
        }
    }
}
