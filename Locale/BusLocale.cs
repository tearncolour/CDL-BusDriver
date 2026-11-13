using System;
using System.Collections.Generic;
using System.IO;
using Colossal.Localization;
using Game.SceneFlow;
using Newtonsoft.Json;

namespace CDLC.BusDriver
{
    /// <summary>
    /// All texts are "CDLC.BusDriver.*" keys (plus the options page keys) in lang/&lt;locale&gt;.json, registered with the game's
    /// localization so language packs can translate or override them. en-US and zh-HANS are embedded; lang/*.json next to
    /// the dll is loaded on top.
    /// </summary>
    internal static class BusLocale
    {
        internal const string kPrefix = "CDLC.BusDriver.";
        private const string kResourcePrefix = "CDLC.BusDriver.lang.";
        private static readonly List<(string locale, MemorySource source)> s_Registered = new List<(string, MemorySource)>();
        private static Dictionary<string, string> s_English = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, Dictionary<string, string>> s_Locales = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        internal static void Register(string modDirectory)
        {
            var locales = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            System.Reflection.Assembly assembly = typeof(BusLocale).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(kResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
                    continue;

                string locale = resource.Substring(kResourcePrefix.Length, resource.Length - kResourcePrefix.Length - ".json".Length);
                using (Stream stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream))
                    Merge(locales, locale, reader.ReadToEnd());
            }

            if (!string.IsNullOrEmpty(modDirectory))
            {
                string folder = Path.Combine(modDirectory, "lang");
                if (Directory.Exists(folder))
                {
                    foreach (string file in Directory.GetFiles(folder, "*.json"))
                    {
                        try
                        {
                            Merge(locales, Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
                        }
                        catch (Exception ex)
                        {
                            Mod.log.Warn($"Language file {file} skipped: {ex.Message}");
                        }
                    }
                }
            }

            s_Locales = locales;
            if (locales.TryGetValue("en-US", out Dictionary<string, string> english))
                s_English = english;

            LocalizationManager manager = GameManager.instance?.localizationManager;
            if (manager == null)
                return;

            foreach (KeyValuePair<string, Dictionary<string, string>> locale in locales)
            {
                var source = new MemorySource(locale.Value);
                manager.AddSource(locale.Key, source);
                s_Registered.Add((locale.Key, source));
            }

            Mod.log.Info($"Registered bus texts for {locales.Count} locale(s).");
        }

        private static void Merge(Dictionary<string, Dictionary<string, string>> locales, string locale, string json)
        {
            Dictionary<string, string> entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (entries == null)
                return;

            if (!locales.TryGetValue(locale, out Dictionary<string, string> target))
            {
                target = new Dictionary<string, string>(StringComparer.Ordinal);
                locales[locale] = target;
            }

            foreach (KeyValuePair<string, string> entry in entries)
                target[entry.Key] = entry.Value;
        }

        internal static void Unregister()
        {
            LocalizationManager manager = GameManager.instance?.localizationManager;
            if (manager != null)
            {
                foreach ((string locale, MemorySource source) in s_Registered)
                    manager.RemoveSource(locale, source);
            }

            s_Registered.Clear();
        }

        /// <summary>Does this mod have texts for the locale (its own or a language pack's)?</summary>
        internal static bool HasLocale(string localeId)
        {
            foreach ((string locale, MemorySource _) in s_Registered)
            {
                if (string.Equals(locale, localeId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// A text of this mod in the active language (the key without the prefix); the English text when that language has none; the key itself when no
        /// language has one, so that a missing text shows as what it is. The code holds no text of its own: the language files are the only place.
        /// </summary>
        internal static string Text(string key)
        {
            try
            {
                LocalizationDictionary dictionary = GameManager.instance?.localizationManager?.activeDictionary;
                if (dictionary != null && dictionary.TryGetValue(kPrefix + key, out string value) && !string.IsNullOrEmpty(value))
                    return value;
            }
            catch (Exception)
            {
            }

            return English(key);
        }

        /// <summary>A text in one language of the mod's files whatever the game's language is (a file that holds two languages); English where that language has none.</summary>
        internal static string In(string locale, string key) =>
            s_Locales.TryGetValue(locale, out Dictionary<string, string> texts) && texts.TryGetValue(kPrefix + key, out string text) && !string.IsNullOrEmpty(text) ? text : English(key);

        /// <summary>
        /// The English text of a key, whatever the game's language is: the English line of a sign that shows two languages, and the text of a language that has
        /// none of its own. An English text that is empty on purpose (the word after a route's number, which English does not have) stays empty.
        /// </summary>
        internal static string English(string key) => s_English.TryGetValue(kPrefix + key, out string english) ? english : kPrefix + key;

        internal static void ReportMissing(IEnumerable<string> keys)
        {
            var missing = new List<string>();
            foreach (string key in keys)
            {
                if (!s_English.ContainsKey(key))
                    missing.Add(key);
            }

            if (missing.Count > 0)
                Mod.log.Warn($"Missing English texts: {string.Join(", ", missing)}");
        }
    }
}
