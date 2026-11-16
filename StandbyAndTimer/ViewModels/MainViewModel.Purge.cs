using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // Standby purge on the status page: what a purge would free now, the
    // purge button and the recommended thresholds, and the purge history
    // with its totals.

    // ── "Clearable": what a purge in the chosen mode would free now ─────────

    public PurgeKind SelectedPurgeKind =>
        DeepPurge ? PurgeKind.Deep : LightPurge ? PurgeKind.Light : PurgeKind.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearableValue), nameof(ClearableUnit))]
    private long _clearableMb;

    /// <summary>"2.8" (GB) from 1 GB up, "150" (MB) below, so a light purge's few MB do not show as 0.0 GB.</summary>
    public string ClearableValue => ClearableMb >= 1024
        ? (ClearableMb / 1024.0).ToString("0.0", CultureInfo.CurrentCulture)
        : ClearableMb.ToString(CultureInfo.CurrentCulture);

    public string ClearableUnit => ClearableMb >= 1024 ? "GB" : "MB";

    public string ClearableLabel => _localization.GetString(SelectedPurgeKind switch
    {
        PurgeKind.Light => "Str_Card_ClearableLight",
        PurgeKind.Deep  => "Str_Card_ClearableDeep",
        _               => "Str_Card_StandbyClearable",
    });

    /// <summary>All three modes side by side, for the card's tooltip.</summary>
    public string ClearableTip => _lastSnapshot is not { } s ? string.Empty : string.Format(CultureInfo.CurrentCulture,
        _localization.GetString("Str_Card_ClearableTip"),
        FormatSize(s.ClearableMb(PurgeKind.Normal)), FormatSize(s.ClearableMb(PurgeKind.Light)),
        FormatSize(s.ClearableMb(PurgeKind.Deep)));

    // The purge mode or the language changed (a tick goes through ShowSnapshot).
    private void UpdateClearable()
    {
        ClearableMb = _lastSnapshot?.ClearableMb(SelectedPurgeKind) ?? StandbyRamMb;
        OnPropertyChanged(nameof(ClearableLabel));
        OnPropertyChanged(nameof(ClearableTip));
    }

    // ── Purge history (kept across restarts) ────────────────────────────────

    private void OnPurgeSucceeded(object? sender, PurgeResult result) =>
        Application.Current.Dispatcher.InvokeAsync(() => RecordPurge(result));

    /// <summary>The last purges (kept across restarts), newest first.</summary>
    public ObservableCollection<PurgeHistoryRow> PurgeHistory { get; } = [];
    internal const int PurgeHistoryMax = PurgeStats.RecentMax;

    private readonly IPurgeStatsStore _purgeStore;
    private PurgeStats _purgeStats;
    // Today's date for the totals; tests set a fixed one.
    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    [ObservableProperty] private bool _isPurgeHistoryVisible;
    // "Reset history" needs a second press while armed.
    [ObservableProperty] private bool _isResetArmed;

    [RelayCommand] private void ShowPurgeHistory()
    {
        RefreshPurgeStats();
        IsPurgeHistoryVisible = true;
    }

    [RelayCommand] private void ClosePurgeHistory()
    {
        IsPurgeHistoryVisible = false;
        IsResetArmed = false;
    }

    [RelayCommand]
    private void ResetPurgeHistory()
    {
        if (!IsResetArmed) { IsResetArmed = true; return; }
        IsResetArmed = false;
        _purgeStats = new PurgeStats();
        _purgeStore.Save(_purgeStats);
        RelabelPurgeHistory();
        Logger.Info("Purge history reset");
    }

    // One finished purge: count, history, status line ("1.8 GB freed"),
    // then the tray balloon (App, if purge notifications are on).
    internal void RecordPurge(PurgeResult result)
    {
        PurgeCount++;
        _purgeStats.Add(result);
        _purgeStore.Save(_purgeStats);   // a few KB, once per purge
        PurgeHistory.Insert(0, RowFor(result));
        while (PurgeHistory.Count > PurgeHistoryMax) PurgeHistory.RemoveAt(PurgeHistory.Count - 1);
        RefreshPurgeStats();
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            _localization.GetString("Str_Status_PurgeFreed"), FormatSize(result.FreedMb));
        PurgeNotification?.Invoke(this, new PurgeNotice(result, PurgeCount));
    }

    // Month and day names follow the app language, not the Windows one: an
    // English UI on a Turkish Windows showed "11 Eki" and "Per". Numbers keep
    // the Windows format (FormatSize).
    private CultureInfo DateCulture => CultureInfo.GetCultureInfo(
        _localization.CurrentLanguage == Language.Turkish ? "tr-TR" : "en-US");

    // Today: "21:05:09"; earlier: "13 Oct 21:05".
    private PurgeHistoryRow RowFor(PurgeResult r) =>
        new(r,
            r.At.Date == Now().Date
                ? r.At.ToString("HH:mm:ss", DateCulture)
                : r.At.ToString("d MMM HH:mm", DateCulture),
            DescribePurge(r), FormatSize(r.FreedMb));

    // The rows carry text: rebuild them (new language, loaded, reset).
    private void RelabelPurgeHistory()
    {
        PurgeHistory.Clear();
        foreach (var r in _purgeStats.Recent) PurgeHistory.Add(RowFor(r));
        RefreshPurgeStats();
    }

    // ── Totals shown above the history list ─────────────────────────────────

    public string StatsTodayFreed => FormatSize(_purgeStats.LastDays(1, Now()).FreedMb);
    public string StatsTodayCount => CountText(_purgeStats.LastDays(1, Now()).Count);
    public string StatsWeekFreed  => FormatSize(_purgeStats.LastDays(7, Now()).FreedMb);
    public string StatsWeekCount  => CountText(_purgeStats.LastDays(7, Now()).Count);
    public string StatsAllFreed   => FormatSize(_purgeStats.AllTime.FreedMb);
    public string StatsAllCount   => CountText(_purgeStats.AllTime.Count);

    /// <summary>"Normal 31.2 GB (40) · Light 12 MB (3) · Deep 9.4 GB (6)".</summary>
    public string StatsByKind => string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_History_ByKind"),
        KindText(PurgeKind.Normal), KindText(PurgeKind.Light), KindText(PurgeKind.Deep));

    public string StatsSince => _purgeStats.Since is { } since
        ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_History_Since"), since.ToString("d MMMM yyyy", DateCulture))
        : string.Empty;

    private string CountText(int count) =>
        string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_History_Count"), count);

    private string KindText(PurgeKind kind)
    {
        var t = _purgeStats.Of(kind);
        return $"{FormatSize(t.FreedMb)} ({t.Count})";
    }

    internal const double WeekBarMaxHeight = 40;

    /// <summary>The last 7 days, oldest first, for the bar chart: height relative to the busiest day.</summary>
    public IReadOnlyList<DayBar> WeekBars
    {
        get
        {
            DateTime today = Now().Date;
            var days = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6))
                                 .Select(d => (Day: d, Total: _purgeStats.LastDays(1, d))).ToList();
            long max = days.Max(d => d.Total.FreedMb);
            return days.Select(d => new DayBar(
                d.Day.ToString("ddd", DateCulture),
                // A day with purges never gets a bar too thin to see.
                max <= 0 || d.Total.Count == 0 ? 0 : Math.Max(3, Math.Round(WeekBarMaxHeight * d.Total.FreedMb / max)),
                d.Day == today,
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_History_DayTip"),
                    d.Day.ToString("d MMMM", DateCulture), FormatSize(d.Total.FreedMb), CountText(d.Total.Count))))
                .ToList();
        }
    }

    private void RefreshPurgeStats()
    {
        foreach (var p in new[] { nameof(StatsTodayFreed), nameof(StatsTodayCount), nameof(StatsWeekFreed), nameof(StatsWeekCount),
                                  nameof(StatsAllFreed), nameof(StatsAllCount), nameof(StatsByKind), nameof(StatsSince), nameof(WeekBars) })
            OnPropertyChanged(p);
    }

    /// <summary>"Deep", "Light, automatic", "Normal, at game start": kind, plus the trigger unless it was a button / hotkey / tray press.</summary>
    public string DescribePurge(PurgeResult result)
    {
        string kind = _localization.GetString(result.Kind switch
        {
            PurgeKind.Light => "Str_PurgeKind_Light",
            PurgeKind.Deep  => "Str_PurgeKind_Deep",
            _               => "Str_PurgeKind_Normal",
        });
        return result.Trigger switch
        {
            PurgeTrigger.Auto      => kind + ", " + _localization.GetString("Str_PurgeTrigger_Auto"),
            PurgeTrigger.GameStart => kind + ", " + _localization.GetString("Str_PurgeTrigger_GameStart"),
            PurgeTrigger.GameExit  => kind + ", " + _localization.GetString("Str_PurgeTrigger_GameExit"),
            _                      => kind,
        };
    }

    /// <summary>"1.8 GB" from 1 GB up, "350 MB" below (current culture).</summary>
    internal static string FormatSize(long mb) =>
        mb >= 1024
            ? (mb / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : mb.ToString(CultureInfo.CurrentCulture) + " MB";

    // ── Purge button, AUTO PURGE hotkey, recommended thresholds ─────────────

    /// <summary>
    /// Recommended (StandbyLimitMb, FreeLimitMb) for a given total RAM in MB,
    /// matching the table in the About / Usage Guide.
    /// </summary>
    public static (int StandbyMb, int FreeMb) GetRecommendedThresholds(long totalRamMb) => totalRamMb switch
    {
        <= 0     => (2048, 1024),
        <= 6144  => (512, 512),     // 4 GB
        <= 12288 => (1024, 1024),   // 8 GB
        <= 24576 => (2048, 1024),   // 16 GB
        <= 49152 => (4096, 2048),   // 32 GB
        _        => (8192, 2048),   // 64 GB+
    };

    [RelayCommand]
    private void ApplyRecommendedThresholds()
    {
        var (standby, free) = GetRecommendedThresholds(TotalRamMb);
        StandbyLimitMb = standby;
        FreeLimitMb    = free;
        PersistSettings();
        StatusMessage  = string.Format(CultureInfo.CurrentCulture,
            _localization.GetString("Str_Status_RecommendedApplied"), standby, free);
        Logger.Info($"Applied recommended thresholds: Standby={standby} MB, Free={free} MB (TotalRam={TotalRamMb} MB)");
    }

    [RelayCommand]
    private async Task ManualPurgeAsync()
    {
        StatusMessage = _localization.GetString("Str_Status_Purging");
        bool ok = await _purgeService.PurgeAsync(PurgeTrigger.Manual).ConfigureAwait(false);
        // Success: RecordPurge already says how much was freed.
        if (!ok)
            await Application.Current.Dispatcher.InvokeAsync(() =>
                StatusMessage = _localization.GetString("Str_Status_PurgeFail"));
    }

    // Global hotkey target (Settings -> Hotkeys). Flips the AUTO PURGE master
    // switch; OnAutoPurgeEnabledChanged syncs the monitor and persists.
    [RelayCommand]
    private void ToggleAutoPurge()
    {
        AutoPurgeEnabled = !AutoPurgeEnabled;
        StatusMessage = _localization.GetString(
            AutoPurgeEnabled ? "Str_Status_AutoPurgeOn" : "Str_Status_AutoPurgeOff");
        Logger.Info($"Auto purge toggled {(AutoPurgeEnabled ? "ON" : "OFF")}");
    }
}
