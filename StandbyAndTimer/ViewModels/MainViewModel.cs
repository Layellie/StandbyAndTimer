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
    // "Turn the timer off on battery": the same pause, plus whether the user
    // pressed TIMER (or the lock) while the battery rule applied; that stays
    // in charge until the power source changes.
    private bool _timerPausedForBattery;
    private bool _timerOnByUserOnBattery;
    private bool _onBattery;
    // Listed games running at the last tick, to spot the ones that just started.
    private readonly HashSet<string> _runningGames = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private double  _actualTimerMs;
    [ObservableProperty] private string  _statusMessage  = string.Empty;
    [ObservableProperty] private GameEntry? _selectedGame;
    [ObservableProperty] private bool    _isAboutVisible;
    [ObservableProperty] private bool    _isWizardVisible;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledGamesVisible))]
    private InstalledGamesViewModel? _installedGamesDialog;

    public bool IsInstalledGamesVisible => InstalledGamesDialog is not null;

    public ObservableCollection<GameEntry> Games    { get; } = [];
    public SettingsViewModel               Settings { get; }
    public SystemViewModel                 SystemSettings { get; }

    /// <summary>The Maintenance tab (created by DI; inert in tests).</summary>
    public MaintenanceViewModel Maintenance { get; }

    // Main TabControl: Status | Maintenance | Settings (bound to SelectedIndex).
    public const int StatusTab = 0, MaintenanceTab = 1, SettingsTab = 2;

    [ObservableProperty] private int _selectedTab;

    partial void OnSelectedTabChanged(int value)
    {
        if (Settings.IsOpen != (value == SettingsTab)) Settings.IsOpen = value == SettingsTab;
        Maintenance.IsActive = value == MaintenanceTab;
    }

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
        IPurgeStatsStore?           purgeStats      = null,
        MaintenanceViewModel?       maintenance     = null,
        IDisplayInfoService?        display         = null)
    {
        _display         = display;
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
            imported.AutoCleanLastRunUtc       = now.AutoCleanLastRunUtc;   // this PC's own last run
            ApplySettings(imported);
        };
        Settings.SnapshotProvider  = BuildCurrentSettings;

        SystemSettings = systemViewModel ?? SystemViewModel.Inert();

        _purgeStore = purgeStats ?? new InMemoryPurgeStatsStore();
        _purgeStats = _purgeStore.Load();
        foreach (var r in _purgeStats.Recent) PurgeHistory.Add(RowFor(r));
        SystemSettings.SettingsChanged += (_, _) =>
        {
            SyncMonitorThresholds();
            PersistSettings();
        };

        Maintenance = maintenance ?? MaintenanceViewModel.Inert();
        Maintenance.SettingsChanged += (_, _) => PersistSettings();
        Maintenance.StatusRequested += (_, text) => StatusMessage = text;
        Maintenance.AutoCleaned     += (_, bytes) =>
        {
            if (!Settings.NotifyOnAutoClean) return;   // Settings -> Notifications; the status line still says it
            AutoMaintenanceNotification?.Invoke(this,
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Str_Tray_NotifyAutoCleanBody"),
                              MainViewModel.FormatSize(bytes / (1024 * 1024))));
        };
        Maintenance.AnyGameRunning  = () => _lastSnapshot?.AnyGameRunning == true;
        // The Settings tab can also be opened from outside (tray "Settings"):
        // keep the selected tab in step with Settings.IsOpen.
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.IsOpen)) return;
            if (Settings.IsOpen && SelectedTab != SettingsTab)       SelectedTab = SettingsTab;
            else if (!Settings.IsOpen && SelectedTab == SettingsTab) SelectedTab = StatusTab;
        };

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

        // Refresh rate on the timer card: read now and when Windows says the
        // display mode changed. No polling.
        UpdateRefreshRate();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Power source: read now and when Windows says it changed. No polling.
        _onBattery = ReadOnBattery();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // The process stays at Normal priority (it used to raise itself to
        // High here). The timer watchdog has its own thread priority (MMCSS);
        // the window, the monitor loop and a purge's kernel work have no
        // reason to run ahead of a game.

        // Only the lock turns the timer on at start-up. This used to restore
        // the last session's state, so a timer left on once came back by
        // itself on every launch.
        if (TimerLock == TimerLockMode.Locked)
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);
        await ApplyBatteryStateAsync(_onBattery, anyGameRunning: false).ConfigureAwait(true);
        ShowWindowsResolution();
        AutoMaintenanceTask = RunAutoMaintenanceAsync();

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
            // A game the user added is never dropped (the next save would erase
            // it): only exact duplicates, and AUTO-added helpers or duplicates.
            foreach (var g in s.Games)
            {
                if (Games.Any(x => x.ExecutablePath.Equals(g.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (g.AutoAdded
                    && (InstalledGamesScanner.IsIgnoredExecutable(Path.GetFileNameWithoutExtension(g.ExecutablePath))
                        || GameDetectionService.IsIgnoredProcess(Path.GetFileNameWithoutExtension(g.ExecutablePath))
                        || IsGameAlreadyListed(g.ExecutablePath)))
                    continue;
                Games.Add(g);
            }

            _ignoredGamePaths.Clear();
            foreach (var p in s.IgnoredGamePaths ?? [])
                _ignoredGamePaths.Add(p);

            Settings.Initialize(
                s.AutoStartEnabled, s.Language, s.UpdateCheckEnabled, s.Theme,
                s.NotifyOnPurge, s.NotifyOnTimerToggle, s.NotifyOnGameDetected,
                s.PurgeHotkey, s.TimerHotkey, s.AutoPurgeHotkey,
                s.TimerIdleOffEnabled, s.TimerIdleOffMinutes, s.TimerOffOnBattery, s.NotifyOnAutoClean);
            SystemSettings.Initialize(s.GlobalTimerPromptAnswered, s.GlobalTimerSetByApp,
                              s.GlobalTimerChangedUtc, s.GlobalTimerBootValue,
                              s.AutoPurgeCooldownEnabled, s.AutoPurgeCooldownSeconds,
                              s.CloseToTrayDirectly);
            Maintenance.Initialize(s.CleanupSelection);
            Maintenance.InitializeAuto(s.AutoCleanEnabled, s.AutoCleanIntervalDays, s.AutoCleanLastRunUtc);
        }
        SyncMonitorThresholds();
        SyncMonitorGames();
    }

    // Pushes only the threshold / flag state to the monitor — does NOT rebuild
    // the GamePaths list. Called on every TextBox keystroke, so the per-tick
    // LINQ + List allocation that GamePaths rebuilding would cost is wasted.
    private void SyncMonitorThresholds()
    {
        _memoryService.StandbyLimitMb           = StandbyLimitMb;
        _memoryService.FreeLimitMb              = FreeLimitMb;
        _memoryService.AutoPurgeEnabled         = AutoPurgeEnabled;
        _memoryService.AutoPurgeIdleOnly        = AutoPurgeIdleOnly;
        _memoryService.AutoPurgeOnlyInGames     = AutoPurgeOnlyInGames;
        _memoryService.IdleThresholdMs          = Math.Max(0, IdleThresholdMinutes) * 60_000;
        _memoryService.AutoPurgeCooldownSeconds = SystemSettings.AutoPurgeCooldownEnabled ? SystemSettings.AutoPurgeCooldownSeconds : 0;
        _memoryService.GameModeEnabled          = GameModeEnabled;
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
        _memoryService.AboveNormalGamePaths = Games.Where(g => g.Priority == GamePriority.AboveNormal).Select(g => g.ExecutablePath).ToList();
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
        TimerResolutionActive = TimerActive || _timerPausedForIdle || _timerPausedForBattery,   // a pause is not "off"
        TimerLock             = TimerLock,
        TimerLocked           = TimerLock == TimerLockMode.Locked,   // for 2.2.x
        Language              = Settings.SelectedLanguage,
        Theme                 = Settings.SelectedTheme,
        UpdateCheckEnabled    = Settings.UpdateCheckEnabled,
        NotifyOnPurge         = Settings.NotifyOnPurge,
        NotifyOnTimerToggle   = Settings.NotifyOnTimerToggle,
        NotifyOnGameDetected  = Settings.NotifyOnGameDetected,
        NotifyOnAutoClean     = Settings.NotifyOnAutoClean,
        PurgeHotkey           = Settings.PurgeHotkeyText,
        TimerHotkey           = Settings.TimerHotkeyText,
        AutoPurgeHotkey       = Settings.AutoPurgeHotkeyText,
        TimerIdleOffEnabled   = Settings.TimerIdleOffEnabled,
        TimerIdleOffMinutes   = Settings.TimerIdleOffMinutes,
        TimerOffOnBattery     = Settings.TimerOffOnBattery,
        GlobalTimerPromptAnswered = SystemSettings.PromptAnswered,
        GlobalTimerSetByApp       = SystemSettings.GlobalTimerSetByApp,
        GlobalTimerChangedUtc     = SystemSettings.GlobalTimerChangedUtc,
        GlobalTimerBootValue      = SystemSettings.GlobalTimerBootValue,
        AutoPurgeCooldownEnabled  = SystemSettings.AutoPurgeCooldownEnabled,
        AutoPurgeCooldownSeconds  = SystemSettings.AutoPurgeCooldownSeconds,
        CloseToTrayDirectly       = SystemSettings.CloseToTrayDirectly,
        FirstRunCompleted     = _firstRunCompleted,
        Games                 = [.. Games],
        IgnoredGamePaths      = [.. _ignoredGamePaths],
        AutoCleanEnabled      = Maintenance.AutoCleanEnabled,
        AutoCleanIntervalDays = Maintenance.AutoCleanIntervalDays,
        AutoCleanLastRunUtc   = Maintenance.AutoCleanLastRunUtc,
        CleanupSelection      = Maintenance.SelectionText
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

    // The last tick's snapshot: the purge card, the battery rule and the
    // Maintenance tab ("does a game run?") read it between ticks.
    private MemorySnapshot? _lastSnapshot;

    internal void ShowSnapshot(MemorySnapshot snapshot)
    {
        TotalRamMb    = snapshot.TotalMb;
        FreeRamMb     = snapshot.FreeMb;
        StandbyRamMb  = snapshot.StandbyMb;

        // Once a second, also in the tray: the card's label does not depend
        // on the numbers, and its tooltip is rebuilt only when one of the
        // three numbers in it moved.
        bool tipChanged = _lastSnapshot is not { } last
                       || last.StandbyMb    != snapshot.StandbyMb
                       || last.StandbyLowMb != snapshot.StandbyLowMb
                       || last.ModifiedMb   != snapshot.ModifiedMb;
        _lastSnapshot = snapshot;
        ClearableMb   = snapshot.ClearableMb(SelectedPurgeKind);
        if (tipChanged) OnPropertyChanged(nameof(ClearableTip));
        ApplyBoostRefusals(snapshot.BoostRefusedPaths);
    }

    // Games first: a game starting while the timer is paused for idle is
    // the in-game lock's to switch on.
    private async Task HandleTickAsync(MemorySnapshot snapshot)
    {
        try
        {
            await HandleRunningGamesAsync(snapshot.RunningGamePaths ?? []).ConfigureAwait(true);
            await ApplyIdleStateAsync(snapshot.IdleFor, snapshot.AnyGameRunning).ConfigureAwait(true);
            await ApplyBatteryStateAsync(_onBattery, snapshot.AnyGameRunning).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn($"Tick handling: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Auto maintenance at start-up ─────────────────────────────────────────

    /// <summary>"Auto maintenance cleaned 1.2 GB" for a tray balloon (the safe cleanups ran by themselves).</summary>
    public event EventHandler<string>? AutoMaintenanceNotification;

    /// <summary>The wait before an unattended cleanup, so it never competes with the start-up (tests skip it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> AutoMaintenanceDelay { get; set; } = Task.Delay;
    internal Task? AutoMaintenanceTask { get; private set; }
    private static readonly TimeSpan AutoMaintenanceWait = TimeSpan.FromSeconds(30);

    // One look at start-up, not a timer: when the chosen number of days has
    // passed, the safe cleanups run once, a little after the window is up.
    private async Task RunAutoMaintenanceAsync()
    {
        try
        {
            if (!Maintenance.IsAutoDueNow) return;
            await AutoMaintenanceDelay(AutoMaintenanceWait, _appCts.Token).ConfigureAwait(false);
            await OnUiAsync(() => Maintenance.RunAutoIfDueAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"Auto maintenance: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Language ─────────────────────────────────────────────────────────────

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

    // ── The usage guide (?) and the first-run wizard ─────────────────────────

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

    private static Task OnUiAsync(Func<Task> work)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess()) return d.InvokeAsync(work).Task.Unwrap();
        return work();
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
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged       -= OnPowerModeChanged;

        InstalledGamesDialog?.Dispose();
        _timerService.Dispose();
        _memoryService.Dispose();
        _gameDetection.Dispose();
        _appCts.Dispose();
    }
}

public sealed record TimerToggledArgs(bool IsActive, double ActualMs);

public sealed record GameStartedArgs(string GameName, string Body);

/// <summary>The Game Mode strip's "Purge once" menu: PurgeOnStart / PurgeOnExit in one choice.</summary>
public enum PurgeOnceOption { Off, AtStart, AtExit, Both }

/// <summary>A finished purge for the tray balloon, with the session's purge count.</summary>
public sealed record PurgeNotice(PurgeResult Result, int TotalPurges);

/// <summary>One line of the purge history: the result and its text in the current language.</summary>
public sealed record PurgeHistoryRow(PurgeResult Result, string Time, string What, string Freed);

/// <summary>One day in the history bar chart; <see cref="Height"/> is in pixels.</summary>
public sealed record DayBar(string Day, double Height, bool IsToday, string Tip);
