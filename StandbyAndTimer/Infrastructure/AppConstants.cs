namespace StandbyAndTimer.Infrastructure;

// Central home for cross-cutting constants that name persistent OS objects —
// mutex, scheduled task, registry hive, MMCSS task. These were previously
// scattered as string literals across App.xaml.cs, AutoStartService.cs,
// SettingsService.cs, TimerResolutionService.cs. A drift (e.g. someone
// renaming the mutex in one file) would silently break single-instance
// detection or settings persistence, so they live here as a single source
// of truth and every call site references the constant.
internal static class AppConstants
{
    // Global\ prefix so a second instance launched in another user session
    // (RDP / fast-user-switch) is still detected. _v1 suffix lets us bump
    // the name if we ever ship a breaking IPC change.
    internal const string SingleInstanceMutexName = @"Global\StandbyAndTimer_v1";

    // Named auto-reset EventWaitHandle the primary instance listens on. A
    // second launch opens it by name and pulses Set() to ask the primary
    // instance to surface its main window, instead of showing a "already
    // running" dialog. Same Global\ scope as the mutex so cross-session
    // launches reach the right process.
    internal const string ShowWindowSignalName = @"Global\StandbyAndTimer_ShowWindow_v1";

    // schtasks /tn name — used by AutoStartService for both create and
    // delete. Must match exactly; a typo would orphan the scheduled task.
    internal const string AutoStartTaskName = "StandbyAndTimer_AutoStart";

    // HKCU subkey for user settings. SettingsService creates / opens this
    // path on every Load and Save.
    internal const string SettingsRegistryPath = @"SOFTWARE\StandbyAndTimer";

    // AVRT MMCSS task name. "Pro Audio" gives the watchdog the highest
    // scheduling class Windows offers and exempts it from Win 11 EcoQoS,
    // which is what keeps the timer-resolution loop from being throttled
    // mid-game.
    internal const string MmcssProAudioTaskName = "Pro Audio";
}
