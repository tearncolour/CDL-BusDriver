using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Entities;
using Game.Routes;
using Unity.Entities;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The terminus TransitTimetables (a separate mod: github.com/AmicusDeus/TransitTimetables, MIT) runs a line to, read by
    /// reflection, so this mod neither references nor needs it. Its terminus is where its buses wait for their departure and
    /// retire; without this the direction sign named the line's last stop whatever the player chose there. Used only while that
    /// mod is loaded, its master switch is on and the line's timetable is on: the conditions its dispatch uses the terminus
    /// under. Not loaded, a layout this code does not know (written against 0.5.4: TimetableSchedule.m_Enabled and
    /// m_TerminusStop, Mod.ActiveSetting.Enabled), or a call that fails: false, and the caller keeps its own terminus. A failed
    /// call switches the link off until the game is restarted.
    /// </summary>
    internal static class TransitTimetablesLink
    {
        private const BindingFlags kInstance = BindingFlags.Public | BindingFlags.Instance;

        private static bool s_Looked;
        private static bool s_Usable;
        private static ComponentType s_ScheduleType;
        private static Func<EntityManager, Entity, object> s_ReadSchedule;
        private static FieldInfo s_Enabled;
        private static FieldInfo s_TerminusStop;
        private static FieldInfo s_ActiveSetting;
        private static PropertyInfo s_SettingEnabled;
        private static readonly List<Entity> s_Stops = new List<Entity>();
        private static readonly List<bool> s_Boarding = new List<bool>();

        /// <summary>
        /// The line's timetable terminus: true with the stop, and whether it is the stop the player chose (false: the first-stop
        /// fallback the timetable uses without a usable choice). False when the timetable does not time this line.
        /// </summary>
        internal static bool TryTerminus(EntityManager entityManager, Entity line, out Entity stop, out bool chosen)
        {
            stop = Entity.Null;
            chosen = false;
            if (!Resolve() || line == Entity.Null)
                return false;

            try
            {
                object setting = s_ActiveSetting.GetValue(null);
                if (setting == null || !(bool)s_SettingEnabled.GetValue(setting) || !entityManager.HasComponent(line, s_ScheduleType))
                    return false;

                object schedule = s_ReadSchedule(entityManager, line);
                if (!(bool)s_Enabled.GetValue(schedule) || !entityManager.TryGetBuffer(line, true, out DynamicBuffer<RouteWaypoint> waypoints))
                    return false;

                s_Stops.Clear();
                s_Boarding.Clear();
                for (int i = 0; i < waypoints.Length; i++)
                {
                    Entity connected = entityManager.TryGetComponent(waypoints[i].m_Waypoint, out Connected c) ? c.m_Connected : Entity.Null;
                    s_Stops.Add(connected);
                    s_Boarding.Add(connected != Entity.Null && entityManager.Exists(connected) && entityManager.HasComponent<BoardingVehicle>(connected));
                }

                int index = TimetableTerminus.Pick(s_Stops, s_Boarding, (Entity)s_TerminusStop.GetValue(schedule), Entity.Null, out chosen);
                if (index < 0)
                    return false;

                stop = s_Stops[index];
                return true;
            }
            catch (Exception ex)
            {
                s_Usable = false;
                Mod.log.Warn($"TransitTimetables: reading line {line}'s timetable failed ({ex.GetType().Name}: {ex.Message}); its terminus is no longer used until the game restarts.");
                return false;
            }
        }

        /// <summary>Finds the mod's types once a session and checks they have the members this code reads.</summary>
        private static bool Resolve()
        {
            if (s_Looked)
                return s_Usable;

            s_Looked = true;
            try
            {
                Type schedule = null;
                Type mod = null;
                Assembly assembly = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t;
                    try
                    {
                        t = a.GetType("TransitTimetables.TimetableSchedule", false);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (t == null)
                        continue;

                    assembly = a;
                    schedule = t;
                    mod = a.GetType("TransitTimetables.Mod", false);
                    break;
                }

                if (schedule == null)
                {
                    Mod.log.Info("TransitTimetables is not loaded: a line's terminus is its last stop.");
                    return false;
                }

                // Its project sets no assembly version (it reads 1.0.0.0): the file names the installed release instead.
                string version;
                try
                {
                    version = string.IsNullOrEmpty(assembly.Location) ? "(no file)" : "(" + assembly.Location + ")";
                }
                catch (Exception)
                {
                    version = "(no file)";
                }

                s_Enabled = schedule.GetField("m_Enabled", kInstance);
                s_TerminusStop = schedule.GetField("m_TerminusStop", kInstance);
                s_ActiveSetting = mod?.GetField("ActiveSetting", BindingFlags.Public | BindingFlags.Static);
                s_SettingEnabled = s_ActiveSetting?.FieldType.GetProperty("Enabled", kInstance);
                if (!schedule.IsValueType || !typeof(IComponentData).IsAssignableFrom(schedule) ||
                    s_Enabled?.FieldType != typeof(bool) || s_TerminusStop?.FieldType != typeof(Entity) ||
                    s_SettingEnabled?.PropertyType != typeof(bool) || s_SettingEnabled.GetGetMethod() == null)
                {
                    Mod.log.Warn($"TransitTimetables {version} is loaded but its timetable is not laid out as this mod expects (written against 0.5.4): a line's terminus is its last stop.");
                    return false;
                }

                MethodInfo read = typeof(TransitTimetablesLink).GetMethod(nameof(ReadComponent), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(schedule);
                s_ReadSchedule = (Func<EntityManager, Entity, object>)Delegate.CreateDelegate(typeof(Func<EntityManager, Entity, object>), read);
                s_ScheduleType = ComponentType.ReadOnly(schedule);
                s_Usable = true;
                Mod.log.Info($"TransitTimetables {version} found: a timetabled line's terminus is the timetable's.");
            }
            catch (Exception ex)
            {
                s_Usable = false;
                Mod.log.Warn($"TransitTimetables: linking to it failed ({ex.GetType().Name}: {ex.Message}): a line's terminus is its last stop.");
            }

            return s_Usable;
        }

        private static object ReadComponent<T>(EntityManager entityManager, Entity entity) where T : unmanaged, IComponentData
        {
            return entityManager.GetComponentData<T>(entity);
        }
    }
}
