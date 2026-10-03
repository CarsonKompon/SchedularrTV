using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Accepts the human-friendly duration forms schedule authors actually write:
/// <c>21:00</c>, <c>00:03:30</c>, <c>90s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c>.
/// </summary>
internal sealed partial class FlexibleTimeSpanConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TimeSpan) || type == typeof(TimeSpan?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        Scalar scalar = parser.Consume<Scalar>();
        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            return type == typeof(TimeSpan?) ? null : TimeSpan.Zero;
        }

        return Parse(scalar.Value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var span = (TimeSpan?)value ?? TimeSpan.Zero;
        emitter.Emit(new Scalar(span.ToString("c", CultureInfo.InvariantCulture)));
    }

    public static TimeSpan Parse(string text)
    {
        string value = text.Trim();

        Match unit = UnitPattern().Match(value);
        if (unit.Success)
        {
            double amount = double.Parse(unit.Groups[1].Value, CultureInfo.InvariantCulture);
            return unit.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => TimeSpan.Zero
            };
        }

        if (TimeSpan.TryParseExact(value, ["hh\\:mm", "h\\:mm", "hh\\:mm\\:ss", "h\\:mm\\:ss", "c"], CultureInfo.InvariantCulture, out TimeSpan exact))
        {
            return exact;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed))
        {
            return parsed;
        }

        throw new SchedulerConfigException($"'{text}' is not a valid duration or time-of-day.");
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(ms|s|m|h|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPattern();
}

/// <summary>
/// Enum converter that ignores case, spaces, hyphens, underscores and commas so
/// <c>season_episode</c>, <c>"Season, Episode"</c> and <c>SeasonEpisode</c> all bind.
/// </summary>
internal sealed class FlexibleEnumConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => Nullable.GetUnderlyingType(type) is { IsEnum: true } || type.IsEnum;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        Scalar scalar = parser.Consume<Scalar>();
        Type enumType = Nullable.GetUnderlyingType(type) ?? type;

        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            return Nullable.GetUnderlyingType(type) is not null ? null : Activator.CreateInstance(enumType);
        }

        string normalized = Normalize(scalar.Value);
        foreach (string name in Enum.GetNames(enumType))
        {
            if (string.Equals(Normalize(name), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse(enumType, name);
            }
        }

        throw new SchedulerConfigException(
            $"'{scalar.Value}' is not a valid {enumType.Name}. Expected one of: {string.Join(", ", Enum.GetNames(enumType))}.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        emitter.Emit(new Scalar(value?.ToString() ?? string.Empty));

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());
}

public sealed class SchedulerConfigException(string message) : Exception(message);
