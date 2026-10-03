namespace SchedularrTV.Providers.Jellyfin;

/// <summary>An extra attached to a title, such as an intro or a behind-the-scenes clip.</summary>
public sealed record JellyfinExtra(string Id, string Name, string Path, TimeSpan Duration);
