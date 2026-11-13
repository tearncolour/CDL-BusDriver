using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using LedPanel = CDLC.Asset.Led.LedPanel;

// The high-density LED signs on the bus model (LedHd: 32 rows, the Shanghai layout, an ink a material): the dots are lit-dots-only
// meshes from CDL Assets (CDLC.Asset.Led.LedPanel and LedMesh), the faces, the layout and the readings its sources
// (DotFont, HdComposer, StopEnglish). What stays here is Bus Driver's: the files the fonts are loaded from and what is said about
// them, the line label and the rear sign's warnings, the inks' materials from the Bus palette, and the lease on that palette
// (CabMaterials.AcquireResources) each panel is handed, which keeps them alive while it draws them. Main thread only.
namespace CDLC.BusDriver.Interior
{
    /// <summary>The dot faces and the readings of the high-density signs, loaded once on a worker thread, and what Bus Driver adds to them.</summary>
    internal static class HdFonts
    {
        static HdFontLoader s_Loader;

        /// <summary>The faces and the readings; null while they load (asked again later) or when they could not be loaded (Failed).</summary>
        internal static HdFontSet Get() =>
            (s_Loader ?? (s_Loader = new HdFontLoader(CDLC.Asset.AssetApi.HdFontFile, CDLC.Asset.AssetApi.PinyinFile, Loaded))).Get();

        internal static bool Failed => s_Loader != null && s_Loader.Failed;

        static void Loaded(HdFontSet set, string error)
        {
            if (set == null)
                Mod.log.Warn($"LED signs: {error}; the outside signs are drawn in the classic style.");
            else
                Mod.log.Info($"LED signs: high-density font loaded ({set.Font.Count} glyphs in {set.Font.FaceCount} faces), " +
                    (set.Pinyin != null ? $"readings of {set.Pinyin.Count} characters." : "no readings (Fonts/pinyin.bin.gz of CDL Assets): no English lines."));
        }

        /// <summary>The English line of a stop name ("" without readings, or for a name that is not Chinese).</summary>
        internal static string English(string name)
        {
            HdFontSet set = s_Loader?.Get();
            return set?.Pinyin != null ? StopEnglish.Of(name, set.Pinyin) : "";
        }

        /// <summary>The English line of a fixed message: its English under a Chinese text, none under one in English already.</summary>
        internal static string Fixed(string text, string english) => StopEnglish.HasCjk(text) ? english : "";

        /// <summary>After the route number, the locale's word for a route ("路" in Chinese, nothing in English); none after a line's name.</summary>
        internal static string Suffix(string number) => LineLabel.IsRouteNumber(number) ? BusLocale.Text("LED.RouteSuffix") : "";

        /// <summary>The rear sign: the route label, or a warning in its ink with its arrow (RearSignRules).</summary>
        internal static HdContent Rear(RearMessage message, string prefix, string number)
        {
            switch (message)
            {
                case RearMessage.Braking:
                {
                    string text = BusLocale.Text("LED.Braking");
                    return HdContent.Warning(text, Fixed(text, BusLocale.English("LED.Braking")), HdInk.Warning, 0);
                }
                case RearMessage.TurnLeft:
                {
                    string text = BusLocale.Text("LED.TurnLeft");
                    return HdContent.Warning(text, Fixed(text, BusLocale.English("LED.TurnLeft")), HdInk.Sign, -1);
                }
                case RearMessage.TurnRight:
                {
                    string text = BusLocale.Text("LED.TurnRight");
                    return HdContent.Warning(text, Fixed(text, BusLocale.English("LED.TurnRight")), HdInk.Sign, 1);
                }
                default:
                    return HdContent.Label(prefix, number, Suffix(number));
            }
        }

        /// <summary>
        /// The inks' materials (HdInk): the amber text, the prefix's colour, the cyan English line, the red warning; <paramref name="cells"/>:
        /// for the solid cells, a lit cell's share as bright (CDLC.Asset.Led.LedGeometry.CellLight).
        /// </summary>
        internal static Material[] Inks(float3 prefixColour, bool cells = false)
        {
            float3[] colours = AiSignRules.HdInkColours(prefixColour);
            var inks = new Material[HdInk.Count];
            for (int i = 0; i < inks.Length; i++)
                if ((inks[i] = Ink(colours[i], cells)) == null)
                    return null;
            return inks;
        }

        /// <summary>One ink's material, the dots' or the solid cells'.</summary>
        internal static Material Ink(float3 colour, bool cells)
        {
            float light = cells ? CDLC.Asset.Led.LedGeometry.CellLight : 1f;
            return CabMaterials.DimmedLamp(colour, AiSignRules.SignNits * light, AiSignRules.SignNight * light);
        }

        /// <summary>The option "LED sign style" asks for the high-density signs.</summary>
        internal static bool Wanted => Mod.Settings == null || Mod.Settings.LedSignStyle == LedSignStyle.Shanghai;

        /// <summary>The option "Rear sign warnings".</summary>
        internal static bool RearWarnings => Mod.Settings == null || Mod.Settings.RearSignWarnings;
    }

    /// <summary>A high-density sign on the bus model: an HdComposer drawn on a LedPanel, rewritten when the content or the scroll changes.</summary>
    internal sealed class HdSign : ISign
    {
        readonly LedPanel m_Panel;
        readonly HdComposer m_Composer;
        readonly float m_DotsPerSecond;
        HdContent m_Content;
        bool m_Shown;
        int m_Offset = -1;
        double m_Start;

        internal HdSign(Transform parent, float3 centre, float2 size, int dotsX, Material[] inks, Material[] cellInks, DotFont font, LedFont fallback,
            quaternion rotation, float dotsPerSecond)
        {
            m_Composer = new HdComposer(font, fallback, dotsX);
            m_Panel = new LedPanel(parent, centre, size, dotsX, HdComposer.Rows, inks, rotation, cellInks, CabMaterials.AcquireResources());
            m_DotsPerSecond = dotsPerSecond;
        }

        internal bool Alive => m_Panel.Alive;

        internal bool Visible { set => m_Panel.Visible = value; }

        /// <summary>The prefix's ink in the line's prefix colour (option "Route prefix colours"), the dots' and the cells'.</summary>
        internal void SetPrefix(float3 srgb) => m_Panel.SetMaterial(HdInk.Prefix, HdFonts.Ink(srgb, false), HdFonts.Ink(srgb, true));

        /// <summary>Shows a content (empty: dark); a scrolling one moves from when it was first shown.</summary>
        internal void Show(in HdContent content, double now)
        {
            m_Panel.UpdateDots();
            if (content.Empty)
            {
                if (m_Shown)
                {
                    m_Composer.Clear();
                    m_Panel.Apply(m_Composer.Buffer, m_Composer.Ink);
                    m_Shown = false;
                    m_Content = default;
                    m_Offset = -1;
                }

                return;
            }

            if (!m_Shown || !content.Equals(m_Content))
            {
                m_Content = content;
                m_Composer.Compose(content);
                m_Start = now;
                m_Offset = -1;
                m_Shown = true;
            }

            int offset = m_Composer.Scrolls ? (int)((now - m_Start) * m_DotsPerSecond) % m_Composer.Cycle : 0;
            if (offset == m_Offset)
                return;
            m_Offset = offset;
            m_Composer.Render(offset);
            m_Panel.Apply(m_Composer.Buffer, m_Composer.Ink);
        }

        internal void Destroy() => m_Panel.Destroy();

        bool ISign.Alive => Alive;
        bool ISign.Visible { set => Visible = value; }
        void ISign.Show(string prefix, string block, string message, double now) => Show(HdContent.Route(prefix, block, "", message, HdFonts.English(message)), now);
        void ISign.Destroy() => Destroy();
    }
}
