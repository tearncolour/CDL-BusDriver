using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Game.UI.Localization;
using Game.UI.Widgets;
using Unity.Mathematics;
using UnityEngine;
using WrapFace = CDLC.BusDriver.Interior.Models.BusInterior.WrapFace;
using WrapFaces = CDLC.BusDriver.Interior.Models.BusInterior.WrapFaces;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Body wraps for the bus the player drives. A wrap is a zip, Wraps/&lt;model&gt;/&lt;name&gt;.zip under ModsData/CDLC.BusDriver (one folder
    /// per vehicle model: a wrap made for one model is never offered or used on another), with a picture per face, any of left.png,
    /// right.png, front.png, rear.png, roof.png (BusInterior.Wrap.cs: what each covers and which way it runs), and wrap.json (the model
    /// and layout version it was made for: WrapPackage). A side without its picture takes the other side's. The pictures are PNGs with
    /// a transparent channel: the picture's alpha is its opacity (alpha blended, so the edges are smooth) and the body, window or door
    /// shows through the transparent parts; in the windows and doors of a model whose wrap covers them (profile wrap.windows / wrap.doors)
    /// the edge of a see-through part (alpha under half) gets a white outline of outlineMm, like a real film. The selected wrap (option "Body wrap", or the panel's manager: load, unload,
    /// new, open the folder) is reloaded when its zip changes (checked every two seconds). "template.zip" of each model holds the
    /// empty (transparent) pictures to paint on, guide/&lt;face&gt;.png maps of what each area is (body, window, door, parts that stand in
    /// front), a README and wrap.json with the model's numbers; loaded as a wrap it shows its guide. The manager's
    /// thumbnails are written to WrapThumbs and shown through the UI host "cdlcbuswraps". Only the mod's own model shows a wrap (the
    /// bus the player drives); the city's buses keep their paint.
    /// </summary>
    internal sealed class BusSkins
    {
        internal const string kTemplate = "template";
        internal const string kHost = "cdlcbuswraps";
        internal const string kGenericModel = "generic";
        internal static readonly string[] kFiles = WrapPackage.Pictures;
        private const float kPollSeconds = 2f;
        private const string kMap = "_BaseColorMap";
        private const int kThumbWidth = 240, kThumbHeight = 48;

        private static int s_Version = 1;
        private static bool s_HostAdded;

        /// <summary>Bumps when the folder may have changed (the lists are read again).</summary>
        internal static int Version => s_Version;
        internal static void Changed() => System.Threading.Interlocked.Increment(ref s_Version);     // also from the prebuild thread (the template)

        // Taken once, on the main thread (Application.persistentDataPath may only be read there): the template, the wrap list and the
        // wrap's load build their paths on worker threads, always after a main-thread call has asked for one first.
        private static string s_Data;
        internal static string Data => s_Data ?? (s_Data = Path.Combine(Application.persistentDataPath, "ModsData", "CDLC.BusDriver"));
        internal static string Root => Path.Combine(Data, "Wraps");
        private static string Thumbs => Path.Combine(Data, "WrapThumbs");

        // ---------------------------------------------------------------- the vehicle the wraps are for

        /// <summary>One vehicle's wrap numbers: its model folder, layout version, outline, and its sections for the picture-space areas.</summary>
        private sealed class Vehicle
        {
            internal string Model = kGenericModel;
            internal int Layout;
            internal float OutlineMm = WrapPackage.DefaultOutlineMm;
            internal List<BusInterior> Sections = new List<BusInterior>();
            internal float[] Starts = new float[0];
            internal float Total = 12f;
            internal VehicleProfile Profile;

            internal string Key => Model + "/" + Layout + "/" + Sections.Count + "/" + Total.ToString("0.00", CultureInfo.InvariantCulture);

            internal static bool Along(WrapFace face) => face == WrapFace.Left || face == WrapFace.Right || face == WrapFace.Roof;

            /// <summary>A face's picture x for a section's u (the sides and the roof run over all sections; the right side from the other end).</summary>
            internal float X(WrapFace face, int section, float u)
            {
                float a = Along(face) ? Starts[section] / Total : 0f, s = Along(face) ? Sections[section].WrapLength / Total : 1f;
                return face == WrapFace.Right ? 1f - (a + s * u) : a + s * u;
            }

            /// <summary>The face's size in metres (the width the picture spans, the height).</summary>
            internal float2 Size(WrapFace face)
            {
                BusInterior m = Sections.FirstOrDefault(s => s.HasWrap(face));
                if (m == null) return float2.zero;
                float2 size = m.WrapSize(face);
                return Along(face) ? new float2(Total, size.y) : size;
            }

            /// <summary>The window / door triangles of a face in its picture's coordinates.</summary>
            internal List<(byte kind, float2x3 uv)> Areas(WrapFace face)
            {
                var list = new List<(byte, float2x3)>();
                for (int i = 0; i < Sections.Count; i++)
                {
                    if (!Sections[i].HasWrap(face)) continue;
                    foreach ((byte kind, float2x3 uv) in Sections[i].WrapAreas(face))
                        list.Add((kind, new float2x3(new float2(X(face, i, uv.c0.x), uv.c0.y), new float2(X(face, i, uv.c1.x), uv.c1.y), new float2(X(face, i, uv.c2.x), uv.c2.y))));
                    if (!Along(face)) break;
                }
                return list;
            }
        }

        private static Vehicle s_Vehicle = new Vehicle();
        private static int s_VehicleId = 1;
        private static readonly HashSet<string> s_Templates = new HashSet<string>();

        /// <summary>The vehicle model whose wraps are listed and used (the generic generated bus before an authored one is driven).</summary>
        internal static string Model => s_Vehicle.Model;

        /// <summary>The wrap layout version of the model being driven (the version a wrap's manifest must name).</summary>
        internal static int Layout => s_Vehicle.Layout;

        /// <summary>Whether a bus has been driven (SetVehicle): before that the model is the generic one by default.</summary>
        internal static bool HasVehicle => s_Vehicle.Sections.Count > 0;

        /// <summary>
        /// The bus being driven (its sections, front first): which model's wraps the manager lists and the cab loads. A change of model or
        /// of its wrap layout makes every cab's BusSkins load again.
        /// </summary>
        internal static void SetVehicle(IList<BusInteriorSpec> sections)
        {
            if (sections == null || sections.Count == 0)
                return;
            var models = sections.Select(s => new BusInterior(s)).ToList();
            VehicleProfile profile = models[0].IsAuthored ? models[0].AuthoredModel : null;
            var v = new Vehicle { Sections = models, Profile = profile };
            if (profile != null && VehicleProfileJson.ModelName(profile.Name))
            {
                v.Model = profile.Name;
                v.Layout = profile.WrapLayout;
                v.OutlineMm = profile.WrapOutlineMm;
            }
            v.Starts = new float[models.Count];
            float at = 0f;
            for (int i = 0; i < models.Count; i++)
            {
                v.Starts[i] = at;
                at += models[i].WrapLength;
            }
            v.Total = at;
            if (v.Key == s_Vehicle.Key && s_Vehicle.Sections.Count > 0)
                return;
            s_Vehicle = v;
            s_VehicleId++;
            Changed();
        }

        private static string ZipOf(string name) =>
            string.IsNullOrEmpty(name) ? null : WrapPackage.ZipPath(Root, s_Vehicle.Model, name);

        // A wrap being made. Its zip is read, its pictures decoded and prepared (the outline round the see-through parts, the colour under
        // them: half a second of one core on a 2048 x 785 side) on a worker thread, which used to freeze the game for that half second
        // (the user's log of 18 October: the wrap's block, 761 and 776 ms in one frame); the main thread only uploads the textures,
        // one a frame, and shows the wrap when all of them are there. The worker writes its results, then sets Done; nothing else
        // is shared (the vehicle's numbers are read from the Vehicle object it was started with, which is never changed).
        private sealed class Load
        {
            internal string Name, Path;
            internal DateTime Time;
            internal bool Template;
            internal int VehicleId;
            internal Vehicle Vehicle;
            internal volatile bool Cancelled, Done;
            // the worker's results
            internal string Warning, WarningKey;                       // set when the wrap cannot be used (the old Warn line)
            internal string Refused;                                   // a picture over the size limit: the wrap worn stays
            internal float OutlineMm;
            internal bool[] Have;
            internal readonly byte[][] Files = new byte[WrapPackage.Faces.Length][];
            internal readonly byte[][] Rgba = new byte[WrapPackage.Faces.Length][];       // decoded and prepared; null: the game's LoadImage on the main thread
            internal readonly int[] Width = new int[WrapPackage.Faces.Length], Height = new int[WrapPackage.Faces.Length];
            internal readonly string[] Notes = new string[WrapPackage.Faces.Length];      // "see-through pixels ..." log line of a picture
            internal double WorkerMs;
            // the main thread's staging
            internal readonly Texture2D[] Textures = new Texture2D[WrapPackage.Faces.Length];
            internal readonly byte[][] Kept = new byte[WrapPackage.Faces.Length][];
            internal int Next;
            internal int Frames;
            internal long UploadTicks;
        }

        private Load m_Load;

        /// <summary>Whether a wrap is being made (Update finishes it a step a frame).</summary>
        internal bool Loading => m_Load != null;

        private void CancelLoad()
        {
            Load load = m_Load;
            if (load == null) return;
            load.Cancelled = true;
            for (int f = 0; f < load.Textures.Length; f++)
                if (load.Textures[f] != null) UnityEngine.Object.Destroy(load.Textures[f]);
            m_Load = null;
        }

        private void StartLoad(string name, string path, DateTime time, Vehicle v)
        {
            var load = new Load { Name = name, Path = path, Time = time, Template = name == kTemplate, VehicleId = s_VehicleId, Vehicle = v };
            m_Load = load;
            new System.Threading.Thread(() => Work(load)) { IsBackground = true, Name = "Bus Driver wrap pictures", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        // The worker: the zip, then every picture decoded (PngDecoder; a picture it will not take is left to the main thread's LoadImage)
        // and prepared.
        private static void Work(Load load)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                WrapPackage.Package package = WrapPackage.Read(load.Path, load.Template);
                string mismatch = WrapPackage.Mismatch(package.Manifest, load.Vehicle.Model, load.Vehicle.Layout);
                if (mismatch != null)
                {
                    load.WarningKey = load.Path;
                    load.Warning = $"Body wraps: {load.Path}: {mismatch}; no wrap.";
                    return;
                }
                load.OutlineMm = load.Template ? 0f : package.Manifest.OutlineMm;
                for (int f = 0; f < load.Files.Length; f++)
                    load.Files[f] = load.Template ? package.Guides[f] ?? package.Faces[f] : package.Faces[f];
                load.Have = new bool[load.Files.Length];
                for (int f = 0; f < load.Files.Length; f++)
                    load.Have[f] = load.Files[f] != null;
                for (int f = 0; f < load.Files.Length; f++)
                {
                    string refusal = load.Files[f] != null ? PngDecoder.Refusal(load.Files[f]) : null;
                    if (refusal == null) continue;
                    load.WarningKey = load.Path;
                    load.Refused = $"Body wraps: {load.Path}: {kFiles[f]} {refusal}; the wrap is not taken (the one worn stays).";
                    return;
                }
                for (int f = 0; f < load.Files.Length && !load.Cancelled; f++)
                {
                    if (load.Files[f] == null) continue;
                    if (!PngDecoder.TryDecode(load.Files[f], out int w, out int h, out byte[] rgba)) continue;
                    load.Notes[f] = Finish(load.Vehicle, f, rgba, w, h, load.Have, load.OutlineMm, load.Name);
                    load.Rgba[f] = rgba;
                    load.Width[f] = w;
                    load.Height[f] = h;
                }
            }
            catch (WrapPackage.Error ex)
            {
                load.WarningKey = load.Path;
                load.Warning = $"Body wraps: {load.Path}: {ex.Message}; no wrap.";
            }
            catch (Exception ex)
            {
                load.WarningKey = load.Path;
                load.Warning = $"Body wraps: cannot read {load.Path}: {ex.Message}";
            }
            finally
            {
                load.WorkerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                load.Done = true;
            }
        }

        private string m_Name;
        private int m_VehicleId;
        private DateTime m_Time;
        private readonly Texture2D[] m_Pictures = new Texture2D[BusInterior.WrapFaceCount];
        private float m_NextPoll;
        // The pictures' pixels as drawn on the faces (after the outline and the colour work), kept to bake each section's share of a face.
        private readonly byte[][] m_Rgba = new byte[BusInterior.WrapFaceCount][];
        // A face's material and the share of the picture it was last given (asked for every frame: only a change is written). Own is the
        // texture baked for the share (null while the material shows a whole picture as it is).
        private sealed class FaceMaterial
        {
            internal Material Material;
            internal Texture2D Own;
            internal int Source = -1, X0, X1;
            internal bool Mirrored;
        }

        private readonly Dictionary<(int section, WrapFace face), FaceMaterial> m_Materials = new Dictionary<(int, WrapFace), FaceMaterial>();
        private string m_Warned;

        /// <summary>The faces the loaded wrap covers (a side without its picture takes the other's).</summary>
        internal WrapFaces Faces
        {
            get
            {
                var f = WrapFaces.None;
                if (Picture(WrapFace.Left) != null) f |= WrapFaces.Left | WrapFaces.Right;
                if (m_Pictures[(int)WrapFace.Front] != null) f |= WrapFaces.Front;
                if (m_Pictures[(int)WrapFace.Rear] != null) f |= WrapFaces.Rear;
                if (m_Pictures[(int)WrapFace.Roof] != null) f |= WrapFaces.Roof;
                return f;
            }
        }

        private Texture2D Picture(WrapFace face) => m_Pictures[PictureOf(face)];

        // The picture a face wears: the other side's when its own is missing.
        private int PictureOf(WrapFace face)
        {
            if (face == WrapFace.Left) return m_Pictures[0] != null ? 0 : 1;
            if (face == WrapFace.Right) return m_Pictures[1] != null ? 1 : 0;
            return (int)face;
        }

        // ---------------------------------------------------------------- the list (option dropdown, panel)

        private sealed class Entry
        {
            internal string Name, Thumb = "", Error;
            internal bool Template;
            internal int Faces;
            internal string[] Sizes = new string[WrapPackage.Faces.Length];
            // being read on a worker: listed by its name for now, the rest when it is done (Version moves on)
            internal bool Pending;
        }

        // What the manager shows of a zip, kept while the zip is unchanged (the list is asked for often; unzipping a K18's pictures is not cheap).
        private static readonly Dictionary<string, (DateTime time, string model, int layout, Entry entry)> s_Described = new Dictionary<string, (DateTime, string, int, Entry)>();

        // The zips being read on the worker (path, time, model, layout) and what it has read. Reading every wrap of a model on the main
        // thread was a second of it for the user's eleven SWB6710EV01G wraps (offline, CDLC.BusDriver.Tests listbench), and a missing
        // thumbnail was decoded and read back from the graphics card in the same frame.
        private static readonly HashSet<string> s_Describing = new HashSet<string>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Described> s_DescribedQueue = new System.Collections.Concurrent.ConcurrentQueue<Described>();
        private static System.Threading.Tasks.Task s_DescribeChain = System.Threading.Tasks.Task.CompletedTask;
        private static readonly object s_DescribeLock = new object();

        private sealed class Described
        {
            internal string Path, Key, Model;
            internal DateTime Time;
            internal int Layout;
            internal Entry Entry;
            internal byte[] ThumbSource;    // a picture the worker could not decode: its thumbnail is made on the main thread
            internal int ThumbFace;
        }

        private static List<Entry> Entries()
        {
            var list = new List<Entry>();
            Vehicle v = s_Vehicle;
            string folder = WrapPackage.ModelFolder(Root, v.Model);
            if (folder != null && Directory.Exists(folder))
            {
                DrainDescribed();
                var paths = Directory.GetFiles(folder, "*.zip").OrderBy(p => Path.GetFileNameWithoutExtension(p) == kTemplate ? "" : Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!WrapPackage.ValidName(name)) continue;
                    list.Add(Describe(path, name, v));
                }
            }
            return list;
        }

        private static Entry Describe(string path, string name, Vehicle v)
        {
            DateTime time = File.GetLastWriteTimeUtc(path);
            if (s_Described.TryGetValue(path, out var known) && known.time == time && known.model == v.Model && known.layout == v.Layout)
                return known.entry;
            string key = path + "|" + time.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + v.Model + "|" + v.Layout.ToString(CultureInfo.InvariantCulture);
            if (s_Describing.Add(key))
            {
                string model = v.Model;
                int layout = v.Layout;
                lock (s_DescribeLock)
                    s_DescribeChain = s_DescribeChain.ContinueWith(_ => DescribeWork(path, name, time, model, layout, key), System.Threading.Tasks.TaskScheduler.Default);
            }
            return new Entry { Name = name, Template = name == kTemplate, Pending = true };
        }

        // The worker's share: the zip read and checked as before, and the thumbnail made without the graphics card.
        private static void DescribeWork(string path, string name, DateTime time, string model, int layout, string key)
        {
            var done = new Described { Path = path, Key = key, Model = model, Time = time, Layout = layout };
            var e = new Entry { Name = name, Template = name == kTemplate };
            done.Entry = e;
            try
            {
                WrapPackage.Package package = WrapPackage.Read(path, e.Template);
                for (int f = 0; f < kFiles.Length; f++)
                {
                    byte[] bytes = e.Template ? package.Guides[f] ?? package.Faces[f] : package.Faces[f];
                    if (bytes == null) continue;
                    e.Faces |= 1 << f;
                    e.Sizes[f] = PngSize(bytes);
                }
                if (!package.Any) e.Error = "Wrap.Err.NoPictures";
                else e.Error = WrapPackage.Mismatch(package.Manifest, model, layout);
                if (e.Error == null)
                    e.Thumb = ThumbWork(model, name, time, package, e.Faces, out done.ThumbSource, out done.ThumbFace);
            }
            catch (WrapPackage.Error ex)
            {
                e.Error = ex.Message;
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
            }
            s_DescribedQueue.Enqueue(done);
            Changed();
        }

        // The worker's results into the list (main thread), with the thumbnails only the game's decoder can make.
        private static void DrainDescribed()
        {
            while (s_DescribedQueue.TryDequeue(out Described d))
            {
                s_Describing.Remove(d.Key);
                if (d.ThumbSource != null)
                    d.Entry.Thumb = ThumbOnMain(d.Model, d.Entry.Name, d.Time, d.ThumbSource, d.ThumbFace);
                s_Described[d.Path] = (d.Time, d.Model, d.Layout, d.Entry);
            }
        }

        /// <summary>The option's dropdown: none, then the wraps of the model being driven (the template first).</summary>
        internal static DropdownItem<string>[] Items()
        {
            var items = new List<DropdownItem<string>> { new DropdownItem<string> { value = "", displayName = LocalizedString.Id("CDLC.BusDriver.UI.WrapNone") } };
            try
            {
                foreach (Entry e in Entries())
                    if (e.Error == null)
                        items.Add(new DropdownItem<string>
                        {
                            value = e.Name,
                            displayName = e.Template ? LocalizedString.Id("CDLC.BusDriver.UI.WrapTemplate") : LocalizedString.Value(e.Name),
                        });
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Body wraps: cannot list {Root}: {ex.Message}");
            }
            return items.ToArray();
        }

        /// <summary>
        /// The wraps of the model being driven for the panel's manager as JSON: {"model", "items": [{"name", "faces" (bit per face: left,
        /// right, front, rear, roof), "sizes" (w x h per face, "" without), "thumb" (a coui:// picture), "template", "error" (why it cannot
        /// be used, "" if it can)}]}.
        /// </summary>
        internal static string ListJson()
        {
            var json = new StringBuilder("{\"model\":").Append(Quote(s_Vehicle.Model)).Append(",\"items\":[");
            try
            {
                Directory.CreateDirectory(Thumbs);
                AddHost();
                var used = new HashSet<string>();
                bool first = true, pending = false;
                foreach (Entry e in Entries())
                {
                    if (e.Thumb.Length > 0) used.Add(Path.GetFileName(e.Thumb));
                    pending |= e.Pending;
                    if (!first) json.Append(',');
                    first = false;
                    json.Append("{\"name\":").Append(Quote(e.Name)).Append(",\"faces\":").Append(e.Faces)
                        .Append(",\"sizes\":[").Append(string.Join(",", e.Sizes.Select(s => Quote(s ?? "")))).Append("]")
                        .Append(",\"thumb\":").Append(Quote(e.Thumb)).Append(",\"template\":").Append(e.Template ? "true" : "false")
                        .Append(",\"pending\":").Append(e.Pending ? "true" : "false")
                        .Append(",\"error\":").Append(Quote(e.Error ?? "")).Append('}');
                }
                // thumbnails of wraps gone or changed (not while a row is still being read: its thumbnail is not known yet)
                if (!pending)
                    foreach (string file in Directory.GetFiles(Thumbs, "*.png"))
                        if (!used.Contains(Path.GetFileName(file)))
                            try { File.Delete(file); } catch (Exception) { }
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Body wraps: cannot list {Root}: {ex.Message}");
            }
            return json.Append("]}").ToString();
        }

        /// <summary>The manager's state: the wrap worn, whether the bus is drawn from outside (wraps show), the list (ListJson).</summary>
        internal static string ManagerJson(string current, bool exterior) =>
            "{\"current\":" + Quote(current ?? "") + ",\"exterior\":" + (exterior ? "true" : "false") + "," + ListJson().Substring(1);

        private static void AddHost()
        {
            if (s_HostAdded || Colossal.UI.UIManager.defaultUISystem == null)
                return;
            Colossal.UI.UIManager.defaultUISystem.AddHostLocation(kHost, Thumbs, false);
            s_HostAdded = true;
        }

        // A wrap's thumbnail (the zip's thumb.png, else its left side, else the first face it has; the template: its guide), made again
        // when the zip changes: coui://host/file. On the worker: decoded (PngDecoder), scaled down and written there; "" with the
        // source given back when the decoder does not take the picture (a JPG, 16 bits: ThumbOnMain makes it).
        private static string ThumbWork(string model, string name, DateTime time, WrapPackage.Package package, int faces, out byte[] mainSource, out int face)
        {
            mainSource = null;
            bool template = name == kTemplate;
            byte[] source = package.Thumb;
            face = (faces & 1) != 0 ? 0 : Enumerable.Range(0, kFiles.Length).FirstOrDefault(f => (faces & (1 << f)) != 0);
            if (source == null) source = template ? package.Guides[face] ?? package.Faces[face] : package.Faces[face];
            if (source == null)
                return "";
            string file = ThumbFile(model, name, time, face);
            string thumbPath = Path.Combine(Thumbs, file);
            if (File.Exists(thumbPath))
                return $"coui://{kHost}/{file}";
            if (PngDecoder.Refusal(source) != null)
                return "";
            if (!PngDecoder.TryDecode(source, out int w, out int h, out byte[] rgba))
            {
                mainSource = source;
                return "";
            }
            int tw = kThumbWidth, th = Math.Min(120, Math.Max(16, (int)Math.Round(kThumbWidth * h / (double)w)));
            Directory.CreateDirectory(Thumbs);
            File.WriteAllBytes(thumbPath, WrapTemplate.Png(tw, th, WrapPackage.ScaleDown(rgba, w, h, tw, th)));
            return $"coui://{kHost}/{file}";
        }

        private static string ThumbFile(string model, string name, DateTime time, int face)
        {
            uint stamp = (uint)(time.Ticks ^ (time.Ticks >> 32)) ^ (uint)StableHash(model + "/" + name);
            return $"{Safe(model)}-{Safe(name)}-{face}-{stamp:x8}.png";
        }

        // The old way (main thread: the game's decoder and the graphics card), for a picture the worker's decoder does not take.
        private static string ThumbOnMain(string model, string name, DateTime time, byte[] source, int face)
        {
            string file = ThumbFile(model, name, time, face);
            string thumbPath = Path.Combine(Thumbs, file);
            if (!File.Exists(thumbPath))
            {
                Texture2D picture = Decode(source, false);
                if (picture == null)
                    return "";
                try
                {
                    Directory.CreateDirectory(Thumbs);
                    File.WriteAllBytes(thumbPath, Scaled(picture, kThumbWidth, Mathf.Clamp(Mathf.RoundToInt(kThumbWidth * picture.height / (float)picture.width), 16, 120)));
                }
                finally
                {
                    UnityEngine.Object.Destroy(picture);
                }
            }
            return $"coui://{kHost}/{file}";
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in s) h = h * 31 + c;
                return h;
            }
        }

        private static byte[] Scaled(Texture2D picture, int w, int h)
        {
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture was = RenderTexture.active;
            Graphics.Blit(picture, rt);
            RenderTexture.active = rt;
            var small = new Texture2D(w, h, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            small.Apply();
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            byte[] png = small.EncodeToPNG();
            UnityEngine.Object.Destroy(small);
            return png;
        }

        // A PNG's size from its header ("w x h"; "?" when it is not a PNG).
        private static string PngSize(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                {
                    var b = new byte[24];
                    return f.Read(b, 0, 24) < 24 ? "?" : PngSize(b);
                }
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string PngSize(byte[] b)
        {
            if (b == null || b.Length < 24 || b[1] != 'P' || b[2] != 'N' || b[3] != 'G')
                return "?";
            int w = b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19], h = b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23];
            return $"{w}x{h}";
        }

        private static string Safe(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray());

        private static string Quote(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append($"\\u{(int)c:x4}");
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        // ---------------------------------------------------------------- the manager's actions

        private static string ModelFolder() => WrapPackage.ModelFolder(Root, s_Vehicle.Model);

        /// <summary>
        /// Opens the folder of the model's wraps (made with the README first) and reads the lists again.
        /// </summary>
        internal static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Root);
                WriteReadme();
                string dir = ModelFolder();
                if (dir != null) Directory.CreateDirectory(dir);
                if (dir == null || Path.GetFullPath(dir).IndexOf(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) != 0)
                    dir = Root;
                Changed();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Body wraps: cannot open {Root}: {ex.Message}");
            }
        }

        /// <summary>
        /// A new wrap to paint: "wrap-N.zip" in the model's folder, a copy of its template (the empty pictures, the guides), with the
        /// folder opened in Explorer. Returns its name ("" when it failed).
        /// </summary>
        internal static string NewWrap()
        {
            try
            {
                Directory.CreateDirectory(Root);
                WriteReadme();
                string template = EnsureTemplate();
                if (template == null) return "";
                string folder = Path.GetDirectoryName(template);
                int n = 1;
                while (File.Exists(Path.Combine(folder, $"wrap-{n}.zip"))) n++;
                string name = $"wrap-{n}";
                File.Copy(template, Path.Combine(folder, name + ".zip"));
                Changed();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
                return name;
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Body wraps: cannot make a new wrap: {ex.Message}");
                return "";
            }
        }

        private static void WriteReadme()
        {
            // the template (prebuild thread) and the manager (main thread) both write it: a write that finds it open is the same text
            // English first, then Chinese: a wrap is passed on between players of either
            string manual = BusLocale.In("en-US", "Wrap.Readme") + "\n\n" + BusLocale.In("zh-HANS", "Wrap.Readme") + "\n";
            try { File.WriteAllText(Path.Combine(Root, "README.txt"), manual.Replace("\n", "\r\n"), Encoding.UTF8); }
            catch (IOException) { }
        }

        // ---------------------------------------------------------------- loading

        /// <summary>
        /// Per frame: the selected wrap loaded (again when its zip changes), or none. A load takes a few frames: the zip and the pictures are
        /// made on a worker thread (Work), and this uploads one texture a frame; the wrap shown meanwhile (the previous one of the same
        /// vehicle) stays until the new one replaces it all at once. <see cref="Loading"/> says one is under way.
        /// </summary>
        internal void Update(string name)
        {
            if (m_Load != null)
                Pump();
            name = name ?? "";
            float now = Time.unscaledTime;
            if (name == m_Name && m_VehicleId == s_VehicleId && now < m_NextPoll)
                return;
            m_NextPoll = now + kPollSeconds;
            if (name.Length == 0)
            {
                if (m_Name != name)
                {
                    CancelLoad();
                    Unload();
                }
                m_Name = name;
                m_VehicleId = s_VehicleId;
                return;
            }

            Vehicle v = s_Vehicle;
            string path = ZipOf(name);
            DateTime time = path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            if (name == m_Name && m_VehicleId == s_VehicleId && time == m_Time)
                return;
            // the pictures worn belong to this vehicle: they stay until the new wrap is ready; another vehicle's are no use at all
            bool keep = m_VehicleId == s_VehicleId && time != default;
            m_Name = name;
            m_VehicleId = s_VehicleId;
            CancelLoad();
            if (!keep)
                Unload();
            m_Time = time;
            Changed();
            if (time == default)
            {
                Warn(name + "/" + v.Model, $"Body wraps: no wrap \"{name}\" for {v.Model} (a wrap belongs to one model: {WrapPackage.ModelFolder(Root, v.Model)}); no wrap.");
                return;
            }

            StartLoad(name, path, time, v);
        }

        // The main thread's share of a load: wait for the worker, then one picture a frame (a texture made from the prepared pixels, or, for a
        // picture the worker's decoder would not take, the whole old path with the game's LoadImage), then the swap.
        private void Pump()
        {
            Load load = m_Load;
            if (load == null || !load.Done)
                return;
            if (load.VehicleId != s_VehicleId || load.Cancelled)
            {
                CancelLoad();
                return;
            }
            if (load.Refused != null)
            {
                m_Load = null;
                m_Time = load.Time;            // not tried again until the zip changes
                Warn(load.WarningKey, load.Refused);
                return;
            }
            if (load.Warning != null)
            {
                m_Load = null;
                Unload();
                m_Time = load.Time;
                Warn(load.WarningKey, load.Warning);
                Changed();
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (load.Next < load.Files.Length && load.Files[load.Next] == null)
                load.Next++;
            if (load.Next < load.Files.Length)
            {
                int f = load.Next++;
                if (load.Rgba[f] != null)
                {
                    try
                    {
                        load.Textures[f] = MakeTexture(load.Rgba[f], load.Width[f], load.Height[f], "Bus Driver wrap " + kFiles[f]);
                        load.Kept[f] = load.Rgba[f];
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn($"Body wraps: cannot make the texture of {kFiles[f]}: {ex.Message}");
                    }
                }
                else
                    load.Textures[f] = Build(f, load.Files[f], load.Have, load.OutlineMm, load.Name, out load.Kept[f]);
                load.Frames++;
                load.UploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                return;
            }

            // every picture is made: the wrap replaces the one worn, all at once
            m_Load = null;
            Unload();
            m_Time = load.Time;
            var loaded = new List<string>();
            for (int f = 0; f < load.Textures.Length; f++)
            {
                if (load.Notes[f] != null)
                    Mod.log.Info(load.Notes[f]);
                m_Pictures[f] = load.Textures[f];
                m_Rgba[f] = load.Textures[f] != null ? load.Kept[f] : null;
                if (m_Pictures[f] != null)
                    loaded.Add($"{kFiles[f]} {m_Pictures[f].width} x {m_Pictures[f].height}");
            }
            if (loaded.Count > 0)
            {
                Mod.log.Info($"Body wrap \"{load.Name}\" of {load.Vehicle.Model}: {string.Join(", ", loaded)}.");
                Mod.log.Info($"Body wrap \"{load.Name}\": pictures made on a background thread in {load.WorkerMs:0} ms, uploaded in " +
                             $"{load.UploadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms over {load.Frames} frame(s) on the main thread.");
            }
            else
                Warn(load.Path, $"Body wraps: no pictures in {load.Path}; no wrap.");
            Changed();
        }

        private void Warn(string key, string message)
        {
            if (m_Warned == key + s_VehicleId) return;
            m_Warned = key + s_VehicleId;
            Mod.log.Warn(message);
        }

        private static Texture2D Decode(byte[] png, bool mips)
        {
            if (PngDecoder.Refusal(png) != null)
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mips, false) { hideFlags = HideFlags.DontSave, name = "Bus Driver wrap" };
            if (!tex.LoadImage(png, false))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            return tex;
        }

        /// <summary>
        /// A picture ready to draw: decoded, and where it has see-through pixels, the outline round them in the windows and doors it
        /// covers and the colour bled under them (so the scaled-down levels do not darken the edge). The user's PNG is not touched.
        /// This is the main-thread path, for a picture the worker's decoder would not take (16 bits, interlaced, a JPG): the game's LoadImage.
        /// </summary>
        private static Texture2D Build(int picture, byte[] png, bool[] have, float outlineMm, string name, out byte[] pixelsOut)
        {
            pixelsOut = null;
            Texture2D decoded = Decode(png, false);
            if (decoded == null)
            {
                Mod.log.Warn($"Body wraps: {kFiles[picture]} of \"{name}\" is not a PNG or JPG picture.");
                return null;
            }
            int w = decoded.width, h = decoded.height;
            Color32[] pixels = decoded.GetPixels32();
            UnityEngine.Object.Destroy(decoded);
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                rgba[4 * i] = pixels[i].r; rgba[4 * i + 1] = pixels[i].g; rgba[4 * i + 2] = pixels[i].b; rgba[4 * i + 3] = pixels[i].a;
            }
            string note = Finish(s_Vehicle, picture, rgba, w, h, have, outlineMm, name);
            if (note != null)
                Mod.log.Info(note);
            try
            {
                Texture2D tex = MakeTexture(rgba, w, h, "Bus Driver wrap " + kFiles[picture]);
                pixelsOut = rgba;
                return tex;
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Body wraps: cannot make the texture of {kFiles[picture]}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The work on a decoded picture (any thread): the outline round its see-through parts in the vehicle's windows and doors, and the
        /// colour under them. Returns the log line when the picture had see-through pixels, else null.
        /// </summary>
        private static string Finish(Vehicle v, int picture, byte[] rgba, int w, int h, bool[] have, float outlineMm, string name)
        {
            int[] faces = WrapPackage.FacesOf(picture, have);
            var areas = new List<(byte, float2x3)>();
            foreach (int face in faces)
                areas.AddRange(v.Areas((WrapFace)face));
            float metres = faces.Length > 0 ? v.Size((WrapFace)faces[0]).x : 0f;
            if (WrapPackage.Prepare(rgba, w, h, areas, outlineMm, metres, out int outlined, out int recoloured))
                return $"Body wrap \"{name}\" {kFiles[picture]}: see-through pixels, {outlined} outlined white, {recoloured} see-through pixels recoloured.";
            return null;
        }

        // A drawable texture from width x height RGBA bytes (rows from the bottom, as the game's pictures run).
        private static Texture2D MakeTexture(byte[] rgba, int w, int h, string name)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { hideFlags = HideFlags.DontSave, name = name };
            // Level 0 only: the texture has a full mip chain, so LoadRawTextureData (which wants every level's bytes) refused the
            // picture; Apply(true) makes the smaller levels from level 0. SetPixelData writes the bytes as they are (no Color32 array of
            // the whole picture made and filled one pixel at a time); a failure of it falls back to the Color32 path.
            bool set = false;
            try
            {
                tex.SetPixelData(rgba, 0);
                set = true;
            }
            catch (Exception)
            {
            }
            if (!set)
            {
                var level0 = new Color32[w * h];
                for (int i = 0; i < level0.Length; i++)
                    level0[i] = new Color32(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]);
                tex.SetPixels32(level0);
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            tex.Apply(true, true);
            return tex;
        }

        private void Unload()
        {
            foreach (FaceMaterial f in m_Materials.Values)
            {
                if (f.Material != null) UnityEngine.Object.Destroy(f.Material);
                if (f.Own != null) UnityEngine.Object.Destroy(f.Own);
            }
            m_Materials.Clear();
            for (int f = 0; f < m_Pictures.Length; f++)
            {
                if (m_Pictures[f] != null) UnityEngine.Object.Destroy(m_Pictures[f]);
                m_Pictures[f] = null;
                m_Rgba[f] = null;
            }
            m_Time = default;
        }

        internal void Destroy()
        {
            CancelLoad();
            Unload();
            m_Name = null;
        }

        /// <summary>
        /// The material of a section's face: its picture; for the sides and the roof the section's share of it (the section
        /// starts start metres behind the bus's front and is length metres long, of total). Null without a picture. The door pieces use
        /// their side's material too (their picture coordinates are the section's).
        /// </summary>
        internal Material Material(int section, WrapFace face, float start, float length, float total)
        {
            int source = PictureOf(face);
            Texture2D picture = m_Pictures[source];
            if (picture == null)
                return null;
            if (!m_Materials.TryGetValue((section, face), out FaceMaterial f) || f.Material == null)
            {
                Material made = CabMaterials.Wrap(picture);
                if (made == null)
                    return null;
                // The picture's tiling and offset stay as they are: the game's cut-out shader does not apply them (the right side, asked to
                // run its picture backwards through them, came out mirrored in the game, 18 October). The section's share of the picture and
                // the mirroring are baked into the texture below instead, which works with any shader.
                made.SetTextureScale(kMap, Vector2.one);
                made.SetTextureOffset(kMap, Vector2.zero);
                f = new FaceMaterial { Material = made };
                m_Materials[(section, face)] = f;
            }
            // the section's share of the picture (u from the bus's front): the left side and the roof have their front at the picture's
            // left, the right side at its right (it runs backwards); the front and the rear are whole
            float a = start / math.max(0.1f, total), s = length / math.max(0.1f, total);
            int x0 = 0, x1 = picture.width;
            bool mirrored = false;
            if (face == WrapFace.Left || face == WrapFace.Roof || face == WrapFace.Right)
                WrapPackage.ShareColumns(picture.width, a, s, face == WrapFace.Right, out x0, out x1, out mirrored);
            if (f.Source != source || f.X0 != x0 || f.X1 != x1 || f.Mirrored != mirrored)
            {
                Texture2D texture = picture, own = null;
                if ((mirrored || x0 != 0 || x1 != picture.width) && m_Rgba[source] != null)
                {
                    try
                    {
                        byte[] part = WrapPackage.Columns(m_Rgba[source], picture.width, picture.height, x0, x1, mirrored);
                        texture = own = MakeTexture(part, x1 - x0, picture.height, "Bus Driver wrap " + kFiles[source] + $" {x0}-{x1}{(mirrored ? " mirrored" : "")}");
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn($"Body wraps: cannot bake the share of {kFiles[source]} ({x0}-{x1}): {ex.Message}; the whole picture is shown.");
                    }
                }
                f.Material.SetTexture(kMap, texture);
                if (f.Own != null) UnityEngine.Object.Destroy(f.Own);
                f.Own = own;
                f.Source = source; f.X0 = x0; f.X1 = x1; f.Mirrored = mirrored;
            }
            return f.Material;
        }

        // ---------------------------------------------------------------- the template

        /// <summary>
        /// The template of the model being driven (Wraps/&lt;model&gt;/template.zip), once per session and layout: written when it is
        /// missing or from another layout or version of this generator. Made on the prebuild thread (CabPrebuild): projecting a model
        /// into its guide pictures took 0.2 to 6.6 s of the main thread at the first drive of each model (the log of 21 October:
        /// "wrap template (max 3814.6)", "(max 6636.2)"), while nothing needs the file until a wrap is made from it.
        /// </summary>
        internal static void WriteTemplate()
        {
            Vehicle v = s_Vehicle;
            string path = WrapPackage.ZipPath(Root, v.Model, kTemplate);
            if (path == null || !s_Templates.Add(v.Key + "/" + WrapTemplate.Revision))
                return;
            WrapTemplate.Input input = TemplateInput(v);
            string model = v.Model;
            CabPrebuild.Enqueue(() =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool written = TemplateFile(path, input, out bool any);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (written)
                    Mod.log.Info($"Body wrap template of {model} written on a background thread in {ms:0} ms.");
                else if (!any)
                    Mod.log.Info($"Body wrap template of {model}: the model has no wrap faces (checked on a background thread in {ms:0} ms).");
                return 0;
            });
        }

        /// <summary>The template file now, on the calling thread (a new wrap is copied from it; the review probe reads it). Null when there is none.</summary>
        private static string EnsureTemplate()
        {
            Vehicle v = s_Vehicle;
            string path = WrapPackage.ZipPath(Root, v.Model, kTemplate);
            if (path == null)
                return null;
            bool known = !s_Templates.Add(v.Key + "/" + WrapTemplate.Revision);
            lock (s_TemplateLock)
            {
                if (known && File.Exists(path))
                    return path;      // this session's (a background write of it holds the lock until it is done)
            }
            TemplateFile(path, TemplateInput(v), out bool _);
            return File.Exists(path) ? path : null;
        }

        /// <summary>Writes the review probe's template now (the probe reads it in the same request).</summary>
        internal static void WriteTemplateNow() => EnsureTemplate();

        private static readonly object s_TemplateLock = new object();

        private static WrapTemplate.Input TemplateInput(Vehicle v) =>
            new WrapTemplate.Input { Model = v.Model, Layout = v.Layout, OutlineMm = v.OutlineMm, Sections = v.Sections, Profile = v.Profile,
                English = BusLocale.English, Chinese = key => BusLocale.In("zh-HANS", key) };

        // The template file of a vehicle (any thread; one at a time): kept when it is of this layout and generator, else made and written.
        // True when it was written; any: whether the model has a face to wrap at all.
        private static bool TemplateFile(string path, WrapTemplate.Input input, out bool any)
        {
            any = true;
            lock (s_TemplateLock)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            WrapPackage.Manifest old = WrapPackage.Read(path).Manifest;
                            if (old.Layout == input.Layout && old.Title == WrapTemplate.Title(input)) return false;
                        }
                        catch (WrapPackage.Error)
                        {
                            // an unreadable template is written again
                        }
                    }
                    if (!WrapTemplate.Build(input, out WrapPackage.Manifest manifest, out List<KeyValuePair<string, byte[]>> entries))
                    {
                        any = false;
                        return false;
                    }
                    Directory.CreateDirectory(Root);
                    WriteReadme();
                    WrapPackage.Write(path, manifest, entries);
                    Changed();
                    return true;
                }
                catch (Exception ex)
                {
                    Mod.log.Warn($"Body wraps: cannot write the template: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
