using System.IO;
using System.Runtime.CompilerServices;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// Runs before any test touches Logger: everything the tests log goes to a
// temp folder instead of the user's %APPDATA%\StandbyAndTimer\log.txt.
internal static class TestLogSetup
{
    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "StandbyAndTimer.Tests-log");

    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable(Logger.LogDirVariable, Dir);
}

public class TestLogSetupTests
{
    [Fact]
    public void Tests_log_to_a_temp_folder_not_the_users_log()
    {
        Logger.Info("test log redirect check");

        Assert.StartsWith(TestLogSetup.Dir, Logger.LogFilePath, StringComparison.OrdinalIgnoreCase);
        // Other tests log in parallel: read while they may be appending.
        using var stream = new FileStream(Logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        Assert.Contains("test log redirect check", reader.ReadToEnd());
    }
}
