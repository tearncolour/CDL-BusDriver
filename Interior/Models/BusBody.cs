using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// The box a bus section's body collides with, for the driving mod (Core API 11, SetVehicleBody): the profile's body ends and half
    /// width, which leave out the mirrors. The game's own bounds take in the whole mesh, so a bus with long mirror arms collided 0.5 m
    /// wider than its body over its whole length (the SWB6710EV01G: a 2.35 m body in a 2.9 m box).
    /// </summary>
    internal static class BusBody
    {
        /// <summary>The body box of a section in its prefab's frame (x across, y up from the ground, z forward); false for a spec that has no usable body.</summary>
        internal static bool Box(in BusInteriorSpec spec, out float3 min, out float3 max)
        {
            min = new float3(-spec.HalfWidth, 0f, spec.RearZ);
            max = new float3(spec.HalfWidth, spec.RoofY, spec.FrontZ);
            return spec.HalfWidth >= 0.5f && spec.HalfWidth <= 1.6f && spec.FrontZ - spec.RearZ >= 1f && spec.FrontZ - spec.RearZ <= 30f && spec.RoofY > 0f;
        }
    }
}
