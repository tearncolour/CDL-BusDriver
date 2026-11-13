#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CDLC.BusDriver.Interior;
using UnityEngine;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    internal sealed class BusRoadGlassMirrorFrames : IDisposable
    {
        sealed class Entry
        {
            internal object Source;
            internal Camera Camera;
            internal RenderTexture Texture;
            internal Material Material;
            internal Renderer Renderer;
            internal MeshFilter Filter;
            internal Mesh Mesh;
            internal int Renders, LastFrame = -1, Before;
            internal long LastSequence;
            internal bool Required;
        }

        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        readonly CabMirrors mirrors;
        readonly CabInstance instance;
        readonly Camera eye;
        readonly RenderTexture target;
        readonly Action check;
        readonly Func<bool> owns;
        readonly IDictionary<string, object> report;
        readonly Array originals;
        readonly Entry[] entries;
        readonly string map;
        readonly MeshFilter[] bodyFilters;
        readonly Mesh[] bodyMeshes;
        readonly bool originalEnabled;
        readonly List<object> windows = new List<object>();
        bool subscribed, changed, active;
        int eyeRenders, eyeLastFrame = -1, beforeEye, beforeFrame;
        long renderSequence, eyeLastSequence;
        string renderFailure;

        internal BusRoadGlassMirrorFrames(CabMirrors mirrors, CabInstance instance, Camera eye, RenderTexture target,
            Action check, Func<bool> owns, IDictionary<string, object> report)
        {
            this.mirrors = mirrors; this.instance = instance; this.eye = eye; this.target = target;
            this.check = check; this.owns = owns; this.report = report;
            check();
            Require(instance != null && instance.Alive && mirrors.Alive, "Driver mirror owner is unavailable");
            Require(ReferenceEquals(Field<Transform>(mirrors, "m_Root"), instance.Root), "Driver mirror root differs from the active cab");
            originals = Field<Array>(mirrors, "m_Mirrors");
            map = Field<string>(mirrors, "m_Map");
            Require(originals.Length > 0, "Driver has no production mirrors");
            entries = new Entry[originals.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                object source = originals.GetValue(i);
                var renderer = Field<Renderer>(source, "GlassRenderer");
                entries[i] = new Entry { Source = source, Camera = Field<Camera>(source, "Camera"),
                    Texture = Field<RenderTexture>(source, "Texture"), Material = Field<Material>(source, "Material"),
                    Renderer = renderer, Filter = renderer.GetComponent<MeshFilter>() };
                entries[i].Mesh = entries[i].Filter?.sharedMesh;
            }
            bodyFilters = Field<MeshFilter[]>(instance, "m_Filter");
            bodyMeshes = Field<CabModel>(instance, "m_Model").Meshes;
            originalEnabled = eye.enabled;
            report["eye_enabled_before"] = originalEnabled;
            report["quality"] = Field<object>(mirrors, "m_Quality").ToString();
            report["windows"] = windows;
            report["scope"] = "Existing production mirror cameras and materials; normal Unity camera rendering only";
            Validate();
            report["initial"] = Evidence();
        }

        internal void Start()
        {
            Validate();
            active = true;
            subscribed = true; RenderPipelineManager.endCameraRendering += Rendered;
            if (!eye.enabled) { changed = true; eye.enabled = true; }
        }

        internal void BeginWindow()
        {
            Validate(); beforeEye = eyeRenders; beforeFrame = Time.frameCount;
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(eye);
            foreach (Entry entry in entries)
            {
                Vector3 normal = entry.Filter.transform.TransformDirection(Vector3.back);
                Vector3 direction = (entry.Filter.transform.position - eye.transform.position).normalized;
                entry.Required = GeometryUtility.TestPlanesAABB(planes, entry.Renderer.bounds) &&
                    Vector3.Dot(Vector3.Reflect(direction, normal), normal) > .05f;
                entry.Before = entry.Renders;
            }
        }

        internal bool WindowReady
        {
            get
            {
                Validate();
                return eyeRenders - beforeEye >= 2 && entries.All(entry => !entry.Required ||
                    entry.Renders - entry.Before >= 2 && eyeLastFrame >= entry.LastFrame && eyeLastSequence > entry.LastSequence);
            }
        }

        internal void EndWindow()
        {
            Require(WindowReady, "Driver mirror render window is incomplete");
            windows.Add(new { from_frame = beforeFrame, to_frame = Time.frameCount,
                eye_renders = eyeRenders - beforeEye, state = Evidence() });
        }

        internal void Validate()
        {
            check();
            Require(renderFailure == null, renderFailure);
            Require(instance.Alive && instance.Visible && mirrors.Alive && eye != null && eye.targetTexture == target && target.IsCreated(),
                "Driver camera or mirror owner changed");
            Require(ReferenceEquals(originals, Field<Array>(mirrors, "m_Mirrors")) &&
                ReferenceEquals(instance.Root, Field<Transform>(mirrors, "m_Root")), "Production mirror group was replaced");
            Require(bodyFilters.Length == bodyMeshes.Length && bodyMeshes.Any(mesh => mesh != null), "Driver body mesh layout is unavailable");
            for (int i = 0; i < bodyFilters.Length; i++)
                Require(bodyFilters[i] == null ? bodyMeshes[i] == null : bodyFilters[i].sharedMesh == bodyMeshes[i],
                    "Driver body is not using its expected LOD0 meshes");
            for (int i = 0; i < entries.Length; i++)
            {
                Entry entry = entries[i];
                Require(ReferenceEquals(originals.GetValue(i), entry.Source) && entry.Camera != null && entry.Texture != null &&
                    entry.Texture.IsCreated() && entry.Camera.targetTexture == entry.Texture && entry.Material != null &&
                    entry.Material.HasProperty(map) && entry.Material.GetTexture(map) == entry.Texture && entry.Renderer != null &&
                    entry.Renderer.sharedMaterial == entry.Material && entry.Renderer.enabled && entry.Renderer.gameObject.activeInHierarchy &&
                    entry.Filter != null && entry.Mesh != null && entry.Mesh.vertexCount > 0 && entry.Filter.sharedMesh == entry.Mesh,
                    "Production mirror camera, material, texture or mesh changed");
            }
        }

        void Rendered(ScriptableRenderContext context, Camera camera)
        {
            if (!active) return;
            if (ReferenceEquals(camera, eye))
            {
                if (camera.targetTexture != target) { renderFailure = "Driver eye rendered to a different target"; return; }
                eyeRenders++; eyeLastFrame = Time.frameCount; eyeLastSequence = ++renderSequence;
            }
            foreach (Entry entry in entries)
                if (ReferenceEquals(camera, entry.Camera))
                {
                    if (camera.targetTexture != entry.Texture) { renderFailure = "Mirror rendered to a different target"; return; }
                    entry.Renders++; entry.LastFrame = Time.frameCount; entry.LastSequence = ++renderSequence;
                }
        }

        object Evidence() => new { eye_camera = eye.GetInstanceID(), eye_enabled = eye.enabled, eye_renders = eyeRenders,
            eye_last_frame = eyeLastFrame, eye_last_render_sequence = eyeLastSequence,
            body_lod = 0, body_lod_verified_from_mesh_references = true,
            mirrors = entries.Select((entry, index) => new { index, camera = entry.Camera.GetInstanceID(),
                camera_enabled = entry.Camera.enabled, target = entry.Texture.GetInstanceID(), entry.Texture.width, entry.Texture.height,
                material = entry.Material.GetInstanceID(), texture_slot = map, material_target_matches = entry.Material.GetTexture(map) == entry.Texture,
                renderer = entry.Renderer.GetInstanceID(), renderer_enabled = entry.Renderer.enabled,
                mesh = entry.Mesh.GetInstanceID(), mesh_name = entry.Mesh.name, vertices = entry.Mesh.vertexCount,
                visible_from_driver = entry.Required, renders = entry.Renders, window_renders = entry.Renders - entry.Before,
                last_frame = entry.LastFrame, last_render_sequence = entry.LastSequence }).ToArray() };

        public void Dispose()
        {
            active = false;
            var failures = new List<Exception>();
            if (subscribed && CabCleanup.Try(() => RenderPipelineManager.endCameraRendering -= Rendered, failures)) subscribed = false;
            if (changed)
            {
                try
                {
                    if (!owns()) { report["restore_skipped_after_ownership_change"] = true; changed = false; }
                    else if (CabCleanup.Try(() => eye.enabled = originalEnabled, failures)) changed = false;
                }
                catch (Exception error) { failures.Add(error); }
            }
            report["cleanup"] = new { event_removed = !subscribed, enabled_restored_or_ownership_released = !changed,
                errors = failures.Select(error => error.ToString()).ToArray() };
            if (failures.Count != 0) { CabCleanup.Retain(this); throw new AggregateException("Driver mirror observation cleanup failed", failures); }
        }

        static T Field<T>(object owner, string name)
        {
            FieldInfo field = owner?.GetType().GetField(name, Fields);
            if (field == null || !(field.GetValue(owner) is T value))
                throw new InvalidOperationException("Production mirror field is unavailable: " + name);
            return value;
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
#endif
