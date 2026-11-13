using System;
using System.IO;
using System.Linq;

// The pure rules and the file of the input trace (Logs/CDLC.Input.log): the same files as in Core and MiniMap, tested in each.
static class InputTraceTests
{
    internal static void Run(Action<bool, string> check)
    {
        Rules(check);
        TraceFile(check);
    }

    static void Rules(Action<bool, string> check)
    {
        // the assertion, as Player.log has it
        check(CDLC.InputTracing.InputTraceRules.IsInputFault("Control index out of range", ""), "the assertion 'Control index out of range' is the fault");
        check(CDLC.InputTracing.InputTraceRules.IsInputFault("Control index out of range\nUnityEngine.Debug:Assert (bool,string)", null), "the assertion with its trace is the fault");

        // the exception, in the game's own form and in Unity's
        string exceptionStack = "  at UnityEngine.InputSystem.InputControlExtensions.CheckStateIsAtDefault (UnityEngine.InputSystem.InputControl control) [0x00003]\n" +
            "  at UnityEngine.InputSystem.InputActionState.ProcessControlStateChange (System.Int32 mapIndex, System.Int32 controlIndex) [0x00118]\n";
        check(CDLC.InputTracing.InputTraceRules.IsInputFault("ArgumentNullException: Value cannot be null.\nParameter name: control", exceptionStack), "the exception in ProcessControlStateChange is the fault");
        check(CDLC.InputTracing.InputTraceRules.IsInputFault("Value cannot be null.", "UnityEngine.InputSystem.InputActionState:ProcessControlStateChange (int,int,int,double,UnityEngine.InputSystem.LowLevel.InputEventPtr)"), "Unity's own stack text counts as well");
        check(CDLC.InputTracing.InputTraceRules.IsInputFault("x", "UnityEngine.InputSystem.InputActionState.UnityEngine.InputSystem.LowLevel.IInputStateChangeMonitor.NotifyControlStateChanged (UnityEngine.InputSystem.InputControl control, System.Double time)"), "a stack through NotifyControlStateChanged counts");

        // what is not it
        check(!CDLC.InputTracing.InputTraceRules.IsInputFault("NullReferenceException: Object reference not set", "at CDLC.DriveInput.Update () [0x0001]"), "an exception of ours is not the fault");
        check(!CDLC.InputTracing.InputTraceRules.IsInputFault("Value cannot be null.\nParameter name: control", "at SomeOtherMod.Thing.Run ()"), "the same text from another place is not the fault");
        check(!CDLC.InputTracing.InputTraceRules.IsInputFault("", "UnityEngine.InputSystem.InputActionState:Enable ()"), "an input system stack without a state change is not the fault");
        check(!CDLC.InputTracing.InputTraceRules.IsInputFault(null, null), "nothing is not the fault");

        // a text cut for one line
        check(CDLC.InputTracing.InputTraceRules.FirstLines("a\n\n  b  \nc\nd", 2) == "a | b", "the first two lines that are not empty");
        check(CDLC.InputTracing.InputTraceRules.FirstLines("only", 5) == "only", "fewer lines than asked");
        check(CDLC.InputTracing.InputTraceRules.FirstLines("", 3) == "" && CDLC.InputTracing.InputTraceRules.FirstLines(null, 3) == "" && CDLC.InputTracing.InputTraceRules.FirstLines("x", 0) == "", "nothing to cut");
        check(CDLC.InputTracing.InputTraceRules.FirstLines(new string('z', 400), 1, 50) == new string('z', 50) + "...", "a long line is cut");
        check(CDLC.InputTracing.InputTraceRules.FirstLines("a\r\nb\r\n", 2) == "a | b", "Windows line ends");

        // what is written: ordinary lines up to the limit, faults up to theirs
        check(CDLC.InputTracing.InputTraceRules.ShouldWrite(1, 3000, false, 0, 200), "the first line is written");
        check(CDLC.InputTracing.InputTraceRules.ShouldWrite(3000, 3000, false, 0, 200), "the last ordinary line is written");
        check(!CDLC.InputTracing.InputTraceRules.ShouldWrite(3001, 3000, false, 0, 200), "an ordinary line past the limit is not");
        check(CDLC.InputTracing.InputTraceRules.ShouldWrite(99999, 3000, true, 1, 200), "a fault is written when the ordinary lines are used up");
        check(CDLC.InputTracing.InputTraceRules.ShouldWrite(0, 3000, true, 200, 200), "the last fault is written");
        check(!CDLC.InputTracing.InputTraceRules.ShouldWrite(0, 3000, true, 201, 200), "a fault past its limit is not");

        // method names
        check(CDLC.InputTracing.InputTraceRules.ShortMethod("CDLC.DriveInput", "Update") == "DriveInput.Update", "a method without its namespace");
        check(CDLC.InputTracing.InputTraceRules.ShortMethod("Plain", "Run") == "Plain.Run" && CDLC.InputTracing.InputTraceRules.ShortMethod(null, null) == ".?", "a plain or missing type");
        check(CDLC.InputTracing.InputTraceRules.IsInputSystemType("UnityEngine.InputSystem.InputActionState") && CDLC.InputTracing.InputTraceRules.IsInputSystemType("UnityEngine.InputSystem.LowLevel.NativeInputRuntime"), "Unity's input system types");
        check(!CDLC.InputTracing.InputTraceRules.IsInputSystemType("Game.Input.InputManager") && !CDLC.InputTracing.InputTraceRules.IsInputSystemType("CDLC.DriveInput") && !CDLC.InputTracing.InputTraceRules.IsInputSystemType(null), "the game's and ours are not");
    }

    static void TraceFile(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "cdlc-trace-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string root2 = root + "-lone", root3 = root + "-many", blocker = root + "-blocker";
        try
        {
            string dir = Path.Combine(root, "Logs");
            var now = new Func<DateTime>(() => new DateTime(2026, 10, 19, 12, 0, 0));
            bool on = false;
            int announced = 0;
            object[] shared = CDLC.InputTracing.InputTraceFile.NewShared();
            var core = new CDLC.InputTracing.InputTraceFile(dir, shared, "Core", () => "Core header", p => announced++, 5, 3, now);
            var bus = new CDLC.InputTracing.InputTraceFile(dir, shared, "BusDriver", () => "BusDriver header", p => announced++, 5, 3, now);
            core.AddGate(() => on);     // the Diagnostics option of Core; Bus Driver registers none and follows it

            // gate off: nothing happens at all
            check(!core.Write("a line") && !bus.Write("a line") && !core.Write("a fault", true), "gate off: nothing is written");
            check(!Directory.Exists(root), "gate off: no folder is made");
            check(announced == 0, "gate off: the path is not announced");

            // gate off with the logs of earlier runs there: they are not moved
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "CDLC.Input.log"), prev = Path.Combine(dir, "CDLC.Input-prev.log");
            File.WriteAllText(log, "old run");
            File.WriteAllText(prev, "older run");
            check(!core.Write("a line") && !bus.Write("a line"), "gate off with old logs: nothing is written");
            check(File.ReadAllText(log) == "old run" && File.ReadAllText(prev) == "older run", "gate off: the logs of the earlier runs stay where they are");

            // gate on: the old log moves aside, then the header, then the line
            on = true;
            check(core.Write("first"), "gate on: a line is written");
            check(File.ReadAllText(prev) == "old run", "gate on: the old log became the previous one");
            string[] lines = File.ReadAllLines(log);
            check(lines.Length == 2 && lines[0] == "[2026-10-19 12:00:00.000] [Core] Core header" && lines[1] == "[2026-10-19 12:00:00.000] [Core] first", "gate on: header, then the line, with time and mod");
            check(announced == 1 && core.Path == log, "gate on: the path is announced once");

            // the second mod: its own header, no second move, the gate of the first
            check(bus.Write("second"), "gate on: the second mod writes too, by the gate of the first");
            check(File.ReadAllText(prev) == "old run", "the second mod does not move the log again");
            lines = File.ReadAllLines(log);
            check(lines.Length == 4 && lines[2].EndsWith("[BusDriver] BusDriver header") && lines[3].EndsWith("[BusDriver] second") && announced == 2, "the second mod has its own header, once");
            check(core.Write("third") && File.ReadAllLines(log).Length == 5, "a header is written once per mod");

            // the gate goes off and on during the run
            on = false;
            check(!core.Write("while off") && File.ReadAllLines(log).Length == 5, "gate off during the run: nothing more is written");
            on = true;
            check(core.Write("again") && File.ReadAllLines(log).Length == 6 && File.ReadAllText(prev) == "old run", "gate on again: the same file goes on, no new header, no new move");

            // limits: 5 ordinary lines in all, then only faults, 3 of them
            check(core.Write("fifth"), "the last ordinary line before the limit is written");
            check(File.ReadAllLines(log).Last().Contains("no more ordinary lines this run"), "the limit is said once, on its line");
            check(!core.Write("late") && !File.ReadAllText(log).Contains("late"), "an ordinary line past the limit is not written");
            ((long[])shared[0])[CDLC.InputTracing.InputTraceFile.Faults] = 1;
            check(core.Write("fault one", true), "a fault is written when the ordinary lines are used up");
            ((long[])shared[0])[CDLC.InputTracing.InputTraceFile.Faults] = 3;
            check(core.Write("fault three", true), "the last fault is written");
            ((long[])shared[0])[CDLC.InputTracing.InputTraceFile.Faults] = 4;
            check(!core.Write("fault four", true) && !File.ReadAllText(log).Contains("fault four"), "a fault past its limit is not written");

            // no gate registered at all (Bus Driver or MiniMap beside a Core that has none): never, and nothing touched
            var lone = new CDLC.InputTracing.InputTraceFile(Path.Combine(root2, "Logs"), CDLC.InputTracing.InputTraceFile.NewShared(), "MiniMap", () => "h", null);
            check(!lone.Enabled() && !lone.Write("x") && !Directory.Exists(root2), "no gate registered: never writes, touches nothing");

            // a gate that throws counts as off
            var broken = new CDLC.InputTracing.InputTraceFile(Path.Combine(root2, "Logs"), CDLC.InputTracing.InputTraceFile.NewShared(), "Core", () => "h", null);
            broken.AddGate(() => throw new InvalidOperationException("settings not ready"));
            check(!broken.Enabled() && !broken.Write("x") && !Directory.Exists(root2), "a gate that throws is off");

            // a folder that cannot be made, no folder at all: false, never an exception
            File.WriteAllText(blocker, "a file where the folder should be");
            var noFolder = new CDLC.InputTracing.InputTraceFile(Path.Combine(blocker, "Logs"), CDLC.InputTracing.InputTraceFile.NewShared(), "Core", () => "h", null);
            noFolder.AddGate(() => true);
            check(!noFolder.Write("x"), "a folder that cannot be made: nothing written, no exception");
            var nowhere = new CDLC.InputTracing.InputTraceFile(null, CDLC.InputTracing.InputTraceFile.NewShared(), "Core", () => "h", null);
            nowhere.AddGate(() => true);
            check(!nowhere.Write("x") && nowhere.Path == null, "no folder: nothing written");

            // two mods writing from several threads at once: one file, whole lines, one header each
            var sharedMany = CDLC.InputTracing.InputTraceFile.NewShared();
            var one = new CDLC.InputTracing.InputTraceFile(Path.Combine(root3, "Logs"), sharedMany, "Core", () => "h1", null, 100000, 200, now);
            var two = new CDLC.InputTracing.InputTraceFile(Path.Combine(root3, "Logs"), sharedMany, "BusDriver", () => "h2", null, 100000, 200, now);
            one.AddGate(() => true);
            System.Threading.Tasks.Parallel.For(0, 8, t =>
            {
                for (int i = 0; i < 100; i++)
                    (t % 2 == 0 ? one : two).Write("t" + t + " line " + i);
            });
            string[] all = File.ReadAllLines(Path.Combine(root3, "Logs", "CDLC.Input.log"));
            check(all.Length == 802 && all.All(l => l.StartsWith("[2026-10-19 12:00:00.000] [Core] ") || l.StartsWith("[2026-10-19 12:00:00.000] [BusDriver] ")), "threads and mods together: 800 lines and 2 headers, none torn");
            check(all.Count(l => l.EndsWith("] h1")) == 1 && all.Count(l => l.EndsWith("] h2")) == 1, "threads and mods together: one header each");

            // the state of the process is shared by the mods
            object[] a = CDLC.InputTracing.InputTraceFile.ProcessShared(out bool firstA);
            object[] b = CDLC.InputTracing.InputTraceFile.ProcessShared(out bool firstB);
            check(firstA && !firstB && ReferenceEquals(a, b), "the first mod makes the shared state, the next ones find it");
        }
        finally
        {
            foreach (string path in new[] { root, root2, root3 })
                try { Directory.Delete(path, true); } catch { }
            try { File.Delete(blocker); } catch { }
        }
    }
}
