using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Mathematics;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// A station pack's LED screens (format "cdlc-station-signs", version 1): &lt;prefab&gt;.cdls.json beside the station's .cok, read by this mod as a
    /// vehicle pack's .cdlb.json beside its bus (StationSignPacks). The pack is an asset (a copy of one of the game's bus stations with its own model,
    /// its screens' faces dark); this mod draws live departures on those faces (BusStationBoardSystem). No station is named in this mod's code: a
    /// station is ours when its prefab came from a .cok with such a file beside it.
    /// <para>In the building's frame (the game's object frame: x right, y up, z towards the street at the lot's front; metres):</para>
    /// <list type="bullet">
    /// <item>"stops": each bay's stop where the template's stop marker stands ("bay" a number 1..99, "position"), to find the game's stop of each bay;</item>
    /// <item>"screens": each screen's drawing area: "centre" on its face, "normal" the way the face looks (horizontal), "size" (w, h), "rows" of square
    /// dots (the columns follow), the "layout" it shows ("bay": one bay's lines a page at a time; "pylon": a bay's next buses in a narrow column;
    /// "departures": every bay's lines in rows) and the "bays" it shows.</item>
    /// </list>
    /// Everything is checked before a profile is made: a bad file is rejected with the reason, never half used. Pure: no Unity types but
    /// Unity.Mathematics; tested offline.
    /// </summary>
    internal sealed class StationSignProfile
    {
        internal const string Format = "cdlc-station-signs";
        internal const int Version = 1;
        internal const string Extension = ".cdls.json";
        internal const int MaxStops = 64, MaxScreens = 96, MaxColumns = 2048, MaxDots = 400000;

        internal enum Layout { Bay, Pylon, Departures }

        internal struct Stop
        {
            internal int Bay;
            internal float3 Position;
        }

        internal struct Screen
        {
            internal string Id;
            internal Layout Layout;
            internal int[] Bays;
            internal float3 Centre, Normal;
            internal float2 Size;
            internal int Rows, Columns;
        }

        internal string Prefab, Source;
        internal Stop[] Stops;
        internal Screen[] Screens;

        internal sealed class Error : Exception
        {
            internal Error(string message) : base(message) { }
        }

        internal static StationSignProfile Load(string path)
        {
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception e) { throw new Error("cannot read " + path + ": " + e.Message); }
            return Parse(text, path);
        }

        internal static StationSignProfile Parse(string text, string source)
        {
            Dictionary<string, object> root;
            try { root = MiniJson.Parse(text) as Dictionary<string, object>; }
            catch (MiniJson.FormatError e) { throw new Error("not valid JSON: " + e.Message); }
            if (root == null)
                throw new Error("not a JSON object");
            if (!(Get(root, "format") is string format) || format != Format)
                throw new Error("not a " + Format + " file");
            if (Integer(Get(root, "version"), "version", 1, 1000) != Version)
                throw new Error("version " + Get(root, "version") + " (this mod reads version " + Version + ")");
            if (!(Get(root, "prefab") is string prefab) || !VehicleProfileJson.ModelName(prefab))
                throw new Error("prefab must be ASCII letters and digits");

            var profile = new StationSignProfile { Prefab = prefab, Source = source };
            if (!(Get(root, "stops") is List<object> stops) || stops.Count == 0 || stops.Count > MaxStops)
                throw new Error("stops: 1.." + MaxStops + " expected");
            var stopList = new List<Stop>(stops.Count);
            var bays = new HashSet<int>();
            for (int i = 0; i < stops.Count; i++)
            {
                if (!(stops[i] is Dictionary<string, object> s))
                    throw new Error("stops[" + i + "]: an object expected");
                int bay = Integer(Get(s, "bay"), "stops[" + i + "].bay", 1, 99);
                if (!bays.Add(bay))
                    throw new Error("stops: bay " + bay + " twice");
                stopList.Add(new Stop { Bay = bay, Position = Vector3(Get(s, "position"), "stops[" + i + "].position", 200f) });
            }

            profile.Stops = stopList.ToArray();
            if (!(Get(root, "screens") is List<object> screens) || screens.Count == 0 || screens.Count > MaxScreens)
                throw new Error("screens: 1.." + MaxScreens + " expected");
            var screenList = new List<Screen>(screens.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < screens.Count; i++)
            {
                string where = "screens[" + i + "]";
                if (!(screens[i] is Dictionary<string, object> s))
                    throw new Error(where + ": an object expected");
                if (!(Get(s, "id") is string id) || id.Length == 0 || id.Length > 64 || !ids.Add(id))
                    throw new Error(where + ".id: a unique name expected");
                Layout layout;
                switch (Get(s, "layout") as string)
                {
                    case "bay": layout = Layout.Bay; break;
                    case "pylon": layout = Layout.Pylon; break;
                    case "departures": layout = Layout.Departures; break;
                    default: throw new Error(where + ".layout: bay, pylon or departures");
                }

                if (!(Get(s, "bays") is List<object> list) || list.Count == 0 || list.Count > MaxStops)
                    throw new Error(where + ".bays: 1.." + MaxStops + " bays expected");
                var screenBays = new int[list.Count];
                for (int k = 0; k < list.Count; k++)
                {
                    screenBays[k] = Integer(list[k], where + ".bays[" + k + "]", 1, 99);
                    if (!bays.Contains(screenBays[k]))
                        throw new Error(where + ".bays: bay " + screenBays[k] + " has no stop");
                }

                if (layout != Layout.Departures && screenBays.Length != 1)
                    throw new Error(where + ": a " + Get(s, "layout") + " screen shows one bay");
                float3 normal = Vector3(Get(s, "normal"), where + ".normal", 2f);
                if (math.abs(math.length(normal) - 1f) > 0.01f || math.abs(normal.y) > 0.01f)
                    throw new Error(where + ".normal: a horizontal unit vector expected (the face is vertical)");
                if (!(Get(s, "size") is List<object> size) || size.Count != 2 || !(size[0] is MiniJson.Number w) || !(size[1] is MiniJson.Number h) ||
                    !(w.Double >= 0.05 && w.Double <= 30.0) || !(h.Double >= 0.05 && h.Double <= 10.0))
                    throw new Error(where + ".size: [w, h] in metres (0.05..30, 0.05..10)");
                int rows = Integer(Get(s, "rows"), where + ".rows", 8, 256);
                int columns = Columns(new float2(w.Float, h.Float), rows);
                if (columns < 8 || columns > MaxColumns || (long)columns * rows > MaxDots)
                    throw new Error(where + $": {columns} x {rows} dots (8..{MaxColumns} columns, {MaxDots} dots at most)");
                screenList.Add(new Screen
                {
                    Id = id, Layout = layout, Bays = screenBays, Centre = Vector3(Get(s, "centre"), where + ".centre", 200f), Normal = math.normalize(normal),
                    Size = new float2(w.Float, h.Float), Rows = rows, Columns = columns,
                });
            }

            profile.Screens = screenList.ToArray();
            return profile;
        }

        /// <summary>The columns of square dots a screen of this size and rows has.</summary>
        internal static int Columns(float2 size, int rows) => rows <= 0 || size.y <= 0f ? 0 : (int)math.round(size.x / (size.y / rows));

        private static object Get(Dictionary<string, object> o, string key) => o.TryGetValue(key, out object v) ? v : null;

        private static int Integer(object value, string what, int min, int max)
        {
            if (!(value is MiniJson.Number n) || n.Double != Math.Floor(n.Double) || n.Double < min || n.Double > max)
                throw new Error(what + ": a whole number " + min + ".." + max + " expected");
            return (int)n.Double;
        }

        private static float3 Vector3(object value, string what, float limit)
        {
            if (!(value is List<object> l) || l.Count != 3 || !(l[0] is MiniJson.Number x) || !(l[1] is MiniJson.Number y) || !(l[2] is MiniJson.Number z))
                throw new Error(what + ": [x, y, z] expected");
            var v = new float3(x.Float, y.Float, z.Float);
            if (!math.all(math.isfinite(v)) || math.any(math.abs(v) > limit))
                throw new Error(what + ": out of range (" + limit + " m)");
            return v;
        }
    }
}
