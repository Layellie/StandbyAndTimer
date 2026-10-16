using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.Services.Maintenance;

namespace StandbyAndTimer.ViewModels;

/// <summary>One cleanup row: tick, name, size (or state), result of the last clean.</summary>
public sealed partial class CleanupRowViewModel : ObservableObject
{
    public CleanupRowViewModel(ICleanupTarget target) => Target = target;

    public ICleanupTarget Target { get; }
    public string Id => Target.Id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _tip  = "";
    [ObservableProperty] private bool   _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClean))]
    private CleanupScan? _scan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClean))]
    private bool _isBlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _sizeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _resultText = "";

    /// <summary>What the last clean of this row did (null: not cleaned, still cleaning, or failed).</summary>
    [ObservableProperty] private CleanupResult? _lastResult;

    /// <summary>The last clean's result until the next plain scan, else the size.</summary>
    public string DisplayText => ResultText.Length > 0 ? ResultText : SizeText;

    public bool CanClean => Scan is { Found: true } && !IsBlocked;
}

/// <summary>
/// The shader caches as one row (spec §10.1). The caches stay rows of their
/// own (ids, ticks, cleaning); this only groups them. The tick ticks every
/// cache that can be cleaned or none; some ticked shows as null.
/// MaintenanceViewModel keeps Name, Tip and DisplayText current.
/// </summary>
public sealed partial class ShaderGroupViewModel : ObservableObject
{
    public ShaderGroupViewModel(IReadOnlyList<CleanupRowViewModel> caches)
    {
        Caches = caches;
        foreach (var cache in caches)
            cache.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CleanupRowViewModel.IsChecked) or nameof(CleanupRowViewModel.CanClean))
                {
                    OnPropertyChanged(nameof(IsChecked));
                    OnPropertyChanged(nameof(CanClean));
                    OnPropertyChanged(nameof(IsBlocked));
                }
            };
    }

    public IReadOnlyList<CleanupRowViewModel> Caches { get; }

    [ObservableProperty] private string _name        = "";
    [ObservableProperty] private string _tip         = "";
    [ObservableProperty] private string _displayText = "";

    public bool CanClean  => Caches.Any(c => c.CanClean);
    public bool IsBlocked => Caches.Any(c => c.IsBlocked);

    public bool? IsChecked
    {
        get
        {
            int cleanable = Caches.Count(c => c.CanClean);
            int ticked    = Caches.Count(c => c.CanClean && c.IsChecked);
            return ticked == 0 ? false : ticked == cleanable ? true : null;
        }
        set
        {
            foreach (var cache in Caches) cache.IsChecked = value == true && cache.CanClean;
        }
    }
}

public sealed record CleanupHistoryRow(CleanupRecord Record, string Time, string What, string Freed);

/// <summary>
/// The Maintenance tab. MainViewModel owns it: sets IsActive when the tab is
/// selected or left, persists SelectionText, says whether a game runs.
/// Nothing runs while the tab is not shown: scans start on selection (and
/// Rescan / after a clean) and are cancelled when the tab is left.
/// </summary>
public sealed partial class MaintenanceViewModel : ObservableObject, IDisposable
{
    private readonly ICleanupStatsStore _store;
    private readonly ISystemInfoService _info;
    private readonly ISystemToolLauncher _tools;
    private readonly IDownloadsService _downloads;
    private readonly ILocalizationService _loc;
    private CleanupStats? _stats;               // loaded on first use, not at start-up
    private CancellationTokenSource? _scanCts;
    private bool _initializing;
    private string? _rawSelection;

    public MaintenanceViewModel(IEnumerable<ICleanupTarget> targets, ICleanupStatsStore store, ISystemInfoService info,
                                ISystemToolLauncher tools, IDownloadsService downloads, ILocalizationService loc)
    {
        _store     = store;
        _info      = info;
        _tools     = tools;
        _downloads = downloads;
        _loc       = loc;
        foreach (var t in targets)
        {
            var row = new CleanupRowViewModel(t);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        // The shader caches show as one row where the first of them is.
        var shaderRows = Rows.Where(r => ShaderIds.Contains(r.Id)).ToList();
        if (shaderRows.Count > 0) Shaders = new ShaderGroupViewModel(shaderRows);
        foreach (var row in Rows)
        {
            if (!ShaderIds.Contains(row.Id))   DisplayRows.Add(row);
            else if (row == shaderRows[0])      DisplayRows.Add(Shaders!);
        }
        Relabel();
        _loc.LanguageChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(Relabel);
    }

    /// <summary>No targets, nothing on disk: tests and tools that don't need the tab.</summary>
    internal static MaintenanceViewModel Inert() =>
        new([], new InMemoryCleanupStatsStore(), new NoInfo(), new NoTools(), new NoDownloads(), new InertLocalization());

    public ObservableCollection<CleanupRowViewModel> Rows    { get; } = [];
    public ObservableCollection<CleanupHistoryRow>   History { get; } = [];

    private static readonly HashSet<string> ShaderIds =
        [CleanupTargetIds.NvidiaShader, CleanupTargetIds.AmdShader, CleanupTargetIds.DirectXShader, CleanupTargetIds.SteamShader];

    /// <summary>What the cleanup card lists: the shader group in place of the four shader rows.</summary>
    public ObservableCollection<object> DisplayRows { get; } = [];

    /// <summary>The shader caches row and its chooser (null without shader targets).</summary>
    public ShaderGroupViewModel? Shaders { get; }

    [ObservableProperty] private bool _isShadersVisible;

    [RelayCommand] private void OpenShaders()  => IsShadersVisible = true;
    [RelayCommand] private void CloseShaders() => IsShadersVisible = false;

    public event EventHandler? SettingsChanged;
    public event EventHandler<string>? StatusRequested;

    internal Func<bool>     AnyGameRunning { get; set; } = () => false;
    internal Func<DateTime> Now            { get; set; } = () => DateTime.Now;

    [ObservableProperty] private bool   _isActive;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    private bool _isCleaning;
    [ObservableProperty] private string _powerPlanText     = "";
    [ObservableProperty] private string _pageFileText      = "";
    [ObservableProperty] private string _hagsText            = "";
    [ObservableProperty] private string _recordingText       = "";
    [ObservableProperty] private string _memoryIntegrityText = "";
    [ObservableProperty] private string _gameModeText        = "";
    [ObservableProperty] private string _powerModeText       = "";
    [ObservableProperty] private string _downloadsSizeText = "";
    [ObservableProperty] private string _cleanButtonText   = "";
    [ObservableProperty] private long   _selectedBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairButtonText))]
    private bool _isRepairArmed;
    [ObservableProperty] private bool _isHistoryVisible;
    [ObservableProperty] private bool _isResetArmed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadsVisible))]
    private DownloadsViewModel? _downloadsDialog;

    public bool IsDownloadsVisible => DownloadsDialog is not null;

    public string RepairButtonText => _loc.GetString(IsRepairArmed ? "Str_Maint_RepairConfirm" : "Str_Maint_Repair");

    // ── Selection (persisted by MainViewModel as CleanupSelection) ───────────

    /// <summary>Comma-separated ticked ids. With no rows (inert), what Initialize got.</summary>
    public string? SelectionText => Rows.Count == 0 ? _rawSelection : string.Join(",", Rows.Where(r => r.IsChecked).Select(r => r.Id));

    public void Initialize(string? selection)
    {
        _rawSelection = selection;
        var ids = selection is null
            ? CleanupTargetIds.DefaultSelection.ToHashSet()
            : selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        _initializing = true;
        try { foreach (var r in Rows) r.IsChecked = ids.Contains(r.Id); }
        finally { _initializing = false; }
        UpdateSelectedTotal();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupRowViewModel.IsChecked) or nameof(CleanupRowViewModel.CanClean) or nameof(CleanupRowViewModel.Scan))
            UpdateSelectedTotal();
        if (e.PropertyName == nameof(CleanupRowViewModel.IsChecked) && !_initializing)
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (sender is CleanupRowViewModel row && ShaderIds.Contains(row.Id))
            UpdateShaderGroup();
    }

    // The group row's text: cleaning → failure → the summed result → scanning →
    // the ticked caches' size, or every found cache's size when none is ticked.
    private void UpdateShaderGroup()
    {
        if (Shaders is not { } group) return;
        var caches = group.Caches;
        string cleaning = _loc.GetString("Str_Maint_Cleaning");
        string scanning = _loc.GetString("Str_Maint_Scanning");
        string readError = _loc.GetString("Str_Maint_ReadError");

        if (caches.Any(c => c.ResultText == cleaning))
            group.DisplayText = cleaning;
        else if (caches.FirstOrDefault(c => c.ResultText.Length > 0 && c.LastResult is null) is { } failed)
            group.DisplayText = failed.ResultText;
        else if (caches.Any(c => c.LastResult is not null))
            group.DisplayText = ResultText(new CleanupResult(
                caches.Sum(c => c.LastResult?.FreedBytes ?? 0), caches.Sum(c => c.LastResult?.Deleted ?? 0), caches.Sum(c => c.LastResult?.Skipped ?? 0)));
        else if (caches.Any(c => c.SizeText == scanning))
            group.DisplayText = scanning;
        else if (caches.All(c => c.Scan is null && c.SizeText.Length == 0))
            group.DisplayText = "";
        else
        {
            var found  = caches.Where(c => c.Scan is { Found: true }).ToList();
            var ticked = found.Where(c => c.IsChecked).ToList();
            group.DisplayText = found.Count > 0 ? SizeText((ticked.Count > 0 ? ticked : found).Sum(c => c.Scan!.Bytes))
                              : caches.Any(c => c.SizeText == readError) ? readError
                              : _loc.GetString("Str_Maint_NotFound");
        }

        var lines = caches.Select(c => $"{c.Name}: {c.DisplayText}");
        string head = group.IsBlocked ? _loc.GetString("Str_Maint_BlockedGame") : _loc.GetString("Str_CleanTip_shader-caches");
        group.Tip = head + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private void UpdateSelectedTotal()
    {
        SelectedBytes = Rows.Where(r => r.IsChecked && r.CanClean).Sum(r => r.Scan!.Bytes);
        CleanButtonText = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_CleanSelected"), SizeText(SelectedBytes));
        CleanSelectedCommand.NotifyCanExecuteChanged();
    }

    // ── Tab activation and scanning ──────────────────────────────────────────

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            if (!IsCleaning) _ = RefreshAsync();   // a running clean rescans by itself when done
        }
        else
        {
            _scanCts?.Cancel();
            IsRepairArmed = false;
        }
    }

    // No scan while cleaning (spec §4.6).
    private bool CanRescan() => !IsCleaning;

    [RelayCommand(CanExecute = nameof(CanRescan))] private Task RescanAsync() => RefreshAsync();

    internal async Task RefreshAsync(bool keepResults = false)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var cts = _scanCts = new CancellationTokenSource();
        RefreshInfo();
        RefreshHistory();
        bool gameRunning = AnyGameRunning();
        var work = Rows.Select(r => ScanRowAsync(r, gameRunning, keepResults, cts.Token)).ToList();
        work.Add(ScanDownloadsAsync(cts.Token));
        try { await Task.WhenAll(work); }
        catch (OperationCanceledException) { }
    }

    private async Task ScanRowAsync(CleanupRowViewModel row, bool gameRunning, bool keepResults, CancellationToken ct)
    {
        row.IsBlocked = gameRunning && row.Target.BlockedWhileGameRuns;
        if (!keepResults) { row.ResultText = ""; row.LastResult = null; }
        row.SizeText = _loc.GetString("Str_Maint_Scanning");
        try
        {
            var scan = await row.Target.ScanAsync(ct);
            ct.ThrowIfCancellationRequested();
            row.Scan = scan;
            row.SizeText = scan.Found ? SizeText(scan.Bytes) : _loc.GetString("Str_Maint_NotFound");
        }
        catch (OperationCanceledException)
        {
            row.SizeText = "";
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Cleanup scan {row.Id} failed: {ex.Message}");
            row.Scan = null;
            row.SizeText = _loc.GetString("Str_Maint_ReadError");
        }
        row.Tip = _loc.GetString(row.IsBlocked ? "Str_Maint_BlockedGame" : "Str_CleanTip_" + row.Id);
    }

    private async Task ScanDownloadsAsync(CancellationToken ct)
    {
        try
        {
            var scan = await _downloads.ScanAsync(ct);
            ct.ThrowIfCancellationRequested();
            DownloadsSizeText = scan is { Found: true } ? SizeText(scan.Bytes) : _loc.GetString("Str_Maint_NotFound");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"Downloads scan failed: {ex.Message}");
            DownloadsSizeText = _loc.GetString("Str_Maint_ReadError");
        }
    }

    private void RefreshInfo()
    {
        var plan = _info.GetPowerPlan();
        PowerPlanText = plan?.Name ?? _loc.GetString("Str_Maint_PowerUnknown");
        var files = _info.GetPageFiles() ?? [];
        PageFileText = files.Count == 0
            ? _loc.GetString("Str_Maint_PageFileOff")
            : string.Join(" · ", files.Select(f => string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_PageFileFormat"),
                  f.Drive, MainViewModel.FormatSize(f.TotalMb),
                  _loc.GetString(f.SystemManaged ? "Str_Maint_SystemManaged" : "Str_Maint_Custom"),
                  MainViewModel.FormatSize(f.InUseMb))));

        // Gaming rows: state only; the buttons open Windows' own pages.
        string OnOff(bool on) => _loc.GetString(on ? "Str_Maint_On" : "Str_Maint_Off");
        if (_info.GetGamingInfo() is { } gaming)
        {
            HagsText            = gaming.Hags is { } hags ? OnOff(hags) : _loc.GetString("Str_Maint_WindowsDefault");
            RecordingText       = OnOff(gaming.BackgroundRecording);
            MemoryIntegrityText = OnOff(gaming.MemoryIntegrity);
            GameModeText        = OnOff(gaming.GameMode);
        }
        else
            HagsText = RecordingText = MemoryIntegrityText = GameModeText = _loc.GetString("Str_Maint_PowerUnknown");
        PowerModeText = _loc.GetString(_info.GetPowerMode() switch
        {
            PowerMode.BestPerformance     => "Str_Maint_PowerBest",
            PowerMode.Balanced            => "Str_Maint_PowerBalanced",
            PowerMode.BestPowerEfficiency => "Str_Maint_PowerEfficient",
            PowerMode.NotOnThisPlan       => "Str_Maint_PowerNotOnPlan",
            _                             => "Str_Maint_PowerUnknown",
        });
    }

    // ── Cleaning ─────────────────────────────────────────────────────────────

    private bool CanCleanSelected() => !IsCleaning && Rows.Any(r => r.IsChecked && r.CanClean);

    [RelayCommand(CanExecute = nameof(CanCleanSelected))]
    private async Task CleanSelectedAsync()
    {
        IsCleaning = true;
        _scanCts?.Cancel();
        var stats = Stats;
        long total = 0;
        try
        {
            foreach (var row in Rows.Where(r => r.IsChecked && r.CanClean).ToList())
            {
                row.LastResult = null;
                row.ResultText = _loc.GetString("Str_Maint_Cleaning");
                try
                {
                    var result = await row.Target.CleanAsync(CancellationToken.None);
                    total += result.FreedBytes;
                    row.LastResult = result;
                    row.ResultText = ResultText(result);
                    if (result.Deleted > 0 || result.Skipped > 0 || result.FreedBytes > 0)
                        stats.Add(new CleanupRecord(Now(), row.Id, result.FreedBytes, result.Skipped));
                    Logger.Info($"Cleanup {row.Id}: freed {result.FreedBytes / (1024 * 1024)} MB, {result.Deleted} deleted, {result.Skipped} skipped");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Cleanup {row.Id} failed: {ex.Message}");
                    row.ResultText = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultFailed"), ex.Message);
                }
            }
            _store.Save(stats);
            RefreshHistory();
            StatusRequested?.Invoke(this, string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Status_CleanDone"), SizeText(total)));
        }
        finally { IsCleaning = false; }
        await RefreshAsync(keepResults: true);
    }

    private string ResultText(CleanupResult r)
    {
        string text = string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultFreed"), SizeText(r.FreedBytes));
        if (r.Skipped > 0)
            text += " · " + string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultSkipped"), r.Skipped);
        return text;
    }

    // ── Windows tools ────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenTool(SystemTool tool)
    {
        if (tool == SystemTool.RepairSystemFiles && !IsRepairArmed) { IsRepairArmed = true; return; }
        IsRepairArmed = false;
        if (!_tools.Launch(tool))
            StatusRequested?.Invoke(this, string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Status_ToolFailed"), tool));
    }

    // ── Downloads dialog ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenDownloadsAsync()
    {
        var dialog = new DownloadsViewModel(_downloads, _loc, OnDownloadsMoved);
        DownloadsDialog = dialog;
        await dialog.LoadAsync();
    }

    [RelayCommand]
    private void CloseDownloads()
    {
        DownloadsDialog?.Dispose();
        DownloadsDialog = null;
        if (IsActive) _ = RefreshAsync(keepResults: true);
    }

    private void OnDownloadsMoved(long bytes, int moved, int failed)
    {
        if (moved == 0) return;
        Stats.Add(new CleanupRecord(Now(), CleanupTargetIds.Downloads, bytes, failed));
        _store.Save(Stats);
        RefreshHistory();
    }

    // ── History ──────────────────────────────────────────────────────────────

    private CleanupStats Stats => _stats ??= _store.Load();

    [RelayCommand] private void ShowHistory() { RefreshHistory(); IsHistoryVisible = true; }
    [RelayCommand] private void CloseHistory() { IsHistoryVisible = false; IsResetArmed = false; }

    [RelayCommand]
    private void ResetHistory()
    {
        if (!IsResetArmed) { IsResetArmed = true; return; }
        IsResetArmed = false;
        _stats = new CleanupStats();
        _store.Save(_stats);
        RefreshHistory();
        Logger.Info("Cleanup history reset");
    }

    public string StatsTodayFreed => SizeText(Stats.LastDays(1, Now()).FreedBytes);
    public string StatsTodayCount => CountText(Stats.LastDays(1, Now()).Count);
    public string StatsWeekFreed  => SizeText(Stats.LastDays(7, Now()).FreedBytes);
    public string StatsWeekCount  => CountText(Stats.LastDays(7, Now()).Count);
    public string StatsAllFreed   => SizeText(Stats.AllTime.FreedBytes);
    public string StatsAllCount   => CountText(Stats.AllTime.Count);
    public string FreedSoFarText  => string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_FreedSoFar"), StatsAllFreed);

    /// <summary>"NVIDIA shader cache 3.4 GB (2) · Temporary files (user) 1.1 GB (5)", targets with cleanups only.</summary>
    public string ByTargetText => string.Join(" · ", Stats.ByTarget
        .Where(kv => kv.Value.Count > 0)
        .OrderByDescending(kv => kv.Value.FreedBytes)
        .Select(kv => $"{TargetName(kv.Key)} {SizeText(kv.Value.FreedBytes)} ({kv.Value.Count})"));

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var r in Stats.Recent) History.Add(RowFor(r));
        foreach (var p in new[] { nameof(StatsTodayFreed), nameof(StatsTodayCount), nameof(StatsWeekFreed), nameof(StatsWeekCount),
                                  nameof(StatsAllFreed), nameof(StatsAllCount), nameof(FreedSoFarText), nameof(ByTargetText) })
            OnPropertyChanged(p);
    }

    private CultureInfo DateCulture => CultureInfo.GetCultureInfo(_loc.CurrentLanguage == Language.Turkish ? "tr-TR" : "en-US");

    private CleanupHistoryRow RowFor(CleanupRecord r)
    {
        string time = r.At.Date == Now().Date ? r.At.ToString("HH:mm", DateCulture) : r.At.ToString("d MMM HH:mm", DateCulture);
        string what = TargetName(r.TargetId);
        if (r.Skipped > 0) what += " · " + string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_Maint_ResultSkipped"), r.Skipped);
        return new CleanupHistoryRow(r, time, what, SizeText(r.FreedBytes));
    }

    private string CountText(int count) =>
        string.Format(CultureInfo.CurrentCulture, _loc.GetString("Str_MHist_Count"), count);

    private string TargetName(string id) => _loc.GetString("Str_Clean_" + id);

    private void Relabel()
    {
        foreach (var r in Rows)
        {
            r.Name = TargetName(r.Id);
            r.Tip  = _loc.GetString("Str_CleanTip_" + r.Id);
        }
        if (Shaders is not null) Shaders.Name = _loc.GetString("Str_Clean_shader-caches");
        UpdateShaderGroup();
        UpdateSelectedTotal();
        OnPropertyChanged(nameof(RepairButtonText));
        if (_stats is not null) RefreshHistory();
    }

    /// <summary>"845 MB", "1.2 GB", "&lt; 1 MB", "0 MB".</summary>
    internal static string SizeText(long bytes) =>
        bytes > 0 && bytes < 1024 * 1024 ? "< 1 MB" : MainViewModel.FormatSize(bytes / (1024 * 1024));

    /// <summary>App exit (the DI container disposes it): stops a running scan.</summary>
    public void Dispose()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        DownloadsDialog?.Dispose();
    }

    // ── Inert stand-ins ──────────────────────────────────────────────────────

    private sealed class NoInfo : ISystemInfoService
    {
        public PowerPlanInfo? GetPowerPlan() => null;
        public IReadOnlyList<PageFileInfo> GetPageFiles() => [];
        public GamingInfo GetGamingInfo() => new(null, false, false, true);
        public PowerMode GetPowerMode() => PowerMode.Unknown;
    }

    private sealed class NoTools : ISystemToolLauncher
    {
        public bool Launch(SystemTool tool) => false;
    }

    private sealed class NoDownloads : IDownloadsService
    {
        public string? FolderPath => null;
        public Task<CleanupScan> ScanAsync(CancellationToken ct) => Task.FromResult(CleanupScan.NotFound);
        public Task<IReadOnlyList<DownloadEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DownloadEntry>>([]);
        public Task<IReadOnlyList<DownloadEntry>> MoveToRecycleBinAsync(IReadOnlyList<DownloadEntry> entries) => Task.FromResult(entries);
    }

    private sealed class InertLocalization : ILocalizationService
    {
        public Language CurrentLanguage => Language.English;
        public void SetLanguage(Language lang) { }
        public string GetString(string key) => key;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
