namespace StandbyAndTimer.ViewModels;

public sealed partial class MaintenanceViewModel
{
    /// <summary>"845 MB", "1.2 GB", "&lt; 1 MB", "0 MB".</summary>
    internal static string SizeText(long bytes) =>
        bytes > 0 && bytes < 1024 * 1024 ? "< 1 MB" : MainViewModel.FormatSize(bytes / (1024 * 1024));
}
