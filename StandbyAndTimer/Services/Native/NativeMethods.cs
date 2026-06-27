using System.Runtime.InteropServices;

namespace StandbyAndTimer.Services.Native;

/// <summary>
/// All P/Invoke declarations live here — nowhere else in the codebase.
/// Every entry point is internal so nothing leaks outside the Services layer.
/// </summary>
internal static class NativeMethods
{
    // ── ntdll.dll ────────────────────────────────────────────────────────────

    [DllImport("ntdll.dll", SetLastError = true)]
    internal static extern int NtSetTimerResolution(
        uint DesiredResolution,
        [MarshalAs(UnmanagedType.Bool)] bool SetResolution,
        out uint CurrentResolution);

    /// <summary>Queries the current, minimum, and maximum timer resolution (100-ns units).</summary>
    [DllImport("ntdll.dll", SetLastError = true)]
    internal static extern int NtQueryTimerResolution(
        out uint MinimumResolution,
        out uint MaximumResolution,
        out uint CurrentResolution);

    /// <summary>
    /// SystemInformationClass 80 = SystemMemoryListInformation.
    /// Write value 4 (MemoryPurgeStandbyList) to flush the standby cache.
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtSetSystemInformation(
        int    SystemInformationClass,
        IntPtr SystemInformation,
        int    SystemInformationLength);

    // ── kernel32.dll ─────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Pass <see cref="ProcessPowerThrottling"/> (4) for processInformationClass with
    /// PROCESS_POWER_THROTTLING_STATE (12 bytes) to disable EcoQoS / timer throttling.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetProcessInformation(
        IntPtr hProcess,
        int    processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        int    processInformationSize);

    /// <summary>Prevents the system from sleeping while the app is running.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>Closes a kernel object handle (process token, file, etc.).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Restricts where LoadLibrary searches for DLLs — used at process start
    /// to prevent DLL planting attacks (e.g. a malicious version.dll dropped
    /// next to our admin-elevated exe).
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

    // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = AppDir | UserDirs | System32.
    // Crucially excludes the legacy "current working directory" lookup.
    internal const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    // ── avrt.dll ─────────────────────────────────────────────────────────────

    /// <summary>Registers this thread as a real-time multimedia task ("Pro Audio").</summary>
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr AvSetMmThreadCharacteristics(
        [MarshalAs(UnmanagedType.LPWStr)] string TaskName,
        ref uint TaskIndex);

    /// <summary>Reverts the thread characteristic set by AvSetMmThreadCharacteristics.</summary>
    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AvRevertMmThreadCharacteristics(IntPtr AvrtHandle);

    // ── user32.dll ───────────────────────────────────────────────────────────

    /// <summary>Releases an HICON returned by Bitmap.GetHicon / CreateIcon.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Window the user is currently interacting with — used by game auto-detect.</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>Returns the bounding rectangle of a window in screen coordinates.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>Identifies the process that owns a window.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Looks up the monitor that contains a window — needed to compare against the right resolution on multi-monitor setups.</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>Reads monitor geometry; we need rcMonitor to know whether a window covers the full screen.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // ── dwmapi.dll ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sets a DWM window attribute.
    /// Use attr=20 (DWMWA_USE_IMMERSIVE_DARK_MODE) with value=1 to force a dark title bar.
    /// </summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int    attr,
        ref int attrValue,
        int    attrSize);

    // ── advapi32.dll ─────────────────────────────────────────────────────────

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr h, int acc, ref IntPtr phtok);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(
        [MarshalAs(UnmanagedType.LPWStr)] string? host,
        [MarshalAs(UnmanagedType.LPWStr)] string  name,
        ref long pluid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern bool AdjustTokenPrivileges(
        IntPtr htok,
        [MarshalAs(UnmanagedType.Bool)] bool disall,
        ref TOKEN_PRIVILEGES newst,
        int    len,
        IntPtr prev,
        IntPtr relen);

    // ── Structures ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        internal uint  dwLength;
        internal uint  dwMemoryLoad;
        internal ulong ullTotalPhys;
        internal ulong ullAvailPhys;
        internal ulong ullTotalPageFile;
        internal ulong ullAvailPageFile;
        internal ulong ullTotalVirtual;
        internal ulong ullAvailVirtual;
        internal ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MONITORINFO
    {
        internal int  cbSize;
        internal RECT rcMonitor;
        internal RECT rcWork;
        internal uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TOKEN_PRIVILEGES
    {
        internal int  PrivilegeCount;
        internal long Luid;
        internal int  Attributes;
    }

    /// <summary>
    /// Passed to SetProcessInformation with processInformationClass = <see cref="ProcessPowerThrottling"/> (4). Version=1.
    /// ControlMask = OR of bits this process wants to override (EXECUTION_SPEED,
    /// IGNORE_TIMER_RESOLUTION). For each controlled bit, StateMask=0 disables
    /// that throttle, bit set enables it. Bits absent from ControlMask follow
    /// the OS default.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    internal const uint TARGET_TIMER_RESOLUTION = 5000;   // 0.5 ms in 100-ns units
    internal const uint ES_CONTINUOUS           = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED      = 0x00000001;

    // PROCESS_INFORMATION_CLASS enum value for ProcessPowerThrottling (winnt.h).
    // Used as the processInformationClass arg to SetProcessInformation. A prior
    // typo used 34 here — that value matches no class, so SetProcessInformation
    // silently returned ERROR_INVALID_PARAMETER and the throttling state was
    // never applied. Keep callers using this constant, never a literal.
    internal const int  ProcessPowerThrottling = 4;

    // PROCESS_POWER_THROTTLING_STATE flags (winnt.h)
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED         = 0x1;
    // Win11+: when ControlMask has this bit AND StateMask=0, the OS honors
    // NtSetTimerResolution even while this process is in the background.
    internal const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;
}
