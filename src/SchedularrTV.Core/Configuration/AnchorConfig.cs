namespace SchedularrTV.Core.Configuration;

public enum Recurrence
{
    Daily,
    DaysOfWeek,
    SpecificDate
}

public enum RepeatModifier
{
    None,

    /// <summary>Matches the MM-DD portion of <see cref="BlockSchedule.Date"/> every year.</summary>
    Yearly,

    /// <summary>Matches the DD portion of <see cref="BlockSchedule.Date"/> every month.</summary>
    Monthly
}

public sealed class BlockSchedule
{
    /// <summary>
    /// <c>MM-DD</c> or <c>yyyy-MM-DD</c> or <c>DD</c> depending on <see cref="Repeat"/>.
    /// </summary>
    public string? Date { get; set; }

    public RepeatModifier Repeat { get; set; } = RepeatModifier.None;
}

/// <summary>A time-locked show slot, either channel-level or nested inside a block (spec 2.1.1).</summary>
public sealed class AnchorConfig
{
    public string? Name { get; set; }

    public required TimeSpan Time { get; set; }

    /// <summary>Days this anchor fires. Empty means every day the parent scope is active.</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    public ContentSelector? Show { get; set; }

    /// <summary>Multiple selectors are drawn from in order, one item each, per firing.</summary>
    public List<ContentSelector> Shows { get; set; } = [];

    /// <summary>How many consecutive items to schedule when the anchor fires.</summary>
    public int Count { get; set; } = 1;

    public IEnumerable<ContentSelector> AllSelectors()
    {
        if (Show is not null)
        {
            yield return Show;
        }

        foreach (ContentSelector selector in Shows)
        {
            yield return selector;
        }
    }
}
