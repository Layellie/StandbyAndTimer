using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Infrastructure;
using StandbyAndTimer.Services;
using StandbyAndTimer.Services.Native;
using StandbyAndTimer.ViewModels;
using WinForms = System.Windows.Forms;

namespace StandbyAndTimer;

// CA1001: App owns disposable WinForms fields (tray icon, menu items). The
// WPF Application's lifetime is the process; OnExit() releases everything,
// and the runtime never calls IDisposable.Dispose() on Application. Adding
// IDisposable here would mislead readers more than it would help.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Usage",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application lifetime is the process; cleanup happens in OnExit.")]
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private IServiceProvider?     _services;
    private ILocalizationService? _localization;
    private IThemeService?        _themeService;
    private MainViewModel?        _viewModel;
    private MainWindow?           _mainWindow;
    private WinForms.NotifyIcon?  _trayIcon;
    private DispatcherTimer?      _tooltipTicker;

    // Two tray icons: base = neutral, active = neutral + green dot overlay.
    private System.Drawing.Icon?  _iconBase;
    private System.Drawing.Icon?  _iconActive;

    private WinForms.ToolStripMenuItem? _showMenuItem;
    private WinForms.ToolStripMenuItem? _timerMenuItem;
    private WinForms.ToolStripMenuItem? _purgeMenuItem;
    private WinForms.ToolStripMenuItem? _exitMenuItem;

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

        // ── 0b. Crash-safe restore handlers (timer must be released on exit) ─
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit        += OnProcessExit;
        DispatcherUnhandledException               += OnDispatcherUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Logger.Info($"=== Application starting (PID {Environment.ProcessId}) ===");
        Mark("crash handlers + hardening");

        // ── 1. Single-instance guard ──────────────────────────────────────────
        _singleInstanceMutex = new Mutex(true, AppConstants.SingleInstanceMutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "Application is already running in the background.\nLook for the icon in the system tray.",
                "StandbyAndTimer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        Mark("single-instance + base.OnStartup");

        // ── 2. Disable Windows Power Throttling / EcoQoS for this process ────
        DisablePowerThrottling();
        Mark("DisablePowerThrottling");

        // ── 3. Build DI container ─────────────────────────────────────────────
        _services     = AppBootstrapper.Build();
        Mark("DI container build");

        _viewModel    = _services.GetRequiredService<MainViewModel>();
        Mark("resolve MainViewModel (chain instantiation)");

        _localization = _services.GetRequiredService<ILocalizationService>();
        _themeService = _services.GetRequiredService<IThemeService>();
        Mark("resolve localization + theme");

        // ── 4. Apply persisted language + theme before window is created ─────
        var savedSettings = _services.GetRequiredService<ISettingsService>().Load();
        Mark("settings load");

        if (savedSettings.Language != Core.Models.Language.English)
            _localization.SetLanguage(savedSettings.Language);
        if (savedSettings.Theme != Core.Models.Theme.Dark)
            _themeService.SetTheme(savedSettings.Theme);
        Mark("apply language + theme");

        _localization.LanguageChanged             += OnLanguageChanged;
        _themeService.ThemeChanged                += OnThemeChanged;
        _viewModel.PropertyChanged                += OnViewModelPropertyChanged;
        _viewModel.PurgeNotification              += OnPurgeNotification;
        _viewModel.TimerToggledNotification       += OnTimerToggledNotification;
        _viewModel.GameAutoDetected               += OnGameAutoDetected;

        // ── 5. Build window and tray ──────────────────────────────────────────
        _iconBase   = LoadAppIcon();
        Mark("LoadAppIcon");
        _iconActive = BuildActiveIcon(_iconBase);
        Mark("BuildActiveIcon");
        _mainWindow = new MainWindow(_viewModel);
        Mark("new MainWindow");
        _mainWindow.Icon = IconToImageSource(_iconBase);
        SetupTray(_iconBase);
        Mark("SetupTray");

        // Show the window FIRST so the user gets immediate visual feedback,
        // then kick off the (potentially-slow) viewmodel initialisation in
        // parallel. Timer activation includes a 150 ms warm-up which used to
        // run before the window appeared.
        if (e.Args.Contains("-hidden"))
            _mainWindow.HideOffscreen();
        else
            _mainWindow.Show();
        Mark("window shown");

        // Live tooltip updater — every 3 s, refresh icon and text. Cheap.
        _tooltipTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tooltipTicker.Tick += (_, _) => UpdateTrayVisuals();
        _tooltipTicker.Start();

        // Run viewmodel init in the background; refresh the tray once it
        // settles so the icon reflects timer/purge state.
        _ = InitializeViewModelAsync(sw);

        // Auto-update check (silent, non-blocking)
        if (savedSettings.UpdateCheckEnabled)
            _ = SilentUpdateCheckAsync();
    }

    private async Task InitializeViewModelAsync(Stopwatch sw)
    {
        try
        {
            await _viewModel!.InitializeAsync().ConfigureAwait(true);
            Logger.Info($"  startup    background  (total {sw.ElapsedMilliseconds,5} ms) — ViewModel.InitializeAsync done");
            UpdateTrayVisuals();
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

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        DisposeIcon(ref _iconBase);
        DisposeIcon(ref _iconActive);

        // Task.Run hop so the async chain inside ShutdownAsync resumes on a
        // ThreadPool thread instead of the WPF dispatcher we're blocking with
        // GetResult. Awaiting via the dispatcher's SynchronizationContext
        // while .GetResult() pins it would deadlock the moment any callee
        // tries to ConfigureAwait(true) — and at least one (the memory
        // service stop path) does exactly that during its cancel handshake.
        if (_viewModel is not null)
            Task.Run(_viewModel.ShutdownAsync).GetAwaiter().GetResult();
        (_services as IDisposable)?.Dispose();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    // ── Crash handlers ────────────────────────────────────────────────────────

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Logger.Error("UnhandledException", (Exception)e.ExceptionObject);
        TryEmergencyRestore();
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("DispatcherUnhandledException", e.Exception);
        TryEmergencyRestore();
        // Show the crash reporter dialog and mark the exception handled — the
        // alternative (let WPF show its default fatal error popup and exit)
        // gives the user nothing to act on. Our window has Copy + Open-Issue
        // buttons so the report actually reaches us. We only handle the
        // exception if the reporter actually displays; if it fails too, fall
        // through to the default fatal path.
        try
        {
            var reporter = new Views.CrashReporterWindow(FormatCrash(e.Exception));
            reporter.ShowDialog();
            e.Handled = true;
        }
        catch (Exception ex2)
        {
            Logger.Error("CrashReporter failed to show", ex2);
        }
    }

    private static string FormatCrash(Exception ex)
    {
        var sb  = new System.Text.StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"Type: {ex.GetType().FullName}");
        sb.AppendLine(inv, $"Message: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("Stack:");
        sb.AppendLine(ex.StackTrace);
        if (ex.InnerException is { } inner)
        {
            sb.AppendLine();
            sb.AppendLine(inv, $"InnerException: {inner.GetType().FullName}: {inner.Message}");
            sb.AppendLine(inner.StackTrace);
        }
        return sb.ToString();
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Logger.Info("ProcessExit fired");
        TryEmergencyRestore();
    }

    private void TryEmergencyRestore()
    {
        try
        {
            // Force release the timer even if Deactivate path didn't run.
            // Safe to call multiple times — TimerResolutionService guards IsActive.
            (_services?.GetService<ITimerResolutionService>())?.Dispose();
        }
        catch { /* best-effort */ }
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
        // PROCESS_INFORMATION_CLASS::ProcessPowerThrottling = 4.
        // The previous value (34) didn't match any class and SetProcessInformation
        // returned ERROR_INVALID_PARAMETER (87), which silently left
        // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION at its default — and
        // that default caused the kernel to ignore our NtSetTimerResolution(0.5 ms)
        // request, so the timer sat at the OS default (~15.6 ms / 1 ms) until the
        // user toggled it manually.
        bool ok = NativeMethods.SetProcessInformation(
            Process.GetCurrentProcess().Handle,
            NativeMethods.ProcessPowerThrottling,
            ref state,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());

        if (!ok)
            Logger.Warn($"SetProcessInformation failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    // ── App icon loading ──────────────────────────────────────────────────────

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/app_icon.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream);
            }
        }
        catch (Exception ex) { Logger.Warn($"LoadAppIcon: {ex.Message}"); }

        return CreateFallbackIcon();
    }

    // Fallback if app_icon.ico isn't bundled — 32×32 purple square with a white "S".
    // Renders to a stream-backed Icon so disposal frees the HICON cleanly (no leak).
    private static System.Drawing.Icon CreateFallbackIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(System.Drawing.Color.Transparent);

            using var bg = new System.Drawing.SolidBrush(
                System.Drawing.Color.FromArgb(255, 124, 106, 247));
            g.FillRectangle(bg, 0, 0, 32, 32);

            using var font = new System.Drawing.Font(
                "Segoe UI", 19, System.Drawing.FontStyle.Bold,
                System.Drawing.GraphicsUnit.Pixel);
            var sf = new System.Drawing.StringFormat
            {
                Alignment     = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center
            };
            g.DrawString("S", font, System.Drawing.Brushes.White,
                new System.Drawing.RectangleF(0, 1, 32, 32), sf);
        }

        return BitmapToIcon(bmp);
    }

    // Bitmap → HICON → Icon via GetHicon, but we destroy the temporary HICON
    // immediately after cloning it through a memory stream — no leak.
    private static System.Drawing.Icon BitmapToIcon(System.Drawing.Bitmap bmp)
    {
        IntPtr hicon = bmp.GetHicon();
        try
        {
            using var temp = System.Drawing.Icon.FromHandle(hicon);
            using var ms   = new MemoryStream();
            temp.Save(ms);
            ms.Position = 0;
            return new System.Drawing.Icon(ms);
        }
        finally
        {
            NativeMethods.DestroyIcon(hicon);
        }
    }

    // Builds the "active" tray icon by drawing a green dot over the base icon.
    private static System.Drawing.Icon BuildActiveIcon(System.Drawing.Icon baseIcon)
    {
        try
        {
            using var bmp = baseIcon.ToBitmap();
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int size = Math.Max(8, bmp.Width / 3);
                int x    = bmp.Width  - size - 1;
                int y    = bmp.Height - size - 1;
                using var dot    = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 40, 220, 90));
                using var border = new System.Drawing.Pen(System.Drawing.Color.Black, 1.5f);
                g.FillEllipse(dot, x, y, size, size);
                g.DrawEllipse(border, x, y, size, size);
            }
            return BitmapToIcon(bmp);
        }
        catch (Exception ex)
        {
            Logger.Warn($"BuildActiveIcon: {ex.Message}");
            return (System.Drawing.Icon)baseIcon.Clone();
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource IconToImageSource(System.Drawing.Icon icon)
    {
        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
    }

    private static void DisposeIcon(ref System.Drawing.Icon? icon)
    {
        if (icon is null) return;
        try { icon.Dispose(); } catch { }
        icon = null;
    }

    // ── Tray icon (WinForms NotifyIcon — direct Shell_NotifyIcon API) ─────────

    private void SetupTray(System.Drawing.Icon appIcon)
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer         = new DarkMenuRenderer(),
            ShowImageMargin  = false,
            ShowCheckMargin  = false,
            Font             = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Regular),
            BackColor        = DarkPalette.SurfaceBg,
            ForeColor        = DarkPalette.AppFg,
            Padding          = new WinForms.Padding(2)
        };

        _showMenuItem  = new WinForms.ToolStripMenuItem();
        _timerMenuItem = new WinForms.ToolStripMenuItem();
        _purgeMenuItem = new WinForms.ToolStripMenuItem();
        _exitMenuItem  = new WinForms.ToolStripMenuItem();

        _showMenuItem.Click  += (_, _) => ShowMainWindow();
        _timerMenuItem.Click += (_, _) =>
            _viewModel?.ToggleTimerResolutionCommand.Execute(null);
        _purgeMenuItem.Click += (_, _) =>
            _viewModel?.ManualPurgeCommand.Execute(null);
        _exitMenuItem.Click  += (_, _) => Shutdown();

        UpdateTrayHeaders();

        menu.Items.Add(_showMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_timerMenuItem);
        menu.Items.Add(_purgeMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_exitMenuItem);

        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            item.BackColor = DarkPalette.SurfaceBg;
            item.ForeColor = DarkPalette.AppFg;
            if (item is WinForms.ToolStripMenuItem mi)
                mi.Padding = new WinForms.Padding(8, 4, 8, 4);
        }

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon             = appIcon,
            Text             = "StandbyAndTimer",
            Visible          = true,
            ContextMenuStrip = menu
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ShowMainWindow();
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow?.ShowFromOffscreen();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(UpdateTrayVisuals);

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
            Dispatcher.InvokeAsync(UpdateTrayVisuals);
    }

    // Note on `_localization!`: this handler is only subscribed AFTER
    // _localization is resolved from the DI container in OnStartup, and is
    // unsubscribed at the top of OnExit. The field is non-null for the entire
    // lifetime of the handler subscription, so the bang is a fact-stated
    // assertion, not a guess. LocalizationService.GetString returns the key
    // itself when the resource is missing, so we don't need a fallback string.
    private void OnPurgeNotification(object? sender, int totalPurges)
    {
        string title = _localization!.GetString("Str_Tray_NotifyPurgeTitle");
        string body  = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Str_Tray_NotifyPurgeBody"),
            totalPurges);
        ShowBalloon(title, body);
    }

    // Detection event handler — shows a tray balloon. We don't auto-add the
    // game to the list (could be a fullscreen video player). The body uses
    // the filename only so it stays under the balloon's ~256-char cap even
    // for paths with deep folder nesting.
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
            ShowBalloon(title, body);
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

        ShowBalloon(title, body);
    }

    private void ShowBalloon(string title, string body)
    {
        if (_trayIcon is null) return;
        try
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText  = body;
            _trayIcon.BalloonTipIcon  = WinForms.ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(2500);
        }
        catch (Exception ex) { Logger.Warn($"ShowBalloon: {ex.Message}"); }
    }

    // Both _localization and _viewModel are non-null for the lifetime of these
    // calls — SetupTray runs after they're assigned in OnStartup, and the
    // DispatcherTimer that drives UpdateTrayVisuals is stopped at the top of
    // OnExit before the fields are released. `?` is dead weight here.
    private void UpdateTrayHeaders()
    {
        if (_showMenuItem is not null)
            _showMenuItem.Text = _localization!.GetString("Str_Tray_Show");

        if (_timerMenuItem is not null)
        {
            bool active = _viewModel!.TimerActive;
            _timerMenuItem.Text = _localization!.GetString(
                active ? "Str_Tray_TimerDisable" : "Str_Tray_TimerEnable");
        }

        if (_purgeMenuItem is not null)
            _purgeMenuItem.Text = _localization!.GetString("Str_Tray_PurgeNow");

        if (_exitMenuItem is not null)
            _exitMenuItem.Text = _localization!.GetString("Str_Tray_Exit");
    }

    // Updates icon image, menu labels, and live tooltip in a single pass.
    private void UpdateTrayVisuals()
    {
        UpdateTrayHeaders();

        if (_trayIcon is null) return;
        try
        {
            bool active = _viewModel!.TimerActive;
            _trayIcon.Icon = active ? (_iconActive ?? _iconBase!) : _iconBase!;

            string state = _localization!.GetString(
                active ? "Str_Tray_TimerStateOn" : "Str_Tray_TimerStateOff");
            string fmt   = _localization.GetString("Str_Tray_TooltipFormat");

            string tip = string.Format(CultureInfo.CurrentCulture, fmt, state, _viewModel.PurgeCount);
            if (tip.Length > 127) tip = tip[..127]; // NotifyIcon.Text limit
            _trayIcon.Text = tip;
        }
        catch (Exception ex) { Logger.Warn($"UpdateTrayVisuals: {ex.Message}"); }
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
                    ShowBalloon("Update available",
                        $"New version {result.LatestVersion} — open Settings to download."));
            }
        }
        catch (Exception ex) { Logger.Warn($"SilentUpdateCheck: {ex.Message}"); }
    }

    // ── Dark theme for the tray context menu ──────────────────────────────────

    private static class DarkPalette
    {
        public static readonly Color SurfaceBg     = Color.FromArgb(0x13, 0x13, 0x1A);
        public static readonly Color AppFg         = Color.FromArgb(0xE2, 0xE2, 0xF0);
        public static readonly Color AccentPrimary = Color.FromArgb(0x7C, 0x6A, 0xF7);
        public static readonly Color SelectionBg   = Color.FromArgb(0x3D, 0x36, 0x70);
        public static readonly Color AccentPressed = Color.FromArgb(0x64, 0x55, 0xE0);
        public static readonly Color BorderColor   = Color.FromArgb(0x2A, 0x2A, 0x3D);
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground       => DarkPalette.SurfaceBg;
        public override Color ImageMarginGradientBegin          => DarkPalette.SurfaceBg;
        public override Color ImageMarginGradientMiddle         => DarkPalette.SurfaceBg;
        public override Color ImageMarginGradientEnd            => DarkPalette.SurfaceBg;
        public override Color MenuBorder                        => DarkPalette.BorderColor;
        public override Color MenuItemBorder                    => DarkPalette.AccentPrimary;
        public override Color MenuItemSelected                  => DarkPalette.SelectionBg;
        public override Color MenuItemSelectedGradientBegin     => DarkPalette.SelectionBg;
        public override Color MenuItemSelectedGradientEnd       => DarkPalette.SelectionBg;
        public override Color MenuItemPressedGradientBegin      => DarkPalette.AccentPressed;
        public override Color MenuItemPressedGradientEnd        => DarkPalette.AccentPressed;
        public override Color SeparatorDark                     => DarkPalette.BorderColor;
        public override Color SeparatorLight                    => DarkPalette.BorderColor;
    }

    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? DarkPalette.AppFg : Color.FromArgb(0x70, 0x70, 0xA0);
            base.OnRenderItemText(e);
        }
    }
}
