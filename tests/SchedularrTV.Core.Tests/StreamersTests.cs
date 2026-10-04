using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.LiveCapture;
using SchedularrTV.Plugins;
using SchedularrTV.Streamers;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class StreamersTests : IDisposable
{
    // A Wednesday.
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "streamers-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private StreamerSettings Settings() =>
        new(new FakeContext(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5252" }).Build(), _folder));

    // --- Who ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StreamPlatform.Twitch, "xQc", "xqc")]
    [InlineData(StreamPlatform.Twitch, "https://www.twitch.tv/Some_One/videos", "some_one")]
    [InlineData(StreamPlatform.Twitch, "@shroud", "shroud")]
    [InlineData(StreamPlatform.Twitch, "not a name!", "")]
    [InlineData(StreamPlatform.YouTube, "@LinusTechTips", "@LinusTechTips")]
    [InlineData(StreamPlatform.YouTube, "LinusTechTips", "@LinusTechTips")]
    [InlineData(StreamPlatform.YouTube, "https://www.youtube.com/@LinusTechTips/streams", "@LinusTechTips")]
    [InlineData(StreamPlatform.YouTube, "https://youtube.com/channel/UCXuqSBlHAE6Xw-yeJA0Tunw", "channel/UCXuqSBlHAE6Xw-yeJA0Tunw")]
    [InlineData(StreamPlatform.YouTube, "UCXuqSBlHAE6Xw-yeJA0Tunw", "channel/UCXuqSBlHAE6Xw-yeJA0Tunw")]
    [InlineData(StreamPlatform.YouTube, "https://www.youtube.com/c/Somebody/live", "c/Somebody")]
    public void Streamers_can_be_typed_or_pasted(StreamPlatform platform, string input, string expected)
    {
        Assert.Equal(expected, StreamerHandles.Normalize(platform, input));
    }

    [Fact]
    public void Saving_drops_unknown_and_repeated_streamers_and_keeps_values_in_range()
    {
        var store = new StreamerStore(_folder);
        store.Save([new StreamerChannel
        {
            Name = "Friday Night",
            Number = 0,
            Streamers =
            [
                new StreamerEntry { Platform = StreamPlatform.Twitch, Handle = "https://twitch.tv/Shroud" },
                new StreamerEntry { Platform = StreamPlatform.Twitch, Handle = "shroud" },
                new StreamerEntry { Platform = StreamPlatform.YouTube, Handle = "??" },
                new StreamerEntry { Platform = StreamPlatform.YouTube, Handle = "@someone" }
            ],
            Chat = new ChatOptions { FontSize = 400, Template = " " },
            Offline = new OfflineOptions { AccentColor = "red;}</style>" }
        }]);

        StreamerChannel saved = Assert.Single(new StreamerStore(_folder).Channels);
        Assert.Equal("friday-night", saved.Id);
        Assert.Equal(1, saved.Number);
        Assert.Equal(new[] { "shroud", "@someone" }, saved.Streamers.Select(streamer => streamer.Handle));
        Assert.Equal(48, saved.Chat.FontSize);
        Assert.Equal(ChatOptions.DefaultTemplate, saved.Chat.Template);
        Assert.Equal("#9146ff", saved.Offline.AccentColor);
    }

    [Fact]
    public void The_same_stream_seen_twice_is_kept_once_with_its_longer_length()
    {
        List<StreamSession> known = [new(Now.AddHours(-5), TimeSpan.FromHours(1))];

        bool changed = StreamerStore.Merge(known,
            [new(Now.AddHours(-5).AddMinutes(4), TimeSpan.FromHours(3), "Title"), new(Now.AddDays(-100), TimeSpan.FromHours(2))],
            Now - StreamerStore.HistoryKept);

        Assert.True(changed);
        StreamSession session = Assert.Single(known);
        Assert.Equal((TimeSpan.FromHours(3), "Title"), (session.Length, session.Title));
    }

    // --- Predictions -------------------------------------------------------------------------------

    private static List<StreamSession> Saturdays(int weeks, int hour = 20) =>
        [.. Enumerable.Range(1, weeks).Select(week => new StreamSession(new DateTimeOffset(2026, 9, 26, hour, 0, 0, TimeSpan.Zero).AddDays(-7 * (week - 1)), TimeSpan.FromHours(3)))];

    [Fact]
    public void A_weekly_habit_predicts_the_next_one()
    {
        StreamPrediction prediction = StreamPredictor.Next(Saturdays(6), Now, TimeZoneInfo.Utc)!;

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero), prediction.At);
        Assert.Equal(PredictionKind.Weekly, prediction.Kind);
        Assert.Equal(TimeSpan.FromHours(3), prediction.Length);
        Assert.InRange(prediction.Confidence, 0.8, 1);
    }

    [Fact]
    public void A_late_night_stream_belongs_to_the_evening_it_carries_on_from()
    {
        // Saturdays just after midnight (so Sunday by the calendar) are still a Saturday-night habit.
        List<StreamSession> history = [.. Saturdays(5, hour: 0).Select(session => session with { Start = session.Start.AddDays(1).AddMinutes(30) })];

        StreamPrediction prediction = StreamPredictor.Next(history, Now, TimeZoneInfo.Utc)!;

        Assert.Equal(new DateTimeOffset(2026, 10, 4, 0, 30, 0, TimeSpan.Zero), prediction.At);
    }

    [Fact]
    public void A_slot_already_streamed_in_is_not_predicted_again()
    {
        List<StreamSession> history = [.. Saturdays(5), new(new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(50))];

        StreamPrediction prediction = StreamPredictor.Next(history, new DateTimeOffset(2026, 10, 3, 19, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc)!;

        Assert.Equal(new DateTimeOffset(2026, 10, 10, 20, 0, 0, TimeSpan.Zero), prediction.At);
    }

    [Fact]
    public void An_announced_stream_beats_a_later_habit()
    {
        DateTimeOffset announced = Now.AddHours(5);

        StreamPrediction prediction = StreamPredictor.Next(Saturdays(6), Now, TimeZoneInfo.Utc, announced)!;

        Assert.Equal((announced, PredictionKind.Scheduled, 1.0), (prediction.At, prediction.Kind, prediction.Confidence));
    }

    [Fact]
    public void A_habit_well_before_an_announced_stream_is_expected_first()
    {
        // Saturdays at 8pm as usual; next Tuesday's special announced.
        DateTimeOffset announced = new(2026, 10, 6, 18, 0, 0, TimeSpan.Zero);

        StreamPrediction first = StreamPredictor.Next(Saturdays(6), Now, TimeZoneInfo.Utc, announced)!;
        Assert.Equal((new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero), PredictionKind.Weekly), (first.At, first.Kind));

        // Announced for that Saturday a little earlier than usual: the same stream, at the announced time.
        DateTimeOffset early = new(2026, 10, 3, 18, 30, 0, TimeSpan.Zero);
        StreamPrediction same = StreamPredictor.Next(Saturdays(6), Now, TimeZoneInfo.Utc, early)!;
        Assert.Equal((early, PredictionKind.Scheduled), (same.At, same.Kind));
    }

    [Fact]
    public void A_just_missed_start_is_still_expected_any_minute()
    {
        DateTimeOffset justAfter = new(2026, 10, 3, 20, 20, 0, TimeSpan.Zero);

        StreamPrediction prediction = StreamPredictor.Next(Saturdays(6), justAfter, TimeZoneInfo.Utc)!;

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero), prediction.At);
    }

    [Fact]
    public void Without_a_habit_the_usual_gap_is_the_guess()
    {
        List<StreamSession> history = [.. Enumerable.Range(0, 4).Select(index => new StreamSession(Now.AddDays(-13 + 3 * index), TimeSpan.FromHours(2)))];

        StreamPrediction prediction = StreamPredictor.Next(history, Now, TimeZoneInfo.Utc)!;

        // Every three days from the last (four days ago): yesterday's has gone by, so the day after tomorrow.
        Assert.Equal(PredictionKind.Interval, prediction.Kind);
        Assert.Equal(Now.AddDays(2), prediction.At);
    }

    [Fact]
    public void One_stream_is_not_enough_to_guess_from()
    {
        Assert.Null(StreamPredictor.Next([new StreamSession(Now.AddDays(-2), TimeSpan.FromHours(2))], Now, TimeZoneInfo.Utc));
    }

    // --- Twitch ------------------------------------------------------------------------------------

    [Fact]
    public void Twitch_chat_lines_become_messages_with_emotes_as_pictures()
    {
        var parsed = TwitchChat.Parse(
            @"@badge-info=;color=#1E90FF;display-name=Some\sOne;emotes=25:6-10;id=x :someone!someone@someone.tmi.twitch.tv PRIVMSG #chan :hello Kappa !")!.Value;

        Assert.Equal(("Some One", "#1E90FF"), (parsed.Author, parsed.Color));
        Assert.Equal(3, parsed.Parts.Count);
        Assert.Equal("hello ", parsed.Parts[0].Text);
        Assert.Equal(("Kappa", "https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/2.0"), (parsed.Parts[1].Text, parsed.Parts[1].Image));
        Assert.Equal(" !", parsed.Parts[2].Text);
    }

    [Fact]
    public void Twitch_counts_emote_positions_in_characters_not_utf16()
    {
        IReadOnlyList<ChatPart> parts = TwitchChat.Emotes("😀 Kappa", "25:2-6");

        Assert.Equal(new[] { "😀 ", "Kappa" }, parts.Select(part => part.Text));
        Assert.NotNull(parts[1].Image);
    }

    [Fact]
    public void BetterTtv_FrankerFaceZ_and_7tv_emotes_are_read_from_their_public_lists()
    {
        using JsonDocument bttvGlobal = JsonDocument.Parse("""[{"id":"54fa8f1401e468494b85b537","code":":tf:"}]""");
        using JsonDocument bttvUser = JsonDocument.Parse("""{"channelEmotes":[{"id":"a1","code":"catJAM"}],"sharedEmotes":[{"id":"b2","code":"PepeLaugh"}]}""");
        using JsonDocument ffz = JsonDocument.Parse("""{"room":{"set":9},"sets":{"9":{"emoticons":[{"name":"OMEGALUL","urls":{"1":"https://cdn.frankerfacez.com/emote/1/1","2":"https://cdn.frankerfacez.com/emote/1/2"}},{"name":"Old","urls":{"1":"//cdn.frankerfacez.com/emote/2/1"}}]}}}""");
        using JsonDocument seventv = JsonDocument.Parse("""{"emote_set":{"emotes":[{"name":"Clap","data":{"host":{"url":"//cdn.7tv.app/emote/x9"}}}]}}""");

        Assert.Equal((":tf:", "https://cdn.betterttv.net/emote/54fa8f1401e468494b85b537/2x"), Assert.Single(ThirdPartyEmotes.BetterTtv(bttvGlobal.RootElement)));
        Assert.Equal(new[] { "catJAM", "PepeLaugh" }, ThirdPartyEmotes.BetterTtv(bttvUser.RootElement).Select(emote => emote.Name));
        Assert.Equal(new[] { "https://cdn.frankerfacez.com/emote/1/2", "https://cdn.frankerfacez.com/emote/2/1" },
            ThirdPartyEmotes.FrankerFaceZ(ffz.RootElement).Select(emote => emote.Url));
        Assert.Equal(("Clap", "https://cdn.7tv.app/emote/x9/2x.webp"), Assert.Single(ThirdPartyEmotes.SevenTv(seventv.RootElement)));
    }

    [Fact]
    public void Third_party_emote_names_become_pictures_without_touching_the_rest()
    {
        var emotes = new Dictionary<string, string> { ["catJAM"] = "https://cdn/cat", ["Clap"] = "https://cdn/clap" };
        IReadOnlyList<ChatPart> parts = [new("so good catJAM  catJAMs "), new("Kappa", "https://twitch/kappa"), new(" Clap")];

        IReadOnlyList<ChatPart> shown = ThirdPartyEmotes.Apply(parts, emotes);

        Assert.Equal(new[] { "so good ", "catJAM", "  catJAMs ", "Kappa", " ", "Clap" }, shown.Select(part => part.Text));
        Assert.Equal(new[] { null, "https://cdn/cat", null, "https://twitch/kappa", null, "https://cdn/clap" }, shown.Select(part => part.Image));
    }

    [Theory]
    [InlineData("PING :tmi.twitch.tv")]
    [InlineData(":tmi.twitch.tv 001 justinfan123 :Welcome, GLHF!")]
    [InlineData("@emote-only=0 :tmi.twitch.tv ROOMSTATE #chan")]
    public void Twitch_lines_that_are_not_messages_are_ignored(string line)
    {
        Assert.Null(TwitchChat.Parse(line));
    }

    [Fact]
    public void Twitch_says_who_is_live_and_their_past_broadcasts()
    {
        using JsonDocument live = JsonDocument.Parse("""
            {"data":{"user":{"id":"37402112","login":"shroud","displayName":"shroud","profileImageURL":"https://img/150.png",
              "broadcastSettings":{"title":"Ranked grind","game":{"displayName":"Valorant"}},
              "stream":{"id":"1","createdAt":"2026-09-30T10:00:00Z","viewersCount":12345,"game":{"displayName":"Valorant"}},
              "lastBroadcast":{"startedAt":"2026-09-30T10:00:00Z","title":"Ranked grind"},
              "videos":{"edges":[{"node":{"id":"2250001","createdAt":"2026-09-28T18:00:00Z","lengthSeconds":10800,"title":"Old"}}]}}}}
            """);
        JsonElement user = TwitchClient.User(live.RootElement)!.Value;

        StreamerSnapshot snapshot = TwitchClient.ParseStatus(user);
        Assert.True(snapshot.Live);
        Assert.Equal("37402112", snapshot.UserId);
        Assert.Equal(("Ranked grind", "Valorant", 12345), (snapshot.Title, snapshot.Game, snapshot.Viewers));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), snapshot.StartedAt);
        StreamSession past = Assert.Single(TwitchClient.ParseVideos(user));
        Assert.Equal((TimeSpan.FromHours(3), "2250001"), (past.Length, past.VideoId));

        using JsonDocument missing = JsonDocument.Parse("""{"data":{"user":null}}""");
        Assert.Null(TwitchClient.User(missing.RootElement));
        using JsonDocument broken = JsonDocument.Parse("""{"errors":[{"message":"service timeout"}]}""");
        Assert.Contains("service timeout", Assert.Throws<InvalidOperationException>(() => TwitchClient.User(broken.RootElement)).Message, StringComparison.Ordinal);
    }

    // --- YouTube -----------------------------------------------------------------------------------

    [Fact]
    public void A_live_youtube_page_gives_the_stream_even_with_braces_in_its_title()
    {
        const string html = """
            <html><head><meta property="og:title" content="A Channel"></head><script>var ytInitialPlayerResponse = {"videoDetails":{"videoId":"abcdefghijk",
            "title":"A } tricky \" title","isLive":true,"author":"A Channel","viewCount":"420"},"microformat":{"playerMicroformatRenderer":
            {"liveBroadcastDetails":{"isLiveNow":true,"startTimestamp":"2026-09-30T11:00:00+00:00"}}}};var other = {};</script>
            "videoOwnerRenderer":{"thumbnail":{"thumbnails":[{"url":"https://yt3.example/avatar=.jpg"
            """;

        StreamerSnapshot snapshot = YouTubeClient.ParseLivePage(html);

        Assert.True(snapshot.Live);
        Assert.Equal(("abcdefghijk", "A } tricky \" title", "A Channel", 420), (snapshot.VideoId, snapshot.Title, snapshot.DisplayName, snapshot.Viewers));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 11, 0, 0, TimeSpan.Zero), snapshot.StartedAt);
        Assert.Equal("https://yt3.example/avatar=.jpg", snapshot.Avatar);
    }

    [Fact]
    public void An_upcoming_youtube_stream_is_its_announced_time()
    {
        const string html = """
            <script>var ytInitialPlayerResponse = {"videoDetails":{"videoId":"zzzzzzzzzzz","title":"Big announcement","isUpcoming":true,"author":"A Channel"},
            "microformat":{"playerMicroformatRenderer":{"liveBroadcastDetails":{"isLiveNow":false,"startTimestamp":"2026-10-02T19:00:00+00:00"}}}};</script>
            """;

        StreamerSnapshot snapshot = YouTubeClient.ParseLivePage(html);

        Assert.False(snapshot.Live);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.Zero), snapshot.Scheduled);
        Assert.Equal("Big announcement", snapshot.ScheduledTitle);
    }

    [Fact]
    public void A_youtube_channel_that_is_not_live_still_gives_its_name_and_picture()
    {
        StreamerSnapshot snapshot = YouTubeClient.ParseLivePage(
            """<meta property="og:title" content="Tom &amp; Co"><meta property="og:image" content="https://yt3.example/me.jpg">""");

        Assert.False(snapshot.Live);
        Assert.Equal(("Tom & Co", "https://yt3.example/me.jpg"), (snapshot.DisplayName, snapshot.Avatar));
    }

    [Fact]
    public void A_past_youtube_stream_has_its_start_and_length()
    {
        StreamSession session = YouTubeClient.ParseWatchPage("""
            <script>var ytInitialPlayerResponse = {"videoDetails":{"title":"Last week","lengthSeconds":"9000"},"microformat":{"playerMicroformatRenderer":
            {"liveBroadcastDetails":{"isLiveNow":false,"startTimestamp":"2026-09-26T20:00:00+00:00","endTimestamp":"2026-09-26T23:00:00+00:00"}}}};</script>
            """)!;

        Assert.Equal((new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(3), "Last week"), (session.Start, session.Length, session.Title));
        Assert.Null(YouTubeClient.ParseWatchPage("""<script>var ytInitialPlayerResponse = {"videoDetails":{"title":"Just a video"}};</script>"""));
    }

    [Fact]
    public void Youtube_chat_messages_and_super_chats_are_read_with_their_emoji()
    {
        using JsonDocument json = JsonDocument.Parse("""
            {"actions":[
              {"addChatItemAction":{"item":{"liveChatTextMessageRenderer":{"authorName":{"simpleText":"@viewer"},
                "message":{"runs":[{"text":"hi "},{"emoji":{"emojiId":"😀","shortcuts":[":grinning:"]}},{"emoji":{"emojiId":"UCx/abc","isCustomEmoji":true,
                  "shortcuts":[":yt:"],"image":{"thumbnails":[{"url":"https://yt3.example/small"},{"url":"https://yt3.example/big"}]}}}]}}}}},
              {"addChatItemAction":{"item":{"liveChatPaidMessageRenderer":{"authorName":{"simpleText":"fan"},"purchaseAmountText":{"simpleText":"$5.00"},
                "message":{"runs":[{"text":"love the stream"}]}}}}},
              {"addLiveChatTickerItemAction":{}}],
             "continuations":[{"timedContinuationData":{"continuation":"NEXT","timeoutMs":3500}}]}
            """);

        var messages = YouTubeChat.Messages(json.RootElement.GetProperty("actions")).ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("viewer", messages[0].Author);
        Assert.Equal(new[] { "hi ", "😀", ":yt:" }, messages[0].Parts.Select(part => part.Text));
        Assert.Equal("https://yt3.example/big", messages[0].Parts[2].Image);
        Assert.Equal(("fan", "$5.00"), (messages[1].Author, messages[1].Highlight));
        Assert.Equal(("NEXT", 3500), YouTubeChat.Continuation(json.RootElement.GetProperty("continuations")));
    }

    // --- The channel -------------------------------------------------------------------------------

    [Fact]
    public void The_guide_puts_whoever_is_live_first_and_fills_round_them_by_priority()
    {
        var channel = new StreamerChannel { Id = "fri", Name = "Friday", Number = 900 };
        GuideSlot[] slots =
        [
            new(Now.AddMinutes(-30), Now.AddHours(2), "A: live", null, -1),
            new(Now.AddHours(1), Now.AddHours(3), "B (expected)", null, 0),
            new(Now.AddHours(2.5), Now.AddHours(4), "C (expected)", null, 1),
            new(Now.AddHours(3.5), Now.AddHours(3.6), "D (expected)", null, 2)
        ];

        ChannelSchedule schedule = StreamerChannels.Build(channel, "http://localhost:5252/plugins/streamers/live/fri.ts", slots, Now, Now.AddHours(12),
            "https://img.example/top-streamer.png");

        Assert.Equal(Now.AddMinutes(-30), schedule.Start);
        Assert.Equal(new[] { "A: live", "B (expected)", "C (expected)", "Offline" }, schedule.Entries.Select(entry => entry.Item!.Title));
        Assert.Equal(Now.AddHours(2), schedule.Entries[1].Start);
        Assert.Equal(Now.AddHours(3), schedule.Entries[2].Start);
        Assert.All(schedule.Entries.Zip(schedule.Entries.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
        Assert.Equal(Now.AddHours(12), schedule.Entries[^1].End);
        Assert.All(schedule.Entries, entry =>
        {
            Assert.Equal("http://localhost:5252/plugins/streamers/live/fri.ts", entry.Item!.Path);
            Assert.Equal("true", entry.Item.Attributes["ersatztv.live"]);
        });
        Assert.Equal((900, "streamers.fri", "https://img.example/top-streamer.png"), (schedule.ChannelNumber, schedule.ChannelTvgId, schedule.ChannelLogo));
    }

    [Fact]
    public void Live_the_page_is_drawn_over_the_stream_and_offline_it_is_the_picture()
    {
        var channel = new StreamerChannel { Id = "fri", Name = "Friday", Number = 900, Volume = 0.8, Chat = new ChatOptions { Enabled = true, Layout = ChatLayout.PanelRight } };

        LiveStreamSpec live = StreamerPage.Spec(channel, new ChannelOnAir("twitch:a", "https://video.example/live.m3u8", Now, null), Settings());
        List<string> arguments = [.. LiveStream.Arguments(live, null)];
        string graph = arguments[arguments.IndexOf("-filter_complex") + 1];

        Assert.True(live.PageOnTop);
        Assert.Equal(("https://video.example/live.m3u8", LiveAudio.Stream, 0.8), (live.WindowStreamUrl, live.Audio, live.Volume));
        Assert.Equal(new FrameRect(0, 90, 960, 540), live.Window);
        Assert.StartsWith("http://localhost:5252/plugins/streamers/view/fri?v=", live.PageUrl, StringComparison.Ordinal);
        Assert.Equal("png", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.Contains("[base][window]overlay=0:90", graph, StringComparison.Ordinal);
        Assert.Contains("[windowed][pagetop]overlay=0:0", graph, StringComparison.Ordinal);

        LiveStreamSpec offline = StreamerPage.Spec(channel, null, Settings());
        Assert.False(offline.PageOnTop);
        Assert.Null(offline.WindowStreamUrl);
        Assert.Equal(LiveAudio.Silence, offline.Audio);
        Assert.Equal("mjpeg", LiveStream.Arguments(offline, null).SkipWhile(argument => argument != "-c:v").Skip(1).First());

        // A change to how the page looks gives it a new address, so a running channel restarts with it.
        channel.Chat.Style = ChatStyle.Bubbles;
        Assert.NotEqual(offline.PageUrl, StreamerPage.Spec(channel, null, Settings()).PageUrl);
    }

    [Fact]
    public void A_replay_plays_from_where_it_has_got_to_under_the_page()
    {
        var channel = new StreamerChannel { Id = "fri", Name = "Friday", Number = 900, Offline = new OfflineOptions { Replay = ReplayStyle.Framed } };
        var recording = new StreamSession(Now.AddDays(-1), TimeSpan.FromHours(3), "Last night", "v123");
        var onAir = new ChannelOnAir(null, "https://vod.example/video.m3u8", Now, null)
        {
            ReplayKey = "twitch:a",
            Replay = recording,
            ReplayFrom = DateTimeOffset.UtcNow - TimeSpan.FromHours(4),
            AudioUrl = "https://vod.example/audio.m4a"
        };

        LiveStreamSpec spec = StreamerPage.Spec(channel, onAir, Settings());
        List<string> arguments = [.. LiveStream.Arguments(spec, null)];

        Assert.True(spec.PageOnTop && spec.Recorded);
        Assert.Equal(new FrameRect(40, 104, 864, 486), spec.Window);
        Assert.Equal("https://vod.example/audio.m4a", spec.AudioStreamUrl);
        Assert.InRange(spec.SeekTo!.Value, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
        int window = arguments.IndexOf("https://vod.example/video.m3u8");
        Assert.Equal("-ss", arguments[window - 5]);
        Assert.DoesNotContain("-re", arguments);
        Assert.Contains("https://vod.example/audio.m4a", arguments);

        // Where it starts isn't a change of settings, but starting over is.
        Assert.Equal(spec.Fingerprint, (spec with { SeekTo = TimeSpan.Zero }).Fingerprint);
        Assert.NotEqual(spec.Fingerprint, StreamerPage.Spec(channel, onAir with { Round = 1 }, Settings()).Fingerprint);

        channel.Offline.Replay = ReplayStyle.Popup;
        Assert.Equal(new FrameRect(0, 0, 1280, 720), StreamerPage.Spec(channel, onAir, Settings()).Window);
        Assert.Equal(TimeSpan.FromHours(1), onAir.ReplayPosition(onAir.ReplayFrom + TimeSpan.FromHours(7)));
    }

    [Theory]
    [InlineData(40, 104, 864, 40, 104, 864, 486)]
    [InlineData(500, 600, 641, 500, 360, 640, 360)]
    [InlineData(-10, 0, 99999, 0, 0, 1280, 720)]
    [InlineData(1200, 0, 100, 960, 0, 320, 180)]
    public void A_framed_replay_is_16_by_9_even_sized_and_on_the_picture(int x, int y, int width, int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new PixelBox(expectedX, expectedY, expectedWidth, expectedHeight), StreamerLayout.ReplayBox(new PixelBox(x, y, width, 1)));
    }

    [Fact]
    public void A_recording_found_later_gives_a_seen_stream_its_video_and_real_length()
    {
        List<StreamSession> known = [new(Now.AddHours(-5), TimeSpan.FromHours(4))];

        StreamerStore.Merge(known, [new(Now.AddHours(-5).AddMinutes(2), TimeSpan.FromHours(3.5), "Stream", "v9")], Now - StreamerStore.HistoryKept);

        StreamSession session = Assert.Single(known);
        Assert.Equal(("v9", TimeSpan.FromHours(3.5)), (session.VideoId, session.Length));
    }

    [Fact]
    public void The_page_carries_its_settings_without_letting_them_end_the_script()
    {
        var channel = new StreamerChannel
        {
            Id = "fri", Name = "</script><script>alert(1)</script>",
            Chat = new ChatOptions { Style = ChatStyle.Custom, Template = "<b>{author}</b></script>" }
        };

        string html = StreamerPage.Html(StreamerPageConfig.For(channel, preview: false));

        Assert.DoesNotContain("__CONFIG__", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</script><script>alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</b></script>", html, StringComparison.Ordinal);
        Assert.Contains("\"style\":\"Custom\"", html, StringComparison.Ordinal);
    }

    private sealed class FakeContext(IConfiguration app, string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "streamers", Name = "Streamer channels" };
        public string? PluginDirectory => null;
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration => new ConfigurationBuilder().Build();
        public IConfiguration AppConfiguration => app;
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/streamers/";
    }
}
