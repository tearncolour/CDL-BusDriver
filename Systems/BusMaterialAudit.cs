using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CDLC.BusDriver.Kit;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// A diagnostic for the textures of the world (paving, ground, buildings) showing on the bus the player drives or rides (the owner,
    /// 2 November 2026: "经过某些建筑物或者铺装地面之后他们的贴图材质覆盖在车上"; the roof and sides of the LCK5120A, the cab of a game bus).
    /// While the player drives or rides, every few seconds it writes into the log what decides how the game's decals and textures meet
    /// the bus, and only what has changed since:
    ///  - each material on the bus's renderers: shader, queue, the decal layer mask, _SupportDecals, the _DISABLE_DECALS and ENABLE_VT
    ///    keywords, the passes that are off, the textures it holds (with the texture's update count);
    ///  - the renderers' layers (rendering layer mask, shadows, probes, a property block's layer mask);
    ///  - every few seconds more, the table of the game's materials that have the decal layer property (how the game's own vehicles and
    ///    its decals are set), and the pipeline's decal settings;
    /// and, when the file ModsData/CDLC.BusDriver/material-experiment.txt exists, it tries what the file says on this bus's own materials
    /// (queue, decal mask, _SupportDecals, the keyword, the rendering layer mask, any float property or local keyword by name; never the glass, never the game's) and puts it all back
    /// when the file is removed or the view ends. Costs a few ms every 5 s; the log is capped.
    /// </summary>
    internal sealed class BusMaterialAudit
    {
        private const double kTickSeconds = 5.0, kCensusSeconds = 20.0, kExperimentSeconds = 0.5, kReadbackSeconds = 15.0;
        private const int kLogCap = 700, kCensusLines = 40, kTextureCap = 8;

        private static readonly int s_MaskId = Shader.PropertyToID("colossal_DecalLayerMask");
        private static readonly int s_SupportId = Shader.PropertyToID("_SupportDecals");
        private const string kDisableDecals = "_DISABLE_DECALS";

        private sealed class Original
        {
            internal Material Material;
            internal int Queue, Mask;
            internal bool HasMask, HasSupport, HasKeyword, Keyword;
            internal float Support;
            // what an experiment's own "float" and "keyword" lines found, taken before it first changed them
            internal readonly Dictionary<string, float> Floats = new Dictionary<string, float>(StringComparer.Ordinal);
            internal readonly Dictionary<string, bool> Keywords = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        private readonly List<Renderer> m_Renderers = new List<Renderer>();
        private readonly Dictionary<int, Material> m_Set = new Dictionary<int, Material>();
        private readonly Dictionary<int, string[]> m_Last = new Dictionary<int, string[]>();
        private readonly Dictionary<int, Original> m_Originals = new Dictionary<int, Original>();
        private readonly Dictionary<int, uint> m_Layers = new Dictionary<int, uint>();
        private readonly MaterialPropertyBlock m_Block = new MaterialPropertyBlock();
        private string m_RendererTable = "", m_Census = "", m_Pipeline = "", m_Subject = "";
        private double m_NextTick, m_NextCensus, m_NextExperiment;
        private DateTime m_ExperimentStamp = DateTime.MinValue;
        private MaterialAuditRules.Experiment m_Experiment = new MaterialAuditRules.Experiment();
        private int m_Logged, m_Seen;
        private bool m_Capped, m_Started;
        private string m_ExperimentReach = "";
        private readonly GameShaderMove m_GameShader = new GameShaderMove();
        // the main camera's frame settings before the experiment's cameraDecals line changed them
        private HDAdditionalCameraData m_CameraData;
        private bool m_CameraSaved, m_CameraWasCustom;
        private bool? m_CameraApplied;
        private FrameSettings m_CameraSettings;
        private FrameSettingsOverrideMask m_CameraMask;
        private double m_NextReadback;
        private readonly Dictionary<int, string> m_ReadbackLast = new Dictionary<int, string>();
        private readonly Dictionary<Fin, byte[]> m_PaletteBytes = new Dictionary<Fin, byte[]>();
        // what the audit itself costs: the five-second pass over the bus and the census of the game's materials (milliseconds, all and the worst)
        private double m_AuditMilliseconds, m_AuditMaxMilliseconds, m_CensusMilliseconds, m_CensusMaxMilliseconds;

        internal static string ExperimentFile => Path.Combine(Application.persistentDataPath, "ModsData", "CDLC.BusDriver", "material-experiment.txt");

        /// <summary>Cheap: whether anything is due (the caller then gathers the bus's roots and calls <see cref="Update"/>).</summary>
        internal bool Due(double now) => !m_Started || now >= m_NextTick || now >= m_NextExperiment;

        /// <param name="subject">What is driven or ridden, for the log.</param>
        /// <param name="roots">The model roots of the bus's sections: every renderer under them is the bus's.</param>
        internal void Update(double now, string subject, IReadOnlyList<Transform> roots)
        {
            if (!m_Started)
            {
                m_Started = true;
                m_Subject = subject;
                m_NextTick = m_NextCensus = now;
                Log($"Material audit of {subject} starts (Bus Driver {Mod.kVersion}). To try a change on this bus's own materials while it runs, write lines such as " +
                    $"'queue = 2000', 'decalMask = Vehicles', 'supportDecals = 0', 'disableDecals = on', 'renderingLayer = 8', 'float _DetailNormalScale = 0', 'keyword _DETAIL_MAP = off', 'cameraDecals = off', 'gameShader = on' into {ExperimentFile}; removing the file puts everything back.");
            }

            bool tick = now >= m_NextTick;
            bool experimentDue = now >= m_NextExperiment;
            bool changed = experimentDue && ReadExperiment(now);
            long started = Stopwatch.GetTimestamp();
            if (tick || changed)
            {
                Gather(roots);
                if (tick)
                {
                    m_NextTick = now + kTickSeconds;
                    AuditMaterials();
                    AuditRenderers();
                    Spent(started, ref m_AuditMilliseconds, ref m_AuditMaxMilliseconds);
                }
            }

            if (now >= m_NextReadback)
            {
                m_NextReadback = now + kReadbackSeconds;
                RequestReadbacks();
            }

            if (now >= m_NextCensus)
            {
                m_NextCensus = now + kCensusSeconds;
                started = Stopwatch.GetTimestamp();
                Census();
                Spent(started, ref m_CensusMilliseconds, ref m_CensusMaxMilliseconds);
            }

            if (experimentDue || changed)
            {
                m_NextExperiment = now + kExperimentSeconds;
                ApplyExperiment();
            }
        }

        /// <summary>The view has ended: whatever the experiment changed is put back, and the next view starts again.</summary>
        internal void Stop()
        {
            if (!m_Started) return;
            Restore("the view ended");
            Log($"ended: {m_Seen} material(s) seen on the bus, {m_Logged} line(s) written; the audit took {m_AuditMilliseconds:0.0} ms in all (worst pass {m_AuditMaxMilliseconds:0.00} ms), " +
                $"the census of the game's materials {m_CensusMilliseconds:0.0} ms (worst {m_CensusMaxMilliseconds:0.00} ms).");
            m_AuditMilliseconds = m_AuditMaxMilliseconds = m_CensusMilliseconds = m_CensusMaxMilliseconds = 0.0;
            m_Started = false;
            m_Renderers.Clear();
            m_Set.Clear();
            m_Last.Clear();
            m_RendererTable = m_Census = m_Pipeline = "";
            m_ExperimentStamp = DateTime.MinValue;
            m_Experiment = new MaterialAuditRules.Experiment();
            m_Seen = 0;
            m_ReadbackLast.Clear();
            m_NextReadback = 0.0;
        }

        private static void Spent(long started, ref double total, ref double max)
        {
            double milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            total += milliseconds;
            if (milliseconds > max) max = milliseconds;
        }

        private void Log(string line)
        {
            if (m_Capped) return;
            if (++m_Logged >= kLogCap)
            {
                m_Capped = true;
                Mod.log.Info("Material audit: " + line + " (the log of this audit stops here for the session)");
                return;
            }

            Mod.log.Info("Material audit: " + line);
        }

        // ---------------------------------------------------------------- what is on the bus

        private void Gather(IReadOnlyList<Transform> roots)
        {
            m_Renderers.Clear();
            m_Set.Clear();
            var found = new List<Renderer>();
            foreach (Transform root in roots)
            {
                if (root == null) continue;
                found.Clear();
                root.GetComponentsInChildren(true, found);
                m_Renderers.AddRange(found);
            }

            var materials = new List<Material>();
            foreach (Renderer renderer in m_Renderers)
            {
                if (renderer == null) continue;
                materials.Clear();
                renderer.GetSharedMaterials(materials);
                foreach (Material material in materials)
                    if (material != null && !m_Set.ContainsKey(material.GetInstanceID())) m_Set.Add(material.GetInstanceID(), material);
            }
        }

        private static bool IsGlass(Material material)
        {
            string shader = material.shader != null ? material.shader.name : "";
            return shader.IndexOf("Gls", StringComparison.Ordinal) >= 0 || shader.IndexOf("Glf", StringComparison.Ordinal) >= 0 ||
                   shader.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The fields of a material that decide how a decal or a stream of textures meets it, in a fixed order (the log names what changed).
        private static readonly string[] s_Fields = { "name", "shader", "queue", "layer mask", "_SupportDecals", "_DISABLE_DECALS", "ENABLE_VT", "keywords", "passes off", "textures" };

        private static string[] Describe(Material material)
        {
            string shaderName = material.shader != null ? material.shader.name : "?";
            string mask = material.HasProperty(s_MaskId)
                ? MaterialAuditRules.Layers(MaterialAuditRules.MaskOf(material.GetFloat(s_MaskId))) + " (0x" + ((uint)MaterialAuditRules.MaskOf(material.GetFloat(s_MaskId))).ToString("x") + ")"
                : "absent";
            string support = material.HasProperty(s_SupportId) ? material.GetFloat(s_SupportId).ToString("0.##") : "absent";
            return new[]
            {
                material.name,
                shaderName,
                material.renderQueue.ToString(),
                mask,
                support,
                KeywordState(material, kDisableDecals),
                KeywordState(material, "ENABLE_VT"),
                Keywords(material),
                PassesOff(material),
                Textures(material),
            };
        }

        private static string KeywordState(Material material, string name)
        {
            if (material.shader == null) return "n/a";
            LocalKeyword keyword = material.shader.keywordSpace.FindKeyword(name);
            return keyword.isValid ? (material.IsKeywordEnabled(keyword) ? "on" : "off") : "n/a";
        }

        private static string Keywords(Material material)
        {
            var text = new StringBuilder();
            foreach (LocalKeyword keyword in material.enabledKeywords)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(keyword.name);
            }

            return text.Length == 0 ? "none" : text.ToString();
        }

        private static string PassesOff(Material material)
        {
            var text = new StringBuilder();
            for (int i = 0; i < material.passCount; i++)
            {
                string pass = material.GetPassName(i);
                if (material.GetShaderPassEnabled(pass)) continue;
                if (text.Length > 0) text.Append(' ');
                text.Append(string.IsNullOrEmpty(pass) ? "#" + i : pass);
            }

            return text.Length == 0 ? "none" : text.ToString();
        }

        private static string Textures(Material material)
        {
            var text = new StringBuilder();
            int shown = 0;
            foreach (string property in material.GetTexturePropertyNames())
            {
                Texture texture = material.GetTexture(property);
                if (texture == null) continue;
                if (shown++ >= kTextureCap) { text.Append(" ..."); break; }
                if (text.Length > 0) text.Append("; ");
                // a render texture (a mirror) is written every frame: its update count would only be noise
                string updates = texture is RenderTexture ? "render texture" : "updates " + texture.updateCount;
                text.Append($"{property}={texture.name} {texture.width}x{texture.height} {texture.graphicsFormat} mips {texture.mipmapCount} {updates}");
            }

            return text.Length == 0 ? "none" : text.ToString();
        }

        private void AuditMaterials()
        {
            foreach (KeyValuePair<int, Material> pair in m_Set)
            {
                Material material = pair.Value;
                if (material == null) continue;
                string[] now = Describe(material);
                if (!m_Last.TryGetValue(pair.Key, out string[] before))
                {
                    m_Last[pair.Key] = now;
                    m_Seen++;
                    var line = new StringBuilder("material");
                    for (int i = 0; i < s_Fields.Length; i++)
                        line.Append(i == 0 ? " " : ", ").Append(s_Fields[i]).Append(' ').Append(i == 0 ? "'" + now[i] + "'" : now[i]);
                    Log(line.ToString());
                    WarnQueue(now);
                    continue;
                }

                var changes = new StringBuilder();
                for (int i = 0; i < s_Fields.Length; i++)
                {
                    if (now[i] == before[i]) continue;
                    if (changes.Length > 0) changes.Append("; ");
                    changes.Append(s_Fields[i]).Append(": ").Append(before[i]).Append(" -> ").Append(now[i]);
                }

                if (changes.Length == 0) continue;
                m_Last[pair.Key] = now;
                Log($"material '{now[0]}' CHANGED: {changes}");
                WarnQueue(now);
            }
        }

        // A material of ours the road's decals would be drawn through: its name, shader and queue are in the log with the reason, whatever else the audit is for.
        private void WarnQueue(string[] fields)
        {
            if (!int.TryParse(fields[2], out int queue) || MaterialAuditRules.InDecalQueue(queue) || !MaterialAuditRules.WantsDecalQueue(fields[0], fields[1])) return;
            Log($"WARNING: '{fields[0]}' ({fields[1]}) is in render queue {queue}, outside the depth prepass the road's decals are cut against (2225 to 2500): the road's markings and paving show through it");
        }

        // The cab's palette textures are made by Bus Driver from known bytes (KitPalette.PaletteBytes) and never change. Every few seconds each one in use is read back from
        // the graphics card and compared with those bytes; a line is logged when the answer changes. Identical = the memory is as made and a wrong picture comes from how the
        // shader reads it; different = something overwrote the texture (the world's paving and walls on the bus, the owner's reports of 2 and 5 November).
        private void RequestReadbacks()
        {
            if (!SystemInfo.supportsAsyncGPUReadback) return;
            var asked = new HashSet<int>();
            foreach (Material material in m_Set.Values)
            {
                if (material == null || !material.HasProperty("_BaseColorMap")) continue;
                var texture = material.GetTexture("_BaseColorMap") as Texture2D;
                const string prefix = "Bus Driver cab palette ";
                if (texture == null || !texture.name.StartsWith(prefix, StringComparison.Ordinal) || !asked.Add(texture.GetInstanceID())) continue;
                if (!Enum.TryParse(texture.name.Substring(prefix.Length), out Fin fin)) continue;
                if (!m_PaletteBytes.TryGetValue(fin, out byte[] expected)) m_PaletteBytes[fin] = expected = KitPalette.PaletteBytes(fin);
                int id = texture.GetInstanceID(), width = texture.width;
                string name = texture.name;
                AsyncGPUReadback.Request(texture, 0, texture.graphicsFormat, request =>
                {
                    string answer;
                    if (request.hasError) answer = "the read-back failed";
                    else answer = MaterialAuditRules.CompareTexels(request.GetData<byte>().ToArray(), expected, width);
                    if (m_ReadbackLast.TryGetValue(id, out string before) && before == answer) return;
                    m_ReadbackLast[id] = answer;
                    Log($"texture read-back '{name}' {width}x{request.height}: {answer}");
                });
            }
        }

        private void AuditRenderers()
        {
            var groups = new SortedDictionary<string, KeyValuePair<int, string>>(StringComparer.Ordinal);
            foreach (Renderer renderer in m_Renderers)
            {
                if (renderer == null) continue;
                string block = "no block";
                if (renderer.HasPropertyBlock())
                {
                    renderer.GetPropertyBlock(m_Block);
                    block = m_Block.HasFloat(s_MaskId) ? "block layer mask 0x" + ((uint)MaterialAuditRules.MaskOf(m_Block.GetFloat(s_MaskId))).ToString("x") : "block without a layer mask";
                }

                string key = $"{renderer.GetType().Name}, unity layer {renderer.gameObject.layer}, rendering layer mask 0x{renderer.renderingLayerMask:x}, shadows {renderer.shadowCastingMode}/" +
                             $"{(renderer.receiveShadows ? "receive" : "no receive")}, motion {renderer.motionVectorGenerationMode}, probes {renderer.lightProbeUsage}/{renderer.reflectionProbeUsage}, {block}";
                groups.TryGetValue(key, out KeyValuePair<int, string> group);
                groups[key] = new KeyValuePair<int, string>(group.Key + 1, group.Value ?? renderer.name);
            }

            var table = new StringBuilder();
            foreach (KeyValuePair<string, KeyValuePair<int, string>> group in groups)
                table.Append(group.Value.Key).Append(" x ").Append(group.Key).Append("; e.g. ").Append(group.Value.Value).Append('\n');
            string text = table.ToString();
            if (text == m_RendererTable) return;
            m_RendererTable = text;
            Log($"renderers ({m_Renderers.Count}):\n" + text.TrimEnd('\n').Replace("\n", "\n    "));
        }

        // ---------------------------------------------------------------- what the game has

        private void Census()
        {
            var rows = new List<MaterialAuditRules.Row>();
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || !material.HasProperty(s_MaskId)) continue;
                string disable = KeywordState(material, kDisableDecals);
                rows.Add(new MaterialAuditRules.Row
                {
                    Shader = material.shader != null ? material.shader.name : "?",
                    Name = material.name,
                    Queue = material.renderQueue,
                    Mask = MaterialAuditRules.MaskOf(material.GetFloat(s_MaskId)),
                    DisableDecals = disable == "n/a" ? -1 : disable == "on" ? 1 : 0,
                    SupportDecals = material.HasProperty(s_SupportId) ? material.GetFloat(s_SupportId) : float.NaN,
                });
            }

            List<string> lines = MaterialAuditRules.Census(rows, kCensusLines);
            string text = string.Join("\n", lines);
            if (text != m_Census)
            {
                m_Census = text;
                Log($"the game's materials with a decal layer ({rows.Count}), grouped:\n    " + text.Replace("\n", "\n    "));
            }

            string pipeline = Pipeline();
            if (pipeline != m_Pipeline)
            {
                m_Pipeline = pipeline;
                Log("pipeline: " + pipeline);
            }
        }

        private static string Pipeline()
        {
            var text = new StringBuilder();
            try
            {
                var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
                if (asset == null) text.Append("no HDRP asset");
                else
                {
                    RenderPipelineSettings settings = asset.currentPlatformRenderPipelineSettings;
                    text.Append($"asset '{asset.name}': supportDecals {settings.supportDecals}, supportDecalLayers {settings.supportDecalLayers}, " +
                                $"decal draw distance {settings.decalSettings.drawDistance}, perChannelMask {settings.decalSettings.perChannelMask}");
                }
            }
            catch (Exception failure) { text.Append("asset unreadable: " + failure.GetType().Name + ": " + failure.Message); }

            try
            {
                Camera camera = Camera.main;
                if (camera == null) text.Append("; no main camera");
                else
                {
                    text.Append($"; camera '{camera.name}'");
                    var data = camera.GetComponent<HDAdditionalCameraData>();
                    if (data == null) text.Append(" without HDAdditionalCameraData");
                    else
                    {
                        FrameSettings frame = data.renderingPathCustomFrameSettings;
                        text.Append($", custom frame settings {(data.customRenderingSettings ? "used" : "not used")} (decals {frame.IsEnabled(FrameSettingsField.Decals)}, decal layers {frame.IsEnabled(FrameSettingsField.DecalLayers)})");
                    }

                    HDCamera hd = HDCamera.GetOrCreate(camera);
                    if (hd != null)
                        text.Append($"; resolved for the camera: decals {hd.frameSettings.IsEnabled(FrameSettingsField.Decals)}, decal layers {hd.frameSettings.IsEnabled(FrameSettingsField.DecalLayers)}");
                }
            }
            catch (Exception failure) { text.Append("; camera unreadable: " + failure.GetType().Name + ": " + failure.Message); }

            try
            {
                // the game's own enum against the numbers this audit prints the masks with
                var names = new StringBuilder();
                foreach (Game.Rendering.DecalLayers layer in Enum.GetValues(typeof(Game.Rendering.DecalLayers)))
                    names.Append(layer).Append('=').Append((int)layer).Append(' ');
                text.Append("; the game's DecalLayers: ").Append(names.ToString().TrimEnd());
            }
            catch (Exception failure) { text.Append("; DecalLayers unreadable: " + failure.Message); }

            return text.ToString();
        }

        // ---------------------------------------------------------------- the experiment

        /// <summary>True when the file's content has changed since the last read (or the file has gone).</summary>
        private bool ReadExperiment(double now)
        {
            DateTime stamp;
            try { stamp = File.Exists(ExperimentFile) ? File.GetLastWriteTimeUtc(ExperimentFile) : DateTime.MinValue; }
            catch (Exception) { stamp = DateTime.MinValue; }
            if (stamp == m_ExperimentStamp) return false;
            m_ExperimentStamp = stamp;

            var problems = new List<string>();
            MaterialAuditRules.Experiment experiment = new MaterialAuditRules.Experiment();
            if (stamp != DateTime.MinValue)
            {
                try { experiment = MaterialAuditRules.ParseExperiment(File.ReadAllText(ExperimentFile), problems); }
                catch (Exception failure) { problems.Add("the file cannot be read: " + failure.Message); }
            }

            foreach (string problem in problems) Log("experiment file: " + problem);
            if (!experiment.Any)
            {
                Restore(stamp == DateTime.MinValue ? "the file is gone" : "the file asks for nothing");
                m_Experiment = experiment;
                return true;
            }

            m_Experiment = experiment;
            m_ExperimentReach = "";
            Log("experiment: trying " + experiment.Describe() + " on this bus's own materials and renderers");
            return true;
        }

        private void ApplyExperiment()
        {
            MaterialAuditRules.Experiment e = m_Experiment;
            if (!e.Any) return;
            if (e.CameraDecals.HasValue) ApplyCameraDecals(e.CameraDecals.Value);
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            int touched = 0;
            foreach (Material material in m_Set.Values)
            {
                if (material == null || IsGlass(material)) continue;
                int id = material.GetInstanceID();
                if (!m_Originals.TryGetValue(id, out Original original)) m_Originals.Add(id, original = Remember(material));
                touched++;
                if (e.Queue.HasValue && material.renderQueue != e.Queue.Value) material.renderQueue = e.Queue.Value;
                if (e.DecalMask.HasValue && material.HasProperty(s_MaskId) && MaterialAuditRules.MaskOf(material.GetFloat(s_MaskId)) != e.DecalMask.Value)
                    material.SetFloat(s_MaskId, MaterialAuditRules.StoredOf(e.DecalMask.Value));
                if (e.SupportDecals.HasValue && material.HasProperty(s_SupportId) && material.GetFloat(s_SupportId) != e.SupportDecals.Value)
                    material.SetFloat(s_SupportId, e.SupportDecals.Value);
                if (e.DisableDecals.HasValue && KeywordState(material, kDisableDecals) != "n/a" && material.IsKeywordEnabled(kDisableDecals) != e.DisableDecals.Value)
                {
                    if (e.DisableDecals.Value) material.EnableKeyword(kDisableDecals);
                    else material.DisableKeyword(kDisableDecals);
                }

                foreach (KeyValuePair<string, float> pair in e.Floats)
                {
                    if (!material.HasProperty(pair.Key)) continue;
                    float current = material.GetFloat(pair.Key);
                    if (!original.Floats.ContainsKey(pair.Key)) original.Floats.Add(pair.Key, current);
                    if (current != pair.Value) material.SetFloat(pair.Key, pair.Value);
                    found[pair.Key] = (found.TryGetValue(pair.Key, out int n) ? n : 0) + 1;
                }

                foreach (KeyValuePair<string, bool> pair in e.Keywords)
                {
                    if (material.shader == null || !material.shader.keywordSpace.FindKeyword(pair.Key).isValid) continue;
                    bool current = material.IsKeywordEnabled(pair.Key);
                    if (!original.Keywords.ContainsKey(pair.Key)) original.Keywords.Add(pair.Key, current);
                    if (current != pair.Value)
                    {
                        if (pair.Value) material.EnableKeyword(pair.Key);
                        else material.DisableKeyword(pair.Key);
                    }

                    found[pair.Key] = (found.TryGetValue(pair.Key, out int n) ? n : 0) + 1;
                }
            }

            // said again whenever the reach changes (the bus's materials are not all there at the first tick)
            if (e.Floats.Count != 0 || e.Keywords.Count != 0)
            {
                string reach = $"of {touched} own material(s) the float and keyword lines reached " + (found.Count == 0 ? "none" : string.Join(", ", found.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + " on " + p.Value)));
                if (reach != m_ExperimentReach)
                {
                    m_ExperimentReach = reach;
                    Log("experiment: " + reach);
                }
            }

            if (e.GameShader == true)
            {
                string moved = m_GameShader.Apply(m_Set.Values);
                if (moved != null) Log("experiment: " + moved);
            }
            else if (e.GameShader == false && m_GameShader.Count != 0)
            {
                Log($"experiment: game shader off: {m_GameShader.Restore()} material(s) back on HDRP/Lit");
            }

            if (!e.RenderingLayer.HasValue) return;
            foreach (Renderer renderer in m_Renderers)
            {
                if (renderer == null) continue;
                int id = renderer.GetInstanceID();
                if (!m_Layers.ContainsKey(id)) m_Layers.Add(id, renderer.renderingLayerMask);
                if (renderer.renderingLayerMask != e.RenderingLayer.Value) renderer.renderingLayerMask = e.RenderingLayer.Value;
            }
        }

        // The decal pass of the main camera on or off, to tell the world's decals from anything else on the bus. Diagnosis only: with it off the road markings go too.
        private void ApplyCameraDecals(bool on)
        {
            if (m_CameraApplied == on && m_CameraData != null) return;
            Camera camera = Camera.main;
            HDAdditionalCameraData data = camera != null ? camera.GetComponent<HDAdditionalCameraData>() : null;
            if (data == null) return;
            if (!m_CameraSaved || m_CameraData != data)
            {
                m_CameraData = data;
                m_CameraWasCustom = data.customRenderingSettings;
                m_CameraSettings = data.renderingPathCustomFrameSettings;
                m_CameraMask = data.renderingPathCustomFrameSettingsOverrideMask;
                m_CameraSaved = true;
            }

            FrameSettings settings = m_CameraSettings;
            FrameSettingsOverrideMask mask = m_CameraMask;
            mask.mask[(uint)FrameSettingsField.Decals] = true;
            settings.SetEnabled(FrameSettingsField.Decals, on);
            data.customRenderingSettings = true;
            data.renderingPathCustomFrameSettings = settings;
            data.renderingPathCustomFrameSettingsOverrideMask = mask;
            m_CameraApplied = on;
            Log($"experiment: the decal pass of camera '{camera.name}' is {(on ? "on" : "off")} (its frame settings were {(m_CameraWasCustom ? "custom" : "the pipeline's")})");
        }

        private static Original Remember(Material material)
        {
            return new Original
            {
                Material = material,
                Queue = material.renderQueue,
                HasMask = material.HasProperty(s_MaskId),
                Mask = material.HasProperty(s_MaskId) ? MaterialAuditRules.MaskOf(material.GetFloat(s_MaskId)) : 0,
                HasSupport = material.HasProperty(s_SupportId),
                Support = material.HasProperty(s_SupportId) ? material.GetFloat(s_SupportId) : 0f,
                HasKeyword = KeywordState(material, kDisableDecals) != "n/a",
                Keyword = material.IsKeywordEnabled(kDisableDecals),
            };
        }

        private void Restore(string why)
        {
            if (m_Originals.Count == 0 && m_Layers.Count == 0 && !m_CameraSaved && m_GameShader.Count == 0) return;
            int materials = 0, renderers = 0;
            // first: the game shader's snapshot is the whole material, the lines below only repeat what the decal settings were
            int shaders = m_GameShader.Restore();
            if (m_CameraSaved)
            {
                if (m_CameraData != null)
                {
                    m_CameraData.customRenderingSettings = m_CameraWasCustom;
                    m_CameraData.renderingPathCustomFrameSettings = m_CameraSettings;
                    m_CameraData.renderingPathCustomFrameSettingsOverrideMask = m_CameraMask;
                }

                m_CameraSaved = false;
                m_CameraApplied = null;
                m_CameraData = null;
            }

            foreach (Original original in m_Originals.Values)
            {
                Material material = original.Material;
                if (material == null) continue;
                material.renderQueue = original.Queue;
                if (original.HasMask) material.SetFloat(s_MaskId, MaterialAuditRules.StoredOf(original.Mask));
                if (original.HasSupport) material.SetFloat(s_SupportId, original.Support);
                if (original.HasKeyword)
                {
                    if (original.Keyword) material.EnableKeyword(kDisableDecals);
                    else material.DisableKeyword(kDisableDecals);
                }

                foreach (KeyValuePair<string, float> pair in original.Floats) material.SetFloat(pair.Key, pair.Value);
                foreach (KeyValuePair<string, bool> pair in original.Keywords)
                {
                    if (pair.Value) material.EnableKeyword(pair.Key);
                    else material.DisableKeyword(pair.Key);
                }

                materials++;
            }

            foreach (Renderer renderer in m_Renderers)
            {
                if (renderer != null && m_Layers.TryGetValue(renderer.GetInstanceID(), out uint layer))
                {
                    renderer.renderingLayerMask = layer;
                    renderers++;
                }
            }

            m_Originals.Clear();
            m_Layers.Clear();
            Log($"experiment ended ({why}): {materials} material(s) and {renderers} renderer(s) put back as they were" + (shaders != 0 ? $", {shaders} material(s) back on HDRP/Lit" : ""));
        }
    }
}
