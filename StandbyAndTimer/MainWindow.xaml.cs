using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using StandbyAndTimer.Services.Native;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    private bool _forceClose;

    // Tray state. The window used to stay Visible off-screen at Opacity 0 so
    // Windows 11 would not ignore the timer request of a hidden window. The
    // process opts out of that throttling (App.DisablePowerThrottling), and
    // measured with the window really hidden the system stayed at 0.5 ms
    // while a game held 1 ms; a visible window kept rendering (bindings tick
    // every second) and held its GPU resources for nothing.
    private bool   _offscreen;
    private double _savedLeft = double.NaN;
    private double _savedTop  = double.NaN;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
    }

    // ── Dark title bar (DWM) ──────────────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        ApplyDarkTitleBar(hwnd);
        ApplyRoundedCorners(hwnd);
    }

    // WindowStyle=None windows get square corners by default. Ask DWM for
    // the standard Windows 11 rounding. Cosmetic only: Windows 10 does not
    // know attribute 33 and just returns an error code, leaving the corners
    // square.
    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int pref = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
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
        CloseDialog.Show();
    }

    private void OnCloseDialogMinimize(object? sender, EventArgs e) => HideOffscreen();

    private void OnCloseDialogExit(object? sender, EventArgs e)
    {
        _forceClose = true;
        Application.Current.Shutdown();
    }

    // ── Off-screen "tray" mode ────────────────────────────────────────────────

    /// <summary>True while the window is hidden in the tray.</summary>
    internal bool IsOffscreen => _offscreen;

    /// <summary>
    /// HWND of this window's HwndSource. Becomes non-zero after Show() forces
    /// SourceInitialized. HotkeyService attaches here so WM_HOTKEY messages
    /// route through our WndProc.
    /// </summary>
    internal IntPtr WindowHandle =>
        new System.Windows.Interop.WindowInteropHelper(this).Handle;

    internal void HideOffscreen()
    {
        if (_offscreen) return;
        _offscreen = true;

        if (IsVisible && WindowState == WindowState.Normal)
        {
            _savedLeft = Left;
            _savedTop  = Top;
        }

        WindowState   = WindowState.Normal;
        ShowInTaskbar = false;
        // The HWND must exist even if the window was never shown (start-up
        // with -hidden): hotkeys and the single-instance signal attach to it.
        new WindowInteropHelper(this).EnsureHandle();
        Hide();
        SetRenderMode(RenderMode.SoftwareOnly);   // let WPF drop its GPU resources while hidden
        // Once the hide has been processed: give the pages the window no
        // longer touches back to Windows (games can use that RAM).
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(TrimWorkingSet));
    }

    internal void ShowFromOffscreen()
    {
        _offscreen = false;

        SetRenderMode(RenderMode.Default);
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


    private void SetRenderMode(RenderMode mode)
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.CompositionTarget is { } target)
            target.RenderMode = mode;
    }

    private void TrimWorkingSet()
    {
        if (!_offscreen) return;   // shown again meanwhile
        if (!NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1))
            Services.Logger.Warn($"Working set trim failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
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
}
