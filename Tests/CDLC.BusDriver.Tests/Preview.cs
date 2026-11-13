using System;
using CDLC.BusDriver;
using System.IO;
using System.IO.Compression;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

// Offline preview of the kit models (software rasteriser after Realistic Road Works' KitPreview): palette colours, a sun and sky
// light, near-plane clipping (views from inside a model), magenta = a back face seen (inside-out or open geometry).
// Usage: dotnet run -c Release --project Tests/CDLC.BusDriver.Tests -- preview <model|all> [lod=0] [pose=-1|0|1] [view=all|...]
//        [spec=solo|lht|midi|front|rear|long|short-rear|mini|<pack model>:front|<pack model>:rear] [out=out/preview] [eye=x,y,z look=x,y,z fov=deg]
// An articulated pack's section (spec=CRRC_K18:rear) wears its share of the side and roof pictures, as BusSkins maps them over the sections.
// Views of bus-interior: cab (the driver's view), cab-door (towards the doors), cab-up (the dashboard, wheel and dials),
// saloon (from the front, looking back), rear (from the back, looking forward), plan (cut-away from above), outside (4 views).
static class Preview
{
    const int W = 960, H = 600;

    static string[] s_Args = new string[0];

    public static int Run(string[] args)
    {
        s_Args = args;
        string name = args.Length > 1 ? args[1] : "all";
        int lod = int.Parse(Opt(args, "lod", "0"));
        int pose = int.Parse(Opt(args, "pose", "-1"));
        string view = Opt(args, "view", "all"), outDir = Opt(args, "out", "out/preview"), specName = Opt(args, "spec", "solo");
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var e in KitRegistry.All)
        {
            if (name != "all" && !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            KitSource src = e.Name == "bus-interior" ? new BusInterior(SpecByName(specName)) : (KitSource)Activator.CreateInstance(e.Type);
            var data = KitBuild.BuildAll(src);
            if (data.Error != null) { Console.WriteLine("preview " + e.Name + ": " + data.Error); return 1; }
            // Read-only LOD strategy diagnostic: retain selected exported LOD0
            // parts in this process, without changing any source/asset file.
            string retained = Opt(args, "retain", "");
            if (lod > 0 && retained.Length > 0)
            {
                foreach (string value in retained.Split(','))
                {
                    int part = int.Parse(value);
                    if (part < 0 || part >= data.Parts.Length) throw new ArgumentOutOfRangeException("retain");
                    data.Parts[part].Data[lod] = data.Parts[part].Data[0];
                }
                Console.WriteLine("diagnostic LOD0 retained parts: " + retained);
            }
            var buf = new KitPoseBuffer(data.Parts);
            if (pose >= 0 && pose < src.BoardPoses) src.BoardPose(buf, pose);
            string tag = e.Name + (e.Name == "bus-interior" ? "_" + specName.Replace(':', '-') : "") + (pose >= 0 ? "_pose" + pose : "") + "_lod" + lod;
            foreach (var v in Views(src, data, Opt(args, "eye", ""), Opt(args, "look", ""), float.Parse(Opt(args, "fov", "62"), System.Globalization.CultureInfo.InvariantCulture)))
            {
                if (view != "all" && view != v.Name) continue;
                string path = Path.Combine(outDir, tag + "_" + v.Name + ".png");
                // the bus: the exterior parts (glazing, wheels, lamps) only in the views from outside, as the cab system shows them
                if (src is BusInterior bus)
                {
                    var boardPose = BusInterior.BoardPoseOf(math.max(0, pose), v.Name.StartsWith("outside"));
                    boardPose.Wrap = LoadWrapOptions(args, bus);
                    s_DoorPieces.Clear();
                    foreach ((int part, BusInterior.WrapFace face) in BusInterior.WrapDoorPieces(bus.Spec)) s_DoorPieces[part] = (int)face;
                    boardPose.JointFold = math.radians(float.Parse(Opt(args, "fold", "0"), System.Globalization.CultureInfo.InvariantCulture));     // fold=deg: the articulation
                    string door = Opt(args, "door", "");                                                                                         // door=0..1: every door that far open
                    if (door.Length > 0) boardPose.DoorOpen = new float3(float.Parse(door, System.Globalization.CultureInfo.InvariantCulture));
                    BusInterior.Apply(buf, boardPose, bus.Spec);
                }
                s_Signs = src is BusInterior signed ? Signs(signed.Spec, v.Name.StartsWith("outside")) : null;
                s_Extra = src is BusInterior carried ? Riders(carried, float.Parse(Opt(args, "riders", "0"), System.Globalization.CultureInfo.InvariantCulture), v.Name.StartsWith("outside")) : null;
                Render(data, buf, lod, v, path);
                Console.WriteLine("preview " + path);
                n++;
            }
            Console.WriteLine($"  {e.Name}: tris LOD0 {data.Tris[0]} LOD1 {data.Tris[1]} LOD2 {data.Tris[2]}, renderers {data.Renderers[0]}, build {data.BuildMs:0} ms (worst step {data.MaxStepMs:0.0} ms)");
            for (int i = 0; i < data.Parts.Length; i++)
                Console.WriteLine($"    part {i,2} {data.Parts[i].Name,-14} tris {data.Parts[i].Tris[0],6} / {data.Parts[i].Tris[1],5} / {data.Parts[i].Tris[2],4}");
        }
        if (n == 0) Console.WriteLine("preview: nothing rendered (model '" + name + "', view '" + view + "')");
        return n > 0 ? 0 : 1;
    }

    // The section's share of the along pictures (start, share of the width) for spec=<pack model>:<section>; (0, 1) for a whole bus.
    static float2 s_Share = new float2(0f, 1f);
    static VehicleProfile s_WrapModel;

    static BusInteriorSpec SpecByName(string s)
    {
        var spec = BusInteriorSpec.Default;
        s_Share = new float2(0f, 1f);
        s_WrapModel = null;
        int colon = s.IndexOf(':');
        if (colon > 0)
        {
            VehicleProfile pack = VehicleProfileJson.Load(TestPacks.ProfilePath(s.Substring(0, colon)));
            if (pack.Assembly == null) throw new ArgumentException(s + ": not an articulated pack");
            VehicleModels.RegisterAssembly(pack.Assembly);
            var sections = new[] { new BusInterior(pack.Assembly.Front.Profile.Spec), new BusInterior(pack.Assembly.Rear.Profile.Spec) };
            bool rear = s.Substring(colon + 1) == "rear";
            float total = sections[0].WrapLength + sections[1].WrapLength;
            s_Share = rear ? new float2(sections[0].WrapLength / total, sections[1].WrapLength / total) : new float2(0f, sections[0].WrapLength / total);
            s_WrapModel = pack.Assembly.Front.Profile;
            return sections[rear ? 1 : 0].Spec;
        }
        switch (s)
        {
            case "sunwin": return TestPacks.Spec("SWB6710EV01G");
            case "sunwin6109":
            case "swb6109": return TestPacks.Spec("SWB6109EV12G");
            case "lht": spec.DriverSide = 1f; break;
            case "midi": spec.FrontZ = 4.6f; spec.RearZ = -4.6f; spec.FrontAxleZ = 2.2f; spec.RearAxleZ = -2.0f; spec.Doors = 2; break;
            case "front": spec.FrontZ = 5.2f; spec.RearZ = -5.0f; spec.FrontAxleZ = 2.6f; spec.RearAxleZ = -2.4f; spec.Doors = 2; spec.Section = BusSection.Front; break;
            case "rear": spec.FrontZ = 3.6f; spec.RearZ = -4.4f; spec.RearAxleZ = -0.6f; spec.Doors = 2; spec.Section = BusSection.Rear; break;
            case "long": spec.FrontZ = 6.8f; spec.RearZ = -11.2f; spec.FrontAxleZ = 3.3f; spec.RearAxleZ = -8.7f; spec.RoofY = 3.3f; break;
            case "short-rear": spec.FrontZ = 7.0f; spec.RearZ = -5.0f; spec.FrontAxleZ = 3.4f; spec.RearAxleZ = -3.7f; spec.RoofY = 2.7f; break;
            case "mini": spec.FrontZ = 3.8f; spec.RearZ = -3.8f; spec.FrontAxleZ = 2.0f; spec.RearAxleZ = -1.8f; spec.HalfWidth = 1.05f; spec.RoofY = 2.8f; spec.FloorY = 0.42f; spec.Doors = 1; break;
        }
        return spec;
    }

    static string Opt(string[] a, string key, string def)
    {
        foreach (var s in a) if (s.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return s.Substring(key.Length + 1);
        return def;
    }

    static float3 Vec(string s)
    {
        var q = s.Split(',');
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return new float3(float.Parse(q[0], c), float.Parse(q[1], c), float.Parse(q[2], c));
    }

    struct ViewDef
    {
        public string Name;
        public float3 Eye, Target;
        public float Fov;          // vertical, degrees
        public float CutY;         // drop triangles above this height (plan view); +inf = none
        public bool Ortho;
    }

    static ViewDef[] Views(KitSource src, KitModelData data, string eye, string look, float fov)
    {
        var list = new System.Collections.Generic.List<ViewDef>();
        const float none = float.PositiveInfinity;
        if (eye.Length > 0 && look.Length > 0)
            list.Add(new ViewDef { Name = Opt(s_Args, "outside", "0") == "1" ? "outside-custom" : "custom", Eye = Vec(eye), Target = Vec(look), Fov = fov, CutY = Opt(s_Args, "cut", "").Length > 0 ? float.Parse(Opt(s_Args, "cut", ""), System.Globalization.CultureInfo.InvariantCulture) : none });     // cut=y: drop what is above
        if (src is BusInterior bus)
        {
            var s = bus.Spec;
            if (bus.HasCab)
            {
                float3 e = BusInterior.Eye(s);
                list.Add(new ViewDef { Name = "cab", Eye = e, Target = e + new float3(0f, -0.16f, 1f), Fov = fov, CutY = none });
                list.Add(new ViewDef { Name = "cab-door", Eye = e, Target = e + new float3(-s.DriverSide * 0.9f, -0.25f, 0.6f), Fov = fov, CutY = none });
                list.Add(new ViewDef { Name = "cab-up", Eye = e, Target = bus.WheelHub, Fov = 50f, CutY = none });
            }
            float3 front = new float3(0.1f, s.FloorY + 1.65f, (bus.HasCab ? s.FrontAxleZ - 0.2f : s.FrontZ - 0.6f));
            list.Add(new ViewDef { Name = "saloon", Eye = front, Target = front + new float3(0f, -0.25f, -1f), Fov = 70f, CutY = none });
            float3 back = new float3(0.1f, s.FloorY + 1.75f, s.RearZ + 0.9f);
            list.Add(new ViewDef { Name = "rear", Eye = back, Target = back + new float3(0f, -0.2f, 1f), Fov = 70f, CutY = none });
            float mid = (s.FrontZ + s.RearZ) * 0.5f;
            list.Add(new ViewDef { Name = "plan", Eye = new float3(0f, 60f, mid - 0.01f), Target = new float3(0f, 0f, mid), Fov = 13f, CutY = s.FloorY + 1.35f });
        }
        float3 c = data.Center;
        float r = math.max(1f, data.Radius);
        list.Add(new ViewDef { Name = "outside-fl", Eye = c + new float3(-1.6f, 0.9f, 1.7f) * r, Target = c, Fov = 34f, CutY = none });
        list.Add(new ViewDef { Name = "outside-rr", Eye = c + new float3(1.7f, 0.7f, -1.6f) * r, Target = c, Fov = 34f, CutY = none });
        if (src is BusInterior b2)
        {
            // the bus from the kerb, from the front and the rear at eye height, and low at the front wheel
            var s2 = b2.Spec;
            float3 mid = new float3(0f, 1.5f, 0.5f * (s2.FrontZ + s2.RearZ));
            list.Add(new ViewDef { Name = "outside-kerb", Eye = mid + new float3(-s2.DriverSide * 16f, 0.3f, 0f), Target = mid, Fov = 42f, CutY = none });
            list.Add(new ViewDef { Name = "outside-front", Eye = new float3(-s2.DriverSide * 2.2f, 1.7f, s2.FrontZ + 7f), Target = new float3(0f, 1.4f, s2.FrontZ), Fov = 40f, CutY = none });
            list.Add(new ViewDef { Name = "outside-rear", Eye = new float3(s2.DriverSide * 2.2f, 1.7f, s2.RearZ - 7f), Target = new float3(0f, 1.4f, s2.RearZ), Fov = 40f, CutY = none });
            float3 wheel = new float3(-s2.DriverSide * s2.HalfWidth, 0.5f, s2.Section == BusSection.Rear ? s2.RearAxleZ : s2.FrontAxleZ);
            list.Add(new ViewDef { Name = "outside-wheel", Eye = wheel + new float3(-s2.DriverSide * 3.2f, 0.4f, 1.6f), Target = wheel, Fov = 40f, CutY = none });
        }
        return list.ToArray();
    }

    // ---------------------------------------------------------------- raster

    // ---------------------------------------------------------------- LED signs (drawn at runtime by the cab system)

    static System.Collections.Generic.List<float3> s_Signs;      // lit triangles (model space), three points each
    static System.Collections.Generic.List<(KitMeshData, float4x4)> s_Extra;   // more meshes to draw (the riders)
    static KitModelData s_Riders;

    // The people aboard at a load of 0 .. 1 as the cab system places them (the same split, a fixed choice of places and looks).
    static System.Collections.Generic.List<(KitMeshData, float4x4)> Riders(BusInterior bus, float load, bool outside)
    {
        if (load <= 0f) return null;
        if (s_Riders == null) s_Riders = KitBuild.BuildAll(new BusRiders());
        var slots = bus.RiderSlots();
        int seats = 0, stands = 0;
        foreach (var s in slots) { if (s.Kind == BusInterior.RiderKind.Seated) seats++; else if (s.Kind != BusInterior.RiderKind.Driver) stands++; }
        int figures = BusRiders.Figures((int)math.round(load * 100f), 100, seats + stands);
        BusRiders.Split(figures, seats, stands, out int seated, out int standing);
        var list = new System.Collections.Generic.List<(KitMeshData, float4x4)>();
        var rng = new System.Random(7);
        var order = new System.Collections.Generic.List<int>();
        for (int i = 0; i < slots.Count; i++) order.Add(i);
        for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        foreach (int i in order)
        {
            var s = slots[i];
            if (s.Kind == BusInterior.RiderKind.Seated) { if (seated-- <= 0) continue; }
            else if (s.Kind == BusInterior.RiderKind.Driver) { if (!outside) continue; }
            else if (standing-- <= 0) continue;
            int part = BusRiders.PartFor(s.Kind, rng.Next(BusRiders.Looks));
            var d = s_Riders.Parts[part].Data[0];
            if (d != null) list.Add((d, float4x4.TRS(s.Position, quaternion.RotateY(s.Yaw), 1f)));
        }
        Console.WriteLine($"  riders at load {load:0.00}: {figures} of {seats} seats + {stands} standing places");
        return list;
    }
    static CDLC.BusDriver.Interior.LedFont s_Font;

    // The bus's outside signs as the cab system lights them: line 12 to "Central Station" on the front and the side, 12 at the rear.
    // The outside signs from outside; the passengers' next-stop screen in every view (it is inside, on shift: BusCabSystem.UpdateLedScreen).
    static System.Collections.Generic.List<float3> Signs(BusInteriorSpec spec, bool outside)
    {
        if (s_Font == null)
        {
            string fontPath = LedFontTests.FontPath();
            var font = new CDLC.BusDriver.Interior.LedFont();
            if (fontPath == null || !font.Load(fontPath)) return null;
            s_Font = font;
        }
        var tris = new System.Collections.Generic.List<float3>();
        var s = spec.Snapped();
        if (s.Section != BusSection.Rear && VehicleModels.Find(s.ModelVariant)?.NextStopEnabled != false)
        {
            BusInterior.LedScreenFrame(s, out float3 nc, out float2 ns);
            int2 matrix = BusInterior.LedScreenMatrix(s);
            Sign(tris, "145", "NEXT STOP", matrix.x, matrix.y, nc, ns, quaternion.identity);     // fits the screen: no scroll
        }
        if (!outside)
            return tris;
        if (s.Section != BusSection.Rear)
        {
            BusInterior.FrontSignFrame(s, out float3 fc, out float2 fs);
            int2 frontMatrix = BusInterior.FrontSignMatrix(s);
            Sign(tris, "12", "Central Station", frontMatrix.x, frontMatrix.y, fc, fs, quaternion.RotateY(math.PI));
            if (BusInterior.SideSignFrame(s, out float3 sc, out float2 ss, out float kerb))
            {
                int2 sideMatrix = BusInterior.SideSignMatrix(s);
                Sign(tris, "12", "Central Station", sideMatrix.x, sideMatrix.y, sc, ss, BusInterior.SideSignRotation(s, kerb));
            }
        }
        if (s.Section != BusSection.Front)
        {
            BusInterior.RearRouteFrame(s, out float3 rc, out float2 rs);
            int2 matrix = BusInterior.RearRouteMatrix(s);
            Sign(tris, "", "12", matrix.x, matrix.y, rc, rs, quaternion.identity);
        }
        return tris;
    }

    // One sign: the dots of the composed text as small quads on the plane facing -Z of the frame (centre, rotation), x right, y down.
    static void Sign(System.Collections.Generic.List<float3> tris, string block, string message, int dx, int dy, float3 centre, float2 size, quaternion rot)
    {
        var composer = new CDLC.BusDriver.Interior.LedComposer(s_Font, dx, dy);
        composer.RenderSign(block, message, 0);
        float2 pitch = size / new float2(dx, dy), half = 0.39f * pitch;
        for (int y = 0; y < dy; y++)
            for (int x = 0; x < dx; x++)
            {
                if (!composer.Buffer[x, y]) continue;
                float2 c = new float2(-0.5f * size.x + (x + 0.5f) * pitch.x, 0.5f * size.y - (y + 0.5f) * pitch.y);
                float3 P(float u, float w) => centre + math.mul(rot, new float3(c.x + u, c.y + w, 0f));
                float3 a = P(-half.x, -half.y), b = P(half.x, -half.y), cc = P(half.x, half.y), d = P(-half.x, half.y);
                tris.Add(a); tris.Add(d); tris.Add(cc);
                tris.Add(a); tris.Add(cc); tris.Add(b);
            }
    }

    static void Render(KitModelData data, KitPoseBuffer buf, int lod, ViewDef v, string path)
    {
        var color = new float3[W * H];
        var depth = new float[W * H];
        for (int i = 0; i < depth.Length; i++) { depth[i] = float.MaxValue; color[i] = Sky(i / W); }
        float3 f = math.normalize(v.Target - v.Eye);
        float3 rt = math.normalize(math.cross(new float3(0f, 1f, 0f), f));
        if (!math.all(math.isfinite(rt)) || math.lengthsq(rt) < 1e-6f) rt = new float3(1f, 0f, 0f);
        float3 up = math.cross(f, rt);
        float focal = 0.5f * H / math.tan(math.radians(v.Fov * 0.5f));
        float3 sun = math.normalize(new float3(-0.4f, 0.8f, 0.45f));
        var draws = new System.Collections.Generic.List<(KitMeshData, float4x4)>();
        var drawPart = new System.Collections.Generic.List<int>();
        for (int i = 0; i < data.Parts.Length; i++)
        {
            if (!buf.Visible[i]) continue;
            var dd = data.Parts[i].Data[lod] ?? (data.Parts[i].MeshOf >= 0 ? data.Parts[data.Parts[i].MeshOf].Data[lod] : null);
            if (dd != null) { draws.Add((dd, buf.PartToModel(i))); drawPart.Add(i); }
        }
        if (s_Extra != null) { draws.AddRange(s_Extra); foreach (var _ in s_Extra) drawPart.Add(-1); }
        string ray = Opt(s_Args, "ray", "");
        if (ray.Length > 0)
        {
            // diagnostic: a ray from the door side (x = 3) towards -x at height y, depth z (ray=y;z): every triangle it passes, nearest first
            var q = ray.Split(';');
            float ry = float.Parse(q[0], System.Globalization.CultureInfo.InvariantCulture), rz = float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture);
            var hits = new System.Collections.Generic.List<(float x, string text)>();
            for (int di = 0; di < draws.Count; di++)
            {
                var (dd2, mm) = draws[di];
                for (int t = 0; t + 2 < dd2.Idx.Length; t += 3)
                {
                    float3 a = math.transform(mm, dd2.P[dd2.Idx[t]]), bq = math.transform(mm, dd2.P[dd2.Idx[t + 1]]), c = math.transform(mm, dd2.P[dd2.Idx[t + 2]]);
                    // the point (ry, rz) in the triangle's (y, z) projection, then its x
                    float d = (bq.y - a.y) * (c.z - a.z) - (bq.z - a.z) * (c.y - a.y);
                    if (math.abs(d) < 1e-12f) continue;
                    float bu = ((ry - a.y) * (c.z - a.z) - (rz - a.z) * (c.y - a.y)) / d, bv = ((bq.y - a.y) * (rz - a.z) - (bq.z - a.z) * (ry - a.y)) / d;
                    if (bu < -1e-5f || bv < -1e-5f || bu + bv > 1f + 1e-5f) continue;
                    float x = a.x + bu * (bq.x - a.x) + bv * (c.x - a.x);
                    float3 n = math.normalizesafe(math.cross(bq - a, c - a));
                    hits.Add((x, $"x {x:0.000}  part {drawPart[di]} {(drawPart[di] >= 0 && drawPart[di] < data.Parts.Length ? data.Parts[drawPart[di]].Name : "?")}  facing {(n.x > 0.2f ? "OUT" : n.x < -0.2f ? "in" : "side")} ({n.x:0.00},{n.y:0.00},{n.z:0.00})"));
                }
            }
            hits.Sort((l, r) => r.x.CompareTo(l.x));
            foreach (var h in hits.GetRange(0, Math.Min(10, hits.Count))) Console.WriteLine("  RAY " + h.text);
        }
        string pick = Opt(s_Args, "pick", "");
        if (pick.Length > 0)
        {
            // diagnostic: which parts have vertices in the box x0,x1,y0,y1,z0,z1 (model space), and how far out they stand
            var q = pick.Split(';');
            float[] b = Array.ConvertAll(q, t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
            for (int di = 0; di < draws.Count; di++)
            {
                var (dd2, mm) = draws[di];
                int count = 0; float lo = 9, hi = -9, zlo = 99, zhi = -99;
                for (int k = 0; k < dd2.P.Length; k++)
                {
                    float3 pp = math.transform(mm, dd2.P[k]);
                    if (pp.x < b[0] || pp.x > b[1] || pp.y < b[2] || pp.y > b[3] || pp.z < b[4] || pp.z > b[5]) continue;
                    count++; lo = math.min(lo, pp.x); hi = math.max(hi, pp.x); zlo = math.min(zlo, pp.z); zhi = math.max(zhi, pp.z);
                }
                if (count > 0)
                {
                    // the triangles with a vertex in the box: where they face (the geometric normal as the part is placed) and how tall they are
                    float3 sum = float3.zero; int tris = 0; float ylo = 9, yhi = -9;
                    for (int t = 0; t + 2 < dd2.Idx.Length; t += 3)
                    {
                        float3 a = math.transform(mm, dd2.P[dd2.Idx[t]]), bq = math.transform(mm, dd2.P[dd2.Idx[t + 1]]), c = math.transform(mm, dd2.P[dd2.Idx[t + 2]]);
                        bool inside = false;
                        foreach (float3 pp in new[] { a, bq, c }) inside |= pp.x >= b[0] && pp.x <= b[1] && pp.y >= b[2] && pp.y <= b[3] && pp.z >= b[4] && pp.z <= b[5];
                        if (!inside) continue;
                        sum += math.normalizesafe(math.cross(bq - a, c - a)); tris++;
                        ylo = math.min(ylo, math.min(a.y, math.min(bq.y, c.y))); yhi = math.max(yhi, math.max(a.y, math.max(bq.y, c.y)));
                    }
                    float3 nn = math.normalizesafe(sum);
                    Console.WriteLine($"  PICK part {drawPart[di]} {(drawPart[di] >= 0 && drawPart[di] < data.Parts.Length ? data.Parts[drawPart[di]].Name : "?")}: {count} vertices, x {lo:0.000} .. {hi:0.000}; {tris} triangles facing ({nn.x:0.00}, {nn.y:0.00}, {nn.z:0.00}), y {ylo:0.00} .. {yhi:0.00}, z {zlo:0.00} .. {zhi:0.00}");
                }
            }
        }
        int drawIndex = -1;
        foreach (var (d, m) in draws)
        {
            drawIndex++;
            int wrapFace = drawPart[drawIndex] - BusInterior.WrapLeft;
            if (drawPart[drawIndex] >= 0 && s_DoorPieces.TryGetValue(drawPart[drawIndex], out int pieceFace)) wrapFace = pieceFace;      // a door piece wears its side's picture
            WrapTex tex = wrapFace >= 0 && wrapFace < BusInterior.WrapFaceCount ? s_WrapTex[wrapFace] : null;
            var nm = math.transpose(math.inverse(new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz)));
            var P = new float3[d.P.Length];
            var Cv = new float3[d.P.Length];
            var fin = new Fin[d.P.Length];
            for (int s = 0; s < d.SubFin.Length; s++)
                for (int t = d.SubStart[s]; t < d.SubStart[s] + d.SubCount[s]; t++) fin[d.Idx[t]] = d.SubFin[s];
            for (int k = 0; k < d.P.Length; k++)
            {
                P[k] = math.transform(m, d.P[k]);
                float3 n = math.normalize(math.mul(nm, d.N[k]));
                Cv[k] = tex != null ? new float3(Lit(n, sun)) : Shade(d, k, fin[k], n, sun, v.Eye - P[k]);
            }
            for (int s = 0; s < d.SubFin.Length; s++)
            {
                bool emissive = KitPalette.IsLight(d.SubFin[s]);
                for (int t = d.SubStart[s]; t < d.SubStart[s] + d.SubCount[s]; t += 3)
                {
                    int a = d.Idx[t], b = d.Idx[t + 1], cc = d.Idx[t + 2];
                    if (P[a].y > v.CutY || P[b].y > v.CutY || P[cc].y > v.CutY) continue;
                    float3 g = math.cross(P[b] - P[a], P[cc] - P[a]);
                    bool front = math.dot(g, v.Eye - P[a]) > 0f;
                    float3 ca = Cv[a], cb = Cv[b], ccol = Cv[cc];
                    if (!front) ca = cb = ccol = new float3(1f, 0f, 1f);
                    else if (emissive) ca = cb = ccol = KitPalette.FlatSrgb(d.SubFin[s]) * 1.1f;
                    if (tex != null && !front) continue;
                    Tri(View(P[a], v.Eye, f, rt, up), View(P[b], v.Eye, f, rt, up), View(P[cc], v.Eye, f, rt, up), ca, cb, ccol, focal, color, depth, !front, tex, d.UV[a], d.UV[b], d.UV[cc]);
                }
            }
        }
        if (s_Signs != null)
        {
            float3 amber = new float3(1f, 0.62f, 0.12f);
            for (int t = 0; t + 2 < s_Signs.Count; t += 3)
            {
                float3 a = s_Signs[t], b = s_Signs[t + 1], cc = s_Signs[t + 2];
                if (math.dot(math.cross(b - a, cc - a), v.Eye - a) <= 0f) continue;      // the dots face one way
                Tri(View(a, v.Eye, f, rt, up), View(b, v.Eye, f, rt, up), View(cc, v.Eye, f, rt, up), amber, amber, amber, focal, color, depth, false);
            }
        }
        var img = new byte[W * H * 3];
        for (int i = 0; i < W * H; i++)
        {
            float3 c = math.saturate(color[i]);
            img[i * 3] = (byte)(c.x * 255f); img[i * 3 + 1] = (byte)(c.y * 255f); img[i * 3 + 2] = (byte)(c.z * 255f);
        }
        WritePng(path, W, H, img);
    }

    // sky=leak: a saturated green sky, so a slit between two panels that lets the sky through stands out
    static float3 Sky(int y) => Opt(s_Args, "sky", "") == "leak" ? new float3(0f, 1f, 0f) :
        math.lerp(new float3(0.62f, 0.72f, 0.82f), new float3(0.80f, 0.80f, 0.76f), y / (float)H);

    static float3 View(float3 p, float3 eye, float3 f, float3 r, float3 u) { float3 d = p - eye; return new float3(math.dot(d, r), math.dot(d, u), math.dot(d, f)); }

    const float kNear = 0.03f;

    // Clips the triangle at the near plane (z = kNear) and rasterises the 1-2 resulting triangles.
    static void Tri(float3 a, float3 b, float3 c, float3 ca, float3 cb, float3 cc, float focal, float3[] color, float[] depth, bool back,
        WrapTex tex = null, float2 ua = default, float2 ub = default, float2 uc = default)
    {
        var vp = new float3[4];
        var vc = new float3[4];
        var vu = new float2[4];
        int n = 0;
        float3[] p = { a, b, c };
        float3[] col = { ca, cb, cc };
        float2[] uvs = { ua, ub, uc };
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3;
            bool inI = p[i].z >= kNear, inJ = p[j].z >= kNear;
            if (inI) { vp[n] = p[i]; vc[n] = col[i]; vu[n] = uvs[i]; n++; }
            if (inI != inJ)
            {
                float t = (kNear - p[i].z) / (p[j].z - p[i].z);
                vp[n] = math.lerp(p[i], p[j], t); vc[n] = math.lerp(col[i], col[j], t); vu[n] = math.lerp(uvs[i], uvs[j], t); n++;
            }
        }
        if (n < 3) return;
        Raster(vp[0], vp[1], vp[2], vc[0], vc[1], vc[2], focal, color, depth, back, tex, vu[0], vu[1], vu[2]);
        if (n == 4) Raster(vp[0], vp[2], vp[3], vc[0], vc[2], vc[3], focal, color, depth, back, tex, vu[0], vu[2], vu[3]);
    }

    static float2 Proj(float3 v, float focal) => new float2(0.5f * W + v.x / v.z * focal, 0.5f * H - v.y / v.z * focal);
    static float Edge(float2 a, float2 b, float2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    static void Raster(float3 va, float3 vb, float3 vc, float3 ca, float3 cb, float3 cc, float focal, float3[] color, float[] depth, bool back,
        WrapTex tex = null, float2 ua = default, float2 ub = default, float2 uc = default)
    {
        float2 pa = Proj(va, focal), pb = Proj(vb, focal), pc = Proj(vc, focal);
        float area = Edge(pa, pb, pc);
        if (math.abs(area) < 1e-7f) return;
        int x0 = math.max(0, (int)math.floor(math.cmin(new float3(pa.x, pb.x, pc.x)))), x1 = math.min(W - 1, (int)math.ceil(math.cmax(new float3(pa.x, pb.x, pc.x))));
        int y0 = math.max(0, (int)math.floor(math.cmin(new float3(pa.y, pb.y, pc.y)))), y1 = math.min(H - 1, (int)math.ceil(math.cmax(new float3(pa.y, pb.y, pc.y))));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float2 q = new float2(x + 0.5f, y + 0.5f);
                float w0 = Edge(pb, pc, q) / area, w1 = Edge(pc, pa, q) / area, w2 = Edge(pa, pb, q) / area;
                if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                float z = 1f / (w0 / va.z + w1 / vb.z + w2 / vc.z);
                int i = y * W + x;
                float zz = back ? z * 1.002f + 0.002f : z;
                if (zz >= depth[i]) continue;
                float3 lit = (ca * (w0 / va.z) + cb * (w1 / vb.z) + cc * (w2 / vc.z)) * z;
                if (tex != null)
                {
                    float2 uv = (ua * (w0 / va.z) + ub * (w1 / vb.z) + uc * (w2 / vc.z)) * z;
                    float3 texel = tex.Sample(uv, out bool opaque);
                    if (!opaque) continue;                 // cut out: the body shows
                    lit = texel * lit;
                }
                depth[i] = zz;
                color[i] = lit;
            }
    }

    // The wrap pictures for the preview (wrap=<left.png> wraprear=... : any of wrap, wrapright, wrapfront, wraprear, wraproof): the faces
    // that have a picture are shown, a side without its picture takes the other side's (as BusSkins does). The pictures are 8-bit RGB/RGBA PNGs.
    sealed class WrapTex
    {
        public int W, H;
        public byte[] Rgba;
        public bool Mirror;
        public float2 Share = new float2(0f, 1f);       // the section's part of the picture's width (an along face of an articulated bus)

        public float3 Sample(float2 uv, out bool opaque)
        {
            float along = Share.x + Share.y * uv.x;
            float u = Mirror ? 1f - along : along;
            int x = math.clamp((int)math.floor(u * W), 0, W - 1), y = math.clamp((int)math.floor((1f - uv.y) * H), 0, H - 1);
            int i = (y * W + x) * 4;
            opaque = Rgba[i + 3] >= 128;
            return new float3(Rgba[i], Rgba[i + 1], Rgba[i + 2]) / 255f;
        }
    }

    static readonly WrapTex[] s_WrapTex = new WrapTex[BusInterior.WrapFaceCount];
    static readonly System.Collections.Generic.Dictionary<int, int> s_DoorPieces = new System.Collections.Generic.Dictionary<int, int>();

    static void FlipRows(byte[] rgba, int w, int h)
    {
        var row = new byte[w * 4];
        for (int y = 0; y < h / 2; y++)
        {
            Buffer.BlockCopy(rgba, y * w * 4, row, 0, w * 4);
            Buffer.BlockCopy(rgba, (h - 1 - y) * w * 4, rgba, y * w * 4, w * 4);
            Buffer.BlockCopy(row, 0, rgba, (h - 1 - y) * w * 4, w * 4);
        }
    }

    static BusInterior.WrapFaces LoadWrapOptions(string[] args, BusInterior bus)
    {
        string[] keys = { "wrap", "wrapright", "wrapfront", "wraprear", "wraproof" };
        var loaded = new WrapTex[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            string path = Opt(args, keys[i], "");
            if (path.Length > 0) loaded[i] = LoadPng(path);
        }
        string zip = Opt(args, "wrapzip", "");
        if (zip.Length > 0)
        {
            // a wrap zip as the game reads it: WrapPackage.Read, the model and layout check, then the same picture work (WrapPackage.Prepare)
            WrapPackage.Package package = WrapPackage.Read(zip);
            VehicleProfile model = s_WrapModel ?? bus.AuthoredModel;        // an articulated bus's wraps are its front section's model's
            string mismatch = model == null ? "the preview model is not an authored bus (use spec=sunwin)" : WrapPackage.Mismatch(package.Manifest, model.Name, model.WrapLayout);
            Console.WriteLine("  wrap zip: model " + package.Manifest.Model + ", layout " + package.Manifest.Layout + ", outline " + package.Manifest.OutlineMm + " mm; " + (mismatch ?? "fits the model"));
            if (mismatch != null) throw new InvalidDataException(mismatch);
            var have = new bool[keys.Length];
            for (int i = 0; i < keys.Length; i++) have[i] = package.Faces[i] != null;
            for (int i = 0; i < keys.Length; i++)
            {
                if (package.Faces[i] == null) continue;
                WrapTex raw = LoadPngBytes(package.Faces[i], WrapPackage.Pictures[i]);
                FlipRows(raw.Rgba, raw.W, raw.H);                                    // the game's pictures run from the bottom row
                int[] faceList = WrapPackage.FacesOf(i, have);
                var areas = new System.Collections.Generic.List<(byte, float2x3)>();
                // a section's areas in the whole picture, as BusSkins.Vehicle.Areas places them (the right side from the other end)
                foreach (int face in faceList)
                {
                    bool along = WrapTemplate.Along((BusInterior.WrapFace)face), right = face == (int)BusInterior.WrapFace.Right;
                    float X(float u) { float x = along ? s_Share.x + s_Share.y * u : u; return right ? 1f - x : x; }
                    foreach ((byte kind, float2x3 uv) in bus.WrapAreas((BusInterior.WrapFace)face))
                        areas.Add((kind, new float2x3(new float2(X(uv.c0.x), uv.c0.y), new float2(X(uv.c1.x), uv.c1.y), new float2(X(uv.c2.x), uv.c2.y))));
                }
                float metres = bus.WrapSize((BusInterior.WrapFace)faceList[0]).x / (WrapTemplate.Along((BusInterior.WrapFace)faceList[0]) ? s_Share.y : 1f);
                bool see = WrapPackage.Prepare(raw.Rgba, raw.W, raw.H, areas, package.Manifest.OutlineMm, metres, out int outlined, out int recoloured);
                string dump = Opt(args, "dump", "");
                if (dump.Length > 0)
                {
                    // what the game feeds the shader (the picture after the white outline and the colour bleed), as a PNG with alpha
                    System.IO.Directory.CreateDirectory(dump);
                    File.WriteAllBytes(Path.Combine(dump, WrapPackage.Pictures[i]), WrapTemplate.Png(raw.W, raw.H, raw.Rgba));
                }
                FlipRows(raw.Rgba, raw.W, raw.H);
                Console.WriteLine($"  {WrapPackage.Pictures[i],-9} {raw.W} x {raw.H}: {(see ? $"see-through pixels, {outlined} outlined white, {recoloured} see-through pixels recoloured" : "opaque, nothing to cut")}");
                loaded[i] = raw;
            }
        }
        var faces = BusInterior.WrapFaces.None;
        for (int i = 0; i < keys.Length; i++) s_WrapTex[i] = null;
        if (loaded[0] != null || loaded[1] != null)
        {
            s_WrapTex[0] = loaded[0] ?? loaded[1];
            s_WrapTex[1] = loaded[1] ?? loaded[0];
            s_WrapTex[1] = new WrapTex { W = s_WrapTex[1].W, H = s_WrapTex[1].H, Rgba = s_WrapTex[1].Rgba };
            s_WrapTex[0] = new WrapTex { W = s_WrapTex[0].W, H = s_WrapTex[0].H, Rgba = s_WrapTex[0].Rgba };
            s_WrapTex[1].Mirror = true;                      // the right side: the front at the picture's right edge (BusSkins.Material)
            faces |= BusInterior.WrapFaces.Left | BusInterior.WrapFaces.Right;
        }
        if (loaded[2] != null) { s_WrapTex[2] = loaded[2]; faces |= BusInterior.WrapFaces.Front; }
        if (loaded[3] != null) { s_WrapTex[3] = loaded[3]; faces |= BusInterior.WrapFaces.Rear; }
        if (loaded[4] != null) { s_WrapTex[4] = new WrapTex { W = loaded[4].W, H = loaded[4].H, Rgba = loaded[4].Rgba }; faces |= BusInterior.WrapFaces.Roof; }
        foreach (int along in new[] { 0, 1, 4 })
            if (s_WrapTex[along] != null) s_WrapTex[along].Share = s_Share;
        if (faces != BusInterior.WrapFaces.None)
        {
            float2 side = bus.WrapSize(BusInterior.WrapFace.Left);
            Console.WriteLine($"  wrap {faces}: the side face is {side.x:0.00} x {side.y:0.00} m (picture shape {side.x / side.y:0.00} : 1); left picture {s_WrapTex[0]?.W} x {s_WrapTex[0]?.H} (shape {(float)(s_WrapTex[0]?.W ?? 1) / (s_WrapTex[0]?.H ?? 1):0.00} : 1)");
        }
        if (faces != BusInterior.WrapFaces.None)
        {
            // where the generated bus's side wrap shows: the pieces (z0, y0, z1, y1 in metres) as shares of the picture's height
            float bottom = BusInterior.WrapBottom, height = bus.WrapSize(BusInterior.WrapFace.Left).y;
            foreach (float4 r in bus.WrapRects(-1f))
                if (r.z - r.x > 0.8f) Console.WriteLine($"  side wrap piece {r.z - r.x:0.0} m long, from picture height {(r.y - bottom) / height:0.00} to {(r.w - bottom) / height:0.00} (y {r.y:0.00} .. {r.w:0.00} m)");
        }
        return faces;
    }

    static WrapTex LoadPng(string path) => LoadPngBytes(File.ReadAllBytes(path), path);

    static WrapTex LoadPngBytes(byte[] data, string path)
    {
        if (data.Length < 33 || data[1] != 'P') throw new InvalidDataException(path + " is not a PNG");
        int pos = 8, width = 0, height = 0, depth = 0, colour = 0, interlace = 0;
        var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int n = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            string kind = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (kind == "IHDR")
            {
                width = (data[pos + 8] << 24) | (data[pos + 9] << 16) | (data[pos + 10] << 8) | data[pos + 11];
                height = (data[pos + 12] << 24) | (data[pos + 13] << 16) | (data[pos + 14] << 8) | data[pos + 15];
                depth = data[pos + 16]; colour = data[pos + 17]; interlace = data[pos + 20];
            }
            else if (kind == "IDAT") idat.Write(data, pos + 8, n);
            pos += 12 + n;
        }
        if (depth != 8 || (colour != 2 && colour != 6) || interlace != 0) throw new InvalidDataException(path + ": only 8-bit RGB/RGBA PNGs that are not interlaced are read");
        int bpp = colour == 2 ? 3 : 4, stride = width * bpp;
        idat.Position = 2;
        var raw = new byte[(stride + 1) * height];
        using (var z = new DeflateStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length) { int k = z.Read(raw, read, raw.Length - read); if (k <= 0) break; read += k; }
        }
        var rgba = new byte[width * height * 4];
        var prev = new byte[stride];
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int f = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, line, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0, add = 0;
                switch (f)
                {
                    case 1: add = a; break;
                    case 2: add = b; break;
                    case 3: add = (a + b) / 2; break;
                    case 4: { int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c); add = pa <= pb && pa <= pc ? a : (pb <= pc ? b : c); break; }
                }
                line[i] = (byte)(line[i] + add);
            }
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                rgba[o] = line[x * bpp]; rgba[o + 1] = line[x * bpp + 1]; rgba[o + 2] = line[x * bpp + 2]; rgba[o + 3] = bpp == 4 ? line[x * bpp + 3] : (byte)255;
            }
            var swap = prev; prev = line; line = swap;
        }
        return new WrapTex { W = width, H = height, Rgba = rgba };
    }

    static float Lit(float3 n, float3 sun)
    {
        float lam = math.max(0f, math.dot(n, sun));
        float fill = 0.5f + 0.5f * math.saturate(n.y * 0.5f + 0.5f);
        return 0.3f * fill + 0.55f * lam + 0.15f;
    }

    static float3 Shade(KitMeshData d, int k, Fin fin, float3 n, float3 sun, float3 toEye)
    {
        float2 uv = d.UV[k];
        float3 albedo;
        if (KitPalette.UsesPalette(fin))
        {
            int row = math.clamp((int)math.floor(uv.y * KitPalette.Rows), 0, KitPalette.Rows - 1);
            float ramp = math.saturate((uv.x * KitPalette.Width - 0.5f) / (KitPalette.Width - 1));
            albedo = KitPalette.SurfaceSrgb(fin, (Sw)row, ramp);
        }
        else if (fin == Fin.Hazard)
        {
            float s = (uv.x + uv.y) * 2f;
            albedo = ((int)math.floor(s) & 1) == 0 ? KitPalette.Srgb(Sw.Yellow) : new float3(0.04f);
        }
        else albedo = KitPalette.FlatSrgb(fin);
        float lam = math.max(0f, math.dot(n, sun));
        float fill = 0.5f + 0.5f * math.saturate(n.y * 0.5f + 0.5f);          // sky fill: interiors are lit by the windows
        float spec = fin == Fin.Chrome || fin == Fin.Metal ? math.pow(math.max(0f, math.dot(math.reflect(-sun, n), math.normalize(toEye))), 24f) * 0.5f : 0f;
        if (fin == Fin.Glass)
        {
            float3 v = math.normalize(toEye);
            float fres = 0.04f + 0.96f * math.pow(1f - math.saturate(math.dot(n, v)), 5f);
            return KitPalette.ToSrgb(KitPalette.ToLinear(albedo) * (0.6f + 1.2f * lam) + fres * KitPalette.ToLinear(new float3(0.6f, 0.7f, 0.8f)));
        }
        return albedo * (0.3f * fill + 0.55f * lam + 0.15f) + spec;
    }

    // ---- minimal PNG writer
    static void WritePng(string path, int w, int h, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        var raw = new byte[(w * 3 + 1) * h];
        for (int y = 0; y < h; y++) { raw[y * (w * 3 + 1)] = 0; Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3); }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", new byte[0]);
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, (int)Crc(td)); s.Write(crc);
    }

    static uint[] s_Crc;
    static uint Crc(byte[] d)
    {
        if (s_Crc == null)
        {
            s_Crc = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; s_Crc[n] = c; }
        }
        uint r = 0xFFFFFFFFu;
        foreach (byte b in d) r = s_Crc[(r ^ b) & 0xFF] ^ (r >> 8);
        return r ^ 0xFFFFFFFFu;
    }
}
