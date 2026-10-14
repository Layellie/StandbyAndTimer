namespace StandbyAndTimer.Core.Interfaces;

/// <summary>
/// Windows 11's GlobalTimerResolutionRequests switch. Since Windows 10 2004 a
/// timer resolution request only reaches processes that ask for one
/// themselves; games that never ask stay at the 15.6 ms default whatever
/// this app requests. With the switch on, requests are system-wide again.
/// Read by the kernel at boot, so a change needs a restart.
/// </summary>
public interface IGlobalTimerSetting
{
    /// <summary>Windows 11 or later; the value does nothing on Windows 10.</summary>
    bool IsSupported { get; }

    /// <summary>The value is 1 in the registry now (in effect after the next restart if it was just set).</summary>
    bool IsEnabled { get; }

    /// <summary>Writes (true) or removes (false) the value. False when that failed; the reason is logged.</summary>
    bool TrySetEnabled(bool enabled);

    /// <summary>When Windows last started, to tell whether a change is in effect yet.</summary>
    DateTime LastBootUtc { get; }
}
