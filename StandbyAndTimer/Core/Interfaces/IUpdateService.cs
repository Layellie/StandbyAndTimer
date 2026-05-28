using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);

    Task DownloadAsync(
        string             assetUrl,
        string             targetPath,
        IProgress<double>? progress = null,
        CancellationToken  ct       = default);
}
