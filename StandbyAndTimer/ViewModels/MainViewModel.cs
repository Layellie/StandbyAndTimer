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
    [ObservableProperty] private double  _actualTimerMs;
    [ObservableProperty] private string  _statusMessage  = string.Empty;
    [ObservableProperty] private GameEntry? _selectedGame;
    [ObservableProperty] private bool    _isAboutVisible;
    [ObservableProperty] private bool    _isWizardVisible;

    public ObservableCollection<GameEntry> Games    { get; } = [];
    public SettingsViewModel               Settings { get; }

    public string TimerButtonLabel => _localization.GetString(
        TimerActive ? "Str_Timer_Active" : "Str_Timer_Inactive");

    public string AboutTitle => _localization.GetString("Str_About_Title");
    public string AboutClose => _localization.GetString("Str_About_Close");

    public string WizardTitle  => _localization.GetString("Str_Wizard_Title");
    public string WizardBody   => _localization.GetString("Str_Wizard_Body");
    public string WizardFinish => _localization.GetString("Str_Wizard_Finish");

    // Raised once a manual or auto purge succeeds — App.xaml.cs subscribes to
    // show a balloon notification without needing to know about MVVM internals.
    public event EventHandler<int>? PurgeNotification;

    // Raised when timer was toggled (true=on, false=off). Includes actual ms.
    public event EventHandler<TimerToggledArgs>? TimerToggledNotification;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel(
        ITimerResolutionService     timerService,
        IStandbyPurgeService        purgeService,
        IMemoryMonitorService       memoryService,
        ISettingsService            settingsService,
        ILocalizationService        localization,
        IGameDetectionService       gameDetection,
        SettingsViewModel           settingsViewModel)
    {
        _timerService    = timerService;
        _purgeService    = purgeService;
        _memoryService   = memoryService;
        _settingsService = settingsService;
        _localization    = localization;
        _gameDetection   = gameDetection;

        Settings = settingsViewModel;
        Settings.SettingsChanged   += (_, _) => PersistSettings();
        Settings.SettingsImported  += (_, imported) => ApplySettings(imported);
        Settings.SnapshotProvider  = BuildCurrentSettings;

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
            LightPurge           = s.LightPurge;
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
                s.PurgeHotkey, s.TimerHotkey, s.AutoPurgeHotkey);
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
        IdleThresholdMinutes  = IdleThresholdMinutes,
        GameModeEnabled       = GameModeEnabled,
        AutoStartEnabled      = Settings.AutoStartEnabled,
        TimerResolutionActive = TimerActive,
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
            TotalRamMb   = snapshot.TotalMb;
            FreeRamMb    = snapshot.FreeMb;
            StandbyRamMb = snapshot.StandbyMb;
            _ = ApplyGameStateAsync(snapshot.AnyGameRunning);
        });

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

    private void OnPurgeSucceeded(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            PurgeCount++;
            PurgeNotification?.Invoke(this, PurgeCount);
        });

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SetIdleStatus();
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
        _purgeService.LightMode = value;
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

    private void SetSelectedGameRule(Action<GameEntry> apply)
    {
        if (SelectedGame is null) return;
        apply(SelectedGame);
        OnPropertyChanged(nameof(SelectedGameBoost));
        OnPropertyChanged(nameof(SelectedGameAutoPurge));
        SyncMonitorGames();
        PersistSettings();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ManualPurgeAsync()
    {
        StatusMessage = _localization.GetString("Str_Status_Purging");
        bool ok = await _purgeService.PurgeAsync().ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
            StatusMessage = _localization.GetString(ok ? "Str_Status_PurgeOk" : "Str_Status_PurgeFail"));
    }

    [RelayCommand]
    private async Task ToggleTimerResolutionAsync()
    {
        _timerStartedForGame = false;   // the user is in charge now
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
    // the games: on if one runs right now, off otherwise. Unlocked leaves
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
            case TimerLockMode.InGame when !_anyGameRunning && TimerActive:
                DeactivateTimer(notify: false);
                break;
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
