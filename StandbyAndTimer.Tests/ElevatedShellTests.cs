using System.IO;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

// The app runs as administrator. What it opens through the shell must not
// become an elevated browser, and a string that came from the network must
// never be handed to the shell as something to run.
public class ElevatedShellTests
{
    private static readonly string Explorer =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    [Fact]
    public void The_release_page_goes_to_the_non_elevated_shell()
    {
        var psi = SettingsViewModel.ReleasePageStartInfo("https://github.com/Layellie/StandbyAndTimer/releases/tag/v3.0.8");

        Assert.NotNull(psi);
        Assert.Equal(Explorer, psi.FileName, ignoreCase: true);
        Assert.False(psi.UseShellExecute);
        Assert.Equal("\"https://github.com/Layellie/StandbyAndTimer/releases/tag/v3.0.8\"", psi.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("cmd.exe /c calc")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("http://github.com/Layellie/StandbyAndTimer/releases")]          // not https
    [InlineData("https://github.com/someone-else/StandbyAndTimer/releases")]     // another repository
    [InlineData("https://github.com.example.org/Layellie/StandbyAndTimer/x")]    // another host
    [InlineData("https://example.org/Layellie/StandbyAndTimer/releases")]
    [InlineData("https://github.com/Layellie/StandbyAndTimer/releases\" /select,C:/x")]
    public void Anything_that_is_not_one_of_the_projects_own_pages_is_not_opened(string url)
    {
        Assert.Null(SettingsViewModel.ReleasePageStartInfo(url));
    }

    [Fact]
    public void Showing_a_file_in_Explorer_starts_Windows_own_explorer_by_its_full_path()
    {
        var psi = DownloadsViewModel.ExplorerSelectStartInfo(@"C:\Games\Some Game\game.exe");

        Assert.Equal(Explorer, psi.FileName, ignoreCase: true);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(@"/select,""C:\Games\Some Game\game.exe""", psi.Arguments);
    }
}
