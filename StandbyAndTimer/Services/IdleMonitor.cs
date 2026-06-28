using System.Runtime.InteropServices;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

internal sealed class IdleMonitor : IIdleMonitor
{
    public TimeSpan TimeSinceLastInput
    {
        get
        {
            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
            };
            if (!NativeMethods.GetLastInputInfo(ref info))
                return TimeSpan.Zero;

            // Environment.TickCount wraps every ~24.8 days; an unchecked
            // subtraction over the wrap point still yields the correct
            // positive delta because both sides are read as uint.
            uint delta = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(delta);
        }
    }
}
