using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// Seen on a real PC: fullscreen detection added Windows Photos (a picture
// viewed fullscreen) and the Claude desktop app as games.
public class DetectionFalsePositiveTests
{
    private const string Photos = @"C:\Program Files\WindowsApps\Microsoft.Windows.Photos_2026.11090.22001.0_x64__8wekyb3d8bbwe\Photos.exe";
    private const string Claude = @"C:\Program Files\WindowsApps\Claude_4.16336.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe";

    [Theory]
    [InlineData(Photos, "Photos")]
    [InlineData(Claude, "Claude")]
    public void Viewers_and_desktop_apps_in_fullscreen_are_not_games(string path, string process)
    {
        var detection = new GameDetectionService();

        Assert.False(detection.ShouldReport(path, process));
        Assert.False(detection.ShouldReport(path, process));
    }

    private static GameEntry Game(string path, bool auto) =>
        new() { DisplayName = System.IO.Path.GetFileNameWithoutExtension(path), ExecutablePath = path, AutoAdded = auto };

    [Fact]
    public async Task One_that_detection_added_before_leaves_the_list_at_the_next_start()
    {
        var h = new Harness(new AppSettings
        {
            FirstRunCompleted = true,
            Games = [Game(@"C:\Games\Shooter\Shooter.exe", auto: true), Game(Photos, auto: true)],
        });
        await h.Main.InitializeAsync();

        Assert.Equal(@"C:\Games\Shooter\Shooter.exe", Assert.Single(h.Main.Games).ExecutablePath);
    }

    [Fact]
    public async Task One_the_user_added_by_hand_stays()
    {
        var h = new Harness(new AppSettings { FirstRunCompleted = true, Games = [Game(Photos, auto: false)] });
        await h.Main.InitializeAsync();

        Assert.Single(h.Main.Games);
    }
}
