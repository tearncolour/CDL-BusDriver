using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior.Models;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Developer probe (a "probe=citizen" request, cs2_research/bus_asset/probe_citizen.sh): the game's citizens as BusCitizens
    /// makes them for the bus, checked on the main menu. Figures in the four poses (seated, standing, holding a strap, the driver)
    /// at rider slots, with markers where the bus has its seat cushion, the strap's handle and the wheel's grips, rendered by a
    /// private camera to citizen-probe.png in the mod's folder; the result says how much of each view is covered.
    /// </summary>
    internal static class BusCitizenProbe
    {
        internal const string kImage = "citizen-probe.png";
        private const int kW = 320, kH = 560;

        internal static async Task<string> Run(EntityManager em, PrefabSystem prefabs, EntityQuery humans)
        {
            var log = new StringBuilder();
            var cleanup = new List<Action>();
            var citizens = new BusCitizens(em, prefabs, humans) { Synchronous = true };
            citizens.Trace = s => log.Append(s).Append("; ");
            cleanup.Add(() => { BusCitizens.s_OnlyLevel = -1; BusCitizens.s_ShowShadowCaster = false; });
            cleanup.Add(citizens.Dispose);
            try
            {
                // the game initialises its virtual-texture materials in the background after start: wait for that (up to 5 min)
                var streaming = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<Colossal.IO.AssetDatabase.VirtualTexturing.TextureStreamingSystem>();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int left = streaming != null ? streaming.VTMaterialsLeftToLoadCount + streaming.VTMaterialsDuplicatesToProcessCount : -1;
                log.Append($"VT materials left to initialise at start: {left}; ");
                while (streaming != null && streaming.VTMaterialsLeftToLoadCount + streaming.VTMaterialsDuplicatesToProcessCount > 0 && clock.Elapsed.TotalSeconds < 300)
                    await Task.Delay(500);
                log.Append($"waited {clock.Elapsed.TotalSeconds:0} s, {(streaming != null ? streaming.VTMaterialsLeftToLoadCount + streaming.VTMaterialsDuplicatesToProcessCount : -1)} left; ");

                Vector3 origin = new Vector3(0f, 3000f, 0f);
                var root = new GameObject("CDLC citizen probe") { hideFlags = HideFlags.DontSave };
                root.transform.position = origin;
                cleanup.Add(() => UnityEngine.Object.Destroy(root));
                Material marker = Marker();
                if (marker != null)
                    cleanup.Add(() => UnityEngine.Object.Destroy(marker));

                // seated, standing, holding, the driver, and two more seated people; 3 m apart along X; a row (10 m along Z) per level
                // of detail, each drawn alone (the full meshes, the lighter level), and the shadow casters
                const int kLevels = 3;
                var poses = new[] { BusCitizens.Pose.Seated, BusCitizens.Pose.Standing, BusCitizens.Pose.Holding, BusCitizens.Pose.Driver, BusCitizens.Pose.Seated, BusCitizens.Pose.Standing };
                for (int i = 0; i < poses.Length * kLevels; i++)
                {
                    int row = i / poses.Length, column = i % poses.Length;
                    BusCitizens.s_OnlyLevel = Math.Min(row, 1);
                    BusCitizens.s_ShowShadowCaster = row == 2;           // the third row: the shadow caster alone
                    citizens.Trace = column == 0 ? (Action<string>)(s => log.Append(s).Append("; ")) : null;
                    bool seated = poses[column] == BusCitizens.Pose.Seated || poses[column] == BusCitizens.Pose.Driver;
                    var slot = new BusInterior.RiderSlot { Position = new float3(column * 3f, seated ? 0.47f : 0f, row * 10f), Yaw = 0f, Kind = seated ? BusInterior.RiderKind.Seated : BusInterior.RiderKind.Standing };
                    GameObject figure = citizens.Make(root.transform, slot, poses[column], Entity.Null, 11 * column + 3);
                    log.Append($"{poses[column]}: {(figure != null ? figure.name + ", " + figure.GetComponentsInChildren<MeshRenderer>().Length + " renderers" : "none")}; ");
                    // the markers: the cushion (a seat), the strap's handle, the wheel's grips
                    Vector3 at = new Vector3(slot.Position.x, slot.Position.y, slot.Position.z);
                    if (seated)
                        Box(root.transform, marker, at + new Vector3(0f, -0.04f, -0.02f), new Vector3(0.44f, 0.08f, 0.42f));
                    if (poses[column] == BusCitizens.Pose.Holding)
                        Box(root.transform, marker, at + (Vector3)(float3)BusRiders.HoldingHand + new Vector3(0f, 0.09f, 0f), new Vector3(0.12f, 0.02f, 0.02f));
                    if (poses[column] == BusCitizens.Pose.Driver)
                    {
                        Box(root.transform, marker, at + (Vector3)(float3)BusRiders.DriverHandL, new Vector3(0.03f, 0.03f, 0.03f));
                        Box(root.transform, marker, at + (Vector3)(float3)BusRiders.DriverHandR, new Vector3(0.03f, 0.03f, 0.03f));
                    }
                }
                if (citizens.Failed)
                    return "failed: " + log;

                var sunObject = new GameObject("CDLC probe sun") { hideFlags = HideFlags.DontSave };
                Light sun = sunObject.AddComponent<Light>();
                sun.type = UnityEngine.LightType.Directional;
                sun.shadows = LightShadows.None;                // the game's sun casts the shadows: HDRP allows one directional light with them
                sunObject.AddComponent<HDAdditionalLightData>().intensity = 30000f;
                var camObject = new GameObject("CDLC probe camera") { hideFlags = HideFlags.DontSave };
                Camera cam = camObject.AddComponent<Camera>();
                cam.enabled = false;
                Plain(camObject.AddComponent<HDAdditionalCameraData>());
                cam.fieldOfView = 26f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 200f;
                var rt = new RenderTexture(kW, kH, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                cam.targetTexture = rt;
                cleanup.Add(() => { UnityEngine.Object.Destroy(camObject); UnityEngine.Object.Destroy(sunObject); rt.Release(); UnityEngine.Object.Destroy(rt); });

                var views = new (int figure, Vector3 dir, float height, string name)[]
                {
                    (0, new Vector3(1f, 0.15f, 0.25f), 0.6f, "seated side"), (2, new Vector3(0.35f, 0.1f, 1f), 0.95f, "holding"),
                    (3, new Vector3(1f, 0.2f, 0.35f), 0.6f, "driver side"), (3, new Vector3(0.05f, 1f, 0.3f), 0.5f, "driver top"),
                    (4, new Vector3(0.3f, 0.15f, 1f), 0.6f, "seated front"), (5, new Vector3(-0.3f, 0.1f, 1f), 0.95f, "standing"),
                };
                int aimRow = 0;
                void Aim(int v)
                {
                    Vector3 target = origin + new Vector3(views[v].figure * 3f, views[v].height, aimRow * 10f), dir = views[v].dir.normalized;
                    cam.transform.position = target + dir * 4.6f;
                    cam.transform.LookAt(target);
                    sunObject.transform.rotation = Quaternion.LookRotation(-dir + Vector3.down * 0.8f);
                }
                for (int frame = 0; frame < 200; frame++)
                {
                    citizens.RequestTextures(1024f);
                    aimRow = frame / views.Length % kLevels;
                    Aim(frame % views.Length);
                    cam.Render();
                    await Task.Delay(15);
                }
                var shots = new Color32[views.Length * kLevels][];
                for (int k = 0; k < shots.Length; k++)
                {
                    aimRow = k / views.Length;
                    Aim(k % views.Length);
                    cam.Render();
                    shots[k] = Read(rt);
                }
                SavePng(shots, Path.Combine(Mod.ModDirectory, kImage), views.Length);
                return (citizens.Failed ? "failed: " : "ok; ") + log;
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

        private static Material Marker()
        {
            Shader lit = Shader.Find("HDRP/Lit");
            if (lit == null)
                return null;
            var m = new Material(lit) { hideFlags = HideFlags.DontSave };
            m.SetColor("_BaseColor", new Color(0.9f, 0.5f, 0.1f));
            try { HDMaterial.ValidateMaterial(m); } catch (Exception) { }
            return m;
        }

        private static void Box(Transform parent, Material material, Vector3 local, Vector3 size)
        {
            if (material == null)
                return;
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.hideFlags = HideFlags.DontSave;
            UnityEngine.Object.Destroy(box.GetComponent<Collider>());
            box.transform.SetParent(parent, false);
            box.transform.localPosition = local;
            box.transform.localScale = size;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void Plain(HDAdditionalCameraData hd)
        {
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = new Color(0.3f, 0.42f, 0.58f, 1f);
            hd.customRenderingSettings = true;
            FrameSettings fs = hd.renderingPathCustomFrameSettings;
            FrameSettingsOverrideMask mask = hd.renderingPathCustomFrameSettingsOverrideMask;
            foreach (FrameSettingsField f in new[] { FrameSettingsField.MotionBlur, FrameSettingsField.DepthOfField, FrameSettingsField.Volumetrics, FrameSettingsField.VolumetricClouds })
            {
                mask.mask[(uint)f] = true;
                fs.SetEnabled(f, false);
            }
            hd.renderingPathCustomFrameSettings = fs;
            hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
        }

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

        // The share of the view not in the background's colour (a corner pixel).
        private static float Coverage(Color32[] pixels)
        {
            if (pixels.Length == 0)
                return 0f;
            Color32 c = pixels[pixels.Length - 1];
            int n = 0;
            foreach (Color32 p in pixels)
                if (Math.Abs(p.r - c.r) + Math.Abs(p.g - c.g) + Math.Abs(p.b - c.b) > 24)
                    n++;
            return n / (float)pixels.Length;
        }

        // The shots in rows of `columns`, the first row at the top.
        private static void SavePng(Color32[][] shots, string path, int columns)
        {
            int rows = (shots.Length + columns - 1) / columns;
            var tex = new Texture2D(kW * columns, kH * rows, TextureFormat.RGBA32, false);
            for (int i = 0; i < shots.Length; i++)
                tex.SetPixels32(i % columns * kW, (rows - 1 - i / columns) * kH, kW, kH, shots[i]);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }
    }
}
