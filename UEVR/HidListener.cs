using System;
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

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize);

        private HwndSource? m_hwndSource;
        private HidBinding? m_binding;

        // Recording state
        private bool m_isRecording = false;
        private byte[]? m_recordingBaseline = null;
        private int m_baselineSamples = 0;
        private const int BASELINE_SAMPLE_COUNT = 5;

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
            m_recordingBaseline = null;
            m_baselineSamples = 0;
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
            // Path looks like \\?\HID#VID_046D&PID_C21F&...
            // Return the HID-ID segment as a human-readable label.
            try {
                var parts = devicePath.Split('#');
                if (parts.Length >= 2) return parts[1];
            } catch { }
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
            // Accumulate baseline so at-rest axis bytes don't falsely register as buttons
            if (m_baselineSamples < BASELINE_SAMPLE_COUNT) {
                if (m_recordingBaseline == null || m_recordingBaseline.Length != report.Length)
                    m_recordingBaseline = new byte[report.Length];
                for (int i = 0; i < report.Length; i++)
                    m_recordingBaseline[i] |= report[i]; // OR-accumulate high/rest bits
                m_baselineSamples++;
                return;
            }

            // Look for a bit set in the current report that was never set at rest
            for (int b = 0; b < report.Length; b++) {
                byte restMask = b < m_recordingBaseline!.Length ? m_recordingBaseline[b] : (byte)0;
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
