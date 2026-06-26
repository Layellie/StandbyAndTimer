namespace StandbyAndTimer.Core.Models;

public sealed class AppSettings
{
    public int         StandbyLimitMb        { get; set; } = 1024;
    public int         FreeLimitMb           { get; set; } = 1024;
    public bool        GameModeEnabled       { get; set; }
    public bool        AutoStartEnabled      { get; set; }
    public bool        TimerResolutionActive { get; set; }
    public Language    Language              { get; set; } = Language.English;
    public Theme       Theme                 { get; set; } = Theme.Dark;
    public bool        UpdateCheckEnabled    { get; set; } = true;
    public List<GameEntry> Games             { get; set; } = [];
}
