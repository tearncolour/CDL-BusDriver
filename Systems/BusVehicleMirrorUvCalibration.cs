using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Game;
using Game.SceneFlow;
using Newtonsoft.Json;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Optional main-menu diagnostic fixture. The caller keeps the complete bus,
    /// glass and driver eye unchanged, turns every target board OFF, and supplies
    /// its real multi-frame camera-settle callback. No Camera.Render fallback.
    /// Calibrate before the independent target off/on/off cycle, then freeze Map.
    /// This is an optical-coordinate witness, not a production-UV-flip test.
    /// </summary>
    internal static class BusVehicleMirrorUvCalibration
    {
        internal const int MinimumContrast = 35;
        internal const int MinimumValidPixels = 100;

        internal sealed class VisibleComponent
        {
            internal int Id, Pixels, GeometryOverlap, MinX, MinY, MaxX, MaxY;
        }

        internal sealed class ComponentSelection
        {
            internal bool[] Mask;
            internal readonly List<VisibleComponent> Components = new List<VisibleComponent>();
            internal int RawPixels, SelectedPixels, SelectedId, BestOverlap, BestTies;
            internal bool Accepted => BestOverlap > 0 && BestTies == 1;
        }

        // Pure topology selection. Never reads target colours or moves/dilates the
        // geometry seed. The seed only chooses the unique primary optical image.
        internal static ComponentSelection SelectComponent(bool[] response, bool[] geometry, int width, int height)
        {
            int count = checked(width * height);
            if (width <= 0 || height <= 0 || response == null || geometry == null || response.Length != count || geometry.Length != count)
                throw new ArgumentException("Response/geometry dimensions do not agree.");
            var result = new ComponentSelection { Mask = new bool[count] };
            var labels = new int[count]; var queue = new int[count];
            for (int seed = 0; seed < count; seed++)
            {
                if (!response[seed] || labels[seed] != 0) continue;
                var component = new VisibleComponent { Id = result.Components.Count + 1,
                    MinX = width, MinY = height, MaxX = -1, MaxY = -1 };
                int head = 0, tail = 0; queue[tail++] = seed; labels[seed] = component.Id;
                while (head < tail)
                {
                    int pixel = queue[head++], x = pixel % width, y = pixel / width;
                    component.Pixels++; if (geometry[pixel]) component.GeometryOverlap++;
                    component.MinX = Math.Min(component.MinX, x); component.MaxX = Math.Max(component.MaxX, x);
                    component.MinY = Math.Min(component.MinY, y); component.MaxY = Math.Max(component.MaxY, y);
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        int neighbour = ny * width + nx;
                        if (!response[neighbour] || labels[neighbour] != 0) continue;
                        labels[neighbour] = component.Id; queue[tail++] = neighbour;
                    }
                }
                result.RawPixels += component.Pixels; result.Components.Add(component);
                if (component.GeometryOverlap > result.BestOverlap)
                {
                    result.BestOverlap = component.GeometryOverlap; result.BestTies = 1;
                    result.SelectedId = component.Id; result.SelectedPixels = component.Pixels;
                }
                else if (component.GeometryOverlap > 0 && component.GeometryOverlap == result.BestOverlap) result.BestTies++;
            }
            if (result.Accepted) for (int pixel = 0; pixel < count; pixel++) result.Mask[pixel] = labels[pixel] == result.SelectedId;
            return result;
        }

        internal sealed class Map
        {
            // All indices use Unity GetPixels32 bottom-left row order.
            // ValidMask and TexelIndex must not be altered to fit target images.
            internal readonly int EyeWidth, EyeHeight, TextureWidth, TextureHeight;
            internal readonly bool[] ValidMask;
            internal readonly int[] TexelIndex;
            internal readonly Dictionary<string, object> Report;
            internal readonly List<object> EvidenceFiles;
            internal readonly bool Passed;

            internal Map(int eyeWidth, int eyeHeight, int textureWidth, int textureHeight,
                bool[] valid, int[] indices, Dictionary<string, object> report, List<object> files, bool passed)
            {
                EyeWidth = eyeWidth; EyeHeight = eyeHeight;
                TextureWidth = textureWidth; TextureHeight = textureHeight;
                ValidMask = valid; TexelIndex = indices;
                Report = report; EvidenceFiles = files; Passed = passed;
            }
        }

        /// <summary>Evidence is returned separately in Map.Report/files and Map.EvidenceFiles,
        /// below dir/calibration/prefix. No formal probe screenshot list is modified.</summary>
        internal static async Task<Map> Calibrate(Material material, string map, RenderTexture mirrorRt,
            RenderTexture eyeRt, bool[] geometryProjection, Func<Task> settle, string dir, string prefix)
        {
            CheckMainMenu();
            Require(material != null && !string.IsNullOrEmpty(map) && material.HasProperty(map), "Mirror material/map is missing.");
            Require(mirrorRt != null && mirrorRt.IsCreated() && eyeRt != null && eyeRt.IsCreated(), "Created mirror and eye render textures are required.");
            Require(material.GetTexture(map) == mirrorRt, "Calibration requires the original production reflection RT to be bound.");
            Require(mirrorRt != eyeRt, "Mirror and eye render textures must be distinct.");
            Require(settle != null, "A real-frame settle callback is required.");
            Require(!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(prefix) &&
                prefix.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && prefix != "." && prefix != "..", "Invalid calibration evidence directory/prefix.");
            int tw = mirrorRt.width, th = mirrorRt.height, ew = eyeRt.width, eh = eyeRt.height;
            Require(tw > 1 && th > 1 && tw <= 4096 && th <= 4096 && ew > 0 && eh > 0,
                "Unsupported calibration texture dimensions.");
            int eyePixels = checked(ew * eh), texturePixels = checked(tw * th);
            Require(geometryProjection != null && geometryProjection.Length == eyePixels, "Independent geometry projection dimensions mismatch.");
            bool[] geometrySeed = (bool[])geometryProjection.Clone();
            int uBits = Bits(tw), vBits = Bits(th);
            string directory = Path.Combine(dir, "calibration", prefix);
            Directory.CreateDirectory(directory);
            Require(!File.Exists(Path.Combine(directory, "calibration-report.json")), "Refusing to overwrite an existing calibration round.");
            var evidence = new List<object>();
            var bitRows = new List<object>();
            var report = new Dictionary<string, object> {
                ["schema"] = "cdlc-main-menu-mirror-gray-code-uv-v2", ["status"] = "running",
                ["utc"] = DateTime.UtcNow.ToString("o"), ["prefix"] = prefix,
                ["scope"] = "Independent optical coordinate calibration through the complete bus/glass, with all target boards OFF. Temporary texture on the existing production mirror material; original RT restored before independent target evaluation.",
                ["caller_preconditions"] = "Complete BusGlass and opaque assembly retained; fixed production eye, stable head orientation and rendering settings; every target board OFF; settle waits for real consecutive Unity renders.",
                ["not_proved"] = new[] { "correct production UV orientation/flip", "reflection camera direction", "road-scene brightness", "map driving" },
                ["coordinate_order"] = "Unity GetPixels32, bottom-left; texelIndex = v * mirrorWidth + u. Saved PNG encoder handles PNG row order.",
                ["eye_width"] = ew, ["eye_height"] = eh, ["mirror_width"] = tw, ["mirror_height"] = th,
                ["u_bits"] = uBits, ["v_bits"] = vBits,
                ["brightness"] = "Integer Rec.709 8-bit luma: (2126 R + 7152 G + 722 B + 5000) / 10000.",
                ["minimum_absolute_bit_luma_difference"] = MinimumContrast,
                ["minimum_white_minus_black_luma"] = MinimumContrast,
                ["minimum_valid_pixels"] = MinimumValidPixels,
                ["primary_component_policy"] = "8-neighbour white-minus-black response; choose the unique component with the greatest positive overlap with the independent bare mirror geometry mask. No overlap or a tie is rejected. No colour targets, mask movement or dilation are used.",
                ["coverage_policy"] = "Every accepted pixel passes every bit; invalid pixels stay invalid. Coverage ratio is recorded, not used to relax contrast. Fewer than 100 decoded pixels or any confident out-of-range decode rejects the whole map. Independent target evaluation must still satisfy all target/coverage requirements.",
                ["filter_mode"] = mirrorRt.filterMode.ToString(), ["wrap_u"] = mirrorRt.wrapModeU.ToString(),
                ["wrap_v"] = mirrorRt.wrapModeV.ToString(), ["wrap_w"] = mirrorRt.wrapModeW.ToString(),
                ["aniso_level"] = mirrorRt.anisoLevel, ["mipmaps"] = mirrorRt.useMipMap, ["srgb"] = mirrorRt.sRGB,
                ["original_rt_name"] = mirrorRt.name, ["original_rt_instance_id"] = mirrorRt.GetInstanceID(),
                ["bits"] = bitRows, ["files"] = evidence,
                ["original_rt_restored"] = false
            };
            Texture original = material.GetTexture(map);
            Vector2 originalScale = material.GetTextureScale(map), originalOffset = material.GetTextureOffset(map);
            report["production_map_scale"] = new[] { originalScale.x, originalScale.y };
            report["production_map_offset"] = new[] { originalOffset.x, originalOffset.y };
            Texture2D code = null;
            try
            {
                code = new Texture2D(tw, th, TextureFormat.RGBA32, mirrorRt.useMipMap, !mirrorRt.sRGB) {
                    name = "CDLC independent mirror UV calibration " + prefix, hideFlags = HideFlags.DontSave,
                    filterMode = mirrorRt.filterMode, wrapModeU = mirrorRt.wrapModeU,
                    wrapModeV = mirrorRt.wrapModeV, wrapModeW = mirrorRt.wrapModeW, anisoLevel = mirrorRt.anisoLevel
                };
                Require(code.width == tw && code.height == th, "Calibration texture dimensions changed.");
                material.SetTexture(map, code);
                var pixels = new Color32[texturePixels];
                var black = await Capture("black", -1, -1, false);
                var white = await Capture("white", -2, -1, false);
                var rawVisible = new bool[eyePixels];
                var grayU = new int[eyePixels]; var grayV = new int[eyePixels];
                for (int p = 0; p < eyePixels; p++)
                    rawVisible[p] = Luma(white[p]) - Luma(black[p]) >= MinimumContrast;
                ComponentSelection selection = SelectComponent(rawVisible, geometrySeed, ew, eh);
                var componentRows = new List<object>();
                foreach (var component in selection.Components)
                    componentRows.Add(new { id = component.Id, pixels = component.Pixels, geometry_overlap = component.GeometryOverlap,
                        bbox_png_xyxy = new[] { component.MinX, eh - 1 - component.MaxY, component.MaxX, eh - 1 - component.MinY },
                        selected = selection.Accepted && component.Id == selection.SelectedId });
                report["components"] = componentRows;
                report["selected_component"] = selection.Accepted ? selection.SelectedId : 0;
                report["maximum_geometry_overlap"] = selection.BestOverlap;
                report["components_tied_for_maximum_positive_overlap"] = selection.BestTies;
                report["white_black_visible_pixels"] = selection.RawPixels;
                report["primary_white_black_visible_pixels"] = selection.Accepted ? selection.SelectedPixels : 0;
                RecordMask("raw-visible-mask-u8.bin", rawVisible);
                RecordMask("geometry-seed-mask-u8.bin", geometrySeed);
                RecordMask("primary-visible-mask-u8.bin", selection.Mask);
                Require(selection.Accepted, "Primary mirror response has no unique positive geometric overlap.");
                bool[] visible = selection.Mask, valid = (bool[])selection.Mask.Clone();
                for (int axis = 0; axis < 2; axis++)
                    for (int bit = (axis == 0 ? uBits : vBits) - 1; bit >= 0; bit--)
                    {
                        string label = (axis == 0 ? "u" : "v") + "-bit-" + bit.ToString("D2");
                        Color32[] positive = await Capture(label + "-positive", axis, bit, false);
                        Color32[] negative = await Capture(label + "-complement", axis, bit, true);
                        int contrastPass = 0, previouslyValid = 0, stillValid = 0;
                        for (int p = 0; p < eyePixels; p++)
                        {
                            if (!visible[p]) continue;
                            if (valid[p]) previouslyValid++;
                            int delta = Luma(positive[p]) - Luma(negative[p]);
                            bool confident = Math.Abs(delta) >= MinimumContrast;
                            if (confident) contrastPass++;
                            valid[p] &= confident;
                            if (!valid[p]) continue;
                            stillValid++;
                            if (delta > 0) { if (axis == 0) grayU[p] |= 1 << bit; else grayV[p] |= 1 << bit; }
                        }
                        bitRows.Add(new { axis = axis == 0 ? "u" : "v", bit,
                            visible_pixels_passing_this_bit = contrastPass, previously_valid = previouslyValid,
                            valid_after_this_bit = stillValid });
                    }
                var indices = new int[eyePixels]; int validCount = 0, outOfRange = 0;
                for (int p = 0; p < eyePixels; p++)
                {
                    indices[p] = -1;
                    if (!valid[p]) continue;
                    int u = Decode(grayU[p]), v = Decode(grayV[p]);
                    if (u < 0 || u >= tw || v < 0 || v >= th) { valid[p] = false; outOfRange++; continue; }
                    indices[p] = checked(v * tw + u); validCount++;
                }
                bool passed = validCount >= MinimumValidPixels && outOfRange == 0;
                report["valid_pixels"] = validCount;
                report["valid_fraction_of_white_black_visible"] = selection.RawPixels == 0 ? 0d : validCount / (double)selection.RawPixels;
                report["valid_fraction_of_primary_visible"] = validCount / (double)selection.SelectedPixels;
                report["out_of_range_decoded_pixels"] = outOfRange;
                report["passed"] = passed;
                report["status"] = passed ? "CALIBRATION_MAP_READY_NOT_MIRROR_ACCEPTANCE" : "CALIBRATION_REJECTED";
                // Persist the exact frozen mapping for independent replay. Explicit
                // little-endian int32; -1 means invalid, no inferred/fitted texel.
                byte[] mapBytes = new byte[checked(indices.Length * 4)];
                for (int p = 0; p < indices.Length; p++)
                {
                    uint value = unchecked((uint)indices[p]); int b = p * 4;
                    mapBytes[b] = (byte)value; mapBytes[b + 1] = (byte)(value >> 8);
                    mapBytes[b + 2] = (byte)(value >> 16); mapBytes[b + 3] = (byte)(value >> 24);
                }
                Record("eye-to-rt-texel-int32le.bin", mapBytes, "frozen bottom-left texel mapping, invalid=-1");
                return new Map(ew, eh, tw, th, valid, indices, report, evidence, passed);

                async Task<Color32[]> Capture(string label, int axis, int bit, bool complement)
                {
                    CheckMainMenu();
                    Require(mirrorRt.width == tw && mirrorRt.height == th && eyeRt.width == ew && eyeRt.height == eh,
                        "Render texture dimensions changed during calibration.");
                    Require(material.GetTexture(map) == code, "Another owner replaced the calibration texture.");
                    Require(material.GetTextureScale(map) == originalScale && material.GetTextureOffset(map) == originalOffset,
                        "Production mirror texture transform changed during calibration.");
                    for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++)
                    {
                        int coordinate = axis == 0 ? x : y;
                        int gray = coordinate ^ (coordinate >> 1);
                        bool bright = axis == -2 || (axis >= 0 && (((gray >> bit) & 1) != 0) != complement);
                        byte value = bright ? (byte)255 : (byte)0;
                        pixels[y * tw + x] = new Color32(value, value, value, 255);
                    }
                    code.SetPixels32(pixels); code.Apply(mirrorRt.useMipMap, false);
                    Record(label + "-code.png", code.EncodeToPNG(), "known independent Gray-code source texture");
                    await settle();
                    CheckMainMenu();
                    Require(material.GetTexture(map) == code && mirrorRt.width == tw && mirrorRt.height == th &&
                        eyeRt.width == ew && eyeRt.height == eh, "Texture binding/dimensions changed while settling.");
                    Color32[] image = BusGlassProbe.Read(eyeRt);
                    Require(image.Length == eyePixels, "Readback dimensions mismatch.");
                    var saved = new Texture2D(ew, eh, TextureFormat.RGBA32, false);
                    try { saved.SetPixels32(image); saved.Apply(); Record(label + "-eye.png", saved.EncodeToPNG(), "complete-assembly actual eye readback"); }
                    finally { UnityEngine.Object.Destroy(saved); }
                    return image;
                }
            }
            catch (Exception ex)
            {
                report["status"] = "CALIBRATION_ERROR"; report["passed"] = false; report["error"] = ex.ToString();
                throw;
            }
            finally
            {
                if (material != null) material.SetTexture(map, original);
                bool restored = material != null && material.GetTexture(map) == mirrorRt;
                report["original_rt_restored"] = restored;
                if (code != null) UnityEngine.Object.Destroy(code);
                if (!restored) { report["status"] = "CALIBRATION_RESTORE_FAILED"; report["passed"] = false; }
                File.WriteAllText(Path.Combine(directory, "calibration-report.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
                Require(restored, "Original production mirror RT could not be restored.");
            }

            void Record(string name, byte[] bytes, string meaning)
            {
                File.WriteAllBytes(Path.Combine(directory, name), bytes);
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                var entry = new { filename = name, path = Path.Combine("calibration", prefix, name).Replace('\\', '/'),
                    sha256 = hash, bytes = bytes.Length, scope = "independent mirror UV calibration", meaning };
                evidence.Add(entry);
            }
            void RecordMask(string name, bool[] mask)
            {
                var bytes = new byte[mask.Length];
                for (int p = 0; p < bytes.Length; p++) bytes[p] = mask[p] ? (byte)1 : (byte)0;
                Record(name, bytes, "bottom-left uint8 boolean mask; 1=true, 0=false");
            }
        }

        private static int Luma(Color32 p) => (2126 * p.r + 7152 * p.g + 722 * p.b + 5000) / 10000;
        private static int Bits(int dimension) { int n = 0; for (int v = dimension - 1; v > 0; v >>= 1) n++; return n; }
        private static int Decode(int gray) { int binary = 0; for (; gray != 0; gray >>= 1) binary ^= gray; return binary; }
        private static void CheckMainMenu() => Require(GameManager.instance != null && GameManager.instance.gameMode == GameMode.MainMenu && !GameManager.instance.isGameLoading,
            "UV calibration only runs in a settled main menu.");
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
