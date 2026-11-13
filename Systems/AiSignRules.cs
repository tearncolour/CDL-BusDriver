using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>What an AI bus's LED signs say (BusAiSignSystem).</summary>
    internal enum AiSignState
    {
        /// <summary>Dark: in its depot, parked, or a bus the game does not run on a line (an evacuation, a test).</summary>
        Off,
        /// <summary>The line's label and terminus.</summary>
        InService,
        /// <summary>"Not in service": on the road without a line (on its way back to the depot).</summary>
        NotInService
    }

    /// <summary>The outside LED signs: the Shanghai style (32 rows, LedHd), or the classic 16-row signs (option "LED sign style").</summary>
    public enum LedSignStyle { Shanghai = 0, Classic = 1 }

    /// <summary>What the rear sign shows on the high-density signs (RearSignRules).</summary>
    internal enum RearMessage : byte { Route, Braking, TurnLeft, TurnRight }

    /// <summary>A rear sign's message and since when, and until when a brake shown stays (RearSignRules.Next).</summary>
    internal struct RearSignState
    {
        internal RearMessage Shown;
        internal double Since, BrakeUntil;
    }

    /// <summary>
    /// The rear sign's warnings (the user, 21 October: "车尾电子牌支持显示刹车注意，车辆左/右转"): "车辆左转" / "车辆右转" while the bus
    /// indicates or (an AI bus) turns, "刹车注意" while it brakes moving and a moment after, else the route. The same rules for the
    /// driven bus (its brake pedal and indicator stalk) and the AI buses (the game's braking and turning flags of their frames).
    /// </summary>
    internal static class RearSignRules
    {
        /// <summary>"Braking" stays this long after the brake is let go or the bus has stopped (no flicker on a light brake).</summary>
        internal const double BrakeHoldSeconds = 1.5;
        /// <summary>A message stays up at least this long before a lower one replaces it (a turn comes at once).</summary>
        internal const double MinShowSeconds = 1.0;
        /// <summary>Braking counts above this speed (m/s): a bus standing on its brake shows its route.</summary>
        internal const float MovingSpeed = 1.0f;

        /// <summary>The message now; turn: -1 left, +1 right, 0 none (the hazard lamps: none).</summary>
        internal static RearMessage Next(ref RearSignState state, bool braking, int turn, float speed, double now)
        {
            if (braking && Math.Abs(speed) >= MovingSpeed)
                state.BrakeUntil = now + BrakeHoldSeconds;
            RearMessage want = turn < 0 ? RearMessage.TurnLeft : turn > 0 ? RearMessage.TurnRight : now < state.BrakeUntil ? RearMessage.Braking : RearMessage.Route;
            if (want != state.Shown)
            {
                bool urgent = want == RearMessage.TurnLeft || want == RearMessage.TurnRight || want == RearMessage.Braking && state.Shown == RearMessage.Route;
                if (urgent || now - state.Since >= MinShowSeconds)
                {
                    state.Shown = want;
                    state.Since = now;
                }
            }

            return state.Shown;
        }
    }

    /// <summary>
    /// The pure rules of the AI buses' LED signs (the user, 18 October: "the AI buses' electronic signs, as an option that can be
    /// switched off in the game to save performance"). The signs are the driven bus's (LedComposer with the same font, matrices and
    /// pitch), so a line reads the same on every bus of it; their dots are drawn as one mesh per sign and text, shared by every bus
    /// showing it, holding only the lit dots (a sign is 9 to 24 % lit).
    /// </summary>
    internal static class AiSignRules
    {
        /// <summary>
        /// The outside signs' brightness (nits) by day and by night, the driven bus's and the AI buses' (CabMaterials.DimmedLamp). The
        /// night level was 32 until 0.12.6 (the user, 21 October: "夜间的电子牌还是稍微亮了一点").
        /// </summary>
        internal const float SignNits = 500f, SignNight = 20f;

        /// <summary>The inks of the signs (sRGB): the amber text, the cyan English line, the red warning (HdInk).</summary>
        internal static readonly float3 SignAmber = new float3(1f, 0.62f, 0.12f), SignCyan = new float3(0.1f, 0.85f, 1f), SignRed = new float3(1f, 0.1f, 0.06f);

        /// <summary>A high-density sign's inks in HdInk order, the prefix in its own colour.</summary>
        internal static float3[] HdInkColours(float3 prefix) => new[] { SignAmber, prefix, SignCyan, SignRed };

        /// <summary>Dots a second a message scrolls, as the driven bus's signs.</summary>
        internal const float DotsPerSecond = 20f;

        /// <summary>A sign drawn now stays drawn this much farther (no flicker at the edge).</summary>
        internal const float DistanceHysteresis = 1.1f;

        /// <summary>
        /// The state from the vehicle: on a passenger line (its CurrentRoute names one) it shows the line; on the road without one,
        /// returning to its depot, "Not in service"; parked, disabled, or doing something else (an evacuation, a test run, dummy
        /// traffic at the city's edge), dark.
        /// </summary>
        internal static AiSignState State(bool onPassengerLine, bool returning, bool parked, bool special)
        {
            if (parked || special)
                return AiSignState.Off;
            if (onPassengerLine && !returning)
                return AiSignState.InService;
            return returning || !onPassengerLine ? AiSignState.NotInService : AiSignState.Off;
        }

        // The housing and the font grid can have different aspect ratios. Keep glyphs square and centred within the existing opening.
        internal static float2 SquareCanvas(float2 frame, int2 matrix)
        {
            float pitch = math.min(frame.x / matrix.x, frame.y / matrix.y);
            return new float2(matrix.x * pitch, matrix.y * pitch);
        }

        /// <summary>Drawn at this distance from the camera: within the option's distance, a sign already drawn up to a tenth farther.</summary>
        internal static bool Drawn(float distance, float maxDistance, bool shown)
        {
            if (float.IsNaN(distance) || float.IsNaN(maxDistance) || maxDistance <= 0f)
                return false;
            return distance <= maxDistance * (shown ? DistanceHysteresis : 1f);
        }

        /// <summary>
        /// The scroll position of a message (dots), from the game's real time: every bus showing the same text on the same sign shows
        /// the same position (one mesh for all of them). 0 when it does not scroll.
        /// </summary>
        internal static int ScrollOffset(double seconds, int cycle, bool scrolls)
        {
            if (!scrolls || cycle <= 1 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return 0;
            long dots = (long)Math.Floor(Math.Max(0.0, seconds) * DotsPerSecond);
            return (int)(dots % cycle);
        }
    }
}
