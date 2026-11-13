using System.Collections.Concurrent;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

namespace CDLC.BusDriver.Interior.Models
{
    internal sealed class GenericDoorRig
    {
        static readonly ConcurrentDictionary<KitModelKey, GenericDoorRig> s_Cache = new ConcurrentDictionary<KitModelKey, GenericDoorRig>();
        internal readonly DoorLinkage[][] Links;
        internal readonly int FirstDrive;
        internal int DriveCount => Links.Length * 2;
        internal int FirstStrap => FirstDrive + DriveCount;
        internal readonly float Height;

        internal static GenericDoorRig For(in BusInteriorSpec value)
        {
            BusInteriorSpec spec = value.Snapped();
            KitModelKey key = BusInterior.KeyFor(spec);
            if (s_Cache.TryGetValue(key, out GenericDoorRig rig)) return rig;
            return s_Cache.GetOrAdd(key, new GenericDoorRig(spec));
        }

        GenericDoorRig(BusInteriorSpec spec)
        {
            var bus = new BusInterior(spec, true);
            FirstDrive = spec.Section == BusSection.Front ? BusInterior.StrapFirst : BusInterior.PartCount;
            Height = BusInterior.GenericDoorTop(spec) + .035f;
            Links = new DoorLinkage[bus.DoorCount][];
            float rawX = -spec.DriverSide * (spec.HalfWidth - .028f - .065f);
            float x = BusBarrel.Point(spec, new float3(rawX, Height, 0)).x;
            for (int d = 0; d < Links.Length; d++)
            {
                float2 door = bus.Door(d); float width = door.y * .5f;
                Links[d] = new DoorLinkage[2];
                for (int leaf = 0; leaf < 2; leaf++)
                {
                    float direction = leaf == 0 ? -1f : 1f, jamb = door.x - direction * width;
                    float z = jamb + direction * width * .28f, span = width * .44f;
                    var link = new DoorLinkage(new float2(x, z), new float2(x, z + direction * span * .5f),
                        new float2(x, z + direction * span), -spec.DriverSide * -direction * 80f, FirstDrive + d * 2 + leaf);
                    string error = link.Check();
                    if (error != null) throw new System.InvalidOperationException("Generic door linkage: " + error);
                    Links[d][leaf] = link;
                }
            }
        }
    }
}
