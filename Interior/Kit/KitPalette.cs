using Unity.Mathematics;

// MeshKit palette (pure). Every palette finish (Satin, Matte, Metal, Concrete, Glass) gets a small runtime texture (KitMaterials):
// one 4-pixel band per swatch row (UV.y), and along UV.x a ramp: 0 = deepest AO / dirt, 0.5 = albedo, 1 = full edge wear (glass:
// flat rows of linear ratios to its _BaseColor, see the glass notes).
// Primitives write the ramp coordinate per vertex (fake AO without vertex colours, which HDRP/Lit ignores).
namespace CDLC.BusDriver.Kit
{
    public static class KitPalette
    {
        public const int Width = 64;             // ramp texels
        public const int RowHeight = 4;          // texels per swatch row (UV at the band centre: no bleeding between rows)
        public static int Rows => (int)Sw.Count;
        public static int Height => Rows * RowHeight;
        public const float HazardRepeat = 0.4f;  // metres per hazard texture repeat (two 0.1 m black / yellow stripe pairs)

        // Swatch colours (sRGB, starting values from meshkit_design.md 4.7; liveries may swap rows later).
        static readonly float3[] s_Srgb =
        {
            new float3(0.97f, 0.68f, 0.02f),   // Yellow (XCMG / Liebherr style)
            new float3(0.95f, 0.42f, 0.05f),   // Orange
            new float3(0.80f, 0.04f, 0.06f),   // Red (SANY style)
            new float3(0.10f, 0.28f, 0.62f),   // Blue
            new float3(0.86f, 0.87f, 0.85f),   // White
            new float3(0.18f, 0.46f, 0.20f),   // Green
            new float3(0.55f, 0.56f, 0.56f),   // Grey (Zoomlion star grey)
            new float3(0.05f, 0.05f, 0.05f),   // Black (chassis)
            new float3(0.16f, 0.16f, 0.16f),   // DarkGrey (dark plastics, grilles)
            new float3(0.045f, 0.045f, 0.045f),// Rubber
            new float3(0.58f, 0.58f, 0.58f),   // Galvanised
            new float3(0.42f, 0.42f, 0.42f),   // Steel
            new float3(0.20f, 0.20f, 0.20f),   // Rope
            new float3(0.34f, 0.32f, 0.30f),   // Rail
            new float3(0.38f, 0.20f, 0.11f),   // Rust (rebar, old steel forms)
            new float3(0.66f, 0.65f, 0.62f),   // Precast
            new float3(0.50f, 0.50f, 0.48f),   // FreshConcrete
            new float3(0.42f, 0.24f, 0.12f),   // Plywood (phenolic form face)
            new float3(0.70f, 0.52f, 0.31f),   // Timber
            new float3(0.14f, 0.38f, 0.18f),   // Net (safety netting)
            new float3(0.75f, 0.76f, 0.78f),   // Chrome
            new float3(0.09f, 0.11f, 0.105f),  // Glass (clear automotive glass over a dark cab: see the glass notes below)
            new float3(1.00f, 1.00f, 1.00f),   // Hazard (the stripe texture carries the colour)
            new float3(0.95f, 0.95f, 0.92f),   // LampWhite
            new float3(0.70f, 0.05f, 0.03f),   // LampRed
            new float3(1.00f, 0.60f, 0.15f),   // Amber
            new float3(0.85f, 0.05f, 0.04f),   // AviationRed
            // ---- appended for bridge works v3 wave 2 (rows after the emissive ones: existing rows keep their UVs)
            new float3(0.15f, 0.15f, 0.15f),   // Asphalt (cured, grey-black)
            new float3(0.07f, 0.065f, 0.06f),  // FreshAsphalt (hot mat, darker)
            new float3(0.035f, 0.035f, 0.04f), // Membrane (waterproofing / bitumen sheet)
            new float3(0.36f, 0.27f, 0.18f),   // Soil (spoil, excavated earth)
            new float3(0.48f, 0.45f, 0.40f),   // Gravel
            new float3(0.76f, 0.66f, 0.48f),   // Sand
            new float3(0.30f, 0.27f, 0.23f),   // Mud (bentonite slurry, wet ground)
            new float3(0.45f, 0.14f, 0.10f),   // SteelPrimer (red-brown primer: steel segments, Bailey beams)
            new float3(0.52f, 0.55f, 0.57f),   // SteelGrey (painted bridge steel)
            new float3(0.93f, 0.93f, 0.90f),   // SafetyWhite (hoarding panels, signs)
            new float3(0.06f, 0.075f, 0.072f), // GlassTinted (crane / machine operator cabs, site offices: grey-green solar glass)
            new float3(0.04f, 0.62f, 0.25f),   // LampGreen (starboard navigation sidelight lens, unlit)
            // ---- Bus Driver interior
            new float3(0.13f, 0.20f, 0.40f),   // Fabric (seat moquette, deep blue)
            new float3(0.62f, 0.12f, 0.10f),   // FabricAccent (priority seats, red)
            new float3(0.30f, 0.31f, 0.32f),   // FloorVinyl (grey anti-slip floor)
            new float3(0.80f, 0.79f, 0.75f),   // Panel (light warm grey wall and ceiling panels)
            new float3(0.07f, 0.07f, 0.075f),  // DashBlack (dashboard, trim)
            new float3(0.03f, 0.035f, 0.04f),  // Screen (switched-off screens, mirror faces)
            new float3(1.00f, 0.58f, 0.10f),   // LedAmber (LED display)
            // ---- Bus Driver passengers
            new float3(0.89f, 0.72f, 0.60f),   // SkinLight
            new float3(0.74f, 0.54f, 0.39f),   // SkinMid
            new float3(0.43f, 0.29f, 0.19f),   // SkinDark
            new float3(0.17f, 0.25f, 0.40f),   // Denim
            new float3(0.62f, 0.56f, 0.41f),   // Khaki
            new float3(0.21f, 0.13f, 0.08f),   // HairBrown
            new float3(0.045f, 0.04f, 0.04f),  // HairBlack (matte: the paint Black shines)
            new float3(0.60f, 0.46f, 0.27f),   // HairBlonde
            new float3(0.09f, 0.12f, 0.22f),   // Navy
            new float3(0.86f, 0.55f, 0.62f),   // Pink
            new float3(0.40f, 0.09f, 0.13f),   // Burgundy
            new float3(0.77f, 0.69f, 0.55f),   // Beige
            new float3(0.58f, 0.71f, 0.86f),   // ShirtBlue (the driver's uniform shirt)
            new float3(0.66f, 0.36f, 0.33f),   // Lip
            // Authored 6109 colours were measured/estimated in Blender's linear space.
            // Store their sRGB equivalents, as every other palette row does.
            ToSrgb(new float3(.56f, .58f, .59f)), // BrushedStainless
            ToSrgb(new float3(.032f, .038f, .041f)), // SeatFabricGrey
            ToSrgb(new float3(.55f, .72f, .008f)), // SeatPrintYellow
            ToSrgb(new float3(.32f, .37f, .36f)), // SeatPrintGrey
            ToSrgb(new float3(.035f, .042f, .045f)), // SeatShellGrey
            ToSrgb(new float3(.22f, .24f, .25f)), // HandGripGrey
            ToSrgb(new float3(.37f, .52f, .58f)), // CurtainBlueGrey
            ToSrgb(new float3(.46f, .49f, .50f)), // CabinSilver
            ToSrgb(new float3(.61f, .63f, .63f)), // CabinPanelGrey
            ToSrgb(new float3(.64f, .67f, .66f)), // CurtainClipGrey
            ToSrgb(new float3(.035f, .043f, .046f)), // CabinRecessGrey
            ToSrgb(new float3(.022f, .025f, .026f)), // DoorFeetGrey
            ToSrgb(new float3(.67f, .28f, .025f)), // RequestAmber
            ToSrgb(new float3(.045f, .035f, .025f)), // RequestDark
            ToSrgb(new float3(.63f, .025f, .018f)), // RequestRed
            ToSrgb(new float3(.92f, .59f, .018f)), // RequestYellow
            ToSrgb(new float3(.025f, .24f, .13f)), // AccessibilityGreen
            ToSrgb(new float3(.87f, .89f, .86f)), // AccessibilityWhite
            ToSrgb(new float3(.012f, .015f, .015f)), // GasketGrey
            ToSrgb(new float3(.55f, .72f, .008f)), // RearPrintYellow
            ToSrgb(new float3(.32f, .37f, .36f)), // RearPrintGrey
            ToSrgb(new float3(.038f, .05f, .054f)), // CabinGraphite: cool dark plastic, not brown button material
            ToSrgb(new float3(.12f, .15f, .16f)), // CharcoalVinyl: cool grey floor, not gravel
            ToSrgb(new float3(.12f, .47f, .51f)), // ShanghaiAqua: exact authored stripe colour; explicit route only
            ToSrgb(new float3(.040f, .072f, .118f)), // FloorDarkBlue: anti-slip blue vinyl
            ToSrgb(new float3(.86f, .58f, .020f)), // SeatMouldedYellow: yellow injection-moulded seat shell
            new float3(0.753f, 0.187f, 0.155f), // RedGloss: Red at 85 % chroma, the same lightness and hue (K18 demonstration paint)
            new float3(0.938f, 0.693f, 0.268f), // YellowGloss: Yellow at 85 % chroma
            new float3(0.139f, 0.290f, 0.574f), // BlueGloss: Blue at 85 % chroma
            new float3(0.86f, 0.87f, 0.85f),   // WhiteGloss (= White)
        };

        static readonly Fin[] s_Fin =
        {
            Fin.Satin, Fin.Satin, Fin.Satin, Fin.Satin, Fin.Satin, Fin.Satin, Fin.Satin, Fin.Satin, Fin.Matte,
            Fin.Matte, Fin.Metal, Fin.Metal, Fin.Metal, Fin.Metal, Fin.Metal,
            Fin.Concrete, Fin.Concrete,
            Fin.Matte, Fin.Matte, Fin.Matte,
            Fin.Chrome, Fin.Glass, Fin.Hazard,
            Fin.Lamp, Fin.Tail, Fin.Beacon, Fin.Aviation,
            Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte,   // Asphalt .. Mud
            Fin.Satin, Fin.Satin, Fin.Satin,                                                // SteelPrimer, SteelGrey, SafetyWhite
            Fin.Glass,                                                                      // GlassTinted
            Fin.NavGreen,                                                                   // LampGreen
            Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte,               // Fabric .. Screen
            Fin.Glow,                                                                       // LedAmber
            Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte,               // SkinLight .. HairBrown
            Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte, Fin.Matte,   // HairBlack .. Lip
            Fin.BrushedMetal, Fin.Textile, Fin.Textile, Fin.Textile, Fin.MouldedPlastic,
            Fin.Satin, Fin.Matte, Fin.BrushedMetal, Fin.Satin, Fin.Textile,
            Fin.Textile, Fin.Satin, Fin.Satin, Fin.Satin, Fin.MouldedPlastic, Fin.MouldedPlastic,
            Fin.Satin, Fin.Satin, Fin.Matte,
            Fin.MouldedPlastic, Fin.MouldedPlastic,
            Fin.Satin, Fin.Matte,
            Fin.MouldedPlastic, // ShanghaiAqua uses the existing 0/.72 glossy dielectric recipe; native metallic remains .15
            Fin.Matte, Fin.MouldedPlastic, // FloorDarkBlue / SeatMouldedYellow
            Fin.MouldedPlastic, Fin.MouldedPlastic, Fin.MouldedPlastic, Fin.MouldedPlastic, // RedGloss .. WhiteGloss: the same 0 / .72 glossy dielectric as the 6109's stripes
        };

        public static float3 Srgb(Sw sw) => s_Srgb[(int)sw];
        public static Fin FinOf(Sw sw) => s_Fin[(int)sw];

        // Planar metric UVs instead of palette UVs (stripe textures).
        public static bool Metric(Sw sw) => sw == Sw.Hazard;

        // Finishes drawn with the palette texture (_BaseColorMap, times _BaseColor = FlatSrgb); the others use a flat _BaseColor.
        // Glass uses the palette only to tell its rows apart (Glass / GlassTinted share one material = one draw): its _BaseColor is
        // the Glass colour (the roller-verified flat path) and its rows are flat linear ratios to it (Glass 1, GlassTinted ~0.56),
        // so a failed map still leaves dark glass, and no AO, dirt or wear ever reaches it.
        public static bool UsesPalette(Fin f) => f == Fin.Satin || f == Fin.Matte || f == Fin.Metal || f == Fin.Concrete || f == Fin.Glass ||
            f == Fin.BrushedMetal || f == Fin.Textile || f == Fin.MouldedPlastic;
        public static bool IsLight(Fin f) => f >= Fin.Lamp && f <= Fin.Glow;

        // Glass notes. Glass is OPAQUE (no transparency / alpha-clip / normal-map keyword: their variants may be stripped from the
        // game build), so it is drawn as automotive glass looks from outside: a dark body (the unlit cab interior seen through
        // ~75 % transmitting glass returns about 1-2 % of the light) under a mirror-like dielectric reflection.
        //  * Metallic 0: HDRP's dielectric F0 = 0.04 (soda-lime glass, n = 1.52: F0 = 0.043). Fresnel turns it into a full mirror
        //    at grazing angles: that, not a bright albedo, is what makes glass read as glass instead of black plastic.
        //  * Smoothness 0.975: the sky and SSR reflect crisply (perceptual roughness 0.025; CS2 has no reflection probes: the
        //    physically based sky, dimmed at night, plus SSR, which HDRP applies above smoothness 0.9); not 1.0, which aliases on
        //    curved panes and shrinks the sun's highlight to a sub-pixel firefly.
        //  * Glass sRGB (0.09, 0.11, 0.105) = linear ~(0.009, 0.012, 0.011): slightly green-grey (iron in float glass), dark enough
        //    that the reflection dominates and the "interior" reads dark behind it, not pure black. GlassTinted sRGB (0.06, 0.075,
        //    0.072) = linear ~(0.005, 0.0065, 0.006): solar-tinted operator cabs and site-office windows.
        //  * _BaseColor = Glass, palette rows = flat linear ratios (RampSrgb): no AO, dirt or edge wear on glass, whatever the
        //    scope's occlusion / shade / wear says. SurfaceSrgb is the product the shader shows.
        //  * Decals off: _SupportDecals 0 on every kit material (KitMaterials).
        // Geometry matters as much (MeshKitGlass.cs): a flat pane mirrors one uniform colour; a slight bow with smooth normals bends
        // the reflection across the pane, and a black gasket frames it.
        public const float GlassSmoothness = 0.975f;

        // HDRP/Lit settings per finish (meshkit_design.md 4.7).
        public static float Metallic(Fin f) => f == Fin.Metal ? 0.8f : f == Fin.Chrome || f == Fin.BrushedMetal ? 1f : 0f;

        public static float Smoothness(Fin f)
        {
            switch (f)
            {
                case Fin.Satin: return 0.5f;
                case Fin.Matte: return 0.2f;
                case Fin.Metal: return 0.4f;
                case Fin.Chrome: return 0.85f;
                case Fin.Glass: return GlassSmoothness;
                case Fin.BrushedMetal: return 0.75f;
                case Fin.Textile: return 0.32f;
                case Fin.MouldedPlastic: return 0.72f;
                case Fin.Concrete: return 0.18f;
                case Fin.Hazard: return 0.45f;
                default: return 0.8f;      // lamp lenses
            }
        }

        // Flat base colour (sRGB) of the non-palette finishes.
        public static float3 FlatSrgb(Fin f)
        {
            switch (f)
            {
                case Fin.Chrome: return Srgb(Sw.Chrome);
                case Fin.Glass: return Srgb(Sw.Glass);
                case Fin.Lamp: return Srgb(Sw.LampWhite);
                case Fin.Tail: return Srgb(Sw.LampRed);
                case Fin.Beacon: return Srgb(Sw.Amber);
                case Fin.Aviation: return Srgb(Sw.AviationRed);
                case Fin.NavGreen: return Srgb(Sw.LampGreen);
                case Fin.Glow: return Srgb(Sw.LedAmber);
                default: return new float3(1f);
            }
        }

        // Emission of a lit light finish: colour (sRGB) and level in nits (roller levels, verified in game for lamp / tail /
        // beacon; the aviation level is unverified in game).
        public static float3 EmissiveSrgb(Fin f)
        {
            switch (f)
            {
                case Fin.Lamp: return new float3(1f, 0.96f, 0.88f);
                case Fin.Tail: return new float3(1f, 0.05f, 0.03f);
                case Fin.Beacon: return new float3(1f, 0.55f, 0.05f);
                case Fin.NavGreen: return new float3(0.05f, 1f, 0.35f);
                case Fin.Glow: return new float3(1f, 0.55f, 0.08f);
                default: return new float3(1f, 0.04f, 0.03f);
            }
        }

        public static float EmissiveNits(Fin f)
        {
            switch (f)
            {
                case Fin.Lamp: return 300f;
                case Fin.Tail: return 150f;
                case Fin.Beacon: return 3000f;
                case Fin.NavGreen: return 150f;
                case Fin.Glow: return 60f;
                default: return 2000f;
            }
        }

        // ---- palette UVs (row centre, ramp texel centres)

        public static float RampU(float ramp) => (0.5f + math.saturate(ramp) * (Width - 1)) / Width;
        public static float RowV(Sw sw) => ((int)sw * RowHeight + 0.5f * RowHeight) / Height;
        public static float2 UV(Sw sw, float ramp) => new float2(RampU(ramp), RowV(sw));

        // Ramp coordinate from occlusion (1 = open, 0 = fully occluded) and wear (0..1): wear wins when > 0.
        public static float Ramp(float occ, float wear) => wear > 0f ? 0.5f + 0.5f * math.saturate(wear) : 0.5f * math.saturate(occ);

        // Planar UV by the dominant normal axis, in hazard texture repeats.
        public static float2 MetricUV(float3 p, float3 n)
        {
            float3 a = math.abs(n);
            float2 uv = a.x >= a.y && a.x >= a.z ? p.zy : a.y >= a.z ? p.xz : p.xy;
            return uv / HazardRepeat;
        }

        // ---- ramp colours (linear maths, sRGB output for an sRGB texture)

        // Per palette finish: deepest AO multiplier, dirt tint strength, wear lift, wear desaturation.
        static void RampParams(Fin f, out float aoMin, out float dirtK, out float lift, out float desat)
        {
            switch (f)
            {
                case Fin.Matte: aoMin = 0.40f; dirtK = 0.20f; lift = 0.10f; desat = 0.5f; return;
                case Fin.Metal: aoMin = 0.30f; dirtK = 0.30f; lift = 0.25f; desat = 0.8f; return;
                case Fin.Concrete: aoMin = 0.45f; dirtK = 0.35f; lift = 0.12f; desat = 0.6f; return;
                default: aoMin = 0.35f; dirtK = 0.25f; lift = 0.22f; desat = 0.7f; return;   // Satin
            }
        }

        static readonly float3 s_DirtLinear = new float3(0.040f, 0.030f, 0.020f);   // dark brown grime

        // Colour (sRGB 0..1) of swatch sw at ramp position x in the palette of finish f.
        public static float3 RampSrgb(Fin f, Sw sw, float x)
        {
            if (f == Fin.Glass)                             // flat linear ratio to the Glass colour (_BaseColor): no AO, dirt, wear
                return ToSrgb(math.saturate(ToLinear(Srgb(sw)) / math.max(ToLinear(Srgb(Sw.Glass)), new float3(1e-6f))));
            RampParams(f, out float aoMin, out float dirtK, out float lift, out float desat);
            float3 alb = ToLinear(Srgb(sw));
            x = math.saturate(x);
            float3 c;
            if (x < 0.5f)
            {
                float t = x / 0.5f;
                c = math.lerp(alb, s_DirtLinear, dirtK * (1f - t)) * math.lerp(aoMin, 1f, t);
            }
            else
            {
                float t = (x - 0.5f) / 0.5f;
                float lum = math.dot(alb, new float3(0.2126f, 0.7152f, 0.0722f));
                float3 wear = math.saturate(math.lerp(alb, new float3(lum), desat) + lift);
                c = math.lerp(alb, wear, t);
            }
            return ToSrgb(c);
        }

        // Colour (sRGB) the shader shows for swatch sw at ramp x: _BaseColor (FlatSrgb: white for paints, the Glass colour for glass)
        // times the palette texel, in linear light. The offline preview uses it.
        public static float3 SurfaceSrgb(Fin f, Sw sw, float x) => ToSrgb(ToLinear(FlatSrgb(f)) * ToLinear(RampSrgb(f, sw, x)));

        public static float ToLinear(float s) => s <= 0.04045f ? s / 12.92f : math.pow((s + 0.055f) / 1.055f, 2.4f);
        public static float ToSrgb(float l) => l <= 0.0031308f ? l * 12.92f : 1.055f * math.pow(math.max(l, 0f), 1f / 2.4f) - 0.055f;
        public static float3 ToLinear(float3 s) => new float3(ToLinear(s.x), ToLinear(s.y), ToLinear(s.z));
        public static float3 ToSrgb(float3 l) => new float3(ToSrgb(l.x), ToSrgb(l.y), ToSrgb(l.z));

        // RGBA32 texels of one palette (Width x Height, row-major from the bottom row, as Texture2D.SetPixels32 expects).
        public static byte[] PaletteBytes(Fin f)
        {
            int w = Width, h = Height;
            var px = new byte[w * h * 4];
            for (int r = 0; r < Rows; r++)
                for (int x = 0; x < w; x++)
                {
                    float3 c = math.saturate(RampSrgb(f, (Sw)r, (x + 0.5f) / w));
                    byte cr = (byte)math.round(c.x * 255f), cg = (byte)math.round(c.y * 255f), cb = (byte)math.round(c.z * 255f);
                    for (int y = 0; y < RowHeight; y++)
                    {
                        int i = ((r * RowHeight + y) * w + x) * 4;
                        px[i] = cr; px[i + 1] = cg; px[i + 2] = cb; px[i + 3] = 255;
                    }
                }
            return px;
        }

        /// <summary>
        /// Whether a swatch is a body paint that takes a clear coat on the game's shader: the Satin paints (Yellow to Black) and the glossy paints (RedGloss to WhiteGloss). The game's own vehicle
        /// paint is a low base smoothness under a clear coat (the mask map's G channel: the packs' native masks have 255 there on the paint, K18 on its paint swatches only, SWB6710 everywhere;
        /// the built-in 12 m's own mask has none); a plain smoothness of 0.5 without it reads flat. The interior plastics, the rubber, the metals and the glass are not coated.
        /// </summary>
        public static bool ClearCoat(Sw sw) => sw <= Sw.Black || sw >= Sw.RedGloss && sw <= Sw.WhiteGloss;

        /// <summary>Whether a finish has any coated row (the others get a flat mask map).</summary>
        public static bool HasClearCoat(Fin f) => f == Fin.Satin || f == Fin.MouldedPlastic;

        /// <summary>
        /// The mask map of a palette finish on the game's shader, laid out as its palette (Width x Height, row-major from the bottom row): metallic in R, the clear coat in G (255 on a paint row, else 0),
        /// 0 in B, smoothness in A, the finish's own numbers, so that the paint rows are the only change against a flat map.
        /// </summary>
        public static byte[] MaskBytes(Fin f)
        {
            byte metal = (byte)math.round(math.saturate(Metallic(f)) * 255f), smooth = (byte)math.round(math.saturate(Smoothness(f)) * 255f);
            var px = new byte[Width * Height * 4];
            for (int r = 0; r < Rows; r++)
            {
                byte coat = (byte)(ClearCoat((Sw)r) ? 255 : 0);
                for (int y = 0; y < RowHeight; y++)
                    for (int x = 0; x < Width; x++)
                    {
                        int i = ((r * RowHeight + y) * Width + x) * 4;
                        px[i] = metal; px[i + 1] = coat; px[i + 2] = 0; px[i + 3] = smooth;
                    }
            }

            return px;
        }

        // 64 x 64 black / yellow 45 degree stripes (two stripe pairs per repeat), RGBA32 sRGB.
        public static byte[] HazardBytes(int size = 64)
        {
            var px = new byte[size * size * 4];
            float3 yellow = Srgb(Sw.Yellow), black = new float3(0.04f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int band = ((x + y) * 4 / size) & 1;          // 4 bands along the diagonal: yellow, black, yellow, black
                    float3 c = band == 0 ? yellow : black;
                    int i = (y * size + x) * 4;
                    px[i] = (byte)math.round(c.x * 255f); px[i + 1] = (byte)math.round(c.y * 255f); px[i + 2] = (byte)math.round(c.z * 255f); px[i + 3] = 255;
                }
            return px;
        }
    }
}
