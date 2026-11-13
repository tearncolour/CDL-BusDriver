using System.Collections.Generic;
using CDLC.BusDriver.Interior.Models;
using CDLC.InputTracing;
using Game.Input;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Keys through the game's own input system (Options -> Bus Driver -> Keys). The panel key works anywhere in game; the
    /// indicator, hazard and head lamp keys while the player drives a bus, the door and immersive keys only during a shift, when
    /// CSL Driving Life Core has paused the camera keys (E and Q are camera rotation otherwise). Tap the door key to open or
    /// close; hold it to close at once without waiting for people still walking up. The indicator keys flip the stalk: left or
    /// right on, the same key again off; the hazard key flips the warning lamps; the light key steps the head lamp switch and the
    /// flash key gives the main beam while it is held.
    /// </summary>
    internal static class BusInput
    {
        internal const string kPanel = "Panel";
        internal const string kDoors = "Doors";
        internal const string kIndicateLeft = "IndicateLeft";
        internal const string kIndicateRight = "IndicateRight";
        internal const string kImmersive = "Immersive";
        internal const string kLights = "Lights";
        internal const string kFlash = "FlashBeam";
        internal const string kHazard = "Hazard";
        private const float kHoldSeconds = 0.8f;

        private static readonly Dictionary<string, ProxyAction> s_Actions = new Dictionary<string, ProxyAction>();
        private static bool s_InGame;
        private static bool s_Driving;
        private static bool s_Shift;
        private static bool s_StateSet;
        private static float s_DoorDown = -1f;
        private static bool s_HoldFired;

        internal static void Initialize(BusSettings settings)
        {
            InputTrace.Install("BusDriver", Mod.kVersion, text => Mod.log.Info(text), null);
            s_Actions.Clear();
            foreach (string name in new[] { kPanel, kDoors, kIndicateLeft, kIndicateRight, kImmersive, kLights, kFlash, kHazard })
            {
                try
                {
                    ProxyAction action = settings.GetAction(name);
                    if (action != null)
                        s_Actions[name] = action;
                }
                catch (System.Exception ex)
                {
                    Mod.log.Warn($"Key action {name} is not available: {ex.Message}");
                }
            }

            s_StateSet = false;
            SetState(false, false, false);
        }

        internal static void Dispose()
        {
            SetState(false, false, false);
            s_Actions.Clear();
        }

        /// <summary>
        /// What the keys may do now: the panel key in game; the indicator, hazard and head lamp keys while the player drives a bus
        /// (driving); the door and immersive keys during a shift (shift). The stalk and the hazard switch fall back when the driving
        /// ends, not when a shift starts or ends.
        /// </summary>
        internal static void SetState(bool inGame, bool driving, bool shift)
        {
            driving &= inGame;
            shift &= inGame;
            if (s_StateSet && inGame == s_InGame && driving == s_Driving && shift == s_Shift)
                return;

            s_StateSet = true;
            s_InGame = inGame;
            s_Driving = driving;
            s_Shift = shift;
            SetEnabled(kPanel, inGame);
            SetEnabled(kDoors, shift);
            SetEnabled(kImmersive, shift);
            foreach (string name in s_DriveActions)
                SetEnabled(name, driving);
            s_DoorDown = -1f;
            if (!driving)
            {
                s_Indicate = 0;
                s_Hazard = false;
            }
            if (driving || shift)
                BlockSharedShortcuts(driving, shift);
            else
                ReleaseSharedShortcuts();
        }

        // The keys that run while driving, and those that run on a shift (not the panel key, which works everywhere): game shortcuts
        // on the same keys are paused while the keys are live.
        private static readonly string[] s_DriveActions = { kIndicateLeft, kIndicateRight, kHazard, kLights, kFlash };
        private static readonly string[] s_ShiftActions = { kDoors, kImmersive };
        private static readonly string[] s_SharedKeyMaps = { InputManager.kShortcutsMap, InputManager.kToolMap };
        // The pad also has camera actions on its buttons (the right stick's free look is not in this list of controls: it is not ours).
        private static readonly string[] s_SharedPadMaps = { InputManager.kCameraMap, InputManager.kShortcutsMap, InputManager.kToolMap };
        private static InputBarrier s_SharedKeys;
        private static InputBarrier s_SharedPad;

        /// <summary>
        /// Pauses the game actions (no modifier) bound to the same key or pad button as one of our live controls, e.g. a game shortcut
        /// on I while I is the immersive mode key, or a game action on the X button while X is the doors: the game's action took the
        /// press first (or both fired) and ours never worked right. The keyboard and the pad are each their own barrier. Worked out
        /// when the controls go live, so rebinding either side works; logged.
        /// </summary>
        private static void BlockSharedShortcuts(bool driving, bool shift)
        {
            ReleaseSharedShortcuts();
            try
            {
                var keyPaths = new HashSet<string>();
                var padPaths = new HashSet<string>();
                var live = new List<string>();
                if (driving) live.AddRange(s_DriveActions);
                if (shift) live.AddRange(s_ShiftActions);
                foreach (string name in live)
                    if (s_Actions.TryGetValue(name, out ProxyAction own))
                        foreach (ProxyBinding binding in own.bindings)
                        {
                            if (string.IsNullOrEmpty(binding.path))
                                continue;
                            if (binding.isKeyboard)
                                keyPaths.Add(binding.path);
                            else if (binding.isGamepad)
                                padPaths.Add(binding.path);
                        }

                if (InputManager.instance == null)
                    return;
                s_SharedKeys = Pause("CDLC.BusDriver.SharedKeys", "keys", keyPaths, s_SharedKeyMaps, InputManager.DeviceType.Keyboard);
                s_SharedPad = Pause("CDLC.BusDriver.SharedPad", "pad buttons", padPaths, s_SharedPadMaps, InputManager.DeviceType.Gamepad);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn($"Keys: the game actions on the same controls could not be paused: {ex.Message}");
            }
        }

        // One barrier over the game actions of the maps that sit unmodified on one of the paths (null when there is none).
        private static InputBarrier Pause(string barrier, string what, HashSet<string> paths, string[] mapNames, InputManager.DeviceType device)
        {
            if (paths.Count == 0)
                return null;

            bool pad = device == InputManager.DeviceType.Gamepad;
            var blocked = new List<ProxyAction>();
            var names = new List<string>();
            foreach (string mapName in mapNames)
            {
                ProxyActionMap map = InputManager.instance.FindActionMap(mapName);
                if (map == null)
                    continue;
                foreach (ProxyAction action in map.actions.Values)
                    foreach (ProxyBinding binding in action.bindings)
                        if ((pad ? binding.isGamepad : binding.isKeyboard) && !string.IsNullOrEmpty(binding.path) && paths.Contains(binding.path) && binding.modifiers.Count == 0)
                        {
                            blocked.Add(action);
                            names.Add(mapName + "/" + action.name + " (" + binding.path + ")");
                            break;
                        }
            }

            Mod.log.Info($"Live {what} {string.Join(", ", paths)}; game actions paused while they are live: {(names.Count > 0 ? string.Join(", ", names) : "none")}.");
            return blocked.Count > 0 ? InputTrace.Create(barrier, blocked, device, true) : null;
        }

        private static void ReleaseSharedShortcuts()
        {
            Release(ref s_SharedKeys);
            Release(ref s_SharedPad);
        }

        private static void Release(ref InputBarrier barrier)
        {
            if (barrier == null)
                return;
            try
            {
                InputTrace.Release(barrier);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn($"Keys: the paused game actions could not be released: {ex.Message}");
            }
            barrier = null;
        }

        private static int s_Indicate;

        private static bool s_Hazard;

        /// <summary>The indicator stalk the driver set (-1 left, 0 off, +1 right): the keys flip it, Cancel puts it back.</summary>
        internal static int Indicator => s_Indicate;

        /// <summary>The hazard warning lamps are on (the hazard key flips them; they stay on until it is pressed again).</summary>
        internal static bool Hazard => s_Hazard;

        /// <summary>The stalk springs back (after a turn) or the driving ends.</summary>
        internal static void CancelIndicator() => s_Indicate = 0;

        /// <summary>The hazard lamps go off (another bus is taken).</summary>
        internal static void CancelHazard() => s_Hazard = false;

        // The frame in which both indicator controls were pressed together: the hazard chord (LB + RB on the pad, Q + E on the keyboard).
        private static int s_ChordFrame = -1;

        /// <summary>
        /// Per frame, after PollIndicators: the hazard key, or both indicator keys (buttons) pressed together. Returns true when the
        /// lamps were switched.
        /// </summary>
        internal static bool PollHazard()
        {
            if (!s_Driving || !(WasPressed(kHazard) || s_ChordFrame == Time.frameCount))
                return false;
            s_Hazard = !s_Hazard;
            return true;
        }

        /// <summary>Per frame: the head lamp switch key was pressed (while driving).</summary>
        internal static bool LightsPressed() => s_Driving && WasPressed(kLights);

        /// <summary>Per frame: the flash key is held (while driving): the main beam for as long as it is.</summary>
        internal static bool FlashHeld() => s_Driving && IsPressed(kFlash);

        /// <summary>Per frame: the indicator keys. Returns true when the stalk moved.</summary>
        internal static bool PollIndicators()
        {
            if (!s_Driving)
                return false;
            int was = s_Indicate;
            s_Indicate = Stalk.Press(s_Indicate, WasPressed(kIndicateLeft), WasPressed(kIndicateRight), out bool chord);
            if (chord)
                s_ChordFrame = Time.frameCount;
            return s_Indicate != was;
        }

        /// <summary>Per frame: the immersive mode key was pressed (during a shift).</summary>
        internal static bool PollImmersive()
        {
            if (!s_Shift || !s_Actions.TryGetValue(kImmersive, out ProxyAction action))
                return false;
            if (!action.enabled && !s_ImmersiveDisabledLogged)
            {
                s_ImmersiveDisabledLogged = true;
                Mod.log.Warn("The immersive mode key is not enabled during the shift (the game's input system did not switch it on).");
            }
            return action.enabled && action.WasPressedThisFrame() || PadPressed(kImmersive);
        }

        private static bool s_ImmersiveDisabledLogged;

        // ---------------------------------------------------------------- the pad's buttons, read from the device
        // The game's own actions only come through on the pad while its control scheme is the pad's, and any key or mouse movement
        // switches it to the keyboard (the key hints flipped up to three times in 200 ms with a pad in the hands): a pad button pressed
        // then was lost, and so was the first one after the switch back (the user's report: LB, RB and the D-pad did nothing). So the
        // pad's bindings of our actions (the options page still decides which; combinations hold R3 or L3 with a button) are also
        // read from the device (PadChord). Either path firing is the same press: both read this frame's state.
        private static readonly GamepadButtons s_Buttons = new GamepadButtons();
        private static readonly Dictionary<string, PadBind[]> s_PadBinds = new Dictionary<string, PadBind[]>();
        private static float s_PadBindsTime = -10f;
        private static int s_PadFrame = -1;
        private static bool s_PadReady;

        private static bool PadLive(string name)
        {
            if (!s_Actions.ContainsKey(name))
                return false;
            int frame = Time.frameCount;
            if (frame != s_PadFrame)
            {
                s_PadFrame = frame;
                // not while a platform overlay is up, a text field has the focus, or the game is paused by a menu (the D-pad
                // moves around in the menu then, and the game's own actions already stand still)
                bool blocked;
                try { blocked = InputManager.instance == null || InputManager.instance.overlayActive || InputManager.instance.hasInputFieldFocus || Time.timeScale <= 0f; }
                catch (System.Exception) { blocked = true; }
                s_PadReady = !blocked && s_Buttons.Refresh();
            }

            if (!s_PadReady)
                return false;
            return name == kPanel ? s_InGame : System.Array.IndexOf(s_ShiftActions, name) >= 0 ? s_Shift : s_Driving;
        }

        /// <summary>The pad bindings of an action as the options page has them now (looked up again every second: the player may rebind).</summary>
        private static PadBind[] PadBindsOf(string name)
        {
            float now = Time.unscaledTime;
            if (now - s_PadBindsTime > 1f || now < s_PadBindsTime)
            {
                s_PadBinds.Clear();
                s_PadBindsTime = now;
            }

            if (s_PadBinds.TryGetValue(name, out PadBind[] known))
                return known;

            var binds = new List<PadBind>();
            if (s_Actions.TryGetValue(name, out ProxyAction action))
                foreach (ProxyBinding binding in action.bindings)
                {
                    if (!binding.isGamepad || string.IsNullOrEmpty(binding.path))
                        continue;
                    var modifiers = new List<string>();
                    foreach (ProxyModifier modifier in binding.modifiers)
                    {
                        string control = PadChord.ControlOf(modifier.m_Path);
                        if (control.Length > 0)
                            modifiers.Add(control);
                    }

                    binds.Add(new PadBind(PadChord.ControlOf(binding.path), modifiers.ToArray(), binding.ignoreModifiers));
                }

            PadBind[] result = binds.ToArray();
            s_PadBinds[name] = result;
            return result;
        }

        private static bool PadHeld(string name) => PadLive(name) && PadChord.Held(PadBindsOf(name), s_Buttons);

        private static bool PadPressed(string name) => PadLive(name) && PadChord.Pressed(PadBindsOf(name), s_Buttons);

        /// <summary>Per frame: panel toggle and the door key (tap = open/close, hold = close now).</summary>
        internal static void Poll(out bool togglePanel, out ShiftRequest doorRequest)
        {
            togglePanel = WasPressed(kPanel);
            doorRequest = ShiftRequest.None;
            if (!s_Shift)
                return;

            float now = Time.unscaledTime;
            if (WasPressed(kDoors))
            {
                s_DoorDown = now;
                s_HoldFired = false;
            }

            if (s_DoorDown >= 0f && IsPressed(kDoors) && !s_HoldFired && now - s_DoorDown >= kHoldSeconds && BusState.DoorsOpen)
            {
                s_HoldFired = true;
                doorRequest = ShiftRequest.ForceClose;
            }

            if (s_DoorDown >= 0f && !IsPressed(kDoors))
            {
                if (!s_HoldFired)
                    doorRequest = ShiftRequest.ToggleDoors;
                s_DoorDown = -1f;
            }
        }

        /// <summary>
        /// What the panel and the hints call the action's control: the pad's button (as the pad in hand names it: A or the cross)
        /// while the player uses a pad, the keyboard key otherwise. "" for a pad action without a pad button.
        /// </summary>
        internal static string Display(string name) => Core.PadActive ? DisplayPad(name) : DisplayKeyboard(name);

        /// <summary>The action's pad control paths for the driving mod's hint rows (it names them for the pad in hand); "" if none.</summary>
        internal static string PadControls(string name)
        {
            if (!s_Actions.TryGetValue(name, out ProxyAction action))
                return "";

            foreach (ProxyBinding binding in action.bindings)
                if (binding.isGamepad && !string.IsNullOrEmpty(binding.path))
                    return PadCombo(binding);

            return "";
        }

        /// <summary>A pad binding as the driving mod names it: the modifiers first, joined with "+" ("&lt;Gamepad&gt;/rightStickPress+&lt;Gamepad&gt;/buttonWest").</summary>
        private static string PadCombo(ProxyBinding binding)
        {
            var paths = new List<string>();
            foreach (ProxyModifier modifier in binding.modifiers)
                if (!string.IsNullOrEmpty(modifier.m_Path))
                    paths.Add(modifier.m_Path);
            paths.Add(binding.path);
            return PadChord.Join(paths);
        }

        /// <summary>Two actions on one hint row ("Q / E"): both names, or the one that has a name; "" if neither.</summary>
        internal static string Pair(string first, string second) =>
            first.Length > 0 && second.Length > 0 ? first + " / " + second : first.Length > 0 ? first : second;

        /// <summary>
        /// The pad's controls for the hazard lamps: its own button when one is bound, else both indicator buttons (pressed together
        /// they switch the hazard lamps, see Stalk.Press); "" if the pad has neither.
        /// </summary>
        internal static string HazardPadControls()
        {
            string own = PadControls(kHazard);
            return own.Length > 0 ? own : Pair(PadControls(kIndicateLeft), PadControls(kIndicateRight));
        }

        private static string DisplayPad(string name) => Core.PadName(PadControls(name));

        /// <summary>The keyboard key whatever device is in use (the hint rows carry both; the driving mod picks).</summary>
        internal static string DisplayKeyboardKey(string name) => DisplayKeyboard(name);

        private static string DisplayKeyboard(string name)
        {
            if (!s_Actions.TryGetValue(name, out ProxyAction action))
                return "";

            foreach (ProxyBinding binding in action.bindings)
            {
                if (!binding.isKeyboard || string.IsNullOrEmpty(binding.path))
                    continue;     // the keyboard key; the pad's button is shown in pad mode (DisplayPad)

                var parts = new List<string>();
                foreach (ProxyModifier modifier in binding.modifiers)
                {
                    string control = Control(modifier.m_Path).ToLowerInvariant();
                    parts.Add(control.Contains("alt") ? "Alt" : control.Contains("ctrl") || control.Contains("control") ? "Ctrl" : control.Contains("shift") ? "Shift" : control);
                }

                string key = Control(binding.path);
                parts.Add(key.Length == 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key.Substring(1));
                return string.Join("+", parts);
            }

            return "";
        }

        private static string Control(string path)
        {
            path = path ?? "";
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        private static bool WasPressed(string name) => s_Actions.TryGetValue(name, out ProxyAction action) && action.enabled && action.WasPressedThisFrame() || PadPressed(name);

        private static bool IsPressed(string name) => s_Actions.TryGetValue(name, out ProxyAction action) && action.enabled && action.IsPressed() || PadHeld(name);

        private static void SetEnabled(string name, bool enabled)
        {
            if (!s_Actions.TryGetValue(name, out ProxyAction action))
                return;

            try
            {
                InputTrace.Switch(name, enabled);
                action.shouldBeEnabled = enabled;
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn($"Key action {name} could not be switched: {ex.Message}");
            }
        }
    }
}
