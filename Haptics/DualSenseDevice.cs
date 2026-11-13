using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CDLC.BusDriver
{
    /// <summary>
    /// A DualSense / DualSense Edge opened for output through Windows HID, next to the game's own reader (both handles share the
    /// device). A worker thread writes the latest output: the game thread never waits for the controller. While anything is on,
    /// the report is repeated every 40 ms (the controller drops rumble that is not refreshed); when everything is off it is
    /// written once.
    /// </summary>
    internal sealed class DualSenseDevice : IDisposable
    {
        private const ushort kSonyVendor = 0x054C;
        private const ushort kDualSense = 0x0CE6, kDualSenseEdge = 0x0DF2;
        private const int kRepeatMs = 40;
        private const int kMaxFailures = 3;
        private const int kHandBackMs = 12;
        private const int kBluetoothReport = 78;

        private readonly SafeFileHandle m_Handle;
        private readonly int m_ReportLength;
        private readonly Thread m_Thread;
        private readonly object m_Lock = new object();
        private readonly AutoResetEvent m_Wake = new AutoResetEvent(false);
        private DualSenseOutput m_Latest;
        private volatile bool m_Stop;
        private volatile bool m_Failed;

        internal bool Failed => m_Failed;
        internal string Path { get; }

        private DualSenseDevice(SafeFileHandle handle, string path, int reportLength)
        {
            m_Handle = handle;
            Path = path;
            m_ReportLength = reportLength;
            m_Thread = new Thread(Run) { IsBackground = true, Name = "CDLC.BusDriver.DualSense" };
            m_Thread.Start();
        }

        /// <summary>The first DualSense found, or null with the reason (no controller, or its HID interface refused the write handle).</summary>
        internal static DualSenseDevice Open(out string reason)
        {
            reason = "no DualSense HID interface found";
            try
            {
                foreach (string path in HidNative.Interfaces())
                {
                    if (!HidNative.Query(path, out HidNative.Attributes attributes, out HidNative.Caps caps))
                        continue;
                    if (attributes.VendorId != kSonyVendor || attributes.ProductId != kDualSense && attributes.ProductId != kDualSenseEdge)
                        continue;

                    // The gamepad collection: generic desktop page. Over Bluetooth its output report is 78 bytes (a different format,
                    // not supported); over USB it is 48.
                    if (caps.UsagePage != 0x01)
                        continue;
                    if (caps.OutputReportByteLength >= kBluetoothReport)
                    {
                        reason = "the DualSense is connected over Bluetooth (only USB is supported)";
                        continue;
                    }

                    if (caps.OutputReportByteLength < DualSenseReport.kUsbLength)
                    {
                        reason = $"DualSense interface with an unexpected output report ({caps.OutputReportByteLength} bytes, usage page {caps.UsagePage})";
                        continue;
                    }

                    SafeFileHandle handle = HidNative.OpenForWrite(path);
                    if (handle.IsInvalid)
                    {
                        reason = $"the DualSense could not be opened for writing (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})";
                        handle.Dispose();
                        continue;
                    }

                    reason = null;
                    return new DualSenseDevice(handle, path, caps.OutputReportByteLength);
                }
            }
            catch (Exception ex)
            {
                reason = $"{ex.GetType().Name}: {ex.Message}";
            }

            return null;
        }

        /// <summary>Hands over what the controller should do now; the worker writes it.</summary>
        internal void Submit(in DualSenseOutput output)
        {
            lock (m_Lock)
            {
                m_Latest = output;
            }

            m_Wake.Set();
        }

        private void Run()
        {
            byte[] last = null;
            long lastWrite = 0;
            int failures = 0;
            bool motorsWereOn = false;
            while (!m_Stop)
            {
                m_Wake.WaitOne(kRepeatMs);
                if (m_Stop)
                    break;

                DualSenseOutput output;
                lock (m_Lock)
                {
                    output = m_Latest;
                }

                bool motorsOn = output.Heavy > 0f || output.Light > 0f;
                // Repeated while something plays. Releasing the triggers is written once (a changed report): repeating it kept the
                // pad written 25 times a second for the rest of the session after the first drive.
                bool active = motorsOn || (output.SetTriggers && (!output.Right.IsOff || !output.Left.IsOff));
                byte[] report = DualSenseReport.Build(output, m_ReportLength);
                long now = Environment.TickCount;
                bool changed = last == null || !SameReport(report, last);
                if (!changed && !(active && now - lastWrite >= kRepeatMs))
                    continue;

                // A rumble ending while the audio haptics own the actuators: one report that says "motors off" with the rumble flags,
                // then the one without them (the pad takes two packets to leave its rumble emulation for the audio).
                if (output.AudioHaptics && !motorsOn && motorsWereOn)
                {
                    DualSenseOutput stop = output;
                    stop.StopMotors = true;
                    Write(DualSenseReport.Build(stop, m_ReportLength), ref failures);
                    if (m_Failed)
                        return;
                    Thread.Sleep(kHandBackMs);
                }

                if (Write(report, ref failures))
                {
                    last = report;
                    lastWrite = now;
                    motorsWereOn = motorsOn;
                }
                else if (m_Failed)
                {
                    return;
                }
            }
        }

        /// <summary>One write: true when the controller took it. Too many failures in a row mark the device failed (the worker then ends).</summary>
        private bool Write(byte[] report, ref int failures)
        {
            if (HidNative.WriteFile(m_Handle, report, (uint)report.Length, out _, IntPtr.Zero))
            {
                failures = 0;
                return true;
            }

            if (++failures >= kMaxFailures)
                m_Failed = true;
            return false;
        }

        private static bool SameReport(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        /// <summary>Everything off (motors stopped, triggers released), then the handle closed.</summary>
        public void Dispose()
        {
            if (m_Stop)
                return;

            m_Stop = true;
            m_Wake.Set();
            if (!m_Thread.Join(200))
            {
                try { HidNative.CancelIoEx(m_Handle, IntPtr.Zero); } catch (Exception) { }
                if (!m_Thread.Join(200))
                {
                    // Still stuck in the driver: closing the handle or the event under it could crash the worker. Both are left to
                    // the finalizer; the worker ends on its own once the write returns (m_Stop is set).
                    return;
                }
            }

            if (!m_Failed && !m_Handle.IsClosed)
            {
                try
                {
                    var off = new DualSenseOutput { SetTriggers = true, Right = TriggerEffect.Off, Left = TriggerEffect.Off };
                    byte[] report = DualSenseReport.Build(off, m_ReportLength);
                    HidNative.WriteFile(m_Handle, report, (uint)report.Length, out _, IntPtr.Zero);
                    // and the pad back to its native haptics (a report without the rumble flags), for whatever plays audio haptics next
                    Thread.Sleep(kHandBackMs);
                    var handBack = new DualSenseOutput { AudioHaptics = true };
                    report = DualSenseReport.Build(handBack, m_ReportLength);
                    HidNative.WriteFile(m_Handle, report, (uint)report.Length, out _, IntPtr.Zero);
                }
                catch (Exception)
                {
                }
            }

            m_Handle.Dispose();
            m_Wake.Dispose();
        }
    }
}
