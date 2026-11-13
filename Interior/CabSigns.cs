using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using LedPanel = CDLC.Asset.Led.LedPanel;

// Runtime extras on the bus model that change while driving (main thread only, nothing created per frame):
//  * LedSign: an LED dot-matrix sign (a LedPanel + LedComposer) showing the route label (a prefix in its own colour, the number)
//    and a message, the message scrolling when it does not fit; rewritten only when a dot changes. The destination signs, the
//    route number on the rear, the next-stop screen inside, the gear on the instrument cluster.
//  * CabTelltales: the warning lamps of the instrument cluster as small lit quads, one per lamp, switched on and off.
namespace CDLC.BusDriver.Interior
{
    internal sealed class LedSign : ISign, IDisposable
    {
        // the sign's ink (0) and the prefix's (1): two submeshes of one panel, a material each
        readonly LedPanel m_Panel;
        readonly LedComposer m_Composer;
        readonly float m_DotsPerSecond;
        string m_PrefixText = "", m_Block = "", m_Message = "";
        bool m_Scrolls, m_Shown;
        int m_Cycle = 1, m_Offset = -1;
        double m_Start;

        /// <summary>A sign in the lit material; the prefix in prefixLit (null: the same).</summary>
        internal LedSign(Transform parent, float3 centre, float2 size, int dotsX, int dotsY, Material lit, LedFont font, quaternion rotation, float dotsPerSecond, Material prefixLit = null)
        {
            try
            {
                size = AiSignRules.SquareCanvas(size, new int2(dotsX, dotsY));
                m_Panel = new LedPanel(parent, centre, size, dotsX, dotsY, new[] { lit, prefixLit ?? lit }, rotation, null, CabMaterials.AcquireResources());
                m_Composer = new LedComposer(font, dotsX, dotsY);
                m_DotsPerSecond = dotsPerSecond;
            }
            catch (Exception failure)
            {
                try { Destroy(); }
                catch (Exception cleanup) { CabCleanup.Retain(this); throw new AggregateException("LED sign construction and cleanup failed.", failure, cleanup); }
                throw;
            }
        }

        internal bool Alive => m_Panel.Alive;

        internal bool Visible
        {
            set => m_Panel.Visible = value;
        }

        /// <summary>The prefix's colour (its material).</summary>
        internal Material PrefixMaterial { set => m_Panel.SetMaterial(1, value); }

        /// <summary>Shows a block (left, fixed) and a message (centred, or scrolling); both empty = dark.</summary>
        internal void Show(string block, string message, double now) => Show("", block, message, now);

        /// <summary>Shows the route label (prefix in its colour, the number; left) and a message; all empty = dark.</summary>
        internal void Show(string prefix, string block, string message, double now)
        {
            prefix = prefix ?? "";
            block = block ?? "";
            message = message ?? "";
            if (prefix.Length == 0 && block.Length == 0 && message.Length == 0)
            {
                if (m_Shown)
                {
                    m_Composer.Clear();
                    m_Panel.Apply(m_Composer.Buffer, m_Composer.Ink);
                    m_Shown = false;
                    m_PrefixText = m_Block = m_Message = "";
                    m_Offset = -1;
                }

                return;
            }

            if (prefix != m_PrefixText || block != m_Block || message != m_Message || !m_Shown)
            {
                m_PrefixText = prefix;
                m_Block = block;
                m_Message = message;
                m_Scrolls = m_Composer.Scrolls(prefix, block, message);
                m_Cycle = Mathf.Max(1, m_Composer.Cycle(prefix, block, message));
                m_Start = now;
                m_Offset = -1;
                m_Shown = true;
            }

            int offset = m_Scrolls ? (int)((now - m_Start) * m_DotsPerSecond) % m_Cycle : 0;
            if (offset == m_Offset)
                return;

            m_Offset = offset;
            m_Composer.RenderSign(m_PrefixText, m_Block, m_Message, offset);
            m_Panel.Apply(m_Composer.Buffer, m_Composer.Ink);
        }

        internal void Destroy() => Dispose();

        public void Dispose() => m_Panel?.Dispose();

        bool ISign.Alive => Alive;
        bool ISign.Visible { set => Visible = value; }
        void ISign.Show(string prefix, string block, string message, double now) => Show(prefix, block, message, now);
        void ISign.Destroy() => Destroy();
    }

    /// <summary>Warning lamps: one small lit quad each (its own material colour), facing -Z of its frame.</summary>
    internal sealed class CabTelltales : IDisposable
    {
        static Mesh s_Quad;
        static int s_QuadUsers;
        readonly List<GameObject> m_Lamps = new List<GameObject>();
        readonly List<bool> m_On = new List<bool>();
        IDisposable m_MaterialLease;
        bool m_HasQuad;
        bool m_DisposeStarted;

        static Mesh Quad()
        {
            if (s_Quad != null)
                return s_Quad;

            Mesh mesh = null;
            try
            {
                mesh = new Mesh { name = "Bus Driver telltale", hideFlags = HideFlags.DontSave };
                mesh.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) });
                mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
                mesh.SetTriangles(new[] { 0, 3, 2, 0, 2, 1 }, 0);
                mesh.bounds = new Bounds(Vector3.zero, new Vector3(1.1f, 1.1f, 0.1f));
                return s_Quad = mesh;
            }
            catch (Exception failure) { CabCleanup.FailedAllocation(mesh, failure); throw; }
        }

        /// <summary>Adds a lamp (model space centre, rotation of its -Z facing frame, size); returns its index.</summary>
        internal int Add(Transform parent, float3 centre, quaternion rotation, float2 size, Material lit)
        {
            if (m_DisposeStarted) throw new ObjectDisposedException(nameof(CabTelltales));
            GameObject go = null;
            bool acquired = !m_HasQuad;
            try
            {
                if (acquired)
                {
                    m_MaterialLease = CabMaterials.AcquireResources();
                    Quad();
                    s_QuadUsers++;
                    m_HasQuad = true;
                }
                go = new GameObject("telltale " + m_Lamps.Count) { hideFlags = HideFlags.DontSave, layer = parent.gameObject.layer };
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(centre.x, centre.y, centre.z);
                go.transform.localRotation = new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);
                go.transform.localScale = new Vector3(size.x, size.y, 1f);
                go.AddComponent<MeshFilter>().sharedMesh = s_Quad;
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                r.renderingLayerMask = uint.MaxValue;
                r.sharedMaterial = lit;
                go.SetActive(false);
                m_Lamps.Add(go);
                m_On.Add(false);
                return m_Lamps.Count - 1;
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                if (go != null && !ReleaseLamp(go, failures))
                {
                    m_Lamps.Add(go);
                    m_On.Add(false);
                }
                if (acquired && m_Lamps.Count == 0) CabCleanup.Try(ReleaseResources, failures);
                if (failures.Count > 1) throw new AggregateException("Telltale construction and cleanup failed.", failures);
                throw;
            }
        }

        internal bool Alive => !m_DisposeStarted && m_Lamps.Count > 0 && m_Lamps[0] != null;

        internal void Set(int lamp, bool on)
        {
            if (m_DisposeStarted || lamp < 0 || lamp >= m_Lamps.Count || m_On[lamp] == on || m_Lamps[lamp] == null)
                return;

            m_On[lamp] = on;
            m_Lamps[lamp].SetActive(on);
        }

        internal void Destroy() => Dispose();

        public void Dispose()
        {
            m_DisposeStarted = true;
            var failures = new List<Exception>();
            for (int i = m_Lamps.Count - 1; i >= 0; i--)
                if (m_Lamps[i] == null || ReleaseLamp(m_Lamps[i], failures))
                {
                    m_Lamps.RemoveAt(i);
                    m_On.RemoveAt(i);
                }
            if (m_Lamps.Count == 0) CabCleanup.Try(ReleaseResources, failures);
            CabCleanup.ThrowIfAny(failures, "Telltale cleanup failed.");
        }

        static bool ReleaseLamp(GameObject go, List<Exception> failures)
        {
            CabCleanup.Try(() => go.SetActive(false), failures);
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            MeshFilter filter = go.GetComponent<MeshFilter>();
            bool detached = true;
            if (renderer != null)
            {
                CabCleanup.Try(() => renderer.enabled = false, failures);
                detached &= CabCleanup.Try(() => renderer.sharedMaterials = Array.Empty<Material>(), failures);
            }
            if (filter != null) detached &= CabCleanup.Try(() => filter.sharedMesh = null, failures);
            return detached && CabCleanup.Try(() => UnityEngine.Object.Destroy(go), failures);
        }

        void ReleaseResources()
        {
            var failures = new List<Exception>();
            if (m_HasQuad)
            {
                if (s_QuadUsers > 1)
                {
                    s_QuadUsers--;
                    m_HasQuad = false;
                }
                else if (s_Quad == null || CabCleanup.Try(() => UnityEngine.Object.Destroy(s_Quad), failures))
                {
                    s_Quad = null;
                    s_QuadUsers--;
                    m_HasQuad = false;
                }
            }
            if (!m_HasQuad && m_MaterialLease != null && CabCleanup.Try(m_MaterialLease.Dispose, failures)) m_MaterialLease = null;
            CabCleanup.ThrowIfAny(failures, "Telltale resource cleanup failed.");
        }
    }
}
