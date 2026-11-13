using System;
using System.Collections.Generic;
using CDLC.Asset.Rendering;
using CDLC.BusDriver.Interior;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Imported window geometry over the cab's openings. CDL's shared transparent material recipe is preferred;
    /// unsupported native materials remain usable through its fallback. Moving panes keep the imported vertex encoding.
    /// Built in steps: the glass submeshes are read and cut off the main thread (<see cref="NativeGeometryRead"/>), then
    /// <see cref="Step"/> makes the materials and the panes a few at a time. Ready only when every pane is there.
    /// </summary>
    internal sealed class BusGlass : IDisposable
    {
        private sealed class Pane
        {
            internal GameObject Object;
            internal Mesh Mesh;
#if CDLC_AUTHORING
            internal SurfaceAsset Surface;
#endif
        }

        // a pane's triangles at most: one pane is one step's upload
        internal const int kPaneTriangles = 16384;

        private readonly List<Pane> m_Panes = new List<Pane>();
        private readonly List<GlassMaterial> m_Materials = new List<GlassMaterial>();
        private readonly Dictionary<int, GlassMaterial> m_BySubMesh = new Dictionary<int, GlassMaterial>();
        private readonly Dictionary<int, int> m_Triangles = new Dictionary<int, int>();
        private readonly Dictionary<int, SortedSet<int>> m_Parts = new Dictionary<int, SortedSet<int>>();
        // A queued cleanup owns this instance until drained; rebuilding it would let that queue destroy a new build.
        private bool m_RetainedForCleanup;

        private RenderPrefab m_Mesh;
        private NativeGeometryRead m_Cut;
        private bool m_OwnsCut;
        private int m_Next;
        private Transform m_Parent;
        private int m_Layer;
        private Func<int, Transform> m_PartTransform;
        private Func<int, Matrix4x4> m_RestModelToPart;
        private bool m_KeepAuthoredMask;

        internal int PaneCount => m_Panes.Count;
        internal bool Ready { get; private set; }
        internal bool Started => m_Cut != null;
#if CDLC_AUTHORING
        internal void CollectOpticsBindings(List<BusGlassOpticsBinding> bindings, string section)
        {
            if (!Ready) throw new InvalidOperationException("Optics requires complete driving glass");
            foreach (Pane pane in m_Panes)
                bindings.Add(BusGlassOpticsBinding.ForPane(pane.Surface, pane.Object.GetComponent<MeshRenderer>(), section));
        }
#endif

        // Main-thread Stopwatch totals: material loading/validation and pane meshes.
        internal static long s_MaterialTicks, s_PieceTicks;

        internal static string Spent()
        {
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return $"glass on the main thread: materials {s_MaterialTicks * ms:0.0} ms, panes {s_PieceTicks * ms:0.0}";
        }

        /// <summary>The first render prefab of a vehicle, which carries its glass; null for none.</summary>
        internal static RenderPrefab MeshOf(PrefabBase vehicle) =>
            vehicle is ObjectGeometryPrefab geometry && geometry.m_Meshes != null && geometry.m_Meshes.Length > 0 ? geometry.m_Meshes[0].m_Mesh as RenderPrefab : null;

        /// <summary>
        /// Starts reading and cutting the glass submeshes of <paramref name="mesh"/>: each triangle goes to the part <paramref name="partOf"/>
        /// gives for its centre (model frame; called on a worker thread, so it must be pure), -1 the body. Null when it has no glass surface.
        /// </summary>
        internal static NativeGeometryRead StartCut(RenderPrefab mesh, Func<Vector3, int> partOf)
        {
            if (mesh == null) return null;
            var wanted = new bool[mesh.materialCount];
            bool any = false;
            for (int i = 0; i < wanted.Length; i++)
                any |= wanted[i] = IsGlass(mesh.GetSurfaceAsset(i));
            if (!any) return null;
            return new NativeGeometryRead(mesh, wanted, () => (a, b, c) =>
            {
                if (partOf == null) return NativeMeshCut.Body;
                float3 centre = (a + b + c) / 3f;
                int part = partOf(new Vector3(centre.x, centre.y, centre.z));
                return part >= 0 ? part : NativeMeshCut.Body;
            }, kPaneTriangles);
        }

        /// <summary>The prefab's geometry is being read in the background (GeometryAsset.LoadSync would throw until it is done).</summary>
        internal sealed class GeometryBusyException : Exception
        {
            internal GeometryBusyException() : base("the game is still streaming this model's geometry") { }
        }

        /// <remarks>
        /// Any ObtainMeshes in that state is unsafe, not just slow: GeometryAsset.LoadSync completes and disposes the async header read
        /// before it finds that the game asked for fewer attributes and throws, and GeometryAssetLoadingSystem then drops the asset with
        /// its load never finished, so every later sync load throws again for the rest of the session (Colossal.IO.AssetDatabase).
        /// The glass and the native cab read the file themselves and never ask for that load.
        /// </remarks>
        internal static bool GeometryLoading(RenderPrefab prefab)
        {
            GeometryAsset asset = prefab != null && prefab.hasGeometryAsset ? prefab.geometryAsset : null;
            if (asset == null) return false;
            ref readonly GeometryAsset.Loading loading = ref asset.loading;
            return loading.m_AsyncLoadingStarted || loading.m_AsyncLoadingScheduled;
        }
        internal string Describe { get; private set; } = "none";

        /// <summary>
        /// Starts the glass of a vehicle prefab's first mesh, under <paramref name="parent"/> (the model's root, at its rest pose); false
        /// when it has none. <paramref name="partOf"/> (optional) says which moving part of the model a glass triangle belongs to,
        /// from its centre in the model frame (-1: the body): door glass rides on its door leaf (<paramref name="partTransform"/>),
        /// the asset's own glass is static (an AI bus's doors do not swing). <paramref name="restModelToPart"/> (optional) is a part's
        /// rest frame: the panes bind to it even when the model is already posed (a door open when the glass is built again).
        /// <paramref name="cut"/> (optional) is a cut made ahead for the same mesh and the same parts: it stays its owner's.
        /// </summary>
        internal bool Begin(PrefabBase vehicle, Transform parent, int layer, Func<Vector3, int> partOf = null, Func<int, Transform> partTransform = null,
            Func<int, Matrix4x4> restModelToPart = null, bool keepAuthoredMask = false, NativeGeometryRead cut = null)
        {
            if (m_RetainedForCleanup)
                throw new InvalidOperationException("A glass instance retained for cleanup cannot be rebuilt.");
            Dispose();
            m_Mesh = MeshOf(vehicle);
            if (m_Mesh == null || parent == null)
            {
                Describe = "no render prefab";
                return false;
            }
            m_Cut = cut;
            m_OwnsCut = cut == null;
            if (m_OwnsCut) m_Cut = StartCut(m_Mesh, partOf);
            if (m_Cut == null)
            {
                Describe = $"no glass surface in {m_Mesh.name} ({m_Mesh.materialCount} surfaces)";
                return false;
            }
            m_Parent = parent;
            m_Layer = layer;
            m_PartTransform = partTransform;
            m_RestModelToPart = restModelToPart;
            m_KeepAuthoredMask = keepAuthoredMask;
            m_Next = 0;
            Describe = "reading";
            return true;
        }

        /// <summary>
        /// Advances the build within <paramref name="budgetTicks"/> (at least one material or pane once the cut is there); true when it is
        /// complete. A failure is thrown; Dispose then releases what was made.
        /// </summary>
        internal bool Step(long budgetTicks)
        {
            if (m_Cut == null) return true;
            if (Ready) return true;
            if (!m_Cut.Poll()) return false;
            long end = budgetTicks == long.MaxValue ? long.MaxValue : System.Diagnostics.Stopwatch.GetTimestamp() + budgetTicks;
            List<NativePiece> pieces = m_Cut.Pieces;
            while (m_Next < pieces.Count)
            {
                MakePane(pieces[m_Next++]);
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= end) break;
            }
            if (m_Next < pieces.Count) return false;

            var found = new List<string>();
            foreach (KeyValuePair<int, GlassMaterial> entry in m_BySubMesh)
                found.Add($"{m_Mesh.GetSurfaceAsset(entry.Key)?.name} ({entry.Value.Material.shader?.name}, {(entry.Value.UsesCdlRendering ? "CDL" : "native fallback")}, " +
                    $"{m_Triangles[entry.Key]} triangles on {m_Parts[entry.Key].Count} part(s): {string.Join(" ", m_Parts[entry.Key])})");
            Describe = (found.Count > 0 ? string.Join(", ", found) : $"no glass triangles in {m_Mesh.name}") +
                $"; read and cut off the main thread in {m_Cut.WorkerMs} ms, {m_Panes.Count} pane(s)";
            Ready = m_Panes.Count > 0;
            if (m_OwnsCut) m_Cut.Dispose();
            m_Cut = null;
            return true;
        }

        /// <summary>The whole build in one call (the authoring probes, which make a cab at once): Begin, wait for the cut, every pane.</summary>
        internal bool Build(PrefabBase vehicle, Transform parent, int layer, Func<Vector3, int> partOf = null, Func<int, Transform> partTransform = null,
            Func<int, Matrix4x4> restModelToPart = null, bool keepAuthoredMask = false)
        {
            try
            {
                if (!Begin(vehicle, parent, layer, partOf, partTransform, restModelToPart, keepAuthoredMask)) return false;
                m_Cut.Finish();
                Step(long.MaxValue);
                return Ready;
            }
            catch (Exception failure)
            {
                try { Dispose(); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Glass construction and cleanup failed.", failure, cleanup);
                }
                throw;
            }
        }

        private void MakePane(NativePiece piece)
        {
            if (!m_BySubMesh.TryGetValue(piece.SubMesh, out GlassMaterial material))
            {
                long tMaterial = System.Diagnostics.Stopwatch.GetTimestamp();
                material = GlassMaterial.Create(m_Mesh.GetSurfaceAsset(piece.SubMesh), m_KeepAuthoredMask);
                m_Materials.Add(material);
                m_BySubMesh[piece.SubMesh] = material;
                m_Triangles[piece.SubMesh] = 0;
                m_Parts[piece.SubMesh] = new SortedSet<int>();
                s_MaterialTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tMaterial;
            }

            long tPiece = System.Diagnostics.Stopwatch.GetTimestamp();
            int part = piece.Group;
            Transform on = part >= 0 ? m_PartTransform?.Invoke(part) : null;
            if (on == null) part = -1;
            var pane = new Pane { Object = new GameObject($"Glass {piece.SubMesh}" + (part >= 0 ? " on part " + part : "")) };
#if CDLC_AUTHORING
            pane.Surface = m_Mesh.GetSurfaceAsset(piece.SubMesh);
#endif
            m_Panes.Add(pane);
            pane.Object.hideFlags = HideFlags.DontSave;
            pane.Object.layer = m_Layer;
            // Keep the imported vertex data in its model frame. In particular, BH shaders read packed
            // octahedral normals (SNorm16 x2) and tangents (Float32 x1), not ordinary Vector3/Vector4 data.
            // Carry the inverse rest transform on the child instead of rewriting those attributes.
            AttachInModelFrame(pane.Object.transform, m_Parent, on ?? m_Parent, part >= 0 ? m_RestModelToPart?.Invoke(part) : null);
            pane.Mesh = NativeGeometryRead.MakeMesh(piece, m_Cut.Format.Meshes[piece.Mesh], m_Mesh.name + " glass");
            pane.Object.AddComponent<MeshFilter>().sharedMesh = pane.Mesh;
            var renderer = pane.Object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material.Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            // Native vehicle batches receive all HDRP light layers.
            renderer.renderingLayerMask = uint.MaxValue;
            m_Triangles[piece.SubMesh] += piece.IndexCount / 3;
            m_Parts[piece.SubMesh].Add(part);
            s_PieceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tPiece;
        }

        internal bool Visible
        {
            set
            {
                foreach (Pane pane in m_Panes)
                    if (pane.Object != null && pane.Object.activeSelf != value)
                        pane.Object.SetActive(value);
            }
        }

        // The importer marks glass by the _Gls suffix (Constants.Surface.kGlass) and gives it a glass template.
        internal static bool IsGlass(SurfaceAsset surface)
        {
            if (surface == null)
                return false;
            string name = surface.name ?? "";
            if (name.IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("_Glf", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            Material template = surface.GetTemplateMaterial();
            return template != null && template.name.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static void AttachInModelFrame(Transform pane, Transform model, Transform on, Matrix4x4? restModelToPart)
        {
            // Use only the local hierarchy: a bus can be far from the world origin, where subtracting two
            // world positions loses precision. Without the part's rest frame, the current one: Build is then
            // called before the first animated pose is applied.
            Matrix4x4 inverse;
            if (restModelToPart.HasValue) inverse = restModelToPart.Value;
            else
            {
                Matrix4x4 partToModel = Matrix4x4.identity;
                for (Transform node = on; node != model; node = node.parent)
                {
                    if (node == null) throw new InvalidOperationException("Glass part is outside the model hierarchy");
                    partToModel = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * partToModel;
                }
                inverse = partToModel.inverse;
            }
            Vector3 x = inverse.GetColumn(0), y = inverse.GetColumn(1), z = inverse.GetColumn(2);
            Vector3 scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z)) < 1e-8f)
                throw new InvalidOperationException("Glass part has a singular rest transform");
            if (inverse.determinant < 0f) scale.x = -scale.x;
            Quaternion rotation = Quaternion.LookRotation(z / scale.z, y / scale.y);
            Vector3 position = inverse.GetColumn(3);
            Matrix4x4 reconstructed = Matrix4x4.TRS(position, rotation, scale);
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(inverse[i]) || float.IsInfinity(inverse[i]) ||
                    Mathf.Abs(inverse[i] - reconstructed[i]) > 1e-5f * Mathf.Max(1f, Mathf.Abs(inverse[i])))
                    throw new InvalidOperationException("Glass part rest transform contains unsupported shear");
            pane.SetParent(on, false);
            pane.SetLocalPositionAndRotation(position, rotation);
            pane.localScale = scale;
        }

        public void Dispose()
        {
            Ready = false;
            var failures = new List<Exception>();
            if (m_Cut != null)
            {
                if (m_OwnsCut) CabCleanup.Try(m_Cut.Dispose, failures);
                m_Cut = null;
            }
            for (int i = m_Panes.Count - 1; i >= 0; i--)
            {
                Pane pane = m_Panes[i];
                try
                {
                    bool detached = true;
                    if (pane.Object != null)
                    {
                        detached &= CabCleanup.Try(() => pane.Object.SetActive(false), failures);
                        MeshRenderer renderer = pane.Object.GetComponent<MeshRenderer>();
                        if (renderer != null)
                        {
                            detached &= CabCleanup.Try(() => renderer.enabled = false, failures);
                            detached &= CabCleanup.Try(() => renderer.sharedMaterials = Array.Empty<Material>(), failures);
                        }
                        MeshFilter filter = pane.Object.GetComponent<MeshFilter>();
                        if (filter != null) detached &= CabCleanup.Try(() => filter.sharedMesh = null, failures);
                    }
                    if (!detached) continue;
                    if (pane.Mesh != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(pane.Mesh), failures)) pane.Mesh = null;
                    if (pane.Object != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(pane.Object), failures)) pane.Object = null;
                    if (pane.Mesh == null && pane.Object == null) m_Panes.RemoveAt(i);
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (m_Panes.Count == 0)
            {
                for (int i = m_Materials.Count - 1; i >= 0; i--)
                    if (CabCleanup.Try(m_Materials[i].Dispose, failures)) m_Materials.RemoveAt(i);
                if (m_Materials.Count == 0)
                {
                    m_BySubMesh.Clear();
                    m_Triangles.Clear();
                    m_Parts.Clear();
                }
            }
            if (failures.Count != 0)
            {
                m_RetainedForCleanup = true;
                CabCleanup.Retain(this);
                CabCleanup.ThrowIfAny(failures, "Bus glass cleanup failed.");
            }
            Describe = "none";
        }
    }
}
