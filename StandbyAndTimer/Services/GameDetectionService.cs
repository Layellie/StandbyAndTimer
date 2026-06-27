using System.Diagnostics;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

// Polls the foreground window every few seconds. If it fully covers its
// monitor (classic exclusive-fullscreen or borderless-fullscreen) and its
// owning process isn't already in the user's Game Mode list, raise
// GameDetected once. The ViewModel hands the exe path to the tray which
// shows a balloon — clicking it adds the game.
//
// Why not WMI / ETW / D3DKMT? They're heavier, require extra permissions
// in some cases, and the false-positive surface is bigger. A simple
// "window covers the monitor" check catches every fullscreen game that
// matters and almost nothing else (Explorer, browsers, IDEs don't run
// fullscreen). The ~3 sec poll cost is one syscall trio per tick.
internal sealed class GameDetectionService : IGameDetectionService
{
    private const int  PollIntervalMs = 3_000;

    // System processes whose windows commonly go fullscreen but should
    // never be promoted to "game" — e.g. lockscreen, sign-in UI, the shell.
    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "logonui", "lockapp", "shellexperiencehost",
        "searchhost", "applicationframehost", "winlogon", "csrss",
        "standbyandtimer",
    };

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
        if (hwnd == IntPtr.Zero) return;
        if (!IsFullscreen(hwnd)) return;

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

        if (string.IsNullOrWhiteSpace(exePath))               return;
        if (IgnoredProcessNames.Contains(procName))           return;
        if (KnownGamePaths.Contains(exePath))                 return;
        if (!_alreadyNotified.Add(exePath))                   return;

        GameDetected?.Invoke(this, exePath);
        Logger.Info($"GameDetection: fullscreen process detected — {procName} ({exePath})");
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
