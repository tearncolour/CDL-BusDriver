using System;
using Unity.Mathematics;

// MeshKit shared value types (pure: Unity.Mathematics + System only, no UnityEngine). Read Src/Core/Kit/README.md first.
namespace CDLC.BusDriver.Kit
{
    // Finish = one shared HDRP/Lit material = one submesh. A baked mesh has one submesh per finish it uses, in this order.
    public enum Fin : byte
    {
        Satin,      // paints and plastics: metallic 0, smoothness 0.5, palette
        Matte,      // rubber, timber, net, dark plastics: 0 / 0.2, palette
        Metal,      // galvanised, steel, rope, rail, rust: 0.8 / 0.4, palette
        Chrome,     // piston rods, sheave faces: 1 / 0.85, flat colour
        Glass,      // opaque dielectric: metallic 0 (F0 0.04), smoothness 0.975; palette rows Glass / GlassTinted, flat ramp
        Concrete,   // precast, fresh concrete: 0 / 0.18, palette
        Hazard,     // black / yellow 45 degree stripes, runtime texture, metric UVs
        Lamp,       // white head / work lamps: emissive on / off
        Tail,       // red tail lamps: emissive on / off
        Beacon,     // amber rotating beacon: off or one of KitLights.BeaconPhases shared flash phases
        Aviation,   // red aviation light: off, steady or flashing
        NavGreen,   // green navigation sidelight (starboard; port = Tail red): on / off with KitLights.Tail (no code bits of its own)
        Glow,       // Bus Driver: always-lit display (LED destination / next-stop panels, dashboard screens), flat colour of the swatch
        BrushedMetal, // authored stainless/aluminium: 1 / 0.75, palette; appended to preserve existing finish IDs
        Textile,      // authored cloth and printed cloth: 0 / 0.32, palette
        MouldedPlastic, // authored hard seat shells/buttons: 0 / 0.72, palette
        Count,
    }

    // Swatch = palette row (colour + finish). Pick the swatch with MeshKit.Paint(sw); the finish follows from the swatch
    // (KitPalette.FinOf). Rows are shared by every palette texture, so the UV of a vertex never depends on its finish.
    public enum Sw : byte
    {
        Yellow, Orange, Red, Blue, White, Green, Grey, Black, DarkGrey,       // paints (Satin; DarkGrey = dark plastic, Matte)
        Rubber, Galvanised, Steel, Rope, Rail, Rust,                          // Rubber Matte, the rest Metal
        Precast, FreshConcrete,                                               // Concrete
        Plywood, Timber, Net,                                                 // Matte
        Chrome, Glass, Hazard,                                                // Chrome flat colour, Glass (flat-ramp palette rows), stripes
        LampWhite, LampRed, Amber, AviationRed,                               // emissive finishes (Lamp, Tail, Beacon, Aviation)
        Asphalt, FreshAsphalt, Membrane, Soil, Gravel, Sand, Mud,             // Matte ground / deck materials (wave 2 rows, appended)
        SteelPrimer, SteelGrey, SafetyWhite,                                  // Satin: red-brown primer, bridge steel grey, hoarding white
        GlassTinted,                                                          // Glass: darker solar glass (operator cabs, site offices)
        LampGreen,                                                            // NavGreen: starboard navigation sidelight lens
        // ---- Bus Driver interior rows (appended)
        Fabric, FabricAccent, FloorVinyl, Panel, DashBlack, Screen,           // Matte: seat moquette, floor, wall / ceiling panels, dash, dark screens
        LedAmber,                                                             // Glow: LED displays
        SkinLight, SkinMid, SkinDark, Denim, Khaki, HairBrown,                // Matte: the passengers (skin tones, jeans, chinos, hair)
        HairBlack, HairBlonde, Navy, Pink, Burgundy, Beige, ShirtBlue, Lip,   // Matte: more hair and clothes, lips
        BrushedStainless, SeatFabricGrey, SeatPrintYellow, SeatPrintGrey, SeatShellGrey,
        HandGripGrey, CurtainBlueGrey, CabinSilver, CabinPanelGrey, CurtainClipGrey,
        CabinRecessGrey, DoorFeetGrey, RequestAmber, RequestDark, RequestRed, RequestYellow,
        AccessibilityGreen, AccessibilityWhite, GasketGrey, // authored 6109; append only, never renumber existing resources
        RearPrintYellow, RearPrintGrey, // same ink colours on the smooth hard rear shell
        CabinGraphite, CharcoalVinyl, // explicit base cabin plastics and floor; append-only
        ShanghaiAqua, // 6109 fixed-colour stripes; native paint retains its own metallic parameter
        FloorDarkBlue, SeatMouldedYellow, // explicit generic vinyl and moulded-seat finishes; append only
        RedGloss, YellowGloss, BlueGloss, WhiteGloss, // the paints Red/Yellow/Blue/White in the glossy dielectric finish (MouldedPlastic); WhiteGloss is also the glossy livery white
        Count,
    }

    // Fake ambient occlusion rules applied per vertex (MeshKit.Shade). Flags: Hemi and Height combine.
    [Flags]
    public enum ShadeRule : byte
    {
        Flat = 0,     // no automatic darkening (only explicit occlusion)
        Hemi = 1,     // undersides up to 22 % darker (default)
        Height = 2,   // darker towards the ground band y0..y1 (model space, y = 0 is the ground)
        Both = 3,
    }

    // End caps of extrusions, cylinders, frusta, tubes, sweeps: A = start, B = end.
    [Flags]
    public enum Caps : byte { None = 0, A = 1, B = 2, Both = 3 }

    // Faces of Box (bit per face). Hidden faces against other geometry can be dropped.
    [Flags]
    public enum Faces : byte
    {
        None = 0, XNeg = 1, XPos = 2, YNeg = 4, YPos = 8, ZNeg = 16, ZPos = 32,
        All = 63, NoBottom = All & ~YNeg, NoTop = All & ~YPos, Sides = XNeg | XPos | ZNeg | ZPos,
    }

    // Cross-section of a lattice member (Member / Lattice). LOD1+ always draws Box (4 quads).
    public enum Prof : byte { Box, Tube, Angle, Channel, IBeam }

    // Lattice cross-section. Chord order (local right x, up y): Rect 0 (-,-) 1 (+,-) 2 (+,+) 3 (-,+);
    // TriApexUp 0 (-,-) 1 (+,-) 2 (0,+); TriApexDown 0 (-,+) 1 (+,+) 2 (0,-). Face f lies between chord f and chord f+1.
    public enum LatSection : byte { Rect, TriApexUp, TriApexDown }

    public enum Lacing : byte { Warren, WarrenPosts, Pratt, X }

    // Part flags (KitPartList.Add).
    [Flags]
    public enum PartFlags : byte
    {
        None = 0,
        Details = 1,       // thin details (ropes, handrails, ladders, mirror arms): shadows off
        ShadowProxy = 2,   // heavy lattice: LOD0 casts no shadow itself; a ShadowsOnly child renderer casts from the LOD1 mesh
        Static = 4,        // the whole instance never moves in the world (structures): camera-only motion vectors (MotionVectorGenerationMode.Camera).
                           // Never on machines: a part that is fixed to a moving chassis still needs object motion vectors.
        Alternate = 8,     // one of a group of mutually exclusive parts (track phase meshes 1-3, formwork states): the model shows one at a time
                           // with Show(); renderer and triangle totals (budgets) skip Alternate parts, so a group counts once
    }

    // Model variant = material set (KitMaterials). Decal receiver mask and render queue follow from it.
    public enum KitVariant : byte
    {
        Machine = 0,       // colossal_DecalLayerMask = Vehicles, queue from ValidateMaterial
        DeckMachine = 1,   // Vehicles, render queue 2450 (drives over raised RRW surfaces: the roller rule)
        Structure = 2,     // Other (the vanilla object default), queue from ValidateMaterial
        Count,
    }

    // Light state of an instance (KitInstance.SetLights). default(KitLights) = everything off.
    public struct KitLights : IEquatable<KitLights>
    {
        public const int BeaconPhases = 4;
        public bool Lamps, Tail;
        public byte Beacon;            // 0 = off, 1..BeaconPhases = flashing with shared phase Beacon - 1 (pick by seed: Flash(seed))
        public KitAviation Aviation;

        public static KitLights Off => default;

        public static byte Flash(int seed) => (byte)(1 + (uint)seed % BeaconPhases);

        public static KitLights Working(int seed) => new KitLights { Lamps = true, Tail = true, Beacon = Flash(seed) };

        // Dense code (0 .. Codes-1) for material-set caches.
        public readonly int Code => (Lamps ? 1 : 0) | (Tail ? 2 : 0) | (math.min((int)Beacon, BeaconPhases) << 2) | ((int)Aviation << 5);
        public const int Codes = 1 << 7;

        // Code reduced to the light finishes present in a submesh layout (bit per Fin): a part without lights keeps one material
        // array (its renderer is never rewritten) whatever the lights do; a part with head lamps only ignores the beacon phase.
        public readonly int CodeFor(ushort finMask)
        {
            int c = 0;
            if ((finMask & (1 << (int)Fin.Lamp)) != 0 && Lamps) c |= 1;
            if ((finMask & (1 << (int)Fin.Tail | 1 << (int)Fin.NavGreen)) != 0 && Tail) c |= 2;   // the green sidelight burns with the tail
            if ((finMask & (1 << (int)Fin.Beacon)) != 0) c |= math.min((int)Beacon, BeaconPhases) << 2;
            if ((finMask & (1 << (int)Fin.Aviation)) != 0) c |= (int)Aviation << 5;
            return c;
        }

        public readonly bool Equals(KitLights o) => Code == o.Code;
        public override readonly bool Equals(object o) => o is KitLights l && Equals(l);
        public override readonly int GetHashCode() => Code;
    }

    public enum KitAviation : byte { Off = 0, Steady = 1, Flash = 2 }

    // Axis-aligned bounds (pure; KitUploader converts to UnityEngine.Bounds). Empty = Min > Max.
    public struct KitBounds
    {
        public float3 Min, Max;

        public static KitBounds Empty => new KitBounds { Min = new float3(float.MaxValue), Max = new float3(float.MinValue) };
        public readonly bool IsEmpty => Min.x > Max.x;
        public readonly float3 Center => IsEmpty ? float3.zero : 0.5f * (Min + Max);
        public readonly float3 Size => IsEmpty ? float3.zero : Max - Min;
        public readonly float Radius => IsEmpty ? 0f : 0.5f * math.length(Max - Min);

        public void Add(float3 p) { Min = math.min(Min, p); Max = math.max(Max, p); }

        public void Add(in KitBounds b)
        {
            if (b.IsEmpty) return;
            Min = math.min(Min, b.Min);
            Max = math.max(Max, b.Max);
        }

        // Bounds of this box after an affine transform (all 8 corners).
        public readonly KitBounds Transformed(in float4x4 m)
        {
            var r = Empty;
            if (IsEmpty) return r;
            for (int k = 0; k < 8; k++)
                r.Add(math.transform(m, new float3((k & 1) != 0 ? Max.x : Min.x, (k & 2) != 0 ? Max.y : Min.y, (k & 4) != 0 ? Max.z : Min.z)));
            return r;
        }
    }

    // Growable array reused between builds (Clear keeps the capacity). Not thread safe; one per builder.
    public sealed class KList<T> where T : struct
    {
        public T[] A;
        public int Count;

        public KList(int capacity) { A = new T[math.max(4, capacity)]; }

        public void Add(in T v)
        {
            if (Count == A.Length) Array.Resize(ref A, A.Length * 2);
            A[Count++] = v;
        }

        public void Clear() => Count = 0;

        public T[] ToArray()
        {
            var r = new T[Count];
            Array.Copy(A, r, Count);
            return r;
        }
    }

    // Lathe profile point (r = radius from the axis, y = height along the axis). Crease = hard edge at this point;
    // Occ < 1 darkens it (crevices: inside rims, under flanges).
    public struct LP
    {
        public float R, Y, Occ;
        public bool Crease;

        public LP(float r, float y, bool crease = false, float occ = 1f) { R = r; Y = y; Crease = crease; Occ = occ; }
    }

    // Wheel: tyre (lathe) + rim + hub; tread blocks and bolts at LOD0. The wheel axis is local X; Side = +1 when the outer face
    // (rim dish, bolts) looks towards +X.
    public struct WheelSpec
    {
        public float Radius, Width;       // tyre outer radius and section width (12R22.5: 0.525 / 0.30)
        public float RimRadius;           // rim flange radius (22.5": ~0.29)
        public int Lugs;                  // tread blocks around the tyre at LOD0 (0 = road tyre; ~20 for off-road machines, +200 tris)
        public int Bolts;                 // wheel nuts at LOD0 (0 = none)
        public bool Dual;                 // twin tyres (inner + outer) on one hub, DualGap apart (centre to centre)
        public float DualGap;
        public Sw Tyre, Rim;              // default Rubber / Steel

        public static WheelSpec Truck(float radius = 0.525f, float width = 0.30f, bool dual = false) =>
            new WheelSpec { Radius = radius, Width = width, RimRadius = 0.29f, Lugs = 0, Bolts = 8, Dual = dual, DualGap = 0.32f, Tyre = Sw.Rubber, Rim = Sw.Steel };
    }

    // Crawler track loop in the (z, y) plane, centred on x = 0, ground contact at y = 0: sprocket at z = -Length/2 (rear), idler
    // at +Length/2 (front). Phase 0..1 shifts every shoe by phase x Pitch along the loop (ground run towards +Z): 4 phase meshes
    // give a moving track for free; KitAnim.TrackPhase picks the mesh so the ground run moves backwards when driving forwards.
    public struct TrackSpec
    {
        public float Length;              // sprocket centre to idler centre
        public float SprocketR, IdlerR;   // wheel radii (the loop runs ShoeH outside them)
        public float Pitch;               // shoe pitch along the loop
        public float ShoeW, ShoeH;        // shoe width (x) and plate thickness
        public int Rollers;               // bottom rollers between the wheels (LOD0)

        public static TrackSpec Crawler(float length = 5.0f, float shoeW = 0.8f) =>
            new TrackSpec { Length = length, SprocketR = 0.42f, IdlerR = 0.40f, Pitch = 0.22f, ShoeW = shoeW, ShoeH = 0.06f, Rollers = 6 };
    }

    // Lattice (truss) between A and B. Up orients the section; Size = (width, height) at A and B (taper).
    public struct LatticeSpec
    {
        public float3 A, B, Up;
        public float2 SizeA, SizeB;
        public LatSection Section;
        public float Panel;               // target panel length (m); the panel count is rounded
        public Lacing Pattern;
        public byte Faces;                // bit f = face f carries lacing (see LatSection for the face order)
        public Prof Chord, Lace;
        public float2 ChordSize, LaceSize;
        public float JointEvery;          // > 0: pin-joint lug plates every N m (LOD0)
        public bool Gussets;              // node plates (LOD0)
        public bool SolidFar;             // LOD2: a dark solid of the mean section instead of the chords (short lattices: mast
                                          // sections, gantry legs, pylons of a few metres)
    }
}
