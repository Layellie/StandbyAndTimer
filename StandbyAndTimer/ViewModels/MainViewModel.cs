using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly ITimerResolutionService     _timerService;
    private readonly IStandbyPurgeService        _purgeService;
    private readonly IMemoryMonitorService       _memoryService;
    private readonly ISettingsService            _settingsService;
    private readonly ILocalizationService        _localization;
    private readonly IGameDetectionService       _gameDetection;
    private readonly CancellationTokenSource     _appCts = new();

    // Debounces registry writes triggered by rapidly-changing TextBox-bound
    // numeric properties (StandbyLimitMb / FreeLimitMb). Each new change cancels
    // the previous pending save, so typing "1024" results in one write, not four.
    private const int PersistDebounceMs = 500;
    private CancellationTokenSource? _persistDebounceCts;
    private readonly object _persistDebounceGate = new();

    private bool _isInitializing;
    private bool _disposed;

    // Exes the user removed after detection auto-added them. Detection skips
    // these (via KnownGamePaths); adding one by hand takes it back off.
    private readonly HashSet<string> _ignoredGamePaths = new(StringComparer.OrdinalIgnoreCase);

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private long    _totalRamMb;
    [ObservableProperty] private long    _freeRamMb;
    [ObservableProperty] private long    _standbyRamMb;
    [ObservableProperty] private int     _purgeCount;
    [ObservableProperty] private int     _standbyLimitMb;
    [ObservableProperty] private int     _freeLimitMb;
    [ObservableProperty] private bool    _autoPurgeEnabled;
    [ObservableProperty] private bool    _autoPurgeIdleOnly;
    // Light purge: only the low-priority standby pages (see IStandbyPurgeService.LightMode).
    [ObservableProperty] private bool    _lightPurge;
    // Deep purge: modified list + system file cache onto standby first (see
    // IStandbyPurgeService.DeepMode). Light and deep exclude each other.
    [ObservableProperty] private bool    _deepPurge;
    [ObservableProperty] private int     _idleThresholdMinutes = 5;
    [ObservableProperty] private bool    _gameModeEnabled;
    [ObservableProperty] private bool    _timerActive;
    // The lock on the Timer card: locked = the timer turns on by itself at
    // every app start. Independent of TimerActive (the TIMER button can
    // still switch the timer off for this session while it stays locked).
    [ObservableProperty] private TimerLockMode _timerLock;
    // Automatic purges only while a listed game runs (excludes AutoPurgeIdleOnly).
    [ObservableProperty] private bool    _autoPurgeOnlyInGames;

    // In-game lock bookkeeping: whether a listed game ran at the last tick,
    // and whether the timer was turned on by a game start (only then does
    // the last game exiting turn it off again).
    private bool _anyGameRunning;
    private bool _timerStartedForGame;
    // Timer switched off by "turn off when idle"; the next input turns it
    // back on. A TIMER press clears it (the user is in charge then).
    private bool _timerPausedForIdle;
    // Listed games running at the last tick, to spot the ones that just started.
    private readonly HashSet<string> _runningGames = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private double  _actualTimerMs;
    [ObservableProperty] private string  _statusMessage  = string.Empty;
    [ObservableProperty] private GameEntry? _selectedGame;
    [ObservableProperty] private bool    _isAboutVisible;
    [ObservableProperty] private bool    _isWizardVisible;

    public ObservableCollection<GameEntry> Games    { get; } = [];
    public SettingsViewModel               Settings { get; }
    public SystemViewModel                 SystemSettings { get; }

    public string TimerButtonLabel => _localization.GetString(
        TimerActive ? "Str_Timer_Active" : "Str_Timer_Inactive");

    public string AboutTitle => _localization.GetString("Str_About_Title");
    public string AboutClose => _localization.GetString("Str_About_Close");

    public string WizardTitle  => _localization.GetString("Str_Wizard_Title");
    public string WizardBody   => _localization.GetString("Str_Wizard_Body");
    public string WizardFinish => _localization.GetString("Str_Wizard_Finish");

    // Raised once a manual or auto purge succeeds — App.xaml.cs subscribes to
    // show a balloon notification without needing to know about MVVM internals.
    public event EventHandler<PurgeNotice>? PurgeNotification;

    // Raised when timer was toggled (true=on, false=off). Includes actual ms.
    public event EventHandler<TimerToggledArgs>? TimerToggledNotification;

    // Raised once per listed game start, after the in-game lock has acted,
    // with what the app does for that game. App shows it as a balloon
    // (Settings -> Notifications -> game notifications).
    public event EventHandler<GameStartedArgs>? GameStartedNotification;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel(
        ITimerResolutionService     timerService,
        IStandbyPurgeService        purgeService,
        IMemoryMonitorService       memoryService,
        ISettingsService            settingsService,
        ILocalizationService        localization,
        IGameDetectionService       gameDetection,
        SettingsViewModel           settingsViewModel,
        SystemViewModel?            systemViewModel = null,
        IPurgeStatsStore?           purgeStats      = null)
    {
        _timerService    = timerService;
        _purgeService    = purgeService;
        _memoryService   = memoryService;
        _settingsService = settingsService;
        _localization    = localization;
        _gameDetection   = gameDetection;

        Settings = settingsViewModel;
        Settings.SettingsChanged   += (_, _) => PersistSettings();
        // System settings describe this machine (its registry, its last
        // boot), not the exported one: an import keeps the current values.
        Settings.SettingsImported  += (_, imported) =>
        {
            var now = BuildCurrentSettings();
            imported.GlobalTimerPromptAnswered = now.GlobalTimerPromptAnswered;
            imported.GlobalTimerSetByApp       = now.GlobalTimerSetByApp;
            imported.GlobalTimerChangedUtc     = now.GlobalTimerChangedUtc;
            imported.GlobalTimerBootValue      = now.GlobalTimerBootValue;
            ApplySettings(imported);
        };
        Settings.SnapshotProvider  = BuildCurrentSettings;

        SystemSettings = systemViewModel ?? SystemViewModel.Inert();

        _purgeStore = purgeStats ?? new InMemoryPurgeStatsStore();
        _purgeStats = _purgeStore.Load();
        foreach (var r in _purgeStats.Recent) PurgeHistory.Add(RowFor(r));
        SystemSettings.SettingsChanged += (_, _) => PersistSettings();

        _memoryService.SnapshotUpdated     += OnSnapshotUpdated;
        _purgeService.PurgeSucceeded       += OnPurgeSucceeded;
        _localization.LanguageChanged      += OnLanguageChanged;
        _timerService.ResolutionMeasured   += OnTimerResolutionMeasured;
        _gameDetection.GameDetected        += OnGameDetected;
    }

    // ── Initialisation (called once from App.OnStartup) ───────────────────────

    public async Task InitializeAsync()
    {
        var settings = _settingsService.Load();
        ApplySettings(settings);

        System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
            System.Diagnostics.ProcessPriorityClass.High;

        // Only the lock turns the timer on at start-up. This used to restore
        // the last session's state, so a timer left on once came back by
        // itself on every launch.
        if (TimerLock == TimerLockMode.Locked)
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);

        // Don't overwrite "Timer active — 0.50 ms" with "Ready".
        SetIdleStatus();

        await _memoryService.StartAsync(_appCts.Token).ConfigureAwait(false);
        await _gameDetection.StartAsync(_appCts.Token).ConfigureAwait(false);

        // Show the first-run wizard on the very first launch only. Persisting
        // FirstRunCompleted=true happens when the user dismisses it, so a
        // crash mid-wizard won't strand them out of the onboarding.
        if (!settings.FirstRunCompleted)
            await Application.Current.Dispatcher.InvokeAsync(() => IsWizardVisible = true);
        else
            // The one-time "apply the timer to all games?" question; on a
            // first run it follows the wizard (DismissWizard).
            await OnUiAsync(SystemSettings.MaybeShowPrompt).ConfigureAwait(false);
    }

    // ── Settings helpers ──────────────────────────────────────────────────────

    private void ApplySettings(AppSettings s)
    {
        using (new InitScope(v => _isInitializing = v))
        {
            StandbyLimitMb       = s.StandbyLimitMb;
            FreeLimitMb          = s.FreeLimitMb;
            AutoPurgeEnabled     = s.AutoPurgeEnabled;
            AutoPurgeIdleOnly    = s.AutoPurgeIdleOnly;
            AutoPurgeOnlyInGames = s.AutoPurgeOnlyInGames && !s.AutoPurgeIdleOnly;
            LightPurge           = s.LightPurge && !s.DeepPurge;
            DeepPurge            = s.DeepPurge;
            IdleThresholdMinutes = s.IdleThresholdMinutes > 0 ? s.IdleThresholdMinutes : 5;
            GameModeEnabled      = s.GameModeEnabled;
            // TimerActive is NOT taken from settings: it mirrors the timer
            // service. Setting it here showed the timer as active at start-up
            // before anything ran, and after an import without activating it.
            // 2.2.x saved only the two-state TimerLocked flag.
            TimerLock            = s.TimerLock != TimerLockMode.Unlocked ? s.TimerLock
                                 : s.TimerLocked ? TimerLockMode.Locked : TimerLockMode.Unlocked;
            _firstRunCompleted   = s.FirstRunCompleted;

            Games.Clear();
            foreach (var g in s.Games)
                Games.Add(g);

            _ignoredGamePaths.Clear();
            foreach (var p in s.IgnoredGamePaths ?? [])
                _ignoredGamePaths.Add(p);

            Settings.Initialize(
                s.AutoStartEnabled, s.Language, s.UpdateCheckEnabled, s.Theme,
                s.NotifyOnPurge, s.NotifyOnTimerToggle, s.NotifyOnGameDetected,
                s.PurgeHotkey, s.TimerHotkey, s.AutoPurgeHotkey,
                s.TimerIdleOffEnabled, s.TimerIdleOffMinutes);
            SystemSettings.Initialize(s.GlobalTimerPromptAnswered, s.GlobalTimerSetByApp,
                              s.GlobalTimerChangedUtc, s.GlobalTimerBootValue);
        }
        SyncMonitorThresholds();
        SyncMonitorGames();
    }

    // Pushes only the threshold / flag state to the monitor — does NOT rebuild
    // the GamePaths list. Called on every TextBox keystroke, so the per-tick
    // LINQ + List allocation that GamePaths rebuilding would cost is wasted.
    private void SyncMonitorThresholds()
    {
        _memoryService.StandbyLimitMb    = StandbyLimitMb;
        _memoryService.FreeLimitMb       = FreeLimitMb;
        _memoryService.AutoPurgeEnabled  = AutoPurgeEnabled;
        _memoryService.AutoPurgeIdleOnly = AutoPurgeIdleOnly;
        _memoryService.AutoPurgeOnlyInGames = AutoPurgeOnlyInGames;
        _memoryService.IdleThresholdMs   = Math.Max(0, IdleThresholdMinutes) * 60_000;
        _memoryService.GameModeEnabled   = GameModeEnabled;
    }

    // Pushes the executable-path list to the monitor. Called only when the
    // Games collection actually mutates (add / remove / settings load / import).
    private void SyncMonitorGames()
    {
        var paths = Games.Select(g => g.ExecutablePath).ToList();
        // Per-game switches: boost-off games are left to other tools (e.g.
        // Process Lasso) entirely; every listed game is still watched, for
        // the auto-purge pause and the "only in games" options.
        _memoryService.GamePaths            = Games.Where(g => g.BoostPriority).Select(g => g.ExecutablePath).ToList();
        _memoryService.WatchedGamePaths     = paths;
        _memoryService.NoAutoPurgeGamePaths = Games.Where(g => !g.AllowAutoPurge).Select(g => g.ExecutablePath).ToList();
        // Also keep the detection service in sync — without this, a game the
        // user just added would be re-detected on the next poll, and a game
        // the user removed after auto-add would come straight back.
        var known = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(_ignoredGamePaths);
        _gameDetection.KnownGamePaths = known;
    }

    private void PersistSettings()
    {
        if (_isInitializing) return;
        var snapshot = BuildCurrentSettings();
        _settingsService.Save(snapshot);
        SettingsPersisted?.Invoke(this, snapshot);
    }

    /// <summary>Raised after every successful Save — App.xaml.cs keeps a cached snapshot for balloon-guard decisions.</summary>
    public event EventHandler<AppSettings>? SettingsPersisted;

    private void SchedulePersist()
    {
        if (_isInitializing) return;

        CancellationToken token;
        lock (_persistDebounceGate)
        {
            _persistDebounceCts?.Cancel();
            _persistDebounceCts?.Dispose();
            _persistDebounceCts = new CancellationTokenSource();
            token = _persistDebounceCts.Token;
        }

        _ = Task.Delay(PersistDebounceMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try { PersistSettings(); }
            catch (Exception ex) { Logger.Error("SchedulePersist", ex); }
        }, TaskScheduler.Default);
    }

    private void FlushPendingPersist()
    {
        lock (_persistDebounceGate)
        {
            if (_persistDebounceCts is null) return;
            _persistDebounceCts.Cancel();
            _persistDebounceCts.Dispose();
            _persistDebounceCts = null;
        }
    }

    internal AppSettings BuildCurrentSettings() => new()
    {
        StandbyLimitMb        = StandbyLimitMb,
        FreeLimitMb           = FreeLimitMb,
        AutoPurgeEnabled      = AutoPurgeEnabled,
        AutoPurgeIdleOnly     = AutoPurgeIdleOnly,
        AutoPurgeOnlyInGames  = AutoPurgeOnlyInGames,
        LightPurge            = LightPurge,
        DeepPurge             = DeepPurge,
        IdleThresholdMinutes  = IdleThresholdMinutes,
        GameModeEnabled       = GameModeEnabled,
        AutoStartEnabled      = Settings.AutoStartEnabled,
        TimerResolutionActive = TimerActive || _timerPausedForIdle,   // idle pause is not "off"
        TimerLock             = TimerLock,
        TimerLocked           = TimerLock == TimerLockMode.Locked,   // for 2.2.x
        Language              = Settings.SelectedLanguage,
        Theme                 = Settings.SelectedTheme,
        UpdateCheckEnabled    = Settings.UpdateCheckEnabled,
        NotifyOnPurge         = Settings.NotifyOnPurge,
        NotifyOnTimerToggle   = Settings.NotifyOnTimerToggle,
        NotifyOnGameDetected  = Settings.NotifyOnGameDetected,
        PurgeHotkey           = Settings.PurgeHotkeyText,
        TimerHotkey           = Settings.TimerHotkeyText,
        AutoPurgeHotkey       = Settings.AutoPurgeHotkeyText,
        TimerIdleOffEnabled   = Settings.TimerIdleOffEnabled,
        TimerIdleOffMinutes   = Settings.TimerIdleOffMinutes,
        GlobalTimerPromptAnswered = SystemSettings.PromptAnswered,
        GlobalTimerSetByApp       = SystemSettings.GlobalTimerSetByApp,
        GlobalTimerChangedUtc     = SystemSettings.GlobalTimerChangedUtc,
        GlobalTimerBootValue      = SystemSettings.GlobalTimerBootValue,
        FirstRunCompleted     = _firstRunCompleted,
        Games                 = [.. Games],
        IgnoredGamePaths      = [.. _ignoredGamePaths]
    };

    // Local mirror of the persisted FirstRunCompleted flag so BuildCurrentSettings
    // doesn't have to re-read from disk. DismissWizard flips this true before
    // calling Save.
    private bool _firstRunCompleted;

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSnapshotUpdated(object? sender, MemorySnapshot snapshot) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ShowSnapshot(snapshot);
            _ = HandleTickAsync(snapshot);
        });

    internal void ShowSnapshot(MemorySnapshot snapshot)
    {
        TotalRamMb    = snapshot.TotalMb;
        FreeRamMb     = snapshot.FreeMb;
        StandbyRamMb  = snapshot.StandbyMb;
        _lastSnapshot = snapshot;
        UpdateClearable();
    }

    // Games first: a game starting while the timer is paused for idle is
    // the in-game lock's to switch on.
    private async Task HandleTickAsync(MemorySnapshot snapshot)
    {
        try
        {
            await HandleRunningGamesAsync(snapshot.RunningGamePaths ?? []).ConfigureAwait(true);
            await ApplyIdleStateAsync(snapshot.IdleFor, snapshot.AnyGameRunning).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn($"Tick handling: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// "Turn the timer off when idle": no input for the chosen minutes turns
    /// a running timer off, which also lets Windows sleep again; the next
    /// input turns it back on. Never while a listed game runs: gamepad input
    /// does not count as input for Windows' idle time.
    /// </summary>
    internal async Task ApplyIdleStateAsync(TimeSpan idleFor, bool anyGameRunning)
    {
        int  minutes = Settings.TimerIdleOffMinutes;
        bool idle    = Settings.TimerIdleOffEnabled && !anyGameRunning
                    && idleFor >= TimeSpan.FromMinutes(minutes);

        if (idle && TimerActive && !_timerPausedForIdle)
        {
            DeactivateTimer(notify: false);
            _timerPausedForIdle = true;
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_TimerIdleOff"), minutes);
            Logger.Info($"Idle {minutes} min: timer OFF");
        }
        else if (!idle && _timerPausedForIdle)
        {
            _timerPausedForIdle = false;
            if (TimerActive) return;   // already back on (a game start, a lock change)
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_TimerIdleBack"), ActualTimerMs);
            Logger.Info("Input after idle: timer ON");
        }
    }

    /// <summary>
    /// Per tick: lets the in-game lock act, then announces each listed game
    /// that was not running at the last tick.
    /// </summary>
    internal async Task HandleRunningGamesAsync(IReadOnlyList<string> running)
    {
        var started = running.Where(p => !_runningGames.Contains(p)).ToList();
        _runningGames.Clear();
        _runningGames.UnionWith(running);

        await ApplyGameStateAsync(running.Count > 0).ConfigureAwait(true);

        bool purgeNow = false;
        foreach (string path in started)
        {
            var game = Games.FirstOrDefault(g => g.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (game is null) continue;   // removed from the list meanwhile
            purgeNow |= game.PurgeOnStart;
            GameStartedNotification?.Invoke(this, new GameStartedArgs(game.DisplayName, DescribeGameStart(game)));
            Logger.Info($"Game started: {game.DisplayName}");
        }

        // "Purge once when it starts": one purge for the tick even if two such
        // games start together, in the chosen mode, without AUTO PURGE.
        if (purgeNow)
            await _purgeService.PurgeAsync(PurgeTrigger.GameStart).ConfigureAwait(true);
    }

    // "High priority · Timer on (0.50 ms) · Auto purge paused": only the
    // features that are switched on, as they apply to this game right now.
    private string DescribeGameStart(GameEntry game)
    {
        var parts = new List<string>(3);
        if (GameModeEnabled)
            parts.Add(_localization.GetString(game.BoostPriority ? "Str_GameNotify_Boost" : "Str_GameNotify_NoBoost"));
        parts.Add(TimerActive
            ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_GameNotify_TimerOn"), ActualTimerMs)
            : _localization.GetString("Str_GameNotify_TimerOff"));
        if (AutoPurgeEnabled)
            parts.Add(_localization.GetString(game.AllowAutoPurge ? "Str_GameNotify_PurgeOn" : "Str_GameNotify_PurgePaused"));
        if (game.PurgeOnStart)
            parts.Add(_localization.GetString("Str_GameNotify_PurgeAtStart"));
        return string.Join(" · ", parts);
    }

    // ── "Clearable": what a purge in the chosen mode would free now ──────────

    private MemorySnapshot? _lastSnapshot;

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

    private void UpdateClearable()
    {
        ClearableMb = _lastSnapshot?.ClearableMb(SelectedPurgeKind) ?? StandbyRamMb;
        OnPropertyChanged(nameof(ClearableLabel));
        OnPropertyChanged(nameof(ClearableTip));
    }

    /// <summary>
    /// In-game lock: a listed game starting turns the timer on, the last one
    /// exiting turns it off again, but only if a game start had turned it
    /// on. Acts on transitions only, so a TIMER press stays in charge until
    /// the next game starts. No-op in the other lock modes.
    /// </summary>
    internal async Task ApplyGameStateAsync(bool anyGameRunning)
    {
        bool started = anyGameRunning && !_anyGameRunning;
        bool stopped = !anyGameRunning && _anyGameRunning;
        _anyGameRunning = anyGameRunning;
        if (TimerLock != TimerLockMode.InGame) return;

        if (started && !TimerActive)
        {
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
            _timerStartedForGame = true;
            StatusMessage = _localization.GetString("Str_Status_TimerGameOn");
            Logger.Info("In-game lock: game started, timer ON");
        }
        else if (stopped && TimerActive && _timerStartedForGame)
        {
            DeactivateTimer(notify: false);
            StatusMessage = _localization.GetString("Str_Status_TimerGameOff");
            Logger.Info("In-game lock: last game exited, timer OFF");
        }
    }

    private void OnPurgeSucceeded(object? sender, PurgeResult result) =>
        Application.Current.Dispatcher.InvokeAsync(() => RecordPurge(result));

    /// <summary>Newest first, this session only (PurgeHistoryMax entries).</summary>
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

    // ── Totals shown above the list ──────────────────────────────────────

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
            _                      => kind,
        };
    }

    /// <summary>"1.8 GB" from 1 GB up, "350 MB" below (current culture).</summary>
    internal static string FormatSize(long mb) =>
        mb >= 1024
            ? (mb / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : mb.ToString(CultureInfo.CurrentCulture) + " MB";

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SetIdleStatus();
            UpdateClearable();
            RelabelPurgeHistory();
            OnPropertyChanged(nameof(TimerButtonLabel));
            OnPropertyChanged(nameof(AboutTitle));
            OnPropertyChanged(nameof(AboutClose));
            OnPropertyChanged(nameof(WizardTitle));
            OnPropertyChanged(nameof(WizardBody));
            OnPropertyChanged(nameof(WizardFinish));
        });

    // Detection already filtered out known non-games and momentary overlays
    // (two consecutive fullscreen polls); what reaches here is added to the
    // list, tagged AUTO, and persisted. Removing it later dismisses it for good.
    private void OnGameDetected(object? sender, string exePath) =>
        Application.Current.Dispatcher.InvokeAsync(() => AutoAddDetectedGame(exePath));

    internal void AutoAddDetectedGame(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || _ignoredGamePaths.Contains(exePath)) return;
        if (Games.Any(g => g.ExecutablePath.Equals(exePath, StringComparison.OrdinalIgnoreCase))) return;

        string name = Path.GetFileNameWithoutExtension(exePath);
        Games.Add(new GameEntry { DisplayName = name, ExecutablePath = exePath, AutoAdded = true });
        SyncMonitorGames();
        PersistSettings();
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            _localization.GetString("Str_Status_GameAutoAdded"), name);
        Logger.Info($"Game auto-added: {exePath}");

        // App.xaml.cs owns the tray icon and shows the "added" balloon.
        GameAutoDetected?.Invoke(this, exePath);
    }

    public event EventHandler<string>? GameAutoDetected;

    private void OnTimerResolutionMeasured(object? sender, double measuredMs) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!TimerActive) return;
            ActualTimerMs = measuredMs;
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_TimerActive"), measuredMs);
        });

    // ── Property-change side effects ──────────────────────────────────────────

    partial void OnStandbyLimitMbChanged(int value)
    {
        SyncMonitorThresholds();
        SchedulePersist();  // debounced — TextBox emits a change per keystroke
    }

    partial void OnFreeLimitMbChanged(int value)
    {
        SyncMonitorThresholds();
        SchedulePersist();  // debounced — TextBox emits a change per keystroke
    }

    partial void OnAutoPurgeEnabledChanged(bool value)
    {
        SyncMonitorThresholds();
        PersistSettings();
    }

    partial void OnLightPurgeChanged(bool value)
    {
        // Light keeps the cache, deep empties more than a normal purge: one
        // or the other.
        if (value) DeepPurge = false;
        _purgeService.LightMode = value;
        UpdateClearable();
        PersistSettings();
    }

    partial void OnDeepPurgeChanged(bool value)
    {
        if (value) LightPurge = false;
        _purgeService.DeepMode = value;
        UpdateClearable();
        PersistSettings();
    }

    partial void OnAutoPurgeIdleOnlyChanged(bool value)
    {
        // "Only when idle" and "only while a game runs" exclude each other:
        // nobody is idle in the middle of a game, so both together would
        // never purge.
        if (value) AutoPurgeOnlyInGames = false;
        SyncMonitorThresholds();
        PersistSettings();
    }

    partial void OnAutoPurgeOnlyInGamesChanged(bool value)
    {
        if (value) AutoPurgeIdleOnly = false;
        SyncMonitorThresholds();
        PersistSettings();
    }

    partial void OnIdleThresholdMinutesChanged(int value)
    {
        SyncMonitorThresholds();
        SchedulePersist();  // debounced — TextBox emits a change per keystroke
    }

    partial void OnGameModeEnabledChanged(bool value)
    {
        SyncMonitorThresholds();
        PersistSettings();
    }

    partial void OnTimerActiveChanged(bool value) =>
        OnPropertyChanged(nameof(TimerButtonLabel));

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        RemoveGameCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedGameBoost));
        OnPropertyChanged(nameof(SelectedGameAutoPurge));
        OnPropertyChanged(nameof(SelectedGamePurgeOnStart));
    }

    // Per-game switches for the selected list entry (the strip under the
    // Game Mode list). Each change re-syncs the monitor and persists.
    public bool SelectedGameBoost
    {
        get => SelectedGame?.BoostPriority ?? true;
        set => SetSelectedGameRule(g => g.BoostPriority = value);
    }

    public bool SelectedGameAutoPurge
    {
        get => SelectedGame?.AllowAutoPurge ?? true;
        set => SetSelectedGameRule(g => g.AllowAutoPurge = value);
    }

    public bool SelectedGamePurgeOnStart
    {
        get => SelectedGame?.PurgeOnStart ?? false;
        set => SetSelectedGameRule(g => g.PurgeOnStart = value);
    }

    private void SetSelectedGameRule(Action<GameEntry> apply)
    {
        if (SelectedGame is null) return;
        apply(SelectedGame);
        OnPropertyChanged(nameof(SelectedGameBoost));
        OnPropertyChanged(nameof(SelectedGameAutoPurge));
        OnPropertyChanged(nameof(SelectedGamePurgeOnStart));
        SyncMonitorGames();
        PersistSettings();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

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

    [RelayCommand]
    private async Task ToggleTimerResolutionAsync()
    {
        _timerStartedForGame = false;   // the user is in charge now
        _timerPausedForIdle  = false;
        if (TimerActive)
        {
            DeactivateTimer(notify: true);
            StatusMessage = _localization.GetString("Str_Status_TimerOff");
            Logger.Info("User toggled timer OFF");
        }
        else
        {
            await ActivateTimerInternalAsync(silent: false).ConfigureAwait(true);
        }
        PersistSettings();
    }

    // Timer card lock, cycling Unlocked -> Locked -> InGame -> Unlocked.
    // Locked switches the timer on now ("always on"). InGame hands it to
    // the games: on now if one runs, and from then on it follows games
    // starting and exiting; a timer already on stays on. Unlocked leaves
    // the running session alone and only affects the next start. App
    // auto-start is a separate setting.
    [RelayCommand]
    private async Task ToggleTimerLockAsync()
    {
        TimerLock = TimerLock switch
        {
            TimerLockMode.Unlocked => TimerLockMode.Locked,
            TimerLockMode.Locked   => TimerLockMode.InGame,
            _                      => TimerLockMode.Unlocked,
        };
        _timerStartedForGame = false;

        switch (TimerLock)
        {
            case TimerLockMode.Locked when !TimerActive:
                await ActivateTimerInternalAsync(silent: false).ConfigureAwait(true);
                break;
            case TimerLockMode.InGame when _anyGameRunning && !TimerActive:
                await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
                _timerStartedForGame = true;
                break;
            // InGame with no game running leaves a running timer alone:
            // the cycle passes through InGame on the way from Locked to
            // Unlocked, and unlocking must not switch the timer off.
        }

        StatusMessage = _localization.GetString(TimerLock switch
        {
            TimerLockMode.Locked => "Str_Status_TimerLockedOn",
            TimerLockMode.InGame => "Str_Status_TimerLockedInGame",
            _                    => "Str_Status_TimerLockedOff",
        });
        Logger.Info($"Timer lock {TimerLock} (timer active={TimerActive})");
        PersistSettings();
    }

    // notify: tray balloon. Only a TIMER press notifies; the in-game lock
    // switches quietly (its activation is silent too) and says so in the
    // status line instead.
    private void DeactivateTimer(bool notify)
    {
        _timerService.Deactivate();
        TimerActive          = false;
        _timerStartedForGame = false;
        if (notify)
            TimerToggledNotification?.Invoke(this, new TimerToggledArgs(false, 0));
    }

    // Idle status line: the live timer reading while the timer runs, else "Ready".
    private void SetIdleStatus() =>
        StatusMessage = TimerActive
            ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_Status_TimerActive"), ActualTimerMs)
            : _localization.GetString("Str_Status_Ready");

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

    [RelayCommand]
    private void AddGame()
    {
        var dlg = new OpenFileDialog
        {
            Title  = _localization.GetString("Str_Game_FileDialogTitle"),
            Filter = _localization.GetString("Str_Game_FileDialogFilter")
        };
        if (dlg.ShowDialog() != true) return;
        AddGameFromPath(dlg.FileName);
    }

    // Tests swap in a fake; the app starts games through the shell.
    internal Func<string, bool> StartGame { get; set; } = GameLauncher.StartUnelevated;

    /// <summary>Double-click on a listed game: start it, not elevated (see GameLauncher).</summary>
    public void LaunchGame(GameEntry game)
    {
        if (!File.Exists(game.ExecutablePath))
        {
            StatusMessage = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Status_GameMissing"), game.DisplayName);
            return;
        }
        bool ok = StartGame(game.ExecutablePath);
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            _localization.GetString(ok ? "Str_Status_GameLaunching" : "Str_Status_GameLaunchFailed"), game.DisplayName);
    }

    public void AddGameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

        if (Games.Any(g => g.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = _localization.GetString("Str_Status_GameExists");
            return;
        }

        Games.Add(new GameEntry
        {
            DisplayName    = Path.GetFileNameWithoutExtension(path),
            ExecutablePath = path
        });
        // An explicit add overrides an earlier dismissal of the same exe.
        _ignoredGamePaths.Remove(path);
        SyncMonitorGames();
        PersistSettings();
        StatusMessage = _localization.GetString("Str_Status_GameAdded");
        Logger.Info($"Game added: {path}");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveGame))]
    private void RemoveGame()
    {
        if (SelectedGame is null) return;
        // Removing an auto-added game means "this is not a game": remember it
        // so detection never adds it again. User-added games just go.
        if (SelectedGame.AutoAdded)
            _ignoredGamePaths.Add(SelectedGame.ExecutablePath);
        Games.Remove(SelectedGame);
        SelectedGame  = null;
        SyncMonitorGames();
        PersistSettings();
        StatusMessage = _localization.GetString("Str_Status_GameRemoved");
    }

    private bool CanRemoveGame() => SelectedGame is not null;

    [RelayCommand]
    private void ShowAbout()
    {
        OnPropertyChanged(nameof(AboutTitle));
        OnPropertyChanged(nameof(AboutClose));
        IsAboutVisible = true;
    }

    [RelayCommand]
    private void CloseAbout() => IsAboutVisible = false;

    [RelayCommand]
    private void DismissWizard()
    {
        IsWizardVisible    = false;
        _firstRunCompleted = true;
        // Persist immediately so the wizard doesn't return on the next launch
        // even if the process is killed before normal OnExit shutdown runs.
        _settingsService.Save(BuildCurrentSettings());
        SystemSettings.MaybeShowPrompt();
    }

    // UI-thread work from InitializeAsync, which may resume off it. Inline
    // when already on the UI thread (a blocked caller would deadlock on
    // InvokeAsync) or without a WPF Application (unit tests).
    private static Task OnUiAsync(Action action)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess()) return d.InvokeAsync(action).Task;
        action();
        return Task.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task ActivateTimerInternalAsync(bool silent)
    {
        double ms = await _timerService.ActivateAsync(_appCts.Token).ConfigureAwait(true);
        ActualTimerMs = ms;
        TimerActive   = true;
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Str_Status_TimerActive"), ms);
        if (!silent)
            TimerToggledNotification?.Invoke(this, new TimerToggledArgs(true, ms));
        Logger.Info(FormattableString.Invariant($"Timer activated (silent={silent}, actual={ms:F3} ms)"));
    }

    // ── Shutdown (called from App.OnExit) ─────────────────────────────────────

    public async Task ShutdownAsync()
    {
        // Cancel any pending debounced save — we're about to write the final
        // snapshot synchronously, so a delayed write would be redundant or stale.
        FlushPendingPersist();

        await _appCts.CancelAsync().ConfigureAwait(false);
        await _memoryService.StopAsync().ConfigureAwait(false);
        await _gameDetection.StopAsync().ConfigureAwait(false);
        _settingsService.Save(BuildCurrentSettings());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        FlushPendingPersist();

        _memoryService.SnapshotUpdated     -= OnSnapshotUpdated;
        _purgeService.PurgeSucceeded       -= OnPurgeSucceeded;
        _localization.LanguageChanged      -= OnLanguageChanged;
        _timerService.ResolutionMeasured   -= OnTimerResolutionMeasured;
        _gameDetection.GameDetected        -= OnGameDetected;

        _timerService.Dispose();
        _memoryService.Dispose();
        _gameDetection.Dispose();
        _appCts.Dispose();
    }
}

public sealed record TimerToggledArgs(bool IsActive, double ActualMs);

public sealed record GameStartedArgs(string GameName, string Body);

/// <summary>A finished purge for the tray balloon, with the session's purge count.</summary>
public sealed record PurgeNotice(PurgeResult Result, int TotalPurges);

/// <summary>One line of the purge history: the result and its text in the current language.</summary>
public sealed record PurgeHistoryRow(PurgeResult Result, string Time, string What, string Freed);

/// <summary>One day in the history bar chart; <see cref="Height"/> is in pixels.</summary>
public sealed record DayBar(string Day, double Height, bool IsToday, string Tip);
