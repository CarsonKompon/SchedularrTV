using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.Jellyfin;

/// <summary>Writes edited show and movie metadata to Jellyfin through its API (see <see cref="IMetadataEditor"/>).</summary>
public sealed class JellyfinMetadataEditor(JellyfinClient client, ILogger<JellyfinMetadataEditor> logger) : IMetadataEditor
{
    public string Name => "Jellyfin";

    public string WriteLabel => "Push to Jellyfin";

    public MetadataFields Supported => MetadataFields.All;

    public bool Owns(string id) => JellyfinArtworkSource.IsJellyfinId(id);

    public async ValueTask<EditableMetadata?> ReadAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default) =>
        await client.GetItemDocumentAsync(id, cancellationToken).ConfigureAwait(false) is { } item
            ? JellyfinItemMetadata.Read(item)
            : null;

    public async ValueTask<string?> DescribeTargetAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default)
    {
        JsonObject? item = await client.GetItemDocumentAsync(id, cancellationToken).ConfigureAwait(false);
        return item is null
            ? null
            : $"This changes “{item["Name"]?.GetValue<string>() ?? id}” on your Jellyfin server ({client.ServerAddress}), for every app that uses it. "
              + "The changed fields are locked there, so Jellyfin's next metadata refresh keeps them instead of downloading them again.";
    }

    public async ValueTask WriteAsync(string id, MetadataItemKind kind, EditableMetadata metadata, MetadataFields fields,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // The update replaces the whole item, so it's read fresh, changed only where edited, and sent back whole.
            JsonObject item = await client.GetItemDocumentAsync(id, cancellationToken).ConfigureAwait(false)
                              ?? throw new MetadataWriteException("Jellyfin no longer has this item. Rescan the library and try again.");
            JellyfinItemMetadata.Apply(item, metadata.Normalized(), fields);
            await client.UpdateItemDocumentAsync(id, item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JellyfinConfigurationException or HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Couldn't push metadata for {Item} to Jellyfin.", id);
            throw new MetadataWriteException(ex is TaskCanceledException ? "Jellyfin didn't answer in time." : ex.Message, ex);
        }
    }
}

/// <summary>Between Jellyfin's item JSON (a <c>BaseItemDto</c>) and <see cref="EditableMetadata"/>.</summary>
public static class JellyfinItemMetadata
{
    public static EditableMetadata Read(JsonObject item) => new EditableMetadata
    {
        Title = Text(item, "Name"),
        // ForcedSortName is the one the user sets; SortName is otherwise worked out from the name.
        SortTitle = Text(item, "ForcedSortName"),
        Year = item["ProductionYear"] is JsonValue year && year.TryGetValue(out int value) ? value : null,
        Overview = Text(item, "Overview"),
        Rating = Text(item, "OfficialRating"),
        Genres = Strings(item["Genres"]),
        Studios = item["Studios"] is JsonArray studios
            ? [.. studios.Select(studio => studio?["Name"]?.GetValue<string>()).OfType<string>()]
            : [],
        Tags = Strings(item["Tags"])
    }.Normalized();

    /// <summary>
    /// Sets <paramref name="fields"/> of <paramref name="item"/> from <paramref name="metadata"/>, leaving every other
    /// field as it was, and locks the changed ones that Jellyfin can lock.
    /// </summary>
    public static void Apply(JsonObject item, EditableMetadata metadata, MetadataFields fields)
    {
        if (fields.HasFlag(MetadataFields.Title) && metadata.Title is { } title)
        {
            // A blank name isn't allowed; the old one stays.
            item["Name"] = title;
        }

        if (fields.HasFlag(MetadataFields.SortTitle))
        {
            item["ForcedSortName"] = metadata.SortTitle;
        }

        if (fields.HasFlag(MetadataFields.Year))
        {
            item["ProductionYear"] = metadata.Year;
        }

        if (fields.HasFlag(MetadataFields.Overview))
        {
            item["Overview"] = metadata.Overview;
        }

        if (fields.HasFlag(MetadataFields.Rating))
        {
            item["OfficialRating"] = metadata.Rating;
        }

        if (fields.HasFlag(MetadataFields.Genres))
        {
            item["Genres"] = new JsonArray([.. metadata.Genres.Select(genre => (JsonNode?)JsonValue.Create(genre))]);
        }

        if (fields.HasFlag(MetadataFields.Studios))
        {
            // Matched to the server's studios by name; an Id isn't needed.
            item["Studios"] = new JsonArray([.. metadata.Studios.Select(studio => (JsonNode?)new JsonObject { ["Name"] = studio })]);
        }

        if (fields.HasFlag(MetadataFields.Tags))
        {
            item["Tags"] = new JsonArray([.. metadata.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]);
        }

        Lock(item, fields);
    }

    // Jellyfin's lockable fields (MetadataField) for the ones edited here; year and sort title have none.
    private static readonly (MetadataFields Field, string Locked)[] Lockable =
    [
        (MetadataFields.Title, "Name"),
        (MetadataFields.Overview, "Overview"),
        (MetadataFields.Rating, "OfficialRating"),
        (MetadataFields.Genres, "Genres"),
        (MetadataFields.Studios, "Studios"),
        (MetadataFields.Tags, "Tags")
    ];

    private static void Lock(JsonObject item, MetadataFields fields)
    {
        List<string> locked = Strings(item["LockedFields"]).ToList();
        foreach ((MetadataFields field, string name) in Lockable)
        {
            if (fields.HasFlag(field) && !locked.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                locked.Add(name);
            }
        }

        item["LockedFields"] = new JsonArray([.. locked.Select(name => (JsonNode?)JsonValue.Create(name))]);
    }

    private static string? Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? [.. array.Select(entry => entry is JsonValue value && value.TryGetValue(out string? text) ? text : null).OfType<string>()]
            : [];
}
