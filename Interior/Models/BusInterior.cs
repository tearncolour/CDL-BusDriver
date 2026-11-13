using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// The bus the player drives, inside and out: the saloon, the driver's cab and the doors around the camera in the driver's view
// (the game's bus has no interior: from inside, its body faces away from the camera and is not drawn), and with the exterior
// parts (body paint, the black glazing frames, the line's livery band, wheels, lamps) the whole bus in the chase view, drawn instead
// of the game's. The windows are clear openings (the game's shader build has no transparent glass): the saloon is seen from outside.
//
// Model frame = the bus entity's own frame (the vanilla Transform): metres, origin on the ground at the vehicle origin, +Z forward,
// +Y up, +X right. Every size comes from BusInteriorSpec (measured from the game's bus prefab), so the interior fits any bus.
// The driver sits on the side given by DriverSide (-1 left: right-hand traffic; +1 right: left-hand traffic, the whole layout
// mirrored); the doors are on the other (kerb) side.
//
// CONTRACT (BusCabSystem relies on it; keep it when refining the model):
//  * Part indices 0..PartCount-1 below; the animated parts keep their index, pivot and local axes; static geometry may be split
//    into any number of static parts (add them after the animated ones and raise PartCount).
//  * Pose: SteerDeg (steering wheel turn, positive = clockwise as the driver sees it = steering right), SpeedKmh, Rpm, DoorOpen
//    (0 closed .. 1 open, per door: x front, y middle, z rear), Lights (Lamps = saloon lights, Tail = "stop" request sign,
//    Beacon = door warning lamps flashing).
//  * Eye(spec): the driver's eye point (model space). DashFacing: the driver looks along +Z.
//  * Exterior parts (BusInterior.Exterior.cs) are shown only when the bus is seen from outside (Pose.Outside): the glazing frames
//    would crowd the view out of the cab. Wheels: pivot = wheel centre, turned about local Y (steering) after local X (rolling).
//    Lamp parts are drawn lit by the cab system's own materials (CabInstance.OverrideMaterial) and switched with Show.
namespace CDLC.BusDriver.Interior.Models
{
    public enum BusSection : byte
    {
        Solo = 0,      // a whole rigid bus
        Front = 1,     // front section of an articulated bus: open at the rear end (bellows ring)
        Rear = 2,      // rear section of an articulated bus: open at the front end, no driver's cab
    }

    /// <summary>Which model draws the bus: 0 the procedural one, 1..255 an authored vehicle registered from a vehicle pack
    /// (VehicleModels; the number is handed out per session, in registration order).</summary>
    public enum BusModelVariant : byte
    {
        Generic = 0,
    }

    public struct BusInteriorSpec
    {
        public float FrontZ, RearZ;     // body ends (front bumper face, rear face)
        public float HalfWidth;         // outer half width of the body (no mirrors), 1.0 .. 1.35
        public float RoofY;             // top of the body shell (roof skin, without roof pods)
        public float FloorY;            // saloon floor above the ground (low floor ~0.36)
        public float FrontAxleZ;        // front axle (Solo / Front)
        public float RearAxleZ;         // rear (drive) axle
        public int Doors;               // 1 .. 3 on the kerb side
        public float DriverSide;        // -1 driver on the left (right-hand traffic), +1 on the right
        public BusSection Section;
        public BusModelVariant ModelVariant;
        public float JointZ;            // Front / Rear: the articulation joint (turntable centre) on the section's axis
        public float GangwayLength;     // Front: from its end to the rear section's end over the joint (straight); the bellows' length

        /// <summary>
        /// The spec of a bus we have no model for, measured from its prefab's bounds (they include the mirrors, so a width over 1.35 m is taken as a bus body's 1.27 m;
        /// the roof is the top less the roof pods). The mod's own bus is never measured: OwnBus.Spec.
        /// </summary>
        public BusInteriorSpec FromBounds(float3 min, float3 max)
        {
            var s = this;
            s.FrontZ = max.z - 0.05f;
            s.RearZ = min.z + 0.05f;
            float half = (max.x - min.x) * 0.5f;
            s.HalfWidth = half > 1.35f ? 1.27f : math.max(1.0f, half - 0.02f);
            s.RoofY = math.clamp(max.y - 0.3f, 2.7f, 3.3f);
            return s;
        }

        public static BusInteriorSpec Default => new BusInteriorSpec
        {
            FrontZ = 6.0f, RearZ = -6.0f, HalfWidth = 1.27f, RoofY = 3.05f, FloorY = 0.36f,
            FrontAxleZ = 3.4f, RearAxleZ = -2.5f, Doors = 3, DriverSide = -1f, Section = BusSection.Solo,
        };

        // Snapped copy (10 cm; the side and section as is): one built model per distinct bus shape.
        public BusInteriorSpec Snapped()
        {
            // This authored asset has exact wheel/door frames. Quantising them
            // to the generic 10 cm buckets would move the model off its axles.
            VehicleProfile model = VehicleModels.Find(ModelVariant);
            if (model != null) return model.Spec;
            var s = this;
            s.FrontZ = KitModelKey.Snap(FrontZ, 0.1f);
            s.RearZ = KitModelKey.Snap(RearZ, 0.1f);
            s.HalfWidth = KitModelKey.Snap(math.clamp(HalfWidth, 1.0f, 1.35f), 0.02f);
            s.RoofY = KitModelKey.Snap(math.clamp(RoofY, 2.5f, 3.6f), 0.05f);
            s.FloorY = KitModelKey.Snap(math.clamp(FloorY, 0.25f, 1.0f), 0.02f);
            s.FrontAxleZ = KitModelKey.Snap(FrontAxleZ, 0.1f);
            s.RearAxleZ = KitModelKey.Snap(RearAxleZ, 0.1f);
            s.JointZ = KitModelKey.Snap(JointZ, 0.05f);
            s.GangwayLength = KitModelKey.Snap(math.clamp(GangwayLength, 0f, 3f), 0.05f);
            s.Doors = math.clamp(Doors, 1, 3);
            s.DriverSide = DriverSide >= 0f ? 1f : -1f;
            return s;
        }
    }

    // Geometry: BusInterior.Geometry.cs (Define).
    [KitModel("bus-interior", Category = "Cab")]
    public sealed partial class BusInterior : KitSource
    {
        // ---- part contract
        public const int Shell = 0;          // floor, walls with window openings, ceiling, wheel arches (static)
        public const int SteeringWheel = 1;  // pivot = wheel hub centre; local +Z = column axis pointing at the driver; turns about local Z
        public const int SpeedNeedle = 2;    // pivot = dial centre; local +Z = out of the dial (towards the driver); turns about local Z
        public const int RpmNeedle = 3;
        public const int DoorFrontA = 4, DoorFrontB = 5, DoorMidA = 6, DoorMidB = 7, DoorRearA = 8, DoorRearB = 9;   // leaves; pivot = hinge post
        public const int Fittings = 10;      // passenger seats (static); more static parts follow (BusInterior.Geometry.cs)
        // exterior (BusInterior.Exterior.cs): seen from outside only
        public const int Exterior = 16;      // body paint details, bumpers, lamp housings, roof pods, underbody (static)
        public const int Glazing = 17;       // the flush glazing's black frames: window band, pillar covers, windscreen and rear frit (static)
        public const int Livery = 18;        // the line-colour band (static; drawn in the line's colour)
        public const int WheelFL = 19, WheelFR = 20, WheelRL = 21, WheelRR = 22;   // pivot = wheel centre
        public const int HeadLamps = 23, TailLamps = 24, BrakeLamps = 25, IndicatorL = 26, IndicatorR = 27, ReverseLamps = 28;
        // the body wrap's pictures (BusInterior.Wrap.cs; shown only with a wrap): WrapLeft + (int)WrapFace
        public const int WrapLeft = 29, WrapRight = 30, WrapFront = 31, WrapRear = 32, WrapRoof = 33;
        public const int PartCount = 34;     // the contract parts; the joint (BusInterior.Joint.cs) and the straps follow

        // Needle sweep: 0 at -120 degrees (lower left), full scale at +120 degrees.
        public const float SpeedFullScale = 120f, RpmFullScale = 3000f, NeedleSweepDeg = 240f;

        public readonly BusInteriorSpec Spec;
        internal readonly bool RawSource;
        public BusInterior() : this(BusInteriorSpec.Default) { }
        public BusInterior(BusInteriorSpec spec) : this(spec, false) { }
        internal BusInterior(BusInteriorSpec spec, bool rawSource) { Spec = spec.Snapped(); RawSource = rawSource; }

        public override KitModelKey Key => KeyFor(Spec, AuthoredModel?.Key ?? "bus-interior");
        internal static KitModelKey KeyFor(in BusInteriorSpec s, string name = "bus-interior") => new KitModelKey(name)
            .With(s.FrontZ, 0.1f).With(s.RearZ, 0.1f).With(s.HalfWidth, 0.02f).With(s.RoofY, 0.05f).With(s.FloorY, 0.02f)
            .With(s.FrontAxleZ, 0.1f).With(s.RearAxleZ, 0.1f).With(s.Doors).With(s.DriverSide > 0f ? 1 : 0).With((int)s.Section)
            .With(s.JointZ, 0.05f).With(s.GangwayLength, 0.05f);

        public override KitVariant Variant => KitVariant.Machine;
        public override int3 RendererBudget => new int3(72, 72, 72);

        // ---------------------------------------------------------------- layout (shared by Parts, Define and the camera)

        // Driver side x sign: the driver's cab is at x = Side * (HalfWidth - ...), the doors at x = -Side * HalfWidth.
        public float Side => Spec.DriverSide;
        public bool HasCab => Spec.Section != BusSection.Rear;
        internal VehicleProfile AuthoredModel => VehicleModels.Find(Spec.ModelVariant);
        public bool IsAuthored => AuthoredModel != null;

        // The driver's eye: seated on the raised cab platform (0.75 m above the seat cushion), about 1.3 m behind the windscreen.
        internal const float kEyeHeight = 1.62f, kEyeBack = 1.40f;     // above the saloon floor, behind the front
        // The camera's eye sits a little lower than the model's (kEyeHeight also aims the dash, which stays): the user found the bus
        // driver's view "still a little high" (18 October); Core 0.13.26 took 6 cm off every bus, now this model's own cab data does.
        internal const float kEyeSeated = -0.06f;

        /// <summary>
        /// The formal driver's eye of the model (an authored model's runtime-mesh eye): what the mirrors were aimed from and the
        /// review probes look from. The camera uses <see cref="CameraEye"/>.
        /// </summary>
        public static float3 Eye(in BusInteriorSpec s)
        {
            var spec = s.Snapped();
            VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
            if (model != null) return model.Mesh.Eye;
            return new float3(spec.DriverSide * (spec.HalfWidth - 0.66f), spec.FloorY + kEyeHeight, spec.FrontZ - kEyeBack);
        }

        /// <summary>
        /// The driver's eye for the driver's view: the formal eye moved by an authored model's cab.eyeOffset, the procedural model's
        /// 6 cm lower (kEyeSeated).
        /// </summary>
        public static float3 CameraEye(in BusInteriorSpec s)
        {
            VehicleProfile model = VehicleModels.Find(s.Snapped().ModelVariant);
            return Eye(s) + (model != null ? model.Cab.EyeOffset : new float3(0f, kEyeSeated, 0f));
        }

        /// <summary>
        /// The rest of the driver's cab for Core (look-down, seat 0..1, insulation 0..1; NaN = Core decides): an authored model's
        /// profile, the procedural bus an air-sprung seat in a fully insulated saloon (what Core gave every bus before).
        /// </summary>
        internal static VehicleProfile.CabData DriverCab(in BusInteriorSpec s)
        {
            VehicleProfile model = VehicleModels.Find(s.Snapped().ModelVariant);
            return model != null ? model.Cab : new VehicleProfile.CabData(float3.zero, float.NaN, 1f, 1f);
        }

        // Steering wheel hub: in front of the driver, tilted like a bus wheel (column 35 degrees from vertical towards the driver).
        public float3 WheelHub => IsAuthored ? AuthoredModel.Mesh.Pivot(SteeringWheel) :
            new float3(Side * (Spec.HalfWidth - 0.66f), Spec.FloorY + 1.14f, Spec.FrontZ - 0.84f);
        public static readonly float WheelTiltDeg = 55f;      // wheel plane from vertical (a bus wheel is nearly flat)
        // local +Z = column axis towards the driver (up and back), local +Y = towards the top of the rim (forward and up), local +X =
        // the driver's left. A positive turn about local +Z is clockwise as the driver sees it.
        public quaternion WheelRot => IsAuthored ? AuthoredModel.Mesh.Rotation(SteeringWheel) :
            math.mul(quaternion.RotateX(math.radians(WheelTiltDeg)), quaternion.RotateY(math.PI));

        // Dials in the low cluster behind the wheel, read through the rim's upper half (between the rim top and the hub, as on a
        // VDV dash): its top stays under the line from the eye over the rim, so it never stands in the view of the road.
        // Speedometer on the driver's left, tachometer on the right.
        public float3 DialCentre(int which) => IsAuthored ? AuthoredModel.Mesh.Pivot(which == 0 ? SpeedNeedle : RpmNeedle) :
            new float3(Side * (Spec.HalfWidth - 0.66f) + (which == 0 ? -0.145f : 0.145f), Spec.FloorY + 1.07f, Spec.FrontZ - 0.50f);
        // facing the driver (-Z), leaned back 30 degrees (square to the line of sight); local +Y = the top of the dial, local +X = the
        // driver's left, local +Z = out of the dial towards the driver (a positive turn about it is clockwise as the driver sees it).
        public quaternion DialRot => IsAuthored ? AuthoredModel.Mesh.Rotation(SpeedNeedle) :
            math.mul(quaternion.RotateY(math.PI), quaternion.RotateX(math.radians(-30f)));

        // Door openings on the kerb side: centre z and clear width.
        public int DoorCount => IsAuthored ? AuthoredModel.Doors.Length : Spec.Section == BusSection.Rear ? math.min(Spec.Doors, 2) : Spec.Doors;
        public float2 Door(int i)
        {
            if (IsAuthored)
                return AuthoredModel.Doors[i];
            float len = Spec.FrontZ - Spec.RearZ;
            switch (Spec.Section)
            {
                case BusSection.Rear:
                    return i == 0 ? new float2(Spec.RearAxleZ + 2.1f, 1.2f) : new float2(Spec.RearZ + 1.2f, 1.2f);
                default:
                {
                    if (i == 0)
                    {
                        // ahead of the front wheel arch: narrower on a short front overhang (a minibus), never over the wheel
                        float front = Spec.FrontZ - .42f, back = math.max(Spec.FrontZ - 1.62f, Spec.FrontAxleZ + .66f + .05f);
                        float w = math.clamp(front - back, 0.7f, 1.2f);
                        return new float2(front - 0.5f * w, w);
                    }
                    // The rear door behind the rear axle (a 12 m bus's overhang, the engine bay behind it); a bus whose rear axle
                    // sits near the end has no room there (the door stood across the rear corner): just ahead of the rear arch.
                    float behind = Spec.RearAxleZ - 1.9f;
                    bool rearRoom = behind - 0.5f - Spec.RearZ >= 0.95f;
                    float2 rear = rearRoom ? new float2(behind, 1.0f) : new float2(Spec.RearAxleZ + 0.66f + 0.05f + 0.5f, 1.0f);
                    if (i == 2) return rear;
                    if (rearRoom || Spec.Doors < 3)
                        return new float2(math.lerp(Spec.FrontAxleZ, Spec.RearAxleZ, Spec.Section == BusSection.Front ? 0.55f : 0.62f), 1.2f);
                    // the middle door halfway between the front arch and the rear door
                    return new float2(0.5f * (Spec.FrontAxleZ - 0.66f + rear.x + 0.5f * rear.y), 1.2f);
                }
            }
        }

        /// <summary>
        /// The part the mod's card readers are built into: the Cab part, or for a native-cab model with readers a static part of its
        /// own after the mesh's parts (-1 for a model without readers). The native cab hides the Cab part (the game asset stands in
        /// for it) and drops the asset's own triangles inside the readers' housings, so a housing left in Cab was drawn by neither:
        /// only the readers' lights showed (the BYD B6 session's report of 13 November).
        /// </summary>
        internal static int CardReaderPart(VehicleProfile model) =>
            model == null || model.CardReaders.Length == 0 ? -1 : HasOwnReaderPart(model) ? model.Mesh.PartCount : Cab;

        static bool HasOwnReaderPart(VehicleProfile model) => model.NativeCab && model.CardReaders.Length != 0;

        public override void Parts(KitPartList p)
        {
            if (IsAuthored)
            {
                AuthoredModel.Mesh.Parts(p);
                int readers = CardReaderPart(AuthoredModel);
                if (readers > Cab)
                    p.Add(readers, "CardReaders", Shell, flags: PartFlags.Details);
                return;
            }
            p.Add(Shell, "Shell");
            p.Add(SteeringWheel, "SteeringWheel", Shell, WheelHub, WheelRot);
            p.Add(SpeedNeedle, "SpeedNeedle", Shell, DialCentre(0), DialRot);
            p.Add(RpmNeedle, "RpmNeedle", Shell, DialCentre(1), DialRot);
            for (int d = 0; d < 3; d++)
            {
                // Rest pivots retain the jamb reference; the linkage moves and turns the complete leaf from this frame.
                float2 door = d < DoorCount ? Door(d) : new float2(0f, 1f);
                float x = LeafX;
                p.Add(DoorFrontA + 2 * d, "Door" + d + "A", Shell, new float3(x, Spec.FloorY, door.x + door.y * 0.5f));
                p.Add(DoorFrontA + 2 * d + 1, "Door" + d + "B", Shell, new float3(x, Spec.FloorY, door.x - door.y * 0.5f));
            }
            p.Add(Fittings, "Fittings", Shell);
            p.Add(Dash, "Dash", Shell);
            p.Add(Cab, "Cab", Shell);
            p.Add(Poles, "Poles", Shell, default, default, PartFlags.Details);
            p.Add(Ceiling, "Ceiling", Shell);
            p.Add(Outside, "Outside", Shell, default, default, PartFlags.Details);
            p.Add(Exterior, "Exterior", Shell);
            p.Add(Glazing, "Glazing", Shell, default, default, PartFlags.Details);
            p.Add(Livery, "Livery", Shell, default, default, PartFlags.Details);
            p.Add(WheelFL, "WheelFL", Shell, WheelCentre(true, -1f));
            p.Add(WheelFR, "WheelFR", Shell, WheelCentre(true, 1f));
            p.Add(WheelRL, "WheelRL", Shell, WheelCentre(false, -1f));
            p.Add(WheelRR, "WheelRR", Shell, WheelCentre(false, 1f));
            p.Add(HeadLamps, "HeadLamps", Shell, default, default, PartFlags.Details);
            p.Add(TailLamps, "TailLamps", Shell, default, default, PartFlags.Details);
            p.Add(BrakeLamps, "BrakeLamps", Shell, default, default, PartFlags.Details);
            p.Add(IndicatorL, "IndicatorL", Shell, default, default, PartFlags.Details);
            p.Add(IndicatorR, "IndicatorR", Shell, default, default, PartFlags.Details);
            p.Add(ReverseLamps, "ReverseLamps", Shell, default, default, PartFlags.Details);
            p.Add(WrapLeft, "WrapLeft", Shell, default, default, PartFlags.Details);
            p.Add(WrapRight, "WrapRight", Shell, default, default, PartFlags.Details);
            p.Add(WrapFront, "WrapFront", Shell, default, default, PartFlags.Details);
            p.Add(WrapRear, "WrapRear", Shell, default, default, PartFlags.Details);
            p.Add(WrapRoof, "WrapRoof", Shell, default, default, PartFlags.Details);
            AddJointParts(p);
            AddDoorDriveParts(p);
            AddStrapParts(p);
            if (!RawSource)
                for (int i = DoorFrontA; i <= DoorRearB; i++) p[i].Pivot = BusBarrel.Point(Spec, p[i].Pivot);
        }

        // ---------------------------------------------------------------- pose

        public struct Pose
        {
            public float3 Position;
            public quaternion Rotation;
            public float SteerDeg, SpeedKmh, Rpm;
            public float3 DoorOpen;
            public float FourthDoorOpen;
            public KitLights Lights;
            // Exterior: seen from outside (exterior parts on, glazing in), road-wheel angle (degrees, + right), how far the wheels
            // have rolled (radians), the kneel to undo for the wheels (DrivingApi.GetKneelTransform: rotation, offset), the lamps.
            public bool Outside;
            public float WheelSteerDeg, WheelRoll;
            public quaternion Kneel;
            public float3 KneelOffset;
            public ExteriorLamps Lamps;
            // The body wrap's faces that have a picture (the cab system gives the wrap parts their materials): shown from outside.
            public WrapFaces Wrap;
            // The hand straps' swing (radians): x about the rail's cross axis (fore and aft), y about the rail (sideways).
            public float2 StrapSwing;
            // Articulated front section: the fold (radians, the rear section's heading turned about +Y from this one's).
            public float JointFold;
        }

        /// <summary>The exterior lamps that are lit (bit per lamp part).</summary>
        [System.Flags]
        public enum ExteriorLamps : byte { None = 0, Head = 1, Tail = 2, Brake = 4, Left = 8, Right = 16, Reverse = 32 }

        // Every part each frame (KitInstance drops unchanged writes). spec = the model's (snapped) spec: door count and side.
        public static void Apply(IKitPoser t, in Pose p, in BusInteriorSpec spec)
        {
            t.SetRoot(p.Position, p.Rotation);
            t.Pose(SteeringWheel, float3.zero, quaternion.RotateZ(math.radians(p.SteerDeg)));    // local +Z faces the driver: + = clockwise
            float sweep = math.radians(NeedleSweepDeg);
            float speed = math.saturate(math.abs(p.SpeedKmh) / SpeedFullScale);
            float rpm = math.saturate(p.Rpm / RpmFullScale);
            // needles modelled pointing up (local +Y = mid scale); 0 = -120 degrees (lower left), full scale = +120 (lower right)
            t.Pose(SpeedNeedle, float3.zero, quaternion.RotateZ(sweep * (speed - 0.5f)));
            t.Pose(RpmNeedle, float3.zero, quaternion.RotateZ(sweep * (rpm - 0.5f)));

            // Leaf A hangs from the front post and reaches back (-Z), leaf B from the rear post forward (+Z). Turning A by +a about
            // +Y swings its free edge to -X: inwards for doors on the right (driver on the left, DriverSide -1). A door with a
            // linkage in its profile (outward swing) moves its leaves and turns their shafts as the linkage solves instead.
            VehicleProfile model = VehicleModels.Find(spec.ModelVariant);
            GenericDoorRig generic = model == null ? GenericDoorRig.For(spec) : null;
            int doors = model == null && spec.Section == BusSection.Rear ? math.min(spec.Doors, 2) : spec.Doors;
            for (int d = 0; d < math.max(3, doors); d++)
            {
                float open = d < 3 ? p.DoorOpen[d] : p.FourthDoorOpen;
                bool shown = d < doors;
                int first = model?.DoorPart(d, 0) ?? DoorFrontA + 2 * d;
                int second = model?.DoorPart(d, 1) ?? DoorFrontA + 2 * d + 1;
                float signA = shown && model != null ? model.DoorSide(d) : -spec.DriverSide;
                t.Show(first, shown && (model == null || model.HasDoorLeaf(d, false)));
                if (d < 3 || model.HasDoorLeaf(d, true)) t.Show(second, shown && (model == null || model.HasDoorLeaf(d, true)));
                DoorLinkage[] linkage = generic != null && d < generic.Links.Length ? generic.Links[d] :
                    model?.DoorLinkages != null && d < model.DoorLinkages.Length ? model.DoorLinkages[d] : null;
                if (linkage != null)
                {
                    for (int leaf = 0; leaf < linkage.Length; leaf++)
                        PoseLinkedLeaf(t, model?.DoorPart(d, leaf) ?? DoorFrontA + 2 * d + leaf, linkage[leaf], open);
                    continue;
                }
                float degrees = model?.DoorOpenDegrees != null && d < model.DoorOpenDegrees.Length ? model.DoorOpenDegrees[d] : DoorOpenDeg;
                float a = math.radians(degrees) * DoorEase(open);
                t.Pose(first, float3.zero, quaternion.RotateY(signA * a));
                if (d < 3 || model.HasDoorLeaf(d, true)) t.Pose(second, float3.zero, quaternion.RotateY(-signA * a));
            }
            t.SetLights(p.Lights);
            ApplyExterior(t, p, spec);
            ApplyJoint(t, p.JointFold, spec);
            // an authored model's straps are the parts after its 34 contract parts, its door drives and its wrap surfaces (its export
            // splits them out), up to the end of its mesh's parts: the native cab's card readers, the one part added after them
            // (CardReaderPart), are not straps. Counted from the posed model, so a profile is posed without its mesh being read.
            if (generic != null) ApplyStraps(t, p.StrapSwing, generic.FirstStrap, t.PartCount);
            else ApplyStraps(t, p.StrapSwing, model.FirstDoorDrive + model.DoorDriveCount + model.WrapSurfaceCount, t.PartCount - (HasOwnReaderPart(model) ? 1 : 0));
        }

        // An outward-swing leaf where its linkage puts it (in its parent's rest frame, so it follows the body's kneel), and the
        // shaft with its arms turned about the shaft's axis (the drive part keeps the body's axes). A four-bar leaf has a second
        // shaft, with the balance arm, turned by the same angle. One solve from the opening gives all of it: no state is carried over.
        internal static void PoseLinkedLeaf(IKitPoser t, int part, in DoorLinkage linkage, float open)
        {
            if (!linkage.Solve(open, out float shaft, out float2 joint, out float yaw, out _))
                return;                                  // never: the profile's check solved the whole travel
            KitPartDef def = t.Def(part);
            float3 position = linkage.Move(def.Pivot, joint, yaw);
            quaternion rotation = math.mul(quaternion.RotateY(yaw), def.Rot);
            if (def.Parent >= 0)
            {
                KitPartDef parent = t.Def(def.Parent);
                quaternion inverse = math.inverse(parent.Rot);
                position = math.rotate(inverse, position - parent.Pivot);
                rotation = math.mul(inverse, rotation);
            }
            t.PoseLocal(part, position, rotation);
            if (linkage.DrivePart >= 0 && linkage.DrivePart < t.PartCount)
                t.Pose(linkage.DrivePart, float3.zero, quaternion.RotateY(shaft));
            if (linkage.BalanceDrivePart >= 0 && linkage.BalanceDrivePart < t.PartCount)
                t.Pose(linkage.BalanceDrivePart, float3.zero, quaternion.RotateY(shaft));
        }

        public const float DoorOpenDeg = 86f;
        public const float DoorOpenSeconds = 1.4f, DoorCloseSeconds = 1.8f;

        /// <summary>Every door's motion from its opening 0..1 (hinge angle, linkage shaft): smooth at both ends, as door engines cushion.</summary>
        internal static float DoorEase(float x) { x = math.saturate(x); return x * x * (3f - 2f * x); }

        public override int BoardPoses => 2;
        public override void BoardPose(IKitPoser t, int pose) => Apply(t, BoardPoseOf(pose, true), Spec);

        /// <summary>The test board's poses: 0 at rest (doors shut, lamps off), 1 at work (doors open, wheels turned, lamps on).</summary>
        public static Pose BoardPoseOf(int pose, bool outside) => new Pose
        {
            Rotation = quaternion.identity, SteerDeg = pose == 1 ? 120f : 0f, SpeedKmh = pose == 1 ? 45f : 0f, Rpm = pose == 1 ? 1700f : 800f,
            Outside = outside, WheelSteerDeg = pose == 1 ? 25f : 0f, WheelRoll = pose == 1 ? 1.3f : 0f, Kneel = quaternion.identity,
            StrapSwing = pose == 1 ? new float2(0.35f, 0.2f) : float2.zero,
            Lamps = pose == 1 ? ExteriorLamps.Head | ExteriorLamps.Tail | ExteriorLamps.Brake | ExteriorLamps.Right : ExteriorLamps.None,
            DoorOpen = pose == 1 ? new float3(1f) : float3.zero, FourthDoorOpen = pose == 1 ? 1f : 0f,
            Lights = new KitLights { Lamps = pose == 1, Tail = pose == 1, Beacon = (byte)(pose == 1 ? 1 : 0) },
        };
    }
}
