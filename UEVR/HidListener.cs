using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UEVR {
    public class HidBinding {
        public string DevicePath { get; set; } = "";
        public string DeviceFriendlyName { get; set; } = "";
        public int ButtonUsage { get; set; } = -1;  // HID button usage (1-based)

        public bool IsValid => !string.IsNullOrEmpty(DevicePath) && ButtonUsage >= 1;

        public override string ToString() {
            if (!IsValid) return "None";
            return $"{DeviceFriendlyName} (btn {ButtonUsage})";
        }
    }

    public class HidListener : IDisposable {
        private const int WM_INPUT = 0x00FF;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIM_TYPEHID = 2;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDI_PREPARSEDDATA = 0x20000005;
        private const uint RID_INPUT = 0x10000003;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;
        private const int HidP_Input = 0;
        private const ushort HID_USAGE_PAGE_BUTTON = 0x09;
        private const int MAX_BUTTONS = 128;

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

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST {
            public IntPtr hDevice;
            public uint dwType;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(
            [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] devices,
            uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputDeviceList(
            IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetRawInputData(
            IntPtr hRawInput, uint uiCommand, IntPtr pData,
            ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
        static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = false)]
        static extern bool HidD_GetProductString(
            IntPtr hidDevice, IntPtr buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = false)]
        static extern int HidP_GetUsages(
            int reportType,
            ushort usagePage,
            ushort linkCollection,
            [Out] ushort[] usageList,
            ref uint usageLength,
            IntPtr preparsedData,
            IntPtr report,
            uint reportLength);

        private HwndSource? m_hwndSource;
        private HidBinding? m_binding;

        // Preparsed HID data cache — keyed by device path, freed in Dispose
        private readonly Dictionary<string, IntPtr> m_preparsedCache = new(StringComparer.OrdinalIgnoreCase);

        // Recording state — per device, cleared on StartRecording
        private class DeviceRecordingState {
            public HashSet<ushort>? PrevUsages;  // null until first report establishes initial state
        }
        private bool m_isRecording = false;
        private readonly Dictionary<string, DeviceRecordingState> m_deviceStates = new(StringComparer.OrdinalIgnoreCase);

        // Shared scratch buffer for HidP_GetUsages (UI thread only)
        private readonly ushort[] m_usageScratch = new ushort[MAX_BUTTONS];

        // Trigger state: set of button usages pressed in the previous report
        private readonly HashSet<ushort> m_lastUsages = new();

        public event Action? BindingTriggered;
        public event Action<HidBinding>? RecordingComplete;

        public void Initialize(IntPtr hwnd) {
            m_hwndSource = HwndSource.FromHwnd(hwnd);
            m_hwndSource?.AddHook(WndProc);
            RegisterDevices(hwnd);
        }

        private void RegisterDevices(IntPtr hwnd) {
            var devices = new RAWINPUTDEVICE[] {
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            };
            RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }

        public void SetBinding(HidBinding? binding) {
            m_binding = binding;
            m_lastUsages.Clear();
            if (binding != null && binding.IsValid)
                PrimePreparsedCache(binding.DevicePath);
        }

        // Must enumerate at bind time: GetRawInputDeviceInfo(RIDI_PREPARSEDDATA) silently returns size=0 in background WM_INPUT context.
        private void PrimePreparsedCache(string devicePath) {
            if (m_preparsedCache.ContainsKey(devicePath)) return;

            uint count = 0;
            int itemSize = Marshal.SizeOf<RAWINPUTDEVICELIST>();
            GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)itemSize);
            if (count == 0) return;

            var buf = Marshal.AllocHGlobal((int)count * itemSize);
            try {
                if (GetRawInputDeviceList(buf, ref count, (uint)itemSize) == uint.MaxValue) return;
                for (uint i = 0; i < count; i++) {
                    var item = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + (int)(i * itemSize));
                    if (GetDevicePath(item.hDevice).Equals(devicePath, StringComparison.OrdinalIgnoreCase)) {
                        GetOrFetchPreparsedData(devicePath, item.hDevice);
                        return;
                    }
                }
            } finally {
                Marshal.FreeHGlobal(buf);
            }
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

        private IntPtr GetOrFetchPreparsedData(string devicePath, IntPtr hDevice) {
            if (m_preparsedCache.TryGetValue(devicePath, out var cached)) return cached;
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size == 0) return IntPtr.Zero;
            var buf = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, buf, ref size) == uint.MaxValue) {
                Marshal.FreeHGlobal(buf);
                return IntPtr.Zero;
            }
            m_preparsedCache[devicePath] = buf;
            return buf;
        }

        private static string FriendlyNameFromPath(string devicePath) {
            const uint FILE_SHARE_READ  = 0x00000001;
            const uint FILE_SHARE_WRITE = 0x00000002;
            const uint OPEN_EXISTING    = 3;
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

            foreach (var part in devicePath.Split('#')) {
                if (part.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    part.IndexOf("PID_", StringComparison.OrdinalIgnoreCase) >= 0)
                    return part;
            }
            return devicePath;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == WM_INPUT) ProcessRawInput(lParam);
            return IntPtr.Zero;
        }

        private void ProcessRawInput(IntPtr hRawInput) {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            var buf = Marshal.AllocHGlobal((int)size);
            try {
                if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, headerSize) != size) return;

                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (header.dwType != RIM_TYPEHID) return;

                int rawHidOffset = (int)headerSize;
                var rawHid = Marshal.PtrToStructure<RAWHID>(buf + rawHidOffset);
                if (rawHid.dwCount == 0 || rawHid.dwSizeHid == 0) return;

                IntPtr reportsPtr = buf + rawHidOffset + Marshal.SizeOf<RAWHID>();
                string devicePath = GetDevicePath(header.hDevice);

                if (m_isRecording) {
                    HandleRecording(devicePath, reportsPtr, rawHid.dwSizeHid, rawHid.dwCount, header.hDevice);
                } else if (m_binding != null && m_binding.IsValid &&
                           devicePath.Equals(m_binding.DevicePath, StringComparison.OrdinalIgnoreCase)) {
                    HandleTrigger(reportsPtr, rawHid.dwSizeHid, rawHid.dwCount, header.hDevice);
                }
            } finally {
                Marshal.FreeHGlobal(buf);
            }
        }

        private int GetPressedButtons(IntPtr preparsed, IntPtr reportPtr, uint reportLength) {
            uint count = MAX_BUTTONS;
            int status = HidP_GetUsages(HidP_Input, HID_USAGE_PAGE_BUTTON, 0,
                m_usageScratch, ref count, preparsed, reportPtr, reportLength);
            return status == HIDP_STATUS_SUCCESS ? (int)count : 0;
        }

        private void HandleRecording(string devicePath, IntPtr reportsPtr, uint reportSize,
                                     uint reportCount, IntPtr hDevice) {
            var preparsed = GetOrFetchPreparsedData(devicePath, hDevice);
            if (preparsed == IntPtr.Zero) return;

            if (!m_deviceStates.TryGetValue(devicePath, out var ds)) {
                ds = new DeviceRecordingState();
                m_deviceStates[devicePath] = ds;
            }

            // Merge pressed buttons from all sub-reports into a single set
            var current = new HashSet<ushort>();
            for (uint r = 0; r < reportCount; r++) {
                int count = GetPressedButtons(preparsed, reportsPtr + (int)(r * reportSize), reportSize);
                for (int i = 0; i < count; i++) current.Add(m_usageScratch[i]);
            }

            if (ds.PrevUsages == null) {
                // First report: snapshot the initial state (captures anything already held)
                ds.PrevUsages = current;
                return;
            }

            // Bind on the first rising edge — a button now pressed that wasn't before
            ushort newUsage = 0;
            foreach (var usage in current) {
                if (!ds.PrevUsages.Contains(usage)) { newUsage = usage; break; }
            }

            ds.PrevUsages = current;

            if (newUsage == 0) return;

            m_isRecording = false;
            m_binding = new HidBinding {
                DevicePath = devicePath,
                DeviceFriendlyName = FriendlyNameFromPath(devicePath),
                ButtonUsage = newUsage,
            };
            m_lastUsages.Clear();
            RecordingComplete?.Invoke(m_binding);
        }

        private void HandleTrigger(IntPtr reportsPtr, uint reportSize, uint reportCount, IntPtr hDevice) {
            if (m_binding == null) return;
            var preparsed = GetOrFetchPreparsedData(m_binding.DevicePath, hDevice);
            if (preparsed == IntPtr.Zero) return;

            var targetUsage = (ushort)m_binding.ButtonUsage;
            bool wasPressed = m_lastUsages.Contains(targetUsage);
            bool isPressed = false;

            m_lastUsages.Clear();
            for (uint r = 0; r < reportCount; r++) {
                int count = GetPressedButtons(preparsed, reportsPtr + (int)(r * reportSize), reportSize);
                for (int i = 0; i < count; i++) {
                    m_lastUsages.Add(m_usageScratch[i]);
                    if (m_usageScratch[i] == targetUsage) isPressed = true;
                }
            }

            if (isPressed && !wasPressed) BindingTriggered?.Invoke();
        }

        public void Dispose() {
            m_hwndSource?.RemoveHook(WndProc);
            foreach (var ptr in m_preparsedCache.Values)
                if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }
}
