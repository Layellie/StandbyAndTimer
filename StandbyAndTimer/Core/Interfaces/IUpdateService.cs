using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the installer to <paramref name="targetPath"/>. If
    /// <paramref name="expectedSha256"/> is non-empty, the file's SHA-256 is
    /// computed during streaming and compared; on mismatch the file is deleted
    /// and an <see cref="InvalidDataException"/> is thrown.
    /// </summary>
    Task DownloadAsync(
        string             assetUrl,
        string             targetPath,
        string             expectedSha256 = "",
        IProgress<double>? progress       = null,
        CancellationToken  ct             = default);
}
