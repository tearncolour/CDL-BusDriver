using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    internal static class GenericDetailUv
    {
        internal static void Triangle(int part, float3 a, float3 b, float3 c, float3 normal,
            out float2 ua, out float2 ub, out float2 uc)
        {
            if (part == 1 && Rim(a) && Rim(b) && Rim(c))
            {
                float2 phase = RimPhase(a);
                ua = phase * new float2(19, 2);
                ub = (phase + Wrap(RimPhase(b) - phase)) * new float2(19, 2);
                uc = (phase + Wrap(RimPhase(c) - phase)) * new float2(19, 2);
                return;
            }
            float3 face = math.abs(normal);
            float3 u = face.x > face.y && face.x > face.z ? new float3(0, 0, 1) : new float3(1, 0, 0);
            float3 v = face.y >= face.x && face.y >= face.z ? new float3(0, 0, 1) : new float3(0, 1, 0);
            ua = new float2(math.dot(a, u), math.dot(a, v)) / .08f;
            ub = new float2(math.dot(b, u), math.dot(b, v)) / .08f;
            uc = new float2(math.dot(c, u), math.dot(c, v)) / .08f;
        }

        // Whole repeats close both seams and remain attached to the animated wheel's local frame.
        static bool Rim(float3 p) => math.length(p.xy) > .210f && math.length(p.xy) < .260f && math.abs(p.z) < .025f;
        static float2 RimPhase(float3 p) => new float2(math.atan2(p.y, p.x),
            math.atan2(p.z / .021f, (math.length(p.xy) - .235f) / .017f)) / (2f * math.PI);
        static float2 Wrap(float2 p) => p - math.round(p);

        internal static void Basis(float3 a, float3 b, float3 c, float2 ua, float2 ub, float2 uc, out float3 u, out float3 v)
        {
            float2 d1 = ub - ua, d2 = uc - ua;
            float det = d1.x * d2.y - d1.y * d2.x;
            u = ((b - a) * d2.y - (c - a) * d1.y) / det;
            v = ((c - a) * d1.x - (b - a) * d2.x) / det;
        }
    }
}
