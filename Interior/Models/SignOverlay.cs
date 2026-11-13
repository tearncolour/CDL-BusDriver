using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// Our destination signs on a bus asset we do not model (format "cdlb-signs", a &lt;prefab&gt;.cdlb-signs.json beside its .cok):
    /// the front, kerb-side and rear signs drawn on the asset's own body, in the prefab's object space (+Z forward), each with an
    /// optional dark board behind its dots that covers the sign the asset has painted. Nothing else of the bus is ours.
    /// </summary>
    internal sealed class SignOverlay
    {
        /// <summary>A board behind a sign's dots (sRGB 0..255, metres; Size.x 0: none).</summary>
        internal readonly struct Backing
        {
            internal readonly float3 Srgb;
            internal readonly float2 Size;
            internal Backing(float3 srgb, float2 size) { Srgb = srgb; Size = size; }
            internal bool Present => Size.x > 0f;
        }

        // the board lies this far behind the dots, along the sign's facing
        internal const float BackingDepth = 0.004f;

        internal readonly string Name, Source;
        internal readonly VehicleProfile.Sign FrontSign, SideSign, RearSign;
        internal readonly Backing FrontBacking, SideBacking, RearBacking;

        internal SignOverlay(string name, string source, VehicleProfile.Sign front, VehicleProfile.Sign side, VehicleProfile.Sign rear,
            Backing frontBacking, Backing sideBacking, Backing rearBacking)
        {
            Name = name; Source = source;
            FrontSign = front; SideSign = side; RearSign = rear;
            FrontBacking = frontBacking; SideBacking = sideBacking; RearBacking = rearBacking;
        }

        internal bool HasCards => FrontSign.IsCard || SideSign.IsCard || RearSign.IsCard;

        // the side sign faces the side its centre is on
        internal float Kerb => SideSign.Centre.x >= 0f ? 1f : -1f;

        internal quaternion FrontRotation => BusInterior.EndSignRotation(true, FrontSign.Tilt);
        internal quaternion RearRotation => BusInterior.EndSignRotation(false, RearSign.Tilt);
        internal quaternion SideRotation => BusInterior.SideSignRotation(Kerb, SideSign.Tilt);

        /// <summary>Where a board goes for a sign at <paramref name="centre"/> facing -Z of <paramref name="rotation"/>.</summary>
        internal static float3 BackingCentre(float3 centre, quaternion rotation) => centre + math.mul(rotation, new float3(0f, 0f, BackingDepth));
    }
}
