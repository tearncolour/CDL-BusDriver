#if CDLC_AUTHORING
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.UI.Binding;
using Game.Simulation;
using Game.UI.InGame;

namespace CDLC.BusDriver
{
    internal sealed class BusRoadGlassPause : IDisposable
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly TimeUISystem m_Time;
        readonly SimulationSystem m_Simulation;
        readonly Func<bool> m_OwnsWorld;
        readonly Action<string> m_Interrupt;
        readonly Dictionary<string, object> m_Report;
        readonly FieldInfo m_BarrierField, m_SpeedField, m_PendingField;
        readonly MethodInfo m_SetPaused, m_Subscribe, m_Unsubscribe;
        readonly EventBinding<bool> m_Barrier;
        float m_OriginalSpeed, m_OriginalRememberedSpeed, m_PausedRememberedSpeed;
        bool m_OriginalPending, m_Captured, m_Subscribed, m_Active, m_Restored;
        bool m_RestorationStarted, m_RememberedRestored, m_PendingRestored, m_SelectedRestored;

        internal BusRoadGlassPause(TimeUISystem time, SimulationSystem simulation, Func<bool> ownsWorld,
            Action<string> interrupt, Dictionary<string, object> report)
        {
            m_Time = time ?? throw new ArgumentNullException(nameof(time));
            m_Simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
            m_OwnsWorld = ownsWorld; m_Interrupt = interrupt; m_Report = report;
            m_BarrierField = Field("m_SimulationPausedBarrierBinding", typeof(EventBinding<bool>));
            m_SpeedField = Field("m_SpeedBeforePause", typeof(float));
            m_PendingField = Field("m_UnpausedBeforeForcedPause", typeof(bool));
            if (Field("m_SimulationSystem", typeof(SimulationSystem)).GetValue(time) != simulation)
                throw new InvalidOperationException("TimeUI pause targets a different simulation");
            m_Barrier = (EventBinding<bool>)m_BarrierField.GetValue(time) ?? throw new InvalidOperationException("TimeUI pause barrier is unavailable");
            m_SetPaused = Method(typeof(TimeUISystem), "SetSimulationPaused", new[] { typeof(bool) });
            m_Subscribe = Method(typeof(EventBindingBase), "OnSubscribe", Type.EmptyTypes);
            m_Unsubscribe = Method(typeof(EventBindingBase), "OnUnsubscribe", Type.EmptyTypes);
        }

        internal void Enter()
        {
            if (m_Captured) throw new InvalidOperationException("Pause scope was already entered");
            if (!m_OwnsWorld()) throw new OperationCanceledException("Pause scope no longer owns its World");
            m_OriginalSpeed = m_Simulation.selectedSpeed;
            m_OriginalRememberedSpeed = (float)m_SpeedField.GetValue(m_Time);
            m_OriginalPending = (bool)m_PendingField.GetValue(m_Time);
            m_Captured = true;
            m_Report["before"] = State();
            // Use the same handlers as the UI. The second call clears a pending forced-pause resume while our barrier is active.
            m_SetPaused.Invoke(m_Time, new object[] { true });
            int before = m_Barrier.observerCount;
            m_Subscribe.Invoke(m_Barrier, null);
            m_Subscribed = true;
            if (m_Barrier.observerCount != before + 1) throw new InvalidOperationException("TimeUI pause subscription count did not increase once");
            m_SetPaused.Invoke(m_Time, new object[] { true });
            m_PausedRememberedSpeed = (float)m_SpeedField.GetValue(m_Time);
            m_Active = true;
            m_Report["paused"] = State();
            Validate();
        }

        internal object State() => new {
            simulation_frame = m_Simulation.frameIndex, selected_speed = m_Simulation.selectedSpeed,
            remembered_speed = (float)m_SpeedField.GetValue(m_Time),
            resume_pending = (bool)m_PendingField.GetValue(m_Time),
            barrier_observers = m_Barrier.observerCount, owned_subscription = m_Subscribed,
            same_barrier = ReferenceEquals(m_BarrierField.GetValue(m_Time), m_Barrier),
            restoration_started = m_RestorationStarted, remembered_restored = m_RememberedRestored,
            pending_restored = m_PendingRestored, selected_restored = m_SelectedRestored
        };

        internal void Validate()
        {
            if (!m_Active || m_Restored) return;
            string reason = null;
            if (m_RestorationStarted)
            {
                if (m_Simulation.selectedSpeed != (m_SelectedRestored ? m_OriginalSpeed : 0f) ||
                    (bool)m_PendingField.GetValue(m_Time) != (m_PendingRestored && m_OriginalPending) ||
                    (float)m_SpeedField.GetValue(m_Time) != (m_RememberedRestored ? m_OriginalRememberedSpeed : m_PausedRememberedSpeed))
                    reason = "Glass pause cleanup observed a new speed or pause request";
            }
            else if (!m_Subscribed || !ReferenceEquals(m_BarrierField.GetValue(m_Time), m_Barrier) || m_Barrier.observerCount < 1)
                reason = "Glass pause lost its native TimeUI barrier";
            else if (m_Simulation.selectedSpeed != 0f || (bool)m_PendingField.GetValue(m_Time) ||
                (float)m_SpeedField.GetValue(m_Time) != m_PausedRememberedSpeed)
                reason = "Glass pause observed a speed or pause request change";
            if (reason == null) return;
            m_Report["interruption"] = new { reason, state = State() };
            m_Interrupt(reason);
            throw new OperationCanceledException(reason + "; original World state will not be restored");
        }

        public void Dispose()
        {
            if (!m_Captured || m_Restored) return;
            var errors = new List<Exception>();
            bool owns = m_OwnsWorld();
            if (owns)
            {
                try { Validate(); }
                catch (OperationCanceledException) { owns = false; }
            }
            if (m_Subscribed)
                try
                {
                    if (m_Barrier.observerCount > 0) m_Unsubscribe.Invoke(m_Barrier, null);
                    m_Subscribed = false;
                }
                catch (Exception error) { errors.Add(error); }
            // Do not restore until our subscription is gone. A retry keeps the original snapshot after a partial cleanup failure.
            if (!m_Subscribed && owns)
                try
                {
                    m_RestorationStarted = true;
                    if (!m_RememberedRestored)
                    { m_SpeedField.SetValue(m_Time, m_OriginalRememberedSpeed); m_RememberedRestored = true; }
                    if (!m_SelectedRestored)
                    { m_Simulation.selectedSpeed = m_OriginalSpeed; m_SelectedRestored = true; }
                    if (!m_PendingRestored)
                    { m_PendingField.SetValue(m_Time, m_OriginalPending); m_PendingRestored = true; }
                    m_Restored = true;
                }
                catch (Exception error) { errors.Add(error); }
            else if (!m_Subscribed) m_Restored = true;
            m_Report["cleanup"] = new { restored_original_state = owns && m_Restored, pending = !m_Restored,
                state = State(), errors = errors.ConvertAll(error => error.ToString()).ToArray() };
            if (errors.Count > 0) throw new AggregateException("TimeUI glass pause cleanup failed", errors);
        }

        static FieldInfo Field(string name, Type expected)
        {
            FieldInfo result = typeof(TimeUISystem).GetField(name, Private);
            if (result == null || result.FieldType != expected) throw new InvalidOperationException("Unsupported TimeUI pause field: " + name);
            return result;
        }
        static MethodInfo Method(Type type, string name, Type[] parameters)
        {
            MethodInfo result = type.GetMethod(name, Private, null, parameters, null);
            if (result == null || result.ReturnType != typeof(void)) throw new InvalidOperationException("Unsupported TimeUI pause method: " + name);
            return result;
        }
    }
}
#endif
