using System.Reflection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

// No-op stand-in for any service interface: void -> nothing, value types ->
// default, Task -> completed, Task<T> -> completed default.
public class NullProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var rt = targetMethod!.ReturnType;
        if (rt == typeof(void)) return null;
        if (rt == typeof(Task)) return Task.CompletedTask;
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var arg = rt.GetGenericArguments()[0];
            var def = arg.IsValueType ? Activator.CreateInstance(arg) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(arg).Invoke(null, [def]);
        }
        return rt.IsValueType ? Activator.CreateInstance(rt) : null;
    }

    public static T Of<T>() where T : class => Create<T, NullProxy>();
}

// Returns the key itself, so tests compare against resource keys.
public sealed class FakeLocalization : ILocalizationService
{
    public Language CurrentLanguage { get; set; } = Language.English;
    public void SetLanguage(Language lang) => CurrentLanguage = lang;
    public string GetString(string key) => key;
    public event EventHandler? LanguageChanged { add { } remove { } }
}

// Counts Activate / Deactivate; never calls NtSetTimerResolution.
public sealed class FakeTimer : ITimerResolutionService
{
    public int Activations { get; private set; }
    public int Deactivations { get; private set; }
    public bool IsActive { get; private set; }
    public event EventHandler<double>? ResolutionMeasured { add { } remove { } }
    public double Activate() { Activations++; IsActive = true; return 0.5; }
    public Task<double> ActivateAsync(CancellationToken ct = default) => Task.FromResult(Activate());
    public void Deactivate() { Deactivations++; IsActive = false; }
    public void Dispose() { }
}

// Returns a fixed snapshot from Load and records every Save in memory.
public sealed class FakeSettingsStore(AppSettings? toLoad = null) : ISettingsService
{
    public List<AppSettings> Saved { get; } = [];
    public AppSettings Load() => toLoad ?? new AppSettings { FirstRunCompleted = true };
    public void Save(AppSettings settings) => Saved.Add(settings);
}

// Builds the view model pair the way App does, with fakes everywhere.
public sealed class Harness
{
    public FakeSettingsStore Store { get; }
    public FakeTimer Timer { get; } = new();
    public FakeLocalization Loc { get; } = new();
    public SettingsViewModel Settings { get; }
    public MainViewModel Main { get; }

    public Harness(AppSettings? toLoad = null, IStandbyPurgeService? purge = null,
                   IMemoryMonitorService? memory = null, IGameDetectionService? detection = null,
                   IPurgeStatsStore? purgeStats = null)
    {
        Store    = new FakeSettingsStore(toLoad);
        Settings = new SettingsViewModel(NullProxy.Of<IAutoStartService>(), Loc, Store,
                                         NullProxy.Of<IUpdateService>(), NullProxy.Of<IThemeService>(),
                                         NullProxy.Of<ILogTailReader>());
        Main     = new MainViewModel(Timer, purge ?? NullProxy.Of<IStandbyPurgeService>(),
                                     memory ?? NullProxy.Of<IMemoryMonitorService>(), Store, Loc,
                                     detection ?? NullProxy.Of<IGameDetectionService>(), Settings,
                                     purgeStats: purgeStats);
    }

    public AppSettings LastSaved => Store.Saved[^1];
}
