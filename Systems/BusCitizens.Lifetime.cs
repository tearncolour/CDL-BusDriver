using System;
using System.Collections.Generic;
using System.Linq;
using CDLC.BusDriver.Interior;
using UnityEngine;

namespace CDLC.BusDriver
{
    internal sealed partial class BusCitizens
    {
        private readonly HashSet<Figure> m_Figures = new HashSet<Figure>();
        private bool m_DisposeStarted;

        internal sealed class Figure : MonoBehaviour, IDisposable
        {
            internal ComputeBuffer Meta, ShadowMeta;
            internal HandsRig Hands;
            internal List<(int stack, int texture)> Pages;
            internal static readonly HashSet<Figure> s_Alive = new HashSet<Figure>();
            internal BusCitizens Owner;
            internal GameObject Root;

            private void OnEnable() => s_Alive.Add(this);
            private void OnDisable() => s_Alive.Remove(this);
            private void OnDestroy()
            {
                s_Alive.Remove(this);
                Root = null;
                try { Dispose(); }
                catch { CabCleanup.Retain((IDisposable)this); throw; }
            }

            public void Dispose()
            {
                var failures = new List<Exception>();
                bool detached = true;
                if (Root != null)
                {
                    CabCleanup.Try(() => Root.SetActive(false), failures);
                    foreach (MeshRenderer renderer in Root.GetComponentsInChildren<MeshRenderer>(true))
                        detached &= CabCleanup.Try(() =>
                        {
                            renderer.enabled = false;
                            renderer.SetPropertyBlock(null);
                            renderer.sharedMaterials = Array.Empty<Material>();
                        }, failures);
                    foreach (MeshFilter filter in Root.GetComponentsInChildren<MeshFilter>(true))
                        detached &= CabCleanup.Try(() => filter.sharedMesh = null, failures);
                }
                if (detached)
                {
                    if (Meta != null) CabCleanup.Try(() => { Meta.Release(); Meta = null; }, failures);
                    if (ShadowMeta != null) CabCleanup.Try(() => { ShadowMeta.Release(); ShadowMeta = null; }, failures);
                    if (failures.Count == 0 && Root != null)
                        CabCleanup.Try(() => { UnityEngine.Object.Destroy(Root); Root = null; }, failures);
                }
                CabCleanup.ThrowIfAny(failures, "Citizen figure cleanup failed.");
                Owner?.m_Figures.Remove(this);
                Owner = null;
                Hands = null;
                s_Alive.Remove(this);
            }
        }

        private void OwnFigure(Figure figure)
        {
            figure.Owner = this;
            figure.Root = figure.gameObject;
            m_Figures.Add(figure);
        }

        public void Dispose()
        {
            m_DisposeStarted = true;
            foreach (var work in m_StyleWork.Values) CabPrebuild.Observe(work.task);
            m_StyleWork.Clear();
            var failures = new List<Exception>();
            foreach (Figure figure in new List<Figure>(m_Figures))
                CabCleanup.Try(figure.Dispose, failures);
            // Shared style and part resources must survive every still-owned renderer, including inactive figures.
            if (m_Figures.Count == 0)
            {
                foreach (var entry in m_Styles.ToArray())
                    CabCleanup.Try(() => ReleaseStyle(entry.Key, entry.Value), failures);
                foreach (var entry in m_Parts.ToArray())
                    CabCleanup.Try(() => ReleasePart(entry.Key, entry.Value), failures);
            }
            CabCleanup.ThrowIfAny(failures, "Citizen owner cleanup failed.");
        }
    }
}
