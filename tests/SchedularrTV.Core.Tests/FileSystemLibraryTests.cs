using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Providers.FileSystem;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class FileSystemLibraryTests
{
    private const string Root = @"D:\Media\Commercials";

    private static MediaFolderOptions Folder(bool tagFromFolders = true, int? maxDepth = null) => new()
    {
        Path = Root,
        Tags = ["Commercials"],
        TagFromFolders = tagFromFolders,
        MaxTagDepth = maxDepth
    };

    [Fact]
    public void Every_folder_between_the_root_and_the_file_becomes_a_tag()
    {
        List<string> tags = FileSystemMediaLibrary.BuildTags(
            Root,
            Path.Combine(Root, "Adult Swim", "Bumpers", "Any Time", "clip.mp4"),
            Folder());

        Assert.Equal(["Commercials", "Adult Swim", "Bumpers", "Any Time"], tags);
    }

    [Fact]
    public void Nested_folders_below_the_named_levels_are_also_tagged()
    {
        List<string> tags = FileSystemMediaLibrary.BuildTags(
            Root,
            Path.Combine(Root, "Adult Swim", "Ads", "Any Time", "2002_12_00", "clip.mkv"),
            Folder());

        Assert.Contains("2002_12_00", tags);
    }

    [Fact]
    public void A_file_directly_in_the_root_only_gets_the_root_tags()
    {
        List<string> tags = FileSystemMediaLibrary.BuildTags(Root, Path.Combine(Root, "clip.mp4"), Folder());

        Assert.Equal(["Commercials"], tags);
    }

    [Fact]
    public void Folder_tagging_can_be_limited_by_depth()
    {
        List<string> tags = FileSystemMediaLibrary.BuildTags(
            Root,
            Path.Combine(Root, "Adult Swim", "Bumpers", "Any Time", "clip.mp4"),
            Folder(maxDepth: 2));

        Assert.Equal(["Commercials", "Adult Swim", "Bumpers"], tags);
    }

    [Fact]
    public void Folder_tagging_can_be_disabled()
    {
        List<string> tags = FileSystemMediaLibrary.BuildTags(
            Root,
            Path.Combine(Root, "Adult Swim", "Bumpers", "clip.mp4"),
            Folder(tagFromFolders: false));

        Assert.Equal(["Commercials"], tags);
    }

    [Fact]
    public void A_duplicate_folder_name_is_not_tagged_twice()
    {
        List<string> tags = FileSystemMediaLibrary.BuildTags(
            Root,
            Path.Combine(Root, "Commercials", "Ads", "clip.mp4"),
            Folder());

        Assert.Equal(["Commercials", "Ads"], tags);
    }

    [Fact]
    public void The_legacy_style_query_matches_the_folder_hierarchy()
    {
        var item = new MediaItem
        {
            Id = "fs:1",
            Title = "clip",
            Kind = MediaKind.Filler,
            Duration = TimeSpan.FromSeconds(30),
            Tags = FileSystemMediaLibrary.BuildTags(
                Root,
                Path.Combine(Root, "Adult Swim", "Bumpers", "Any Time", "clip.mp4"),
                Folder())
        };

        QueryExpression matching = QueryParser.Parse(
            """
            tag:"Commercials" AND (tag:"Adult Swim" OR tag:"Any Channel" OR tag:"Any Adults")
            AND tag:"Bumpers" AND (tag:"Any Time" OR tag:"January" OR tag:"Winter")
            """);

        QueryExpression wrongType = QueryParser.Parse("""tag:"Commercials" AND tag:"Ads" """);
        QueryExpression wrongWhen = QueryParser.Parse("""tag:"Commercials" AND (tag:"January" OR tag:"Winter")""");

        Assert.True(QueryEvaluator.Matches(item, matching));
        Assert.False(QueryEvaluator.Matches(item, wrongType));
        Assert.False(QueryEvaluator.Matches(item, wrongWhen));
    }

    [Fact]
    public void The_levels_under_a_promos_folder_become_the_show_and_promo_type()
    {
        (List<string> tags, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "Promos", "Top Cat", "Up Next", "clip.mp4"),
            Folder());

        Assert.Equal(["Commercials", "Cartoon Network", "Promos"], tags);
        Assert.Equal("Top Cat", attributes["show_id"]);
        Assert.Equal("up_next", attributes["promo_type"]);
    }

    [Theory]
    [InlineData("Up Next", "up_next")]
    [InlineData("Later Today", "later_today")]
    [InlineData("Coming Up", "coming_up")]
    [InlineData("Coming Up At 4pm", "coming_up_at_4pm")]
    [InlineData("tonight", "tonight")]
    [InlineData("You're Watching", "youre_watching")]
    [InlineData("Youre Watching", "youre_watching")]
    [InlineData("Back To The Show", "back_to_the_show")]
    [InlineData("Back to the Show", "back_to_the_show")]
    public void A_promo_folder_reads_as_plain_english(string folderName, string expected) =>
        Assert.Equal(expected, FileSystemMediaLibrary.NormalisePromoType(folderName));

    [Fact]
    public void Folders_outside_a_promos_folder_are_untouched()
    {
        (List<string> tags, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "You're Watching", "Top Cat", "clip.mp4"),
            Folder());

        Assert.Equal(["Commercials", "Cartoon Network", "You're Watching", "Top Cat"], tags);
        Assert.Empty(attributes);
    }

    [Fact]
    public void A_key_value_folder_becomes_an_attribute_rather_than_a_tag()
    {
        (List<string> tags, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "show_id=Top Cat", "promo_type=up_next", "clip.mp4"),
            Folder());

        Assert.Equal(["Commercials", "Cartoon Network"], tags);
        Assert.Equal("Top Cat", attributes["show_id"]);
        Assert.Equal("up_next", attributes["promo_type"]);
    }

    [Fact]
    public void A_folder_whose_key_is_not_whitelisted_stays_a_tag()
    {
        (List<string> tags, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Season 1=2", "clip.mp4"),
            Folder());

        Assert.Contains("Season 1=2", tags);
        Assert.Empty(attributes);
    }

    [Fact]
    public void A_clock_folder_under_the_promo_type_becomes_the_promo_clock()
    {
        (_, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "Promos", "Top Cat", "Coming Up", "4pm", "clip.mp4"),
            Folder());

        Assert.Equal("Top Cat", attributes["show_id"]);
        Assert.Equal("coming_up", attributes["promo_type"]);
        Assert.Equal("4pm", attributes["promo_clock"]);
    }

    [Fact]
    public void A_promo_type_folder_with_no_clock_below_it_carries_no_clock()
    {
        (_, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "Promos", "Top Cat", "Coming Up", "clip.mp4"),
            Folder());

        Assert.False(attributes.ContainsKey("promo_clock"));
    }

    [Theory]
    [InlineData("4pm", "4pm")]
    [InlineData("8:30 AM", "830am")]
    [InlineData("830am", "830am")]
    public void A_clock_folder_reads_however_it_is_written(string folderName, string expected) =>
        Assert.Equal(expected, FileSystemMediaLibrary.NormaliseClock(folderName));

    [Fact]
    public void A_day_and_a_time_under_the_promo_type_read_as_the_airing()
    {
        (_, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "Promos", "Super Chunk", "Eventually", "Saturday", "400pm", "clip.mp4"),
            Folder());

        Assert.Equal("Super Chunk", attributes["show_id"]);
        Assert.Equal("eventually", attributes["promo_type"]);
        Assert.Equal("saturday", attributes["promo_day"]);
        Assert.Equal("400pm", attributes["promo_clock"]);
    }

    [Fact]
    public void A_day_folder_stands_on_its_own_when_no_time_follows_it()
    {
        (_, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "Promos", "Super Chunk", "Eventually", "Saturday", "clip.mp4"),
            Folder());

        Assert.Equal("saturday", attributes["promo_day"]);
        Assert.False(attributes.ContainsKey("promo_clock"));
    }

    [Theory]
    [InlineData("Saturday", true)]
    [InlineData("saturday", true)]
    [InlineData("4pm", false)]
    [InlineData("400pm", false)]
    // DayOfWeek is an enum, so a bare number would otherwise parse as Thursday.
    [InlineData("4", false)]
    public void A_day_folder_is_told_apart_from_a_time_one(string folderName, bool expected) =>
        Assert.Equal(expected, FileSystemMediaLibrary.TryReadDay(folderName, out _));

    [Fact]
    public void An_any_show_folder_is_stored_as_the_show()
    {
        (_, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Any Channel", "Promos", "Any Show", "Up Next", "clip.mp4"),
            Folder());

        Assert.Equal("Any Show", attributes["show_id"]);
    }

    [Fact]
    public void A_promo_filed_by_folder_matches_the_up_next_query()
    {
        (List<string> tags, Dictionary<string, string> attributes) = FileSystemMediaLibrary.Describe(
            Root,
            Path.Combine(Root, "Cartoon Network", "Promos", "Top Cat", "Up Next", "clip.mp4"),
            Folder());

        var promo = new MediaItem
        {
            Id = "fs:1",
            Title = "clip",
            Kind = MediaKind.Filler,
            Duration = TimeSpan.FromSeconds(15),
            Tags = tags,
            Attributes = attributes
        };

        QueryExpression upNext = Scheduling.InterstitialQueryBuilder.BuildUpNext(
            new Configuration.PromoConfig(),
            ["Top Cat"]);

        Assert.True(QueryEvaluator.Matches(promo, upNext));
    }
}
