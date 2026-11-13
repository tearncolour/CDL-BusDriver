using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// The texts of the mod's language files (lang/&lt;locale&gt;.json) the way BusLocale.Text gives them: the keys without the "CDLC.BusDriver." prefix; the English text where
/// the language has none; the key where no language has one. The code of the mod holds no text of its own, so a test that wants a sentence reads it from the file.
/// </summary>
static class LangPack
{
    const string Prefix = "CDLC.BusDriver.";
    static readonly Dictionary<string, Dictionary<string, string>> s_Loaded = new Dictionary<string, Dictionary<string, string>>();

    /// <summary>The language files of the tree the test program was built from (the csproj copies them beside it).</summary>
    internal static string Dir() => Path.Combine(AppContext.BaseDirectory, "lang");

    internal static Dictionary<string, string> Load(string locale)
    {
        if (s_Loaded.TryGetValue(locale, out Dictionary<string, string> loaded)) return loaded;
        var texts = new Dictionary<string, string>();
        string file = Path.Combine(Dir(), locale + ".json");
        if (File.Exists(file))
            foreach (JsonProperty p in JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿')).RootElement.EnumerateObject())
                if (p.Name.StartsWith(Prefix, StringComparison.Ordinal)) texts[p.Name.Substring(Prefix.Length)] = p.Value.GetString();
        return s_Loaded[locale] = texts;
    }

    internal static Func<string, string> Text(string locale)
    {
        Dictionary<string, string> own = Load(locale), english = Load("en-US");
        return key => own.TryGetValue(key, out string text) && !string.IsNullOrEmpty(text) ? text
            : english.TryGetValue(key, out string fallback) ? fallback : Prefix + key;
    }
}
