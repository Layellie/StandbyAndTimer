namespace StandbyAndTimer.Core.Models;

public enum NoteKind { Text, Heading, Bullet }

/// <summary>One line of the "what's new" view: plain text, no Markdown.</summary>
public sealed record NoteLine(string Text, NoteKind Kind);
