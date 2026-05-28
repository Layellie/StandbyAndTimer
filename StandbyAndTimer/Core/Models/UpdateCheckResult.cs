namespace StandbyAndTimer.Core.Models;

public sealed record UpdateCheckResult(
    bool   IsNewer,
    string LatestVersion,
    string ReleaseUrl,
    string AssetDownloadUrl)
{
    public static UpdateCheckResult None() => new(false, string.Empty, string.Empty, string.Empty);
}
