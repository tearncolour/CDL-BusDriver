using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The pure parts of the material audit (BusMaterialAudit): the game's decal layers as text, the table of the materials of the game
    /// that take or give a decal layer, and the settings of the file that tries a change on Bus Driver's own materials. No game types;
    /// tested offline (MaterialAuditTests).
    /// </summary>
    internal static class MaterialAuditRules
    {
        // Game.Rendering.DecalLayers (a [Flags] enum of the game's, checked against it by the audit when it runs).
        internal static readonly string[] LayerNames = { "Terrain", "Roads", "Buildings", "Vehicles", "Creatures", "Other" };
        internal const int Vehicles = 8;

        /// <summary>The layers of a mask as "Roads|Vehicles"; "none" for 0; the remaining bits as a number.</summary>
        internal static string Layers(int mask)
        {
            if (mask == 0) return "none";
            var text = new StringBuilder();
            int rest = mask;
            for (int i = 0; i < LayerNames.Length; i++)
            {
                int bit = 1 << i;
                if ((mask & bit) == 0) continue;
                if (text.Length > 0) text.Append('|');
                text.Append(LayerNames[i]);
                rest &= ~bit;
            }
            if (rest != 0)
            {
                if (text.Length > 0) text.Append('|');
                text.Append("0x").Append(((uint)rest).ToString("x"));
            }
            return text.ToString();
        }

        /// <summary>A layer mask stored in a float property the way the game stores it (the bits of an int).</summary>
        internal static int MaskOf(float stored) => math.asint(stored);

        internal static float StoredOf(int mask) => math.asfloat(mask);

        /// <summary>One material of the game that has the decal layer property.</summary>
        internal struct Row
        {
            internal string Shader, Name;
            internal int Queue, Mask;
            /// <summary>-1 absent, 0 off, 1 on.</summary>
            internal int DisableDecals;
            /// <summary>NaN when the shader has no _SupportDecals.</summary>
            internal float SupportDecals;
        }

        /// <summary>
        /// The rows grouped by what decides how a decal meets them (shader, queue, layer mask, the keyword, the float): one line each,
        /// the biggest group first, a few names as examples. Sorted, so the same materials give the same lines.
        /// </summary>
        internal static List<string> Census(IList<Row> rows, int maxLines)
        {
            var groups = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            foreach (Row row in rows)
            {
                string key = $"{row.Shader}|{row.Queue}|{row.Mask}|{row.DisableDecals}|{(float.IsNaN(row.SupportDecals) ? "n" : row.SupportDecals.ToString("0.##", CultureInfo.InvariantCulture))}";
                if (!groups.TryGetValue(key, out List<Row> list)) groups.Add(key, list = new List<Row>());
                list.Add(row);
            }

            var ordered = new List<KeyValuePair<string, List<Row>>>(groups);
            ordered.Sort((a, b) =>
            {
                int byCount = b.Value.Count.CompareTo(a.Value.Count);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });

            var lines = new List<string>();
            foreach (KeyValuePair<string, List<Row>> group in ordered)
            {
                if (lines.Count >= maxLines)
                {
                    lines.Add($"... and {ordered.Count - lines.Count} more groups");
                    break;
                }

                Row first = group.Value[0];
                // the first three names in alphabetical order: the game lists its materials in no fixed order, and the table is only written again
                // when it changes
                var distinct = new SortedSet<string>(StringComparer.Ordinal);
                foreach (Row row in group.Value)
                    distinct.Add(row.Name ?? "");
                var names = new List<string>();
                foreach (string name in distinct)
                {
                    if (names.Count == 3) break;
                    names.Add(name);
                }

                string keyword = first.DisableDecals < 0 ? "n/a" : first.DisableDecals == 0 ? "off" : "on";
                string support = float.IsNaN(first.SupportDecals) ? "n/a" : first.SupportDecals.ToString("0.##", CultureInfo.InvariantCulture);
                lines.Add($"{group.Value.Count} x [{first.Shader}] queue {first.Queue}, layer mask {Layers(first.Mask)}{((first.Mask & Vehicles) != 0 ? " (VEHICLES)" : "")}, " +
                          $"_DISABLE_DECALS {keyword}, _SupportDecals {support}; e.g. {string.Join(", ", names)}");
            }

            return lines;
        }

        /// <summary>
        /// What the file `material-experiment.txt` asks to try on Bus Driver's own materials and renderers (not on the glass, not on
        /// the game's): lines "name = value", "#" starts a comment, a value of "keep" or none leaves that setting alone. queue: a number
        /// (2000 geometry, 2225 the decal-receiving opaque queue); decalMask: a number or layer names ("Vehicles|Other", "none");
        /// supportDecals: 0 or 1; disableDecals: on or off (the _DISABLE_DECALS keyword); renderingLayer: a number for the renderers'
        /// rendering layer mask (4294967295 is all bits, 8 the vehicle layer alone). "float _Name = number" sets a float property on every own
        /// material that has it (for example _DetailNormalScale = 0), "keyword _NAME = on|off" a local keyword; both are put back with the rest.
        /// cameraDecals: on or off switches the decal pass of the main camera's frame settings (the whole picture, not only the bus; put back with the rest).
        /// gameShader: on moves the bus's own HDRP/Lit materials that do not glow to the game's shader (GameShaderMove), off puts them back.
        /// </summary>
        internal sealed class Experiment
        {
            internal int? Queue;
            internal int? DecalMask;
            internal float? SupportDecals;
            internal bool? DisableDecals;
            internal uint? RenderingLayer;
            internal bool? CameraDecals;
            internal bool? GameShader;
            internal readonly Dictionary<string, float> Floats = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly Dictionary<string, bool> Keywords = new Dictionary<string, bool>(StringComparer.Ordinal);

            internal bool Any => Queue.HasValue || DecalMask.HasValue || SupportDecals.HasValue || DisableDecals.HasValue || RenderingLayer.HasValue || CameraDecals.HasValue || GameShader.HasValue || Floats.Count != 0 || Keywords.Count != 0;

            internal string Describe()
            {
                if (!Any) return "nothing";
                var text = new List<string>();
                if (Queue.HasValue) text.Add("queue " + Queue.Value);
                if (DecalMask.HasValue) text.Add("layer mask " + Layers(DecalMask.Value));
                if (SupportDecals.HasValue) text.Add("_SupportDecals " + SupportDecals.Value.ToString("0.##", CultureInfo.InvariantCulture));
                if (DisableDecals.HasValue) text.Add("_DISABLE_DECALS " + (DisableDecals.Value ? "on" : "off"));
                if (RenderingLayer.HasValue) text.Add("rendering layer mask 0x" + RenderingLayer.Value.ToString("x"));
                if (CameraDecals.HasValue) text.Add("camera decals " + (CameraDecals.Value ? "on" : "off"));
                if (GameShader.HasValue) text.Add("game shader " + (GameShader.Value ? "on" : "off"));
                foreach (KeyValuePair<string, float> pair in Floats) text.Add(pair.Key + " " + pair.Value.ToString("0.####", CultureInfo.InvariantCulture));
                foreach (KeyValuePair<string, bool> pair in Keywords) text.Add(pair.Key + " " + (pair.Value ? "on" : "off"));
                return string.Join(", ", text);
            }
        }

        internal static Experiment ParseExperiment(string text, List<string> problems)
        {
            var result = new Experiment();
            if (text == null) return result;
            int number = 0;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                number++;
                string line = raw;
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0) continue;
                int equals = line.IndexOf('=');
                string name = (equals < 0 ? line : line.Substring(0, equals)).Trim().ToLowerInvariant();
                string value = equals < 0 ? "" : line.Substring(equals + 1).Trim();
                if (value.Length == 0 || value.Equals("keep", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("float ", StringComparison.Ordinal) || name.StartsWith("keyword ", StringComparison.Ordinal))
                {
                    ParseProperty(line.Substring(0, equals).Trim(), value, number, result, problems);
                    continue;
                }

                bool ok;
                switch (name)
                {
                    case "queue":
                        ok = TryNumber(value, out long queue) && queue >= 0 && queue <= 5000;
                        if (ok) result.Queue = (int)queue;
                        break;
                    case "decalmask":
                        ok = TryLayers(value, out int mask);
                        if (ok) result.DecalMask = mask;
                        break;
                    case "supportdecals":
                        ok = value == "0" || value == "1";
                        if (ok) result.SupportDecals = value == "1" ? 1f : 0f;
                        break;
                    case "disabledecals":
                        ok = value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase);
                        if (ok) result.DisableDecals = value.Equals("on", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "gameshader":
                        ok = value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase);
                        if (ok) result.GameShader = value.Equals("on", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "cameradecals":
                        ok = value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase);
                        if (ok) result.CameraDecals = value.Equals("on", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "renderinglayer":
                        ok = TryNumber(value, out long layer) && layer >= 0 && layer <= uint.MaxValue;
                        if (ok) result.RenderingLayer = (uint)layer;
                        break;
                    default:
                        problems?.Add($"line {number}: unknown setting '{name}'");
                        continue;
                }

                if (!ok) problems?.Add($"line {number}: '{value}' is not a value for {name}");
            }

            return result;
        }

        // "float _Name" or "keyword _NAME" (the word case-insensitive, the property or keyword as written) and its value
        private static void ParseProperty(string left, string value, int line, Experiment result, List<string> problems)
        {
            bool isFloat = left.StartsWith("float ", StringComparison.OrdinalIgnoreCase);
            string property = left.Substring(isFloat ? 6 : 8).Trim();
            if (!ValidName(property, upper: !isFloat))
            {
                problems?.Add($"line {line}: '{property}' is not a {(isFloat ? "property" : "keyword")} name");
                return;
            }

            if (isFloat)
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || float.IsNaN(number) || float.IsInfinity(number))
                    problems?.Add($"line {line}: '{value}' is not a value for {property}");
                else
                    result.Floats[property] = number;
                return;
            }

            bool on = value.Equals("on", StringComparison.OrdinalIgnoreCase);
            if (!on && !value.Equals("off", StringComparison.OrdinalIgnoreCase))
                problems?.Add($"line {line}: '{value}' is not on or off for {property}");
            else
                result.Keywords[property] = on;
        }

        // a shader property is letters, digits and underscores starting with a letter or an underscore; a keyword is the same in capitals
        private static bool ValidName(string name, bool upper)
        {
            if (name.Length < 2 || name.Length > 64) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool letter = upper ? c >= 'A' && c <= 'Z' : c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z';
                if (!(letter || c == '_' || i > 0 && c >= '0' && c <= '9')) return false;
            }

            return true;
        }

        /// <summary>
        /// What a texture read back from the graphics card is against the bytes it was made from (RGBA, row after row from the bottom): "identical", or how many texels differ and
        /// the first one, with both colours and the mean colour of each. The audit logs it for the cab's palette textures, to tell a texture whose memory was overwritten from one
        /// the shader reads wrong.
        /// </summary>
        internal static string CompareTexels(IReadOnlyList<byte> got, IReadOnlyList<byte> expected, int width)
        {
            if (got == null || expected == null) return "no data to compare";
            if (got.Count != expected.Count) return $"the read-back has {got.Count} bytes, the texture was made from {expected.Count}";
            int texels = expected.Count / 4, different = 0, first = -1;
            double[] sumGot = new double[3], sumExpected = new double[3];
            for (int t = 0; t < texels; t++)
            {
                bool same = true;
                for (int c = 0; c < 4; c++)
                    if (got[t * 4 + c] != expected[t * 4 + c]) same = false;
                for (int c = 0; c < 3; c++) { sumGot[c] += got[t * 4 + c]; sumExpected[c] += expected[t * 4 + c]; }
                if (same) continue;
                different++;
                if (first < 0) first = t;
            }

            if (different == 0) return "identical to the bytes it was made from";
            int x = first % Math.Max(1, width), y = first / Math.Max(1, width);
            string Texel(IReadOnlyList<byte> b) => $"{b[first * 4]},{b[first * 4 + 1]},{b[first * 4 + 2]},{b[first * 4 + 3]}";
            string Mean(double[] s) => $"{s[0] / texels:0},{s[1] / texels:0},{s[2] / texels:0}";
            return $"{different} of {texels} texels differ; the first at ({x},{y}) is {Texel(got)} against {Texel(expected)}; mean colour {Mean(sumGot)} against {Mean(sumExpected)}";
        }

        /// <summary>
        /// Whether an opaque material is in the queues the depth prepass takes (HDRP's OpaqueDecal 2225 to OpaqueLast 2500). A material of Bus Driver's below 2225 is left out of it, and the road's
        /// decals (markings, a stop's paving) are then drawn through it: the 12 m's baked relief materials were in 2000 from 4 to 7 November.
        /// </summary>
        internal static bool InDecalQueue(int queue) => queue >= 2225 && queue <= 2500;

        /// <summary>
        /// Whether a material is one Bus Driver draws opaque and that the road's decals must not show through: made under a name that starts "Bus Driver " on HDRP/Lit or on the game's shader. The mirrors
        /// (HDRP/Unlit), the glass and the people are other shaders and are not asked.
        /// </summary>
        internal static bool WantsDecalQueue(string materialName, string shaderName) =>
            materialName != null && materialName.StartsWith("Bus Driver ", StringComparison.Ordinal) && (shaderName == "HDRP/Lit" || shaderName == "BH/SG_DefaultShader");

        /// <summary>
        /// Whether a material is one of Bus Driver's own that moves to the game's shader: made as HDRP/Lit under a name that starts "Bus Driver " and not glowing (the game's graph does not read an
        /// emission in nits as HDRP/Lit does). The mirrors are HDRP/Unlit, the glass and the people are other shaders: none of them is.
        /// </summary>
        internal static bool GameShaderMovable(string materialName, string shaderName, float emission) =>
            shaderName == "HDRP/Lit" && materialName != null && materialName.StartsWith("Bus Driver ", StringComparison.Ordinal) && !(emission > 0f);

        private static bool TryNumber(string text, out long value)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryLayers(string text, out int mask)
        {
            mask = 0;
            if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            if (TryNumber(text, out long number))
            {
                if (number < int.MinValue || number > uint.MaxValue) return false;
                mask = unchecked((int)(uint)number);
                return true;
            }

            foreach (string part in text.Split('|'))
            {
                int index = Array.FindIndex(LayerNames, n => n.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase));
                if (index < 0) return false;
                mask |= 1 << index;
            }

            return true;
        }
    }
}
