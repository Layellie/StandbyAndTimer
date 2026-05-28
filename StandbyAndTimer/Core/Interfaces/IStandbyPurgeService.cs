namespace StandbyAndTimer.Core.Interfaces;

public interface IStandbyPurgeService
{
    /// <summary>Fired on the thread-pool each time a purge completes successfully.</summary>
    event EventHandler? PurgeSucceeded;

    /// <summary>
    /// Purges the Windows Standby List via NtSetSystemInformation(80).
    /// Requires SeProfileSingleProcessPrivilege (granted by requireAdministrator manifest).
    /// Returns true if the kernel call succeeded.
    /// </summary>
    Task<bool> PurgeAsync();
}
