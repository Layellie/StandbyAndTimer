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

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private long    _totalRamMb;
    [ObservableProperty] private long    _freeRamMb;
    [ObservableProperty] private long    _standbyRamMb;
    [ObservableProperty] private int     _purgeCount;
    [ObservableProperty] private int     _standbyLimitMb;
    [ObservableProperty] private int     _freeLimitMb;
    [ObservableProperty] private bool    _autoPurgeEnabled;
    [ObservableProperty] private bool    _autoPurgeIdleOnly;
    [ObservableProperty] private int     _idleThresholdMinutes = 5;
    [ObservableProperty] private bool    _gameModeEnabled;
    [ObservableProperty] private bool    _timerActive;
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
    public string AboutText  => _localization.GetString("Str_About_Text");
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

        if (settings.TimerResolutionActive)
            await ActivateTimerInternalAsync(silent: true).ConfigureAwait(true);

        StatusMessage = _localization.GetString("Str_Status_Ready");

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
            IdleThresholdMinutes = s.IdleThresholdMinutes > 0 ? s.IdleThresholdMinutes : 5;
            GameModeEnabled      = s.GameModeEnabled;
            TimerActive          = s.TimerResolutionActive;
            _firstRunCompleted   = s.FirstRunCompleted;

            Games.Clear();
            foreach (var g in s.Games)
                Games.Add(g);

            Settings.Initialize(s.AutoStartEnabled, s.Language, s.UpdateCheckEnabled, s.Theme);
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
        _memoryService.IdleThresholdMs   = Math.Max(0, IdleThresholdMinutes) * 60_000;
        _memoryService.GameModeEnabled   = GameModeEnabled;
    }

    // Pushes the executable-path list to the monitor. Called only when the
    // Games collection actually mutates (add / remove / settings load / import).
    private void SyncMonitorGames()
    {
        var paths = Games.Select(g => g.ExecutablePath).ToList();
        _memoryService.GamePaths = paths;
        // Also keep the detection service in sync — without this, a game the
        // user just added would still trigger a "fullscreen detected" balloon
        // on the next poll tick.
        _gameDetection.KnownGamePaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    }

    private void PersistSettings()
    {
        if (_isInitializing) return;
        _settingsService.Save(BuildCurrentSettings());
    }

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
        IdleThresholdMinutes  = IdleThresholdMinutes,
        GameModeEnabled       = GameModeEnabled,
        AutoStartEnabled      = Settings.AutoStartEnabled,
        TimerResolutionActive = TimerActive,
        Language              = Settings.SelectedLanguage,
        Theme                 = Settings.SelectedTheme,
        UpdateCheckEnabled    = Settings.UpdateCheckEnabled,
        FirstRunCompleted     = _firstRunCompleted,
        Games                 = [.. Games]
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
        });

    private void OnPurgeSucceeded(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            PurgeCount++;
            PurgeNotification?.Invoke(this, PurgeCount);
        });

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusMessage = _localization.GetString("Str_Status_Ready");
            OnPropertyChanged(nameof(TimerButtonLabel));
            OnPropertyChanged(nameof(AboutTitle));
            OnPropertyChanged(nameof(AboutText));
            OnPropertyChanged(nameof(AboutClose));
            OnPropertyChanged(nameof(WizardTitle));
            OnPropertyChanged(nameof(WizardBody));
            OnPropertyChanged(nameof(WizardFinish));
        });

    private void OnGameDetected(object? sender, string exePath) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Surface the candidate to App.xaml.cs which owns the tray icon
            // and can show a balloon. We don't auto-add — that would be
            // surprising behavior and could mis-promote a fullscreen video
            // player. The user decides.
            GameAutoDetected?.Invoke(this, exePath);
        });

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

    partial void OnAutoPurgeIdleOnlyChanged(bool value)
    {
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

    partial void OnSelectedGameChanged(GameEntry? value) =>
        RemoveGameCommand.NotifyCanExecuteChanged();

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
        if (TimerActive)
        {
            _timerService.Deactivate();
            TimerActive   = false;
            StatusMessage = _localization.GetString("Str_Status_TimerOff");
            TimerToggledNotification?.Invoke(this, new TimerToggledArgs(false, 0));
            Logger.Info("User toggled timer OFF");
        }
        else
        {
            await ActivateTimerInternalAsync(silent: false).ConfigureAwait(true);
        }
        PersistSettings();
    }

    [RelayCommand]
    private void OpenSettings() => Settings.IsOpen = true;

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
        SyncMonitorGames();
        PersistSettings();
        StatusMessage = _localization.GetString("Str_Status_GameAdded");
        Logger.Info($"Game added: {path}");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveGame))]
    private void RemoveGame()
    {
        if (SelectedGame is null) return;
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
        OnPropertyChanged(nameof(AboutText));
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
