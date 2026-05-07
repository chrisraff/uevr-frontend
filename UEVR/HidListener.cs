using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UEVR {
    public class HidBinding {
        public string DevicePath { get; set; } = "";
        public string DeviceFriendlyName { get; set; } = "";
        public int ReportBitIndex { get; set; } = -1;

        public bool IsValid => !string.IsNullOrEmpty(DevicePath) && ReportBitIndex >= 0;

        public override string ToString() {
            if (!IsValid) return "None";
            return $"{DeviceFriendlyName} (btn {ReportBitIndex})";
        }
    }

    public class HidListener : IDisposable {
        private const int WM_INPUT = 0x00FF;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIM_TYPEHID = 2;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RID_INPUT = 0x10000003;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        // Only the fixed-size prefix of RAWHID; bRawData follows in the buffer
        [StructLayout(LayoutKind.Sequential)]
        struct RAWHID {
            public uint dwSizeHid;
            public uint dwCount;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(
            [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] devices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
        static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = false)]
        static extern bool HidD_GetProductString(
            IntPtr hidDevice,
            IntPtr buffer,
            uint bufferLength);

        private HwndSource? m_hwndSource;
        private HidBinding? m_binding;

        // Recording state — tracked per device so baselines never cross-contaminate
        private class DeviceRecordingState {
            public byte[] Baseline = Array.Empty<byte>();
            public byte[] ChangeMask = Array.Empty<byte>();
            public int Samples;
        }
        private bool m_isRecording = false;
        private readonly Dictionary<string, DeviceRecordingState> m_deviceStates = new(StringComparer.OrdinalIgnoreCase);
        private const int BASELINE_SAMPLE_COUNT = 10;

        // Trigger state: track previous report to detect rising edge
        private byte[]? m_lastReport = null;

        public event Action? BindingTriggered;
        public event Action<HidBinding>? RecordingComplete;

        public void Initialize(IntPtr hwnd) {
            m_hwndSource = HwndSource.FromHwnd(hwnd);
            m_hwndSource?.AddHook(WndProc);
            RegisterDevices(hwnd);
        }

        private void RegisterDevices(IntPtr hwnd) {
            var devices = new RAWINPUTDEVICE[] {
                // Generic Desktop: Joystick
                new RAWINPUTDEVICE {
                    usUsagePage = 0x01, usUsage = 0x04,
                    dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd
                },
                // Generic Desktop: Gamepad
                new RAWINPUTDEVICE {
                    usUsagePage = 0x01, usUsage = 0x05,
                    dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd
                },
            };
            RegisterRawInputDevices(devices, (uint)devices.Length,
                (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }

        public void SetBinding(HidBinding? binding) {
            m_binding = binding;
            m_lastReport = null;
        }

        public HidBinding? GetBinding() => m_binding;

        public void StartRecording() {
            m_isRecording = true;
            m_deviceStates.Clear();
        }

        public void StopRecording() {
            m_isRecording = false;
        }

        private string GetDevicePath(IntPtr hDevice) {
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return "";
            var buf = Marshal.AllocHGlobal((int)size * 2);
            try {
                GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref size);
                return Marshal.PtrToStringUni(buf) ?? "";
            } finally {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static string FriendlyNameFromPath(string devicePath) {
            const uint FILE_SHARE_READ  = 0x00000001;
            const uint FILE_SHARE_WRITE = 0x00000002;
            const uint OPEN_EXISTING  = 3;
            var invalid = new IntPtr(-1);

            var handle = CreateFile(devicePath, 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle != invalid) {
                try {
                    var buf = Marshal.AllocHGlobal(512);
                    try {
                        if (HidD_GetProductString(handle, buf, 512)) {
                            var name = Marshal.PtrToStringUni(buf)?.Trim();
                            if (!string.IsNullOrEmpty(name)) return name;
                        }
                    } finally {
                        Marshal.FreeHGlobal(buf);
                    }
                } finally {
                    CloseHandle(handle);
                }
            }

            // Fallback: find the path segment that contains VID_/PID_ info
            foreach (var part in devicePath.Split('#')) {
                if (part.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    part.IndexOf("PID_", StringComparison.OrdinalIgnoreCase) >= 0)
                    return part;
            }
            return devicePath;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                               ref bool handled) {
            if (msg == WM_INPUT)
                ProcessRawInput(lParam);
            return IntPtr.Zero;
        }

        private void ProcessRawInput(IntPtr hRawInput) {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            var buf = Marshal.AllocHGlobal((int)size);
            try {
                if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, headerSize) != size)
                    return;

                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (header.dwType != RIM_TYPEHID) return;

                int rawHidOffset = (int)headerSize;
                var rawHid = Marshal.PtrToStructure<RAWHID>(buf + rawHidOffset);
                if (rawHid.dwCount == 0 || rawHid.dwSizeHid == 0) return;

                int reportOffset = rawHidOffset + Marshal.SizeOf<RAWHID>();
                int reportLen = (int)(rawHid.dwSizeHid * rawHid.dwCount);
                var report = new byte[reportLen];
                Marshal.Copy(buf + reportOffset, report, 0, reportLen);

                string devicePath = GetDevicePath(header.hDevice);

                if (m_isRecording) {
                    HandleRecording(devicePath, report, header.hDevice);
                } else if (m_binding != null && m_binding.IsValid &&
                           devicePath.Equals(m_binding.DevicePath,
                               StringComparison.OrdinalIgnoreCase)) {
                    HandleTrigger(report);
                }
            } finally {
                Marshal.FreeHGlobal(buf);
            }
        }

        private void HandleRecording(string devicePath, byte[] report, IntPtr hDevice) {
            if (!m_deviceStates.TryGetValue(devicePath, out var ds)) {
                ds = new DeviceRecordingState {
                    Baseline = (byte[])report.Clone(),
                    ChangeMask = new byte[report.Length],
                    Samples = 1,
                };
                m_deviceStates[devicePath] = ds;
                return;
            }

            if (ds.Samples < BASELINE_SAMPLE_COUNT) {
                // Grow arrays if report length changed (shouldn't happen, but be safe)
                if (report.Length != ds.Baseline.Length) {
                    ds.Baseline = (byte[])report.Clone();
                    ds.ChangeMask = new byte[report.Length];
                } else {
                    for (int i = 0; i < report.Length; i++) {
                        ds.ChangeMask[i] |= (byte)(ds.Baseline[i] ^ report[i]);
                        ds.Baseline[i] |= report[i];
                    }
                }
                ds.Samples++;
                return;
            }

            // Baseline established for this device — look for a newly-set bit
            // in a byte that was stable during baseline (stable byte = button byte).
            for (int b = 0; b < report.Length; b++) {
                if (b < ds.ChangeMask.Length && ds.ChangeMask[b] != 0) continue;
                byte restMask = b < ds.Baseline.Length ? ds.Baseline[b] : (byte)0;
                byte newBits = (byte)(report[b] & ~restMask);
                if (newBits == 0) continue;
                for (int bit = 0; bit < 8; bit++) {
                    if ((newBits & (1 << bit)) == 0) continue;
                    var binding = new HidBinding {
                        DevicePath = devicePath,
                        DeviceFriendlyName = FriendlyNameFromPath(devicePath),
                        ReportBitIndex = b * 8 + bit,
                    };
                    m_isRecording = false;
                    m_binding = binding;
                    m_lastReport = null;
                    RecordingComplete?.Invoke(binding);
                    return;
                }
            }
        }

        private void HandleTrigger(byte[] report) {
            if (m_binding == null) return;
            int byteIdx = m_binding.ReportBitIndex / 8;
            int bitIdx = m_binding.ReportBitIndex % 8;
            if (byteIdx >= report.Length) return;

            bool isPressed = (report[byteIdx] & (1 << bitIdx)) != 0;
            bool wasPressed = m_lastReport != null && byteIdx < m_lastReport.Length
                              && (m_lastReport[byteIdx] & (1 << bitIdx)) != 0;

            if (isPressed && !wasPressed)
                BindingTriggered?.Invoke();

            m_lastReport = report;
        }

        public void Dispose() {
            m_hwndSource?.RemoveHook(WndProc);
        }
    }
}
