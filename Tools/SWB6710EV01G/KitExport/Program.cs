using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using CDLC.BusDriver.Interior.Models;
using CDLC.BusDriver.Kit;
using Unity.Mathematics;

namespace SWB6710EV01G.KitExport
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                var spec = new BusInteriorSpec
                {
                    FrontZ = 3.5725f, RearZ = -3.5725f, HalfWidth = 1.175f, RoofY = 2.9f,
                    FloorY = 0.36f, FrontAxleZ = 2.3725f, RearAxleZ = -2.7275f,
                    Doors = 2, DriverSide = -1f, Section = BusSection.Solo,
                };
                string output = "ModelSource/SWB6710EV01G/kit-cabin.json";
                int boardPose = 0;
                for (int i = 0; i < args.Length; i++)
                {
                    string key = args[i];
                    if (key == "--help")
                    {
                        Console.WriteLine("KitExport [--output file] [--spec JSON] [--length m] [--width m] [--roof m] [--floor m] [--front-axle z] [--rear-axle z] [--doors n] [--pose 0|1]");
                        return 0;
                    }
                    if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                    string value = args[i];
                    if (key == "--output") { output = value; continue; }
                    if (key == "--spec")
                    {
                        spec = JsonSerializer.Deserialize<BusInteriorSpec>(File.ReadAllText(value), new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
                        continue;
                    }
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
                if (spec.FrontZ <= spec.RearZ || spec.HalfWidth <= 0f) throw new ArgumentException("The body dimensions must be positive.");
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
                json.WriteString("reuseNote", "Generic fittings only. BusInterior rounds dimensions and places doors for a conventional bus; its body, glazing, doors, seats and poles are not a verified SWB6710EV01G layout.");
                json.WriteNumber("boardPose", boardPose);
                json.WritePropertyName("requestedSpec"); JsonSerializer.Serialize(json, spec, new JsonSerializerOptions { IncludeFields = true });
                json.WritePropertyName("builtSpec"); JsonSerializer.Serialize(json, source.Spec, new JsonSerializerOptions { IncludeFields = true });
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
                json.WriteStartArray("lods");
                for (int lod = 0; lod <= MeshKit.MaxLod; lod++)
                {
                    json.WriteStartObject(); json.WriteNumber("lod", lod); json.WriteStartArray("parts");
                    foreach (var part in model.Parts) WritePart(json, model, pose, part, lod);
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
