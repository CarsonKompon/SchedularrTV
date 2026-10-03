using SchedularrTV.Plugins.Writing;

namespace SchedularrTV.Service.Writing;

/// <summary>The language models the writer plugins offer, as they are right now.</summary>
public sealed class TextGeneratorCatalog(IEnumerable<ITextGenerator> generators)
{
    // Built on each use, not kept: switched-off plugins drop out while the app runs. A plugin reusing another's id
    // mustn't stop the app starting; the first registered wins.
    public IReadOnlyList<ITextGenerator> Generators => [.. generators
        .GroupBy(generator => generator.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())];

    /// <summary>The one asked for, or with none named the first that's ready (else the first at all); null when there are none.</summary>
    public ITextGenerator? Find(string? id)
    {
        IReadOnlyList<ITextGenerator> available = Generators;
        if (!string.IsNullOrWhiteSpace(id))
        {
            return available.FirstOrDefault(generator => string.Equals(generator.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return available.FirstOrDefault(generator => generator.SetupProblem is null) ?? available.FirstOrDefault();
    }
}
