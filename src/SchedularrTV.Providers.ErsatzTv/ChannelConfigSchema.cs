using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Keeps each channel's <c>channel.json</c> in the schema ErsatzTV Next reads.
/// </summary>
/// <remarks>
/// Next versions its channel config. 0.1.0 was a breaking change: a file with no <c>version</c> reads as 0.0.0 and is
/// refused ("missing schema version (read as 0.0.0) is no longer supported"), so the channel process never starts.
/// The lineup and guide are still served, so the guide looks fine in Jellyfin, but every channel fails to play.
/// The only change in meaning is that a <c>null</c> audio or video <c>format</c>, which used to mean "copy",
/// is now an error; copying is <c>"mode": "copy"</c>.
/// </remarks>
public static class ChannelConfigSchema
{
    public const string Prefix = "https://ersatztv.org/channel/version/";

    /// <summary>The version written, and the one older files are brought up to.</summary>
    public const string Current = Prefix + "0.1.0";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// The config brought up to <see cref="Current"/>, or null when it's already at 0.1 or later (or has a version
    /// this doesn't recognise, which is left for ErsatzTV to judge).
    /// </summary>
    public static JsonObject? Upgrade(JsonObject config)
    {
        if (config["version"] is { } version)
        {
            if (version.GetValueKind() != JsonValueKind.String || Breaking(version.GetValue<string>()) is not 0)
            {
                return null;
            }
        }

        // Before 0.1.0, "format": null meant copy.
        if (config["normalization"] is JsonObject normalization)
        {
            foreach (string kind in new[] { "audio", "video" })
            {
                if (normalization[kind] is JsonObject track && track.ContainsKey("format") && track["format"] is null)
                {
                    track.Remove("format");
                    track["mode"] = "copy";

                    // Not allowed together with copying audio.
                    if (kind == "audio" && track.ContainsKey("normalize_loudness"))
                    {
                        track["normalize_loudness"] = false;
                    }
                }
            }
        }

        // The version goes first, where people look for it.
        var upgraded = new JsonObject { ["version"] = Current };
        foreach (KeyValuePair<string, JsonNode?> property in config.ToList())
        {
            config.Remove(property.Key);
            if (property.Key != "version")
            {
                upgraded[property.Key] = property.Value;
            }
        }

        return upgraded;
    }

    /// <summary>The breaking part of a channel schema version ("…/0.1.0" is 1), or null if it isn't one.</summary>
    private static int? Breaking(string uri)
    {
        if (!uri.StartsWith(Prefix + "0.", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = uri[(Prefix.Length + 2)..].Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int breaking)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? breaking
            : null;
    }

    /// <summary>
    /// Upgrades a channel.json file in place when it needs it, keeping the original beside it as
    /// <c>channel.json.bak</c>. A file that can't be read as JSON is left alone (ErsatzTV will say what's wrong).
    /// </summary>
    /// <returns>Whether the file was rewritten.</returns>
    public static async Task<bool> UpgradeFileAsync(string path, ILogger logger, CancellationToken cancellationToken)
    {
        string original;
        JsonObject? config;
        try
        {
            original = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            config = JsonNode.Parse(original) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Couldn't read {Path} to check its schema version: {Message}", path, ex.Message);
            return false;
        }

        if (config is null || Upgrade(config) is not { } upgraded)
        {
            return false;
        }

        try
        {
            string backup = path + ".bak";
            if (!File.Exists(backup))
            {
                await File.WriteAllTextAsync(backup, original, cancellationToken).ConfigureAwait(false);
            }

            string temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, upgraded.ToJsonString(Indented), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't update {Path} to ErsatzTV's channel schema {Version}: {Message}", path, Current, ex.Message);
            return false;
        }

        logger.LogInformation("Updated {Path} to ErsatzTV's channel schema {Version} (the original is kept as channel.json.bak).", path, Current);
        return true;
    }
}
