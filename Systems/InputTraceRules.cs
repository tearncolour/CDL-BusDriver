using System;
using System.Collections.Generic;
using System.Text;

namespace CDLC.InputTracing
{
    /// <summary>
    /// The pure rules of the input trace (Logs/CDLC.Input.log), kept free of Unity so they can be tested. The same file is in Core,
    /// Bus Driver and MiniMap: the mods do not depend on each other's internals, each has its own copy of the trace.
    /// </summary>
    internal static class InputTraceRules
    {
        /// <summary>
        /// A report of the fault seen on 19 October 2026 in Unity's input system, in either of its two forms: the assertion
        /// "Control index out of range" and the ArgumentNullException for a null control, both raised by
        /// InputActionState.ProcessControlStateChange when a state change monitor (there: Keyboard/ctrl and Keyboard/alt) reports
        /// a control that is not in the action's control list. Player.log has no times, so the trace has to catch them itself.
        /// </summary>
        internal static bool IsInputFault(string condition, string stackTrace)
        {
            condition = condition ?? "";
            stackTrace = stackTrace ?? "";
            if (condition.IndexOf("Control index out of range", StringComparison.Ordinal) >= 0)
                return true;

            // the stack of an exception reads "InputActionState.ProcessControlStateChange", Unity's own text "InputActionState:ProcessControlStateChange"
            return stackTrace.IndexOf("InputActionState", StringComparison.Ordinal) >= 0 &&
                (stackTrace.IndexOf("ProcessControlStateChange", StringComparison.Ordinal) >= 0 ||
                 stackTrace.IndexOf("NotifyControlStateChanged", StringComparison.Ordinal) >= 0);
        }

        /// <summary>The first lines of a text that are not empty, each cut to a length, joined on one line.</summary>
        internal static string FirstLines(string text, int count, int maxLength = 170)
        {
            if (string.IsNullOrEmpty(text) || count <= 0)
                return "";

            var result = new StringBuilder();
            int taken = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (taken > 0)
                    result.Append(" | ");
                result.Append(line.Length > maxLength ? line.Substring(0, maxLength) + "..." : line);
                if (++taken >= count)
                    break;
            }

            return result.ToString();
        }

        /// <summary>
        /// Whether a line is written: ordinary lines up to a limit per run (the limit itself is said once), the faults the trace is
        /// there for up to their own, so a noisy run cannot use up the room for them.
        /// </summary>
        internal static bool ShouldWrite(long linesWritten, long maxLines, bool isFault, long faultsWritten, long maxFaults)
        {
            return isFault ? faultsWritten <= maxFaults : linesWritten <= maxLines;
        }

        /// <summary>The name of a method for the trace: "Type.Method" without the namespace.</summary>
        internal static string ShortMethod(string declaringType, string method)
        {
            string type = declaringType ?? "";
            int dot = type.LastIndexOf('.');
            if (dot >= 0)
                type = type.Substring(dot + 1);
            return type + "." + (method ?? "?");
        }

        /// <summary>Whether a type belongs to Unity's input system (a call from there is a call from inside an input callback).</summary>
        internal static bool IsInputSystemType(string declaringTypeFullName)
        {
            return declaringTypeFullName != null &&
                declaringTypeFullName.StartsWith("UnityEngine.InputSystem", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The file of the input trace, free of Unity so that its one promise can be tested: <b>nothing is written, and nothing is touched
    /// on disk, unless one of the registered gates says yes</b> (the gate of the mods is Core's Diagnostics option; Bus Driver and
    /// MiniMap have none of their own and register none). With every gate off no folder is made, no file is made, and the
    /// log of the run before is not moved to CDLC.Input-prev.log. The first line that is written after a gate turns on moves the old
    /// log aside (once per run, whichever mod writes first), then writes the mod's header, then the line.
    ///
    /// The mods share one state through the app domain (<see cref="ProcessShared"/>: the counters, the gates and a lock, all of types
    /// of the base library, because each mod has its own copy of this class): one file, one limit, one lock.
    /// </summary>
    internal sealed class InputTraceFile
    {
        internal const string FileName = "CDLC.Input.log";
        internal const string PrevName = "CDLC.Input-prev.log";

        // positions in the shared counters
        internal const int Rebinds = 0, Switches = 1, FocusState = 2, FocusAt = 3, Lines = 4, Faults = 5, Started = 6;
        private const int kCounterCount = 8;
        private const string kShared = "CDLC.InputTrace.shared";

        private readonly string m_Directory;
        private readonly object[] m_Shared;
        private readonly string m_Tag;
        private readonly Func<string> m_Header;
        private readonly Action<string> m_Announce;
        private readonly Func<DateTime> m_Now;
        private readonly long m_MaxLines, m_MaxFaults;
        private bool m_HeaderDone;

        /// <summary>A fresh shared state: counters (the focus not yet known), the gates, the lock.</summary>
        internal static object[] NewShared()
        {
            var counters = new long[kCounterCount];
            counters[FocusState] = -1;
            return new object[] { counters, new List<Func<bool>>(), new object() };
        }

        /// <summary>The state of this process: made by the first mod to ask (<paramref name="first"/>), found by the others.</summary>
        internal static object[] ProcessShared(out bool first)
        {
            var shared = AppDomain.CurrentDomain.GetData(kShared) as object[];
            first = shared == null;
            if (first)
            {
                shared = NewShared();
                AppDomain.CurrentDomain.SetData(kShared, shared);
            }

            return shared;
        }

        /// <param name="directory">The folder of the log; null never writes.</param>
        /// <param name="header">The first line of this mod in the file, made when it is first needed (so nothing is read for it while the gates are off).</param>
        /// <param name="announce">Told the path of the file when this mod writes its first line.</param>
        internal InputTraceFile(string directory, object[] shared, string tag, Func<string> header, Action<string> announce,
            long maxLines = 3000, long maxFaults = 200, Func<DateTime> now = null)
        {
            m_Directory = directory;
            m_Shared = shared;
            m_Tag = tag;
            m_Header = header;
            m_Announce = announce;
            m_MaxLines = maxLines;
            m_MaxFaults = maxFaults;
            m_Now = now ?? (() => DateTime.Now);
        }

        internal long[] Counters => (long[])m_Shared[0];

        private List<Func<bool>> Gates => (List<Func<bool>>)m_Shared[1];

        private object Lock => m_Shared[2];

        internal string Path => m_Directory == null ? null : System.IO.Path.Combine(m_Directory, FileName);

        /// <summary>Registers a gate (a mod with an option for it); a gate that throws counts as off.</summary>
        internal void AddGate(Func<bool> gate)
        {
            if (gate == null)
                return;
            lock (Lock)
                Gates.Add(gate);
        }

        /// <summary>Whether any registered gate is on right now: with none registered, never.</summary>
        internal bool Enabled()
        {
            lock (Lock)
            {
                List<Func<bool>> gates = Gates;
                for (int i = 0; i < gates.Count; i++)
                {
                    try
                    {
                        if (gates[i]())
                            return true;
                    }
                    catch
                    {
                        // off
                    }
                }
            }

            return false;
        }

        /// <summary>Writes one line (nothing at all while the gates are off). Returns whether it was written. Never throws: a trace must not be the reason for a fault.</summary>
        internal bool Write(string text, bool fault = false)
        {
            try
            {
                if (m_Directory == null || !Enabled())
                    return false;

                string path = Path;
                string announced = null;
                lock (Lock)
                {
                    long[] counters = Counters;
                    if (System.Threading.Interlocked.Read(ref counters[Started]) == 0)
                    {
                        System.IO.Directory.CreateDirectory(m_Directory);
                        Rotate(path);
                        System.Threading.Interlocked.Exchange(ref counters[Started], 1);
                    }

                    long lines = fault ? 0 : System.Threading.Interlocked.Increment(ref counters[Lines]);
                    if (!InputTraceRules.ShouldWrite(lines, m_MaxLines, fault, System.Threading.Interlocked.Read(ref counters[Faults]), m_MaxFaults))
                        return false;

                    if (!m_HeaderDone)
                    {
                        m_HeaderDone = true;
                        Append(path, m_Header != null ? m_Header() : m_Tag + " trace started");
                        announced = path;
                    }

                    if (!fault && lines == m_MaxLines)
                        text += $"; {m_MaxLines} lines written: no more ordinary lines this run (faults are still written)";
                    Append(path, text);
                }

                if (announced != null)
                    m_Announce?.Invoke(announced);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Append(string path, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"[{m_Now():yyyy-MM-dd HH:mm:ss.fff}] [{m_Tag}] {text}\r\n");
            using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                stream.Write(bytes, 0, bytes.Length);
        }

        // The log of the run before becomes CDLC.Input-prev.log; when that cannot be done the old file stays and this run appends to it.
        private void Rotate(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Copy(path, System.IO.Path.Combine(m_Directory, PrevName), true);
                    System.IO.File.Delete(path);
                }
            }
            catch
            {
                // appended to the old file
            }
        }
    }
}
