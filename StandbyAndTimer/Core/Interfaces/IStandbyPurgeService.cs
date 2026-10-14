namespace StandbyAndTimer.Core.Interfaces;

public interface IStandbyPurgeService
{
    /// <summary>Fired on the thread-pool each time a purge completes successfully.</summary>
    event EventHandler? PurgeSucceeded;

    /// <summary>
    /// False = purge the whole standby list; true = purge only its low-priority
    /// part, so frequently used files stay cached. Set by MainViewModel and
    /// applied to every purge (button, auto purge, hotkey, tray).
    /// </summary>
    bool LightMode { get; set; }

    /// <summary>
    /// Deep purge: write out the modified page list and trim the system file
    /// cache first, so their pages are on the standby list when it is purged.
    /// Wins over <see cref="LightMode"/>. Applied to every purge.
    /// </summary>
    bool DeepMode { get; set; }

    /// <summary>
    /// Purges the Windows Standby List via NtSetSystemInformation(80).
    /// Requires SeProfileSingleProcessPrivilege (granted by requireAdministrator manifest).
    /// Returns true if the kernel call succeeded.
    /// </summary>
    Task<bool> PurgeAsync();
}
