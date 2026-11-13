using System;

namespace CDLC.BusDriver
{
    /// <summary>One adaptive trigger effect (the DualSense's R2/L2 motors).</summary>
    internal struct TriggerEffect
    {
        internal byte Mode;
        internal byte P0, P1, P2, P3, P4, P5, P8;      // the parameters in use; the rest of the 10 stay 0

        internal static readonly TriggerEffect Off = new TriggerEffect { Mode = 0x05 };

        /// <summary>No effect: the trigger is released (Off) or was never set (all zero).</summary>
        internal bool IsOff => Mode == 0x05 || Mode == 0;

        /// <summary>
        /// Resistance from a zone on: the trigger pushes back with the strength (1 = light .. 8 = hard) from startZone (0..9) to the
        /// end of its travel.
        /// </summary>
        internal static TriggerEffect Resistance(int startZone, int strength)
        {
            startZone = Math.Max(0, Math.Min(9, startZone));
            int s = Math.Max(1, Math.Min(8, strength)) - 1;
            int zones = 0;
            uint amounts = 0;
            for (int zone = startZone; zone < 10; zone++)
            {
                zones |= 1 << zone;
                amounts |= (uint)s << (3 * zone);
            }

            return Zones(0x21, zones, amounts, 0);
        }

        /// <summary>The same strength per zone from a zone on, with the trigger buzzing at the frequency (Hz, 1..255).</summary>
        internal static TriggerEffect Vibration(int startZone, int strength, int frequency)
        {
            startZone = Math.Max(0, Math.Min(9, startZone));
            int s = Math.Max(1, Math.Min(8, strength)) - 1;
            int zones = 0;
            uint amounts = 0;
            for (int zone = startZone; zone < 10; zone++)
            {
                zones |= 1 << zone;
                amounts |= (uint)s << (3 * zone);
            }

            return Zones(0x26, zones, amounts, (byte)Math.Max(1, Math.Min(255, frequency)));
        }

        /// <summary>A stiff wall with a click: resistance between the zones (2..7 to 3..8), then it gives way.</summary>
        internal static TriggerEffect Weapon(int startZone, int endZone, int strength)
        {
            startZone = Math.Max(2, Math.Min(7, startZone));
            endZone = Math.Max(startZone + 1, Math.Min(8, endZone));
            int zones = (1 << startZone) | (1 << endZone);
            return new TriggerEffect
            {
                Mode = 0x25,
                P0 = (byte)(zones & 0xFF),
                P1 = (byte)(zones >> 8),
                P2 = (byte)(Math.Max(1, Math.Min(8, strength)) - 1)
            };
        }

        private static TriggerEffect Zones(byte mode, int zones, uint amounts, byte frequency)
        {
            return new TriggerEffect
            {
                Mode = mode,
                P0 = (byte)(zones & 0xFF),
                P1 = (byte)(zones >> 8),
                P2 = (byte)(amounts & 0xFF),
                P3 = (byte)((amounts >> 8) & 0xFF),
                P4 = (byte)((amounts >> 16) & 0xFF),
                P5 = (byte)((amounts >> 24) & 0xFF),
                P8 = frequency
            };
        }

        internal void WriteTo(byte[] buffer, int offset)
        {
            buffer[offset] = Mode;
            buffer[offset + 1] = P0;
            buffer[offset + 2] = P1;
            buffer[offset + 3] = P2;
            buffer[offset + 4] = P3;
            buffer[offset + 5] = P4;
            buffer[offset + 6] = P5;
            buffer[offset + 9] = P8;
        }
    }

    /// <summary>What to send to the controller: the two rumble motors and the two triggers.</summary>
    internal struct DualSenseOutput
    {
        /// <summary>The strong, low-frequency motor (left grip), 0..1.</summary>
        internal float Heavy;

        /// <summary>The light, high-frequency motor (right grip), 0..1.</summary>
        internal float Light;

        /// <summary>
        /// The driving mod plays the fine haptics through the pad's audio endpoint (USB) and needs the actuators for itself. The pad
        /// has two exclusive vibration modes: rumble emulation (these motors, chosen by the flags of a report) and native audio
        /// haptics; every report that carries the rumble flags, motors at zero or not, keeps it in emulation and mutes the audio.
        /// With this set the rumble flags go out only while a motor is on (or <see cref="StopMotors"/>): a report without them hands
        /// the actuators back to the audio.
        /// </summary>
        internal bool AudioHaptics;

        /// <summary>One report that says "motors off" with the rumble flags set (the last of a rumble, before handing back to the audio).</summary>
        internal bool StopMotors;

        internal bool SetTriggers;
        internal TriggerEffect Right;
        internal TriggerEffect Left;
    }

    /// <summary>
    /// The DualSense's output reports, as the community has worked them out (the Linux hid-playstation driver for the common
    /// part; the trigger effect modes from the reverse-engineered notes). Pure functions, no I/O: DualSenseDevice writes them.
    /// USB only: report 0x02, the controller's OutputReportByteLength bytes (48 for the DualSense on Windows). Bluetooth (report
    /// 0x31 with a CRC) is not supported.
    /// </summary>
    internal static class DualSenseReport
    {
        /// <summary>The shortest USB output report: the id and the 47-byte common block. Windows reports 48 for a DualSense over USB.</summary>
        internal const int kUsbLength = 48;
        internal const byte kUsbId = 0x02;

        // Offsets inside the common block of 47 bytes.
        private const int kFlag0 = 0, kMotorRight = 2, kMotorLeft = 3;
        private const int kRightTrigger = 10, kLeftTrigger = 21;
        private const int kFlag2 = 38;

        // valid_flag0: rumble (compatible vibration + haptics select), the right and the left trigger.
        private const byte kFlag0Vibration = 0x01 | 0x02, kFlag0RightTrigger = 0x04, kFlag0LeftTrigger = 0x08;
        // valid_flag2: the improved rumble emulation.
        private const byte kFlag2ImprovedRumble = 0x04;

        /// <summary>The USB report for a controller whose output report is the given length (WriteFile wants exactly that many bytes).</summary>
        internal static byte[] Build(in DualSenseOutput output, int outputReportLength)
        {
            var report = new byte[Math.Max(kUsbLength, outputReportLength)];
            const int common = 1;
            report[0] = kUsbId;

            // The rumble flags select the pad's rumble emulation, which silences the audio haptics (see AudioHaptics): when the audio
            // owns the actuators, only a report that really rumbles (or stops a rumble) carries them.
            if (Rumbles(output))
            {
                report[common + kFlag0] = kFlag0Vibration;
                report[common + kFlag2] = kFlag2ImprovedRumble;
                report[common + kMotorRight] = Motor(output.Light);
                report[common + kMotorLeft] = Motor(output.Heavy);
            }

            if (output.SetTriggers)
            {
                report[common + kFlag0] |= kFlag0RightTrigger | kFlag0LeftTrigger;
                output.Right.WriteTo(report, common + kRightTrigger);
                output.Left.WriteTo(report, common + kLeftTrigger);
            }

            return report;
        }

        /// <summary>Does this report carry the rumble flags? Always, unless the audio haptics own the actuators and no motor is on.</summary>
        internal static bool Rumbles(in DualSenseOutput output) =>
            !output.AudioHaptics || output.StopMotors || output.Heavy > 0f || output.Light > 0f;

        private static byte Motor(float value) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value * 255f)));
    }
}
