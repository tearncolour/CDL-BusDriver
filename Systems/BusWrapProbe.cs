using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using FrameSettings = UnityEngine.Rendering.HighDefinition.FrameSettings;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Developer probe (a "probe=wrap" request, cs2_research/bus_asset/probe_citizen.sh wrap): the body wrap on the mod's 12 m
    /// bus. On the main menu it writes the template wrap, loads it (or the wrap named in the request's "wrap" line) and renders the
    /// bus from the left, the right and the front quarter to citizen-probe.png in the mod's folder.
    /// </summary>
    internal static class BusWrapProbe
    {
        private const int kW = 900, kH = 340;

        /// <param name="wrap">The wrap's name ("probe-test": a test wrap is written; empty: the same).</param>
        /// <param name="vehicleName">An authored vehicle's model name ("SWB6710EV01G": its bus is drawn with its own wraps folder, more views); null: the generic 12 m bus.</param>
        /// <param name="variants">The material experiment (request line variants=1): which settings of the game's HDRP/Lit really cut a picture out and survive
        /// behind a transparent pane, measured from the pixels (see <see cref="Variants"/>).</param>
        internal static async Task<string> Run(string wrap, string vehicleName = null, bool variants = false)
        {
            if (variants)
                return await Variants();
            var log = new StringBuilder();
            var cleanup = new List<Action>();
            var skins = new BusSkins();
            cleanup.Add(skins.Destroy);
            try
            {
                BusInteriorSpec spec = BusInteriorSpec.Default;
                if (!string.IsNullOrEmpty(vehicleName))
                {
                    // a pack is registered when a bus of it is first asked about: here no bus is, so the probe reads the pack itself
                    VehicleProfile profile = VehicleModels.Snapshot().Find(p => p.Name == vehicleName);
                    if (profile == null)
                    {
                        string mods = System.IO.Path.GetDirectoryName(Mod.ModDirectory.TrimEnd('/', '\\'));
                        string file = null;
                        foreach (string folder in System.IO.Directory.GetDirectories(mods, "CDLC.Bus.*"))
                        {
                            string candidate = System.IO.Path.Combine(folder, vehicleName + ".cdlb.json");
                            if (System.IO.File.Exists(candidate)) { file = candidate; break; }
                        }
                        if (file == null) return "failed: no pack folder with " + vehicleName + ".cdlb.json under " + mods;
                        profile = VehicleModels.Register(VehicleProfileJson.LoadForPrefab(file, vehicleName));
                        log.Append("pack ").Append(file).Append("; ");
                    }
                    spec = profile.Spec;
                }
                BusSkins.SetVehicle(new[] { spec });
                BusSkins.WriteTemplateNow();
                string name = string.IsNullOrEmpty(wrap) ? "probe-test" : wrap;
                if (name == "probe-template")
                    name = BusSkins.kTemplate;         // the template's guide pictures as a wrap
                else if (name == "probe-test")
                    TestWrap(WrapPackage.ZipPath(BusSkins.Root, BusSkins.Model, name));
                skins.Update(name);
                log.Append($"wrap {name}: faces {skins.Faces}; folder {BusSkins.Root}; ");
                if (skins.Faces == BusInterior.WrapFaces.None)
                    return "failed: " + log;

                KitModelData data = KitBuild.BuildAll(new BusInterior(spec), new MeshKit());
                if (data.Error != null)
                    return "failed: model: " + data.Error;
                var model = new CabModel(data);
                cleanup.Add(model.Destroy);
                var bus = new CabInstance(model, "CDLC wrap probe");
                cleanup.Add(bus.Destroy);
                float length = new BusInterior(spec).WrapLength;
                // the wrap's material, as BusCabSystem gives it
                for (int f = 0; f < BusInterior.WrapFaceCount; f++)
                    bus.OverrideMaterial(BusInterior.WrapLeft + f, skins.Material(0, (BusInterior.WrapFace)f, 0f, length, length));
                // the door leaves' pieces wear their side's picture
                foreach ((int part, BusInterior.WrapFace face) in BusInterior.WrapDoorPieces(spec))
                    bus.OverrideMaterial(part, skins.Material(0, face, 0f, length, length));
                bus.OverrideMaterial(BusInterior.Livery, CabMaterials.Livery(new Color32(0, 110, 200, 255)));
                Vector3 origin = new Vector3(0f, 3000f, 0f);
                var pose = new BusInterior.Pose { Position = origin, Rotation = quaternion.identity, Outside = true, Wrap = skins.Faces, Kneel = quaternion.identity };
                BusInterior.Apply(bus, pose, new BusInterior(spec).Spec);
                bus.Visible = true;

                var sunObject = new GameObject("CDLC probe sun") { hideFlags = HideFlags.DontSave };
                Light sun = sunObject.AddComponent<Light>();
                sun.type = UnityEngine.LightType.Directional;
                sun.shadows = LightShadows.None;
                sunObject.AddComponent<HDAdditionalLightData>().intensity = 30000f;
                var camObject = new GameObject("CDLC probe camera") { hideFlags = HideFlags.DontSave };
                Camera cam = camObject.AddComponent<Camera>();
                cam.enabled = false;
                var hd = camObject.AddComponent<HDAdditionalCameraData>();
                hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                hd.backgroundColorHDR = new Color(0.3f, 0.42f, 0.58f, 1f);
                hd.customRenderingSettings = true;
                FrameSettings fs = hd.renderingPathCustomFrameSettings;
                FrameSettingsOverrideMask mask = hd.renderingPathCustomFrameSettingsOverrideMask;
                foreach (FrameSettingsField field in new[] { FrameSettingsField.DepthOfField, FrameSettingsField.MotionBlur, FrameSettingsField.Volumetrics })
                {
                    mask.mask[(uint)field] = true;
                    fs.SetEnabled(field, false);
                }
                hd.renderingPathCustomFrameSettings = fs;
                hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                cam.fieldOfView = 30f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 300f;
                var rt = new RenderTexture(kW, kH, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                cam.targetTexture = rt;
                cleanup.Add(() => { UnityEngine.Object.Destroy(camObject); UnityEngine.Object.Destroy(sunObject); rt.Release(); UnityEngine.Object.Destroy(rt); });

                float mid = 0.5f * (spec.FrontZ + spec.RearZ);
                var views = new (Vector3 eye, Vector3 at)[]
                {
                    (new Vector3(-24f, 1.6f, mid), new Vector3(0f, 1.6f, mid)),
                    (new Vector3(24f, 1.6f, mid), new Vector3(0f, 1.6f, mid)),
                    (new Vector3(-9f, 2.2f, spec.FrontZ + 9f), new Vector3(0f, 1.4f, mid + 2f)),
                    (new Vector3(0.01f, 1.2f, spec.FrontZ + 7f), new Vector3(0f, 0.9f, spec.FrontZ)),
                    (new Vector3(0.01f, 1.6f, spec.RearZ - 7f), new Vector3(0f, 1.2f, spec.RearZ)),
                    (new Vector3(-6f, 12f, mid - 0.01f), new Vector3(0f, spec.RoofY, mid)),
                    // close views of the door side (the kerb side of a left-hand-drive bus is -x; the doors are on the +x side in Unity): the front
                    // wing and the window row, then the doors and the sill, from standing height, to look at edges and fringes
                    (new Vector3(5.0f, 1.5f, spec.FrontAxleZ + 1.2f), new Vector3(0f, 1.2f, spec.FrontAxleZ)),
                    (new Vector3(5.5f, 1.5f, mid), new Vector3(0f, 1.3f, mid)),
                };
                var shots = new Color32[views.Length][];
                for (int frame = 0; frame < 30 + views.Length; frame++)
                {
                    int v = frame % views.Length;
                    cam.transform.position = origin + views[v].eye;
                    cam.transform.LookAt(origin + views[v].at);
                    sunObject.transform.rotation = Quaternion.LookRotation((views[v].at - views[v].eye).normalized + Vector3.down * 0.7f);
                    cam.Render();
                    if (frame >= 30)
                        shots[v] = Read(rt);
                    await Task.Yield();
                }
                var tex = new Texture2D(kW, kH * views.Length, TextureFormat.RGBA32, false);
                for (int i = 0; i < views.Length; i++)
                    tex.SetPixels32(0, (views.Length - 1 - i) * kH, kW, kH, shots[i]);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(Mod.ModDirectory, BusCitizenProbe.kImage), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                return "ok; " + log;
            }
            catch (Exception ex)
            {
                return $"failed: {ex.GetType().Name}: {ex.Message} {ex.StackTrace?.Split('\n')[0]} after {log}";
            }
            finally
            {
                foreach (Action a in cleanup)
                {
                    try { a(); } catch (Exception) { }
                }
            }
        }

        // The material experiment. A flat quad with a picture whose left half is opaque red and whose right half is see-through (alpha 0, colour
        // magenta) stands in front of a grey wall, once for each material setting; the pixels left and right of the picture's middle say whether the
        // setting cuts the picture out (the right half shows the wall, not magenta), and a second render with a transparent pane (HDRP transparent with
        // its depth prepass, like a bus stop's glass) in front says whether the red half survives it. The answers are in the result text.
        private static async Task<string> Variants()
        {
            var report = new StringBuilder();
            var cleanup = new List<Action>();
            try
            {
                var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                        tex.SetPixel(x, y, x < 32 ? new Color32(255, 0, 0, 255) : new Color32(255, 0, 255, 0));
                tex.Apply(false, false);
                cleanup.Add(() => UnityEngine.Object.Destroy(tex));

                Vector3 origin = new Vector3(0f, 3200f, 0f);
                GameObject Quad(string name, Vector3 at, Vector3 scale, Material material)
                {
                    var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    q.name = name;
                    q.hideFlags = HideFlags.DontSave;
                    UnityEngine.Object.Destroy(q.GetComponent<Collider>());
                    q.transform.position = origin + at;
                    q.transform.localScale = scale;
                    q.GetComponent<Renderer>().sharedMaterial = material;
                    cleanup.Add(() => UnityEngine.Object.Destroy(q));
                    return q;
                }

                var wallMaterial = new Material(Shader.Find("HDRP/Lit")) { hideFlags = HideFlags.DontSave };
                wallMaterial.SetColor("_BaseColor", new Color(0.55f, 0.6f, 0.55f));
                wallMaterial.SetFloat("_Smoothness", 0f);
                wallMaterial.SetFloat("_DoubleSidedEnable", 1f);
                wallMaterial.SetFloat("_CullMode", 0f);
                HDMaterial.ValidateMaterial(wallMaterial);
                cleanup.Add(() => UnityEngine.Object.Destroy(wallMaterial));
                Quad("wall", new Vector3(0f, 0f, 9f), new Vector3(60f, 30f, 1f), wallMaterial);

                var paneMaterial = new Material(Shader.Find("HDRP/Lit")) { hideFlags = HideFlags.DontSave };
                paneMaterial.SetFloat("_SurfaceType", 1f);
                paneMaterial.SetFloat("_BlendMode", 0f);
                paneMaterial.SetColor("_BaseColor", new Color(1f, 1f, 0.2f, 0.25f));
                paneMaterial.SetFloat("_Smoothness", 0f);
                paneMaterial.SetFloat("_EnableBlendModePreserveSpecularLighting", 0f);
                paneMaterial.SetFloat("_TransparentDepthPrepassEnable", 1f);
                paneMaterial.SetFloat("_DoubleSidedEnable", 1f);
                paneMaterial.SetFloat("_CullMode", 0f);
                HDMaterial.ValidateMaterial(paneMaterial);
                report.Append($"pane: queue {paneMaterial.renderQueue}, transparent {(paneMaterial.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ? "yes" : "NO")}, prepass pass {paneMaterial.GetShaderPassEnabled("TransparentDepthPrepass")}; ");
                cleanup.Add(() => UnityEngine.Object.Destroy(paneMaterial));
                GameObject pane = Quad("pane", new Vector3(0f, 0f, 3f), new Vector3(30f, 15f, 1f), paneMaterial);
                pane.SetActive(false);

                var sunObject = new GameObject("CDLC probe sun") { hideFlags = HideFlags.DontSave };
                Light sun = sunObject.AddComponent<Light>();
                sun.type = UnityEngine.LightType.Directional;
                sun.shadows = LightShadows.None;
                sunObject.AddComponent<HDAdditionalLightData>().intensity = 30000f;
                sunObject.transform.rotation = Quaternion.Euler(20f, 0f, 0f);
                var camObject = new GameObject("CDLC probe camera") { hideFlags = HideFlags.DontSave };
                Camera cam = camObject.AddComponent<Camera>();
                cam.enabled = false;
                var hd = camObject.AddComponent<HDAdditionalCameraData>();
                hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                hd.backgroundColorHDR = new Color(0.3f, 0.42f, 0.58f, 1f);
                cam.fieldOfView = 30f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;
                cam.transform.position = origin;
                cam.transform.rotation = Quaternion.identity;
                var rt = new RenderTexture(kW, kH, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                cam.targetTexture = rt;
                cleanup.Add(() => { UnityEngine.Object.Destroy(camObject); UnityEngine.Object.Destroy(sunObject); rt.Release(); UnityEngine.Object.Destroy(rt); });

                Color32 At(Color32[] px, float fx, float fy) => px[(int)(fy * kH) * kW + (int)(fx * kW)];
                string Rgb(Color32 c) => $"({c.r},{c.g},{c.b})";
                bool Magenta(Color32 c) => c.r > 60 && c.b > 60 && c.g < 0.55f * (c.r + c.b) * 0.5f;
                bool Red(Color32 c) => c.r > 2 * c.g + 20 && c.r > 2 * c.b + 20;

                // the setting list: name, how to make the material
                var variants = new List<(string name, Func<Material> make)>
                {
                    ("the wrap's material (CabMaterials.Wrap)", () => CabMaterials.Wrap(tex)),
                    // one setting at a time against the library's own decal settings (queue 2475, _SupportDecals 1, layer mask 0): the red half's
                    // brightness differed from the library's cut-out in the first 0.11.2 probe run, and _SupportDecals set by SetFloat alone
                    // may not have moved the shader's keyword
                    ("wrap as 0.11.1 had it: queue 2475, _SupportDecals 1, layer mask 0", () => { Material m = CabMaterials.Wrap(tex); m.SetFloat("_SupportDecals", 1f); m.SetFloat("colossal_DecalLayerMask", 0f); m.renderQueue = 2475; return m; }),
                    ("wrap, queue 2475 only (the other decal settings of 0.11.2)", () => { Material m = CabMaterials.Wrap(tex); m.renderQueue = 2475; return m; }),
                    ("wrap, queue 2225 and layer mask 8, _SupportDecals 1", () => { Material m = CabMaterials.Wrap(tex); m.SetFloat("_SupportDecals", 1f); return m; }),
                    ("wrap, queue 2225, _SupportDecals 0, layer mask 0", () => { Material m = CabMaterials.Wrap(tex); m.SetFloat("colossal_DecalLayerMask", 0f); return m; }),
                    ("wrap with HDMaterial.ValidateMaterial after the settings, queue back to 2225", () => { Material m = CabMaterials.Wrap(tex); HDMaterial.ValidateMaterial(m); m.renderQueue = 2225; return m; }),
                    ("unlit cut-out, queue 2450", () =>
                    {
                        var m = new Material(Shader.Find("HDRP/Unlit")) { hideFlags = HideFlags.DontSave };
                        m.SetTexture("_UnlitColorMap", tex);
                        m.SetFloat("_AlphaCutoffEnable", 1f);
                        m.SetFloat("_AlphaCutoff", 0.9f);
                        HDMaterial.ValidateMaterial(m);
                        m.renderQueue = 2450;
                        return m;
                    }),
                };
                // the game's own materials (the library the importer takes its templates from) and shaders that might honour alpha
                try
                {
                    var census = new StringBuilder();
                    foreach (Shader shader in Resources.FindObjectsOfTypeAll<Shader>())
                    {
                        string n = shader != null ? shader.name : "";
                        if (n.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Foliage", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Transp", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("BH/", StringComparison.Ordinal) || n.StartsWith("HDRP/", StringComparison.Ordinal))
                            census.Append(n).Append(" | ");
                    }
                    report.Append("shaders: ").Append(census).Append("; ");
                    for (int index = 0; index < 64; index++)
                    {
                        Material library;
                        try { library = Colossal.IO.AssetDatabase.AssetDatabase.global.resources.materialLibrary.GetMaterial(index); }
                        catch (Exception) { break; }
                        if (library == null) break;
                        int captured = index;
                        string label = $"library {captured} {library.name} [{library.shader?.name}] queue {library.renderQueue}";
                        variants.Add((label, () =>
                        {
                            var m = new Material(library) { hideFlags = HideFlags.DontSave };
                            if (m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", tex);
                            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                            if (m.HasProperty("_DoubleSidedEnable")) m.SetFloat("_DoubleSidedEnable", 1f);
                            if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", 0f);
                            if (m.HasProperty("_LodFade") || m.HasProperty("colossal_LodFade")) m.SetFloat("colossal_LodFade", 1f);
                            return m;
                        }));
                    }
                }
                catch (Exception ex)
                {
                    report.Append("[library: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("] ");
                }
                int frames = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    pane.SetActive(pass == 1);
                    if (pass == 1)
                    {
                        // is the pane a real transparent surface? the wall seen through it must differ from the plain wall, but only a little
                        await RenderFrames(cam, 6);
                        Color32 behind = At(Read(rt), 0.15f, 0.5f);
                        report.Append($"wall through the pane {Rgb(behind)}; ");
                    }
                    foreach ((string name, Func<Material> make) in variants)
                    {
                        Material m = make();
                        if (m == null) { report.Append($"[{name}: material is null] "); continue; }
                        GameObject q = Quad("test", new Vector3(0f, 0f, 6f), new Vector3(2.4f, 1.2f, 1f), m);
                        await RenderFrames(cam, 6);
                        Color32[] px = Read(rt);
                        frames += 6;
                        Color32 left = At(px, 0.43f, 0.5f), right = At(px, 0.57f, 0.5f), wall = At(px, 0.15f, 0.5f);
                        if (pass == 0)
                        {
                            var keywords = new StringBuilder();
                            foreach (string keyword in m.shaderKeywords)
                                if (keyword.IndexOf("DECAL", StringComparison.OrdinalIgnoreCase) >= 0 || keyword.IndexOf("ALPHA", StringComparison.OrdinalIgnoreCase) >= 0) keywords.Append(keyword).Append(' ');
                            report.Append($"[{name}: red half {(Red(left) ? "drawn" : "NOT drawn")} {Rgb(left)}, see-through half {(Magenta(right) ? "MAGENTA (not cut out)" : "cut out")} {Rgb(right)}, wall {Rgb(wall)}; queue {m.renderQueue}, _SupportDecals {(m.HasProperty("_SupportDecals") ? m.GetFloat("_SupportDecals").ToString("0.##") : "absent")}, keywords {(keywords.Length > 0 ? keywords.ToString().TrimEnd() : "none")}] ");
                        }
                        else
                            report.Append($"[behind the pane: {name}: red half {(Red(left) ? "visible" : "HIDDEN")} {Rgb(left)}, wall {Rgb(wall)}] ");
                        if (pass == 0 && name.StartsWith("opaque cut-out, queue 2225"))
                        {
                            var shot = new Texture2D(kW, kH, TextureFormat.RGBA32, false);
                            shot.SetPixels32(px);
                            shot.Apply();
                            File.WriteAllBytes(Path.Combine(Mod.ModDirectory, BusCitizenProbe.kImage), shot.EncodeToPNG());
                            UnityEngine.Object.Destroy(shot);
                        }
                        UnityEngine.Object.Destroy(q);
                        UnityEngine.Object.Destroy(m);
                    }
                }
                // The gap test: the game's glass (the library's DefaultGlassDoubleSided, set up as BusGlass sets the window glass up) a few
                // millimetres BEHIND the wrap, as a door's glass is behind the door piece. If the glass shows over the wrap, the red half
                // changes colour (it is seen through the glass: tinted, reflecting). The wrap quad stands 8 m from the camera.
                {
                    Material glassTemplate = null;
                    for (int i = 0; i < 64 && glassTemplate == null; i++)
                    {
                        Material candidate;
                        try { candidate = Colossal.IO.AssetDatabase.AssetDatabase.global.resources.materialLibrary.GetMaterial(i); }
                        catch (Exception) { break; }
                        if (candidate == null) break;
                        if (candidate.name == "DefaultGlassDoubleSided") glassTemplate = candidate;
                    }
                    pane.SetActive(false);
                    if (glassTemplate == null)
                        report.Append("[gap test: no DefaultGlassDoubleSided in the library] ");
                    else
                    {
                        var glass = new Material(glassTemplate) { hideFlags = HideFlags.DontSave };
                        glass.SetFloat("colossal_LodFade", 1f);
                        if (glass.HasProperty("_ReceivesSSRTransparent")) glass.SetFloat("_ReceivesSSRTransparent", 0f);
                        if (glass.HasProperty("_TransparentDepthPrepassEnable")) glass.SetFloat("_TransparentDepthPrepassEnable", 0f);
                        HDMaterial.ValidateMaterial(glass);
                        glass.SetShaderPassEnabled("TransparentDepthPrepass", false);
                        cleanup.Add(() => UnityEngine.Object.Destroy(glass));
                        Material wrapMaterial = CabMaterials.Wrap(tex);
                        GameObject front = Quad("gap-wrap", new Vector3(0f, 0f, 8f), new Vector3(2.4f, 1.2f, 1f), wrapMaterial);
                        await RenderFrames(cam, 6);
                        Color32 baseline = At(Read(rt), 0.43f, 0.5f);
                        report.Append($"[gap test: the wrap alone, red half {Rgb(baseline)}] ");
                        foreach (float gap in new[] { 0.009f, 0.02f, 0.04f, 0.08f, 0.15f })
                        {
                            GameObject back = Quad("gap-glass", new Vector3(0f, 0f, 8f + gap), new Vector3(3f, 2f, 1f), glass);
                            await RenderFrames(cam, 6);
                            Color32 seen = At(Read(rt), 0.43f, 0.5f);
                            int change = Math.Abs(seen.r - baseline.r) + Math.Abs(seen.g - baseline.g) + Math.Abs(seen.b - baseline.b);
                            report.Append($"[glass {gap * 1000f:0} mm behind the wrap: red half {Rgb(seen)}, change {change} => {(change > 12 ? "THE GLASS SHOWS OVER THE WRAP" : "the wrap stays in front")}] ");
                            UnityEngine.Object.Destroy(back);
                        }
                        UnityEngine.Object.Destroy(front);
                        UnityEngine.Object.Destroy(wrapMaterial);
                    }
                }
                return "ok; material experiment: " + report;
            }
            catch (Exception ex)
            {
                return $"failed: {ex.GetType().Name}: {ex.Message} {ex.StackTrace?.Split('\n')[0]} after {report}";
            }
            finally
            {
                foreach (Action a in cleanup)
                {
                    try { a(); } catch (Exception) { }
                }
            }
        }

        private static async Task RenderFrames(Camera cam, int count)
        {
            for (int i = 0; i < count; i++)
            {
                cam.Render();
                await Task.Yield();
            }
        }

        // A test wrap (a zip for the model driven): stripes, and see-through parts (alpha 0) whose colour is bright magenta: across the rows of the windows
        // (v .36 to .78 of the face's height) and a disc. Where a layer that should cut out does not, the magenta shows.
        private static void TestWrap(string zip)
        {
            const int w = 1024, h = 400;
            var entries = new List<KeyValuePair<string, byte[]>>();
            foreach (string file in BusSkins.kFiles)
            {
                var px = new Color32[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float fromFront = x / (float)w;
                        Color c = Color.HSVToRGB(0.66f * fromFront, 0.9f, ((x / 32) & 1) == 0 ? 1f : 0.75f);
                        if (y > h * 0.45f && y < h * 0.55f) c = Color.white;
                        Color32 p = c;
                        float v = y / (float)h;
                        float dx = x - 0.7f * w, dy = y - 0.2f * h;
                        if ((v > 0.36f && v < 0.78f) || dx * dx + dy * dy < 40f * 40f)
                            p = new Color32(255, 0, 255, 0);
                        px[y * w + x] = p;
                    }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(px);
                tex.Apply();
                entries.Add(new KeyValuePair<string, byte[]>(file, tex.EncodeToPNG()));
                UnityEngine.Object.Destroy(tex);
            }
            var manifest = new WrapPackage.Manifest { Model = BusSkins.Model, Layout = ProbeLayout(), Title = "probe test" };
            WrapPackage.Write(zip, manifest, entries);
        }

        private static int ProbeLayout() => BusSkins.Layout;

        private static Color32[] Read(RenderTexture rt)
        {
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = was;
            Color32[] pixels = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);
            return pixels;
        }
    }
}
