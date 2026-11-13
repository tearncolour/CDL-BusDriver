using System;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;

namespace CDLC.BusDriver
{
    /// <summary>
    /// Rumble and DualSense triggers while the player drives the bus. A DualSense (as the game's input system sees it, so not
    /// behind Steam Input, which turns it into an Xbox pad) is written through Windows HID by <see cref="DualSenseDevice"/>: the
    /// rumble and the adaptive triggers. Any other pad gets the input system's own rumble. If the DualSense cannot be opened for
    /// writing, its rumble goes through the input system too and the triggers stay as they are.
    /// </summary>
    internal static class GamepadHaptics
    {
        private const float kRetrySeconds = 5f;

        private static readonly HapticsModel s_Model = new HapticsModel();
        private static DualSenseDevice s_Device;
        private static float s_NextOpen;
        private static bool s_Active;
        private static bool s_UnityRumble;
        private static bool s_TriggersOn;
        private static bool s_TriggerLogged;
        private static bool s_SampleLogged;
        private static bool s_TickLogged;
        private static string s_OpenFailure;
        private static float s_OpenRetry = kRetrySeconds;
        private static Gamepad s_RumblePad;
        private static float s_RumbleHeavy, s_RumbleLight, s_RumbleResend;

        /// <summary>Per frame, from the cab system.</summary>
        internal static void Tick(float dt)
        {
            try
            {
                TickCore(dt);
            }
            catch (Exception ex)
            {
                // Never let the pad take the cab system down with it.
                if (!s_TickLogged)
                {
                    s_TickLogged = true;
                    Mod.log.Warn($"Gamepad feedback: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void TickCore(float dt)
        {
            BusSettings settings = Mod.Settings;
            bool wanted = settings != null && settings.GamepadFeedback && Time.timeScale > 0f && IsDrivingBus();
            if (!wanted)
            {
                if (s_Active)
                    Silence();
                return;
            }

            Gamepad pad = Gamepad.current;
            if (pad == null)
            {
                if (s_Active)
                    Silence();
                return;
            }

            if (!Sample(out DriveSample sample))
                return;

            s_Active = true;
            s_Model.Strength = Mathf.Clamp01(settings.FeedbackStrength / 100f);
            bool dualSense = pad is DualSenseGamepadHID;
            s_Model.Triggers = settings.AdaptiveTriggers && dualSense;
            s_Model.TriggerVibration = settings.TriggerVibration && dualSense;
            // The driving mod feels the engine and the road through the actuators itself when it can: the motors then add only ours.
            s_Model.SoundFeel = Core.PadHapticsActive();
            DualSenseOutput output = s_Model.Compute(sample, dt);
            // With the driving mod's audio haptics on the actuators, our rumble flags go out only while a motor really rumbles.
            output.AudioHaptics = s_Model.SoundFeel;
            bool triggerEffects = s_Model.Triggers || s_Model.TriggerVibration;
            if (s_TriggersOn && !triggerEffects)
                output.SetTriggers = true;      // the options were switched off while driving: release both triggers once
            s_TriggersOn = triggerEffects;
            if (triggerEffects && !s_TriggerLogged && s_Device != null && !s_Device.Failed)
            {
                s_TriggerLogged = true;
                Mod.log.Info($"DualSense trigger effects on ({(s_Model.TriggerVibration ? "vibration" : "")}{(s_Model.TriggerVibration && s_Model.Triggers ? " and " : "")}{(s_Model.Triggers ? "stiffness" : "")}).");
            }

            if (pad is DualSenseGamepadHID && EnsureDevice())
            {
                if (s_UnityRumble)
                {
                    SafeReset(pad);
                    s_UnityRumble = false;
                }

                s_Device.Submit(output);
                return;
            }

            // Sent when it changes (each call is a report to the device), and again every half second in case one was lost.
            float now = Time.realtimeSinceStartup;
            if (s_UnityRumble && pad == s_RumblePad && output.Heavy == s_RumbleHeavy && output.Light == s_RumbleLight && now < s_RumbleResend)
                return;
            s_UnityRumble = true;
            s_RumblePad = pad;
            s_RumbleHeavy = output.Heavy;
            s_RumbleLight = output.Light;
            s_RumbleResend = now + 0.5f;
            try
            {
                pad.SetMotorSpeeds(output.Heavy, output.Light);
            }
            catch (Exception ex)
            {
                Mod.log.Warn($"Rumble: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// A control was operated (indicator, hazard, light switch, flash): a short click through the motors, only while the
        /// feedback is running and the player is on the pad (a pad lying on the desk does not click for keys).
        /// </summary>
        internal static void Cue(HapticCue cue)
        {
            try
            {
                if (s_Active && Core.PadActive)
                    s_Model.Cue(cue);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The mod unloads: everything off, the controller released.</summary>
        internal static void Shutdown()
        {
            Silence();
            if (s_Device != null)
            {
                s_Device.Dispose();
                s_Device = null;
            }
        }

        private static bool IsDrivingBus()
        {
            try
            {
                // Any bus the player drives, on a shift or not (the indicators, lights and the feel work off shift too).
                return BusState.DrivingBus && Core.Available && Core.IsDriving();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Sample(out DriveSample sample)
        {
            sample = default;
            try
            {
                sample.Driving = true;
                sample.SpeedMps = Core.SpeedMps() * Core.TimeScale();     // the road's rumble and the speed thresholds are per real second
                sample.Throttle = Core.ThrottleInput();
                sample.Brake = Core.BrakeInput();
                Vector3 acceleration = Core.Acceleration();
                sample.AccelAlong = acceleration.z;
                sample.AccelVertical = acceleration.y;
                sample.EngineRpm = Core.EngineRpm();
                sample.Shifting = Core.Shifting();
                sample.ParkingBrake = Core.HasApi4 && Core.ParkingBrake();
                sample.DoorsMoving = BusState.State == ShiftState.DoorsOpening || BusState.State == ShiftState.DoorsClosing;
                return true;
            }
            catch (Exception ex)
            {
                // An older driving mod without these readouts: no feedback, said once.
                if (!s_SampleLogged)
                {
                    s_SampleLogged = true;
                    Mod.log.Info($"Gamepad feedback needs a newer CSL Driving Life Core ({ex.GetType().Name}: {ex.Message}).");
                }

                return false;
            }
        }

        private static bool EnsureDevice()
        {
            if (s_Device != null && s_Device.Failed)
            {
                Mod.log.Warn("DualSense output stopped working; the pad's rumble goes through the game's input system until it can be opened again.");
                s_Device.Dispose();
                s_Device = null;
                s_NextOpen = Time.realtimeSinceStartup + kRetrySeconds;
            }

            if (s_Device != null)
                return true;
            if (Time.realtimeSinceStartup < s_NextOpen)
                return false;

            s_NextOpen = Time.realtimeSinceStartup + kRetrySeconds;
            s_Device = DualSenseDevice.Open(out string reason);
            if (s_Device != null)
            {
                s_OpenFailure = null;
                s_OpenRetry = kRetrySeconds;
                Mod.log.Info("DualSense opened for output (USB).");
                return true;
            }

            // Opening walks every HID device on the main thread: the same failure again (a pad on Bluetooth) waits twice as long
            // before the next try, up to a minute; a new reason starts over at 5 s.
            if (reason != s_OpenFailure)
            {
                s_OpenFailure = reason;
                s_OpenRetry = kRetrySeconds;
                Mod.log.Warn($"DualSense output unavailable, using the game's rumble: {reason}.");
            }
            else
            {
                s_OpenRetry = Mathf.Min(s_OpenRetry * 2f, 60f);
            }

            s_NextOpen = Time.realtimeSinceStartup + s_OpenRetry;
            return false;
        }

        private static void Silence()
        {
            s_Active = false;
            s_TriggersOn = false;
            s_Model.Reset();
            if (s_UnityRumble)
            {
                SafeReset(Gamepad.current);
                s_UnityRumble = false;
                s_RumblePad = null;
            }

            if (s_Device != null && !s_Device.Failed)
                s_Device.Submit(new DualSenseOutput { AudioHaptics = true, SetTriggers = true, Right = TriggerEffect.Off, Left = TriggerEffect.Off });      // and the pad back to its native haptics
        }

        private static void SafeReset(Gamepad pad)
        {
            try
            {
                pad?.ResetHaptics();
            }
            catch (Exception)
            {
            }
        }
    }
}
