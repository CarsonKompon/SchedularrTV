using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Plugins.Writing;

namespace SchedularrTV.Service.Writing;

/// <summary>Whether the writing is read aloud (a voice-over) or shown on screen (a caption).</summary>
public enum AiTextUse
{
    Spoken,
    Shown
}

/// <summary>Something the model may say, e.g. <c>Next: SpongeBob SquarePants</c>.</summary>
public sealed record AiTextFact(string Label, string Value);

/// <summary>
/// One thing for a language model to write, with everything it may use and the usual wording to fall back on.
/// Two jobs with the same <see cref="AiTextRules.Identity"/> get the same writing, so give each airing that should
/// sound different its own <see cref="Seed"/>.
/// </summary>
public sealed record AiTextJob
{
    public required AiTextConfig Config { get; init; }

    public AiTextUse Use { get; init; } = AiTextUse.Spoken;

    public string Channel { get; init; } = string.Empty;

    /// <summary>What it's writing, finishing "Write …", e.g. <c>the announcer's voice-over for an Up Next promo</c>.</summary>
    public required string Purpose { get; init; }

    /// <summary>The usual wording: shown to the model for reference, and used whenever its writing isn't.</summary>
    public required string Fallback { get; init; }

    public IReadOnlyList<AiTextFact> Facts { get; init; } = [];

    /// <summary>Extra direction for this kind of job, one sentence each.</summary>
    public IReadOnlyList<string> Guidance { get; init; } = [];

    /// <summary>Names the writing must include (the shows a promo is for), or it's thrown away.</summary>
    public IReadOnlyList<string> MustSay { get; init; } = [];

    /// <summary>The length when <see cref="AiTextConfig.MaxWords"/> isn't set.</summary>
    public int MaxWords { get; init; } = 30;

    /// <summary>Varies the writing: the same seed writes the same thing.</summary>
    public string Seed { get; init; } = string.Empty;

    /// <summary>Who asked (a plugin id), so it can tell its own finished writing apart in <see cref="AiTextWriter.Written"/>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>When it's needed by (when it airs); sooner ones are written first.</summary>
    public DateTimeOffset Due { get; init; }

    /// <summary>
    /// For writing that isn't a plain line of words (a promo's plan of lines and clips, say): how it's asked for, cleaned
    /// up and checked. Null uses the usual rules for <see cref="Use"/>.
    /// </summary>
    public AiTextFormat? Format { get; init; }
}

/// <summary>
/// How a job with its own shape of reply is put to the model and checked, in place of <see cref="AiTextRules"/>'s
/// usual request, clean-up and checks. What <see cref="Accept"/> returns is what's kept, and what callers get back.
/// </summary>
public abstract class AiTextFormat
{
    /// <summary>Part of the job's identity: change it whenever the way it's asked changes, so old writing isn't reused.</summary>
    public abstract string Id { get; }

    /// <summary>The request for one try; a second try should change the seed and calm the sampling.</summary>
    public abstract TextGenerationRequest Request(AiTextJob job, int attempt);

    /// <summary>The reply as it's to be kept, and why it can't be used (null when it can).</summary>
    public abstract (string Text, string? Problem) Accept(string reply, AiTextJob job);
}

/// <summary>What to use: the model's writing when <see cref="Written"/>, otherwise the usual wording and why.</summary>
public sealed record AiTextResult(string Text, bool Written, string? Problem = null)
{
    /// <summary>
    /// The model's reply as it came, before clean-up and checks (the last try's, when it was set aside), for
    /// troubleshooting; null when there wasn't one (no model, an error) or it wasn't kept.
    /// </summary>
    public string? Raw { get; init; }
}

/// <summary>How a job is put to the model, and how its reply is cleaned up and checked.</summary>
public static partial class AiTextRules
{
    private static readonly string[] DayWords =
        ["today", "tonight", "tomorrow", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "weekend"];

    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal) { "the", "and", "a", "an", "of", "in", "on", "to", "with" };

    /// <summary>Words an announcer says in this long, at an easy pace.</summary>
    public static int SpokenWords(TimeSpan time) => (int)Math.Clamp(Math.Floor(time.TotalSeconds * 2.6), 5, 90);

    /// <summary>The job's word limit: the configured one, else the job's own.</summary>
    public static int Limit(AiTextJob job) => job.Config.MaxWords > 0 ? Math.Clamp(job.Config.MaxWords, 1, 200) : Math.Max(1, job.MaxWords);

    /// <summary>Whether this job is one of the share the model writes (<see cref="AiTextConfig.Chance"/>), fixed by its seed.</summary>
    public static bool Chosen(AiTextJob job) =>
        job.Config.Chance >= 1 || (job.Config.Chance > 0 && Fraction($"{job.Seed}|chance") < job.Config.Chance);

    /// <summary>Everything that changes what's asked of the model; the same identity gets the same writing.</summary>
    public static string Identity(AiTextJob job)
    {
        var text = new StringBuilder();
        void Add(string? value) => text.Append(value ?? string.Empty).Append('\u001f');
        Add(job.Use.ToString());
        Add(job.Channel);
        Add(job.Purpose);
        Add(job.Fallback);
        Add(job.Config.Prompt?.Trim());
        Add(job.Config.Style?.Trim());
        Add(Limit(job).ToString(CultureInfo.InvariantCulture));
        Add(job.Seed);
        foreach (AiTextFact fact in job.Facts)
        {
            Add(fact.Label);
            Add(fact.Value);
        }

        Add("guidance");
        foreach (string line in job.Guidance)
        {
            Add(line);
        }

        Add("must");
        foreach (string name in job.MustSay)
        {
            Add(name);
        }

        // Only jobs with a format of their own say so, so every other job keeps the writing it already has.
        if (job.Format is { } format)
        {
            Add("format");
            Add(format.Id);
        }

        return text.ToString();
    }

    /// <summary>The request for one try; a second try changes the seed and calms the sampling.</summary>
    public static TextGenerationRequest Request(AiTextJob job, int attempt = 0)
    {
        int limit = Limit(job);
        bool spoken = job.Use == AiTextUse.Spoken;
        string channel = string.IsNullOrWhiteSpace(job.Channel) ? "a TV channel" : $"the TV channel \"{job.Channel.Trim()}\"";

        var instructions = new StringBuilder();
        instructions.AppendLine(spoken
            ? $"You write what the announcer says on {channel}."
            : $"You write short on-screen text for {channel}.");
        instructions.AppendLine("Rules:");
        instructions.AppendLine(spoken
            ? "- Reply with only the words to be read aloud. No quotes, labels, notes, lists, hashtags, emoji or stage directions."
            : "- Reply with only the words to show on screen. No quotes, labels, notes, lists, hashtags or emoji.");
        instructions.AppendLine("- Use only the facts you're given. Never make up shows, episodes, days, times or plot details.");
        instructions.AppendLine("- Write show names exactly as they're given.");
        instructions.AppendLine($"- Use at most {limit} words.");
        instructions.AppendLine(spoken
            ? "- Write it to be spoken: plain sentences, no abbreviations or symbols."
            : "- Keep it to one line, two at most.");
        if (job.Config.Style is { Length: > 0 } style && style.Trim().Length > 0)
        {
            instructions.AppendLine($"Style: {style.Trim()}");
        }

        var prompt = new StringBuilder();
        prompt.AppendLine($"Write {job.Purpose.Trim().TrimEnd('.')}.");
        if (job.Config.Prompt is { Length: > 0 } wanted && wanted.Trim().Length > 0)
        {
            prompt.AppendLine(wanted.Trim());
        }

        if (job.Facts.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Facts:");
            foreach (AiTextFact fact in job.Facts.Where(fact => !string.IsNullOrWhiteSpace(fact.Value)))
            {
                prompt.AppendLine($"- {fact.Label}: {OneLine(fact.Value)}");
            }
        }

        if (job.Guidance.Count > 0)
        {
            prompt.AppendLine();
            foreach (string line in job.Guidance)
            {
                prompt.AppendLine(line);
            }
        }

        if (!string.IsNullOrWhiteSpace(job.Fallback))
        {
            prompt.AppendLine();
            prompt.AppendLine($"The usual wording, for reference (say it better, don't copy it): {OneLine(job.Fallback)}");
        }

        return new TextGenerationRequest(instructions.ToString().Trim(), prompt.ToString().Trim())
        {
            MaxTokens = limit * 3 + 24,
            Temperature = attempt == 0 ? 0.8 : 0.55,
            Seed = (int)(Hash($"{job.Seed}|{attempt}") & int.MaxValue)
        };
    }

    /// <summary>
    /// The reply without what small models like to add: reasoning in <c>&lt;think&gt;</c> tags, a "Here's your
    /// voice-over:" line, labels, quotes, markdown and emoji.
    /// </summary>
    public static string Clean(string? reply, AiTextUse use)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return string.Empty;
        }

        string text = ThinkPattern().Replace(reply, " ");
        int open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            // Reasoning that never finished: nothing after it is the answer.
            text = text[..open];
        }

        text = text.Replace("</think>", " ", StringComparison.OrdinalIgnoreCase).Replace("**", string.Empty).Replace("__", string.Empty);

        List<string> lines = [.. text.Replace("\r", string.Empty).Split('\n')
            .Select(line => LabelPattern().Replace(line.Trim().TrimStart('-', '•', '>', ' ').Trim(), string.Empty).Trim())
            .Where(line => line.Length > 0)];
        if (lines.Count > 1 && lines[0].EndsWith(':'))
        {
            lines.RemoveAt(0);
        }

        lines = [.. lines.Select(line => Unquote(WithoutEmoji(SpacePattern().Replace(line, " ").Trim()))).Where(line => line.Length > 0)];
        return use == AiTextUse.Spoken
            ? Unquote(string.Join(' ', lines))
            : string.Join('\n', lines.Take(2));
    }

    /// <summary>Why the writing can't be used, or null when it can.</summary>
    public static string? Check(string text, AiTextJob job)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "it wrote nothing";
        }

        int limit = Limit(job);
        int words = text.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > limit + Math.Max(3, limit / 2))
        {
            return $"it ran long ({words} words)";
        }

        if (text.IndexOfAny(['{', '}', '<', '>', '[', ']', '*', '#', '`', '|']) >= 0)
        {
            return "it added formatting";
        }

        foreach (string name in job.MustSay.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Mentions(text, name))
            {
                return $"it left out {name}";
            }
        }

        string known = string.Join(' ', job.Facts.Select(fact => $"{fact.Label} {fact.Value}")
            .Concat([job.Fallback, job.Channel, job.Config.Prompt ?? string.Empty, job.Purpose])
            .Concat(job.MustSay));
        HashSet<string> numbers = KnownNumbers(known);
        foreach (Match number in NumberPattern().Matches(text))
        {
            if (!numbers.Contains(Digits(number.Value)))
            {
                return $"it made up the number {number.Value}";
            }
        }

        string knownWords = $" {Normalize(known)} ";
        string written = $" {Normalize(text)} ";
        foreach (string day in DayWords)
        {
            if (written.Contains($" {day} ", StringComparison.Ordinal) && !knownWords.Contains($" {day} ", StringComparison.Ordinal))
            {
                return $"it said \"{day}\", which it wasn't told";
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="text"/> names <paramref name="name"/>, allowing for case, punctuation and a dropped "The".</summary>
    public static bool Mentions(string text, string name)
    {
        string written = $" {Normalize(text)} ";
        string wanted = Normalize(name);
        if (wanted.Length == 0 || written.Contains(wanted, StringComparison.Ordinal))
        {
            return true;
        }

        if (wanted.StartsWith("the ", StringComparison.Ordinal) && written.Contains(wanted[4..], StringComparison.Ordinal))
        {
            return true;
        }

        // A long title shortened the way people say it ("SpongeBob" for "SpongeBob SquarePants") still counts when its
        // first meaningful word is long enough to be unmistakable.
        string[] words = [.. wanted.Split(' ').Where(word => word.Length > 2 && !SmallWords.Contains(word))];
        return words.Length >= 2 && words[0].Length >= 6 && written.Contains($" {words[0]}", StringComparison.Ordinal);
    }

    /// <summary>Lower case, letters and digits only, single spaces.</summary>
    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        bool space = true;
        foreach (char character in text.Replace("&", " and ", StringComparison.Ordinal))
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                space = false;
            }
            else if (character is '\'' or '’')
            {
                // "SpongeBob's" stays one word.
            }
            else if (!space)
            {
                result.Append(' ');
                space = true;
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>A stable number in [0, 1) for a seed.</summary>
    public static double Fraction(string seed) => Hash(seed) / ((double)uint.MaxValue + 1);

    private static uint Hash(string seed) => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private static string OneLine(string text) => SpacePattern().Replace(text.Replace('\n', ' ').Replace('\r', ' '), " ").Trim();

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();
        while (trimmed.Length >= 2 && IsQuote(trimmed[0]) && IsQuote(trimmed[^1]))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool IsQuote(char character) => character is '"' or '“' or '”' or '\'' or '‘' or '’' or '«' or '»';

    private static string WithoutEmoji(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            if (char.IsSurrogate(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.OtherSymbol
                || character is '️' or '‍')
            {
                continue;
            }

            result.Append(character);
        }

        return result.ToString();
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ThinkPattern();

    [GeneratedRegex(@"^(voice[- ]?over|announcer|narrator|caption|on[- ]screen text|text|output|answer|script)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();

    /// <summary>
    /// Puts back the colon in a time written as "230" when the job gave it as "2:30", so a voice says "two thirty"
    /// rather than "two hundred and thirty". Everything else is left as written.
    /// </summary>
    public static string Tidy(string text, AiTextJob job)
    {
        string known = string.Join(' ', job.Facts.Select(fact => fact.Value).Append(job.Fallback));
        Dictionary<string, string> times = new(StringComparer.Ordinal);
        foreach (Match time in TimePattern().Matches(known))
        {
            times.TryAdd(Digits(time.Value), time.Value.Replace('.', ':'));
        }

        return times.Count == 0
            ? text
            : SquashedTimePattern().Replace(text, match => times.TryGetValue(Digits(match.Value), out string? time) ? time : match.Value);
    }

    [GeneratedRegex(@"\b\d{1,2}[:.]\d{2}\b")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"\b\d{3,4}\b")]
    private static partial Regex SquashedTimePattern();

    /// <summary>
    /// The numbers the writing may use, as digits only: every number given, and every way of writing a time given.
    /// "2:30" may come back as 2:30, 2.30, 230, 14:30, or just 2 or 30; "at 5" as 5, 5:00, 500 or 17:00.
    /// </summary>
    private static HashSet<string> KnownNumbers(string known)
    {
        HashSet<string> numbers = new(StringComparer.Ordinal);
        foreach (Match match in NumberPattern().Matches(known))
        {
            numbers.Add(Digits(match.Value));
            string[] parts = match.Value.Split(':', '.');
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out int hour) || hour > 24)
            {
                continue;
            }

            string minutes = parts.Length > 1 ? parts[1] : "00";
            foreach (int clock in (int[])[hour, hour < 12 ? hour + 12 : hour - 12])
            {
                if (clock is < 0 or > 24)
                {
                    continue;
                }

                string h = clock.ToString(CultureInfo.InvariantCulture);
                numbers.Add(Digits(h));
                numbers.Add(Digits(h + minutes));
                numbers.Add(Digits(minutes));
            }
        }

        return numbers;
    }

    /// <summary>A number as digits alone without leading zeros: "02:30" and "230" are both "230".</summary>
    private static string Digits(string number) =>
        string.Concat(number.Where(char.IsAsciiDigit)).TrimStart('0') is { Length: > 0 } digits ? digits : "0";

    // A time ("2:30", "2.30") counts as one number, so it's checked as a whole.
    [GeneratedRegex(@"\d{1,2}[:.]\d{2}(?!\d)|\d+")]
    private static partial Regex NumberPattern();
}
