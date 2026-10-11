using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SchedularrTV.Service.Stats;

/// <summary>What aired on a channel in one sampled minute, and who was watching it.</summary>
/// <param name="Minute">Unix minutes (UTC).</param>
/// <param name="AiringStart">When the airing began (Unix seconds), so its minutes add up to one row.</param>
/// <param name="Kind">program, interstitial or break.</param>
public sealed record MinuteSample(
    long Minute,
    string ChannelId,
    long AiringStart,
    string Kind,
    string Show,
    string? Title,
    string? ItemId,
    int Viewers,
    IReadOnlyList<Viewer> Who);

/// <summary>One airing as recorded: how many of its minutes were sampled, watched, and by how many people at most.</summary>
public sealed record AiringRow(
    string ChannelId,
    long Start,
    string Kind,
    string Show,
    string? Title,
    string? ItemId,
    int AiredMinutes,
    int WatchedMinutes,
    int ViewerMinutes,
    int Peak);

/// <summary>A watched minute: how many were watching.</summary>
public sealed record WatchedMinute(long Minute, int Viewers);

/// <summary>Someone's (or a direct player's) minute on a channel.</summary>
public sealed record ViewerMinute(long Minute, string Name, string Via);

public sealed record ChannelViewingTotal(string ChannelId, long ViewerMinutes);

/// <summary>
/// The viewing history: a small SQLite database in the data folder (<c>stats/viewing.db</c>).
/// <list type="bullet">
/// <item><c>airings</c>: one row per programme, short show or ad that aired while the scheduler was running, with how
/// many of its minutes were sampled, how many were watched, the viewer-minutes and the peak.</item>
/// <item><c>watched</c>: each minute someone was watching a channel, and how many.</item>
/// <item><c>viewers</c>: who, in each watched minute (a Jellyfin or Plex user, or "Direct" for a player straight from ErsatzTV).</item>
/// </list>
/// Minutes nobody watched aren't stored one by one (only counted in their airing), so a channel left on its own costs
/// a row per programme, not one a minute.
/// </summary>
public sealed class ViewingStore
{
    private readonly string _connection;
    private readonly Lock _gate = new();

    public ViewingStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        using SqliteConnection db = Open();
        Execute(db, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS airings (
                channel TEXT NOT NULL, start INTEGER NOT NULL, kind TEXT NOT NULL, show TEXT NOT NULL, title TEXT, item TEXT,
                aired INTEGER NOT NULL DEFAULT 0, watched INTEGER NOT NULL DEFAULT 0, viewer_minutes INTEGER NOT NULL DEFAULT 0, peak INTEGER NOT NULL DEFAULT 0,
                last INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (channel, start));
            CREATE TABLE IF NOT EXISTS watched (
                minute INTEGER NOT NULL, channel TEXT NOT NULL, viewers INTEGER NOT NULL, airing INTEGER NOT NULL,
                PRIMARY KEY (channel, minute));
            CREATE TABLE IF NOT EXISTS viewers (
                minute INTEGER NOT NULL, channel TEXT NOT NULL, name TEXT NOT NULL, via TEXT NOT NULL,
                PRIMARY KEY (channel, minute, name, via));
            CREATE INDEX IF NOT EXISTS airings_by_time ON airings (channel, start);
            """);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>Records one minute for each channel; a minute already recorded for a channel is left as it was.</summary>
    public void Record(IEnumerable<MinuteSample> samples)
    {
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteTransaction transaction = db.BeginTransaction();
            foreach (MinuteSample sample in samples)
            {
                // Counted once per channel and minute, even if the recorder runs twice in one (a minute belongs to one airing).
                int watched = sample.Viewers > 0 ? 1 : 0;
                using (SqliteCommand airing = db.CreateCommand())
                {
                    airing.Transaction = transaction;
                    airing.CommandText = """
                        INSERT INTO airings (channel, start, kind, show, title, item, aired, watched, viewer_minutes, peak, last)
                        VALUES ($c, $s, $k, $show, $title, $item, 1, $w, $v, $v, $m)
                        ON CONFLICT (channel, start) DO UPDATE SET
                            aired = aired + 1, watched = watched + $w, viewer_minutes = viewer_minutes + $v, peak = max(peak, $v),
                            kind = $k, show = $show, title = $title, item = $item, last = $m
                        WHERE airings.last < $m
                        """;
                    airing.Parameters.AddWithValue("$m", sample.Minute);
                    airing.Parameters.AddWithValue("$c", sample.ChannelId);
                    airing.Parameters.AddWithValue("$s", sample.AiringStart);
                    airing.Parameters.AddWithValue("$k", sample.Kind);
                    airing.Parameters.AddWithValue("$show", sample.Show);
                    airing.Parameters.AddWithValue("$title", (object?)sample.Title ?? DBNull.Value);
                    airing.Parameters.AddWithValue("$item", (object?)sample.ItemId ?? DBNull.Value);
                    airing.Parameters.AddWithValue("$w", watched);
                    airing.Parameters.AddWithValue("$v", sample.Viewers);
                    airing.ExecuteNonQuery();
                }

                if (sample.Viewers <= 0)
                {
                    continue;
                }

                using (SqliteCommand minute = db.CreateCommand())
                {
                    minute.Transaction = transaction;
                    minute.CommandText = "INSERT OR IGNORE INTO watched (minute, channel, viewers, airing) VALUES ($m, $c, $v, $s)";
                    minute.Parameters.AddWithValue("$m", sample.Minute);
                    minute.Parameters.AddWithValue("$c", sample.ChannelId);
                    minute.Parameters.AddWithValue("$v", sample.Viewers);
                    minute.Parameters.AddWithValue("$s", sample.AiringStart);
                    minute.ExecuteNonQuery();
                }

                foreach (Viewer viewer in sample.Who)
                {
                    using SqliteCommand who = db.CreateCommand();
                    who.Transaction = transaction;
                    who.CommandText = "INSERT OR IGNORE INTO viewers (minute, channel, name, via) VALUES ($m, $c, $n, $via)";
                    who.Parameters.AddWithValue("$m", sample.Minute);
                    who.Parameters.AddWithValue("$c", sample.ChannelId);
                    who.Parameters.AddWithValue("$n", viewer.Name);
                    who.Parameters.AddWithValue("$via", viewer.Via);
                    who.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>The airings that began from <paramref name="fromMinute"/> on (Unix minutes), oldest first.</summary>
    public IReadOnlyList<AiringRow> Airings(string channelId, long fromMinute)
    {
        List<AiringRow> rows = [];
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = """
                SELECT channel, start, kind, show, title, item, aired, watched, viewer_minutes, peak
                FROM airings WHERE channel = $c AND start >= $from ORDER BY start
                """;
            command.Parameters.AddWithValue("$c", channelId);
            command.Parameters.AddWithValue("$from", fromMinute * 60);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AiringRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9)));
            }
        }

        return rows;
    }

    /// <summary>Channels ranked by viewer-minutes within retained history, with an exclusive upper bound.</summary>
    public IReadOnlyList<ChannelViewingTotal> ChannelTotals(long fromMinute, long toMinute)
    {
        List<ChannelViewingTotal> totals = [];
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = """
                SELECT channel, SUM(viewers) AS total FROM watched
                WHERE minute >= $from AND minute < $to
                GROUP BY channel ORDER BY total DESC, channel ASC
                """;
            command.Parameters.AddWithValue("$from", fromMinute);
            command.Parameters.AddWithValue("$to", toMinute);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) totals.Add(new ChannelViewingTotal(reader.GetString(0), reader.GetInt64(1)));
        }
        return totals;
    }

    public IReadOnlyList<WatchedMinute> Watched(string channelId, long fromMinute)
    {
        List<WatchedMinute> rows = [];
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT minute, viewers FROM watched WHERE channel = $c AND minute >= $from ORDER BY minute";
            command.Parameters.AddWithValue("$c", channelId);
            command.Parameters.AddWithValue("$from", fromMinute);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new WatchedMinute(reader.GetInt64(0), reader.GetInt32(1)));
            }
        }

        return rows;
    }

    public IReadOnlyList<ViewerMinute> Viewers(string channelId, long fromMinute)
    {
        List<ViewerMinute> rows = [];
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT minute, name, via FROM viewers WHERE channel = $c AND minute >= $from ORDER BY minute";
            command.Parameters.AddWithValue("$c", channelId);
            command.Parameters.AddWithValue("$from", fromMinute);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ViewerMinute(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return rows;
    }

    /// <summary>When this channel's history begins (Unix minutes), or null when there's none.</summary>
    public long? FirstMinute(string channelId)
    {
        lock (_gate)
        {
            using SqliteConnection db = Open();
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT min(start) FROM airings WHERE channel = $c";
            command.Parameters.AddWithValue("$c", channelId);
            return command.ExecuteScalar() is long start ? start / 60 : null;
        }
    }

    /// <summary>Forgets everything older than <paramref name="beforeMinute"/> (Unix minutes).</summary>
    public void Prune(long beforeMinute)
    {
        lock (_gate)
        {
            using SqliteConnection db = Open();
            Execute(db, "DELETE FROM airings WHERE start < $s; DELETE FROM watched WHERE minute < $m; DELETE FROM viewers WHERE minute < $m;",
                ("$s", beforeMinute * 60), ("$m", beforeMinute));
        }
    }

    /// <summary>Forgets one channel's history (from the Stats tab).</summary>
    public void Clear(string channelId)
    {
        lock (_gate)
        {
            using SqliteConnection db = Open();
            Execute(db, "DELETE FROM airings WHERE channel = $c; DELETE FROM watched WHERE channel = $c; DELETE FROM viewers WHERE channel = $c;",
                ("$c", channelId));
        }
    }

    public static long ToMinute(DateTimeOffset at) => at.ToUnixTimeSeconds() / 60;

    public static DateTimeOffset FromMinute(long minute) => DateTimeOffset.FromUnixTimeSeconds(minute * 60);

    public static string Describe(long minute) => FromMinute(minute).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
