using System;
using System.Collections.Generic;

namespace CDLC.BusDriver
{
    /// <summary>The relation of a sound condition, numbered as OMSI's sound.cfg writes it.</summary>
    internal enum SoundRelation
    {
        NotEqual = 0,
        Equal = 1,
        Less = 2,
        Greater = 3,
        LessOrEqual = 4,
        GreaterOrEqual = 5,
    }

    /// <summary>The sound is heard only while <c>variable relation value</c> holds ("engine_rpm &gt; 200": the engine runs).</summary>
    internal readonly struct SoundCondition
    {
        internal readonly string Variable;
        internal readonly float Value;
        internal readonly SoundRelation Relation;

        internal SoundCondition(string variable, float value, SoundRelation relation)
        {
            Variable = variable;
            Value = value;
            Relation = relation;
        }

        internal bool Holds(float v)
        {
            switch (Relation)
            {
                case SoundRelation.NotEqual: return v != Value;
                case SoundRelation.Equal: return v == Value;
                case SoundRelation.Less: return v < Value;
                case SoundRelation.Greater: return v > Value;
                case SoundRelation.LessOrEqual: return v <= Value;
                case SoundRelation.GreaterOrEqual: return v >= Value;
                default: return true;
            }
        }
    }

    /// <summary>
    /// A volume that follows a variable: straight lines between the points, the first point's value to the left of it and the
    /// last one's from the last point on. Two points at the same place give their mean there.
    /// </summary>
    internal sealed class SoundCurve
    {
        internal readonly string Variable;
        private readonly float[] m_X, m_Y;

        internal SoundCurve(string variable, params float[] xy)
        {
            if (xy == null || xy.Length == 0 || (xy.Length & 1) != 0)
                throw new ArgumentException("a sound curve is made of (x, y) pairs", nameof(xy));
            Variable = variable;
            int n = xy.Length / 2;
            m_X = new float[n];
            m_Y = new float[n];
            for (int i = 0; i < n; i++)
            {
                m_X[i] = xy[2 * i];
                m_Y[i] = xy[2 * i + 1];
            }
        }

        internal float Evaluate(float x)
        {
            int n = m_X.Length;
            if (x < m_X[0])
                return m_Y[0];
            if (x >= m_X[n - 1])
                return m_Y[n - 1];
            int i = 1;
            while (i < n - 1 && x >= m_X[i])
                i++;
            float x0 = m_X[i - 1], x1 = m_X[i];
            if (x1 == x0)
                return (m_Y[i - 1] + m_Y[i]) * 0.5f;
            return m_Y[i - 1] + (x - x0) * ((m_Y[i] - m_Y[i - 1]) / (x1 - x0));
        }
    }

    /// <summary>
    /// How one sound is played from the bus's variables, after OMSI's sound.cfg as openOMSI documents it (openOMSI, AGPL-3.0,
    /// docs/FORMATS.md and crates/omsi-audio/src/soundset.rs):
    /// volume = the entry's volume x the product of its curves; a pitched loop plays at <c>|variable| / reference</c> of its
    /// base rate; a sound with a trigger plays once each time the trigger fires, without looking at its conditions; one without
    /// loops while its conditions hold, or plays once when they start to hold (<see cref="OnceOnRise"/>). Distance and panning
    /// are left to the audio source.
    /// </summary>
    internal sealed class SoundDef
    {
        internal string Name;
        internal float Volume = 1f;
        internal SoundCurve[] Curves = Array.Empty<SoundCurve>();
        internal SoundCondition[] Conditions = Array.Empty<SoundCondition>();

        /// <summary>The trigger that starts it (a one-shot); null for a sound that loops while its conditions hold.</summary>
        internal string Trigger;

        /// <summary>Without a trigger: play once when the conditions start to hold instead of looping while they do.</summary>
        internal bool OnceOnRise;

        /// <summary>Pitched loop: the variable that sets the pitch (empty: plays at its own rate), its value at the base rate,
        /// the base rate in Hz and the clip's own rate in Hz.</summary>
        internal string PitchVariable;
        internal float PitchReference = 1f;
        internal float BaseRate = 44100f;
        internal float ClipRate = 44100f;

        internal bool IsPitched => !string.IsNullOrEmpty(PitchVariable);
        internal bool IsTriggered => !string.IsNullOrEmpty(Trigger);
    }

    /// <summary>What a sound should do this frame.</summary>
    internal struct SoundFrame
    {
        /// <summary>It should be playing after this frame.</summary>
        internal bool Play;

        /// <summary>Begin playing now (a loop that is not running, a trigger that fired, conditions that just started to hold).</summary>
        internal bool Start;

        /// <summary>Linear volume of the source.</summary>
        internal float Volume;

        /// <summary>Playback speed relative to the clip (the audio source's pitch).</summary>
        internal float Pitch;
    }

    /// <summary>One sound's state between frames; <see cref="Update"/> turns the variables into a <see cref="SoundFrame"/>.</summary>
    internal sealed class SoundVoice
    {
        /// <summary>Below this the sound is not started (-100 dB).</summary>
        internal const float kMinGain = 1e-5f;

        /// <summary>A pitched loop below this frequency is silent (DirectSound's minimum, which OMSI's content is tuned to).</summary>
        internal const float kMinFrequency = 100f;

        private readonly SoundDef m_Def;
        private bool m_Held;
        private float m_Kept;

        internal SoundVoice(SoundDef def)
        {
            m_Def = def ?? throw new ArgumentNullException(nameof(def));
        }

        internal SoundDef Def => m_Def;

        internal void Reset()
        {
            m_Held = false;
            m_Kept = 0f;
        }

        /// <param name="variable">The bus's variables by name; an unknown one reads 0.</param>
        /// <param name="fired">The sound's trigger fired this frame.</param>
        /// <param name="playing">The source is still playing from an earlier frame.</param>
        internal SoundFrame Update(Func<string, float> variable, bool fired, bool playing)
        {
            SoundDef d = m_Def;
            var frame = new SoundFrame { Pitch = 1f };

            bool holds = d.IsTriggered || ConditionsHold(variable);
            float curves = Curves(variable);

            if (d.IsPitched)
            {
                float frequency = d.BaseRate * Math.Abs(variable(d.PitchVariable)) / d.PitchReference;
                if (frequency < kMinFrequency)
                    holds = false;
                else
                    frame.Pitch = frequency / Math.Max(d.ClipRate, 1f);
            }

            if (d.IsTriggered)
            {
                // A triggered sound keeps the loudest its curves gave since it fired: a door's hit reads the door's speed, which is
                // back to 0 a frame after the leaf stops, and must not be cut to a click.
                if (fired)
                    m_Kept = 0f;
                if (fired || playing)
                    m_Kept = Math.Max(m_Kept, curves);
                else
                    m_Kept = 0f;
                frame.Volume = d.Volume * m_Kept;
                frame.Start = fired && frame.Volume > kMinGain;
                frame.Play = frame.Start || (playing && !fired);
                return frame;
            }

            frame.Volume = holds ? d.Volume * curves : 0f;
            bool audible = holds && frame.Volume > kMinGain;
            if (d.OnceOnRise)
            {
                frame.Start = audible && !m_Held;
                frame.Play = frame.Start || playing;
            }
            else
            {
                frame.Play = audible;
                frame.Start = audible && !playing;
            }
            m_Held = holds;
            return frame;
        }

        private bool ConditionsHold(Func<string, float> variable)
        {
            SoundCondition[] c = m_Def.Conditions;
            for (int i = 0; i < c.Length; i++)
                if (!c[i].Holds(variable(c[i].Variable)))
                    return false;
            return true;
        }

        private float Curves(Func<string, float> variable)
        {
            SoundCurve[] c = m_Def.Curves;
            float v = 1f;
            for (int i = 0; i < c.Length; i++)
                v *= c[i].Evaluate(variable(c[i].Variable));
            return v;
        }
    }
}
