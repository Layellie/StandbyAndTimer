using System.Collections.ObjectModel;
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
    private readonly CancellationTokenSource     _appCts = new();

    private bool _isInitializing;
    private bool _disposed;

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private long    _totalRamMb;
    [ObservableProperty] private long    _freeRamMb;
    [ObservableProperty] private long    _standbyRamMb;
    [ObservableProperty] private int     _purgeCount;
    [ObservableProperty] private int     _standbyLimitMb = 1024;
    [ObservableProperty] private int     _freeLimitMb    = 1024;
    [ObservableProperty] private bool    _gameModeEnabled;
    [ObservableProperty] private bool    _timerActive;
    [ObservableProperty] private double  _actualTimerMs;
    [ObservableProperty] private string  _statusMessage  = string.Empty;
    [ObservableProperty] private GameEntry? _selectedGame;
    [ObservableProperty] private bool    _isAboutVisible;

    public ObservableCollection<GameEntry> Games    { get; } = [];
    public SettingsViewModel               Settings { get; }

    public string TimerButtonLabel => _localization.GetString(
        TimerActive ? "Str_Timer_Active" : "Str_Timer_Inactive");

    public string AboutTitle => _localization.GetString("Str_About_Title");
    public string AboutText  => _localization.GetString("Str_About_Text");
    public string AboutClose => _localization.GetString("Str_About_Close");

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
        SettingsViewModel           settingsViewModel)
    {
        _timerService    = timerService;
        _purgeService    = purgeService;
        _memoryService   = memoryService;
        _settingsService = settingsService;
        _localization    = localization;

        Settings = settingsViewModel;
        Settings.SettingsChanged += (_, _) => PersistSettings();
        Settings.SnapshotProvider = BuildCurrentSettings;

        _memoryService.SnapshotUpdated += OnSnapshotUpdated;
        _purgeService.PurgeSucceeded   += OnPurgeSucceeded;
        _localization.LanguageChanged  += OnLanguageChanged;
    }

    // ── Initialisation (called once from App.OnStartup) ───────────────────────

    public async Task InitializeAsync()
    {
        var settings = _settingsService.Load();
        ApplySettings(settings);

        System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
            System.Diagnostics.ProcessPriorityClass.High;

        if (settings.TimerResolutionActive)
            ActivateTimerInternal(silent: true);

        StatusMessage = _localization.GetString("Str_Status_Ready");

        await _memoryService.StartAsync(_appCts.Token).ConfigureAwait(false);
    }

    // ── Settings helpers ──────────────────────────────────────────────────────

    private void ApplySettings(AppSettings s)
    {
        _isInitializing = true;
        try
        {
            StandbyLimitMb   = s.StandbyLimitMb;
            FreeLimitMb      = s.FreeLimitMb;
            GameModeEnabled  = s.GameModeEnabled;
            TimerActive      = s.TimerResolutionActive;

            Games.Clear();
            foreach (var g in s.Games)
                Games.Add(g);

            Settings.Initialize(s.AutoStartEnabled, s.Language, s.UpdateCheckEnabled, s.Theme);
        }
        finally
        {
            _isInitializing = false;
        }
        SyncMonitorSettings();
    }

    private void SyncMonitorSettings()
    {
        _memoryService.StandbyLimitMb  = StandbyLimitMb;
        _memoryService.FreeLimitMb     = FreeLimitMb;
        _memoryService.GameModeEnabled = GameModeEnabled;
        _memoryService.GamePaths       = Games.Select(g => g.ExecutablePath).ToList();
    }

    private void PersistSettings()
    {
        if (_isInitializing) return;
        _settingsService.Save(BuildCurrentSettings());
    }

    internal AppSettings BuildCurrentSettings() => new()
    {
        StandbyLimitMb        = StandbyLimitMb,
        FreeLimitMb           = FreeLimitMb,
        GameModeEnabled       = GameModeEnabled,
        AutoStartEnabled      = Settings.AutoStartEnabled,
        TimerResolutionActive = TimerActive,
        Language              = Settings.SelectedLanguage,
        Theme                 = Settings.SelectedTheme,
        UpdateCheckEnabled    = Settings.UpdateCheckEnabled,
        Games                 = [.. Games]
    };

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
        });

    // ── Property-change side effects ──────────────────────────────────────────

    partial void OnStandbyLimitMbChanged(int value)
    {
        SyncMonitorSettings();
        PersistSettings();
    }

    partial void OnFreeLimitMbChanged(int value)
    {
        SyncMonitorSettings();
        PersistSettings();
    }

    partial void OnGameModeEnabledChanged(bool value)
    {
        SyncMonitorSettings();
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
    private void ToggleTimerResolution()
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
            ActivateTimerInternal(silent: false);
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
        SyncMonitorSettings();
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
        SyncMonitorSettings();
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

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ActivateTimerInternal(bool silent)
    {
        double ms = _timerService.Activate();
        ActualTimerMs = ms;
        TimerActive   = true;
        StatusMessage = string.Format(_localization.GetString("Str_Status_TimerActive"), ms);
        if (!silent)
            TimerToggledNotification?.Invoke(this, new TimerToggledArgs(true, ms));
        Logger.Info($"Timer activated (silent={silent}, actual={ms:F3} ms)");
    }

    // ── Shutdown (called from App.OnExit) ─────────────────────────────────────

    public async Task ShutdownAsync()
    {
        await _appCts.CancelAsync().ConfigureAwait(false);
        await _memoryService.StopAsync().ConfigureAwait(false);
        _settingsService.Save(BuildCurrentSettings());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _memoryService.SnapshotUpdated -= OnSnapshotUpdated;
        _purgeService.PurgeSucceeded   -= OnPurgeSucceeded;
        _localization.LanguageChanged  -= OnLanguageChanged;

        _timerService.Dispose();
        _memoryService.Dispose();
        _appCts.Dispose();
    }
}

public sealed record TimerToggledArgs(bool IsActive, double ActualMs);
