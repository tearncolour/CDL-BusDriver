using Unity.Mathematics;

namespace CDLC.BusDriver.Interior
{
    /// <summary>
    /// Two-bone IK for an arm (shoulder joint a, elbow b, wrist c) in global space: turns the upper and the lower bone so the
    /// wrist reaches a target (stretched towards it when out of reach), the elbow bending towards a pole direction. The bones keep
    /// their lengths; the hand turns with the forearm. Used to put the game's citizens' hands on a strap or the steering wheel
    /// (CitizenSkeleton): the game has no clips for either.
    /// </summary>
    internal static class TwoBoneIk
    {
        /// <summary>
        /// The new global rotations of the upper bone (at a) and the lower bone (at b), from their current ones (ra, rb) and the
        /// joints' current global positions; also where the elbow and the wrist end up.
        /// </summary>
        internal static void Solve(float3 a, float3 b, float3 c, quaternion ra, quaternion rb, float3 target, float3 pole,
            out quaternion na, out quaternion nb, out float3 elbow, out float3 wrist)
        {
            float lab = math.distance(a, b), lbc = math.distance(b, c);
            float3 d = target - a;
            float dist = math.length(d);
            float3 dn = dist > 1e-5f ? d / dist : math.normalizesafe(c - a, new float3(0f, -1f, 0f));
            dist = math.clamp(dist, math.abs(lab - lbc) + 1e-4f, (lab + lbc) * 0.9995f);
            float cosA = math.clamp((lab * lab + dist * dist - lbc * lbc) / math.max(1e-6f, 2f * lab * dist), -1f, 1f);

            // the elbow's side: the pole, square to the reach; without one, the elbow's present side
            float3 side = pole - dn * math.dot(pole, dn);
            if (math.lengthsq(side) < 1e-8f)
                side = (b - a) - dn * math.dot(b - a, dn);
            if (math.lengthsq(side) < 1e-8f)
                side = math.cross(dn, math.abs(dn.y) < 0.9f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f));
            side = math.normalize(side);

            elbow = a + dn * (cosA * lab) + side * (math.sqrt(math.max(0f, 1f - cosA * cosA)) * lab);
            wrist = a + dn * dist;
            quaternion qa = FromTo(b - a, elbow - a);
            na = math.normalize(math.mul(qa, ra));
            float3 c1 = a + math.mul(qa, c - a);                // the wrist after turning the upper bone
            quaternion qb = FromTo(c1 - elbow, wrist - elbow);
            nb = math.normalize(math.mul(qb, math.mul(qa, rb)));
        }

        /// <summary>The shortest rotation that turns one direction into another.</summary>
        internal static quaternion FromTo(float3 from, float3 to)
        {
            float3 f = math.normalizesafe(from), t = math.normalizesafe(to);
            float d = math.dot(f, t);
            if (d > 0.999999f)
                return quaternion.identity;
            if (d < -0.999999f)
            {
                float3 axis = math.cross(f, new float3(1f, 0f, 0f));
                if (math.lengthsq(axis) < 1e-6f)
                    axis = math.cross(f, new float3(0f, 1f, 0f));
                return quaternion.AxisAngle(math.normalize(axis), math.PI);
            }
            return quaternion.AxisAngle(math.normalize(math.cross(f, t)), math.acos(d));
        }
    }
}
