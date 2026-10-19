using System.Security.Cryptography;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>What importing a voice pack did: added it whole, brought it up to date, or found nothing new.</summary>
public sealed class GeneratedVoicePackImport
{
    /// <summary>The pack in the library afterwards (the one added, or the one that was already there).</summary>
    public GeneratedVoicePack? Pack { get; set; }

    /// <summary>The pack wasn't installed, and was added whole.</summary>
    public bool Installed { get; set; }

    public int Phrases { get; set; }

    /// <summary>Show names, days, times and channels added to phrases that were already there.</summary>
    public int Values { get; set; }

    public int Templates { get; set; }

    public int Takes { get; set; }

    /// <summary>Whether anything was added.</summary>
    public bool Changed => Installed || Phrases + Values + Templates + Takes > 0;

    public string Summary
    {
        get
        {
            string name = Pack is null ? "The voice pack" : $"Voice pack “{(string.IsNullOrWhiteSpace(Pack.Name) ? "Untitled" : Pack.Name)}”";
            if (Installed)
            {
                return $"{name} installed.";
            }

            if (!Changed)
            {
                return $"{name} is already up to date.";
            }

            List<string> added = [];
            if (Phrases > 0) added.Add(Count(Phrases, "phrase"));
            if (Values > 0) added.Add(Count(Values, "show, day or time", "shows, days or times"));
            if (Templates > 0) added.Add(Count(Templates, "template"));
            if (Takes > 0) added.Add(Count(Takes, "recording"));
            return $"{name} updated: added {string.Join(", ", added)}.";
        }
    }

    private static string Count(int count, string one, string? many = null) => count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";
}

/// <summary>
/// Voice packs as packages (zips with every recording), and the merging that installs one or brings an installed copy
/// up to date without changing anything already there.
/// </summary>
public static class VoicePackPackages
{
    public const string Kind = "voice-pack";

    /// <summary>Writes a copy of the pack, its recordings in the package, and finishes it; returns its name.</summary>
    public static async Task<string> ExportAsync(GeneratedVoicePack pack, TemplatePackageWriter writer, CancellationToken cancellationToken = default)
    {
        GeneratedVoicePack copy = await PackedAsync(pack, writer, cancellationToken).ConfigureAwait(false);
        writer.Finish(Kind, string.IsNullOrWhiteSpace(pack.Name) ? "Voice pack" : pack.Name, copy);
        return string.IsNullOrWhiteSpace(pack.Name) ? "Voice pack" : pack.Name;
    }

    /// <summary>
    /// A copy of the pack for a package: every take's file copied in (its path now pointing into the package) and its
    /// content hash noted, so an installed copy can tell which recordings it already has.
    /// </summary>
    public static async Task<GeneratedVoicePack> PackedAsync(GeneratedVoicePack pack, TemplatePackageWriter writer, CancellationToken cancellationToken = default)
    {
        GeneratedVoicePack copy = GeneratedPromoEditing.Copy(pack);
        foreach (GeneratedVoiceTake take in AllTakes(copy))
        {
            string? path = take.Path?.Trim();
            take.Hash = path is { Length: > 0 } && File.Exists(path) ? await HashAsync(path, cancellationToken).ConfigureAwait(false) : null;
            take.Path = writer.Asset(take.Path) ?? string.Empty;
        }

        return copy;
    }

    /// <summary>Installs the package's voice pack, or brings the installed copy up to date (see <see cref="MergeAsync"/>).</summary>
    public static async Task<GeneratedVoicePackImport> ImportAsync(TemplatePackageReader reader, TemplateAssets assets, List<GeneratedVoicePack> library,
        CancellationToken cancellationToken = default)
    {
        GeneratedVoicePack pack = reader.Item<GeneratedVoicePack>();
        return await MergeAsync(library, pack,
            path => reader.AssetAsync(path, assets, GeneratedPromoPreview.VoicePackAssets, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds <paramref name="incoming"/> to <paramref name="library"/> (matched by id, never by name): a pack not there is
    /// added whole; one that is keeps everything it has (names, settings, templates as they are) and only gains the
    /// phrases, values (shows, days, times, channels), templates and recordings it lacks. Recordings are told apart by
    /// their content, so the same take is never added twice. Files come through <paramref name="keep"/> only for what's
    /// added.
    /// </summary>
    public static async Task<GeneratedVoicePackImport> MergeAsync(List<GeneratedVoicePack> library, GeneratedVoicePack incoming,
        Func<string?, Task<string?>> keep, CancellationToken cancellationToken = default)
    {
        GeneratedVoicePackImport report = new();
        Tidy(incoming);
        if (string.IsNullOrWhiteSpace(incoming.Id))
        {
            incoming.Id = GeneratedVoicePack.NewId();
        }

        incoming.Id = incoming.Id.Trim();
        if (GeneratedVoicePacks.Find(library, incoming.Id) is not { } mine)
        {
            // Names are only labels; one already taken gets a number so the two can be told apart.
            incoming.Name = GeneratedPromoEditing.UniqueName(library.Select(existing => existing.Name),
                string.IsNullOrWhiteSpace(incoming.Name) ? "Voice pack" : incoming.Name.Trim());
            foreach (GeneratedVoicePhrase phrase in incoming.Phrases)
            {
                await KeepAllAsync(phrase.Takes, keep).ConfigureAwait(false);
                foreach (GeneratedVoiceEntry entry in phrase.Entries)
                {
                    await KeepAllAsync(entry.Takes, keep).ConfigureAwait(false);
                    if (entry.More is { } more) await KeepAllAsync(more, keep).ConfigureAwait(false);
                    if (entry.EvenMore is { } evenMore) await KeepAllAsync(evenMore, keep).ConfigureAwait(false);
                    entry.Tidy();
                }
            }

            library.Add(incoming);
            report.Pack = incoming;
            report.Installed = true;
            return report;
        }

        report.Pack = mine;

        // What this computer's recordings are, by content, so takes it has (wherever they're kept) aren't added again.
        HashSet<string> have = new(StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedVoiceTake take in AllTakes(mine))
        {
            if (take.Path?.Trim() is { Length: > 0 } path && File.Exists(path))
            {
                have.Add(await HashAsync(path, cancellationToken).ConfigureAwait(false));
            }
        }

        async Task AddTakesAsync(List<GeneratedVoiceTake> into, IEnumerable<GeneratedVoiceTake> from)
        {
            foreach (GeneratedVoiceTake take in from)
            {
                // A take without a hash (made by hand) is added when its file isn't already among these.
                if (take.Hash is { Length: > 0 } hash && !have.Add(hash))
                {
                    continue;
                }

                if (await keep(take.Path).ConfigureAwait(false) is not { Length: > 0 } kept)
                {
                    continue;
                }

                if (take.Hash is null && into.Any(existing => string.Equals(existing.Path, kept, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                into.Add(new GeneratedVoiceTake { Path = kept, Length = take.Length });
                report.Takes++;
            }
        }

        foreach (GeneratedVoicePhrase phrase in incoming.Phrases)
        {
            GeneratedVoicePhrase? existing = mine.Phrases.FirstOrDefault(candidate => string.Equals(candidate.Id, phrase.Id, StringComparison.OrdinalIgnoreCase));
            bool newPhrase = existing is null;
            if (existing is null)
            {
                // A new phrase comes with its settings; its values and recordings are added below.
                existing = GeneratedPromoEditing.Copy(phrase);
                existing.Takes = [];
                existing.Entries = [];
                mine.Phrases.Add(existing);
                report.Phrases++;
            }

            await AddTakesAsync(existing.Takes, phrase.Takes).ConfigureAwait(false);
            foreach (GeneratedVoiceEntry entry in phrase.Entries)
            {
                string key = entry.Key?.Trim() ?? string.Empty;
                GeneratedVoiceEntry? value = existing.Entries.FirstOrDefault(candidate => string.Equals(candidate.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase));
                bool newValue = value is null;
                value ??= new GeneratedVoiceEntry { Key = key };
                int before = report.Takes;
                await AddTakesAsync(value.Takes, entry.Takes).ConfigureAwait(false);
                await AddTakesAsync(value.EnsureTakesOf(GeneratedVoiceVariant.More), entry.More ?? []).ConfigureAwait(false);
                await AddTakesAsync(value.EnsureTakesOf(GeneratedVoiceVariant.EvenMore), entry.EvenMore ?? []).ConfigureAwait(false);
                value.Tidy();

                // A new value is added when it brings recordings, or when the package lists it unrecorded too (a show
                // still to record); one whose recordings this computer already has under another value isn't.
                if (newValue && (report.Takes > before || entry.TakeCount == 0))
                {
                    existing.Entries.Add(value);
                    if (!newPhrase) report.Values++;
                }
            }
        }

        foreach (GeneratedVoicePackTemplate template in incoming.Templates)
        {
            if (mine.Templates.Any(candidate => string.Equals(candidate.Id, template.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            template.Name = GeneratedPromoEditing.UniqueName(mine.Templates.Select(existing => existing.Name),
                string.IsNullOrWhiteSpace(template.Name) ? "Template" : template.Name);
            mine.Templates.Add(template);
            report.Templates++;
        }

        return report;
    }

    /// <summary>Every take of every phrase, value and variant in the pack.</summary>
    public static IEnumerable<GeneratedVoiceTake> AllTakes(GeneratedVoicePack pack) =>
        pack.Phrases.SelectMany(phrase => phrase.Takes.Concat(phrase.Entries.SelectMany(entry =>
            entry.Takes.Concat(entry.More ?? []).Concat(entry.EvenMore ?? []))));

    /// <summary>A recording's content hash: the first 32 hex digits of its SHA-256.</summary>
    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash)[..32];
    }

    private static async Task KeepAllAsync(List<GeneratedVoiceTake> takes, Func<string?, Task<string?>> keep)
    {
        List<GeneratedVoiceTake> kept = [];
        foreach (GeneratedVoiceTake take in takes)
        {
            if (await keep(take.Path).ConfigureAwait(false) is { Length: > 0 } path)
            {
                kept.Add(new GeneratedVoiceTake { Path = path, Length = take.Length });
            }
        }

        takes.Clear();
        takes.AddRange(kept);
    }

    /// <summary>Lists a package left out (an older or hand-made one) are made empty, so merging can rely on them.</summary>
    private static void Tidy(GeneratedVoicePack pack)
    {
        pack.Phrases ??= [];
        pack.Templates ??= [];
        foreach (GeneratedVoicePhrase phrase in pack.Phrases)
        {
            phrase.Id = string.IsNullOrWhiteSpace(phrase.Id) ? GeneratedVoicePack.NewId() : phrase.Id.Trim();
            phrase.Takes ??= [];
            phrase.Entries ??= [];
            foreach (GeneratedVoiceEntry entry in phrase.Entries) entry.Tidy();
        }

        foreach (GeneratedVoicePackTemplate template in pack.Templates) template.Parts ??= [];
    }
}
