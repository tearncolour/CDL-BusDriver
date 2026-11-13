using System;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver.Interior
{
    /// <summary>
    /// The exterior mirrors as real mirrors in the driver's view: a small camera per mirror at its glass, looking along the
    /// driver's line of sight reflected in the glass (a slightly convex bus mirror: a wider view than a flat one), into a low
    /// resolution texture shown on the glass, flipped like a mirror image. The cameras take turns (one at most a frame) and only
    /// mirrors whose glass is in the driver's view render (looking into the saloon, none does; outside the cab view, or with the
    /// option off, the caller passes show false and none does either). How often, how large and what they leave out follows the
    /// option "Mirror quality" (MirrorBudget has the rates):
    ///   High   - each mirror 20 times a second on its own (MirrorBudget.kMirrorHz), 192 x 256 (convex 192 x 128), skipping s_Skipped
    ///            (shadows, volumetrics and clouds, screen-space effects, motion vectors and blur, depth of field).
    ///   Medium - 30 renders a second shared by all the mirrors in view (kMediumHz), the same size, also skipping s_SkippedReduced
    ///            (distortion, custom passes and post-processes, subsurface scattering, planar probes, lens effects, vignette, grain,
    ///            dithering) and with the level of detail bias at kMediumLodBias of the game's.
    ///   Low    - 15 renders a second shared (kLowHz), half the size (96 x 128, convex 96 x 64), also skipping s_SkippedLow (decals,
    ///            so no road markings in the mirrors, refraction, transmission, bloom), the bias at kLowLodBias.
    /// The far clip is kFar on every level. A change of the level applies at the next shown frame (textures remade at a new size,
    /// the cameras' frame settings set again). Only what the game itself has prepared for the frame can show: it prepares the
    /// world around the main view (its own levels of detail too), so traffic right behind the bus may be missing. Main thread only.
    /// </summary>
    internal sealed class CabMirrors
    {
        private const int kWidth = 192, kHeight = 256, kConvexHeight = 128;   // the image on High and Medium (Low: MirrorBudget.kLowScale)
        private const float kFovDeg = 24f, kConvexFovDeg = 55f;
        private const float kFar = 250f;
        // Unity's level of detail bias (the game's LODGroups: the riders' figures) on Medium and Low, a scale of the quality setting's
        // own: the mirrors' narrow view would otherwise pick finer levels than the main view. The game's own objects choose their
        // levels from the main view and do not follow it.
        private const float kMediumLodBias = 0.5f, kLowLodBias = 0.25f;
        private static readonly Plane[] s_Planes = new Plane[6];

        // What every mirror camera skips (High: as before the option).
        private static readonly FrameSettingsField[] s_Skipped =
        {
            FrameSettingsField.ShadowMaps, FrameSettingsField.ContactShadows, FrameSettingsField.ScreenSpaceShadows, FrameSettingsField.SSR,
            FrameSettingsField.TransparentSSR, FrameSettingsField.SSAO, FrameSettingsField.SSGI, FrameSettingsField.Volumetrics,
            FrameSettingsField.ReprojectionForVolumetrics, FrameSettingsField.VolumetricClouds, FrameSettingsField.MotionVectors,
            FrameSettingsField.ObjectMotionVectors, FrameSettingsField.MotionBlur, FrameSettingsField.DepthOfField,
        };

        // What Medium and Low skip as well: nothing a small, briefly seen mirror image shows (the game's custom passes are the selection
        // outlines and world icons, the underground view and the depth of field's focus; it has no custom post-processes and no planar
        // probes or lens flares). Exposure, tonemapping, colour grading, fog and the sky stay, so the image matches the main view.
        private static readonly FrameSettingsField[] s_SkippedReduced =
        {
            FrameSettingsField.TransparentsWriteMotionVector, FrameSettingsField.TransparentPostpass, FrameSettingsField.Distortion,
            FrameSettingsField.RoughDistortion, FrameSettingsField.CustomPass, FrameSettingsField.CustomPostProcess,
            FrameSettingsField.SubsurfaceScattering, FrameSettingsField.PlanarProbe, FrameSettingsField.LensFlareDataDriven,
            FrameSettingsField.LensDistortion, FrameSettingsField.ChromaticAberration, FrameSettingsField.PaniniProjection,
            FrameSettingsField.Vignette, FrameSettingsField.FilmGrain, FrameSettingsField.Dithering,
        };

        // What Low skips as well: visible, but small in a quarter-size image (the decals are the road markings).
        private static readonly FrameSettingsField[] s_SkippedLow =
        {
            FrameSettingsField.Decals, FrameSettingsField.DecalLayers, FrameSettingsField.Refraction, FrameSettingsField.Transmission,
            FrameSettingsField.Bloom,
        };

        private sealed class Mirror
        {
            internal Camera Camera;
            internal HDAdditionalCameraData Hd;
            internal RenderTexture Texture;
            internal Material Material;
            internal GameObject Glass;
            internal float3 Centre;          // model space
            internal quaternion Frame;       // the glass faces -Z of it
            internal Renderer GlassRenderer;
            internal bool Convex;            // the oval blind-spot mirror: a wider view, a lower image
            internal Vector3 Position;       // where its camera goes this frame (Place)
            internal Quaternion Rotation;
            // what the camera and the glass were last set to (Create: the camera off, the glass shown): only a change is written
            internal bool Rendering, Shown = true;
        }

        private Mirror[] m_Mirrors = Array.Empty<Mirror>();
        private int m_Count;
        private Transform m_Root;
        private MirrorBudget m_Budget = new MirrorBudget(0);
        // the level the mirrors are made at: kept over Destroy, so the next bus's mirrors start at the level last asked for
        private MirrorQuality m_Quality = MirrorQuality.High;
        private string m_Map = "_UnlitColorMap";
        private bool m_Failed;
        private static readonly System.Collections.Generic.Dictionary<int, Mesh> s_AuthoredGlass = new System.Collections.Generic.Dictionary<int, Mesh>();

        internal bool Alive => m_Root != null && m_Mirrors.Length > 0 && m_Mirrors[0] != null && m_Mirrors[0].Glass != null;

        /// <summary>Makes the mirrors on a bus model (its root), for its spec. False when the pipeline does not allow it.</summary>
        internal bool Create(Transform root, in BusInteriorSpec spec)
        {
            Destroy();
            if (m_Failed || root == null)
                return false;

            try
            {
                Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("HDRP/Lit");
                if (shader == null)
                {
                    m_Failed = true;
                    Mod.log.Warn("Mirrors: no HDRP shader to show them; the mirrors stay plain.");
                    return false;
                }

                m_Root = root;
                VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
                bool authored = model != null;
                m_Count = authored ? model.MirrorCount : 3;
                m_Mirrors = new Mirror[m_Count];
                m_Budget = new MirrorBudget(m_Count) { Quality = m_Quality };
                m_Map = shader.name == "HDRP/Unlit" ? "_UnlitColorMap" : "_BaseColorMap";
                for (int i = 0; i < m_Count; i++)
                {
                    float3 centre;
                    quaternion frame;
                    float2 size;
                    bool convex = false;
                    if (authored)
                    {
                        MirrorSurface surface = MirrorSurface.Of(spec, i);
                        centre = surface.OverlayCentre; frame = surface.Frame; size = surface.Size;
                        convex = surface.Oval;
                    }
                    else
                    {
                        MirrorSurface surface = BusInterior.GenericMirror(spec, i);
                        centre = surface.OverlayCentre; frame = surface.Frame; size = surface.Size; convex = surface.Oval;
                    }
                    var m = new Mirror { Centre = centre, Frame = frame, Convex = convex };
                    m.Texture = NewTexture(i, convex, m_Quality);
                    m.Material = new Material(shader) { name = "Bus Driver mirror " + i, hideFlags = HideFlags.DontSave };
                    string map = m_Map;
                    if (m.Material.HasProperty(map))
                    {
                        m.Material.SetTexture(map, m.Texture);
                        m.Material.SetTextureScale(map, new Vector2(-1f, 1f));      // a mirror image
                        m.Material.SetTextureOffset(map, new Vector2(1f, 0f));
                    }
                    if (m.Material.HasProperty("_UnlitColor")) m.Material.SetColor("_UnlitColor", Color.white);
                    if (m.Material.HasProperty("_BaseColor")) m.Material.SetColor("_BaseColor", Color.white);
                    if (m.Material.HasProperty("_Smoothness")) m.Material.SetFloat("_Smoothness", 0.95f);
                    try { HDMaterial.ValidateMaterial(m.Material); } catch { }

                    m.Glass = new GameObject("Bus Driver mirror glass " + i) { hideFlags = HideFlags.DontSave, layer = root.gameObject.layer };
                    m.Glass.transform.SetParent(root, false);
                    m.Glass.transform.localPosition = new Vector3(centre.x, centre.y, centre.z);
                    m.Glass.transform.localRotation = new Quaternion(frame.value.x, frame.value.y, frame.value.z, frame.value.w);
                    m.Glass.transform.localScale = Vector3.one;
                    m.Glass.AddComponent<MeshFilter>().sharedMesh = AuthoredGlass(spec, i);
                    var r = m.Glass.AddComponent<MeshRenderer>();
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    r.lightProbeUsage = LightProbeUsage.Off;
                    r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    r.sharedMaterial = m.Material;
                    m.GlassRenderer = r;

                    var go = new GameObject("Bus Driver mirror camera " + i) { hideFlags = HideFlags.DontSave };
                    go.transform.SetParent(root, false);
                    m.Camera = go.AddComponent<Camera>();
                    m.Camera.enabled = false;
                    m.Camera.targetTexture = m.Texture;
                    m.Camera.fieldOfView = convex ? kConvexFovDeg : kFovDeg;
                    m.Camera.aspect = size.x / size.y;
                    m.Camera.nearClipPlane = 0.15f;
                    m.Camera.farClipPlane = kFar;
                    m.Camera.depth = -10f;
                    m.Camera.allowMSAA = false;
                    m.Camera.allowHDR = false;
                    m.Camera.useOcclusionCulling = false;
                    m.Hd = go.AddComponent<HDAdditionalCameraData>();
                    Cheap(m.Hd, m_Quality);
                    m_Mirrors[i] = m;
                }

                Mod.log.Info($"Mirrors: {m_Count} reflection cameras ({shader.name}), quality {m_Quality}.");
                return true;
            }
            catch (Exception ex)
            {
                m_Failed = true;
                Mod.log.Warn($"Mirrors unavailable: {ex.GetType().Name}: {ex.Message}");
                Destroy();
                return false;
            }
        }

        /// <summary>
        /// The mirror cameras skip what a small mirror image does not need, more of it on the lower levels. Set again on a change of
        /// the level: a pass a higher level keeps is no longer overridden (the default frame settings again).
        /// </summary>
        private static void Cheap(HDAdditionalCameraData hd, MirrorQuality quality)
        {
            hd.customRenderingSettings = true;
            FrameSettings fs = hd.renderingPathCustomFrameSettings;
            FrameSettingsOverrideMask mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            Skip(ref fs, ref mask, s_Skipped, true);
            Skip(ref fs, ref mask, s_SkippedReduced, quality != MirrorQuality.High);
            Skip(ref fs, ref mask, s_SkippedLow, quality == MirrorQuality.Low);
            bool biased = quality != MirrorQuality.High;
            mask.mask[(uint)FrameSettingsField.LODBiasMode] = biased;
            mask.mask[(uint)FrameSettingsField.LODBias] = biased;
            fs.lodBiasMode = biased ? LODBiasMode.ScaleQualitySettings : LODBiasMode.FromQualitySettings;
            fs.lodBias = quality == MirrorQuality.Low ? kLowLodBias : quality == MirrorQuality.Medium ? kMediumLodBias : 1f;
            hd.renderingPathCustomFrameSettings = fs;
            hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            hd.volumeLayerMask = ~0;
        }

        private static void Skip(ref FrameSettings fs, ref FrameSettingsOverrideMask mask, FrameSettingsField[] fields, bool skip)
        {
            foreach (FrameSettingsField f in fields)
            {
                mask.mask[(uint)f] = skip;
                fs.SetEnabled(f, !skip);
            }
        }

        private static RenderTexture NewTexture(int index, bool convex, MirrorQuality quality)
        {
            var texture = new RenderTexture(MirrorBudget.Pixels(kWidth, quality), MirrorBudget.Pixels(convex ? kConvexHeight : kHeight, quality), 24,
                RenderTextureFormat.ARGB32) { name = "Bus Driver mirror " + index, hideFlags = HideFlags.DontSave };
            texture.Create();
            return texture;
        }

        /// <summary>
        /// The mirrors made at another level while they exist: a texture of another size in place of the old one (released), the
        /// frame settings and the budget's rates set again. The new texture is blank until its mirror's next image.
        /// </summary>
        private void Apply(MirrorQuality quality)
        {
            m_Quality = quality;
            m_Budget.Quality = quality;
            for (int i = 0; i < m_Count; i++)
            {
                Mirror m = m_Mirrors[i];
                if (m == null)
                    continue;
                int width = MirrorBudget.Pixels(kWidth, quality), height = MirrorBudget.Pixels(m.Convex ? kConvexHeight : kHeight, quality);
                if (m.Texture == null || m.Texture.width != width || m.Texture.height != height)
                {
                    RenderTexture old = m.Texture;
                    m.Texture = NewTexture(i, m.Convex, quality);
                    if (m.Camera != null)
                        m.Camera.targetTexture = m.Texture;
                    if (m.Material != null && m.Material.HasProperty(m_Map))
                        m.Material.SetTexture(m_Map, m.Texture);
                    if (old != null)
                    {
                        old.Release();
                        UnityEngine.Object.Destroy(old);
                    }
                }
                if (m.Hd != null)
                    Cheap(m.Hd, quality);
            }
            Mod.log.Info($"Mirrors: quality {quality} ({(quality == MirrorQuality.High ? MirrorBudget.kMirrorHz + " images a second each" : MirrorBudget.SharedHz(quality) + " images a second shared")}, " +
                $"{MirrorBudget.Pixels(kWidth, quality)} x {MirrorBudget.Pixels(kHeight, quality)}).");
        }

        /// <summary>
        /// Every frame in the driver's view: at most one mirror's camera placed on the reflected line of sight and rendered this frame,
        /// when the budget of the level has one (the others keep their last image). show false: no mirror camera renders, and the
        /// level is left as it is (applied at the next shown frame). The cameras and the glass are switched only when that changes
        /// (each frame's off-and-on again wrote both every frame). quality: the option's level (the probe's default: High).
        /// </summary>
        internal void Update(Camera eye, bool show, MirrorQuality quality = MirrorQuality.High)
        {
            if (!Alive)
                return;

            if (show && quality != m_Quality)
                Apply(quality);

            // the glass first: the mirror due is picked by its renderer's bounds, with the glass as it is this frame
            for (int i = 0; i < m_Count; i++)
            {
                Mirror mirror = m_Mirrors[i];
                if (mirror == null || mirror.Shown == show) continue;
                mirror.Glass.SetActive(show);
                mirror.Shown = show;
            }

            Mirror render = show && eye != null ? Place(eye) : null;
            for (int i = 0; i < m_Count; i++)
            {
                Mirror mirror = m_Mirrors[i];
                bool on = mirror != null && mirror == render;
                if (mirror == null || mirror.Rendering == on) continue;
                mirror.Camera.enabled = on;
                mirror.Rendering = on;
            }
        }

        /// <summary>The mirror to render this frame, its camera placed on the reflected line of sight; null: none.</summary>
        private Mirror Place(Camera eye)
        {
            // only when the budget has a render now: then the mirrors whose glass the driver sees from the front, and of those the
            // next one in turn (MirrorBudget)
            double now = Time.unscaledTimeAsDouble;
            if (!m_Budget.AnyDue(now))
                return null;
            GeometryUtility.CalculateFrustumPlanes(eye, s_Planes);
            Vector3 eyePosition = eye.transform.position;
            int inView = 0;
            for (int i = 0; i < m_Count && i < m_Budget.Count; i++)
            {
                Mirror candidate = m_Mirrors[i];
                if (candidate == null || candidate.GlassRenderer == null || !GeometryUtility.TestPlanesAABB(s_Planes, candidate.GlassRenderer.bounds))
                    continue;
                if (Reflect(candidate, eyePosition))
                    inView |= 1 << i;
            }
            int pick = m_Budget.Take(now, inView);
            if (pick < 0)
                return null;
            Mirror m = m_Mirrors[pick];
            m.Camera.transform.SetPositionAndRotation(m.Position, m.Rotation);
            return m;
        }

        // Where the mirror's camera looks from the eye (Position, Rotation); false: the eye behind the glass, nothing to reflect.
        private bool Reflect(Mirror m, Vector3 eye)
        {
            Quaternion root = m_Root.rotation;
            Vector3 c = m_Root.TransformPoint(new Vector3(m.Centre.x, m.Centre.y, m.Centre.z));
            Quaternion frame = root * new Quaternion(m.Frame.value.x, m.Frame.value.y, m.Frame.value.z, m.Frame.value.w);
            Vector3 n = frame * Vector3.back;                     // out of the glass, towards the driver
            Vector3 d = (c - eye).normalized;
            Vector3 r = Vector3.Reflect(d, n);
            if (Vector3.Dot(r, n) <= 0.05f)
                return false;

            Vector3 up = Vector3.ProjectOnPlane(frame * Vector3.up, r);
            m.Position = c + r * 0.04f;
            m.Rotation = Quaternion.LookRotation(r, up.sqrMagnitude > 1e-6f ? up : Vector3.up);
            return true;
        }

        internal void Destroy()
        {
            for (int i = 0; i < m_Mirrors.Length; i++)
            {
                Mirror m = m_Mirrors[i];
                if (m == null) continue;
                if (m.Camera != null) UnityEngine.Object.Destroy(m.Camera.gameObject);
                if (m.Glass != null) UnityEngine.Object.Destroy(m.Glass);
                if (m.Material != null) UnityEngine.Object.Destroy(m.Material);
                if (m.Texture != null)
                {
                    m.Texture.Release();
                    UnityEngine.Object.Destroy(m.Texture);
                }
                m_Mirrors[i] = null;
            }

            m_Root = null;
            m_Count = 0;
            m_Budget = new MirrorBudget(0);
        }

        // The camera overlays follow the actual pod outline; a rectangular
        // quad would extend past the tapered main mirrors and the oval rim.
        private static Mesh AuthoredGlass(in BusInteriorSpec spec, int index)
        {
            int key = ((int)spec.ModelVariant << 8) | index;
            if (s_AuthoredGlass.TryGetValue(key, out Mesh cached) && cached != null) return cached;
            MirrorSurface surface = MirrorSurface.Of(spec, index);
            int n = surface.Segments, rings = surface.RadialRings;
            var vertices = new Vector3[1 + n * rings];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var indices = new System.Collections.Generic.List<int>(n * (6 * rings - 3));
            vertices[0] = new Vector3(0f, 0f, -surface.ConvexDepth);
            normals[0] = Vector3.back; uv[0] = new Vector2(0.5f, 0.5f);
            for (int ring = 1; ring <= rings; ring++)
            {
                float radius = ring / (float)rings;
                for (int i = 0; i < n; i++)
                {
                    float3 p = surface.SurfacePoint(i, radius);
                    int v = 1 + (ring - 1) * n + i;
                    vertices[v] = p;
                    float3 normal = surface.SurfaceNormal(p);
                    normals[v] = new Vector3(normal.x, normal.y, normal.z);
                    uv[v] = new Vector2(Mathf.Clamp01(p.x / surface.Size.x + 0.5f), Mathf.Clamp01(p.y / surface.Size.y + 0.5f));
                }
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                indices.Add(0); indices.Add(1 + j); indices.Add(1 + i);
            }
            for (int ring = 0; ring < rings - 1; ring++)
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n, a = 1 + ring * n, b = a + n;
                    indices.Add(a + i); indices.Add(a + j); indices.Add(b + j);
                    indices.Add(a + i); indices.Add(b + j); indices.Add(b + i);
                }
            var mesh = new Mesh { name = "Authored mirror reflection " + index, hideFlags = HideFlags.DontSave };
            mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            s_AuthoredGlass[key] = mesh;
            return mesh;
        }

    }
}
