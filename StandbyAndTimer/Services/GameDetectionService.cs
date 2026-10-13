using System.Diagnostics;
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
    };

    // A fullscreen window must still be fullscreen (same exe) on the NEXT
    // poll before it is reported — momentary overlays such as the Snipping
    // Tool capture screen never survive two polls (~6 s).
    private string? _pendingPath;

    private readonly HashSet<string> _alreadyNotified = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task?                    _loop;
    private bool                     _disposed;

    public event EventHandler<string>? GameDetected;

    public IReadOnlySet<string> KnownGamePaths { get; set; } = new HashSet<string>();

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

        // Process.GetProcessById opens a kernel handle — `using` releases it
        // immediately so we don't leak handles to every fullscreen window we
        // sample. MainModule.FileName can throw Access Denied for protected
        // processes (anti-cheat, system services); we treat that as "skip".
        string? exePath;
        string  procName;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            procName = p.ProcessName;
            exePath  = p.MainModule?.FileName;
        }
        catch
        {
            return;
        }

        if (!ShouldReport(exePath, procName)) return;

        GameDetected?.Invoke(this, exePath!);
        Logger.Info($"GameDetection: fullscreen process detected — {procName} ({exePath})");
    }

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
    internal void OnNotFullscreen() => _pendingPath = null;

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
