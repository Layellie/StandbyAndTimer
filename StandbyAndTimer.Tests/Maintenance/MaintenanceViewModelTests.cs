using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests.Maintenance;

internal sealed class FakeTarget(string id, CleanupScan scan, bool blockedWhileGameRuns = false) : ICleanupTarget
{
    public string Id => id;
    public bool BlockedWhileGameRuns => blockedWhileGameRuns;
    public int Cleaned { get; private set; }
    public Exception? Throw { get; set; }
    public CleanupResult Result { get; set; } = new(100L * 1024 * 1024, 3, 1);
    public TaskCompletionSource? ScanGate { get; set; }
    public bool ScanCompleted { get; private set; }

    public async Task<CleanupScan> ScanAsync(CancellationToken ct)
    {
        if (ScanGate is not null) await ScanGate.Task.WaitAsync(ct);
        ScanCompleted = true;
        return scan;
    }

    public Task<CleanupResult> CleanAsync(CancellationToken ct)
    {
        Cleaned++;
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeTools : ISystemToolLauncher
{
    public List<SystemTool> Launched { get; } = [];
    public bool Result { get; set; } = true;
    public bool Launch(SystemTool tool) { Launched.Add(tool); return Result; }
}

public class MaintenanceViewModelTests
{
    private static CleanupScan Mb(long mb) => new(true, mb * 1024 * 1024, 10);

    private static (MaintenanceViewModel vm, FakeTarget[] t, FakeTools tools, InMemoryCleanupStatsStore store) Make(params FakeTarget[] targets)
    {
        var store = new InMemoryCleanupStatsStore();
        var tools = new FakeTools();
        var vm = new MaintenanceViewModel(targets, store, NullProxy.Of<ISystemInfoService>(), tools,
                                          NullProxy.Of<IDownloadsService>(), new FakeLocalization())
        { Now = () => new DateTime(2026, 10, 16, 20, 0, 0) };
        return (vm, targets, tools, store);
    }

    [Fact]
    public async Task Scan_results_reach_the_rows()
    {
        var (vm, _, _, _) = Make(new FakeTarget("user-temp", Mb(845)), new FakeTarget("amd-shader", CleanupScan.NotFound));

        await vm.RefreshAsync();

        Assert.Equal("845 MB", vm.Rows[0].SizeText);
        Assert.True(vm.Rows[0].CanClean);
        Assert.Equal("Str_Maint_NotFound", vm.Rows[1].SizeText);
        Assert.False(vm.Rows[1].CanClean);
    }

    [Fact]
    public async Task Only_ticked_rows_are_cleaned_and_recorded()
    {
        var (vm, t, _, store) = Make(new FakeTarget("user-temp", Mb(845)), new FakeTarget("windows-update", Mb(2000)));
        vm.Initialize("user-temp");
        await vm.RefreshAsync();

        await vm.CleanSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, t[0].Cleaned);
        Assert.Equal(0, t[1].Cleaned);
        var record = Assert.Single(store.Load().Recent);
        Assert.Equal("user-temp", record.TargetId);
        Assert.Equal(1, record.Skipped);
        Assert.Contains("Str_Maint_ResultFreed", vm.Rows[0].DisplayText);
    }

    [Fact]
    public async Task A_failing_target_does_not_stop_the_others()
    {
        var bad = new FakeTarget("user-temp", Mb(10)) { Throw = new UnauthorizedAccessException("denied") };
        var good = new FakeTarget("windows-temp", Mb(20));
        var (vm, _, _, _) = Make(bad, good);
        vm.Initialize("user-temp,windows-temp");
        await vm.RefreshAsync();

        await vm.CleanSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, good.Cleaned);
        Assert.Contains("Str_Maint_ResultFailed", vm.Rows[0].DisplayText);
        Assert.False(vm.IsCleaning);
    }

    [Fact]
    public async Task Leaving_the_tab_cancels_the_scan()
    {
        var slow = new FakeTarget("user-temp", Mb(10)) { ScanGate = new TaskCompletionSource() };
        var (vm, _, _, _) = Make(slow);

        var scan = vm.RefreshAsync();
        vm.IsActive = true;    // selecting starts its own scan, cancelling the first
        vm.IsActive = false;   // leaving cancels that one too
        slow.ScanGate.SetResult();
        await scan;

        Assert.False(slow.ScanCompleted);
        Assert.NotEqual("10 MB", vm.Rows[0].SizeText);
    }

    [Fact]
    public async Task Shader_rows_are_blocked_while_a_game_runs()
    {
        var (vm, _, _, _) = Make(new FakeTarget("nvidia-shader", Mb(500), blockedWhileGameRuns: true));
        vm.AnyGameRunning = () => true;
        vm.Initialize("nvidia-shader");

        await vm.RefreshAsync();

        Assert.True(vm.Rows[0].IsBlocked);
        Assert.False(vm.Rows[0].CanClean);
        Assert.False(vm.CleanSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Ticks_default_save_and_load()
    {
        var (vm, _, _, _) = Make(new FakeTarget("user-temp", Mb(1)), new FakeTarget("windows-temp", Mb(1)),
                                 new FakeTarget("windows-update", Mb(1)), new FakeTarget("recycle-bin", Mb(1)));
        int changes = 0;
        vm.SettingsChanged += (_, _) => changes++;

        vm.Initialize(null);
        Assert.Equal("user-temp,windows-temp,windows-update", vm.SelectionText);
        Assert.Equal(0, changes);

        vm.Rows[3].IsChecked = true;
        Assert.Equal(1, changes);
        Assert.Equal("user-temp,windows-temp,windows-update,recycle-bin", vm.SelectionText);

        vm.Initialize("");
        Assert.All(vm.Rows, r => Assert.False(r.IsChecked));
    }

    [Fact]
    public async Task The_clean_button_shows_the_selected_total()
    {
        var (vm, _, _, _) = Make(new FakeTarget("user-temp", Mb(1024)), new FakeTarget("windows-temp", Mb(512)));
        vm.Initialize("user-temp,windows-temp");

        await vm.RefreshAsync();

        Assert.Equal("Str_Maint_CleanSelected", vm.CleanButtonText);   // FakeLocalization returns keys; format has no {0}
        Assert.Equal(1536L * 1024 * 1024, vm.SelectedBytes);
    }

    [Fact]
    public void Repair_needs_a_second_press_other_tools_open_at_once()
    {
        var (vm, _, tools, _) = Make();

        vm.OpenToolCommand.Execute(SystemTool.OptimizeDrives);
        vm.OpenToolCommand.Execute(SystemTool.RepairSystemFiles);
        Assert.Equal([SystemTool.OptimizeDrives], tools.Launched);
        Assert.True(vm.IsRepairArmed);

        vm.OpenToolCommand.Execute(SystemTool.RepairSystemFiles);
        Assert.Equal([SystemTool.OptimizeDrives, SystemTool.RepairSystemFiles], tools.Launched);
        Assert.False(vm.IsRepairArmed);
    }

    [Fact]
    public async Task History_totals_follow_cleanups_and_reset_needs_two_presses()
    {
        var (vm, _, _, store) = Make(new FakeTarget("user-temp", Mb(10)));
        vm.Initialize("user-temp");
        await vm.RefreshAsync();
        await vm.CleanSelectedCommand.ExecuteAsync(null);

        Assert.Equal("100 MB", vm.StatsTodayFreed);
        Assert.Single(vm.History);

        vm.ResetHistoryCommand.Execute(null);
        Assert.True(vm.IsResetArmed);
        vm.ResetHistoryCommand.Execute(null);
        Assert.Empty(vm.History);
        Assert.Equal(0, store.Load().AllTime.Count);
    }

    [Fact]
    public void The_inert_model_keeps_the_saved_selection()
    {
        var vm = MaintenanceViewModel.Inert();
        vm.Initialize("user-temp");

        Assert.Equal("user-temp", vm.SelectionText);
    }

    [Fact]
    public async Task A_blocked_row_gets_its_normal_tip_back_when_the_game_ends()
    {
        var (vm, _, _, _) = Make(new FakeTarget("nvidia-shader", Mb(500), blockedWhileGameRuns: true));
        bool game = true;
        vm.AnyGameRunning = () => game;
        await vm.RefreshAsync();
        Assert.Equal("Str_Maint_BlockedGame", vm.Rows[0].Tip);

        game = false;
        await vm.RefreshAsync();

        Assert.False(vm.Rows[0].IsBlocked);
        Assert.Equal("Str_CleanTip_nvidia-shader", vm.Rows[0].Tip);
    }

    [Fact]
    public async Task Disposing_cancels_a_running_scan()
    {
        var slow = new FakeTarget("user-temp", Mb(10)) { ScanGate = new TaskCompletionSource() };
        var (vm, _, _, _) = Make(slow);

        var scan = vm.RefreshAsync();
        vm.Dispose();
        slow.ScanGate.SetResult();
        await scan;

        Assert.False(slow.ScanCompleted);
    }

    private sealed class GamingInfoFake(GamingInfo gaming) : ISystemInfoService
    {
        public PowerPlanInfo? GetPowerPlan() => null;
        public IReadOnlyList<PageFileInfo> GetPageFiles() => [];
        public GamingInfo GetGamingInfo() => gaming;
        public PowerMode GetPowerMode() => PowerMode.BestPerformance;
    }

    [Fact]
    public async Task Windows_gaming_rows_show_their_state()
    {
        var vm = new MaintenanceViewModel([], new InMemoryCleanupStatsStore(),
                                          new GamingInfoFake(new GamingInfo(null, true, false, true)),
                                          new FakeTools(), NullProxy.Of<IDownloadsService>(), new FakeLocalization());

        await vm.RefreshAsync();

        Assert.Equal("Str_Maint_WindowsDefault", vm.HagsText);
        Assert.Equal("Str_Maint_On", vm.RecordingText);
        Assert.Equal("Str_Maint_Off", vm.MemoryIntegrityText);
        Assert.Equal("Str_Maint_On", vm.GameModeText);
        Assert.Equal("Str_Maint_PowerBest", vm.PowerModeText);
    }

    // ── Shader caches row (spec §10.1) ──────────────────────────────────────

    private static (MaintenanceViewModel vm, FakeTarget nv, FakeTarget amd, FakeTarget dx) MakeShaders()
    {
        var nv = new FakeTarget("nvidia-shader", Mb(100), blockedWhileGameRuns: true);
        var amd = new FakeTarget("amd-shader", CleanupScan.NotFound, blockedWhileGameRuns: true);
        var dx = new FakeTarget("directx-shader", Mb(10), blockedWhileGameRuns: true);
        var (vm, _, _, _) = Make(nv, amd, dx, new FakeTarget("user-temp", Mb(1)));
        return (vm, nv, amd, dx);
    }

    [Fact]
    public async Task The_shader_caches_are_one_row_first_in_the_list()
    {
        var (vm, _, _, _) = MakeShaders();
        vm.Initialize("");
        await vm.RefreshAsync();

        var group = Assert.IsType<ShaderGroupViewModel>(vm.DisplayRows[0]);
        Assert.Equal(3, group.Caches.Count);
        Assert.Equal(2, vm.DisplayRows.Count);              // the group and user-temp
        Assert.Same(group, vm.Shaders);
    }

    [Fact]
    public async Task Ticking_the_shader_row_ticks_every_cache_that_can_be_cleaned()
    {
        var (vm, _, _, _) = MakeShaders();
        vm.Initialize("");
        await vm.RefreshAsync();

        vm.Shaders!.IsChecked = true;

        Assert.True(vm.Shaders.IsChecked);
        Assert.Equal("nvidia-shader,directx-shader", vm.SelectionText);   // AMD is not found, so not ticked
        vm.Rows.Single(r => r.Id == "directx-shader").IsChecked = false;
        Assert.Null(vm.Shaders.IsChecked);                                // some
        vm.Shaders.IsChecked = false;
        Assert.Equal("", vm.SelectionText);
    }

    [Fact]
    public async Task Only_the_caches_ticked_in_the_chooser_are_cleaned()
    {
        var (vm, nv, _, dx) = MakeShaders();
        vm.Initialize("directx-shader");
        await vm.RefreshAsync();

        await vm.CleanSelectedCommand.ExecuteAsync(null);

        Assert.Equal(0, nv.Cleaned);
        Assert.Equal(1, dx.Cleaned);
        Assert.Contains("Str_Maint_ResultFreed", vm.Shaders!.DisplayText);
    }

    [Fact]
    public async Task The_shader_row_shows_the_ticked_size_or_everything_found()
    {
        var (vm, _, _, _) = MakeShaders();
        vm.Initialize("");
        await vm.RefreshAsync();
        Assert.Equal("110 MB", vm.Shaders!.DisplayText);

        vm.Rows.Single(r => r.Id == "directx-shader").IsChecked = true;
        Assert.Equal("10 MB", vm.Shaders.DisplayText);
    }

    [Fact]
    public void The_chooser_opens_and_closes()
    {
        var (vm, _, _, _) = MakeShaders();

        vm.OpenShadersCommand.Execute(null);
        Assert.True(vm.IsShadersVisible);
        vm.CloseShadersCommand.Execute(null);
        Assert.False(vm.IsShadersVisible);
    }

    // ── After a clean: the result shows for a few seconds, then the new size ──

    [Fact]
    public async Task The_freed_result_gives_way_to_the_new_size_after_a_few_seconds()
    {
        var target = new FakeTarget("user-temp", Mb(845));
        var (vm, _, _, _) = Make(target);
        var delay = new TaskCompletionSource();
        TimeSpan asked = default;
        vm.ResultDelay = (span, ct) => { asked = span; return delay.Task.WaitAsync(ct); };
        vm.Initialize("user-temp");
        await vm.RefreshAsync();

        await vm.CleanSelectedCommand.ExecuteAsync(null);
        Assert.Contains("Str_Maint_ResultFreed", vm.Rows[0].DisplayText);
        Assert.Equal(TimeSpan.FromSeconds(4), asked);

        delay.SetResult();
        await Task.Yield();

        Assert.Equal("845 MB", vm.Rows[0].DisplayText);      // the rescanned size, no result any more
        Assert.Null(vm.Rows[0].LastResult);
    }
}
