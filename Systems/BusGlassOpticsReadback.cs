#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    internal static class BusGlassOpticsReadback
    {
        sealed class Result<T>
        {
            internal T Value;
            internal Exception Error;
            internal bool GpuError;
            internal int Thread;
            internal int? Frame;
            internal long Timestamp;
        }

        internal static async Task<T> Read<T>(Texture texture, GraphicsFormat format,
            Func<AsyncGPUReadbackRequest, T> copy, Action check, IDictionary<string, object> evidence)
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            var watch = Stopwatch.StartNew();
            var completion = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool requested = false;
            try
            {
                check();
                if (texture == null) throw new InvalidOperationException("Optics readback texture is missing");
                evidence["texture"] = texture.name;
                evidence["texture_id"] = texture.GetInstanceID();
                evidence["texture_type"] = texture.GetType().Name;
                evidence["width"] = texture.width; evidence["height"] = texture.height;
                evidence["dimension"] = texture.dimension.ToString();
                evidence["graphics_format"] = texture.graphicsFormat.ToString();
                evidence["is_readable"] = texture is Texture2D image ? (bool?)image.isReadable : null;
                evidence["destination_format"] = format.ToString();
                evidence["request_frame"] = Time.frameCount;
                evidence["request_thread"] = thread;
                bool sourceSupported = SystemInfo.IsFormatSupported(texture.graphicsFormat, FormatUsage.ReadPixels);
                bool destinationSupported = SystemInfo.IsFormatSupported(format, FormatUsage.ReadPixels);
                evidence["source_readback_supported"] = sourceSupported;
                evidence["destination_readback_supported"] = destinationSupported;
                if (!SystemInfo.supportsAsyncGPUReadback || !sourceSupported || !destinationSupported)
                    throw new NotSupportedException("Optics GPU readback format is unsupported");
                if (texture.width != 1 || texture.height != 1 || texture.dimension != TextureDimension.Tex2D)
                    throw new InvalidOperationException("Optics readback requires one 2D texel");

                AsyncGPUReadback.Request(texture, 0, format, request =>
                {
                    var result = new Result<T> { Thread = Thread.CurrentThread.ManagedThreadId,
                        Timestamp = Stopwatch.GetTimestamp() };
                    if (result.Thread == thread) result.Frame = Time.frameCount;
                    try
                    {
                        result.GpuError = request.hasError;
                        if (result.GpuError) throw new InvalidOperationException("Optics GPU readback failed in its completion callback");
                        // Unity releases this data on the next frame; only the copied value crosses an await.
                        result.Value = copy(request);
                    }
                    catch (Exception error) { result.Error = error; }
                    completion.TrySetResult(result);
                });
                requested = true;
                while (!completion.Task.IsCompleted)
                {
                    check();
                    if (watch.Elapsed.TotalSeconds > 5) throw new TimeoutException("Optics GPU readback did not complete within five seconds");
                    await Task.Delay(20);
                }
                Result<T> finished = await completion.Task;
                evidence["callback_frame"] = finished.Frame;
                evidence["callback_thread"] = finished.Thread;
                evidence["callback_timestamp"] = finished.Timestamp;
                evidence["gpu_has_error"] = finished.GpuError;
                evidence["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
                if (finished.Error != null) ExceptionDispatchInfo.Capture(finished.Error).Throw();
                check();
                evidence["status"] = "COPIED_IN_CALLBACK";
                return finished.Value;
            }
            catch (Exception error)
            {
                evidence["status"] = "FAIL";
                evidence["error"] = error.ToString();
                evidence["request_pending"] = requested && !completion.Task.IsCompleted;
                evidence["elapsed_seconds"] = watch.Elapsed.TotalSeconds;
                throw;
            }
        }
    }
}
#endif
