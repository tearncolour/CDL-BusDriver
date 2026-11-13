using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public static class Time { public static int frameCount; }
}

namespace CDLC.Asset
{
    public static class AssetApi
    {
        public static string HdFontFile => null;
        public static string PinyinFile => null;
    }
}

namespace CDLC.BusDriver
{
    public static class Mod
    {
        public sealed class Logger
        {
            public void Warn(string message) { }
            public void Info(string message) { }
        }
        public sealed class SettingsData
        {
            public LedSignStyle LedSignStyle { get; set; }
            public bool RearSignWarnings { get; set; }
        }
        public static Logger log = new Logger();
        public static string ModDirectory => "";
        public static SettingsData Settings => null;
    }
    internal static class BusLocale
    {
        internal static string Text(string key, string fallback) => fallback;
    }
}

namespace CDLC.BusDriver.Interior
{
    internal interface ISign
    {
        bool Alive { get; }
        bool Visible { set; }
        void Show(string prefix, string block, string message, double now);
        void Destroy();
    }
}

namespace Colossal.IO.AssetDatabase
{
    public sealed class AssetDatabase
    {
        public static readonly AssetDatabase global = new AssetDatabase();
        public readonly Resources resources = new Resources();
        public sealed class Resources { public readonly MaterialLibrary materialLibrary = new MaterialLibrary(); }
        public sealed class MaterialLibrary
        {
            public readonly List<Material> Materials = new List<Material>();
            public Material GetMaterial(int index) => index < Materials.Count ? Materials[index] : null;
        }
    }
}
