using System;
using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>The pad's buttons as the chord logic reads them: by control name after the device ("buttonWest", "dpad/up", "rightStickPress").</summary>
    internal interface IPadButtons
    {
        /// <summary>The button is down.</summary>
        bool Down(string control);

        /// <summary>The button went down in this frame.</summary>
        bool Pressed(string control);
    }

    /// <summary>
    /// A pad binding: the button it is on and the modifier buttons held with it (a combination such as R3 + square). Control names are
    /// as the Input System has them after the device ("buttonWest", "dpad/up", "rightStickPress").
    /// </summary>
    internal readonly struct PadBind
    {
        internal readonly string Main;
        internal readonly string[] Modifiers;
        /// <summary>The binding fires whatever other layer buttons are down (the game's "ignore modifiers" option, e.g. the handbrake).</summary>
        internal readonly bool IgnoreModifiers;

        internal PadBind(string main, string[] modifiers, bool ignoreModifiers)
        {
            Main = main ?? "";
            Modifiers = modifiers ?? Array.Empty<string>();
            IgnoreModifiers = ignoreModifiers;
        }

        internal bool IsSet => Main.Length > 0;
        internal bool IsCombo => Modifiers.Length > 0;
    }

    /// <summary>
    /// (The same rules as CSL Driving Life Core's PadChord: this mod does not take its logic from the driving mod, which may not be there.)
    /// The pad's buttons read directly from the device, so the controls work whatever the game thinks the player is using. The game's own
    /// actions only come through while its control scheme is the pad's, and any key or mouse movement switches it to the keyboard (the
    /// key hints showed the scheme changing up to three times in 200 ms while a pad was in the hands): a button pressed then is lost, and
    /// so is the first one after the switch back. Pure rules, tested; <c>GamepadButtons</c> is the Unity side.
    /// <list type="bullet">
    /// <item>A binding is its main button with its modifier buttons held (a combination: hold R3, press the button).</item>
    /// <item>The stick presses are the layer buttons: while one is down, the bindings that do not use it do not fire (R3 + square is not
    /// also the doors on square), unless the binding ignores modifiers. This is the game's own rule for modifiers.</item>
    /// </list>
    /// </summary>
    internal static class PadChord
    {
        /// <summary>The buttons that make a combination layer.</summary>
        internal static readonly string[] LayerControls = { "leftStickPress", "rightStickPress" };

        /// <summary>"&lt;Gamepad&gt;/dpad/up" gives "dpad/up"; the device part may name a specific pad ("&lt;DualShockGamepad&gt;").</summary>
        internal static string ControlOf(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            int device = path.IndexOf(">/", StringComparison.Ordinal);
            return (device >= 0 ? path.Substring(device + 2) : path).Trim();
        }

        /// <summary>
        /// A combination as the driving mod names it for the hints: the controls' paths joined with "+", the modifiers first
        /// ("&lt;Gamepad&gt;/rightStickPress+&lt;Gamepad&gt;/buttonWest"); a single control is its own path.
        /// </summary>
        internal static string Join(IEnumerable<string> paths) => string.Join("+", paths);

        /// <summary>The binding is held now: its button and every modifier of it are down, and no layer button that is not its own is.</summary>
        internal static bool Held(in PadBind bind, IPadButtons pad)
        {
            if (!bind.IsSet || pad == null || !pad.Down(bind.Main))
                return false;
            return ModifiersRight(bind, pad);
        }

        /// <summary>The binding fired in this frame: its button went down while the modifiers (and no stray layer button) were.</summary>
        internal static bool Pressed(in PadBind bind, IPadButtons pad)
        {
            if (!bind.IsSet || pad == null || !pad.Pressed(bind.Main))
                return false;
            return ModifiersRight(bind, pad);
        }

        internal static bool Held(IReadOnlyList<PadBind> binds, IPadButtons pad)
        {
            if (binds == null)
                return false;
            for (int i = 0; i < binds.Count; i++)
                if (Held(binds[i], pad))
                    return true;
            return false;
        }

        internal static bool Pressed(IReadOnlyList<PadBind> binds, IPadButtons pad)
        {
            if (binds == null)
                return false;
            for (int i = 0; i < binds.Count; i++)
                if (Pressed(binds[i], pad))
                    return true;
            return false;
        }

        // Every modifier of the binding down; and, unless the binding ignores modifiers, none of the layer buttons that are not its own.
        private static bool ModifiersRight(in PadBind bind, IPadButtons pad)
        {
            foreach (string modifier in bind.Modifiers)
                if (!pad.Down(modifier))
                    return false;
            if (bind.IgnoreModifiers)
                return true;
            foreach (string layer in LayerControls)
                if (pad.Down(layer) && !Has(bind.Modifiers, layer))
                    return false;
            return true;
        }

        private static bool Has(string[] controls, string control)
        {
            foreach (string c in controls)
                if (string.Equals(c, control, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
