using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    internal static class BusBarrel
    {
        // The glazing belt is the widest section. Both shoulders have zero slope at the belt.
        internal static float Inset(BusInteriorSpec s, float y)
        {
            float belt = s.FloorY + 1.02f;
            float t = y < belt ? (belt - y) / (belt - s.FloorY + .12f) : (y - belt) / (s.RoofY - belt);
            t = math.saturate(t);
            return (y < belt ? .045f : .115f) * t * t * (3f - 2f * t);
        }

        internal static float3 Point(BusInteriorSpec s, float3 p)
        {
            // Keep central equipment rigid and preserve the optical pose of the forward mirror heads.
            float nose = math.saturate((s.FrontZ + .10f - p.z) / .10f);
            float t = math.saturate((math.abs(p.x) - .70f) / (s.HalfWidth - .70f));
            p.x -= math.sign(p.x) * Inset(s, p.y) * t * t * (3f - 2f * t) * nose * nose * (3f - 2f * nose);
            return p;
        }

        internal static float3 Normal(BusInteriorSpec s, float3 p, float3 n)
        {
            const float e = .0005f;
            float3 dx = (Point(s, p + new float3(e, 0, 0)) - Point(s, p - new float3(e, 0, 0))) / (2f * e);
            float3 dy = (Point(s, p + new float3(0, e, 0)) - Point(s, p - new float3(0, e, 0))) / (2f * e);
            float3 dz = (Point(s, p + new float3(0, 0, e)) - Point(s, p - new float3(0, 0, e))) / (2f * e);
            return math.normalizesafe(math.mul(math.transpose(math.inverse(new float3x3(dx, dy, dz))), n), n);
        }

        // Rigid mechanism centres must land on their solved line after the curved shell is generated.
        internal static float3 SourcePoint(BusInteriorSpec s, float3 target)
        {
            float low = -2f * s.HalfWidth, high = 2f * s.HalfWidth;
            float3 p = target;
            for (int i = 0; i < 26; i++)
            {
                p.x = (low + high) * .5f;
                if (Point(s, p).x < target.x) low = p.x; else high = p.x;
            }
            p.x = (low + high) * .5f;
            return p;
        }

        internal static bool MovingRigid(int part) => part >= BusInterior.SteeringWheel && part <= BusInterior.RpmNeedle ||
            part >= BusInterior.WheelFL && part <= BusInterior.WheelRR || part >= BusInterior.JointPlate || part == BusInterior.Outside;
    }
}
