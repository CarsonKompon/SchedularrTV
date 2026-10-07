using System.Text.Json;

namespace SchedularrTV.Streamers;

/// <summary>
/// The streamer channels (<c>plugin-data/streamers/channels.json</c>) and every streamer's past streams
/// (<c>history.json</c>), which the predictions are made from.
/// </summary>
public sealed class StreamerStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>How far back streams are kept: enough for a weekly pattern, not so long an old one outweighs the new.</summary>
    public static readonly TimeSpan HistoryKept = TimeSpan.FromDays(70);

    private readonly string _channelsPath;
    private readonly string _historyPath;
    private readonly Lock _gate = new();
    private IReadOnlyList<StreamerChannel> _channels;
    private Dictionary<string, List<StreamSession>> _history;

    public StreamerStore(string dataDirectory)
    {
        _channelsPath = Path.Combine(dataDirectory, "channels.json");
        _historyPath = Path.Combine(dataDirectory, "history.json");
        List<StreamerChannel> loaded = Load<List<StreamerChannel>>(_channelsPath) ?? [];
        foreach (OfflineOptions? offline in loaded.Select(channel => channel.Offline))
        {
            // An earlier version's music folder is shown, and can be changed, as the channel's music.
            if (offline is not null)
            {
                offline.Music = SchedularrTV.Core.Media.BackgroundMusic.Tidy(offline.Music, offline.MusicFolder);
                offline.MusicFolder = null;
            }
        }

        _channels = loaded;
        _history = new Dictionary<string, List<StreamSession>>(
            Load<Dictionary<string, List<StreamSession>>>(_historyPath) ?? new Dictionary<string, List<StreamSession>>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Raised after the channels are saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<StreamerChannel> Channels
    {
        get
        {
            lock (_gate)
            {
                return _channels;
            }
        }
    }

    public StreamerChannel? Find(string id) =>
        Channels.FirstOrDefault(channel => string.Equals(channel.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every channel; ids are made unique and values tidied. Throws if the file can't be written.</summary>
    public void Save(IEnumerable<StreamerChannel> channels)
    {
        List<StreamerChannel> saved = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase) { "preview" };
        foreach (StreamerChannel channel in channels)
        {
            StreamerChannel copy = channel.Copy();
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "Streamers" : copy.Name.Trim();
            string id = string.IsNullOrWhiteSpace(copy.Id) ? StreamerChannel.Slug(copy.Name) : copy.Id;
            string unique = id;
            for (int suffix = 2; !ids.Add(unique); suffix++)
            {
                unique = $"{id}-{suffix}";
            }

            copy.Id = unique;
            Tidy(copy);
            saved.Add(copy);
        }

        Write(_channelsPath, saved);
        lock (_gate)
        {
            _channels = saved;
        }

        Changed?.Invoke();
    }

    /// <summary>Keeps every value in a range the page and the stream can use; streamers that can't be found are dropped.</summary>
    public static void Tidy(StreamerChannel channel)
    {
        List<StreamerEntry> streamers = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (StreamerEntry streamer in channel.Streamers)
        {
            streamer.Handle = StreamerHandles.Normalize(streamer.Platform, streamer.Handle);
            streamer.DisplayName = string.IsNullOrWhiteSpace(streamer.DisplayName) ? null : streamer.DisplayName.Trim();
            if (streamer.Handle.Length > 0 && seen.Add(streamer.Key))
            {
                streamers.Add(streamer);
            }
        }

        channel.Streamers = streamers;
        channel.Number = Math.Clamp(channel.Number, 1, 99_999);
        channel.Group = string.IsNullOrWhiteSpace(channel.Group) ? null : channel.Group.Trim();
        channel.Logo = string.IsNullOrWhiteSpace(channel.Logo) ? null : channel.Logo.Trim();
        channel.Volume = Math.Clamp(channel.Volume, 0, 2);

        ChatOptions chat = channel.Chat;
        chat.Box = chat.Box.Clamped();
        chat.FontSize = Math.Clamp(chat.FontSize, 10, 48);
        chat.BackgroundOpacity = Math.Clamp(chat.BackgroundOpacity, 0, 1);
        chat.MaxMessages = Math.Clamp(chat.MaxMessages, 1, 60);
        chat.FadeAfterSeconds = Math.Clamp(chat.FadeAfterSeconds, 0, 3600);
        chat.Template = string.IsNullOrWhiteSpace(chat.Template) ? ChatOptions.DefaultTemplate : chat.Template.Trim();

        AlsoLiveOptions also = channel.AlsoLive;
        also.EveryMinutes = Math.Clamp(also.EveryMinutes, 1, 240);
        also.ShowSeconds = Math.Clamp(also.ShowSeconds, 3, 120);
        also.Text = string.IsNullOrWhiteSpace(also.Text) ? new AlsoLiveOptions().Text : also.Text.Trim();

        OfflineOptions offline = channel.Offline;
        offline.Heading = string.IsNullOrWhiteSpace(offline.Heading) ? "Next stream" : offline.Heading.Trim();
        offline.Message = string.IsNullOrWhiteSpace(offline.Message) ? null : offline.Message.Trim();
        offline.AccentColor = IsColour(offline.AccentColor) ? offline.AccentColor.ToLowerInvariant() : "#9146ff";
        offline.BackgroundColor = IsColour(offline.BackgroundColor) ? offline.BackgroundColor.ToLowerInvariant() : "#0e0e14";
        offline.Music = SchedularrTV.Core.Media.BackgroundMusic.Tidy(offline.Music, offline.MusicFolder);
        offline.MusicFolder = null;
        offline.MusicVolume = Math.Clamp(offline.MusicVolume, 0, 2);
        offline.ReplayNoteEveryMinutes = Math.Clamp(offline.ReplayNoteEveryMinutes, 1, 120);
        offline.ReplayNoteSeconds = Math.Clamp(offline.ReplayNoteSeconds, 3, 120);
        offline.ReplayFrame = StreamerLayout.ReplayBox(offline.ReplayFrame);
    }

    public static bool IsColour(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>A streamer's known streams, oldest first.</summary>
    public IReadOnlyList<StreamSession> History(string key)
    {
        lock (_gate)
        {
            return _history.TryGetValue(key, out List<StreamSession>? sessions) ? [.. sessions] : [];
        }
    }

    /// <summary>
    /// Adds streams (from the platform's list of past broadcasts, or seen here) to a streamer's history. A stream that
    /// starts within a few minutes of one already known is the same stream; the longer length is kept. Returns true
    /// when anything changed.
    /// </summary>
    public bool Record(string key, IEnumerable<StreamSession> sessions, DateTimeOffset now)
    {
        bool changed;
        lock (_gate)
        {
            List<StreamSession> known = _history.TryGetValue(key, out List<StreamSession>? existing) ? existing : [];
            changed = Merge(known, sessions, now - HistoryKept);
            if (known.Count > 0)
            {
                _history[key] = known;
            }
        }

        if (changed)
        {
            SaveHistory();
        }

        return changed;
    }

    /// <summary>Merges <paramref name="incoming"/> into <paramref name="known"/> (kept sorted), dropping streams older than <paramref name="since"/>.</summary>
    public static bool Merge(List<StreamSession> known, IEnumerable<StreamSession> incoming, DateTimeOffset since)
    {
        bool changed = known.RemoveAll(session => session.Start < since) > 0;
        foreach (StreamSession session in incoming)
        {
            if (session.Start < since || session.Length < TimeSpan.Zero)
            {
                continue;
            }

            int same = known.FindIndex(candidate => (candidate.Start - session.Start).Duration() <= TimeSpan.FromMinutes(20));
            if (same < 0)
            {
                known.Add(session);
                changed = true;
            }
            else if (session.Length > known[same].Length + TimeSpan.FromMinutes(1) || (known[same].Title is null && session.Title is not null)
                     || (session.VideoId is not null && known[same].VideoId != session.VideoId))
            {
                // A recording's length is the stream's as it really was, so it wins over one seen while live.
                known[same] = known[same] with
                {
                    Length = session.VideoId is not null && session.Length > TimeSpan.Zero ? session.Length
                        : session.Length > known[same].Length ? session.Length : known[same].Length,
                    Title = known[same].Title ?? session.Title,
                    VideoId = session.VideoId ?? known[same].VideoId
                };
                changed = true;
            }
        }

        if (changed)
        {
            known.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return changed;
    }

    private void SaveHistory()
    {
        Dictionary<string, List<StreamSession>> copy;
        lock (_gate)
        {
            copy = _history.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Write(_historyPath, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; written again with the next change.
        }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
