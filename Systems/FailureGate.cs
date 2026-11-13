using System;
using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>
    /// When something that failed may be tried again: after 1, 2, 4, 8 s (at most <see cref="kMaxDelaySeconds"/>), and not at all after
    /// <see cref="kGiveUpAfter"/> failures in a row until it succeeds or the gate is cleared. The driven bus's update used to release
    /// and rebuild the whole bus on the next frame after any exception, without a pause or a limit (112 rebuilds in 107 s on
    /// 9 November, until the game ran out of memory).
    /// </summary>
    internal sealed class FailureGate<TKey>
    {
        internal const int kGiveUpAfter = 5;
        internal const float kFirstDelaySeconds = 1f, kMaxDelaySeconds = 60f;

        private readonly Dictionary<TKey, (int failures, float retryAt)> m_State = new Dictionary<TKey, (int, float)>();

        internal bool Allowed(TKey key, float now) =>
            !m_State.TryGetValue(key, out var state) || state.failures < kGiveUpAfter && now >= state.retryAt;

        internal bool GaveUp(TKey key) => m_State.TryGetValue(key, out var state) && state.failures >= kGiveUpAfter;

        internal int Failures(TKey key) => m_State.TryGetValue(key, out var state) ? state.failures : 0;

        /// <summary>Records a failure at <paramref name="now"/>; the seconds until the next try (infinity once given up).</summary>
        internal float Fail(TKey key, float now)
        {
            int failures = Failures(key) + 1;
            float delay = failures >= kGiveUpAfter ? float.PositiveInfinity : Delay(failures);
            m_State[key] = (failures, now + delay);
            return delay;
        }

        internal static float Delay(int failures) =>
            Math.Min(kMaxDelaySeconds, kFirstDelaySeconds * (float)Math.Pow(2, Math.Max(0, failures - 1)));

        internal void Succeeded(TKey key) => m_State.Remove(key);

        internal void Clear() => m_State.Clear();
    }
}
