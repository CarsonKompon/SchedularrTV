using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace SchedularrTV.Service.Performance;

/// <summary>One graphics card's load. Percentages are 0-100; any of them may be missing on an older driver.</summary>
/// <param name="Encoder">The video encoder (NVENC), which is what hardware transcoding uses.</param>
/// <param name="Decoder">The video decoder (NVDEC).</param>
public sealed record GpuUsage(string Name, double? Utilization, double? Encoder, double? Decoder, long? MemoryUsedBytes, long? MemoryTotalBytes);

/// <summary>
/// NVIDIA cards' load, from <c>nvidia-smi</c> (installed with the driver). Other cards aren't read: there's no tool
/// every system has for them. Finding no <c>nvidia-smi</c> is remembered, and it's looked for again only now and then.
/// </summary>
public sealed class GpuReader(ILogger logger)
{
    private const string FullQuery = "name,utilization.gpu,utilization.encoder,utilization.decoder,memory.used,memory.total";
    private const string BasicQuery = "name,utilization.gpu,memory.used,memory.total";
    private static readonly TimeSpan RetryMissing = TimeSpan.FromMinutes(5);

    private DateTimeOffset _missingSince = DateTimeOffset.MinValue;
    private bool _basicOnly;

    /// <summary>Why no card was read last time (no NVIDIA driver, say); null when it worked.</summary>
    public string? Problem { get; private set; }

    public async Task<IReadOnlyList<GpuUsage>> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now - _missingSince < RetryMissing)
        {
            return [];
        }

        Problem = null;
        string? output = await RunAsync(_basicOnly ? BasicQuery : FullQuery, cancellationToken).ConfigureAwait(false);
        if (output is null && !_basicOnly && Problem is null)
        {
            // Drivers before the encoder and decoder fields refuse the whole query; ask for less from then on.
            output = await RunAsync(BasicQuery, cancellationToken).ConfigureAwait(false);
            _basicOnly = output is not null;
        }

        if (output is null)
        {
            Problem ??= "nvidia-smi couldn't read the graphics card";
            _missingSince = now;
            return [];
        }

        return Parse(output, _basicOnly);
    }

    /// <summary>nvidia-smi's CSV (no header, no units), one card a line, in the order of the query asked for.</summary>
    public static IReadOnlyList<GpuUsage> Parse(string csv, bool basic)
    {
        List<GpuUsage> cards = [];
        foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = [.. line.Split(',').Select(part => part.Trim())];
            if (basic && parts.Length >= 4)
            {
                cards.Add(new GpuUsage(parts[0], Number(parts[1]), null, null, Mebibytes(parts[2]), Mebibytes(parts[3])));
            }
            else if (!basic && parts.Length >= 6)
            {
                cards.Add(new GpuUsage(parts[0], Number(parts[1]), Number(parts[2]), Number(parts[3]), Mebibytes(parts[4]), Mebibytes(parts[5])));
            }
        }

        return cards;
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    private static long? Mebibytes(string text) => Number(text) is { } mib ? (long)(mib * 1024 * 1024) : null;

    /// <summary>The query's output, or null when nvidia-smi isn't there (<see cref="Problem"/> set) or refused it (not set).</summary>
    private async Task<string?> RunAsync(string query, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Executable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add($"--query-gpu={query}");
        start.ArgumentList.Add("--format=csv,noheader,nounits");

        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                Problem = "nvidia-smi couldn't be started";
                return null;
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                Problem = "nvidia-smi didn't answer in time";
                return null;
            }

            string output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            Problem = "No NVIDIA graphics card found (nvidia-smi isn't installed)";
            logger.LogDebug("nvidia-smi isn't available; graphics card use won't be shown.");
            return null;
        }
    }

    /// <summary>nvidia-smi from PATH, or where the Windows driver puts it (desktop apps don't always see PATH).</summary>
    private static string Executable()
    {
        if (OperatingSystem.IsWindows())
        {
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (File.Exists(system))
            {
                return system;
            }

            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
            if (File.Exists(legacy))
            {
                return legacy;
            }
        }

        return "nvidia-smi";
    }
}
