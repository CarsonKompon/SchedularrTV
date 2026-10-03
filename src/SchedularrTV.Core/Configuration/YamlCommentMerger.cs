using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization.NamingConventions;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Re-applies a hand-written document's comments, blank-line spacing and scalar formatting to a
/// freshly serialised one.
/// </summary>
/// <remarks>
/// YamlDotNet discards comments, so saving from the visual editor would otherwise strip the annotated
/// schedule file the user maintains by hand. Scalars whose value did not actually change keep their
/// original text, so <c>12h</c> stays <c>12h</c> rather than becoming <c>12:00:00</c>; genuinely new
/// durations are rendered in the same friendly form.
/// </remarks>
public static partial class YamlCommentMerger
{
    private enum Kind { Blank, Comment, Structural }

    private sealed record Line(
        string Raw,
        Kind Kind,
        int Indent,
        string Path,
        string Key,
        string Scalar,
        int ValueStart,
        string InlineComment);

    private sealed record Frame(int Indent, string Name, bool IsItem);

    /// <summary>Fields that identify a sequence item, so comments survive the item moving.</summary>
    private static readonly string[] IdentityKeys =
        ["id", "name", "key", "show", "movie", "collection", "query"];

    // 'time' and 'end_time' are wall-clock fields; they read better as 21:00 than as "21h".
    private static readonly HashSet<string> DurationKeys =
        BuildKeys(t => t == typeof(TimeSpan) || t == typeof(TimeSpan?), except: ["time", "end_time"]);

    private static readonly HashSet<string> EnumKeys =
        BuildKeys(t => (Nullable.GetUnderlyingType(t) ?? t).IsEnum, except: []);

    public static string Merge(string original, string regenerated)
    {
        List<Line> target = Scan(SplitLines(regenerated));
        Annotations annotations = string.IsNullOrWhiteSpace(original)
            ? new Annotations()
            : Collect(Scan(SplitLines(original)));

        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return Render(target, annotations, newline);
    }

    private sealed class Annotations
    {
        public List<string> Header { get; } = [];
        public List<string> Footer { get; } = [];
        public Dictionary<string, (int Indent, List<string> Lines)> Before { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Inline { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Scalars { get; } = new(StringComparer.Ordinal);
    }

    private static Annotations Collect(List<Line> lines)
    {
        Annotations annotations = new();
        List<string> buffer = [];
        bool seenStructural = false;

        foreach (Line line in lines)
        {
            if (line.Kind != Kind.Structural)
            {
                buffer.Add(line.Raw);
                continue;
            }

            if (!seenStructural)
            {
                annotations.Header.AddRange(buffer);
                seenStructural = true;
            }
            else if (buffer.Count > 0 && line.Path.Length > 0)
            {
                annotations.Before[line.Path] = (line.Indent, [.. buffer]);
            }

            buffer.Clear();

            if (line.InlineComment.Length > 0)
            {
                annotations.Inline[line.Path] = line.InlineComment;
            }

            if (line.Scalar.Length > 0)
            {
                annotations.Scalars[line.Path] = line.Scalar;
            }
        }

        annotations.Footer.AddRange(buffer);
        return annotations;
    }

    private static string Render(List<Line> lines, Annotations annotations, string newline)
    {
        StringBuilder builder = new();

        foreach (string header in annotations.Header)
        {
            builder.Append(header).Append(newline);
        }

        foreach (Line line in lines)
        {
            if (line.Kind != Kind.Structural)
            {
                // The generated document has no comments of its own; its blank lines are just padding.
                if (line.Raw.Trim().Length > 0)
                {
                    builder.Append(line.Raw).Append(newline);
                }

                continue;
            }

            if (annotations.Before.TryGetValue(line.Path, out (int Indent, List<string> Lines) before))
            {
                foreach (string comment in before.Lines)
                {
                    builder.Append(Reindent(comment, line.Indent - before.Indent)).Append(newline);
                }
            }

            builder.Append(Transform(line, annotations)).Append(newline);
        }

        foreach (string footer in annotations.Footer)
        {
            builder.Append(footer).Append(newline);
        }

        return builder.ToString();
    }

    /// <summary>Shifts a carried comment to the depth its key now sits at.</summary>
    private static string Reindent(string line, int shift)
    {
        if (shift == 0 || line.Trim().Length == 0)
        {
            return line;
        }

        return shift > 0
            ? new string(' ', shift) + line
            : line[Math.Min(-shift, Indent(line))..];
    }

    private static string Transform(Line line, Annotations annotations)
    {
        string raw = line.Raw;

        if (line.Scalar.Length > 0)
        {
            string replacement = annotations.Scalars.TryGetValue(line.Path, out string? authored)
                                 && Equivalent(line.Key, authored, line.Scalar)
                ? authored
                : Prettify(line.Key, line.Scalar);

            if (replacement.Length > 0 && !string.Equals(replacement, line.Scalar, StringComparison.Ordinal))
            {
                raw = string.Concat(
                    raw.AsSpan(0, line.ValueStart),
                    replacement,
                    raw.AsSpan(line.ValueStart + line.Scalar.Length));
            }
        }

        return annotations.Inline.TryGetValue(line.Path, out string? inline)
            ? raw.TrimEnd() + "  " + inline
            : raw;
    }

    /// <summary>Renders a machine-formatted value the way the samples are written.</summary>
    private static string Prettify(string key, string scalar)
    {
        if (DurationKeys.Contains(key) && DurationPattern().IsMatch(scalar) && DurationText.TryParse(scalar, out TimeSpan value))
        {
            return DurationText.Format(value);
        }

        // The enum converter reads any casing, and the documented style is season_episode.
        return EnumKeys.Contains(key) && scalar.Length > 0 && char.IsUpper(scalar[0]) && scalar.All(char.IsLetter)
            ? UnderscoredNamingConvention.Instance.Apply(scalar)
            : string.Empty;
    }

    private static bool Equivalent(string key, string authored, string generated)
    {
        string a = Unquote(authored);
        string b = Unquote(generated);

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        // Enums bind through a case- and separator-insensitive converter, so "shuffle" and "Shuffle"
        // are the same value and the authored spelling should win.
        if (EnumKeys.Contains(key) && string.Equals(Simplify(a), Simplify(b), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DurationText.TryParse(a, out TimeSpan first)
               && DurationText.TryParse(b, out TimeSpan second)
               && first == second;
    }

    private static string Simplify(string value) => new([.. value.Where(char.IsLetterOrDigit)]);

    private static List<Line> Scan(string[] lines)
    {
        List<Line> result = new(lines.Length);
        List<Frame> stack = [];
        Dictionary<string, int> counters = new(StringComparer.Ordinal);
        int blockScalarIndent = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();
            int indent = Indent(raw);

            if (blockScalarIndent >= 0)
            {
                if (trimmed.Length == 0 || indent > blockScalarIndent)
                {
                    result.Add(Passthrough(raw));
                    continue;
                }

                blockScalarIndent = -1;
            }

            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                result.Add(new Line(raw, trimmed.Length == 0 ? Kind.Blank : Kind.Comment, indent, "", "", "", 0, ""));
                continue;
            }

            string content = raw[indent..];
            int keyIndent = indent;
            bool isItem = content.StartsWith("- ", StringComparison.Ordinal) || content == "-";

            if (isItem)
            {
                PopForItem(stack, indent);

                string parent = PathOf(stack);
                int index = counters.TryGetValue(parent, out int seen) ? seen : 0;
                counters[parent] = index + 1;

                string identity = ItemIdentity(lines, i, indent);
                stack.Add(new Frame(indent, identity.Length > 0 ? $"[{identity}]" : $"[{index}]", true));

                int dash = content == "-" ? 1 : 2;
                keyIndent = indent + dash;
                content = content[dash..];

                if (content.Trim().Length == 0)
                {
                    result.Add(Passthrough(raw));
                    continue;
                }
            }

            Match key = KeyPattern().Match(content);
            if (!key.Success)
            {
                // A plain scalar list entry such as "- Any Channel". It has to stay structural, or it
                // would be carried over as a comment and re-emitted alongside the generated copy.
                if (isItem)
                {
                    (string itemScalar, string itemComment) = SplitComment(content);
                    result.Add(new Line(
                        raw,
                        Kind.Structural,
                        indent,
                        PathOf(stack),
                        "",
                        itemScalar,
                        raw.Length - content.Length,
                        itemComment));
                }
                else
                {
                    result.Add(Passthrough(raw));
                }

                continue;
            }

            string name = Unquote(key.Groups["k"].Value.Trim());
            PopForKey(stack, keyIndent);
            stack.Add(new Frame(keyIndent, name, false));

            (string scalar, string comment) = SplitComment(key.Groups["v"].Success ? key.Groups["v"].Value : "");
            int valueStart = key.Groups["v"].Success
                ? raw.Length - content.Length + key.Groups["v"].Index
                : raw.Length;

            if (scalar.StartsWith('|') || scalar.StartsWith('>'))
            {
                blockScalarIndent = keyIndent;
            }

            result.Add(new Line(raw, Kind.Structural, indent, PathOf(stack), name, scalar, valueStart, comment));
        }

        return result;
    }

    private static string ItemIdentity(string[] lines, int start, int dashIndent)
    {
        int childIndent = -1;

        for (int i = start; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            int indent = Indent(raw);
            string content = raw[indent..];

            if (i == start)
            {
                int dash = content == "-" ? 1 : 2;
                if (content.Length <= dash)
                {
                    continue;
                }

                content = content[dash..];
                indent += dash;
            }
            else if (indent <= dashIndent)
            {
                break;
            }

            if (childIndent < 0)
            {
                childIndent = indent;
            }

            if (indent != childIndent)
            {
                continue;
            }

            Match key = KeyPattern().Match(content);
            if (!key.Success)
            {
                // A scalar list entry identifies itself, so comments stay attached if it moves.
                return i == start ? Unquote(SplitComment(content).Scalar) : "";
            }

            if (!IdentityKeys.Contains(Unquote(key.Groups["k"].Value.Trim()), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = Unquote(SplitComment(key.Groups["v"].Value).Scalar);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "";
    }

    private static void PopForItem(List<Frame> stack, int indent)
    {
        while (stack.Count > 0 && (stack[^1].Indent > indent || (stack[^1].Indent == indent && stack[^1].IsItem)))
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static void PopForKey(List<Frame> stack, int indent)
    {
        while (stack.Count > 0 && stack[^1].Indent >= indent)
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static string PathOf(List<Frame> stack) => string.Join('.', stack.Select(f => f.Name));

    private static Line Passthrough(string raw) => new(raw, Kind.Comment, Indent(raw), "", "", "", 0, "");

    private static int Indent(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return i;
    }

    private static (string Scalar, string Comment) SplitComment(string value)
    {
        bool single = false;
        bool quoted = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\'' && !quoted)
            {
                single = !single;
            }
            else if (c == '"' && !single)
            {
                quoted = !quoted;
            }
            else if (c == '#' && !single && !quoted && i > 0 && char.IsWhiteSpace(value[i - 1]))
            {
                return (value[..i].TrimEnd(), value[i..]);
            }
        }

        return (value.TrimEnd(), "");
    }

    private static string Unquote(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    private static HashSet<string> BuildKeys(Func<Type, bool> matches, string[] except)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (Type type in typeof(SchedulerConfig).Assembly.GetTypes())
        {
            if (type.Namespace != typeof(SchedulerConfig).Namespace)
            {
                continue;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (matches(property.PropertyType))
                {
                    keys.Add(UnderscoredNamingConvention.Instance.Apply(property.Name));
                }
            }
        }

        keys.ExceptWith(except);
        return keys;
    }

    [GeneratedRegex("""^(?<k>"(?:\\.|[^"\\])*"|'(?:''|[^'])*'|(?:[^:#]|:(?![ \t]|$))+?)\s*:(?:[ \t]+(?<v>.*))?[ \t]*$""")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^-?(\d+\.)?\d{1,2}:\d{2}:\d{2}(\.\d+)?$")]
    private static partial Regex DurationPattern();
}
