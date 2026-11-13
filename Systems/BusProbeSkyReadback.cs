using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

namespace CDLC.BusDriver
{
    /// <summary>Read-only immediate GPU snapshot of the sky actually bound by the probe render.</summary>
    internal static class BusProbeSkyReadback
    {
        internal static object CaptureMipChain(string directory, string group)
        {
            Texture sky = Shader.GetGlobalTexture("_SkyTexture");
            if (sky == null) return new { available = false, reason = "No sky bound" };
            var mips = new List<object>();
            for (int mip = 0; mip < sky.mipmapCount; mip++)
                mips.Add(Capture(directory, group + "-mip" + mip, mip, 4000f));
            return new { available = true, mip_count = sky.mipmapCount, mips };
        }
        internal static object Capture(string directory, string group, int mip = 0, float previewScale = 1f)
        {
            Texture sky = Shader.GetGlobalTexture("_SkyTexture");
            if (sky == null) return new { available = false, reason = "No global _SkyTexture after probe render" };
            var faces = new List<object>();
            var request = AsyncGPUReadback.Request(sky, mip, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            if (request.hasError) return new { available = false, reason = "GPU sky readback rejected by graphics API", sky.name,
                format = sky.graphicsFormat.ToString(), dimension = sky.dimension.ToString() };
            for (int layer = 0; layer < request.layerCount; layer++)
            {
                var pixels = request.GetData<Color>(layer).ToArray();
                if (pixels.Length != request.width * request.height)
                    throw new InvalidOperationException("Unexpected sky readback layer layout");
                byte[] raw = request.GetData<byte>(layer).ToArray();
                string stem = "sky-" + group + "-layer" + layer;
                var exact = new Texture2D(request.width, request.height, TextureFormat.RGBAFloat, false, true);
                var preview = new Texture2D(request.width, request.height, TextureFormat.RGBA32, false, false);
                float low = float.MaxValue, high = float.MinValue;
                double sum = 0, squared = 0, neighborSquared = 0; long neighborCount = 0;
                int nonfinite = 0; var shown = new Color[pixels.Length];
                try
                {
                    exact.SetPixels(pixels); exact.Apply();
                    File.WriteAllBytes(Path.Combine(directory, stem + ".exr"), exact.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Color value = pixels[i]; float y = .2126f * value.r + .7152f * value.g + .0722f * value.b;
                        if (float.IsNaN(y) || float.IsInfinity(y)) { nonfinite++; shown[i] = Color.magenta; continue; }
                        low = Mathf.Min(low, y); high = Mathf.Max(high, y); sum += y; squared += y * y;
                        if (i % request.width != 0) { double d = y - Luma(pixels[i - 1]); neighborSquared += d * d; neighborCount++; }
                        if (i >= request.width) { double d = y - Luma(pixels[i - request.width]); neighborSquared += d * d; neighborCount++; }
                        shown[i] = new Color(Display(value.r / previewScale), Display(value.g / previewScale), Display(value.b / previewScale), 1f);
                    }
                    preview.SetPixels(shown); preview.Apply();
                    File.WriteAllBytes(Path.Combine(directory, stem + ".png"), preview.EncodeToPNG());
                    string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(raw)).Replace("-", "").ToLowerInvariant();
                    double mean = sum / Math.Max(1, pixels.Length - nonfinite);
                    faces.Add(new { layer, pixels = pixels.Length, raw_rgba_float_sha256 = hash, nonfinite,
                        minimum_luminance = low, maximum_luminance = high, mean_luminance = mean,
                        luminance_variance = squared / Math.Max(1, pixels.Length - nonfinite) - mean * mean,
                        adjacent_luminance_rms_difference = Math.Sqrt(neighborSquared / Math.Max(1, neighborCount)),
                        exact_linear_exr = stem + ".exr", preview_png = stem + ".png",
                        preview_mapping = "Per channel nonnegative linear/(scale+linear), then linear-to-sRGB; exposure is not inferred from preview.", preview_scale = previewScale });
                }
                finally { UnityEngine.Object.Destroy(exact); UnityEngine.Object.Destroy(preview); }
            }
            return new { available = true, sky.name, sky.width, sky.height, dimension = sky.dimension.ToString(),
                format = sky.graphicsFormat.ToString(), mip, actual_width = request.width, actual_height = request.height, layer_count = request.layerCount, faces };
        }
        static float Luma(Color c) => .2126f * c.r + .7152f * c.g + .0722f * c.b;
        static float Display(float v) => Mathf.LinearToGammaSpace(Mathf.Max(0, v) / (1f + Mathf.Max(0, v)));
    }
}
