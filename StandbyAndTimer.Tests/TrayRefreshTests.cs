using StandbyAndTimer.Services.Tray;

namespace StandbyAndTimer.Tests;

// The tooltip and the menu labels used to be rebuilt every 3 seconds for as
// long as the app ran. Now they are rebuilt when someone is about to look:
// the mouse reaches the icon, or the menu opens.
public class TrayRefreshTests
{
    [Fact]
    public void The_mouse_reaching_the_icon_asks_for_a_refresh_once_not_for_every_move()
    {
        var tray = new TrayIconService(new FakeLocalization());
        int asked = 0;
        tray.RefreshRequested += (_, _) => asked++;

        tray.OnHover(nowMs: 10_000);
        tray.OnHover(nowMs: 10_010);
        tray.OnHover(nowMs: 10_900);
        Assert.Equal(1, asked);

        tray.OnHover(nowMs: 11_000);   // a second later: the numbers may have moved
        Assert.Equal(2, asked);
    }

    [Fact]
    public void The_very_first_move_asks_even_right_after_start()
    {
        var tray = new TrayIconService(new FakeLocalization());
        int asked = 0;
        tray.RefreshRequested += (_, _) => asked++;

        tray.OnHover(nowMs: 0);

        Assert.Equal(1, asked);
    }
}
