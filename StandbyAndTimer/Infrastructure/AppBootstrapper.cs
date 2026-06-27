using Microsoft.Extensions.DependencyInjection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services;
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
        services.AddSingleton<IMemoryMonitorService,       MemoryMonitorService>();
        services.AddSingleton<IAutoStartService,           AutoStartService>();
        services.AddSingleton<ILocalizationService,        LocalizationService>();
        services.AddSingleton<IThemeService,               ThemeService>();
        services.AddSingleton<IUpdateService,              UpdateService>();
        services.AddSingleton<IGameDetectionService,       GameDetectionService>();

        // ── Presentation ─────────────────────────────────────────────────────
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
