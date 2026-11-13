using System;
using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Whether the Windows voice is tried for a sentence. Before, the first timeout, or a missing voice, or a missing PowerShell, switched the
    /// voice off for the rest of the session (one flag, set in three places, never cleared): a busy machine for 25 seconds, or one language
    /// without a voice, silenced every sentence in every language until the game was restarted. Now:
    ///  - no voice installed for a culture is a fact about that culture: it is not tried again until <see cref="Reset"/> (a changed language or
    ///    option), and other cultures are tried;
    ///  - a timeout, or a PowerShell that could not be found or started, is held for a while and then tried again, the
    ///    wait doubling from 30 seconds to 10 minutes, and a success clears it.
    /// Pure (the clock is given); used from the one synthesis thread, and from the thread that resets it, so every member locks.
    /// </summary>
    internal sealed class WindowsVoiceGate
    {
        internal const double kFirstWaitSeconds = 30.0, kLongestWaitSeconds = 600.0;

        private readonly object m_Lock = new object();
        private readonly HashSet<string> m_NoVoice = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private double m_BlockedUntil = double.NegativeInfinity;
        private double m_Wait = kFirstWaitSeconds;
        private int m_Failures;

        /// <summary>True when the Windows voice may be tried for this culture at this time.</summary>
        internal bool May(string culture, double now)
        {
            lock (m_Lock)
                return !m_NoVoice.Contains(culture ?? "") && now >= m_BlockedUntil;
        }

        /// <summary>The voice was made: the wait and the failures start again.</summary>
        internal void Succeeded()
        {
            lock (m_Lock)
            {
                m_BlockedUntil = double.NegativeInfinity;
                m_Wait = kFirstWaitSeconds;
                m_Failures = 0;
            }
        }

        /// <summary>No voice is installed for the culture (PowerShell answered so): not again for it until <see cref="Reset"/>.</summary>
        internal void NoVoiceFor(string culture)
        {
            lock (m_Lock)
                m_NoVoice.Add(culture ?? "");
        }

        /// <summary>A timeout, or a PowerShell that was not found or did not start: held for the wait, which doubles up to the longest.</summary>
        internal void Failed(double now)
        {
            lock (m_Lock)
            {
                m_Failures++;
                m_BlockedUntil = now + m_Wait;
                m_Wait = Math.Min(m_Wait * 2.0, kLongestWaitSeconds);
            }
        }

        /// <summary>A changed language or option: everything is tried again.</summary>
        internal void Reset()
        {
            lock (m_Lock)
            {
                m_NoVoice.Clear();
                m_BlockedUntil = double.NegativeInfinity;
                m_Wait = kFirstWaitSeconds;
                m_Failures = 0;
            }
        }

        /// <summary>Failures since the last success or reset (for the log).</summary>
        internal int Failures
        {
            get { lock (m_Lock) return m_Failures; }
        }

        /// <summary>The seconds before the voice may be tried again (0 when it may), for the log.</summary>
        internal double WaitLeft(double now)
        {
            lock (m_Lock)
                return Math.Max(0.0, m_BlockedUntil - now);
        }
    }
}
