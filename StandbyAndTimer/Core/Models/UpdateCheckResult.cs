namespace StandbyAndTimer.Core.Models;

public sealed record UpdateCheckResult(
    bool   IsNewer,
    string LatestVersion,
    string ReleaseUrl,
    string AssetDownloadUrl,
    // Lower-case hex SHA-256 of the installer, parsed from the release body
    // ("SHA256: <64 hex>") or fetched from a `*.exe.sha256` companion asset.
    // Empty if the release does not publish one — in that case the update flow
    // proceeds with only TLS-level integrity from the GitHub download.
    string ExpectedSha256)
{
    public static UpdateCheckResult None() => new(false, string.Empty, string.Empty, string.Empty, string.Empty);
}
