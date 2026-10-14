using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Temporal;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Builds the pool queries for each interstitial tier, expanding temporal tags per spec 1.2 and
/// promo targeting per spec 3.2.
/// </summary>
public static class InterstitialQueryBuilder
{
    /// <summary>
    /// Produces e.g.
    /// <c>tag:"Commercials" AND (tag:"Adult Swim" OR tag:"Any Channel") AND (tag:"Any Time" OR tag:"January" OR tag:"Winter" OR tag:"01-10")</c>.
    /// </summary>
    public static QueryExpression BuildCommercials(CommercialsConfig commercials, DateTimeOffset moment, TemporalTagOptions temporalOptions) =>
        commercials.Sources is { } sources
            ? AdBreakCompiler.Pool(sources, commercials.Filter, moment, temporalOptions,
                (commercials.TagPools ?? []).Select(pool => BuildCommercials(pool, moment, temporalOptions)))
            : Build(
            commercials.Query,
            commercials.BaseQuery,
            commercials.BaseTags,
            commercials.Types,
            commercials.AutoTemporalTags == true,
            commercials.Filter,
            moment,
            temporalOptions);

    public static QueryExpression BuildFiller(FillerTierConfig filler, DateTimeOffset moment, TemporalTagOptions temporalOptions) =>
        filler.Sources is { } sources
            ? AdBreakCompiler.Pool(sources, filler.Filter, moment, temporalOptions,
                (filler.TagPools ?? []).Select(pool => BuildFiller(pool, moment, temporalOptions)))
            : Build(
            filler.Query,
            filler.BaseQuery,
            filler.BaseTags,
            filler.Types,
            filler.AutoTemporalTags == true,
            filler.Filter,
            moment,
            temporalOptions);

    /// <summary>
    /// Promos for the programme starting the instant this break ends. Accepts either the
    /// <c>promo_type</c> attribute or an "Up Next" tag, so libraries can use whichever convention
    /// they already have.
    /// </summary>
    public static QueryExpression BuildUpNext(PromoConfig promos, IReadOnlyList<string> showIds) =>
        BuildSpot(promos, promos.UpNext, showIds, null);

    /// <summary>Promos for one fixed position in a break, matched by attribute or by tag.</summary>
    public static QueryExpression BuildSpot(
        PromoConfig promos,
        PromoSpotConfig spot,
        IReadOnlyList<string> showIds,
        IReadOnlyList<string>? baseTags)
    {
        if (showIds.Count == 0)
        {
            return QueryExpression.None;
        }

        List<QueryExpression> markers = [];
        if (spot.PromoTypes.Count > 0)
        {
            markers.Add(new FieldExpression("promo_type", ComparisonOperator.In, spot.PromoTypes));
        }

        if (!string.IsNullOrWhiteSpace(spot.Tag))
        {
            markers.Add(TagIs(spot.Tag));
        }

        if (markers.Count == 0)
        {
            return QueryExpression.None;
        }

        List<QueryExpression> clauses = PromoClauses(promos, showIds, baseTags);
        clauses.Add(QueryExpression.Or(markers));

        return QueryExpression.And(clauses);
    }

    /// <summary>
    /// Every "eventually" trailer in the brand's folders. The show is deliberately not part of the
    /// query: which of them are worth playing depends on what the channel is going to air, which the
    /// library cannot answer.
    /// </summary>
    public static QueryExpression BuildEventually(
        PromoConfig promos,
        EventuallyConfig eventually,
        IReadOnlyList<string>? baseTags)
    {
        List<QueryExpression> markers = [];

        if (eventually.PromoTypes.Count > 0)
        {
            markers.Add(new FieldExpression("promo_type", ComparisonOperator.In, eventually.PromoTypes));
        }

        if (!string.IsNullOrWhiteSpace(eventually.Tag))
        {
            markers.Add(TagIs(eventually.Tag));
        }

        if (markers.Count == 0)
        {
            return QueryExpression.None;
        }

        List<QueryExpression> clauses = [];

        if (!string.IsNullOrWhiteSpace(promos.BaseQuery))
        {
            clauses.Add(QueryParser.Parse(promos.BaseQuery));
        }

        if (baseTags is { Count: > 0 })
        {
            clauses.Add(QueryExpression.Or(baseTags.Select(TagIs)));
        }

        clauses.Add(QueryExpression.Or(markers));

        return QueryExpression.And(clauses);
    }

    /// <summary>
    /// The part every promo query shares: the asset pool, the brand folders it may come from, and the
    /// show it advertises.
    /// </summary>
    public static List<QueryExpression> PromoClauses(
        PromoConfig promos,
        IReadOnlyList<string> showIds,
        IReadOnlyList<string>? baseTags)
    {
        List<QueryExpression> clauses = [];

        if (!string.IsNullOrWhiteSpace(promos.BaseQuery))
        {
            clauses.Add(QueryParser.Parse(promos.BaseQuery));
        }

        if (baseTags is { Count: > 0 })
        {
            clauses.Add(QueryExpression.Or(baseTags.Select(TagIs)));
        }

        clauses.Add(ShowIdIn(WithAnyShow(promos, showIds)));

        return clauses;
    }

    /// <summary>Lets one clip stand in for every show on the channel.</summary>
    private static List<string> WithAnyShow(PromoConfig promos, IReadOnlyList<string> showIds)
    {
        List<string> ids = [.. showIds];

        if (!string.IsNullOrWhiteSpace(promos.AnyShowAlias)
            && !ids.Contains(promos.AnyShowAlias, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(promos.AnyShowAlias);
        }

        return ids;
    }

    public static QueryExpression TagIs(string tag) =>
        new FieldExpression("tag", ComparisonOperator.Match, [tag]);

    public static QueryExpression ShowIdIn(IReadOnlyList<string> showIds) =>
        new FieldExpression("show_id", ComparisonOperator.In, showIds);

    private static QueryExpression Build(
        string? query,
        string? baseQuery,
        List<string>? baseTags,
        List<string>? types,
        bool autoTemporalTags,
        string? filter,
        DateTimeOffset moment,
        TemporalTagOptions temporalOptions)
    {
        List<QueryExpression> clauses = [];

        // An explicit tier query replaces the base query but still composes with tags and filters,
        // so a block can narrow a channel-wide pool without restating it.
        string? pool = !string.IsNullOrWhiteSpace(query) ? query : baseQuery;
        if (!string.IsNullOrWhiteSpace(pool))
        {
            clauses.Add(QueryParser.Parse(pool));
        }

        if (baseTags is { Count: > 0 })
        {
            clauses.Add(QueryExpression.Or(baseTags.Select(TagIs)));
        }

        if (types is { Count: > 0 })
        {
            clauses.Add(QueryExpression.Or(types.Select(TagIs)));
        }

        if (autoTemporalTags)
        {
            TemporalTags tags = TemporalTagResolver.Resolve(moment, temporalOptions);
            List<QueryExpression> temporal = [TagIs(temporalOptions.AnyTimeTag)];
            temporal.AddRange(tags.All.Select(TagIs));
            clauses.Add(QueryExpression.Or(temporal));
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            clauses.Add(QueryParser.Parse(filter));
        }

        return clauses.Count == 0 ? QueryExpression.None : QueryExpression.And(clauses);
    }
}
