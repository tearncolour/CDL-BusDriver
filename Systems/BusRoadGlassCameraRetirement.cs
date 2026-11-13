#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.IO;
using CDLC.BusDriver.Interior;
using Game.Rendering;
using Newtonsoft.Json;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    internal sealed class BusRoadGlassCameraRetirement : IDisposable
    {
        World world;
        CameraUpdateSystem cameras;
        GameObject cameraObject;
        RenderTexture texture;
        readonly string path;
        readonly IDictionary<string, object> report;
        bool subscribed, textureReleased, complete;
        double retryAt;
        int attempts;
        string previousError;

        internal BusRoadGlassCameraRetirement(World world, CameraUpdateSystem cameras, GameObject cameraObject,
            RenderTexture texture, string path, IDictionary<string, object> report)
        {
            this.world = world; this.cameras = cameras; this.cameraObject = cameraObject;
            this.texture = texture; this.path = path; this.report = report;
            report["camera_object_id"] = cameraObject.GetInstanceID();
            report["texture_id"] = texture == null ? (int?)null : texture.GetInstanceID();
            report["pending"] = true; report["attempts"] = 0;
            report["scope"] = "Only the test-owned camera and render texture; user camera/controller state is never changed";
        }

        bool Referenced()
        {
            if (world == null || !world.IsCreated || cameraObject == null || cameras == null) return false;
            Camera camera = cameraObject.GetComponent<Camera>();
            return camera != null && (cameras.activeCamera == camera || cameras.activeViewer?.camera == camera);
        }

        public void Dispose()
        {
            if (complete) return;
            try
            {
                if (Referenced())
                {
                    Subscribe();
                    report["event_removed"] = false;
                    if (!report.TryGetValue("status", out object status) || !Equals(status, "DEFERRED_ACTIVE_CAMERA"))
                    { report["status"] = "DEFERRED_ACTIVE_CAMERA"; Persist(); }
                    return;
                }
                attempts++; report["attempts"] = attempts;
                var errors = new List<Exception>();
                bool detached = true;
                if (cameraObject != null)
                {
                    Camera camera = cameraObject.GetComponent<Camera>();
                    if (camera != null)
                    {
                        detached = CabCleanup.Try(() => camera.enabled = false, errors);
                        if (detached && camera.targetTexture == texture)
                            detached = CabCleanup.Try(() => camera.targetTexture = null, errors);
                    }
                    if (detached && CabCleanup.Try(() => UnityEngine.Object.Destroy(cameraObject), errors)) cameraObject = null;
                }
                if (cameraObject == null && texture != null)
                {
                    if (!textureReleased && CabCleanup.Try(texture.Release, errors)) textureReleased = true;
                    if (textureReleased && CabCleanup.Try(() => UnityEngine.Object.Destroy(texture), errors)) texture = null;
                }
                if (cameraObject == null && texture == null && subscribed &&
                    CabCleanup.Try(() => RenderPipelineManager.endFrameRendering -= Frame, errors)) subscribed = false;
                if (errors.Count > 0) throw new AggregateException("Deferred test camera cleanup failed", errors);
                complete = cameraObject == null && texture == null && !subscribed;
                report["status"] = complete ? "RELEASED" : "PENDING";
                report["pending"] = !complete;
                report["event_removed"] = !subscribed;
                if (complete) { world = null; cameras = null; }
                Persist();
            }
            catch (Exception error)
            {
                RecordFailure(error);
                throw;
            }
        }

        void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            try { RenderPipelineManager.endFrameRendering += Frame; }
            catch { subscribed = false; throw; }
        }

        void Frame(ScriptableRenderContext context, Camera[] rendered)
        {
            try
            {
                if (Time.realtimeSinceStartupAsDouble < retryAt || Referenced()) return;
                Dispose();
            }
            catch (Exception error) { RecordFailure(error); }
        }

        void RecordFailure(Exception error)
        {
            retryAt = Time.realtimeSinceStartupAsDouble + 1;
            report["status"] = "RETRY_PENDING"; report["pending"] = true;
            string text = error.ToString();
            Exception cause = error.GetBaseException();
            string key = cause.GetType().FullName + ": " + cause.Message;
            if (key != previousError)
            {
                previousError = key; report["error"] = text; Persist();
                Mod.log.Error(error, "Deferred test camera cleanup requires retry");
            }
            try { Subscribe(); }
            catch (Exception subscribeError) { report["event_error"] = subscribeError.ToString(); CabCleanup.Retain(this); }
        }

        void Persist()
        {
            try { File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented)); }
            catch (Exception error)
            {
                report["evidence_error"] = error.ToString();
                Mod.log.Error(error, "Could not write deferred test camera cleanup evidence");
            }
        }
    }
}
#endif
