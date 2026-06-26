namespace StandbyAndTimer.Core.Interfaces;

public interface ITimerResolutionService : IDisposable
{
    bool IsActive { get; }

    /// <summary>
    /// Raised by the self-heal watchdog whenever it re-samples the active
    /// timer period. Lets the ViewModel update its "actual ms" display after
    /// the OS has finished settling on the requested resolution (the very
    /// first sample after Activate is often stale).
    /// </summary>
    event EventHandler<double>? ResolutionMeasured;

    /// <summary>
    /// Sets system timer resolution to 0.5 ms (5000 units) and boosts scheduling.
    /// Returns the actual achieved resolution in milliseconds.
    /// </summary>
    double Activate();

    /// <summary>
    /// Same as <see cref="Activate"/> but waits a short warm-up window after
    /// NtSetTimerResolution before sampling the actual period. The first Set
    /// on a cold process (startup, autostart) typically needs a few cycles
    /// before GetSystemTimeAsFileTime reflects the new period — without the
    /// delay the first measurement returns the OS default (~15.6 ms).
    /// </summary>
    Task<double> ActivateAsync(CancellationToken ct = default);

    /// <summary>Restores system timer resolution to OS default.</summary>
    void Deactivate();
}
