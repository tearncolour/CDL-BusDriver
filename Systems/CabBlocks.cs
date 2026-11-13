using System;
using System.Diagnostics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Where the cab's time goes: Stopwatch ticks per block of BusCabSystem.OnUpdate, summed over the frames and read back as
    /// milliseconds a frame (the 60 s log line and the road probe's report). A block costs two timestamps (about 50 ns).
    /// </summary>
    internal static class CabBlocks
    {
        internal const int ReadCore = 0, Doors = 1, DoorSounds = 2, Signals = 3, Driveline = 4, Haptics = 5, Run = 6, Assembly = 7, Led = 8,
            Create = 9, Glass = 10, Materials = 11, Straps = 12, Apply = 13, Readers = 14, Joint = 15, Outside = 16, Cluster = 17,
            Mirrors = 18, Headlights = 19, Riders = 20, Prepare = 21, JointDeform = 22, JointUpload = 23, Measure = 24, Bind = 25, Release = 26, Skins = 27, WrapWrite = 28, Eye = 29, RiderPlaces = 30, Count = 31;

        private static readonly string[] s_Names =
        {
            "core read", "doors", "door sounds", "signals", "driveline", "haptics", "run (all)", "assembly", "led screen",
            "model create", "glass", "materials", "strap swing", "pose apply", "readers", "joint", "outside", "cluster",
            "mirrors", "headlights", "riders", "models ahead", "joint deform", "joint upload", "measure", "bind assembly", "release", "wrap skins", "wrap template", "eye", "rider places"
        };

        private static readonly long[] s_Ticks = new long[Count];
        private static readonly long[] s_Worst = new long[Count];
        private static int s_Frames;

        internal static long Now => Stopwatch.GetTimestamp();

        /// <summary>using (new CabBlocks.Scope(CabBlocks.Mirrors)) ...: the block's time, added when the scope ends.</summary>
        internal readonly struct Scope : IDisposable
        {
            private readonly int m_Block;
            private readonly long m_Start;
            internal Scope(int block) { m_Block = block; m_Start = Stopwatch.GetTimestamp(); }
            public void Dispose() => Add(m_Block, m_Start);
        }

        internal static void Add(int block, long start)
        {
            long ticks = Stopwatch.GetTimestamp() - start;
            s_Ticks[block] += ticks;
            if (ticks > s_Worst[block]) s_Worst[block] = ticks;
        }

        /// <summary>Adds time measured elsewhere (a part of a block that is timed as a whole).</summary>
        internal static void AddTicks(int block, long ticks)
        {
            if (ticks <= 0) return;
            s_Ticks[block] += ticks;
            if (ticks > s_Worst[block]) s_Worst[block] = ticks;
        }

        internal static void Frame() => s_Frames++;

        internal static void Reset()
        {
            Array.Clear(s_Ticks, 0, Count);
            Array.Clear(s_Worst, 0, Count);
            s_Frames = 0;
        }

        internal static int Frames => s_Frames;

        /// <summary>"core read 0.01, run (all) 0.50 ..." in milliseconds a frame; the blocks that cost under 5 microseconds are left out.</summary>
        internal static string Describe()
        {
            double ms = 1000.0 / Stopwatch.Frequency;
            int frames = Math.Max(1, s_Frames);
            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < Count; i++)
            {
                double each = s_Ticks[i] * ms / frames;
                if (each < .005) continue;
                if (parts.Length > 0) parts.Append(", ");
                parts.Append(s_Names[i]).Append(' ').Append(each.ToString("0.000")).Append(" (max ").Append((s_Worst[i] * ms).ToString("0.0")).Append(')');
            }

            return parts.Length > 0 ? parts.ToString() : "nothing measured";
        }

        /// <summary>The same as a name-to-milliseconds-a-frame table (the road probe's report).</summary>
        internal static System.Collections.Generic.Dictionary<string, double> Table()
        {
            double ms = 1000.0 / Stopwatch.Frequency;
            int frames = Math.Max(1, s_Frames);
            var table = new System.Collections.Generic.Dictionary<string, double>();
            for (int i = 0; i < Count; i++)
                table[s_Names[i] + " ms_per_frame"] = Math.Round(s_Ticks[i] * ms / frames, 4);
            table["frames"] = s_Frames;
            return table;
        }
    }
}
