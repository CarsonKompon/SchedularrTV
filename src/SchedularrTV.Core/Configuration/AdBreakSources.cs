namespace SchedularrTV.Core.Configuration;

/// <summary>
/// One folder an ad break draws ads, bumpers or filler from, as a build sees it: everything below
/// <paramref name="Folder"/> except what's below <paramref name="Excluded"/> (folders inside it given another job).
/// </summary>
/// <param name="Seasonal">Clips filed under a month, season, holiday or date folder only play then.</param>
public sealed record AdPoolSource(string Folder, IReadOnlyList<string> Excluded, bool Seasonal);

/// <summary>One folder of promos, as a build sees it. See <see cref="AdBreakPromo"/> for what the flags mean.</summary>
/// <param name="Type">
/// For a folder of promos sorted by type (<see cref="AdBreakPromoRoot"/>): only clips under a folder named for this
/// promo type count, and that folder isn't read as the show. Null for a folder holding one type only.
/// </param>
/// <param name="Seasonal">
/// When set, folders named for a month, season, holiday or date only count then (and aren't read as the show).
/// </param>
/// <param name="AnyMovieFolder">The show folder holding clips for any movie; null is "Any Movie".</param>
public sealed record PromoFolderSource(
    string Folder,
    IReadOnlyList<string> Excluded,
    bool PerShow,
    bool AnyShow,
    bool Times,
    bool Days,
    string AnyShowFolder,
    AdPromoType? Type = null,
    SchedularrTV.Core.Temporal.TemporalTagOptions? Seasonal = null,
    string? AnyMovieFolder = null)
{
    /// <summary>The "any movie" folder's name as used: <see cref="AnyMovieFolder"/>, or "Any Movie".</summary>
    public string AnyMovie => string.IsNullOrWhiteSpace(AnyMovieFolder) ? AdBreakConfig.DefaultAnyMovieFolder : AnyMovieFolder;
}
