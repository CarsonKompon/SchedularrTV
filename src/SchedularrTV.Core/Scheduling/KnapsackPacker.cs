using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

/// <summary>One scheduled slot produced by the packer.</summary>
/// <param name="Duration">Slot length. Shorter than the item's runtime when <paramref name="Trimmed"/> is set.</param>
/// <param name="StopBeforeEnd">Play until the boundary and stop, rather than hard-cutting the asset.</param>
public sealed record PackedItem(MediaItem Item, TimeSpan Duration, bool Trimmed, bool StopBeforeEnd, bool IsFiller);

public sealed record PackResult(IReadOnlyList<PackedItem> Items, TimeSpan Filled, TimeSpan Residual);

/// <summary>Tier 3 behaviour for <see cref="KnapsackPacker.Pack"/>.</summary>
public sealed record FillerPackOptions
{
    public bool Trimmable { get; init; } = true;

    public bool StopBeforeEnd { get; init; }

    /// <summary>Residuals shorter than this are left as padding instead of a barely visible filler.</summary>
    public TimeSpan MinTrimmedDuration { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Two-pass micro-fill (spec 3.1 pass 2).
/// <para>
/// Pass A packs strict-fit commercials with a subset-sum DP. Durations are quantised into buckets so
/// the DP stays bounded, and item weights round <em>up</em> so a commercial is never selected unless
/// its full runtime fits — commercials are guaranteed-playback assets and must never be cut off.
/// </para>
/// <para>
/// Pass B closes whatever remains with filler, which carries no such guarantee and may be trimmed to
/// land exactly on the boundary. This is what eliminates the residual dead air the DP cannot avoid.
/// </para>
/// </summary>
public static class KnapsackPacker
{
    private const int MaxBuckets = 40_000;
    private const int MaxCandidates = 256;
    private const int MaxFillerIterations = 32;

    public static PackResult Pack(
        IReadOnlyList<MediaItem> commercials,
        TimeSpan target,
        TimeSpan granularity,
        int maxRepeatsPerItem = 1,
        IReadOnlyList<MediaItem>? filler = null,
        FillerPackOptions? fillerOptions = null)
    {
        if (target <= TimeSpan.Zero)
        {
            return new PackResult([], TimeSpan.Zero, TimeSpan.Zero);
        }

        List<PackedItem> chosen = [];
        TimeSpan filled = PackCommercials(commercials, target, granularity, maxRepeatsPerItem, chosen);
        TimeSpan residual = target - filled;

        if (residual > TimeSpan.Zero && filler is { Count: > 0 })
        {
            filled += PackFiller(filler, residual, fillerOptions ?? new FillerPackOptions(), chosen);
        }

        return new PackResult(chosen, filled, target - filled);
    }

    private static TimeSpan PackCommercials(
        IReadOnlyList<MediaItem> candidates,
        TimeSpan target,
        TimeSpan granularity,
        int maxRepeatsPerItem,
        List<PackedItem> chosen)
    {
        if (candidates.Count == 0)
        {
            return TimeSpan.Zero;
        }

        long bucketTicks = Math.Max(granularity.Ticks, TimeSpan.TicksPerMillisecond);
        long totalBuckets = target.Ticks / bucketTicks;

        if (totalBuckets > MaxBuckets)
        {
            // Coarsen rather than refuse; a 6-hour gap does not need millisecond buckets.
            bucketTicks = (target.Ticks / MaxBuckets) + 1;
            totalBuckets = target.Ticks / bucketTicks;
        }

        if (totalBuckets <= 0)
        {
            return TimeSpan.Zero;
        }

        int capacity = (int)totalBuckets;

        // Expand to instances so "the same bumper twice" is expressible while keeping a 0/1 DP.
        List<MediaItem> instances = [];
        foreach (MediaItem candidate in candidates)
        {
            if (candidate.Duration <= TimeSpan.Zero || candidate.Duration > target)
            {
                continue;
            }

            for (int r = 0; r < Math.Max(1, maxRepeatsPerItem) && instances.Count < MaxCandidates; r++)
            {
                instances.Add(candidate);
            }

            if (instances.Count >= MaxCandidates)
            {
                break;
            }
        }

        if (instances.Count == 0)
        {
            return TimeSpan.Zero;
        }

        int[] weights = new int[instances.Count];
        int divisor = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            weights[i] = (int)Math.Max(1, (instances[i].Duration.Ticks + bucketTicks - 1) / bucketTicks);
            divisor = GreatestCommonDivisor(divisor, weights[i]);
        }

        // Only multiples of this divisor are reachable. Compress after rounding the weights
        // so fractional runtimes retain exactly the same fit and candidate tie-breaking.
        capacity /= divisor;
        int totalWeight = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] /= divisor;
            totalWeight += weights[i];
        }
        capacity = Math.Min(capacity, totalWeight);

        bool[] reachable = new bool[capacity + 1];
        int[] choice = new int[capacity + 1];
        int[] previous = new int[capacity + 1];
        reachable[0] = true;
        Array.Fill(choice, -1);

        int reachableLimit = 0;
        for (int i = 0; i < instances.Count; i++)
        {
            int w = weights[i];
            if (w > capacity)
            {
                continue;
            }

            reachableLimit = Math.Min(capacity, reachableLimit + w);
            for (int b = reachableLimit; b >= w; b--)
            {
                if (!reachable[b] && reachable[b - w])
                {
                    reachable[b] = true;
                    choice[b] = i;
                    previous[b] = b - w;
                }
            }

            // First-discovered choices never change, so later candidates cannot improve this fit.
            if (reachable[capacity]) break;
        }

        int best = capacity;
        while (best > 0 && !reachable[best])
        {
            best--;
        }

        if (best == 0)
        {
            return TimeSpan.Zero;
        }

        List<PackedItem> packed = [];
        TimeSpan filled = TimeSpan.Zero;
        for (int b = best; b > 0 && choice[b] >= 0;)
        {
            MediaItem item = instances[choice[b]];
            packed.Add(new PackedItem(item, item.Duration, Trimmed: false, StopBeforeEnd: false, IsFiller: false));
            filled += item.Duration;
            b = previous[b];
        }

        packed.Reverse();
        chosen.AddRange(packed);
        return filled;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left;
    }

    private static TimeSpan PackFiller(
        IReadOnlyList<MediaItem> candidates,
        TimeSpan residual,
        FillerPackOptions options,
        List<PackedItem> chosen)
    {
        List<MediaItem> pool = candidates.Where(c => c.Duration > TimeSpan.Zero).ToList();
        TimeSpan remaining = residual;
        TimeSpan filled = TimeSpan.Zero;

        for (int iteration = 0; iteration < MaxFillerIterations && remaining > TimeSpan.Zero && pool.Count > 0; iteration++)
        {
            MediaItem? exact = pool.FirstOrDefault(c => c.Duration == remaining);
            if (exact is not null)
            {
                chosen.Add(new PackedItem(exact, remaining, Trimmed: false, StopBeforeEnd: false, IsFiller: true));
                return filled + remaining;
            }

            // Trim the shortest item that overshoots, so the least content is lost.
            MediaItem? trimmable = remaining >= options.MinTrimmedDuration
                ? pool.Where(c => c.Duration > remaining && (c.Trimmable ?? options.Trimmable))
                    .OrderBy(c => c.Duration)
                    .FirstOrDefault()
                : null;

            if (trimmable is not null)
            {
                chosen.Add(new PackedItem(
                    trimmable,
                    remaining,
                    Trimmed: true,
                    StopBeforeEnd: trimmable.StopBeforeEnd ?? options.StopBeforeEnd,
                    IsFiller: true));

                return filled + remaining;
            }

            // Nothing can close the gap outright; take the longest item that fits and try again.
            MediaItem? fit = pool.Where(c => c.Duration <= remaining).OrderByDescending(c => c.Duration).FirstOrDefault();
            if (fit is null)
            {
                break;
            }

            chosen.Add(new PackedItem(fit, fit.Duration, Trimmed: false, StopBeforeEnd: false, IsFiller: true));
            filled += fit.Duration;
            remaining -= fit.Duration;
            pool.Remove(fit);
        }

        return filled;
    }
}
