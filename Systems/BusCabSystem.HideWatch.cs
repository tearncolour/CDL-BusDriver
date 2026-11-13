using System.Text;
using CDLC.BusDriver.Interior;
using Colossal.Entities;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// A closed native door seen in the doorway of a ridden bus whose body the driving mod hides (owner, 2 Nov 2026). The log of the
    /// ride showed two things (Bus Driver 0.15.4): the game's <c>Hidden</c> was gone from the front section 1.4 s after the driving mod put
    /// it on and again when the doors were fully open, and another bus of the same model lay 0.0 m from the ridden one for the whole ride.
    /// While the native body should be hidden this
    ///  - puts <c>Hidden</c> back at once, every frame, on every section that has lost it (what the driving mod does to hide the body), and
    ///    writes what else was on the entity that frame;
    ///  - once a second counts the mesh batches of each section and the instances of our model that are alive;
    ///  - when a door starts to open, and every few seconds, looks for other buses lying on this one and writes where each is, how it
    ///    moves, its boarding state, its lane against this one's, and whether it is hidden.
    /// A line is written when what was found changes, and one per ride at its end.
    /// </summary>
    public sealed partial class BusCabSystem
    {
        private const float kHideWatchSeconds = 1f, kHideWatchSettleSeconds = 1.5f, kHideWatchScanSeconds = 5f, kHideWatchOverlapMeters = 8f;
        private const int kHideWatchLogCap = 120, kHideWatchGiveUp = 600;

        private EntityQuery m_HideWatchBuses;
        private Entity m_HideWatchBus;
        private float m_HideWatchNext, m_HideWatchScanNext, m_HideWatchSettle;
        private bool m_HideWatchDoorsOpen, m_HideWatchBusesMade, m_HideWatchGaveUp;
        private int m_HideWatchCamera, m_HideWatchLostFrame;
        private int m_HideWatchLogs, m_HideWatchChecks, m_HideWatchLost, m_HideWatchBatchesMin, m_HideWatchBatchesMax;
        private int m_HideWatchInstancesMax, m_HideWatchNearMax;
        private string m_HideWatchLast = "", m_HideWatchLastNear = "";

        /// <summary>Called every frame from Run after the body's hiding is requested of the driving mod.</summary>
        private void WatchNativeBody()
        {
            if (!NativeBodyShouldBeHidden())
            {
                HideWatchEnd();
                return;
            }

            // the driving mod applies a request for the body's hiding in its next update, and a change of camera changes what it hides
            float now = UnityEngine.Time.unscaledTime;
            if (m_HideWatchBus != m_Bus)
            {
                HideWatchEnd();
                m_HideWatchBus = m_Bus;
                m_HideWatchSettle = now + kHideWatchSettleSeconds;
                m_HideWatchNext = m_HideWatchScanNext = 0f;
                m_HideWatchDoorsOpen = false;
                m_HideWatchCamera = m_Core.Camera;
            }
            else if (m_Core.Camera != m_HideWatchCamera)
            {
                m_HideWatchCamera = m_Core.Camera;
                m_HideWatchSettle = now + kHideWatchSettleSeconds;
            }

            if (now < m_HideWatchSettle)
                return;

            KeepNativeBodyHidden();

            bool doorsOpen = m_HideWatchDoorsOpen ? m_Doors > 0.01f : m_Doors > 0.05f;
            bool opened = doorsOpen && !m_HideWatchDoorsOpen;
            m_HideWatchDoorsOpen = doorsOpen;
            if (!opened && now < m_HideWatchNext)
                return;

            m_HideWatchNext = now + kHideWatchSeconds;
            CheckNativeBody(opened ? "a door started to open" : "periodic", opened);
            if (opened || now >= m_HideWatchScanNext)
            {
                m_HideWatchScanNext = now + kHideWatchScanSeconds;
                ScanForBusesOnThisOne(opened ? "a door started to open" : "periodic", opened);
            }
        }

        /// <summary>
        /// The driving mod hides the body in the driver view on a seat and, when the add-on draws the whole bus, in every view (NativeBodyRules); in the chase view and with
        /// a camera fixed on the outside it shows the body, and this watch must not hide it again.
        /// </summary>
        private bool NativeBodyShouldBeHidden() =>
            m_Bus != Entity.Null && m_Sections.Count != 0 && NativeBodyRules.ShouldBeHidden(m_Core.Viewing, m_WholeHidden, m_BodyHidden, m_Core.CabView);

        /// <summary>
        /// Every frame, a component lookup a section: <c>Hidden</c> put back where something took it off. After kHideWatchGiveUp losses in one
        /// ride it stops (something takes it off every frame: adding it back each time would only move the entity between archetypes).
        /// </summary>
        private void KeepNativeBodyHidden()
        {
            if (m_HideWatchGaveUp)
                return;

            foreach (Section section in m_Sections)
            {
                Entity entity = section.Entity;
                if (entity == Entity.Null || !EntityManager.Exists(entity) || EntityManager.HasComponent<Hidden>(entity))
                    continue;

                int frame = UnityEngine.Time.frameCount;
                m_HideWatchLost++;
                string flags = FlagsOn(entity);
                EntityManager.AddComponent<Hidden>(entity);
                EntityManager.AddComponent<BatchesUpdated>(entity);
                HideWatchLog(true, $"Hidden was gone from {entity} ({(m_Core.Riding ? "riding" : "driving")}, {m_Core.Speed * 3.6f:0} km/h, doors {m_Doors:0.00}), " +
                    $"{(m_HideWatchLostFrame > 0 ? frame - m_HideWatchLostFrame + " frames after the last loss" : "the first loss of this ride")}, loss {m_HideWatchLost}; also on it: {flags}; put back.");
                m_HideWatchLostFrame = frame;
                if (m_HideWatchLost >= kHideWatchGiveUp)
                {
                    m_HideWatchGaveUp = true;
                    HideWatchLog(true, $"Hidden was gone {kHideWatchGiveUp} times in this ride: not put back any more.");
                    return;
                }
            }
        }

        /// <summary>The marks of the game's own update and tool systems on an entity this frame, which tell what was working on it.</summary>
        private string FlagsOn(Entity entity)
        {
            var flags = new StringBuilder();
            if (EntityManager.HasComponent<Updated>(entity)) flags.Append("Updated ");
            if (EntityManager.HasComponent<BatchesUpdated>(entity)) flags.Append("BatchesUpdated ");
            if (EntityManager.HasComponent<Created>(entity)) flags.Append("Created ");
            if (EntityManager.HasComponent<Applied>(entity)) flags.Append("Applied ");
            if (EntityManager.HasComponent<Overridden>(entity)) flags.Append("Overridden ");
            if (EntityManager.HasComponent<Deleted>(entity)) flags.Append("Deleted ");
            if (EntityManager.HasComponent<Temp>(entity)) flags.Append("Temp ");
            if (EntityManager.HasComponent<Highlighted>(entity)) flags.Append("Highlighted ");
            return flags.Length > 0 ? flags.ToString().TrimEnd() : "none of Updated, BatchesUpdated, Created, Applied, Overridden, Deleted, Temp, Highlighted";
        }

        private void CheckNativeBody(string reason, bool reassert)
        {
            var found = new StringBuilder();
            int batches = 0;
            foreach (Section section in m_Sections)
            {
                Entity entity = section.Entity;
                if (entity == Entity.Null || !EntityManager.Exists(entity))
                    continue;
                int count = EntityManager.TryGetBuffer(entity, true, out DynamicBuffer<MeshBatch> meshBatches) ? meshBatches.Length : -1;
                batches += math.max(count, 0);
                if (reassert)
                    EntityManager.AddComponent<BatchesUpdated>(entity);

                found.Append(entity).Append(EntityManager.HasComponent<Hidden>(entity) ? " Hidden" : " not hidden").Append(", ").Append(count).Append(" mesh batches; ");
            }

            int instances = CabInstance.Live;
            m_HideWatchChecks++;
            m_HideWatchBatchesMin = m_HideWatchChecks == 1 ? batches : math.min(m_HideWatchBatchesMin, batches);
            m_HideWatchBatchesMax = math.max(m_HideWatchBatchesMax, batches);
            m_HideWatchInstancesMax = math.max(m_HideWatchInstancesMax, instances);

            string state = found + "our instances alive " + instances;
            if (state == m_HideWatchLast && !reassert)
                return;

            m_HideWatchLast = state;
            HideWatchLog(false, $"Native body check ({reason}; {(m_Core.Riding ? "riding" : "driving")}, {m_Core.Speed * 3.6f:0} km/h, doors {m_Doors:0.00}): {state}" +
                (reassert ? "; BatchesUpdated added so the game reads Hidden again" : ""));
        }

        /// <summary>Other buses within a few metres of this one: a second bus lying on it would show through the doorway once our door opens.</summary>
        private void ScanForBusesOnThisOne(string reason, bool always)
        {
            if (!EntityManager.TryGetComponent(m_Bus, out Transform own))
                return;
            if (!m_HideWatchBusesMade)
            {
                m_HideWatchBuses = GetEntityQuery(ComponentType.ReadOnly<Game.Vehicles.PublicTransport>(), ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<PrefabRef>());
                m_HideWatchBusesMade = true;
            }

            EntityManager.TryGetComponent(m_Bus, out PrefabRef ownPrefab);
            EntityManager.TryGetComponent(m_Bus, out CarCurrentLane ownLane);
            float ownSpeed = EntityManager.TryGetComponent(m_Bus, out Moving ownMoving) ? math.length(ownMoving.m_Velocity) : 0f;
            PrefabSystem prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            int near = 0;
            var found = new StringBuilder();
            var signature = new StringBuilder();
            using (NativeArray<Entity> buses = m_HideWatchBuses.ToEntityArray(Allocator.Temp))
            using (NativeArray<Transform> poses = m_HideWatchBuses.ToComponentDataArray<Transform>(Allocator.Temp))
            using (NativeArray<PrefabRef> refs = m_HideWatchBuses.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                for (int i = 0; i < buses.Length; i++)
                {
                    Entity other = buses[i];
                    if (IsOneOfOurSections(other))
                        continue;
                    float distance = math.distance(poses[i].m_Position, own.m_Position);
                    if (distance > kHideWatchOverlapMeters)
                        continue;
                    near++;
                    if (near > 4)
                        continue;

                    string name = "?";
                    try { name = prefabs != null ? prefabs.GetPrefabName(refs[i].m_Prefab) : "?"; } catch { }
                    float speed = EntityManager.TryGetComponent(other, out Moving moving) ? math.length(moving.m_Velocity) : 0f;
                    string boarding = EntityManager.TryGetComponent(other, out Game.Vehicles.PublicTransport transport) ? transport.m_State.ToString() : "?";
                    bool hasLane = EntityManager.TryGetComponent(other, out CarCurrentLane lane);
                    bool sameLane = hasLane && lane.m_Lane != Entity.Null && lane.m_Lane == ownLane.m_Lane;
                    float yaw = math.degrees(math.acos(math.clamp(math.dot(math.forward(poses[i].m_Rotation), math.forward(own.m_Rotation)), -1f, 1f)));
                    bool hidden = EntityManager.HasComponent<Hidden>(other);
                    bool sameModel = refs[i].m_Prefab == ownPrefab.m_Prefab;
                    found.Append(other).Append(" '").Append(name).Append("' ").Append(distance.ToString("0.00")).Append(" m, ").Append(yaw.ToString("0")).Append(" deg apart, ")
                        .Append(speed.ToString("0.0")).Append(" m/s (the ridden one ").Append(ownSpeed.ToString("0.0")).Append("), state ").Append(boarding)
                        .Append(sameLane ? ", the same lane" : hasLane ? ", lane " + lane.m_Lane : ", no lane").Append(sameModel ? ", same model" : "").Append(hidden ? ", Hidden" : ", not hidden").Append("; ");
                    signature.Append(other).Append(sameLane).Append(speed > 0.3f).Append(boarding).Append(hidden).Append(distance < 1f).Append(';');
                }
            }

            m_HideWatchNearMax = math.max(m_HideWatchNearMax, near);
            string now = signature.ToString();
            if (near > 0)
            {
                if (now != m_HideWatchLastNear || always)
                    HideWatchLog(true, $"Other bus(es) within {kHideWatchOverlapMeters:0} m of the one {(m_Core.Riding ? "ridden" : "driven")} ({reason}): {near}: {found}");
            }
            else if (m_HideWatchLastNear.Length > 0 || always)
            {
                HideWatchLog(false, $"No other bus within {kHideWatchOverlapMeters:0} m of the one {(m_Core.Riding ? "ridden" : "driven")} ({reason}).");
            }

            m_HideWatchLastNear = now;
        }

        private bool IsOneOfOurSections(Entity entity)
        {
            foreach (Section section in m_Sections)
                if (section.Entity == entity)
                    return true;
            return false;
        }

        private void HideWatchLog(bool warn, string line)
        {
            if (m_HideWatchLogs >= kHideWatchLogCap)
                return;
            if (++m_HideWatchLogs == kHideWatchLogCap)
                line += " (the log of this check stops here for the session)";
            if (warn) Mod.log.Warn(line); else Mod.log.Info(line);
        }

        /// <summary>One line at the end of each ride or drive.</summary>
        private void HideWatchEnd()
        {
            if (m_HideWatchBus == Entity.Null)
                return;

            HideWatchLog(m_HideWatchLost > 0 || m_HideWatchNearMax > 0,
                $"Native body check of {m_HideWatchBus} ended: {m_HideWatchChecks} checks, Hidden was gone {m_HideWatchLost} times (put back at once), " +
                $"mesh batches {m_HideWatchBatchesMin}..{m_HideWatchBatchesMax}, our instances alive at most {m_HideWatchInstancesMax}, " +
                $"other buses within {kHideWatchOverlapMeters:0} m at most {m_HideWatchNearMax}.");
            m_HideWatchBus = Entity.Null;
            m_HideWatchChecks = m_HideWatchLost = m_HideWatchBatchesMax = m_HideWatchInstancesMax = m_HideWatchNearMax = m_HideWatchLostFrame = 0;
            m_HideWatchBatchesMin = 0;
            m_HideWatchLast = m_HideWatchLastNear = "";
            m_HideWatchGaveUp = false;
        }
    }
}
