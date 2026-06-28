using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

/// <summary>
/// Registers process-global hotkeys against the main window's HWND.
/// Each binding fires its handler on the WPF dispatcher; the WndProc hook
/// dispatches itself, so consumers don't have to.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>
    /// Called once after MainWindow's HwndSource is initialized so the service
    /// knows which HWND will receive the WM_HOTKEY messages. Subsequent
    /// AttachTo calls release any registrations bound to the previous HWND.
    /// </summary>
    void AttachTo(IntPtr hwnd);

    /// <summary>
    /// Registers <paramref name="binding"/> under <paramref name="id"/>.
    /// Returns false if Win32 refused (combo taken by another app, no HWND
    /// attached, etc.). Replaces any prior registration for the same id.
    /// </summary>
    bool Register(string id, HotkeyBinding binding, Action handler);

    /// <summary>Releases the hotkey by id. No-op if it wasn't registered.</summary>
    void Unregister(string id);
}
