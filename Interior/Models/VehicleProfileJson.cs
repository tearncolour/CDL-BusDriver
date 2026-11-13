using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// Reads a vehicle profile (format "cdlb-vehicle", versions 1..4): a vehicle pack's &lt;model&gt;.cdlb.json, or the build tools'
    /// Tools/&lt;model&gt;/vehicle.json (the same file before the pack step adds the mesh fields; its "pack" block is for the
    /// packer and ignored here). Everything is checked before a profile is made: a bad file is rejected with the reason, never
    /// half used. The mesh, a file next to the profile, is only opened when the model is first built (and its SHA-256 checked).
    /// </summary>
    internal static class VehicleProfileJson
    {
        internal const string Format = "cdlb-vehicle";
        // 4: doors[].linkage[].kind (the parallel four-bar). An older Bus Driver refuses a version above its own, so it never runs such a pack as a roller or hinged door.
        internal const int Version = 4;

        /// <summary>A profile version this Bus Driver reads (1 .. <see cref="Version"/>): the reader and the packer ask the same.</summary>
        internal static bool Supported(int version) => version >= 1 && version <= Version;

        /// <summary>A profile of several sections: version 2 always, version 3 and later when it has "sections".</summary>
        internal static bool IsAssembly(int version, bool hasSections) => version == 2 || version >= 3 && hasSections;
        private const int MaxDoors = 4, MaxReaders = 8, MaxMirrors = 8, MaxRiders = 256;
        // Core takes 16 cameras, the driver's seat first: a solo bus 15 besides it, an articulated one 15 over its two sections
        internal const int MaxCameras = 15, MaxCameraName = 40;

        internal sealed class Error : Exception
        {
            internal Error(string message) : base(message) { }
        }

        /// <summary>The profile in a file. requireMesh: a pack's profile must name its mesh (a tool's vehicle.json need not).</summary>
        internal static VehicleProfile Load(string path, bool requireMesh = true)
        {
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception e) { throw new Error("cannot read " + path + ": " + e.Message); }
            return Parse(text, Path.GetDirectoryName(Path.GetFullPath(path)), path, requireMesh);
        }

        internal static VehicleProfile LoadForPrefab(string path, string prefab)
        {
            VehicleProfile first = Load(path);
            VehicleProfile selected = first.Assembly == null ? first : first.Assembly.ForPrefab(prefab);
            if (selected == null || !string.Equals(selected.Name, prefab, StringComparison.Ordinal))
                throw new Error("profile does not explicitly bind prefab " + prefab);
            return selected;
        }

        internal static void ValidateAssemblyEntrances(VehicleAssembly assembly, string directory)
        {
            foreach (VehicleSectionBinding section in new[] { assembly.Front, assembly.Rear })
            {
                string path = Path.Combine(directory, section.Prefab+".cdlb.json");
                string text;
                try { text = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception e) { throw new Error("cannot read assembly entrance "+path+": "+e.Message); }
                if (Sha256(Encoding.UTF8.GetBytes(text)) != assembly.Identity)
                    throw new Error("assembly entrances do not contain the same complete manifest: "+path);
            }
        }

        /// <summary>The profile in a text; its mesh file (if it names one) in <paramref name="directory"/>.</summary>
        internal static VehicleProfile Parse(string text, string directory, string source, bool requireMesh = true)
        {
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(text) as Dictionary<string, object>; }
            catch (MiniJson.FormatError e) { throw new Error("not valid JSON: " + e.Message); }
            if (root == null) throw new Error("not a JSON object");

            if (Str(root, "format") != Format) throw new Error("not a CDLC vehicle profile (format)");
            int version = Int(root, "version", 1, 1000);
            if (!Supported(version)) throw new Error($"profile version {version} needs a newer Bus Driver (this one reads {Version})");
            if (IsAssembly(version, root.ContainsKey("sections")))
                return ParseAssembly(root, directory, source, requireMesh, Sha256(Encoding.UTF8.GetBytes(text)));
            return ParseSection(root, directory, source, requireMesh, BusSection.Solo, version);
        }

        internal const string OverlayFormat = "cdlb-signs";
        internal const int OverlayVersion = 1;

        /// <summary>A sign overlay (SignOverlay): its "signs" as a profile's, each with an optional "backing" board.</summary>
        internal static SignOverlay ParseOverlay(string text, string source)
        {
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(text) as Dictionary<string, object>; }
            catch (MiniJson.FormatError e) { throw new Error("not valid JSON: " + e.Message); }
            if (root == null) throw new Error("not a JSON object");
            if (Str(root, "format") != OverlayFormat) throw new Error("not a CDLC sign overlay (format)");
            int version = Int(root, "version", 1, 1000);
            if (version > OverlayVersion) throw new Error($"overlay version {version} needs a newer Bus Driver (this one reads {OverlayVersion})");
            string name = Str(root, "model");
            if (!ModelName(name)) throw new Error("bad model name: " + name);

            var signs = Obj(root, "signs");
            VehicleProfile.Sign front = SignOf(signs, "front"), side = SignOf(signs, "side"), rear = SignOf(signs, "rear");
            if (front.Matrix.x == 0 && side.Matrix.x == 0 && rear.Matrix.x == 0) throw new Error("signs: no sign");
            if (side.Matrix.x > 0 && math.abs(side.Centre.x) < .5f) throw new Error("signs.side: the centre must be on a side (|x| >= 0.5)");
            return new SignOverlay(name, source, front, side, rear,
                BackingOf(signs, "front", front), BackingOf(signs, "side", side), BackingOf(signs, "rear", rear));
        }

        private static SignOverlay.Backing BackingOf(Dictionary<string, object> signs, string key, in VehicleProfile.Sign sign)
        {
            if (sign.Matrix.x == 0) return default;
            var o = (Dictionary<string, object>)signs[key];
            if (!o.TryGetValue("backing", out object v) || v == null) return default;
            var b = v as Dictionary<string, object> ?? throw new Error("signs." + key + ".backing is not an object");
            var backing = new SignOverlay.Backing(Vec3(b, "colour", 0f, 255f), new float2(Num(b, "width", .1f, 3.2f), Num(b, "height", .05f, 1.2f)));
            if (backing.Size.x < sign.Size.x || backing.Size.y < sign.Size.y) throw new Error("signs." + key + ".backing is smaller than its sign");
            return backing;
        }

        private static VehicleProfile ParseSection(Dictionary<string, object> root, string directory, string source,
            bool requireMesh, BusSection section, int version = 1)
        {
            bool sidedDoors = version >= 3;      // doors.side and a fourth door; version 4 adds doors[].linkage[].kind
            string name = Str(root, "model");
            if (!ModelName(name)) throw new Error("bad model name: " + name);

            if (root.TryGetValue("paletteRows", out object rows) && rows != null)
            {
                int n = Int(root, "paletteRows", 1, 255);
                if (n > (int)Sw.Count) throw new Error($"the mesh uses {n} palette rows; this Bus Driver has {(int)Sw.Count}: it needs a newer Bus Driver");
            }

            // the mesh: a plain file name next to the profile
            Func<Stream> openMesh = null;
            string meshFile = null;
            string sha = null;
            string meshName = root.TryGetValue("mesh", out object m) && m != null ? Str(root, "mesh") : null;
            if (meshName == null)
            {
                if (requireMesh) throw new Error("no runtime mesh named (mesh)");
            }
            else
            {
                if (meshName.Length == 0 || meshName.Length > 128 || meshName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || meshName.Contains("..") ||
                    meshName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new Error("the mesh must be a plain file name next to the profile: " + meshName);
                sha = root.TryGetValue("meshSha256", out object h) && h != null ? Str(root, "meshSha256").ToLowerInvariant() : null;
                if (sha != null && (sha.Length != 64 || !IsHex(sha))) throw new Error("bad meshSha256");
                if (requireMesh && sha == null) throw new Error("no meshSha256");
                string meshPath = Path.Combine(directory ?? ".", meshName);
                meshFile = meshPath;
                if (requireMesh && !File.Exists(meshPath)) throw new Error("the runtime mesh is missing: " + meshName);
                string expected = sha;
                openMesh = () =>
                {
                    byte[] bytes = File.ReadAllBytes(meshPath);
                    if (expected != null)
                    {
                        string actual = Sha256(bytes);
                        if (actual != expected) throw new InvalidDataException($"{meshName}: SHA-256 {actual} is not the profile's {expected}");
                    }
                    return new MemoryStream(bytes, false);
                };
            }

            // the body
            var s = Obj(root, "spec");
            var spec = new BusInteriorSpec
            {
                FrontZ = Num(s, "frontZ", section == BusSection.Solo ? 1f : -30f, section == BusSection.Solo ? 15f : 30f),
                RearZ = Num(s, "rearZ", section == BusSection.Solo ? -15f : -30f, section == BusSection.Solo ? -1f : 30f),
                HalfWidth = Num(s, "halfWidth", .8f, 1.6f), RoofY = Num(s, "roofY", 2f, 4.5f), FloorY = Num(s, "floorY", .1f, 1.5f),
                FrontAxleZ = Num(s, "frontAxleZ", -15f, 15f), RearAxleZ = Num(s, "rearAxleZ", -15f, 15f),
                Doors = Int(s, "doors", section == BusSection.Solo ? 1 : 0, MaxDoors), DriverSide = Num(s, "driverSide", -1f, 1f),
                Section = section, ModelVariant = BusModelVariant.Generic,
            };
            if (spec.FrontZ-spec.RearZ < 1f) throw new Error("spec: body must have positive length >= 1 m");
            if (spec.DriverSide != -1f && spec.DriverSide != 1f) throw new Error("spec.driverSide must be -1 or 1");
            if (section == BusSection.Solo && !(spec.RearZ < spec.RearAxleZ && spec.RearAxleZ < spec.FrontAxleZ && spec.FrontAxleZ < spec.FrontZ))
                throw new Error("spec: the axles must lie between the ends, the front one ahead");
            if (spec.FloorY >= spec.RoofY) throw new Error("spec: the floor is above the roof");

            float wheelRadius = Num(root, "wheelRadius", .2f, .8f);
            var wrap = Obj(root, "wrap");
            float wrapBottom = Num(wrap, "bottom", 0f, 2f);
            float3 wrapThresholds = Vec3(wrap, "thresholds", 0f, 20f);
            bool wrapWindows = Bool(wrap, "windows"), wrapDoors = Bool(wrap, "doors");
            int wrapLayout = OptInt(wrap, "layout", 0, 0, 100000);
            float wrapOutline = Opt(wrap, "outlineMm", VehicleProfile.DefaultWrapOutlineMm, 0f, 60f);
            VehicleProfile.WrapPillar[] wrapPillars = WrapPillars(wrap);
            float3 bodyColor = Vec3(root, "bodyColor", 0f, 255f);

            var doorList = Arr(root, "doors", spec.Doors, spec.Doors);
            if (spec.Doors > 3 && !sidedDoors) throw new Error("four doors require profile version 3");
            var doors = new float2[doorList.Count];
            var leaves = new byte[doorList.Count];
            var openDegrees = new float[doorList.Count];
            var doorSides = new float[doorList.Count];
            bool explicitDoorSides = false;
            var timings = new VehicleProfile.DoorTiming[doorList.Count];
            DoorLinkage[][] linkages = null;
            var drives = new HashSet<int>();
            bool nativeThresholds = section != BusSection.Solo || doorList.Count > 3;
            for (int i = 0; i < doorList.Count; i++)
                nativeThresholds |= Item(doorList, i, "doors").ContainsKey("boardingPosition") ||
                    Item(doorList, i, "doors").ContainsKey("side");
            var nativeDoors = nativeThresholds ? new VehicleProfile.NativeDoor[doorList.Count] : null;
            for (int i = 0; i < doorList.Count; i++)
            {
                var d = Item(doorList, i, "doors");
                doors[i] = new float2(Num(d, "centreZ", spec.RearZ, spec.FrontZ), Num(d, "width", .4f, 2.5f));
                leaves[i] = (byte)Int(d, "leaves", 1, 2);
                doorSides[i] = -spec.DriverSide;
                if (d.TryGetValue("side", out object sideValue))
                {
                    if (!sidedDoors) throw new Error("doors.side requires profile version 3");
                    explicitDoorSides = true;
                    doorSides[i] = sideValue is string sideName && sideName == "left" ? -1f :
                        sideValue is string rightName && rightName == "right" ? 1f : throw new Error("doors.side must be left or right");
                }
                openDegrees[i] = Opt(d, "openDegrees", BusInterior.DoorOpenDeg, 5f, 175f);
                timings[i] = new VehicleProfile.DoorTiming(Opt(d, "openSeconds", BusInterior.DoorOpenSeconds, .3f, 10f),
                    Opt(d, "closeSeconds", BusInterior.DoorCloseSeconds, .3f, 10f), Opt(d, "closeDelay", 0f, 0f, 10f));
                if (d.TryGetValue("linkage", out object lv) && lv != null)
                {
                    var leafList = Arr(d, "linkage", leaves[i], leaves[i]);
                    linkages = linkages ?? new DoorLinkage[doorList.Count][];
                    linkages[i] = new DoorLinkage[leafList.Count];
                    for (int leaf = 0; leaf < leafList.Count; leaf++)
                    {
                        var l = Item(leafList, leaf, "doors[" + i + "].linkage");
                        string at = $"doors[{i}].linkage[{leaf}]";
                        DoorLinkageKind kind = LinkageKind(l, at, version);
                        int part = Int(l, "drivePart", BusInterior.PartCount, 255);
                        if (!drives.Add(part)) throw new Error($"doors[{i}].linkage: drive part {part} is used twice");
                        DoorLinkage linkage;
                        if (kind == DoorLinkageKind.ParallelFourBar)
                        {
                            // the roller belongs to the other kind: a field of it here is a mistake, not something to ignore
                            if (Present(l, "guide")) throw new Error($"{at}: a parallel_four_bar has no guide");
                            int balancePart = Int(l, "balanceDrivePart", BusInterior.PartCount, 255);
                            if (!drives.Add(balancePart)) throw new Error($"doors[{i}].linkage: drive part {balancePart} is used twice");
                            linkage = DoorLinkage.ParallelFourBar(Vec2(l, "shaft", -30f, 30f), Vec2(l, "joint", -30f, 30f),
                                Vec2(l, "balanceShaft", -30f, 30f), Vec2(l, "balanceJoint", -30f, 30f), Num(l, "shaftDegrees", -170f, 170f), part, balancePart);
                        }
                        else
                        {
                            foreach (string key in new[] { "balanceShaft", "balanceJoint", "balanceDrivePart" })
                                if (Present(l, key)) throw new Error($"{at}: {key} belongs to the parallel_four_bar kind");
                            linkage = new DoorLinkage(Vec2(l, "shaft", -30f, 30f), Vec2(l, "joint", -30f, 30f), Vec2(l, "guide", -30f, 30f),
                                Num(l, "shaftDegrees", -170f, 170f), part);
                        }

                        string why = linkage.Check();
                        if (why != null) throw new Error($"{at}: {why}");
                        linkages[i][leaf] = linkage;
                    }
                }
                if (nativeDoors != null)
                {
                    float3 position = Vec3(d, "boardingPosition", -30f, 30f);
                    float4 q = Vec4(d, "boardingRotation", -1.01f, 1.01f);
                    if (math.abs(math.lengthsq(q) - 1f) > .00001f) throw new Error("door boardingRotation must be unit");
                    quaternion rotation = new quaternion(q);
                    if (position.z < spec.RearZ || position.z > spec.FrontZ || math.abs(position.z - doors[i].x) > doors[i].y * .5f ||
                        position.y < spec.FloorY - .25f || position.y > spec.RoofY ||
                        math.abs(position.x) < spec.HalfWidth - .35f || math.abs(position.x) > spec.HalfWidth + .3f ||
                        position.x * doorSides[i] <= 0)
                        throw new Error("door boardingPosition must lie on its declared doorway threshold");
                    if (math.dot(math.forward(rotation), new float3(-doorSides[i], 0, 0)) < .5f)
                        throw new Error("door boardingRotation must face into the vehicle");
                    if (!(d.TryGetValue("boarding", out object boards) && boards is bool canBoard) ||
                        !(d.TryGetValue("alighting", out object alights) && alights is bool canAlight) || !(canBoard || canAlight))
                        throw new Error("each v2 door needs explicit boarding/alighting booleans with at least one allowed");
                    nativeDoors[i] = new VehicleProfile.NativeDoor(position, rotation, canBoard, canAlight);
                }
            }

            int firstDrive = BusInterior.PartCount + (leaves.Length > 3 ? leaves[3] : 0);
            for (int part = firstDrive; part < firstDrive + drives.Count; part++)
                if (!drives.Contains(part)) throw new Error($"the door drive parts must be {firstDrive}..{firstDrive + drives.Count - 1}, one per drive arm (a parallel_four_bar leaf has two)");

            var signs = Obj(root, "signs");
            VehicleProfile.Sign front = SignOf(signs, "front"), side = SignOf(signs, "side"), rear = SignOf(signs, "rear");

            var ns = Obj(root, "nextStop");
            bool nextStopEnabled = true;
            if (ns.TryGetValue("enabled", out object ne))
            {
                if (!(ne is bool)) throw new Error("nextStop.enabled must be boolean");
                nextStopEnabled = (bool)ne;
            }
            float3 nextStopCentre = Vec3(ns, "centre", -30f, 30f);
            float nsWidth = Opt(ns, "width", 0f, 0f, 3f), nsHeight = Opt(ns, "height", 0f, 0f, 1f);
            int nsColumns = OptInt(ns, "columns", 128, 8, 512), nsRows = OptInt(ns, "rows", 16, 8, 64);

            var readerList = Arr(root, "cardReaders", 0, MaxReaders);
            var readers = new (float3, quaternion, float)[readerList.Count];
            for (int i = 0; i < readerList.Count; i++)
            {
                var r = Item(readerList, i, "cardReaders");
                float4 q = Vec4(r, "rotation", -1.01f, 1.01f);
                if (math.abs(math.lengthsq(q) - 1f) > .01f) throw new Error($"cardReaders[{i}].rotation is not a unit quaternion");
                float xSign = Num(r, "xSign", -1f, 1f);
                if (xSign != -1f && xSign != 1f) throw new Error($"cardReaders[{i}].xSign must be -1 or 1");
                readers[i] = (Vec3(r, "origin", -30f, 30f), new quaternion(q), xSign);
            }

            var mirrorList = Arr(root, "mirrors", 0, MaxMirrors);
            var mirrors = new MirrorSurface[mirrorList.Count];
            for (int i = 0; i < mirrorList.Count; i++)
            {
                var mm = Item(mirrorList, i, "mirrors");
                float3 normal = Vec3(mm, "normal", -1.01f, 1.01f), up = Vec3(mm, "up", -1.01f, 1.01f);
                if (math.lengthsq(normal) < .5f || math.lengthsq(up) < .5f) throw new Error($"mirrors[{i}]: normal/up not unit vectors");
                float2 size = Vec2(mm, "size", .02f, 1.5f);
                float convexDepth = Opt(mm, "convexDepth", 0f, 0f, .1f);
                // optional: a flat glass with rounded corners; without it the runtime draws its default rounded, tapered outline
                float2[] outline = null;
                if (mm.TryGetValue("cornerRadius", out object cr) && cr != null)
                {
                    if (convexDepth > 0f) throw new Error($"mirrors[{i}].cornerRadius: only for flat mirrors (no convexDepth)");
                    outline = MirrorSurface.RoundedRect(size, Num(mm, "cornerRadius", 0f, .45f * math.cmin(size)));
                }
                mirrors[i] = new MirrorSurface(Vec3(mm, "centre", -30f, 30f), normal, up, size, convexDepth, outline: outline);
            }

            var riderList = Arr(root, "riders", 0, MaxRiders);
            var riders = new BusInterior.RiderSlot[riderList.Count];
            for (int i = 0; i < riderList.Count; i++)
            {
                var r = Item(riderList, i, "riders");
                if (!Enum.TryParse(Str(r, "kind"), false, out BusInterior.RiderKind kind) || !Enum.IsDefined(typeof(BusInterior.RiderKind), kind))
                    throw new Error($"riders[{i}].kind: unknown kind");
                riders[i] = new BusInterior.RiderSlot { Position = Vec3(r, "position", -30f, 30f), Yaw = Opt(r, "yaw", 0f, -7f, 7f), Kind = kind };
            }

            // The driver's cab (optional): a correction of the mesh's eye, the look-down, the seat, the saloon's insulation.
            VehicleProfile.CabData cab = VehicleProfile.CabData.None;
            if (root.TryGetValue("cab", out object cabValue) && cabValue != null)
            {
                if (!(cabValue is Dictionary<string, object> c)) throw new Error("cab must be an object");
                float3 eyeOffset = c.TryGetValue("eyeOffset", out object eo) && eo != null ? Vec3(c, "eyeOffset", -.5f, .5f) : float3.zero;
                float seat = float.NaN;
                if (c.TryGetValue("seat", out object sv) && sv != null)
                {
                    string seatName = sv as string;
                    seat = seatName == "air" ? 1f : seatName == "fixed" ? 0f : throw new Error("cab.seat must be \"air\" or \"fixed\"");
                }
                DriverFeetData? driverFeet = null;
                if (c.TryGetValue("driverFeet", out object feetValue) && feetValue != null)
                {
                    if (!(feetValue is Dictionary<string, object> feet)) throw new Error("cab.driverFeet must be an object");
                    DriverFootFrame ReadFoot(string side)
                    {
                        if (!feet.TryGetValue(side, out object value) || !(value is Dictionary<string, object> foot))
                            throw new Error("cab.driverFeet requires left and right frames");
                        float4 q = Vec4(foot, "rotation", -1.01f, 1.01f);
                        if (math.abs(math.lengthsq(q) - 1f) > .00001f) throw new Error("driver foot rotation must be unit");
                        float3 pole = foot.ContainsKey("kneePole") ? Vec3(foot, "kneePole", -1f, 1f) : new float3(0f, 0f, 1f);
                        if (math.lengthsq(pole) < 1e-8f) throw new Error("driver foot kneePole must be nonzero");
                        return new DriverFootFrame(Vec3(foot, "origin", -30f, 30f), new quaternion(q), math.normalize(pole));
                    }
                    driverFeet = new DriverFeetData(ReadFoot("left"), ReadFoot("right"));
                }
                cab = new VehicleProfile.CabData(eyeOffset, Opt(c, "lookDownDeg", float.NaN, -10f, 30f), seat, Opt(c, "insulation", float.NaN, 0f, 1f), driverFeet);
            }

            // How the driver's view draws the cab (optional "cabRender"): the runtime mesh (default), or the pack's own game asset.
            bool nativeCab = false;
            if (root.TryGetValue("cabRender", out object renderValue) && renderValue != null)
            {
                string render = renderValue as string ?? throw new Error("cabRender must be text");
                nativeCab = render == "native" ? true : render == "runtime" ? false : throw new Error("cabRender must be \"native\" or \"runtime\"");
            }

            VehicleProfile.CameraData[] cameras = Cameras(root);

            var profile = new VehicleProfile(name, spec, wheelRadius, wrapBottom, wrapThresholds, bodyColor, doors, leaves, front, side, rear,
                nextStopCentre, readers, riders, mirrors, openMesh, sha, source, nsWidth, nsHeight, nsColumns, nsRows)
                { MeshPath = meshFile, NextStopEnabled = nextStopEnabled, DoorOpenDegrees = openDegrees, DoorSides = explicitDoorSides ? doorSides : null, DoorTimings = timings,
                  DoorLinkages = linkages, DoorDriveCount = drives.Count, WrapWindows = wrapWindows, WrapDoors = wrapDoors, WrapLayout = wrapLayout,
                  WrapOutlineMm = wrapOutline, WrapPillars = wrapPillars, Cab = cab, Cameras = cameras, NativeCab = nativeCab,
                  ExtraSideSigns = ExtraSideSigns(signs) };
            if (section != BusSection.Solo)
            {
                if (Str(s, "section") != section.ToString()) throw new Error("spec.section disagrees with assembly binding");
                var axles = Arr(root, "axles", 1, 2); var parsed = new VehicleProfile.Axle[axles.Count]; var used = new HashSet<int>();
                for (int i = 0; i < axles.Count; i++)
                {
                    var a = Item(axles, i, "axles"); float z = Num(a, "z", spec.RearZ, spec.FrontZ);
                    var parts = Floats(a, "parts", 2, 19, 22);
                    int left = (int)parts[0], right = (int)parts[1];
                    if (parts[0] != left || parts[1] != right || !((left == 19 && right == 20) || (left == 21 && right == 22)) ||
                        !used.Add(left) || !used.Add(right)) throw new Error("axles.parts must be unique fixed [19,20] or [21,22] pairs");
                    if (!a.TryGetValue("steer", out object sv) || !(sv is bool steer)) throw new Error("axles.steer must be boolean");
                    if (steer != (left == 19)) throw new Error("fixed SWB1 contract steers only parts 19/20");
                    if (section == BusSection.Rear && left == 19) throw new Error("rear section supports only its nonsteering axle in parts 21/22");
                    if (i > 0 && z >= parsed[i-1].Z) throw new Error("axles must be ordered front to rear");
                    parsed[i] = new VehicleProfile.Axle(z, steer, left, right);
                }
                if (math.abs(spec.FrontAxleZ-parsed[0].Z) > .0001f || math.abs(spec.RearAxleZ-parsed[parsed.Length-1].Z) > .0001f)
                    throw new Error("spec axle endpoints disagree with explicit axles (a one-axle section repeats that axle)");
                profile.Axles = parsed;
            }
            profile.NativeDoors = nativeDoors;
            return profile;
        }

        private static VehicleProfile ParseAssembly(Dictionary<string, object> root, string directory, string source,
            bool requireMesh, string identity)
        {
            string model = Str(root, "model");
            if (!ModelName(model)) throw new Error("bad assembly model name");
            var a = Obj(root, "articulation"); string frontId = Str(a, "frontSection"), rearId = Str(a, "rearSection");
            if (frontId == rearId) throw new Error("articulation needs two distinct sections");
            VehicleSectionBinding front = null, rear = null;
            var list = Arr(root, "sections", 2, 2); var prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object entry in list)
            {
                var d = entry as Dictionary<string, object> ?? throw new Error("section is not an object");
                string id = Str(d, "id"), prefab = Str(d, "prefab");
                if (!ModelName(id) || !ModelName(prefab) || !prefabs.Add(prefab)) throw new Error("invalid or repeated section id/prefab");
                BusSection kind = id == frontId ? BusSection.Front : id == rearId ? BusSection.Rear : throw new Error("unbound assembly section");
                var p = ParseSection(Obj(d, "profile"), directory, source, requireMesh, kind, Int(root, "version", 2, Version));
                if (p.Name != prefab) throw new Error("section profile.model must equal its exact prefab name");
                float4 q = Vec4(d, "restRotation", -1.01f, 1.01f);
                if (math.abs(math.lengthsq(q)-1f) > .00001f) throw new Error("section restRotation is not unit");
                var binding = new VehicleSectionBinding(id, prefab, Vec3(d, "restPosition", -40f, 40f), new quaternion(q), p);
                if (kind == BusSection.Front) { if (front != null) throw new Error("duplicate front section"); front = binding; }
                else { if (rear != null) throw new Error("duplicate rear section"); rear = binding; }
            }
            if (front == null || rear == null) throw new Error("missing front or rear section");
            if (front.Profile.Cameras.Length + rear.Profile.Cameras.Length > MaxCameras)
                throw new Error($"cameras: {front.Profile.Cameras.Length + rear.Profile.Cameras.Length} over the two sections (allowed {MaxCameras} besides the driver's seat)");
            float3 fa = Vec3(a, "frontAnchor", -30f, 30f), ra = Vec3(a, "rearAnchor", -30f, 30f);
            if (math.distance(front.RestPosition+math.mul(front.RestRotation,fa), rear.RestPosition+math.mul(rear.RestRotation,ra)) > .001f)
                throw new Error("assembly rest transforms do not join the source anchors within 1 mm");
            float gap = front.Profile.Spec.RearZ-fa.z + ra.z-rear.Profile.Spec.FrontZ;
            if (gap <= 0f || gap > 4f || fa.z > front.Profile.Spec.RearZ || ra.z < rear.Profile.Spec.FrontZ)
                throw new Error("articulation anchors must be beyond their open body ends with a positive gangway");
            var joint = Obj(a, "joint");
            if (Str(joint, "format") != VehicleArticulation.Format) throw new Error("unsupported articulation joint format");
            string file = Str(joint,"mesh"), hash = Str(joint,"meshSha256").ToLowerInvariant();
            if (file.Length == 0 || file.Length > 128 || file.Contains("..") || file.IndexOfAny(new[] {'/', '\\', ':'}) >= 0 ||
                file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new Error("joint mesh must be a plain file name");
            if (hash.Length != 64 || !IsHex(hash)) throw new Error("bad joint meshSha256");
            string path = Path.Combine(directory ?? ".",file);
            if (requireMesh && !File.Exists(path)) throw new Error("joint mesh is missing: "+file);
            var articulation = new VehicleArticulation(path,hash,Num(joint,"maxBendDegrees",5f,89f));
            var assembly = new VehicleAssembly(model, identity, front, rear, fa, ra, Num(a, "maxSeparation", .001f, .25f),articulation);
            front.Profile.SetArticulation(fa.z, gap); rear.Profile.SetArticulation(ra.z, 0f);
            return assembly.Front.Profile;
        }

        // The kind of a leaf's linkage: none written is the roller in a rail (every version); a kind needs version 4, and an unknown one is refused.
        private static DoorLinkageKind LinkageKind(Dictionary<string, object> linkage, string at, int version)
        {
            if (!Present(linkage, "kind")) return DoorLinkageKind.GuideRail;
            if (version < 4) throw new Error($"{at}.kind needs profile version 4");
            switch (linkage["kind"] as string)
            {
                case "guide_rail": return DoorLinkageKind.GuideRail;
                case "parallel_four_bar": return DoorLinkageKind.ParallelFourBar;
                default: throw new Error($"{at}.kind: unknown linkage kind (guide_rail or parallel_four_bar)");
            }
        }

        private static bool Present(Dictionary<string, object> o, string key) => o.TryGetValue(key, out object value) && value != null;

        internal static bool ModelName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
            foreach (char c in name)
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_')) return false;
            return true;
        }

        internal static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var b = new StringBuilder(64);
                foreach (byte x in hash) b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }

        private static VehicleProfile.Sign[] ExtraSideSigns(Dictionary<string, object> signs)
        {
            if (!signs.ContainsKey("extraSides")) return Array.Empty<VehicleProfile.Sign>();
            var entries = Arr(signs, "extraSides", 0, 3);
            var result = new VehicleProfile.Sign[entries.Count];
            for (int i = 0; i < result.Length; i++)
            {
                var entry = Item(entries, i, "signs.extraSides");
                result[i] = SignOf(new Dictionary<string, object> { ["extraSides[" + i + "]"] = entry }, "extraSides[" + i + "]");
                if (math.abs(result[i].Centre.x) < .01f)
                    throw new Error("signs.extraSides must have an unambiguous vehicle side (nonzero centre.x)");
            }
            return result;
        }

        private static VehicleProfile.Sign SignOf(Dictionary<string, object> signs, string key)
        {
            if (!signs.TryGetValue(key, out object v) || v == null) return default;     // no such sign (Matrix 0)
            var o = v as Dictionary<string, object> ?? throw new Error("signs." + key + " is not an object");
            // style: "led" (the dot matrix, the default) or "card" (a printed board; ink and board colours optional, sRGB 0..255).
            // A Bus Driver before 0.12.5 ignores these keys and draws the dot matrix, so columns stay required.
            VehicleProfile.SignStyle style = VehicleProfile.SignStyle.Led;
            if (o.TryGetValue("style", out object sv) && sv != null)
            {
                string name = sv as string;
                style = name == "led" ? VehicleProfile.SignStyle.Led : name == "card" ? VehicleProfile.SignStyle.Card :
                    throw new Error("signs." + key + ".style must be \"led\" or \"card\"");
            }

            bool hasInk = o.ContainsKey("ink"), hasBoard = o.ContainsKey("board");
            if ((hasInk || hasBoard) && style != VehicleProfile.SignStyle.Card)
                throw new Error("signs." + key + ": ink and board are a card's colours (style \"card\")");
            float3 ink = hasInk ? Vec3(o, "ink", 0f, 255f) : new float3(200f, 30f, 38f);
            float3 board = hasBoard ? Vec3(o, "board", 0f, 255f) : new float3(242f, 240f, 233f);
            return new VehicleProfile.Sign(Vec3(o, "centre", -30f, 30f), Num(o, "width", .1f, 3f), Int(o, "columns", 8, 512),
                Opt(o, "height", 0f, 0f, 1f), Opt(o, "tilt", 0f, -.8f, .8f), style, ink, board, style == VehicleProfile.SignStyle.Card);
        }

        /// <summary>
        /// The optional camera positions besides the driver's seat (Core API 14). A Bus Driver before 0.12.10 ignores the key, so a pack
        /// with cameras still loads there (with the driver's seat only).
        /// </summary>
        private const int MaxWrapPillars = 16;

        // wrap.pillars: [{ "name": "A", "min": [x, y, z], "max": [x, y, z], "faces": ["front", "left", "right"] }]
        private static VehicleProfile.WrapPillar[] WrapPillars(Dictionary<string, object> wrap)
        {
            if (!wrap.TryGetValue("pillars", out object value) || value == null)
                return new VehicleProfile.WrapPillar[0];
            var list = Arr(wrap, "pillars", 0, MaxWrapPillars);
            var pillars = new VehicleProfile.WrapPillar[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var p = Item(list, i, "wrap.pillars");
                string name = Str(p, "name");
                if (name.Length == 0 || name.Length > 16 || !name.All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9'))
                    throw new Error($"wrap.pillars[{i}].name must be 1..16 letters or digits");
                float3 min = Vec3(p, "min", -30f, 30f), max = Vec3(p, "max", -30f, 30f);
                if (!math.all(min < max)) throw new Error($"wrap.pillars[{i}]: min must be below max on every axis");
                var faceList = Arr(p, "faces", 1, BusInterior.WrapFaceCount);
                var faces = new BusInterior.WrapFace[faceList.Count];
                for (int f = 0; f < faces.Length; f++)
                {
                    int index = Array.IndexOf(WrapFaceNames, faceList[f] as string);
                    faces[f] = index >= 0 ? (BusInterior.WrapFace)index : throw new Error($"wrap.pillars[{i}].faces[{f}] must be one of {string.Join(", ", WrapFaceNames)}");
                }
                pillars[i] = new VehicleProfile.WrapPillar { Name = name, Min = min, Max = max, Faces = faces.Distinct().ToArray() };
            }
            return pillars;
        }

        // BusInterior.WrapFace by index
        private static readonly string[] WrapFaceNames = { "left", "right", "front", "rear", "roof" };

        private static VehicleProfile.CameraData[] Cameras(Dictionary<string, object> root)
        {
            if (!root.TryGetValue("cameras", out object value) || value == null)
                return Array.Empty<VehicleProfile.CameraData>();
            var list = Arr(root, "cameras", 0, MaxCameras);
            var cameras = new VehicleProfile.CameraData[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var c = Item(list, i, "cameras");
                string name = c.TryGetValue("name", out object nv) && nv != null ? Str(c, "name").Trim() : "";
                if (name.Length > MaxCameraName) throw new Error($"cameras[{i}].name is longer than {MaxCameraName} characters");
                bool mounted = false;
                if (c.TryGetValue("kind", out object kv) && kv != null)
                {
                    string kind = kv as string;
                    mounted = kind == "mounted" ? true : kind == "seat" ? false : throw new Error($"cameras[{i}].kind must be \"seat\" or \"mounted\"");
                }

                float seat = float.NaN;
                if (c.TryGetValue("seat", out object sv) && sv != null)
                {
                    if (mounted) throw new Error($"cameras[{i}].seat: a mounted camera has no seat");
                    string seatName = sv as string;
                    seat = seatName == "air" ? 1f : seatName == "fixed" ? 0f : throw new Error($"cameras[{i}].seat must be \"air\" or \"fixed\"");
                }

                cameras[i] = new VehicleProfile.CameraData(name, mounted, Vec3(c, "eye", -30f, 30f), Opt(c, "yawDeg", 0f, -180f, 180f),
                    Opt(c, "lookDownDeg", float.NaN, mounted ? -45f : -10f, mounted ? 60f : 30f), Opt(c, "fovDeg", float.NaN, 20f, 110f), seat,
                    Opt(c, "insulation", float.NaN, 0f, 1f));
            }

            return cameras;
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out object v) && v is Dictionary<string, object> d ? d : throw new Error("missing object: " + key);

        private static Dictionary<string, object> Item(List<object> list, int i, string what) =>
            list[i] as Dictionary<string, object> ?? throw new Error($"{what}[{i}] is not an object");

        private static List<object> Arr(Dictionary<string, object> o, string key, int min, int max)
        {
            if (!o.TryGetValue(key, out object v) || !(v is List<object> list)) throw new Error("missing array: " + key);
            if (list.Count < min || list.Count > max) throw new Error($"{key}: {list.Count} entries (allowed {min}..{max})");
            return list;
        }

        private static bool Bool(Dictionary<string, object> o, string key)
        {
            if (!o.TryGetValue(key, out object v) || v == null) return false;
            return v is bool b ? b : throw new Error(key + " must be true or false");
        }

        private static string Str(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out object v) && v is string s ? s : throw new Error("missing text: " + key);

        private static float Num(Dictionary<string, object> o, string key, float min, float max)
        {
            if (!o.TryGetValue(key, out object v) || !(v is MiniJson.Number n)) throw new Error("missing number: " + key);
            return Check(n.Float, key, min, max);
        }

        private static float Opt(Dictionary<string, object> o, string key, float fallback, float min, float max) =>
            o.TryGetValue(key, out object v) && v != null ? Num(o, key, min, max) : fallback;

        private static int Int(Dictionary<string, object> o, string key, int min, int max)
        {
            if (!o.TryGetValue(key, out object v) || !(v is MiniJson.Number n)) throw new Error("missing number: " + key);
            double d = n.Double;
            if (d != Math.Floor(d) || d < min || d > max) throw new Error($"{key}: {n.Text} (an integer {min}..{max})");
            return (int)d;
        }

        private static int OptInt(Dictionary<string, object> o, string key, int fallback, int min, int max) =>
            o.TryGetValue(key, out object v) && v != null ? Int(o, key, min, max) : fallback;

        private static float[] Floats(Dictionary<string, object> o, string key, int count, float min, float max)
        {
            if (!o.TryGetValue(key, out object v) || !(v is List<object> list) || list.Count != count)
                throw new Error($"{key}: expected {count} numbers");
            var r = new float[count];
            for (int i = 0; i < count; i++)
                r[i] = list[i] is MiniJson.Number n ? Check(n.Float, key, min, max) : throw new Error($"{key}[{i}] is not a number");
            return r;
        }

        private static float2 Vec2(Dictionary<string, object> o, string key, float min, float max)
        { float[] f = Floats(o, key, 2, min, max); return new float2(f[0], f[1]); }

        private static float3 Vec3(Dictionary<string, object> o, string key, float min, float max)
        { float[] f = Floats(o, key, 3, min, max); return new float3(f[0], f[1], f[2]); }

        private static float4 Vec4(Dictionary<string, object> o, string key, float min, float max)
        { float[] f = Floats(o, key, 4, min, max); return new float4(f[0], f[1], f[2], f[3]); }

        private static float Check(float f, string key, float min, float max)
        {
            if (float.IsNaN(f) || float.IsInfinity(f) || f < min || f > max) throw new Error($"{key}: {f} (allowed {min}..{max})");
            return f;
        }
    }
}
