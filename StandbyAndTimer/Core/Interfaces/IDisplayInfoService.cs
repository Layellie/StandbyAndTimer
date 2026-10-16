namespace StandbyAndTimer.Core.Interfaces;

public interface IDisplayInfoService
{
    /// <summary>The primary display's current refresh rate in Hz; 0 when unknown.</summary>
    int GetRefreshRate();
}
