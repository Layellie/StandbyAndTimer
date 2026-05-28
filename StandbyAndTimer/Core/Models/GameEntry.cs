namespace StandbyAndTimer.Core.Models;

public sealed class GameEntry
{
    public string DisplayName    { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}
