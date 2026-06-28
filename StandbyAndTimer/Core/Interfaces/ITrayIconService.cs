namespace StandbyAndTimer.Core.Interfaces;

// Owns the WinForms NotifyIcon + its dark-themed context menu. App.xaml.cs
// hands it the icon, viewmodel, and localization service; the tray service
// handles icon swaps, balloon notifications, and menu label refreshes from
// there on. Keeping this behind an interface means App.xaml.cs no longer
// needs to know anything about WinForms Shell_NotifyIcon plumbing.
public interface ITrayIconService : IDisposable
{
    /// <summary>Invoked when the user picks "Show" from the tray context menu — always surfaces the window.</summary>
    event EventHandler? ShowRequested;

    /// <summary>Invoked on a left-click / double-click of the tray icon — App toggles window visibility.</summary>
    event EventHandler? ToggleRequested;

    /// <summary>Invoked when the user picks "Exit" from the tray menu.</summary>
    event EventHandler? ExitRequested;

    /// <summary>Invoked when the user picks "Enable/Disable Timer" from the tray menu.</summary>
    event EventHandler? TimerToggleRequested;

    /// <summary>Invoked when the user picks "Purge Standby Now" from the tray menu.</summary>
    event EventHandler? PurgeRequested;

    /// <summary>Builds the NotifyIcon, attaches the dark menu, and shows it.</summary>
    void Initialize(System.Drawing.Icon baseIcon, System.Drawing.Icon? activeIcon);

    /// <summary>Re-renders the menu labels + tooltip + active/idle icon. Cheap; safe to call on every tick.</summary>
    void Refresh(bool timerActive, int purgeCount);

    /// <summary>Re-renders just the menu labels — call when language changes.</summary>
    void RefreshLabels();

    /// <summary>Shows a balloon notification. Title and body are pre-localized.</summary>
    void ShowBalloon(string title, string body);
}
