using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Cuts pieces out of a render prefab's geometry without the game's synchronous load: the file is read by Unity's asynchronous file
    /// reads (polled, never waited for), decoded and cut on a worker thread into memory of its own, so the main thread pays only for the
    /// meshes made of the pieces. The game's GeometryAsset is not touched (its load state, its request count and its meshes stay the
    /// game's). Poll on the main thread until Done; the pieces live until Dispose.
    /// </summary>
    internal sealed class NativeGeometryRead : IDisposable
    {
        private enum Stage { Prefix, Infos, Body, Cut, Done }

        private readonly AsyncReadDescriptor m_File;
        private readonly bool[] m_Wanted;
        private readonly Func<NativeMeshCut.Grouping> m_Grouping;
        private readonly int m_MaxTriangles;
        private Stage m_Stage;
        private NativeBlock m_Read;
        private ReadHandle m_Handle;
        private bool m_Reading;
        private int m_Meshes;
        private uint m_Flags;
        private NativeGeometryFile m_Format;
        private Task<List<NativePiece>> m_Cut;

        internal List<NativePiece> Pieces { get; private set; }
        internal NativeGeometryFile Format => m_Stage == Stage.Done ? m_Format : null;
        internal bool Done => m_Stage == Stage.Done;
        internal long WorkerMs { get; private set; }

        /// <param name="wanted">By material index (the flattened submesh): the submeshes to cut.</param>
        /// <param name="grouping">Made and called on the worker thread: it must not touch Unity objects.</param>
        internal NativeGeometryRead(RenderPrefab prefab, bool[] wanted, Func<NativeMeshCut.Grouping> grouping, int maxTriangles)
        {
            if (prefab == null || !prefab.hasGeometryAsset || prefab.geometryAsset == null)
                throw new InvalidOperationException($"{prefab?.name} has no geometry asset.");
            m_File = prefab.geometryAsset.GetAsyncReadDescriptor();
            if (string.IsNullOrEmpty(m_File.path)) throw new InvalidOperationException($"{prefab.name}: its geometry has no file.");
            m_Wanted = wanted;
            m_Grouping = grouping;
            m_MaxTriangles = maxTriangles;
            StartRead(0, NativeGeometryFile.kPrefixBytes);
        }

        /// <summary>Advances without waiting; true once the pieces are there. Throws the read's or the cut's failure.</summary>
        internal unsafe bool Poll()
        {
            if (m_Reading)
            {
                ReadStatus status = m_Handle.Status;
                if (status == ReadStatus.InProgress) return false;
                m_Handle.JobHandle.Complete();
                m_Handle.Dispose();
                m_Reading = false;
                if (status != ReadStatus.Complete) throw new InvalidOperationException($"Reading the geometry of {m_File.name} failed ({status}).");
            }

            switch (m_Stage)
            {
                case Stage.Prefix:
                {
                    var prefix = new byte[NativeGeometryFile.kPrefixBytes];
                    System.Runtime.InteropServices.Marshal.Copy(m_Read.Pointer, prefix, 0, prefix.Length);
                    (m_Meshes, m_Flags) = NativeGeometryFile.ReadPrefix(prefix);
                    m_Stage = Stage.Infos;
                    StartRead(NativeGeometryFile.kPrefixBytes, NativeGeometryFile.InfosBytes(m_Meshes));
                    return false;
                }
                case Stage.Infos:
                {
                    var infos = new byte[NativeGeometryFile.InfosBytes(m_Meshes)];
                    System.Runtime.InteropServices.Marshal.Copy(m_Read.Pointer, infos, 0, infos.Length);
                    m_Format = new NativeGeometryFile(m_Meshes, m_Flags, infos);
                    if (m_Format.SubMeshes.Length != m_Wanted.Length)
                        throw new InvalidOperationException($"{m_File.name}: {m_Format.SubMeshes.Length} submeshes for {m_Wanted.Length} materials.");
                    m_Stage = Stage.Body;
                    StartRead(m_Format.SubMeshesAt, m_Format.BodyEnd - m_Format.SubMeshesAt);
                    return false;
                }
                case Stage.Body:
                {
                    NativeBlock body = m_Read;
                    m_Read = null;
                    NativeGeometryFile format = m_Format;
                    bool[] wanted = m_Wanted;
                    Func<NativeMeshCut.Grouping> grouping = m_Grouping;
                    int maxTriangles = m_MaxTriangles;
                    m_Cut = Task.Run(() =>
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        try { return NativeGeometryDecode.Cut(format, body, wanted, grouping(), maxTriangles); }
                        finally
                        {
                            body.Dispose();
                            WorkerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                        }
                    });
                    m_Stage = Stage.Cut;
                    return false;
                }
                case Stage.Cut:
                    if (!m_Cut.IsCompleted) return false;
                    Task<List<NativePiece>> cut = m_Cut;
                    m_Cut = null;
                    m_Stage = Stage.Done;
                    Pieces = cut.GetAwaiter().GetResult();
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>Waits for the whole read and cut (the authoring probes, which build a cab in one call).</summary>
        internal void Finish()
        {
            while (!Poll())
            {
                if (m_Reading) m_Handle.JobHandle.Complete();
                else m_Cut?.Wait();
            }
        }

        private unsafe void StartRead(long at, long size)
        {
            m_Read?.Dispose();
            m_Read = new NativeBlock(size);
            var command = new ReadCommand { Buffer = m_Read.Bytes, Offset = m_File.offset + at, Size = size };
            m_Handle = AsyncReadManager.Read(m_File.path, &command, 1u);
            m_Reading = true;
        }

        /// <summary>
        /// A mesh of a piece, with the game mesh's own vertex layout (GeometryAsset.CreateMeshes: the formats, dimensions and streams of
        /// its header; packed normals and tangents byte for byte) and 16-bit indices when they fit.
        /// </summary>
        internal static unsafe Mesh MakeMesh(NativePiece piece, NativeGeometryFile.MeshInfo info, string name)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            try
            {
                var attributes = new List<VertexAttributeDescriptor>(NativeGeometryFile.kAttributes);
                for (int a = 0; a < NativeGeometryFile.kAttributes; a++)
                    if (info.Has(a))
                        attributes.Add(new VertexAttributeDescriptor((VertexAttribute)a, (VertexAttributeFormat)info.Format[a], info.Dimension[a], info.Stream[a]));
                const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                var bounds = new Bounds();
                bounds.SetMinMax(new Vector3(piece.Min.x, piece.Min.y, piece.Min.z), new Vector3(piece.Max.x, piece.Max.y, piece.Max.z));
                Mesh.MeshDataArray output = Mesh.AllocateWritableMeshData(1);
                bool applied = false;
                try
                {
                    Mesh.MeshData data = output[0];
                    data.SetVertexBufferParams(piece.VertexCount, attributes.ToArray());
                    for (int s = 0; s < piece.Streams.Length; s++)
                        if (piece.Streams[s] != null)
                        {
                            NativeArray<byte> stream = data.GetVertexData<byte>(s);
                            if (stream.Length != piece.Streams[s].Length) throw new InvalidOperationException($"Vertex stream {s} is {stream.Length} bytes for {piece.Streams[s].Length}.");
                            UnsafeUtility.MemCpy(stream.GetUnsafePtr(), piece.Streams[s].Bytes, piece.Streams[s].Length);
                        }
                    bool wide = piece.VertexCount > ushort.MaxValue;
                    data.SetIndexBufferParams(piece.IndexCount, wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
                    uint* indices = (uint*)piece.Indices.Bytes;
                    if (wide)
                        UnsafeUtility.MemCpy(data.GetIndexData<uint>().GetUnsafePtr(), indices, 4L * piece.IndexCount);
                    else
                    {
                        NativeArray<ushort> narrow = data.GetIndexData<ushort>();
                        for (int i = 0; i < piece.IndexCount; i++) narrow[i] = (ushort)indices[i];
                    }
                    data.subMeshCount = 1;
                    data.SetSubMesh(0, new SubMeshDescriptor(0, piece.IndexCount)
                    {
                        bounds = bounds, firstVertex = 0, vertexCount = piece.VertexCount,
                    }, flags);
                    Mesh.ApplyAndDisposeWritableMeshData(output, mesh, flags);
                    applied = true;
                }
                finally { if (!applied) output.Dispose(); }
                mesh.bounds = bounds;
                return mesh;
            }
            catch (Exception failure)
            {
                CabCleanup.FailedAllocation(mesh, failure);
                throw;
            }
        }

        public void Dispose()
        {
            if (m_Reading)
            {
                // the file read writes into m_Read until it ends
                m_Handle.Cancel();
                m_Handle.JobHandle.Complete();
                m_Handle.Dispose();
                m_Reading = false;
            }
            m_Read?.Dispose();
            m_Read = null;
            if (m_Cut != null)
            {
                // the worker owns its result until it ends: let it go then
                m_Cut.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion) foreach (NativePiece p in t.Result) p.Dispose();
                    else _ = t.Exception;
                }, TaskScheduler.Default);
                m_Cut = null;
            }
            if (Pieces != null)
            {
                foreach (NativePiece piece in Pieces) piece.Dispose();
                Pieces = null;
            }
            m_Stage = Stage.Done;
        }
    }
}
