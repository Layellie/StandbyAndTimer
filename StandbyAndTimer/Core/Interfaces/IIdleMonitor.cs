namespace StandbyAndTimer.Core.Interfaces;

/// <summary>
/// Reports how long it has been since the user's last keyboard / mouse /
/// touch input reached the input queue. Wraps <c>GetLastInputInfo</c>;
/// the kernel maintains the tick under the lock screen, so locking the
/// workstation does not reset the counter (matches Windows' own "Away"
/// behavior).
/// </summary>
public interface IIdleMonitor
{
    /// <summary>Time elapsed since the most recent input event.</summary>
    TimeSpan TimeSinceLastInput { get; }
}
