using StandbyAndTimer.Services.Native;

namespace StandbyAndTimer.Services;

/// <summary>Whether the PC runs on battery. Read on demand; Windows says when it changes (SystemEvents.PowerModeChanged), nothing polls.</summary>
internal static class PowerSource
{
    internal static bool IsOnBattery() =>
        NativeMethods.GetSystemPowerStatus(out var status) && OnBatteryFrom(status.ACLineStatus, status.BatteryFlag);

    /// <summary>
    /// ACLineStatus 0 = unplugged, 1 = plugged in, 255 = unknown; BatteryFlag
    /// bit 128 = no battery. A desktop (or an unknown state) is never on battery.
    /// </summary>
    internal static bool OnBatteryFrom(byte acLineStatus, byte batteryFlag) =>
        acLineStatus == 0 && (batteryFlag & 128) == 0;
}
