namespace SchedularrTV.Core.Configuration;

/// <summary>Where a block of clips sits relative to the episodes filling a slot.</summary>
public enum BumperPosition
{
    /// <summary>Before the first episode.</summary>
    Intro,

    /// <summary>After the last episode, before the break that follows.</summary>
    Outro
}

/// <summary>How a block chooses which of its clips to play.</summary>
public enum BumperSelection
{
    /// <summary>Any clip that fits, varying from airing to airing.</summary>
    Random,

    /// <summary>The longest clip that still fits, falling back to shorter ones.</summary>
    LongestThatFits,

    /// <summary>The shortest clip, to leave as much of the slot as possible for programming.</summary>
    ShortestThatFits
}

/// <summary>One video a block may play.</summary>
public sealed class BumperClip
{
    public required string Path { get; set; }

    /// <summary>Display name, so the editor can list clips without re-reading the library.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Episodes this clip is reserved for, written as <c>S01E03</c>. When set the clip plays only
    /// with those episodes, and they get it in preference to anything the behaviour would pick.
    /// Empty means the clip is part of the general pool.
    /// </summary>
    public List<string> Episodes { get; set; } = [];
}

/// <summary>
/// A named group of clips played as one unit around a title's episodes, e.g. an opening theme or a
/// couch gag. Blocks play in the order they are listed.
/// </summary>
public sealed class BumperBlock
{
    public string Name { get; set; } = string.Empty;

    public BumperPosition Position { get; set; } = BumperPosition.Intro;

    public BumperSelection Selection { get; set; } = BumperSelection.Random;

    public List<int> Seasons { get; set; } = [];

    /// <summary>
    /// Skips the whole block when the episode's file name contains any of these, for episodes that
    /// already have the sequence baked in. Matched case-insensitively, e.g. <c>(with Intro)</c>.
    /// </summary>
    public List<string> SkipWhenFileContains { get; set; } = [];

    public List<BumperClip> Clips { get; set; } = [];
}
