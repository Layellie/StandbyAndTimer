using Microsoft.Win32;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

/// <summary>
/// The real HKLM value. Needs administrator rights, so it only runs with
/// SAT_HKLM_TEST=1 in an elevated shell; it restores whatever was there.
/// The kernel reads the value at boot only, so writing it here changes
/// nothing until a restart, and the test leaves it as it found it.
/// </summary>
public class GlobalTimerSettingTests
{
    [Fact]
    public void Writes_reads_and_removes_the_real_value()
    {
        if (Environment.GetEnvironmentVariable("SAT_HKLM_TEST") != "1") return;

        var setting = new GlobalTimerSetting();
        bool before = setting.IsEnabled;
        try
        {
            Assert.True(setting.TrySetEnabled(true));
            Assert.True(setting.IsEnabled);
            using (var key = Registry.LocalMachine.OpenSubKey(GlobalTimerSetting.KeyPath))
                Assert.Equal(RegistryValueKind.DWord, key!.GetValueKind(GlobalTimerSetting.ValueName));

            Assert.True(setting.TrySetEnabled(false));
            Assert.False(setting.IsEnabled);
            using (var key = Registry.LocalMachine.OpenSubKey(GlobalTimerSetting.KeyPath))
                Assert.Null(key!.GetValue(GlobalTimerSetting.ValueName));   // removed, not 0
        }
        finally
        {
            setting.TrySetEnabled(before);
        }
    }

    [Fact]
    public void Last_boot_is_in_the_past() =>
        Assert.True(new GlobalTimerSetting().LastBootUtc < DateTime.UtcNow);
}
