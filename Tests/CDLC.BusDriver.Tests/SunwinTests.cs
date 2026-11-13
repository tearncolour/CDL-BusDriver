using System;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The optional Blender-authored bus must retain the runtime's part and pose contract,
// while keeping its short-overhang dimensions and three-leaf door arrangement.
static class SunwinTests
{
    const string Prefix = "SWB6710EV01G: ";
    static readonly int[] ActiveDoors = { BusInterior.DoorFrontA, BusInterior.DoorFrontB, BusInterior.DoorMidA };
    static readonly int[] AbsentDoors = { BusInterior.DoorMidB, BusInterior.DoorRearA, BusInterior.DoorRearB };

    internal static void Run(Action<bool, string> check)
    {
        var requested = TestPacks.Spec("SWB6710EV01G");
        var bus = new BusInterior(requested);
        var spec = bus.Spec;
        check(bus.IsAuthored, Prefix + "explicit model choice selects the authored bus");
        check(Near(spec.FrontZ - spec.RearZ, 7.145f) && Near(2f * spec.HalfWidth, 2.35f),
            Prefix + "7145 x 2350 mm dimensions survive spec snapping");
        check(Near(spec.FrontAxleZ - spec.RearAxleZ, 5.1f) &&
              Near(spec.FrontZ - spec.FrontAxleZ, 1.2f) && Near(spec.RearAxleZ - spec.RearZ, 0.845f),
            Prefix + "5100 mm wheelbase and 1200/845 mm overhangs survive spec snapping");
        check(spec.Doors == 2 && bus.DoorCount == 2 && bus.HasCab, Prefix + "two doors and a driver cab");

        var genericSpec = requested;
        genericSpec.ModelVariant = BusModelVariant.Generic;
        var generic = new BusInterior(genericSpec);
        check(!generic.IsAuthored && !bus.Key.Equals(generic.Key), Prefix + "generic and authored geometry cannot share a cache entry");
        var defaultBus = new BusInterior();
        check(!defaultBus.IsAuthored && Near(defaultBus.Spec.FrontZ - defaultBus.Spec.RearZ, 12f),
            Prefix + "default buses retain the generic model");

        float2 frontDoor = bus.Door(0), rearDoor = bus.Door(1);
        check(frontDoor.x > rearDoor.x && FrontEdge(rearDoor) < BackEdge(frontDoor),
            Prefix + "front and rear door openings are ordered and separated");
        for (int door = 0; door < bus.DoorCount; door++)
        {
            float2 opening = bus.Door(door);
            check(opening.y > 0.5f && BackEdge(opening) > spec.RearAxleZ && FrontEdge(opening) < spec.FrontAxleZ,
                Prefix + "door " + door + " lies entirely between the axles");
        }

        var data = KitBuild.BuildAll(bus);
        check(data.Error == null, Prefix + "embedded full model builds (" + data.Error + ")");
        if (data.Error != null) return;
        check(data.Parts.Length >= BusInterior.PartCount, Prefix + "runtime part table is complete");
        if (data.Parts.Length < BusInterior.PartCount) return;
        int drives = bus.AuthoredModel?.DoorDriveCount ?? 0, surfaces = bus.AuthoredModel?.WrapSurfaceCount ?? 0;
        if (data.Parts.Length > BusInterior.PartCount + drives + surfaces)
            Sunwin6109Tests.CheckAuthoredStraps(check, data, spec, Prefix, data.Parts.Length - BusInterior.PartCount - drives - surfaces, BusInterior.PartCount + drives + surfaces);
        WrapSurfaces(check, bus, data, spec);

        string[] contract = { "Shell", "SteeringWheel", "SpeedNeedle", "RpmNeedle", "Door0A", "Door0B", "Door1A", "Door1B", "Door2A", "Door2B" };
        for (int part = 0; part < contract.Length; part++)
            check(data.Parts[part].Name == contract[part], Prefix + "contract part " + part + " is " + contract[part]);

        int[] interior = { BusInterior.Shell, BusInterior.Fittings, BusInterior.Dash, BusInterior.Cab, BusInterior.Poles, BusInterior.Ceiling };
        foreach (int part in interior)
            check(HasMesh(data, part, 0), Prefix + "interior " + data.Parts[part].Name + " has geometry");
        int[] exterior = { BusInterior.Outside, BusInterior.Exterior, BusInterior.Glazing, BusInterior.Livery, BusInterior.HeadLamps, BusInterior.TailLamps };
        foreach (int part in exterior)
            check(HasMesh(data, part, 0), Prefix + "exterior " + data.Parts[part].Name + " has geometry");

        Geometry(check, data);
        Wheels(check, data, spec);
        Doors(check, data, spec, frontDoor, rearDoor);
        Instruments(check, data, spec);
        Wraps(check, bus, data);
        Views(check, bus, data, spec);
        Mirrors(check, data, spec);
        DestinationSigns(check, spec);

        var slots = bus.RiderSlots();
        int seated = 0, drivers = 0;
        bool validSlots = true;
        foreach (var slot in slots)
        {
            if (slot.Kind == BusInterior.RiderKind.Seated) seated++;
            if (slot.Kind == BusInterior.RiderKind.Driver) drivers++;
            validSlots &= math.all(math.isfinite(slot.Position)) && math.isfinite(slot.Yaw) &&
                          math.abs(slot.Position.x) < spec.HalfWidth && slot.Position.z > spec.RearZ && slot.Position.z < spec.FrontZ;
        }
        check(seated == 16 && drivers == 1 && validSlots, Prefix + "16 passenger seats and one driver have finite positions inside the bus");
        check(!slots.Exists(slot => slot.Kind == BusInterior.RiderKind.Seated && slot.Position.x > 0f && slot.Position.z > 1f),
            Prefix + "the entry beside the driver remains free of a passenger seat");
    }

    static void Geometry(Action<bool, string> check, KitModelData data)
    {
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
        {
            bool finite = true, topology = true, frames = true;
            int triangles = 0;
            foreach (var part in data.Parts)
            {
                frames &= math.all(math.isfinite(part.Pivot)) && math.all(math.isfinite(part.Rot.value));
                var mesh = part.Data[lod];
                if (mesh == null) continue;
                topology &= mesh.P != null && mesh.N != null && mesh.UV != null && mesh.Idx != null;
                if (mesh.P == null || mesh.N == null || mesh.UV == null || mesh.Idx == null) continue;
                topology &= mesh.P.Length == mesh.N.Length && mesh.P.Length == mesh.UV.Length && mesh.Idx.Length % 3 == 0;
                foreach (float3 v in mesh.P) finite &= math.all(math.isfinite(v));
                foreach (float3 n in mesh.N) finite &= math.all(math.isfinite(n)) && math.lengthsq(n) > 1e-8f;
                foreach (float2 uv in mesh.UV) finite &= math.all(math.isfinite(uv));
                foreach (int index in mesh.Idx) topology &= index >= 0 && index < mesh.P.Length;
                topology &= mesh.SubFin != null && mesh.SubStart != null && mesh.SubCount != null;
                if (mesh.SubFin != null && mesh.SubStart != null && mesh.SubCount != null)
                {
                    topology &= mesh.SubFin.Length == mesh.SubStart.Length && mesh.SubFin.Length == mesh.SubCount.Length;
                    int covered = 0;
                    for (int sub = 0; sub < math.min(mesh.SubFin.Length, math.min(mesh.SubStart.Length, mesh.SubCount.Length)); sub++)
                    {
                        topology &= (int)mesh.SubFin[sub] < (int)Fin.Count && mesh.SubStart[sub] == covered &&
                                    mesh.SubCount[sub] >= 0 && mesh.SubCount[sub] % 3 == 0;
                        covered += mesh.SubCount[sub];
                    }
                    topology &= covered == mesh.Idx.Length;
                }
                triangles += mesh.Idx.Length / 3;
            }
            check(triangles > 0 && data.Tris[lod] > 0, Prefix + "LOD" + lod + " is nonempty");
            check(finite && topology && frames, Prefix + "LOD" + lod + " has finite vertices/normals/UVs, valid indices and part frames");
        }

        var bounds = KitBounds.Empty;
        foreach (int part in new[] { BusInterior.Shell, BusInterior.Exterior, BusInterior.Glazing, BusInterior.Livery })
        {
            var def = data.Parts[part];
            var mesh = def.Data[0];
            if (mesh == null) continue;
            var frame = new float4x4(def.Rot, def.Pivot);
            foreach (float3 vertex in mesh.P) bounds.Add(math.transform(frame, vertex));
        }
        check(!bounds.IsEmpty && Near(bounds.Size.z, 7.145f, 0.08f) && Near(bounds.Size.x, 2.35f, 0.08f),
            Prefix + "body geometry fits the declared length and width (" + bounds.Size + ")");
        check(bounds.Min.y >= -0.02f && bounds.Max.y >= 2.8f && bounds.Max.y <= 3.05f,
            Prefix + "body and roof stay within the 3 m height envelope (" + bounds.Min.y + " .. " + bounds.Max.y + ")");
    }

    static void Wheels(Action<bool, string> check, KitModelData data, BusInteriorSpec spec)
    {
        int[] parts = { BusInterior.WheelFL, BusInterior.WheelFR, BusInterior.WheelRL, BusInterior.WheelRR };
        for (int i = 0; i < parts.Length; i++)
        {
            int part = parts[i];
            float3 pivot = data.Parts[part].Pivot;
            float expectedZ = i < 2 ? spec.FrontAxleZ : spec.RearAxleZ;
            check(Near(pivot.z, expectedZ) && Near(pivot.y, 0.385f, 0.005f) && (i % 2 == 0 ? pivot.x < 0f : pivot.x > 0f),
                Prefix + data.Parts[part].Name + " pivots on the correct axle at tyre radius");
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
                check(HasMesh(data, part, lod), Prefix + data.Parts[part].Name + " has LOD" + lod + " geometry");
        }
        check(Near(data.Parts[BusInterior.WheelFL].Pivot.z - data.Parts[BusInterior.WheelRL].Pivot.z, 5.1f),
            Prefix + "actual wheel pivots have a 5100 mm wheelbase");
        check(Near(data.Parts[BusInterior.WheelFR].Pivot.x - data.Parts[BusInterior.WheelFL].Pivot.x, 1.939f, 0.005f) &&
              Near(data.Parts[BusInterior.WheelRR].Pivot.x - data.Parts[BusInterior.WheelRL].Pivot.x, 1.808f, 0.005f),
            Prefix + "actual wheel pivots retain the 1939/1808 mm front/rear track");
    }

    static void Doors(Action<bool, string> check, KitModelData data, BusInteriorSpec spec, float2 frontDoor, float2 rearDoor)
    {
        var shut = Pose(data, spec, float3.zero);
        var half = Pose(data, spec, new float3(0.5f));
        var open = Pose(data, spec, new float3(1f));
        VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
        foreach (int part in ActiveDoors)
        {
            var def = data.Parts[part];
            float2 opening = part <= BusInterior.DoorFrontB ? frontDoor : rearDoor;
            int door = (part - BusInterior.DoorFrontA) / 2;
            DoorLinkage[] linkage = model?.DoorLinkages?[door];
            if (linkage != null)
            {
                OutwardLeaf(check, data, spec, part, linkage[(part - BusInterior.DoorFrontA) % 2], opening, shut, half, open);
                continue;
            }
            check(Near(def.Pivot.z, BackEdge(opening), 0.03f) || Near(def.Pivot.z, FrontEdge(opening), 0.03f),
                Prefix + def.Name + " pivots at its opening edge");
            check(shut.Visible[part] && open.Visible[part], Prefix + def.Name + " remains visible while opening");
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
                check(HasMesh(data, part, lod), Prefix + def.Name + " has LOD" + lod + " geometry");
            var mesh = data.Parts[part].Data[0];
            if (mesh == null || mesh.P.Length == 0) continue;
            // Use an actual exported vertex away from the hinge axis, not a synthetic
            // generic-bus free edge: this also catches incorrect imported rest frames.
            float3 local = mesh.P[0];
            float distance = -1f;
            foreach (float3 candidate in mesh.P)
            {
                float d = math.lengthsq(candidate.xz);
                if (d > distance) { distance = d; local = candidate; }
            }
            float3 closedPoint = math.transform(shut.PartToModel(part), local);
            float3 openPoint = math.transform(open.PartToModel(part), local);
            float3 closedHinge = math.transform(shut.PartToModel(part), float3.zero);
            float3 openHinge = math.transform(open.PartToModel(part), float3.zero);
            check(math.distance(closedPoint, openPoint) > 0.2f && math.distance(closedHinge, openHinge) < 1e-4f,
                Prefix + def.Name + " moves its real free edge while keeping the hinge fixed");
            float3 halfPoint = math.transform(half.PartToModel(part), local);
            float3 halfHinge = math.transform(half.PartToModel(part), float3.zero);
            float travelled = math.distance(closedPoint, halfPoint), fullTravel = math.distance(closedPoint, openPoint);
            check(half.Visible[part] && math.all(math.isfinite(halfPoint)) && travelled > 0.05f && travelled < fullTravel &&
                  math.distance(halfPoint, openPoint) > 0.05f && math.distance(closedHinge, halfHinge) < 1e-4f,
                Prefix + def.Name + " has a finite intermediate opening with its hinge fixed");
        }
        foreach (int part in AbsentDoors)
        {
            check(!shut.Visible[part] && !open.Visible[part], Prefix + data.Parts[part].Name + " is hidden in both poses");
            bool empty = true;
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++) empty &= !HasMesh(data, part, lod);
            check(empty, Prefix + data.Parts[part].Name + " has no phantom leaf mesh");
        }

        if (model?.DoorLinkages?[0] != null) FrontPairParts(check, data, spec, frontDoor);

        var frontOnly = Pose(data, spec, new float3(1f, 0f, 0f));
        var rearOnly = Pose(data, spec, new float3(0f, 1f, 0f));
        check(SameTransform(shut, frontOnly, BusInterior.DoorMidA) && !SameTransform(shut, rearOnly, BusInterior.DoorMidA),
            Prefix + "rear single leaf responds only to the second door channel");
        check(SameTransform(shut, rearOnly, BusInterior.DoorFrontA) && SameTransform(shut, rearOnly, BusInterior.DoorFrontB) &&
              !SameTransform(shut, frontOnly, BusInterior.DoorFrontA) && !SameTransform(shut, frontOnly, BusInterior.DoorFrontB),
            Prefix + "both front leaves respond only to the first door channel");
    }

    // The wrap surfaces (the window skin and one piece per door leaf, children of their leaves): present at every LOD, each leaf's
    // piece moves with its leaf, the window skin and the faces' pictures cover windows, and nothing shows without a wrap.
    static void WrapSurfaces(Action<bool, string> check, BusInterior bus, KitModelData data, BusInteriorSpec spec)
    {
        VehicleProfile model = bus.AuthoredModel;
        string name = Prefix + "wrap surfaces: ";
        check(model != null && model.WrapWindows && model.WrapDoors && model.WrapSurfaceCount == 4 && model.WrapLayout == 1 && model.WrapOutlineMm == 10f,
            name + "the profile asks for window and door surfaces, layout 1, a 10 mm outline");
        if (model == null || model.WrapSurfaceCount != 4) return;
        int first = BusInterior.PartCount + model.DoorDriveCount;
        check(data.Parts[first].Name == "WrapSurface0" && data.Parts[first].Parent == BusInterior.Shell, name + "the window skin hangs from the shell");
        for (int leaf = 0; leaf < 3; leaf++)
        {
            int part = first + 1 + leaf, parent = ActiveDoors[leaf];
            check(data.Parts[part].Parent == parent && data.Parts[part].Name == "WrapSurface" + (1 + leaf), name + "door piece " + leaf + " hangs from its leaf");
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
                check(HasMesh(data, part, lod), name + "door piece " + leaf + " has LOD" + lod + " geometry");
        }
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            check(data.Parts[first].Data[lod] == null || data.Parts[first].Data[lod].P.Length == 0,
                name + "the window skin is drawn by the wrap faces (its own part draws nothing) at LOD" + lod);
        // the door pieces ride on their leaves: their parent chain carries the leaf's pose
        var shut = Pose(data, spec, float3.zero);
        var open = Pose(data, spec, new float3(1f));
        for (int leaf = 0; leaf < 3; leaf++)
        {
            int part = first + 1 + leaf;
            float3 a = math.transform(shut.PartToModel(part), float3.zero), b = math.transform(open.PartToModel(part), float3.zero);
            check(math.distance(a, b) > .1f, name + "door piece " + leaf + " moves with its leaf when the door opens");
        }
        // nothing of it is drawn without a wrap, the door pieces and the faces show with their side's picture
        var none = Pose(data, spec, float3.zero);
        bool hidden = true;
        for (int leaf = 0; leaf < 3; leaf++) hidden &= !none.Visible[first + 1 + leaf];
        check(hidden, name + "no door piece shows without a wrap");
        float kerb = -spec.DriverSide;
        var wrapped = new KitPoseBuffer(data.Parts);
        var pose = BusInterior.BoardPoseOf(0, true);
        pose.Wrap = BusInterior.WrapFaces.Left | BusInterior.WrapFaces.Right;
        BusInterior.Apply(wrapped, pose, spec);
        bool shown = true;
        for (int leaf = 0; leaf < 3; leaf++) shown &= wrapped.Visible[first + 1 + leaf];
        check(shown, name + "the door pieces show with the side pictures");
        // the faces cover the windows: the left/right wrap meshes have more than the livery's triangles alone
        int rightWrap = data.Parts[BusInterior.WrapRight].Data[0]?.Idx.Length / 3 ?? 0;
        var areas = model.Mesh.WrapAreas(kerb > 0 ? BusInterior.WrapFace.Right : BusInterior.WrapFace.Left);
        int windows = 0, doors = 0;
        foreach ((byte kind, float2x3 uv) in areas) { if (kind == 1) windows++; else if (kind == 2) doors++; }
        check(windows > 30 && doors >= 40 && rightWrap > windows, name + "the kerb side's wrap areas: " + windows + " window and " + doors + " door triangles, in the face's mesh too");
        var other = model.Mesh.WrapAreas(kerb > 0 ? BusInterior.WrapFace.Left : BusInterior.WrapFace.Right);
        check(other.Count > 30 && !other.Exists(a => a.kind == 2), name + "the other side has windows but no door pieces");
        // the projected template: what the viewer sees from outside, pixel by pixel (VehicleMesh.ProjectWrap)
        const int pw = 1024, ph = 392;
        var classes = new byte[pw * ph];
        var depth = new float[pw * ph];
        for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;
        model.Mesh.ProjectWrap(kerb > 0 ? BusInterior.WrapFace.Right : BusInterior.WrapFace.Left, classes, depth, pw, ph, 0f, 1f, kerb > 0);
        int bodyPixels = 0, windowPixels = 0, doorPixels = 0, behindPixels = 0;
        foreach (byte c in classes)
        {
            if (c == VehicleMesh.ClassBody) bodyPixels++;
            else if (c == VehicleMesh.ClassWindow) windowPixels++;
            else if (c == VehicleMesh.ClassDoor) doorPixels++;
            else if (c == VehicleMesh.ClassBehind) behindPixels++;
        }
        check(bodyPixels > pw * ph / 10 && windowPixels > pw * ph / 10 && doorPixels > pw * ph / 20 && behindPixels > 0,
            name + "the projected kerb side has body paint, windows, door pieces and parts in front of the paint (" + bodyPixels + ", " + windowPixels + ", " + doorPixels + ", " + behindPixels + " pixels)");
        var farClasses = new byte[pw * ph];
        var farDepth = new float[pw * ph];
        for (int i = 0; i < farDepth.Length; i++) farDepth[i] = float.PositiveInfinity;
        model.Mesh.ProjectWrap(kerb > 0 ? BusInterior.WrapFace.Left : BusInterior.WrapFace.Right, farClasses, farDepth, pw, ph, 0f, 1f, kerb <= 0);
        int farDoors = 0;
        foreach (byte c in farClasses) if (c == VehicleMesh.ClassDoor) farDoors++;
        check(farDoors == 0, name + "the other side has no door pieces in its projection");
        // the projection is deterministic and the same on a second run
        var again = new byte[pw * ph];
        var againDepth = new float[pw * ph];
        for (int i = 0; i < againDepth.Length; i++) againDepth[i] = float.PositiveInfinity;
        model.Mesh.ProjectWrap(kerb > 0 ? BusInterior.WrapFace.Right : BusInterior.WrapFace.Left, again, againDepth, pw, ph, 0f, 1f, kerb > 0);
        check(again.AsSpan().SequenceEqual(classes), name + "the projection gives the same picture twice");
        // every area triangle lies inside the picture
        bool inside = true;
        foreach ((byte kind, float2x3 uv) in areas)
            foreach (float2 p in new[] { uv.c0, uv.c1, uv.c2 }) inside &= p.x >= 0f && p.x <= 1f && p.y >= 0f && p.y <= 1f;
        check(inside, name + "the areas' picture coordinates lie in the picture");
    }

    // A linkage leaf (the profile's linkage): shut is the rest pose; the shaft's crank carries the leaf's joint, the roller stays
    // on its rail; open, the leaf stands about square to the body at the jamb it parks at, mostly inside, only its meeting edge
    // a little outside (user review 2026-10-16), its inner face (the grab rail) towards the doorway.
    static void OutwardLeaf(Action<bool, string> check, KitModelData data, BusInteriorSpec spec, int part, DoorLinkage linkage, float2 opening,
        KitPoseBuffer shut, KitPoseBuffer half, KitPoseBuffer open)
    {
        var def = data.Parts[part];
        string name = Prefix + def.Name + " (linkage): ";
        check(Near(def.Pivot.x, linkage.Joint.x, .001f) && Near(def.Pivot.z, linkage.Joint.y, .001f), name + "pivots at its linkage joint");
        check(shut.Visible[part] && half.Visible[part] && open.Visible[part], name + "remains visible while opening");
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            check(HasMesh(data, part, lod), name + "has LOD" + lod + " geometry");
        int drive = linkage.DrivePart;
        check(drive >= BusInterior.PartCount && drive < data.Parts.Length && data.Parts[drive].Name.StartsWith("DoorDrive"),
            name + "names its door drive part " + drive);
        if (drive >= data.Parts.Length) return;
        var driveDef = data.Parts[drive];
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            check(HasMesh(data, drive, lod), name + "its door drive has LOD" + lod + " geometry");
        check(SameTransform(shut, Rest(data), part) && SameTransform(shut, Rest(data), drive), name + "shut is exactly the rest pose");
        var mesh = def.Data[0];
        if (mesh == null || mesh.P.Length == 0) return;
        float kerb = -spec.DriverSide;
        float maxOut = float.NegativeInfinity, minOut = float.PositiveInfinity;
        float travel = 0f, halfTravel = 0f;
        foreach (float3 p in mesh.P)
        {
            float3 rest = math.transform(shut.PartToModel(part), p), wide = math.transform(open.PartToModel(part), p);
            float3 mid = math.transform(half.PartToModel(part), p);
            travel = math.max(travel, math.distance(rest, wide));
            halfTravel = math.max(halfTravel, math.distance(rest, mid));
            maxOut = math.max(maxOut, wide.x * kerb);
            minOut = math.min(minOut, wide.x * kerb);
        }
        check(travel > .35f && halfTravel > .05f && halfTravel < travel, name + "moves its leaf, with a finite intermediate opening");
        check(maxOut > spec.HalfWidth + .08f && maxOut < spec.HalfWidth + .20f && minOut < spec.HalfWidth - .35f,
            name + "open, the leaf stands mostly inside the bus, its meeting edge 0.08-0.20 m outside (" + (maxOut - spec.HalfWidth) + " m)");
        // the leaf's turn: its body-along axis (+z) against the model's
        float3 along = math.mul(open.PartToModel(part), new float4(math.rotate(math.inverse(def.Rot), new float3(0, 0, 1)), 0f)).xyz;
        float turned = math.degrees(math.acos(math.clamp(math.abs(along.z), 0f, 1f)));
        check(turned > 79.9f && turned < 82.1f, name + "open, the leaf has turned about square to the body (" + turned + " degrees)");
        float3 wideCentre = math.transform(open.PartToModel(part), math.transform(def.ModelToPart, new float3(def.Pivot.x, def.Pivot.y, opening.x)));
        check(math.abs(wideCentre.z - opening.x) > .2f, name + "open, the leaf has left the middle of its opening");
        foreach (KitPoseBuffer pose in new[] { shut, half, open })
        {
            // the joint is where the turned arm puts it; the shaft's axis does not move
            float3 joint = math.transform(pose.PartToModel(part), float3.zero);
            float3 armEnd = math.transform(pose.PartToModel(drive), math.transform(driveDef.ModelToPart, new float3(linkage.Joint.x, joint.y, linkage.Joint.y)));
            float3 shaftAxis = math.transform(pose.PartToModel(drive), float3.zero);
            check(Near(joint.xz, armEnd.xz, 1e-4f) && Near(shaftAxis.xz, linkage.Shaft, 1e-4f),
                name + "the shaft's arm carries the joint, the shaft stays on its axis");
            float3 roller = math.transform(pose.PartToModel(part), math.transform(def.ModelToPart, new float3(linkage.Guide.x, def.Pivot.y, linkage.Guide.y)));
            check(Near(roller.x, linkage.Guide.x, 1e-4f), name + "the roller stays on its rail");
        }
    }

    // The front pair's meeting stiles part without touching: at every opening their nearest points stay 1.5 mm apart.
    static void FrontPairParts(Action<bool, string> check, KitModelData data, BusInteriorSpec spec, float2 frontDoor)
    {
        float closest(KitPoseBuffer pose)
        {
            float best = float.PositiveInfinity;
            var a = data.Parts[BusInterior.DoorFrontA].Data[0];
            var b = data.Parts[BusInterior.DoorFrontB].Data[0];
            var ap = new System.Collections.Generic.List<float3>();
            foreach (float3 p in a.P)
            {
                float3 m = math.transform(pose.PartToModel(BusInterior.DoorFrontA), p);
                float3 r = math.transform(Rest(data).PartToModel(BusInterior.DoorFrontA), p);
                if (math.abs(r.z - frontDoor.x) < .04f) ap.Add(m);
            }
            foreach (float3 p in b.P)
            {
                float3 r = math.transform(Rest(data).PartToModel(BusInterior.DoorFrontB), p);
                if (math.abs(r.z - frontDoor.x) >= .04f) continue;
                float3 m = math.transform(pose.PartToModel(BusInterior.DoorFrontB), p);
                foreach (float3 q in ap) best = math.min(best, math.distance(m, q));
            }
            return best;
        }
        float shut = closest(Pose(data, spec, float3.zero)), worst = float.PositiveInfinity;
        for (int i = 1; i <= 20; i++) worst = math.min(worst, closest(Pose(data, spec, new float3(i / 20f, 0f, 0f))));
        // A's outer lip and B's inner seal overlap the centre line, 2 mm apart: as the leaves part, the lip slides off the seal
        check(shut > .0015f && worst > .0015f, Prefix + "the front pair's meeting stiles part without touching (shut " + shut + " m, closest " + worst + " m)");
    }

    static KitPoseBuffer s_Rest;
    static KitModelData s_RestData;
    static KitPoseBuffer Rest(KitModelData data)
    {
        if (s_RestData != data) { s_Rest = new KitPoseBuffer(data.Parts); s_RestData = data; }
        return s_Rest;
    }

    static bool Near(float2 a, float2 b, float tolerance) => math.cmax(math.abs(a - b)) <= tolerance;

    static void Mirrors(Action<bool, string> check, KitModelData data, BusInteriorSpec spec)
    {
        var driver = MirrorSurface.Of(spec, 0);
        var door = MirrorSurface.Of(spec, 1);
        var auxiliary = MirrorSurface.Of(spec, 2);
        check(driver.Centre.x < -spec.HalfWidth && door.Centre.x > spec.HalfWidth && auxiliary.Centre.x > spec.HalfWidth &&
              door.Size.y > driver.Size.y && auxiliary.Oval && !driver.Oval && !door.Oval,
            Prefix + "short mirror is on the driver side and long mirror plus convex auxiliary are on the door side");
        check(Near(door.Centre.x, 1.3261f) && Near(driver.Centre.x, -1.2261f) && Near(driver.Centre.z, 3.000f) &&
              Near(door.Centre.z, 3.710f) && door.Centre.z > spec.FrontZ + 0.10f && door.Centre.z < spec.FrontZ + 0.20f &&
              driver.Centre.z < spec.FrontZ,
            Prefix + "door-side long mirror reaches ahead of the bumper (head front ~0.3 m, not the first build's 0.5 m) while the driver-side mirror stays close to the cab");
        var inside = Pose(data, spec, float3.zero);
        var cabPose = BusInterior.BoardPoseOf(0, false);
        BusInterior.Apply(inside, cabPose, spec);
        check(inside.Visible[BusInterior.Outside], Prefix + "physical mirror frames remain visible from inside the cab");

        var def = data.Parts[BusInterior.Outside];
        var physical = def.Data[0];
        float4x4 model = new float4x4(def.Rot, def.Pivot);
        float3 eye = BusInterior.Eye(spec);
        for (int i = 0; i < 3; i++)
        {
            var surface = MirrorSurface.Of(spec, i);
            float3 normal = math.mul(surface.Frame, new float3(0f, 0f, -1f));
            float3 up = math.mul(surface.Frame, new float3(0f, 1f, 0f));
            check(math.all(math.isfinite(surface.Centre)) && math.all(math.isfinite(surface.Normal)) &&
                  Near(math.length(surface.Normal), 1f) && math.dot(normal, surface.Normal) > 0.9999f &&
                  math.dot(up, surface.Up) > 0.9999f && math.abs(math.dot(surface.Up, surface.Normal)) < 0.0001f,
                Prefix + "mirror " + i + " has an orthonormal frame facing its authored glass normal");
            check(Near(math.distance(surface.Centre, surface.OverlayCentre), 0.0007f, 0.00001f) &&
                  math.dot(surface.OverlayCentre - surface.Centre, surface.Normal) > 0f,
                Prefix + "mirror " + i + " reflection overlay sits just above the physical glass");
            float3 incident = math.normalize(surface.Centre - eye);
            float3 reflected = incident - 2f * math.dot(incident, surface.Normal) * surface.Normal;
            check(math.dot(eye - surface.Centre, surface.Normal) > 0f && reflected.z < -0.5f &&
                  (i == 2 ? reflected.y < -0.4f : reflected.z < -0.9f),
                Prefix + "mirror " + i + " reflects toward the rear" + (i == 2 ? " and down toward the blind spot" : ""));
            if (i == 0)
            {
                // set as a driver sets it: the bus's own side a sliver at the inner edge (the mirror camera sees about 6 degrees
                // either side of the reflected line of sight), the lane behind, a little down
                float outward = math.degrees(math.atan2(-reflected.x, -reflected.z));
                check(outward > 3f && outward < 7f && reflected.y < 0f && reflected.y > -0.07f,
                    Prefix + "driver's mirror shows the bus side at its inner edge (reflected " + outward.ToString("0.0") + " degrees outward)");
            }

            bool contourFinite = true, sourceMatches = physical != null;
            float signedArea = 0f;
            for (int p = 0; p < surface.Segments; p++)
            {
                float2 point = surface.Outline(p), next = surface.Outline((p + 1) % surface.Segments);
                contourFinite &= math.all(math.isfinite(point));
                signedArea += point.x * next.y - point.y * next.x;
                if (p % (surface.Segments / 8) != 0 || physical == null) continue;
                float3 expected = surface.Centre + math.mul(surface.Frame, new float3(point, 0f));
                float nearest = float.PositiveInfinity;
                foreach (float3 vertex in physical.P)
                    nearest = math.min(nearest, math.distancesq(expected, math.transform(model, vertex)));
                sourceMatches &= nearest < 0.000004f;
            }
            check(contourFinite && signedArea > 0f && surface.RadialRings == (i == 2 ? 9 : 1) &&
                  Near(surface.ConvexDepth, i == 2 ? 0.007f : 0f),
                Prefix + "mirror " + i + " has a finite rounded outline and the correct convex shape");
            check(sourceMatches, Prefix + "mirror " + i + " runtime outline aligns with the actual embedded mirror rim");
        }
    }

    static void DestinationSigns(Action<bool, string> check, BusInteriorSpec spec)
    {
        BusInterior.FrontSignFrame(spec, out _, out float2 frontSize);
        int2 front = BusInterior.FrontSignMatrix(spec);
        check(front.x == 120 && front.y == 16 && Near(frontSize.x, 1.50f) && frontSize.y <= .205f &&
              Near(frontSize.x / front.x, frontSize.y / front.y, .000001f),
            Prefix + "front destination sign has square dots within its authored aperture");
        bool sidePresent = BusInterior.SideSignFrame(spec, out _, out float2 sideSize, out _);
        int2 side = BusInterior.SideSignMatrix(spec);
        check(sidePresent && side.x == 84 && side.y == 16 && Near(sideSize.x, .83f) && sideSize.y <= .16f &&
              Near(sideSize.x / side.x, sideSize.y / side.y, .000001f),
            Prefix + "side destination sign has square dots within its authored aperture");
        int2 genericFront = BusInterior.FrontSignMatrix(BusInteriorSpec.Default);
        int2 genericSide = BusInterior.SideSignMatrix(BusInteriorSpec.Default);
        BusInterior.FrontSignFrame(BusInteriorSpec.Default, out _, out float2 genericFrontSize);
        check(genericFront.x == 160 && genericFront.y == 16 && Near(genericFrontSize.x, 1.62f) && Near(genericFrontSize.y, .16f),
            Prefix + "generic front destination signs retain their existing matrix and field size");
        check(genericSide.x == 112 && genericSide.y == 16,
            Prefix + "generic side destination signs retain their existing matrix");
        BusInterior.RearRouteFrame(spec, out float3 centre, out float2 size);
        int2 matrix = BusInterior.RearRouteMatrix(spec);
        check(matrix.x == 104 && matrix.y == 16 && Near(size.x, 1.16f) && Near(centre.x, 0f),
            Prefix + "rear route sign retains the full housing width and centred 104 x 16 matrix");
        check(Near(size.x / matrix.x, size.y / matrix.y, 0.000001f),
            Prefix + "rear route sign uses square dots without stretching glyphs");
        int2 generic = BusInterior.RearRouteMatrix(BusInteriorSpec.Default);
        BusInterior.RearRouteFrame(BusInteriorSpec.Default, out _, out float2 genericSize);
        check(generic.x == 48 && generic.y == 16 && Near(genericSize.x, .42f) && Near(genericSize.y, .16f),
            Prefix + "generic rear displays retain their existing resolution and dimensions");
        string fontPath = LedFontTests.FontPath();
        if (fontPath == null) return;
        var font = new CDLC.BusDriver.Interior.LedFont();
        if (!font.Load(fontPath)) return;
        var composer = new CDLC.BusDriver.Interior.LedComposer(font, matrix.x, matrix.y);
        composer.RenderSign("", "12", "", 0);
        int left = matrix.x, right = -1;
        for (int x = 0; x < matrix.x; x++)
            for (int y = 0; y < matrix.y; y++)
                if (composer.Buffer[x, y]) { left = math.min(left, x); right = math.max(right, x); }
        check(right > left && math.abs((left + right + 1f) * .5f - matrix.x * .5f) <= 1.5f,
            Prefix + "rear route digits are centred with empty columns on both sides");
    }

    static void Views(Action<bool, string> check, BusInterior bus, KitModelData data, BusInteriorSpec spec)
    {
        var inside = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(inside, BusInterior.BoardPoseOf(0, false), spec);
        var outside = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(outside, BusInterior.BoardPoseOf(0, true), spec);
        bool insideBody = true, outsideBody = true;
        foreach (int part in new[] { BusInterior.Exterior, BusInterior.Glazing, BusInterior.Livery,
                                    BusInterior.WheelFL, BusInterior.WheelFR, BusInterior.WheelRL, BusInterior.WheelRR })
        {
            insideBody &= inside.Visible[part];
            outsideBody &= outside.Visible[part];
        }
        check(insideBody, Prefix + "cab view retains the closed authored body, glazing, livery and all four wheels");
        check(outsideBody,
            Prefix + "chase view shows the complete authored body");
        float3 eye = BusInterior.Eye(spec);
        check(math.all(math.isfinite(eye)) && eye.x < 0f && math.abs(eye.x) < spec.HalfWidth && eye.y > spec.FloorY + 1f &&
              eye.y < spec.RoofY && eye.z < spec.FrontZ && eye.z > spec.RearZ,
            Prefix + "driver camera remains inside the authored cab");
        check(eye.z <= bus.WheelHub.z - 0.2f && eye.y > bus.WheelHub.y + 0.2f,
            Prefix + "driver camera is behind and above the steering wheel");
    }

    static void Instruments(Action<bool, string> check, KitModelData data, BusInteriorSpec spec)
    {
        var parked = new KitPoseBuffer(data.Parts);
        var driving = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(parked, BusInterior.BoardPoseOf(0, true), spec);
        BusInterior.Apply(driving, BusInterior.BoardPoseOf(1, true), spec);
        foreach (int part in new[] { BusInterior.SteeringWheel, BusInterior.SpeedNeedle, BusInterior.RpmNeedle })
        {
            var mesh = data.Parts[part].Data[0];
            check(HasMesh(data, part, 0), Prefix + data.Parts[part].Name + " has authored moving geometry");
            if (mesh == null || mesh.P.Length == 0) continue;
            float3 vertex = mesh.P[0];
            float radiusSq = -1f;
            foreach (float3 point in mesh.P)
            {
                float r = math.lengthsq(point.xy);
                if (r > radiusSq) { radiusSq = r; vertex = point; }
            }
            float4x4 a = parked.PartToModel(part), b = driving.PartToModel(part);
            float displacement = math.distance(math.transform(a, vertex), math.transform(b, vertex));
            check(math.distance(math.transform(a, float3.zero), math.transform(b, float3.zero)) < 0.0001f &&
                  displacement > (part == BusInterior.SteeringWheel ? 0.05f : 0.002f),
                Prefix + data.Parts[part].Name + " moves a real off-axis vertex around a fixed pivot");
            float3 axisA = math.normalize(a.c2.xyz), axisB = math.normalize(b.c2.xyz);
            check(math.dot(axisA, axisB) > 0.999f, Prefix + data.Parts[part].Name + " retains its authored rotation axis");
        }
    }

    static void Wraps(Action<bool, string> check, BusInterior bus, KitModelData data)
    {
        foreach (BusInterior.WrapFace face in Enum.GetValues(typeof(BusInterior.WrapFace)))
        {
            int part = BusInterior.WrapLeft + (int)face;
            float2 size = bus.WrapSize(face);
            check(bus.HasWrap(face) && math.all(math.isfinite(size)) && math.all(size > 0f) && bus.WrapLayout(face).Count > 0,
                Prefix + face + " custom-image wrap has a finite template and visible surface layout");
            float expectedWidth = face == BusInterior.WrapFace.Front || face == BusInterior.WrapFace.Rear ? 2.35f : 7.145f;
            check(Near(size.x, expectedWidth, 0.1f), Prefix + face + " wrap uses this bus's full width/length");
            if (face == BusInterior.WrapFace.Roof)
                check(Near(size.y, 2.35f, 0.1f), Prefix + "roof wrap uses this bus's full width");
            else
                check(Near(size.y, 2.74f, 0.1f), Prefix + face + " wrap covers the painted body height");

            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            {
                check(HasMesh(data, part, lod), Prefix + face + " wrap has LOD" + lod + " geometry");
                var mesh = data.Parts[part].Data[lod];
                if (mesh == null || mesh.UV == null || mesh.Idx == null) continue;
                bool uvValid = true;
                foreach (float2 uv in mesh.UV)
                    uvValid &= math.all(math.isfinite(uv)) && math.all(uv >= -0.0001f) && math.all(uv <= 1.0001f);
                float uvArea = 0f;
                for (int i = 0; i + 2 < mesh.Idx.Length; i += 3)
                {
                    int a = mesh.Idx[i], b = mesh.Idx[i + 1], c = mesh.Idx[i + 2];
                    if ((uint)a >= (uint)mesh.UV.Length || (uint)b >= (uint)mesh.UV.Length || (uint)c >= (uint)mesh.UV.Length)
                    { uvValid = false; continue; }
                    float2 u = mesh.UV[b] - mesh.UV[a], v = mesh.UV[c] - mesh.UV[a];
                    uvArea += 0.5f * math.abs(u.x * v.y - u.y * v.x);
                }
                check(uvValid && uvArea > 0.00001f, Prefix + face + " wrap LOD" + lod + " has usable normalized image UVs");
            }

            var poser = new KitPoseBuffer(data.Parts);
            var pose = BusInterior.BoardPoseOf(0, true);
            pose.Wrap = (BusInterior.WrapFaces)(1 << (int)face);
            BusInterior.Apply(poser, pose, bus.Spec);
            bool isolated = true;
            for (int other = 0; other < BusInterior.WrapFaceCount; other++)
                isolated &= poser.Visible[BusInterior.WrapLeft + other] == (other == (int)face);
            check(isolated, Prefix + face + " wrap is independently selectable in chase view");
            pose.Outside = false;
            BusInterior.Apply(poser, pose, bus.Spec);
            isolated = true;
            for (int other = 0; other < BusInterior.WrapFaceCount; other++)
                isolated &= poser.Visible[BusInterior.WrapLeft + other] == (other == (int)face);
            check(isolated, Prefix + face + " wrap is independently selectable in the authored cab view");
        }

        var inside = new KitPoseBuffer(data.Parts);
        var insidePose = BusInterior.BoardPoseOf(0, false);
        insidePose.Wrap = BusInterior.WrapFaces.All;
        BusInterior.Apply(inside, insidePose, bus.Spec);
        bool allShown = true;
        for (int face = 0; face < BusInterior.WrapFaceCount; face++) allShown &= inside.Visible[BusInterior.WrapLeft + face];
        check(allShown, Prefix + "all selected custom-image wraps remain on the closed authored body in cab view");
        insidePose.Wrap = BusInterior.WrapFaces.None;
        BusInterior.Apply(inside, insidePose, bus.Spec);
        bool noneShown = true;
        for (int face = 0; face < BusInterior.WrapFaceCount; face++) noneShown &= !inside.Visible[BusInterior.WrapLeft + face];
        check(noneShown, Prefix + "unselected custom-image wraps remain hidden in cab view");
    }

    static KitPoseBuffer Pose(KitModelData data, BusInteriorSpec spec, float3 doorOpen)
    {
        var buffer = new KitPoseBuffer(data.Parts);
        var pose = BusInterior.BoardPoseOf(0, true);
        pose.DoorOpen = doorOpen;
        BusInterior.Apply(buffer, pose, spec);
        return buffer;
    }

    static bool SameTransform(KitPoseBuffer a, KitPoseBuffer b, int part)
    {
        var x = a.PartToModel(part);
        var y = b.PartToModel(part);
        return math.cmax(math.abs(x.c0 - y.c0)) < 1e-5f && math.cmax(math.abs(x.c1 - y.c1)) < 1e-5f &&
               math.cmax(math.abs(x.c2 - y.c2)) < 1e-5f && math.cmax(math.abs(x.c3 - y.c3)) < 1e-5f;
    }

    static bool HasMesh(KitModelData data, int part, int lod)
    {
        var def = data.Parts[part];
        var mesh = def.Data[lod] ?? (def.MeshOf >= 0 ? data.Parts[def.MeshOf].Data[lod] : null);
        return mesh != null && mesh.P != null && mesh.P.Length > 0 && mesh.Idx != null && mesh.Idx.Length >= 3;
    }

    static float BackEdge(float2 door) => door.x - 0.5f * door.y;
    static float FrontEdge(float2 door) => door.x + 0.5f * door.y;
    static bool Near(float a, float b, float tolerance = 0.0001f) => math.abs(a - b) <= tolerance;
}
