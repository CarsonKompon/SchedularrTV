using System.Globalization;
using System.Text.Json.Serialization;

namespace SchedularrTV.News;

/// <summary>
/// One segment of the rundown: what's on screen (<paramref name="Kind"/>, and which story or league), what's read out,
/// and when it starts and how long it runs, in milliseconds from the top of the rundown.
/// </summary>
/// <param name="Kind">intro, story, weather, markets, sports, map, lottery, outro or empty.</param>
/// <param name="Index">The story's index, for a story.</param>
/// <param name="Key">The league, for sports.</param>
public sealed record RundownItem(string Kind, int Index, string? Key, string Label, string Speech, long StartMs, long DurationMs)
{
    /// <summary>The voice-over (16-bit stereo 48 kHz PCM), when it's read out.</summary>
    [JsonIgnore]
    public string? Clip { get; init; }

    [JsonIgnore]
    public long ClipMs { get; init; }

    /// <summary>The voice starts this long into the segment, after the picture has changed.</summary>
    public const long LeadMs = 500;
}

/// <summary>A whole rundown and the data it was written from; it loops until the next one takes over.</summary>
public sealed record Rundown(long Version, NewsSnapshot Snapshot, IReadOnlyList<RundownItem> Items)
{
    public long LengthMs => Items.Count == 0 ? 0 : Items[^1].StartMs + Items[^1].DurationMs;

    public RundownItem? At(long ms)
    {
        foreach (RundownItem item in Items)
        {
            if (ms < item.StartMs + item.DurationMs)
            {
                return item;
            }
        }

        return null;
    }
}

/// <summary>
/// Writes a show's rundown from what was fetched: an intro, the stories with the data segments spread between them, and
/// an outro, each with the words to read for it.
/// </summary>
public static class NewsRundown
{
    public const long IntroMs = 6000;
    public const long OutroMs = 5000;

    /// <summary>The segments in order, without timings (see <see cref="Time"/>).</summary>
    public static IReadOnlyList<RundownItem> Compose(NewsShow show, NewsSnapshot snapshot)
    {
        List<RundownItem> desks = [];
        foreach (NewsDesk desk in show.Desks)
        {
            switch (desk)
            {
                case NewsDesk.Weather when snapshot.Weather is { } weather:
                    desks.Add(Item("weather", 0, null, $"Weather for {Short(weather.Place)}", WeatherScript(weather)));
                    break;
                case NewsDesk.Markets when snapshot.Quotes.Count > 0:
                    desks.Add(Item("markets", 0, null, "Markets", MarketsScript(snapshot.Quotes)));
                    break;
                case NewsDesk.Sports:
                    foreach (IGrouping<string, ScoreGame> league in snapshot.Scores.GroupBy(game => game.League))
                    {
                        desks.Add(Item("sports", 0, league.Key, $"{league.Key} scores", SportsScript(league.Key, [.. league])));
                    }

                    break;
                case NewsDesk.Map when snapshot.Map is { } map:
                    desks.Add(Item("map", 0, null, map.Traffic ? "Traffic" : "Radar", MapScript(map)));
                    break;
                case NewsDesk.Lottery when snapshot.Lottery.Count > 0:
                    desks.Add(Item("lottery", 0, null, "Lottery results", LotteryScript(snapshot.Lottery)));
                    break;
            }
        }

        List<RundownItem> items = [Item("intro", 0, null, show.Name, IntroScript(show))];
        int stories = snapshot.Stories.Count;
        if (stories == 0 && desks.Count == 0)
        {
            items.Add(Item("empty", 0, null, "No news", "There's no news to bring you right now."));
        }

        // A data segment after every few stories, the rest at the end.
        int every = desks.Count == 0 ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(stories / (double)(desks.Count + 1)));
        int nextDesk = 0;
        for (int index = 0; index < stories; index++)
        {
            Story story = snapshot.Stories[index];
            items.Add(Item("story", index, null, story.Title, StoryScript(story, show.Speech)));
            if ((index + 1) % every == 0 && nextDesk < desks.Count && index < stories - 1)
            {
                items.Add(desks[nextDesk++]);
            }
        }

        items.AddRange(desks.Skip(nextDesk));
        items.Add(Item("outro", 0, null, show.Name, OutroScript(show)));

        // Data segments can be left unread: they're shown for the show's own length, under the music.
        if (!show.ReadDesks)
        {
            items = [.. items.Select(item => item.Kind is "story" or "intro" or "outro" or "empty" ? item : item with { Speech = "" })];
        }

        return items;
    }

    /// <summary>
    /// Times the segments: each runs as long as its voice-over (with a moment before and after), or the show's own
    /// length for its kind when there's none.
    /// </summary>
    public static IReadOnlyList<RundownItem> Time(NewsShow show, IReadOnlyList<RundownItem> items)
    {
        List<RundownItem> timed = [];
        long at = 0;
        foreach (RundownItem item in items)
        {
            long standard = item.Kind switch
            {
                "intro" => IntroMs,
                "outro" => OutroMs,
                "story" or "empty" => show.SecondsPerStory * 1000L,
                _ => show.SecondsPerDesk * 1000L
            };
            long length = item.Clip is not null && item.ClipMs > 0
                ? Math.Max(item.ClipMs + RundownItem.LeadMs + 1200, 4000)
                : standard;
            // The map's tiles and the lottery's balls need a moment to settle on screen.
            if (item.Kind is "map" or "lottery" or "markets" or "sports" or "weather")
            {
                length = Math.Max(length, 8000);
            }

            timed.Add(item with { StartMs = at, DurationMs = length });
            at += length;
        }

        return timed;
    }

    private static RundownItem Item(string kind, int index, string? key, string label, string speech) => new(kind, index, key, label, speech, 0, 0);

    // --- What's read -----------------------------------------------------------------------------------------------

    public static string IntroScript(NewsShow show) =>
        show.Intro ?? $"This is {Speakable(show.Name)}. Here are the latest headlines.";

    public static string OutroScript(NewsShow show) =>
        show.Outro ?? "That's the news for now. Stay with us.";

    public static string StoryScript(Story story, SpeechMode mode)
    {
        string headline = Sentence(story.Title);
        string summary = story.Summary;
        return mode switch
        {
            SpeechMode.Headlines => headline,
            SpeechMode.FullSummary when summary.Length > 0 => $"{headline} {FeedReader.Sentences(summary, 99, 900)}",
            SpeechMode.HeadlineAndSummary or SpeechMode.Off when summary.Length > 0 => $"{headline} {FeedReader.Sentences(summary, 2, 280)}",
            _ => headline
        };
    }

    public static string WeatherScript(WeatherReport weather)
    {
        string place = Short(weather.Place);
        string now = $"It's {Degrees(weather.Temperature)} and {weather.Condition.ToLowerInvariant()}";
        string feels = Math.Abs(weather.FeelsLike - weather.Temperature) >= 3 ? $", feeling like {Degrees(weather.FeelsLike)}" : "";
        string today = weather.Days.Count > 0
            ? $" Today, a high of {Degrees(weather.Days[0].High)} and a low of {Degrees(weather.Days[0].Low)}."
            : "";
        string tomorrow = weather.Days.Count > 1 ? $" Tomorrow, {weather.Days[1].Condition.ToLowerInvariant()}, with a high of {Degrees(weather.Days[1].High)}." : "";
        return $"{(place.Length > 0 ? $"Now the weather in {place}. " : "Now the weather. ")}{now}{feels}.{today}{tomorrow}";
    }

    public static string MarketsScript(IReadOnlyList<Quote> quotes)
    {
        IEnumerable<string> lines = quotes.Take(4).Select(quote =>
        {
            string direction = quote.ChangePercent switch
            {
                > 0.05 => $"up {Percent(quote.ChangePercent)}",
                < -0.05 => $"down {Percent(-quote.ChangePercent)}",
                _ => "flat"
            };
            return $"the {Speakable(quote.Name)} is {direction} at {quote.Price.ToString("#,0.##", CultureInfo.InvariantCulture)}";
        });
        string joined = string.Join("; ", lines);
        return joined.Length == 0 ? "" : $"On the markets, {joined}.";
    }

    public static string SportsScript(string league, IReadOnlyList<ScoreGame> games)
    {
        List<string> lines = [];
        foreach (ScoreGame game in games.Take(4))
        {
            if (game.State == "pre")
            {
                lines.Add($"{game.Away.Name} at {game.Home.Name}, {SpokenStatus(game.Status)}");
                continue;
            }

            (ScoreTeam lead, ScoreTeam trail) = Score(game.Home) >= Score(game.Away) ? (game.Home, game.Away) : (game.Away, game.Home);
            bool final = game.State == "post";
            lines.Add(Score(lead) == Score(trail)
                ? $"{lead.Name} and {trail.Name} {(final ? "tied" : "are tied")}, {lead.Score} all"
                : $"{lead.Name} {(final ? "beat" : "lead")} {trail.Name}, {lead.Score} to {trail.Score}");
        }

        return lines.Count == 0 ? "" : $"In the {Speakable(league)}: {string.Join(". ", lines)}.";
    }

    public static string MapScript(MapReport map)
    {
        string place = Short(map.Place);
        if (map.Traffic)
        {
            int serious = map.Incidents.Count(incident => incident.Magnitude >= 3);
            TrafficIncident? worst = map.Incidents.FirstOrDefault();
            string count = map.Incidents.Count switch
            {
                0 => "Traffic is moving well, with no incidents reported",
                1 => "There's one traffic incident",
                var n => $"There are {n} traffic incidents{(serious > 0 ? $", {serious} of them serious" : "")}"
            };
            string detail = worst is null ? "" : $" Watch for {Speakable(worst.Category.ToLowerInvariant())}{(worst.Road.Length > 0 ? $" on {Speakable(worst.Road)}" : "")}.";
            return $"{count}{(place.Length > 0 ? $" around {place}" : "")}.{detail}";
        }

        return place.Length > 0 ? $"Here's the radar around {place}." : "Here's the latest radar.";
    }

    public static string LotteryScript(IReadOnlyList<LotteryDraw> draws)
    {
        IEnumerable<string> lines = draws.Select(draw =>
            $"{draw.Name}: {string.Join(", ", draw.Numbers)}{(draw.Bonus is { } bonus ? $", and the {draw.BonusName}, {bonus}" : "")}");
        return $"The latest lottery numbers. {string.Join(". ", lines)}.";
    }

    // --- Wording helpers ---------------------------------------------------------------------------------------------

    private static double Score(ScoreTeam team) =>
        double.TryParse(team.Score, NumberStyles.Float, CultureInfo.InvariantCulture, out double score) ? score : 0;

    private static string SpokenStatus(string status) => status.Replace(" - ", ", ", StringComparison.Ordinal);

    private static string Degrees(double value) => $"{value.ToString("0", CultureInfo.InvariantCulture)} degrees";

    private static string Percent(double value) => $"{value.ToString("0.0", CultureInfo.InvariantCulture)} percent";

    /// <summary>"Toronto, Ontario, Canada" → "Toronto".</summary>
    public static string Short(string place) => place.Split(',')[0].Trim();

    /// <summary>Ends with a full stop if it doesn't already end a sentence.</summary>
    private static string Sentence(string text) =>
        text.Length == 0 || ".!?…\"'".Contains(text[^1]) ? text : text + ".";

    /// <summary>Text a voice reads well: &amp; as "and", no symbols it would spell out.</summary>
    public static string Speakable(string text) =>
        text.Replace("&", " and ", StringComparison.Ordinal).Replace("^", "", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal).Trim();
}
