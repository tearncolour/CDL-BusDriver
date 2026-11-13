using System;
using System.Reflection;
using Game.SceneFlow;
using Game.UI;
using Unity.Entities;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Names the way the game's own panels show them: a line is "Bus line 12" (or its custom name), a stop is its address
    /// "23 Harbour Street" (or its custom name). NameSystem builds these as formatted names for the UI; this renders them
    /// to plain text with the active language. NameSystem.GetRenderedLabelName would only give the prefab name ("European
    /// bus stop shelter").
    /// </summary>
    internal static class EntityNames
    {
        private static FieldInfo s_Type;
        private static FieldInfo s_Id;
        private static FieldInfo s_Args;
        private static bool s_Initialized;

        /// <summary>The player gave the entity its name (LineLabel then keeps a line's own words).</summary>
        internal static bool PlayerNamed(NameSystem names, Entity entity) => names != null && entity != Entity.Null && names.TryGetCustomName(entity, out _);

        internal static string Text(NameSystem names, Entity entity)
        {
            if (names == null || entity == Entity.Null)
                return "";

            try
            {
                Initialize();
                object name = names.GetName(entity);
                string type = s_Type?.GetValue(name)?.ToString() ?? "";
                string id = s_Id?.GetValue(name) as string;
                if (string.IsNullOrEmpty(id))
                    return "";

                if (type == "Custom")
                    return id;

                string text = Localize(id);
                if (text == null)
                    return type == "Localized" ? "" : id;

                if (type == "Formatted" && s_Args?.GetValue(name) is string[] args)
                {
                    for (int i = 0; i + 1 < args.Length; i += 2)
                    {
                        string value = args[i + 1] ?? "";
                        text = text.Replace("{" + args[i] + "}", Localize(value) ?? value);
                    }
                }

                return text.Trim();
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static void Initialize()
        {
            if (s_Initialized)
                return;

            s_Initialized = true;
            const BindingFlags kFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type type = typeof(NameSystem.Name);
            s_Type = type.GetField("m_NameType", kFlags);
            s_Id = type.GetField("m_NameID", kFlags);
            s_Args = type.GetField("m_NameArgs", kFlags);
            if (s_Type == null || s_Id == null)
                Mod.log.Warn("NameSystem.Name layout changed; stop and line names fall back to numbers.");
        }

        private static string Localize(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
            return dictionary != null && dictionary.TryGetValue(id, out string value) && !string.IsNullOrEmpty(value) ? value : null;
        }
    }
}
