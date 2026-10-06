using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SchedularrTV.Service.Import;

/// <summary>
/// Another app's SQLite database, read from a copy (so the app can keep running, and nothing of its is ever written),
/// with its tables and columns looked up first: these schemas change between versions, so a column that isn't there
/// reads as null rather than failing the import.
/// </summary>
public sealed class SqliteSnapshot : IDisposable
{
    private readonly string _folder;
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, HashSet<string>?> _columns = new(StringComparer.OrdinalIgnoreCase);

    private SqliteSnapshot(string folder, SqliteConnection connection)
    {
        _folder = folder;
        _connection = connection;
    }

    /// <summary>Copies <paramref name="path"/> (and its write-ahead log, if any) somewhere temporary and opens the copy.</summary>
    public static SqliteSnapshot Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There's no database at {path}.", path);
        }

        string folder = Path.Combine(Path.GetTempPath(), "schedularrtv-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "source.db");
        Copy(path, copy);
        Copy(path + "-wal", copy + "-wal");
        Copy(path + "-shm", copy + "-shm");

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
        try
        {
            connection.Open();
            return new SqliteSnapshot(folder, connection);
        }
        catch
        {
            connection.Dispose();
            TryDelete(folder);
            throw;
        }
    }

    // Shared reads, so a database the other app has open copies too.
    private static void Copy(string from, string to)
    {
        if (!File.Exists(from))
        {
            return;
        }

        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
        source.CopyTo(target);
    }

    public bool HasTable(string table) => Columns(table) is not null;

    /// <summary>The table's columns, or null when there's no such table.</summary>
    public HashSet<string>? Columns(string table)
    {
        if (_columns.TryGetValue(table, out HashSet<string>? known))
        {
            return known;
        }

        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({Quote(table)})";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        return _columns[table] = columns.Count == 0 ? null : columns;
    }

    /// <summary>The first of <paramref name="names"/> the table has (schemas rename columns: <c>external_key</c>, <c>externalKey</c>), or null.</summary>
    public string? Column(string table, params string[] names) =>
        Columns(table) is { } columns ? names.FirstOrDefault(columns.Contains) : null;

    /// <summary>
    /// A column for a SELECT list: <c>alias."name"</c> when the table has one of <paramref name="names"/>, else <c>NULL</c>,
    /// so a missing column reads as null.
    /// </summary>
    public string Select(string table, string alias, params string[] names) =>
        Column(table, names) is { } column ? $"{alias}.{Quote(column)}" : "NULL";

    /// <summary>Every row, as column name to value (nulls as null).</summary>
    public List<Dictionary<string, object?>> Rows(string sql, params (string Name, object? Value)[] parameters)
    {
        List<Dictionary<string, object?>> rows = [];
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            Dictionary<string, object?> row = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string? Text(IReadOnlyDictionary<string, object?> row, string column)
    {
        string? text = row.TryGetValue(column, out object? value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static long? Long(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out object? value) && value is not null
            ? value switch
            {
                long number => (long?)number,
                int number => number,
                double number => (long)number,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
                string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => (long)parsed,
                _ => null
            }
            : null;

    public static int? Int(IReadOnlyDictionary<string, object?> row, string column) => Long(row, column) is { } value ? (int)value : null;

    /// <summary>A .NET TimeSpan as EF Core stores it in SQLite ("hh:mm:ss.fffffff", or "d.hh:mm:ss"); ticks as a number.</summary>
    public static TimeSpan? Span(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out object? value) ? value switch
        {
            string text when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan span) => (TimeSpan?)span,
            long ticks => TimeSpan.FromTicks(ticks),
            _ => null
        } : null;

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        TryDelete(_folder);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
