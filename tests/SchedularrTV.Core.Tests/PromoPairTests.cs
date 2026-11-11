using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Promos that always play together: a quiz question and its answer either side of a break, or of a show.</summary>
public sealed class PromoPairTests
{
    private const string Root = @"D:\Media\Commercials\Any Channel";

    private static MediaItem Clip(string path) => new()
    {
        Id = path, Title = Path.GetFileNameWithoutExtension(path), Kind = MediaKind.Filler, Duration = TimeSpan.FromSeconds(10), Path = path
    };

    private static AdBreakConfig AdBreak() => new()
    {
        Promos =
        [
            new AdBreakPromo { Type = AdPromoType.YoureWatching, Path = $@"{Root}\You're Watching" },
            new AdBreakPromo { Type = AdPromoType.BackToTheShow, Path = $@"{Root}\Back To The Show" }
        ],
        PromoPairs =
        [
            new AdPromoPair { Kind = AdPromoPairKind.Break, First = $@"{Root}\You're Watching\quiz 1 question.mp4", Second = $@"{Root}\Back To The Show\quiz 1 answer.mp4" },
            new AdPromoPair { Kind = AdPromoPairKind.Show, First = $@"{Root}\Now Playing\part 1.mp4", Second = $@"{Root}\This Has Been\part 2.mp4" },
            new AdPromoPair { Kind = AdPromoPairKind.Break, First = $@"{Root}\You're Watching\half done.mp4" }
        ]
    };

    [Fact]
    public void Only_complete_pairs_reach_the_planner()
    {
        PromoConfig promos = AdBreakCompiler.Promos([AdBreak()], new TemporalTagOptions());

        Assert.Equal(2, promos.Pairs.Count);
        Assert.All(promos.Pairs, pair => Assert.True(pair.IsComplete));
    }

    [Fact]
    public void A_question_knows_its_answer_however_the_path_is_written()
    {
        PromoConfig promos = AdBreakCompiler.Promos([AdBreak()], new TemporalTagOptions());
        MediaItem question = Clip("d:/media/commercials/any channel/You're Watching/QUIZ 1 question.mp4");

        Assert.Equal($@"{Root}\Back To The Show\quiz 1 answer.mp4", PromoPairing.PartnerOf(promos, AdPromoPairKind.Break, question));
        Assert.True(PromoPairing.IsFirst(promos, AdPromoPairKind.Break, question));

        // The kinds are kept apart: a break pair's question isn't a show pair's.
        Assert.Null(PromoPairing.PartnerOf(promos, AdPromoPairKind.Show, question));
    }

    [Fact]
    public void An_answer_is_known_as_one_so_it_never_plays_alone()
    {
        PromoConfig promos = AdBreakCompiler.Promos([AdBreak()], new TemporalTagOptions());

        Assert.True(PromoPairing.IsSecond(promos, AdPromoPairKind.Break, Clip($@"{Root}\Back To The Show\quiz 1 answer.mp4")));
        Assert.True(PromoPairing.IsSecond(promos, AdPromoPairKind.Show, Clip($@"{Root}\This Has Been\part 2.mp4")));
        Assert.False(PromoPairing.IsSecond(promos, AdPromoPairKind.Break, Clip($@"{Root}\Back To The Show\generic.mp4")));
    }

    [Fact]
    public void Pairs_survive_the_schedule_file()
    {
        SchedulerConfig config = new();
        config.AdBreaks["Any channel"] = AdBreak();

        SchedulerConfig read = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        List<AdPromoPair> pairs = read.AdBreaks["Any channel"].PromoPairs;

        Assert.Equal(3, pairs.Count);
        Assert.Equal(AdPromoPairKind.Show, pairs[1].Kind);
        Assert.Equal($@"{Root}\This Has Been\part 2.mp4", pairs[1].Second);
    }
}
