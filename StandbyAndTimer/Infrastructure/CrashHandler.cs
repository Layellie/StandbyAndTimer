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

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("DispatcherUnhandledException", e.Exception);
        TryEmergencyRestore();

        // Show the crash reporter dialog and mark the exception handled — the
        // alternative (let WPF show its default fatal error popup and exit)
        // gives the user nothing to act on. Our window has Copy + Open-Issue
        // buttons so the report actually reaches us. If the reporter itself
        // fails to display, fall through to the default fatal path.
        try
        {
            var reporter = new CrashReporterWindow(FormatCrash(e.Exception));
            reporter.ShowDialog();
            e.Handled = true;
        }
        catch (Exception ex2)
        {
            Logger.Error("CrashReporter failed to show", ex2);
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
