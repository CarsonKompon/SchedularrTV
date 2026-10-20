namespace SchedularrTV.Providers.Jellyfin;

/// <summary>Credential-free provenance used to stream the exact library item when its file is remote.</summary>
public static class JellyfinPlaybackIdentity
{
    public const string ItemId = "jellyfin.item_id";
    public const string OriginalPath = "jellyfin.original_path";
    public const string Server = "jellyfin.server";
}
