using System.Diagnostics;
using System.IO;

namespace StandbyAndTimer.Services;

/// <summary>
/// Starts a game from the Game Mode list. This app runs as administrator,
/// and a process it starts directly would run elevated too, which breaks
/// things like the Steam overlay and some anti-cheat. Handing the file to
/// explorer.exe makes the running (non-elevated) shell open it, exactly
/// like a double-click on the desktop.
/// </summary>
internal static class GameLauncher
{
    public static bool StartUnelevated(string exePath)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var p = Process.Start(new ProcessStartInfo(explorer, $"\"{exePath}\"") { UseShellExecute = false });
            Logger.Info($"Game launched via the shell: {exePath}");
            return p is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Game launch failed for {exePath}: {ex.Message}");
            return false;
        }
    }
}
