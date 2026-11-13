using System;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// The rear section's share of the kneel (RearKneel): the roll alone, and the pair stays joined.
static class RearKneelTests
{
    public static int Run(Action<bool, string> check)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; check(ok, "rear kneel: " + what); }

        float3 frontAnchor = new float3(0f, 0f, -1.32f);
        quaternion Euler(float x, float y, float z) => quaternion.EulerZXY(math.radians(x), math.radians(y), math.radians(z));
        foreach (float e in new[] { 0.2f, 1f })
        foreach (float kerb in new[] { 1f, -1f })
        {
            // Core.Kneel: Euler(0.35 e, 0, -kerb 1.6 e), drop 0.03 e in the knelt frame, applied to a body anywhere in the world.
            quaternion k = Euler(0.35f * e, 0f, -kerb * 1.6f * e);
            float3 drop = new float3(0f, -0.03f * e, 0f);
            quaternion bodyRotation = quaternion.EulerZXY(math.radians(4f), math.radians(63f), math.radians(-2f));
            float3 bodyPosition = new float3(120f, 7f, -340f);
            quaternion knelt = math.mul(bodyRotation, k);
            float3 kneltPosition = bodyPosition + math.rotate(knelt, drop);
            RearKneel.Compute(knelt, k, drop, frontAnchor, out float3 delta, out quaternion roll);

            // the front's anchor, drawn before and after the kneel, and the rear's origin (= its anchor) moved by delta
            float3 anchorBefore = bodyPosition + math.rotate(bodyRotation, frontAnchor);
            float3 anchorAfter = kneltPosition + math.rotate(knelt, frontAnchor);
            Check(math.distance(anchorBefore + delta, anchorAfter) < 1e-4f, "the rear anchor follows the front's anchor (" + e + ", " + kerb + ")");
            Check(math.length(delta) < 0.05f, "the anchor moves a few centimetres, not metres: " + math.length(delta));

            quaternion expected = Euler(0f, 0f, -kerb * 1.6f * e);
            Check(math.abs(math.dot(roll.value, expected.value)) > 0.99999f, "the roll is the kneel's twist about +Z (" + e + ", " + kerb + ")");

            // the wheels' counter-transform: a point at the unkneeled axle stays where it was in the world
            quaternion rearRotation = math.mul(math.mul(bodyRotation, quaternion.identity), roll);
            float3 rearPosition = bodyPosition + delta;
            float3 off = RearKneel.LocalOffset(rearRotation, delta);
            float3 pivot = new float3(1.0f, 0.49f, -3.1f);
            float3 world = rearPosition + math.rotate(rearRotation, math.rotate(math.inverse(roll), pivot) - off);
            float3 unkneltWorld = bodyPosition + math.rotate(bodyRotation, pivot);
            Check(math.distance(world, unkneltWorld) < 1e-4f, "the rear wheels stay on the road: " + math.distance(world, unkneltWorld));
        }

        RearKneel.Compute(quaternion.identity, quaternion.identity, float3.zero, frontAnchor, out float3 none, out quaternion noRoll);
        Check(math.length(none) < 1e-6f && math.abs(noRoll.value.w) > 0.999999f, "no kneel moves nothing");
        return fails;
    }
}
