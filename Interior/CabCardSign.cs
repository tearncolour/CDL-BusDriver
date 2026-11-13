using System.IO;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

// The printed destination boards (a vehicle profile's signs.*.style "card") on the bus model: the board's picture (CardComposer)
// on a quad in the sign's place, lit by the scene only (CabMaterials.Card). Main thread only; the picture is drawn again
// only when the text changes (no scrolling: a printed board is fixed).
namespace CDLC.BusDriver.Interior
{
    /// <summary>A sign on the bus model outside: an LED matrix (LedSign) or a printed board (CardSign).</summary>
    internal interface ISign
    {
        bool Alive { get; }
        bool Visible { set; }
        /// <summary>The route label (prefix, number) and the text; all empty = dark (an LED sign) or a blank board (a card).</summary>
        void Show(string prefix, string block, string message, double now);
        void Destroy();
    }

    /// <summary>The card font, loaded once on a worker thread for every card sign (the driven bus's and the AI buses').</summary>
    internal static class CardFonts
    {
        static Task<CardFont> s_Load;
        static CardFont s_Font;
        static bool s_Failed;

        /// <summary>The font; null while it loads (asked again later) or when it could not be loaded (Failed).</summary>
        internal static CardFont Get()
        {
            if (s_Font != null || s_Failed)
                return s_Font;
            if (s_Load == null)
            {
                string directory = Mod.ModDirectory;
                if (string.IsNullOrEmpty(directory))
                {
                    s_Failed = true;
                    return null;
                }

                string path = Path.Combine(directory, "Fonts", "card-font.bin.gz");
                s_Load = Task.Run(() =>
                {
                    var font = new CardFont();
                    return font.Load(path) ? font : null;
                });
            }

            if (!s_Load.IsCompleted)
                return null;
            s_Font = s_Load.Status == TaskStatus.RanToCompletion ? s_Load.Result : null;
            if (s_Font == null)
            {
                s_Failed = true;
                Mod.log.Warn("Card signs: Fonts/card-font.bin.gz could not be loaded; the printed boards are drawn as LED signs.");
            }
            else
            {
                Mod.log.Info($"Card signs: card font loaded ({s_Font.Count} glyphs in {s_Font.FaceCount} faces).");
            }

            return s_Font;
        }

        internal static bool Failed => s_Failed;

        /// <summary>A profile's colour (sRGB 0..255; negative: the default).</summary>
        internal static CardRgb Rgb(float3 c, CardRgb fallback) =>
            c.x < 0f ? fallback : new CardRgb((byte)math.clamp(math.round(c.x), 0f, 255f), (byte)math.clamp(math.round(c.y), 0f, 255f), (byte)math.clamp(math.round(c.z), 0f, 255f));

        /// <summary>A prefix colour (sRGB 0..1) as the card's paint.</summary>
        internal static CardRgb Rgb01(float3 c) =>
            new CardRgb((byte)math.round(math.saturate(c.x) * 255f), (byte)math.round(math.saturate(c.y) * 255f), (byte)math.round(math.saturate(c.z) * 255f));

        static Mesh s_Quad;

        /// <summary>
        /// A unit quad facing -Z of its frame like LedDots' dots (scale it to the board's size), the picture's top row (row 0 of the
        /// composer, the texture's bottom row as uploaded) at the top.
        /// </summary>
        internal static Mesh Quad()
        {
            if (s_Quad != null)
                return s_Quad;
            Mesh quad = new Mesh();
            try
            {
                quad.name = "Bus Driver card sign";
                quad.hideFlags = HideFlags.DontSave;
                // a = bottom-left, b = bottom-right, c = top-right, d = top-left; (a, d, c), (a, c, b) face -Z, as AiSignRules.LitQuads
                quad.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) });
                quad.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
                quad.SetUVs(0, new[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) });
                quad.SetTriangles(new[] { 0, 3, 2, 0, 2, 1 }, 0);
                quad.RecalculateTangents();
                quad.bounds = new Bounds(Vector3.zero, new Vector3(1.02f, 1.02f, 0.02f));
                s_Quad = quad;
                return s_Quad;
            }
            catch (System.Exception error) { CabCleanup.FailedAllocation(quad, error); throw; }
        }

        /// <summary>A board picture: sRGB, a mip chain (the AI buses are seen from afar), clamped, filled with the board colour.</summary>
        internal static Texture2D NewPicture(int width, int height, CardRgb board)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            try
            {
                texture.name = "Bus Driver card sign";
                texture.hideFlags = HideFlags.DontSave;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 4;
                var fill = new Color32[width * height];
                var c = new Color32(board.R, board.G, board.B, 255);
                for (int i = 0; i < fill.Length; i++) fill[i] = c;
                texture.SetPixels32(fill);
                texture.Apply(true, false);
                return texture;
            }
            catch (System.Exception error) { CabCleanup.FailedAllocation(texture, error); throw; }
        }

        /// <summary>The composer's picture into the texture (level 0, the mips made again).</summary>
        internal static void Upload(Texture2D texture, CardComposer composer)
        {
            try { texture.SetPixelData(composer.Pixels, 0); }
            catch (System.Exception)
            {
                var px = new Color32[composer.Width * composer.Height];
                for (int i = 0, j = 0; i < px.Length; i++, j += 4)
                    px[i] = new Color32(composer.Pixels[j], composer.Pixels[j + 1], composer.Pixels[j + 2], 255);
                texture.SetPixels32(px);
            }

            texture.Apply(true, false);
        }

        /// <summary>A renderer set up as the signs' (no shadows, no probes, motion vectors from the object).</summary>
        internal static MeshRenderer Renderer(GameObject go)
        {
            MeshRenderer r = go.GetComponent<MeshRenderer>();
            if (r == null) r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            r.renderingLayerMask = uint.MaxValue;
            return r;
        }
    }

    /// <summary>One printed board (the driven bus's): its own picture, material and quad.</summary>
    internal sealed class CardSign : ISign
    {
        readonly GameObject m_Go;
        readonly MeshRenderer m_Renderer;
        readonly Texture2D m_Texture;
        readonly Material m_Material;
        readonly CardComposer m_Composer;
        readonly CardRgb m_Board, m_Ink;
        CardRgb m_PrefixInk;
        string m_Prefix, m_Block, m_Message;
        bool m_Dirty = true;

        /// <summary>A board centred at <paramref name="centre"/> (model space) of <paramref name="size"/>, facing -Z of <paramref name="rotation"/>.</summary>
        internal CardSign(Transform parent, float3 centre, float2 size, quaternion rotation, CardFont font, LedFont fallback, CardRgb board, CardRgb ink)
        {
            CardText.PictureSize(size.x, size.y, out int width, out int height);
            m_Composer = new CardComposer(font, fallback, width, height);
            m_Board = board;
            m_Ink = m_PrefixInk = ink;
            m_Texture = CardFonts.NewPicture(width, height, board);
            m_Material = CabMaterials.Card(m_Texture);
            m_Go = new GameObject("card sign") { hideFlags = HideFlags.DontSave, layer = parent.gameObject.layer };
            m_Go.transform.SetParent(parent, false);
            m_Go.transform.localPosition = new Vector3(centre.x, centre.y, centre.z);
            m_Go.transform.localRotation = new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);
            m_Go.transform.localScale = new Vector3(size.x, size.y, 1f);
            m_Go.AddComponent<MeshFilter>().sharedMesh = CardFonts.Quad();
            m_Renderer = CardFonts.Renderer(m_Go);
            m_Renderer.sharedMaterial = m_Material;
        }

        public bool Alive => m_Go != null && m_Material != null && m_Texture != null;

        public bool Visible
        {
            set
            {
                if (m_Go != null && m_Go.activeSelf != value)
                    m_Go.SetActive(value);
            }
        }

        /// <summary>The prefix's paint (the line prefix's colour).</summary>
        internal CardRgb PrefixInk
        {
            set
            {
                if (value.Equals(m_PrefixInk))
                    return;
                m_PrefixInk = value;
                m_Dirty = true;
            }
        }

        public void Show(string prefix, string block, string message, double now)
        {
            prefix = prefix ?? "";
            block = block ?? "";
            message = message ?? "";
            if (!m_Dirty && prefix == m_Prefix && block == m_Block && message == m_Message)
                return;
            m_Prefix = prefix;
            m_Block = block;
            m_Message = message;
            m_Dirty = false;
            m_Composer.Render(prefix, block, message, m_Board, m_Ink, m_PrefixInk);
            CardFonts.Upload(m_Texture, m_Composer);
        }

        public void Destroy()
        {
            if (m_Go != null) Object.Destroy(m_Go);
            CabMaterials.ReleaseCard(m_Material);
            if (m_Texture != null) Object.Destroy(m_Texture);
        }
    }
}
