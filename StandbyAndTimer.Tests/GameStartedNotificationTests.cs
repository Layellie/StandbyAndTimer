using StandbyAndTimer.Core.Models;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

public class GameStartedNotificationTests
{
    private const string Exe = @"C:\Windows\System32\notepad.exe";
    private readonly Harness _h = new();
    private readonly List<GameStartedArgs> _shown = [];

    public GameStartedNotificationTests()
    {
        _h.Main.AddGameFromPath(Exe);
        _h.Main.GameStartedNotification += (_, a) => _shown.Add(a);
    }

    [Fact]
    public async Task Once_per_game_start_named_after_the_game()
    {
        await _h.Main.HandleRunningGamesAsync([Exe]);
        await _h.Main.HandleRunningGamesAsync([Exe]);   // still running

        var n = Assert.Single(_shown);
        Assert.Equal("notepad", n.GameName);

        await _h.Main.HandleRunningGamesAsync([]);      // exits
        await _h.Main.HandleRunningGamesAsync([Exe]);   // starts again
        Assert.Equal(2, _shown.Count);
    }

    [Fact]
    public async Task Says_what_the_app_does_for_this_game()
    {
        _h.Main.GameModeEnabled  = true;
        _h.Main.AutoPurgeEnabled = true;

        await _h.Main.HandleRunningGamesAsync([Exe]);

        string body = Assert.Single(_shown).Body;
        Assert.Contains("Str_GameNotify_Boost", body);
        Assert.Contains("Str_GameNotify_TimerOff", body);
        Assert.Contains("Str_GameNotify_PurgeOn", body);
    }

    [Fact]
    public async Task Per_game_switches_show_up_in_the_text()
    {
        _h.Main.GameModeEnabled  = true;
        _h.Main.AutoPurgeEnabled = true;
        _h.Main.Games[0].BoostPriority  = false;
        _h.Main.Games[0].AllowAutoPurge = false;

        await _h.Main.HandleRunningGamesAsync([Exe]);

        string body = Assert.Single(_shown).Body;
        Assert.Contains("Str_GameNotify_NoBoost", body);
        Assert.Contains("Str_GameNotify_PurgePaused", body);
    }

    [Fact]
    public async Task Features_that_are_off_are_left_out()
    {
        // Game Mode and auto purge both off: only the timer line.
        await _h.Main.HandleRunningGamesAsync([Exe]);

        Assert.Equal("Str_GameNotify_TimerOff", Assert.Single(_shown).Body);
    }

    [Fact]
    public async Task In_game_lock_reports_the_timer_it_just_turned_on()
    {
        var h = new Harness(new AppSettings { TimerLock = TimerLockMode.InGame, FirstRunCompleted = true });
        await h.Main.InitializeAsync();
        h.Main.AddGameFromPath(Exe);
        var shown = new List<GameStartedArgs>();
        h.Main.GameStartedNotification += (_, a) => shown.Add(a);

        await h.Main.HandleRunningGamesAsync([Exe]);

        Assert.True(h.Main.TimerActive);
        Assert.Contains("Str_GameNotify_TimerOn", Assert.Single(shown).Body);
    }
}
