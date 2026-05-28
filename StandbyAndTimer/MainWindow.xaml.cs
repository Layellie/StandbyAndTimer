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
        NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
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

        CloseDialogOverlay.Visibility = Visibility.Visible;

        CloseDialogOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void OnCloseMinimizeClick(object sender, RoutedEventArgs e)
    {
        HideCloseOverlay();
        HideOffscreen();
    }

    private void OnCloseExitClick(object sender, RoutedEventArgs e)
    {
        HideCloseOverlay();
        _forceClose = true;
        Application.Current.Shutdown();
    }

    private void HideCloseOverlay()
    {
        CloseDialogOverlay.Visibility = Visibility.Collapsed;
    }

    // ── Window lifecycle ──────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _vm.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        base.OnClosed(e);
    }

    // ── Off-screen "tray" mode ────────────────────────────────────────────────

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

    // ── Game list drag-and-drop ──────────────────────────────────────────────

    private void GamesList_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void GamesList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var f in files)
            _vm.AddGameFromPath(f);
        e.Handled = true;
    }

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
