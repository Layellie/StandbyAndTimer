using System.IO;
using System.Security.Principal;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

/// <summary>
/// Run elevated with SAT_LAUNCH_TEST=&lt;path of a probe exe that writes
/// %TEMP%\sat-elevprobe.txt&gt;: from an elevated process, GameLauncher must
/// start the game NOT elevated.
/// </summary>
public class GameLaunchIntegrationTests
{
    [Fact]
    public async Task A_game_started_from_the_elevated_app_is_not_elevated()
    {
        string? probe = Environment.GetEnvironmentVariable("SAT_LAUNCH_TEST");
        if (string.IsNullOrEmpty(probe)) return;
        Assert.True(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
                    "run this test elevated");

        string result = Path.Combine(Path.GetTempPath(), "sat-elevprobe.txt");
        File.Delete(result);

        Assert.True(GameLauncher.StartUnelevated(probe));
        for (int i = 0; i < 50 && !File.Exists(result); i++) await Task.Delay(200);

        Assert.True(File.Exists(result), "the probe never ran");
        Assert.Contains("elevated=False", File.ReadAllText(result));
    }
}
