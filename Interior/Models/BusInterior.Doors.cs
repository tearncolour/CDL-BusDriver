using System.Collections.Generic;
using Unity.Mathematics;
using CDLC.BusDriver.Kit;

namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        internal static float GenericDoorTop(in BusInteriorSpec s) => s.RoofY - .71f;
        float LeafTop => GenericDoorTop(Spec);
        float LeafX => -Side * (HW - .028f);
        const float DoorJambGap = .040f;
        const float DoorScreenReach = .40f, DoorScreenSetback = .14f;
        float DoorEntryX => -Side * (WallIn - DoorScreenReach);

        void DefineLeaf(MeshKit k, int d, bool a, int lod)
        {
            if (d >= DoorCount) return;
            float2 door = Door(d);
            float hinge = door.x + (a ? 1 : -1) * door.y * .5f, dir = a ? -1 : 1;
            float w = door.y * .5f - .010f, start = DoorJambGap, top = LeafTop;
            float3 P(float t, float y, float inward = 0) => new float3(LeafX + Side * inward, y, hinge + dir * t);
            using (k.Paint(Sw.Black))
            {
                foreach (float t in new[] { start + .023f, w - .023f })
                    k.BevelBox(P(t, (F + .027f + top) * .5f), new float3(.044f, top - F - .027f, .046f), .007f);
                foreach (float y in new[] { F + .054f, F + .32f, F + .97f, top - .027f })
                    k.BevelBox(P((start + w) * .5f, y), new float3(.044f, .054f, w - start - .07f), .007f);
            }
            using (k.Paint(Sw.Galvanised)) k.BevelBox(P((start + w) * .5f, F + .187f), new float3(.032f, .224f, w - start - .075f), .006f);
            using (k.Paint(Sw.Rubber))
            {
                k.Box(P(w + .003f, (F + .035f + top) * .5f), new float3(.049f, top - F - .035f, .014f));
                k.Box(P((start + w) * .5f, F + .023f), new float3(.042f, .016f, w - start));
            }
            DoorGrab(k, P(start + .025f, F + 1.23f, .024f), P(w - .025f, F + .85f, .024f), lod);
            // Pins and rollers belong to the leaf; the crank is a separate rigid drive part.
            GenericDoorRig rig = GenericDoorRig.For(Spec); DoorLinkage link = rig.Links[d][a ? 0 : 1];
            float3 R(float2 xy, float y) => BusBarrel.SourcePoint(Spec, new float3(xy.x, y, xy.y));
            using (k.Paint(Sw.Galvanised))
            {
                foreach (float2 xy in new[] { link.Joint, link.Guide })
                {
                    float3 pin = R(xy, top - .015f);
                    k.BoxAlong(new float3(LeafX, pin.y, pin.z), pin, new float2(.028f, .024f), new float3(0, 1, 0));
                }
                k.Cylinder(R(link.Joint, top - .025f), R(link.Joint, rig.Height + .015f), .009f, k.Sides(12));
                k.Cylinder(R(link.Guide, top - .025f), R(link.Guide, top + .079f), .006f, k.Sides(10));
                k.Cylinder(R(link.Guide, top + .063f), R(link.Guide, top + .080f), .014f, k.Sides(16));
                float3 bottom = R(link.Guide, F + .058f);
                k.BoxAlong(new float3(LeafX, bottom.y, bottom.z), bottom, new float2(.018f, .02f), new float3(0, 1, 0));
                k.Cylinder(R(link.Guide, F + .004f), bottom, .0055f, k.Sides(10));
            }
        }

        void DoorGrab(MeshKit k, float3 a, float3 b, int lod)
        {
            float3 inward = new float3(Side, 0, 0), run = math.normalize(b - a);
            const float bend = .034f, stand = .068f;
            var path = new List<float3>();
            for (int end = 0; end < 2; end++)
            {
                float3 foot = end == 0 ? a : b, direction = end == 0 ? run : -run;
                float3 c = foot + inward * stand, p0 = c - inward * bend, p2 = c + direction * bend;
                var points = new List<float3> { foot };
                int count = lod == 0 ? 6 : 3;
                for (int i = 0; i <= count; i++)
                {
                    float t = i / (float)count;
                    points.Add((1-t)*(1-t)*p0 + 2*(1-t)*t*c + t*t*p2);
                }
                if (end == 1) points.Reverse();
                path.AddRange(points);
            }
            using (k.Paint(Sw.BrushedStainless))
            {
                Pipe(k, path.ToArray(), .016f, lod == 0 ? 12 : 8);
                foreach (float3 foot in new[] { a, b }) k.Cylinder(foot - inward * .006f, foot + inward * .009f, .027f, k.Sides(14));
            }
        }

        void AddDoorDriveParts(KitPartList parts)
        {
            GenericDoorRig rig = GenericDoorRig.For(Spec);
            foreach (DoorLinkage[] pair in rig.Links)
                foreach (DoorLinkage link in pair)
                    parts.Add(link.DrivePart, "DoorDrive" + (link.DrivePart - rig.FirstDrive), Shell, new float3(link.Shaft.x, rig.Height, link.Shaft.y));
        }

        void DefineDoorDrives(KitModelBuilder builder, int lod)
        {
            GenericDoorRig rig = GenericDoorRig.For(Spec);
            foreach (DoorLinkage[] pair in rig.Links)
                foreach (DoorLinkage link in pair)
                {
                    if (!builder.Building(link.DrivePart)) continue;
                    MeshKit k = builder.Part(link.DrivePart);
                    float3 shaft = new float3(link.Shaft.x, rig.Height, link.Shaft.y), pin = new float3(link.Joint.x, rig.Height, link.Joint.y);
                    using (k.Paint(Sw.Galvanised))
                    {
                        k.BoxAlong(shaft, pin-math.normalize(pin-shaft)*.018f, new float2(.022f, .012f), new float3(0, 1, 0));
                        k.Cylinder(shaft - new float3(0,.018f,0), shaft + new float3(0,.060f,0), .012f, k.Sides(14));
                        if (lod < 2) DoorBearing(k,pin,.0105f,.022f,.018f,false);
                    }
                }
        }

        void DoorPortals(MeshKit k, int lod)
        {
            float xs = -Side; GenericDoorRig rig = GenericDoorRig.For(Spec);
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                for (int e = -1; e <= 1; e += 2)
                {
                    float ze = door.x + e * (door.y * .5f + .045f);
                    // Keep the structural post inside the body envelope where it meets the flush black header band.
                    using (k.Paint(Sw.Panel)) k.BevelBox(new float3(xs * (WallIn + HW - .01f) * .5f, (F - .12f + WinTop + .03f) * .5f, ze), new float3(HW - WallIn + .01f, WinTop + .15f - F, .09f), .015f);
                    using (k.Paint(Sw.Rubber)) k.Box(new float3(LeafX + xs * .027f, (F + LeafTop) * .5f, ze - e * (.045f+(DoorJambGap-.008f)*.5f)), new float3(.008f, LeafTop-F, DoorJambGap+.008f));
                }
                using (k.Paint(Sw.DarkGrey)) k.BevelBox(new float3(xs * (WallIn - .09f), LeafTop + .14f, door.x), new float3(.26f, .08f, door.y + .20f), .016f);
                using (k.Paint(Sw.Black))
                    k.Box(new float3(LeafX+xs*.011f,(LeafTop+.004f+WinTop+.035f)*.5f,door.x),new float3(.030f,WinTop+.035f-LeafTop-.004f,door.y+.09f));
                foreach (DoorLinkage link in rig.Links[d])
                {
                    if (lod < 2) using (k.Paint(Sw.Galvanised)) DoorBearing(k,new float3(link.Shaft.x,LeafTop+.095f,link.Shaft.y),.0135f,.025f,.020f,true);
                    link.Solve(1, out _, out float2 joint, out float yaw, out _);
                    float guideEnd = link.Move(new float3(link.Guide.x, 0, link.Guide.y), joint, yaw).z;
                    float z0 = math.min(guideEnd, link.Guide.y) - .020f, z1 = math.max(guideEnd, link.Guide.y) + .020f;
                    HeaderChannel(k, link.Guide.x, z0, z1, LeafTop + .089f, .036f, .035f);
                    if (lod < 2) HeaderChannel(k, link.Guide.x, z0, z1, F + .012f, .014f, .011f, true);
                }
                if (lod == 0)
                {
                    using (k.Paint(Sw.Red)) k.BevelBox(new float3(xs * (WallIn - .225f), LeafTop + .14f, door.x + door.y * .5f - .08f), new float3(.018f, .055f, .08f), .006f);
                    k.PanelLine(new float3(xs * (WallIn - .221f), LeafTop + .115f, door.x - door.y * .5f), new float3(xs * (WallIn - .221f), LeafTop + .115f, door.x + door.y * .5f), new float3(Side, 0, 0), .004f);
                }
            }
        }

        void HeaderChannel(MeshKit k, float x, float z0, float z1, float top, float gap, float height, bool floor = false)
        {
            float web = floor ? .003f : .006f;
            float3 P(float xx, float y) => BusBarrel.SourcePoint(Spec, new float3(xx, y, (z0+z1)*.5f));
            using (k.Paint(Sw.Galvanised))
            {
                foreach (float side in new[] {-1f,1f}) k.Box(P(x+side*(gap+web)*.5f,top-height*.5f),new float3(web,height,z1-z0));
                if (!floor) k.Box(P(x,top),new float3(gap+web*2,web,z1-z0));
                foreach (float z in new[]{z0,z1})
                {
                    float3 centre=P(x,top-height*.5f);centre.z=z;
                    k.Box(centre,new float3(gap+web*2,height,web));
                }
            }
        }

        void DoorBearing(MeshKit k,float3 centre,float inner,float outer,float height,bool source)
        {
            int count=math.max(12,k.Sides(20));
            float3 P(float radius,float a,float y)
            {
                float3 p=centre+new float3(radius*math.cos(a),y,radius*math.sin(a));
                return source?BusBarrel.SourcePoint(Spec,p):p;
            }
            for(int i=0;i<count;i++)
            {
                float a=2*math.PI*i/count,b=2*math.PI*(i+1)/count,h=height*.5f;
                float3 n=new float3(math.cos((a+b)*.5f),0,math.sin((a+b)*.5f));
                k.QuadP(P(outer,a,-h),P(outer,b,-h),P(outer,b,h),P(outer,a,h),n);
                k.QuadP(P(inner,a,-h),P(inner,b,-h),P(inner,b,h),P(inner,a,h),-n);
                k.QuadP(P(inner,a,h),P(inner,b,h),P(outer,b,h),P(outer,a,h),new float3(0,1,0));
                k.QuadP(P(inner,a,-h),P(inner,b,-h),P(outer,b,-h),P(outer,a,-h),new float3(0,-1,0));
            }
        }
    }
}
