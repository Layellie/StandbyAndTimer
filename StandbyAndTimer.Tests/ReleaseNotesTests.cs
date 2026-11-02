using StandbyAndTimer.Core.Models;
using StandbyAndTimer.Services;

namespace StandbyAndTimer.Tests;

// The "what's new" view of an update: the GitHub release body holds the notes
// in English and (from 3.0.6 on) Turkish, plus a verification block for the
// updater. The app shows the notes in the UI language, without the checksum
// lines, as plain text (no Markdown).
public class ReleaseNotesTests
{
    private const string Bilingual = """
        ## What's New in v3.0.6

        - **Battery:** the timer switches off on battery.
        - Fixes `ScanAsync` crash.

        ### Fixes
        - One corrupt manifest no longer empties the list.

        ## Yenilikler (v3.0.6)

        - **Pil:** pilde timer kapanır.

        ### Düzeltmeler
        - Bozuk bir manifest artık listeyi boşaltmaz.

        ### Verification
        - **Installer:** `StandbyAndTimer_Setup_3.0.6.exe`
        - **SHA-256:** `924f98cc14f26b3848723c0f02967127ddca7df71c535bc159f981e5ef90b2e7`
        """;

    private const string EnglishOnly = """
        ## What's New in v3.0.4

        - **Dismiss dialog:** click outside to close.

        ### Verification
        - **Installer:** `StandbyAndTimer_Setup_3.0.4.exe`
        - **SHA-256:** `93e268452cd543c71298f7c9d7afe85c8b14b195d97b18ed198ce05ffbde6a65`
        """;

    [Fact]
    public void English_gets_the_english_section_without_the_verification_block()
    {
        string notes = ReleaseNotes.Extract(Bilingual, Language.English);

        Assert.Contains("the timer switches off on battery", notes);
        Assert.Contains("One corrupt manifest", notes);
        Assert.DoesNotContain("pilde timer", notes);
        Assert.DoesNotContain("SHA-256", notes);
        Assert.DoesNotContain("Installer", notes);
    }

    [Fact]
    public void Turkish_gets_the_turkish_section()
    {
        string notes = ReleaseNotes.Extract(Bilingual, Language.Turkish);

        Assert.Contains("pilde timer kapanır", notes);
        Assert.Contains("Bozuk bir manifest", notes);
        Assert.DoesNotContain("switches off", notes);
        Assert.DoesNotContain("SHA-256", notes);
    }

    [Fact]
    public void Turkish_falls_back_to_english_when_the_release_has_no_turkish_section()
    {
        string notes = ReleaseNotes.Extract(EnglishOnly, Language.Turkish);

        Assert.Contains("click outside to close", notes);
        Assert.DoesNotContain("SHA-256", notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_body_has_no_notes(string body)
    {
        Assert.Equal("", ReleaseNotes.Extract(body, Language.English));
        Assert.Empty(ReleaseNotes.ToLines(""));
    }

    [Fact]
    public void A_body_that_is_only_the_checksum_has_no_notes()
    {
        string body = "SHA256: 924f98cc14f26b3848723c0f02967127ddca7df71c535bc159f981e5ef90b2e7";

        Assert.Equal("", ReleaseNotes.Extract(body, Language.English));
    }

    [Fact]
    public void Lines_become_plain_text_headings_and_bullets()
    {
        string notes = ReleaseNotes.Extract(Bilingual, Language.English);

        var lines = ReleaseNotes.ToLines(notes);

        Assert.Equal(
        [
            new NoteLine("Battery: the timer switches off on battery.", NoteKind.Bullet),
            new NoteLine("Fixes ScanAsync crash.", NoteKind.Bullet),
            new NoteLine("Fixes", NoteKind.Heading),
            new NoteLine("One corrupt manifest no longer empties the list.", NoteKind.Bullet),
        ], lines);
    }

    [Fact]
    public void Links_keep_their_text_and_plain_paragraphs_stay_plain()
    {
        var lines = ReleaseNotes.ToLines("See the [release page](https://example.com/x) for more.\n\n* Star bullet");

        Assert.Equal(
        [
            new NoteLine("See the release page for more.", NoteKind.Text),
            new NoteLine("Star bullet", NoteKind.Bullet),
        ], lines);
    }
}
