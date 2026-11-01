using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Tests;

// Loading the saved list must never drop a game the user added: the next save
// would erase it (and its per-game rules) from the registry for good. Only
// exact duplicates and AUTO-added helpers / duplicates are cleaned up.
public class SavedGamesLoadTests
{
    private static GameEntry Game(string path, bool auto = false) =>
        new() { DisplayName = System.IO.Path.GetFileNameWithoutExtension(path), ExecutablePath = path, AutoAdded = auto };

    private static async Task<Harness> LoadAsync(params GameEntry[] games)
    {
        var h = new Harness(new AppSettings { FirstRunCompleted = true, Games = [.. games] });
        await h.Main.InitializeAsync();
        return h;
    }

    [Fact]
    public async Task Games_the_user_added_survive_a_restart()
    {
        // Minecraft Java runs as javaw.exe; every RPG Maker game is Game.exe.
        var h = await LoadAsync(
            Game(@"C:\Games\Minecraft\runtime\bin\javaw.exe"),
            Game(@"C:\Games\RpgOne\Game.exe"),
            Game(@"D:\Games\RpgTwo\Game.exe"));

        Assert.Equal(3, h.Main.Games.Count);
    }

    [Fact]
    public async Task Exact_duplicates_and_auto_added_helpers_are_still_dropped()
    {
        var h = await LoadAsync(
            Game(@"C:\Games\Shooter\Shooter.exe"),
            Game(@"c:\games\shooter\shooter.exe"),
            Game(@"C:\Games\Shooter\UnityCrashHandler64.exe", auto: true));

        Assert.Equal(@"C:\Games\Shooter\Shooter.exe", Assert.Single(h.Main.Games).ExecutablePath);
    }
}
