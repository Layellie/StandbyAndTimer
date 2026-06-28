using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using StandbyAndTimer.Services.Native;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    private bool _forceClose;

    // Off-screen "hidden" state — the window stays Visible (so DWM keeps compositing
    // it, which is what stops Win11 from throttling our 0.5 ms timer request), but
    // is moved off the desktop, made transparent, and removed from the taskbar.
    private bool   _offscreen;
    private double _savedLeft = double.NaN;
    private double _savedTop  = double.NaN;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        _vm.Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    // ── Dark title bar (DWM) ──────────────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    internal static void ApplyDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int dark = (Application.Current?.Resources["DwmDarkMode"] as int?) ?? 1;
        // Cosmetic-only; if the OS happens to refuse the attribute (older
        // Win10 builds, RDP, etc.) we simply get a default title bar.
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    // ── Close → "run in background / exit" overlay ───────────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_forceClose) return;
        e.Cancel = true;
        ShowCloseOverlay();
    }

    private void ShowCloseOverlay()
    {
        if (_vm.Settings.IsOpen)
            _vm.Settings.IsOpen = false;
        CloseDialog.Show();
    }

    private void OnCloseDialogMinimize(object? sender, EventArgs e) => HideOffscreen();

    private void OnCloseDialogExit(object? sender, EventArgs e)
    {
        _forceClose = true;
        Application.Current.Shutdown();
    }

    // ── Window lifecycle ──────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _vm.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        base.OnClosed(e);
    }

    // ── Off-screen "tray" mode ────────────────────────────────────────────────

    /// <summary>True while the window is parked at (-32000, -32000) with Opacity=0 — i.e. "in the tray".</summary>
    internal bool IsOffscreen => _offscreen;

    internal void HideOffscreen()
    {
        if (_offscreen) return;
        _offscreen = true;

        if (WindowState == WindowState.Normal)
        {
            _savedLeft = Left;
            _savedTop  = Top;
        }

        WindowState   = WindowState.Normal;
        ShowInTaskbar = false;
        Opacity       = 0;
        Left          = -32000;
        Top           = -32000;

        if (!IsVisible) Show();
    }

    internal void ShowFromOffscreen()
    {
        _offscreen = false;

        Opacity       = 1;
        ShowInTaskbar = true;

        if (!double.IsNaN(_savedLeft))
        {
            Left = _savedLeft;
            Top  = _savedTop;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = (area.Width  - ActualWidth)  / 2;
            Top  = (area.Height - ActualHeight) / 2;
        }

        if (!IsVisible) Show();
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
            Dispatcher.BeginInvoke(new Action(HideOffscreen));
    }

    // ── Title bar window controls (forwarded from cards:TitleBar) ────────────

    private void OnTitleBarMinimize(object? sender, EventArgs e) => WindowState = WindowState.Minimized;

    private void OnTitleBarClose(object? sender, EventArgs e) => Close();

    // ── Game list drag-and-drop (forwarded from cards:GameModeCard) ──────────

    private void OnGameDropped(object? sender, string path) => _vm.AddGameFromPath(path);

    // ── Settings panel slide animation ────────────────────────────────────────

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.IsOpen)) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_vm.Settings.IsOpen) AnimateSettingsOpen();
            else                     AnimateSettingsClose();
        });
    }

    private void AnimateSettingsOpen()
    {
        DimOverlay.Visibility             = Visibility.Visible;
        SettingsPanelContainer.Visibility = Visibility.Visible;

        PanelTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(270, 0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        DimOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 0.65, TimeSpan.FromMilliseconds(200)));
    }

    private void AnimateSettingsClose()
    {
        var closeAnim = new DoubleAnimation(0, 270, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

        closeAnim.Completed += (_, _) =>
        {
            SettingsPanelContainer.Visibility = Visibility.Collapsed;
            DimOverlay.Visibility             = Visibility.Collapsed;
        };

        PanelTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.XProperty, closeAnim);

        DimOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.65, 0, TimeSpan.FromMilliseconds(160)));
    }
}
