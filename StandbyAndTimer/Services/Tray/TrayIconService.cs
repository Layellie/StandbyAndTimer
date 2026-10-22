using System.Globalization;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.ViewModels;
using WinForms = System.Windows.Forms;

namespace StandbyAndTimer.Services.Tray;

// Concrete WinForms NotifyIcon wrapper. Owns the themed (dark / light)
// context menu and re-renders labels / tooltip / icon on demand. App.xaml.cs holds the
// reference and wires the public events to its own handlers (ShowMainWindow,
// Shutdown, etc.) so this class doesn't need to know about the Application.
internal sealed class TrayIconService : ITrayIconService
{
    private const int BalloonDurationMs = 2_500;
    private const int TooltipMaxLength  = 127; // NotifyIcon.Text hard limit

    private readonly ILocalizationService _localization;

    private WinForms.NotifyIcon?        _notifyIcon;
    private WinForms.ContextMenuStrip?  _menu;
    private TrayMenuPalette             _palette = TrayMenuPalette.Dark;
    private WinForms.ToolStripMenuItem? _showMenuItem;
    private WinForms.ToolStripMenuItem? _settingsMenuItem;
    private WinForms.ToolStripMenuItem? _timerMenuItem;
    private WinForms.ToolStripMenuItem? _purgeMenuItem;
    private WinForms.ToolStripMenuItem? _autoPurgeMenuItem;
    private WinForms.ToolStripMenuItem? _deepMenuItem;
    private WinForms.ToolStripMenuItem? _gameModeMenuItem;
    private WinForms.ToolStripMenuItem? _lockMenuItem;
    private TrayState _state = new(false, 0);
    private WinForms.ToolStripMenuItem? _exitMenuItem;

    private System.Drawing.Icon? _iconBase;
    private System.Drawing.Icon? _iconActive;

    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? TimerToggleRequested;
    public event EventHandler? PurgeRequested;
    public event EventHandler? AutoPurgeToggleRequested;
    public event EventHandler? DeepPurgeToggleRequested;
    public event EventHandler? GameModeToggleRequested;
    public event EventHandler? TimerLockCycleRequested;

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
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font            = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Regular),
            Padding         = new WinForms.Padding(2),
        };

        _showMenuItem      = new WinForms.ToolStripMenuItem();
        _settingsMenuItem  = new WinForms.ToolStripMenuItem();
        _timerMenuItem     = new WinForms.ToolStripMenuItem();
        _purgeMenuItem     = new WinForms.ToolStripMenuItem();
        _autoPurgeMenuItem = new WinForms.ToolStripMenuItem();
        _deepMenuItem      = new WinForms.ToolStripMenuItem();
        _gameModeMenuItem  = new WinForms.ToolStripMenuItem();
        _lockMenuItem      = new WinForms.ToolStripMenuItem();
        _exitMenuItem      = new WinForms.ToolStripMenuItem();

        _showMenuItem.Click      += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _settingsMenuItem.Click  += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _timerMenuItem.Click     += (_, _) => TimerToggleRequested?.Invoke(this, EventArgs.Empty);
        _purgeMenuItem.Click     += (_, _) => PurgeRequested?.Invoke(this, EventArgs.Empty);
        _autoPurgeMenuItem.Click += (_, _) => AutoPurgeToggleRequested?.Invoke(this, EventArgs.Empty);
        _deepMenuItem.Click      += (_, _) => DeepPurgeToggleRequested?.Invoke(this, EventArgs.Empty);
        _gameModeMenuItem.Click  += (_, _) => GameModeToggleRequested?.Invoke(this, EventArgs.Empty);
        _lockMenuItem.Click      += (_, _) => TimerLockCycleRequested?.Invoke(this, EventArgs.Empty);
        _exitMenuItem.Click      += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(_showMenuItem);
        menu.Items.Add(_settingsMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_timerMenuItem);
        menu.Items.Add(_purgeMenuItem);
        // Quick settings: the label carries the current state.
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_autoPurgeMenuItem);
        menu.Items.Add(_deepMenuItem);
        menu.Items.Add(_gameModeMenuItem);
        menu.Items.Add(_lockMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_exitMenuItem);

        foreach (WinForms.ToolStripItem item in menu.Items)
            if (item is WinForms.ToolStripMenuItem mi)
                mi.Padding = new WinForms.Padding(8, 4, 8, 4);

        _menu = menu;
        ApplyPalette(menu, _palette);
        return menu;
    }

    public void ApplyTheme(Theme effectiveTheme)
    {
        _palette = TrayMenuPalette.For(effectiveTheme);
        if (_menu is not null) ApplyPalette(_menu, _palette);
    }

    private static void ApplyPalette(WinForms.ContextMenuStrip menu, TrayMenuPalette palette)
    {
        menu.Renderer  = new TrayMenuRenderer(palette);
        menu.BackColor = palette.SurfaceBg;
        menu.ForeColor = palette.AppFg;

        // Per-item color overrides — the menu's BackColor/ForeColor alone
        // don't propagate to children when using a ProfessionalRenderer.
        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            item.BackColor = palette.SurfaceBg;
            item.ForeColor = palette.AppFg;
        }
    }

    public void Refresh(TrayState state)
    {
        _state = state;
        RefreshLabels(state);

        if (_notifyIcon is null) return;
        try
        {
            _notifyIcon.Icon = state.TimerActive ? (_iconActive ?? _iconBase!) : _iconBase!;
            _notifyIcon.Text = FormatTooltip(state);
        }
        catch (Exception ex) { Logger.Warn($"TrayIconService.Refresh: {ex.Message}"); }
    }

    internal string FormatTooltip(TrayState state)
    {
        string timer = _localization.GetString(
            state.TimerActive ? "Str_Tray_TimerStateOn" : "Str_Tray_TimerStateOff");
        string fmt = _localization.GetString("Str_Tray_TooltipFormat");
        string tip = string.Format(CultureInfo.CurrentCulture, fmt, timer, state.PurgeCount);

        if (state.FreeRamMb > 0 || state.StandbyRamMb > 0)
        {
            string ramFmt = _localization.GetString("Str_Tray_TooltipRamFormat");
            if (!string.IsNullOrEmpty(ramFmt) && ramFmt != "Str_Tray_TooltipRamFormat")
            {
                tip += "\n" + string.Format(CultureInfo.CurrentCulture, ramFmt,
                    MainViewModel.FormatSize(state.FreeRamMb),
                    MainViewModel.FormatSize(state.StandbyRamMb));
            }
        }

        if (tip.Length > TooltipMaxLength) tip = tip[..TooltipMaxLength];
        return tip;
    }

    // Language change: same state, new words (it used to reset the timer
    // item to "Enable" until the next 3 s refresh).
    public void RefreshLabels() => RefreshLabels(_state);

    private void RefreshLabels(TrayState state)
    {
        if (_showMenuItem is not null)
            _showMenuItem.Text = _localization.GetString("Str_Tray_Show");

        if (_settingsMenuItem is not null)
            _settingsMenuItem.Text = _localization.GetString("Str_Tray_Settings");

        if (_timerMenuItem is not null)
            _timerMenuItem.Text = _localization.GetString(
                state.TimerActive ? "Str_Tray_TimerDisable" : "Str_Tray_TimerEnable");

        if (_purgeMenuItem is not null)
            _purgeMenuItem.Text = _localization.GetString("Str_Tray_PurgeNow");

        string OnOff(bool on) => _localization.GetString(on ? "Str_Tray_On" : "Str_Tray_Off");
        if (_autoPurgeMenuItem is not null)
            _autoPurgeMenuItem.Text = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Tray_AutoPurge"), OnOff(state.AutoPurge));
        if (_deepMenuItem is not null)
            _deepMenuItem.Text = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Tray_DeepPurge"), OnOff(state.DeepPurge));
        if (_gameModeMenuItem is not null)
            _gameModeMenuItem.Text = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Tray_GameMode"), OnOff(state.GameMode));
        if (_lockMenuItem is not null)
            _lockMenuItem.Text = string.Format(CultureInfo.CurrentCulture,
                _localization.GetString("Str_Tray_TimerLock"),
                _localization.GetString(state.TimerLock switch
                {
                    TimerLockMode.Locked => "Str_Card_TimerLocked",
                    TimerLockMode.InGame => "Str_Card_TimerInGame",
                    _                    => "Str_Card_TimerUnlocked",
                }));

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
