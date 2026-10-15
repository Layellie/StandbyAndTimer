using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IDownloadsService
{
    /// <summary>The user's Downloads folder (also when moved), or null if it does not exist.</summary>
    string? FolderPath { get; }

    /// <summary>Total size of what <see cref="ListAsync"/> would list.</summary>
    Task<CleanupScan> ScanAsync(CancellationToken ct);

    /// <summary>Top-level files and folders; links and hidden system files are left out.</summary>
    Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct);

    /// <summary>Sends the entries to the Recycle Bin; returns the ones that could not be moved.</summary>
    Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries);
}
