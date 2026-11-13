using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Developer benchmark (a "probe=perf" request, cs2_research/bus_asset/probe_citizen.sh perf): what the passengers cost to
    /// draw. On the main menu it fills a bus-sized layout (30 seated, 16 standing) with the simple figures and with the game's
    /// citizens, each with and without the optimisations (levels of detail, trimmed shadows), and renders each with a private
    /// 1600 x 900 camera, lit by the game's sun, from three views (down the saloon, from outside, the driver's forward view with the
    /// people behind: only their shadows), timing the render (CPU) and the render with the GPU finished (total), with the frame's
    /// draw calls and vertices. Nothing of the city is involved: the numbers compare the figures, they are not a game frame.
    /// </summary>
    internal static class BusPerfProbe
    {
        private const int kW = 1600, kH = 900, kWarm = 6, kTimed = 10;

        private struct Sample
        {
            internal double Cpu, Total;
            internal long Draws, Vertices, Casters;
        }

        internal static async Task<string> Run(EntityManager em, PrefabSystem prefabs, EntityQuery humans)
        {
            var log = new StringBuilder();
            var cleanup = new List<Action>();
            var citizens = new BusCitizens(em, prefabs, humans) { Synchronous = true };
            cleanup.Add(citizens.Dispose);
            bool levels = BusCitizens.s_Levels, trim = BusCitizens.s_TrimShadows, riderLevels = BusCabSystem.s_RiderLevels;
            cleanup.Add(() => { BusCitizens.s_Levels = levels; BusCitizens.s_TrimShadows = trim; BusCabSystem.s_RiderLevels = riderLevels; });
            try
            {
                var streaming = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<Colossal.IO.AssetDatabase.VirtualTexturing.TextureStreamingSystem>();
                var clock = Stopwatch.StartNew();
                while (streaming != null && streaming.VTMaterialsLeftToLoadCount + streaming.VTMaterialsDuplicatesToProcessCount > 0 && clock.Elapsed.TotalSeconds < 300)
                    await Task.Delay(500);

                KitModelData data = KitBuild.BuildAll(new BusRiders(), new MeshKit());
                if (data.Error != null)
                    return "failed: riders: " + data.Error;
                var model = new CabModel(data, 3);
                cleanup.Add(model.Destroy);

                Vector3 origin = new Vector3(0f, 3000f, 0f);
                var slots = Layout();

                // lit by the game's own sun (HDRP allows one directional light with shadows: a second one fails the shadow atlas)
                var camObject = new GameObject("CDLC perf camera") { hideFlags = HideFlags.DontSave };
                Camera cam = camObject.AddComponent<Camera>();
                cam.enabled = false;
                var hd = camObject.AddComponent<HDAdditionalCameraData>();
                hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                hd.backgroundColorHDR = new Color(0.3f, 0.42f, 0.58f, 1f);
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 500f;
                var rt = new RenderTexture(kW, kH, 24, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.DontSave };
                cam.targetTexture = rt;
                cleanup.Add(() => { UnityEngine.Object.Destroy(camObject); rt.Release(); UnityEngine.Object.Destroy(rt); });

                var views = new (string name, Vector3 eye, Vector3 at)[]
                {
                    ("saloon", new Vector3(0f, 1.65f, 8.8f), new Vector3(0f, 0.9f, 0f)),
                    ("outside", new Vector3(-11f, 2.2f, 4.2f), new Vector3(0f, 1.0f, 4.2f)),
                    ("forward", new Vector3(0f, 1.62f, 8.8f), new Vector3(0f, 1.4f, 20f)),
                };

                var configs = new (string name, Func<Transform, int> fill)[]
                {
                    ("empty", root => 0),
                    ("simple, full meshes", root => { BusCabSystem.s_RiderLevels = false; return Simple(model, root, slots); }),
                    ("simple, levels", root => { BusCabSystem.s_RiderLevels = true; return Simple(model, root, slots); }),
                    ("citizens, before", root => { BusCitizens.s_Levels = false; BusCitizens.s_TrimShadows = false; return Citizens(citizens, root, slots); }),
                    ("citizens, optimised", root => { BusCitizens.s_Levels = true; BusCitizens.s_TrimShadows = true; return Citizens(citizens, root, slots); }),
                };

                using (ProfilerRecorder draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count"))
                using (ProfilerRecorder vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count"))
                using (ProfilerRecorder casters = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count"))
                {
                    log.Append($"counters: draws {(draws.Valid ? "yes" : "no")}, vertices {(vertices.Valid ? "yes" : "no")}, casters {(casters.Valid ? "yes" : "no")}; ");
                    foreach (var config in configs)
                    {
                        var root = new GameObject("CDLC perf " + config.name) { hideFlags = HideFlags.DontSave };
                        root.transform.position = origin;
                        int figures = config.fill(root.transform);
                        int renderers = root.GetComponentsInChildren<Renderer>(true).Length;
                        foreach (var view in views)
                        {
                            cam.transform.position = origin + view.eye;
                            cam.transform.LookAt(origin + view.at);
                            var samples = new List<Sample>();
                            for (int i = 0; i < kWarm + kTimed; i++)
                            {
                                citizens.RequestTextures(512f);
                                Sync(rt);
                                var sw = Stopwatch.StartNew();
                                cam.Render();
                                double cpu = sw.Elapsed.TotalMilliseconds;
                                Sync(rt);
                                double total = sw.Elapsed.TotalMilliseconds;
                                await Task.Yield();
                                if (i >= kWarm)
                                    samples.Add(new Sample { Cpu = cpu, Total = total, Draws = draws.LastValue, Vertices = vertices.LastValue, Casters = casters.LastValue });
                            }
                            log.Append($"{config.name} [{figures} figures, {renderers} renderers] {view.name}: draws {samples.Average(s => (double)s.Draws):0}, " +
                                $"vertices {samples.Average(s => (double)s.Vertices) / 1000:0}k, shadow casters {samples.Average(s => (double)s.Casters):0}; ");
                        }
                        UnityEngine.Object.Destroy(root);
                        await Task.Yield();
                    }
                }
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

        // A 12 m bus's people: four seats a row (cushions 0.45 m up, facing the front), 30 of them; 16 standing in the aisle and at
        // the doors, half of them holding a strap.
        private static List<BusInterior.RiderSlot> Layout()
        {
            var slots = new List<BusInterior.RiderSlot>();
            float[] xs = { -0.95f, -0.5f, 0.5f, 0.95f };
            for (int i = 0; i < 30; i++)
                slots.Add(new BusInterior.RiderSlot { Position = new float3(xs[i % 4], 0.45f, 0.6f + (i / 4) * 0.8f), Yaw = 0f, Kind = BusInterior.RiderKind.Seated });
            for (int i = 0; i < 16; i++)
                slots.Add(new BusInterior.RiderSlot
                {
                    Position = new float3((i & 1) == 0 ? -0.12f : 0.18f, 0f, 1.0f + (i / 2) * 0.8f), Yaw = (i & 1) == 0 ? 1.3f : -1.6f,
                    Kind = (i & 2) == 0 ? BusInterior.RiderKind.Holding : BusInterior.RiderKind.Standing,
                });
            return slots;
        }

        private static int Simple(CabModel model, Transform root, List<BusInterior.RiderSlot> slots)
        {
            int n = 0;
            for (int i = 0; i < slots.Count; i++)
                if (BusCabSystem.SimpleFigure(model, BusRiders.PartFor(slots[i].Kind, i), root, slots[i], "figure " + i, BusCabSystem.s_RiderLevels) != null)
                    n++;
            return n;
        }

        private static int Citizens(BusCitizens citizens, Transform root, List<BusInterior.RiderSlot> slots)
        {
            int n = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                BusCitizens.Pose pose = slots[i].Kind == BusInterior.RiderKind.Seated ? BusCitizens.Pose.Seated :
                    slots[i].Kind == BusInterior.RiderKind.Holding ? BusCitizens.Pose.Holding : BusCitizens.Pose.Standing;
                if (citizens.Make(root, slots[i], pose, Entity.Null, 7 * i + 1) != null)
                    n++;
            }
            return n;
        }

        // Waits for the GPU: a one-pixel read back after everything queued.
        private static void Sync(RenderTexture rt) => AsyncGPUReadback.Request(rt, 0, 0, 1, 0, 1, 0, 1, TextureFormat.RGBA32).WaitForCompletion();

        private static double Median(IEnumerable<double> values)
        {
            double[] v = values.OrderBy(x => x).ToArray();
            return v.Length == 0 ? 0 : v[v.Length / 2];
        }
    }
}
