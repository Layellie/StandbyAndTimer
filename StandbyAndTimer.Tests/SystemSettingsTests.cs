using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

public class SystemSettingsTests
{
    private sealed class FakeGlobalTimer(bool supported = true, bool enabled = false, bool writable = true) : IGlobalTimerSetting
    {
        public bool IsSupported => supported;
        public bool IsEnabled { get; private set; } = enabled;
        public List<bool> Writes { get; } = [];
        public DateTime LastBootUtc { get; set; } = DateTime.UtcNow.AddHours(-2);
        public bool TrySetEnabled(bool value)
        {
            if (!writable) return false;
            Writes.Add(value); IsEnabled = value; return true;
        }
    }

    private static (SystemViewModel vm, FakeGlobalTimer reg, List<int> saves) Make(
        FakeGlobalTimer reg, bool answered = false, DateTime? changed = null, bool bootValue = false)
    {
        var vm = new SystemViewModel(reg);
        var saves = new List<int>();
        vm.Initialize(answered, setByApp: false, changed, bootValue);
        vm.SettingsChanged += (_, _) => saves.Add(1);
        return (vm, reg, saves);
    }

    [Fact]
    public void Windows_11_with_the_switch_off_asks_once()
    {
        var (vm, _, _) = Make(new FakeGlobalTimer());

        vm.MaybeShowPrompt();

        Assert.True(vm.IsPromptVisible);
    }

    [Theory]
    [InlineData(false, false)]   // Windows 10: nothing to offer
    [InlineData(true,  true)]    // already on (set by hand)
    public void No_question_when_there_is_nothing_to_decide(bool supported, bool enabled)
    {
        var (vm, _, saves) = Make(new FakeGlobalTimer(supported, enabled));

        vm.MaybeShowPrompt();

        Assert.False(vm.IsPromptVisible);
        Assert.True(vm.PromptAnswered);
        Assert.Single(saves);
    }

    [Fact]
    public void An_answered_question_is_not_asked_again()
    {
        var (vm, _, _) = Make(new FakeGlobalTimer(), answered: true);

        vm.MaybeShowPrompt();

        Assert.False(vm.IsPromptVisible);
    }

    [Fact]
    public void Turn_on_writes_the_switch_and_asks_for_a_restart()
    {
        var (vm, reg, saves) = Make(new FakeGlobalTimer());
        vm.MaybeShowPrompt();

        vm.AcceptPromptCommand.Execute(null);

        Assert.Equal([true], reg.Writes);
        Assert.False(vm.IsPromptVisible);
        Assert.True(vm.PromptAnswered);
        Assert.True(vm.GlobalTimerEnabled);
        Assert.True(vm.GlobalTimerSetByApp);
        Assert.True(vm.RestartPending);
        Assert.NotEmpty(saves);
    }

    [Fact]
    public void Not_now_writes_nothing()
    {
        var (vm, reg, saves) = Make(new FakeGlobalTimer());
        vm.MaybeShowPrompt();

        vm.DeclinePromptCommand.Execute(null);

        Assert.Empty(reg.Writes);
        Assert.True(vm.PromptAnswered);
        Assert.False(vm.IsPromptVisible);
        Assert.Single(saves);
    }

    [Fact]
    public void Switching_back_before_a_restart_needs_no_restart()
    {
        var (vm, reg, _) = Make(new FakeGlobalTimer());

        vm.GlobalTimerEnabled = true;
        vm.GlobalTimerEnabled = false;

        Assert.Equal([true, false], reg.Writes);
        Assert.False(vm.RestartPending);
        Assert.False(vm.GlobalTimerSetByApp);
    }

    [Fact]
    public void A_change_from_before_the_last_boot_is_in_effect()
    {
        var reg = new FakeGlobalTimer(enabled: true);
        var (vm, _, _) = Make(reg, answered: true, changed: reg.LastBootUtc.AddMinutes(-5), bootValue: false);

        Assert.False(vm.RestartPending);
    }

    [Fact]
    public void A_change_after_the_last_boot_is_still_pending_on_the_next_start()
    {
        var reg = new FakeGlobalTimer(enabled: true);
        var (vm, _, _) = Make(reg, answered: true, changed: reg.LastBootUtc.AddMinutes(5), bootValue: false);

        Assert.True(vm.RestartPending);
    }

    [Fact]
    public void A_refused_write_shows_the_real_state()
    {
        var (vm, _, _) = Make(new FakeGlobalTimer(writable: false));

        vm.GlobalTimerEnabled = true;

        Assert.False(vm.GlobalTimerEnabled);
        Assert.True(vm.WriteFailed);
        Assert.False(vm.RestartPending);
    }

    [Fact]
    public async Task System_state_is_saved_and_survives_an_import()
    {
        var reg = new FakeGlobalTimer();
        var system = new SystemViewModel(reg);
        var h = new HarnessWithSystem(system);
        await h.Main.InitializeAsync();
        system.AcceptPromptCommand.Execute(null);

        Assert.True(h.Store.Saved[^1].GlobalTimerPromptAnswered);
        Assert.True(h.Store.Saved[^1].GlobalTimerSetByApp);
        Assert.NotNull(h.Store.Saved[^1].GlobalTimerChangedUtc);
    }

    // Harness with a real SystemViewModel (the shared one uses the inert default).
    private sealed class HarnessWithSystem
    {
        public FakeSettingsStore Store { get; } = new(new AppSettings { FirstRunCompleted = true });
        public MainViewModel Main { get; }

        public HarnessWithSystem(SystemViewModel system)
        {
            var loc = new FakeLocalization();
            var settings = new SettingsViewModel(NullProxy.Of<IAutoStartService>(), loc, Store,
                NullProxy.Of<IUpdateService>(), NullProxy.Of<IThemeService>(), NullProxy.Of<ILogTailReader>());
            Main = new MainViewModel(new FakeTimer(), NullProxy.Of<IStandbyPurgeService>(),
                NullProxy.Of<IMemoryMonitorService>(), Store, loc, NullProxy.Of<IGameDetectionService>(),
                settings, system);
        }
    }
}
