using System.IO;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

// Polls the foreground window every few seconds. If it fully covers its
// monitor (classic exclusive-fullscreen or borderless-fullscreen) on two
// consecutive polls and its owning process isn't already known (listed or
// dismissed by the user) or a known non-game, raise GameDetected once. The
// ViewModel adds it to the Game Mode list (tagged AUTO) and the tray shows
// a balloon.
//
// Why not WMI / ETW / D3DKMT? They're heavier, require extra permissions
// in some cases, and the false-positive surface is bigger. A simple
// "window covers the monitor" check catches every fullscreen game that
// matters and almost nothing else (Explorer, browsers, IDEs don't run
// fullscreen). The ~3 sec poll cost is one syscall trio per tick.
internal sealed class GameDetectionService : IGameDetectionService
{
    private const int  PollIntervalMs = 3_000;

    // Processes whose windows commonly go fullscreen but should never be
    // promoted to "game" now that detection adds to the list on its own:
    // the shell / sign-in UI, plus apps measured or expected to cover the
    // monitor (Discord and the Snipping Tool overlay were the only two
    // "detections" in a real day's log; browsers / players in F11 mode).
    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows shell and system UI
        "explorer", "dwm", "logonui", "lockapp", "shellexperiencehost",
        "searchhost", "applicationframehost", "winlogon", "csrss",
        "snippingtool", "screenclippinghost", "mstsc", "msrdc",
        "standbyandtimer",
        // Chat, calls, streaming
        "discord", "ms-teams", "teams", "zoom", "obs64", "obs32",
        // Browsers (F11 / video fullscreen)
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi",
        // Video players and presentations
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayermini",
        "potplayermini64", "wmplayer", "microsoft.media.player", "video.ui", "powerpnt",
        // Picture viewers and desktop apps that were added as games on a real PC
        "photos", "claude",
    };

    /// <summary>
    /// Whether detection never reports a process of this name. The view model
    /// also uses it at start-up, so an entry detection added before the name
    /// was on this list leaves the Game Mode list again (never one the user added).
    /// </summary>
    internal static bool IsIgnoredProcess(string processName) => IgnoredProcessNames.Contains(processName);

    // A fullscreen window must still be fullscreen (same exe) on the NEXT
    // poll before it is reported — momentary overlays such as the Snipping
    // Tool capture screen never survive two polls (~6 s).
    private string? _pendingPath;

    private readonly HashSet<string> _alreadyNotified = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task?                    _loop;
    private bool                     _disposed;

    public event EventHandler<string>? GameDetected;

    // The process in front that is already decided (listed, ignored, reported,
    // unreadable). While it stays fullscreen in front (a whole gaming session,
    // one poll every 3 s) it is not opened again.
    private uint _settledPid;

    private IReadOnlySet<string> _knownGamePaths = new HashSet<string>();

    public IReadOnlySet<string> KnownGamePaths
    {
        get => _knownGamePaths;
        set
        {
            _knownGamePaths = value;
            _settledPid = 0;   // the list changed: decide again
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch { }
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _cts  = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Tick(); }
                catch (Exception ex) { Logger.Warn($"GameDetection tick: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { /* expected on Stop */ }
    }

    private void Tick()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsFullscreen(hwnd))
        {
            OnNotFullscreen();
            return;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;

        if (Observe(pid, ImagePath) is not { } exePath) return;

        GameDetected?.Invoke(this, exePath);
        Logger.Info($"GameDetection: fullscreen process detected — {exePath}");
    }

    /// <summary>
    /// One poll that found process <paramref name="pid"/> fullscreen in front.
    /// Returns the exe path to report now, else null. The path is looked up
    /// only until the process is decided.
    /// </summary>
    internal string? Observe(uint pid, Func<uint, string?> pathOf)
    {
        if (pid == _settledPid) return null;

        string? exePath = pathOf(pid);
        bool report = false;
        if (exePath is null) _pendingPath = null;   // not the candidate's second poll in a row
        else report = ShouldReport(exePath, RunningGames.ShortName(Path.GetFileName(exePath)));
        // No candidate waiting for its second poll: this process is decided.
        if (_pendingPath is null) _settledPid = pid;
        return report ? exePath : null;
    }

    /// <summary>
    /// Full path of a process's exe, or null (exited, or it refuses even this).
    /// Asks with PROCESS_QUERY_LIMITED_INFORMATION: Process.MainModule, used
    /// before, opens the process for reading its memory and lists its modules
    /// (measured 353 µs against 12 µs), and anti-cheat protected games refuse
    /// that, so they were never detected.
    /// </summary>
    internal static string? ImagePath(uint pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[MaxPathChars];
            int length = buffer.Length;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length)
                ? new string(buffer, 0, length)
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    // Longest path Windows can return (long-path aware apps).
    private const int MaxPathChars = 32_768;

    /// <summary>
    /// Decision for one poll that found <paramref name="exePath"/> fullscreen
    /// in the foreground. Win32-free so it can be exercised directly. True =
    /// report it now (second consecutive sighting, not known / ignored /
    /// already reported this session).
    /// </summary>
    internal bool ShouldReport(string? exePath, string procName)
    {
        if (string.IsNullOrWhiteSpace(exePath)
            || IgnoredProcessNames.Contains(procName)
            || KnownGamePaths.Contains(exePath)
            || _alreadyNotified.Contains(exePath))
        {
            _pendingPath = null;
            return false;
        }

        if (!string.Equals(_pendingPath, exePath, StringComparison.OrdinalIgnoreCase))
        {
            _pendingPath = exePath;   // first sighting: wait for the next poll
            return false;
        }

        _pendingPath = null;
        _alreadyNotified.Add(exePath);
        return true;
    }

    /// <summary>A poll where nothing is fullscreen: the pending candidate resets.</summary>
    internal void OnNotFullscreen()
    {
        _pendingPath = null;
        _settledPid  = 0;
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var win)) return false;

        IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;

        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

        // "Fullscreen" = window rect equals monitor rect exactly. Borderless
        // fullscreen games match this; windowed games don't.
        return win.Left  == mi.rcMonitor.Left
            && win.Top   == mi.rcMonitor.Top
            && win.Right == mi.rcMonitor.Right
            && win.Bottom == mi.rcMonitor.Bottom;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }
}
