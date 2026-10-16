namespace SchedularrTV.Plugins.Images;

/// <summary>A search for pictures, such as <c>SpongeBob SquarePants main character png</c>.</summary>
/// <param name="Query">What to look for, with any tokens already filled in.</param>
public sealed record ImageSearchRequest(string Query)
{
    /// <summary>Only pictures with a transparent background, where the service can tell.</summary>
    public bool TransparentOnly { get; init; }

    /// <summary>Leaves out explicit results, where the service can.</summary>
    public bool SafeSearch { get; init; } = true;

    /// <summary>How many results are wanted at most; a service may return fewer.</summary>
    public int Count { get; init; } = 20;
}

/// <summary>One picture found, best first in the list a search returns.</summary>
/// <param name="Url">The full-size picture (not a thumbnail), to be downloaded.</param>
public sealed record ImageSearchResult(string Url)
{
    public int Width { get; init; }

    public int Height { get; init; }

    public string? Title { get; init; }

    /// <summary>The page the picture is on, for showing where it came from.</summary>
    public string? Page { get; init; }

    /// <summary>A small preview of it, if the service gives one.</summary>
    public string? Thumbnail { get; init; }
}

/// <summary>
/// A picture search service that automatic image layers (in generated promos and overlays) can look pictures up with,
/// selectable by <see cref="Id"/>. Register one with <see cref="PluginServiceCollectionExtensions.AddImageSearchProvider{T}"/>.
/// </summary>
public interface IImageSearchProvider
{
    /// <summary>Stored in templates, so it must never change once released.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Why it can't search right now, such as no API key; null when it can.</summary>
    string? SetupProblem => null;

    /// <summary>The pictures found, best first.</summary>
    Task<IReadOnlyList<ImageSearchResult>> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default);
}
