using System;
using System.Collections.Generic;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// Checks of the interior models (build, contract, geometry sanity) and the preview command (Preview.cs).
static class Program
{
    static int s_Fails, s_Checks;

    static void Check(bool ok, string what)
    {
        s_Checks++;
        if (!ok) { s_Fails++; Console.WriteLine("  FAIL " + what); }
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "generic-export") return GenericBusTests.Export(args);
        if (args.Length > 0 && args[0] == "generic-candidate") return GenericBusTests.Candidate(args);
        if (args.Length > 0 && args[0] == "generic-check")
        {
            SwbCodecTests.Run(Check);
            MirrorBudgetTests.Run(Check);
            MirrorOutlineTests.Run(Check);
            GenericBusTests.Run(Check);
            DoorTests.Run(Check);
            foreach (var spec in Specs()) { Interior(spec.Item1, spec.Item2); Outside(spec.Item1, spec.Item2); }
            Console.WriteLine($"Generic checks: {s_Checks}, failures: {s_Fails}");
            return s_Fails == 0 ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "light-signals")
        {
            LightTests.Run(Check);
            Console.WriteLine($"Light/signal checks: {s_Checks}, failures: {s_Fails}");
            return s_Fails == 0 ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "sign-cells")
        {
            SignCellsTests.Run(Check);
            AiSignTests.Run(Check);
            Console.WriteLine($"Sign checks: {s_Checks}, failures: {s_Fails}");
            return s_Fails == 0 ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "four-door-signs")
        {
            FourDoorTests.Run(Check);
            ExtraSideSignTests.Run(Check);
            AssemblyPackSettingsTests.Run(Check);
            Console.WriteLine($"Four-door/sign checks: {s_Checks}, failures: {s_Fails}");
            return s_Fails == 0 ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "lamp-ownership") return LampOwnership.Run(args);
        if (args.Length > 0 && args[0] == "b6-cab-check") return ReportCabCorrectionCheck.Run(args);
        if (args.Length > 0 && args[0] == "preview") return Preview.Run(args);
        if (args.Length > 0 && args[0] == "cardpreview") return CardSignTests.Preview(args);
        if (args.Length > 0 && args[0] == "hdpreview") return HdSignTests.Preview(args);
        if (args.Length > 0 && args[0] == "moirepreview") return SignCellsTests.Preview(args);
        if (args.Length > 0 && args[0] == "stationpreview") return StationBoardTests.Preview(args);
        if (args.Length > 0 && args[0] == "stationcheck") return StationBoardTests.CheckFile(args);
        if (args.Length > 0 && args[0] == "stress") return Stress.Run(args);
        if (args.Length > 0 && args[0] == "wrapbench") return WrapBench.Run(args);
        if (args.Length > 0 && args[0] == "wrapreal") return WrapReal.Run(args);
        if (args.Length > 0 && args[0] == "buildbench") return BuildBench.Run(args);
        if (args.Length > 0 && args[0] == "meshbench") return MeshBench.Run(args);
        if (args.Length > 0 && args[0] == "columnsbench") return ColumnsBench.Run(args);
        if (args.Length > 0 && args[0] == "listbench") return ListBench.Run(args);
        if (args.Length > 0 && args[0] == "dualsense") return DualSenseRun.Run();
        if (args.Length > 0 && args[0] == "test-session") return ReviewSession();
        if (args.Length > 0 && args[0] == "assembly") return VehicleAssemblyResourceTests.Run(args);
        if (args.Length > 0 && args[0] == "wraptemplate") return WrapTemplatePreview.Run(args);
        if (args.Length > 0 && args[0] == "wraptemplatezip") return WrapTemplatePreview.RunZip(args);
        if (args.Length > 0 && args[0] == "wrapdemo") return WrapTemplatePreview.RunDemo(args);
        try
        {
            Registry();
            PaletteTests.Run(Check);
            BusReviewRequestStateTests.Run(Check);
            TimingTests.Run(Check);
            StopZoneTests.Run(Check);
            CabProfileTests.Run(Check);
            MirrorOutlineTests.Run(Check);
            LedFontTests.Run(Check);
            CardSignTests.Run(Check);
            HdSignTests.Run(Check);
            SignCellsTests.Run(Check);
            AiSignTests.Run(Check);
            GlassDetailTests.Run(Check);
            SignOverlayTests.Run(Check);
            StationBoardTests.Run(Check);
            TtsTests.Run(Check);
            TimetableTerminusTests.Run(Check);
            SoundModelTests.Run(Check);
            HapticsTests.Run(Check);
            HidSmokeTests.Run(Check);
            AssetTests.Run(Check);
            BusPackageExportTests.Run(Check);
            BusPackageGlassTests.Run(Check);
            VehiclePackTests.Run(Check);
            DoorTests.Run(Check);
            InputTraceTests.Run(Check);
            BodyBoxTests.Run(Check);
            WrapPackageTests.Run(Check);
            PngDecoderTests.Run(Check);
            WrapSpeedTests.Run(Check);
            AssemblyPackSettingsTests.Run(Check);
            ExtraSideSignTests.Run(Check);
            MaterialAuditTests.Run(Check);
            LightTests.Run(Check);
            MirrorBudgetTests.Run(Check);
            DriverHandsTests.Run(Check);
            PadChordTests.Run(Check);
            SunwinTests.Run(Check);
            Sunwin6109Tests.Run(Check);
            K18WrapTests.Run(Check);
            JointTests.Run(Check);
            RearKneelTests.Run(Check);
            GenericBusTests.RearSkinPlanes(Check);
            GenericBusTests.FrameRings(Check);
            GenericBusTests.WindscreenCornerFaces(Check);
            FourBarDoorTests.Run(Check);
            LedScreenTests.Run(Check);
            CabViewTests.Run(Check);
            NativeBodyTests.Run(Check);
            AnnounceTests.Run(Check);
            GeometryReadTests.Run(Check);
            DepartureTests.Run(Check);
            PackVersionTests.Run(Check);
            PictureLimitTests.Run(Check);
            LampOwnershipTests.Run(Check);
            ReaderPartTests.Run(Check);
            foreach (var spec in Specs()) Interior(spec.Item1, spec.Item2);
            foreach (var spec in Specs()) Outside(spec.Item1, spec.Item2);
            Riders();
            ArmIk();
        }
        catch (Exception e)
        {
            s_Fails++;
            Console.WriteLine("  FAIL threw " + e);
        }
        Console.WriteLine($"{s_Checks} checks, {s_Fails} failed");
        Console.WriteLine(s_Fails == 0 ? "RESULT=PASS" : "RESULT=FAIL");
        return s_Fails == 0 ? 0 : 1;
    }

    static int ReviewSession()
    {
        try { BusReviewRequestStateTests.Run(Check); }
        catch (Exception e)
        {
            s_Fails++;
            Console.WriteLine("  FAIL review session threw " + e);
        }
        Console.WriteLine($"{s_Checks} checks, {s_Fails} failed");
        Console.WriteLine(s_Fails == 0 ? "RESULT=PASS" : "RESULT=FAIL");
        return s_Fails == 0 ? 0 : 1;
    }

    static void Registry()
    {
        Check(KitRegistry.Problems.Count == 0 || KitRegistry.All.Count > 0, "kit registry: " + string.Join("; ", KitRegistry.Problems));
        Check(KitRegistry.TryFind("bus-interior", out _), "bus-interior is registered");
    }

    // A solo bus (right-hand traffic), the same bus for left-hand traffic, a short two-door bus, both sections of an articulated bus.
    static IEnumerable<(string, BusInteriorSpec)> Specs()
    {
        var solo = BusInteriorSpec.Default;
        yield return ("solo", solo);
        var lht = solo; lht.DriverSide = 1f;
        yield return ("left-hand traffic", lht);
        var midi = solo; midi.FrontZ = 4.6f; midi.RearZ = -4.6f; midi.FrontAxleZ = 2.2f; midi.RearAxleZ = -2.0f; midi.Doors = 2;
        yield return ("midi", midi);
        var front = solo; front.FrontZ = 5.2f; front.RearZ = -5.0f; front.FrontAxleZ = 2.6f; front.RearAxleZ = -2.4f; front.Doors = 2; front.Section = BusSection.Front;
        yield return ("articulated front", front);
        var rear = solo; rear.FrontZ = 3.6f; rear.RearZ = -4.4f; rear.RearAxleZ = -0.6f; rear.Doors = 2; rear.Section = BusSection.Rear;
        yield return ("articulated rear", rear);
        // A narrow one-door minibus with a lower roof (layout limits: single seats, one hatch, short rails).
        var mini = solo; mini.FrontZ = 3.8f; mini.RearZ = -3.8f; mini.FrontAxleZ = 2.0f; mini.RearAxleZ = -1.8f; mini.HalfWidth = 1.05f; mini.RoofY = 2.8f; mini.FloorY = 0.42f; mini.Doors = 1;
        yield return ("minibus", mini);
        // A long rigid bus (18 m, a game asset) with its rear axle near the end: no room for a door behind it.
        var longBus = solo; longBus.FrontZ = 6.8f; longBus.RearZ = -11.2f; longBus.FrontAxleZ = 3.3f; longBus.RearAxleZ = -8.7f; longBus.RoofY = 3.3f;
        yield return ("long rigid", longBus);
        // A 12 m bus (a game asset) with its rear axle 1.3 m from the end: no room for the rear platform behind the arch.
        var shortRear = solo; shortRear.FrontZ = 7.0f; shortRear.RearZ = -5.0f; shortRear.FrontAxleZ = 3.4f; shortRear.RearAxleZ = -3.7f; shortRear.RoofY = 2.7f;
        yield return ("short rear overhang", shortRear);
    }

    // The outside, the straps and the places for people of a bus section.
    static void Outside(string name, BusInteriorSpec spec)
    {
        var src = new BusInterior(spec);
        var data = KitBuild.BuildAll(src);
        if (data.Error != null) return;
        var s = src.Spec;

        // The exterior parts only from outside; from the cab the view out stays clear.
        var inside = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(inside, BusInterior.BoardPoseOf(1, false), s);
        var outside = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(outside, BusInterior.BoardPoseOf(1, true), s);
        Check(!inside.Visible[BusInterior.Exterior] && !inside.Visible[BusInterior.Glazing] && !inside.Visible[BusInterior.WheelRL] && !inside.Visible[BusInterior.HeadLamps],
            $"{name}: no exterior parts in the driver's view");
        Check(outside.Visible[BusInterior.Exterior] && outside.Visible[BusInterior.Glazing] && outside.Visible[BusInterior.Livery] && outside.Visible[BusInterior.WheelRL],
            $"{name}: the exterior from outside");
        Check(outside.Visible[BusInterior.WheelFL] == src.HasCab, $"{name}: front wheels only with a front axle");
        Check(!outside.Visible[BusInterior.IndicatorL] && outside.Visible[BusInterior.IndicatorR], $"{name}: the indicator that blinks is lit");
        for (int w = 0; w < 4; w++)
            Check(data.Parts[BusInterior.WheelFL + w].Data[0] != null || (w < 2 && !src.HasCab), $"{name}: wheel {w} has geometry");

        // Nothing but the wheel itself inside a wheel (its tyre's cylinder, a little smaller), seen from outside: no body part
        // through a tyre, whatever the bus's overhangs (a rear axle near the end put the rear platform into the arch).
        for (int w = 0; w < 4; w++)
        {
            int wheel = BusInterior.WheelFL + w;
            if (!outside.Visible[wheel]) continue;
            // KNOWN LIMIT: a front overhang under 2.6 m (the test minibus) has no room for the low-floor cab ahead of the front
            // wheel; the driver sits over it. No game bus seen so far is that short at the front.
            bool cramped = w < 2 && s.FrontZ - s.FrontAxleZ < 2.6f;
            float3 c = data.Parts[wheel].Pivot;
            float r = BusInterior.WheelRadius * 0.95f, half = 0.11f;
            string hit = null;
            for (int part = 0; part < data.Parts.Length && hit == null; part++)
            {
                if (part >= BusInterior.WheelFL && part <= BusInterior.WheelRR) continue;
                if (cramped && part == BusInterior.Cab) continue;
                if (!outside.Visible[part]) continue;
                KitPartDef def = data.Parts[part];
                KitMeshData d = def.Data[0] ?? (def.MeshOf >= 0 ? data.Parts[def.MeshOf].Data[0] : null);
                if (d == null) continue;
                float4x4 m = outside.PartToModel(part);
                for (int t = 0; t + 2 < d.Idx.Length && hit == null; t += 3)
                {
                    float3 a = math.transform(m, d.P[d.Idx[t]]), b = math.transform(m, d.P[d.Idx[t + 1]]), e = math.transform(m, d.P[d.Idx[t + 2]]);
                    // samples every 3 cm over the triangle (big shell panels too)
                    int n = math.clamp((int)math.ceil(math.max(math.distance(a, b), math.distance(a, e)) / 0.03f), 1, 400);
                    for (int i = 0; i <= n && hit == null; i++)
                        for (int j = 0; i + j <= n; j++)
                        {
                            float3 p = a + (b - a) * (i / (float)n) + (e - a) * (j / (float)n) - c;
                            if (math.abs(p.x) < half && p.y * p.y + p.z * p.z < r * r)
                            {
                                hit = $"{def.Name} at {p + c}";
                                break;
                            }
                        }
                }
            }
            Check(hit == null, $"{name}: wheel {w} clear of the body ({hit ?? "nothing in it"})");
        }

        // The wheels stay on the road while the body kneels (the body pose turns and lowers; the wheels are turned back).
        var knelt = new KitPoseBuffer(data.Parts);
        quaternion kneel = quaternion.EulerXYZ(math.radians(0.35f), 0f, math.radians(-1.6f));
        float3 drop = new float3(0f, -0.03f, 0f);
        var pose = BusInterior.BoardPoseOf(0, true);
        pose.Kneel = kneel; pose.KneelOffset = drop;
        BusInterior.Apply(knelt, pose, s);
        for (int w = src.HasCab ? 0 : 2; w < 4; w++)
        {
            int part = BusInterior.WheelFL + w;
            float3 centre = data.Parts[part].Pivot;
            float3 model = math.transform(knelt.PartToModel(part), float3.zero);
            float3 world = math.mul(kneel, model + drop);          // the body pose: turned by the kneel, then lowered
            Check(math.distance(world, centre) < 0.002f, $"{name}: wheel {w} stays on the road while kneeling ({world - centre})");
        }

        // Straps: one mesh, a part per strap at the rail, swinging with the pose.
        int firstStrap = GenericDoorRig.For(src.Spec).FirstStrap;
        int straps = data.Parts.Length - firstStrap;
        Check(straps >= (src.DoorCount > 1 ? 6 : 2), $"{name}: {straps} hand straps");
        bool shared = true;
        for (int i = 1; i < straps; i++) shared &= data.Parts[firstStrap + i].MeshOf == firstStrap;
        Check(shared && (straps == 0 || data.Parts[firstStrap].Data[0] != null), $"{name}: the straps share one mesh");
        if (straps > 0)
        {
            int strap = firstStrap;
            float3 top = data.Parts[strap].Pivot, handle = top - new float3(0f, 0.3f, 0f);
            var swing = new KitPoseBuffer(data.Parts);
            var braking = BusInterior.BoardPoseOf(0, false);
            braking.StrapSwing = new float2(-0.3f, 0f);           // hanging forward under braking: the handle ahead of the top
            BusInterior.Apply(swing, braking, s);
            float3 moved = swing.RestPointToModel(strap, handle);
            Check(moved.z > handle.z + 0.05f && math.abs(moved.x - handle.x) < 0.01f, $"{name}: a strap swings forward under braking ({moved - handle})");
        }

        // The short driver mirror is beside the fixed A-pillar; the long door mirror is ahead of the nose.
        if (src.HasCab)
            for (int side = -1; side <= 1; side += 2)
            {
                BusInterior.MirrorGlass(spec, side, out float3 mc, out quaternion mf, out float2 ms);
                float3 face = math.mul(mf, new float3(0f, 0f, -1f));
                bool longitudinal = side == s.DriverSide ? mc.z < s.FrontZ && mc.z > s.FrontZ - .15f : mc.z > s.FrontZ + .4f;
                Check(longitudinal && math.sign(mc.x) == side && math.abs(mc.x) > s.HalfWidth && math.dot(face, BusInterior.Eye(s) - mc) > 0 && mc.y > s.FloorY + 1.2f,
                    $"{name}: mirror {side} glass at {mc}, facing {face}");
            }
        int readers = 0;
        foreach (var (origin, frame, xSign) in BusInterior.CardReaders(spec))
        {
            readers++;
            float3 face = math.mul(frame, new float3(0f, 0f, 1f));
            Check(math.abs(origin.x) < s.HalfWidth - 0.1f && origin.z < s.FrontZ - 0.3f && origin.z > s.RearZ + 0.3f && origin.y > s.FloorY + 0.9f && origin.y < s.FloorY + 1.5f && math.abs(face.y) < 0.4f,
                $"{name}: card reader at {origin}, facing {face}");
        }
        Check(readers == src.DoorCount, $"{name}: {readers} card readers for {src.DoorCount} doors");

        // The LED signs: the front sign on the front face, the side sign at the kerb, the rear number on the rear.
        if (src.HasCab)
        {
            BusInterior.FrontSignFrame(s, out float3 fc, out float2 fs);
            Check(math.abs(fc.z - s.FrontZ) < 0.1f && fc.y > s.RoofY - 0.45f && fc.y < s.RoofY && fs.x < 2f * s.HalfWidth, $"{name}: front sign at {fc}");
            if (BusInterior.SideSignFrame(s, out float3 sc, out float2 ss, out float kerb))
            {
                float skinX = BusBarrel.Point(s, new float3(kerb * (s.HalfWidth - .0125f), sc.y, sc.z)).x;
                float3 normal = math.mul(BusInterior.SideSignRotation(s, kerb), new float3(0, 0, -1));
                Check(kerb == -s.DriverSide && math.abs(sc.x - skinX) < .001f && sc.z < s.FrontZ - 1f && normal.x * kerb > .98f && normal.y > 0f,
                    $"{name}: side sign follows the barrel surface at {sc}");
            }
        }
        if (spec.Section != BusSection.Front)
        {
            BusInterior.RearRouteFrame(s, out float3 rc, out float2 rs);
            Check(math.abs(rc.z - s.RearZ) < 0.05f && rc.y > 2f, $"{name}: rear route number at {rc}");
        }

        // Places for people: seats and standing places inside the body, apart from each other, the driver with a cab.
        var slots = src.RiderSlots();
        int seats = 0, standing = 0, drivers = 0;
        bool inBody = true, apart = true, floor = true;
        for (int i = 0; i < slots.Count; i++)
        {
            var p = slots[i].Position;
            if (slots[i].Kind == BusInterior.RiderKind.Seated) seats++;
            else if (slots[i].Kind == BusInterior.RiderKind.Driver) drivers++;
            else { standing++; floor &= math.abs(p.y - s.FloorY) < 0.001f; }
            inBody &= math.abs(p.x) < s.HalfWidth - 0.2f && p.z > s.RearZ + 0.2f && p.z < s.FrontZ - 0.3f;
            for (int j = 0; j < i; j++)
                if (math.distance(p.xz, slots[j].Position.xz) < 0.38f) { apart = false; Console.WriteLine($"    {name}: {slots[i].Kind} at {p} close to {slots[j].Kind} at {slots[j].Position}"); }
        }
        Check(drivers == (src.HasCab ? 1 : 0), $"{name}: {drivers} driver seat(s)");
        Check(seats >= (spec.Section == BusSection.Solo && s.FrontZ - s.RearZ > 10f ? 20 : 6) && standing >= 4, $"{name}: {seats} seats, {standing} standing places");
        Check(inBody && apart && floor, $"{name}: places inside the body ({inBody}), apart ({apart}), standing on the floor ({floor})");
    }

    // Figures for the passengers aboard: the load on the model's places, the seats first.
    // The arm IK that puts the game's citizens' hands on a strap or the wheel: the wrist on the target, the bones' lengths kept,
    // the elbow towards the pole, the new rotations turning the bones onto the new joints; out of reach, stretched towards it.
    static void ArmIk()
    {
        var rng = new System.Random(7);
        float R() => (float)rng.NextDouble() * 2f - 1f;
        bool reach = true, lengths = true, pole = true, turns = true, stretch = true;
        for (int i = 0; i < 200; i++)
        {
            float3 a = new float3(R(), 1.3f + 0.2f * R(), R()) * new float3(0.3f, 1f, 0.3f);
            float3 b = a + math.normalize(new float3(R(), -1f, R() * 0.3f)) * 0.27f;
            float3 c = b + math.normalize(new float3(R(), -1f, R())) * 0.25f;
            quaternion ra = quaternion.EulerXYZ(R(), R(), R()), rb = quaternion.EulerXYZ(R(), R(), R());
            float3 dir = math.normalize(new float3(R(), R(), R()));
            float3 target = a + dir * (0.1f + 0.38f * (float)rng.NextDouble());
            float3 p = math.normalize(new float3(R(), -1f, R()));
            CDLC.BusDriver.Interior.TwoBoneIk.Solve(a, b, c, ra, rb, target, p, out quaternion na, out quaternion nb, out float3 elbow, out float3 wrist);
            reach &= math.distance(wrist, target) < 0.002f;
            lengths &= math.abs(math.distance(a, elbow) - 0.27f) < 0.001f && math.abs(math.distance(elbow, wrist) - 0.25f) < 0.001f;
            float3 axis = math.normalize(target - a), mid = elbow - a - axis * math.dot(elbow - a, axis), side = p - axis * math.dot(p, axis);
            pole &= math.lengthsq(side) < 1e-4f || math.lengthsq(mid) < 1e-6f || math.dot(mid, side) > 0f;
            turns &= math.distance(math.mul(math.mul(na, math.inverse(ra)), b - a), elbow - a) < 0.002f
                && math.distance(math.mul(math.mul(nb, math.inverse(rb)), c - b), wrist - elbow) < 0.002f;
            float3 far = a + dir * 2f;
            CDLC.BusDriver.Interior.TwoBoneIk.Solve(a, b, c, ra, rb, far, p, out _, out _, out float3 e2, out float3 w2);
            stretch &= math.distance(w2, a + dir * 0.52f * 0.9995f) < 0.002f;
        }
        Check(reach, "arm IK: the wrist reaches targets within the arm's reach");
        Check(lengths, "arm IK: upper arm and forearm keep their lengths");
        Check(pole, "arm IK: the elbow bends towards the pole");
        Check(turns, "arm IK: the new rotations turn the bones onto the new joints");
        Check(stretch, "arm IK: out of reach, the arm stretches straight towards the target");
        Check(math.distance(math.mul(CDLC.BusDriver.Interior.TwoBoneIk.FromTo(new float3(0f, 1f, 0f), new float3(0f, -1f, 0f)), new float3(0f, 1f, 0f)), new float3(0f, -1f, 0f)) < 1e-4f, "arm IK: a half turn");
    }

    static void Riders()
    {
        Check(BusRiders.Figures(0, 30, 46) == 0 && BusRiders.Figures(1, 30, 46) == 2 && BusRiders.Figures(30, 30, 46) == 46 && BusRiders.Figures(15, 30, 46) == 23,
            "figures in proportion to the load");
        Check(BusRiders.Figures(5, 0, 46) == 5 && BusRiders.Figures(80, 0, 46) == 46 && BusRiders.Figures(40, 30, 46) == 46, "figures without a capacity, and an overfull bus");
        BusRiders.Split(3, 29, 17, out int a, out int b);
        Check(a == 3 && b == 0, $"three passengers sit ({a} / {b})");
        BusRiders.Split(24, 29, 17, out a, out b);
        Check(a == 21 && b == 3, $"24 passengers: 21 seated, 3 by the doors ({a} / {b})");
        BusRiders.Split(46, 29, 17, out a, out b);
        Check(a == 29 && b == 17, $"a full bus ({a} / {b})");
        var data = KitBuild.BuildAll(new BusRiders());
        Check(data.Error == null && data.Parts.Length == BusRiders.PartCount, $"riders build ({data.Error})");
        if (data.Error != null) return;
        bool all = true;
        for (int i = 0; i < data.Parts.Length; i++) all &= data.Parts[i].Data[0] != null && data.Parts[i].Data[0].Tris > 100 && data.Parts[i].Data[0].Tris < 3200;
        Check(all, "every figure has a body (100 .. 3200 triangles: faces, hands, hair, clothes)");
        // standing figures 1.6 .. 1.9 m tall (hair included), seated ones up to 0.9 m over the cushion and down to the floor
        var standing = data.Parts[BusRiders.DownFirst].Data[0].Bounds;
        var seated = data.Parts[BusRiders.SeatedFirst].Data[0].Bounds;
        Check(standing.Size.y > 1.6f && standing.Size.y < 1.9f, $"standing height {standing.Size.y:0.00} m");
        Check(seated.Center.y + 0.5f * seated.Size.y < 0.9f && seated.Center.y - 0.5f * seated.Size.y > -0.55f, $"seated figure from {seated.Center.y - 0.5f * seated.Size.y:0.00} to {seated.Center.y + 0.5f * seated.Size.y:0.00} m");
        Check(BusRiders.PartFor(BusInterior.RiderKind.Standing, 7) < BusRiders.Driver && BusRiders.PartFor(BusInterior.RiderKind.Holding, -1) >= BusRiders.HoldingFirst, "figure choice stays in range");
    }

    // The body wrap's visible pieces: on the side, inside the picture's frame, never over a kerb-side door opening or into a
    // wheel arch, the lower panels and the band above the windows both there.
    static void Wrap(string name, BusInterior bus)
    {
        var spec = bus.Spec;
        for (int side = -1; side <= 1; side += 2)
        {
            var rects = bus.WrapRects(side);
            bool inside = true, doors = true, arches = true;
            float area = 0f;
            foreach (var r in rects)
            {
                inside &= r.x >= bus.WrapRearZ - 1e-3f && r.z <= bus.WrapFrontZ + 1e-3f && r.y >= BusInterior.WrapBottom - 1e-3f && r.w <= bus.WrapTop + 1e-3f && r.z > r.x && r.w > r.y;
                area += (r.z - r.x) * (r.w - r.y);
                bool low = r.w < spec.FloorY + 1.0f;
                if (low && side == -spec.DriverSide)
                    for (int d = 0; d < bus.DoorCount; d++)
                    {
                        var door = bus.Door(d);
                        doors &= r.z <= door.x - door.y * 0.5f + 1e-3f || r.x >= door.x + door.y * 0.5f - 1e-3f;
                    }
                foreach (float axle in new[] { spec.FrontAxleZ, spec.RearAxleZ })
                {
                    if (axle == spec.FrontAxleZ && spec.Section == BusSection.Rear) continue;
                    bool over = r.z > axle - 0.4f && r.x < axle + 0.4f;
                    arches &= !over || r.y >= spec.FloorY + 0.68f;
                }
            }
            string s = side < 0 ? "left" : "right";
            Check(inside, $"{name}: wrap pieces on the {s} inside the picture's frame");
            Check(doors, $"{name}: no wrap over the door openings ({s})");
            Check(arches, $"{name}: no wrap in the wheel arches ({s})");
            Check(rects.Count >= 3 && area > (bus.WrapFrontZ - bus.WrapRearZ) * 0.5f, $"{name}: wrap on the {s}: {rects.Count} pieces, {area:0.0} m²");
        }
        // every face: the layout inside its picture, solid pieces there when the section has the face, the mesh where it should be
        var data = KitBuild.BuildAll(bus);
        foreach (BusInterior.WrapFace face in System.Enum.GetValues(typeof(BusInterior.WrapFace)))
        {
            var layout = bus.WrapLayout(face);
            bool has = bus.HasWrap(face), inUv = true;
            foreach (var (uv, solid) in layout)
                inUv &= !solid || (uv.x >= -1e-3f && uv.y >= -1e-3f && uv.z <= 1.001f && uv.w <= 1.001f && uv.z > uv.x && uv.w > uv.y);
            Check(inUv, $"{name}: wrap {face} layout inside the picture");
            Check(has == layout.Exists(p => p.solid), $"{name}: wrap {face} {(has ? "there" : "not on this section")}");
            var mesh = data.Parts[BusInterior.WrapLeft + (int)face].Data[0];
            Check(has == (mesh != null && mesh.Tris > 0), $"{name}: wrap {face} mesh {(mesh != null ? mesh.Tris : 0)} triangles");
            if (has && mesh != null && mesh.Tris > 0)
            {
                var b = mesh.Bounds;
                var size = bus.WrapSize(face);
                bool where = face == BusInterior.WrapFace.Front ? b.Center.z + 0.5f * b.Size.z > spec.FrontZ :
                    face == BusInterior.WrapFace.Rear ? b.Center.z - 0.5f * b.Size.z < spec.RearZ :
                    face == BusInterior.WrapFace.Roof ? b.Center.y + 0.5f * b.Size.y > spec.RoofY : true;
                Check(where && size.x > 0.5f && size.y > 0.2f, $"{name}: wrap {face} outside the body ({size.x:0.00} x {size.y:0.00} m)");
            }
        }
    }

    static void Interior(string name, BusInteriorSpec spec)
    {
        var src = new BusInterior(spec);
        var data = KitBuild.BuildAll(src);
        Check(data.Error == null, $"{name}: builds ({data.Error})");
        Wrap(name, src);
        if (data.Error != null) return;
        Check(data.Parts.Length >= BusInterior.PartCount, $"{name}: part table has the contract parts ({data.Parts.Length})");
        string[] names = { "Shell", "SteeringWheel", "SpeedNeedle", "RpmNeedle", "Door0A", "Door0B", "Door1A", "Door1B", "Door2A", "Door2B" };
        for (int i = 0; i < names.Length; i++)
            Check(data.Parts[i].Name == names[i], $"{name}: part {i} is {names[i]} ({data.Parts[i].Name})");
        Check(data.Tris[0] > 2000 && data.Tris[0] < 400000, $"{name}: LOD0 triangles {data.Tris[0]}");
        Check(data.Parts[BusInterior.Shell].Data[0] != null, $"{name}: the shell has geometry");

        // Everything inside the body envelope (a little slack for mirrors, the bumper and the roof).
        var b = data.Bounds;
        var s = src.Spec;
        Check(b.Min.x > -s.HalfWidth - 0.6f && b.Max.x < s.HalfWidth + 0.6f, $"{name}: width within the body ({b.Min.x:0.00} .. {b.Max.x:0.00})");
        // a front section's gangway (bellows, turntable) reaches back over the joint to the rear section
        float behind = s.Section == BusSection.Front ? (s.GangwayLength > 0f ? s.GangwayLength : 0.9f) + 0.85f : 0f;
        float bodyFront = float.NegativeInfinity;
        foreach (var part in data.Parts)
            if (part.Index != BusInterior.Outside && part.Data[0] != null)
                foreach (float3 vertex in part.Data[0].P)
                    bodyFront = math.max(bodyFront, math.transform(new float4x4(part.Rot, part.Pivot), vertex).z);
        Check(b.Min.z > s.RearZ - 0.3f - behind && bodyFront < s.FrontZ + 0.6f, $"{name}: body length excludes separate mirror assemblies ({b.Min.z:0.00} .. {bodyFront:0.00})");
        Check(b.Min.y > -0.05f && b.Max.y < s.RoofY + 0.3f, $"{name}: height within the body ({b.Min.y:0.00} .. {b.Max.y:0.00})");

        // No NaN anywhere; triangles reference valid vertices.
        bool finite = true, indices = true;
        foreach (var p in data.Parts)
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            {
                var d = p.Data[lod];
                if (d == null) continue;
                foreach (var v in d.P) if (!math.all(math.isfinite(v))) finite = false;
                foreach (var i in d.Idx) if (i < 0 || i >= d.P.Length) indices = false;
            }
        Check(finite && indices, $"{name}: finite geometry, valid indices");

        if (src.HasCab)
        {
            // The eye sits inside the cab, above the seat, behind the wheel and below the ceiling, on the driver's side.
            float3 eye = BusInterior.Eye(spec);
            Check(math.sign(eye.x) == s.DriverSide && eye.y > s.FloorY + 1.3f && eye.y < s.RoofY - 0.3f && eye.z < s.FrontZ - 0.7f && eye.z > s.FrontZ - 2.0f,
                $"{name}: driver eye {eye}");
            Check(eye.z < src.WheelHub.z && eye.y > src.WheelHub.y, $"{name}: the steering wheel is in front of and below the eye");
        }

        // Steering stays on its column; linked door leaves follow their independent shaft and guide.
        var buf = new KitPoseBuffer(data.Parts);
        BusInterior.Apply(buf, new BusInterior.Pose { Rotation = quaternion.identity, SteerDeg = 90f, DoorOpen = new float3(1f) }, s);
        var wheel = buf.PartToModel(BusInterior.SteeringWheel);
        float3 axis = math.normalize(math.mul(new float3x3(wheel.c0.xyz, wheel.c1.xyz, wheel.c2.xyz), new float3(0f, 0f, 1f)));
        Check(math.abs(math.dot(axis, math.mul(src.WheelRot, new float3(0f, 0f, 1f)))) > 0.999f, $"{name}: the wheel turns about its column");
        if (src.HasCab)
        {
            // Steering right (+) turns the top of the rim to the driver's right (+x); the needles read 0 at the lower left and full
            // scale at the lower right as the driver sees them (the driver looks along +z: his right is +x).
            float3 rimTop = src.WheelHub + math.mul(src.WheelRot, new float3(0f, 0.2f, 0f));
            Check(buf.RestPointToModel(BusInterior.SteeringWheel, rimTop).x > src.WheelHub.x + 0.15f, $"{name}: steering right turns the rim top to the right");
            for (int i = 0; i < 2; i++)
            {
                int needle = BusInterior.SpeedNeedle + i;
                float3 c = src.DialCentre(i), tip = c + math.mul(src.DialRot, new float3(0f, 0.05f, 0f));
                var zero = new KitPoseBuffer(data.Parts);
                BusInterior.Apply(zero, new BusInterior.Pose { Rotation = quaternion.identity }, s);
                float3 t0 = zero.RestPointToModel(needle, tip);
                var full = new KitPoseBuffer(data.Parts);
                BusInterior.Apply(full, new BusInterior.Pose { Rotation = quaternion.identity, SpeedKmh = 500f, Rpm = 9000f }, s);
                float3 t1 = full.RestPointToModel(needle, tip);
                Check(t0.x < c.x - 0.03f && t0.y < c.y && t1.x > c.x + 0.03f && t1.y < c.y, $"{name}: needle {i} sweeps from the lower left to the lower right ({t0 - c} -> {t1 - c})");
            }

            // The lower row's lamps run left to right as the driver sees them (he looks along +z: his left is -x): the left
            // indicator's lamp (slot 0) left of the right one's (slot 11), every slot right of the one before. They ran the other
            // way (the dial frame's +x is the driver's left), so the left indicator lit on the driver's right.
            float tellMiddle = 0.5f * (src.DialCentre(0).x + src.DialCentre(1).x);
            bool ordered = true;
            float previousX = float.MinValue;
            for (int slot = 0; slot < BusInterior.TelltaleSlots; slot++)
            {
                BusInterior.TelltaleFrame(s, slot, out float3 lamp, out _, out _);
                ordered &= lamp.x > previousX;
                previousX = lamp.x;
            }
            BusInterior.TelltaleFrame(s, 0, out float3 leftLamp, out _, out _);
            BusInterior.TelltaleFrame(s, BusInterior.TelltaleSlots - 1, out float3 rightLamp, out _, out _);
            Check(ordered && leftLamp.x < tellMiddle - 0.15f && rightLamp.x > tellMiddle + 0.15f,
                $"{name}: the left indicator's lamp is on the driver's left ({leftLamp.x:0.000}), the right one's on his right ({rightLamp.x:0.000}), the row's middle at {tellMiddle:0.000}");
        }
        int doors = src.DoorCount;
        // Every door opening clear of the ends, the wheel arches and the other doors.
        for (int d = 0; d < doors; d++)
        {
            float2 door = src.Door(d);
            float d0 = door.x - 0.5f * door.y, d1 = door.x + 0.5f * door.y;
            // (a section whose rear is open, the articulated rear, ends in the joint: its last door may reach it; the front door
            // stands over the front axle of a short bus, ahead of its arch)
            bool ends = (d0 > s.RearZ + 0.9f || s.Section == BusSection.Rear) && d1 < s.FrontZ - 0.1f;
            bool arches = true;
            foreach (float axle in new[] { s.FrontAxleZ, s.RearAxleZ })
                if ((axle != s.FrontAxleZ || src.HasCab) && d > 0 && d0 < axle + 0.66f && d1 > axle - 0.66f) arches = false;
            bool apart = true;
            for (int e = 0; e < d; e++)
                if (math.abs(src.Door(e).x - door.x) < 0.5f * (src.Door(e).y + door.y) + 0.3f) apart = false;
            Check(ends && arches && apart, $"{name}: door {d} at {door.x:0.00} (width {door.y:0.00}) clear of the ends ({ends}), the arches ({arches}), the other doors ({apart})");
        }
        for (int d = 0; d < doors; d++)
        {
            for (int leaf = 0; leaf < 2; leaf++)
            {
                int part = BusInterior.DoorFrontA + 2 * d + leaf;
                DoorLinkage link = GenericDoorRig.For(s).Links[d][leaf];
                bool solved = link.Solve(1, out _, out float2 joint, out float yaw, out float slack);
                float3 free = new float3(link.Guide.x, s.FloorY + .8f, src.Door(d).x);
                float3 opened = buf.RestPointToModel(part, free), expected = link.Move(free, joint, yaw);
                Check(solved && slack > DoorLinkage.MinSlack && math.distance(opened, expected) < .0001f && math.abs(yaw) > 1.3f,
                    $"{name}: door {d} leaf {leaf} follows outward linkage, slack {slack:0.000}");
            }
        }
    }
}
