using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Views;

// Stand-alone window we spin up when an unhandled exception fires. It owns
// its own theme (via the merged Theme.xaml) so it can render even if the
// main window has been disposed by the crash. The pre-built GitHub issue
// URL means the user can submit a useful report in one click instead of
// remembering to paste a stack trace.
public partial class CrashReporterWindow : Window
{
    private const string GitHubIssuesNewUrl =
        "https://github.com/Layellie/StandbyAndTimer/issues/new";

    private readonly string _details;

    public CrashReporterWindow(string details)
    {
        _details = details;
        InitializeComponent();
        DetailsBox.Text = details;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_details);
            StatusText.Text = Application.Current.Resources["Str_Crash_Copied"] as string ?? "Copied";
        }
        catch
        {
            // Clipboard.SetText can throw if another process holds the
            // clipboard open. Best-effort — silent failure is better than
            // a second crash inside the crash dialog.
        }
    }

    private void OnOpenIssueClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string title = Uri.EscapeDataString("Crash report: <one-line summary>");
            // GitHub caps URLs around 8 KB; trim the body so we don't get a
            // 414 URI-Too-Long back. The truncation marker also signals to
            // the maintainer that more detail is in the user's clipboard.
            string excerpt = _details.Length > 6000
                ? _details[..6000] + "\n\n…(truncated — full details on the user's clipboard via Copy details)…"
                : _details;
            string body  = Uri.EscapeDataString(BuildIssueBody(excerpt));
            var psi = new ProcessStartInfo
            {
                FileName        = $"{GitHubIssuesNewUrl}?title={title}&body={body}",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Warn($"CrashReporter.OpenIssue: {ex.Message}");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static string BuildIssueBody(string details)
    {
        // InvariantCulture on every interpolated AppendLine so locale-specific
        // formatting (e.g. tr-TR period-vs-comma in Environment.Version) doesn't
        // leak into the GitHub issue body. CA1305 enforces this project-wide.
        var sb  = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("**What happened:**");
        sb.AppendLine("<!-- briefly describe what you were doing when this fired -->");
        sb.AppendLine();
        sb.AppendLine("**Steps to reproduce:**");
        sb.AppendLine("1.");
        sb.AppendLine("2.");
        sb.AppendLine();
        sb.AppendLine("**Environment:**");
        sb.AppendLine(inv, $"- App version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine(inv, $"- Windows: {Environment.OSVersion}");
        sb.AppendLine(inv, $"- .NET: {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("**Details:**");
        sb.AppendLine("```");
        sb.AppendLine(details);
        sb.AppendLine("```");
        return sb.ToString();
    }
}
