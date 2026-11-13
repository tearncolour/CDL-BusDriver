using System;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        internal static MirrorSurface GenericMirror(in BusInteriorSpec value, int index)
        {
            if ((uint)index >= 3) throw new ArgumentOutOfRangeException(nameof(index));
            BusInteriorSpec s = value.Snapped();
            bool driver = index == 0, convex = index == 2;
            float side = driver ? s.DriverSide : -s.DriverSide;
            float3 centre = new float3(side * (s.HalfWidth + (driver ? .26f : .30f)),
                s.RoofY - (driver ? .91f : convex ? 1.16f : .80f), s.FrontZ + (driver ? -.06f : .58f));
            float3 target = new float3(side * (s.HalfWidth + (convex ? .65f : .80f)),
                convex ? .45f : 1.0f, convex ? s.FrontZ - 1.4f : s.RearZ - 7f);
            float3 normal = math.normalize(math.normalize(CameraEye(s) - centre) + math.normalize(target - centre));
            float2 size = driver ? new float2(.205f, .32f) : convex ? new float2(.22f, .15f) : new float2(.235f, .38f);
            return new MirrorSurface(centre, normal, new float3(0, 1, 0), size, convex ? .012f : 0f,
                outline: convex ? null : MirrorSurface.RoundedRect(size, .027f));
        }

        void ExteriorMirrors(MeshKit k, int lod)
        {
            for (int i = 0; i < 2; i++)
            {
                bool driver = i == 0; float side = driver ? Side : -Side;
                MirrorSurface mirror = GenericMirror(Spec, i);
                float3 mount = BusBarrel.Point(Spec, driver
                    ? new float3(side * HW, Spec.RoofY - .56f, Spec.FrontZ - .295f)
                    : new float3(side * (HW - .18f), Spec.RoofY - .30f, Spec.FrontZ - .060f));
                float3 mountNormal = driver ? new float3(side, 0, 0) : new float3(0, 0, 1);
                float3 end = mirror.Centre - mirror.Normal * .045f + new float3(0, mirror.Size.y * .5f + .015f, 0);
                MirrorFoot(k, driver, side);
                using (k.Paint(Sw.DashBlack))
                using (k.Frame(mount, mountNormal, new float3(0, 1, 0)))
                {
                    k.Cylinder(new float3(0, -.03f, .035f), new float3(0, .03f, .035f), .032f, k.Sides(16));
                }
                float3 start = mount + mountNormal * .045f;
                float3 p1 = driver ? start + new float3(side * .16f, .04f, .03f) : start + new float3(side * .16f, .05f, .44f);
                float3 p2 = end + new float3(side * .04f, driver ? .10f : .24f, driver ? -.04f : .04f);
                int count = lod == 0 ? 16 : lod == 1 ? 10 : 7;
                var path = new float3[count + 1];
                for (int j = 0; j <= count; j++)
                {
                    float t = j / (float)count, u = 1f - t;
                    path[j] = u * u * u * start + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * end;
                }
                using (k.Paint(Sw.DashBlack)) Pipe(k, path, driver ? .021f : .026f, 16);
                using (k.Paint(Sw.Rubber)) k.Cylinder(end - new float3(0, .045f, 0), end + new float3(0, .035f, 0), .032f, k.Sides(16));
                MirrorPod(k, mirror, lod);
            }
            MirrorSurface lower = GenericMirror(Spec, 2), upper = GenericMirror(Spec, 1);
            using (k.Paint(Sw.DashBlack))
                k.Cylinder(upper.Centre - upper.Normal * .055f + new float3(0, -.17f, 0),
                    lower.Centre - lower.Normal * .045f + new float3(0, .065f, 0), .018f, k.Sides(12));
            MirrorPod(k, lower, lod);
        }

        void MirrorFoot(MeshKit k, bool driver, float side)
        {
            var outline = MirrorSurface.RoundedRect(new float2(driver ? .06f : .115f, .14f), .014f, 3);
            float3 Centre(float2 p) => driver
                ? new float3(side * HW, Spec.RoofY - .56f + p.y, Spec.FrontZ - .295f + p.x)
                : new float3(side * (HW - .18f) + p.x, Spec.RoofY - .30f + p.y, Spec.FrontZ - .060f);
            float3 n = driver ? new float3(side, 0, 0) : new float3(0, 0, 1);
            float3 Point(float2 p, float depth) => BusBarrel.Point(Spec, Centre(p)) + n * depth;
            using (k.Paint(Sw.DashBlack))
                for (int i = 0; i < outline.Length; i++)
                {
                    float2 a = outline[i], b = outline[(i + 1) % outline.Length];
                    float3 frontA = Point(a, .026f), frontB = Point(b, .026f), backA = Point(a, -.002f), backB = Point(b, -.002f);
                    k.QuadP(backA, frontA, frontB, backB, math.normalizesafe((frontA + frontB) * .5f - Point(float2.zero, .026f)));
                    k.Tri(k.V(Point(float2.zero, .026f), n), k.V(frontA, n), k.V(frontB, n), n);
                    k.Tri(k.V(Point(float2.zero, -.002f), -n), k.V(backA, -n), k.V(backB, -n), -n);
                }
        }

        void MirrorPod(MeshKit k, MirrorSurface mirror, int lod)
        {
            using (k.At(mirror.Centre, mirror.Frame))
            {
                // The low LODs retain the optical surface: the runtime overlay uses this same tessellation.
                int count = mirror.Segments;
                float3 backCentre = new float3(0, 0, .075f);
                for (int i = 0; i < count; i++)
                {
                    float2 a = mirror.Outline(i), b = mirror.Outline((i + 1) % count);
                    float3 frontA = new float3(a * 1.10f, .004f), frontB = new float3(b * 1.10f, .004f);
                    float3 rearA = new float3(a * .92f, .075f), rearB = new float3(b * .92f, .075f);
                    using (k.Paint(Sw.DashBlack))
                    {
                        float3 radial = math.normalizesafe(new float3(a + b, 0));
                        k.QuadP(frontA, rearA, rearB, frontB, radial);
                        int c = k.V(backCentre, new float3(0, 0, 1));
                        k.Tri(c, k.V(rearA, new float3(0, 0, 1)), k.V(rearB, new float3(0, 0, 1)), math.mul(mirror.Frame, new float3(0, 0, 1)));
                    }
                    using (k.Paint(Sw.Rubber)) k.QuadP(frontA, frontB, new float3(b, 0), new float3(a, 0), new float3(0, 0, -1));
                    using (k.Paint(Sw.Chrome))
                    {
                        int V(int edge, float radius)
                        {
                            float3 p = mirror.SurfacePoint(edge, radius);
                            return k.V(p, mirror.SurfaceNormal(p));
                        }
                        int next = (i + 1) % count;
                        float unit = 1f / mirror.RadialRings;
                        k.Tri(V(i, 0), V(i, unit), V(next, unit), mirror.Normal);
                        for (int ring = 1; ring < mirror.RadialRings; ring++)
                        {
                            int ia = V(i, ring * unit), ib = V(next, ring * unit), oa = V(i, (ring + 1) * unit), ob = V(next, (ring + 1) * unit);
                            k.Tri(ia, ib, ob, mirror.Normal); k.Tri(ia, ob, oa, mirror.Normal);
                        }
                    }
                }
            }
        }
    }
}
