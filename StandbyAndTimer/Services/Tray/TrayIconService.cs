using System.Globalization;
using StandbyAndTimer.Core.Interfaces;
using WinForms = System.Windows.Forms;

namespace StandbyAndTimer.Services.Tray;

// Concrete WinForms NotifyIcon wrapper. Owns the dark-themed context menu
// and re-renders labels / tooltip / icon on demand. App.xaml.cs holds the
// reference and wires the public events to its own handlers (ShowMainWindow,
// Shutdown, etc.) so this class doesn't need to know about the Application.
internal sealed class TrayIconService : ITrayIconService
{
    private const int BalloonDurationMs = 2_500;
    private const int TooltipMaxLength  = 127; // NotifyIcon.Text hard limit

    private readonly ILocalizationService _localization;

    private WinForms.NotifyIcon?        _notifyIcon;
    private WinForms.ToolStripMenuItem? _showMenuItem;
    private WinForms.ToolStripMenuItem? _timerMenuItem;
    private WinForms.ToolStripMenuItem? _purgeMenuItem;
    private WinForms.ToolStripMenuItem? _exitMenuItem;

    private System.Drawing.Icon? _iconBase;
    private System.Drawing.Icon? _iconActive;

    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? TimerToggleRequested;
    public event EventHandler? PurgeRequested;

    public TrayIconService(ILocalizationService localization)
    {
        _localization = localization;
    }

    public void Initialize(System.Drawing.Icon baseIcon, System.Drawing.Icon? activeIcon)
    {
        _iconBase   = baseIcon;
        _iconActive = activeIcon;

        var menu = BuildMenu();
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon             = baseIcon,
            Text             = "StandbyAndTimer",
            Visible          = true,
            ContextMenuStrip = menu,
        };

        // Left-click and double-click both toggle visibility. The context-menu
        // "Show" item still raises ShowRequested — semantically "always show",
        // distinct from "toggle" (which can hide a visible window).
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ToggleRequested?.Invoke(this, EventArgs.Empty);
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);

        RefreshLabels();
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer        = new TrayDarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font            = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Regular),
            BackColor       = TrayDarkPalette.SurfaceBg,
            ForeColor       = TrayDarkPalette.AppFg,
            Padding         = new WinForms.Padding(2),
        };

        _showMenuItem  = new WinForms.ToolStripMenuItem();
        _timerMenuItem = new WinForms.ToolStripMenuItem();
        _purgeMenuItem = new WinForms.ToolStripMenuItem();
        _exitMenuItem  = new WinForms.ToolStripMenuItem();

        _showMenuItem.Click  += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _timerMenuItem.Click += (_, _) => TimerToggleRequested?.Invoke(this, EventArgs.Empty);
        _purgeMenuItem.Click += (_, _) => PurgeRequested?.Invoke(this, EventArgs.Empty);
        _exitMenuItem.Click  += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(_showMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_timerMenuItem);
        menu.Items.Add(_purgeMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_exitMenuItem);

        // Per-item color overrides — the menu's BackColor/ForeColor alone
        // don't propagate to children when using a ProfessionalRenderer.
        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            item.BackColor = TrayDarkPalette.SurfaceBg;
            item.ForeColor = TrayDarkPalette.AppFg;
            if (item is WinForms.ToolStripMenuItem mi)
                mi.Padding = new WinForms.Padding(8, 4, 8, 4);
        }

        return menu;
    }

    public void Refresh(bool timerActive, int purgeCount)
    {
        RefreshLabels(timerActive);

        if (_notifyIcon is null) return;
        try
        {
            _notifyIcon.Icon = timerActive ? (_iconActive ?? _iconBase!) : _iconBase!;

            string state = _localization.GetString(
                timerActive ? "Str_Tray_TimerStateOn" : "Str_Tray_TimerStateOff");
            string fmt   = _localization.GetString("Str_Tray_TooltipFormat");

            string tip = string.Format(CultureInfo.CurrentCulture, fmt, state, purgeCount);
            if (tip.Length > TooltipMaxLength) tip = tip[..TooltipMaxLength];
            _notifyIcon.Text = tip;
        }
        catch (Exception ex) { Logger.Warn($"TrayIconService.Refresh: {ex.Message}"); }
    }

    public void RefreshLabels() => RefreshLabels(timerActive: false);

    private void RefreshLabels(bool timerActive)
    {
        if (_showMenuItem is not null)
            _showMenuItem.Text = _localization.GetString("Str_Tray_Show");

        if (_timerMenuItem is not null)
            _timerMenuItem.Text = _localization.GetString(
                timerActive ? "Str_Tray_TimerDisable" : "Str_Tray_TimerEnable");

        if (_purgeMenuItem is not null)
            _purgeMenuItem.Text = _localization.GetString("Str_Tray_PurgeNow");

        if (_exitMenuItem is not null)
            _exitMenuItem.Text = _localization.GetString("Str_Tray_Exit");
    }

    public void ShowBalloon(string title, string body)
    {
        if (_notifyIcon is null) return;
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText  = body;
            _notifyIcon.BalloonTipIcon  = WinForms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(BalloonDurationMs);
        }
        catch (Exception ex) { Logger.Warn($"TrayIconService.ShowBalloon: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
