using System;
using System.IO;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// The K18 pack's wrap surfaces (SWB1 frame bit 0x20000) in both sections, from the generated pack in the repository (skipped, said so, when
// the pack is not there). Pack-described only: BusDriver finds the surfaces from the mesh and the profile's wrap block (windows, doors).
static class K18WrapTests
{
    const string Prefix = "CRRC_K18 wrap surfaces: ";

    internal static void Run(Action<bool, string> check)
    {
        string dir = AppContext.BaseDirectory, path = null;
        for (int i = 0; i < 8 && dir != null && path == null; i++, dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "Packs", "CDLC.Bus.CRRC_K18", "CRRC_K18.cdlb.json");
            if (File.Exists(candidate)) path = candidate;
        }
        if (path == null)
        {
            Console.WriteLine("  (no K18 pack in the repository: the K18 wrap surface checks are skipped)");
            return;
        }
        VehicleProfile profile = VehicleProfileJson.Load(path);
        VehicleAssembly assembly = profile.Assembly;
        check(assembly != null, Prefix + "the pack is a two-section assembly");
        if (assembly == null) return;
        VehicleModels.RegisterAssembly(assembly);
        // front: windows + 2 doors x 2 leaves; rear: windows + 1 door x 2 leaves
        foreach ((VehicleSectionBinding binding, int surfaces, int leaves) in new[] { (assembly.Front, 5, 4), (assembly.Rear, 3, 2) })
            Section(check, binding, surfaces, leaves);
        Paint(check, assembly);
    }

    // The demonstrator's fixed paints (red, yellow, blue) are livery for the wrap but keep their colours: the Livery part is drawn in the
    // line colour with only the white left in it, the rest with the exterior paint. The wrap reaches every face: the front below and
    // around the windscreen, the rear below its window (K18 1.3.1: the ends' paint in the Livery part).
    static void Paint(Action<bool, string> check, VehicleAssembly assembly)
    {
        float white = KitPalette.RowV(Sw.White), whiteGloss = KitPalette.RowV(Sw.WhiteGloss);
        var sections = new System.Collections.Generic.List<BusInterior>();
        foreach (VehicleSectionBinding binding in new[] { assembly.Front, assembly.Rear })
        {
            string name = Prefix + binding.Id + ": ";
            var bus = new BusInterior(binding.Profile.Spec);
            sections.Add(bus);
            KitModelData data = KitBuild.BuildAll(bus);
            if (data.Error != null) continue;
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            {
                KitMeshData livery = data.Parts[BusInterior.Livery].Data[lod], exterior = data.Parts[BusInterior.Exterior].Data[lod];
                // the white is the plain White row or the glossy one (a glossy livery uses only the second)
                bool onlyWhite = livery != null && livery.UV.Length > 0 &&
                    (Array.TrueForAll(livery.UV, uv => math.abs(uv.y - white) < 1e-5f) || Array.TrueForAll(livery.UV, uv => math.abs(uv.y - whiteGloss) < 1e-5f));
                check(onlyWhite, name + "the line-colour part holds only the livery white at LOD" + lod);
                if (lod == 0) check(binding.Profile.Mesh.LiveryGloss, name + "the livery is the glossy white (K18 1.3.4)");
                bool red = false, yellow = false;
                foreach (float2 uv in exterior.UV)
                {
                    red |= math.abs(uv.y - KitPalette.RowV(Sw.Red)) < 1e-5f || math.abs(uv.y - KitPalette.RowV(Sw.RedGloss)) < 1e-5f;
                    yellow |= math.abs(uv.y - KitPalette.RowV(Sw.Yellow)) < 1e-5f || math.abs(uv.y - KitPalette.RowV(Sw.YellowGloss)) < 1e-5f;
                }
                check(red && yellow, name + "the demonstrator's red and yellow keep their colours at LOD" + lod);
            }
            BusInterior.WrapFace end = binding == assembly.Front ? BusInterior.WrapFace.Front : BusInterior.WrapFace.Rear;
            KitMeshData face = data.Parts[BusInterior.WrapLeft + (int)end].Data[0];
            float low = float.PositiveInfinity;
            if (face != null) foreach (float3 p in face.P) low = math.min(low, p.y);
            check(low < 1.0f, name + "the " + end + " wrap reaches down to the bumper (lowest " + low.ToString("0.00") + " m)");
        }
        var input = new WrapTemplate.Input { English = LangPack.Text("en-US"), Chinese = LangPack.Text("zh-HANS"), Model = assembly.Front.Profile.Name, Layout = assembly.Front.Profile.WrapLayout, OutlineMm = assembly.Front.Profile.WrapOutlineMm,
            Profile = assembly.Front.Profile, Sections = sections };
        check(WrapTemplate.Build(input, out WrapPackage.Manifest manifest, out var entries), Prefix + "the template builds");
        if (manifest == null) return;
        check(entries.Exists(e => e.Key == "front.png") && entries.Exists(e => e.Key == "rear.png"), Prefix + "the template has a front and a rear picture");
        check(manifest.Info.Contains("\"doorLeaves\": [2, 2, 2]") && manifest.Info.Contains("\"sectionMetres\": ["), Prefix + "the template lists the three doors' leaves and the sections' lengths");
        foreach (BusInterior.WrapFace f in new[] { BusInterior.WrapFace.Left, BusInterior.WrapFace.Right, BusInterior.WrapFace.Roof, BusInterior.WrapFace.Rear })
        {
            byte[] px = WrapTemplate.Guide(input, f, out int w, out int h, out float2 _);
            int joint = 0;
            for (int i = 0; px != null && i < w * h; i++) if (px[4 * i] == 255 && px[4 * i + 1] == 40 && px[4 * i + 2] == 160) joint++;
            check(f == BusInterior.WrapFace.Rear ? joint == 0 : joint > h, Prefix + "the " + f + " guide " + (f == BusInterior.WrapFace.Rear ? "has no joint mark" : "marks the joint") + " (" + joint + " px)");
        }
    }

    static void Section(Action<bool, string> check, VehicleSectionBinding binding, int surfaces, int leaves)
    {
        VehicleProfile model = binding.Profile;
        string name = Prefix + binding.Id + ": ";
        // layout 2: the side glazing rebuilt from the photographs (Tools/CRRC_K18/build_profile.py WRAP_LAYOUT); layout 1 was the 1.23 m bays
        // hinged leaves (no drives) or outward-swing leaves with one door drive each, which the surfaces follow
        int drives = model.DoorDriveCount;
        check(model.WrapWindows && model.WrapDoors && model.WrapSurfaceCount == surfaces && (drives == 0 || drives == leaves) && model.WrapLayout == 2 && model.WrapOutlineMm == 10f,
            name + "the profile asks for window and door surfaces (" + surfaces + " parts) after " + drives + " door drives, layout 2, a 10 mm outline");
        if (model.WrapSurfaceCount != surfaces) return;
        var bus = new BusInterior(model.Spec);
        KitModelData data = KitBuild.BuildAll(bus);
        check(data.Error == null, name + "builds: " + data.Error);
        if (data.Error != null) return;
        int first = BusInterior.PartCount + drives;
        check(data.Parts[first].Name == "WrapSurface0" && data.Parts[first].Parent == BusInterior.Shell, name + "the windows' skin hangs from the shell");
        for (int leaf = 0; leaf < leaves; leaf++)
        {
            int part = first + 1 + leaf, parent = BusInterior.DoorFrontA + leaf;
            check(data.Parts[part].Parent == parent && data.Parts[part].Name == "WrapSurface" + (1 + leaf), name + "door sheet " + leaf + " hangs from its leaf " + parent);
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
                check(data.Parts[part].Data[lod]?.Idx.Length > 0, name + "door sheet " + leaf + " has LOD" + lod + " geometry");
        }
        for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            check(data.Parts[first].Data[lod] == null || data.Parts[first].Data[lod].P.Length == 0, name + "the windows' skin is drawn by the wrap faces at LOD" + lod);
        // the straps follow the surfaces and are what they were
        int strapFirst = first + surfaces;
        check(data.Parts.Length > strapFirst && data.Parts[strapFirst].Name == "Strap0" && data.Parts[strapFirst].Parent == BusInterior.Shell, name + "Strap0 is part " + strapFirst);
        // the sheets ride on their leaves, turning about the leaf's hinge
        var shut = new KitPoseBuffer(data.Parts);
        var open = new KitPoseBuffer(data.Parts);
        var pose = BusInterior.BoardPoseOf(0, true);
        BusInterior.Apply(shut, pose, model.Spec);
        pose.DoorOpen = new float3(1f);
        BusInterior.Apply(open, pose, model.Spec);
        for (int leaf = 0; leaf < leaves; leaf++)
        {
            int part = first + 1 + leaf, leafPart = BusInterior.DoorFrontA + leaf;
            float3[] points = data.Parts[part].Data[0].P;
            float3 far = points[0];
            foreach (float3 p in points) if (math.length(p) > math.length(far)) far = p;
            float moved = math.distance(math.transform(shut.PartToModel(part), far), math.transform(open.PartToModel(part), far));
            check(moved > .3f, name + "door sheet " + leaf + " moves with its leaf (" + moved.ToString("0.00") + " m)");
            float3 hingeShut = math.transform(shut.PartToModel(leafPart), float3.zero), hingeOpen = math.transform(open.PartToModel(leafPart), float3.zero);
            check(math.distance(math.transform(shut.PartToModel(part), float3.zero), hingeShut) < .001f && math.distance(math.transform(open.PartToModel(part), float3.zero), hingeOpen) < .001f,
                name + "door sheet " + leaf + " turns about its leaf's hinge");
        }
        // nothing shows without a wrap; with the side pictures the sheets show
        var none = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(none, BusInterior.BoardPoseOf(0, true), model.Spec);
        var wrapped = new KitPoseBuffer(data.Parts);
        var withWrap = BusInterior.BoardPoseOf(0, true);
        withWrap.Wrap = BusInterior.WrapFaces.Left | BusInterior.WrapFaces.Right;
        BusInterior.Apply(wrapped, withWrap, model.Spec);
        bool hidden = true, shown = true;
        for (int leaf = 0; leaf < leaves; leaf++) { hidden &= !none.Visible[first + 1 + leaf]; shown &= wrapped.Visible[first + 1 + leaf]; }
        check(hidden, name + "no door sheet shows without a wrap");
        check(shown, name + "the door sheets show with the side pictures");
        // the picture areas: the kerb side has the sheets (2 triangles each) and the windows, the other side windows only
        float kerb = -model.Spec.DriverSide;
        var areas = model.Mesh.WrapAreas(kerb > 0 ? BusInterior.WrapFace.Right : BusInterior.WrapFace.Left);
        int windows = 0, doors = 0;
        foreach ((byte kind, float2x3 uv) in areas) { if (kind == 1) windows++; else if (kind == 2) doors++; }
        check(doors == 2 * leaves && windows >= 8, name + "the kerb side's wrap areas: " + windows + " window triangles, " + doors + " door triangles (" + leaves + " sheets x 2)");
        var other = model.Mesh.WrapAreas(kerb > 0 ? BusInterior.WrapFace.Left : BusInterior.WrapFace.Right);
        check(other.Count >= 8 && !other.Exists(a => a.kind == 2), name + "the other side has windows but no door sheets");
        bool inside = true;
        foreach ((byte kind, float2x3 uv) in areas)
            foreach (float2 p in new[] { uv.c0, uv.c1, uv.c2 }) inside &= p.x >= 0f && p.x <= 1f && p.y >= 0f && p.y <= 1f;
        check(inside, name + "the areas' picture coordinates lie in the picture");
    }
}
