namespace SchedularrTV.Streamers;

/// <summary>One past (or current) stream: when it started and how long it ran.</summary>
/// <param name="VideoId">Its recording (a Twitch past broadcast, a YouTube video), when there is one to replay.</param>
public sealed record StreamSession(DateTimeOffset Start, TimeSpan Length, string? Title = null, string? VideoId = null);

/// <summary>What a platform said about a streamer just now.</summary>
public sealed record StreamerSnapshot
{
    public required bool Live { get; init; }

    /// <summary>Twitch: the channel's numeric id, which BetterTTV, FrankerFaceZ and 7TV know it by.</summary>
    public string? UserId { get; init; }

    public string? DisplayName { get; init; }

    public string? Avatar { get; init; }

    public string? Title { get; init; }

    public string? Game { get; init; }

    public int? Viewers { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>YouTube: the live (or scheduled) video, for the stream and its chat.</summary>
    public string? VideoId { get; init; }

    /// <summary>A stream the streamer has announced (YouTube's upcoming stream, Twitch's schedule).</summary>
    public DateTimeOffset? Scheduled { get; init; }

    public string? ScheduledTitle { get; init; }
}

/// <summary>Everything known about a streamer: the latest check, and their streams so far.</summary>
public sealed class StreamerState
{
    public required string Key { get; init; }

    public required StreamPlatform Platform { get; init; }

    public required string Handle { get; init; }

    public StreamerSnapshot? Latest { get; set; }

    /// <summary>Live as far as the channel is concerned: going offline takes two checks in a row, so a hiccup doesn't switch away.</summary>
    public bool Live { get; set; }

    public int OfflineChecks { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public DateTimeOffset? HistoryAt { get; set; }

    /// <summary>Why the last check failed, if it did.</summary>
    public string? Problem { get; set; }

    public string Name(StreamerEntry? entry = null) =>
        entry?.DisplayName is { Length: > 0 } own ? own : Latest?.DisplayName is { Length: > 0 } given ? given : Handle.TrimStart('@');
}
