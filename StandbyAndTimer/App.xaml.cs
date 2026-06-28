using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using StandbyAndTimer.Core.Interfaces;
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

    private readonly CrashHandler _crashHandler = new();

    private IServiceProvider?       _services;
    private ILocalizationService?   _localization;
    private IThemeService?          _themeService;
    private ITrayIconService?       _tray;
    private MainViewModel?          _viewModel;
    private MainWindow?             _mainWindow;
    private DispatcherTimer?        _tooltipTicker;
    private EventWaitHandle?        _showWindowSignal;
    private CancellationTokenSource? _showWindowSignalCts;

    private System.Drawing.Icon?  _iconBase;
    private System.Drawing.Icon?  _iconActive;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        long prev = 0;
        void Mark(string step)
        {
            long now = sw.ElapsedMilliseconds;
            Logger.Info($"  startup +{now - prev,4} ms (total {now,5} ms) — {step}");
            prev = now;
        }

        // ── 0a. DLL planting hardening ───────────────────────────────────────
        HardenDllSearchPath();

        // ── 0b. Crash handlers (timer must be released on exit) ──────────────
        _crashHandler.Install();
        _crashHandler.AttachToDispatcher(this);

        Logger.Info($"=== Application starting (PID {Environment.ProcessId}) ===");
        Mark("crash handlers + hardening");

        // ── 1. Single-instance guard ──────────────────────────────────────────
        // Primary acquires the mutex AND owns the named EventWaitHandle the
        // listener thread blocks on. A secondary launch opens that handle by
        // name, grants foreground rights (ASFW_ANY) so the primary's
        // SetForegroundWindow call isn't muted by the foreground-lock
        // timeout, signals it, then exits silently — no "already running"
        // dialog. The primary listener dispatches to the UI thread and
        // surfaces the window from offscreen / tray.
        _singleInstanceMutex = new Mutex(true, AppConstants.SingleInstanceMutexName, out bool isNewInstance);
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
            Current.Shutdown();
            return;
        }

        _showWindowSignal    = new EventWaitHandle(false, EventResetMode.AutoReset, AppConstants.ShowWindowSignalName);
        _showWindowSignalCts = new CancellationTokenSource();

        base.OnStartup(e);
        Mark("single-instance + base.OnStartup");

        // ── 2. Disable Windows Power Throttling / EcoQoS for this process ────
        DisablePowerThrottling();
        Mark("DisablePowerThrottling");

        // ── 3. Build DI container ─────────────────────────────────────────────
        _services = AppBootstrapper.Build();
        _crashHandler.SetServices(_services);
        Mark("DI container build");

        _viewModel    = _services.GetRequiredService<MainViewModel>();
        _localization = _services.GetRequiredService<ILocalizationService>();
        _themeService = _services.GetRequiredService<IThemeService>();
        _tray         = _services.GetRequiredService<ITrayIconService>();
        Mark("resolve viewmodel + services");

        // ── 4. Apply persisted language + theme before window is created ─────
        var savedSettings = _services.GetRequiredService<ISettingsService>().Load();
        if (savedSettings.Language != Core.Models.Language.English)
            _localization.SetLanguage(savedSettings.Language);
        if (savedSettings.Theme != Core.Models.Theme.Dark)
            _themeService.SetTheme(savedSettings.Theme);
        Mark("settings + apply language/theme");

        _localization.LanguageChanged       += OnLanguageChanged;
        _themeService.ThemeChanged          += OnThemeChanged;
        _viewModel.PropertyChanged          += OnViewModelPropertyChanged;
        _viewModel.PurgeNotification        += OnPurgeNotification;
        _viewModel.TimerToggledNotification += OnTimerToggledNotification;
        _viewModel.GameAutoDetected         += OnGameAutoDetected;

        _tray.ShowRequested        += (_, _) => ShowMainWindow();
        _tray.ToggleRequested      += (_, _) => ToggleMainWindow();
        _tray.ExitRequested        += (_, _) => Shutdown();
        _tray.TimerToggleRequested += (_, _) => _viewModel.ToggleTimerResolutionCommand.Execute(null);
        _tray.PurgeRequested       += (_, _) => _viewModel.ManualPurgeCommand.Execute(null);

        // ── 5. Build window and tray ──────────────────────────────────────────
        _iconBase   = AppIconLoader.LoadAppIcon();
        _iconActive = AppIconLoader.BuildActiveIcon(_iconBase);
        _mainWindow = new MainWindow(_viewModel) { Icon = AppIconLoader.IconToImageSource(_iconBase) };
        _tray.Initialize(_iconBase, _iconActive);
        Mark("window + tray");

        // Show the window FIRST so the user gets immediate visual feedback,
        // then kick off the (potentially-slow) viewmodel initialisation in
        // parallel.
        if (e.Args.Contains("-hidden"))
            _mainWindow.HideOffscreen();
        else
            _mainWindow.Show();
        Mark("window shown");

        // Background listener for second-launch "show me" pulses. Started
        // only after MainWindow exists so the dispatched ShowMainWindow()
        // call actually has a window to surface.
        StartShowWindowListener(_showWindowSignal!, _showWindowSignalCts!.Token);

        // Live tooltip updater — every 3 s, refresh icon and text. Cheap.
        _tooltipTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tooltipTicker.Tick += (_, _) => RefreshTray();
        _tooltipTicker.Start();

        _ = InitializeViewModelAsync(sw);

        if (savedSettings.UpdateCheckEnabled)
            _ = SilentUpdateCheckAsync();
    }

    private async Task InitializeViewModelAsync(Stopwatch sw)
    {
        try
        {
            await _viewModel!.InitializeAsync().ConfigureAwait(true);
            Logger.Info($"  startup    background  (total {sw.ElapsedMilliseconds,5} ms) — ViewModel.InitializeAsync done");
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

        _tooltipTicker?.Stop();
        _tooltipTicker = null;

        if (_localization is not null)
            _localization.LanguageChanged -= OnLanguageChanged;

        if (_themeService is not null)
            _themeService.ThemeChanged -= OnThemeChanged;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged          -= OnViewModelPropertyChanged;
            _viewModel.PurgeNotification        -= OnPurgeNotification;
            _viewModel.TimerToggledNotification -= OnTimerToggledNotification;
            _viewModel.GameAutoDetected         -= OnGameAutoDetected;
        }

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

        _singleInstanceMutex?.ReleaseMutex();
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
            if (_mainWindow is null) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle;
            StandbyAndTimer.MainWindow.ApplyDarkTitleBar(hwnd);
        });

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TimerActive)
            || e.PropertyName == nameof(MainViewModel.PurgeCount))
            Dispatcher.InvokeAsync(RefreshTray);
    }

    private void RefreshTray()
    {
        if (_tray is null || _viewModel is null) return;
        _tray.Refresh(_viewModel.TimerActive, _viewModel.PurgeCount);
    }

    // `_localization!`: these handlers are only subscribed AFTER _localization
    // is resolved from the DI container in OnStartup, and unsubscribed at the
    // top of OnExit. Non-null for the lifetime of the subscription.
    private void OnPurgeNotification(object? sender, int totalPurges)
    {
        string title = _localization!.GetString("Str_Tray_NotifyPurgeTitle");
        string body  = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Str_Tray_NotifyPurgeBody"),
            totalPurges);
        _tray?.ShowBalloon(title, body);
    }

    private void OnGameAutoDetected(object? sender, string exePath)
    {
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

    private void OnTimerToggledNotification(object? sender, TimerToggledArgs args)
    {
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
                await Dispatcher.InvokeAsync(() =>
                    _tray?.ShowBalloon("Update available",
                        $"New version {result.LatestVersion} — open Settings to download."));
            }
        }
        catch (Exception ex) { Logger.Warn($"SilentUpdateCheck: {ex.Message}"); }
    }
}
