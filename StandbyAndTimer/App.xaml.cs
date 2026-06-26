using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
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
        // ── 0a. DLL planting hardening ───────────────────────────────────────
        // Run before anything else so that any DLL we (or the WPF/runtime
        // machinery) delay-load later cannot be spoofed by a binary dropped
        // next to our admin-elevated exe.
        HardenDllSearchPath();

        // ── 0b. Crash-safe restore handlers (timer must be released on exit) ─
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit        += OnProcessExit;
        DispatcherUnhandledException               += OnDispatcherUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Logger.Info($"=== Application starting (PID {Environment.ProcessId}) ===");

        // ── 1. Single-instance guard ──────────────────────────────────────────
        _singleInstanceMutex = new Mutex(true, @"Global\StandbyAndTimer_v1", out bool isNewInstance);
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

        // ── 2. Disable Windows Power Throttling / EcoQoS for this process ────
        DisablePowerThrottling();

        // ── 3. Build DI container ─────────────────────────────────────────────
        _services     = AppBootstrapper.Build();
        _viewModel    = _services.GetRequiredService<MainViewModel>();
        _localization = _services.GetRequiredService<ILocalizationService>();
        _themeService = _services.GetRequiredService<IThemeService>();

        // ── 4. Apply persisted language + theme before window is created ─────
        var savedSettings = _services.GetRequiredService<ISettingsService>().Load();
        if (savedSettings.Language != Core.Models.Language.English)
            _localization.SetLanguage(savedSettings.Language);
        if (savedSettings.Theme != Core.Models.Theme.Dark)
            _themeService.SetTheme(savedSettings.Theme);

        _localization.LanguageChanged             += OnLanguageChanged;
        _themeService.ThemeChanged                += OnThemeChanged;
        _viewModel.PropertyChanged                += OnViewModelPropertyChanged;
        _viewModel.PurgeNotification              += OnPurgeNotification;
        _viewModel.TimerToggledNotification       += OnTimerToggledNotification;

        // ── 5. Build window and tray ──────────────────────────────────────────
        _iconBase   = LoadAppIcon();
        _iconActive = BuildActiveIcon(_iconBase);
        _mainWindow = new MainWindow(_viewModel);
        _mainWindow.Icon = IconToImageSource(_iconBase);
        SetupTray(_iconBase);

        await _viewModel.InitializeAsync();

        // Refresh tray now that ViewModel has loaded state (timer / purge count).
        UpdateTrayVisuals();

        // Live tooltip updater — every 3 s, refresh icon and text. Cheap.
        _tooltipTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tooltipTicker.Tick += (_, _) => UpdateTrayVisuals();
        _tooltipTicker.Start();

        if (e.Args.Contains("-hidden"))
            _mainWindow.HideOffscreen();
        else
            _mainWindow.Show();

        // Auto-update check (silent, non-blocking)
        if (savedSettings.UpdateCheckEnabled)
            _ = SilentUpdateCheckAsync();
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
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        DisposeIcon(ref _iconBase);
        DisposeIcon(ref _iconActive);

        _viewModel?.ShutdownAsync().GetAwaiter().GetResult();
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
        // Don't mark as handled — let the app crash visibly, but at least the
        // timer is restored so the user's system clock isn't stuck at 0.5 ms.
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
            4,
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

    private static System.Windows.Media.ImageSource IconToImageSource(System.Drawing.Icon icon)
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

    private void OnPurgeNotification(object? sender, int totalPurges)
    {
        string title = _localization?.GetString("Str_Tray_NotifyPurgeTitle") ?? "Standby cache purged";
        string body  = string.Format(
            _localization?.GetString("Str_Tray_NotifyPurgeBody") ?? "Total purges: {0}",
            totalPurges);
        ShowBalloon(title, body);
    }

    private void OnTimerToggledNotification(object? sender, TimerToggledArgs args)
    {
        string title = args.IsActive
            ? _localization?.GetString("Str_Tray_NotifyTimerOnTitle")  ?? "Timer activated"
            : _localization?.GetString("Str_Tray_NotifyTimerOffTitle") ?? "Timer disabled";
        string body  = args.IsActive
            ? string.Format(_localization?.GetString("Str_Tray_NotifyTimerOnBody") ?? "Locked to {0:F3} ms", args.ActualMs)
            : _localization?.GetString("Str_Tray_NotifyTimerOffBody") ?? "Restored to default";

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

    private void UpdateTrayHeaders()
    {
        if (_showMenuItem is not null)
            _showMenuItem.Text = _localization?.GetString("Str_Tray_Show") ?? "Show";

        if (_timerMenuItem is not null)
        {
            bool active  = _viewModel?.TimerActive ?? false;
            string key   = active ? "Str_Tray_TimerDisable" : "Str_Tray_TimerEnable";
            string fallback = active ? "Disable Timer" : "Enable Timer";
            _timerMenuItem.Text = _localization?.GetString(key) ?? fallback;
        }

        if (_purgeMenuItem is not null)
            _purgeMenuItem.Text = _localization?.GetString("Str_Tray_PurgeNow") ?? "Purge Standby Now";

        if (_exitMenuItem is not null)
            _exitMenuItem.Text = _localization?.GetString("Str_Tray_Exit") ?? "Exit";
    }

    // Updates icon image, menu labels, and live tooltip in a single pass.
    private void UpdateTrayVisuals()
    {
        UpdateTrayHeaders();

        if (_trayIcon is null) return;
        try
        {
            bool active = _viewModel?.TimerActive ?? false;
            _trayIcon.Icon = active ? (_iconActive ?? _iconBase!) : _iconBase!;

            string state = active
                ? _localization?.GetString("Str_Tray_TimerStateOn")  ?? "ON"
                : _localization?.GetString("Str_Tray_TimerStateOff") ?? "OFF";
            string fmt   = _localization?.GetString("Str_Tray_TooltipFormat")
                           ?? "StandbyAndTimer\nTimer: {0}  •  Purges: {1}";

            string tip = string.Format(fmt, state, _viewModel?.PurgeCount ?? 0);
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
