using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Infrastructure;
using StandbyAndTimer.Services;
using StandbyAndTimer.Services.Native;
using StandbyAndTimer.Services.Tray;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer;

// CA1001: App owns disposable icon/tooltip fields. The WPF Application's
// lifetime is the process; OnExit() releases everything, and the runtime
// never calls IDisposable.Dispose() on Application. Adding IDisposable here
// would mislead readers more than it would help.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Usage",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application lifetime is the process; cleanup happens in OnExit.")]
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    // A second launch opens the mutex without owning it; releasing it then
    // threw in OnExit and crashed that process (WER event 1000).
    private static bool   _ownsSingleInstanceMutex;

    private readonly CrashHandler _crashHandler = new();

    private IServiceProvider?       _services;
    private ILocalizationService?   _localization;
    private IThemeService?          _themeService;
    private ITrayIconService?       _tray;
    private MainViewModel?          _viewModel;
    private MainWindow?             _mainWindow;
    private EventWaitHandle?        _showWindowSignal;
    private CancellationTokenSource? _showWindowSignalCts;

    // Cached latest AppSettings snapshot. Used by the balloon handlers
    // (OnPurgeNotification / OnTimerToggledNotification / OnGameAutoDetected)
    // to skip raising the WinForms balloon when the user has opted out of
    // that particular notification kind. Refreshed via the SettingsPersisted
    // event MainViewModel raises after every successful Save.
    private AppSettings?            _userSettings;

    private System.Drawing.Icon?  _iconBase;
    private System.Drawing.Icon?  _iconActive;

    // ── Start-up ─────────────────────────────────────────────────────────────
    // OnStartup lists the steps in order; each step is a method below it.
    // The order matters: the crash handlers come first, the saved theme and
    // language before the window exists, the window before hotkeys (they
    // need its handle), and the view model's own start last, in the
    // background, so the window is on screen as early as possible.

    // Start-up timing in the log: "startup + N ms (total N ms) — step".
    private readonly Stopwatch _startupClock = new();
    private long _lastStartupMark;

    private void Mark(string step)
    {
        long now = _startupClock.ElapsedMilliseconds;
        Logger.Info($"  startup +{now - _lastStartupMark,4} ms (total {now,5} ms) — {step}");
        _lastStartupMark = now;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        _startupClock.Start();

        // 0. DLL planting hardening, then the crash handlers (the timer must
        //    be released on exit).
        HardenDllSearchPath();
        _crashHandler.Install();
        _crashHandler.AttachToDispatcher(this);
        Logger.Info($"=== Application starting (PID {Environment.ProcessId}) ===");
        Mark("crash handlers + hardening");

        // 1. One instance. A second launch asks the first to show its window
        //    and leaves.
        if (!BecomeTheSingleInstance())
        {
            Current.Shutdown();
            return;
        }
        base.OnStartup(e);
        Mark("single-instance + base.OnStartup");

        // 2. No Windows power throttling / EcoQoS for this process.
        DisablePowerThrottling();
        Mark("DisablePowerThrottling");

        // 3. Services and the view model.
        ResolveServices();

        // 4. The saved language and theme, before the window is created.
        AppSettings saved = ApplySavedLanguageAndTheme();
        SubscribeToViewModelAndTray();

        // 5. The window (hidden with "-hidden": the auto-start), the tray,
        //    the listener for second launches, the hotkeys.
        bool startHidden = e.Args.Contains("-hidden");
        CreateWindow(startHidden);
        ShowWindowAndTray(startHidden);
        StartShowWindowListener(_showWindowSignal!, _showWindowSignalCts!.Token);
        RegisterHotkeys(saved);

        // 6. The rest runs while the window is already up.
        _ = InitializeViewModelAsync();
        if (saved.UpdateCheckEnabled)
            _ = SilentUpdateCheckAsync();
        DeleteOldInstallersInBackground();
    }

    /// <summary>
    /// Single-instance guard. The primary acquires the mutex AND owns the
    /// named EventWaitHandle the listener thread blocks on. A secondary launch
    /// opens that handle by name, grants foreground rights (ASFW_ANY) so the
    /// primary's SetForegroundWindow call isn't muted by the foreground-lock
    /// timeout, signals it, then exits silently: no "already running" dialog.
    /// The primary listener dispatches to the UI thread and surfaces the
    /// window from offscreen / tray. Returns false in a secondary launch.
    /// </summary>
    private bool BecomeTheSingleInstance()
    {
        _singleInstanceMutex = new Mutex(true, AppConstants.SingleInstanceMutexName, out bool isNewInstance);
        _ownsSingleInstanceMutex = isNewInstance;
        if (!isNewInstance)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(AppConstants.ShowWindowSignalName);
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Primary hasn't published the handle yet (startup race) — nothing we can do but bail.
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Warn($"Single-instance signal: {ex.Message}");
            }
            return false;
        }

        _showWindowSignal    = new EventWaitHandle(false, EventResetMode.AutoReset, AppConstants.ShowWindowSignalName);
        _showWindowSignalCts = new CancellationTokenSource();
        return true;
    }

    private void ResolveServices()
    {
        _services = AppBootstrapper.Build();
        _crashHandler.SetServices(_services);
        Mark("DI container build");

        _viewModel    = _services.GetRequiredService<MainViewModel>();
        _localization = _services.GetRequiredService<ILocalizationService>();
        _themeService = _services.GetRequiredService<IThemeService>();
        _tray         = _services.GetRequiredService<ITrayIconService>();
        Mark("resolve viewmodel + services");
    }

    /// <summary>Loads the settings and applies the language and theme (the defaults, English and Dark, are already in place).</summary>
    private AppSettings ApplySavedLanguageAndTheme()
    {
        var savedSettings = _services!.GetRequiredService<ISettingsService>().Load();
        _userSettings = savedSettings;
        if (savedSettings.Language != Core.Models.Language.English)
            _localization!.SetLanguage(savedSettings.Language);
        if (savedSettings.Theme != Core.Models.Theme.Dark)
            _themeService!.SetTheme(savedSettings.Theme);
        Mark("settings + apply language/theme");
        return savedSettings;
    }

    private void SubscribeToViewModelAndTray()
    {
        var viewModel = _viewModel!;
        var tray      = _tray!;

        _localization!.LanguageChanged      += OnLanguageChanged;
        _themeService!.ThemeChanged         += OnThemeChanged;
        viewModel.PropertyChanged           += OnViewModelPropertyChanged;
        viewModel.PurgeNotification         += OnPurgeNotification;
        viewModel.TimerToggledNotification  += OnTimerToggledNotification;
        viewModel.GameStartedNotification   += OnGameStartedNotification;
        viewModel.AutoMaintenanceNotification += OnAutoMaintenanceNotification;
        viewModel.GameAutoDetected          += OnGameAutoDetected;
        viewModel.SettingsPersisted         += (_, latest) => _userSettings = latest;

        tray.ShowRequested        += (_, _) => ShowMainWindow();
        tray.SettingsRequested    += (_, _) => ShowSettings();
        tray.ToggleRequested      += (_, _) => ToggleMainWindow();
        tray.ExitRequested        += (_, _) => Shutdown();
        tray.TimerToggleRequested += (_, _) => viewModel.ToggleTimerResolutionCommand.Execute(null);
        tray.PurgeRequested       += (_, _) => viewModel.ManualPurgeCommand.Execute(null);
        // Tray quick settings: the same setters as the window, then refresh the labels now.
        tray.AutoPurgeToggleRequested += (_, _) => { viewModel.ToggleAutoPurgeCommand.Execute(null); RefreshTray(); };
        tray.DeepPurgeToggleRequested += (_, _) => { viewModel.DeepPurge = !viewModel.DeepPurge; RefreshTray(); };
        tray.GameModeToggleRequested  += (_, _) => { viewModel.GameModeEnabled = !viewModel.GameModeEnabled; RefreshTray(); };
        tray.TimerLockCycleRequested  += async (_, _) => { await viewModel.ToggleTimerLockCommand.ExecuteAsync(null); RefreshTray(); };

        // The tray's tooltip (with its RAM line) and menu labels are refreshed
        // when the mouse reaches the icon or the menu opens, and when a state
        // they show changes (OnViewModelPropertyChanged). A timer used to
        // rebuild them every 3 seconds for as long as the app ran.
        tray.RefreshRequested += (_, _) => RefreshTray();
    }

    private void CreateWindow(bool startHidden)
    {
        // Started hidden (auto-start): no GPU until the window is first
        // shown (MainWindow.ShowFromOffscreen switches back). Otherwise WPF
        // sets up Direct3D at start-up and the graphics driver keeps its
        // memory and threads while the window is never on screen. Measured:
        // 110 MB / 51 threads -> 71 MB / 28 threads in the tray.
        if (startHidden)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // Window / taskbar icon: none set, so WPF takes the exe's own icon
        // (every size, no decoding); Icon="/app_icon.ico" decoded all eight
        // images first (~65 ms measured).
        _mainWindow = new MainWindow(_viewModel!);
        _mainWindow.MinimizedToTrayOnClose += OnMinimizedToTrayOnClose;
        // The saved theme was applied before ThemeChanged was subscribed, so
        // hand the tray its starting palette explicitly.
        _tray!.ApplyTheme(EffectiveTheme());
        Mark("window");
    }

    /// <summary>
    /// Shows the window FIRST, so the user gets immediate visual feedback;
    /// the tray icon follows once the window is on screen (or right away
    /// when starting hidden).
    /// </summary>
    private void ShowWindowAndTray(bool startHidden)
    {
        if (startHidden)
        {
            _mainWindow!.HideOffscreen();
            InitTray();
        }
        else
            _mainWindow!.ShowCloaked(onScreen: InitTray);
        Mark("window shown");
    }

    // Tray icon + menu: ~60 ms on the UI thread (WinForms menu font on first
    // use, icon variants). Until it has run, the tray service skips refreshes
    // and balloons.
    private void InitTray()
    {
        if (_tray is null || _iconActive is not null) return;
        // Tray: grey while the timer is off, the lime app icon while it is on.
        _iconActive = AppIconLoader.LoadAppIcon();
        _iconBase   = AppIconLoader.BuildIdleIcon(_iconActive);
        _tray.Initialize(_iconBase, _iconActive);
        RefreshTray();
        Mark("tray");
    }

    /// <summary>
    /// Hotkeys require the HWND to exist, which is guaranteed after Show()
    /// (and after HideOffscreen, which creates it without showing the
    /// window). Each binding dispatches back to the UI thread; the command
    /// itself does the dispatcher hop work.
    /// </summary>
    private void RegisterHotkeys(AppSettings savedSettings)
    {
        var hotkeys = _services!.GetRequiredService<IHotkeyService>();
        hotkeys.AttachTo(_mainWindow!.WindowHandle);
        var vm = _viewModel!;
        Action HandlerFor(string id) => id switch
        {
            SettingsViewModel.HotkeyIdPurge     => () => vm.ManualPurgeCommand.Execute(null),
            SettingsViewModel.HotkeyIdTimer     => () => vm.ToggleTimerResolutionCommand.Execute(null),
            SettingsViewModel.HotkeyIdAutoPurge => () => vm.ToggleAutoPurgeCommand.Execute(null),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, null)
        };

        RegisterHotkeyFromSetting(hotkeys, SettingsViewModel.HotkeyIdPurge, savedSettings.PurgeHotkey,
            HandlerFor(SettingsViewModel.HotkeyIdPurge));
        RegisterHotkeyFromSetting(hotkeys, SettingsViewModel.HotkeyIdTimer, savedSettings.TimerHotkey,
            HandlerFor(SettingsViewModel.HotkeyIdTimer));
        RegisterHotkeyFromSetting(hotkeys, SettingsViewModel.HotkeyIdAutoPurge, savedSettings.AutoPurgeHotkey,
            HandlerFor(SettingsViewModel.HotkeyIdAutoPurge));

        // Settings → Hotkeys rebinding goes through the same registration path.
        vm.Settings.RegisterHotkey   = (id, text) => RegisterHotkeyFromSetting(hotkeys, id, text, HandlerFor(id));
        vm.Settings.UnregisterHotkey = hotkeys.Unregister;
        Mark("hotkeys registered");
    }

    // Installers downloaded by in-app updates; off the UI thread. The one
    // that just started us is still running for a few seconds, so retry.
    // %TEMP% held them up to 2.4.0-beta3; DownloadDirectory since.
    private static void DeleteOldInstallersInBackground()
    {
        foreach (string dir in new[] { UpdateService.DownloadDirectory, Path.GetTempPath() })
            _ = Task.Run(() => UpdateService.DeleteDownloadedInstallersAsync(
                dir, TimeSpan.FromSeconds(10), maxAttempts: 6));
    }

    private async Task InitializeViewModelAsync()
    {
        try
        {
            await _viewModel!.InitializeAsync().ConfigureAwait(true);
            Logger.Info($"  startup    background  (total {_startupClock.ElapsedMilliseconds,5} ms) — ViewModel.InitializeAsync done");
            RefreshTray();
        }
        catch (Exception ex)
        {
            Logger.Error("InitializeViewModelAsync", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("=== Application exiting ===");

        if (_localization is not null)
            _localization.LanguageChanged -= OnLanguageChanged;

        if (_themeService is not null)
            _themeService.ThemeChanged -= OnThemeChanged;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged          -= OnViewModelPropertyChanged;
            _viewModel.PurgeNotification        -= OnPurgeNotification;
            _viewModel.TimerToggledNotification -= OnTimerToggledNotification;
            _viewModel.GameStartedNotification  -= OnGameStartedNotification;
            _viewModel.AutoMaintenanceNotification -= OnAutoMaintenanceNotification;
            _viewModel.GameAutoDetected         -= OnGameAutoDetected;
        }

        if (_mainWindow is not null)
            _mainWindow.MinimizedToTrayOnClose -= OnMinimizedToTrayOnClose;

        // Release every Win32 RegisterHotKey before the HWND goes away —
        // otherwise the OS keeps the registrations bound to a dead window
        // and the next launch fails with "hot key already registered."
        (_services?.GetService<IHotkeyService>() as IDisposable)?.Dispose();

        _tray?.Dispose();

        AppIconLoader.DisposeIcon(ref _iconBase);
        AppIconLoader.DisposeIcon(ref _iconActive);

        // Task.Run hop so the async chain inside ShutdownAsync resumes on a
        // ThreadPool thread instead of the WPF dispatcher we're blocking with
        // GetResult — avoids deadlock if any callee ConfigureAwait(true)s.
        if (_viewModel is not null)
            Task.Run(_viewModel.ShutdownAsync).GetAwaiter().GetResult();
        (_services as IDisposable)?.Dispose();

        // Shut down the second-launch listener: cancel first (wakes WaitAny
        // via the cancellation handle), then dispose. The handle itself is
        // disposed last so the listener never observes a disposed handle.
        _showWindowSignalCts?.Cancel();
        _showWindowSignal?.Dispose();
        _showWindowSignalCts?.Dispose();

        if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    // ── Single-instance "show me" listener ───────────────────────────────────

    private void StartShowWindowListener(EventWaitHandle signal, CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { signal, ct.WaitHandle };
            try
            {
                while (true)
                {
                    int idx = WaitHandle.WaitAny(handles);
                    if (idx == 1) return;  // cancelled — shutting down
                    Dispatcher.BeginInvoke(new Action(SurfaceMainWindow));
                }
            }
            catch (ObjectDisposedException) { /* handles disposed during shutdown */ }
            catch (Exception ex)            { Logger.Warn($"ShowWindowListener: {ex.Message}"); }
        })
        {
            IsBackground = true,
            Name         = "ShowWindowListener"
        };
        thread.Start();
    }

    private void SurfaceMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.ShowFromOffscreen();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(hwnd);
    }

    // ── DLL search-path hardening ─────────────────────────────────────────────

    private static void HardenDllSearchPath()
    {
        try
        {
            // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS removes the legacy "current dir"
            // lookup (the planting vector) while still allowing the app dir,
            // user-added dirs (AddDllDirectory), and System32.
            if (!NativeMethods.SetDefaultDllDirectories(NativeMethods.LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
                Logger.Warn($"SetDefaultDllDirectories failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Logger.Warn($"HardenDllSearchPath: {ex.Message}"); }
    }

    // ── Power Throttling ──────────────────────────────────────────────────────

    private static void DisablePowerThrottling()
    {
        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
        {
            Version     = 1,
            ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                        | NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask   = 0
        };
        // PROCESS_INFORMATION_CLASS::ProcessPowerThrottling = 4. A prior typo
        // used 34 here — that value matches no class and SetProcessInformation
        // silently returned ERROR_INVALID_PARAMETER, leaving the kernel free
        // to ignore our NtSetTimerResolution(0.5 ms) request in the background.
        bool ok = NativeMethods.SetProcessInformation(
            Process.GetCurrentProcess().Handle,
            NativeMethods.ProcessPowerThrottling,
            ref state,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());

        if (!ok)
            Logger.Warn($"SetProcessInformation failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    // ── Event handlers / forwarders ──────────────────────────────────────────

    private void ShowMainWindow() => _mainWindow?.ShowFromOffscreen();

    // Tray "Settings": surface the window and switch to the Settings tab.
    // Settings.IsOpen keeps MainViewModel.SelectedTab on the Settings tab (and refreshes the logs).
    private void ShowSettings()
    {
        if (_mainWindow is null || _viewModel is null) return;
        SurfaceMainWindow();
        _viewModel.Settings.IsOpen = true;
    }

    private static bool RegisterHotkeyFromSetting(IHotkeyService svc, string id, string? text, Action handler)
    {
        var parsed = HotkeyBinding.Parse(text ?? string.Empty);
        if (parsed is null)
        {
            Logger.Warn($"Hotkey '{id}': could not parse '{text}', skipping");
            return false;
        }
        // Marshal the handler onto the dispatcher — RelayCommand.Execute on
        // off-thread can race with the WPF binding system. BeginInvoke is fine
        // here; the user won't notice a one-frame latency on a hotkey fire.
        return svc.Register(id, parsed, () =>
            Application.Current.Dispatcher.BeginInvoke(handler));
    }

    // Tray left-click semantics: if hidden → surface; if visible → hide.
    // Right-click "Show" still always surfaces (via ShowRequested).
    private void ToggleMainWindow()
    {
        if (_mainWindow is null) return;
        if (_mainWindow.IsOffscreen) SurfaceMainWindow();
        else                          _mainWindow.HideOffscreen();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(RefreshTray);

    private void OnThemeChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            _tray?.ApplyTheme(EffectiveTheme());

            if (_mainWindow is null) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle;
            StandbyAndTimer.MainWindow.ApplyDarkTitleBar(hwnd);
            StandbyAndTimer.MainWindow.ApplyBorderColor(hwnd);
        });

    // Theme.System is already resolved by EffectiveTheme; no service yet
    // keeps the dark default.
    private Core.Models.Theme EffectiveTheme() =>
        _themeService?.EffectiveTheme ?? Core.Models.Theme.Dark;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Everything the icon and the menu labels show (the tooltip's RAM
        // numbers are fetched when the mouse reaches the icon).
        if (e.PropertyName is nameof(MainViewModel.TimerActive)
                           or nameof(MainViewModel.PurgeCount)
                           or nameof(MainViewModel.AutoPurgeEnabled)
                           or nameof(MainViewModel.DeepPurge)
                           or nameof(MainViewModel.GameModeEnabled)
                           or nameof(MainViewModel.TimerLock))
            Dispatcher.InvokeAsync(RefreshTray);
    }

    private void RefreshTray()
    {
        if (_tray is null || _viewModel is null) return;
        _tray.Refresh(new TrayState(_viewModel.TimerActive, _viewModel.PurgeCount,
            _viewModel.DeepPurge, _viewModel.GameModeEnabled, _viewModel.TimerLock,
            _viewModel.AutoPurgeEnabled, _viewModel.FreeRamMb, _viewModel.StandbyRamMb));
    }

    // `_localization!`: these handlers are only subscribed AFTER _localization
    // is resolved from the DI container in OnStartup, and unsubscribed at the
    // top of OnExit. Non-null for the lifetime of the subscription.
    // "1.8 GB freed · Deep, at game start · Total purges: 3"
    private void OnPurgeNotification(object? sender, PurgeNotice notice)
    {
        if (_userSettings is { NotifyOnPurge: false }) return;
        string title = _localization!.GetString("Str_Tray_NotifyPurgeTitle");
        string body  = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Str_Tray_NotifyPurgeBody"),
            MainViewModel.FormatSize(notice.Result.FreedMb),
            _viewModel!.DescribePurge(notice.Result),
            notice.TotalPurges);
        _tray?.ShowBalloon(title, body);
    }

    private void OnGameAutoDetected(object? sender, string exePath)
    {
        if (_userSettings is { NotifyOnGameDetected: false }) return;
        try
        {
            string fileName = Path.GetFileName(exePath);
            string title    = _localization!.GetString("Str_Detect_NotifyTitle");
            string body     = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetString("Str_Detect_NotifyBody"),
                fileName);
            _tray?.ShowBalloon(title, body);
            Logger.Info($"GameDetection balloon shown for {fileName}");
        }
        catch (Exception ex) { Logger.Warn($"OnGameAutoDetected: {ex.Message}"); }
    }

    // Same switch as the auto-detect balloon: Settings -> "Game notifications".
    private void OnGameStartedNotification(object? sender, GameStartedArgs args)
    {
        if (_userSettings is { NotifyOnGameDetected: false }) return;
        string title = string.Format(
            CultureInfo.CurrentCulture,
            _localization!.GetString("Str_GameNotify_Title"),
            args.GameName);
        _tray?.ShowBalloon(title, args.Body);
    }

    // The safe cleanups ran by themselves (Auto maintenance). MainViewModel raises
    // this only while Settings -> Notifications -> Auto maintenance is on.
    private void OnAutoMaintenanceNotification(object? sender, string body) =>
        _tray?.ShowBalloon(_localization!.GetString("Str_Tray_NotifyAutoCleanTitle"), body);

    private void OnTimerToggledNotification(object? sender, TimerToggledArgs args)
    {
        if (_userSettings is { NotifyOnTimerToggle: false }) return;
        string title = _localization!.GetString(args.IsActive
            ? "Str_Tray_NotifyTimerOnTitle"
            : "Str_Tray_NotifyTimerOffTitle");
        string body  = args.IsActive
            ? string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Tray_NotifyTimerOnBody"),
                args.ActualMs)
            : _localization.GetString("Str_Tray_NotifyTimerOffBody");

        _tray?.ShowBalloon(title, body);
    }

    private void OnMinimizedToTrayOnClose(object? sender, EventArgs e)
    {
        if (_localization is null) return;
        _tray?.ShowBalloon(
            _localization.GetString("Str_Tray_NotifyBackgroundTitle"),
            _localization.GetString("Str_Tray_NotifyBackgroundBody"));
    }

    // ── Silent startup update check ──────────────────────────────────────────

    private async Task SilentUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            var svc = _services?.GetService<IUpdateService>();
            if (svc is null) return;
            var result = await svc.CheckAsync().ConfigureAwait(false);
            if (result.IsNewer)
            {
                // Strings are read inside the dispatcher call: GetString looks
                // up Application.Current.Resources, which belongs to the UI
                // thread, and this method resumes on the thread pool.
                await Dispatcher.InvokeAsync(() =>
                    _tray?.ShowBalloon(
                        _localization!.GetString("Str_Tray_NotifyUpdateTitle"),
                        string.Format(CultureInfo.CurrentCulture,
                            _localization.GetString("Str_Tray_NotifyUpdateBody"),
                            result.LatestVersion)));
            }
        }
        catch (Exception ex) { Logger.Warn($"SilentUpdateCheck: {ex.Message}"); }
    }
}
