using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchedularrTV.Service.Hosting;

/// <summary>How option objects are written to and read from settings.json.</summary>
public static class AppSettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        // Nulls are written: a cleared value (e.g. no promo folder) must not fall back to its non-null default.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };
}
