namespace SchedularrTV.Providers.Jellyfin;

/// <summary>A library configured on the Jellyfin server, and the folders it maps to.</summary>
public sealed record JellyfinLibrary(string Name, IReadOnlyList<string> Locations);
