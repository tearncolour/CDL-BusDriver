using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>An explicitly authored section in one two-body assembly. Rest transforms are evidence,
    /// never a substitute for either vehicle entity's live transform.</summary>
    internal sealed class VehicleSectionBinding
    {
        internal readonly string Id, Prefab;
        internal readonly float3 RestPosition;
        internal readonly quaternion RestRotation;
        internal readonly VehicleProfile Profile;
        internal VehicleSectionBinding(string id, string prefab, float3 position, quaternion rotation, VehicleProfile profile)
        { Id = id; Prefab = prefab; RestPosition = position; RestRotation = rotation; Profile = profile; }
    }

    internal sealed class VehicleAssembly
    {
        internal readonly string Model, Identity;
        internal readonly VehicleSectionBinding Front, Rear;
        internal readonly float3 FrontAnchor, RearAnchor;
        internal readonly float MaxSeparation;
        internal readonly VehicleArticulation Joint;
        internal VehicleAssembly(string model, string identity, VehicleSectionBinding front, VehicleSectionBinding rear,
            float3 frontAnchor, float3 rearAnchor, float maxSeparation, VehicleArticulation joint)
        {
            Model = model; Identity = identity; Front = front; Rear = rear;
            FrontAnchor = frontAnchor; RearAnchor = rearAnchor; MaxSeparation = maxSeparation;
            Joint = joint;
            front.Profile.Assembly = this; front.Profile.SectionId = front.Id;
            rear.Profile.Assembly = this; rear.Profile.SectionId = rear.Id;
        }
        internal VehicleProfile ForPrefab(string prefab) => string.Equals(prefab, Front.Prefab, StringComparison.Ordinal) ? Front.Profile :
            string.Equals(prefab, Rear.Prefab, StringComparison.Ordinal) ? Rear.Profile : null;
    }

    /// <summary>
    /// A real mirror's glass on an authored model, as the runtime mirror camera and the overlay use it (model coordinates):
    /// centre, normal (out of the glass, towards the driver), up, size; a convex depth makes it the oval convex kind.
    /// </summary>
    internal readonly struct MirrorSurface
    {
        internal readonly float3 Centre, Normal, Up;
        internal readonly float2 Size;
        internal readonly float ConvexDepth;
        internal readonly bool Oval;
        private readonly float m_Exponent, m_Taper;
        private readonly float2[] m_Outline;
        internal int Segments => m_Outline?.Length ?? (Oval ? 80 : 96);
        internal int RadialRings => Oval ? 9 : 1;
        internal float3 OverlayCentre => Centre + Normal * 0.0007f;
        internal quaternion Frame => quaternion.LookRotationSafe(-Normal, Up);

        internal MirrorSurface(float3 centre, float3 normal, float3 up, float2 size, float convexDepth = 0f,
            float exponent = 3.7f, float taper = .075f, float2[] outline = null)
        {
            Centre = centre; Normal = math.normalize(normal); Up = math.normalize(up);
            Size = size; ConvexDepth = convexDepth; Oval = convexDepth > 0f;
            m_Exponent = exponent; m_Taper = taper; m_Outline = outline;
        }

        internal float2 Outline(int i)
        {
            if (m_Outline != null) return m_Outline[i];
            float angle = 2f * math.PI * i / Segments;
            float c = math.cos(angle), s = math.sin(angle);
            if (Oval) return 0.5f * Size * new float2(c, s);
            float u = math.sign(c) * math.pow(math.abs(c), 2f / m_Exponent);
            float v = math.sign(s) * math.pow(math.abs(s), 2f / m_Exponent);
            return new float2(0.5f * Size.x * u * (1f + m_Taper * v), 0.5f * Size.y * v);
        }

        internal float3 SurfacePoint(int i, float radius)
            => new float3(Outline(i) * radius, -ConvexDepth * (1f - radius * radius));

        internal float3 SurfaceNormal(float3 p) => math.normalize(new float3(
            8f * ConvexDepth * p.x / (Size.x * Size.x), 8f * ConvexDepth * p.y / (Size.y * Size.y), -1f));

        /// <summary>
        /// The outline of a flat glass that is a rectangle with rounded corners (radius in metres, 0 = square corners), counter-clockwise
        /// from the top of the right edge like the default outline; the corners are exact, so the reflection covers the whole glass.
        /// </summary>
        internal static float2[] RoundedRect(float2 size, float radius, int arcSegments = 6)
        {
            float2 half = size * 0.5f;
            float r = math.clamp(radius, 0f, math.cmin(half));
            int n = r > 0f ? arcSegments : 0;
            var points = new float2[4 * (n + 1)];
            int k = 0;
            for (int corner = 0; corner < 4; corner++)
            {
                float a0 = corner * 0.5f * math.PI;
                float2 c = new float2(corner == 0 || corner == 3 ? half.x - r : r - half.x, corner < 2 ? half.y - r : r - half.y);
                for (int i = 0; i <= n; i++)
                {
                    float a = a0 + (n == 0 ? 0f : 0.5f * math.PI * i / n);
                    points[k++] = c + r * new float2(math.cos(a), math.sin(a));
                }
            }
            return points;
        }

        /// <summary>The mirror of an authored model's spec (0 driver side, 1 door side, 2 the door side's convex one).</summary>
        internal static MirrorSurface Of(in BusInteriorSpec spec, int index) =>
            VehicleModels.Find(spec.ModelVariant)?.Mirror(index) ?? BusInterior.GenericMirror(spec, index);
    }

    /// <summary>
    /// One authored vehicle: what the mod needs besides its runtime mesh (VehicleMesh) to drive it - the body dimensions, the
    /// doors, the LED signs, the card readers, the mirrors, the places for people. Comes from a vehicle pack's profile
    /// (&lt;model&gt;.cdlb.json, VehicleProfileJson), all in the model's runtime coordinates (+X the door side for a left-hand-drive
    /// bus, +Y up, +Z forward, metres).
    /// </summary>
    internal sealed class VehicleProfile
    {
        internal readonly struct NativeDoor
        {
            internal readonly float3 Position;
            internal readonly quaternion Rotation;
            internal readonly bool CanBoard, CanAlight;
            internal NativeDoor(float3 position, quaternion rotation, bool canBoard, bool canAlight)
            { Position = position; Rotation = rotation; CanBoard = canBoard; CanAlight = canAlight; }
        }
        internal NativeDoor[] NativeDoors { get; set; }
        internal readonly struct Axle
        {
            internal readonly float Z;
            internal readonly bool Steer;
            internal readonly int LeftPart, RightPart;
            internal Axle(float z, bool steer, int left, int right) { Z = z; Steer = steer; LeftPart = left; RightPart = right; }
        }
        /// <summary>
        /// The driver's cab the profile declares (optional "cab" block): a correction of the runtime mesh's eye (the mesh's own eye stays
        /// the formal one the mirrors were aimed from), how far the driver looks down, the seat (1 air-sprung, 0 fixed) and how much the
        /// closed saloon muffles the outside. NaN: not said, the driving mod decides. Handed to Core with SetCab (API 10).
        /// </summary>
        internal readonly struct CabData
        {
            internal static readonly CabData None = new CabData(float3.zero, float.NaN, float.NaN, float.NaN);
            internal readonly float3 EyeOffset;
            internal readonly float LookDownDeg, SeatSpring, Insulation;
            internal readonly DriverFeetData? DriverFeet;
            internal CabData(float3 eyeOffset, float lookDownDeg, float seatSpring, float insulation, DriverFeetData? driverFeet = null)
            { EyeOffset = eyeOffset; LookDownDeg = lookDownDeg; SeatSpring = seatSpring; Insulation = insulation; DriverFeet = driverFeet; }
        }
        internal CabData Cab { get; set; } = CabData.None;
        /// <summary>
        /// The driver's view draws this pack's own game asset (its LOD0 meshes with their native materials: normal and mask maps)
        /// instead of the runtime mesh's static parts (optional "cabRender": "native", BusDriver 0.13.1; BusNativeCab). The moving
        /// parts and the card reader still come from the runtime mesh and the mod. Default: the runtime mesh draws the cab.
        /// </summary>
        internal bool NativeCab { get; set; }

        /// <summary>
        /// A camera position the profile declares besides the driver's seat (optional "cameras" array; Core API 14): a seat inside (a
        /// passenger's) or a camera fixed on the outside, its eye in the profile's model space (a rear section's on that section), its yaw
        /// (degrees, 0 forward, positive to the right), and optionally its look-down, field of view, seat (1 air-sprung, 0 fixed) and
        /// insulation. NaN: not said, the driving mod decides. The driver's seat (the mesh's eye and the "cab" block) is always camera 0.
        /// </summary>
        internal readonly struct CameraData
        {
            internal readonly string Name;
            internal readonly bool Mounted;
            internal readonly float3 Eye;
            internal readonly float YawDeg, LookDownDeg, FovDeg, SeatSpring, Insulation;
            internal CameraData(string name, bool mounted, float3 eye, float yawDeg, float lookDownDeg, float fovDeg, float seatSpring, float insulation)
            {
                Name = name; Mounted = mounted; Eye = eye; YawDeg = yawDeg; LookDownDeg = lookDownDeg; FovDeg = fovDeg; SeatSpring = seatSpring;
                Insulation = insulation;
            }
        }
        internal CameraData[] Cameras { get; set; } = Array.Empty<CameraData>();

        internal VehicleAssembly Assembly { get; set; }
        internal string SectionId { get; set; }
        internal Axle[] Axles { get; set; }
        internal bool NextStopEnabled { get; set; } = true;
        internal float[] DoorOpenDegrees { get; set; }
        internal float[] DoorSides { get; set; }
        internal int ExtraDoorLeafCount => DoorLeaves.Length > 3 ? DoorLeaves[3] : 0;
        internal int FirstDoorDrive => BusInterior.PartCount + ExtraDoorLeafCount;
        internal int DoorPart(int door, int leaf) => door < 3 ? BusInterior.DoorFrontA + 2 * door + leaf : BusInterior.PartCount + leaf;
        internal float DoorSide(int door) => DoorSides == null ? -Spec.DriverSide : DoorSides[door];
        internal bool IsDoorPart(int part)
        {
            for (int d = 0; d < DoorLeaves.Length; d++)
                for (int leaf = 0; leaf < DoorLeaves[d]; leaf++)
                    if (DoorPart(d, leaf) == part) return true;
            return false;
        }

        /// <summary>How long a door takes to open and to close, and how long after the close command it starts to close.</summary>
        internal readonly struct DoorTiming
        {
            internal static readonly DoorTiming Default = new DoorTiming(BusInterior.DoorOpenSeconds, BusInterior.DoorCloseSeconds, 0f);
            internal readonly float OpenSeconds, CloseSeconds, CloseDelay;
            internal DoorTiming(float open, float close, float delay) { OpenSeconds = open; CloseSeconds = close; CloseDelay = delay; }
        }
        internal DoorTiming[] DoorTimings { get; set; }
        /// <summary>Per door: null for hinged leaves, else one outward-swing linkage per leaf (A, then B): a roller in a rail, or a parallel four-bar with a second drive part.</summary>
        internal DoorLinkage[][] DoorLinkages { get; set; }
        /// <summary>How many parts after the 34 contract parts turn with door shafts (DoorDrive0..: one per drive arm, a four-bar leaf has two); the wrap surfaces and straps come after them.</summary>
        internal int DoorDriveCount { get; set; }
        /// <summary>
        /// The body wrap also covers the side and rear windows (<see cref="WrapWindows"/>: one wrap surface part with their skin) and
        /// the door leaves (<see cref="WrapDoors"/>: one per leaf, a child of the leaf, so the picture goes with the door). The mesh
        /// carries them as wrap surface parts after the door drives. Layout: the version of their geometry (a wrap file is made for
        /// one); OutlineMm: the white edge of a see-through hole in a window or door, in millimetres.
        /// </summary>
        internal bool WrapWindows { get; set; }
        internal bool WrapDoors { get; set; }
        internal int WrapLayout { get; set; }
        internal const float DefaultWrapOutlineMm = 10f;
        internal float WrapOutlineMm { get; set; } = DefaultWrapOutlineMm;
        /// <summary>
        /// Named pillars (wrap.pillars): the glazing inside each box is body paint for the wrap on every face, the front too (whose
        /// picture otherwise leaves the windscreen's frame out), and the template outlines the box on the faces listed. Model space.
        /// </summary>
        internal WrapPillar[] WrapPillars { get; set; } = new WrapPillar[0];
        internal sealed class WrapPillar
        {
            internal string Name;
            internal float3 Min, Max;
            internal BusInterior.WrapFace[] Faces;
            internal bool Contains(float3 p) => math.all(p >= Min & p <= Max);
        }
        internal int WrapSurfaceCount
        {
            get
            {
                int n = WrapWindows ? 1 : 0;
                if (WrapDoors)
                    foreach (byte leaves in DoorLeaves) n += leaves;
                return n;
            }
        }
        /// <summary>How a sign is drawn: an LED dot matrix, or a printed board ("card": the 1990s white board, CardComposer).</summary>
        internal enum SignStyle : byte { Led, Card }

        internal readonly struct Sign
        {
            internal readonly float3 Centre;
            internal readonly float2 Size;
            internal readonly int2 Matrix;
            // A side sign on a body that narrows towards the roof: its face leans in at the top by this much (radians).
            internal readonly float Tilt;
            internal readonly SignStyle Style;
            // a card's paint and board (sRGB 0..255; Ink.x < 0: the defaults, CardRgb.DefaultInk / DefaultBoard)
            internal readonly float3 Ink, Board;
            internal Sign(float3 centre, float width, int columns, float height = 0f, float tilt = 0f, SignStyle style = SignStyle.Led,
                float3 ink = default, float3 board = default, bool colours = false)
            {
                Centre = centre;
                Matrix = new int2(columns, 16);
                Size = new float2(width, height > 0f ? height : width * 16f / columns);
                Tilt = tilt;
                Style = style;
                Ink = colours ? ink : new float3(-1f);
                Board = colours ? board : new float3(-1f);
            }

            internal bool IsCard => Style == SignStyle.Card && Matrix.x > 0;
        }

        internal readonly string Name, MeshSha256, Source;
        private readonly string m_Key;
        internal string Key => Assembly == null ? m_Key : m_Key+"-assembly-"+Assembly.Identity;
        private BusInteriorSpec m_Spec;
        internal BusInteriorSpec Spec => m_Spec;
        internal readonly float WheelRadius, WrapBottom;
        internal readonly float3 DefaultBodyColor, NextStopCentre, WrapThresholds;
        internal readonly float2 NextStopSize;
        internal readonly int2 NextStopMatrix;
        internal readonly float2[] Doors;
        internal readonly byte[] DoorLeaves;
        internal readonly Sign FrontSign, SideSign, RearSign;
        internal Sign[] ExtraSideSigns { get; set; } = Array.Empty<Sign>();
        internal readonly (float3 origin, quaternion frame, float xSign)[] CardReaders;
        private readonly MirrorSurface[] m_Mirrors;
        private readonly BusInterior.RiderSlot[] m_Riders;
        private readonly Lazy<VehicleMesh> m_Mesh;

        internal VehicleProfile(string name, BusInteriorSpec spec, float wheelRadius, float wrapBottom, float3 wrapThresholds,
            float3 bodyColor, float2[] doors, byte[] doorLeaves, Sign front, Sign side, Sign rear, float3 nextStopCentre,
            (float3 origin, quaternion frame, float xSign)[] cardReaders, BusInterior.RiderSlot[] riders, MirrorSurface[] mirrors,
            Func<Stream> openMesh, string meshSha256 = null, string source = null,
            float nextStopWidth = 0f, float nextStopHeight = 0f, int nextStopColumns = 128, int nextStopRows = 16)
        {
            Name = name; MeshSha256 = meshSha256; Source = source;
            m_Key = "pack-" + name.ToLowerInvariant() + (string.IsNullOrEmpty(meshSha256) ? "" : "-" + meshSha256.Substring(0, Math.Min(8, meshSha256.Length)));
            m_Spec = spec; m_Spec.ModelVariant = BusModelVariant.Generic;
            WheelRadius = wheelRadius;
            WrapBottom = wrapBottom; WrapThresholds = wrapThresholds; DefaultBodyColor = bodyColor; Doors = doors; DoorLeaves = doorLeaves;
            FrontSign = front; SideSign = side; RearSign = rear; NextStopCentre = nextStopCentre;
            NextStopSize = new float2(nextStopWidth, nextStopHeight);
            NextStopMatrix = new int2(nextStopColumns, nextStopRows);
            CardReaders = cardReaders; m_Riders = riders; m_Mirrors = mirrors;
            m_Mesh = new Lazy<VehicleMesh>(() =>
            {
                if (openMesh == null) throw new InvalidDataException("Vehicle " + name + " has no runtime mesh");
                using (Stream stream = openMesh())
                {
                    VehicleMesh mesh = VehicleMesh.Load(this, stream);
                    ValidateAxles(mesh);
                    ValidateDoorDrives(mesh);
                    ValidateWrapSurfaces(mesh);
                    return mesh;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>The runtime mesh file (a pack's; null: the mesh comes from elsewhere).</summary>
        internal string MeshPath { get; set; }

        internal int MirrorCount => m_Mirrors.Length;
        internal VehicleMesh Mesh => m_Mesh.Value;
        internal MirrorSurface Mirror(int index) => m_Mirrors[index];
        internal List<BusInterior.RiderSlot> RiderSlots() => new List<BusInterior.RiderSlot>(m_Riders);
        internal bool HasDoorLeaf(int door, bool second) => door >= 0 && door < DoorLeaves.Length &&
            DoorLeaves[door] >= (second ? 2 : 1);

        // VehicleModels hands out the variant (the spec carries it; every model-specific lookup goes through it).
        internal void AssignVariant(BusModelVariant variant) => m_Spec.ModelVariant = variant;
        internal void SetArticulation(float jointZ, float gangwayLength)
        { m_Spec.JointZ = jointZ; m_Spec.GangwayLength = gangwayLength; }

        private void ValidateAxles(VehicleMesh mesh)
        {
            if (Axles == null) return; // v1 keeps its existing mesh contract.
            var declared = new HashSet<int>();
            foreach (Axle axle in Axles)
                foreach (int part in new[] { axle.LeftPart, axle.RightPart })
                {
                    declared.Add(part);
                    if (math.abs(mesh.Pivot(part).z-axle.Z) > .001f)
                        throw new InvalidDataException(Name+": wheel pivot disagrees with declared axle "+part);
                    for (int lod = 0; lod < 3; lod++)
                        if (mesh.TriangleCount(part,lod) == 0)
                            throw new InvalidDataException(Name+": declared wheel has no geometry "+part+" LOD "+lod);
                }
            for (int part = 19; part <= 22; part++)
                if (!declared.Contains(part))
                    for (int lod = 0; lod < 3; lod++)
                        if (mesh.TriangleCount(part,lod) != 0)
                            throw new InvalidDataException(Name+": undeclared extra wheel geometry "+part);
        }

        // The mesh's door drive parts are exactly the ones the linkages name, each on its shaft's axis; a linked leaf has geometry.
        private void ValidateDoorDrives(VehicleMesh mesh)
        {
            if (mesh.ExtraDoorLeafCount != ExtraDoorLeafCount)
                throw new InvalidDataException(Name + ": appended door leaves disagree with the profile");
            for (int leaf = 0; leaf < ExtraDoorLeafCount; leaf++)
                for (int lod = 0; lod < 3; lod++)
                    if (mesh.TriangleCount(DoorPart(3, leaf), lod) == 0)
                        throw new InvalidDataException(Name + ": fourth door leaf has no geometry at LOD " + lod);
            for (int d = 0; d < DoorLeaves.Length; d++)
                for (int leaf = 0; leaf < DoorLeaves[d]; leaf++)
                    if ((DoorSides != null || ExtraDoorLeafCount > 0) && mesh.Pivot(DoorPart(d, leaf)).x * DoorSide(d) <= 0f)
                        throw new InvalidDataException(Name + ": door leaf pivot is on the wrong side");
            if (mesh.DriveCount != DoorDriveCount)
                throw new InvalidDataException($"{Name}: the mesh has {mesh.DriveCount} door drive parts, the profile's linkages {DoorDriveCount}");
            if (DoorLinkages == null) return;
            for (int d = 0; d < DoorLinkages.Length; d++)
            {
                if (DoorLinkages[d] == null) continue;
                for (int leaf = 0; leaf < DoorLinkages[d].Length; leaf++)
                {
                    DoorLinkage linkage = DoorLinkages[d][leaf];
                    CheckDrivePart(mesh, d, leaf, linkage.DrivePart, linkage.Shaft);
                    if (linkage.Kind == DoorLinkageKind.ParallelFourBar)
                        CheckDrivePart(mesh, d, leaf, linkage.BalanceDrivePart, linkage.BalanceShaft);
                }
            }
        }

        // One drive part of a leaf's linkage: a drive part of the mesh, its pivot on its shaft, the body's axes, geometry at every LOD (the leaf's too).
        private void CheckDrivePart(VehicleMesh mesh, int door, int leaf, int part, float2 shaft)
        {
            if (!mesh.IsDrive(part))
                throw new InvalidDataException($"{Name}: door {door} leaf {leaf}: part {part} is not a door drive part");
            float3 pivot = mesh.Pivot(part);
            if (math.distance(pivot.xz, shaft) > .001f)
                throw new InvalidDataException($"{Name}: door drive part {part} is not on its shaft");
            if (math.abs(math.dot(mesh.Rotation(part).value, quaternion.identity.value)) < .99999f)
                throw new InvalidDataException($"{Name}: door drive part {part} must keep the body's axes");
            for (int lod = 0; lod < 3; lod++)
                if (mesh.TriangleCount(part, lod) == 0 || mesh.TriangleCount(DoorPart(door, leaf), lod) == 0)
                    throw new InvalidDataException($"{Name}: door {door} leaf {leaf} or its drive has no geometry at LOD {lod}");
        }

        // The mesh's wrap surfaces are exactly the ones the profile's wrap block asks for: the window skin (if windows), then each
        // door leaf's, in leaf order, each a child of its leaf, each with geometry at every LOD.
        private void ValidateWrapSurfaces(VehicleMesh mesh)
        {
            if (mesh.SurfaceCount != WrapSurfaceCount)
                throw new InvalidDataException($"{Name}: the mesh has {mesh.SurfaceCount} wrap surfaces, the profile's wrap block asks for {WrapSurfaceCount}");
            int part = mesh.FirstSurface;
            if (WrapWindows)
            {
                if (mesh.SurfaceParent(part) != BusInterior.Shell) throw new InvalidDataException($"{Name}: the window wrap surface must hang from the shell");
                for (int lod = 0; lod < 3; lod++)
                    if (mesh.TriangleCount(part, lod) == 0) throw new InvalidDataException($"{Name}: the window wrap surface is empty at LOD {lod}");
                part++;
            }
            if (!WrapDoors) return;
            for (int d = 0; d < DoorLeaves.Length; d++)
                for (int leaf = 0; leaf < DoorLeaves[d]; leaf++, part++)
                {
                    if (mesh.SurfaceParent(part) != DoorPart(d, leaf))
                        throw new InvalidDataException($"{Name}: door {d} leaf {leaf}: its wrap surface must hang from its leaf");
                    for (int lod = 0; lod < 3; lod++)
                        if (mesh.TriangleCount(part, lod) == 0) throw new InvalidDataException($"{Name}: door {d} leaf {leaf}: its wrap surface is empty at LOD {lod}");
                }
        }

        /// <summary>The timing of a door (the defaults when the profile gives none).</summary>
        internal DoorTiming Timing(int door) => DoorTimings != null && door >= 0 && door < DoorTimings.Length ? DoorTimings[door] : DoorTiming.Default;
    }

    /// <summary>
    /// The authored vehicles known in this session, by the variant number their spec carries (BusModelVariant: 0 = the generic
    /// procedural bus, 1..255 = registered profiles in registration order). Registered on the main thread when a pack is found;
    /// read from model build tasks too (a slot is written once, before its variant is handed out).
    /// </summary>
    internal static class VehicleModels
    {
        private static readonly VehicleProfile[] s_Slots = new VehicleProfile[256];
        private static readonly object s_Lock = new object();
        private static int s_Count;

        internal static VehicleProfile Find(BusModelVariant variant)
        {
            int i = (int)variant;
            return i == 0 ? null : Volatile.Read(ref s_Slots[i]);
        }

        /// <summary>How many profiles are registered (a change tells the cab to look for new ones to prepare ahead).</summary>
        internal static int Count { get { lock (s_Lock) return s_Count; } }

        /// <summary>The registered profiles, in registration order.</summary>
        internal static List<VehicleProfile> Snapshot()
        {
            lock (s_Lock)
            {
                var list = new List<VehicleProfile>(s_Count);
                for (int i = 1; i <= s_Count; i++)
                    if (s_Slots[i] != null) list.Add(s_Slots[i]);
                return list;
            }
        }

        /// <summary>Gives the profile its variant (once) and makes it findable; the same profile again keeps its variant.</summary>
        internal static VehicleProfile Register(VehicleProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            lock (s_Lock)
            {
                if (profile.Spec.ModelVariant != BusModelVariant.Generic && Find(profile.Spec.ModelVariant) == profile)
                    return profile;
                if (s_Count >= s_Slots.Length - 1)
                    throw new InvalidOperationException("Too many vehicle profiles in one session");
                int slot = ++s_Count;
                profile.AssignVariant((BusModelVariant)slot);
                Volatile.Write(ref s_Slots[slot], profile);
                return profile;
            }
        }

        internal static void RegisterAssembly(VehicleAssembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            lock (s_Lock)
            {
                int needed = (assembly.Front.Profile.Spec.ModelVariant == BusModelVariant.Generic ? 1 : 0) +
                             (assembly.Rear.Profile.Spec.ModelVariant == BusModelVariant.Generic ? 1 : 0);
                if (s_Count+needed >= s_Slots.Length) throw new InvalidOperationException("Too many vehicle profiles in one session");
                Register(assembly.Front.Profile); Register(assembly.Rear.Profile);
            }
        }
    }
}
