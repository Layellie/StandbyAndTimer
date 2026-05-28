namespace StandbyAndTimer.Core.Interfaces;

public interface IAutoStartService
{
    /// <summary>Registers the executable in Task Scheduler to run at logon with highest privileges.</summary>
    Task EnableAsync(string executablePath);

    /// <summary>Removes the scheduled task if it exists.</summary>
    Task DisableAsync();
}
