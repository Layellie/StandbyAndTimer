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
    public List<GameEntry> Games             { get; set; } = [];
}
