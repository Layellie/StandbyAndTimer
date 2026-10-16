using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

public class RefreshRateTests
{
    private sealed class FakeDisplay(int hz) : IDisplayInfoService
    {
        public int Hz { get; set; } = hz;
        public int GetRefreshRate() => Hz;
    }

    private static MainViewModel Make(IDisplayInfoService display)
    {
        var loc = new FakeLocalization();
        var store = new FakeSettingsStore(null);
        var settings = new SettingsViewModel(NullProxy.Of<IAutoStartService>(), loc, store, NullProxy.Of<IUpdateService>(),
                                             NullProxy.Of<IThemeService>(), NullProxy.Of<ILogTailReader>());
        return new MainViewModel(new FakeTimer(), NullProxy.Of<IStandbyPurgeService>(), NullProxy.Of<IMemoryMonitorService>(),
                                 store, loc, NullProxy.Of<IGameDetectionService>(), settings, display: display);
    }

    [Fact]
    public void The_timer_card_shows_the_refresh_rate_and_follows_changes()
    {
        var display = new FakeDisplay(144);
        var vm = Make(display);

        vm.UpdateRefreshRate();
        Assert.Equal("144 Hz", vm.RefreshRateText);

        display.Hz = 60;                    // e.g. the user changed it in Windows
        vm.UpdateRefreshRate();
        Assert.Equal("60 Hz", vm.RefreshRateText);
    }

    [Fact]
    public void An_unknown_refresh_rate_shows_nothing()
    {
        var vm = Make(new FakeDisplay(0));

        vm.UpdateRefreshRate();

        Assert.Equal("", vm.RefreshRateText);
    }

    // Reads this PC's display: proves the DEVMODE layout (a wrong dmSize makes
    // EnumDisplaySettings fail and return 0).
    [Fact]
    public void Reads_this_pcs_refresh_rate() =>
        Assert.InRange(new DisplayInfoService().GetRefreshRate(), 1, 1000);
}
