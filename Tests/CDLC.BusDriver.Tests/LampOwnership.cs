using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using CDLC.BusDriver;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;

// How the native cab splits a pack's game asset (NativeCabOwnership.Split), measured on the pack's own files: the profile, its runtime
// mesh and the LOD0 geometry in its .cok. A triangle of the asset is a copy of a runtime part when its three corners lie on that
// part's corners. The copies of the lamp parts 23..28 are the asset's unlit lamp faces: the cab hides each while its lamp is lit (the
// runtime mesh draws the lit face at the same place; the ZK6128H session's report of 12 November).
// Reported: the lamp faces by lamp part with their area and how far they are from the runtime faces; whether any of them is a triangle
// of the static parts as well (the dark housing); and, by rays along the body axis a face looks along, what is behind each lamp face
// when it is hidden (nothing within 300 mm would be a hole under a lit face that does not cover it) and what stands in front of it (an
// opaque surface there hides the lit runtime face; glass does not).
// Usage: lamp-ownership <pack folder> <model> [<model> ...]   (e.g. the installed Mods/CDLC.Bus.ZK6128H and ZK6128H)
static unsafe class LampOwnership
{
    static readonly string[] s_LampNames = { "HeadLamps", "TailLamps", "BrakeLamps", "IndicatorL", "IndicatorR", "ReverseLamps" };
    const float kReach = 0.3f;

    sealed class Tally
    {
        internal int Triangles;
        internal double Area;
        internal float3 Min = new float3(float.MaxValue), Max = new float3(float.MinValue);
        internal void Add(float3 a, float3 b, float3 c)
        {
            Triangles++;
            Area += 0.5 * math.length(math.cross(b - a, c - a));
            Min = math.min(Min, math.min(a, math.min(b, c)));
            Max = math.max(Max, math.max(a, math.max(b, c)));
        }
        internal string Box => Triangles == 0 ? "-" : $"x {Min.x:0.000}..{Max.x:0.000}  y {Min.y:0.000}..{Max.y:0.000}  z {Min.z:0.000}..{Max.z:0.000}";
    }

    internal static int Run(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("lamp-ownership <pack folder> <model> [<model> ...]"); return 2; }
        GeometryReadTests.Plugin();
        int failures = 0;
        for (int i = 2; i < args.Length; i++)
            try { Report(args[1], args[i]); }
            catch (Exception e) { failures++; Console.WriteLine($"{args[i]}: FAILED {e.GetType().Name}: {e.Message}"); }
        return failures == 0 ? 0 : 1;
    }

    static void Report(string folder, string model)
    {
        string profilePath = Path.Combine(folder, model + ".cdlb.json");
        VehicleProfile profile = VehicleModels.Register(VehicleProfileJson.LoadForPrefab(profilePath, model));
        VehicleMesh mesh = profile.Mesh;
        string package = Directory.GetFiles(folder, "*.cok").Single();
        Console.WriteLine($"== {model}: profile {Sha(profilePath)} ({profile.Spec.Section}, {profile.Spec.RearZ:0.00}..{profile.Spec.FrontZ:0.00} m), " +
            $"package {Path.GetFileName(package)} {Sha(package)}, native cab {profile.NativeCab}");

        int[] lamps = Enumerable.Range(BusInterior.HeadLamps, BusInterior.ReverseLamps - BusInterior.HeadLamps + 1).ToArray();
        Console.WriteLine("  runtime lamp parts (LOD0 of the runtime mesh):");
        var corners = new List<float3>();
        foreach (int part in lamps)
        {
            corners.Clear();
            mesh.Corners(part, 0, corners);
            var own = new Tally();
            for (int c = 0; c + 2 < corners.Count; c += 3) own.Add(corners[c], corners[c + 1], corners[c + 2]);
            Console.WriteLine($"    {part} {s_LampNames[part - BusInterior.HeadLamps],-12} {own.Triangles,7} triangles {own.Area * 1e4,8:0.0} cm2  {own.Box}");
        }

        // the cab's own split (dropped, a lamp face of one or more lamp parts, body), and the static parts the asset stands in for
        var split = new NativeCabOwnership.Split(profile);
        HashSet<(long, long, long)> staticTriangles = TriangleKeys(mesh, NativeCabOwnership.ReplacedParts(mesh));
        HashSet<(long, long, long)> lampTriangles = TriangleKeys(mesh, lamps);
        Console.WriteLine($"  runtime mesh: {lampTriangles.Count} distinct lamp triangles, {lampTriangles.Count(staticTriangles.Contains)} of them are triangles of the static parts too");

        // every lamp corner of the runtime mesh in 2 mm cells: how far the asset's copies are from the runtime faces
        var lampCells = new Dictionary<(int, int, int), List<float3>>();
        corners.Clear();
        foreach (int part in lamps) mesh.Corners(part, 0, corners);
        foreach (float3 p in corners)
        {
            var cell = ((int)math.floor(p.x / 0.002f), (int)math.floor(p.y / 0.002f), (int)math.floor(p.z / 0.002f));
            if (!lampCells.TryGetValue(cell, out List<float3> list)) lampCells[cell] = list = new List<float3>();
            list.Add(p);
        }
        float Nearest(float3 p)
        {
            float best = float.MaxValue;
            int cx = (int)math.floor(p.x / 0.002f), cy = (int)math.floor(p.y / 0.002f), cz = (int)math.floor(p.z / 0.002f);
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                    for (int dz = -2; dz <= 2; dz++)
                        if (lampCells.TryGetValue((cx + dx, cy + dy, cz + dz), out List<float3> list))
                            foreach (float3 q in list) best = math.min(best, math.distance(p, q));
            return best;
        }

        var opaque = new List<float3>();        // what the cab would still draw of the asset: not dropped now, not a lamp copy
        var glass = new List<float3>();         // the asset's glass (drawn by BusGlass)
        var copies = new List<(int part, float3 a, float3 b, float3 c)>();
        foreach ((string name, byte[] bytes, bool[] isGlass) in Lod0Geometry(package, model))
        {
            (int meshes, uint flags) = NativeGeometryFile.ReadPrefix(bytes);
            var infos = new byte[NativeGeometryFile.InfosBytes(meshes)];
            Array.Copy(bytes, NativeGeometryFile.kPrefixBytes, infos, 0, infos.Length);
            var format = new NativeGeometryFile(meshes, flags, infos);
            using var body = new NativeBlock(format.BodyEnd - format.SubMeshesAt);
            Marshal.Copy(bytes, (int)format.SubMeshesAt, body.Pointer, (int)body.Length);
            using NativeBlock decoded = NativeGeometryDecode.Decode(format, body);
            Console.WriteLine($"  geometry {name}: {format.Meshes.Length} mesh(es), {format.SubMeshes.Length} submesh(es)");

            for (int s = 0; s < format.SubMeshes.Length; s++)
            {
                NativeGeometryFile.SubMesh sub = format.SubMeshes[s];
                NativeGeometryFile.MeshInfo info = format.Meshes[sub.Mesh];
                byte* indices = decoded.Bytes + format.DecodedIndicesAt[sub.Mesh];
                byte* positions = decoded.Bytes + format.DecodedAttributeAt[sub.Mesh, NativeGeometryFile.kPosition];
                int fmt = info.Format[NativeGeometryFile.kPosition], dim = info.Dimension[NativeGeometryFile.kPosition];
                bool glassSurface = s < isGlass.Length && isGlass[s];
                var total = new Tally();
                var droppedNow = new Tally();
                var extra = new Tally();
                int alsoStatic = 0, sharedFaces = 0;
                var byLamp = lamps.ToDictionary(p => p, _ => new Tally());
                var offsets = new List<float>();
                for (int t = 0; t < sub.IndexCount; t += 3)
                {
                    float3 a = NativeMeshCut.Position(positions, fmt, dim, Index(indices, info, sub.IndexStart + t));
                    float3 b = NativeMeshCut.Position(positions, fmt, dim, Index(indices, info, sub.IndexStart + t + 1));
                    float3 c = NativeMeshCut.Position(positions, fmt, dim, Index(indices, info, sub.IndexStart + t + 2));
                    total.Add(a, b, c);
                    if (glassSurface) { glass.Add(a); glass.Add(b); glass.Add(c); continue; }
                    int group = split.GroupOf(a, b, c);
                    if (group == NativeMeshCut.Drop) { droppedNow.Add(a, b, c); continue; }
                    if (group == NativeMeshCut.Body) { opaque.Add(a); opaque.Add(b); opaque.Add(c); continue; }
                    int lit = NativeCabOwnership.LampsOf(group);
                    if ((lit & (lit - 1)) != 0) sharedFaces++;
                    extra.Add(a, b, c);
                    offsets.Add(math.max(Nearest(a), math.max(Nearest(b), Nearest(c))));
                    if (staticTriangles.Contains(TriangleKey(a, b, c))) alsoStatic++;
                    foreach (int part in lamps)
                        if ((lit & NativeCabOwnership.LampBit(part)) != 0) { byLamp[part].Add(a, b, c); copies.Add((part, a, b, c)); break; }
                }
                if (glassSurface)
                {
                    Console.WriteLine($"    submesh {s} (glass, drawn by BusGlass, not cut by the cab): {total.Triangles} triangles {total.Area:0.00} m2");
                    continue;
                }
                Console.WriteLine($"    submesh {s} (opaque): {total.Triangles} triangles {total.Area:0.00} m2; dropped (moving parts, readers) {droppedNow.Triangles} ({droppedNow.Area:0.000} m2); " +
                    $"lamp faces, hidden while lit: {extra.Triangles} ({extra.Area * 1e4:0.0} cm2), {sharedFaces} shared by two lamp parts, {alsoStatic} of them are a static part's triangle too");
                if (offsets.Count > 0)
                {
                    offsets.Sort();
                    Console.WriteLine($"      their corners from the runtime lamp corners: median {offsets[offsets.Count / 2] * 1e6:0} um, worst {offsets[offsets.Count - 1] * 1e6:0} um");
                }
                foreach (int part in lamps)
                    if (byLamp[part].Triangles > 0)
                        Console.WriteLine($"      lamp {part} {s_LampNames[part - BusInterior.HeadLamps],-12} {byLamp[part].Triangles,7} triangles {byLamp[part].Area * 1e4,8:0.0} cm2  {byLamp[part].Box}");
            }
        }
        LampOffState(copies, opaque, glass, (profile.Spec.FrontZ + profile.Spec.RearZ) / 2f);
    }

    // For every lamp copy that faces along the body (forward, backward or sideways): from its centre, the nearest of the asset's
    // remaining opaque triangles behind it (towards the inside of the bus) and in front of it, and the nearest glass in front of it.
    static void LampOffState(List<(int part, float3 a, float3 b, float3 c)> copies, List<float3> opaque, List<float3> glass, float midZ)
    {
        if (copies.Count == 0) { Console.WriteLine("  no copies of the lamp parts in the asset: nothing would change"); return; }
        Console.WriteLine($"  with the copies gone, by rays from each lamp face (reach {kReach * 1000:0} mm):");
        foreach (IGrouping<int, (int part, float3 a, float3 b, float3 c)> group in copies.GroupBy(c => c.part).OrderBy(g => g.Key))
        {
            float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
            foreach (var l in group) { lo = math.min(lo, math.min(l.a, math.min(l.b, l.c))); hi = math.max(hi, math.max(l.a, math.max(l.b, l.c))); }
            float3[] solid = Near(opaque, lo - kReach, hi + kReach), panes = Near(glass, lo - kReach, hi + kReach);
            double cast = 0, skipped = 0, covered = 0, behindGlass = 0;
            var behind = new double[5];           // <= 2 mm, <= 10 mm, <= 50 mm, <= reach, nothing
            var coverAt = new List<float>();
            var holes = new Tally();
            var coveredFront = new Tally();        // covered lamp faces at the front of the bus (z above the middle), and at the rear
            var coveredRear = new Tally();
            foreach (var l in group)
            {
                float3 cross = math.cross(l.b - l.a, l.c - l.a);
                double area = 0.5 * math.length(cross);
                if (area < 1e-13) continue;
                float3 n = math.abs(cross / math.length(cross)), centre = (l.a + l.b + l.c) / 3f;
                bool alongZ = n.z >= n.x;
                if (math.max(n.z, n.x) < 0.3f) { skipped += area; continue; }      // faces up or down: no body axis to look along
                float sign = alongZ ? (centre.z > midZ ? 1f : -1f) : math.sign(centre.x);
                cast += area;
                Ray(solid, centre, alongZ, sign, out float front, out float back);
                Ray(panes, centre, alongZ, sign, out float glassFront, out _);
                if (front != float.MaxValue)
                {
                    covered += area;
                    coverAt.Add(front);
                    (centre.z > midZ ? coveredFront : coveredRear).Add(l.a, l.b, l.c);
                }
                if (glassFront != float.MaxValue) behindGlass += area;
                behind[back <= 0.002f ? 0 : back <= 0.010f ? 1 : back <= 0.050f ? 2 : back != float.MaxValue ? 3 : 4] += area;
                if (back == float.MaxValue) holes.Add(l.a, l.b, l.c);
            }
            double f = Math.Max(cast, 1e-13);
            coverAt.Sort();
            Console.WriteLine($"    lamp {group.Key} {s_LampNames[group.Key - BusInterior.HeadLamps],-12} {cast * 1e4,7:0.0} cm2 looked along ({skipped * 1e4:0.0} cm2 faces up or down); " +
                $"behind it: <=2 mm {behind[0] / f:P0}, <=10 mm {behind[1] / f:P0}, <=50 mm {behind[2] / f:P0}, <=300 mm {behind[3] / f:P0}, NOTHING {behind[4] / f:P0}; " +
                $"opaque in front {covered / f:P0}{(coverAt.Count > 0 ? $" (median {coverAt[coverAt.Count / 2] * 1000:0.0} mm)" : "")}; glass in front {behindGlass / f:P0}");
            if (holes.Triangles > 0)
                Console.WriteLine($"      nothing behind: {holes.Triangles} triangles {holes.Area * 1e4:0.0} cm2  {holes.Box}");
            if (coverAt.Count > 0)
                Console.WriteLine($"      opaque in front at {coverAt[0] * 1000:0.0} .. {coverAt[coverAt.Count - 1] * 1000:0.0} mm (10% {coverAt[coverAt.Count / 10] * 1000:0.0}, 90% {coverAt[coverAt.Count * 9 / 10] * 1000:0.0})");
            if (coveredFront.Triangles > 0)
                Console.WriteLine($"      covered faces at the front: {coveredFront.Triangles} triangles {coveredFront.Area * 1e4:0.0} cm2  {coveredFront.Box}");
            if (coveredRear.Triangles > 0)
                Console.WriteLine($"      covered faces at the rear:  {coveredRear.Triangles} triangles {coveredRear.Area * 1e4:0.0} cm2  {coveredRear.Box}");
        }
    }

    static float3[] Near(List<float3> triangles, float3 lo, float3 hi)
    {
        var near = new List<float3>();
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            float3 tmin = math.min(triangles[i], math.min(triangles[i + 1], triangles[i + 2])), tmax = math.max(triangles[i], math.max(triangles[i + 1], triangles[i + 2]));
            if (math.all(tmax >= lo) && math.all(tmin <= hi)) { near.Add(triangles[i]); near.Add(triangles[i + 1]); near.Add(triangles[i + 2]); }
        }
        return near.ToArray();
    }

    // A ray through `from` along the body's Z (or X) axis: the nearest triangle on the outer side (front, metres) and on the inner side (back).
    static void Ray(float3[] triangles, float3 from, bool alongZ, float sign, out float front, out float back)
    {
        const float eps = 0.00005f;
        front = back = float.MaxValue;
        float u0 = alongZ ? from.x : from.z, v0 = from.y, d0 = alongZ ? from.z : from.x;
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            float3 p0 = triangles[i], p1 = triangles[i + 1], p2 = triangles[i + 2];
            float a0 = alongZ ? p0.x : p0.z, a1 = alongZ ? p1.x : p1.z, a2 = alongZ ? p2.x : p2.z;
            if (u0 < math.min(a0, math.min(a1, a2)) || u0 > math.max(a0, math.max(a1, a2)) ||
                v0 < math.min(p0.y, math.min(p1.y, p2.y)) || v0 > math.max(p0.y, math.max(p1.y, p2.y))) continue;
            float e1u = a1 - a0, e1v = p1.y - p0.y, e2u = a2 - a0, e2v = p2.y - p0.y;
            float det = e1u * e2v - e2u * e1v;
            if (math.abs(det) < 1e-12f) continue;
            float bu = ((u0 - a0) * e2v - e2u * (v0 - p0.y)) / det, bv = (e1u * (v0 - p0.y) - (u0 - a0) * e1v) / det;
            if (bu < -1e-4f || bv < -1e-4f || bu + bv > 1.0001f) continue;
            float q0 = alongZ ? p0.z : p0.x, q1 = alongZ ? p1.z : p1.x, q2 = alongZ ? p2.z : p2.x;
            float along = (q0 + bu * (q1 - q0) + bv * (q2 - q0) - d0) * sign;
            if (along > eps && along <= kReach) front = math.min(front, along);
            else if (along < -eps && along >= -kReach) back = math.min(back, -along);
        }
    }

    static long Cell(float3 p)
    {
        int3 c = (int3)math.round(p / 0.0005f);
        return ((long)(c.x & 0x1FFFFF) << 42) | ((long)(c.y & 0x1FFFFF) << 21) | (long)(c.z & 0x1FFFFF);
    }

    // a triangle by its three corners on a 0.5 mm grid, whatever their order
    static (long, long, long) TriangleKey(float3 a, float3 b, float3 c)
    {
        long x = Cell(a), y = Cell(b), z = Cell(c);
        if (x > y) (x, y) = (y, x);
        if (y > z) (y, z) = (z, y);
        if (x > y) (x, y) = (y, x);
        return (x, y, z);
    }

    static HashSet<(long, long, long)> TriangleKeys(VehicleMesh mesh, IEnumerable<int> parts)
    {
        var keys = new HashSet<(long, long, long)>();
        var corners = new List<float3>();
        foreach (int part in parts)
        {
            corners.Clear();
            mesh.Corners(part, 0, corners);
            for (int i = 0; i + 2 < corners.Count; i += 3) keys.Add(TriangleKey(corners[i], corners[i + 1], corners[i + 2]));
        }
        return keys;
    }

    static int Index(byte* indices, NativeGeometryFile.MeshInfo mesh, int i) => mesh.WideIndices ? ((int*)indices)[i] : ((ushort*)indices)[i];

    // The LOD0 geometry of the model's own mesh prefab(s) ("<model> Mesh_*.Prefab" and "<model>SkinN Mesh" palettes name it by content
    // ID), with which of its surfaces are glass (the importer names glass "_Gls": BusGlass.IsGlass).
    static IEnumerable<(string, byte[], bool[])> Lod0Geometry(string package, string model)
    {
        using ZipArchive zip = ZipFile.OpenRead(package);
        var geometries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var surfaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName.EndsWith(".cid", StringComparison.OrdinalIgnoreCase)))
        {
            string id;
            using (var reader = new StreamReader(entry.Open())) id = reader.ReadToEnd().Trim();
            string target = entry.FullName.Substring(0, entry.FullName.Length - 4);
            if (target.EndsWith(".Geometry", StringComparison.OrdinalIgnoreCase)) geometries[id] = zip.GetEntry(target);
            else if (target.EndsWith(".Surface", StringComparison.OrdinalIgnoreCase)) surfaces[id] = target;
        }
        var seen = new HashSet<string>();
        foreach (ZipArchiveEntry prefab in zip.Entries.Where(e => e.FullName.EndsWith(".Prefab", StringComparison.OrdinalIgnoreCase) && e.FullName.Contains(" Mesh_") &&
                     !e.FullName.Contains("_LOD") && (e.FullName.StartsWith(model + " Mesh_", StringComparison.Ordinal) || e.FullName.StartsWith(model + "Skin", StringComparison.Ordinal))))
        {
            string text;
            using (var reader = new StreamReader(prefab.Open())) text = reader.ReadToEnd();
            int at = text.IndexOf("\"m_GeometryAsset\"", StringComparison.Ordinal);
            if (at < 0) continue;
            string id = text.Substring(text.IndexOf("CID:", at, StringComparison.Ordinal) + 4, 32);
            if (!geometries.TryGetValue(id, out ZipArchiveEntry geometry) || !seen.Add(id)) continue;
            var glass = new List<bool>();
            int list = text.IndexOf("$rcontent", text.IndexOf("\"m_SurfaceAssets\"", StringComparison.Ordinal), StringComparison.Ordinal);
            int end = text.IndexOf("]", list, StringComparison.Ordinal);
            for (int c = text.IndexOf("CID:", list, StringComparison.Ordinal); c >= 0 && c < end; c = text.IndexOf("CID:", c + 4, StringComparison.Ordinal))
                glass.Add(surfaces.TryGetValue(text.Substring(c + 4, 32), out string surface) && surface.IndexOf("_Gls", StringComparison.OrdinalIgnoreCase) >= 0);
            using Stream s = geometry.Open();
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            yield return (prefab.FullName.Substring(0, prefab.FullName.IndexOf(" Mesh_", StringComparison.Ordinal)) + " -> " + geometry.FullName, copy.ToArray(), glass.ToArray());
        }
    }

    static string Sha(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 8).ToLowerInvariant();
    }
}
