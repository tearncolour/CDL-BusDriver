using System;
using System.IO;
using System.Text.Json;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

static class GenericBusTests
{
    internal static int Candidate(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("generic-candidate <DLL> <report.json>");
        var assembly = System.Reflection.Assembly.LoadFile(Path.GetFullPath(args[1]));
        var rows = new System.Collections.Generic.List<object>();
        foreach (string file in new[] { "CDLCBus12.swb2.gz", "CDLCBus12.swu1.gz", "polymer-Detail.png", "leather-Detail.png", "woven-Detail.png", "brushed-Detail.png", "vehicle.json" })
        {
            string resource = file.EndsWith("-Detail.png") ? "CDLC.BusDriver.Detail." + file.Replace("-Detail.png", ".png") :
                file == "vehicle.json" ? "CDLC.BusDriver.CDLCBus12.vehicle.json" : "CDLC.BusDriver." + file;
            string path = file == "vehicle.json" ? "Tools/CDLCBus12/vehicle.json" : "Asset/CDLCBus12/Runtime/" + file;
            using (Stream stream = assembly.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy); byte[] bytes = copy.ToArray();
                string hash = VehicleProfileJson.Sha256(bytes);
                if (hash != VehicleProfileJson.Sha256(File.ReadAllBytes(path))) throw new InvalidDataException("Stale embedded resource: " + resource);
                rows.Add(new { resource, sha256 = hash, bytes = bytes.Length });
            }
        }
        using (Stream mesh = assembly.GetManifestResourceStream(GenericBusMesh.Resource + ".swb2.gz"))
        using (Stream uv = assembly.GetManifestResourceStream(GenericBusMesh.Resource + ".swu1.gz"))
            GenericBusMesh.Read(mesh, uv, KitBuild.PartTable(new BusInterior(BusInteriorSpec.Default)));
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { pass = true, assembly = Path.GetFullPath(args[1]),
            mvid = assembly.ManifestModule.ModuleVersionId, resources = rows, scope = "Embedded resource hashes and real SWB2/SWU1 reader; no Unity rendering." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Candidate embedded resources and SWB2/SWU1 readback passed: " + rows.Count);
        return 0;
    }

    internal static int Export(string[] args)
    {
        string folder = Path.GetFullPath(args.Length > 1 ? args[1] : "ModelSource/CDLCBus12/rebuild-r1");
        Directory.CreateDirectory(folder);
        var spec = BusInteriorSpec.Default.Snapped();
        var mesh = GenericBusMesh.Generate(spec);
        using (var swb = File.Create(Path.Combine(folder, "CDLCBus12.swb2.gz")))
        using (var uv = File.Create(Path.Combine(folder, "CDLCBus12.swu1.gz"))) mesh.Write(swb, uv, BusInterior.Eye(spec));
        var bus = new BusInterior(spec); var data = KitBuild.BuildAll(bus);
        if (data.Error != null) throw new Exception(data.Error);
        var frames = Array.ConvertAll(data.Parts, p => new { index = p.Index, name = p.Name, parent = p.Parent, flags = (int)p.Flags | (p.Name.StartsWith("DoorDrive") ? 0x10000 : 0),
            pivot = Vec(p.Pivot), rotation = new[] { p.Rot.value.x, p.Rot.value.y, p.Rot.value.z, p.Rot.value.w } });
        File.WriteAllText(Path.Combine(folder, "frames.json"), JsonSerializer.Serialize(frames, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "spec.json"), JsonSerializer.Serialize(spec, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        var detailLods = new object[3];
        for (int lod = 0; lod < 3; lod++)
        {
            var uvParts = new System.Collections.Generic.List<object>();
            foreach (KitPartDef p in data.Parts)
            {
                KitMeshData d = (p.MeshOf >= 0 ? data.Parts[p.MeshOf] : p).Data[lod];
                if (d == null) continue;
                var coords = new float[d.P.Length][];
                for (int t = 0; t < d.Idx.Length; t += 3)
                {
                    int ia = d.Idx[t], ib = d.Idx[t + 1], ic = d.Idx[t + 2];
                    GenericDetailUv.Triangle(p.Index, d.P[ia], d.P[ib], d.P[ic], d.N[ia] + d.N[ib] + d.N[ic], out float2 ua, out float2 ub, out float2 uc);
                    coords[ia] = new[] { ua.x, ua.y }; coords[ib] = new[] { ub.x, ub.y }; coords[ic] = new[] { uc.x, uc.y };
                }
                uvParts.Add(new { index = p.Index, uv = coords });
            }
            detailLods[lod] = uvParts;
        }
        File.WriteAllText(Path.Combine(folder, "detail-uv.json"), JsonSerializer.Serialize(detailLods));
        var mirrors = new object[3];
        for (int i = 0; i < 3; i++)
        {
            MirrorSurface m = BusInterior.GenericMirror(spec, i);
            mirrors[i] = new { centre = Vec(m.Centre), normal = Vec(m.Normal), up = Vec(m.Up), size = new[] { m.Size.x, m.Size.y }, convexDepth = m.ConvexDepth,
                cornerRadius = i < 2 ? (float?).027f : null };
        }
        var windowParts = new System.Collections.Generic.List<int>();
        var windows = bus.WindowPanes(windowParts).ConvertAll(p => Array.ConvertAll(p, Vec));
        File.WriteAllText(Path.Combine(folder, "layout.json"), JsonSerializer.Serialize(new { eye = Vec(BusInterior.Eye(spec)), cameraEye = Vec(BusInterior.CameraEye(spec)), mirrors, windows,
            windowParts, triangles = data.Tris, buildMilliseconds = data.BuildMs, runtimeFormat = "SWB2 + SWU1 palette/wrap UV companion", gameValidated = false }, new JsonSerializerOptions { WriteIndented = true }));
        BusInterior.FrontSignFrame(spec, out float3 fc, out float2 fs);
        BusInterior.SideSignFrame(spec, out float3 sc, out float2 ss, out float kerb);
        BusInterior.RearRouteFrame(spec, out float3 rc, out float2 rs);
        BusInterior.LedScreenFrame(spec, out float3 nc, out float2 ns);
        float3 sn = math.mul(BusInterior.SideSignRotation(spec, kerb), new float3(0, 0, -1));
        GenericDoorRig rig = GenericDoorRig.For(spec);
        var motion = new System.Collections.Generic.List<object>();
        for (int frame = 0; frame <= 100; frame++)
        {
            var buffer = new KitPoseBuffer(data.Parts);
            BusInterior.Apply(buffer, new BusInterior.Pose { Rotation = quaternion.identity, Kneel = quaternion.identity, DoorOpen = new float3(frame / 100f), Outside = true }, spec);
            var moved = new System.Collections.Generic.List<object>();
            for (int part = 0; part < data.Parts.Length; part++)
            {
                if (!(part >= 4 && part <= 9 || part >= rig.FirstDrive)) continue;
                float4x4 m = buffer.PartToModel(part); float4 q = math.rotation(new float3x3(m.c0.xyz,m.c1.xyz,m.c2.xyz)).value;
                moved.Add(new { part, position = Vec(m.c3.xyz), rotation = new[] { q.x, q.y, q.z, q.w } });
            }
            motion.Add(new { frame, parts = moved });
        }
        File.WriteAllText(Path.Combine(folder, "door-motion.json"), JsonSerializer.Serialize(motion));
        var strapMotion = new System.Collections.Generic.List<object>();
        foreach (float opening in new[] { 0f, .5f, 1f })
            for (int sx = -2; sx <= 2; sx++)
                for (int sy = -2; sy <= 2; sy++)
                {
                    float2 swing = new float2(sx,sy) * (BusInterior.StrapSwingLimit*.5f);
                    var buffer = new KitPoseBuffer(data.Parts);
                    BusInterior.Apply(buffer, new BusInterior.Pose { Rotation=quaternion.identity,Kneel=quaternion.identity,DoorOpen=new float3(opening),StrapSwing=swing,Outside=true },spec);
                    var moved = new System.Collections.Generic.List<object>();
                    for (int part = 4; part < data.Parts.Length; part++)
                    {
                        if (part > 9 && part < rig.FirstDrive) continue;
                        float4x4 matrix = buffer.PartToModel(part);float4 q=math.rotation(new float3x3(matrix.c0.xyz,matrix.c1.xyz,matrix.c2.xyz)).value;
                        moved.Add(new { part,position=Vec(matrix.c3.xyz),rotation=new[]{q.x,q.y,q.z,q.w} });
                    }
                    strapMotion.Add(new { frame=200+strapMotion.Count,doorOpen=opening,swing=new[]{swing.x,swing.y},parts=moved });
                }
        File.WriteAllText(Path.Combine(folder,"strap-motion.json"),JsonSerializer.Serialize(strapMotion));
        var doors = new object[spec.Doors];
        for (int i = 0; i < doors.Length; i++)
        {
            float2 d = bus.Door(i);
            var linkage = Array.ConvertAll(rig.Links[i], l => new { shaft = new[] { l.Shaft.x,l.Shaft.y }, joint = new[] { l.Joint.x,l.Joint.y },
                guide = new[] { l.Guide.x,l.Guide.y }, shaftDegrees = math.degrees(l.ShaftRadians), drivePart = l.DrivePart });
            doors[i] = new { centreZ = d.x, width = d.y, leaves = 2, linkage };
        }
        object Sign(float3 c, float2 size, int columns, float tilt = 0) => new { centre = Vec(c), width = size.x, height = size.y, columns, tilt };
        var profile = new {
            format = "cdlb-vehicle", version = 1, model = "CDLCBus12",
            note = "Original built-in generic 12 m bus. Design dimensions; not an exact production-model reproduction. Tool metadata only: built-in runtime remains the generic SWB2 + SWU1 path.",
            spec = new { frontZ=spec.FrontZ,rearZ=spec.RearZ,halfWidth=spec.HalfWidth,roofY=spec.RoofY,floorY=spec.FloorY,frontAxleZ=spec.FrontAxleZ,rearAxleZ=spec.RearAxleZ,doors=spec.Doors,driverSide=spec.DriverSide },
            wheelRadius=BusInterior.WheelRadius,wrap=new { bottom=BusInterior.WrapBottom,thresholds=new[] { .9f,2.4f,5.5f },windows=false,doors=false },bodyColor=new[] { 230,230,226 },doors,
            signs=new { front=Sign(fc,fs,BusInterior.FrontSignMatrix(spec).x),side=Sign(sc,ss,BusInterior.SideSignMatrix(spec).x,math.atan2(sn.y,math.abs(sn.x))),rear=Sign(rc,rs,BusInterior.RearRouteMatrix(spec).x) },
            nextStop=new { centre=Vec(nc),width=ns.x,height=ns.y,columns=BusInterior.LedScreenMatrix(spec).x,rows=BusInterior.LedScreenMatrix(spec).y },
            cardReaders=BusInterior.CardReaders(spec).ConvertAll(r=>new { origin=Vec(r.origin),rotation=new[] {r.frame.value.x,r.frame.value.y,r.frame.value.z,r.frame.value.w},xSign=r.xSign }),mirrors,
            riders=bus.RiderSlots().ConvertAll(r=>new { position=Vec(r.Position),yaw=r.Yaw,kind=r.Kind.ToString() }),
            cab=new { eyeOffset=new[] { 0f,-.06f,0f },seat="air",insulation=1 },cabRender="runtime"
        };
        string profileJson=JsonSerializer.Serialize(profile,new JsonSerializerOptions { WriteIndented=true });
        VehicleProfileJson.Parse(profileJson,folder,"generated CDLCBus12 profile",false);
        File.WriteAllText(Path.Combine(folder,"vehicle.json"),profileJson);
        Console.WriteLine(folder + ": " + string.Join(" / ", data.Tris));
        return 0;
    }

    static float[] Vec(float3 p) => new[] { p.x, p.y, p.z };

    // The own bus is drawn and signed for the one spec its native body, glass and embedded mesh are made from. A spec measured from the prefab's bounds (what a bus of
    // someone else's gets) is another bus: with r27's native bounds it is 0.7 m longer and 10 cm lower, builds a different mesh at run time under glass made for the first,
    // and puts the AI buses' LED signs 0.7 m in front of the body.
    static void OwnBusSpec(Action<bool, string> check)
    {
        BusInteriorSpec own = OwnBus.Spec(false).Snapped(), measured = BusInteriorSpec.Default.FromBounds(new float3(-1.655f, 0f, -6.059f), new float3(1.714f, 3.261f, 6.705f)).Snapped();
        check(OwnBus.Is("CDLCBus12") && OwnBus.Is("cdlcbus12") && !OwnBus.Is("Bus01"), "own bus is told by its prefab's name");
        check(new BusInterior(own, true).Key.Equals(new BusInterior(BusInteriorSpec.Default, true).Key), "own bus takes the default spec: the embedded mesh, not one generated at run time");
        check(!new BusInterior(measured, true).Key.Equals(new BusInterior(BusInteriorSpec.Default, true).Key) && measured.FrontZ - own.FrontZ > .5f && own.RoofY - measured.RoofY > .05f,
            "a spec measured from r27's native bounds is not the default (the former defect): front " + measured.FrontZ + " / roof " + measured.RoofY);
        check(OwnBus.Spec(true).DriverSide > 0f && OwnBus.Spec(false).DriverSide < 0f && OwnBus.Spec(true).Section == BusSection.Solo, "own bus: the driver's side follows the traffic side");

        // the AI buses' signs are put where the profile of the native body has them (Tools/CDLCBus12/vehicle.json, checked on the render of the native prefab)
        var signs = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "Tools", "CDLCBus12", "vehicle.json")).TrimStart('\uFEFF')).RootElement.GetProperty("signs");
        float3 Centre(string name) { var c = signs.GetProperty(name).GetProperty("centre"); return new float3(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle()); }
        BusInterior.FrontSignFrame(own, out float3 front, out _);
        BusInterior.RearRouteFrame(own, out float3 rear, out _);
        bool side = BusInterior.SideSignFrame(own, out float3 sideCentre, out _, out _);
        check(math.distance(front, Centre("front")) < .002f, "AI front sign frame is the profile's: " + front + " vs " + Centre("front"));
        check(math.distance(rear, Centre("rear")) < .002f, "AI rear sign frame is the profile's: " + rear + " vs " + Centre("rear"));
        check(side && math.distance(sideCentre, Centre("side")) < .002f, "AI side sign frame is the profile's: " + sideCentre + " vs " + Centre("side"));
        BusInterior.FrontSignFrame(measured, out float3 wrongFront, out _);
        check(math.distance(wrongFront, Centre("front")) > .5f, "the measured spec put the front sign " + math.distance(wrongFront, Centre("front")).ToString("0.00") + " m off the body (the former defect)");
    }

    // Two triangles, of any parts, that lie in one plane (within half a millimetre), face the same way, overlap in it, differ in colour and are seen from outside are drawn in turn
    // by the depth test: the game flickers (the owner, 8 November: a grey patch and a white edge line on the right rear corner beside the lamp housing). Seen from outside: a ray
    // from the overlap along the normal of the face meets no triangle. The wrap overlays are separate parts laid 1.5 mm over the body by design and the lamp lenses lie 1 mm over
    // their housings; neither is a coincidence. Every LOD of both traffic sides, in model space: the mesh of a part (its own, or that of its MeshOf owner, as the game draws it) at
    // the rest frame of the part, the part of each overlap that lies from 0.1 m behind RearZ to 0.3 m in front of it (the rear corner, its lamps and its panels).
    internal static void RearSkinPlanes(Action<bool, string> check)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var s = BusInteriorSpec.Default; s.DriverSide = side;
            var data = KitBuild.BuildAll(new BusInterior(s));
            if (data.Error != null) { check(false, "rear skin: the generic bus builds: " + data.Error); continue; }
            for (int lod = 0; lod < 3; lod++)
            {
                float zLow = s.RearZ - .1f, zHigh = s.RearZ + .3f;
                var near = new System.Collections.Generic.List<RearTri>();
                var occluders = new System.Collections.Generic.List<float3>();      // three points a triangle, every part but the wraps
                foreach (KitPartDef p in data.Parts)
                {
                    KitMeshData d = p.Data[lod] ?? (p.MeshOf >= 0 ? data.Parts[p.MeshOf].Data[lod] : null);
                    if (d == null || p.Name.StartsWith("Wrap", StringComparison.Ordinal)) continue;
                    float4x4 toModel = math.inverse(p.ModelToPart);
                    for (int sub = 0; sub < d.SubFin.Length; sub++)
                        for (int t = d.SubStart[sub]; t < d.SubStart[sub] + d.SubCount[sub]; t += 3)
                        {
                            float3 a = math.transform(toModel, d.P[d.Idx[t]]), b = math.transform(toModel, d.P[d.Idx[t + 1]]), c = math.transform(toModel, d.P[d.Idx[t + 2]]);
                            float3 n = math.cross(b - a, c - a);
                            float len = math.length(n);
                            if (len < 1e-10f) continue;
                            occluders.Add(a); occluders.Add(b); occluders.Add(c);
                            if (math.max(a.z, math.max(b.z, c.z)) < zLow || math.min(a.z, math.min(b.z, c.z)) > zHigh) continue;
                            near.Add(new RearTri { Part = p.Name, Fin = d.SubFin[sub], UV = d.UV[d.Idx[t]], A = a, B = b, C = c, N = n / len,
                                Lo = math.min(a, math.min(b, c)), Hi = math.max(a, math.max(b, c)) });
                        }
                }

                var kinds = new System.Collections.Generic.SortedDictionary<string, int>(StringComparer.Ordinal);
                string first = null;
                for (int i = 0; i < near.Count; i++)
                    for (int j = i + 1; j < near.Count; j++)
                    {
                        RearTri f = near[i], g = near[j];
                        if (f.Fin == g.Fin && math.all(math.abs(f.UV - g.UV) < .001f)) continue;      // the same finish and palette colour: the order they are drawn in shows nothing
                        if (math.any(f.Lo > g.Hi + .0005f) || math.any(g.Lo > f.Hi + .0005f) || math.dot(f.N, g.N) < .999f) continue;
                        if (math.abs(math.dot(f.N, g.A - f.A)) > .0005f || math.abs(math.dot(f.N, g.B - f.A)) > .0005f || math.abs(math.dot(f.N, g.C - f.A)) > .0005f) continue;
                        if (!OverlapInPlane(f.N, f.A, f.B, f.C, g.A, g.B, g.C)) continue;
                        if (!OverlapCentre(f, g, zLow, zHigh, out float3 centre)) continue;
                        if (Hidden(occluders, centre + f.N * .0005f, f.N)) continue;
                        string kind = string.CompareOrdinal(f.Part, g.Part) <= 0 ? f.Part + " & " + g.Part : g.Part + " & " + f.Part;
                        kind += " " + Direction(f.N) + " [" + SwatchName(f) + " / " + SwatchName(g) + "]";
                        kinds[kind] = (kinds.TryGetValue(kind, out int count) ? count : 0) + 1;
                        if (first == null) first = $"{kind} at ({centre.x:0.000}, {centre.y:0.000}, {centre.z:0.000}): {Describe(f)} / {Describe(g)}";
                    }

                var listed = new System.Text.StringBuilder();
                int shown = 0;
                foreach (var kind in kinds)
                {
                    if (shown++ == 6) { listed.Append(" ..."); break; }
                    listed.Append(listed.Length > 0 ? "; " : "").Append(kind.Key).Append(" x").Append(kind.Value);
                }
                check(kinds.Count == 0, "rear skin: no two faces of different colour lie in one plane and overlap in sight at the back (side " + side + ", LOD" + lod + "): " +
                    (kinds.Count == 0 ? near.Count + " triangles examined" : listed + "; first " + first));
            }
        }
    }

    // The frame rings of the far LODs. RoundRect with no segments in a corner put the one point of the corner at the start of its arc, on the edge of the rectangle, so the ring of
    // LOD1 and LOD2 was a skewed quadrilateral: its bottom bar rose by the corner radius at one end of every window and left a slit above the black sill strip (the owner, 8
    // November, the rear side window at LOD2). The ring of the corner-less LOD is the rectangle itself.
    internal static void FrameRings(Action<bool, string> check)
    {
        float3 o = new float3(1f, 2f, 3f), u = new float3(0f, 0f, 1f), v = new float3(0f, 1f, 0f);
        float2 h = new float2(0.4f, 0.25f);
        foreach (int n in new[] { 0, 1, 3 })
        {
            float3[] ring = BusInterior.RoundRect(o, u, v, h, 0.07f, n);
            bool inside = true;
            foreach (float3 p in ring)
                inside &= math.abs(math.dot(p - o, u)) <= h.x + 1e-5f && math.abs(math.dot(p - o, v)) <= h.y + 1e-5f;
            check(ring.Length == 4 * (n + 1) && inside, "frame ring with " + n + " segments a corner: " + ring.Length + " points, all inside the rectangle");
        }

        float3[] plain = BusInterior.RoundRect(o, u, v, h, 0.07f, 0);
        var corners = new[] { new float2(h.x, h.y), new float2(-h.x, h.y), new float2(-h.x, -h.y), new float2(h.x, -h.y) };
        bool atCorners = true, alongAxes = true;
        for (int i = 0; i < 4; i++)
        {
            atCorners &= math.all(math.abs(new float2(math.dot(plain[i] - o, u), math.dot(plain[i] - o, v)) - corners[i]) < 1e-5f);
            float3 edge = plain[(i + 1) % 4] - plain[i];
            alongAxes &= math.abs(math.dot(edge, u)) < 1e-5f || math.abs(math.dot(edge, v)) < 1e-5f;
        }

        check(atCorners && alongAxes, "frame ring of the corner-less LOD is the rectangle: the four corners in order, every edge along an axis");
    }

    // The windscreen's corners seen from the front: through the gap between the front skin's window opening and the raked A-pillar a light Panel face of the side wall (the end of its
    // first window pillar) showed as a light strip that widens upwards (the owner, 8 November, a screenshot of the game, both sides alike). A Panel face looking forward in the corner
    // zone (the A-pillar's 27 cm; the front door's jamb behind it is an interior seen through the glass, as designed) must be hidden from every camera position in front of the bus: rays to 18 of them (3 heights, 6 lateral offsets, 7 m ahead). The glass is not in this mesh: what shows
    // through it counts. Both traffic sides, all three LODs, the rest pose.
    internal static void WindscreenCornerFaces(Action<bool, string> check)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            var s = BusInteriorSpec.Default; s.DriverSide = side;
            var data = KitBuild.BuildAll(new BusInterior(s));
            if (data.Error != null) { check(false, "windscreen corner: the generic bus builds: " + data.Error); continue; }
            var eyes = new System.Collections.Generic.List<float3>();
            foreach (float x in new[] { -6f, -3.5f, -1.5f, 1.5f, 3.5f, 6f })
                foreach (float y in new[] { 1.0f, 1.8f, 2.6f })
                    eyes.Add(new float3(x, y, s.FrontZ + 7f));
            for (int lod = 0; lod < 3; lod++)
            {
                var occluders = new System.Collections.Generic.List<float3>();
                var candidates = new System.Collections.Generic.List<(float3 centre, float3 normal, string text)>();
                foreach (KitPartDef p in data.Parts)
                {
                    KitMeshData d = p.Data[lod] ?? (p.MeshOf >= 0 ? data.Parts[p.MeshOf].Data[lod] : null);
                    if (d == null || p.Name.StartsWith("Wrap", StringComparison.Ordinal)) continue;
                    float4x4 toModel = math.inverse(p.ModelToPart);
                    for (int sub = 0; sub < d.SubFin.Length; sub++)
                        for (int t = d.SubStart[sub]; t < d.SubStart[sub] + d.SubCount[sub]; t += 3)
                        {
                            float3 a = math.transform(toModel, d.P[d.Idx[t]]), b = math.transform(toModel, d.P[d.Idx[t + 1]]), c = math.transform(toModel, d.P[d.Idx[t + 2]]);
                            if (math.min(a.z, math.min(b.z, c.z)) < s.FrontZ - 1.2f) continue;      // behind the front 1.2 m nothing lies between a corner face and an eye ahead
                            float3 n = math.cross(b - a, c - a);
                            float len = math.length(n);
                            if (len < 1e-10f) continue;
                            occluders.Add(a); occluders.Add(b); occluders.Add(c);
                            float3 centre = (a + b + c) / 3f, unit = n / len;
                            int row = (int)math.floor(d.UV[d.Idx[t]].y * KitPalette.Height / KitPalette.RowHeight);
                            if (row == (int)Sw.Panel && unit.z > .5f && math.abs(centre.x) > .95f && centre.y > 1.0f && centre.y < 2.6f && centre.z > s.FrontZ - .27f && centre.z < s.FrontZ - .02f)
                                candidates.Add((centre, unit, $"{p.Name} ({centre.x:0.000}, {centre.y:0.000}, {centre.z:0.000})"));
                        }
                }

                string first = null;
                int exposed = 0;
                foreach (var face in candidates)
                {
                    float3 from = face.centre + face.normal * .0005f;
                    bool seen = false;
                    foreach (float3 eye in eyes)
                        if (!Hidden(occluders, from, math.normalize(eye - from), math.distance(eye, from))) { seen = true; break; }
                    if (!seen) continue;
                    exposed++;
                    if (first == null) first = face.text;
                }

                check(exposed == 0, "windscreen corner: no light Panel face in the corners is seen from the front (side " + side + ", LOD" + lod + "): " +
                    (exposed == 0 ? candidates.Count + " faces examined" : exposed + " of " + candidates.Count + " exposed, first " + first));
            }
        }
    }

    static string Direction(float3 n)
    {
        float3 a = math.abs(n);
        return a.x >= a.y && a.x >= a.z ? (n.x > 0f ? "+x" : "-x") : a.y >= a.z ? (n.y > 0f ? "+y" : "-y") : (n.z > 0f ? "+z" : "-z");
    }

    // The palette swatch the first vertex of a triangle lies in (the palette bands are RowHeight texels high), or its raw UV where the part is not drawn from the palette
    static string SwatchName(RearTri tri)
    {
        int row = (int)math.floor(tri.UV.y * KitPalette.Height / KitPalette.RowHeight);
        return tri.UV.y >= 0f && tri.UV.y <= 1f && row >= 0 && row < (int)Sw.Count ? ((Sw)row).ToString() + "/" + tri.Fin : "uv(" + tri.UV.x.ToString("0.00") + "," + tri.UV.y.ToString("0.00") + ")/" + tri.Fin;
    }

    static string Describe(RearTri r) => $"{r.Part} ({r.A.x:0.000},{r.A.y:0.000},{r.A.z:0.000}) ({r.B.x:0.000},{r.B.y:0.000},{r.B.z:0.000}) ({r.C.x:0.000},{r.C.y:0.000},{r.C.z:0.000})";

    sealed class RearTri { public string Part; public Fin Fin; public float2 UV; public float3 A, B, C, N, Lo, Hi; }

    // Whether two coplanar triangles overlap by more than a tenth of a millimetre: no edge of either separates them (triangles that only share an edge do not overlap)
    static bool OverlapInPlane(float3 n, float3 a0, float3 a1, float3 a2, float3 b0, float3 b1, float3 b2)
    {
        float3[] a = { a0, a1, a2 }, b = { b0, b1, b2 };
        for (int which = 0; which < 2; which++)
        {
            float3[] poly = which == 0 ? a : b;
            for (int e = 0; e < 3; e++)
            {
                float3 axis = math.cross(n, poly[(e + 1) % 3] - poly[e]);
                float length = math.length(axis);
                if (length < 1e-12f) continue;
                axis /= length;
                float loA = float.MaxValue, hiA = float.MinValue, loB = float.MaxValue, hiB = float.MinValue;
                for (int i = 0; i < 3; i++)
                {
                    float x = math.dot(a[i], axis); loA = math.min(loA, x); hiA = math.max(hiA, x);
                    x = math.dot(b[i], axis); loB = math.min(loB, x); hiB = math.max(hiB, x);
                }
                if (hiA <= loB + 1e-4f || hiB <= loA + 1e-4f) return false;
            }
        }
        return true;
    }

    // The middle of the overlap of two coplanar triangles between the planes z = zLow and z = zHigh: f clipped by the three edges of g and by the two planes (a convex polygon), the mean
    // of its vertices; false when nothing of the overlap lies in between
    static bool OverlapCentre(RearTri f, RearTri g, float zLow, float zHigh, out float3 centre)
    {
        var poly = new System.Collections.Generic.List<float3> { f.A, f.B, f.C };
        for (int k = 0; k < 3; k++)
        {
            float3 p0 = k == 0 ? g.A : k == 1 ? g.B : g.C, along = (k == 0 ? g.B : k == 1 ? g.C : g.A) - p0;
            poly = Clip(poly, p0, math.cross(g.N, along));
        }

        poly = Clip(poly, new float3(0f, 0f, zLow), new float3(0f, 0f, 1f));
        poly = Clip(poly, new float3(0f, 0f, zHigh), new float3(0f, 0f, -1f));
        centre = float3.zero;
        if (poly.Count < 3) return false;
        foreach (float3 point in poly) centre += point;
        centre /= poly.Count;
        return true;
    }

    // The part of a convex polygon on the side of the plane through origin that inward points to
    static System.Collections.Generic.List<float3> Clip(System.Collections.Generic.List<float3> poly, float3 origin, float3 inward)
    {
        var clipped = new System.Collections.Generic.List<float3>();
        for (int i = 0; i < poly.Count; i++)
        {
            float3 current = poly[i], previous = poly[(i + poly.Count - 1) % poly.Count];
            float dc = math.dot(current - origin, inward), dp = math.dot(previous - origin, inward);
            if (dc >= 0f != dp >= 0f) clipped.Add(previous + (current - previous) * (dp / (dp - dc)));
            if (dc >= 0f) clipped.Add(current);
        }

        return clipped;
    }

    // Whether a ray from origin along direction meets any of the triangles (Moller-Trumbore, both faces: the depth buffer takes either)
    static bool Hidden(System.Collections.Generic.List<float3> triangles, float3 origin, float3 direction, float maxDistance = float.MaxValue)
    {
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            float3 a = triangles[i], e1 = triangles[i + 1] - a, e2 = triangles[i + 2] - a, p = math.cross(direction, e2);
            float det = math.dot(e1, p);
            if (math.abs(det) < 1e-12f) continue;
            float3 to = origin - a;
            float u = math.dot(to, p) / det;
            if (u < 0f || u > 1f) continue;
            float3 q = math.cross(to, e1);
            float v = math.dot(direction, q) / det;
            if (v < 0f || u + v > 1f) continue;
            float distance = math.dot(e2, q) / det;
            if (distance > 1e-6f && distance < maxDistance) return true;
        }

        return false;
    }

    internal static void Run(Action<bool, string> check)
    {
        OwnBusSpec(check);
        check(GenericBusMesh.FrameComponentMatches(1f, math.asfloat(math.asint(1f) + 1), 0), "generic frame accepts one float ULP");
        check(GenericBusMesh.FrameComponentMatches(-1f, math.asfloat(math.asint(-1f) + 1), 3), "generic frame accepts negative quaternion rounding");
        check(GenericBusMesh.FrameComponentMatches(.174000114f, .174000025f, 2), "generic frame accepts recorded Mono door linkage subtraction rounding");
        check(!GenericBusMesh.FrameComponentMatches(1f, math.asfloat(math.asint(1f) + 20), 0), "generic frame rejects position beyond two micrometres");
        check(!GenericBusMesh.FrameComponentMatches(1f, math.asfloat(math.asint(1f) + 5), 3), "generic frame retains quaternion component bound");
        check(!GenericBusMesh.FrameComponentMatches(64f, math.asfloat(math.asint(64f) + 1), 0), "generic frame retains absolute position bound");
        check(!GenericBusMesh.FrameComponentMatches(1f, 1.001f, 0), "generic frame rejects displaced pivot");
        check(!GenericBusMesh.FrameComponentMatches(float.NaN, 0f, 0) &&
            !GenericBusMesh.FrameComponentMatches(float.PositiveInfinity, float.PositiveInfinity, 3), "generic frame rejects nonfinite components");
        foreach (float side in new[] { -1f, 1f })
        {
            var s = BusInteriorSpec.Default; s.DriverSide = side;
            var bus = new BusInterior(s); var data = KitBuild.BuildAll(bus);
            check(data.Error == null, "generic SWB2 builds both traffic sides: " + data.Error);
            if (data.Error != null) continue;
            // far LODs: lighter at each step, within the budget this build was made for (r27 had 30,656 and 23,436), and no part loses all its geometry (the in-game vehicle probe requires it of the straps too)
            check(data.Tris[1] < data.Tris[0] && data.Tris[2] < data.Tris[1], "generic LOD triangles fall with distance: " + string.Join(" / ", data.Tris));
            check(data.Tris[0] <= 80000 && data.Tris[1] <= 30000 && data.Tris[2] <= 22000, "generic triangle budget LOD0 <= 80,000 (the spec's), LOD1 <= 30,000, LOD2 <= 22,000: " + string.Join(" / ", data.Tris));
            bool partsKeepGeometry = true;
            foreach (KitPartDef part in data.Parts)
                for (int lod = 0; lod < 3; lod++)
                    partsKeepGeometry &= part.Tris[lod] > 0;
            check(partsKeepGeometry, "generic every part has geometry at every LOD");
            float3 belt = new float3(s.HalfWidth, s.FloorY + 1.02f, 0);
            check(BusBarrel.Point(s, belt).x > BusBarrel.Point(s, new float3(s.HalfWidth, s.FloorY, 0)).x + .03f &&
                BusBarrel.Point(s, belt).x > BusBarrel.Point(s, new float3(s.HalfWidth, s.RoofY, 0)).x + .10f, "barrel belt wider than skirt and shoulder");
            for (int i = 0; i < 3; i++)
            {
                MirrorSurface m = BusInterior.GenericMirror(s, i);
                float3 incoming = math.normalize(m.Centre - BusInterior.CameraEye(s));
                float3 reflected = incoming - 2f * math.dot(incoming, m.Normal) * m.Normal;
                check(reflected.z < -.4f && math.dot(BusInterior.Eye(s) - m.Centre, m.Normal) > 0,
                    "generic mirror " + i + " faces eye and reflects towards side/rear target");
            }
            for (int lod = 0; lod < 3; lod++)
            {
                bool valid = true, uvValid = true;
                foreach (var p in data.Parts)
                {
                    KitMeshData d = p.Data[lod]; if (d == null) continue;
                    for (int t = 0; t < d.Idx.Length; t += 3)
                    {
                        int a = d.Idx[t], b = d.Idx[t + 1], c = d.Idx[t + 2];
                        float3 cross = math.cross(d.P[b] - d.P[a], d.P[c] - d.P[a]);
                        valid &= math.all(math.isfinite(cross)) && math.lengthsq(cross) >= 1e-18f;
                        GenericDetailUv.Triangle(p.Index, d.P[a], d.P[b], d.P[c], d.N[a] + d.N[b] + d.N[c], out float2 ua, out float2 ub, out float2 uc);
                        GenericDetailUv.Basis(d.P[a], d.P[b], d.P[c], ua, ub, uc, out float3 u, out float3 v);
                        uvValid &= math.all(math.isfinite(u)) && math.all(math.isfinite(v)) && math.lengthsq(math.cross(u, v)) > 1e-14f;
                    }
                }
                check(valid, "generic finite nondegenerate exported triangles LOD" + lod);
                check(uvValid, "generic detail UV tangent basis LOD" + lod);
            }
        }
    }
}
