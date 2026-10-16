using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface ISystemToolLauncher
{
    /// <summary>Starts the Windows tool; false (and a log line) when it could not be started.</summary>
    bool Launch(SystemTool tool);
}
