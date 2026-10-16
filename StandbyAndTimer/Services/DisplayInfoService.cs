using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

/// <summary>The primary display's current mode (EnumDisplaySettings). Read on demand, never polled.</summary>
internal sealed class DisplayInfoService : IDisplayInfoService
{
    public int GetRefreshRate()
    {
        var mode = new NativeMethods.DEVMODE { dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODE>() };
        if (!NativeMethods.EnumDisplaySettings(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode)) return 0;
        // 0 and 1 both mean "the hardware's default rate": not a number to show.
        return mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 0;
    }
}
