using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// While a game stays fullscreen in front, detection polls every 3 s for the
// whole session. Once the process in front is decided (listed, ignored,
// reported) it is not opened again until something else comes to the front.
public class GameDetectionPollTests
{
    private const string Game = @"C:\Games\Hades\Hades.exe";

    private readonly GameDetectionService _det = new();
    private int _lookups;

    private string? PathOf(uint pid) { _lookups++; return pid == 42 ? Game : null; }

    [Fact]
    public void A_listed_game_in_front_is_looked_up_once_not_at_every_poll()
    {
        _det.KnownGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Game };

        for (int poll = 0; poll < 5; poll++)
            Assert.Null(_det.Observe(42, PathOf));

        Assert.Equal(1, _lookups);
    }

    [Fact]
    public void A_new_game_is_reported_on_its_second_poll_and_left_alone_afterwards()
    {
        Assert.Null(_det.Observe(42, PathOf));          // first sighting: wait
        Assert.Equal(Game, _det.Observe(42, PathOf));   // still in front: report
        Assert.Null(_det.Observe(42, PathOf));
        Assert.Null(_det.Observe(42, PathOf));

        Assert.Equal(2, _lookups);
    }

    [Fact]
    public void Leaving_fullscreen_makes_the_next_poll_look_again()
    {
        _det.KnownGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Game };
        _det.Observe(42, PathOf);
        _det.OnNotFullscreen();
        _det.Observe(42, PathOf);

        Assert.Equal(2, _lookups);
    }

    [Fact]
    public void A_changed_game_list_makes_the_next_poll_look_again()
    {
        _det.KnownGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Game };
        _det.Observe(42, PathOf);
        _det.KnownGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Null(_det.Observe(42, PathOf));          // first sighting again
        Assert.Equal(Game, _det.Observe(42, PathOf));
    }

    [Fact]
    public void A_process_that_cannot_be_read_is_not_asked_again_while_it_stays_in_front()
    {
        for (int poll = 0; poll < 4; poll++)
            Assert.Null(_det.Observe(7, PathOf));

        Assert.Equal(1, _lookups);
    }

    [Fact]
    public void The_running_test_process_has_a_readable_image_path()
    {
        string? path = GameDetectionService.ImagePath((uint)Environment.ProcessId);

        Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
    }
}
