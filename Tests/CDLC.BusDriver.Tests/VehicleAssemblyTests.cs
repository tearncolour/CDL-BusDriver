using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

static class VehicleAssemblyTests
{
    static Dictionary<string, object> Profile(bool front)
    {
        return new Dictionary<string, object>
        {
            ["model"] = front ? "AssemblyTestFront" : "AssemblyTestRear",
            ["spec"] = new { frontZ = front ? 5f : -.6f, rearZ = front ? -.6f : -7f,
                halfWidth = 1.25f, roofY = 3f, floorY = .35f, frontAxleZ = front ? 2f : -4f,
                rearAxleZ = front ? -.2f : -4f, doors = 1, driverSide = -1, section = front ? "Front" : "Rear" },
            ["axles"] = front ? new object[] { new {z=2f,steer=true,parts=new[]{19,20}},new {z=-.2f,steer=false,parts=new[]{21,22}} }
                                : new object[] { new {z=-4f,steer=false,parts=new[]{21,22}} },
            ["wheelRadius"] = .49f, ["wrap"] = new { bottom=.2f, thresholds=new[]{1f,2f,3f} },
            ["bodyColor"] = new[]{255,255,255}, ["doors"] = new[] { new {centreZ=front?1f:-2f,width=1.2f,leaves=2,
                boardingPosition = new[]{1.25f,.35f,front?1f:-2f}, boardingRotation = new[]{0f,-.70710677f,0f,.70710677f},
                boarding=front, alighting=!front} },
            ["signs"] = new {}, ["nextStop"] = new {centre=new[]{0f,2f,0f}},
            ["cardReaders"] = Array.Empty<object>(), ["mirrors"] = Array.Empty<object>(), ["riders"] = Array.Empty<object>()
        };
    }

    internal static void Run(Action<bool,string> check)
    {
        var front = Profile(true); var rear = Profile(false);
        check(TestPacks.Profile("SWB6710EV01G").NativeDoors == null && TestPacks.Profile("SWB6109EV12G").NativeDoors == null,
            "native boarding: v1 profiles retain their previous native template contract");
        string good = JsonSerializer.Serialize(new { format="cdlb-vehicle", version=2, model="AssemblyTest",
            sections=new[] {
                new {id="front",prefab="AssemblyTestFront",restPosition=new[]{0f,0f,0f},restRotation=new[]{0f,0f,0f,1f},profile=front},
                new {id="rear",prefab="AssemblyTestRear",restPosition=new[]{0f,0f,-1.2f},restRotation=new[]{0f,0f,0f,1f},profile=rear}},
            articulation=new {frontSection="front",rearSection="rear",frontAnchor=new[]{0f,0f,-1.2f},rearAnchor=new[]{0f,0f,0f},
                maxSeparation=.05f,joint=new {format="cdlc-joint-v2",mesh="joint.gz",meshSha256=new string('0',64),maxBendDegrees=55}} });
        VehicleProfile p = VehicleProfileJson.Parse(good,".","assembly test",false);
        check(p.Assembly != null && p.Spec.Section==BusSection.Front && p.Assembly.Rear.Profile.Spec.Section==BusSection.Rear,
            "assembly profile: explicit front/rear, independent local coordinates");
        check(p.Assembly.Rear.Profile.Axles.Length==1 && p.Assembly.Rear.Profile.Axles[0].LeftPart==21 &&
              p.Assembly.Rear.Profile.Spec.FrontAxleZ==p.Assembly.Rear.Profile.Spec.RearAxleZ,
            "assembly profile: one real rear axle, no invented front wheels");
        check(p.Assembly.ForPrefab("AssemblyTestRear")==p.Assembly.Rear.Profile && p.Assembly.ForPrefab("AssemblyTestRear_suffix")==null,
            "assembly profile: exact prefab binding, no suffix matching");
        check(p.NativeDoors.Length==1 && p.Assembly.Rear.Profile.NativeDoors.Length==1 &&
            p.NativeDoors[0].Position.z==1 && p.Assembly.Rear.Profile.NativeDoors[0].Position.z==-2 &&
            math.forward(p.NativeDoors[0].Rotation).x < -.99f && p.NativeDoors[0].CanBoard &&
            !p.NativeDoors[0].CanAlight && p.Assembly.Rear.Profile.NativeDoors[0].CanAlight,
            "assembly profile: explicit native boarding thresholds are separate local door coordinates");
        var frontPoints = VehicleBoarding.Points(p); var rearPoints = VehicleBoarding.Points(p.Assembly.Rear.Profile);
        check(frontPoints.Length==1 && frontPoints[0].Enter && rearPoints.Length==1 && !rearPoints[0].Enter &&
            math.forward(frontPoints[0].Rotation).x < -.99f && math.forward(rearPoints[0].Rotation).x > .99f,
            "native boarding: declared Enter faces in, Exit faces out at the same local threshold");
        check(rearPoints[0].Position.z==-2 && rearPoints[0].Position.x==1.25f,
            "native boarding: rear uses its own local doorway, no rest offset or template door");
        string folder = Path.Combine(Path.GetTempPath(),"cdlc-assembly-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder,"AssemblyTestFront.cdlb.json"),good);
            bool missing=false;
            try { VehicleProfileJson.ValidateAssemblyEntrances(p.Assembly,folder); }
            catch (VehicleProfileJson.Error) { missing=true; }
            check(missing,"assembly profile: missing rear entrance refused before registry mutation");
            File.WriteAllText(Path.Combine(folder,"AssemblyTestRear.cdlb.json"),good);
            VehicleProfileJson.ValidateAssemblyEntrances(p.Assembly,folder);
            check(true,"assembly profile: identical complete front/rear entrances accepted");
            File.AppendAllText(Path.Combine(folder,"AssemblyTestRear.cdlb.json")," ");
            bool different=false;
            try { VehicleProfileJson.ValidateAssemblyEntrances(p.Assembly,folder); }
            catch (VehicleProfileJson.Error) { different=true; }
            check(different,"assembly profile: contradictory entrance identities refused");
        }
        finally { Directory.Delete(folder,true); }
        VehicleModels.RegisterAssembly(p.Assembly);
        check(p.Spec.ModelVariant != p.Assembly.Rear.Profile.Spec.ModelVariant &&
            math.abs(p.Spec.Snapped().JointZ+1.2f)<1e-6f && p.Assembly.Rear.Profile.Spec.Snapped().Section==BusSection.Rear,
            "assembly profile: separate variants preserve authored joint and section");
        (string name,string text)[] invalid = {
            ("future version",good.Replace("\"version\":2","\"version\":5")),
            ("duplicate section",good.Replace("\"id\":\"rear\"","\"id\":\"front\"")),
            ("unbound rear prefab",good.Replace("\"prefab\":\"AssemblyTestRear\"","\"prefab\":\"OtherRear\"")),
            ("mismatched source anchors",good.Replace("\"frontAnchor\":[0,0,-1.2]","\"frontAnchor\":[0,0,-1.3]")),
            ("nonunit rest quaternion",good.Replace("\"restRotation\":[0,0,0,1]","\"restRotation\":[0,0,0,0.5]")),
            ("wrong section contract",good.Replace("\"section\":\"Rear\"","\"section\":\"Solo\"")),
            ("undeclared rear steer",good.Replace("\"z\":-4,\"steer\":false","\"z\":-4,\"steer\":true")),
            ("joint path escape",good.Replace("\"mesh\":\"joint.gz\"","\"mesh\":\"../joint.gz\"")),
            ("joint invalid hash",good.Replace(new string('0',64),"invalid")),
            ("body/axle discrepancy",good.Replace("\"frontAxleZ\":-4","\"frontAxleZ\":-3"))
            ,("missing native boarding",good.Replace("\"boardingPosition\"", "\"wrongBoardingPosition\""))
            ,("boarding outside door",good.Replace("\"boardingPosition\":[1.25,0.35,1]", "\"boardingPosition\":[1.25,0.35,4]"))
            ,("boarding on wrong side",good.Replace("\"boardingPosition\":[1.25,", "\"boardingPosition\":[-1.25,"))
            ,("boarding direction outward",good.Replace("\"boardingRotation\":[0,-0.70710677,0,0.70710677]", "\"boardingRotation\":[0,0.70710677,0,0.70710677]"))
            ,("no boarding policy",good.Replace("\"boarding\":", "\"notBoarding\":"))
            ,("inaccessible door",good.Replace("\"boarding\":true", "\"boarding\":false"))
        };
        foreach (var item in invalid)
        {
            bool refused=false;
            try { VehicleProfileJson.Parse(item.text,".","invalid assembly test",false); }
            catch (VehicleProfileJson.Error) { refused=true; }
            check(item.text!=good && refused,"assembly profile rejects "+item.name);
        }
        string disabled = good.Replace("\"nextStop\":{", "\"nextStop\":{\"enabled\":false,");
        VehicleProfile noDisplay = VehicleProfileJson.Parse(disabled,".","no passenger display",false);
        check(!noDisplay.NextStopEnabled && !noDisplay.Assembly.Rear.Profile.NextStopEnabled && p.NextStopEnabled,
            "assembly profile: explicit next-stop suppression, legacy default enabled");
        VehicleProfile exactDoor = VehicleProfileJson.Parse(good.Replace("\"leaves\":2", "\"leaves\":2,\"openDegrees\":85"),".","authored door sweep",false);
        check(exactDoor.DoorOpenDegrees[0]==85f && p.DoorOpenDegrees[0]==BusInterior.DoorOpenDeg,
            "assembly profile: authored door sweep and unchanged legacy default");
        VehicleModels.RegisterAssembly(exactDoor.Assembly);
        var buffer = new KitPoseBuffer(KitBuild.PartTable(new BusInterior()));
        foreach (float open in new[]{0f,.5f,1f})
        {
            var pose = BusInterior.BoardPoseOf(0,true); pose.DoorOpen = new float3(open);
            BusInterior.Apply(buffer,pose,exactDoor.Spec);
            float angle = math.radians(85f*open*open*(3f-2f*open));
            check(math.distance(buffer.Delta[4].value,quaternion.RotateY(angle).value)<1e-6f &&
                  math.distance(buffer.Delta[5].value,quaternion.RotateY(-angle).value)<1e-6f,
                  "assembly production poser: two leaves retain authored sweep at "+open);
        }
    }
}
