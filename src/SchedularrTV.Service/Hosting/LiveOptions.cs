using Microsoft.Extensions.Options;

namespace SchedularrTV.Service.Hosting;

/// <summary>
/// <c>IOptions&lt;T&gt;</c> that follows the configuration instead of freezing at first use. Saving settings reloads the
/// configuration (<see cref="AppRuntime.SaveSettings"/>), so everything that reads <c>.Value</c> when it needs a
/// setting, rather than keeping the object from its constructor, gets the new values without a restart.
/// </summary>
public sealed class LiveOptions<TOptions>(IOptionsMonitor<TOptions> monitor) : IOptions<TOptions>
    where TOptions : class
{
    public TOptions Value => monitor.CurrentValue;
}
