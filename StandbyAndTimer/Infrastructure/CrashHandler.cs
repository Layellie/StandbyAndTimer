using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Services;
using StandbyAndTimer.Views;

namespace StandbyAndTimer.Infrastructure;

// Owns the global crash-handling wiring. App.OnStartup hands it the
// IServiceProvider once available, so the emergency-restore path can
// release the timer resolution lock even when the exception came from
// a code path that didn't have a reference to the timer service.
//
// Subscribes to:
//   - AppDomain.UnhandledException     — non-UI thread fatal errors.
//   - Dispatcher.UnhandledException    — UI thread errors. Shows the
//     CrashReporterWindow + marks handled so we don't get WPF's default
//     fatal popup.
//   - TaskScheduler.UnobservedTask…    — orphaned task faults. Logged
//     and acknowledged so they don't crash the process.
//   - AppDomain.ProcessExit            — last-ditch best-effort restore.
internal sealed class CrashHandler
{
    private IServiceProvider? _services;

    internal void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit        += OnProcessExit;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        // Dispatcher handler is attached separately in App.OnStartup against
        // the Application instance — it isn't a static event.
    }

    internal void AttachToDispatcher(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    internal void SetServices(IServiceProvider services) => _services = services;

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Logger.Error("UnhandledException", (Exception)e.ExceptionObject);
        TryEmergencyRestore();
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e) =>
        e.Handled = HandleUiException(e.Exception, details => new CrashReporterWindow(details).ShowDialog());

    /// <summary>
    /// A UI-thread exception: show the crash reporter and carry on (true), so
    /// the user gets Copy / Open-Issue buttons instead of WPF's fatal popup.
    /// The app keeps running then, and so does the timer: releasing it here
    /// left the timer off while the window still showed it as on. Only when
    /// the reporter itself cannot be shown does the default fatal path follow
    /// (false), and the timer is released first.
    /// </summary>
    internal bool HandleUiException(Exception exception, Action<string> showReporter)
    {
        Logger.Error("DispatcherUnhandledException", exception);
        try
        {
            showReporter(FormatCrash(exception));
            return true;
        }
        catch (Exception ex2)
        {
            Logger.Error("CrashReporter failed to show", ex2);
            TryEmergencyRestore();
            return false;
        }
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Logger.Info("ProcessExit fired");
        TryEmergencyRestore();
    }

    private void TryEmergencyRestore()
    {
        try
        {
            // Force release the timer even if Deactivate path didn't run.
            // Safe to call multiple times — TimerResolutionService guards IsActive.
            _services?.GetService<ITimerResolutionService>()?.Dispose();
        }
        catch { /* best-effort */ }
    }

    internal static string FormatCrash(Exception ex)
    {
        var sb  = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"Type: {ex.GetType().FullName}");
        sb.AppendLine(inv, $"Message: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("Stack:");
        sb.AppendLine(ex.StackTrace);
        if (ex.InnerException is { } inner)
        {
            sb.AppendLine();
            sb.AppendLine(inv, $"InnerException: {inner.GetType().FullName}: {inner.Message}");
            sb.AppendLine(inner.StackTrace);
        }
        return sb.ToString();
    }
}
