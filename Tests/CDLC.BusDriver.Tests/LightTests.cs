using System;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The driver's lights: the switch and the daylight sensor (BusLightState), the indicator and hazard flashing (Flasher), the beams
// (BeamSpec) and where the head lamps are on the built models (HeadlampFrames).
static class LightTests
{
    const string Prefix = "lights: ";

    internal static void Run(Action<bool, string> check)
    {
        Switch(check);
        Sensor(check);
        FlashKey(check);
        Flashing(check);
        Beams(check);
        Lamps(check);
    }

    static void Switch(Action<bool, string> check)
    {
        var s = new BusLightState();
        check(s.Mode == LightMode.Auto && s.Base == Beam.Off && !s.Flashing, Prefix + "a new switch is on Auto, the lamps at the daytime strip");
        LightMode[] order = { LightMode.Low, LightMode.High, LightMode.Off, LightMode.Auto, LightMode.Low };
        bool inOrder = true;
        foreach (LightMode expected in order)
            inOrder &= s.Cycle() == expected && s.Mode == expected;
        check(inOrder, Prefix + "L steps Auto, Low, High, Off, Auto, Low");

        foreach ((LightMode mode, Beam beam) in new[] { (LightMode.Low, Beam.Low), (LightMode.High, Beam.High), (LightMode.Off, Beam.Off) })
        {
            var t = new BusLightState(mode);
            foreach (float day in new[] { 0f, 0.5f, 1f })
                check(t.Update(day) == beam, Prefix + mode + " gives " + beam + " at daylight " + day);
        }

        var tails = new BusLightState(LightMode.Low);
        tails.Update(1f);
        check(tails.Tail, Prefix + "the dipped beam lights the tail lamps by day");
        tails.SetMode(LightMode.High); tails.Update(1f);
        check(tails.Tail, Prefix + "the main beam lights the tail lamps");
        tails.SetMode(LightMode.Off); tails.Update(0f);
        check(!tails.Tail, Prefix + "the switch off leaves the tail lamps dark even at night");
        tails.SetMode(LightMode.Auto); tails.Update(0f);
        check(tails.Tail && tails.Beam == Beam.Low, Prefix + "Auto at night: dipped beam and tail lamps");

        // The switch moved between two samples takes effect at once (the telltale does not wait a frame).
        var u = new BusLightState(LightMode.Off);
        u.Update(1f);
        u.Cycle();
        check(u.Base == Beam.Off && u.Mode == LightMode.Auto, Prefix + "cycling off to Auto in daylight stays dark");
        var v = new BusLightState(LightMode.Auto);
        v.Update(0f);
        v.Cycle();
        check(v.Base == Beam.Low, Prefix + "the cycle shows its new beam before the next sample");
    }

    static void Sensor(Action<bool, string> check)
    {
        var s = new BusLightState();
        // Hysteresis: on below 0.47, off above 0.53, held between.
        check(s.Update(0.49f) == Beam.Low, Prefix + "the first reading in the middle of the band counts by the midpoint: 0.49 is dark");
        check(s.Update(0.52f) == Beam.Low, Prefix + "lamps stay on at 0.52 (below the off threshold 0.53)");
        check(s.Update(0.54f) == Beam.Off, Prefix + "lamps go out above 0.53");
        check(s.Update(0.50f) == Beam.Off, Prefix + "and stay out at 0.50 (above the on threshold 0.47)");
        check(s.Update(0.46f) == Beam.Low, Prefix + "lamps come on below 0.47");
        check(s.Update(0.50f) == Beam.Low, Prefix + "and stay on back at 0.50");

        var mid = new BusLightState();
        check(mid.Update(0.51f) == Beam.Off, Prefix + "a first reading of 0.51 is light");
        var day = new BusLightState();
        check(day.Update(0.9f) == Beam.Off, Prefix + "a first reading in daylight: lamps off");
        var night = new BusLightState();
        check(night.Update(0f) == Beam.Low, Prefix + "a first reading at night: dipped beam");

        // Flutter around one threshold never flickers: a noisy reading across 0.5 changes the state at most twice.
        var flutter = new BusLightState();
        int changes = 0;
        Beam last = flutter.Update(0.60f);
        var rng = new System.Random(7);
        for (int i = 0; i < 400; i++)
        {
            float d = 0.485f + (float)(rng.NextDouble() - 0.5) * 0.04f;   // 0.465 .. 0.505
            Beam b = flutter.Update(d);
            if (b != last) { changes++; last = b; }
        }
        check(changes <= 1, Prefix + "a reading flickering across 0.47..0.53 does not make the lamps flicker (" + changes + " changes)");

        var reset = new BusLightState();
        reset.Update(0f); reset.Cycle(); reset.Flash(true);
        reset.Reset(LightMode.High);
        check(reset.Mode == LightMode.High && !reset.Flashing && reset.Base == Beam.Off, Prefix + "a new drive starts from the option's position without flash or sensor memory");
        check(reset.Update(1f) == Beam.High, Prefix + "and the switch position works at the first reading");
    }

    static void FlashKey(Action<bool, string> check)
    {
        foreach (LightMode mode in new[] { LightMode.Auto, LightMode.Low, LightMode.High, LightMode.Off })
        {
            var s = new BusLightState(mode);
            s.Update(1f);
            Beam before = s.Beam;
            s.Flash(true);
            check(s.Update(1f) == Beam.High && s.Beam == Beam.High, Prefix + "the flash key gives the main beam in " + mode);
            s.Flash(false);
            check(s.Update(1f) == before && s.Mode == mode, Prefix + "letting go brings " + mode + " back as it was (" + before + ")");
        }

        var off = new BusLightState(LightMode.Off);
        off.Update(0f);
        off.Flash(true);
        off.Update(0f);
        check(off.Beam == Beam.High && !off.Tail, Prefix + "flashing with the switch off lights no tail lamps");
        var low = new BusLightState(LightMode.Low);
        low.Update(0f);
        low.Flash(true);
        low.Update(0f);
        check(low.Tail && low.Base == Beam.Low, Prefix + "flashing from the dipped beam keeps the tail lamps");

        // Cycling while the flash is held does not lose the flash.
        var held = new BusLightState(LightMode.Low);
        held.Flash(true);
        held.Cycle();
        held.Update(0f);
        check(held.Beam == Beam.High && held.Mode == LightMode.High, Prefix + "the switch can move during a flash");
    }

    static void Flashing(Action<bool, string> check)
    {
        check(Flasher.On(0.0) && Flasher.On(0.30) && !Flasher.On(0.34) && !Flasher.On(0.60) && Flasher.On(0.67),
            Prefix + "the flash is on first, about 0.33 s on and 0.33 s off (90 a minute)");
        int ons = 0;
        for (int i = 0; i < 6000; i++)
            if (Flasher.On(i / 100.0) && !Flasher.On((i - 1) / 100.0)) ons++;
        check(ons >= 89 && ons <= 91, Prefix + "ninety flashes in a minute (" + ons + ")");
        check(Flasher.Hz * 60f >= 60f && Flasher.Hz * 60f <= 120f, Prefix + "the rate is inside UN R48's 60 to 120 a minute");

        check(DrivingSignals.Left == 1 && DrivingSignals.Right == 2 && DrivingSignals.Hazard == 3, Prefix + "the driving mod's signal bits are left 1, right 2, hazard 3 (DrivingApi 7)");
        check(!Flasher.Flashing(0, false) && Flasher.Flashing(-1, false) && Flasher.Flashing(1, false) && Flasher.Flashing(0, true) && Flasher.Flashing(1, true),
            Prefix + "the lamps flash for the stalk or the hazard key");

        // The stalk under the keys or buttons: a side on, the same again off, the other over; both together is the hazard chord.
        check(Stalk.Press(0, true, false, out bool chord) == -1 && !chord, Prefix + "stalk: left on");
        check(Stalk.Press(-1, true, false, out chord) == 0 && !chord, Prefix + "stalk: left again, off");
        check(Stalk.Press(-1, false, true, out chord) == 1 && !chord, Prefix + "stalk: right switches over from left");
        check(Stalk.Press(1, false, true, out chord) == 0 && !chord, Prefix + "stalk: right again, off");
        check(Stalk.Press(1, true, false, out chord) == -1 && !chord, Prefix + "stalk: left switches over from right");
        check(Stalk.Press(1, false, false, out chord) == 1 && !chord && Stalk.Press(-1, false, false, out chord) == -1 && Stalk.Press(0, false, false, out chord) == 0 && !chord,
            Prefix + "stalk: nothing pressed, nothing moves");
        foreach (int stalk in new[] { -1, 0, 1 })
        {
            check(Stalk.Press(stalk, true, true, out chord) == stalk && chord, Prefix + "stalk: both together (LB + RB, Q + E) leave the stalk at " + stalk + " and ask for the hazard lamps");
        }

        foreach (bool on in new[] { true, false })
        {
            Flasher.Sides(-1, false, on, out bool l, out bool r);
            check(l == on && !r, Prefix + "stalk left lights the left side only (flash " + (on ? "on" : "off") + ")");
            Flasher.Sides(1, false, on, out l, out r);
            check(!l && r == on, Prefix + "stalk right lights the right side only (flash " + (on ? "on" : "off") + ")");
            Flasher.Sides(0, false, on, out l, out r);
            check(!l && !r, Prefix + "no stalk and no hazard: no side lit");
            foreach (int stalk in new[] { -1, 0, 1 })
            {
                Flasher.Sides(stalk, true, on, out l, out r);
                check(l == on && r == on, Prefix + "the hazard lights both sides together over stalk " + stalk + " (flash " + (on ? "on" : "off") + ")");
            }
        }
    }

    static void Beams(Action<bool, string> check)
    {
        BeamSpec low = BeamSpec.For(Beam.Low), high = BeamSpec.For(Beam.High);
        check(high.OuterAngle < low.OuterAngle && high.Range > low.Range && high.Candela > low.Candela && high.PitchDown < low.PitchDown,
            Prefix + "the main beam is narrower, longer, brighter and higher than the dipped beam");
        check(low.OuterAngle > 0f && low.OuterAngle < 179f && high.OuterAngle > 0f && high.OuterAngle < 179f &&
            low.Range >= 20f && high.Range >= 60f && low.Candela > 0f && low.PitchDown >= 0f && low.PitchDown < 10f &&
            low.InnerPercent > 0f && low.InnerPercent <= 100f && high.InnerPercent > 0f && high.InnerPercent <= 100f,
            Prefix + "the beam values are plausible (angles, ranges, pitch)");
        check(BeamSpec.For(Beam.Off).OuterAngle == low.OuterAngle, Prefix + "a beam that is off asks for no spec of its own (the dipped one stands in)");
    }

    static void Lamps(Action<bool, string> check)
    {
        // The generic bus, the two authored ones, and a section without front.
        var specs = new System.Collections.Generic.List<(string, BusInteriorSpec)>
        {
            ("generic", BusInteriorSpec.Default),
            ("SWB6710EV01G", TestPacks.Spec("SWB6710EV01G")),
            ("SWB6109EV12G", TestPacks.Spec("SWB6109EV12G")),
        };
        foreach ((string name, BusInteriorSpec spec) in specs)
        {
            KitModelData data = KitBuild.BuildAll(new BusInterior(spec));
            bool found = HeadlampFrames.Find(data, BusInterior.HeadLamps, out float3 a, out float3 b);
            check(found, Prefix + name + ": the head lamps are found on the model");
            if (!found) continue;
            check(math.abs(a.x + b.x) < 0.05f && math.abs(a.y - b.y) < 0.05f && math.abs(a.z - b.z) < 0.05f,
                Prefix + name + $": the head lamps are mirror images ({a}, {b})");
            check(math.abs(a.x) > 0.3f && math.abs(a.x) < spec.HalfWidth && math.abs(b.x) < spec.HalfWidth,
                Prefix + name + $": the lamps are inside the body width and apart ({a.x:0.00}, {b.x:0.00}, half width {spec.HalfWidth:0.00})");
            check(a.y > 0.4f && a.y < 1.4f && b.y > 0.4f && b.y < 1.4f, Prefix + name + $": the lamps are at a bumper-top height ({a.y:0.00})");
            check(a.z > spec.FrontZ - 0.6f && a.z < spec.FrontZ + 0.15f && b.z > spec.FrontZ - 0.6f && b.z < spec.FrontZ + 0.15f,
                Prefix + name + $": the lamps are at the front ({a.z:0.00}, body front {spec.FrontZ:0.00})");
        }

        BusInteriorSpec rear = BusInteriorSpec.Default;
        rear.FrontZ = 3.6f; rear.RearZ = -4.4f; rear.RearAxleZ = -0.6f; rear.Doors = 2; rear.Section = BusSection.Rear;
        KitModelData rearData = KitBuild.BuildAll(new BusInterior(rear));
        check(!HeadlampFrames.Find(rearData, BusInterior.HeadLamps, out _, out _), Prefix + "the rear section of an articulated bus has no head lamps");
        check(!HeadlampFrames.Find(null, BusInterior.HeadLamps, out _, out _) && !HeadlampFrames.Find(rearData, -1, out _, out _) &&
            !HeadlampFrames.Find(rearData, 9999, out _, out _), Prefix + "no model, or a part out of range: no lamps, no throw");
    }
}
