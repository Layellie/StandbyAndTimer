namespace StandbyAndTimer.Core.Models;

public sealed class GameEntry
{
    public string DisplayName    { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>True when fullscreen detection added it (shown with an "AUTO" tag); false for user-added.</summary>
    public bool   AutoAdded      { get; init; }

    public override string ToString() => DisplayName;
}
