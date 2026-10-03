using System.Diagnostics;
using System.Text;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Runs a template's external generator and finds the video it made.</summary>
public static class ExternalPromoGenerator
{
    /// <summary>
    /// The generated file: <paramref name="output"/> when the program wrote it, else the path on the last line it
    /// printed. Arguments go straight to the program, never through a shell, so show titles can't inject commands.
    /// </summary>
    public static async Task<string> RunAsync(
        GeneratedScriptConfig generator,
        IReadOnlyList<string> arguments,
        string output,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(generator.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(generator.WorkingDirectory) ? Path.GetDirectoryName(output)! : generator.WorkingDirectory
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument
                .Replace(GeneratedPromoResolver.OutputToken, output, StringComparison.Ordinal)
                .Replace(GeneratedPromoResolver.OutputDirectoryToken, Path.GetDirectoryName(output)!, StringComparison.Ordinal));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(generator.Timeout);
        using Process process = Start(info);
        var stdout = new Tail();
        var stderr = new Tail();
        Task reading = Task.WhenAll(stdout.ReadAsync(process.StandardOutput), stderr.ReadAsync(process.StandardError));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await reading.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"The generator didn't finish within {generator.Timeout.TotalSeconds:0} seconds.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The generator exited with code {process.ExitCode}: {(stderr.Text.Length > 0 ? stderr.Text : stdout.Text).Trim()}");
        }

        if (File.Exists(output))
        {
            return output;
        }

        string? printed = stdout.LastLine?.Trim().Trim('"');
        return !string.IsNullOrEmpty(printed) && Path.IsPathFullyQualified(printed) && File.Exists(printed)
            ? printed
            : throw new InvalidOperationException(
                "The generator finished but no video was found. Pass {output} as the file to write, or print the finished file's full path last.");
    }

    /// <summary>The command line as it would run, for the editor.</summary>
    public static string Describe(GeneratedScriptConfig generator, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { generator.Command }.Concat(arguments.Select(argument => argument
                .Replace(GeneratedPromoResolver.OutputToken, "<output>", StringComparison.Ordinal)
                .Replace(GeneratedPromoResolver.OutputDirectoryToken, "<output folder>", StringComparison.Ordinal)))
            .Select(argument => argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"')
                ? $"\"{argument.Replace("\"", "\\\"")}\""
                : argument));

    private static Process Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new InvalidOperationException($"'{info.FileName}' could not be started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"'{info.FileName}' could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>Keeps the end of a stream for error messages and the printed result path.</summary>
    private sealed class Tail
    {
        private const int Limit = 4_000;
        private readonly StringBuilder _text = new();

        public string? LastLine { get; private set; }

        public string Text
        {
            get
            {
                lock (_text) return _text.ToString();
            }
        }

        public async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_text)
                {
                    if (line.Trim().Length > 0) LastLine = line;
                    _text.AppendLine(line);
                    if (_text.Length > Limit) _text.Remove(0, _text.Length - Limit);
                }
            }
        }
    }
}
