using System;

namespace CDLC.BusDriver
{
    /// <summary>What a stop announcement is made from, one frame of it: the shift's state (BusState) of the bus the player drives, or the ride's (BusRide) of the bus the player rides.</summary>
    internal struct AnnounceFacts
    {
        /// <summary>The index of the stop the bus heads for among the line's; -1 for none. Only a change of it matters.</summary>
        internal int Target;
        /// <summary>That stop is known by a name.</summary>
        internal bool StopValid;
        /// <summary>Under way with the doors shut.</summary>
        internal bool Driving;
        /// <summary>The doors are opening: the frame an arrival is announced on (for a ride, the doors are open).</summary>
        internal bool Opening;
        /// <summary>The doors are opening or open.</summary>
        internal bool AtStop;
        /// <summary>The bus is within 150 m of the stop, and not on it.</summary>
        internal bool InRange;
    }

    /// <summary>What is due this frame, from <see cref="AnnounceRules.Step"/>.</summary>
    [Flags]
    internal enum AnnounceDue : byte
    {
        None = 0,
        /// <summary>"Next stop X": the target moved on and the bus is under way.</summary>
        Next = 1,
        /// <summary>"Approaching X": within 150 m, once for each target.</summary>
        Arriving = 2,
        /// <summary>"X": the doors opened at the stop.</summary>
        AtStop = 4,
    }

    /// <summary>What the announcements remember between frames.</summary>
    internal struct AnnounceState
    {
        internal int LastTarget;
        internal bool ArrivalDone, NextPending, LastAtStop;
    }

    /// <summary>
    /// Which bus the announcements follow: a shift's or a ride's, on which line, which vehicle (entities as index and version). Another bus on
    /// the same line, even heading for the same stop, is a new source: what was queued for the old one is dropped and the rules begin again.
    /// </summary>
    internal readonly struct AnnounceSource : IEquatable<AnnounceSource>
    {
        internal readonly bool Active, Ride;
        internal readonly long Line, Bus;

        internal AnnounceSource(bool active, bool ride, long line, long bus)
        {
            Active = active; Ride = active && ride; Line = active ? line : 0; Bus = active ? bus : 0;
        }

        public bool Equals(AnnounceSource other) => Active == other.Active && Ride == other.Ride && Line == other.Line && Bus == other.Bus;
        public override bool Equals(object obj) => obj is AnnounceSource other && Equals(other);
        public override int GetHashCode() => unchecked(((Line.GetHashCode() * 397) ^ Bus.GetHashCode()) * 4 + (Active ? 2 : 0) + (Ride ? 1 : 0));
    }

    /// <summary>
    /// When a stop announcement is due, from the facts of a shift or of a ride alone (BusAnnouncementSystem makes the sentences and plays them). Taken out of the system
    /// so that both are held to the same rules and the rules can be tested without the game; for a shift they are what the system always did.
    /// </summary>
    internal static class AnnounceRules
    {
        /// <summary>"Approaching X" is said from this far (metres, by the road).</summary>
        internal const float ArrivingMeters = 150f;

        /// <summary>The facts of a shift's frame. <paramref name="distanceMeters"/>: to the stop, 0 or less when it is not known.</summary>
        internal static AnnounceFacts ShiftFacts(int target, bool stopValid, bool driving, bool opening, bool atStop, float distanceMeters) => new AnnounceFacts
        {
            Target = target, StopValid = stopValid, Driving = driving, Opening = opening, AtStop = atStop, InRange = distanceMeters > 0f && distanceMeters < ArrivingMeters,
        };

        /// <summary>
        /// The facts of a ride's frame. The bus is under way while its doors are shut; the stop it heads for is the one it boards at (it keeps it as its target until the
        /// boarding is over). <paramref name="approaching"/> is the ride system's "within 150 m", <paramref name="shownDistance"/> its distance in metres, 0 when not known.
        /// </summary>
        internal static AnnounceFacts RideFacts(int target, bool hasRoute, string next, bool doorsOpen, bool approaching, int shownDistance) => new AnnounceFacts
        {
            Target = target, StopValid = hasRoute && !string.IsNullOrEmpty(next), Driving = !doorsOpen, Opening = doorsOpen, AtStop = doorsOpen, InRange = approaching && shownDistance > 0,
        };

        /// <summary>Whether the frame's source is another than the one followed (or none): the queue is dropped and <see cref="Begin"/> starts again.</summary>
        internal static bool Switched(in AnnounceSource now, in AnnounceSource before) => !now.Active || !now.Equals(before);

        /// <summary>The bus the announcements follow is a new one (a shift or a ride began, another bus, or its line changed): nothing is announced for what is already so.</summary>
        internal static void Begin(ref AnnounceState s, in AnnounceFacts f)
        {
            s.LastTarget = -1;
            s.ArrivalDone = false;
            s.NextPending = false;
            s.LastAtStop = f.AtStop;
        }

        /// <param name="settled">The first moments after a shift or a ride began are over (the rules still follow the target while they last).</param>
        internal static AnnounceDue Step(ref AnnounceState s, in AnnounceFacts f, bool settled)
        {
            AnnounceDue due = AnnounceDue.None;
            if (f.Target != s.LastTarget)
            {
                s.LastTarget = f.Target;
                s.ArrivalDone = false;
                s.NextPending = f.Target >= 0 && f.StopValid;
            }

            // "Next stop": when the target moved on and the bus is on its way (never while the doors are open).
            if (s.NextPending && f.Driving && settled)
            {
                s.NextPending = false;
                due |= AnnounceDue.Next;
            }

            // Approaching: once per target, within 150 m, while driving.
            if (!s.ArrivalDone && f.Driving && f.StopValid && settled && f.InRange)
            {
                s.ArrivalDone = true;
                due |= AnnounceDue.Arriving;
            }

            // The doors open at a stop.
            if (f.Opening && !s.LastAtStop)
                due |= AnnounceDue.AtStop;

            s.LastAtStop = f.AtStop;
            return due;
        }
    }
}
