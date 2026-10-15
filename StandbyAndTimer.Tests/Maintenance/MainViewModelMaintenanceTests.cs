using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests.Maintenance;

public class MainViewModelMaintenanceTests
{
    private static (MainViewModel main, MaintenanceViewModel m, FakeSettingsStore store) Make()
    {
        var store = new FakeSettingsStore(null);
        var loc = new FakeLocalization();
        var settings = new SettingsViewModel(NullProxy.Of<IAutoStartService>(), loc, store, NullProxy.Of<IUpdateService>(),
                                             NullProxy.Of<IThemeService>(), NullProxy.Of<ILogTailReader>());
        var m = new MaintenanceViewModel([new FakeTarget("user-temp", new CleanupScan(true, 1, 1)),
                                          new FakeTarget("recycle-bin", new CleanupScan(true, 1, 1))],
                                         new InMemoryCleanupStatsStore(), NullProxy.Of<ISystemInfoService>(),
                                         new FakeTools(), NullProxy.Of<IDownloadsService>(), loc);
        var main = new MainViewModel(new FakeTimer(), NullProxy.Of<IStandbyPurgeService>(), NullProxy.Of<IMemoryMonitorService>(),
                                     store, loc, NullProxy.Of<IGameDetectionService>(), settings, maintenance: m);
        return (main, m, store);
    }

    [Fact]
    public void Ticking_a_row_saves_the_selection()
    {
        var (_, m, store) = Make();
        m.Initialize(null);

        m.Rows[1].IsChecked = true;

        Assert.Equal("user-temp,recycle-bin", store.Saved[^1].CleanupSelection);
    }

    [Fact]
    public void Tabs_drive_settings_open_and_maintenance_active()
    {
        var (main, m, _) = Make();

        main.SelectedTab = MainViewModel.MaintenanceTab;
        Assert.True(m.IsActive);
        Assert.False(main.Settings.IsOpen);

        main.SelectedTab = MainViewModel.SettingsTab;
        Assert.False(m.IsActive);
        Assert.True(main.Settings.IsOpen);

        main.Settings.IsOpen = false;               // e.g. something closes Settings
        Assert.Equal(MainViewModel.StatusTab, main.SelectedTab);

        main.Settings.IsOpen = true;                // tray "Settings"
        Assert.Equal(MainViewModel.SettingsTab, main.SelectedTab);
    }
}
