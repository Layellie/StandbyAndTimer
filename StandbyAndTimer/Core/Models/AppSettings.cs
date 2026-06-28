namespace StandbyAndTimer.Core.Models;

public sealed class AppSettings
{
    public int         StandbyLimitMb        { get; set; }
    public int         FreeLimitMb           { get; set; }
    public bool        AutoPurgeEnabled      { get; set; }
    public bool        GameModeEnabled       { get; set; }
    public bool        AutoStartEnabled      { get; set; }
    public bool        TimerResolutionActive { get; set; }
    public Language    Language              { get; set; } = Language.English;
    public Theme       Theme                 { get; set; } = Theme.Dark;
    public bool        UpdateCheckEnabled    { get; set; } = true;
    // True until the user dismisses the first-run wizard. Defaulting to true
    // means *every* fresh install gets the onboarding overlay; the wizard's
    // Finish handler sets this to false + persists so it never reappears.
    public bool        FirstRunCompleted     { get; set; }

    // v2.1.0 — notification preferences. Default to true so the upgrade path
    // for existing users keeps balloons firing; opt-out happens in Settings.
    public bool        NotifyOnPurge         { get; set; } = true;
    public bool        NotifyOnTimerToggle   { get; set; } = true;
    public bool        NotifyOnGameDetected  { get; set; } = true;

    // v2.1.0 — idle gate for auto-purge. When the flag is on, the monitor
    // only fires a threshold-triggered purge once the user has been idle for
    // at least IdleThresholdMinutes, so gaming / typing isn't interrupted.
    public bool        AutoPurgeIdleOnly     { get; set; }
    public int         IdleThresholdMinutes  { get; set; } = 5;

    // v2.1.0 — global hotkeys for manual purge / timer toggle. Stored as
    // "Ctrl+Alt+P"-style strings so the registry value is human-readable and
    // a future capture-textbox can round-trip via HotkeyBinding.Parse.
    public string      PurgeHotkey           { get; set; } = "Ctrl+Alt+P";
    public string      TimerHotkey           { get; set; } = "Ctrl+Alt+T";

    public List<GameEntry> Games             { get; set; } = [];
}
