#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Linq;
using CDLC.BusDriver.Interior;
using UnityEngine;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    internal sealed class BusRoadGlassCameraFrames : IDisposable
    {
        readonly Camera camera;
        readonly RenderTexture target;
        readonly Action check;
        readonly Func<bool> owns;
        readonly IDictionary<string, object> report;
        readonly bool originalEnabled;
        readonly List<object> windows = new List<object>();
        bool subscribed, changed, active;
        int renders, lastFrame = -1, before, firstFrame;
        string failure;

        internal BusRoadGlassCameraFrames(Camera camera, RenderTexture target, Action check,
            Func<bool> owns, IDictionary<string, object> report)
        {
            this.camera = camera; this.target = target; this.check = check; this.owns = owns; this.report = report;
            Validate(); originalEnabled = camera.enabled;
            report["camera_id"] = camera.GetInstanceID(); report["target_id"] = target.GetInstanceID();
            report["enabled_before"] = originalEnabled; report["windows"] = windows;
            report["scope"] = "Owned inspection eye uses normal Unity rendering throughout driver and native AI captures";
        }

        internal void Start()
        {
            Validate(); active = true;
            subscribed = true; RenderPipelineManager.endCameraRendering += Rendered;
            if (!camera.enabled) { changed = true; camera.enabled = true; }
        }

        internal void Validate()
        {
            check();
            if (failure != null) throw new InvalidOperationException(failure);
            if (camera == null || target == null || camera.targetTexture != target || !target.IsCreated() || active && !camera.enabled)
                throw new InvalidOperationException("Owned inspection eye or render target changed");
        }

        internal void BeginWindow() { Validate(); before = renders; firstFrame = Time.frameCount; }
        internal bool WindowReady { get { Validate(); return renders - before >= 2 && lastFrame > firstFrame; } }
        internal void EndWindow()
        {
            if (!WindowReady) throw new InvalidOperationException("Owned inspection camera did not complete a real render window");
            windows.Add(new { from_frame = firstFrame, to_frame = Time.frameCount, last_render_frame = lastFrame,
                completed_renders = renders - before, camera_enabled = camera.enabled });
        }

        void Rendered(ScriptableRenderContext context, Camera value)
        {
            if (!active || !ReferenceEquals(value, camera)) return;
            if (value.targetTexture != target) { failure = "Owned inspection camera rendered to a different target"; return; }
            renders++; lastFrame = Time.frameCount;
        }

        public void Dispose()
        {
            active = false;
            var errors = new List<Exception>();
            if (subscribed && CabCleanup.Try(() => RenderPipelineManager.endCameraRendering -= Rendered, errors)) subscribed = false;
            if (changed)
            {
                try
                {
                    if (!owns()) { report["restore_skipped_after_ownership_change"] = true; changed = false; }
                    else if (CabCleanup.Try(() => camera.enabled = originalEnabled, errors)) changed = false;
                }
                catch (Exception error) { errors.Add(error); }
            }
            report["cleanup"] = new { event_removed = !subscribed, enabled_restored_or_ownership_released = !changed,
                errors = errors.Select(error => error.ToString()).ToArray() };
            if (errors.Count != 0) { CabCleanup.Retain(this); throw new AggregateException("Owned inspection camera cleanup failed", errors); }
        }
    }
}
#endif
