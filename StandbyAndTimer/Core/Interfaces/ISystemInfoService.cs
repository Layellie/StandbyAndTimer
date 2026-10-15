using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface ISystemInfoService
{
    /// <summary>The active power plan, or null if it cannot be read.</summary>
    PowerPlanInfo? GetPowerPlan();

    /// <summary>The page files in use (empty when there is none or it cannot be read).</summary>
    IReadOnlyList<PageFileInfo> GetPageFiles();
}
