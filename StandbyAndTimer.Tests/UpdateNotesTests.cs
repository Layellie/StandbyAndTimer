using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;
using StandbyAndTimer.ViewModels;

namespace StandbyAndTimer.Tests;

// Pressing "Check for updates" and finding a newer release opens the notes
// of that release (in the UI language) with the choice to install or not.
public class UpdateNotesTests
{
    private const string Body = """
        ## What's New in v9.9.9

        - **Big thing:** it works.

        ## Yenilikler (v9.9.9)

        - **Büyük şey:** çalışıyor.

        ### Verification
        - **SHA-256:** `924f98cc14f26b3848723c0f02967127ddca7df71c535bc159f981e5ef90b2e7`
        """;

    private sealed class FakeUpdates(UpdateCheckResult result) : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default) => Task.FromResult(result);
        public Task DownloadAsync(string assetUrl, string targetPath, string expectedSha256,
                                  IProgress<double>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("no download in a notes test");
    }

    private static SettingsViewModel Make(UpdateCheckResult result, Language language = Language.English)
    {
        var settings = new SettingsViewModel(NullProxy.Of<IAutoStartService>(), new FakeLocalization(),
            new FakeSettingsStore(), new FakeUpdates(result), NullProxy.Of<IThemeService>(), NullProxy.Of<ILogTailReader>());
        settings.Initialize(false, language, true, Theme.Dark, true, true, true, "Ctrl+Alt+P", "Ctrl+Alt+T", "Ctrl+Alt+O");
        return settings;
    }

    private static UpdateCheckResult Newer(string body = Body) =>
        new(true, "v9.9.9", "https://example.com/release", "https://example.com/setup.exe", new string('a', 64), body);

    [Fact]
    public async Task A_newer_release_opens_its_notes_in_english()
    {
        var s = Make(Newer());

        await s.CheckUpdateCommand.ExecuteAsync(null);

        Assert.True(s.IsUpdateNotesVisible);
        Assert.Equal("Str_UpdateNotes_Title", s.UpdateNotesTitle);   // FakeLocalization returns keys
        Assert.Equal([new NoteLine("Big thing: it works.", NoteKind.Bullet)], s.UpdateNotesLines);
        Assert.True(s.UpdateAvailable);
    }

    [Fact]
    public async Task The_notes_follow_the_ui_language()
    {
        var s = Make(Newer(), Language.Turkish);

        await s.CheckUpdateCommand.ExecuteAsync(null);

        Assert.Equal([new NoteLine("Büyük şey: çalışıyor.", NoteKind.Bullet)], s.UpdateNotesLines);
    }

    [Fact]
    public async Task A_release_without_notes_opens_nothing()
    {
        var s = Make(Newer(body: "SHA256: " + new string('a', 64)));

        await s.CheckUpdateCommand.ExecuteAsync(null);

        Assert.False(s.IsUpdateNotesVisible);
        Assert.True(s.UpdateAvailable);   // the Download button is still there
    }

    [Fact]
    public async Task Up_to_date_opens_nothing()
    {
        var s = Make(new UpdateCheckResult(false, "v3.0.5", "", "", "", Body));

        await s.CheckUpdateCommand.ExecuteAsync(null);

        Assert.False(s.IsUpdateNotesVisible);
    }

    [Fact]
    public async Task Later_closes_the_notes_and_keeps_the_update_available()
    {
        var s = Make(Newer());
        await s.CheckUpdateCommand.ExecuteAsync(null);

        s.CloseUpdateNotesCommand.Execute(null);

        Assert.False(s.IsUpdateNotesVisible);
        Assert.True(s.UpdateAvailable);
    }
}
