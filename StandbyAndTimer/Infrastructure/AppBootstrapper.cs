using Microsoft.Extensions.DependencyInjection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services;
using StandbyAndTimer.Services.Logging;
using StandbyAndTimer.Services.Maintenance;
using StandbyAndTimer.Services.Tray;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Infrastructure;

internal static class AppBootstrapper
{
    internal static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // ── Services (all singletons — one instance for the app lifetime) ──
        services.AddSingleton<ISettingsService,            SettingsService>();
        services.AddSingleton<ITimerResolutionService,     TimerResolutionService>();
        services.AddSingleton<IStandbyPurgeService,        StandbyPurgeService>();
        services.AddSingleton<IProcessOptimizationService, ProcessOptimizationService>();
        services.AddSingleton<IIdleMonitor,                IdleMonitor>();
        services.AddSingleton<IHotkeyService,              HotkeyService>();
        services.AddSingleton<IMemoryMonitorService,       MemoryMonitorService>();
        services.AddSingleton<IAutoStartService,           AutoStartService>();
        services.AddSingleton<ILocalizationService,        LocalizationService>();
        services.AddSingleton<IThemeService,               ThemeService>();
        services.AddSingleton<IUpdateService,              UpdateService>();
        services.AddSingleton<IGameDetectionService,       GameDetectionService>();
        services.AddSingleton<ITrayIconService,            TrayIconService>();
        services.AddSingleton<ILogTailReader,              LogTailReader>();
        services.AddSingleton<IGlobalTimerSetting,         GlobalTimerSetting>();
        services.AddSingleton<IPurgeStatsStore,            PurgeStatsStore>();
        services.AddSingleton<IDisplayInfoService,         DisplayInfoService>();

        // Maintenance tab. Row order = registration order.
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.NvidiaShader());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.AmdShader());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.DirectXShader());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.SteamShader());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.DriverLeftovers());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.UserTemp());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.WindowsTemp());
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.WindowsUpdate());
        services.AddSingleton<ICleanupTarget, DeliveryOptimizationTarget>();
        services.AddSingleton<ICleanupTarget>(_ => CleanupTargets.CrashDumps());
        services.AddSingleton<ICleanupTarget, RecycleBinTarget>();
        services.AddSingleton<ICleanupStatsStore,  CleanupStatsStore>();
        services.AddSingleton<ISystemInfoService,  SystemInfoService>();
        services.AddSingleton<ISystemToolLauncher, SystemToolLauncher>();
        services.AddSingleton<IDownloadsService,   DownloadsService>();
        services.AddSingleton<MaintenanceViewModel>();

        // ── Presentation ─────────────────────────────────────────────────────
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SystemViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
