namespace StandbyAndTimer.Core.Interfaces;

public interface IAutoStartService
{
    /// <summary>Registers the executable in Task Scheduler to run at logon with highest privileges. Returns true on success.</summary>
    Task<bool> EnableAsync(string executablePath);

    /// <summary>Removes the scheduled task if it exists. Returns true on success.</summary>
    Task<bool> DisableAsync();
}
