using Unity.Mathematics;
using CDLC.BusDriver.Kit;

// The driver's lights, as pure logic (the cab system draws what it says): the head lamp switch, the indicator and hazard flashing, and where
// the head lamps are on a model.
namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>The head lamp switch. L steps Auto -> Low -> High -> Off -> Auto.</summary>
    public enum LightMode { Auto = 0, Low = 1, High = 2, Off = 3 }

    /// <summary>What the head lamps give: only the daytime running strip (Off), the dipped beam or the main beam.</summary>
    public enum Beam { Off = 0, Low = 1, High = 2 }

    /// <summary>
    /// The switch and the daylight sensor. Auto turns the dipped beam on when it gets dark, with the driving mod's hysteresis (on below
    /// 0.47, off above 0.53 of the game's daylight, so a cloud or a tunnel mouth does not make the lamps flicker). The flash key puts the
    /// main beam on for as long as it is held, whatever the switch says, and the switch comes back as it was. The tail lamps follow the
    /// switch, not the flash: lit with the dipped or main beam, dark with the switch off.
    /// </summary>
    public sealed class BusLightState
    {
        public const float OnBelow = 0.47f, OffAbove = 0.53f;

        public LightMode Mode { get; private set; }
        public bool Flashing { get; private set; }
        private bool m_AutoOn;
        private bool m_Sampled;
        private Beam m_Base;

        public BusLightState(LightMode mode = LightMode.Auto) { Mode = mode; }

        /// <summary>The beam the switch gives now (what Update last worked out), without the flash.</summary>
        public Beam Base => m_Base;

        /// <summary>The beam the lamps give now: the main beam while flashing, the switch's otherwise.</summary>
        public Beam Beam => Flashing ? Beam.High : m_Base;

        /// <summary>The tail (and side) lamps: with the dipped or main beam the switch gives; the flash does not light them.</summary>
        public bool Tail => m_Base != Beam.Off;

        /// <summary>The next switch position (L), returned.</summary>
        public LightMode Cycle()
        {
            Mode = (LightMode)(((int)Mode + 1) % 4);
            m_Base = BaseFor(Mode, m_AutoOn);
            return Mode;
        }

        public void SetMode(LightMode mode)
        {
            Mode = mode;
            m_Base = BaseFor(Mode, m_AutoOn);
        }

        /// <summary>The flash key is held (true) or let go (false).</summary>
        public void Flash(bool held) => Flashing = held;

        /// <summary>A new drive: the switch at the option's position, no flash, the daylight sensor not yet read.</summary>
        public void Reset(LightMode mode)
        {
            Mode = mode;
            Flashing = false;
            m_Sampled = false;
            m_AutoOn = false;
            m_Base = Beam.Off;
        }

        /// <summary>Reads the daylight (0 night .. 1 sun) and returns the beam now.</summary>
        public Beam Update(float daylight)
        {
            if (!m_Sampled)
            {
                m_Sampled = true;
                m_AutoOn = daylight < (OnBelow + OffAbove) * 0.5f;
            }
            else if (m_AutoOn && daylight > OffAbove)
                m_AutoOn = false;
            else if (!m_AutoOn && daylight < OnBelow)
                m_AutoOn = true;

            m_Base = BaseFor(Mode, m_AutoOn);
            return Beam;
        }

        private static Beam BaseFor(LightMode mode, bool autoOn)
        {
            switch (mode)
            {
                case LightMode.Low: return Beam.Low;
                case LightMode.High: return Beam.High;
                case LightMode.Off: return Beam.Off;
                default: return autoOn ? Beam.Low : Beam.Off;
            }
        }
    }

    /// <summary>The indicator stalk under the two indicator keys (or buttons).</summary>
    public static class Stalk
    {
        /// <summary>
        /// The stalk after this frame's presses (-1 left, 0 off, +1 right): a key switches its side on, the same key again off, the
        /// other key over. Both pressed in the same frame is the hazard chord: the stalk stays as it was and hazardToggle says the
        /// warning lamps are to be switched (the pad has no spare button for them: LB and RB together).
        /// </summary>
        public static int Press(int stalk, bool left, bool right, out bool hazardToggle)
        {
            hazardToggle = left && right;
            if (hazardToggle)
                return stalk;
            if (left)
                return stalk == -1 ? 0 : -1;
            if (right)
                return stalk == 1 ? 0 : 1;
            return stalk;
        }
    }

    /// <summary>The bits of the driving mod's SetManualSignals (DrivingApi 7): the left indicator, the right one, both together.</summary>
    public static class DrivingSignals
    {
        public const int Left = 1, Right = 2, Hazard = Left | Right;
    }

    /// <summary>The indicator and hazard flashing.</summary>
    public static class Flasher
    {
        /// <summary>90 flashes a minute (UN R48 allows 60 to 120).</summary>
        public const float Hz = 1.5f;

        /// <summary>Is the flash lit this far into it (on first)?</summary>
        public static bool On(double seconds) => Hz > 0f && (seconds * Hz) - System.Math.Floor(seconds * Hz) < 0.5;

        /// <summary>Do the indicators flash at all: the hazard warning lamps, or the stalk (-1 left, +1 right) moved.</summary>
        public static bool Flashing(int stalk, bool hazard) => hazard || stalk != 0;

        /// <summary>
        /// Which sides are lit this instant. The hazard warning lamps light both sides together and win over the stalk; the stalk lights
        /// its own side only.
        /// </summary>
        public static void Sides(int stalk, bool hazard, bool flashOn, out bool left, out bool right)
        {
            left = flashOn && (hazard || stalk < 0);
            right = flashOn && (hazard || stalk > 0);
        }
    }

    /// <summary>One beam's light: how wide, how far, how bright and how far down it points.</summary>
    public readonly struct BeamSpec
    {
        public readonly float OuterAngle, InnerPercent, Range, PitchDown, Candela;

        public BeamSpec(float outerAngle, float innerPercent, float range, float pitchDown, float candela)
        {
            OuterAngle = outerAngle; InnerPercent = innerPercent; Range = range; PitchDown = pitchDown; Candela = candela;
        }

        // Starting values, to be tuned on a night road in the game (the exposure there decides how bright is bright): the dipped beam wide and
        // short and lowered, the main beam narrow, long and about three times as bright (real lamps: 10 000 to 25 000 cd dipped and up to
        // 50 000 cd main, per lamp, against the game's night exposure these give a road that is lit, not a bleached one).
        public static readonly BeamSpec Low = new BeamSpec(62f, 45f, 45f, 2.0f, 3500f);
        public static readonly BeamSpec High = new BeamSpec(34f, 55f, 120f, 0.5f, 11000f);

        public static BeamSpec For(Beam beam) => beam == Beam.High ? High : Low;
    }

    /// <summary>Where the head lamps are on a built model (their part's vertices), for the real lights.</summary>
    public static class HeadlampFrames
    {
        /// <summary>
        /// The two head lamps in model space (+Z forward): in the middle of each side's lamp vertices, a little in front of the
        /// front-most of them. False when the model has no head lamp part (the rear section of an articulated bus, a stub).
        /// A model with lamp vertices on one side only gets its mirror image.
        /// </summary>
        public static bool Find(KitModelData data, int lampPart, out float3 first, out float3 second)
        {
            first = second = default;
            if (data?.Parts == null || (uint)lampPart >= (uint)data.Parts.Length)
                return false;

            KitPartDef part = data.Parts[lampPart];
            KitPartDef source = part.MeshOf >= 0 ? data.Parts[part.MeshOf] : part;
            KitMeshData mesh = null;
            for (int lod = 0; lod < source.Data.Length && mesh == null; lod++)
                mesh = source.Data[lod];
            if (mesh == null || mesh.P == null || mesh.P.Length == 0)
                return false;

            var toModel = new float4x4(part.Rot, part.Pivot);
            KitBounds negative = KitBounds.Empty, positive = KitBounds.Empty;
            foreach (float3 p in mesh.P)
            {
                float3 m = math.transform(toModel, p);
                if (m.x < 0f) negative.Add(m);
                else positive.Add(m);
            }

            if (negative.IsEmpty && positive.IsEmpty)
                return false;
            if (negative.IsEmpty) negative = Mirror(positive);
            if (positive.IsEmpty) positive = Mirror(negative);
            first = Lamp(negative);
            second = Lamp(positive);
            return true;
        }

        private static float3 Lamp(in KitBounds side) => new float3(side.Center.x, side.Center.y, side.Max.z + 0.02f);

        private static KitBounds Mirror(in KitBounds b)
        {
            var m = KitBounds.Empty;
            m.Add(new float3(-b.Min.x, b.Min.y, b.Min.z));
            m.Add(new float3(-b.Max.x, b.Max.y, b.Max.z));
            return m;
        }
    }
}
