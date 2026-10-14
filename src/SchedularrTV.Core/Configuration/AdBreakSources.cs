namespace SchedularrTV.Core.Configuration;

/// <summary>
/// One folder an ad break draws ads, bumpers or filler from, as a build sees it: everything below
/// <paramref name="Folder"/> except what's below <paramref name="Excluded"/> (folders inside it given another job).
/// </summary>
/// <param name="Seasonal">Clips filed under a month, season, holiday or date folder only play then.</param>
public sealed record AdPoolSource(string Folder, IReadOnlyList<string> Excluded, bool Seasonal);

/// <summary>One folder of promos, as a build sees it. See <see cref="AdBreakPromo"/> for what the flags mean.</summary>
public sealed record PromoFolderSource(
    string Folder,
    IReadOnlyList<string> Excluded,
    bool PerShow,
    bool AnyShow,
    bool Times,
    bool Days,
    string AnyShowFolder);
