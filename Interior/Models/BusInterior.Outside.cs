using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Exterior mirrors use separate driver-side and door-side mounts; wipers follow the lower windscreen.
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        /// <summary>
        /// The main glass of an exterior mirror (side -1: the left one, +1: the right one), model space: its centre 0.5 mm in front
        /// of the glass, the frame its face looks along -Z of, its size (CabMirrors puts the mirror image there).
        /// </summary>
        public static void MirrorGlass(in BusInteriorSpec spec, int side, out float3 centre, out quaternion frame, out float2 size)
        {
            var bus = new BusInterior(spec.Snapped());
            float s = side < 0 ? -1f : 1f;
            if (bus.IsAuthored)
            {
                MirrorSurface mirror = MirrorSurface.Of(bus.Spec, side < 0 ? 0 : 1);
                centre = mirror.OverlayCentre;
                frame = mirror.Frame;
                size = mirror.Size;
                return;
            }
            MirrorSurface generic = GenericMirror(bus.Spec, s == bus.Side ? 0 : 1);
            centre = generic.OverlayCentre; frame = generic.Frame; size = generic.Size;
        }
        void DefineOutside(MeshKit k, int lod)
        {
            ExteriorMirrors(k, lod);
            // The arm meets the blade's centre adaptor. The rubber follows the glass rake at its parked height.
            float3 OnGlass(float x, float y, float off) => BusBarrel.Point(Spec, new float3(x, y, WsZ(y) + off));
            for (int i = 0; i < 2; i++)
            {
                float span = WsHalf - 0.16f;
                float px = (i == 0 ? -WsHalf + 0.14f : 0.04f) * -Side, dx = -Side * span, rise = 0.055f;
                float3 piv = OnGlass(px, WsBotY - 0.025f, 0.026f);
                float3 adaptor = OnGlass(px + 0.52f * dx, WsBotY + 0.065f + 0.52f * rise, 0.030f);
                using (k.Paint(Sw.Black))
                {
                    k.Cylinder(piv, piv + new float3(0f, 0f, 0.025f), 0.026f, k.Sides(12));
                    k.BoxAlong(piv + new float3(0f, 0f, 0.022f), adaptor, new float2(0.018f, 0.010f), new float3(0f, 0f, 1f));
                    k.BoxAlong(adaptor - new float3(0.032f, 0f, 0f), adaptor + new float3(0.032f, 0f, 0f), new float2(0.022f, 0.022f), new float3(0f, 0f, 1f));
                }

                // blade: 0.9 m of rubber on a black spine, its whole length two centimetres off the opening (clear of the glass)
                const int n = 6;
                var spine = new float3[n + 1];
                for (int j = 0; j <= n; j++)
                {
                    float t = 0.08f + 0.92f * j / n;
                    spine[j] = OnGlass(px + t * dx, WsBotY + 0.065f + t * rise, 0.020f);
                }
                using (k.Paint(Sw.Black)) Pipe(k, spine, 0.007f, 8);
                using (k.Paint(Sw.Rubber)) k.BoxAlong(spine[0] - new float3(0f, 0f, 0.010f), spine[n] - new float3(0f, 0f, 0.010f), new float2(0.012f, 0.012f), new float3(0f, 0f, 1f));
            }
        }
    }
}
