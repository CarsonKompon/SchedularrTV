using SchedularrTV.Plugins.Editor;

namespace SchedularrTV.Service.Editor;

/// <summary>The installed fonts, as SkiaSharp sees them (the same list the canvas editors offer). Re-read every few minutes, so a newly installed font turns up.</summary>
public sealed class SystemFontCatalog(TimeProvider clock) : IFontCatalog
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private IReadOnlyList<string> _families = [];
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public IReadOnlyList<string> Families
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset now = clock.GetUtcNow();
                if (now - _readAt > RefreshAfter || _families.Count == 0)
                {
                    _families = Read();
                    _readAt = now;
                }

                return _families;
            }
        }
    }

    private static IReadOnlyList<string> Read()
    {
        try
        {
            return
            [
                .. SkiaSharp.SKFontManager.Default.GetFontFamilies()
                    .Where(family => !string.IsNullOrWhiteSpace(family) && family[0] != '@')
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
            ];
        }
        catch (Exception)
        {
            return ["Arial"];
        }
    }
}
