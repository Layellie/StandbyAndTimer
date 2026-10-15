using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

        // A hidden start runs the whole process in software (App.OnStartup);
        // the first show brings the GPU back for good.
        if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly)
            RenderOptions.ProcessRenderMode = RenderMode.Default;
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

        if (!IsVisible) ShowCloaked();
        Activate();
    }

    // ── Show without an empty frame ──────────────────────────────────────────
    // Between Show() and the first frame reaching the screen, DWM shows the
    // bare window: an empty dark grey box, measured 80-140 ms on a direct
    // start and 130-160 ms when opening from the tray (the switch back from
    // software to GPU rendering). Cloak the window until DWM really has its
    // content. WPF's own signals don't fit: its "rendered" events come about
    // 100 ms before the content is on screen, and the UI thread stays busy
    // for ~200 ms inside Show() while the render thread already presents.
    // So a background thread watches what DWM holds for the window (plain
    // Win32 calls, no WPF objects) and uncloaks once the content is in.
    // PrintWindow still waits for the UI thread, so the window appears when
    // Show() is done: content on screen ~60 ms later than without the cloak
    // (measured), but never the empty box.

    private const int UncloakPollMs     = 4;
    private const int UncloakFallbackMs = 1000;
    private int _showGeneration;

    // Captures what DWM holds for the window into one reused bitmap and
    // looks at the left and right gutters at mid-height (window background,
    // outside every card): the theme AppBg there means the content is in.
    // Any failure counts as "in", so this check never keeps the window hidden.
    private sealed class ContentProbe(IntPtr hwnd, System.Windows.Media.Color appBg) : IDisposable
    {
        private System.Drawing.Bitmap? _bmp;

        public bool ContentOnScreen()
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 16 || h <= 16) return true;
            if (_bmp is null || _bmp.Width != w || _bmp.Height != h)
            {
                _bmp?.Dispose();
                _bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
            using (var g = System.Drawing.Graphics.FromImage(_bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { if (!NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT)) return true; }
                finally { g.ReleaseHdc(hdc); }
            }
            return Near(_bmp.GetPixel(6, h / 2)) && Near(_bmp.GetPixel(w - 7, h / 2));
        }

        private bool Near(System.Drawing.Color c) =>
            Math.Abs(c.R - appBg.R) <= 3 && Math.Abs(c.G - appBg.G) <= 3 && Math.Abs(c.B - appBg.B) <= 3;

        public void Dispose() => _bmp?.Dispose();
    }

    internal void ShowCloaked()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var appBg = (TryFindResource("AppBg") as SolidColorBrush)?.Color;
        int on = 1;
        if (appBg is null || NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAK, ref on, sizeof(int)) != 0)
        {
            Show();   // no colour to check, or DWM refused (RDP, old build): plain show, as before
            return;
        }

        // A newer show (hide + show again quickly) owns the cloak from then on.
        int generation = Interlocked.Increment(ref _showGeneration);
        var color = appBg.Value;
        var watcher = new Thread(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int checks = 0;
            bool inPlace = false;
            using (var probe = new ContentProbe(hwnd, color))
            {
                while (clock.ElapsedMilliseconds < UncloakFallbackMs && Volatile.Read(ref _showGeneration) == generation)
                {
                    Thread.Sleep(UncloakPollMs);
                    checks++;
                    try { inPlace = probe.ContentOnScreen(); }
                    catch { inPlace = true; }
                    if (inPlace) break;
                }
            }
            if (Volatile.Read(ref _showGeneration) != generation) return;
            int off = 0;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAK, ref off, sizeof(int));
            Services.Logger.Info($"Window shown after {clock.ElapsedMilliseconds} ms " +
                                 $"({(inPlace ? "content on screen" : "1 s fallback")}, {checks} checks)");
        }) { IsBackground = true, Name = "UncloakWatcher" };
        watcher.Start();
        Show();
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

    private void OnGameLaunchRequested(object? sender, Core.Models.GameEntry game) => _vm.LaunchGame(game);
}
