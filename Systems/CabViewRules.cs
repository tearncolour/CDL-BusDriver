using Unity.Mathematics;

namespace CDLC.BusDriver
{
    /// <summary>
    /// The driver can move the camera of the driver's view from the model's seat: up or down, left or right (the options Cab eye height and Cab eye side, in centimetres).
    /// This is only what turns the two numbers into a step in the bus's model space; it is added where the eye is handed to the driving mod, and the model's own eye
    /// (BusInterior.Eye, what the mirrors are set up from) is never changed.
    /// </summary>
    internal static class CabViewRules
    {
        /// <summary>How far the eye may be moved from the seat either way, in centimetres: far enough for a tall or a short driver, not out through the window or the roof.</summary>
        internal const float MaxOffsetCm = 25f;

        /// <summary>
        /// The step from the model's eye in the bus's model space (Unity: +X to the right of someone who faces the front, +Y up, +Z forward, so the same for a bus that is
        /// driven from the left and for one driven from the right). <paramref name="heightCm"/> up is positive, <paramref name="sideCm"/> right is positive. Each is held
        /// to the range (a settings file can be edited by hand); zero for both is no step.
        /// </summary>
        internal static float3 Offset(int heightCm, int sideCm) => new float3(Clamp(sideCm), Clamp(heightCm), 0f) * 0.01f;

        /// <summary>The option's number as it is used: held to the range.</summary>
        internal static float Clamp(int centimetres) => math.clamp((float)centimetres, -MaxOffsetCm, MaxOffsetCm);
    }
}
