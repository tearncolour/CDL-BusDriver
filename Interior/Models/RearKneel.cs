using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// What the rear section of an articulated pair takes from the kneel Core gave the front body only. The kneel is a rotation
    /// about the front's origin (pitch, then roll about +Z) and a drop in the knelt frame. The sections share axes (rest
    /// rotations identity, the rear's origin is its anchor), so the rear takes the roll alone, the twist of the kneel about +Z
    /// (the front's pitch stays with the front: the joint bends there), and moves with the front's anchor, which keeps the pair
    /// joined while the whole bus leans to the kerb.
    /// </summary>
    public static class RearKneel
    {
        /// <param name="frontRotation">the front body's rotation as drawn (the kneel already applied)</param>
        /// <param name="kneel">the kneel rotation (Core's GetKneelTransform)</param>
        /// <param name="drop">the kneel's offset, in the knelt frame (Core's GetKneelTransform)</param>
        /// <param name="frontAnchor">the front's anchor in its own frame</param>
        /// <param name="delta">how far the kneel moved the front's anchor, in the world: the rear body moves by the same</param>
        /// <param name="roll">the kneel's twist about +Z</param>
        public static void Compute(quaternion frontRotation, quaternion kneel, float3 drop, float3 frontAnchor,
            out float3 delta, out quaternion roll)
        {
            quaternion unknelt = math.mul(frontRotation, math.inverse(kneel));
            delta = math.rotate(frontRotation, drop + frontAnchor) - math.rotate(unknelt, frontAnchor);
            float z = kneel.value.z, w = kneel.value.w, n = math.sqrt(z * z + w * w);
            roll = n > 1e-6f ? new quaternion(0f, 0f, z / n, w / n) : quaternion.identity;
        }

        /// <summary>The rear body's offset from its unkneeled place in its own (rolled) frame, as the wheels' counter-offset needs.</summary>
        public static float3 LocalOffset(quaternion rearRotation, float3 delta) => math.rotate(math.inverse(rearRotation), delta);
    }
}
