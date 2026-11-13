using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Game.Prefabs;
using Game.Rendering;
using Game.Rendering.Debug;
using Unity.Entities;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>Own one actual game renderer with its native light/animation buffers.</summary>
    internal sealed class BusNativeRendererControl : IDisposable
    {
        readonly Dictionary<Material, Material> m_Snapshots = new Dictionary<Material, Material>();
        readonly Dictionary<Material, Dictionary<string, bool>> m_Passes = new Dictionary<Material, Dictionary<string, bool>>();
        internal GameObject Root { get; private set; }
        RenderPrefabRenderer m_Renderer;
        int m_StartFrame;
        readonly int m_ExpectedLightCount;

        internal BusNativeRendererControl(RenderPrefab render, Vector3 origin, int lod = 0, bool useVT = false)
        {
            try
            {
                var emission = render.GetComponent<EmissiveProperties>();
                m_ExpectedLightCount = emission != null && emission.active ? emission.lightsCount : 0;
                var levels = new List<RenderPrefab> { render };
                if (render.TryGet(out LodProperties properties) && properties.m_LodMeshes != null) levels.AddRange(properties.m_LodMeshes);
                foreach (var level in levels) for (int i = 0; i < level.materialCount; i++)
                {
                    var material = level.ObtainMaterial(i, useVT);
                    if (m_Snapshots.ContainsKey(material)) continue;
                    m_Snapshots[material] = new Material(material) { hideFlags = HideFlags.DontSave };
                    m_Passes[material] = Enumerable.Range(0, material.passCount).Select(material.GetPassName)
                        .Concat(new[] { "MOTIONVECTORS" }).Distinct().ToDictionary(pass => pass, material.GetShaderPassEnabled);
                }
                Root = new GameObject("CDLC actual native renderer control") { hideFlags = HideFlags.DontSave };
                Root.SetActive(false); Root.transform.position = origin;
                m_Renderer = Root.AddComponent<RenderPrefabRenderer>();
                m_Renderer.m_Prefab = render; m_Renderer.m_NoVT = !useVT; m_Renderer.m_Animate = false;
                m_Renderer.m_LODIndex = lod; m_Renderer.m_AnimationIndex = -1; m_Renderer.m_ColorIndex = -1;
                m_Renderer.m_EmissiveLight = -1; m_Renderer.m_WindowsLight = 0;
                m_StartFrame = Time.frameCount; Root.SetActive(true);
            }
            catch { Dispose(); throw; }
        }

        internal async Task WaitForUpdate()
        {
            for (int attempt = 0; attempt < 150 && Time.frameCount < m_StartFrame + 2; attempt++) await Task.Delay(20);
            if (Time.frameCount < m_StartFrame + 2 || m_Renderer.GetActiveRoot() == null || m_Renderer.hierarchies.Count(x => x.enabled) != 1)
                throw new InvalidOperationException("Actual native renderer did not complete two real frames with one active LOD");
        }

        internal object Evidence()
        {
            var buffers = new List<object>();
            foreach (var instance in m_Renderer.hierarchies.Where(x => x.enabled))
            {
                var field = instance.GetType().GetField("m_LightBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
                var buffer = field?.GetValue(instance) as ComputeBuffer;
                if (buffer == null && m_ExpectedLightCount == 0)
                {
                    buffers.Add(new { instance.name, count = 0, declared_lamps = 0, no_procedural_lamps = true });
                    continue;
                }
                if (buffer == null) throw new InvalidOperationException("Actual native renderer has no declared procedural light buffer");
                var values = new Color[buffer.count]; buffer.GetData(values);
                if (values.Length != m_ExpectedLightCount + 1 || buffer.stride != 16 || values[0] != Color.clear ||
                    values.Any(v => !Finite(v.r) || !Finite(v.g) || !Finite(v.b) || !Finite(v.a)))
                    throw new InvalidOperationException("Native renderer light buffer differs from its declared lamp count, RGBA stride or zero-valued nonlamp ID0");
                buffers.Add(new { instance.name, count = buffer.count, declared_lamps = m_ExpectedLightCount, stride = buffer.stride,
                    rgba = values.Select(v => new[] { v.r, v.g, v.b, v.a }).ToArray(), nonlamp_id0_zero = true });
            }
            return new { renderer = typeof(RenderPrefabRenderer).FullName, m_Renderer.m_NoVT, m_Renderer.m_LODIndex,
                m_Renderer.m_Animate, m_Renderer.m_ColorIndex, m_Renderer.m_EmissiveLight, m_Renderer.m_WindowsLight,
                real_frames_elapsed = Time.frameCount - m_StartFrame, buffers, properties = PropertyEvidence(Root),
                material_state_cleanup = "Restores cached material properties, keywords, queues and passes; game-owned global buffer singleton retained, the game's own global shape/cull/overlay/bone/meta buffers rebound" };
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static object PropertyEvidence(GameObject root)
        {
            return root.GetComponentsInChildren<MeshRenderer>().Where(x => x.enabled && x.gameObject.activeInHierarchy).Select(renderer =>
            {
                var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                var light = block.GetVector("colossal_LightParameters");
                var hasBuffer = typeof(MaterialPropertyBlock).GetMethod("HasBuffer", new[] { typeof(string) });
                return new { renderer.name, block.isEmpty, has_light_info = hasBuffer?.Invoke(block, new object[] { "_LightInfo" }),
                    light_parameters = new[] { light.x, light.y, light.z, light.w }, single_lights_offset = block.GetFloat("colossal_SingleLightsOffset"),
                    shaders = renderer.sharedMaterials.Select(m => new { m.name, shader = m.shader.name, m.shaderKeywords }).ToArray() };
            }).ToArray();
        }

        // RenderPrefabRenderer's GlobalBufferRenderer binds its own shapeBuffer/cullBuffer/overlayBuffer (and, for characters, the
        // bone and meta buffers) under the global names the game's BatchMeshSystem and AnimatedSystem use. The game binds its
        // buffers only when it resizes them, so after a check the city's citizens read the checker's buffers: stretched and
        // kneeling figures. Call after the renderer is inactive; a buffer the game has not created yet is bound when it is.
        internal static void RestoreGameGlobalBuffers()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var batches = world.GetExistingSystemManaged<BatchMeshSystem>();
            if (batches != null)
            {
                Bind(batches, "m_ShapeBuffer", "shapeBuffer");
                Bind(batches, "m_CullBuffer", "cullBuffer");
                Bind(batches, "m_OverlayBuffer", "overlayBuffer");
            }
            var animation = world.GetExistingSystemManaged<AnimatedSystem>();
            if (animation != null)
            {
                Bind(animation, "m_BoneBuffer", "boneBuffer");
                Bind(animation, "m_MetaBuffer", "metaBuffer");
                if (!Bind(animation, "m_BoneHistoryBuffer", "boneHistoryBuffer")) Bind(animation, "m_BoneBuffer", "boneHistoryBuffer");
            }
        }

        static bool Bind(object system, string field, string global)
        {
            FieldInfo info = system.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new NotSupportedException(system.GetType().Name + "." + field + " is missing: the game's global buffer contract changed");
            switch (info.GetValue(system))
            {
                case GraphicsBuffer graphics: Shader.SetGlobalBuffer(global, graphics); return true;
                case ComputeBuffer compute: Shader.SetGlobalBuffer(global, compute); return true;
                case null: return false;
                default: throw new NotSupportedException(system.GetType().Name + "." + field + " is no GPU buffer");
            }
        }

        public void Dispose()
        {
            if (Root != null) { Root.SetActive(false); RestoreGameGlobalBuffers(); UnityEngine.Object.Destroy(Root); Root = null; }
            foreach (var pair in m_Snapshots)
            {
                pair.Key.CopyPropertiesFromMaterial(pair.Value); pair.Key.shaderKeywords = pair.Value.shaderKeywords;
                pair.Key.renderQueue = pair.Value.renderQueue; pair.Key.enableInstancing = pair.Value.enableInstancing;
                foreach (var pass in m_Passes[pair.Key]) pair.Key.SetShaderPassEnabled(pass.Key, pass.Value);
                UnityEngine.Object.Destroy(pair.Value);
            }
            m_Snapshots.Clear(); m_Passes.Clear();
        }
    }
}
