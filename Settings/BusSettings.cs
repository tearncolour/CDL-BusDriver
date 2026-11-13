using System.Collections.Generic;
using CDLC.BusDriver.Interior.Models;
using Colossal.IO.AssetDatabase;
using Game.Input;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace CDLC.BusDriver
{
    /// <summary>Options page (Options -> Bus Driver): what the guidance shows, and the keys.</summary>
    [FileLocation("ModsSettings\\" + Mod.kModName + "\\" + Mod.kModName)]
    [SettingsUITabOrder(kGeneralTab, kKeysTab)]
    [SettingsUIGroupOrder(kGeneralGroup, kCabGroup, kAnnounceGroup, kGamepadGroup, kGuidanceGroup, kKeysGroup)]
    [SettingsUIShowGroupName(kCabGroup, kAnnounceGroup, kGamepadGroup, kGuidanceGroup, kKeysGroup)]
    [SettingsUIKeyboardAction(BusInput.kPanel, ActionType.Button, usages: new[] { Usages.kDefaultUsage, Usages.kOverlayUsage, Usages.kToolUsage, Usages.kCancelableToolUsage, Usages.kDiscardableToolUsage, kShiftUsage })]
    [SettingsUIKeyboardAction(BusInput.kDoors, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIKeyboardAction(BusInput.kIndicateLeft, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIKeyboardAction(BusInput.kIndicateRight, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIKeyboardAction(BusInput.kImmersive, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIKeyboardAction(BusInput.kLights, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIKeyboardAction(BusInput.kFlash, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIKeyboardAction(BusInput.kHazard, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kPanel, ActionType.Button, usages: new[] { Usages.kDefaultUsage, Usages.kOverlayUsage, Usages.kToolUsage, Usages.kCancelableToolUsage, Usages.kDiscardableToolUsage, kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kDoors, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kIndicateLeft, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kIndicateRight, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kImmersive, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kLights, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kFlash, ActionType.Button, usages: new[] { kShiftUsage })]
    [SettingsUIGamepadAction(BusInput.kHazard, ActionType.Button, usages: new[] { kShiftUsage })]
    public sealed class BusSettings : ModSetting
    {
        public const string kGeneralTab = "General";
        public const string kKeysTab = "Keys";
        public const string kGeneralGroup = "GeneralGroup";
        public const string kGuidanceGroup = "Guidance";
        public const string kCabGroup = "Cab";
        public const string kAnnounceGroup = "Announce";
        public const string kKeysGroup = "KeyBindings";
        public const string kGamepadGroup = "Gamepad";

        /// <summary>
        /// The usage of every key of ours but the panel's, so the game flags two of them on one key. They run while the player drives (the
        /// indicators, the hazard lamps, the head lamps) or is on a shift (the doors, immersive mode), when the driving mod has paused
        /// the camera keys they would clash with (BusInput.SetState). The name stays "Shift" for the bindings saved under it.
        /// </summary>
        public const string kShiftUsage = "CDLC.BusDriver.Shift";

        /// <summary>
        /// The key layout of this build: 1 had the doors on E and the right indicator on R, 2 the doors on F, the indicators on Q / E, the
        /// lights on L, K and Z, 3 the same and the pad's panel (R3 + square) and hazard lamps (R3 + D-pad right) on combinations.
        /// </summary>
        internal const int kKeyLayout = 3;

        public BusSettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(kGeneralTab, kGeneralGroup)]
        [SettingsUIMultilineText]
        public string Intro => string.Empty;

        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool ShowInterior { get; set; }

        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool CabViewOnShift { get; set; }

        /// <summary>
        /// Where the camera of the driver view sits above or below the model seat, in centimetres (up is positive; CabViewRules): for a taller or a shorter driver. Zero is
        /// the model eye, so a settings file from before this option has it too. Applies at once, in the driver view of a shift and of a ride.
        /// </summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUISlider(min = -CabViewRules.MaxOffsetCm, max = CabViewRules.MaxOffsetCm, step = 1f, unit = Unit.kInteger)]
        public int CabEyeHeight { get; set; }

        /// <summary>Where the camera of the driver view sits to the left or right of the model seat, in centimetres (right is positive, whichever side the driver sits on).</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUISlider(min = -CabViewRules.MaxOffsetCm, max = CabViewRules.MaxOffsetCm, step = 1f, unit = Unit.kInteger)]
        public int CabEyeSide { get; set; }

        /// <summary>Taking a bus of a line directly (the driving mod's key or the info panel) starts the shift on its line (BusShiftRules).</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool ShiftOnDrive { get; set; }

        /// <summary>The options' layout the saved settings follow (kOptionsLayout): not an option, only what MigrateOptions goes by.</summary>
        [SettingsUIHidden]
        public int OptionsLayout { get; set; }

        internal const int kOptionsLayout = 3;

        /// <summary>
        /// A settings file from before an option existed gives it the type's default when it is read, not the mod's: layout 1 (BusDriver
        /// with the shift on a direct take) switches ShiftOnDrive on once; layout 2 (the AI buses' signs) switches AiBusSigns on at
        /// 120 m; layout 3 (the station packs' boards) switches StationBoards on. Returns nothing; saves when it changed something.
        /// </summary>
        internal void MigrateOptions()
        {
            if (OptionsLayout >= kOptionsLayout)
                return;
            if (OptionsLayout < 1)
                ShiftOnDrive = true;
            if (OptionsLayout < 2)
            {
                AiBusSigns = true;
                AiBusSignDistance = 120;
            }
            if (OptionsLayout < 3)
                StationBoards = true;
            OptionsLayout = kOptionsLayout;
            ApplyAndSave();
        }

        /// <summary>The whole bus, outside too, drawn instead of the game's bus while you drive it (needs CSL Driving Life Core 0.10).</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool ShowExterior { get; set; }

        /// <summary>
        /// The colours of route prefixes (a line named K3, BRT1, 夜12: the prefix on the signs and in the HUD): "K=green; BRT=green;
        /// *=red" (names or #RRGGBB; * any other prefix).
        /// </summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUITextInput]
        public string RoutePrefixColors { get; set; }

        /// <summary>A body wrap on the bus the player drives: a folder of ModsData/CDLC.BusDriver/Wraps ("": none). BusSkins.</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUIDropdown(typeof(BusSettings), nameof(BodyWrapItems))]
        [SettingsUIValueVersion(typeof(BusSettings), nameof(BodyWrapVersion))]
        public string BodyWrap { get; set; }

        /// <summary>Opens the wraps folder (with its README and, after a drive, the template).</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUIButton]
        public bool OpenWrapFolder
        {
            set => BusSkins.OpenFolder();
        }

        public Game.UI.Widgets.DropdownItem<string>[] BodyWrapItems() => BusSkins.Items();

        public int BodyWrapVersion() => BusSkins.Version;

        /// <summary>The exterior mirrors show the view behind in the driver's view (a small camera each; costs some frame time).</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool RealMirrors { get; set; }

        /// <summary>
        /// What those mirrors may cost: how often they are redrawn, their resolution and the passes they leave out (CabMirrors,
        /// MirrorBudget). Applies while driving. The initial value is Medium, so a settings file from before this option has it too.
        /// </summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(MirrorsOff))]
        public MirrorQuality MirrorQuality { get; set; } = MirrorQuality.Medium;

        public bool MirrorsOff => !RealMirrors;

        /// <summary>
        /// How much the glass of our buses computes (GlassDetailRules): High as it always was; Balanced, glass that writes no motion vectors of its own
        /// (the picture behind stays sharp under anti-aliasing and upscalers) and no pixel depth offset; Fast, also no fog on the glass,
        /// and the glass of the bus you sit in is not drawn in the views from a seat. CDL Assets 0.6.2 does the material part. The initial value is
        /// High, so a settings file from before this option has it too.
        /// </summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public GlassDetail GlassDetail { get; set; } = GlassDetail.High;

        /// <summary>Where the head lamp switch starts each time you take a bus (the light key steps it: Auto, Low, High, Off).</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public LightMode LightsMode { get; set; }

        /// <summary>The head lamps of our own buses also light the road (two spot lights per bus); off: the lamps only glow.</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool RealHeadlights { get; set; }

        /// <summary>How bright those spot lights are, 100 = the mod's setting; the game's night exposure decides what looks right.</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUISlider(min = 25f, max = 300f, step = 25f, unit = Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(HeadlightsOff))]
        public int HeadlightStrength { get; set; }

        public bool HeadlightsOff => !RealHeadlights;

        /// <summary>
        /// The outside LED signs (the driven bus's and the AI buses'): Shanghai, 32 rows of square dots, the route label as tall as the
        /// sign, a chevron and the stop in Chinese above its English line (LedHd); Classic, the 16-row signs before 0.12.6. The initial
        /// value is Shanghai, so a settings file from before this option has it too.
        /// </summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public LedSignStyle LedSignStyle { get; set; } = LedSignStyle.Shanghai;

        /// <summary>The Shanghai-style rear sign shows "刹车注意" while braking and "车辆左转" / "车辆右转" while indicating (RearSignRules).</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(ClassicSigns))]
        public bool RearSignWarnings { get; set; } = true;

        public bool ClassicSigns => LedSignStyle == LedSignStyle.Classic;

        /// <summary>
        /// The LED destination signs of our own buses also on the AI buses of the city (front, kerb side, rear: route and terminus,
        /// "Not in service" off a line), within the distance below (BusAiSignSystem). Off: no AI bus is looked at (saves the work).
        /// </summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool AiBusSigns { get; set; }


        /// <summary>How far from the camera the AI buses' signs are drawn (m); farther ones stay dark.</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        [SettingsUISlider(min = 40f, max = 300f, step = 10f, unit = Unit.kInteger)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(AiBusSignsOff))]
        public int AiBusSignDistance { get; set; }

        public bool AiBusSignsOff => !AiBusSigns;

        /// <summary>
        /// Live departures on the LED screens of station packs (a bus station asset with a .cdls.json beside its .cok): each bay's lines, their
        /// terminus and nearest bus, the people waiting (BusStationBoardSystem). Off: the screens stay dark and no station is looked at.
        /// </summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool StationBoards { get; set; }

        /// <summary>The key layout the saved keys follow (kKeyLayout): not an option, only what MigrateKeys goes by.</summary>
        [SettingsUIHidden]
        public int KeyLayout { get; set; }

        /// <summary>The passengers on board as people in the seats and standing in the bus model, in proportion to the load.</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool ShowRiders { get; set; }

        /// <summary>The passengers and the driver are the game's own citizens (their models, clothes and hair), posed by the mod.</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool GameCitizens { get; set; }

        /// <summary>The bus kneels (lowers its door side) at every stop while the doors are open.</summary>
        [SettingsUISection(kGeneralTab, kCabGroup)]
        public bool KneelAtStops { get; set; }

        /// <summary>Stop announcements: chime and voice when the bus leaves a stop, approaches one and opens its doors.</summary>
        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        public bool Announcements { get; set; }

        /// <summary>Where the voice comes from: Microsoft's neural voices online (cached), the Windows voices offline, or none.</summary>
        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(AnnouncementsOff))]
        public AnnounceVoice AnnounceVoice { get; set; }

        /// <summary>Downloads Piper and the voices for the game's language and English (about 150 MB) in the background.</summary>
        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(AnnouncementsOff))]
        public bool InstallPiper
        {
            set => BusAnnouncementSystem.InstallPiperRequested = true;
        }

        /// <summary>The key of the player's Azure Speech resource (the neural voice); the free tier covers a city's announcements.</summary>
        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUITextInput]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(NeuralOff))]
        public string AzureKey { get; set; }

        /// <summary>The resource's region: eastasia, southeastasia, westeurope, eastus, ...</summary>
        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUITextInput]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(NeuralOff))]
        public string AzureRegion { get; set; }

        /// <summary>HTTP proxy for the service ("127.0.0.1:7897"); empty = the system's proxy; "none" = direct.</summary>
        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUITextInput]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(NeuralOff))]
        public string NeuralProxy { get; set; }

        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUISlider(min = 10f, max = 100f, step = 5f, unit = Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(AnnouncementsOff))]
        public int AnnounceVolume { get; set; }

        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(AnnouncementsOff))]
        public bool AnnounceTerminus { get; set; }

        /// <summary>The spoken files kept on disk (%LOCALAPPDATA%\CDLC\tts), in MB; the oldest go first.</summary>
        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUISlider(min = 50f, max = 1000f, step = 50f, unit = Unit.kInteger)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(AnnouncementsOff))]
        public int AnnounceCacheMb { get; set; }

        [SettingsUISection(kGeneralTab, kAnnounceGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool ClearAnnounceCache
        {
            set => BusAnnouncementSystem.ClearCacheRequested = true;
        }

        /// <summary>Rumble in the pad while driving the bus: the engine, the road, gear shifts, hard braking, the doors.</summary>
        [SettingsUISection(kGeneralTab, kGamepadGroup)]
        public bool GamepadFeedback { get; set; }

        [SettingsUISection(kGeneralTab, kGamepadGroup)]
        [SettingsUISlider(min = 10f, max = 100f, step = 5f, unit = Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(FeedbackOff))]
        public int FeedbackStrength { get; set; }

        /// <summary>DualSense only (not behind Steam Input): the brake and throttle triggers push back and buzz. See HapticsModel.</summary>
        [SettingsUISection(kGeneralTab, kGamepadGroup)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(FeedbackOff))]
        public bool AdaptiveTriggers { get; set; }

        /// <summary>
        /// DualSense on USB only: the triggers themselves vibrate - the throttle trigger (R2) with the engine (faster with the revs,
        /// stronger with the pedal), the brake trigger (L2) in pulses under hard braking, both for a jolt of the road and a gear shift.
        /// The initial value is on, so a settings file from before this option has it on too.
        /// </summary>
        [SettingsUISection(kGeneralTab, kGamepadGroup)]
        [SettingsUIDisableByCondition(typeof(BusSettings), nameof(FeedbackOff))]
        public bool TriggerVibration { get; set; } = true;

        public bool FeedbackOff => !GamepadFeedback;

        public bool AnnouncementsOff => !Announcements;
        public bool NeuralOff => !Announcements || (AnnounceVoice != AnnounceVoice.Azure && AnnounceVoice != AnnounceVoice.Auto);

        [SettingsUISection(kGeneralTab, kGuidanceGroup)]
        public bool ShowZones { get; set; }

        [SettingsUISection(kGeneralTab, kGuidanceGroup)]
        public bool ShowAllStops { get; set; }

        [SettingsUISection(kGeneralTab, kGuidanceGroup)]
        public bool ShowRoute { get; set; }

        [SettingsUISection(kGeneralTab, kGuidanceGroup)]
        public bool ShowHud { get; set; }

        /// <summary>Immersive mode: no guide line and no other stops' zones on the road, only the next stop's zone, fainter (key I).</summary>
        [SettingsUISection(kGeneralTab, kGuidanceGroup)]
        public bool Immersive { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.B, BusInput.kPanel, alt: true)]
        public ProxyBinding KeyPanel { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.F, BusInput.kDoors)]
        public ProxyBinding KeyDoors { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Q, BusInput.kIndicateLeft)]
        public ProxyBinding KeyIndicateLeft { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.E, BusInput.kIndicateRight)]
        public ProxyBinding KeyIndicateRight { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.Z, BusInput.kHazard)]
        public ProxyBinding KeyHazard { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.L, BusInput.kLights)]
        public ProxyBinding KeyLights { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.K, BusInput.kFlash)]
        public ProxyBinding KeyFlash { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIKeyboardBinding(BindingKeyboard.I, BusInput.kImmersive)]
        public ProxyBinding KeyImmersive { get; set; }

        // The pad: the same actions. The ones used while driving are on a button; the panel and the hazard lamps are combinations: hold the
        // right stick (R3) and press the button (read from the pad itself, PadChord; the game's own action on that button does not
        // fire with R3 down). The indicators' LB + RB pressed together still switch the hazard lamps.
        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.West, BusInput.kPanel, rightStick: true)]
        public ProxyBinding PadPanel { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.West, BusInput.kDoors)]
        public ProxyBinding PadDoors { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.LeftShoulder, BusInput.kIndicateLeft)]
        public ProxyBinding PadIndicateLeft { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.RightShoulder, BusInput.kIndicateRight)]
        public ProxyBinding PadIndicateRight { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.DpadRight, BusInput.kHazard, rightStick: true)]
        public ProxyBinding PadHazard { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.DpadLeft, BusInput.kLights)]
        public ProxyBinding PadLights { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.DpadRight, BusInput.kFlash)]
        public ProxyBinding PadFlash { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIGamepadBinding(BindingGamepad.DpadUp, BusInput.kImmersive)]
        public ProxyBinding PadImmersive { get; set; }

        [SettingsUISection(kKeysTab, kKeysGroup)]
        [SettingsUIButton]
        public bool ResetKeys
        {
            set => ResetKeyBindings();
        }

        public override void SetDefaults()
        {
            ShowInterior = true;
            CabViewOnShift = true;
            CabEyeHeight = 0;
            CabEyeSide = 0;
            ShiftOnDrive = true;
            OptionsLayout = kOptionsLayout;
            ShowExterior = true;
            RoutePrefixColors = LineLabel.DefaultColors;
            RealMirrors = true;
            MirrorQuality = MirrorQuality.Medium;
            GlassDetail = GlassDetail.High;
            LightsMode = LightMode.Auto;
            RealHeadlights = true;
            HeadlightStrength = 100;
            AiBusSigns = true;
            AiBusSignDistance = 120;
            StationBoards = true;
            LedSignStyle = LedSignStyle.Shanghai;
            RearSignWarnings = true;
            KeyLayout = 0;
            BodyWrap = "";
            ShowRiders = true;
            GameCitizens = true;
            KneelAtStops = true;
            Announcements = true;
            AnnounceVoice = AnnounceVoice.Auto;
            AzureKey = "";
            AzureRegion = "eastasia";
            NeuralProxy = "";
            AnnounceVolume = 80;
            AnnounceTerminus = true;
            AnnounceCacheMb = 200;
            GamepadFeedback = true;
            FeedbackStrength = 80;
            AdaptiveTriggers = false;
            TriggerVibration = true;
            ShowZones = true;
            ShowAllStops = true;
            ShowRoute = true;
            ShowHud = true;
        }

        /// <summary>
        /// Moves a player who never rebound a key to the current layout (the doors on F, the indicators on Q / E): the old defaults are
        /// saved with the settings, so a new default would not reach them. A player who changed any of those keys keeps their keys
        /// (the "Reset keys" button gives the new layout).
        /// </summary>
        internal void MigrateKeys()
        {
            if (KeyLayout >= kKeyLayout)
                return;

            try
            {
                // Every key of the earlier layout at its default (the reset below puts all of them back, so one customised key,
                // the panel's and the pad's included, keeps the player's keys as they are).
                bool untouched;
                if (KeyLayout < 2)
                    untouched = IsKey(KeyDoors, "e") && IsKey(KeyIndicateLeft, "q") && IsKey(KeyIndicateRight, "r") && IsKey(KeyImmersive, "i") &&
                        IsKey(KeyPanel, "b", alt: true) && IsPad(PadPanel, null) && IsPad(PadDoors, "buttonWest") && IsPad(PadIndicateLeft, "leftShoulder") &&
                        IsPad(PadIndicateRight, "rightShoulder") && IsPad(PadImmersive, "dpad/up");
                else
                    // layout 2: the keys as the 0.8 defaults had them, the panel and the hazard lamps on no pad button yet
                    untouched = IsKey(KeyDoors, "f") && IsKey(KeyIndicateLeft, "q") && IsKey(KeyIndicateRight, "e") && IsKey(KeyHazard, "z") &&
                        IsKey(KeyLights, "l") && IsKey(KeyFlash, "k") && IsKey(KeyImmersive, "i") && IsKey(KeyPanel, "b", alt: true) &&
                        IsPad(PadPanel, null) && IsPad(PadHazard, null) && IsPad(PadDoors, "buttonWest") && IsPad(PadIndicateLeft, "leftShoulder") &&
                        IsPad(PadIndicateRight, "rightShoulder") && IsPad(PadLights, "dpad/left") && IsPad(PadFlash, "dpad/right") && IsPad(PadImmersive, "dpad/up");
                if (untouched)
                    ResetKeyBindings();
                Mod.log.Info(untouched
                    ? "Keys: the earlier default layout, moved to the new one (doors F, indicators Q / E, lights L / K, hazard Z; on the pad R3 + square for the panel, R3 + D-pad right for the hazard lamps)."
                    : "Keys: customised or already the new layout, kept as they are (the Reset keys button gives the new layout).");
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn($"Keys: the layout check failed ({ex.GetType().Name}: {ex.Message}); the keys stay as they are.");
            }

            KeyLayout = kKeyLayout;
            ApplyAndSave();
        }

        // A keyboard binding on this key alone (alt: with Alt held).
        private static bool IsKey(ProxyBinding binding, string key, bool alt = false)
        {
            if (!string.Equals(binding.path, "<Keyboard>/" + key, System.StringComparison.OrdinalIgnoreCase))
                return false;
            int modifiers = binding.modifiers == null ? 0 : binding.modifiers.Count;
            if (!alt)
                return modifiers == 0;
            if (modifiers != 1)
                return false;
            foreach (ProxyModifier modifier in binding.modifiers)
                return (modifier.m_Path ?? "").ToLowerInvariant().Contains("alt");
            return false;
        }

        // A pad binding on this control ("buttonWest"), or none (null).
        private static bool IsPad(ProxyBinding binding, string control)
        {
            string path = binding.path ?? "";
            if (control == null)
                return path.Length == 0;
            return path.EndsWith("/" + control, System.StringComparison.OrdinalIgnoreCase);
        }

        internal IEnumerable<string> RequiredLocaleKeys()
        {
            yield return GetSettingsLocaleID();
            foreach (string tab in new[] { kGeneralTab, kKeysTab })
                yield return GetOptionTabLocaleID(tab);
            foreach (string group in new[] { kCabGroup, kAnnounceGroup, kGamepadGroup, kGuidanceGroup, kKeysGroup })
                yield return GetOptionGroupLocaleID(group);
            foreach (System.Reflection.PropertyInfo property in GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (property.Name == nameof(AnnouncementsOff) || property.Name == nameof(NeuralOff) || property.Name == nameof(FeedbackOff) ||
                    property.Name == nameof(HeadlightsOff) || property.Name == nameof(MirrorsOff) || property.Name == nameof(AiBusSignsOff) || property.Name == nameof(ClassicSigns) ||
                    property.Name == nameof(KeyLayout) ||
                    property.Name == nameof(OptionsLayout))
                    continue;
                yield return GetOptionLabelLocaleID(property.Name);
                if (property.Name != nameof(Intro))
                    yield return GetOptionDescLocaleID(property.Name);
            }

            yield return GetOptionWarningLocaleID(nameof(ClearAnnounceCache));
            foreach (AnnounceVoice voice in System.Enum.GetValues(typeof(AnnounceVoice)))
                yield return GetEnumValueLocaleID(voice);

            foreach (LightMode mode in System.Enum.GetValues(typeof(LightMode)))
                yield return GetEnumValueLocaleID(mode);

            foreach (MirrorQuality quality in System.Enum.GetValues(typeof(MirrorQuality)))
                yield return GetEnumValueLocaleID(quality);

            foreach (GlassDetail detail in System.Enum.GetValues(typeof(GlassDetail)))
                yield return GetEnumValueLocaleID(detail);

            foreach (LedSignStyle style in System.Enum.GetValues(typeof(LedSignStyle)))
                yield return GetEnumValueLocaleID(style);

            yield return GetBindingMapLocaleID();
            yield return GetBindingKeyLocaleID(BusInput.kPanel);
            yield return GetBindingKeyLocaleID(BusInput.kDoors);
            yield return GetBindingKeyLocaleID(BusInput.kIndicateLeft);
            yield return GetBindingKeyLocaleID(BusInput.kIndicateRight);
            yield return GetBindingKeyLocaleID(BusInput.kImmersive);
            yield return GetBindingKeyLocaleID(BusInput.kLights);
            yield return GetBindingKeyLocaleID(BusInput.kFlash);
            yield return GetBindingKeyLocaleID(BusInput.kHazard);
        }
    }
}
