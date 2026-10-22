using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

public class GameDetectionTests
{
    private readonly GameDetectionService _det = new();
    private const string Game = @"C:\Games\Hades\Hades.exe";

    [Fact]
    public void A_game_is_reported_on_the_second_consecutive_fullscreen_poll_only()
    {
        Assert.False(_det.ShouldReport(Game, "Hades"));
        Assert.True(_det.ShouldReport(Game, "Hades"));
    }

    [Fact]
    public void A_game_is_reported_once_per_session()
    {
        _det.ShouldReport(Game, "Hades");
        _det.ShouldReport(Game, "Hades");

        Assert.False(_det.ShouldReport(Game, "Hades"));
        Assert.False(_det.ShouldReport(Game, "Hades"));
    }

    [Fact]
    public void A_poll_without_fullscreen_resets_the_count()
    {
        _det.ShouldReport(Game, "Hades");
        _det.OnNotFullscreen();

        Assert.False(_det.ShouldReport(Game, "Hades"));
    }

    [Theory]
    [InlineData(@"C:\Users\x\Discord.exe", "Discord")]
    [InlineData(@"C:\W\SnippingTool.exe", "SnippingTool")]
    [InlineData(@"C:\P\chrome.exe", "chrome")]
    [InlineData(@"C:\P\vlc.exe", "vlc")]
    public void Known_non_games_are_never_reported(string path, string proc)
    {
        Assert.False(_det.ShouldReport(path, proc));
        Assert.False(_det.ShouldReport(path, proc));
    }

    [Fact]
    public void Known_paths_are_skipped()
    {
        _det.KnownGamePaths = new HashSet<string>([Game], StringComparer.OrdinalIgnoreCase);

        Assert.False(_det.ShouldReport(Game, "Hades"));
        Assert.False(_det.ShouldReport(Game, "Hades"));
    }
}

public class GameListTests
{
    // Must exist: AddGameFromPath checks File.Exists.
    private const string Exe = @"C:\Windows\System32\notepad.exe";
    private readonly Harness _h = new();

    [Fact]
    public void A_detected_game_is_added_tagged_auto_and_persisted()
    {
        _h.Main.AutoAddDetectedGame(Exe);

        var g = Assert.Single(_h.Main.Games);
        Assert.True(g.AutoAdded);
        Assert.Equal("notepad", g.DisplayName);
        Assert.Contains(_h.LastSaved.Games, x => x.AutoAdded && x.ExecutablePath == Exe);
    }

    [Fact]
    public void Re_detection_does_not_duplicate()
    {
        _h.Main.AutoAddDetectedGame(Exe);
        _h.Main.AutoAddDetectedGame(Exe);

        Assert.Single(_h.Main.Games);
    }

    [Fact]
    public void Removing_an_auto_added_game_dismisses_it_for_good()
    {
        _h.Main.AutoAddDetectedGame(Exe);
        _h.Main.SelectedGame = _h.Main.Games[0];
        _h.Main.RemoveGameCommand.Execute(null);

        Assert.Contains(Exe, _h.LastSaved.IgnoredGamePaths);

        _h.Main.AutoAddDetectedGame(Exe);
        Assert.Empty(_h.Main.Games);
    }

    [Fact]
    public void Adding_by_hand_clears_a_dismissal_and_user_games_are_not_dismissed()
    {
        _h.Main.AutoAddDetectedGame(Exe);
        _h.Main.SelectedGame = _h.Main.Games[0];
        _h.Main.RemoveGameCommand.Execute(null);

        _h.Main.AddGameFromPath(Exe);
        Assert.False(Assert.Single(_h.Main.Games).AutoAdded);
        Assert.DoesNotContain(Exe, _h.LastSaved.IgnoredGamePaths);

        _h.Main.SelectedGame = _h.Main.Games[0];
        _h.Main.RemoveGameCommand.Execute(null);
        Assert.DoesNotContain(Exe, _h.LastSaved.IgnoredGamePaths);
    }

    [Theory]
    [InlineData(1,   0x1L)]
    [InlineData(8,   0xFFL)]
    [InlineData(16,  0xFFFFL)]
    [InlineData(32,  0xFFFFFFFFL)]
    [InlineData(63,  0x7FFFFFFFFFFFFFFFL)]
    [InlineData(64,  -1L)]
    [InlineData(128, -1L)]
    public void Affinity_mask_covers_all_logical_cores_including_64(int cpuCount, long expectedMask)
    {
        Assert.Equal(expectedMask, ProcessOptimizationService.AffinityMaskFor(cpuCount));
    }
}
