using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The longer iEV10 keeps its own authored model and equipment coordinates.
static class Sunwin6109Tests
{
    const string Prefix = "SWB6109EV12G: ";
    static bool Near(float a, float b, float epsilon = .001f) => math.abs(a - b) <= epsilon;
    static bool Near(float3 a, float3 b, float epsilon = .001f) => math.distance(a, b) <= epsilon;
    static bool Near(float2 a, float2 b, float epsilon = .001f) => math.distance(a, b) <= epsilon;
    static bool HasMesh(KitModelData data, int part, int lod) => data.Parts[part].Data[lod]?.Idx.Length > 0;

    static int RuntimeCardReaderTriangles(BusInterior bus, int lod)
    {
        var kit = new MeshKit();
        kit.Reset(lod);
        foreach (var reader in bus.AuthoredModel.CardReaders)
            using (kit.At(reader.origin, reader.frame))
                BusInterior.CardReader(kit, lod);
        return kit.Triangles;
    }

    static bool ClosedStrap(KitMeshData mesh)
    {
        if (mesh == null || mesh.Idx.Length == 0) return false;
        var points = new Dictionary<(int, int, int), int>();
        var welded = new int[mesh.P.Length];
        for (int i = 0; i < mesh.P.Length; i++)
        {
            float3 p = mesh.P[i];
            var key = ((int)Math.Round(p.x * 1000000), (int)Math.Round(p.y * 1000000), (int)Math.Round(p.z * 1000000));
            if (!points.TryGetValue(key, out int index)) { index = points.Count; points.Add(key, index); }
            welded[i] = index;
        }
        var edges = new Dictionary<(int, int), int>();
        for (int i = 0; i < mesh.Idx.Length; i += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = welded[mesh.Idx[i + j]], b = welded[mesh.Idx[i + (j + 1) % 3]];
                if (a == b) return false;
                var edge = a < b ? (a, b) : (b, a);
                edges.TryGetValue(edge, out int count); edges[edge] = count + 1;
            }
        foreach (int count in edges.Values) if (count != 2) return false;
        return true;
    }

    // The wrap surfaces of the two outward-swing doors (parts 38..42, after the four door drives 34..37): the windows' skin under the
    // shell and one sheet per leaf, a child of its leaf (4..7). Pack-described (wrap.windows / wrap.doors in vehicle.json), found by
    // BusDriver from the mesh alone.
    static void WrapSurfaces(Action<bool, string> check, BusInterior bus, KitModelData data, BusInteriorSpec spec)
    {
        VehicleProfile model = bus.AuthoredModel;
        string name = Prefix + "wrap surfaces: ";
        check(model != null && model.WrapWindows && model.WrapDoors && model.WrapSurfaceCount == 5 && model.DoorDriveCount == 4 && model.WrapLayout == 1 && model.WrapOutlineMm == 10f,
            name + "the profile asks for window and door surfaces (2 doors x 2 leaves + the windows' skin) after 4 door drives, layout 1, a 10 mm outline");
        if (model == null || model.WrapSurfaceCount != 5) return;
        int first = BusInterior.PartCount + model.DoorDriveCount;
        check(data.Parts[first].Name == "WrapSurface0" && data.Parts[first].Parent == BusInterior.Shell, name + "the windows' skin hangs from the shell");
        int[] leaves = { BusInterior.DoorFrontA, BusInterior.DoorFrontB, BusInterior.DoorMidA, BusInterior.DoorMidB };
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            int part = first + 1 + leaf;
            check(data.Parts[part].Parent == leaves[leaf] && data.Parts[part].Name == "WrapSurface" + (1 + leaf), name + "door sheet " + leaf + " hangs from its leaf");
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
                check(HasMesh(data, part, lod), name + "door sheet " + leaf + " has LOD" + lod + " geometry");
        }
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            check(data.Parts[first].Data[lod] == null || data.Parts[first].Data[lod].P.Length == 0,
                name + "the windows' skin is drawn by the wrap faces (its own part draws nothing) at LOD" + lod);
        // the sheets ride on their leaves
        var shut = new KitPoseBuffer(data.Parts);
        var open = new KitPoseBuffer(data.Parts);
        var pose = BusInterior.BoardPoseOf(0, true);
        BusInterior.Apply(shut, pose, spec);
        pose.DoorOpen = new float3(1f);
        BusInterior.Apply(open, pose, spec);
        for (int leaf = 0; leaf < leaves.Length; leaf++)
        {
            int part = first + 1 + leaf;
            // a point of the sheet's own geometry (the sheet is stored about its pivot, its leaf's joint): the far corner swings with the leaf
            float3[] points = data.Parts[part].Data[0].P;
            float3 corner = points[0], far = corner;
            foreach (float3 p in points) if (math.length(p) > math.length(far)) far = p;
            float3 a = math.transform(shut.PartToModel(part), far), b = math.transform(open.PartToModel(part), far);
            check(math.distance(a, b) > .3f, name + "door sheet " + leaf + " moves with its leaf when the door opens (" + math.distance(a, b).ToString("0.00") + " m)");
            // the sheet's pivot is its leaf's pivot (the linkage joint), shut and open: the sheet rides its leaf
            float3 hingeShut = math.transform(shut.PartToModel(leaves[leaf]), float3.zero), hingeOpen = math.transform(open.PartToModel(leaves[leaf]), float3.zero);
            float3 pivotShut = math.transform(shut.PartToModel(part), float3.zero), pivotOpen = math.transform(open.PartToModel(part), float3.zero);
            check(math.distance(pivotShut, hingeShut) < .001f && math.distance(pivotOpen, hingeOpen) < .001f, name + "door sheet " + leaf + " turns about its leaf's pivot");
            // and the leaf itself swings: its point that moves most (an outward-swing leaf turns about its moving linkage joint, so its
            // jamb end moves little, about 0.2 m, and its meeting edge sweeps round to the jamb)
            float leafMoved = 0f;
            foreach (float3 p in data.Parts[leaves[leaf]].Data[0].P)
                leafMoved = math.max(leafMoved, math.distance(math.transform(shut.PartToModel(leaves[leaf]), p), math.transform(open.PartToModel(leaves[leaf]), p)));
            check(leafMoved > .3f, name + "leaf " + leaf + " itself swings (" + leafMoved.ToString("0.00") + " m at the point that moves most)");
        }
        // nothing is drawn without a wrap; with the side pictures the door sheets show, once per leaf
        var none = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(none, BusInterior.BoardPoseOf(0, true), spec);
        bool hidden = true;
        for (int leaf = 0; leaf < leaves.Length; leaf++) hidden &= !none.Visible[first + 1 + leaf];
        check(hidden, name + "no door sheet shows without a wrap");
        var wrapped = new KitPoseBuffer(data.Parts);
        var withWrap = BusInterior.BoardPoseOf(0, true);
        withWrap.Wrap = BusInterior.WrapFaces.Left | BusInterior.WrapFaces.Right;
        BusInterior.Apply(wrapped, withWrap, spec);
        bool shown = true;
        for (int leaf = 0; leaf < leaves.Length; leaf++) shown &= wrapped.Visible[first + 1 + leaf];
        check(shown, name + "the door sheets show with the side pictures");
        // the kerb side's picture areas: windows (side panes and the rear window's share) and the four sheets; the other side has windows only
        float kerb = -spec.DriverSide;
        var areas = model.Mesh.WrapAreas(kerb > 0 ? BusInterior.WrapFace.Right : BusInterior.WrapFace.Left);
        int windows = 0, doors = 0;
        foreach ((byte kind, float2x3 uv) in areas) { if (kind == 1) windows++; else if (kind == 2) doors++; }
        int sheetTriangles = 0;
        for (int leaf = 0; leaf < leaves.Length; leaf++) sheetTriangles += data.Parts[first + 1 + leaf].Data[0].Idx.Length / 3;
        check(windows >= 10 && doors == sheetTriangles && doors >= 8, name + "the kerb side's wrap areas: " + windows + " window triangles, " + doors +
              " door triangles (the 4 sheets' own " + sheetTriangles + ")");
        var other = model.Mesh.WrapAreas(kerb > 0 ? BusInterior.WrapFace.Left : BusInterior.WrapFace.Right);
        check(other.Count >= 10 && !other.Exists(a => a.kind == 2), name + "the other side has windows but no door sheets");
        bool inside = true;
        foreach ((byte kind, float2x3 uv) in areas)
            foreach (float2 p in new[] { uv.c0, uv.c1, uv.c2 }) inside &= p.x >= 0f && p.x <= 1f && p.y >= 0f && p.y <= 1f;
        check(inside, name + "the areas' picture coordinates lie in the picture");
        // the projected template shows body paint, windows and door sheets on the kerb side and no door sheets on the other
        const int pw = 1024, ph = 392;
        var classes = new byte[pw * ph];
        var depth = new float[pw * ph];
        for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;
        model.Mesh.ProjectWrap(kerb > 0 ? BusInterior.WrapFace.Right : BusInterior.WrapFace.Left, classes, depth, pw, ph, 0f, 1f, kerb > 0);
        int body = 0, window = 0, door = 0;
        foreach (byte c in classes)
        {
            if (c == VehicleMesh.ClassBody) body++;
            else if (c == VehicleMesh.ClassWindow) window++;
            else if (c == VehicleMesh.ClassDoor) door++;
        }
        check(body > pw * ph / 10 && window > pw * ph / 10 && door > pw * ph / 25, name + "the projected kerb side has body paint, windows and door sheets (" + body + ", " + window + ", " + door + " pixels)");
        var farClasses = new byte[pw * ph];
        var farDepth = new float[pw * ph];
        for (int i = 0; i < farDepth.Length; i++) farDepth[i] = float.PositiveInfinity;
        model.Mesh.ProjectWrap(kerb > 0 ? BusInterior.WrapFace.Left : BusInterior.WrapFace.Right, farClasses, farDepth, pw, ph, 0f, 1f, kerb <= 0);
        int farDoors = 0;
        foreach (byte c in farClasses) if (c == VehicleMesh.ClassDoor) farDoors++;
        check(farDoors == 0, name + "the other side has no door sheets in its projection");
        // Nothing stands outside the glass of the short window beside the middle door (z -2.0 .. -1.1, 1.7 .. 2.55 m up; checked with rays
        // through the runtime mesh): its whole area is window. The door leaves, stored in model space, were once moved by their pivots
        // and drew their frames and braces over it as parts in front of the picture.
        float bottom = model.WrapBottom, height = spec.RoofY - bottom, length = spec.FrontZ - spec.RearZ;
        int inPane = 0, notWindow = 0;
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                float zz = spec.RearZ + (x + .5f) / pw * length, yy = bottom + (y + .5f) / ph * height;     // the kerb (right) side runs rear to front
                if (kerb <= 0f || zz < -2.0f || zz > -1.1f || yy < 1.7f || yy > 2.55f) continue;
                inPane++;
                if (classes[y * pw + x] != VehicleMesh.ClassWindow) notWindow++;
            }
        check(kerb > 0f && inPane > 2000 && notWindow == 0, name + "the short window's area is all window in the projection, no leaf ghosts in front of it (" + notWindow + " of " + inPane + " pixels are not)");
    }

    // first: the first strap part (after the 34 contract parts and any door drives)
    internal static void CheckAuthoredStraps(Action<bool, string> check, KitModelData data, BusInteriorSpec spec, string prefix, int count,
        int first = BusInterior.PartCount)
    {
        check(data.Parts.Length == first + count, prefix + "keeps the fixed 34 parts" +
            (first > BusInterior.PartCount ? ", " + (first - BusInterior.PartCount) + " door drives" : "") + " plus " + count + " independent straps");
        if (data.Parts.Length != first + count) return;
        var rest = new KitPoseBuffer(data.Parts);
        var fore = new KitPoseBuffer(data.Parts);
        var side = new KitPoseBuffer(data.Parts);
        var pose = BusInterior.BoardPoseOf(0, false);
        BusInterior.Apply(rest, pose, spec);
        pose.StrapSwing = new float2(.25f, 0f); BusInterior.Apply(fore, pose, spec);
        pose.StrapSwing = new float2(0f, .20f); BusInterior.Apply(side, pose, spec);
        for (int number = 0; number < count; number++)
        {
            int part = first + number;
            var definition = data.Parts[part];
            check(definition.Name == "Strap" + number && definition.Parent == BusInterior.Shell &&
                  math.abs(definition.Rot.value.w) > .99999f && math.lengthsq(definition.Rot.value.xyz) < 1e-8f,
                prefix + "strap " + number + " retains its named body-aligned attachment frame");
            float3 tip = float3.zero; bool local = true;
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            {
                var mesh = definition.Data[lod];
                check(ClosedStrap(mesh), prefix + "strap " + number + " LOD" + lod + " retains a nonempty closed stem and grip");
                if (mesh == null) { local = false; continue; }
                float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
                foreach (float3 p in mesh.P)
                {
                    top = math.max(top, p.y); bottom = math.min(bottom, p.y);
                    if (lod == 0 && p.y < tip.y) tip = p;
                }
                local &= math.abs(top) < .045f && bottom < -.10f && bottom > -1.2f;
            }
            check(local && definition.Pivot.y > 2.0f && definition.Pivot.y < spec.RoofY &&
                  math.abs(data.Bounds.Max.y - spec.RoofY) < .8f,
                prefix + "strap " + number + " KitBuild geometry stays local to its elevated rail, not at ground level");
            float3 pivot = math.transform(rest.PartToModel(part), float3.zero);
            float3 a = math.transform(rest.PartToModel(part), tip);
            float3 b = math.transform(fore.PartToModel(part), tip);
            float3 c = math.transform(side.PartToModel(part), tip);
            check(Near(pivot, definition.Pivot, .00001f) &&
                  Near(pivot, math.transform(fore.PartToModel(part), float3.zero), .00001f) &&
                  Near(pivot, math.transform(side.PartToModel(part), float3.zero), .00001f),
                prefix + "strap " + number + " keeps its actual attachment fixed while swinging");
            check(math.abs(b.z-a.z) > .01f && math.abs(b.x-a.x) < .0001f &&
                  math.abs(c.x-a.x) > .01f && math.abs(c.z-a.z) < .0001f,
                prefix + "strap " + number + " actual grip vertex swings about separate X and Z axes");
        }
        if (count > 1)
            check(math.abs(math.dot(fore.Delta[first].value, fore.Delta[first + 1].value)) < .999999f,
                prefix + "adjacent straps have different swing amplitudes");
    }

    static void AuthoredVisibility(Action<bool, string> check, KitModelData data, BusInteriorSpec spec)
    {
        var buffer = new KitPoseBuffer(data.Parts);
        foreach (bool outside in new[] { false, true })
        {
            var pose = BusInterior.BoardPoseOf(0, outside);
            BusInterior.Apply(buffer, pose, spec);
            bool bodyShown = true;
            foreach (int part in new[] { BusInterior.Exterior, BusInterior.Glazing, BusInterior.Livery,
                                        BusInterior.WheelFL, BusInterior.WheelFR, BusInterior.WheelRL, BusInterior.WheelRR })
                bodyShown &= buffer.Visible[part];
            check(bodyShown, Prefix + (outside ? "chase" : "cab") + " retains the complete closed authored body and all four wheels");
            foreach (BusInterior.WrapFace face in Enum.GetValues(typeof(BusInterior.WrapFace)))
            {
                pose.Wrap = (BusInterior.WrapFaces)(1 << (int)face);
                BusInterior.Apply(buffer, pose, spec);
                bool isolated = true;
                for (int other = 0; other < BusInterior.WrapFaceCount; other++)
                    isolated &= buffer.Visible[BusInterior.WrapLeft + other] == (other == (int)face);
                check(isolated, Prefix + face + " wrap selection remains independent in " + (outside ? "chase" : "cab") + " view");
            }
            pose.Wrap = BusInterior.WrapFaces.None;
            BusInterior.Apply(buffer, pose, spec);
            bool noneShown = true;
            for (int face = 0; face < BusInterior.WrapFaceCount; face++) noneShown &= !buffer.Visible[BusInterior.WrapLeft + face];
            check(noneShown, Prefix + "unselected wraps remain hidden in " + (outside ? "chase" : "cab") + " view");
        }
    }

    internal static void Run(Action<bool, string> check)
    {
        SmallTriangles(check);
        var requested = TestPacks.Spec("SWB6109EV12G");
        var bus = new BusInterior(requested);
        var spec = bus.Spec;
        var old = new BusInterior(TestPacks.Spec("SWB6710EV01G"));
        check(bus.IsAuthored && spec.ModelVariant == TestPacks.Spec("SWB6109EV12G").ModelVariant && bus.AuthoredModel == TestPacks.Profile("SWB6109EV12G"), Prefix + "selects the separate authored model");
        check(Near(spec.FrontZ - spec.RearZ, 10.5f) && Near(spec.HalfWidth * 2f, 2.55f) && Near(spec.RoofY, 3.2f), Prefix + "exact announced dimensions survive snapping");
        check(Near(spec.FrontAxleZ, 2.85f) && Near(spec.RearAxleZ, -2.75f) && Near(spec.FrontZ - spec.FrontAxleZ, 2.4f) && Near(spec.RearAxleZ - spec.RearZ, 2.5f), Prefix + "5600 mm wheelbase and 2400/2500 mm overhangs");
        check(!bus.Key.Equals(old.Key) && !bus.Key.Equals(new BusInterior().Key), Prefix + "separate model/cache key from iEV7 and generic");
        check(bus.AuthoredModel != old.AuthoredModel && old.AuthoredModel == TestPacks.Profile("SWB6710EV01G"), Prefix + "each pack has its own profile and variant");
        check(bus.DoorCount == 2 && Near(bus.Door(0).x, 4.30f) && Near(bus.Door(1).x, -.30f) && Near(bus.Door(0).y, 1.30f) && Near(bus.Door(1).y, 1.30f), Prefix + "independently authored front/middle door openings");
        check(bus.Door(0).x - bus.Door(0).y / 2f > spec.FrontAxleZ && bus.Door(1).x > spec.RearAxleZ, Prefix + "front door ahead of front axle and middle door between axles");
        check(Near(bus.AuthoredModel.WheelRadius, .47825f), Prefix + "nominal 275/70R22.5 wheel radius");
        check(!ReferenceEquals(bus.AuthoredModel.Mesh, old.AuthoredModel.Mesh), Prefix + "independent pack geometry instance");

        var data = KitBuild.BuildAll(bus);
        check(data.Error == null, Prefix + "embedded model builds: " + data.Error);
        if (data.Error != null) return;
        MaterialRoutes(check, data);
        // This validates the exported attachments and motion, not photographic
        // fidelity of their count. The reviewed reference inventory owns that.
        check(data.Parts.Length >= BusInterior.PartCount, Prefix + "retains the fixed 34-part contract");
        int surfaces = bus.AuthoredModel?.WrapSurfaceCount ?? 0, strapFirst = BusInterior.PartCount + (bus.AuthoredModel?.DoorDriveCount ?? 0) + surfaces;
        if (data.Parts.Length >= strapFirst)
            CheckAuthoredStraps(check, data, spec, Prefix, data.Parts.Length - strapFirst, strapFirst);
        WrapSurfaces(check, bus, data, spec);
        AuthoredVisibility(check, data, spec);
        var bounds = KitBounds.Empty;
        foreach (int part in new[] { BusInterior.Shell, BusInterior.Exterior, BusInterior.Glazing, BusInterior.Livery })
        {
            var p = data.Parts[part];
            var mesh = p.Data[0];
            if (mesh == null) continue;
            var frame = new float4x4(p.Rot, p.Pivot);
            foreach (float3 v in mesh.P) bounds.Add(math.transform(frame, v));
        }
        check(!bounds.IsEmpty && Near(bounds.Size.z, 10.5f, .10f) && Near(bounds.Size.x, 2.55f, .10f), Prefix + "body mesh fits declared envelope " + bounds.Size);
        check(bounds.Max.y >= 3.10f && bounds.Max.y <= 3.24f && bounds.Min.y >= -.03f, Prefix + "body height fits 3.2 m envelope");
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
        {
            bool finite = true, valid = true;
            foreach (var part in data.Parts)
            {
                finite &= math.all(math.isfinite(part.Pivot)) && math.all(math.isfinite(part.Rot.value));
                var mesh = part.Data[lod];
                if (mesh == null) continue;
                valid &= mesh.P.Length == mesh.N.Length && mesh.P.Length == mesh.UV.Length && mesh.Idx.Length % 3 == 0;
                foreach (float3 v in mesh.P) finite &= math.all(math.isfinite(v));
                foreach (float3 n in mesh.N) finite &= math.all(math.isfinite(n)) && math.lengthsq(n) > 1e-8f;
                foreach (float2 uv in mesh.UV) finite &= math.all(math.isfinite(uv));
                foreach (int i in mesh.Idx) valid &= i >= 0 && i < mesh.P.Length;
            }
            check(finite && valid && data.Tris[lod] > 0, Prefix + "LOD" + lod + " finite geometry, valid topology and frames");
            bool retained = true;
            int readerTriangles = RuntimeCardReaderTriangles(bus, lod);
            for (int part = 0; part < BusInterior.WrapLeft; part++)
            {
                int expected = bus.AuthoredModel.Mesh.TriangleCount(part, lod);
                if (part == BusInterior.Cab) expected += readerTriangles;
                retained &= data.Parts[part].Tris[lod] == expected;
            }
            check(retained, Prefix + "LOD" + lod + " preserves every validated source triangle plus the shared card-reader geometry");
            foreach (int part in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22 })
                check(HasMesh(data, part, lod), Prefix + "LOD" + lod + " authored part " + part);
            foreach (int part in new[] { BusInterior.DoorRearA, BusInterior.DoorRearB })
                check(!HasMesh(data, part, lod), Prefix + "LOD" + lod + " unused third-door part remains empty");
            for (int drive = 0; drive < 4; drive++)
                check(HasMesh(data, BusInterior.PartCount + drive, lod), Prefix + "LOD" + lod + " door drive " + drive + " (shaft and crank) has geometry");
            for (int face = 0; face < BusInterior.WrapFaceCount; face++)
            {
                var mesh = data.Parts[BusInterior.WrapLeft + face].Data[lod];
                bool uv = mesh != null && mesh.Idx.Length > 0;
                if (mesh != null) foreach (float2 p in mesh.UV) uv &= math.all(p >= 0f) && math.all(p <= 1f);
                check(uv, Prefix + "LOD" + lod + " wrap " + face + " reuses source surface with normalized UVs");
                check(WrapOrientation(data, face, lod), Prefix + "LOD" + lod + " wrap " + face + " has outward normals and expected image direction");
            }
        }
        for (int wheel = 0; wheel < 4; wheel++)
        {
            var pivot = data.Parts[BusInterior.WheelFL + wheel].Pivot;
            float track = wheel < 2 ? 2.117f : 1.876f;
            check(Near(pivot.y, .47825f) && Near(pivot.z, wheel < 2 ? 2.85f : -2.75f) && Near(math.abs(pivot.x), track / 2f), Prefix + "wheel " + wheel + " uses its announced axle variant");
        }
        float frontSpan = WheelSpan(data, BusInterior.WheelFL), rearSpan = WheelSpan(data, BusInterior.WheelRL);
        check(frontSpan > .25f && frontSpan < .40f && rearSpan > frontSpan + .20f, Prefix + "front single and rear dual tyres retain distinct physical widths");
        var shut = Posed(data, spec, 0f);
        var half = Posed(data, spec, .5f);
        var open = Posed(data, spec, 1f);
        // outward-swing doors (the user 2026-10-22: the SWB6710EV01G's doors; Tools/SWB6109EV12G/door_linkage.py): each leaf rides its
        // profile linkage, its pivot on the joint, its door drive turning about the shaft; open, it stands mostly inside the bus at its
        // jamb with its meeting edge a little outside
        var linkages = bus.AuthoredModel.DoorLinkages;
        check(linkages != null && linkages.Length == 2 && linkages[0]?.Length == 2 && linkages[1]?.Length == 2 && bus.AuthoredModel.DoorDriveCount == 4,
            Prefix + "two outward-swing doors of two leaves each, four door drives");
        for (int part = BusInterior.DoorFrontA; part <= BusInterior.DoorMidB; part++)
        {
            var mesh = data.Parts[part].Data[0];
            float3 free = float3.zero;
            if (mesh != null) foreach (float3 p in mesh.P) if (math.lengthsq(p.xz) > math.lengthsq(free.xz)) free = p;
            float3 a = math.transform(shut.PartToModel(part), free), b = math.transform(half.PartToModel(part), free), c = math.transform(open.PartToModel(part), free);
            check(shut.Visible[part] && half.Visible[part] && open.Visible[part] && math.distance(a, b) > .10f && math.distance(b, c) > .10f, Prefix + "door " + part + " moves through intermediate pose");
            if (linkages == null || linkages.Length != 2) continue;
            int d = (part - BusInterior.DoorFrontA) / 2, leaf = (part - BusInterior.DoorFrontA) % 2;
            DoorLinkage l = linkages[d][leaf];
            check(l.Solve(1f, out float shaft, out float2 joint, out _, out _), Prefix + "door " + part + " linkage solves open");
            float3 pivotShut = math.transform(shut.PartToModel(part), float3.zero), pivotOpen = math.transform(open.PartToModel(part), float3.zero);
            check(Near(pivotShut.xz, l.Joint) && Near(pivotOpen.xz, joint), Prefix + "door " + part + " pivot is the linkage joint, shut and open");
            float3 arm = new float3(0f, 0f, 1f);
            check(Near(math.transform(open.PartToModel(l.DrivePart), float3.zero), math.transform(shut.PartToModel(l.DrivePart), float3.zero)) &&
                  Near(math.rotate(open.PartToModel(l.DrivePart), arm), math.rotate(quaternion.RotateY(shaft), arm)), Prefix + "door " + part + " drive turns about its shaft");
            float outside = float.MinValue, reach = float.MaxValue;
            foreach (float3 p in mesh.P) { float3 q = math.transform(open.PartToModel(part), p); outside = math.max(outside, q.x); reach = math.min(reach, q.x); }
            check(outside - spec.HalfWidth > .02f && outside - spec.HalfWidth < .20f && reach < spec.HalfWidth - .40f,
                Prefix + "door " + part + " opens mostly inside, its meeting edge " + (outside - spec.HalfWidth).ToString("0.00") + " m outside, " + (spec.HalfWidth - reach).ToString("0.00") + " m in");
        }
        check(!open.Visible[BusInterior.DoorRearA] && !open.Visible[BusInterior.DoorRearB], Prefix + "unused third door stays hidden");
        var working = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(working, BusInterior.BoardPoseOf(1, true), spec);
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
        {
            check(NearestRayIsFront(data, shut, lod, new float3(0f, .36f, 6f), new float3(0f, .36f, 5f), BusInterior.Exterior),
                Prefix + "LOD" + lod + " front plate is visible ahead of the painted bumper moulding");
            check(NearestRayIsFront(data, working, lod, new float3(2.2f, 1.7f, 12.25f), new float3(.4094521f, 2.6052378f, 1.7086801f)),
                Prefix + "LOD" + lod + " far-side hanging strap stays closed after simplification");
            check(NearestRayIsFront(data, working, lod, new float3(4.475f, .9f, 4.45f), new float3(-1.148896f, 1.293214f, -3.535809f)),
                Prefix + "LOD" + lod + " rear seat leg stays closed after simplification");
            check(NearestRayIsFront(data, working, lod, new float3(4.475f, .9f, 4.45f), new float3(1.1199347f, .3127971f, 2.6551108f)),
                Prefix + "LOD" + lod + " front tyre/rim near edge stays closed while steering");
            check(NearestRayIsFront(data, working, lod, new float3(4.475f, .9f, 4.45f), new float3(1.0832378f, .0542872f, 3.0794091f)),
                Prefix + "LOD" + lod + " front tyre/rim lower edge stays closed while steering");
        }
        foreach (int part in new[] { BusInterior.SteeringWheel, BusInterior.SpeedNeedle, BusInterior.RpmNeedle })
        {
            float3 tip = float3.zero;
            foreach (float3 p in data.Parts[part].Data[0].P)
                if (math.lengthsq(p.xy) > math.lengthsq(tip.xy)) tip = p;
            check(math.distance(math.transform(shut.PartToModel(part), tip), math.transform(working.PartToModel(part), tip)) > .005f &&
                  Near(math.transform(shut.PartToModel(part), float3.zero), math.transform(working.PartToModel(part), float3.zero)),
                Prefix + "instrument " + part + " animates authored geometry around its fixed pivot");
        }
        for (int part = BusInterior.HeadLamps; part <= BusInterior.ReverseLamps; part++)
            check(HasMesh(data, part, 0), Prefix + "independent lamp circuit " + part + " has geometry");
        Fin[] lampFinishes = { Fin.Lamp, Fin.Tail, Fin.Tail, Fin.Beacon, Fin.Beacon, Fin.Lamp };
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            for (int part = BusInterior.HeadLamps; part <= BusInterior.ReverseLamps; part++)
            {
                var mesh = data.Parts[part].Data[lod];
                bool optical = mesh != null && mesh.SubFin.Length > 0;
                if (mesh != null) foreach (Fin f in mesh.SubFin) optical &= f == lampFinishes[part - BusInterior.HeadLamps];
                check(optical, Prefix + "LOD" + lod + " lamp circuit " + part + " contains emitting optics only; fixed housings remain outside switches");
            }
        bool darkHeadHousing = false;
        var fixedExterior = data.Parts[BusInterior.Exterior];
        var fixedFrame = new float4x4(fixedExterior.Rot, fixedExterior.Pivot);
        for (int i = 0; i < fixedExterior.Data[0].P.Length; i++)
        {
            float3 p = math.transform(fixedFrame, fixedExterior.Data[0].P[i]);
            var sw = (Sw)(int)math.floor(fixedExterior.Data[0].UV[i].y * KitPalette.Rows);
            if (math.abs(p.x) > .7f && p.y > .49f && p.y < .8f && p.z > 4.9f &&
                (sw == Sw.Black || sw == Sw.GlassTinted || sw == Sw.DashBlack || sw == Sw.Screen)) darkHeadHousing = true;
        }
        check(darkHeadHousing && shut.Visible[BusInterior.Exterior] && !shut.Visible[BusInterior.HeadLamps], Prefix + "headlamp housing remains drawn with lamps switched off");
        check(Near(BusInterior.Eye(spec), new float3(-.69f, 1.95f, 4.18f)) && Near(bus.WheelHub, new float3(-.69f, 1.48f, 4.45f)), Prefix + "authored driver eye and steering hub");
        int seats = 0, drivers = 0, lowSeats = 0, highSeats = 0;
        bool inside = true;
        foreach (var slot in bus.RiderSlots())
        {
            if (slot.Kind == BusInterior.RiderKind.Seated)
            {
                seats++;
                if (Near(slot.Position.y, .821f)) lowSeats++;
                if (Near(slot.Position.y, 1.5605f)) highSeats++;
            }
            if (slot.Kind == BusInterior.RiderKind.Driver)
            {
                drivers++;
                check(Near(slot.Position, new float3(-.69f, 1.0875f, 3.95f)), Prefix + "driver follows the independent seat cushion");
            }
            inside &= math.all(math.isfinite(slot.Position)) && math.abs(slot.Position.x) < spec.HalfWidth && slot.Position.z > spec.RearZ && slot.Position.z < spec.FrontZ;
        }
        check(seats == 27 && drivers == 1 && inside, Prefix + "independent 27-seat usable design plus driver within body");
        check(lowSeats == 6 && highSeats == 21, Prefix + "six low-floor seats and 21 raised rear-deck seats");
        check(!bus.RiderSlots().Exists(slot => slot.Kind == BusInterior.RiderKind.Seated && Near(slot.Position.z,-1.4f)),
            Prefix + "the unusable high row beside the middle-door stair is absent");
        check(!bus.RiderSlots().Exists(slot => slot.Kind == BusInterior.RiderKind.Holding),
            Prefix + "standing figures do not hold unsupported empty space");
        check(old.RiderSlots().FindAll(s => s.Kind == BusInterior.RiderKind.Seated).Count == 16, Prefix + "iEV7 retains its user-authored 16-seat layout");
        // pack 1.1.2: the barrel section moved the heads inward with the leaning wall (mirror-surfaces.json centre_unity)
        float3[] mirrorCentres = { new float3(-1.3773f, 2.17f, 4.94f), new float3(1.3939f, 2.30f, 5.74f), new float3(1.2439f, 2.23f, 5.32f) };
        float3[] mirrorNormals = { new float3(.37341261f, -.10953438f, -.92117602f), new float3(-.45109388f, -.07343386f, -.88945031f), new float3(-.48709628f, -.43981081f, -.75452220f) };
        float2[] mirrorSizes = { new float2(.192f, .412f), new float2(.205f, .425f), new float2(.17385f, .1188f) };
        for (int index = 0; index < 3; index++)
        {
            var mirror = bus.AuthoredModel.Mirror(index);
            var previous = old.AuthoredModel.Mirror(index);
            check(math.distance(mirror.Centre, previous.Centre) > .5f, Prefix + "mirror " + index + " has independent location");
            check(Near(math.length(mirror.Normal), 1f) && Near(math.length(mirror.Up), 1f) && math.abs(math.dot(mirror.Normal, mirror.Up)) < 1e-4f, Prefix + "mirror " + index + " orthonormal reflector frame");
            check(Near(math.mul(mirror.Frame, new float3(0, 0, -1)), mirror.Normal), Prefix + "mirror " + index + " normal agrees with runtime frame");
            check(Near(mirror.Centre, mirrorCentres[index]) && Near(mirror.Normal, mirrorNormals[index]) && math.distance(mirror.Size, mirrorSizes[index]) < .0001f, Prefix + "mirror " + index + " agrees with source metadata");
        }
        Signs(check, spec);
    }

    static KitPoseBuffer Posed(KitModelData data, BusInteriorSpec spec, float doors)
    {
        var pose = BusInterior.BoardPoseOf(0, true);
        pose.DoorOpen = new float3(doors);
        var result = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(result, pose, spec);
        return result;
    }

    static float WheelSpan(KitModelData data, int part)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        var def = data.Parts[part];
        var frame = new float4x4(def.Rot, def.Pivot);
        foreach (float3 p in def.Data[0].P)
        {
            float x = math.transform(frame, p).x;
            min = math.min(min, x); max = math.max(max, x);
        }
        return max - min;
    }

    static bool WrapOrientation(KitModelData data, int face, int lod)
    {
        var def = data.Parts[BusInterior.WrapLeft + face];
        var mesh = def.Data[lod];
        if (mesh == null || mesh.Idx.Length == 0) return false;
        var frame = new float4x4(def.Rot, def.Pivot);
        float3 outward = face == 0 ? new float3(-1, 0, 0) : face == 1 ? new float3(1, 0, 0) :
            face == 2 ? new float3(0, 0, 1) : face == 3 ? new float3(0, 0, -1) : new float3(0, 1, 0);
        for (int t = 0; t < mesh.Idx.Length; t += 3)
        {
            int a = mesh.Idx[t], b = mesh.Idx[t + 1], c = mesh.Idx[t + 2];
            if (math.dot(math.rotate(def.Rot, mesh.N[a] + mesh.N[b] + mesh.N[c]), outward) <= 0f) return false;
            foreach (int end in new[] { b, c })
            {
                float3 p = math.transform(frame, mesh.P[end] - .008f * mesh.N[end]) - math.transform(frame, mesh.P[a] - .008f * mesh.N[a]);
                float2 uv = mesh.UV[end] - mesh.UV[a];
                float horizontal = face == 2 ? -p.x : face == 3 ? p.x : -p.z;
                float vertical = face == 4 ? p.x : p.y;
                if (uv.x * horizontal < -1e-7f || uv.y * vertical < -1e-7f) return false;
            }
        }
        return true;
    }

    internal static void ShanghaiAquaPalette(Action<bool, string> check)
    {
        check((int)Sw.ShanghaiAqua == 83 && (int)Sw.RearPrintGrey == 80 && (int)Sw.CharcoalVinyl == 82 && (int)Fin.Count == 16,
            Prefix + "aqua appends one swatch without renumbering resources or expanding the 16-bit finish contract");
        check(Near(KitPalette.ToLinear(KitPalette.Srgb(Sw.ShanghaiAqua)), new float3(.12f, .47f, .51f), 1e-6f) &&
              KitPalette.FinOf(Sw.ShanghaiAqua) == Fin.MouldedPlastic &&
              Near(KitPalette.Metallic(KitPalette.FinOf(Sw.ShanghaiAqua)), 0f) &&
              Near(1f - KitPalette.Smoothness(KitPalette.FinOf(Sw.ShanghaiAqua)), .28f) &&
              !KitPalette.IsLight(KitPalette.FinOf(Sw.ShanghaiAqua)),
            Prefix + "aqua retains its exact linear colour and gloss with explicitly documented dielectric metallic approximation");
        var kit = new MeshKit(); kit.Reset(0);
        using (kit.Paint(Sw.ShanghaiAqua)) using (kit.Shade(ShadeRule.Flat))
            kit.QuadP(new float3(0,0,0), new float3(1,0,0), new float3(1,1,0), new float3(0,1,0), new float3(0,0,1));
        KitMeshData mesh = kit.Bake(float4x4.identity);
        bool routed = mesh != null && mesh.SubFin.Length == 1 && mesh.SubFin[0] == Fin.MouldedPlastic &&
                      mesh.FinMask == (1 << (int)Fin.MouldedPlastic);
        if (mesh != null) foreach (float2 uv in mesh.UV)
            routed &= (int)math.floor(uv.y * KitPalette.Rows) == (int)Sw.ShanghaiAqua;
        check(routed, Prefix + "actual MeshKit bake routes aqua to its colour row and existing non-emissive finish");
    }

    static void MaterialRoutes(Action<bool, string> check, KitModelData data)
    {
        VehicleProfile profile = TestPacks.Profile("SWB6109EV12G");
        ShanghaiAquaPalette(check);
        check((int)Fin.Glow == 12 && (int)Fin.BrushedMetal == 13 && (int)Fin.Count == 16 &&
              (int)Sw.Chrome == 20 && (int)Sw.BrushedStainless == (int)Sw.Lip + 1,
            Prefix + "authored finishes append to existing IDs and fit the 16-bit finish mask");
        check(KitPalette.FinOf(Sw.BrushedStainless) == Fin.BrushedMetal &&
              Near(KitPalette.Metallic(Fin.BrushedMetal), 1f) && Near(KitPalette.Smoothness(Fin.BrushedMetal), .75f) &&
              Near(KitPalette.Metallic(KitPalette.FinOf(Sw.HandGripGrey)), 0f) &&
              KitPalette.FinOf(Sw.SeatFabricGrey) == Fin.Textile &&
              KitPalette.FinOf(Sw.SeatPrintYellow) == Fin.Textile && KitPalette.FinOf(Sw.SeatPrintGrey) == Fin.Textile &&
              KitPalette.FinOf(Sw.RearPrintYellow) == Fin.MouldedPlastic && KitPalette.FinOf(Sw.RearPrintGrey) == Fin.MouldedPlastic &&
              Near(KitPalette.Smoothness(Fin.Textile), .32f) && !KitPalette.IsLight(Fin.Textile) &&
              !KitPalette.IsLight(KitPalette.FinOf(Sw.RequestRed)),
            Prefix + "stainless, nonmetal grips, cloth and printed cloth retain their actual finish semantics");
        check(math.distance(KitPalette.ToLinear(KitPalette.Srgb(Sw.SeatFabricGrey)), new float3(.032f, .038f, .041f)) < 1e-5f &&
              math.distance(KitPalette.ToLinear(KitPalette.Srgb(Sw.BrushedStainless)), new float3(.56f, .58f, .59f)) < 1e-5f,
            Prefix + "exported sRGB palette converts back to the authored linear material colour");
        check(KitPalette.FinOf(Sw.CabinGraphite) == Fin.Satin && KitPalette.FinOf(Sw.CharcoalVinyl) == Fin.Matte &&
              math.distance(KitPalette.ToLinear(KitPalette.Srgb(Sw.CabinGraphite)), new float3(.038f,.05f,.054f)) < 1e-5f &&
              math.distance(KitPalette.ToLinear(KitPalette.Srgb(Sw.CharcoalVinyl)), new float3(.12f,.15f,.16f)) < 1e-5f,
            Prefix + "base cabin plastics and vinyl retain explicit cool grey colours rather than brown approximations");
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
        {
            bool present = false, routed = true;
            var authored = new System.Collections.Generic.HashSet<Sw>();
            for (int partIndex = 0; partIndex < data.Parts.Length; partIndex++)
            {
                // Wrap parts and the wrap surfaces (after the door drives) carry picture UVs, not palette row coordinates.
                if (partIndex >= BusInterior.WrapLeft && partIndex < BusInterior.PartCount) continue;
                int firstSurface = BusInterior.PartCount + profile.DoorDriveCount;
                if (partIndex >= firstSurface && partIndex < firstSurface + profile.WrapSurfaceCount) continue;
                var mesh = data.Parts[partIndex].Data[lod];
                if (mesh == null) continue;
                for (int sub = 0; sub < mesh.SubFin.Length; sub++)
                    for (int i = mesh.SubStart[sub]; i < mesh.SubStart[sub] + mesh.SubCount[sub]; i++)
                    {
                        int vertex = mesh.Idx[i];
                        var swatch = (Sw)(int)math.floor(mesh.UV[vertex].y * KitPalette.Rows);
                        if (swatch == Sw.Chrome) { present = true; routed &= mesh.SubFin[sub] == Fin.Chrome; }
                        if (mesh.SubFin[sub] == Fin.Chrome) routed &= swatch == Sw.Chrome;
                        if (swatch >= Sw.BrushedStainless && swatch < Sw.Count)
                        {
                            authored.Add(swatch);
                            routed &= mesh.SubFin[sub] == KitPalette.FinOf(swatch) && !KitPalette.IsLight(mesh.SubFin[sub]);
                            routed &= (mesh.FinMask & (1 << (int)mesh.SubFin[sub])) != 0;
                        }
                    }
            }
            check(present && routed && Near(KitPalette.Metallic(Fin.Chrome), 1f) && !KitPalette.IsLight(Fin.Chrome),
                Prefix + "LOD" + lod + " exported chrome uses the metallic, non-emissive Chrome finish rather than grey plastic");
            check(routed && authored.Contains(Sw.BrushedStainless) && authored.Contains(Sw.HandGripGrey) &&
                  authored.Contains(Sw.SeatFabricGrey) && authored.Contains(Sw.SeatShellGrey) &&
                  authored.Contains(Sw.CabinGraphite) && authored.Contains(Sw.CharcoalVinyl) &&
                  (lod != 0 || authored.Contains(Sw.SeatPrintYellow) && authored.Contains(Sw.SeatPrintGrey) &&
                               authored.Contains(Sw.RearPrintYellow) && authored.Contains(Sw.RearPrintGrey)),
                Prefix + "LOD" + lod + " authored stainless, grips, seat fabric and shell reach their correct runtime submeshes");
            check(authored.Contains(Sw.ShanghaiAqua), Prefix + "LOD" + lod + " embedded resource contains explicit aqua stripes instead of grey fallback");
        }
    }

    static void SmallTriangles(Action<bool, string> check)
    {
        var kit = new MeshKit();
        float3 normal = new float3(0f, 0f, 1f);
        int a = kit.V(float3.zero, normal), b = kit.V(new float3(.00005f, 0f, 0f), normal),
            c = kit.V(new float3(0f, .00005f, 0f), normal);
        kit.Tri(a, b, c, normal);
        check(kit.Triangles == 0, Prefix + "procedural triangle threshold remains unchanged");
        kit.Tri(a, b, c, normal, 1e-18f);
        check(kit.Triangles == 1, Prefix + "healthy small imported triangle survives producer-matched threshold");
        kit.Tri(a, b, b, normal, 1e-18f);
        check(kit.Triangles == 1, Prefix + "truly degenerate imported triangle is still removed");
    }

    static bool NearestRayIsFront(KitModelData data, KitPoseBuffer pose, int lod, float3 eye, float3 target, int requiredPart = -1)
    {
        float3 worldRay = math.normalize(target - eye);
        float nearest = float.PositiveInfinity;
        int nearestPart = -1;
        bool front = false;
        for (int part = 0; part < BusInterior.WrapLeft; part++)
        {
            if (!pose.Visible[part]) continue;
            var mesh = data.Parts[part].Data[lod];
            if (mesh == null) continue;
            float4x4 inv = math.inverse(pose.PartToModel(part));
            float3 origin = math.transform(inv, eye), ray = math.rotate(inv, worldRay);
            for (int t = 0; t < mesh.Idx.Length; t += 3)
            {
                float3 a = mesh.P[mesh.Idx[t]], b = mesh.P[mesh.Idx[t + 1]], c = mesh.P[mesh.Idx[t + 2]];
                float3 e1 = b - a, e2 = c - a, h = math.cross(ray, e2);
                float determinant = math.dot(e1, h);
                if (math.abs(determinant) < 1e-10f) continue;
                float3 s = origin - a;
                float u = math.dot(s, h) / determinant;
                if (u < 0f || u > 1f) continue;
                float3 q = math.cross(s, e1);
                float v = math.dot(ray, q) / determinant;
                if (v < 0f || u + v > 1f) continue;
                float distance = math.dot(e2, q) / determinant;
                if (distance <= 0f || distance > nearest + 1e-6f) continue;
                bool facesEye = determinant > 0f;
                if (distance < nearest - 1e-6f) { nearest = distance; front = facesEye; nearestPart = part; }
                else
                {
                    front |= facesEye;
                    if (facesEye && part == requiredPart) nearestPart = part;
                }
            }
        }
        return math.isfinite(nearest) && front && (requiredPart < 0 || nearestPart == requiredPart);
    }

    static void Signs(Action<bool, string> check, BusInteriorSpec spec)
    {
        BusInterior.FrontSignFrame(spec, out float3 front, out float2 fs);
        BusInterior.SideSignFrame(spec, out float3 side, out float2 ss, out float kerb);
        BusInterior.RearRouteFrame(spec, out float3 rear, out float2 rs);
        float2[] sizes = { fs, ss, rs };
        int2[] matrices = { BusInterior.FrontSignMatrix(spec), BusInterior.SideSignMatrix(spec), BusInterior.RearRouteMatrix(spec) };
        for (int i = 0; i < sizes.Length; i++)
            check(matrices[i].x > 0 && matrices[i].y == 16 && Near(sizes[i].x / matrices[i].x, sizes[i].y / matrices[i].y, 1e-6f), Prefix + "sign " + i + " keeps square LED pitch");
        check(Near(front, new float3(0f, 2.60f, 5.09f)) && math.distance(fs, new float2(1.68f, .24f)) < .0001f, Prefix + "front sign agrees with source metadata");
        // pack 1.1.2: 12 mm inward with the barrel wall, leaning in 1.49 degrees (sign-surfaces.json)
        check(Near(side, new float3(1.2798f, 1.47f, 2.60f)) && math.distance(ss, new float2(.96f, .192f)) < .0001f, Prefix + "side sign agrees with source metadata");
        check(Near(rear, new float3(0f, 2.61f, -5.12f)) && math.distance(rs, new float2(1.54f, .22f)) < .0001f, Prefix + "rear sign agrees with source metadata");
        check(front.z > 4.8f && rear.z < -5f && side.x > 1.20f && kerb > 0f && side.y > 1.4f && side.y < 1.65f, Prefix + "front/rear and low door-side sign use iEV10 positions");
        BusInterior.LedScreenFrame(spec, out float3 next, out float2 ns);
        int2 nm = BusInterior.LedScreenMatrix(spec);
        check(Near(next, new float3(0f, 2.49f, 3.37f)) && Near(ns.x / nm.x, ns.y / nm.y, 1e-6f), Prefix + "interior next-stop display has authored plane and square pixels");
    }
}
