using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the installer to <paramref name="targetPath"/> and checks
    /// it against <paramref name="expectedSha256"/> while streaming. No hash
    /// (nothing to verify against) or a mismatch throws
    /// <see cref="InvalidDataException"/>; a mismatching file is deleted.
    /// </summary>
    Task DownloadAsync(
        string             assetUrl,
        string             targetPath,
        string             expectedSha256,
        IProgress<double>? progress       = null,
        CancellationToken  ct             = default);
}
