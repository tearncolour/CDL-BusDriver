using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Game.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace CDLC.InputTracing
{
    /// <summary>
    /// A trace of the game's input layer of its own, Logs/CDLC.Input.log (the run before it in CDLC.Input-prev.log), written by Core,
    /// Bus Driver and MiniMap together: when each of their input barriers is made, switched and released and from where, which of
    /// their actions are switched on or off, the window focus, every change of the input devices (the keyboard layout among them:
    /// an input method switch rebuilds the keyboard), how often the game re-resolved its bindings, and, with the time that
    /// Player.log lacks, every report of the fault of 19 October 2026 (ArgumentNullException "control" / "Control index out of range"
    /// in InputActionState.ProcessControlStateChange on Keyboard/ctrl and Keyboard/alt) with the state around it.
    ///
    /// It only watches: nothing here changes what the barriers or actions do. <b>It prints only while Core's Diagnostics option is on</b>
    /// (Options, CSL Driving Life Core, Diagnostics): with it off nothing is written, no file is made, and the log of the run before is
    /// left where it is (InputTraceFile, tested). A fault that comes while it is off is not recorded: turn Diagnostics on before the
    /// drive. Bus Driver and MiniMap have no Diagnostics option; they pass no gate and follow Core's. At most 3000 lines a run (the
    /// faults have room of their own). The same file is in the three mods on purpose (they do not depend on each other); they share
    /// one file, one set of counters and one lock through the app domain.
    /// </summary>
    internal static class InputTrace
    {
        private static readonly object s_Lock = new object();
        private static readonly List<InputBarrier> s_Live = new List<InputBarrier>();
        private static readonly Dictionary<string, bool> s_Switched = new Dictionary<string, bool>();
        private static InputTraceFile s_File;
        private static string s_Tag = "?";
        private static int s_MainThread = -1;
        private static bool s_Installed;
        private static bool s_Owner;

        /// <summary>
        /// Starts the trace for one mod (once, on the main thread); the first mod of the run takes the global hooks. Nothing is written
        /// here. <paramref name="gate"/> is the mod's Diagnostics option (null for a mod that has none); <paramref name="note"/> is
        /// told the path of the log when the first line of the mod is written, which is when Diagnostics is on.
        /// </summary>
        internal static void Install(string tag, string version, Action<string> note, Func<bool> gate)
        {
            if (s_Installed)
                return;

            s_Installed = true;
            try
            {
                s_Tag = tag;
                s_MainThread = Thread.CurrentThread.ManagedThreadId;
                object[] shared = InputTraceFile.ProcessShared(out s_Owner);
                string atStart = Devices();
                bool owner = s_Owner;
                s_File = new InputTraceFile(
                    Path.Combine(Application.persistentDataPath, "Logs"), shared, tag,
                    () => $"{tag} {version} trace started{(owner ? " (first of the run: owns the window and device hooks)" : "")}; game {Application.version}, process {Process.GetCurrentProcess().Id}; at start {atStart}",
                    path => note?.Invoke($"Input trace of the game's input layer: {path}"));
                s_File.AddGate(gate);
                Application.logMessageReceivedThreaded += OnLog;
                if (s_Owner)
                {
                    Application.focusChanged += OnFocus;
                    InputSystem.onDeviceChange += OnDevice;
                    InputSystem.onActionChange += OnAction;
                }
            }
            catch (Exception ex)
            {
                s_File = null;
                note?.Invoke($"Input trace not started: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>A barrier over actions, as <c>new InputBarrier</c> makes it, with the trace of it.</summary>
        internal static InputBarrier Create(string name, IList<ProxyAction> actions, InputManager.DeviceType mask, bool blocked)
        {
            var barrier = new InputBarrier(name, actions, mask, blocked);
            lock (s_Lock)
                s_Live.Add(barrier);
            if (On)
                Write($"barrier {Describe(barrier)} created; {Where()}; {Counters()}");
            return barrier;
        }

        /// <summary>Blocks or unblocks a barrier (nothing when it is as asked), the line written before the switch so that a fault in it follows its cause.</summary>
        internal static void SetBlocked(InputBarrier barrier, bool blocked)
        {
            if (barrier == null || barrier.blocked == blocked)
                return;

            if (On)
                Write($"barrier {Describe(barrier)} blocked {barrier.blocked} -> {blocked}; {Where()}; {Counters()}");
            barrier.blocked = blocked;
        }

        /// <summary>Unblocks and disposes a barrier, as the mods did.</summary>
        internal static void Release(InputBarrier barrier)
        {
            if (barrier == null)
                return;

            if (On)
                Write($"barrier {Describe(barrier)} released; {Where()}; {Counters()}");
            barrier.blocked = false;
            Dispose(barrier);
        }

        /// <summary>Disposes a barrier.</summary>
        internal static void Dispose(InputBarrier barrier)
        {
            if (barrier == null)
                return;

            lock (s_Lock)
                s_Live.Remove(barrier);
            barrier.Dispose();
        }

        /// <summary>An action switched on or off by a mod (written when its state changes while Diagnostics is on).</summary>
        internal static void Switch(string action, bool enabled)
        {
            if (!On)
                return;

            lock (s_Lock)
            {
                if (s_Switched.TryGetValue(action, out bool known) && known == enabled)
                    return;
                s_Switched[action] = enabled;
            }

            Write($"action {action} {(enabled ? "on" : "off")}; {Where()}");
        }

        private static string Describe(InputBarrier barrier)
        {
            try
            {
                return $"{barrier.name} (blocked {barrier.blocked}, {barrier.mask}, {barrier.actions.Count} action(s), {barrier.maps.Count} map(s))";
            }
            catch (Exception ex)
            {
                return $"{barrier?.name} (unreadable: {ex.GetType().Name})";
            }
        }

        /// <summary>Whether a line may be written now: the trace is installed and Diagnostics is on. Every text of the trace is made only after this says yes.</summary>
        private static bool On => s_File != null && s_File.Enabled();

        private static string Counters()
        {
            if (s_File == null)
                return "";
            long[] c = s_File.Counters;
            return $"the game re-resolved bindings {Interlocked.Read(ref c[InputTraceFile.Rebinds])}x, actions switched {Interlocked.Read(ref c[InputTraceFile.Switches])}x";
        }

        private static string Focus()
        {
            if (s_File == null)
                return "";
            long[] c = s_File.Counters;
            long state = Interlocked.Read(ref c[InputTraceFile.FocusState]);
            if (state < 0)
                return "focus unknown (no change seen yet)";
            double seconds = (Stopwatch.GetTimestamp() - Interlocked.Read(ref c[InputTraceFile.FocusAt])) / (double)Stopwatch.Frequency;
            return $"window focus {(state == 1 ? "gained" : "lost")} {seconds:0.0} s ago";
        }

        private static string Devices()
        {
            try
            {
                var text = new StringBuilder();
                int shown = 0, total = 0;
                foreach (InputDevice device in InputSystem.devices)
                {
                    total++;
                    if (shown >= 10)
                        continue;
                    if (shown++ > 0)
                        text.Append(", ");
                    text.Append(device.name).Append('#').Append(device.deviceId);
                    if (!device.enabled)
                        text.Append(" (disabled)");
                }

                return $"{total} input device(s): {text}";
            }
            catch (Exception ex)
            {
                return $"input devices unreadable ({ex.GetType().Name})";
            }
        }

        /// <summary>Where a call comes from: whether it is inside a callback of the input system, and the three methods of ours above it.</summary>
        private static string Where()
        {
            try
            {
                StackFrame[] frames = new StackTrace(2, false).GetFrames();
                if (frames == null)
                    return "caller unknown";

                string inside = null;
                var ours = new List<string>();
                foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod();
                    Type type = method?.DeclaringType;
                    if (method == null)
                        continue;
                    if (InputTraceRules.IsInputSystemType(type?.FullName))
                    {
                        inside = InputTraceRules.ShortMethod(type.FullName, method.Name);
                        break;
                    }

                    if (ours.Count < 3)
                        ours.Add(InputTraceRules.ShortMethod(type?.FullName, method.Name));
                }

                string chain = string.Join(" < ", ours);
                return inside != null ? $"INSIDE the input system ({inside}) from {chain}" : $"from {chain}, not inside the input system";
            }
            catch
            {
                return "caller unknown";
            }
        }

        private static void Write(string text, bool fault = false) => s_File?.Write(text, fault);

        private static void OnFocus(bool gained)
        {
            if (s_File == null)
                return;

            // the state is kept always (a few numbers), so that the first line after Diagnostics goes on can say how long ago the focus changed
            long[] c = s_File.Counters;
            Interlocked.Exchange(ref c[InputTraceFile.FocusState], gained ? 1 : 0);
            Interlocked.Exchange(ref c[InputTraceFile.FocusAt], Stopwatch.GetTimestamp());
            if (On)
                Write($"window focus {(gained ? "gained" : "lost")}; {Counters()}");
        }

        private static void OnDevice(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.UsageChanged || !On)
                return;

            string layout = "";
            try
            {
                if (device is Keyboard keyboard)
                    layout = $", keyboard layout {keyboard.keyboardLayout}";
            }
            catch
            {
                // the layout is only a detail
            }

            Write($"input device {change}: {device?.name}#{device?.deviceId} ({device?.layout}){layout}; {Focus()}; {Counters()}");
        }

        private static void OnAction(object source, InputActionChange change)
        {
            if (s_File == null)
                return;

            long[] c = s_File.Counters;
            switch (change)
            {
                case InputActionChange.BoundControlsChanged:
                    Interlocked.Increment(ref c[InputTraceFile.Rebinds]);
                    break;
                case InputActionChange.ActionEnabled:
                case InputActionChange.ActionDisabled:
                case InputActionChange.ActionMapEnabled:
                case InputActionChange.ActionMapDisabled:
                    Interlocked.Increment(ref c[InputTraceFile.Switches]);
                    break;
            }
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (s_File == null || !InputTraceRules.IsInputFault(condition, stackTrace) || !s_File.Enabled())
                    return;

                // One report is seen by the handler of every mod, in the order they installed (the owner's first): the owner counts it and
                // writes the detail, the others write their barriers under the same number.
                long[] c = s_File.Counters;
                long number = s_Owner ? Interlocked.Increment(ref c[InputTraceFile.Faults]) : Interlocked.Read(ref c[InputTraceFile.Faults]);
                if (number > 200 || number == 0)
                    return;

                string barriers;
                lock (s_Lock)
                {
                    var text = new StringBuilder();
                    foreach (InputBarrier barrier in s_Live)
                        text.Append(text.Length > 0 ? ", " : "").Append(Describe(barrier));
                    barriers = text.Length > 0 ? text.ToString() : "none live";
                }

                if (s_Owner)
                    Write($"UNITY INPUT FAULT #{number} ({type}): {InputTraceRules.FirstLines(condition, 2)} || stack: {InputTraceRules.FirstLines(stackTrace, 6)} || {World()}", true);
                Write($"UNITY INPUT FAULT #{number}: live barriers of {s_Tag}: {barriers}", true);
            }
            catch
            {
                // a trace must never be the reason for a fault
            }
        }

        /// <summary>The state around a fault: focus, counters, the keyboard and its modifiers, the devices (on the main thread only: the input system is not for other threads).</summary>
        private static string World()
        {
            var text = new StringBuilder(Focus()).Append("; ").Append(Counters());
            if (Thread.CurrentThread.ManagedThreadId != s_MainThread)
                return text.Append("; (not on the main thread: keys and devices not read)").ToString();

            try
            {
                text.Append("; frame ").Append(Time.frameCount).Append(", input updates ").Append(InputState.updateCount);
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null)
                    text.Append("; keyboard #").Append(keyboard.deviceId).Append(" layout ").Append(keyboard.keyboardLayout)
                        .Append(", ctrl ").Append(keyboard.ctrlKey.isPressed ? 1 : 0)
                        .Append(" alt ").Append(keyboard.altKey.isPressed ? 1 : 0)
                        .Append(" shift ").Append(keyboard.shiftKey.isPressed ? 1 : 0);
                else
                    text.Append("; no current keyboard");
                text.Append("; ").Append(Devices());
            }
            catch (Exception ex)
            {
                text.Append("; state unreadable (").Append(ex.GetType().Name).Append(')');
            }

            return text.ToString();
        }
    }
}
