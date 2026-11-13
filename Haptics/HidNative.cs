using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CDLC.BusDriver
{
    /// <summary>The few Windows HID and SetupAPI calls the DualSense output needs (hid.dll, setupapi.dll, kernel32.dll).</summary>
    internal static class HidNative
    {
        internal const uint kGenericRead = 0x80000000, kGenericWrite = 0x40000000;
        internal const uint kShareRead = 1, kShareWrite = 2, kOpenExisting = 3;
        private const uint kPresent = 0x2, kDeviceInterface = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInterfaceData
        {
            internal uint Size;
            internal Guid Class;
            internal uint Flags;
            internal UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Attributes
        {
            internal uint Size;
            internal ushort VendorId;
            internal ushort ProductId;
            internal ushort Version;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Caps
        {
            internal ushort Usage;
            internal ushort UsagePage;
            internal ushort InputReportByteLength;
            internal ushort OutputReportByteLength;
            internal ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            internal ushort[] Reserved;
            internal ushort NumberLinkCollectionNodes;
            internal ushort NumberInputButtonCaps;
            internal ushort NumberInputValueCaps;
            internal ushort NumberInputDataIndices;
            internal ushort NumberOutputButtonCaps;
            internal ushort NumberOutputValueCaps;
            internal ushort NumberOutputDataIndices;
            internal ushort NumberFeatureButtonCaps;
            internal ushort NumberFeatureValueCaps;
            internal ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr data, ref Caps caps);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref DeviceInterfaceData data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint count, out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        /// <summary>The paths of every present HID interface (the DualSense is one of them).</summary>
        internal static System.Collections.Generic.List<string> Interfaces()
        {
            var paths = new System.Collections.Generic.List<string>();
            HidD_GetHidGuid(out Guid hid);
            IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, kPresent | kDeviceInterface);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
                return paths;

            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf(typeof(DeviceInterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index, ref data))
                        break;

                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                    if (required < 6)
                        continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W: a DWORD size (8 on 64-bit, 6 on 32-bit), then the path.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(detail + 4);
                            if (!string.IsNullOrEmpty(path))
                                paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return paths;
        }

        /// <summary>Opens an interface to ask its ids and report sizes only (no read or write access).</summary>
        internal static bool Query(string path, out Attributes attributes, out Caps caps)
        {
            attributes = new Attributes { Size = (uint)Marshal.SizeOf(typeof(Attributes)) };
            caps = default;
            using (SafeFileHandle handle = CreateFile(path, 0, kShareRead | kShareWrite, IntPtr.Zero, kOpenExisting, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid || !HidD_GetAttributes(handle, ref attributes))
                    return false;
                if (!HidD_GetPreparsedData(handle, out IntPtr data))
                    return false;
                try
                {
                    caps = new Caps { Reserved = new ushort[17] };
                    return HidP_GetCaps(data, ref caps) >= 0;
                }
                finally
                {
                    HidD_FreePreparsedData(data);
                }
            }
        }

        /// <summary>Opens an interface for writing, sharing it with the game's own reader.</summary>
        internal static SafeFileHandle OpenForWrite(string path) =>
            CreateFile(path, kGenericRead | kGenericWrite, kShareRead | kShareWrite, IntPtr.Zero, kOpenExisting, 0, IntPtr.Zero);
    }
}
