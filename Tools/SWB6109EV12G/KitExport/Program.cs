using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

namespace SWB6109EV12G.KitExport
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                // Dimensions are supplied by the new model specification, never by
                // the short-bus source. Only reusable cabin fittings are exported.
                var spec = new BusInteriorSpec
                {
                    FloorY = 0.36f, DriverSide = -1f, Section = BusSection.Solo,
                };
                var selected = new HashSet<string>(new[] { "Dash", "Cab", "SteeringWheel", "SpeedNeedle", "RpmNeedle" }, StringComparer.Ordinal);
                string output = "ModelSource/SWB6109EV12G/kit-cabin.json";
                bool readerOnly = false, outputSpecified = false;
                int boardPose = 0;
                for (int i = 0; i < args.Length; i++)
                {
                    string key = args[i];
                    if (key == "--help")
                    {
                        Console.WriteLine("KitExport [--reader-only] [--output file] [--spec JSON] [--length m] [--width m] [--roof m] [--floor m] [--front-axle z] [--rear-axle z] [--doors n] [--pose 0|1] [--parts Dash,Cab,... | --all-parts]");
                        return 0;
                    }
                    if (key == "--reader-only") { readerOnly = true; continue; }
                    if (key == "--all-parts") { selected = null; continue; }
                    if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                    string value = args[i];
                    if (key == "--output") { output = value; outputSpecified = true; continue; }
                    if (key == "--parts") { selected = new HashSet<string>(value.Split(','), StringComparer.Ordinal); continue; }
                    if (key == "--spec") { spec = ReadModelSpec(value); continue; }
                    float n = float.Parse(value, CultureInfo.InvariantCulture);
                    if (float.IsNaN(n) || float.IsInfinity(n)) throw new ArgumentException("Dimensions must be finite.");
                    switch (key)
                    {
                        case "--length": spec.FrontZ = n / 2f; spec.RearZ = -n / 2f; break;
                        case "--width": spec.HalfWidth = n / 2f; break;
                        case "--roof": spec.RoofY = n; break;
                        case "--floor": spec.FloorY = n; break;
                        case "--front-axle": spec.FrontAxleZ = n; break;
                        case "--rear-axle": spec.RearAxleZ = n; break;
                        case "--doors": spec.Doors = (int)n; break;
                        case "--pose": boardPose = (int)n; break;
                        default: throw new ArgumentException("Unknown option " + key);
                    }
                }
                if (readerOnly) return ExportReaders(outputSpecified ? output : "ModelSource/SWB6109EV12G/common-card-reader.json");
                if (spec.FrontZ <= spec.RearZ || spec.HalfWidth <= 0f || spec.RoofY <= spec.FloorY) throw new ArgumentException("The body dimensions must be positive.");
                if (boardPose < 0 || boardPose > 1) throw new ArgumentException("Pose must be 0 or 1.");

                var source = new BusInterior(spec);
                var model = KitBuild.BuildAll(source);
                if (model.Error != null) throw new InvalidOperationException(model.Error);
                var pose = new KitPoseBuffer(model.Parts);
                BusInterior.Apply(pose, BusInterior.BoardPoseOf(boardPose, true), source.Spec);

                string fullPath = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                using var stream = File.Create(fullPath);
                using var json = new Utf8JsonWriter(stream);
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", 1);
                json.WriteString("source", "CDLC.BusDriver.Interior.Models.BusInterior");
                json.WriteString("modelKey", source.Key.ToString());
                json.WriteString("units", "metres");
                json.WriteString("axes", "+X right, +Y up, +Z front; same handedness as the MeshKit source");
                json.WriteString("geometrySpace", "posed model space; partToModel already applied to positions and normals");
                json.WriteString("colorsSpace", "linear RGBA; palette.rgb is sRGB");
                json.WriteString("matrixLayout", "partToModel is column-major, 16 scalar elements");
                json.WriteString("reuseNote", "Generic cabin fittings only. The new SWB6109EV12G exterior, doors, glazing and passenger layout must be authored independently from its own references. BusInterior may round dimensions.");
                json.WriteNumber("boardPose", boardPose);
                json.WritePropertyName("requestedSpec"); JsonSerializer.Serialize(json, spec, new JsonSerializerOptions { IncludeFields = true });
                json.WritePropertyName("builtSpec"); JsonSerializer.Serialize(json, source.Spec, new JsonSerializerOptions { IncludeFields = true });
                WritePalette(json);
                json.WriteStartArray("lods");
                for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
                {
                    json.WriteStartObject(); json.WriteNumber("lod", lod); json.WriteStartArray("parts");
                    foreach (var part in model.Parts)
                        if (selected == null || selected.Contains(part.Name)) WritePart(json, model, pose, part, lod);
                    json.WriteEndArray(); json.WriteEndObject();
                }
                json.WriteEndArray(); json.WriteEndObject(); json.Flush();
                Console.WriteLine("Exported " + fullPath);
                Console.WriteLine("Parts: " + model.Parts.Length + "; triangles by LOD: " + string.Join(", ", model.Tris));
                Console.WriteLine("Requested length  " + (spec.FrontZ - spec.RearZ).ToString("F4", CultureInfo.InvariantCulture) + "; built length " + (source.Spec.FrontZ - source.Spec.RearZ).ToString("F4", CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }

        sealed class ReaderSource : KitSource
        {
            // the vehicle profile (Tools/SWB6109EV12G/vehicle.json: the card reader frames), registered like a pack's
            internal readonly BusInteriorSpec Spec = VehicleModels.Register(VehicleProfileJson.Load(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "vehicle.json"), requireMesh: false)).Spec;
            public override KitModelKey Key => new KitModelKey("swb6109-shared-card-reader");
            public override void Parts(KitPartList parts) => parts.Add(0, "CardReader");
            public override void Define(KitModelBuilder builder, int lod)
            {
                var kit = builder.Part(0);
                foreach (var reader in BusInterior.CardReaders(Spec))
                    using (kit.At(reader.origin, reader.frame)) BusInterior.CardReader(kit, lod);
            }
        }

        static int ExportReaders(string output)
        {
            var source = new ReaderSource();
            var model = KitBuild.BuildAll(source);
            if (model.Error != null) throw new InvalidOperationException(model.Error);
            var pose = new KitPoseBuffer(model.Parts);
            string fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            using var stream = File.Create(fullPath);
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 2);
            json.WriteString("source", "BusInterior.CardReader(MeshKit,int)");
            json.WriteString("placementSource", "BusInterior.CardReaders(Tools/SWB6109EV12G/vehicle.json)");
            json.WriteString("units", "metres");
            json.WriteString("geometrySpace", "Unity model space, +X door side, +Y up, +Z front; reader frame already applied");
            json.WriteString("canonicalPreparationMapping", "(unity.x, -unity.z, unity.y)");
            json.WriteString("finalBlenderMapping", "(-unity.x, -unity.z, unity.y)");
            json.WriteString("colorsSpace", "linear RGBA; palette.rgb is sRGB");
            json.WriteString("matrixLayout", "partToModel is column-major, 16 scalar elements");
            json.WriteString("reuseNote", "Exact shared procedural card reader at each authored reader frame; source/FBX may close open UI sheets without changing original front faces; runtime regenerates the original method.");
            json.WriteStartArray("readers");
            foreach (var reader in BusInterior.CardReaders(source.Spec))
            {
                json.WriteStartObject(); Vec(json, "originUnity", reader.origin);
                json.WriteStartArray("frameRotationUnityXyzw");
                foreach (float v in new[] { reader.frame.value.x, reader.frame.value.y, reader.frame.value.z, reader.frame.value.w }) json.WriteNumberValue(v);
                json.WriteEndArray(); json.WriteNumber("xSign", reader.xSign); json.WriteEndObject();
            }
            json.WriteEndArray(); WritePalette(json);
            json.WriteStartArray("lods");
            for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
            {
                json.WriteStartObject(); json.WriteNumber("lod", lod); json.WriteNumber("triangleCount", model.Tris[lod]);
                json.WriteStartArray("parts");
                foreach (var part in model.Parts) WritePart(json, model, pose, part, lod);
                json.WriteEndArray(); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject(); json.Flush();
            Console.WriteLine("Exported shared card reader " + fullPath + "; triangles by LOD: " + string.Join(", ", model.Tris));
            return 0;
        }

        static void WritePalette(Utf8JsonWriter json)
        {
            json.WriteStartArray("palette");
            for (int s = 0; s < KitPalette.Rows; s++)
            {
                var swatch = (Sw)s; var finish = KitPalette.FinOf(swatch);
                json.WriteStartObject(); json.WriteNumber("index", s); json.WriteString("name", swatch.ToString());
                json.WriteString("finish", finish.ToString()); Vec(json, "rgb", KitPalette.Srgb(swatch));
                json.WriteNumber("metallic", KitPalette.Metallic(finish)); json.WriteNumber("roughness", 1f - KitPalette.Smoothness(finish));
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }

        static float Required(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || !value.TryGetSingle(out float number) || float.IsNaN(number) || float.IsInfinity(number))
                throw new ArgumentException("Missing or nonfinite model dimension: " + name);
            return number;
        }

        static BusInteriorSpec ReadModelSpec(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var data = document.RootElement;
            if (!data.TryGetProperty("length_m", out _))
                return JsonSerializer.Deserialize<BusInteriorSpec>(data.GetRawText(), new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
            float half = Required(data, "length_m") / 2f;
            int doors = 2;
            if (data.TryGetProperty("door_count", out var count)) doors = count.GetInt32();
            else if (data.TryGetProperty("door_leaves", out var leaves))
                doors = leaves.EnumerateArray().Select(leaf => (leaf.GetProperty("part").GetInt32() - 4) / 2).Distinct().Count();
            return new BusInteriorSpec
            {
                FrontZ = half, RearZ = -half, HalfWidth = Required(data, "width_m") / 2f,
                RoofY = Required(data, "body_roof_m"),
                FloorY = data.TryGetProperty("floor_height_m", out var floor) ? floor.GetSingle()
                    : data.TryGetProperty("floor_m", out var lowFloor) ? lowFloor.GetSingle() : .36f,
                FrontAxleZ = half - Required(data, "front_overhang_m"),
                RearAxleZ = -half + Required(data, "rear_overhang_m"),
                Doors = doors, DriverSide = -1f, Section = BusSection.Solo,
            };
        }

        static void WritePart(Utf8JsonWriter json, KitModelData model, KitPoseBuffer pose, KitPartDef part, int lod)
        {
            var owner = part.MeshOf >= 0 ? model.Parts[part.MeshOf] : part;
            var mesh = owner.Data[lod];
            if (mesh == null) return;
            float4x4 transform = pose.PartToModel(part.Index);
            var normalMatrix = math.transpose(math.inverse(new float3x3(transform.c0.xyz, transform.c1.xyz, transform.c2.xyz)));
            var colors = new float3[mesh.Verts];
            for (int sub = 0; sub < mesh.SubFin.Length; sub++)
                for (int t = mesh.SubStart[sub]; t < mesh.SubStart[sub] + mesh.SubCount[sub]; t++)
                {
                    int vertex = mesh.Idx[t]; float2 uv = mesh.UV[vertex]; Fin fin = mesh.SubFin[sub]; float3 srgb;
                    if (KitPalette.UsesPalette(fin))
                    {
                        int row = math.clamp((int)math.floor(uv.y * KitPalette.Rows), 0, KitPalette.Rows - 1);
                        float ramp = math.saturate((uv.x * KitPalette.Width - 0.5f) / (KitPalette.Width - 1));
                        srgb = KitPalette.SurfaceSrgb(fin, (Sw)row, ramp);
                    }
                    else if (fin == Fin.Hazard) srgb = (((int)math.floor((uv.x + uv.y) * 2f)) & 1) == 0 ? KitPalette.Srgb(Sw.Yellow) : new float3(0.04f);
                    else srgb = KitPalette.FlatSrgb(fin);
                    colors[vertex] = KitPalette.ToLinear(srgb);
                }
            json.WriteStartObject(); json.WriteNumber("index", part.Index); json.WriteString("name", part.Name);
            json.WriteNumber("parent", part.Parent); json.WriteNumber("meshSource", owner.Index); json.WriteBoolean("visible", pose.Visible[part.Index]);
            json.WriteString("flags", part.Flags.ToString()); Vec(json, "restPivot", part.Pivot);
            json.WriteStartArray("partToModel");
            foreach (float4 col in new[] { transform.c0, transform.c1, transform.c2, transform.c3 })
            { json.WriteNumberValue(col.x); json.WriteNumberValue(col.y); json.WriteNumberValue(col.z); json.WriteNumberValue(col.w); }
            json.WriteEndArray();
            json.WriteStartArray("positions"); foreach (float3 p in mesh.P) Vec(json, null, math.transform(transform, p)); json.WriteEndArray();
            json.WriteStartArray("normals"); foreach (float3 n in mesh.N) Vec(json, null, math.normalizesafe(math.mul(normalMatrix, n))); json.WriteEndArray();
            json.WriteStartArray("uv"); foreach (float2 uv in mesh.UV) { json.WriteStartArray(); json.WriteNumberValue(uv.x); json.WriteNumberValue(uv.y); json.WriteEndArray(); } json.WriteEndArray();
            json.WriteStartArray("colors"); foreach (float3 color in colors) { json.WriteStartArray(); json.WriteNumberValue(color.x); json.WriteNumberValue(color.y); json.WriteNumberValue(color.z); json.WriteNumberValue(1f); json.WriteEndArray(); } json.WriteEndArray();
            json.WriteStartArray("indices"); foreach (int index in mesh.Idx) json.WriteNumberValue(index); json.WriteEndArray();
            json.WriteStartArray("submeshes");
            for (int s = 0; s < mesh.SubFin.Length; s++)
            {
                json.WriteStartObject(); json.WriteString("finish", mesh.SubFin[s].ToString());
                json.WriteNumber("start", mesh.SubStart[s]); json.WriteNumber("count", mesh.SubCount[s]); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }

        static void Vec(Utf8JsonWriter json, string name, float3 v)
        {
            if (name != null) json.WritePropertyName(name);
            json.WriteStartArray(); json.WriteNumberValue(v.x); json.WriteNumberValue(v.y); json.WriteNumberValue(v.z); json.WriteEndArray();
        }
    }
}
