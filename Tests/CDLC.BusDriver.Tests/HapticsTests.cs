using System;
using System.Text;
using CDLC.BusDriver;

// The gamepad feedback's pure side: the DualSense output report (USB layout, the trigger effect bytes) and
// the model that turns the driving mod's readouts into rumble and trigger effects. These check the encoding against the layout
// the code is written from; they cannot show what a controller does with it (that needs the pad).
static class HapticsTests
{
    internal static void Run(Action<bool, string> check)
    {
        var motors = new DualSenseOutput { Heavy = 1f, Light = 0.5f, Right = TriggerEffect.Off, Left = TriggerEffect.Off };
        byte[] usb = DualSenseReport.Build(motors, 64);
        check(usb.Length == 64 && usb[0] == 0x02, "USB report: id 0x02, as long as the pad reports (64 here)");
        check(usb[1] == 0x03 && usb[1 + 38] == 0x04, "USB report: rumble flags, improved rumble emulation, no trigger flags");
        check(usb[1 + 2] == 128 && usb[1 + 3] == 255, "USB report: right (light) motor 128, left (heavy) motor 255");
        check(usb[1 + 10] == 0 && usb[1 + 21] == 0, "USB report: triggers untouched when not set");

        AudioHapticsFlags(check);

        byte[] usb48 = DualSenseReport.Build(motors, 48);
        check(usb48.Length == 48 && usb48[0] == 0x02 && usb48[1 + 3] == 255 && usb48[1 + 46] == 0, "USB report at the 48 bytes Windows reports for a DualSense: the whole common block fits");

        TriggerEffect rigid =TriggerEffect.Resistance(9, 8);
        check(rigid.Mode == 0x21 && rigid.P0 == 0x00 && rigid.P1 == 0x02 && rigid.P2 == 0 && rigid.P3 == 0 && rigid.P4 == 0 && rigid.P5 == 0x38,
            "resistance from the last zone: mode 0x21, zone 9, strength 8 in the top three bits of the amounts");
        TriggerEffect soft = TriggerEffect.Resistance(2, 1);
        check(soft.P0 == 0xFC && soft.P1 == 0x03 && soft.P2 == 0 && soft.P5 == 0, "resistance from zone 2: zones 2..9 active, strength 1 is amount 0");
        TriggerEffect wall = TriggerEffect.Weapon(2, 4, 8);
        check(wall.Mode == 0x25 && wall.P0 == 0x14 && wall.P1 == 0 && wall.P2 == 7, "weapon: start and end zone bits, strength 8");
        TriggerEffect buzz = TriggerEffect.Vibration(3, 4, 40);
        check(buzz.Mode == 0x26 && buzz.P8 == 40 && (buzz.P0 & 0x08) != 0 && (buzz.P0 & 0x04) == 0, "vibration: mode 0x26, frequency 40, from zone 3");
        check(TriggerEffect.Off.Mode == 0x05, "trigger off is mode 0x05");

        var triggers = new DualSenseOutput { SetTriggers = true, Right = wall, Left = buzz };
        byte[] withTriggers = DualSenseReport.Build(triggers, 48);
        check(withTriggers[1] == 0x0F, "USB report: both trigger flags set");
        check(withTriggers[1 + 10] == 0x25 && withTriggers[1 + 21] == 0x26, "USB report: right trigger effect at 10, left at 21");
        check(withTriggers[1 + 11] == 0x14 && withTriggers[1 + 22 + 8] == 40, "USB report: effect parameters follow the mode byte");

        // The model
        var model = new HapticsModel { Strength = 1f, Triggers = false };
        var parked = new DriveSample { Driving = false };
        DualSenseOutput idle = model.Compute(parked, 0.016f);
        check(idle.Heavy == 0f && idle.Light == 0f && !idle.SetTriggers, "not driving: silent, triggers left alone");

        var cruising = new DriveSample { Driving = true, SpeedMps = 12f, Throttle = 0.5f, EngineRpm = 1500f };
        DualSenseOutput run = model.Compute(cruising, 0.016f);
        check(run.Heavy > 0.05f && run.Heavy < 0.5f && run.Light > 0f && run.Light < 0.2f, "cruising: a gentle engine and road rumble");

        var braking = cruising;
        braking.AccelAlong = -8f;
        check(model.Compute(braking, 0.016f).Heavy > run.Heavy + 0.2f, "hard braking: the heavy motor jumps");

        var bump = cruising;
        bump.AccelVertical = 7f;
        check(model.Compute(bump, 0.016f).Light > run.Light + 0.3f, "a bump (vertical acceleration): the light motor jumps");

        model.Reset();
        var shifting = cruising;
        shifting.Shifting = true;
        float shiftLight = model.Compute(shifting, 0.016f).Light;
        check(shiftLight > run.Light + 0.3f, "a gear shift starts: a pulse");
        float later = 0f;
        for (int i = 0; i < 20; i++) later = model.Compute(shifting, 0.016f).Light;
        check(later < shiftLight, "the shift pulse fades while the shift goes on (one pulse per shift)");

        var strengthModel = new HapticsModel { Strength = 0f };
        DualSenseOutput none = strengthModel.Compute(braking, 0.016f);
        check(none.Heavy == 0f && none.Light == 0f, "strength 0: no rumble");

        var padModel = new HapticsModel { Strength = 1f, Triggers = true };
        DualSenseOutput pad = padModel.Compute(cruising, 0.016f);
        check(pad.SetTriggers && pad.Left.Mode == 0x21 && pad.Right.Mode == 0x21, "triggers on: L2 and R2 resistance");
        var stiff = cruising; stiff.Brake = 1f;
        check(padModel.Compute(stiff, 0.016f).Left.P2 > pad.Left.P2, "harder brake: stiffer L2");
        var held = cruising; held.ParkingBrake = true;
        check(padModel.Compute(held, 0.016f).Left.Mode == 0x25, "parking brake on: L2 is a wall");
        padModel.Reset();
        var gear = cruising; gear.Shifting = true;
        check(padModel.Compute(gear, 0.016f).Right.Mode == 0x26, "gear shift: R2 buzzes");

        Cues(check, cruising);
        SoundFeel(check, cruising);
        TriggerVibration(check, cruising);
    }

    // The triggers themselves vibrate (the option "DualSense trigger vibration"): R2 with the engine, L2 in pulses under hard braking, both
    // for a jolt over a bump, R2 through a gear shift. The encodings are checked against the layout the code is written from; what the
    // trigger motors make of them needs the pad.
    static void TriggerVibration(Action<bool, string> check, DriveSample cruising)
    {
        const string P = "trigger vibration: ";
        // amplitude of a zone from the effect's bytes (the three bits of zone 1 when the effect starts at zone 1), the frequency byte
        int Amp(TriggerEffect e, int zone) => (int)(((uint)e.P2 | (uint)e.P3 << 8 | (uint)e.P4 << 16 | (uint)e.P5 << 24) >> (3 * zone) & 7) + 1;
        DualSenseOutput Settle(HapticsModel m, DriveSample s, int frames = 30)
        {
            DualSenseOutput o = default;
            for (int i = 0; i < frames; i++) o = m.Compute(s, 0.016f);
            return o;
        }

        // off: nothing sent to the triggers
        var off = new HapticsModel { Strength = 0.8f };
        DualSenseOutput none = Settle(off, cruising);
        check(!none.SetTriggers && none.Right.Mode == 0x05 && none.Left.Mode == 0x05, P + "with the option off the triggers are left alone");

        // R2 shivers with the engine, L2 stays free
        var vib = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        DualSenseOutput run = Settle(vib, cruising);
        check(run.SetTriggers && run.Right.Mode == 0x26 && run.Left.Mode == 0x05, P + "pressing the throttle: R2 vibrates, L2 is free");
        check((run.Right.P0 & 0x02) != 0 && (run.Right.P0 & 0x01) == 0, P + "from zone 1 on (a light press already feels it)");

        // faster with the revs, stronger with the pedal
        var slow = cruising; slow.EngineRpm = 600f;
        var fast = cruising; fast.EngineRpm = 2400f;
        int fSlow = Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, slow).Right.P8;
        int fFast = Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, fast).Right.P8;
        check(fFast > fSlow + 20 && fSlow >= 24 && fFast <= 94, P + $"the frequency follows the revs ({fSlow} Hz at 600, {fFast} Hz at 2400)");
        var light = cruising; light.Throttle = 0.15f;
        var full = cruising; full.Throttle = 1f;
        int aLight = Amp(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, light).Right, 1);
        int aFull = Amp(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, full).Right, 1);
        check(aFull > aLight && aLight >= 1 && aFull <= 8, P + $"the amplitude follows the pedal ({aLight} at 15 %, {aFull} at full)");
        var idleDrive = new DriveSample { Driving = true, Throttle = 0.5f };      // standing, no revs read: a quiet shiver, not nothing
        check(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, idleDrive).Right.Mode == 0x26, P + "pressing the throttle standing still is felt too");
        var ev = cruising; ev.EngineRpm = 0f;        // an electric bus reads no revs: the speed drives the frequency
        int fEv = Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, ev).Right.P8;
        check(fEv > 24, P + $"without revs the speed drives the frequency ({fEv} Hz at 12 m/s)");

        // no pedal, no shiver
        var coast = cruising; coast.Throttle = 0f;
        check(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, coast).Right.Mode == 0x05, P + "off the throttle R2 is free");
        var touch = cruising; touch.Throttle = 0.03f;
        check(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, touch).Right.Mode == 0x05, P + "a touch under the press threshold is not a shiver");

        // the report does not change for a few revs more or less (the effect is quantised: the pad is not written at frame rate)
        var a = cruising; a.EngineRpm = 1500f;
        var b = cruising; b.EngineRpm = 1512f;
        var m1 = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        TriggerEffect e1 = Settle(m1, a).Right, e2 = Settle(m1, b).Right;
        check(e1.Mode == e2.Mode && e1.P2 == e2.P2 && e1.P8 == e2.P8, P + "12 revs more or less is the same effect");
        int changes = 0; TriggerEffect last = default; bool first = true;
        var sweep = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        for (int i = 0; i < 600; i++)
        {
            var s = cruising; s.EngineRpm = 1000f + 400f * (float)Math.Sin(i * 0.02f);
            TriggerEffect e = sweep.Compute(s, 0.016f).Right;
            if (!first && (e.P2 != last.P2 || e.P8 != last.P8)) changes++;
            last = e; first = false;
        }
        check(changes < 80, P + $"a slow sweep of the revs changes the effect {changes} times in 600 frames (not every frame)");

        // L2 pulses under a hard stop, and lets go a moment after
        var stop = cruising; stop.Throttle = 0f; stop.Brake = 0.9f; stop.AccelAlong = -6f;
        var hard = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        DualSenseOutput braking = Settle(hard, stop, 3);
        check(braking.Left.Mode == 0x26 && braking.Left.P8 == 16 && Amp(braking.Left, 1) >= 4, P + $"a hard stop: L2 pulses at 16 Hz ({Amp(braking.Left, 1)})");
        var soft = stop; soft.AccelAlong = -3.5f;
        int aSoft = Amp(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, soft, 3).Left, 1);
        var harder = stop; harder.AccelAlong = -8f;
        int aHarder = Amp(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, harder, 3).Left, 1);
        check(aHarder > aSoft, P + $"the pulses are stronger the harder the stop ({aSoft} at 3.5 m/s2, {aHarder} at 8)");
        var gentle = stop; gentle.AccelAlong = -2f;
        check(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, gentle, 3).Left.Mode == 0x05, P + "a gentle stop does not pulse");
        var pedalLight = stop; pedalLight.Brake = 0.1f;
        check(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, pedalLight, 3).Left.Mode == 0x05, P + "a hard deceleration with the brake barely touched (a steep hill, a collision) is not the brake's pulse");
        var crawl = stop; crawl.SpeedMps = 1f;
        check(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, crawl, 3).Left.Mode == 0x05, P + "no pulses crawling");
        var after = stop; after.AccelAlong = 0f; after.Brake = 0f;
        DualSenseOutput held = hard.Compute(after, 0.05f);
        check(held.Left.Mode == 0x26, P + "the pulses hold for a moment after the stop eases (no flicker)");
        DualSenseOutput over = Settle(hard, after, 20);
        check(over.Left.Mode == 0x05, P + "and are over within a fifth of a second");

        // the parking brake's wall wins on L2 when the stiffness is on
        var parked = cruising; parked.ParkingBrake = true; parked.SpeedMps = 0f; parked.Throttle = 0f;
        var wall = new HapticsModel { Strength = 0.8f, Triggers = true, TriggerVibration = true };
        check(Settle(wall, parked).Left.Mode == 0x25, P + "the parking brake's wall stays on L2 with both options on");

        // a jolt over a bump: both triggers, a tenth of a second
        var bump = cruising; bump.AccelVertical = 7f;
        var bumpy = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        DualSenseOutput jolt = bumpy.Compute(bump, 0.016f);
        check(jolt.Left.Mode == 0x26 && jolt.Left.P8 == 60 && jolt.Right.Mode == 0x26 && jolt.Right.P8 == 60 && (jolt.Left.P0 & 0x01) != 0,
            P + "a bump: both triggers jolt at 60 Hz from zone 0");
        var flat = cruising;
        for (int i = 0; i < 4; i++) bumpy.Compute(flat, 0.016f);
        check(bumpy.Compute(flat, 0.016f).Left.Mode == 0x26, P + "the jolt runs on for its tenth of a second");
        check(Settle(bumpy, flat, 10).Left.Mode == 0x05, P + "and is over after it");
        var rough = cruising; rough.AccelVertical = 7f;
        int pulses = 0; bool inPulse = false;
        var roughModel = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        for (int i = 0; i < 120; i++)
        {
            bool now = roughModel.Compute(rough, 0.016f).Left.Mode == 0x26;
            if (now && !inPulse) pulses++;
            inPulse = now;
        }
        check(pulses >= 1, P + "a road that stays rough does not freeze the jolt on (it ends and starts again)");
        var slowBump = cruising; slowBump.SpeedMps = 1f; slowBump.AccelVertical = 7f;
        check(Settle(new HapticsModel { Strength = 0.8f, TriggerVibration = true }, slowBump, 3).Left.Mode == 0x05, P + "no jolt crawling");

        // a gear shift buzzes R2, with the vibration alone as with the stiffness alone
        var shift = cruising; shift.Shifting = true;
        DualSenseOutput buzzOnly = new HapticsModel { Strength = 0.8f, TriggerVibration = true }.Compute(shift, 0.016f);
        DualSenseOutput stiffOnly = new HapticsModel { Strength = 0.8f, Triggers = true }.Compute(shift, 0.016f);
        check(buzzOnly.Right.Mode == 0x26 && buzzOnly.Right.P8 == 40 && stiffOnly.Right.Mode == 0x26 && stiffOnly.Right.P8 == 40, P + "a gear shift buzzes R2 at 40 Hz with either option");

        // the two options together: the stiffness between the events, the vibration over it
        var both = new HapticsModel { Strength = 0.8f, Triggers = true, TriggerVibration = true };
        var rest = cruising; rest.Throttle = 0f; rest.Brake = 0f;
        DualSenseOutput between = Settle(both, rest);
        check(between.Right.Mode == 0x21 && between.Left.Mode == 0x21, P + "both options: resting triggers have the stiffness");
        check(Settle(both, cruising).Right.Mode == 0x26, P + "both options: the engine's shiver is felt over the stiffness");
        var stiffRight = new HapticsModel { Strength = 0.8f, Triggers = true };
        check(Settle(stiffRight, cruising).Right.Mode == 0x21, P + "the stiffness alone is as it was (R2 resists)");

        // strength: follows the rumble strength; 0 is silent (and the stiffness stays)
        var strong = new HapticsModel { Strength = 1f, TriggerVibration = true };
        var weak = new HapticsModel { Strength = 0.3f, TriggerVibration = true };
        int aStrong = Amp(Settle(strong, full).Right, 1), aWeak = Amp(Settle(weak, full).Right, 1);
        check(aStrong > aWeak, P + $"a stronger rumble setting is a stronger shiver ({aWeak} at 30 %, {aStrong} at 100 %)");
        var silent = new HapticsModel { Strength = 0f, TriggerVibration = true, Triggers = true };
        DualSenseOutput quiet = Settle(silent, cruising);
        check(quiet.Right.Mode == 0x21 && quiet.Left.Mode == 0x21, P + "strength 0: no vibration, the stiffness stays");
        check(!Settle(new HapticsModel { Strength = 0f, TriggerVibration = true }, cruising).SetTriggers, P + "strength 0 with only the vibration on: the triggers are left alone");
        var turnedDown = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        Settle(turnedDown, cruising, 5);
        turnedDown.Strength = 0f;
        DualSenseOutput release = turnedDown.Compute(cruising, 0.016f);
        check(release.SetTriggers && release.Right.Mode == 0x05 && release.Left.Mode == 0x05, P + "the strength turned to 0 while R2 vibrates frees the triggers once");
        check(!turnedDown.Compute(cruising, 0.016f).SetTriggers, P + "and then leaves them alone");
        turnedDown.Strength = 0.8f; turnedDown.TriggerVibration = false;
        check(!turnedDown.Compute(cruising, 0.016f).SetTriggers, P + "the option switched off with nothing sent leaves them alone");
        var switchedOff = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        Settle(switchedOff, cruising, 5);
        switchedOff.TriggerVibration = false;
        check(switchedOff.Compute(cruising, 0.016f).SetTriggers, P + "the option switched off while R2 vibrates frees the triggers once");

        // not driving: nothing, and what was running is forgotten
        var driving = new HapticsModel { Strength = 0.8f, TriggerVibration = true };
        Settle(driving, stop, 3);
        DualSenseOutput parkedOut = driving.Compute(new DriveSample { Driving = false }, 0.016f);
        check(!parkedOut.SetTriggers && parkedOut.Left.Mode == 0x05 && parkedOut.Right.Mode == 0x05, P + "not driving: the triggers are left alone");
        check(driving.Compute(rest, 0.016f).Left.Mode == 0x05, P + "a pulse that was running when the driving stopped is gone, not played later");

        // what goes to the pad: the effects with no rumble flags when the audio haptics own the actuators
        var onPad = new DualSenseOutput { AudioHaptics = true, SetTriggers = true, Right = TriggerEffect.Vibration(1, 4, 60), Left = TriggerEffect.Off };
        byte[] report = DualSenseReport.Build(onPad, 48);
        check((report[1] & 0x03) == 0 && report[1 + 38] == 0 && (report[1] & 0x0C) == 0x0C && report[1 + 10] == 0x26 && report[1 + 10 + 9] == 60 && report[1 + 21] == 0x05,
            P + "the report carries the trigger effects (R2 vibration 60 Hz, L2 off) and no rumble flags");
    }

    // The DualSense has two exclusive vibration modes: rumble emulation (the flags of a report) and native audio haptics. Every report that
    // carries the rumble flags keeps it in emulation and mutes the audio haptics the driving mod plays (found in the game: no fine feel at all,
    // the flags went out on every report, motors at zero too). With the audio haptics on the actuators the flags go only with a real rumble.
    static void AudioHapticsFlags(Action<bool, string> check)
    {
        var idle = new DualSenseOutput { AudioHaptics = true };
        byte[] none = DualSenseReport.Build(idle, 48);
        check(none[1] == 0 && none[1 + 38] == 0 && none[1 + 2] == 0 && none[1 + 3] == 0, "audio haptics: an idle report carries no rumble flags (the pad stays in, or goes back to, native haptics)");
        check(!DualSenseReport.Rumbles(idle), "audio haptics: an idle report is not a rumble");

        var triggersOnly = new DualSenseOutput { AudioHaptics = true, SetTriggers = true, Right = TriggerEffect.Resistance(9, 8), Left = TriggerEffect.Off };
        byte[] trig = DualSenseReport.Build(triggersOnly, 48);
        check((trig[1] & 0x03) == 0 && trig[1 + 38] == 0 && (trig[1] & 0x0C) == 0x0C && trig[1 + 10] == 0x21,
            "audio haptics: the adaptive triggers go out without the rumble flags");

        var door = new DualSenseOutput { AudioHaptics = true, Heavy = 0.6f };
        byte[] rumble = DualSenseReport.Build(door, 48);
        check(rumble[1] == 0x03 && rumble[1 + 38] == 0x04 && rumble[1 + 3] == 153 && DualSenseReport.Rumbles(door),
            "audio haptics: a door's rumble carries the rumble flags and the motor value");

        var stop = new DualSenseOutput { AudioHaptics = true, StopMotors = true };
        byte[] stopped = DualSenseReport.Build(stop, 48);
        check(stopped[1] == 0x03 && stopped[1 + 38] == 0x04 && stopped[1 + 2] == 0 && stopped[1 + 3] == 0,
            "audio haptics: the report that ends a rumble says motors off with the flags set (the pad leaves emulation next)");

        var own = new DualSenseOutput();
        byte[] ownIdle = DualSenseReport.Build(own, 48);
        check(ownIdle[1] == 0x03 && ownIdle[1 + 38] == 0x04 && DualSenseReport.Rumbles(own),
            "without the driving mod's audio haptics the rumble flags go on every report, as before");
    }

    // The driving mod plays the engine and the road through the actuators (DrivingApi 8): the motors keep only the doors and the cues.
    static void SoundFeel(Action<bool, string> check, DriveSample cruising)
    {
        var own = new HapticsModel { Strength = 1f };
        var shared = new HapticsModel { Strength = 1f, SoundFeel = true };
        DualSenseOutput a = own.Compute(cruising, 0.016f), b = shared.Compute(cruising, 0.016f);
        check(a.Heavy > 0.05f && b.Heavy == 0f && b.Light == 0f, "sound feel on: the engine and road rumble leave the motors to the actuators");
        var braking = cruising; braking.AccelAlong = -8f; braking.AccelVertical = 7f; braking.Shifting = true;
        DualSenseOutput c = shared.Compute(braking, 0.016f);
        check(c.Heavy == 0f && c.Light == 0f, "sound feel on: hard braking, bumps and gear shifts are the actuators' too");
        var doors = cruising; doors.DoorsMoving = true;
        DualSenseOutput d = shared.Compute(doors, 0.016f);
        check(d.Heavy > 0.3f, "sound feel on: the doors still move the motors (the driving mod does not make that)");
        var cued = new HapticsModel { Strength = 1f, SoundFeel = true };
        cued.Cue(HapticCue.Hazard);
        DualSenseOutput e = cued.Compute(cruising, 0.016f);
        check(e.Heavy > 0.2f, "sound feel on: the clicks of the driver's controls still do");
        var padModel = new HapticsModel { Strength = 1f, Triggers = true, SoundFeel = true };
        check(padModel.Compute(cruising, 0.016f).SetTriggers, "sound feel on: the adaptive triggers are untouched");
    }

    // The click of an operated control (the indicator stalk, the light switch, the hazard button, the flash).
    static void Cues(Action<bool, string> check, DriveSample cruising)
    {
        var model = new HapticsModel { Strength = 1f };
        DualSenseOutput quiet = model.Compute(cruising, 0.016f);
        foreach (HapticCue cue in Enum.GetValues(typeof(HapticCue)))
        {
            model.Reset();
            model.Cue(cue);
            DualSenseOutput click = model.Compute(cruising, 0.016f);
            check(click.Heavy + click.Light > quiet.Heavy + quiet.Light + 0.15f, "cue " + cue + ": the motors click above the engine and road");
            DualSenseOutput after = default;
            for (int i = 0; i < 20; i++) after = model.Compute(cruising, 0.016f);      // 0.32 s later
            check(Math.Abs(after.Heavy - quiet.Heavy) < 1e-4f && Math.Abs(after.Light - quiet.Light) < 1e-4f, "cue " + cue + ": over within a third of a second, nothing left");
        }

        // A hazard button is firmer than an indicator stalk; the light switch has a heavier detent than the flash.
        var hazard = new HapticsModel { Strength = 1f }; hazard.Cue(HapticCue.Hazard);
        var stalk = new HapticsModel { Strength = 1f }; stalk.Cue(HapticCue.Indicator);
        var lamps = new HapticsModel { Strength = 1f }; lamps.Cue(HapticCue.LightSwitch);
        var flash = new HapticsModel { Strength = 1f }; flash.Cue(HapticCue.Flash);
        DualSenseOutput h = hazard.Compute(cruising, 0.016f), s = stalk.Compute(cruising, 0.016f), l = lamps.Compute(cruising, 0.016f), f = flash.Compute(cruising, 0.016f);
        check(h.Heavy > s.Heavy && l.Heavy > f.Heavy, "cues: the hazard button and the light switch have a heavier detent than the stalk and the flash");

        // A stronger cue still running is not cut short by a weaker one.
        var mixed = new HapticsModel { Strength = 1f };
        mixed.Cue(HapticCue.Hazard);
        mixed.Cue(HapticCue.Flash);
        DualSenseOutput kept = mixed.Compute(cruising, 0.016f);
        check(kept.Heavy >= h.Heavy - 1e-4f, "cues: a weak cue does not cut a strong one short");

        // Strength scales the click, 0 silences it; not driving drops it.
        var off = new HapticsModel { Strength = 0f }; off.Cue(HapticCue.Hazard);
        DualSenseOutput none = off.Compute(cruising, 0.016f);
        check(none.Heavy == 0f && none.Light == 0f, "cues: strength 0 is silent");
        var half = new HapticsModel { Strength = 0.5f }; half.Cue(HapticCue.Hazard);
        DualSenseOutput soft = half.Compute(cruising, 0.016f);
        var full = new HapticsModel { Strength = 1f }; full.Cue(HapticCue.Hazard);
        DualSenseOutput hard = full.Compute(cruising, 0.016f);
        check(soft.Heavy + soft.Light < hard.Heavy + hard.Light, "cues: a lower strength is a softer click");
        var dropped = new HapticsModel { Strength = 1f }; dropped.Cue(HapticCue.Hazard);
        dropped.Compute(new DriveSample { Driving = false }, 0.016f);
        DualSenseOutput afterStop = dropped.Compute(cruising, 0.016f);
        check(Math.Abs(afterStop.Heavy - quiet.Heavy) < 1e-4f, "cues: a cue raised and not felt before the driving stopped is gone, not played later");
    }
}

// The Windows side, on whatever HID devices this machine has: the struct layouts and calls must work; with no DualSense plugged in
// the device must come back as "not found" without throwing.
static class HidSmokeTests
{
    internal static void Run(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var paths = HidNative.Interfaces();
        int parsed = 0, sony = 0;
        foreach (string path in paths)
        {
            if (!HidNative.Query(path, out HidNative.Attributes attributes, out HidNative.Caps caps))
                continue;
            if (caps.UsagePage != 0)
                parsed++;
            if (attributes.VendorId == 0x054C)
                sony++;
        }

        Console.WriteLine($"  HID: {paths.Count} interfaces, {parsed} with readable report sizes, {sony} Sony");
        check(paths.Count == 0 || parsed > 0, "HID enumeration reads ids and report sizes of at least one interface");

        DualSenseDevice device = DualSenseDevice.Open(out string reason);
        check(device != null || !string.IsNullOrEmpty(reason), "opening a DualSense gives a device or a reason");
        if (device != null)
        {
            Console.WriteLine("  DualSense found (USB)");
            device.Dispose();
        }
        else
        {
            Console.WriteLine($"  no DualSense: {reason}");
        }
    }
}

// dotnet run -c Release -- dualsense: drives a connected DualSense through the same DualSenseDevice the mod uses, step by step,
// and says what you should feel. No game needed. USB only.
static class DualSenseRun
{
    internal static int Run()
    {
        DualSenseDevice device = DualSenseDevice.Open(out string reason);
        if (device == null)
        {
            Console.WriteLine("No DualSense could be opened: " + reason);
            return 2;
        }

        Console.WriteLine($"DualSense opened over USB: {device.Path}");
        var off = new DualSenseOutput { SetTriggers = true, Right = TriggerEffect.Off, Left = TriggerEffect.Off };
        try
        {
            Step(device, "1/6 heavy motor (left grip) for 1 s", new DualSenseOutput { Heavy = 0.8f, Right = TriggerEffect.Off, Left = TriggerEffect.Off }, 1000);
            Step(device, "2/6 light motor (right grip) for 1 s", new DualSenseOutput { Light = 0.8f, Right = TriggerEffect.Off, Left = TriggerEffect.Off }, 1000);
            Step(device, "3/6 L2 resistance, strength 6: press L2 slowly for 4 s (it should push back from the start)", new DualSenseOutput { SetTriggers = true, Left = TriggerEffect.Resistance(1, 6), Right = TriggerEffect.Off }, 4000);
            Step(device, "4/6 R2 resistance, strength 6: press R2 for 4 s", new DualSenseOutput { SetTriggers = true, Left = TriggerEffect.Off, Right = TriggerEffect.Resistance(1, 6) }, 4000);
            Step(device, "5/6 L2 wall (the parking brake): press L2 for 4 s (stiff, then it gives way)", new DualSenseOutput { SetTriggers = true, Left = TriggerEffect.Weapon(2, 4, 8), Right = TriggerEffect.Off }, 4000);
            Step(device, "6/6 R2 buzz (a gear shift): press R2 for 4 s", new DualSenseOutput { SetTriggers = true, Left = TriggerEffect.Off, Right = TriggerEffect.Vibration(3, 6, 40) }, 4000);
        }
        finally
        {
            device.Submit(off);
            System.Threading.Thread.Sleep(200);
            Console.WriteLine(device.Failed ? "The writes FAILED (the controller refused them)." : "Done; everything off.");
            bool failed = device.Failed;
            device.Dispose();
            if (failed) Environment.ExitCode = 3;
        }

        return Environment.ExitCode;
    }

    static void Step(DualSenseDevice device, string what, DualSenseOutput output, int ms)
    {
        Console.WriteLine("  " + what);
        device.Submit(output);
        System.Threading.Thread.Sleep(ms);
        device.Submit(new DualSenseOutput { SetTriggers = true, Right = TriggerEffect.Off, Left = TriggerEffect.Off });
        System.Threading.Thread.Sleep(400);
    }
}
