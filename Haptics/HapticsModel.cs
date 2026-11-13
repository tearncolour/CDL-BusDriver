using System;

namespace CDLC.BusDriver
{
    /// <summary>What the driving mod reports this frame (CSL Driving Life Core's DrivingApi), as plain numbers.</summary>
    internal struct DriveSample
    {
        internal bool Driving;
        internal float SpeedMps;
        internal float Throttle;
        internal float Brake;
        /// <summary>m/s2 along the bus (negative when slowing down) and vertical (crests, dips, kerbs).</summary>
        internal float AccelAlong;
        internal float AccelVertical;
        internal float EngineRpm;
        internal bool Shifting;
        internal bool DoorsMoving;
        internal bool ParkingBrake;
    }

    /// <summary>The controls that click through the pad's motors when operated (HapticsModel.Cue).</summary>
    internal enum HapticCue { Indicator, IndicatorCancel, Flash, LightSwitch, Hazard }

    /// <summary>
    /// The feel of the bus: engine and road through the rumble motors, jolts when the gearbox shifts or the doors move, hard
    /// braking; and, on a DualSense, a weighted brake trigger (L2) and a throttle trigger (R2) that buzzes through a shift. Pure
    /// numbers in, motor and trigger values out. The edges (a shift starts, the doors start to move) are kept in the instance.
    /// </summary>
    internal sealed class HapticsModel
    {
        private const float kEngineRpmMax = 2400f;
        private const float kRoadSpeedMps = 22f;
        private const float kHardDecel = 3f, kBump = 2.5f;
        private const float kShiftPulse = 0.18f, kDoorPulse = 0.16f;

        // The trigger vibration (TriggerVibration). The R2 engine shiver: from a light press on, amplitude 1..6 by the pedal and the
        // load, frequency 24..94 Hz by the revs (or, with no revs to read - an electric bus - by the speed), the drive smoothed so the
        // effect does not change at frame rate. The L2 pulses under hard braking: amplitude 2..7 by how hard, 16 Hz, held a moment.
        // A jolt for a bump: both triggers, 3..7, 60 Hz, a tenth of a second.
        private const float kTriggerPress = 0.06f;
        private const float kEngineFrequencyLow = 24f, kEngineFrequencySpan = 70f, kFrequencyStep = 4f;
        private const float kDriveSeconds = 0.12f;
        private const float kAbsDecel = 3f, kAbsSpeed = 2f, kAbsBrake = 0.3f, kAbsHold = 0.15f, kAbsFrequency = 16f;
        private const float kTriggerBump = 4.5f, kTriggerBumpSpeed = 3f, kBumpSeconds = 0.1f, kBumpFrequency = 60f;
        /// <summary>The rumble strength at which the trigger effects are as designed (the default of the option); above it they get stronger.</summary>
        private const float kNominalStrength = 0.8f;

        private bool m_WasShifting, m_WasDoorsMoving;
        private float m_ShiftLeft, m_DoorLeft;
        private float m_CueLeft, m_CueHeavy, m_CueLight;
        private float m_Drive, m_AbsLeft, m_AbsAmplitude, m_BumpLeft, m_BumpAmplitude;
        private bool m_TriggersSent;

        /// <summary>Strength of the rumble, 0..1.</summary>
        internal float Strength = 0.8f;

        /// <summary>Drive the DualSense triggers as well (L2 brake weight, R2 throttle).</summary>
        internal bool Triggers;

        /// <summary>
        /// The DualSense triggers vibrate (the option "DualSense trigger vibration"): R2 with the engine, L2 in pulses under hard
        /// braking, both for a jolt over a bump, R2 through a gear shift. Independent of <see cref="Triggers"/> (the stiffness); where
        /// both want a trigger the vibration is what is felt at that moment. Follows <see cref="Strength"/>.
        /// </summary>
        internal bool TriggerVibration;

        /// <summary>
        /// The driving mod already plays the sounds' low end through the actuators (DrivingApi 8, PadHapticsActive): the engine, the
        /// road, the braking and bumps, the gear shifts are felt there. The motors then carry only what the driving mod does not make:
        /// the doors and the clicks of the driver's controls.
        /// </summary>
        internal bool SoundFeel;

        internal void Reset()
        {
            m_WasShifting = m_WasDoorsMoving = false;
            m_ShiftLeft = m_DoorLeft = 0f;
            m_CueLeft = m_CueHeavy = m_CueLight = 0f;
            m_Drive = m_AbsLeft = m_AbsAmplitude = m_BumpLeft = m_BumpAmplitude = 0f;
            m_TriggersSent = false;        // (whoever stops the model frees the triggers: GamepadHaptics.Silence)
        }

        /// <summary>
        /// A control was operated: a short detent through the motors, like the switch under the hand (the indicator stalk, the light
        /// switch, the hazard button). Felt in the next Compute; a stronger cue still running is not cut short by a weaker one.
        /// </summary>
        internal void Cue(HapticCue cue)
        {
            float seconds, heavy, light;
            switch (cue)
            {
                case HapticCue.Indicator: seconds = 0.05f; heavy = 0f; light = 0.35f; break;
                case HapticCue.IndicatorCancel: seconds = 0.06f; heavy = 0.2f; light = 0.2f; break;
                case HapticCue.Flash: seconds = 0.04f; heavy = 0f; light = 0.3f; break;
                case HapticCue.LightSwitch: seconds = 0.07f; heavy = 0.3f; light = 0.4f; break;
                case HapticCue.Hazard: seconds = 0.09f; heavy = 0.4f; light = 0.3f; break;
                default: return;
            }

            if (seconds * (heavy + light) >= m_CueLeft * (m_CueHeavy + m_CueLight))
            {
                m_CueLeft = seconds;
                m_CueHeavy = heavy;
                m_CueLight = light;
            }
        }

        internal DualSenseOutput Compute(in DriveSample s, float dt)
        {
            var output = new DualSenseOutput { Right = TriggerEffect.Off, Left = TriggerEffect.Off };
            if (!s.Driving)
            {
                Reset();
                return output;
            }

            if (s.Shifting && !m_WasShifting)
                m_ShiftLeft = kShiftPulse;
            if (s.DoorsMoving && !m_WasDoorsMoving)
                m_DoorLeft = kDoorPulse;
            m_WasShifting = s.Shifting;
            m_WasDoorsMoving = s.DoorsMoving;
            m_ShiftLeft = Math.Max(0f, m_ShiftLeft - dt);
            m_DoorLeft = Math.Max(0f, m_DoorLeft - dt);

            float throttle = Clamp01(s.Throttle);
            float rpm = Clamp01(s.EngineRpm / kEngineRpmMax);
            float speed = Clamp01(Math.Abs(s.SpeedMps) / kRoadSpeedMps);
            bool alive = Math.Abs(s.SpeedMps) > 0.3f || throttle > 0.05f;

            float heavy = 0f, light = 0f;
            if (alive && !SoundFeel)
            {
                heavy += 0.03f + 0.10f * rpm + 0.12f * throttle;
                light += 0.05f * speed;
            }

            float decel = -s.AccelAlong;
            if (decel > kHardDecel && !SoundFeel)
                heavy += Clamp01((decel - kHardDecel) / 6f) * 0.5f;

            float bump = Math.Abs(s.AccelVertical);
            if (bump > kBump && !SoundFeel)
                light += Clamp01((bump - kBump) / 6f) * 0.7f;

            if (m_ShiftLeft > 0f && !SoundFeel)
            {
                heavy += 0.2f;
                light += 0.5f;
            }

            if (m_DoorLeft > 0f)
                heavy += 0.45f;

            if (m_CueLeft > 0f)
            {
                heavy += m_CueHeavy;
                light += m_CueLight;
                m_CueLeft = Math.Max(0f, m_CueLeft - dt);
            }

            output.Heavy = Clamp01(heavy * Strength);
            output.Light = Clamp01(light * Strength);

            bool vibrate = TriggerVibration && Strength > 0.01f;
            float amplitudeScale = Math.Max(0.25f, Math.Min(1.25f, Strength / kNominalStrength));
            if (vibrate)
                UpdateTriggerEvents(s, rpm, speed, dt);
            else
                m_Drive = m_AbsLeft = m_BumpLeft = 0f;

            if (Triggers || vibrate)
            {
                output.SetTriggers = true;
                m_TriggersSent = true;

                // L2: the brake pedal's weight (the harder the brake, the stiffer; a stiff wall while the parking brake is on), or,
                // with the vibration on, the ABS-like pulses of a hard stop and the jolt of a bump.
                float brake = Clamp01(s.Brake);
                if (Triggers && s.ParkingBrake)
                    output.Left = TriggerEffect.Weapon(2, 4, 8);
                else if (vibrate && m_AbsLeft > 0f)
                    output.Left = TriggerEffect.Vibration(1, Amplitude(m_AbsAmplitude, amplitudeScale), (int)kAbsFrequency);
                else if (vibrate && m_BumpLeft > 0f)
                    output.Left = TriggerEffect.Vibration(0, Amplitude(m_BumpAmplitude, amplitudeScale), (int)kBumpFrequency);
                else if (Triggers)
                    output.Left = TriggerEffect.Resistance(1, 2 + (int)Math.Round(brake * 5f));

                // R2: a buzz through a gearbox shift, the jolt of a bump, the engine shivering under the pedal; else a light spring,
                // heavier under load.
                if ((Triggers || vibrate) && m_ShiftLeft > 0f)
                    output.Right = TriggerEffect.Vibration(3, Amplitude(4f, vibrate ? amplitudeScale : 1f), 40);
                else if (vibrate && m_BumpLeft > 0f)
                    output.Right = TriggerEffect.Vibration(0, Amplitude(m_BumpAmplitude, amplitudeScale), (int)kBumpFrequency);
                else if (vibrate && throttle > kTriggerPress)
                {
                    float load = throttle * (0.4f + 0.6f * m_Drive);
                    float frequency = kEngineFrequencyLow + kEngineFrequencySpan * m_Drive;
                    frequency = (float)Math.Round(frequency / kFrequencyStep) * kFrequencyStep;
                    output.Right = TriggerEffect.Vibration(1, Amplitude(1f + 5f * load, amplitudeScale), (int)frequency);
                }
                else if (Triggers)
                    output.Right = TriggerEffect.Resistance(1, throttle > 0.8f ? 4 : 2);
            }
            else if (m_TriggersSent)
            {
                // the effects were on and now neither is wanted (an option switched off, the strength at 0): one report that frees both
                output.SetTriggers = true;
                m_TriggersSent = false;
            }

            return output;
        }

        // The events behind the trigger vibration: the engine's drive (revs, or speed when there are none), the hard stop, the bump.
        private void UpdateTriggerEvents(in DriveSample s, float rpm, float speed, float dt)
        {
            float target = Math.Max(rpm, 0.8f * speed);
            float k = dt > 0f ? 1f - (float)Math.Exp(-dt / kDriveSeconds) : 1f;
            m_Drive += (target - m_Drive) * k;

            float decel = -s.AccelAlong;
            float brake = Clamp01(s.Brake);
            if (decel > kAbsDecel && Math.Abs(s.SpeedMps) > kAbsSpeed && brake > kAbsBrake)
            {
                m_AbsLeft = kAbsHold;
                m_AbsAmplitude = 2f + 5f * Clamp01((decel - kAbsDecel) / 6f);
            }
            else
                m_AbsLeft = Math.Max(0f, m_AbsLeft - dt);

            // a jolt runs for its tenth of a second whatever follows; a bigger one while one runs replaces it, an equal one does not restart it
            m_BumpLeft = Math.Max(0f, m_BumpLeft - dt);
            float bump = Math.Abs(s.AccelVertical);
            if (bump > kTriggerBump && Math.Abs(s.SpeedMps) > kTriggerBumpSpeed)
            {
                float amplitude = 3f + 4f * Clamp01((bump - kTriggerBump) / 6f);
                if (m_BumpLeft <= 0f || amplitude > m_BumpAmplitude)
                {
                    m_BumpLeft = kBumpSeconds;
                    m_BumpAmplitude = amplitude;
                }
            }
        }

        // An amplitude 1..8 for the trigger effect, scaled by the user's strength.
        private static int Amplitude(float value, float scale) => Math.Max(1, Math.Min(8, (int)Math.Round(value * scale)));

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
