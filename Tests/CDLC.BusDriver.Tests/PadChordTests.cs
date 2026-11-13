using System;
using System.Collections.Generic;
using CDLC.BusDriver;

// The pad's buttons read from the device (the user's report: LB, RB and the D-pad did nothing - the game's own actions only work while
// its control scheme is the pad's, and any key or mouse movement switches it): a plain button, a combination that holds R3, and the layer
// rule (R3 + square is not also the doors on square). The Unity side (GamepadButtons) needs the game.
static class PadChordTests
{
    const string P = "pad chords: ";

    sealed class FakePad : IPadButtons
    {
        internal readonly HashSet<string> Held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Down(string control) => Held.Contains(control);
        public bool Pressed(string control) => Fresh.Contains(control);
        internal void Press(params string[] controls) { foreach (string c in controls) { Held.Add(c); Fresh.Add(c); } }
        internal void NextFrame() => Fresh.Clear();
        internal void Release(params string[] controls) { foreach (string c in controls) Held.Remove(c); }
    }

    internal static void Run(Action<bool, string> check)
    {
        var indicate = new[] { new PadBind("leftShoulder", null, false) };
        var lights = new[] { new PadBind("dpad/left", null, false) };
        var panel = new[] { new PadBind("buttonWest", new[] { "rightStickPress" }, false) };
        var doors = new[] { new PadBind("buttonWest", null, false) };
        var hazard = new[] { new PadBind("dpad/right", new[] { "rightStickPress" }, false) };
        var flash = new[] { new PadBind("dpad/right", null, false) };

        // LB and the D-pad on their own: the controls that did nothing
        var pad = new FakePad();
        pad.Press("leftShoulder");
        check(PadChord.Pressed(indicate, pad) && PadChord.Held(indicate, pad), P + "LB is the left indicator");
        pad = new FakePad();
        pad.Press("dpad/left");
        check(PadChord.Pressed(lights, pad) && !PadChord.Pressed(indicate, pad), P + "D-pad left is the lights, not the indicator");
        pad.NextFrame();
        check(!PadChord.Pressed(lights, pad) && PadChord.Held(lights, pad), P + "held after the frame it went down, no longer 'pressed'");
        pad.Release("dpad/left");
        check(!PadChord.Held(lights, pad), P + "released");

        // the high beam is held while the D-pad right is
        pad = new FakePad();
        pad.Press("dpad/right");
        check(PadChord.Held(flash, pad) && !PadChord.Pressed(hazard, pad), P + "D-pad right alone is the flash, not the hazard lamps");

        // hazard lamps: R3 held, then D-pad right; and then not also the flash
        pad = new FakePad();
        pad.Press("rightStickPress");
        pad.NextFrame();
        pad.Press("dpad/right");
        check(PadChord.Pressed(hazard, pad) && PadChord.Held(hazard, pad), P + "R3 held, then D-pad right: the hazard lamps");
        check(!PadChord.Pressed(flash, pad) && !PadChord.Held(flash, pad), P + "and not also the high beam (the plain action is denied while R3 is down)");

        // the panel: R3 + square, and the doors stay out of it
        pad = new FakePad();
        pad.Press("rightStickPress");
        pad.NextFrame();
        pad.Press("buttonWest");
        check(PadChord.Pressed(panel, pad) && !PadChord.Pressed(doors, pad), P + "R3 held, then square: the panel, not the doors");
        pad = new FakePad();
        pad.Press("buttonWest");
        check(PadChord.Pressed(doors, pad) && !PadChord.Pressed(panel, pad), P + "square alone: the doors, not the panel");

        // the modifier pressed after the button is not the combination
        pad = new FakePad();
        pad.Press("buttonWest");
        pad.NextFrame();
        pad.Press("rightStickPress");
        check(!PadChord.Pressed(panel, pad), P + "R3 pressed after the button does not fire the combination");

        // L3 is a layer button too (it denies the plain actions) and is not R3
        pad = new FakePad();
        pad.Press("leftStickPress");
        pad.NextFrame();
        pad.Press("leftShoulder");
        check(!PadChord.Pressed(indicate, pad), P + "with L3 down the plain button does not fire either");

        // a binding that ignores modifiers fires whatever layer button is down
        var ignoring = new[] { new PadBind("buttonEast", null, true) };
        pad = new FakePad();
        pad.Press("rightStickPress");
        pad.NextFrame();
        pad.Press("buttonEast");
        check(PadChord.Pressed(ignoring, pad), P + "a binding that ignores modifiers fires with R3 down");

        // an action with two bindings, an unset one, no pad, no bindings
        var two = new[] { new PadBind("dpad/up", null, false), new PadBind("buttonNorth", new[] { "rightStickPress" }, false), new PadBind("", null, false) };
        pad = new FakePad();
        pad.Press("dpad/up");
        check(PadChord.Pressed(two, pad), P + "any of an action's bindings fires it");
        check(!PadChord.Pressed(two, null) && !PadChord.Held(two, null) && !PadChord.Pressed((IReadOnlyList<PadBind>)null, pad), P + "no pad and no bindings are no press");
        check(!new PadBind("", null, false).IsSet && panel[0].IsCombo && !doors[0].IsCombo, P + "an empty binding is not set; a binding with a modifier is a combination");

        // names from binding paths, and the string the hints take
        check(PadChord.ControlOf("<Gamepad>/dpad/up") == "dpad/up" && PadChord.ControlOf("<DualShockGamepad>/buttonWest") == "buttonWest" &&
            PadChord.ControlOf("") == "" && PadChord.ControlOf(null) == "", P + "control names from binding paths");
        check(PadChord.Join(new[] { "<Gamepad>/rightStickPress", "<Gamepad>/buttonWest" }) == "<Gamepad>/rightStickPress+<Gamepad>/buttonWest" &&
            PadChord.Join(new[] { "<Gamepad>/dpad/up" }) == "<Gamepad>/dpad/up", P + "a combination is its paths joined with '+', the modifiers first");
    }
}
