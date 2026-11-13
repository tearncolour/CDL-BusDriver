using System;
using System.Collections.Generic;
using CDLC.BusDriver;

// The sound model's pure side: volume curves, conditions, pitch and the three ways a sound plays (looped while its conditions hold,
// once when they start to hold, once per trigger). The expected numbers are what openOMSI documents for OMSI's sound.cfg.
static class SoundModelTests
{
    internal static void Run(Action<bool, string> check)
    {
        bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

        var curve = new SoundCurve("v", 0f, 0f, 10f, 1f, 20f, 0.5f);
        check(Near(curve.Evaluate(-5f), 0f) && Near(curve.Evaluate(0f), 0f), "curve: the first point's value to its left");
        check(Near(curve.Evaluate(5f), 0.5f) && Near(curve.Evaluate(15f), 0.75f), "curve: straight lines between points");
        check(Near(curve.Evaluate(20f), 0.5f) && Near(curve.Evaluate(99f), 0.5f), "curve: the last point's value from there on");
        check(Near(new SoundCurve("v", 5f, 0f, 5f, 1f).Evaluate(5f), 1f), "curve: at two points in one place the later one wins from there on");
        bool threw = false;
        try { new SoundCurve("v", 1f, 2f, 3f); } catch (ArgumentException) { threw = true; }
        check(threw, "curve: an odd number of values is refused");

        var runs = new SoundCondition("engine_rpm", 200f, SoundRelation.Greater);
        check(!runs.Holds(0f) && !runs.Holds(200f) && runs.Holds(520f), "condition: 'engine_rpm 200 3' means above 200");
        check(new SoundCondition("v", 2f, SoundRelation.LessOrEqual).Holds(2f) && !new SoundCondition("v", 2f, SoundRelation.Less).Holds(2f), "condition: < and <=");
        check(new SoundCondition("v", 2f, SoundRelation.NotEqual).Holds(3f) && !new SoundCondition("v", 2f, SoundRelation.Equal).Holds(3f), "condition: = and <>");

        var vars = new Dictionary<string, float>();
        Func<string, float> var = n => vars.TryGetValue(n, out float v) ? v : 0f;

        // a compressor-like loop: pitched by the engine, heard while the engine runs and the bus is nearly standing
        var loop = new SoundVoice(new SoundDef
        {
            Name = "compressor",
            Volume = 0.8f,
            PitchVariable = "engine_rpm",
            PitchReference = 600f,
            BaseRate = 44100f,
            ClipRate = 44100f,
            Conditions = new[] { new SoundCondition("engine_rpm", 200f, SoundRelation.Greater), new SoundCondition("speed", 2f, SoundRelation.Less) },
        });
        vars["engine_rpm"] = 0f;
        SoundFrame f = loop.Update(var, false, false);
        check(!f.Play && !f.Start && f.Volume == 0f, "loop: silent with the engine off");
        vars["engine_rpm"] = 900f;
        vars["speed"] = 0f;
        f = loop.Update(var, false, false);
        check(f.Play && f.Start && Near(f.Pitch, 1.5f) && Near(f.Volume, 0.8f), "loop: starts at 1.5x for 900 rpm over a reference of 600");
        f = loop.Update(var, false, true);
        check(f.Play && !f.Start, "loop: keeps running without starting again");
        vars["engine_rpm"] = -300f;
        vars["speed"] = 0f;
        f = loop.Update(var, false, true);
        check(!f.Play, "loop: below the 200 rpm condition it stops");
        vars["engine_rpm"] = 900f;
        vars["speed"] = 5f;
        f = loop.Update(var, false, true);
        check(!f.Play, "loop: stops when a second condition fails");

        // a pitched loop with no condition is silent under 100 Hz
        var low = new SoundVoice(new SoundDef { PitchVariable = "rpm", PitchReference = 1000f, BaseRate = 1000f, ClipRate = 1000f });
        vars["rpm"] = 50f;
        check(!low.Update(var, false, false).Play, "pitched loop: 50 Hz is under the minimum and is not started");
        vars["rpm"] = 500f;
        check(Near(low.Update(var, false, false).Pitch, 0.5f), "pitched loop: half the reference, half the speed");
        vars["rpm"] = -1000f;
        check(Near(low.Update(var, false, false).Pitch, 1f), "pitched loop: the absolute value of the variable");

        // a volume curve on the engine: fades in with the throttle
        var swell = new SoundVoice(new SoundDef { Volume = 1f, Curves = new[] { new SoundCurve("throttle", 0f, 0.2f, 1f, 1f) } });
        vars["throttle"] = 0.5f;
        check(Near(swell.Update(var, false, false).Volume, 0.6f), "volume: the entry's volume times the curve");

        // a triggered sound ignores its conditions, plays once and keeps the loudest curve value it has seen
        var door = new SoundVoice(new SoundDef
        {
            Trigger = "door_hit",
            Volume = 1f,
            Curves = new[] { new SoundCurve("door_speed", 0f, 0f, 1f, 1f) },
            Conditions = new[] { new SoundCondition("never", 1f, SoundRelation.Equal) },
        });
        vars["door_speed"] = 0.8f;
        f = door.Update(var, true, false);
        check(f.Start && f.Play && Near(f.Volume, 0.8f), "trigger: starts at the curve's value, whatever its conditions say");
        vars["door_speed"] = 0f;
        f = door.Update(var, false, true);
        check(f.Play && !f.Start && Near(f.Volume, 0.8f), "trigger: keeps its loudest volume after the leaf has stopped");
        vars["door_speed"] = 1f;
        f = door.Update(var, false, true);
        check(Near(f.Volume, 1f), "trigger: and takes a louder one while it plays");
        f = door.Update(var, false, false);
        check(!f.Play && !f.Start, "trigger: over when the source has finished");
        vars["door_speed"] = 0f;
        f = door.Update(var, true, false);
        check(!f.Start && !f.Play, "trigger: fired while its curve is at 0 is not heard (under -100 dB)");
        vars["door_speed"] = 0.4f;
        f = door.Update(var, true, false);
        check(f.Start && Near(f.Volume, 0.4f), "trigger: starts again with a fresh volume (the old peak is forgotten)");

        // once when the conditions start to hold (a valve's hiss, a chime)
        var chime = new SoundVoice(new SoundDef { OnceOnRise = true, Conditions = new[] { new SoundCondition("ignition", 1f, SoundRelation.Equal) } });
        vars["ignition"] = 0f;
        check(!chime.Update(var, false, false).Start, "once: nothing while the conditions fail");
        vars["ignition"] = 1f;
        f = chime.Update(var, false, false);
        check(f.Start && f.Play, "once: starts on the rising edge");
        f = chime.Update(var, false, true);
        check(!f.Start && f.Play, "once: not again while the conditions keep holding");
        f = chime.Update(var, false, false);
        check(!f.Start && !f.Play, "once: and not looped after the clip has ended");
        vars["ignition"] = 0f;
        chime.Update(var, false, false);
        vars["ignition"] = 1f;
        check(chime.Update(var, false, false).Start, "once: starts again at the next rising edge");

        // an unknown variable reads 0, the trigger voice can be reset
        door.Reset();
        check(!door.Update(k => 0f, false, false).Play, "reset: a triggered voice is idle");
    }
}
