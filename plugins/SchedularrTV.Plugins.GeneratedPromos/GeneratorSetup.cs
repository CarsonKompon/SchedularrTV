using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// Runs an external generator's setup command (e.g. <c>npm install</c>) the first time its folder is used, and
/// remembers that it has, per folder and command: changing either runs it again. What's been set up is kept in the
/// plugin's data folder, so nothing is written into the generator's own folder.
/// </summary>
public sealed class GeneratorSetup(string dataDirectory, ILogger<GeneratorSetup> logger)
{
    /// <summary>Installing packages can take a while on a slow connection.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

    private readonly string _stateFile = Path.Combine(dataDirectory, "generator-setup.json");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private Dictionary<string, DateTimeOffset>? _done;

    /// <summary>Whether the generator has a setup command and a folder to run it in.</summary>
    public static bool Needs(GeneratedScriptConfig? generator) =>
        generator is { SetupCommand: { } command, WorkingDirectory: { } folder } && command.Trim().Length > 0 && folder.Trim().Length > 0;

    /// <summary>When the generator's setup last finished; null if it hasn't (or needs none).</summary>
    public DateTimeOffset? DoneAt(GeneratedScriptConfig? generator)
    {
        if (Key(generator) is not { } key)
        {
            return null;
        }

        lock (_lock)
        {
            return Done().TryGetValue(key, out DateTimeOffset at) ? at : null;
        }
    }

    /// <summary>Runs the setup command unless it has already run for this folder and command.</summary>
    public async Task EnsureAsync(GeneratedScriptConfig generator, CancellationToken cancellationToken)
    {
        if (Key(generator) is not { } key || DoneAt(generator) is not null)
        {
            return;
        }

        // Promos render side by side; only one of them sets the folder up, the others wait for it.
        SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DoneAt(generator) is not null)
            {
                return;
            }

            string folder = generator.WorkingDirectory!.Trim();
            string command = generator.SetupCommand!.Trim();
            if (!Directory.Exists(folder))
            {
                throw new InvalidOperationException($"The generator's folder doesn't exist: {folder}");
            }

            logger.LogInformation("Setting up the promo generator in {Folder}: {Command}", folder, command);
            await ExternalPromoGenerator.RunSetupAsync(command, folder, Limit, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                Done()[key] = DateTimeOffset.UtcNow;
                Save();
            }

            logger.LogInformation("The promo generator in {Folder} is set up.", folder);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs the setup command now, even if it has run before.</summary>
    public Task RunAgainAsync(GeneratedScriptConfig generator, CancellationToken cancellationToken)
    {
        Forget(generator);
        return EnsureAsync(generator, cancellationToken);
    }

    /// <summary>Has the setup run again the next time the generator is used.</summary>
    public void Forget(GeneratedScriptConfig generator)
    {
        if (Key(generator) is not { } key)
        {
            return;
        }

        lock (_lock)
        {
            if (Done().Remove(key))
            {
                Save();
            }
        }
    }

    private static string? Key(GeneratedScriptConfig? generator)
    {
        if (!Needs(generator))
        {
            return null;
        }

        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generator!.WorkingDirectory!.Trim()));
        if (OperatingSystem.IsWindows())
        {
            folder = folder.ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{folder}\n{generator.SetupCommand!.Trim()}"));
        return Convert.ToHexStringLower(hash);
    }

    private Dictionary<string, DateTimeOffset> Done()
    {
        if (_done is not null)
        {
            return _done;
        }

        try
        {
            _done = File.Exists(_stateFile)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_stateFile)) ?? new Dictionary<string, DateTimeOffset>()
                : new Dictionary<string, DateTimeOffset>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Couldn't read {File}; generators will be set up again.", _stateFile);
            _done = new Dictionary<string, DateTimeOffset>();
        }

        return _done;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            string partial = _stateFile + ".partial";
            File.WriteAllText(partial, JsonSerializer.Serialize(_done));
            File.Move(partial, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't save {File}; generators may be set up again after a restart.", _stateFile);
        }
    }
}
