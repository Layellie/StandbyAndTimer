using System.Text.RegularExpressions;
using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Services;

/// <summary>
/// The "what's new" view of an update. The GitHub release body holds the notes
/// as an English "## What's New" section and, from 3.0.6 on, a Turkish
/// "## Yenilikler" section, followed by a "### Verification" block (installer
/// name and SHA-256, for the updater). The app shows the section of the UI
/// language (English when a release has no Turkish one), without the checksum.
/// </summary>
internal static partial class ReleaseNotes
{
    [GeneratedRegex(@"SHA[-\s]?256", RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Mention();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    public static string Extract(string? body, Language language)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        // Everything from the verification heading on is for the updater.
        var kept = new List<string>();
        foreach (string raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            string text = raw.Trim().TrimStart('#').Trim();
            if (raw.TrimStart().StartsWith('#')
                && (text.StartsWith("Verification", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("Doğrulama", StringComparison.OrdinalIgnoreCase)))
                break;
            if (!Sha256Mention().IsMatch(raw)) kept.Add(raw);
        }

        var sections = new List<(string Heading, List<string> Lines)>();
        var preamble = new List<string>();
        foreach (string line in kept)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
                sections.Add((line[3..].Trim(), []));
            else if (sections.Count == 0) preamble.Add(line);
            else sections[^1].Lines.Add(line);
        }

        if (sections.Count == 0) return Join(preamble);

        bool IsTurkish((string Heading, List<string> Lines) s) =>
            s.Heading.Contains("Yenilik", StringComparison.OrdinalIgnoreCase)
            || s.Heading.Contains("Türkçe", StringComparison.OrdinalIgnoreCase);

        var turkish = sections.Where(IsTurkish).Select(s => s.Lines).FirstOrDefault();
        var english = sections.Where(s => !IsTurkish(s)).Select(s => s.Lines).FirstOrDefault();
        var picked  = language == Language.Turkish ? turkish ?? english : english ?? turkish;
        return Join(picked ?? []);
    }

    private static string Join(List<string> lines) => string.Join("\n", lines).Trim();

    /// <summary>Plain-text lines: "###" headings, "-" / "*" bullets, everything else a paragraph.</summary>
    public static IReadOnlyList<NoteLine> ToLines(string notes)
    {
        var result = new List<NoteLine>();
        foreach (string raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
                result.Add(new NoteLine(Plain(line.TrimStart('#')), NoteKind.Heading));
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                result.Add(new NoteLine(Plain(line[2..]), NoteKind.Bullet));
            else
                result.Add(new NoteLine(Plain(line), NoteKind.Text));
        }
        return result;
    }

    private static string Plain(string markdown) =>
        MarkdownLink().Replace(markdown, "$1").Replace("**", "").Replace("`", "").Trim();
}
