using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.AssetPipeline;
using Colossal.AssetPipeline.Importers;
using Colossal.IO.AssetDatabase;
using Game;
using Game.AssetPipeline;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.UI.Editor;
using CDLC.BusDriver.Interior.Models;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Makes the asset package of each of our buses as one of the game's bus models (Asset/<name>.cok: CDLCBus12, the 12 m city bus,
    /// and SWB6710EV01G, the Sunwin 7 m electric bus), which the mod ships and the game loads from the mod's folder like any asset
    /// mod; the same package works on its own in the player's .packages folder, without the mod. A developer's tool, run
    /// by pack_asset.sh: it puts a request file in the deployed mod and starts the game; on the main menu this system runs what the
    /// editor runs, without the editor:
    ///  * the game's asset importer on the model files (FBX and PNG, made from the mod's model by the asset exporter): the mesh
    ///    prefab with body and window glass, three LODs, the wheel bones, the baked lamp layers;
    ///  * a copy of the vanilla diesel bus (Bus01: capacity, speed, AI, sounds) with that mesh, the wheel bone types, the lamp
    ///    purposes, the line colour on the livery, and no icon of its own (the game renders the model's thumbnail);
    ///  * the editor's packaging (dependencies, prefab resave, virtual texturing) into a .cok, copied where the request says;
    ///  * then the working files (the import's data, the package in the player's packages) deleted, a result file written, and
    ///    the game quit.
    /// A verify request (the script's second start, with the package deployed) checks the bus as the game loaded it at boot: where
    /// it came from, its vehicle data, LODs, wheel bones, lamps, line colour and sounds. Without a request file it does nothing.
    /// </summary>
    public partial class BusVehicleAssetSystem : GameSystemBase
    {
        /// <summary>The prefab's name (CarPrefab:CDLCBus12), the asset folder's and the package's.</summary>
        internal const string kName = OwnBus.Name;
        internal const string kRequestFile = "pack-request.txt";
        internal const string kResultFile = "pack-result.txt";
        private const string kSeed = "CDLCBusDriver";      // the import's working folder (ImportedData/<seed>); a dot would be escaped

        /// <summary>One of our bus models: its name (prefab, asset folder, package), the vanilla bus it copies (capacity, speed,
        /// AI, sounds: diesel Bus01, electric Bus02), its own capacity (0: the template's) and its body colour without a line.</summary>
        internal struct Model
        {
            internal string Name;
            internal string Template;
            internal int Capacity;
            internal Color Livery;
            // the audited strict pipeline (SWB6109EV12G's): a unique import seed, the single-mip palette import, the checked
            // export, the shared glass surface with its coat/prepass settings, three LODs with bones
            internal bool Strict;
            internal int[] LightLayers;
            // three LODs on one glass surface saved with its coat/prepass settings and checked in the package (Strict implies it)
            internal bool SharedGlass;
            // the shared glass's coat (_CoatMask), the pack block's glassCoatMask (PackSettings.AuditedGlassCoatMask without one)
            internal float GlassCoatMask;
            internal float? GlassNormalStrength;
            internal string Profile;       // the vehicle profile it came from (null: the mod's own bus)
            internal string Icon;          // the PNG of pack.icon beside the profile (null: the game renders the icon)
            // The vehicle prefab's identity in saves (PrefabID = type + name + this GUID): frozen at the value already in use, so a
            // repack keeps it (a strict model's import folder is new every time, which gave it a new GUID each pack and left
            // "unknown prefab" ghosts in the saves of the older builds). null: whatever the import path gives.
            internal string Guid;
            // Every earlier GUID of this vehicle prefab that saves may still hold: they resolve to this prefab (ObsoleteIdentifiers).
            internal string[] ObsoleteGuids;
            internal VehicleAssembly Assembly;
            internal VehicleProfile SectionProfile;
            internal PackSettings.Section[] NativeSections;
        }

        /// <summary>The mod's own bus, packed by the same request (name=...) as the vehicle packs' buses: a self-contained .cok in
        /// the mod's Asset folder, only the game's own components, nothing of the mod's code. An authored bus's settings come
        /// from its vehicle profile's "pack" block (Tools/&lt;model&gt;/vehicle.json, or the pack's &lt;model&gt;.cdlb.json).</summary>
        internal static readonly Model[] s_Models =
        {
            new Model { Name = kName, Template = "Bus01", Livery = new Color(0.1f, 0.28f, 0.62f), Guid = "5f3b315fd4839cf3fd555cd8faa5b1e6" },
        };

        // profile=<path> of the running request, by model name
        private static readonly Dictionary<string, string> s_RequestProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static BusVehicleAssetSystem s_Instance;

        /// <summary>
        /// A bus model to pack or check: the mod's own, or an authored one by its vehicle profile - the request's profile=, else
        /// the profile beside the loaded bus's package (a vehicle pack).
        /// </summary>
        internal static Model ModelNamed(string name)
        {
            foreach (Model m in s_Models)
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    return m;
            string path = s_RequestProfiles.TryGetValue(name ?? "", out string requested) ? requested : null;
            if (path == null && s_Instance != null)
                path = VehiclePacks.ProfilePath(s_Instance.FindOurBus(name)?.asset?.path, name);
            if (path == null || !File.Exists(path))
                throw new ArgumentException($"No bus model named '{name}': not the mod's own ({kName}) and no vehicle profile (profile=, or a pack's {name}{VehiclePacks.ProfileExtension})");
            return ModelFromProfile(name, path);
        }

        /// <summary>Does this bus's package carry one shared glass surface with the saved coat/prepass settings (pack.sharedGlass or
        /// pack.strict)? False when it is no model of ours.</summary>
        internal static bool HasSharedGlass(string name)
        {
            try { return ModelNamed(name).SharedGlass; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>A model's packing settings from its vehicle profile's "pack" block (PackSettings).</summary>
        internal static Model ModelFromProfile(string name, string path)
        {
            PackSettings pack = PackSettings.Parse(File.ReadAllText(path), name, path);
            VehicleProfile profile = VehicleProfileJson.Load(path, requireMesh: false);
            VehicleProfile selected = profile.Assembly == null ? profile :
                profile.Assembly.ForPrefab(name) ?? (name == profile.Assembly.Model ? profile.Assembly.Front.Profile : null);
            if (selected == null) throw new ArgumentException(path + " does not declare section " + name);
            PackSettings.Section native = pack.Sections.FirstOrDefault(s => s.Prefab == selected.Name);
            return new Model
            {
                Name = selected.Name, Template = pack.Template, Capacity = native?.Capacity ?? pack.Capacity, Livery = new Color(pack.Livery.x, pack.Livery.y, pack.Livery.z),
                Strict = pack.Strict, SharedGlass = pack.SharedGlass, GlassCoatMask = pack.GlassCoatMask, GlassNormalStrength = pack.GlassNormalStrength, Profile = path, Guid = native?.Guid ?? pack.Guid, ObsoleteGuids = native?.ObsoleteGuids ?? pack.ObsoleteGuids,
                Icon = pack.Icon == null ? null : Path.Combine(Path.GetDirectoryName(path), pack.Icon),
                Assembly = profile.Assembly, SectionProfile = selected, NativeSections = pack.Sections, LightLayers = pack.LightLayers,
            };
        }

        private static Model[] SectionsOf(Model model)
        {
            if (model.Assembly == null) return new[] { model };
            return new[] { model.Assembly.Front, model.Assembly.Rear }.Select(binding =>
            {
                Model part = model;
                PackSettings.Section native = model.NativeSections.Single(s => s.Id == binding.Id && s.Prefab == binding.Prefab);
                part.Name = binding.Prefab; part.SectionProfile = binding.Profile;
                part.Capacity = native.Capacity; part.Guid = native.Guid; part.ObsoleteGuids = native.ObsoleteGuids;
                return part;
            }).ToArray();
        }

        private static (string bone, BoneType type)[] BonesOf(Model model)
        {
            if (model.Assembly == null) return s_Bones;
            return model.SectionProfile.Axles.SelectMany(a => new[]
            {
                (a.LeftPart == 19 ? "WheelFL" : "WheelRL", a.Steer ? BoneType.SteeringTire : BoneType.RollingTire),
                (a.RightPart == 20 ? "WheelFR" : "WheelRR", a.Steer ? BoneType.SteeringTire : BoneType.RollingTire),
            }).ToArray();
        }

        // The exporter's wheel bones, and its lamp groups by light layer (the EmissiveID texture's red value).
        private static readonly (string bone, BoneType type)[] s_Bones =
        {
            ("WheelFL", BoneType.SteeringTire), ("WheelFR", BoneType.SteeringTire),
            ("WheelRL", BoneType.RollingTire), ("WheelRR", BoneType.RollingTire),
        };

        private static readonly EmissiveProperties.Purpose[] s_Lights =
        {
            EmissiveProperties.Purpose.None,
            EmissiveProperties.Purpose.DaytimeRunningLight,
            EmissiveProperties.Purpose.Headlight_LowBeam,
            EmissiveProperties.Purpose.RearLight,
            EmissiveProperties.Purpose.BrakeLight,
            EmissiveProperties.Purpose.TurnSignalLeft,
            EmissiveProperties.Purpose.TurnSignalRight,
            EmissiveProperties.Purpose.ReverseLight,
            EmissiveProperties.Purpose.Interior1,
            EmissiveProperties.Purpose.DecorativeLight,     // the LED signs: always lit
        };

        private PrefabSystem m_PrefabSystem;
        private bool m_Done;
        private double m_NextRequestCheck;
        private static int s_TestQuitDelaySeconds;
        private static bool s_TestAutoQuitPending, s_TestAutoQuitCancelled;
        private static bool s_RoadOwnsTransition;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            s_Instance = this;
        }

        protected override void OnUpdate()
        {
            GameManager game = GameManager.instance;
            // A developer request owns auto-exit only while the user stays in
            // the ready main menu. Latch cancellation even if a save is loaded
            // and later returns to the menu before the diagnostic finishes.
            ObserveTestQuitOwnership(game);
            if (game == null || Mod.ModDirectory == null)
                return;
            PumpReviewSession(game);
            if (m_Done || s_Review.Busy || s_Review.Stopping || s_LegacyBusy) return;

            // On the main menu, once the game's prefabs are loaded (the template bus among them).
            if (game.gameMode != GameMode.MainMenu || game.state != GameManager.State.WorldReady || game.isGameLoading)
                return;

            // looked for twice a second, not every frame (a file system call on the main thread)
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < m_NextRequestCheck)
                return;
            m_NextRequestCheck = now + 0.5;
            string request = Path.Combine(Mod.ModDirectory, kRequestFile);
            if (!File.Exists(request))
                return;
            // its checks load our materials: not before HDRP exists (PipelineReadyForChecks)
            if (!PipelineReadyForChecks(now))
                return;
            m_Done = true;
            s_LegacyBusy = true;
            s_LegacyInterrupted = false;
            try { StartLegacyRequest(request); }
            catch (Exception error) { Finish("failed legacy request: " + error, false); }
        }

        private void StartLegacyRequest(string request)
        {
            Dictionary<string, string> args = File.ReadAllLines(request)
                .Select(line => line.Split(new[] { '=' }, 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim());
            File.Delete(request);
            bool quitAfter = args.TryGetValue("quit", out string q) && q == "1";
            s_TestAutoQuitPending = quitAfter;
            s_TestAutoQuitCancelled = false;
            // Only an explicit developer request changes exit timing. Normal
            // gameplay and existing requests retain their previous behavior.
            s_TestQuitDelaySeconds = 0;
            if (args.TryGetValue("quit_delay_seconds", out string delayText))
            {
                if (!int.TryParse(delayText, out s_TestQuitDelaySeconds) || s_TestQuitDelaySeconds < 0 || s_TestQuitDelaySeconds > 120)
                {
                    s_TestQuitDelaySeconds = 0;
                    Finish("failed invalid quit_delay_seconds (expected 0..120)", quitAfter);
                    return;
                }
            }
            if (args.TryGetValue("probe", out string wrapProbe) && wrapProbe == "wrap")
            {
                ProbeWrap(args.TryGetValue("wrap", out string wrap) ? wrap : null, quitAfter, args.TryGetValue("model", out string wrapModel) ? wrapModel : null, args.TryGetValue("variants", out string wrapVariants) && wrapVariants == "1");
                return;
            }
            if (args.TryGetValue("probe", out string glassProbe) && glassProbe == "glass")
            {
                ProbeGlass(args.TryGetValue("names", out string glassNames) && !string.IsNullOrEmpty(glassNames) ? glassNames : kName, quitAfter);
                return;
            }
            if (args.TryGetValue("mode", out string roadMode) && (roadMode == "assembly-road" || roadMode == "glass-road"))
            {
                // Dedicated new-map owner; the normal main-menu review latch must not cancel its own NewGame transition.
                s_TestAutoQuitPending = false; s_TestAutoQuitCancelled = true;
                ProbeRoad(args); return;
            }
            if ((args.TryGetValue("probe", out string assemblyProbe) && assemblyProbe == "assembly") ||
                (args.TryGetValue("mode", out string assemblyMode) && assemblyMode == "assembly"))
            {
                string assemblyName = args.TryGetValue("name", out string one) ? one :
                    args.TryGetValue("names", out string many) ? many : "";
                ProbeAssembly(assemblyName, quitAfter, args.TryGetValue("source_sha256", out string hash) ? hash : null);
                return;
            }
            if ((args.TryGetValue("probe", out string vehicleProbe) && (vehicleProbe == "vehicle" || vehicleProbe == "vehicle-material" || vehicleProbe == "vehicle-isolation" || vehicleProbe == "vehicle-shader" || vehicleProbe == "vehicle-inspection" || vehicleProbe == "vehicle-shadows" || vehicleProbe == "vehicle-renderer" || vehicleProbe == "vehicle-surface" || vehicleProbe == "vehicle-context" || vehicleProbe == "vehicle-reflection" || vehicleProbe == "vehicle-pipeline" || vehicleProbe == "vehicle-transmission" || vehicleProbe == "vehicle-review-sample" || vehicleProbe == "vehicle-review-exterior" || vehicleProbe == "vehicle-review-mips" || vehicleProbe == "vehicle-mirrors")) ||
                (args.TryGetValue("mode", out string vehicleMode) && vehicleMode == "vehicle"))
            {
                // No model is assumed: the probe looks at the buses it is told to (a bus of one pack is not every bus's stand-in).
                if (!args.TryGetValue("names", out string vehicleNames) || string.IsNullOrEmpty(vehicleNames))
                {
                    Finish("failed the vehicle probe needs names=<model>[,<model>]; no model is assumed", quitAfter);
                    return;
                }
                ProbeVehicle(vehicleNames, quitAfter,
                    vehicleProbe == "vehicle-material" || vehicleProbe == "vehicle-isolation" || vehicleProbe == "vehicle-shader" || vehicleProbe == "vehicle-inspection" || vehicleProbe == "vehicle-shadows" || vehicleProbe == "vehicle-renderer" || vehicleProbe == "vehicle-surface" || vehicleProbe == "vehicle-context" || vehicleProbe == "vehicle-reflection" || vehicleProbe == "vehicle-pipeline" || vehicleProbe == "vehicle-transmission" || vehicleProbe == "vehicle-review-sample" || vehicleProbe == "vehicle-review-exterior" || vehicleProbe == "vehicle-review-mips" || vehicleProbe == "vehicle-mirrors", vehicleProbe == "vehicle-isolation", vehicleProbe == "vehicle-shader", vehicleProbe == "vehicle-inspection", vehicleProbe == "vehicle-shadows", vehicleProbe == "vehicle-renderer", vehicleProbe == "vehicle-surface", vehicleProbe == "vehicle-context", vehicleProbe == "vehicle-reflection", vehicleProbe == "vehicle-pipeline", vehicleProbe == "vehicle-transmission", vehicleProbe == "vehicle-review-sample" || vehicleProbe == "vehicle-review-exterior" || vehicleProbe == "vehicle-review-mips", vehicleProbe == "vehicle-review-exterior" || vehicleProbe == "vehicle-review-mips", vehicleProbe == "vehicle-review-mips", vehicleProbe == "vehicle-mirrors", args.TryGetValue("source_sha256", out string sourceHash) ? sourceHash : null);
                return;
            }
            if (args.TryGetValue("probe", out string probe) && (probe == "citizen" || probe == "perf"))
            {
                ProbeCitizen(probe == "perf", quitAfter);
                return;
            }
            // name=: which of our buses (default the 12 m one); verify=1 checks every one of ours the game loaded
            string modelName = args.TryGetValue("name", out string n) && !string.IsNullOrEmpty(n) ? n : kName;
            // profile=: the vehicle profile of an authored bus to pack (Tools/<model>/vehicle.json); a check finds a loaded
            // pack's own profile beside its package
            s_RequestProfiles.Clear();
            if (args.TryGetValue("profile", out string profilePath) && !string.IsNullOrEmpty(profilePath))
                s_RequestProfiles[modelName] = profilePath;
            if (args.TryGetValue("verify", out string verify) && verify == "1")
            {
                string names = args.TryGetValue("names", out string list) && !string.IsNullOrEmpty(list) ? list : modelName;
                var results = names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim() + ": " + Verify(ModelNamed(x.Trim()))).ToList();
                bool ok = results.All(r => r.Contains(": ok "));
                Finish((ok ? "ok " : "failed ") + string.Join(" || ", results), quitAfter);
                return;
            }

            // Imported objects and materials cannot become a proven cold-loaded package in this process.
            s_Review.MarkRequiresRestart();
            Pack(ModelNamed(modelName), args.TryGetValue("source", out string source) ? source : null,
                args.TryGetValue("output", out string output) ? output : null,
                args.TryGetValue("quit", out string quit) && quit == "1");
        }

        // Developer probes: the game's citizens drawn by the mod (BusCitizenProbe), or what the passengers cost (BusPerfProbe).
        private async void ProbeCitizen(bool perf, bool quit)
        {
            EntityQuery humans = GetEntityQuery(ComponentType.ReadOnly<HumanData>(), ComponentType.ReadOnly<PrefabData>());
            string result;
            try { result = perf ? await BusPerfProbe.Run(EntityManager, m_PrefabSystem, humans) : await BusCitizenProbe.Run(EntityManager, m_PrefabSystem, humans); }
            catch (Exception ex) { result = "failed: " + ex; }
            Finish(result, quit);
        }

        /// <summary>Every bus the game loaded from a vehicle pack (a profile beside its package), with that profile.</summary>
        private List<(PrefabBase prefab, VehicleProfile profile)> LoadedPacks()
        {
            var found = new List<(PrefabBase, VehicleProfile)>();
            EntityQuery query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<PublicTransportVehicleData>());
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
                foreach (Entity e in entities)
                    if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p is CarPrefab && VehiclePacks.ForPrefab(p) is VehicleProfile profile)
                        found.Add((p, profile));
            return found;
        }

        /// <summary>One of our buses as the game loaded it (by its name: an asset's prefab ID carries the asset's hash).</summary>
        private PrefabBase FindOurBus(string name)
        {
            EntityQuery query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<PublicTransportVehicleData>());
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
                foreach (Entity e in entities)
                    if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p.name == name && p is CarPrefab)
                        return p;
            return null;
        }

        private async void ProbeGlass(string names, bool quit)
        {
            string result;
            try { result = await BusGlassProbe.Run(FindOurBus, names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim())); }
            catch (Exception ex) { result = "failed: " + ex; }
            Finish(result, quit);
        }

        private PrefabBase FindAssemblySection(string name)
        {
            EntityQuery query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CarData>());
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in entities)
                    if (m_PrefabSystem.TryGetPrefab(entity, out PrefabBase prefab) && prefab.name == name && prefab is CarBasePrefab)
                        return prefab;
            return null;
        }

        private async void ProbeAssembly(string name, bool quit, string sourceHash)
        {
            string result;
            try { result = await BusVehicleAssemblyProbe.Run(FindAssemblySection, name, n => Verify(ModelNamed(n)), sourceHash: sourceHash); }
            catch (Exception error) { result = "failed assembly probe: " + error; }
            Finish(result, quit);
        }

        private async void ProbeRoad(Dictionary<string, string> args)
        {
            string result;
            try
            {
                s_RoadOwnsTransition = true;
                bool stationaryGlass = args.TryGetValue("mode", out string mode) && mode == "glass-road";
                int seconds = args.TryGetValue("seconds", out string duration) && int.TryParse(duration, out int parsed) ? parsed : 90;
                if (seconds < 30 || seconds > 300) throw new ArgumentException("road seconds must be 30..300");
                bool glassCycle = false;
                if (args.TryGetValue("glass-cycle", out string cycle) && !bool.TryParse(cycle, out glassCycle))
                    throw new ArgumentException("glass-cycle must be true or false");
                if (stationaryGlass) glassCycle = true;
                bool glassRoadLights = false;
                if (args.TryGetValue("glass-road-lights", out string roadLights) && !bool.TryParse(roadLights, out glassRoadLights))
                    throw new ArgumentException("glass-road-lights must be true or false");
                if (glassRoadLights && !glassCycle) throw new ArgumentException("glass-road-lights requires glass-cycle=true");
                bool glassOptics = false;
                if (args.TryGetValue("glass-optics", out string optics) && !bool.TryParse(optics, out glassOptics))
                    throw new ArgumentException("glass-optics must be true or false");
                if (glassOptics && !glassCycle) throw new ArgumentException("glass-optics requires glass-cycle=true");
                result = await BusVehicleRoadProbe.Run(this, m_PrefabSystem, args.TryGetValue("name", out string name) ? name : "",
                    FindAssemblySection, n => Verify(ModelNamed(n)), args.TryGetValue("map", out string map) ? map : "auto",
                    args.TryGetValue("output", out string output) ? output : null, seconds, glassCycle, glassRoadLights, glassOptics, stationaryGlass);
            }
            catch (Exception e) { result = "failed native road smoke: " + e; }
            finally { s_RoadOwnsTransition = false; }
            Finish(result, false);
        }

        private async void ProbeVehicle(string names, bool quit, bool materialDiagnostic = false, bool refractionDiagnostic = false, bool shaderDiagnostic = false, bool inspectionDiagnostic = false, bool shadowDiagnostic = false, bool rendererDiagnostic = false, bool surfaceDiagnostic = false, bool contextDiagnostic = false, bool reflectionDiagnostic = false, bool pipelineDiagnostic = false, bool transmissionDiagnostic = false, bool reviewSample = false, bool reviewExterior = false, bool reviewMips = false, bool mirrorProbe = false, string sourceSha256 = null)
        {
            string result;
            try { result = await BusVehicleProbe.Run(FindOurBus, names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), materialDiagnostic, refractionDiagnostic, shaderDiagnostic, inspectionDiagnostic, shadowDiagnostic, rendererDiagnostic, surfaceDiagnostic, contextDiagnostic, reflectionDiagnostic, pipelineDiagnostic, transmissionDiagnostic, reviewSample, reviewExterior, reviewMips, mirrorProbe, sourceSha256, n => Verify(ModelNamed(n))); }
            catch (Exception ex) { result = "failed: " + ex; }
            Finish(result, quit);
        }

        private static async void ProbeWrap(string wrap, bool quit, string model, bool variants)
        {
            string result;
            try { result = await BusWrapProbe.Run(wrap, model, variants); }
            catch (Exception ex) { result = "failed: " + ex; }
            Finish(result, quit);
        }

        private async void Pack(Model model, string source, string output, bool quit)
        {
            Model[] sectionModels = SectionsOf(model);
            model = sectionModels[0];
            string result = "failed packaging did not complete";
            ILocalAssetDatabase database = AssetImportPipeline.targetDatabase;
            string seed = AssetImportPipeline.importSeed;
            bool parallel = AssetImportPipeline.useParallelImport;
            int effort = TextureImporter.overrideCompressionEffort;
            bool checkedModel = model.Strict;
            string importSeed = checkedModel ? kSeed + "_" + Guid.NewGuid().ToString("N") : kSeed;
            string importRoot = Path.GetFullPath(Path.Combine(AssetDatabase.user.rootPath, "ImportedData", importSeed));
            var cleanupErrors = new List<string>();
            var paletteImports = new List<BusPaletteImport>();
            try
            {
                if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
                    throw new DirectoryNotFoundException($"The model files are not at '{source}'");
                if (string.IsNullOrEmpty(output))
                    throw new ArgumentException("The request names no output");
                if (checkedModel)
                {
                    output = Path.GetFullPath(output);
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                }
                foreach (Model section in sectionModels)
                    if (m_PrefabSystem.TryGetPrefab(new PrefabID(section.SectionProfile?.Spec.Section == BusSection.Rear ? nameof(CarTrailerPrefab) : nameof(CarPrefab), section.Name), out _))
                        throw new InvalidOperationException($"{section.Name} is loaded already (a package of it in the mods or the local assets): take it out first");
                if (!m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(CarPrefab), model.Template), out PrefabBase template))
                    throw new InvalidOperationException($"The template bus {model.Template} is not loaded");

                string sourceParent = Path.GetDirectoryName(Path.GetFullPath(source));
                var importSources = new List<string>();
                foreach (Model section in sectionModels)
                {
                    string sectionSource = sectionModels.Length == 1 ? source : Path.Combine(sourceParent, section.Name);
                    if (model.Assembly != null) VerifyNativeSourceNames(sectionSource, section.Name);
                    if (checkedModel)
                    {
                        BusPaletteImport palette = BusPaletteImport.Create(sectionSource, section.Name);
                        paletteImports.Add(palette);
                        sectionSource = palette.Source;
                    }
                    importSources.Add(sectionSource);
                }
                Mod.log.Info($"Packing the bus model {model.Name} (a copy of {model.Template}): importing {source}");
                AssetImportPipeline.targetDatabase = AssetDatabase.user;
                AssetImportPipeline.importSeed = importSeed;
                // Colossal.Json's first-use type cache is shared and is not safe to initialize from two groups at once.
                // Keep each section's import/settings isolated and awaited before the next, retaining the same factory.
                AssetImportPipeline.useParallelImport = false;
                TextureImporter.overrideCompressionEffort = -1;
                var factory = new AssetImportPanel.PrefabFactory();
                foreach (string importSource in importSources)
                {
                    string projectRoot = Path.GetDirectoryName(importSource);
                    if (!AssetImportPipeline.IsArtRootPath(projectRoot, new[] { importSource }, out string artRoot, out List<string> relative))
                        throw new InvalidOperationException($"The declared section source is not inside {projectRoot}");
                    await AssetImportPipeline.ImportPath(artRoot, relative, ImportMode.All, false, (title, info, progress) => false, factory);
                }

                // The body is the folder's main model, so the mesh prefab is named after it; else the import's only mesh.
                List<(PrefabBase prefab, string source)> roots = factory.rootPrefabs.Where(r => r.prefab is RenderPrefab).ToList();
                var imported = new List<(Model model, RenderPrefab mesh, string source)>();
                foreach (Model section in sectionModels)
                {
                    var root = roots.FirstOrDefault(r => r.prefab.name == section.Name + " Mesh");
                    if (root.prefab == null && sectionModels.Length == 1 && roots.Count == 1) root = roots[0];
                    if (!(root.prefab is RenderPrefab mesh) || mesh.geometryAsset == null)
                        throw new InvalidOperationException($"The import made no exact mesh for {section.Name} ({roots.Count} roots)");
                    SetUpMesh(mesh, section);
                    imported.Add((section, mesh, root.source));
                }
                if (checkedModel) BusPaletteImport.VerifyAssembly(imported.Select(s => (s.mesh, s.model.Name)));
                var first = imported[0];
                CarPrefab bus = VehiclePrefab((CarPrefab)template, first.source, first.model, importSeed);
                bus.m_Meshes = new[] { new ObjectMeshInfo { m_Mesh = first.mesh } };
                CarTrailerPrefab rear = null;
                if (model.Assembly != null)
                {
                    var last = imported[1];
                    rear = TrailerPrefab((CarPrefab)template, last.source, last.model, importSeed);
                    rear.m_Meshes = new[] { new ObjectMeshInfo { m_Mesh = last.mesh } };
                    bus.Remove<CarTractor>();
                    CarTractor tractor = bus.AddComponent<CarTractor>();
                    tractor.m_TrailerType = CarTrailerType.Towbar;
                    tractor.m_AttachOffset = PackSettings.TractorOffset(model.Assembly.FrontAnchor, first.mesh.bounds.min.z);
                    tractor.m_FixedTrailer = rear;
                    rear.m_TrailerType = CarTrailerType.Towbar;
                    rear.m_MovementType = TrailerMovementType.Free;
                    rear.m_AttachOffset = PackSettings.TrailerOffset(model.Assembly.RearAnchor, last.mesh.bounds.max.z);
                    // Native stock fixed pairs use one dependency direction. A reciprocal back-reference
                    // recurses in PrefabSystem.IsUnlockable before its cache is populated.
                    rear.m_FixedTractor = null;
                    VerifyNativeDependencyDirection(bus, rear);
                    rear.asset.Save(last.source, force: true);
                }
                // Front includes its fixed rear in the dependency tree; rear has no reverse edge.
                bus.asset.Save(first.source, force: true);

                var collector = new DependencyCollector();
                collector.AddAsset(bus.asset);
                if (rear != null) collector.AddAsset(rear.asset);
                AssetData[] assets = collector.CompileAllAssets();
                Mod.log.Info($"Packing {assets.Length} assets: {string.Join(", ", assets.Select(a => $"{a.name} ({a.GetType().Name})"))}");
                PackageAsset package = await PackageHelper.CreatePackage(collector);
                if (package == null)
                    throw new InvalidOperationException("The packaging failed (see Editor.log)");
                try { RetainNativeSourceTextures(package, assets); }
                catch (Exception retentionError)
                {
                    Mod.log.Error(retentionError, "Native source texture retention failed");
                    string rejectedWorkingPath = package.path;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        string rejected = output + ".texture-retention-rejected-" + Guid.NewGuid().ToString("N") + ".bin";
                        File.Copy(rejectedWorkingPath, rejected, overwrite: false);
                    }
                    finally
                    {
                        PackageHelper.DeletePackage(package);
                        if (File.Exists(rejectedWorkingPath))
                            throw new IOException("Texture retention failed; working package cleanup also failed: " + rejectedWorkingPath, retentionError);
                    }
                    throw;
                }
                string workingPackagePath = package.path;

                if (!checkedModel) Directory.CreateDirectory(Path.GetDirectoryName(output));
                bool workingPackageDeleted = false;
                if (checkedModel)
                {
                    try
                    {
                        string[] sectionNames = sectionModels.Select(s => s.Name).ToArray();
                        BusPackageExport.Result checkedExport = BusPackageExport.ExportChecked(workingPackagePath, output,
                            candidate => BusPackageGlass.Verify(candidate, sectionNames, model.GlassCoatMask), BusPackageExport.Fixes.All);
                        Mod.log.Info($"Checked package export: removed {checkedExport.RemovedEntries.Length} empty-cache entries; " +
                            $"{checkedExport.RemovedIdentifiers} prefab self-identities taken out of {checkedExport.RewrittenEntries.Length} prefab(s); retained {checkedExport.RetainedEntries}; SHA256 {checkedExport.OutputSha256}");
                    }
                    catch
                    {
                        // Preserve evidence outside an auto-loadable .cok/.zip,
                        // then remove only the working package created above.
                        try
                        {
                            string diagnostic = Path.Combine(Path.GetDirectoryName(output),
                                Path.GetFileNameWithoutExtension(output) + ".rejected-" + Guid.NewGuid().ToString("N") + ".bin");
                            File.Copy(workingPackagePath, diagnostic, overwrite: false);
                            Mod.log.Warn("Rejected package retained for diagnosis: " + diagnostic);
                        }
                        catch (Exception snapshotError) { Mod.log.Warn("Could not retain rejected package: " + snapshotError.Message); }
                        throw;
                    }
                    finally
                    {
                        try
                        {
                            PackageHelper.DeletePackage(package);
                            workingPackageDeleted = !File.Exists(workingPackagePath);
                            if (!workingPackageDeleted) cleanupErrors.Add("Working package still exists: " + workingPackagePath);
                        }
                        catch (Exception cleanupError) { cleanupErrors.Add("Working package " + workingPackagePath + ": " + cleanupError.Message); }
                    }
                }
                else
                {
                    // every model: the prefabs' own identities out of their obsolete ones (the game's packer puts them there and
                    // warns "Duplicate prefab ID" at every load); the caches of a model not strictly checked stay as the game wrote them
                    Action<string> glass = null;
                    if (model.SharedGlass)
                    {
                        string[] sectionNames = sectionModels.Select(s => s.Name).ToArray();
                        glass = candidate => BusPackageGlass.Verify(candidate, sectionNames, model.GlassCoatMask);
                    }
                    BusPackageExport.Result export = BusPackageExport.ExportChecked(workingPackagePath, output, glass, BusPackageExport.Fixes.SelfIdentifiers);
                    Mod.log.Info($"Package export: {export.RemovedIdentifiers} prefab self-identities taken out of {export.RewrittenEntries.Length} prefab(s); " +
                        $"{export.RetainedEntries} entries; SHA256 {export.OutputSha256}");
                }
                long size = new FileInfo(output).Length;
                Mod.log.Info($"The bus model is packed: {output} ({size} bytes, from {workingPackagePath})");

                // The working files go: only the mod's copy of the package may provide the prefab, or it would be there twice.
                if (!checkedModel) PackageHelper.DeletePackage(package);
                if (!checkedModel)
                {
                    foreach (AssetData asset in assets.Where(a => a.database == AssetDatabase.user)) asset.Delete();
                    if (Directory.Exists(importRoot)) Directory.Delete(importRoot, true);
                }

                result = $"ok {size} {output}";
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Packing the bus model failed");
                result = "failed " + ex.Message.Replace('\n', ' ');
            }
            finally
            {
                foreach (BusPaletteImport paletteImport in paletteImports)
                {
                    try { paletteImport.Dispose(); }
                    catch (Exception cleanupError) { cleanupErrors.Add("Palette staging: " + cleanupError.Message); }
                }
                if (checkedModel)
                {
                    try { CleanupOwnImport(importRoot, importSeed, cleanupErrors); }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError.Message); }
                    if (cleanupErrors.Count != 0)
                    {
                        string detail = "cleanup failed: " + string.Join("; ", cleanupErrors);
                        Mod.log.Warn(detail);
                        result = result.StartsWith("failed", StringComparison.Ordinal) ? result + "; " + detail : "failed " + detail;
                    }
                }
                AssetImportPipeline.targetDatabase = database;
                AssetImportPipeline.importSeed = seed;
                AssetImportPipeline.useParallelImport = parallel;
                TextureImporter.overrideCompressionEffort = effort;
            }

            Finish(result, quit);
        }

        private static void CleanupOwnImport(string importRoot, string importSeed, List<string> errors)
        {
            string parent = Path.GetFullPath(Path.Combine(AssetDatabase.user.rootPath, "ImportedData")) + Path.DirectorySeparatorChar;
            string root = Path.GetFullPath(importRoot);
            if (!importSeed.StartsWith(kSeed + "_", StringComparison.Ordinal) ||
                !root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(root) != importSeed)
                throw new InvalidOperationException("Refusing cleanup outside this request's unique import directory: " + root);
            string prefix = root + Path.DirectorySeparatorChar;
            int before = errors.Count;
            foreach (AssetData asset in AssetDatabase.user.GetAssets<AssetData>().ToArray())
            {
                if (asset.database != AssetDatabase.user || string.IsNullOrEmpty(asset.path)) continue;
                string path = Path.GetFullPath(asset.path);
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                try { asset.Delete(); }
                catch (Exception error) { errors.Add("Import asset " + path + ": " + error.Message); }
            }
            if (errors.Count == before && Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) { errors.Add("Import directory " + root + ": " + error.Message); }
            }
        }

        private static void VerifyNativeDependencyDirection(CarPrefab front, CarTrailerPrefab rear)
        {
            if (!front.TryGet(out CarTractor tractor) || tractor.m_FixedTrailer != rear || rear.m_FixedTractor != null)
                throw new InvalidDataException("Native assembly must declare only front.FixedTrailer -> rear; reverse fixed dependencies can recurse during native unlock registration");
        }

        private static void VerifyNativeSourceNames(string directory, string mainName)
        {
            if (!PackSettings.NativeMainName(mainName))
                throw new InvalidDataException("Native prefab main name must be ASCII alphanumeric; underscores are importer suffixes: " + mainName);
            // Call the installed parser as well as the portable profile check. A clear filename error must precede ImportPath,
            // which otherwise logs/skips bad groups and only leaves the misleading '0 roots' symptom.
            foreach (string stem in new[] { mainName }.Concat(Directory.GetFiles(directory)
                .Where(f => Path.GetExtension(f).Equals(".fbx", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileNameWithoutExtension)))
            {
                try { AssetUtils.ParseName(stem, out _, out _, out _, out _, out _, out _, out _, out _); }
                catch (Exception error) { throw new InvalidDataException("Native importer rejects declared asset name '" + stem + "': " + error.Message, error); }
            }
        }

        private static bool TestMenuReady(GameManager game) => game != null &&
            game.gameMode == GameMode.MainMenu && game.state == GameManager.State.WorldReady && !game.isGameLoading;

        private static void ObserveTestQuitOwnership(GameManager game)
        {
            if (s_TestAutoQuitPending && !TestMenuReady(game)) s_TestAutoQuitCancelled = true;
        }

        private static async void Finish(string result, bool quit)
        {
            Mod.log.Info("Bus model: " + result);
            File.WriteAllText(Path.Combine(Mod.ModDirectory, kResultFile), result);
            if (quit)
            {
                try
                {
                    ObserveTestQuitOwnership(GameManager.instance);
                    if (s_TestQuitDelaySeconds > 0 && !s_TestAutoQuitCancelled)
                    {
                        Mod.log.Info("Bus model: explicit test exit settling delay " + s_TestQuitDelaySeconds + " seconds; background import completion is not guaranteed");
                        int remaining = s_TestQuitDelaySeconds * 1000;
                        while (remaining > 0 && !s_TestAutoQuitCancelled)
                        {
                            int interval = Math.Min(250, remaining);
                            await System.Threading.Tasks.Task.Delay(interval);
                            remaining -= interval;
                            ObserveTestQuitOwnership(GameManager.instance);
                        }
                    }
                    ObserveTestQuitOwnership(GameManager.instance);
                    if (s_TestAutoQuitCancelled)
                    {
                        Mod.log.Info("Bus model: automatic test exit cancelled because the game left the ready main menu; user session retained");
                        return;
                    }
                    GameManager.QuitGame();
                }
                finally { s_TestAutoQuitPending = false; s_LegacyBusy = false; }
            }
            else s_LegacyBusy = false;
        }

        /// <summary>The bus as the game loaded it: "ok" and what it found, or "failed" and the first thing that is not right.</summary>
        private string Verify(Model model)
        {
            if (model.Assembly != null) return VerifyAssembly(model);
            try
            {
                // As a line's vehicle list finds it: the vehicle prefabs with public transport data (an asset's prefab ID carries
                // the asset's hash, so it is found by its name, not by an ID).
                var buses = new List<(Entity entity, CarPrefab prefab)>();
                EntityQuery query = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<CarData>(), ComponentType.ReadOnly<PublicTransportVehicleData>());
                using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity e in entities)
                    {
                        if (m_PrefabSystem.TryGetPrefab(e, out PrefabBase p) && p.name == model.Name && p is CarPrefab car)
                            buses.Add((e, car));
                    }
                }

                if (buses.Count != 1)
                    return $"failed {buses.Count} bus prefabs named {model.Name} (one expected); {Sources(model)}";

                (Entity entity, CarPrefab bus) = buses[0];
                var found = new List<string> { $"from {bus.asset?.database?.name}:{bus.asset?.path}" };
                if (model.Name != kName)
                {
                    // a vehicle pack's bus: Bus Driver finds its profile beside the package, and its runtime mesh loads
                    VehicleProfile profile = VehiclePacks.ForPrefab(bus);
                    if (profile == null)
                        return $"failed no vehicle pack profile beside {bus.asset?.path} (see the log for why)";
                    int parts;
                    try { parts = profile.Mesh.PartCount; }
                    catch (Exception e) { return "failed the pack's runtime mesh: " + e.Message; }
                    found.Add($"vehicle pack {VehiclePacks.FolderOf(profile)}: profile {profile.Name}, mesh {profile.MeshSha256?.Substring(0, 12)} with {parts} parts");
                    if (profile.NativeDoors != null)
                    {
                        VerifyNativeBoarding(bus, entity, profile);
                        found.Add("declared native door thresholds and passenger activities verified");
                    }
                }
                PublicTransportVehicleData transport = EntityManager.GetComponentData<PublicTransportVehicleData>(entity);
                if (transport.m_TransportType != TransportType.Bus || (transport.m_PurposeMask & PublicTransportPurpose.TransportLine) == 0)
                    return $"failed not a line bus ({transport.m_TransportType}, {transport.m_PurposeMask})";
                if (model.Capacity > 0 && transport.m_PassengerCapacity != model.Capacity)
                    return $"failed passenger capacity {transport.m_PassengerCapacity}, expected {model.Capacity}";
                if (EntityManager.HasComponent<Locked>(entity) && EntityManager.IsComponentEnabled<Locked>(entity))
                    return "failed the bus is locked (it would not be in a line's vehicle list)";
                found.Add($"a line bus for {transport.m_PassengerCapacity} passengers, unlocked");

                if (!(bus.m_Meshes?.FirstOrDefault()?.m_Mesh is RenderPrefab mesh))
                    return "failed no mesh";
                int lods = mesh.TryGet(out LodProperties lod) && lod.m_LodMeshes != null ? lod.m_LodMeshes.Length : 0;
                found.Add($"mesh {mesh.name}, {mesh.meshCount} sub-meshes, {mesh.indexCount / 3} triangles, {lods} lower LODs");

                if (!mesh.TryGet(out ProceduralAnimationProperties animation) || animation.m_Bones == null)
                    return "failed no bones";
                foreach ((string bone, BoneType type) in BonesOf(model))
                {
                    if (animation.m_Bones.FirstOrDefault(b => b.name == bone)?.m_Type != type)
                        return $"failed bone {bone} is not {type}";
                }
                found.Add("bones " + string.Join(", ", animation.m_Bones.Select(b => $"{b.name} {b.m_Type}")));

                if (model.GlassNormalStrength.HasValue)
                {
                    foreach (var item in new[] { mesh }.Concat(lod?.m_LodMeshes ?? Array.Empty<RenderPrefab>()))
                        RequireGlassNormalStrength(SingleGlassSurface(item), model.GlassNormalStrength.Value);
                    found.Add("all LOD glass normal strengths match saved and loaded materials");
                }

                if (model.SharedGlass)
                {
                    if (lods != 2) return $"failed {model.Name} requires two lower LODs";
                    if (model.Strict)
                    {
                        BusPaletteImport.Verify(mesh, model.Name);
                        found.Add("eighteen persisted 64x64 single-mip palette textures and original sampling headers verified");
                    }
                    SurfaceAsset sharedGlass = SingleGlassSurface(mesh);
                    if (!HdrpActive(out string pipeline))
                    {
                        Mod.log.Warn(model.Name + " glass material check not run: HDRP does not exist yet (" + pipeline + ")");
                        return "failed precondition: the glass material can only be checked once HDRP exists (" + pipeline + ")";
                    }
                    LogStrictGlassEvidence(model.Name, "before-properties-load", sharedGlass);
                    sharedGlass.LoadProperties(false);
                    try
                    {
                        LogStrictGlassEvidence(model.Name, "after-properties-load", sharedGlass);
                        if (!sharedGlass.TryGetFloatProperty("_CoatMask", out float coat) || !Mathf.Approximately(coat, model.GlassCoatMask) ||
                            !sharedGlass.TryGetFloatProperty("_ReceivesSSRTransparent", out float ssr) || ssr != 0f ||
                            !sharedGlass.TryGetFloatProperty("_TransparentDepthPrepassEnable", out float prepass) || prepass != 0f)
                            return $"failed {model.Name} saved glass coat/depth compatibility properties";
                        // A cached object's Load adds only an object reference;
                        // an uncached Load acquires its own data reference too.
                        if (sharedGlass.isObjectLoaded) sharedGlass.LoadProperties(false);
                        LogStrictGlassEvidence(model.Name, "before-material-load", sharedGlass);
                        Material loadedGlass = sharedGlass.Load(loadTextures: false, useVT: false);
                        try
                        {
                            LogStrictGlassEvidence(model.Name, "after-material-load", sharedGlass, loadedGlass);
                            RequireStrictGlassMaterial(model.Name, model.GlassCoatMask, loadedGlass);
                        }
                        finally
                        {
                            sharedGlass.Unload();
                            LogStrictGlassEvidence(model.Name, "after-material-unload", sharedGlass);
                        }
                    }
                    finally
                    {
                        sharedGlass.UnloadProperties();
                        LogStrictGlassEvidence(model.Name, "after-properties-unload", sharedGlass);
                    }
                    foreach (RenderPrefab lower in lod.m_LodMeshes)
                    {
                        if (lower == null || !lower.TryGet(out ProceduralAnimationProperties lowerAnimation) ||
                            lowerAnimation.m_Bones == null || lowerAnimation.m_Bones.Length != 5 ||
                            s_Bones.Any(expected => lowerAnimation.m_Bones.FirstOrDefault(b => b.name == expected.bone)?.m_Type != expected.type))
                            return $"failed {model.Name} lower LOD wheel bones";
                        if (SingleGlassSurface(lower).id != sharedGlass.id)
                            return $"failed {model.Name} LODs do not share the glass surface/texture set";
                    }
                    found.Add("all three LODs share one glass surface/texture set");
                    found.Add($"glass coat {model.GlassCoatMask.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}; transparent SSR/prepass disabled on this vehicle's surface; loaded material verified");
                }

                int[] expectedLights = model.LightLayers ?? Enumerable.Range(1, s_Lights.Length - 1).ToArray();
                if (!mesh.TryGet(out EmissiveProperties emissive) || emissive.m_MultiLights == null ||
                    !emissive.m_MultiLights.Select(l => l.layerId).OrderBy(id => id).SequenceEqual(expectedLights) ||
                    emissive.m_MultiLights.Any(l => l.layerId <= 0 || l.layerId >= s_Lights.Length || l.purpose != s_Lights[l.layerId]))
                    return "failed lamp layers";
                found.Add($"{emissive.m_MultiLights.Count} lamp layers");

                if (!mesh.TryGet(out ColorProperties colors) || !colors.CanBeModifiedByExternal(0) || colors.m_ExternalColorSource != ColorSourceType.Brand)
                    return "failed the livery takes no line colour";
                found.Add("line colour on channel 0");

                found.Add($"{(bus.TryGet(out EffectSource effects) && effects.m_Effects != null ? effects.m_Effects.Count : 0)} effects (sounds, lights)");
                found.Add(bus.TryGet(out UIObject ui) ? $"icon {(string.IsNullOrEmpty(ui.m_Icon) ? "none (rendered thumbnail)" : ui.m_Icon)}" : "no UI object");
                if (model.Icon != null)
                {
                    // the pack's own icon (pack.icon) must have come with the package and resolve as the editor's icon field does
                    if (ui == null || string.IsNullOrEmpty(ui.m_Icon))
                        return "failed the profile declares pack.icon but the loaded bus has no icon";
                    if (!AssetDatabase.global.TryGetAsset(ui.m_Icon, out FileAsset icon) || !(icon is ImageAsset))
                        return "failed the bus's icon " + ui.m_Icon + " does not resolve to an image asset";
                    // a package in the local mods belongs to the user database too: what tells it from a loose working file is the
                    // entry's package, which must be the bus's own
                    SourceMeta iconMeta = icon.GetMeta();
                    if (!iconMeta.packaged)
                        return "failed the bus's icon " + ui.m_Icon + " is a loose file (a working file or a copy), not part of a package";
                    if (bus.asset != null && iconMeta.package != bus.asset.GetMeta().package)
                        return "failed the bus's icon " + ui.m_Icon + " comes from the package " + iconMeta.packageName + ", not the bus's own";
                    found.Add($"icon {icon.name} from the bus's own package {iconMeta.packageName}");
                }
                found.Add(Sources(model));
                return "ok " + string.Join("; ", found);
            }
            catch (Exception ex)
            {
                Mod.log.Error(ex, "Checking the bus model failed");
                return "failed " + ex.Message.Replace('\n', ' ');
            }
        }

        private string VerifyAssembly(Model model)
        {
            try
            {
                Model[] expected = SectionsOf(model);
                var prefabs = new CarBasePrefab[2];
                var entities = new Entity[2];
                var meshes = new RenderPrefab[2];
                string runtimeIdentity = null;
                var assemblyLightLayers = new HashSet<int>();
                using (NativeArray<Entity> all = GetEntityQuery(ComponentType.ReadOnly<PrefabData>()).ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var matches = new List<(Entity entity, CarBasePrefab prefab)>();
                        foreach (Entity entity in all)
                            if (m_PrefabSystem.TryGetPrefab(entity, out PrefabBase p) && p.name == expected[i].Name && p is CarBasePrefab car)
                                matches.Add((entity, car));
                        if (matches.Count != 1) throw new InvalidOperationException(expected[i].Name + " requires exactly one native prefab");
                        (entities[i], prefabs[i]) = matches[0];
                    }
                }
                if (!(prefabs[0] is CarPrefab) || !(prefabs[1] is CarTrailerPrefab rear))
                    throw new InvalidOperationException("Assembly native types must be CarPrefab then CarTrailerPrefab");
                if (!string.Equals(prefabs[0].asset?.path, prefabs[1].asset?.path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Assembly sections are not loaded from the same native package");
                if (!prefabs[0].TryGet(out CarTractor tractor) || tractor.m_FixedTrailer != rear || rear.m_FixedTractor != null ||
                    rear.m_MovementType != TrailerMovementType.Free || tractor.m_TrailerType != rear.m_TrailerType)
                    throw new InvalidOperationException("Assembly fixed tractor/trailer references or Free movement mismatch");
                if (!EntityManager.HasComponent<CarTractorData>(entities[0]) || !EntityManager.HasComponent<CarTrailerData>(entities[1]))
                    throw new InvalidOperationException("Assembly lacks initialized native attachment data");
                CarTractorData td = EntityManager.GetComponentData<CarTractorData>(entities[0]);
                CarTrailerData rd = EntityManager.GetComponentData<CarTrailerData>(entities[1]);
                if (td.m_FixedTrailer != entities[1] || rd.m_FixedTractor != Entity.Null || rd.m_MovementType != TrailerMovementType.Free ||
                    Unity.Mathematics.math.distance(td.m_AttachPosition, model.Assembly.FrontAnchor) > .001f ||
                    Unity.Mathematics.math.distance(rd.m_AttachPosition, model.Assembly.RearAnchor) > .001f)
                    throw new InvalidOperationException("Initialized native attachment points/references differ from the assembly contract");
                for (int i = 0; i < 2; i++)
                {
                    Model section = expected[i];
                    VehicleProfile profile = VehiclePacks.ForPrefab(prefabs[i]);
                    if (profile?.Assembly?.Model != model.Assembly.Model || profile.Name != section.Name ||
                        profile.Assembly.Front.Prefab != model.Assembly.Front.Prefab || profile.Assembly.Rear.Prefab != model.Assembly.Rear.Prefab ||
                        Unity.Mathematics.math.distance(profile.Assembly.FrontAnchor, model.Assembly.FrontAnchor) > .0001f ||
                        Unity.Mathematics.math.distance(profile.Assembly.RearAnchor, model.Assembly.RearAnchor) > .0001f ||
                        (runtimeIdentity != null && profile.Assembly.Identity != runtimeIdentity))
                        throw new InvalidOperationException(section.Name + " runtime manifest differs from the native assembly");
                    runtimeIdentity = profile.Assembly.Identity;
                    _ = profile.Mesh;
                    if (!EntityManager.HasComponent<PublicTransportVehicleData>(entities[i]) ||
                        EntityManager.GetComponentData<PublicTransportVehicleData>(entities[i]).m_PassengerCapacity != section.Capacity)
                        throw new InvalidOperationException(section.Name + " native capacity allocation differs");
                    PublicTransportVehicleData transport = EntityManager.GetComponentData<PublicTransportVehicleData>(entities[i]);
                    if (transport.m_TransportType != TransportType.Bus || (i == 0
                        ? (transport.m_PurposeMask & PublicTransportPurpose.TransportLine) == 0
                        : transport.m_PurposeMask != 0))
                        throw new InvalidOperationException(section.Name + " native route-selection purpose differs");
                    VerifyNativeBoarding(prefabs[i], entities[i], section.SectionProfile);
                    if (!(prefabs[i].m_Meshes?.SingleOrDefault()?.m_Mesh is RenderPrefab mesh))
                        throw new InvalidOperationException(section.Name + " must have exactly one native root mesh");
                    meshes[i] = mesh;
                    if (!mesh.TryGet(out LodProperties lods) || lods.m_LodMeshes?.Length != 2)
                        throw new InvalidOperationException(section.Name + " requires all three native LODs");
                    var bones = BonesOf(section);
                    foreach (RenderPrefab lod in new[] { mesh }.Concat(lods.m_LodMeshes))
                        if (lod == null || !lod.TryGet(out ProceduralAnimationProperties animation) ||
                            animation.m_Bones?.Length != bones.Length + 1 || animation.m_Bones.Count(b => b.name == "Root") != 1 ||
                            bones.Any(b => animation.m_Bones.FirstOrDefault(a => a.name == b.bone)?.m_Type != b.type))
                            throw new InvalidOperationException(section.Name + " has missing or undeclared native axle bones");
                    if (!mesh.TryGet(out EmissiveProperties lights) || lights.m_MultiLights == null ||
                        lights.m_MultiLights.Count == 0 ||
                        lights.m_MultiLights.Select(l => l.layerId).Distinct().Count() != lights.m_MultiLights.Count ||
                        lights.m_MultiLights.Any(l => l.layerId <= 0 || l.layerId >= s_Lights.Length || l.purpose != s_Lights[l.layerId]))
                        throw new InvalidOperationException(section.Name + " lamp layer mapping differs");
                    foreach (var light in lights.m_MultiLights) assemblyLightLayers.Add(light.layerId);
                    if (!mesh.TryGet(out ColorProperties color) || !color.CanBeModifiedByExternal(0) || color.m_ExternalColorSource != ColorSourceType.Brand)
                        throw new InvalidOperationException(section.Name + " does not retain line-colour routing");
                    SurfaceAsset glass = SingleGlassSurface(mesh);
                    foreach (RenderPrefab lod in lods.m_LodMeshes)
                        if (SingleGlassSurface(lod).id != glass.id)
                            throw new InvalidOperationException(section.Name + " LODs do not share their section glass");
                }
                if (Enumerable.Range(1, s_Lights.Length - 1).Any(id => !assemblyLightLayers.Contains(id)))
                    throw new InvalidOperationException("Assembly is missing a required exterior/interior lamp channel");
                if (model.Strict) BusPaletteImport.VerifyAssembly(meshes.Select((mesh, i) => (mesh, expected[i].Name)));
                return "ok native two-section assembly; explicit fixed trailer, Free motion, initialized anchors within 1 mm; " +
                    string.Join("; ", expected.Select(s => s.Name + " capacity " + s.Capacity + " axles " + s.SectionProfile.Axles.Length)) +
                    "; native passenger thresholds and Enter/Exit ECS masks matched; separate glass/palette and all LOD bones; live driving/AI joint validation remains a separate test";
            }
            catch (Exception error) { return "failed " + error.Message; }
        }

        /// <summary>Every copy of the bus's vehicle prefab the game's databases hold, and whether it is registered.</summary>
        private string Sources(Model model)
        {
            var copies = new List<string>();
            foreach (PrefabAsset asset in AssetDatabase.global.GetAssets(SearchFilter<PrefabAsset>.ByCondition(a => a.name != null && a.name.StartsWith(model.Name + "_"))))
            {
                bool registered = asset.GetInstance<PrefabBase>() is PrefabBase instance && m_PrefabSystem.TryGetEntity(instance, out _);
                copies.Add($"{asset.database?.name}:{asset.path} ({(registered ? "registered" : "not registered")})");
            }

            return $"{copies.Count} copies: " + string.Join(" | ", copies);
        }

        /// <summary>The wheel bone types (every LOD), the lamp purposes by layer, the line colour on the livery channel.</summary>
        private static void SetUpMesh(RenderPrefab mesh, Model model)
        {
            var meshes = new List<RenderPrefab> { mesh };
            if (mesh.TryGet(out LodProperties lods) && lods.m_LodMeshes != null)
                meshes.AddRange(lods.m_LodMeshes.Where(m => m != null));

            foreach (RenderPrefab m in meshes)
            {
                if (!m.TryGet(out ProceduralAnimationProperties animation) || animation.m_Bones == null)
                    throw new InvalidOperationException($"{m.name} has no bones");

                var expectedBones = BonesOf(model);
                if (model.Assembly != null && (animation.m_Bones.Length != expectedBones.Length + 1 ||
                    animation.m_Bones.Count(b => b.name == "Root") != 1))
                    throw new InvalidOperationException($"{m.name} does not have precisely Root and its declared axle bones");
                foreach ((string bone, BoneType type) in expectedBones)
                {
                    ProceduralAnimationProperties.BoneInfo info = animation.m_Bones.FirstOrDefault(b => b.name == bone)
                        ?? throw new InvalidOperationException($"{m.name} has no bone {bone}");
                    info.m_Type = type;
                }
            }

            if (model.SharedGlass)
            {
                if (meshes.Count != 3) throw new InvalidOperationException(model.Name + " requires three native LODs");
                // The importer matches PNGs by LOD, so retain each LOD's source
                // files. All use the same palette UV grid and identical maps.
                // Share the actual packaged SurfaceAsset, not duplicate textures.
                SurfaceAsset sharedGlass = SingleGlassSurface(mesh);
                sharedGlass.LoadProperties(false);
                try
                {
                    Material glassTemplate = sharedGlass.GetTemplateMaterial();
                    if (glassTemplate == null || glassTemplate.shader == null || glassTemplate.shader.name != "BH/GlsShader" ||
                        !glassTemplate.HasProperty("_CoatMask") || !glassTemplate.HasProperty("_ReceivesSSRTransparent") ||
                        !glassTemplate.HasProperty("_TransparentDepthPrepassEnable"))
                        throw new InvalidOperationException(model.Name + " requires the audited BH/GlsShader coat/depth compatibility properties");
                    // Gls MaskMap G is AO. Save the separate coat on this
                    // surface before PackageHelper copies its disk data.
                    // UpdateFloats alone changes only the loaded dictionary.
                    var glassFloats = sharedGlass.floats.ToDictionary(pair => pair.Key, pair => pair.Value);
                    glassFloats["_CoatMask"] = model.GlassCoatMask;
                    // This installed Gls forward variant writes offset depth;
                    // its transparent prepass self-conflicts (verified in two
                    // independent in-game controls). SSR forces that prepass
                    // back on during material validation, so persist both flags.
                    // Only this vehicle's surface changes; sky/probe reflections
                    // and refraction retain their original settings.
                    glassFloats["_ReceivesSSRTransparent"] = 0f;
                    glassFloats["_TransparentDepthPrepassEnable"] = 0f;
                    sharedGlass.UpdateFloats(glassFloats);
                    if (sharedGlass.isObjectLoaded)
                    {
                        // An existing object's Load adds no data reference,
                        // whereas Unload releases both; balance them explicitly.
                        sharedGlass.LoadProperties(false);
                        Material loadedGlass = sharedGlass.Load(loadTextures: false, useVT: false);
                        try
                        {
                            loadedGlass.SetFloat("_CoatMask", model.GlassCoatMask);
                            loadedGlass.SetFloat("_ReceivesSSRTransparent", 0f);
                            loadedGlass.SetFloat("_TransparentDepthPrepassEnable", 0f);
                            if (!HDMaterial.ValidateMaterial(loadedGlass))
                                throw new InvalidOperationException(model.Name + " glass material validator is unsupported");
                            RequireStrictGlassMaterial(model.Name, model.GlassCoatMask, loadedGlass);
                        }
                        finally { sharedGlass.Unload(); }
                    }
                    if (!sharedGlass.Save(mipBias: 0, force: true, saveTextures: false, vt: false))
                        throw new InvalidOperationException(model.Name + " glass surface could not be saved");

                    // This unregistered reader has independent reference counts
                    // and reads the saved file, not the importer's cached data.
                    // Never force-unload a surface owned by another importer.
                    var saved = new SurfaceAsset { database = sharedGlass.database, id = sharedGlass.id };
                    Material savedMaterial = saved.Load(loadTextures: false, useVT: false);
                    try
                    {
                        if (!saved.TryGetFloatProperty("_CoatMask", out float coat) || !Mathf.Approximately(coat, model.GlassCoatMask) ||
                            !saved.TryGetFloatProperty("_ReceivesSSRTransparent", out float ssr) || ssr != 0f ||
                            !saved.TryGetFloatProperty("_TransparentDepthPrepassEnable", out float prepass) || prepass != 0f)
                            throw new InvalidOperationException(model.Name + " saved glass coat/depth compatibility failed disk readback");
                        RequireStrictGlassMaterial(model.Name, model.GlassCoatMask, savedMaterial);
                        if (saved.textures.Count != sharedGlass.textures.Count || sharedGlass.textures.Any(pair =>
                            !saved.textures.TryGetValue(pair.Key, out var texture) || texture.id != pair.Value.id))
                            throw new InvalidOperationException(model.Name + " saved glass texture bindings changed");
                        Mod.log.Info($"Saved native glass surface: _CoatMask={coat}, _ReceivesSSRTransparent={ssr}, _TransparentDepthPrepassEnable={prepass}; validated prepass disabled; disk/material readback passed; textures unchanged");
                    }
                    finally { saved.Unload(); }
                }
                finally { sharedGlass.UnloadProperties(); }
                foreach (RenderPrefab lower in meshes.Skip(1))
                {
                    SurfaceAsset ownGlass = SingleGlassSurface(lower);
                    for (int i = 0; i < lower.materialCount; i++)
                        if (lower.GetSurfaceAsset(i).id == ownGlass.id)
                            lower.SetSurfaceAsset(i, sharedGlass);
                }
            }

            if (model.GlassNormalStrength.HasValue)
                foreach (var surface in meshes.Select(SingleGlassSurface).Distinct())
                    SaveGlassNormalStrength(surface, model.GlassNormalStrength.Value);

            if (!mesh.TryGet(out EmissiveProperties emissive) || emissive.m_MultiLights == null)
                throw new InvalidOperationException($"{mesh.name} has no lamp layers");

            foreach (EmissiveProperties.MultiLightMapping light in emissive.m_MultiLights)
            {
                if (light.layerId > 0 && light.layerId < s_Lights.Length)
                    light.purpose = s_Lights[light.layerId];
            }

            if (model.Assembly == null && !emissive.m_MultiLights.Select(l => l.layerId).OrderBy(id => id)
                .SequenceEqual(model.LightLayers ?? Enumerable.Range(1, s_Lights.Length - 1).ToArray()))
                throw new InvalidOperationException($"{mesh.name}: native lamp layers do not match the authored pack.lightLayers contract");

            // The ControlMask's red channel is colour channel 0: the line's colour (Brand: a company's colours, else the route's).
            ColorProperties colors = mesh.TryGet(out ColorProperties existing) ? existing : mesh.AddComponent<ColorProperties>();
            colors.m_ColorVariations = new List<ColorProperties.VariationSet>
            {
                new ColorProperties.VariationSet { m_Colors = new[] { model.Livery, Color.white, Color.white } },
            };
            colors.m_ChannelsBinding = new List<ColorProperties.ColorChannelBinding>
            {
                new ColorProperties.ColorChannelBinding { m_ChannelId = 0, m_CanBeModifiedByExternal = true },
                new ColorProperties.ColorChannelBinding { m_ChannelId = 1 },
                new ColorProperties.ColorChannelBinding { m_ChannelId = 2 },
            };
            colors.m_VariationGroups = null;
            colors.m_VariationRanges = default;      // the line's exact colour
            colors.m_ExternalColorSource = ColorSourceType.Brand;
        }

        private static void SaveGlassNormalStrength(SurfaceAsset surface, float strength)
        {
            surface.LoadProperties(false);
            try
            {
                var template = surface.GetTemplateMaterial();
                if (template?.shader?.name != "BH/GlsShader" || !template.HasProperty("_NormalStrength"))
                    throw new InvalidOperationException("Glass shader does not expose normal strength");
                var values = surface.floats.ToDictionary(pair => pair.Key, pair => pair.Value);
                values["_NormalStrength"] = strength;
                surface.UpdateFloats(values);
                if (surface.isObjectLoaded)
                {
                    surface.LoadProperties(false);
                    var material = surface.Load(loadTextures: false, useVT: false);
                    try { material.SetFloat("_NormalStrength", strength); }
                    finally { surface.Unload(); }
                }
                if (!surface.Save(mipBias: 0, force: true, saveTextures: false, vt: false))
                    throw new InvalidOperationException("Could not persist glass normal strength");
                // Independent disk reader prevents accepting the importer's modified cache as persistence proof.
                var disk = new SurfaceAsset { database = surface.database, id = surface.id };
                RequireGlassNormalStrength(disk, strength);
            }
            finally { surface.UnloadProperties(); }
        }

        private static void RequireGlassNormalStrength(SurfaceAsset surface, float strength)
        {
            surface.LoadProperties(false);
            try
            {
                if (!surface.TryGetFloatProperty("_NormalStrength", out float saved) || saved != strength)
                    throw new InvalidOperationException("Saved glass normal strength differs from the pack declaration");
                if (surface.isObjectLoaded) surface.LoadProperties(false);
                var material = surface.Load(loadTextures: false, useVT: false);
                try
                {
                    if (!material.HasProperty("_NormalStrength") || material.GetFloat("_NormalStrength") != strength)
                        throw new InvalidOperationException("Loaded glass normal strength differs from the saved surface");
                }
                finally { surface.Unload(); }
            }
            finally { surface.UnloadProperties(); }
        }

        private static SurfaceAsset SingleGlassSurface(RenderPrefab mesh)
        {
            SurfaceAsset[] glass = mesh.surfaceAssets.Where(s => s != null &&
                (s.name ?? "").IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (glass.Length != 1)
                throw new InvalidOperationException($"{mesh.name} requires exactly one _Gls surface, found {glass.Length}");
            return glass[0];
        }

        private static void RequireStrictGlassMaterial(string model, float coatMask, Material material)
        {
            // The pass has to exist and be off: GetShaderPassEnabled is true for a pass the active sub-shader does not have.
            if (material == null || material.shader == null || material.shader.name != "BH/GlsShader" ||
                !material.HasProperty("_CoatMask") || !Mathf.Approximately(material.GetFloat("_CoatMask"), coatMask) ||
                !material.HasProperty("_ReceivesSSRTransparent") || material.GetFloat("_ReceivesSSRTransparent") != 0f ||
                !material.HasProperty("_TransparentDepthPrepassEnable") || material.GetFloat("_TransparentDepthPrepassEnable") != 0f ||
                material.FindPass("TransparentDepthPrepass") < 0 || material.GetShaderPassEnabled("TransparentDepthPrepass"))
            {
                HdrpActive(out string pipeline);
                throw new InvalidOperationException(model + " loaded glass material violates its coat/depth compatibility contract; " +
                    pipeline + "; " + GlassMaterialEvidence(material));
            }
        }

        /// <summary>
        /// HDRP exists, so Shader Graph materials resolve to their HDRP sub-shader. HDRP sets Shader.globalRenderPipeline when it is
        /// created (on the first camera render) and clears it when disposed; until then every Shader Graph material, the game's own
        /// DefaultGlassDoubleSided too, resolves to its FallbackError sub-shader, which has no TransparentDepthPrepass pass. Seen in
        /// review boots with the window hidden: the package check at about 32 s found FallbackError on both the template and our
        /// glass (depthPrepassIndex=-1), the boots checked a few seconds later found HDLitSubTarget (index 2, pass off).
        /// </summary>
        private static bool HdrpActive(out string state)
        {
            string tag = Shader.globalRenderPipeline;
            UnityEngine.Rendering.RenderPipeline pipeline = UnityEngine.Rendering.RenderPipelineManager.currentPipeline;
            state = "globalRenderPipeline=" + (string.IsNullOrEmpty(tag) ? "empty" : tag) +
                "; currentPipeline=" + (pipeline == null ? "none" : pipeline.GetType().Name);
            return tag == "HDRenderPipeline" && pipeline is HDRenderPipeline;
        }

        // The package checks on the main menu wait for HDRP (a precondition of the glass material check, not a retry of it), at
        // most kPipelineWaitSeconds; past that they run and say the precondition was not met.
        private const double kPipelineWaitSeconds = 120;
        private static double s_PipelineWaitStart = -1;
        private static bool s_PipelineWaitLogged, s_PipelineReadyLogged;

        private static bool PipelineReadyForChecks(double now)
        {
            if (HdrpActive(out string state))
            {
                if (s_PipelineWaitStart >= 0 && !s_PipelineReadyLogged)
                {
                    s_PipelineReadyLogged = true;
                    Mod.log.Info($"Render pipeline active after waiting {now - s_PipelineWaitStart:0.0} s ({state}); package checks run now.");
                }
                return true;
            }

            if (s_PipelineWaitStart < 0)
                s_PipelineWaitStart = now;
            if (!s_PipelineWaitLogged)
            {
                s_PipelineWaitLogged = true;
                Mod.log.Info($"Package checks wait for the render pipeline ({state}): Shader Graph materials cannot be checked before HDRP exists.");
            }

            if (now - s_PipelineWaitStart < kPipelineWaitSeconds)
                return false;
            if (!s_PipelineReadyLogged)
            {
                s_PipelineReadyLogged = true;
                Mod.log.Warn($"Render pipeline still not active after {kPipelineWaitSeconds:0} s ({state}); package checks run and report it.");
            }
            return true;
        }

        private static string GlassFloatEvidence(Material material, string property) =>
            material == null || !material.HasProperty(property) ? "missing" :
            material.GetFloat(property).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        // Evidence getters only: do not validate, synchronize or mutate either
        // the shared material or the global template while locating a mismatch.
        private static string GlassMaterialEvidence(Material material)
        {
            if (material == null) return "material=null";
            try
            {
                Shader shader = material.shader;
                return "material=" + material.name + "; instanceId=" + material.GetInstanceID() +
                    "; shader=" + (shader == null ? "null" : shader.name) +
                    "; shaderInstanceId=" + (shader == null ? "unavailable" : shader.GetInstanceID().ToString()) +
                    "; shaderSupported=" + (shader == null ? "unavailable" : shader.isSupported.ToString()) +
                    "; ShaderGraphShader=" + material.GetTag("ShaderGraphShader", false, "missing") +
                    "; ShaderGraphTargetId=" + material.GetTag("ShaderGraphTargetId", false, "missing") +
                    "; hasCoat=" + material.HasProperty("_CoatMask") +
                    "; coat=" + GlassFloatEvidence(material, "_CoatMask") +
                    "; hasTransparentSSR=" + material.HasProperty("_ReceivesSSRTransparent") +
                    "; transparentSSR=" + GlassFloatEvidence(material, "_ReceivesSSRTransparent") +
                    "; hasDepthPrepass=" + material.HasProperty("_TransparentDepthPrepassEnable") +
                    "; depthPrepass=" + GlassFloatEvidence(material, "_TransparentDepthPrepassEnable") +
                    "; depthPrepassIndex=" + material.FindPass("TransparentDepthPrepass") +
                    "; depthPrepassPass=" + material.GetShaderPassEnabled("TransparentDepthPrepass");
            }
            catch (Exception ex) { return "material-evidence-read-failed=" + ex.GetType().Name + ":" + ex.Message; }
        }

        private static string GlassSurfaceEvidence(SurfaceAsset surface)
        {
            if (surface == null) return "surface=null";
            try
            {
                bool dataLoaded = surface.isDataLoaded;
                string properties = "not-loaded";
                if (dataLoaded)
                    properties = string.Join(",", new[] { "_CoatMask", "_ReceivesSSRTransparent", "_TransparentDepthPrepassEnable" }.Select(property =>
                        property + "[float=" + (surface.TryGetFloatProperty(property, out float value) ?
                            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "missing") +
                        ";int=" + (surface.ints != null && surface.ints.TryGetValue(property, out int integer) ? integer.ToString() : "missing") + "]"));
                return "surface=" + surface.name + "; id=" + surface.id +
                    "; managedInstanceId=" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(surface) +
                    "; database=" + surface.database?.name + "; path=" + surface.path + "; subPath=" + surface.subPath +
                    "; isObjectLoaded=" + surface.isObjectLoaded + "; isDataLoaded=" + dataLoaded +
                    "; materialTemplateHash=" + surface.materialTemplateHash + "; persistedProperties=" + properties +
                    "; template={" + (dataLoaded ? GlassMaterialEvidence(surface.GetTemplateMaterial()) : "not-read-before-properties") + "}";
            }
            catch (Exception ex) { return "surface-evidence-read-failed=" + ex.GetType().Name + ":" + ex.Message; }
        }

        private static void LogStrictGlassEvidence(string model, string phase, SurfaceAsset surface, Material material = null)
        {
            HdrpActive(out string pipeline);
            Mod.log.Info(model + " glass lifecycle " + phase + ": " + pipeline + "; " + GlassSurfaceEvidence(surface) +
                (material == null ? "" : "; loaded={" + GlassMaterialEvidence(material) + "}"));
        }

        /// <summary>
        /// The vehicle prefab: a copy of the template bus without what the editor's import leaves out too (sub-objects, areas,
        /// lanes, nets, the obsolete names); its sounds and light effects are kept, its icon is not (the template's picture): the pack's
        /// own (pack.icon) or none, then the game renders one.
        /// </summary>
        private static CarPrefab VehiclePrefab(CarPrefab template, string source, Model model, string importSeed)
        {
            AssetDataPath path = AssetImportPipeline.GetPath(importSeed, model.Name, HashUtils.GetObjectName(model.Name, source), DataType.Prefab, AssetDatabase.user);
            if (AssetImportPanel.ExistsAndIsWritable(path, typeof(CarPrefab), out PrefabAsset existing))
            {
                existing.MarkDirty();
                CarPrefab again = existing.GetInstance<CarPrefab>();
                if (again.TryGet(out UIObject againUi)) againUi.m_Icon = IconUri(model, importSeed);
                return again;
            }

            Colossal.Hash128 guid = default;
            if (model.Guid != null)
            {
                if (!Colossal.Hash128.TryParse(model.Guid, out guid))
                    throw new InvalidOperationException($"{model.Name}: cannot read the GUID {model.Guid}");
                // a GUID the databases hold already (leftover import data of an earlier pack, or a loaded package) would make the
                // new asset silently lose to the old entry: stop with the owner named instead
                if (AssetDatabase.global.TryGetAsset(guid, out IAssetData stale))
                    throw new InvalidOperationException($"{model.Name}: the prefab GUID {model.Guid} is held by {stale.uri}; take that out first");
            }
            var bus = (CarPrefab)template.Clone(model.Name);
            bus.Remove<ObjectSubObjects>();
            bus.Remove<ObjectSubAreas>();
            bus.Remove<ObjectSubLanes>();
            bus.Remove<ObjectSubNets>();
            bus.Remove<NetSubObjects>();
            bus.Remove<AreaSubObjects>();
            bus.Remove<ObsoleteIdentifiers>();
            if (model.ObsoleteGuids != null && model.ObsoleteGuids.Length > 0)
            {
                // the earlier builds' identities: a save that holds one resolves to this prefab (the hash must match exactly)
                var aliases = bus.AddComponent<ObsoleteIdentifiers>();
                aliases.m_PrefabIdentifiers = model.ObsoleteGuids.Select(old => new PrefabIdentifierInfo
                    { m_Type = nameof(CarPrefab), m_Name = model.Name, m_Hash = old }).ToArray();
            }
            if (bus.TryGet(out UIObject ui))
                ui.m_Icon = IconUri(model, importSeed);
            if ((model.Assembly != null || model.Capacity > 0) && bus.TryGet(out PublicTransport transport))
                transport.m_PassengerCapacity = model.Capacity;
            SetUpNativeBoarding(bus, template, model);
            if (model.Guid != null)
                AssetDatabase.user.AddAsset<PrefabAsset, ScriptableObject>(path, bus, guid);
            else
                AssetDatabase.user.AddAsset(path, bus);
            Mod.log.Info($"Vehicle prefab {model.Name}: GUID {bus.asset?.id.guid}" +
                (model.ObsoleteGuids?.Length > 0 ? $", {model.ObsoleteGuids.Length} earlier GUIDs as aliases" : ""));
            return bus;
        }

        /// <summary>
        /// The asset menu icon of a pack's bus (pack.icon, a PNG beside its profile) as an image asset of the user database, referenced
        /// the way the editor's icon field does (UIIconField: an assetdb:// URI of the global database): the editor's dependency
        /// collection then packs the image with the prefab (DependencyCollector follows UIIconField strings to their image asset), and
        /// the loaded package resolves it. It sits in the request's import folder (ImportedData/&lt;seed&gt;) like the prefab, so the
        /// working files' cleanup takes it away even if the packer did not collect it (then the verify finds no icon in the package).
        /// null without an icon.
        /// </summary>
        private static string IconUri(Model model, string importSeed)
        {
            if (model.Icon == null) return null;
            if (!File.Exists(model.Icon)) throw new InvalidOperationException($"{model.Name}: pack.icon {model.Icon} does not exist");
            byte[] bytes = File.ReadAllBytes(model.Icon);
            if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P' || bytes[2] != (byte)'N' || bytes[3] != (byte)'G')
                throw new InvalidOperationException($"{model.Name}: pack.icon {model.Icon} is not a PNG");
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(bytes), 0, 8).Replace("-", "").ToLowerInvariant();
            AssetDataPath path = AssetImportPipeline.GetPath(importSeed, model.Name, $"{model.Name}_icon_{hash}.png", DataType.RawTexture,
                AssetDatabase.user, useExtension: true);
            if (AssetDatabase.user.Exists<ImageAsset>(path, out ImageAsset known))
            {
                Mod.log.Info($"Vehicle prefab {model.Name}: icon {Path.GetFileName(model.Icon)} reused ({GlobalUri(known)})");
                return GlobalUri(known);
            }
            using (ImageAsset image = AssetDatabase.user.AddAsset<ImageAsset>(path))
            {
                if (image == null) throw new InvalidOperationException($"{model.Name}: the icon image asset could not be added");
                using (Stream write = image.GetWriteStream()) write.Write(bytes, 0, bytes.Length);
                string uri = GlobalUri(image);
                Mod.log.Info($"Vehicle prefab {model.Name}: icon {Path.GetFileName(model.Icon)} ({bytes.Length} bytes) as {uri}");
                return uri;
            }
        }

        // UIExtensions.ToGlobalUri (Colossal.UI, not referenced here): the asset by its GUID in the global database
        private static string GlobalUri(AssetData asset) => $"assetdb://{AssetDatabase.global.hostname}/{asset.id.guid}";

        private static CarTrailerPrefab TrailerPrefab(CarPrefab template, string source, Model model, string importSeed)
        {
            AssetDataPath path = AssetImportPipeline.GetPath(importSeed, model.Name,
                HashUtils.GetObjectName(model.Name, source), DataType.Prefab, AssetDatabase.user);
            if (AssetImportPanel.ExistsAndIsWritable(path, typeof(CarTrailerPrefab), out PrefabAsset existing))
                throw new InvalidOperationException("Trailer import path already exists: " + model.Name);
            var trailer = PrefabBase.Create<CarTrailerPrefab>(model.Name);
            // Copy only the shared vehicle fields; never alias the template's component list.
            trailer.m_SizeClass = template.m_SizeClass;
            trailer.m_EnergyType = template.m_EnergyType;
            trailer.m_MaxSpeed = template.m_MaxSpeed;
            trailer.m_Acceleration = template.m_Acceleration;
            trailer.m_Braking = template.m_Braking;
            trailer.m_Turning = template.m_Turning;
            trailer.m_Stiffness = template.m_Stiffness;
            trailer.m_Circular = template.m_Circular;
            foreach (ComponentBase component in template.components) trailer.AddComponentFrom(component);
            trailer.Remove<ObjectSubObjects>(); trailer.Remove<ObjectSubAreas>(); trailer.Remove<ObjectSubLanes>();
            trailer.Remove<ObjectSubNets>(); trailer.Remove<NetSubObjects>(); trailer.Remove<AreaSubObjects>();
            trailer.Remove<ObsoleteIdentifiers>(); trailer.Remove<CarTractor>();
            if (trailer.TryGet(out UIObject ui)) ui.m_Icon = null;
            if (!trailer.TryGet(out PublicTransport transport))
                throw new InvalidOperationException("The trailer template lacks PublicTransport");
            transport.m_PassengerCapacity = model.Capacity;
            // Bus selection does not exclude CarTrailerData. Retain its passenger buffer/capacity
            // while preventing the dependent trailer from being selected as a standalone line bus.
            transport.m_Purposes = 0;
            SetUpNativeBoarding(trailer, template, model);
            if (model.ObsoleteGuids != null && model.ObsoleteGuids.Length > 0)
                trailer.AddComponent<ObsoleteIdentifiers>().m_PrefabIdentifiers = model.ObsoleteGuids.Select(old =>
                    new PrefabIdentifierInfo { m_Type = nameof(CarTrailerPrefab), m_Name = model.Name, m_Hash = old }).ToArray();
            if (model.Guid == null || !Colossal.Hash128.TryParse(model.Guid, out Colossal.Hash128 guid))
                throw new InvalidOperationException("A fixed trailer prefab requires its explicit GUID");
            if (AssetDatabase.global.TryGetAsset(guid, out IAssetData stale))
                throw new InvalidOperationException($"{model.Name}: prefab GUID is held by {stale.uri}");
            AssetDatabase.user.AddAsset<PrefabAsset, ScriptableObject>(path, trailer, guid);
            return trailer;
        }

        private static void SetUpNativeBoarding(CarBasePrefab vehicle, CarPrefab template, Model model)
        {
            if (model.Assembly == null && model.SectionProfile?.NativeDoors == null) return;
            if (model.SectionProfile.NativeDoors == null || model.SectionProfile.NativeDoors.Length != model.SectionProfile.Doors.Length)
                throw new InvalidOperationException("Vehicle lacks explicit native door thresholds: " + model.Name);
            if (!template.TryGet(out Game.Prefabs.ActivityLocation original) || original.m_Locations == null)
                throw new InvalidOperationException("Bus template lacks native passenger activities");
            ActivityLocationPrefab Pure(ActivityType type) => original.m_Locations.Select(x => x.m_Activity)
                .FirstOrDefault(a => a != null && a.m_Activities != null && a.m_Activities.Length == 1 && a.m_Activities[0] == type);
            ActivityLocationPrefab enter = Pure(ActivityType.Enter), exit = Pure(ActivityType.Exit);
            if (enter == null || exit == null)
                throw new InvalidOperationException("Template has no pure Enter/Exit passenger activities; refuse ambiguous seat/driver activity reuse");
            VehicleBoarding.Point[] points = VehicleBoarding.Points(model.SectionProfile);
            if (points.Length == 0) throw new InvalidOperationException("Passenger section has no accessible native door");
            vehicle.Remove<Game.Prefabs.ActivityLocation>();
            var activity = vehicle.AddComponent<Game.Prefabs.ActivityLocation>();
            activity.m_InvertWhen = NetInvertMode.Never;
            activity.m_AnimatedPropName = string.Empty;
            activity.m_RequireAuthorization = false;
            activity.m_Locations = points.Select(p => new Game.Prefabs.ActivityLocation.LocationInfo
            {
                m_Activity = p.Enter ? enter : exit, m_Position = p.Position, m_Rotation = p.Rotation,
            }).ToArray();
        }

        private static void RetainNativeSourceTextures(PackageAsset package, AssetData[] sourceAssets)
        {
            // The game's VT conversion removes ordinary textures. CDL's private non-VT cab
            // materials still need those exact resources, alongside the native VT surfaces.
            var original = new Dictionary<string, string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(package.path))
            {
                foreach (var entry in archive.Entries)
                {
                    using (var stream = entry.Open()) original.Add(entry.FullName, Digest(stream));
                    if (entry.FullName.EndsWith(".cid", StringComparison.Ordinal))
                        using (var reader = new StreamReader(entry.Open())) identities.Add(reader.ReadToEnd().Trim());
                }
            }
            Colossal.IO.AssetDatabase.TextureAsset[] missing = sourceAssets.OfType<Colossal.IO.AssetDatabase.TextureAsset>()
                .Where(texture => !identities.Contains(texture.id.guid.ToString())).ToArray();
            if (missing.Length == 0) return;
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Colossal.IO.AssetDatabase.TextureAsset texture in missing)
            {
                if (texture.isDummy) throw new InvalidOperationException("Cannot retain a missing source texture: " + texture.id);
                using (var stream = texture.GetReadStream()) expected.Add(texture.id.guid.ToString(), Digest(stream));
            }
            // GetPackageWriter opens a replacement archive. Open the existing package
            // explicitly so the native ZIP writer updates it without dropping VT data.
            using (var file = File.Open(package.path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (IPackageWriter writer = new ZipPackageWriter(file))
            {
                foreach (Colossal.IO.AssetDatabase.TextureAsset texture in missing) writer.Add(texture, preserveTimestamp: true);
                if (!writer.Commit()) throw new IOException("Could not retain native source textures in " + package.path);
            }
            using (var archive = System.IO.Compression.ZipFile.OpenRead(package.path))
            {
                foreach (var pair in original)
                {
                    var entry = archive.GetEntry(pair.Key) ?? throw new IOException("Texture retention removed " + pair.Key);
                    using (var stream = entry.Open())
                        if (Digest(stream) != pair.Value) throw new IOException("Texture retention changed " + pair.Key);
                }
                var checkedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".cid", StringComparison.Ordinal)))
                {
                    string id;
                    using (var reader = new StreamReader(entry.Open())) id = reader.ReadToEnd().Trim();
                    if (!checkedIds.Add(id)) throw new IOException("Duplicate packaged asset identity: " + id);
                    if (!expected.TryGetValue(id, out string digest)) continue;
                    var data = archive.GetEntry(entry.FullName.Substring(0, entry.FullName.Length - 4))
                        ?? throw new IOException("Retained texture has no data: " + id);
                    using (var stream = data.Open())
                        if (Digest(stream) != digest) throw new IOException("Retained texture differs from import: " + id);
                    expected.Remove(id);
                }
                if (expected.Count != 0) throw new IOException("Native source textures were not retained");
            }
            Mod.log.Info($"Retained {missing.Length} native source textures for CDL non-VT materials; existing package entries unchanged");

            string Digest(Stream stream)
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
        }

        /// <summary>
        /// The packed prefab's passenger activity (its ActivityLocation component and the entity's buffer) is the profile's doors exactly.
        /// Every field that differs is reported with the expected and the actual value, not only the first.
        /// </summary>
        private void VerifyNativeBoarding(CarBasePrefab vehicle, Entity entity, VehicleProfile profile)
        {
            VehicleBoarding.Point[] expected = VehicleBoarding.Points(profile);
            var differences = new List<string>();
            void Differs<T>(string field, T want, T have)
            {
                if (!EqualityComparer<T>.Default.Equals(want, have)) differences.Add($"{field}: expected {want}, actual {have}");
            }
            static string P(Unity.Mathematics.float3 p) => $"({p.x:0.###}, {p.y:0.###}, {p.z:0.###})";
            static string Q(Unity.Mathematics.quaternion q) => $"({q.value.x:0.####}, {q.value.y:0.####}, {q.value.z:0.####}, {q.value.w:0.####})";
            static bool SameRotation(Unity.Mathematics.quaternion a, Unity.Mathematics.quaternion b) => 1 - Unity.Mathematics.math.abs(Unity.Mathematics.math.dot(a.value, b.value)) <= 1e-5f;

            if (!vehicle.TryGet(out Game.Prefabs.ActivityLocation activity))
                throw new InvalidOperationException(profile.Name + " native passenger activity differs: the prefab has no ActivityLocation component");
            Differs("ActivityLocation.m_Locations.Length", expected.Length, activity.m_Locations?.Length ?? -1);
            Differs("ActivityLocation.m_InvertWhen", NetInvertMode.Never, activity.m_InvertWhen);
            Differs("ActivityLocation.m_RequireAuthorization", false, activity.m_RequireAuthorization);
            Differs("ActivityLocation.m_AnimatedPropName", "", activity.m_AnimatedPropName ?? "");
            bool buffer = EntityManager.HasBuffer<ActivityLocationElement>(entity);
            if (!buffer) differences.Add("ActivityLocationElement buffer: expected present, actual missing");
            DynamicBuffer<ActivityLocationElement> actual = buffer ? EntityManager.GetBuffer<ActivityLocationElement>(entity, true) : default;
            if (buffer) Differs("ActivityLocationElement buffer length (template entries left?)", expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                var point = expected[i];
                string at = $"location {i} (door {point.Door}, {(point.Enter ? "enter" : "exit")})";
                ActivityType kind = point.Enter ? ActivityType.Enter : ActivityType.Exit;
                if (activity.m_Locations != null && i < activity.m_Locations.Length)
                {
                    var entry = activity.m_Locations[i];
                    ActivityType[] kinds = entry.m_Activity?.m_Activities;
                    Differs(at + " prefab activities", kind.ToString(), kinds == null ? "none" : string.Join("+", kinds));
                    if (Unity.Mathematics.math.distance(entry.m_Position, point.Position) > .001f)
                        differences.Add($"{at} prefab position: expected {P(point.Position)}, actual {P(entry.m_Position)}");
                    if (!SameRotation(entry.m_Rotation, point.Rotation))
                        differences.Add($"{at} prefab rotation: expected {Q(point.Rotation)}, actual {Q(entry.m_Rotation)}");
                }
                if (buffer && i < actual.Length)
                {
                    Differs(at + " buffer activity mask", new ActivityMask(kind).m_Mask, actual[i].m_ActivityMask.m_Mask);
                    Differs(at + " buffer activity flags", default(ActivityFlags), actual[i].m_ActivityFlags);
                    if (Unity.Mathematics.math.distance(actual[i].m_Position, point.Position) > .001f)
                        differences.Add($"{at} buffer position: expected {P(point.Position)}, actual {P(actual[i].m_Position)}");
                    if (!SameRotation(actual[i].m_Rotation, point.Rotation))
                        differences.Add($"{at} buffer rotation: expected {Q(point.Rotation)}, actual {Q(actual[i].m_Rotation)}");
                }
            }
            if (differences.Count > 0)
                throw new InvalidOperationException($"{profile.Name} native passenger activity differs in {differences.Count} field(s): {string.Join("; ", differences)}");
        }
    }
}
