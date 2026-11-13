using System;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The boarding zone at a stop and the boarding count shown while the doors are open (pure, tested in CDLC.BusDriver.Tests).
    /// The zone is one long box along the road at the stop, from well behind the whole bus to a few metres past the stop
    /// point, and the bus is in it when its middle and its front door both are: there is no stop line to pull up to and no
    /// heading test (a bus reversed into the box, or standing at an angle that keeps both points in it, can open). Across the
    /// road the box keeps the bus in the stop lane: the middle up to 1.8 m from the axis towards the kerb (closer to the stop
    /// is fine) and 1.2 m towards the traffic, 1.4 m either way where the kerb side is unknown; the front door 0.4 m more.
    /// </summary>
    internal static class BusStopZone
    {
        // The box: 6 m past the stop point, and behind it the whole bus and 10 m more (24 .. 44 m).
        internal const float kAheadMeters = 6f;
        internal const float kBehindMinMeters = 24f;
        internal const float kBehindMaxMeters = 44f;
        internal const float kBehindPastBus = 10f;

        // The front door is about 1.5 m behind the bus front.
        internal const float kFrontDoorBehindFront = 1.5f;
        internal const float kLateralTolerance = 1.4f;
        internal const float kLateralKerb = 1.8f;
        internal const float kLateralTraffic = 1.2f;
        internal const float kFrontDoorSlack = 0.4f;

        // The stop's own level: a stop on a bridge above or a road below does not count.
        internal const float kLevelMeters = 3f;

        // Driving past the next stop: with nobody to pick up or set down it counts as skipped once the front door is 3 m past
        // the box (the front 10.5 m past the stop point), otherwise as missed 25 m past it, room to back up into the box.
        internal const float kPassedSkipMeters = kAheadMeters + kFrontDoorBehindFront + 3f;
        internal const float kPassedMissMeters = 25f;

        /// <summary>How far the box reaches behind the stop point for a bus (all its sections) of this length.</summary>
        internal static float Behind(float rigLength)
        {
            if (!(rigLength > 0f) || float.IsInfinity(rigLength))
                return kBehindMinMeters;
            return math.clamp(rigLength + kBehindPastBus, kBehindMinMeters, kBehindMaxMeters);
        }

        /// <summary>Within the box lengthwise: from <paramref name="behind"/> behind the stop point to 6 m past it.</summary>
        internal static bool Along(float along, float behind) => along >= -behind && along <= kAheadMeters;

        /// <summary>Near the box axis: more room towards the kerb than towards the traffic (the same both ways where the kerb side is unknown).</summary>
        internal static bool Across(float lateral, float kerbSide, float slack)
        {
            if (kerbSide == 0f)
                return math.abs(lateral) <= kLateralTolerance + slack;

            float towardsKerb = lateral * kerbSide;
            return towardsKerb <= kLateralKerb + slack && -towardsKerb <= kLateralTraffic + slack;
        }

        /// <summary>
        /// The bus in the box: its middle and its front door, each within the box lengthwise and near its axis. The stop is
        /// given by its point, the road's direction there and the right of that direction, all flat.
        /// </summary>
        internal static bool Inside(float2 stop, float2 direction, float2 right, float kerbSide, float behind, float2 middle, float2 frontDoor)
        {
            return Along(math.dot(middle - stop, direction), behind) && Along(math.dot(frontDoor - stop, direction), behind) &&
                Across(math.dot(middle - stop, right), kerbSide, 0f) && Across(math.dot(frontDoor - stop, right), kerbSide, kFrontDoorSlack);
        }

        /// <summary>
        /// How far the bus has to move along the box (+ the way the road runs at the stop, − back) for its middle and its
        /// front door both to be in the box lengthwise; 0 when they are.
        /// </summary>
        internal static float Shift(float middleAlong, float doorAlong, float behind)
        {
            float onward = -behind - math.min(middleAlong, doorAlong);
            if (onward > 0f)
                return onward;
            float back = kAheadMeters - math.max(middleAlong, doorAlong);
            return back < 0f ? back : 0f;
        }

        /// <summary>The same move in the bus's own terms (+ drive on, − reverse): the bus may stand either way round in the box.</summary>
        internal static float Move(float shift, float headingAlongBox) => headingAlongBox >= 0f ? shift : -shift;

        /// <summary>Riders still at the stop: the game's last count less the riders who have joined the bus since it was taken.</summary>
        internal static int StillWaiting(int counted, int joinedSince) => Math.Max(0, counted - Math.Max(0, joinedSince));

        /// <summary>
        /// Riders still to board: those walking to the doors (on the bus's list, not yet inside) and those still waiting that
        /// the bus has room for (capacity less the people aboard, the walking ones included); the rest stays behind.
        /// </summary>
        internal static int ToBoard(int walking, int stillWaiting, int people, int capacity, out int leftBehind)
        {
            walking = Math.Max(0, walking);
            stillWaiting = Math.Max(0, stillWaiting);
            int room = capacity > 0 ? Math.Max(0, capacity - Math.Max(0, people)) : stillWaiting;
            int boarding = Math.Min(stillWaiting, room);
            leftBehind = stillWaiting - boarding;
            return walking + boarding;
        }

        /// <summary>
        /// The stop's waiting count as it is now. The game counts the riders waiting at a stop every 256 simulation frames
        /// (WaitingPassengersSystem, about 4 s), so between two counts it still holds the riders who have joined the bus since;
        /// they are taken off it until the next count. A new count shows as a change of the count or of its waiting-time sum
        /// (m_OngoingAccumulation), which only that count writes and which grows while anybody waits.
        /// </summary>
        internal struct WaitingCount
        {
            private bool m_Has;
            private int m_Count;
            private int m_WaitingSum;
            private int m_JoinedAt;

            internal void Reset() => m_Has = false;

            internal int Observe(int count, int waitingSum, int joined)
            {
                if (!m_Has || count != m_Count || waitingSum != m_WaitingSum)
                {
                    m_Has = true;
                    m_Count = count;
                    m_WaitingSum = waitingSum;
                    m_JoinedAt = joined;
                }

                return StillWaiting(m_Count, joined - m_JoinedAt);
            }
        }
    }
}
