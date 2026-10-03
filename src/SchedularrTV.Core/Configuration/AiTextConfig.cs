namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Has a language model write something (a promo's voice-over, a caption) instead of filling in the usual wording.
/// The usual wording stays as the fallback: it's used whenever no model is set up, the model's writing doesn't pass
/// the checks (every show named, no made-up times), or <see cref="Chance"/> says to.
/// </summary>
public sealed class AiTextConfig
{
    /// <summary>The writer plugin's id, e.g. <c>local-ai</c>; empty uses the first one that's ready.</summary>
    public string? Generator { get; set; }

    /// <summary>What to write, e.g. "a five-word tagline for the episode". Optional for voice-overs, which know their job.</summary>
    public string? Prompt { get; set; }

    /// <summary>How it should sound, e.g. "an excitable late-90s cartoon channel announcer".</summary>
    public string? Style { get; set; }

    /// <summary>Hands the model the episode's description, so it can say something about the plot.</summary>
    public bool UseDescriptions { get; set; } = true;

    /// <summary>The most words it may write; 0 works it out (from the promo's length for a voice-over).</summary>
    public int MaxWords { get; set; }

    /// <summary>0 to 1: how often the model writes it; the rest of the time the usual wording is used.</summary>
    public double Chance { get; set; } = 1;
}
