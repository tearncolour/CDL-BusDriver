#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal sealed class BusGlassOpticsExposure : IDisposable
    {
        HDAdditionalCameraData camera;
        LayerMask layers;
        Transform anchor;
        GameObject holder;
        VolumeProfile profile;
        bool changed;

        internal void Activate(Camera target, float ev)
        {
            camera = target.GetComponent<HDAdditionalCameraData>();
            if (camera == null) throw new InvalidOperationException("Optics camera has no HDRP data");
            foreach (Volume volume in Resources.FindObjectsOfTypeAll<Volume>())
                if (volume != null && volume.enabled && volume.gameObject.activeInHierarchy && volume.gameObject.layer == 31)
                    throw new InvalidOperationException("Optics private exposure layer is already occupied");
            layers = camera.volumeLayerMask; anchor = camera.volumeAnchorOverride;
            holder = new GameObject("CDLC optics camera exposure"); holder.hideFlags = HideFlags.DontSave; holder.layer = 31;
            holder.transform.position = target.transform.position;
            var bounds = holder.AddComponent<BoxCollider>(); bounds.isTrigger = true; bounds.size = Vector3.one * 2f;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var exposure = profile.Add<Exposure>(true);
            exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(ev); exposure.compensation.Override(0f);
            var ownVolume = holder.AddComponent<Volume>();
            ownVolume.isGlobal = false; ownVolume.blendDistance = 0; ownVolume.weight = 1; ownVolume.priority = float.MaxValue;
            ownVolume.sharedProfile = profile;
            changed = true;
            camera.volumeLayerMask = layers.value | (1 << 31); camera.volumeAnchorOverride = target.transform;
        }

        internal readonly struct GpuExposure
        {
            internal readonly float Multiplier, Ev100;
            internal readonly string Texture;
            internal readonly object Readback;
            internal GpuExposure(float multiplier, float ev, string texture, object readback)
            { Multiplier = multiplier; Ev100 = ev; Texture = texture; Readback = readback; }
            internal object Evidence() => new { multiplier = Multiplier, ev100 = Ev100, texture = Texture,
                readback = Readback,
                source = "GPU RGFloat texel of this HDCamera's current exposure texture" };
        }

        internal static async Task<GpuExposure> Read(Camera target, Action check, IDictionary<string, object> evidence)
        {
            check();
            evidence["camera_id"] = target.GetInstanceID();
            evidence["camera_enabled"] = target.enabled;
            evidence["camera_active"] = target.gameObject.activeInHierarchy;
            evidence["camera_type"] = target.cameraType.ToString();
            evidence["persistent_history"] = target.GetComponent<HDAdditionalCameraData>()?.hasPersistentHistory;
            evidence["read_frame"] = Time.frameCount;
            evidence["camera_target_id"] = target.targetTexture == null ? (int?)null : target.targetTexture.GetInstanceID();
            if (!SystemInfo.supportsAsyncGPUReadback) throw new NotSupportedException("Actual exposure readback is unavailable");
            var hd = HDCamera.GetOrCreate(target);
            evidence["actual_exposure_control"] = hd.frameSettings.IsEnabled(FrameSettingsField.ExposureControl);
            var exposure = hd.volumeStack?.GetComponent<Exposure>();
            evidence["exposure_mode"] = exposure?.mode.value.ToString();
            PropertyInfo storageProperty = typeof(HDCamera).GetProperty("currentExposureTextures", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new NotSupportedException("HDRP camera exposure contract changed");
            object storage = storageProperty.GetValue(hd);
            Type type = storage.GetType();
            FieldInfo ownField = type.GetField("useCurrentCamera"), currentField = type.GetField("current");
            evidence["camera_owns_exposure"] = ownField?.FieldType == typeof(bool) ? ownField.GetValue(storage) : null;
            if (ownField?.FieldType != typeof(bool) || currentField?.FieldType != typeof(RTHandle) || !(bool)ownField.GetValue(storage))
                throw new NotSupportedException("Optics requires this camera's independent exposure texture");
            var texture = (RTHandle)currentField.GetValue(storage);
            evidence["exposure_handle_present"] = texture != null;
            evidence["exposure_texture_present"] = texture?.rt != null;
            if (texture?.rt == null) throw new InvalidOperationException("Camera has no actual exposure texture");
            string textureName = texture.rt.name;
            GpuExposure measured = await BusGlassOpticsReadback.Read(texture.rt, GraphicsFormat.R32G32_SFloat, request =>
            {
                var data = request.GetData<float>();
                if (data.Length != 2 || !Finite(data[0]) || data[0] <= 0 || !Finite(data[1]))
                    throw new InvalidOperationException("Exposure GPU texel is invalid");
                return new GpuExposure(data[0], data[1], textureName, evidence);
            }, check, evidence);
            evidence["gpu_multiplier"] = measured.Multiplier;
            evidence["gpu_ev100"] = measured.Ev100;
            return measured;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public void Dispose()
        {
            var failures = new List<Exception>();
            if (changed)
            {
                bool restored = true;
                if (camera != null)
                {
                    restored &= CabCleanup.Try(() => camera.volumeLayerMask = layers, failures);
                    restored &= CabCleanup.Try(() => camera.volumeAnchorOverride = anchor, failures);
                }
                if (restored) changed = false;
            }
            if (!changed && holder != null)
            {
                CabCleanup.Try(() => holder.SetActive(false), failures);
                if (CabCleanup.Try(() => UnityEngine.Object.Destroy(holder), failures)) holder = null;
            }
            if (!changed && holder == null && profile != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(profile), failures)) profile = null;
            if (failures.Count > 0) { CabCleanup.Retain(this); throw new AggregateException("Optics exposure cleanup failed", failures); }
        }
    }
}
#endif
