using System;
using System.Reflection;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Hands the option "Glass detail" to CDL Assets (<c>AssetApi.GlassQualityLevel</c>, 0.6.2 and later), which owns the glass material of the driven
    /// bus's glass, of the AI buses' and of the stations'. The property is looked up by reflection: Assets 0.6.0 and 0.6.1 do not have it, and Bus Driver
    /// then still runs (only the part of the option that is Bus Driver's own works: Fast leaves the cab glass out of the seat views).
    /// </summary>
    internal static class GlassQualityLink
    {
        private static PropertyInfo s_Property;
        private static bool s_Looked;
        private static int s_Pushed = -1;

        /// <summary>Main thread: sets Assets' glass quality when the option changed (cheap otherwise: one comparison).</summary>
        internal static void Sync(GlassDetail detail)
        {
            int level = (int)detail;
            if (level == s_Pushed)
                return;

            if (!s_Looked)
            {
                s_Looked = true;
                s_Property = typeof(CDLC.Asset.AssetApi).GetProperty("GlassQualityLevel", BindingFlags.Public | BindingFlags.Static);
                if (s_Property == null)
                    Mod.log.Warn("The installed CDL Assets has no glass quality (it needs 0.6.2): the option \"Glass detail\" only leaves the cab glass out of the seat views on Fast.");
            }

            s_Pushed = level;
            if (s_Property == null)
                return;
            try { s_Property.SetValue(null, level); }
            catch (TargetInvocationException failure)
            {
                Exception inner = failure.InnerException ?? failure;
                Mod.log.Warn($"CDL Assets could not take the glass detail {detail}: {inner.GetType().Name}: {inner.Message}");
            }
        }
    }
}
