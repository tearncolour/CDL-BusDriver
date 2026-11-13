using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// Explicit command for a real generated pack; never silently skips a missing K18 resource.
static class VehicleAssemblyResourceTests
{
    internal static int Run(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("assembly <complete .cdlb.json> <report.json>"); return 2; }
        int checks=0,failed=0;
        var failures=new List<string>();var sections=new List<object>();
        void Check(bool yes,string message) { checks++;if (!yes) { failed++;failures.Add(message); } }
        var report=new Dictionary<string,object>();
        try
        {
            string path=Path.GetFullPath(args[1]);
            VehicleProfile profile=VehicleProfileJson.Load(path);
            VehicleAssembly assembly=profile.Assembly ?? throw new InvalidDataException("Expected a v2 assembly");
            VehicleProfileJson.ValidateAssemblyEntrances(assembly,Path.GetDirectoryName(path));
            VehicleModels.RegisterAssembly(assembly);
            report["manifest_sha256"]=VehicleProfileJson.Sha256(File.ReadAllBytes(path));
            report["assembly_identity"]=assembly.Identity;
            // the joint itself is read and checked by CDL Asset (Articulation.Tests); here only that the declared file is the one shipped
            Check(File.Exists(assembly.Joint.MeshPath) &&
                VehicleProfileJson.Sha256(File.ReadAllBytes(assembly.Joint.MeshPath))==assembly.Joint.Sha256,"CJA2 joint resource matches its declared hash");
            report["joint_sha256"]=assembly.Joint.Sha256;
            foreach (VehicleSectionBinding binding in new[]{assembly.Front,assembly.Rear})
            {
                VehicleProfile model=binding.Profile;
                VehicleMesh source=model.Mesh; // verifies hash, actual wheels and declared pivot Z.
                KitModelData data=KitBuild.BuildAll(new BusInterior(model.Spec));
                Check(data.Error==null,binding.Id+" compile: "+data.Error);
                if (data.Error!=null) continue;
                Check(data.Parts.Length==source.PartCount,binding.Id+" retains fixed parts and all straps");
                for (int lod=0;lod<3;lod++)
                {
                    Check(data.Tris[lod]>0,binding.Id+" LOD "+lod+" nonempty");
                    for (int part=0;part<source.PartCount;part++)
                    {
                        // Readers and wrap copies are deliberately generated at runtime.
                        if (part==BusInterior.Cab || part>=BusInterior.WrapLeft && part<=BusInterior.WrapRoof ||
                            part==BusInterior.Ceiling && model.NextStopEnabled) continue;
                        // The windows' wrap skin (a wrap surface hanging from the shell) is drawn by the wrap faces, its own part draws nothing;
                        // the door sheets (children of their leaves) are drawn as stored.
                        if (source.IsSurface(part) && source.SurfaceParent(part)==BusInterior.Shell)
                        {
                            Check(data.Parts[part].Tris[lod]==0 && source.TriangleCount(part,lod)>0,binding.Id+" LOD "+lod+" the windows' wrap skin is in the source and drawn only by the wrap faces");
                            continue;
                        }
                        Check(data.Parts[part].Tris[lod]==source.TriangleCount(part,lod),
                            binding.Id+" LOD "+lod+" part "+part+" retained source triangles");
                    }
                }
                foreach (float open in new[]{0f,.5f,1f})
                {
                    var pose=BusInterior.BoardPoseOf(0,true);pose.DoorOpen=new float3(open);
                    var buffer=new KitPoseBuffer(data.Parts);BusInterior.Apply(buffer,pose,model.Spec);
                    for (int door=0;door<model.Doors.Length;door++)
                    {
                        // an outward-swing door (doors[].linkage): each leaf's pivot rides its linkage joint, its drive turns about the shaft
                        DoorLinkage[] linkage=model.DoorLinkages!=null && door<model.DoorLinkages.Length ? model.DoorLinkages[door] : null;
                        if (linkage!=null)
                        {
                            bool rides=true;
                            for (int leaf=0;leaf<linkage.Length;leaf++)
                            {
                                DoorLinkage l=linkage[leaf];
                                rides&=l.Solve(open,out float shaft,out float2 linked,out _,out _);
                                rides&=math.distance(math.transform(buffer.PartToModel(4+2*door+leaf),float3.zero).xz,linked)<1e-4f;
                                rides&=math.distance(math.rotate(buffer.PartToModel(l.DrivePart),new float3(0f,0f,1f)),math.rotate(quaternion.RotateY(shaft),new float3(0f,0f,1f)))<1e-4f;
                            }
                            Check(rides,binding.Id+" actual door "+door+" linkage sweep "+open);
                            continue;
                        }
                        float a=math.radians(model.DoorOpenDegrees[door]*open*open*(3-2*open));
                        float sign=model.Spec.DriverSide<0 ? 1 : -1;
                        Check(math.distance(buffer.Delta[4+2*door].value,quaternion.RotateY(sign*a).value)<1e-6f &&
                              math.distance(buffer.Delta[5+2*door].value,quaternion.RotateY(-sign*a).value)<1e-6f,
                              binding.Id+" actual door "+door+" sweep "+open);
                    }
                    Check(buffer.Visible[19]==(binding==assembly.Front),binding.Id+" front wheel visibility at "+open);
                }
                sections.Add(new {id=binding.Id,mesh_sha256=model.MeshSha256,parts=source.PartCount,
                    triangles=data.Tris,axle_count=model.Axles.Length,door_count=model.Doors.Length,
                    seats=model.RiderSlots().FindAll(r=>r.Kind==BusInterior.RiderKind.Seated).Count,
                    next_stop_enabled=model.NextStopEnabled});
            }
        }
        catch(Exception e) { failed++;failures.Add(e.ToString()); }
        report["status"]=failed==0 ? "PASS" : "FAIL";report["checks"]=checks;report["failures"]=failures;
        report["sections"]=sections;report["scope"]="Real manifest/SWB1 parsing, CJA2 joint hash and production KitBuild/poser. No native package, renderer or game acceptance.";
        string output=Path.GetFullPath(args[2]);Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(checks+" real assembly checks, "+failed+" failed");
        return failed==0 ? 0 : 1;
    }
}
