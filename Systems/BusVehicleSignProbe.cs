using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CDLC.BusDriver.Interior;
using CDLC.BusDriver.Interior.Models;
using Unity.Mathematics;
using UnityEngine;

namespace CDLC.BusDriver
{
    /// <summary>Production LED components with explicit, controlled text; no route entity is fabricated.</summary>
    internal sealed class BusVehicleSignProbe : IDisposable
    {
        sealed class Screen
        {
            internal string Name;
            internal Vector3 Centre, Facing;
            internal float2 Size;
            internal ISign Sign;
        }

        readonly List<Screen> m_Screens = new List<Screen>();
        readonly List<object> m_Checks = new List<object>();
        readonly List<object> m_Visibility = new List<object>();
        readonly Material m_Material;
        readonly string m_FontPath, m_FontHash;
        readonly int m_GlyphCount;

        /// <summary>Waits for the card font (a printed sign needs it); call before constructing the probe of a model with card signs.</summary>
        internal static async Task WaitForCardFont()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (CardFonts.Get() == null && !CardFonts.Failed && Time.realtimeSinceStartupAsDouble < deadline)
                await Task.Delay(20);
        }

        internal BusVehicleSignProbe(Transform root, BusInteriorSpec spec)
        {
            m_FontPath = Path.Combine(Mod.ModDirectory, "Fonts", "led-font.bin.gz");
            var font = new LedFont();
            Require(font.Load(m_FontPath), "Production LED font failed to load: " + m_FontPath);
            foreach (char c in "12上海火车站") Require(font.Has(c), "Production LED font lacks glyph: " + c);
            m_GlyphCount = font.Count;
            using (var stream = File.OpenRead(m_FontPath))
            using (var sha = SHA256.Create()) m_FontHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            // Same recipe as BusCabSystem.UpdateOutside; its existing daylight
            // state is observed, not replaced by an artificial emissive material.
            m_Material = CabMaterials.DimmedLamp(AiSignRules.SignAmber, AiSignRules.SignNits, AiSignRules.SignNight);
            Require(m_Material != null, "Production LED material unavailable");
            try
            {
                VehicleProfile authored = VehicleModels.Find(spec.ModelVariant);
                BusInterior.FrontSignFrame(spec, out float3 front, out float2 fs);
                if (authored != null && authored.FrontSign.IsCard)
                    AddCard(root, font, "front", front, fs, quaternion.RotateY(math.PI), authored.FrontSign, "上海火车站");
                else if (authored == null || authored.FrontSign.Matrix.x > 0)
                    Add(root, font, "front", front, fs, BusInterior.FrontSignMatrix(spec), quaternion.RotateY(math.PI), "上海火车站");
                if (BusInterior.SideSignFrame(spec, out float3 side, out float2 ss, out float kerb) &&
                    (authored == null || authored.SideSign.Matrix.x > 0))
                {
                    if (authored != null && authored.SideSign.IsCard)
                        AddCard(root, font, "side", side, ss, BusInterior.SideSignRotation(spec, kerb), authored.SideSign, "上海火车站");
                    else Add(root, font, "side", side, ss, BusInterior.SideSignMatrix(spec), BusInterior.SideSignRotation(spec, kerb), "上海火车站");
                }
                if (authored != null)
                    for (int i = 0; i < authored.ExtraSideSigns.Length; i++)
                    {
                        var sign = authored.ExtraSideSigns[i];
                        var rotation = BusInterior.SideSignRotation(math.sign(sign.Centre.x), sign.Tilt);
                        if (sign.IsCard) AddCard(root, font, "side-extra-" + i, sign.Centre, sign.Size, rotation, sign, "上海火车站");
                        else Add(root, font, "side-extra-" + i, sign.Centre, sign.Size, sign.Matrix, rotation, "上海火车站");
                    }
                BusInterior.RearRouteFrame(spec, out float3 rear, out float2 rs);
                if (authored != null && authored.RearSign.IsCard)
                    AddCard(root, font, "rear", rear, rs, quaternion.identity, authored.RearSign, "");
                else if (authored == null || authored.RearSign.Matrix.x > 0)
                    Add(root, font, "rear", rear, rs, BusInterior.RearRouteMatrix(spec), quaternion.identity, "");
            }
            catch { Dispose(); throw; }
        }

        // A printed board is not a dot matrix: the game draws it with CardSign, so the probe builds that component and renders the
        // same text with its own CardComposer to confirm the picture has ink and matches the board size.
        void AddCard(Transform root, LedFont fallback, string name, float3 centre, float2 size, quaternion rotation, VehicleProfile.Sign model, string message)
        {
            CardFont card = CardFonts.Get();
            Require(card != null, "Production card font is unavailable for the printed " + name + " sign");
            CardRgb board = CardFonts.Rgb(model.Board, CardRgb.DefaultBoard), ink = CardFonts.Rgb(model.Ink, CardRgb.DefaultInk);
            var sign = new CardSign(root, centre, size, rotation, card, fallback, board, ink);
            var screen = new Screen { Name = name, Centre = new Vector3(centre.x, centre.y, centre.z), Size = size,
                Facing = (Quaternion)rotation * Vector3.back, Sign = sign };
            m_Screens.Add(screen);
            sign.Show("", "12", message, 0);
            sign.Visible = true;
            CardText.PictureSize(size.x, size.y, out int width, out int height);
            var composer = new CardComposer(card, fallback, width, height);
            composer.Render("", "12", message, board, ink, ink);
            int inked = 0;
            for (int i = 0; i < composer.Pixels.Length; i += 4)
                if (composer.Pixels[i] != board.R || composer.Pixels[i + 1] != board.G || composer.Pixels[i + 2] != board.B) inked++;
            Require(inked > 0, name + " printed sign rendered no ink for route 12");
            Require(Mathf.Abs(width / (float)height - size.x / size.y) < .02f * size.x / size.y + .01f, name + " picture proportions differ from the board");
            m_Checks.Add(new { screen = name, style = "card", centre_m = new[] { centre.x, centre.y, centre.z }, size_m = new[] { size.x, size.y },
                local_rotation_xyzw = new[] { rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w },
                picture = new[] { width, height }, inked_pixels = inked, route = "12", destination = message,
                board_rgb = new[] { (int)board.R, board.G, board.B }, ink_rgb = new[] { (int)ink.R, ink.G, ink.B },
                scope = "Production CardSign and CardComposer; the LED square-dot rule does not apply to a printed board" });
        }

        void Add(Transform root, LedFont font, string name, float3 centre, float2 size, int2 matrix, quaternion rotation, string message)
        {
            float2 canvas = AiSignRules.SquareCanvas(size, matrix);
            float2 pitch = canvas / new float2(matrix.x, matrix.y);
            Require(math.abs(pitch.x - pitch.y) < .000001f, name + " LED dots are not square");
            var before = new HashSet<MeshFilter>(root.GetComponentsInChildren<MeshFilter>(true));
            var sign = new LedSign(root, centre, size, matrix.x, matrix.y, m_Material, font, rotation, 20f);
            var screen = new Screen { Name = name, Centre = new Vector3(centre.x, centre.y, centre.z), Size = size,
                Facing = (Quaternion)rotation * Vector3.back, Sign = sign };
            m_Screens.Add(screen);
            var composer = new LedComposer(font, matrix.x, matrix.y);
            bool scrolling = composer.Scrolls("", "12", message);
            int areaWidth = matrix.x - composer.LabelWidth("", "12") - LedComposer.BlockGap;
            int offset = scrolling ? areaWidth : 0;
            double elapsed = offset / 20.0;
            // Two real Show calls exercise the production initial/scroll update.
            // Freeze a declared phase for consistent inspection photographs.
            sign.Show("", "12", message, 0);
            sign.Show("", "12", message, elapsed);
            sign.Visible = true;
            composer.RenderSign("", "12", message, offset);
            int wanted = 0, minX = matrix.x, maxX = -1;
            for (int y = 0; y < matrix.y; y++) for (int x = 0; x < matrix.x; x++)
                if (composer.Buffer[x, y]) { wanted++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
            int actual = 0;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true).Where(x => !before.Contains(x)))
            {
                var vertices = filter.sharedMesh.vertices;
                for (int v = 0; v < vertices.Length; v += 4)
                {
                    float dx = Vector3.Distance(vertices[v], vertices[v + 1]);
                    float dy = Vector3.Distance(vertices[v + 1], vertices[v + 2]);
                    if (dx < .0000001f && dy < .0000001f) continue;
                    Require(Mathf.Abs(dx - pitch.x * .78f) < .000001f && Mathf.Abs(dy - pitch.y * .78f) < .000001f,
                        name + " uploaded LED quad differs from production square pitch");
                    actual++;
                }
            }
            Require(actual == wanted && actual > 0, name + " uploaded LED pattern differs from real composer");
            int labelWidth = composer.LabelWidth("", "12");
            int labelStart = (matrix.x - labelWidth) / 2;
            if (name == "rear") Require(maxX - minX + 1 <= labelWidth && minX >= labelStart && maxX < labelStart + labelWidth,
                "Rear short route was stretched instead of centered");
            m_Checks.Add(new { screen = name, centre_m = new[] { centre.x, centre.y, centre.z }, size_m = new[] { size.x, size.y },
                local_rotation_xyzw = new[] { rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w },
                matrix = new[] { matrix.x, matrix.y }, canvas_size_m = new[] { canvas.x, canvas.y }, pitch_m = new[] { pitch.x, pitch.y }, dot_fill = .78f,
                route = "12", destination = message, scrolling, controlled_elapsed_seconds = elapsed, scroll_offset_dots = offset,
                uploaded_lit_quads = actual, expected_lit_quads = wanted, lit_x_bounds = new[] { minX, maxX },
                label_width_dots = labelWidth, centered_label_start = name == "rear" ? (int?)labelStart : null });
        }

        internal object Evidence(string model, int lod)
        {
            Vector4 emission = m_Material.GetVector("_EmissiveColor");
            return new { model, lod, scope = "Production LedSign/LedDots/LedComposer, controlled text and frozen scroll phase; no game route entity or automatic route assignment tested",
                font_path = m_FontPath, font_sha256 = m_FontHash, font_glyph_count = m_GlyphCount,
                material = m_Material.name, shader = m_Material.shader.name, day_nits = AiSignRules.SignNits, night_nits = AiSignRules.SignNight,
                effective_nits = emission.x, emissive_linear_rgb = new[] { emission.x, emission.y, emission.z }, screens = m_Checks,
                visibility = m_Visibility };
        }

        internal void SetVisible(bool visible, string view)
        {
            foreach (var screen in m_Screens) screen.Sign.Visible = visible;
            m_Visibility.Add(new { view, visible });
        }

        internal async Task Capture(Camera camera, RenderTexture target, string directory, string model, int lod, List<object> shots)
        {
            foreach (var screen in m_Screens)
            {
                // Square to each real sign, far enough to include the housing
                // and glazing. Nothing is hidden for these close-up views.
                float distance = Mathf.Max(2f, screen.Size.x * 1.65f);
                await BusVehicleProbe.CaptureReviewed(camera, target, directory, model, lod, "led-" + screen.Name + "-outside",
                    screen.Centre + screen.Facing * distance, screen.Centre, 35f, shots);
            }
        }

        public void Dispose()
        {
            foreach (var screen in m_Screens) { screen.Sign.Visible = false; screen.Sign.Destroy(); }
            m_Screens.Clear();
        }

        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
