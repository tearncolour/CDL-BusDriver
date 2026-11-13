#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    internal sealed class BusGlassWitnessDiagnostics : IDisposable
    {
        sealed class ReadResult { internal float[] Pixels; internal Exception Error; }
        sealed class Pending
        {
            internal readonly TaskCompletionSource<ReadResult> Completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal volatile bool CallbackPending;
            internal bool EnqueueConfirmed;
            internal int Frame;
            internal readonly IDictionary<string, object> Evidence;
            internal Pending(IDictionary<string, object> evidence) { Evidence = evidence; }
        }
        sealed class CopyPass : CustomPass
        {
            readonly BusGlassWitnessDiagnostics owner;
            internal CopyPass(BusGlassWitnessDiagnostics owner)
            {
                this.owner = owner; name = "CDLC witness raw colour copy";
                targetColorBuffer = TargetBuffer.None; targetDepthBuffer = TargetBuffer.None; clearFlags = ClearFlag.None;
            }
            protected override void Execute(CustomPassContext context) { owner.Copy(context); }
        }

        readonly Camera camera;
        readonly RenderTexture target;
        readonly MeshRenderer card, box;
        readonly Material originalCard, originalBox;
        readonly MaterialPropertyBlock originalBlock = new MaterialPropertyBlock();
        readonly bool boxActive;
        readonly Action check;
        readonly IDictionary<string, object> report;
        readonly List<object> captures = new List<object>();
        readonly Vector3 position;
        readonly Quaternion rotation;
        readonly float fov, aspect;
        readonly int width, height, cardX, cardY, boxX, boxY;
        readonly Mesh cardMesh, boxMesh;
        readonly Matrix4x4 cardMatrix, boxMatrix;
        readonly HDAdditionalCameraData cameraData;
        readonly FrameSettings originalSettings;
        readonly FrameSettingsOverrideMask originalMask;
        GameObject holder;
        CopyPass pass;
        RTHandle copy;
        Material calibration;
        Pending pending;
        bool stopping, restored, blockChanged, boxChanged, settingsChanged;

        internal BusGlassWitnessDiagnostics(Camera camera, GameObject cardObject, GameObject boxObject,
            int cardX, int cardY, int boxX, int boxY, Action check, IDictionary<string, object> report)
        {
            this.camera = camera; target = camera.targetTexture; this.check = check; this.report = report;
            card = cardObject.GetComponent<MeshRenderer>(); box = boxObject.GetComponent<MeshRenderer>();
            originalCard = card.sharedMaterial; originalBox = box.sharedMaterial; card.GetPropertyBlock(originalBlock);
            cardMesh = card.GetComponent<MeshFilter>().sharedMesh; boxMesh = box.GetComponent<MeshFilter>().sharedMesh;
            cardMatrix = card.transform.localToWorldMatrix; boxMatrix = box.transform.localToWorldMatrix;
            boxActive = boxObject.activeSelf;
            position = camera.transform.position; rotation = camera.transform.rotation; fov = camera.fieldOfView; aspect = camera.aspect;
            width = target.width; height = target.height;
            cameraData = camera.GetComponent<HDAdditionalCameraData>();
            originalSettings = cameraData.renderingPathCustomFrameSettings; originalMask = cameraData.renderingPathCustomFrameSettingsOverrideMask;
            this.cardX = cardX; this.cardY = cardY; this.boxX = boxX; this.boxY = boxY;
            report["scope"] = "Independent input calibration of private witness materials only; raw HDR is linear before post-processing and already pre-exposed, not radiance";
            report["captures"] = captures; report["active_color_space"] = QualitySettings.activeColorSpace.ToString();
            report["lights"] = Resources.FindObjectsOfTypeAll<HDAdditionalLightData>().Where(light => light.enabled && light.gameObject.activeInHierarchy)
                .Select(light => new { light.name, light.intensity, unit = light.lightUnit.ToString(), light_layers = light.lightlayersMask.ToString(),
                    light_culling_mask = light.GetComponent<Light>().cullingMask, light_enabled = light.GetComponent<Light>().enabled,
                    light_rendering_layers = light.GetComponent<Light>().renderingLayerMask, forward = V(light.transform.forward) }).ToArray();
            report["original_card"] = RendererEvidence(card); report["original_box"] = RendererEvidence(box);
            report["rois_frozen_before_colours"] = new { card_pixel = new[] { cardX, cardY }, box_pixel = new[] { boxX, boxY }, size = 5, origin = "Unity bottom-left" };
        }

        internal void Start()
        {
            Validate();
            FrameSettings settings = originalSettings; FrameSettingsOverrideMask mask = originalMask;
            settings.SetEnabled(FrameSettingsField.CustomPass, true); mask.mask[(uint)FrameSettingsField.CustomPass] = true;
            settingsChanged = true;
            cameraData.renderingPathCustomFrameSettings = settings; cameraData.renderingPathCustomFrameSettingsOverrideMask = mask;
            if (!SystemInfo.supportsAsyncGPUReadback || !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.ReadPixels))
                throw new NotSupportedException("Witness raw float render/readback format is unavailable");
            copy = RTHandles.Alloc(width, height, colorFormat: GraphicsFormat.R32G32B32A32_SFloat, name: "CDLC witness owned raw float copy");
            calibration = new Material(originalCard) { hideFlags = HideFlags.DontSave, name = "CDLC private witness calibration" };
            holder = new GameObject("CDLC witness raw input diagnostic") { hideFlags = HideFlags.DontSave };
            holder.SetActive(false);
            var volume = holder.AddComponent<CustomPassVolume>();
            volume.isGlobal = true; volume.targetCamera = camera; volume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
            pass = new CopyPass(this); volume.customPasses.Add(pass); holder.SetActive(true);
            report["copy_target"] = new { id = copy.rt.GetInstanceID(), format = copy.rt.graphicsFormat.ToString(), copy.rt.width, copy.rt.height };
        }

        internal async Task Run(BusRoadGlassCameraFrames frames, string directory, string model)
        {
            Start(); boxChanged = true; box.gameObject.SetActive(true);
            await Capture("original", frames, directory, model);
            card.sharedMaterial = originalBox; box.sharedMaterial = originalCard;
            await Capture("materials-swapped", frames, directory, model);
            card.sharedMaterial = originalCard; box.sharedMaterial = originalBox;
            await Capture("original-restored", frames, directory, model);

            blockChanged = true; card.SetPropertyBlock(null); card.sharedMaterial = calibration;
            float[][] raw = new float[3][];
            float[] values = { 0f, .18f, .36f };
            for (int i = 0; i < values.Length; i++)
            {
                float value = values[i]; calibration.SetColor("_BaseColor", new Color(value, value, value, 1));
                HDMaterial.ValidateMaterial(calibration);
                raw[i] = await Capture("material-" + i, frames, directory, model);
            }
            report["black_subtracted_material_ratio"] = Ratios(raw[0], raw[1], raw[2]);
            calibration.SetColor("_BaseColor", new Color(.18f, .18f, .18f, 1)); HDMaterial.ValidateMaterial(calibration);
            var vector = new MaterialPropertyBlock(); vector.SetVector("_BaseColor", new Vector4(.18f, .18f, .18f, 1));
            card.SetPropertyBlock(vector);
            await Capture("mpb-vector-018", frames, directory, model);
            report["status"] = "COLLECTED_NO_COLOR_SPACE_CONCLUSION";
        }

        async Task<float[]> Capture(string label, BusRoadGlassCameraFrames frames, string directory, string model)
        {
            Validate(); await BusGlassProbe.WaitForRenderedWindow(frames, Validate); Validate();
            if (pending != null) throw new InvalidOperationException("A previous raw witness request is still pending");
            var row = new Dictionary<string, object> { ["state"] = label, ["request_frame"] = Time.frameCount,
                ["card"] = RendererEvidence(card), ["box"] = RendererEvidence(box),
                ["camera"] = new { id = camera.GetInstanceID(), position = V(camera.transform.position), rotation = V(camera.transform.eulerAngles),
                    camera.fieldOfView, camera.aspect, enabled = camera.enabled, target_id = target.GetInstanceID(),
                    target_format = target.graphicsFormat.ToString(), target.sRGB, camera.allowHDR } };
            captures.Add(row);
            var request = new Pending(row); pending = request;
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (!request.Completion.Task.IsCompleted)
            {
                Validate();
                if (Time.realtimeSinceStartupAsDouble >= deadline) throw new TimeoutException("Witness BeforePostProcess float readback did not complete");
                await Task.Delay(15);
            }
            ReadResult result = await request.Completion.Task;
            if (result.Error != null) throw new InvalidOperationException("Witness raw float capture failed", result.Error);
            Validate(); pending = null;
            row["raw_frame"] = request.Frame;
            string stem = model + "-input-" + label;
            string rawPath = Path.Combine(directory, stem + ".rgba-f32le");
            using (var stream = File.Create(rawPath)) using (var writer = new BinaryWriter(stream))
                foreach (float value in result.Pixels) writer.Write(value);
            row["raw_file"] = Path.GetFileName(rawPath); row["raw_sha256"] = BusGlassProbe.PackageHash(rawPath);
            row["raw_format"] = new { width, height, channels = 4, type = "float32 little-endian", origin = "Unity bottom-left" };
            row["card_raw"] = Samples(result.Pixels, width, height, cardX, cardY);
            row["box_raw"] = Samples(result.Pixels, width, height, boxX, boxY);
            string pngPath = Path.Combine(directory, stem + ".png");
            BusGlassProbe.SaveFrame(BusGlassProbe.Read(target), pngPath);
            row["png"] = Path.GetFileName(pngPath); row["png_frame"] = Time.frameCount;
            row["png_scope"] = "Same unchanged private camera/state; normal target may be a later render than raw_frame";
            var exposure = new Dictionary<string, object>(); row["exposure_read"] = exposure;
            var measured = await BusGlassOpticsExposure.Read(camera, Validate, exposure);
            row["gpu_exposure"] = measured.Evidence(); row["status"] = "CAPTURED";
            return (float[])((Dictionary<string, object>)row["card_raw"])["mean_rgba"];
        }

        void Copy(CustomPassContext context)
        {
            Pending request = pending;
            if (stopping || request == null || request.CallbackPending || request.Completion.Task.IsCompleted || !ReferenceEquals(context.hdCamera.camera, camera)) return;
            try
            {
                Validate(); request.Frame = Time.frameCount;
                request.Evidence["copy_frame"] = request.Frame;
                request.Evidence["source_format"] = context.cameraColorBuffer.rt.graphicsFormat.ToString();
                request.Evidence["actual_custom_pass"] = context.hdCamera.frameSettings.IsEnabled(FrameSettingsField.CustomPass);
                HDUtils.BlitCameraTexture(context.cmd, context.cameraColorBuffer, copy);
                request.CallbackPending = true;
                request.Evidence["request_enqueue_confirmed"] = false;
                context.cmd.RequestAsyncReadback(copy.rt, 0, gpu =>
                {
                    var result = new ReadResult();
                    try
                    {
                        if (gpu.hasError) throw new InvalidOperationException("Witness float GPU readback failed");
                        result.Pixels = gpu.GetData<float>().ToArray();
                        if (result.Pixels.Length != width * height * 4) throw new InvalidOperationException("Witness float GPU dimensions changed");
                    }
                    catch (Exception error) { result.Error = error; }
                    finally { request.CallbackPending = false; request.Completion.TrySetResult(result); }
                });
                request.EnqueueConfirmed = true; request.Evidence["request_enqueue_confirmed"] = true;
            }
            catch (Exception error) { request.Completion.TrySetResult(new ReadResult { Error = error }); }
            finally
            {
                try { CoreUtils.SetRenderTarget(context.cmd, context.cameraColorBuffer, context.cameraDepthBuffer, ClearFlag.None); }
                catch (Exception error) { request.Completion.TrySetResult(new ReadResult { Error = error }); }
            }
        }

        void Validate()
        {
            check();
            if (stopping || camera == null || target == null || camera.targetTexture != target || !target.IsCreated() || !camera.enabled ||
                target.width != width || target.height != height || card == null || box == null ||
                !ReferenceEquals(card.GetComponent<MeshFilter>().sharedMesh, cardMesh) || !ReferenceEquals(box.GetComponent<MeshFilter>().sharedMesh, boxMesh) ||
                !card.transform.localToWorldMatrix.Equals(cardMatrix) || !box.transform.localToWorldMatrix.Equals(boxMatrix) ||
                Vector3.Distance(camera.transform.position, position) > .0001f || Quaternion.Angle(camera.transform.rotation, rotation) > .001f ||
                Math.Abs(camera.fieldOfView - fov) > .001f || Math.Abs(camera.aspect - aspect) > .0001f)
                throw new InvalidOperationException("Private witness camera or frozen projection changed during input diagnostics");
        }

        static object RendererEvidence(MeshRenderer renderer)
        {
            var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
            Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            return new { renderer_id = renderer.GetInstanceID(), enabled = renderer.enabled, active = renderer.gameObject.activeInHierarchy,
                layer = renderer.gameObject.layer, rendering_layer_mask = renderer.renderingLayerMask,
                material_id = renderer.sharedMaterial.GetInstanceID(), material = BusVehicleMaterialDiagnostics.ShaderEvidence(renderer.sharedMaterial),
                property_block = new { is_empty = block.isEmpty, has_base_color = block.HasColor("_BaseColor"), has_base_vector = block.HasVector("_BaseColor"),
                    base_color = V((Vector4)block.GetColor("_BaseColor")), base_vector = V(block.GetVector("_BaseColor")) },
                mesh = new { id = mesh.GetInstanceID(), mesh.name, normals = mesh.normals.Select(V).ToArray(), tangents = mesh.tangents.Select(V).ToArray(),
                    world_normals = mesh.normals.Select(normal => V(renderer.transform.localToWorldMatrix.inverse.transpose.MultiplyVector(normal).normalized)).ToArray(),
                    normal_scope = "Actual mesh vertex normals/tangents and inverse-transpose world normals; shader maps can modify them, not a GPU shading-normal buffer readback" } };
        }
        static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        static float[] V(Vector4 value) => new[] { value.x, value.y, value.z, value.w };

        internal static object Samples(float[] pixels, int width, int height, int x, int y)
        {
            if (pixels == null || pixels.Length != width * height * 4 || x < 2 || y < 2 || x >= width - 2 || y >= height - 2)
                throw new InvalidOperationException("Witness raw sample lies outside the frozen image");
            var samples = new List<object>(); var mean = new float[4];
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
            {
                int index = ((y + dy) * width + x + dx) * 4; var rgba = new float[4];
                for (int channel = 0; channel < 4; channel++)
                {
                    float value = pixels[index + channel];
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Witness raw ROI has nonfinite GPU values");
                    rgba[channel] = value; mean[channel] += value / 25;
                }
                samples.Add(new { pixel = new[] { x + dx, y + dy }, rgba });
            }
            return new Dictionary<string, object> { ["samples"] = samples, ["mean_rgba"] = mean };
        }

        internal static object Ratios(float[] black, float[] low, float[] high)
        {
            var channels = new List<object>();
            for (int i = 0; i < 3; i++)
            {
                double denominator = low[i] - black[i], numerator = high[i] - black[i];
                double? ratio = Math.Abs(denominator) > 1e-8 ? numerator / denominator : (double?)null;
                channels.Add(new { channel = i, black = black[i], material_018 = low[i], material_036 = high[i], denominator, numerator, ratio });
            }
            return new { channels, interpretation = "Diagnostic ratio only. Theoretical linear ratio is 2; exact sRGB EOTF for .36/.18 gives about 3.915. Neither is asserted by this report." };
        }

        public void Dispose()
        {
            stopping = true; if (pass != null) pass.enabled = false;
            var errors = new List<Exception>();
            if (settingsChanged)
            {
                bool done = true;
                if (cameraData != null)
                {
                    done &= CabCleanup.Try(() => cameraData.renderingPathCustomFrameSettings = originalSettings, errors);
                    done &= CabCleanup.Try(() => cameraData.renderingPathCustomFrameSettingsOverrideMask = originalMask, errors);
                }
                if (done) settingsChanged = false;
            }
            if (!restored)
            {
                bool done = true;
                if (card != null)
                {
                    done &= CabCleanup.Try(() => card.sharedMaterial = originalCard, errors);
                    if (blockChanged) done &= CabCleanup.Try(() => card.SetPropertyBlock(originalBlock), errors);
                }
                if (box != null)
                {
                    done &= CabCleanup.Try(() => box.sharedMaterial = originalBox, errors);
                    if (boxChanged) done &= CabCleanup.Try(() => box.gameObject.SetActive(boxActive), errors);
                }
                restored = done;
            }
            if (holder != null)
            {
                bool inactive = CabCleanup.Try(() => holder.SetActive(false), errors);
                if (inactive && CabCleanup.Try(() => UnityEngine.Object.Destroy(holder), errors)) holder = null;
            }
            if (pending?.CallbackPending == true) errors.Add(new InvalidOperationException(pending.EnqueueConfirmed
                ? "Witness GPU callback pending; owned float RT is retained until completion"
                : "Witness readback enqueue was not confirmed; a partial GPU request may exist, so the RT is retained until a callback or process restart"));
            else if (copy != null && CabCleanup.Try(() => RTHandles.Release(copy), errors)) copy = null;
            if (restored && calibration != null && CabCleanup.Try(() => UnityEngine.Object.Destroy(calibration), errors)) calibration = null;
            report["cleanup"] = new { restored, pass_disabled = pass == null || !pass.enabled, volume_released = holder == null,
                raw_texture_released = copy == null, material_released = calibration == null, callback_pending = pending?.CallbackPending == true,
                enqueue_confirmed = pending?.EnqueueConfirmed,
                errors = errors.Select(error => error.ToString()).ToArray() };
            if (errors.Count > 0) { CabCleanup.Retain(this); throw new AggregateException("Witness input diagnostic cleanup failed", errors); }
        }
    }
}
#endif
