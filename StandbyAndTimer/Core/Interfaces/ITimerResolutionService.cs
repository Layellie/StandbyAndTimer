namespace StandbyAndTimer.Core.Interfaces;

public interface ITimerResolutionService : IDisposable
{
    bool IsActive { get; }

    /// <summary>
    /// Sets system timer resolution to 0.5 ms (5000 units) and boosts scheduling.
    /// Returns the actual achieved resolution in milliseconds.
    /// </summary>
    double Activate();

    /// <summary>Restores system timer resolution to OS default.</summary>
    void Deactivate();
}
