#if CDLC_AUTHORING
using System;
using CDLC.BusDriver.Interior;
using Game.Prefabs;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>Owns one native mesh-array reference; returned meshes are borrowed until disposal.</summary>
    internal sealed class BusNativeMeshLease : IDisposable
    {
        RenderPrefab m_Owner;
        readonly Mesh[] m_Meshes;
        readonly int m_MaterialCount;

        internal BusNativeMeshLease(RenderPrefab prefab)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            m_Meshes = prefab.ObtainMeshes();
            m_Owner = prefab;
            try
            {
                if (m_Meshes == null) throw new InvalidOperationException("Native mesh array is unavailable.");
                m_MaterialCount = prefab.materialCount;
                int submeshes = 0;
                foreach (Mesh mesh in m_Meshes)
                {
                    if (mesh == null) throw new InvalidOperationException("Native mesh array contains an unavailable mesh.");
                    submeshes = checked(submeshes + mesh.subMeshCount);
                }
                if (submeshes != m_MaterialCount)
                    throw new InvalidOperationException("Native material count does not match the flattened mesh submeshes.");
            }
            catch (Exception failure)
            {
                try { Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("Native mesh acquisition and release failed.", failure, cleanup); }
                throw;
            }
        }

        internal Mesh Get(int material, out int submesh)
        {
            if (m_Owner == null) throw new ObjectDisposedException(nameof(BusNativeMeshLease));
            if ((uint)material >= (uint)m_MaterialCount) throw new ArgumentOutOfRangeException(nameof(material));
            submesh = material;
            foreach (Mesh mesh in m_Meshes)
            {
                if (submesh < mesh.subMeshCount) return mesh;
                submesh -= mesh.subMeshCount;
            }
            throw new InvalidOperationException("Native submesh layout changed while borrowed.");
        }

        public void Dispose()
        {
            if (m_Owner == null) return;
            try { m_Owner.ReleaseMeshes(); m_Owner = null; }
            catch { CabCleanup.Retain(this); throw; }
        }
    }
}
#endif
