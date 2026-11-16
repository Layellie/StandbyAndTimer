using Microsoft.Extensions.DependencyInjection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Infrastructure;

namespace StandbyAndTimer.Tests;

// After the crash dialog the app keeps running. Releasing the timer there
// left it off while the window still said it was on.
public class CrashHandlerTests
{
    private sealed class CountingTimer : ITimerResolutionService
    {
        public int Disposals;
        public bool IsActive => true;
        public event EventHandler<double>? ResolutionMeasured { add { } remove { } }
        public double QueryCurrentMs() => 0.5;
        public double Activate() => 0.5;
        public Task<double> ActivateAsync(CancellationToken ct = default) => Task.FromResult(0.5);
        public void Deactivate() { }
        public void Dispose() => Disposals++;
    }

    private static (CrashHandler handler, CountingTimer timer) Build()
    {
        var timer = new CountingTimer();
        var handler = new CrashHandler();
        handler.SetServices(new ServiceCollection().AddSingleton<ITimerResolutionService>(timer).BuildServiceProvider());
        return (handler, timer);
    }

    [Fact]
    public void A_reported_UI_error_keeps_the_timer()
    {
        var (handler, timer) = Build();
        string? shown = null;

        bool handled = handler.HandleUiException(new InvalidOperationException("boom"), text => { shown = text; });

        Assert.True(handled);
        Assert.Contains("boom", shown);
        Assert.Equal(0, timer.Disposals);
    }

    [Fact]
    public void A_UI_error_the_dialog_cannot_show_releases_the_timer_before_the_process_ends()
    {
        var (handler, timer) = Build();

        bool handled = handler.HandleUiException(new InvalidOperationException("boom"),
                                                 _ => throw new InvalidOperationException("no window"));

        Assert.False(handled);
        Assert.Equal(1, timer.Disposals);
    }
}
