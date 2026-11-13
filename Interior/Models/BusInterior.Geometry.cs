using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// Geometry of the bus interior: the entry point (Define), the shared layout and small helpers. The parts are built in
//   BusInterior.Shell.cs    floor, podiums, wheel arches, side walls with window and door openings, front, rear, roof / ceiling
//   BusInterior.Dash.cs     dashboard, instrument binnacle, dials, switch panels, steering wheel, needles
//   BusInterior.Cab.cs      driver's platform, seat, pedals, partition, fare box, visor, mirrors, overhead console
//   BusInterior.Saloon.cs   passenger seats (Fittings)
//   BusInterior.Poles.cs    grab poles, ceiling handrails, straps, stop buttons, validators
//   BusInterior.Ceiling.cs  light strips, ducts, hatches, displays, cameras, speakers, the STOP sign
//   BusInterior.Doors.cs    door leaves, door posts, door drives and warning lamps
// Model space, rest pose, metres. Dimensions after Mercedes-Benz Citaro / Yutong E12 / BYD K9 data sheets: inner width ~2.40 m,
// standing height 2.40 m in the low-floor part, window sill ~0.75 m above the floor, window band ~1.3 m, door clear height 2.05 m,
// seat pitch ~0.75 m, seat width 0.44 m, grab poles 35 mm, driver's platform +0.34 m (VDV 2010 cockpit).
namespace CDLC.BusDriver.Interior.Models
{
    public sealed partial class BusInterior
    {
        // ---- extra static parts (after the contract parts)
        public const int Dash = 11;          // dashboard, binnacle, dials, switch panels, column shroud
        public const int Cab = 12;           // driver's platform, seat, partition, fare box, visor, mirrors, overhead console
        public const int Poles = 13;         // grab poles, ceiling handrails, hand straps, stop buttons, validators
        public const int Ceiling = 14;       // light strips, ducts, hatches, displays, cameras, speakers, STOP sign
        public const int Outside = 15;       // exterior mirrors and wipers (seen through the windscreen)

        // ---- layout (all from the spec)
        float HW => Spec.HalfWidth;
        float F => Spec.FloorY;
        float WallIn => Spec.HalfWidth - 0.07f;          // inner face of the side walls
        float SillY => Spec.FloorY + 0.74f;              // window sill
        float WinTop => Spec.RoofY - 0.64f;              // top of the side windows and the door openings
        float CeilY => Spec.RoofY - 0.27f;               // flat centre ceiling
        bool OpenFront => Spec.Section == BusSection.Rear;
        bool OpenRear => Spec.Section == BusSection.Front;
        float FrontIn => OpenFront ? Spec.FrontZ : Spec.FrontZ - 0.12f;   // inner face of the front wall (below the windscreen)
        float RearIn => OpenRear ? Spec.RearZ : Spec.RearZ + 0.12f;      // inner face of the rear wall
        float BodyFront => OpenFront ? Spec.FrontZ : Spec.FrontZ - 0.40f; // where the roof profile meets the front (destination box)

        // The front corners, rounded in plan (the side walls end where the round starts).
        const float kNoseR = 0.12f;

        // The roof's cant rails: their radius (RoofProfile), and the outer skin's height at x (flat in the middle, rounded down to
        // the sides, where the side wall's top meets it).
        const float kCantR = 0.28f;
        float RoofOuterY(float x)
        {
            float ax = math.abs(x), ro = Spec.RoofY;
            if (ax >= HW) return ro - 0.8f * kCantR;
            if (ax <= HW - kCantR) return ro;
            float c = (ax - (HW - kCantR)) / kCantR, s = math.sqrt(math.max(0f, 1f - c * c));
            return ro - kCantR + kCantR * s * 0.8f + 0.2f * kCantR;
        }

        // Cross-section (x, y) of an end of the body from y0 up, following the roof: half width HW - inset, the top drop below the
        // roof's outer skin (the rear wall's top, the destination box, the rear's black upper panel). n segments per cant.
        Profile EndSection(float y0, float inset, float drop, int n)
        {
            float half = HW - inset;
            var right = new System.Collections.Generic.List<float2> { new float2(half, RoofOuterY(half) - drop) };
            for (int i = 0; i <= n; i++)
            {
                float t = 0.5f * math.PI * i / n, x = HW - kCantR + kCantR * math.cos(t);
                if (x < half - 1e-4f) right.Add(new float2(x, RoofOuterY(x) - drop));
            }
            var pts = new System.Collections.Generic.List<float2> { new float2(-half, y0), new float2(half, y0) };
            pts.AddRange(right);
            for (int i = right.Count - 1; i >= 0; i--) pts.Add(new float2(-right[i].x, right[i].y));
            return new Profile(pts.ToArray());
        }

        // Plan outline (x, z) of a skin round the front: off metres outside the body's rounded front, t thick, from zBack on the
        // right side round the right corner, across the front and round the left corner back to zBack (both = false: only the
        // corner of side s, from zBack round to the front at x = s * xEnd). Points are smooth on the arcs.
        Profile NoseSkin(float off, float t, float zBack, bool both, float s = 1f, float xEnd = 0f, int arc = 6)
        {
            float fz = Spec.FrontZ, cx = HW - kNoseR, cz = fz - kNoseR;
            var outer = new System.Collections.Generic.List<float2>();
            var inner = new System.Collections.Generic.List<float2>();
            var smooth = new System.Collections.Generic.List<bool>();
            void Corner(float side, bool forward, float o, System.Collections.Generic.List<float2> into)
            {
                for (int i = 0; i <= arc; i++)
                {
                    float a = 0.5f * math.PI * (forward ? i : arc - i) / arc;      // 0: on the side, pi/2: on the front
                    into.Add(new float2(side * (cx + (kNoseR + o) * math.cos(a)), cz + (kNoseR + o) * math.sin(a)));
                }
            }
            void Build(float o, System.Collections.Generic.List<float2> into)
            {
                float sa = both ? 1f : s;
                into.Add(new float2(sa * (HW + o), zBack));
                Corner(sa, true, o, into);
                if (both)
                {
                    Corner(-1f, false, o, into);
                    into.Add(new float2(-(HW + o), zBack));
                }
                else
                    into.Add(new float2(s * xEnd, fz + o));
            }
            Build(off, outer);
            Build(off - t, inner);
            inner.Reverse();
            var pts = new System.Collections.Generic.List<float2>(outer);
            pts.AddRange(inner);
            for (int i = 0; i < pts.Count; i++)
            {
                int j = i < outer.Count ? i : i - outer.Count;
                int n = outer.Count;
                bool onArc = j > 0 && j < n - 1 && !(both ? false : j == n - 1);
                smooth.Add(onArc);
            }
            return new Profile(pts.ToArray(), smooth.ToArray());
        }

        // Windscreen: raked ~6 degrees (top further back), its opening between the A-pillars.
        float WsBotY => Spec.FloorY + 0.80f;
        float WsTopY => Spec.RoofY - 0.42f;
        float WsBotZ => Spec.FrontZ - 0.07f;
        float WsTopZ => Spec.FrontZ - 0.24f;
        float WsHalf => Spec.HalfWidth - 0.11f;
        float WsZ(float y) => math.lerp(WsBotZ, WsTopZ, math.saturate((y - WsBotY) / (WsTopY - WsBotY)));

        // Driver's cab (driver on Side): centre line, aisle-side partition, rear end, platform.
        float CabX => Side * (Spec.HalfWidth - 0.66f);
        float PartX => Side * (Spec.HalfWidth - 1.10f);
        float CabRearZ => Spec.FrontZ - 1.90f;
        float PlatY => Spec.FloorY + 0.34f;

        // Rear platform (the bench over the engine): from the rear wall forward, stopping short of a door opening and of the
        // rear wheel arch when the axle is near the end (a short rear overhang: the platform went into the wheel).
        bool HasRearPlatform => !OpenRear;
        float RearPlatZ
        {
            get
            {
                float z = RearIn + 1.05f;
                float arch = Spec.RearAxleZ - kArchHalf - 0.03f;
                if (arch > RearIn && arch < z) z = arch;
                for (int d = 0; d < DoorCount; d++)
                {
                    float2 door = Door(d);
                    float r = door.x - door.y * 0.5f;
                    if (r < z + 0.05f && door.x + door.y * 0.5f > RearIn) z = math.min(z, r - 0.05f);
                }
                return math.max(z, RearIn + 0.45f);
            }
        }
        float RearPlatY => Spec.FloorY + 0.42f;

        // Wheel arches: front (with a cab) and the rear / drive axle; z half length, inward reach, the top of the housing above the
        // floor. The housings are hollow (the wheels turn inside them, seen through the arch openings from outside) with room
        // above the tyre for the body to kneel over it.
        bool HasFrontArch => HasCab;
        const float kArchHalf = 0.66f, kFrontReach = 0.55f, kRearReach = 0.52f;
        const float kFrontArchTop = 0.68f, kRearArchTop = 0.68f, kArchShell = 0.02f;

        // The wheels (exterior): 275/70 R22.5 at the front and the middle axle, a wide single on the drive axle; centres inside
        // the body side, outer tyre face 5 cm in.
        public const float WheelRadius = 0.46f;
        const float kFrontTyre = 0.28f, kDriveTyre = 0.40f;
        bool DriveAxleWide => Spec.Section != BusSection.Front;
        float FrontWheelX => Spec.HalfWidth - 0.05f - 0.5f * kFrontTyre;
        float RearWheelX => Spec.HalfWidth - 0.05f - 0.5f * (DriveAxleWide ? kDriveTyre : kFrontTyre);

        // The arches on one side (both sides alike): x = axle z, y = inward reach, z = housing top.
        float3[] Arches()
        {
            return HasFrontArch
                ? new[] { new float3(Spec.FrontAxleZ, kFrontReach, F + kFrontArchTop), new float3(Spec.RearAxleZ, kRearReach, F + kRearArchTop) }
                : new[] { new float3(Spec.RearAxleZ, kRearReach, F + kRearArchTop) };
        }

        // [z0, z1] without the arch openings (with a margin), as (from, to) pairs.
        float2[] OutsideArches(float z0, float z1, float margin = 0f)
        {
            var parts = new System.Collections.Generic.List<float2> { new float2(z0, z1) };
            foreach (float3 a in Arches())
            {
                float a0 = a.x - kArchHalf - margin, a1 = a.x + kArchHalf + margin;
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    float2 s = parts[i];
                    if (a1 <= s.x || a0 >= s.y) continue;
                    parts.RemoveAt(i);
                    if (a1 < s.y) parts.Insert(i, new float2(a1, s.y));
                    if (a0 > s.x) parts.Insert(i, new float2(s.x, a0));
                }
            }
            return parts.ToArray();
        }

        // Outline (z, y) of an arch opening (and its housing) from the floor at the front, over the rounded top, down at the rear.
        float2[] ArchOutline(float z, float top, int lod)
        {
            int n = lod == 0 ? 5 : 2;
            float h = top - F, r = math.min(0.32f, h * 0.6f);
            var pts = new float2[2 * (n + 1) + 2];
            int c = 0;
            pts[c++] = new float2(z + kArchHalf, F);
            for (int i = 0; i <= n; i++) { float a = 0.5f * math.PI * i / n; pts[c++] = new float2(z + kArchHalf - r + r * math.cos(a), top - r + r * math.sin(a)); }
            for (int i = 0; i <= n; i++) { float a = 0.5f * math.PI * (1f + i / (float)n); pts[c++] = new float2(z - kArchHalf + r + r * math.cos(a), top - r + r * math.sin(a)); }
            pts[c++] = new float2(z - kArchHalf, F);
            return pts;
        }

        // Is z inside a kerb-side door opening (with a margin)?
        bool InDoor(float z, float margin)
        {
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                if (math.abs(z - door.x) < door.y * 0.5f + margin) return true;
            }
            return false;
        }

        // Kerb-side door openings sorted by z (front first): x = rear edge, y = front edge.
        float2[] DoorCuts(float margin)
        {
            var cuts = new float2[DoorCount];
            for (int d = 0; d < DoorCount; d++)
            {
                float2 door = Door(d);
                cuts[d] = new float2(door.x - door.y * 0.5f - margin, door.x + door.y * 0.5f + margin);
            }
            for (int i = 1; i < cuts.Length; i++)
                for (int j = i; j > 0 && cuts[j].y > cuts[j - 1].y; j--) (cuts[j], cuts[j - 1]) = (cuts[j - 1], cuts[j]);
            return cuts;
        }

        void DefineCardReaders(MeshKit k, int lod)
        {
            foreach ((float3 origin, quaternion frame, float _) in AuthoredModel.CardReaders)
                using (k.At(origin, frame))
                    CardReader(k, lod);
        }

        public override void Define(KitModelBuilder b, int lod)
        {
            if (!IsAuthored && !RawSource)
            {
                GenericBusMesh.For(Spec).Emit(b, lod);
                return;
            }
            if (IsAuthored)
            {
                int readers = CardReaderPart(AuthoredModel);
                // the readers' own part is not one of the mesh's: the mesh has nothing to define for it
                if (b.Active >= WrapLeft && b.Active <= WrapRoof)
                    AuthoredModel.Mesh.DefineWrap(b, lod, (WrapFace)(b.Active - WrapLeft));
                else if (b.Active < AuthoredModel.Mesh.PartCount)
                    AuthoredModel.Mesh.Define(b, lod);
                if (b.Building(Ceiling) && AuthoredModel.NextStopEnabled && AuthoredModel.NextStopSize.x <= 0f)
                    LedMatrixScreen(b.Part(Ceiling), LedScreenCentre(Spec));
                // the mod's own card reader (screen, tap zone, QR scanner; lit by BusCabSystem.Readers) at the model's reader points:
                // the part's frame is the model frame
                if (readers == Cab && b.Building(Cab))
                    DefineCardReaders(b.Part(Cab), lod);
                else if (readers > Cab && b.Building(readers))
                    // a part of its own has nothing else in it: the far LODs keep the housing whatever its size
                    using (b.Part(readers).KeepSmallStructure())
                        DefineCardReaders(b.Part(readers), lod);
                return;
            }
            using (b.Part(b.Active).KeepSmallStructure())
            {
                if (b.Building(Shell)) DefineShell(b.Part(Shell), lod);
                if (b.Building(SteeringWheel) && HasCab) DefineWheel(b.Part(SteeringWheel), lod);
                if (b.Building(SpeedNeedle) && HasCab) DefineNeedle(b.Part(SpeedNeedle), DialCentre(0));
                if (b.Building(RpmNeedle) && HasCab) DefineNeedle(b.Part(RpmNeedle), DialCentre(1));
                for (int d = 0; d < 3; d++)
                {
                    if (b.Building(DoorFrontA + 2 * d)) DefineLeaf(b.Part(DoorFrontA + 2 * d), d, true, lod);
                    if (b.Building(DoorFrontA + 2 * d + 1)) DefineLeaf(b.Part(DoorFrontA + 2 * d + 1), d, false, lod);
                }
                if (b.Building(Fittings)) DefineSeats(b.Part(Fittings), lod);
                if (b.Building(Dash) && HasCab) DefineDash(b.Part(Dash), lod);
                if (b.Building(Cab) && HasCab) DefineCab(b.Part(Cab), lod);
                if (b.Building(Poles)) DefinePoles(b.Part(Poles), lod);
                if (b.Building(Ceiling)) DefineCeiling(b.Part(Ceiling), lod);
                if (b.Building(Outside) && HasCab) DefineOutside(b.Part(Outside), lod);
                DefineExteriorParts(b, lod);
                DefineJoint(b, lod);
                DefineDoorDrives(b, lod);
                DefineStraps(b);
            }
        }

        // ---------------------------------------------------------------- helpers

        static void ClosedSkin(MeshKit k, float3 a, float3 b, float3 c, float3 d, float3 n, float thickness)
        {
            float3 back = -n * thickness;
            k.QuadP(a, b, c, d, n); k.QuadP(a + back, b + back, c + back, d + back, -n);
            var points = new[] { a, b, c, d };
            float3 centre = (a + b + c + d) * .25f;
            for (int i = 0; i < 4; i++)
            {
                float3 p = points[i], q = points[(i + 1) % 4];
                float3 side = math.normalizesafe(math.cross(q - p, n));
                if (math.dot(side, (p + q) * .5f - centre) < 0f) side = -side;
                k.QuadP(p, q, q + back, p + back, side);
            }
        }

        // Points of a rounded rectangle in the plane (o, u, v): centre o, half sizes h, corner radius r, n segments per corner. Without segments (the far LODs) a corner is a point of the
        // rectangle itself: the start of its arc would lie on an edge and the four points would make a skewed quadrilateral.
        internal static float3[] RoundRect(float3 o, float3 u, float3 v, float2 h, float r, int n)
        {
            if (n == 0) return new[] { o + u * h.x + v * h.y, o - u * h.x + v * h.y, o - u * h.x - v * h.y, o + u * h.x - v * h.y };
            r = math.min(r, 0.95f * math.min(h.x, h.y));
            var p = new float3[4 * (n + 1)];
            int k = 0;
            for (int c = 0; c < 4; c++)
            {
                float2 cc = c == 0 ? new float2(h.x - r, h.y - r) : c == 1 ? new float2(-h.x + r, h.y - r) : c == 2 ? new float2(-h.x + r, -h.y + r) : new float2(h.x - r, -h.y + r);
                for (int i = 0; i <= n; i++)
                {
                    float a = (c + i / (float)math.max(1, n)) * 0.5f * math.PI;
                    float2 q = cc + r * new float2(math.cos(a), math.sin(a));
                    p[k++] = o + u * q.x + v * q.y;
                }
            }
            return p;
        }

        // Closed frame (gasket, bezel) of section w x d around a rounded rectangle; normal n = the side the frame faces.
        void Frame(MeshKit k, float3 o, float3 u, float3 v, float2 h, float r, float w, float d, int n)
        {
            if (k.Skipping) return;
            float3 nn = math.cross(u, v);
            k.Sweep(RoundRect(o, u, v, h, r, k.Lod == 0 ? n : 0), Profile.Rect(w, d), nn, Caps.None, null, true);
        }

        // Tube along a polyline (poles, rails, handles): round section r.
        void Pipe(MeshKit k, float3[] path, float r, int sides, Caps caps = Caps.Both)
        {
            if (k.Skipping || k.Tiny(new float3(r * 2f, r * 2f, 0.05f * PathLen(path)))) return;
            // up = the normal of the path's plane: never parallel to a segment (parallel transport flips at two 90-degree bends
            // in the plane of a vertical up)
            float3 up = new float3(0f, 1f, 0f);
            for (int i = 2; i < path.Length; i++)
            {
                float3 n = math.cross(path[i - 1] - path[i - 2], path[i] - path[i - 1]);
                if (math.lengthsq(n) > 1e-8f) { up = math.normalize(n); break; }
            }
            if (path.Length == 2 && math.abs(math.normalizesafe(path[1] - path[0]).y) > 0.9f) up = new float3(1f, 0f, 0f);
            k.Sweep(path, Profile.Circle(r, k.Sides(sides)), up, caps);
        }

        static float PathLen(float3[] p)
        {
            float l = 0f;
            for (int i = 1; i < p.Length; i++) l += math.distance(p[i - 1], p[i]);
            return l;
        }

        // Arc of n + 1 points from a0 to a1 (radians) in the plane (c, u, v).
        static float3[] Arc(float3 c, float3 u, float3 v, float r, float a0, float a1, int n)
        {
            var p = new float3[n + 1];
            for (int i = 0; i <= n; i++)
            {
                float a = math.lerp(a0, a1, i / (float)n);
                p[i] = c + r * (u * math.cos(a) + v * math.sin(a));
            }
            return p;
        }
    }
}
