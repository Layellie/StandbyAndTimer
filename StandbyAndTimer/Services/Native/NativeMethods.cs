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

    /// <summary>
    /// SystemInformationClass 5 = SystemProcessInformation: a chain of
    /// SYSTEM_PROCESS_INFORMATION entries (winternl.h). Returns
    /// STATUS_INFO_LENGTH_MISMATCH when the buffer is too small.
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int     SystemInformationClass,
        IntPtr  SystemInformation,
        int     SystemInformationLength,
        out int ReturnLength);

    internal const int SystemProcessInformation    = 5;
    internal const int SystemMemoryListInformation = 80;   // query: SYSTEM_MEMORY_LIST_INFORMATION
    internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

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

    /// <summary>
    /// With (-1, -1): removes as many pages as possible from the process's
    /// working set (they move to the standby / modified lists and fault back
    /// in on use). Used once when the window goes to the tray.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// (SIZE_T)-1 for both sizes empties the system file cache's working set
    /// (its pages move to the standby list). Needs SeIncreaseQuotaPrivilege.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetSystemFileCacheSize(nint MinimumFileCacheSize, nint MaximumFileCacheSize, uint Flags);

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

    /// <summary>Window the user is currently interacting with — used by game auto-detect.</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Pulls a window to the top and gives it keyboard focus. Subject to
    /// foreground-lock rules — only works when the calling process already
    /// has foreground rights or was granted them by another process via
    /// <see cref="AllowSetForegroundWindow"/>.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Allows another process to call <see cref="SetForegroundWindow"/>.
    /// Used by the secondary single-instance launcher: before signaling the
    /// primary process to surface itself, we grant ASFW_ANY so the primary
    /// can call SetForegroundWindow without being silently rejected by the
    /// foreground-lock timeout.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint dwProcessId);

    internal const uint ASFW_ANY = 0xFFFFFFFFu;

    /// <summary>
    /// Registers a system-wide hotkey. <paramref name="id"/> is delivered as
    /// wParam in <see cref="WM_HOTKEY"/>. Returns false if the combo is taken
    /// by another app — caller should log the Win32 error code.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>Releases a hotkey previously registered with <see cref="RegisterHotKey"/>.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Fills <paramref name="plii"/> with the system tick count of the most recent input event.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // RegisterHotKey modifier flags (winuser.h).
    internal const uint MOD_ALT      = 0x0001;
    internal const uint MOD_CONTROL  = 0x0002;
    internal const uint MOD_SHIFT    = 0x0004;
    internal const uint MOD_WIN      = 0x0008;

    /// <summary>
    /// Suppresses auto-repeat when the user holds the chord — without this,
    /// holding Ctrl+Alt+P would fire the purge handler tens of times per
    /// second until they let go.
    /// </summary>
    internal const uint MOD_NOREPEAT = 0x4000;

    /// <summary>Window message id for hotkey events.</summary>
    internal const int WM_HOTKEY     = 0x0312;

    /// <summary>Returns the bounding rectangle of a window in screen coordinates.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>Copies what DWM shows for a window into a DC (with PW_RENDERFULLCONTENT also for DirectX content), even while it is covered or cloaked.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

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

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11+); value is a DWM_WINDOW_CORNER_PREFERENCE.</summary>
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>DWMWA_CLOAK (Windows 8+): value 1 keeps a shown window off the screen until set back to 0.</summary>
    internal const int DWMWA_CLOAK = 13;

    /// <summary>DWMWCP_ROUND — the standard Windows 11 rounded window corners.</summary>
    internal const int DWMWCP_ROUND = 2;

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

    /// <summary>A display's mode; device null = the primary display, mode ENUM_CURRENT_SETTINGS = the current one.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    internal const int ENUM_CURRENT_SETTINGS = -1;

    // ── shell32.dll ──────────────────────────────────────────────────────────

    /// <summary>Size and item count of the Recycle Bin (pszRootPath null = all drives).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>Empties the Recycle Bin (pszRootPath null = all drives).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    internal const uint SHERB_NOCONFIRMATION = 0x1;
    internal const uint SHERB_NOPROGRESSUI   = 0x2;
    internal const uint SHERB_NOSOUND        = 0x4;

    /// <summary>Shell file operation; with FO_DELETE + FOF_ALLOWUNDO it moves to the Recycle Bin.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    internal const uint   FO_DELETE           = 0x3;
    internal const ushort FOF_SILENT          = 0x0004;
    internal const ushort FOF_NOCONFIRMATION  = 0x0010;
    internal const ushort FOF_ALLOWUNDO       = 0x0040;
    internal const ushort FOF_NOERRORUI       = 0x0400;
    // Asks before an item that is too big for the Recycle Bin would be
    // deleted for good: never delete a user's file permanently in silence.
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Known folder path (Downloads also when the user moved it). Free with CoTaskMemFree.</summary>
    [DllImport("shell32.dll")]
    internal static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags,
                                                    IntPtr hToken, out IntPtr ppszPath);

    internal static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    // ── ole32.dll ────────────────────────────────────────────────────────────

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);

    // ── powrprof.dll ─────────────────────────────────────────────────────────

    /// <summary>Active power scheme GUID; the returned pointer is freed with LocalFree.</summary>
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    /// <summary>Friendly name of a scheme (UTF-16, size in bytes). Buffer null = size query.</summary>
    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
                                                      IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid,
                                                      IntPtr buffer, ref uint bufferSize);

    /// <summary>Windows 10/11 power mode overlay now in effect (Settings → Power mode).</summary>
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>AC / battery state; BatteryFlag 128 = no system battery.</summary>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    /// <summary>NtQuerySystemInformation class: a chain of SYSTEM_PAGEFILE_INFORMATION entries.</summary>
    internal const int SystemPageFileInformation = 18;

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

    /// <summary>
    /// Out parameter for <see cref="GetLastInputInfo"/>. <c>dwTime</c> is the
    /// system tick count (millisecond wall clock that wraps every ~24.8 days)
    /// of the last input event reaching the input queue.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        internal uint cbSize;
        internal uint dwTime;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        internal byte ACLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal int  BatteryLifeTime;
        internal int  BatteryFullLifeTime;
    }

    /// <summary>DEVMODEW, display variant of the union (220 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string dmDeviceName;
        internal short dmSpecVersion;
        internal short dmDriverVersion;
        internal short dmSize;
        internal short dmDriverExtra;
        internal int   dmFields;
        internal int   dmPositionX;
        internal int   dmPositionY;
        internal int   dmDisplayOrientation;
        internal int   dmDisplayFixedOutput;
        internal short dmColor;
        internal short dmDuplex;
        internal short dmYResolution;
        internal short dmTTOption;
        internal short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string dmFormName;
        internal short dmLogPixels;
        internal int   dmBitsPerPel;
        internal int   dmPelsWidth;
        internal int   dmPelsHeight;
        internal int   dmDisplayFlags;
        internal int   dmDisplayFrequency;
        internal int   dmICMMethod;
        internal int   dmICMIntent;
        internal int   dmMediaType;
        internal int   dmDitherType;
        internal int   dmReserved1;
        internal int   dmReserved2;
        internal int   dmPanningWidth;
        internal int   dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO
    {
        internal int  cbSize;
        internal long i64Size;
        internal long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        internal IntPtr hwnd;
        internal uint   wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] internal string  pFrom;   // double-null terminated
        [MarshalAs(UnmanagedType.LPWStr)] internal string? pTo;
        internal ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] internal bool fAnyOperationsAborted;
        internal IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszProgressTitle;
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
