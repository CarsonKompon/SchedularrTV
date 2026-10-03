using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Plugins.Writing;

namespace SchedularrTV.Service.Writing;

/// <summary>
/// Has the writer plugins' language models write voice-overs and captions, checks what they write, and keeps it, so
/// each piece is written once. Anything that can't be written (no model set up, a reply that fails the checks, the
/// model erroring) gets the job's usual wording instead, so asking never fails.
/// </summary>
/// <remarks>
/// One piece is written at a time: local models use the whole processor, and two at once would only take twice as
/// long each. <see cref="WriteAsync"/> waits for its turn; <see cref="Peek"/> doesn't wait at all, queueing the piece
/// (soonest due first) and answering with the usual wording until it's written.
/// </remarks>
public sealed class AiTextWriter : IDisposable
{
    private const int Attempts = 2;
    private const int QueueLimit = 400;

    // A reply that failed the checks would probably fail again; an error may be a model still loading or a server down.
    private static readonly TimeSpan RetryRejectedAfter = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryErrorsAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(60);

    private readonly TextGeneratorCatalog _generators;
    private readonly Func<string> _folder;
    private readonly TimeProvider _clock;
    private readonly ILogger<AiTextWriter> _logger;
    private readonly SemaphoreSlim _model = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Problem)> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<AiTextResult>>> _calls = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly PriorityQueue<(AiTextJob Job, string Key), DateTimeOffset> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private bool _working;

    public AiTextWriter(TextGeneratorCatalog generators, Func<string> folder, TimeProvider clock, ILogger<AiTextWriter> logger)
    {
        _generators = generators;
        _folder = folder;
        _clock = clock;
        _logger = logger;
        _ = Task.Run(Prune);
    }

    public AiTextWriter(TextGeneratorCatalog generators, string folder, TimeProvider clock, ILogger<AiTextWriter> logger)
        : this(generators, () => folder, clock, logger)
    {
    }

    /// <summary>Raised from a worker thread when a piece has been written (or given up on), with what to use.</summary>
    public event Action<AiTextJob, AiTextResult>? Written;

    public TextGeneratorCatalog Generators => _generators;

    /// <summary>Pieces waiting to be written for <see cref="Peek"/>.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate) return _queue.Count;
        }
    }

    /// <summary>What to use right now, without waiting: the writing when it's done, else the usual wording (queueing it).</summary>
    public AiTextResult Peek(AiTextJob job, bool queue = true)
    {
        if (Ready(job, out ITextGenerator? generator, out AiTextResult? instead) is not { } key)
        {
            return instead!;
        }

        if (Cached(key) is { } text)
        {
            return new AiTextResult(text, true);
        }

        if (RecentFailure(key) is { } problem)
        {
            return new AiTextResult(job.Fallback, false, problem);
        }

        if (queue)
        {
            Enqueue(job, key);
        }

        return new AiTextResult(job.Fallback, false, $"{generator!.DisplayName} is still writing it");
    }

    /// <summary>The writing, written now if it hasn't been; the usual wording when it can't be. Only cancelling throws.</summary>
    public async Task<AiTextResult> WriteAsync(AiTextJob job, CancellationToken cancellationToken = default)
    {
        if (Ready(job, out ITextGenerator? generator, out AiTextResult? instead) is not { } key)
        {
            return instead!;
        }

        if (Cached(key) is { } text)
        {
            return new AiTextResult(text, true);
        }

        if (RecentFailure(key) is { } problem)
        {
            return new AiTextResult(job.Fallback, false, problem);
        }

        // The same piece asked for twice at once (two renders, a render and the queue) is written once.
        Lazy<Task<AiTextResult>> call = _calls.GetOrAdd(key, _ => new Lazy<Task<AiTextResult>>(() => WriteAndKeepAsync(job, generator!, key)));
        return await call.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// For the editors' "Try it": a fresh attempt every time (whatever's kept, and however often the model is set to
    /// write), not kept. Says why when it couldn't be used.
    /// </summary>
    public async Task<AiTextResult> TryAsync(AiTextJob job, CancellationToken cancellationToken = default)
    {
        ITextGenerator? generator = _generators.Find(job.Config.Generator);
        if (generator is null)
        {
            return new AiTextResult(job.Fallback, false, NoGenerator(job));
        }

        if (generator.SetupProblem is { } setup)
        {
            return new AiTextResult(job.Fallback, false, setup);
        }

        return await RunAsync(job, generator, cancellationToken).ConfigureAwait(false);
    }

    // The token source and gate aren't disposed: work still finishing may touch them, and they hold nothing native.
    public void Dispose() => _stopping.Cancel();

    /// <summary>The cache key when the job can be written, else null with what to use instead.</summary>
    private string? Ready(AiTextJob job, out ITextGenerator? generator, out AiTextResult? instead)
    {
        generator = null;
        instead = null;
        if (!AiTextRules.Chosen(job))
        {
            instead = new AiTextResult(job.Fallback, false);
            return null;
        }

        generator = _generators.Find(job.Config.Generator);
        if (generator is null)
        {
            instead = new AiTextResult(job.Fallback, false, NoGenerator(job));
            return null;
        }

        if (generator.SetupProblem is { } setup)
        {
            instead = new AiTextResult(job.Fallback, false, setup);
            return null;
        }

        return Key(job, generator);
    }

    private static string NoGenerator(AiTextJob job) =>
        string.IsNullOrWhiteSpace(job.Config.Generator)
            ? "no AI writer plugin is installed and switched on"
            : $"the AI writer '{job.Config.Generator}' isn't installed or is switched off";

    private static string Key(AiTextJob job, ITextGenerator generator) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{generator.Id}\u001f{generator.CacheIdentity}\u001f{AiTextRules.Identity(job)}")))[..32];

    private async Task<AiTextResult> WriteAndKeepAsync(AiTextJob job, ITextGenerator generator, string key)
    {
        try
        {
            AiTextResult result = await RunAsync(job, generator, _stopping.Token).ConfigureAwait(false);
            if (result.Written)
            {
                Keep(key, result.Text);
            }
            else
            {
                _failed[key] = (_clock.GetUtcNow() + (result.Problem?.StartsWith("the AI writer failed", StringComparison.Ordinal) == true
                    ? RetryErrorsAfter
                    : RetryRejectedAfter), result.Problem ?? "it couldn't be written");
            }

            try
            {
                Written?.Invoke(job, result);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A listener for finished AI writing failed.");
            }

            return result;
        }
        finally
        {
            _calls.TryRemove(key, out _);
        }
    }

    private async Task<AiTextResult> RunAsync(AiTextJob job, ITextGenerator generator, CancellationToken cancellationToken)
    {
        string? problem = null;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            string reply;
            await _model.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                reply = await generator.GenerateAsync(AiTextRules.Request(job, attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{Writer} couldn't write {Purpose}: {Message}", generator.DisplayName, job.Purpose, ex.GetBaseException().Message);
                return new AiTextResult(job.Fallback, false, $"the AI writer failed: {ex.GetBaseException().Message}");
            }
            finally
            {
                _model.Release();
            }

            string text = AiTextRules.Clean(reply, job.Use);
            problem = AiTextRules.Check(text, job);
            if (problem is null)
            {
                return new AiTextResult(AiTextRules.Tidy(text, job), true);
            }

            _logger.LogDebug("{Writer}'s writing for {Purpose} was set aside ({Problem}): {Text}", generator.DisplayName, job.Purpose, problem, text);
        }

        return new AiTextResult(job.Fallback, false, problem);
    }

    private void Enqueue(AiTextJob job, string key)
    {
        lock (_gate)
        {
            if (_queued.Contains(key) || _queue.Count >= QueueLimit)
            {
                return;
            }

            _queued.Add(key);
            _queue.Enqueue((job, key), job.Due);
            if (_working)
            {
                return;
            }

            _working = true;
        }

        _ = Task.Run(WorkAsync);
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            (AiTextJob Job, string Key) next;
            lock (_gate)
            {
                if (_stopping.IsCancellationRequested || !_queue.TryDequeue(out next, out _))
                {
                    _working = false;
                    return;
                }
            }

            try
            {
                await WriteAsync(next.Job, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _queue.Clear();
                    _queued.Clear();
                    _working = false;
                }

                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI writing for {Purpose} failed.", next.Job.Purpose);
            }
            finally
            {
                lock (_gate) _queued.Remove(next.Key);
            }
        }
    }

    private string? RecentFailure(string key)
    {
        if (_failed.TryGetValue(key, out (DateTimeOffset Until, string Problem) failure))
        {
            if (_clock.GetUtcNow() < failure.Until)
            {
                return failure.Problem;
            }

            _failed.TryRemove(key, out _);
        }

        return null;
    }

    private string? Cached(string key)
    {
        if (_memory.TryGetValue(key, out string? text))
        {
            return text;
        }

        try
        {
            string path = Path.Combine(_folder(), key + ".txt");
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            // Kept while it's used: Prune counts from the last use.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }

            _memory[key] = text;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Keep(string key, string text)
    {
        _memory[key] = text;
        try
        {
            string folder = _folder();
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, key + ".txt");
            string partial = Path.Combine(folder, $"{key}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(partial, text);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't keep AI writing on disk; it's kept until the scheduler restarts.");
        }
    }

    /// <summary>Removes writing nothing has used for two months, and leftovers from interrupted saves.</summary>
    private void Prune()
    {
        try
        {
            string folder = _folder();
            if (!Directory.Exists(folder))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - KeepFor;
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't tidy the AI writing folder.");
        }
    }
}
