using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;

namespace CDLC.BusDriver
{
    public sealed class Mod : IMod
    {
        public const string kModName = "CDLC.BusDriver";
        public const string kVersion = "0.17.7";

        public static readonly ILog log = LogManager.GetLogger($"{kModName}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        internal static BusSettings Settings { get; private set; }

        /// <summary>The mod's own folder (set on load), for content files like the LED font.</summary>
        internal static string ModDirectory { get; private set; }
        private bool m_LoadStarted;

        /// <summary>
        /// Whether the CDL Asset installed holds the LED screens' shared parts. Builds before them have no AssetApi.Features at all, so it is
        /// read by reflection: a missing property says "no" instead of failing where this method is first compiled or run.
        /// </summary>
        private static bool HasLedCore()
        {
            // (int)Feature.LedCore is a constant the compiler writes into the code: the enum type is never resolved at run time, which is
            // what lets this run beside a CDL Asset that does not have it. Do not write AssetApi.Features.HasFlag(...) here: that fails there.
            System.Reflection.PropertyInfo features = typeof(CDLC.Asset.AssetApi).GetProperty("Features", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return features != null && (Convert.ToInt32(features.GetValue(null)) & (int)CDLC.Asset.AssetApi.Feature.LedCore) != 0;
        }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"{kModName} {kVersion} loading");
            if (CDLC.Asset.AssetApi.ApiVersion < 7)
            {
                log.Error("Bus Driver requires CDL Asset API 7 or newer.");
                return;
            }
            if (!HasLedCore())
            {
                log.Error("Bus Driver requires CDL Assets 0.6.0 or newer (the LED core, AssetApi.Features.LedCore); update CDL Assets.");
                return;
            }
            m_LoadStarted = true;
            string modDirectory = null;
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                modDirectory = Path.GetDirectoryName(asset.path);

            BusLocale.Register(modDirectory);
            BusDoorSounds.AudioDirectory = modDirectory != null ? Path.Combine(modDirectory, "Audio") : null;
            ModDirectory = modDirectory;
            Settings = new BusSettings(this);
            Settings.RegisterInOptionsUI();
            Settings.RegisterKeyBindings();
            AssetDatabase.global.LoadSettings(kModName, Settings, new BusSettings(this));
            if (Settings.RoutePrefixColors == LineLabel.OldDefaultColors)
                Settings.RoutePrefixColors = LineLabel.DefaultColors;       // the old defaults: K now green
            Settings.MigrateKeys();
            Settings.MigrateOptions();
            BusInput.Initialize(Settings);
            BusLocale.ReportMissing(Settings.RequiredLocaleKeys());

            bool drivingMod = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "CDLC");
            log.Info(drivingMod
                ? "CSL Driving Life Core found."
                : "CSL Driving Life Core not loaded yet; the bus panel will ask for it if it stays missing.");

            // Before the bus AI: the stop marks and the departure time must be in place when it ticks.
            updateSystem.UpdateBefore<BusShiftSystem, TransportCarAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<BusGuidanceSystem, OverlayRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<BusUiSystem>(SystemUpdatePhase.UIUpdate);
            // the passenger mode: the player rides a bus the game drives (Core API 17), picked and followed from here
            updateSystem.UpdateAt<BusRideSystem>(SystemUpdatePhase.UIUpdate);
#if CDLC_AUTHORING
            // Packs the bus as a vehicle asset when pack_asset.sh asks (on the main menu; the UI phase runs there too).
            updateSystem.UpdateAt<BusVehicleAssetSystem>(SystemUpdatePhase.UIUpdate);
#endif
            // Every frame, paused game included: the announcements follow the shift's state and play their clips.
            updateSystem.UpdateAt<BusAnnouncementSystem>(SystemUpdatePhase.Rendering);
            // After the driving mod's camera, which publishes the body pose of this frame: the interior sits where it draws the bus.
            updateSystem.UpdateAfter<BusCabSystem, CDLC.DirectDriveCameraSystem>(SystemUpdatePhase.PreCulling);
            // the AI buses' LED signs (option), after the cab: the bus being driven is the cab's
            updateSystem.UpdateAfter<BusAiSignSystem, BusCabSystem>(SystemUpdatePhase.PreCulling);
            // the station packs' departure boards (option): still screens, after the signs (the same materials' dimming)
            updateSystem.UpdateAfter<BusStationBoardSystem, BusAiSignSystem>(SystemUpdatePhase.PreCulling);
        }

        public void OnDispose()
        {
            log.Info($"{kModName} disposing");
            if (!m_LoadStarted) return;
            m_LoadStarted = false;
            var failures = new List<Exception>();
            foreach (Action cleanup in new Action[]
            {
                CabPrebuild.Shutdown, Core.ClearAll, GamepadHaptics.Shutdown, BusInput.Dispose, Interior.CabMaterials.DestroyAll,
                () => Settings?.UnregisterInOptionsUI(), BusState.Reset, BusLocale.Unregister,
            })
                try { cleanup(); } catch (Exception error) { failures.Add(error); }
            Settings = null;
            ModDirectory = null;
            if (failures.Count != 0) throw new AggregateException("Bus Driver shutdown failed.", failures);
        }
    }
}
