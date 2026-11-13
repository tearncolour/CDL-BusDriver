using System;
using CDLC.BusDriver;

// The Windows voice's gate (WindowsVoiceGate): a timeout is held and tried again, a culture without a voice stays out until reset, and neither
// silences the other cultures for the rest of the session as the old single flag did.
static class WindowsVoiceGateTests
{
    internal static void Run(Action<bool, string> check)
    {
        var gate = new WindowsVoiceGate();
        check(gate.May("zh-CN", 0.0) && gate.May("en-US", 0.0) && gate.Failures == 0, "voice gate: a new gate lets every culture try");

        gate.Failed(100.0);
        check(!gate.May("zh-CN", 100.0) && !gate.May("en-US", 129.9), "voice gate: after a failure it holds every culture for 30 s");
        check(gate.May("zh-CN", 130.0) && gate.May("en-US", 131.0), "voice gate: after the wait it lets them try again");
        check(Math.Abs(gate.WaitLeft(110.0) - 20.0) < 1e-9 && gate.WaitLeft(200.0) == 0.0, "voice gate: the wait left is told for the log");

        gate.Failed(130.0);
        check(!gate.May("zh-CN", 189.9) && gate.May("zh-CN", 190.0), "voice gate: a second failure waits twice as long (60 s)");
        gate.Failed(190.0);
        check(!gate.May("zh-CN", 309.9) && gate.May("zh-CN", 310.0), "voice gate: and a third four times (120 s)");
        double now = 310.0;
        for (int i = 0; i < 12; i++)
        {
            gate.Failed(now);
            now += WindowsVoiceGate.kLongestWaitSeconds;
        }

        check(!gate.May("zh-CN", now - WindowsVoiceGate.kLongestWaitSeconds + 599.9) && gate.May("zh-CN", now), "voice gate: the wait stops at 10 minutes however often it fails");
        check(gate.Failures == 15, "voice gate: the failures are counted (" + gate.Failures + ")");

        gate.Succeeded();
        check(gate.May("zh-CN", 0.0) && gate.Failures == 0, "voice gate: a success clears the hold and the count");
        gate.Failed(1000.0);
        check(!gate.May("zh-CN", 1029.0) && gate.May("zh-CN", 1030.0), "voice gate: after a success the wait starts at 30 s again");

        var cultures = new WindowsVoiceGate();
        cultures.NoVoiceFor("de-DE");
        check(!cultures.May("de-DE", 0.0) && !cultures.May("DE-de", 1e9) && cultures.May("zh-CN", 0.0) && cultures.May("en-US", 0.0), "voice gate: no voice for one culture stays out for it, whatever the time, and no other culture");
        cultures.Failed(10.0);
        check(!cultures.May("zh-CN", 11.0) && !cultures.May("de-DE", 1e9), "voice gate: a hold of the others does not clear the culture without a voice");
        cultures.Succeeded();
        check(!cultures.May("de-DE", 0.0), "voice gate: a success does not bring back a culture that has no voice");
        cultures.Reset();
        check(cultures.May("de-DE", 0.0) && cultures.May("zh-CN", 0.0) && cultures.Failures == 0, "voice gate: a reset (a changed language or option) tries everything again");
        cultures.NoVoiceFor(null);
        check(cultures.May("zh-CN", 0.0), "voice gate: no culture given does not silence the others");
        check(new WindowsVoiceGate().May(null, 0.0), "voice gate: a missing culture is let through to be refused where it is used");
    }
}
