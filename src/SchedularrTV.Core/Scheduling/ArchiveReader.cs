using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Core.Scheduling;

public sealed record ArchiveSlot(TimeSpan Start, TimeSpan End, string Show, string? Episode, IReadOnlyList<string> Blocks);

public sealed record ArchiveDay(DateOnly Date, IReadOnlyList<ArchiveSlot> Slots);

public sealed record ArchiveEpisode(string Show, string Episode, long Count);

public sealed record ArchiveCoverage(int Year, int Days, double AverageMatches, int ZeroMatches, int OneMatch);

public sealed record ArchiveScan(IReadOnlyList<string> Blocks, IReadOnlyList<string> Shows, int Days, IReadOnlyList<string> Warnings)
{
    public IReadOnlyDictionary<string, long> BlockCounts { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, long> ShowCounts { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ArchiveEpisode> Episodes { get; init; } = [];
}

public static class ArchiveReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static async Task<ArchiveDay?> SelectDayAsync(
        ArchiveFollowConfig follow, DateOnly date, string seed, CancellationToken cancellationToken = default,
        Func<ArchiveSlot, int>? matchScore = null)
    {
        Validate(follow);
        List<(DateOnly Date, string Path)> candidates = [];
        if (follow.FollowWeekday)
        {
            candidates.AddRange(ListDates(follow, cancellationToken)
                .Where(archived => CalendarWeight(follow, date, archived) > 0)
                .Select(archived => (archived, DayPath(follow.Directory, archived))));
        }
        for (int year = follow.StartYear; !follow.FollowWeekday && year <= follow.EndYear; year++)
        {
            if (date.Day > DateTime.DaysInMonth(year, date.Month))
            {
                continue;
            }

            var archived = new DateOnly(year, date.Month, date.Day);
            string path = DayPath(follow.Directory, archived);
            if (File.Exists(path))
            {
                candidates.Add((archived, path));
            }
        }

        List<(ArchiveDay Day, long SecondaryWeight, (int Tier, decimal Episodes, decimal Shows, int ExactSlots, int ShowSlots) Coverage)> candidatesWithCoverage = [];
        foreach (var candidate in candidates)
        {
            ArchiveDay day = await ReadDayAsync(candidate.Path, candidate.Date, cancellationToken).ConfigureAwait(false);
            long coveredSeconds = (long)day.Slots.Sum(slot => (slot.End - slot.Start).TotalSeconds);
            int[] scores = day.Slots.Select(slot => matchScore is null ? 0 : Math.Clamp(matchScore(slot), 0, 2)).ToArray();
            int exactSlots = scores.Count(score => score == 2);
            int showSlots = scores.Count(score => score > 0);
            int slotCount = scores.Length;
            int tier = slotCount > 0 && exactSlots == slotCount ? 2 : slotCount > 0 && showSlots == slotCount ? 1 : 0;
            var coverage = (tier, slotCount == 0 ? 0 : (decimal)exactSlots / slotCount,
                slotCount == 0 ? 0 : (decimal)showSlots / slotCount, exactSlots, showSlots);
            candidatesWithCoverage.Add((day, Math.Max(3600, coveredSeconds) * CalendarWeight(follow, date, day.Date), coverage));
        }

        var coverageRanks = candidatesWithCoverage.Select(candidate => candidate.Coverage).Distinct().Order()
            .Select((coverage, rank) => (coverage, rank)).ToDictionary(entry => entry.coverage, entry => entry.rank);
        long band = candidatesWithCoverage.Select(candidate => candidate.SecondaryWeight).DefaultIfEmpty(0).Max() + 1;
        var weighted = candidatesWithCoverage.Select(candidate =>
            (candidate.Day, Weight: coverageRanks[candidate.Coverage] * band + candidate.SecondaryWeight)).ToArray();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{date:yyyy-MM-dd}"));
        long totalWeight = weighted.Sum(candidate => candidate.Weight);
        long ticket = totalWeight == 0 ? 0 : (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash) % (ulong)totalWeight);
        foreach (var candidate in weighted)
        {
            if (ticket < candidate.Weight)
            {
                return candidate.Day;
            }
            ticket -= candidate.Weight;
        }

        return null;
    }

    public static IReadOnlyList<DateOnly> ListDates(ArchiveFollowConfig follow, CancellationToken cancellationToken = default)
    {
        Validate(follow);
        SortedSet<DateOnly> dates = [];
        for (int year = follow.StartYear; year <= follow.EndYear; year++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int month = 1; month <= 12; month++)
            {
                string directory = Path.GetDirectoryName(DayPath(follow.Directory, new DateOnly(year, month, 1)))!;
                if (!System.IO.Directory.Exists(directory)) continue;
                foreach (string path in System.IO.Directory.EnumerateFiles(directory, "*.json"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateOnly archived) && archived.Year == year && archived.Month == month)
                    {
                        dates.Add(archived);
                    }
                }
            }
        }
        return dates.ToArray();
    }

    public static int CalendarWeight(ArchiveFollowConfig follow, DateOnly date, DateOnly archived)
    {
        if (archived.Year < follow.StartYear || archived.Year > follow.EndYear) return 0;
        bool sameMonth = archived.Month == date.Month;
        bool sameDate = sameMonth && archived.Day == date.Day;
        if (!follow.FollowWeekday) return sameDate ? 1 : 0;
        if (archived.DayOfWeek != date.DayOfWeek || (follow.FollowMonth && !sameMonth)) return 0;
        return sameDate ? 16 : sameMonth ? 4 : 1;
    }

    public static ArchiveCoverage Coverage(ArchiveFollowConfig follow, IEnumerable<DateOnly> dates, int year)
    {
        var first = new DateOnly(year, 1, 1);
        int days = DateTime.IsLeapYear(year) ? 366 : 365;
        DateOnly[] available = dates.Distinct().ToArray();
        int[] counts = Enumerable.Range(0, days)
            .Select(offset => available.Count(archived => CalendarWeight(follow, first.AddDays(offset), archived) > 0)).ToArray();
        return new ArchiveCoverage(year, days, counts.Average(), counts.Count(count => count == 0), counts.Count(count => count == 1));
    }

    public static async Task<ArchiveScan> ScanAsync(ArchiveFollowConfig follow, CancellationToken cancellationToken = default)
    {
        Validate(follow);
        Dictionary<string, long> showCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Dictionary<string, long>> episodeCounts = new(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = [];
        string indexPath = Path.Combine(follow.Directory, "_block_index.json");
        await using FileStream indexStream = File.OpenRead(indexPath);
        using JsonDocument indexDocument = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<string, long> blockCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in indexDocument.RootElement.EnumerateObject())
        {
            long count = property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("count", out JsonElement value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed)
                ? Math.Max(0, parsed) : 0;
            blockCounts[property.Name] = blockCounts.GetValueOrDefault(property.Name) + count;
        }
        int days = 0;
        for (int year = follow.StartYear; year <= follow.EndYear; year++)
        {
            string directory = Path.Combine(follow.Directory, year.ToString("D4", CultureInfo.InvariantCulture));
            if (!System.IO.Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in System.IO.Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly date) || date.Year != year)
                {
                    continue;
                }

                try
                {
                    await using FileStream stream = File.OpenRead(path);
                    Document? document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    foreach (Row row in document?.Schedule ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(row.Show))
                        {
                            string name = row.Show.Trim();
                            showCounts[name] = showCounts.GetValueOrDefault(name) + 1;
                            if (!episodeCounts.TryGetValue(name, out var episodes))
                            {
                                episodeCounts[name] = episodes = new(StringComparer.OrdinalIgnoreCase);
                            }
                            string episode = row.Episode?.Trim() ?? string.Empty;
                            episodes[episode] = episodes.GetValueOrDefault(episode) + 1;
                        }
                    }
                    days++;
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }

        return new ArchiveScan(
            blockCounts.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            showCounts.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(), days, warnings)
        {
            BlockCounts = blockCounts,
            ShowCounts = showCounts,
            Episodes = episodeCounts.SelectMany(show => show.Value.Select(episode =>
                new ArchiveEpisode(show.Key, episode.Key, episode.Value))).ToArray()
        };
    }

    public static async Task<ArchiveDay> ReadDayAsync(string path, DateOnly date, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        Document? document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        var rows = (document?.Schedule ?? []).Where(row => ParseTime(row.Time) is not null)
            .OrderBy(row => ParseTime(row.Time)).ToArray();
        List<ArchiveSlot> slots = [];
        for (int index = 0; index < rows.Length; index++)
        {
            Row row = rows[index];
            TimeSpan start = ParseTime(row.Time)!.Value;
            TimeSpan next = index + 1 < rows.Length ? ParseTime(rows[index + 1].Time)!.Value : TimeSpan.FromDays(1);
            TimeSpan end = ParseTime(row.EndTime) ?? next;
            if (end == TimeSpan.Zero && start > TimeSpan.Zero)
            {
                end = TimeSpan.FromDays(1);
            }
            end = end < next ? end : next;
            if (end > start && !string.IsNullOrWhiteSpace(row.Show))
            {
                slots.Add(new ArchiveSlot(start, end, row.Show.Trim(), row.Episode, row.Blocks ?? []));
            }
        }

        return new ArchiveDay(date, slots);
    }

    public static string NormalizeTitle(string? title) =>
        string.Concat((title ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    public static string? ResolveShow(ArchiveFollowConfig follow, string archived, IEnumerable<string> titles)
    {
        if (follow.Shows.TryGetValue(archived, out string? assigned))
        {
            return string.IsNullOrWhiteSpace(assigned) ? null : assigned;
        }

        return new ArchiveShowMatcher(titles).Match(archived);
    }

    private static string DayPath(string directory, DateOnly date) => Path.Combine(directory,
        date.Year.ToString("D4", CultureInfo.InvariantCulture), date.Month.ToString("D2", CultureInfo.InvariantCulture), $"{date:yyyy-MM-dd}.json");

    private static TimeSpan? ParseTime(string? value) => value == "24:00" ? TimeSpan.FromDays(1)
        : TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan parsed) ? parsed : null;

    private static void Validate(ArchiveFollowConfig follow)
    {
        if (follow.StartYear < 1 || follow.EndYear > 9999 || follow.StartYear > follow.EndYear)
        {
            throw new ArgumentException("Archive year range must be between 1 and 9999, with the start no later than the end.");
        }
        if (string.IsNullOrWhiteSpace(follow.Directory) || !System.IO.Directory.Exists(follow.Directory))
        {
            throw new DirectoryNotFoundException("Archive directory does not exist or is unavailable.");
        }
    }

    private sealed class Document
    {
        public List<Row>? Schedule { get; set; }
    }

    private sealed class Row
    {
        public string? Time { get; set; }
        public string? EndTime { get; set; }
        public string? Show { get; set; }
        public string? Episode { get; set; }
        public List<string>? Blocks { get; set; }
    }
}