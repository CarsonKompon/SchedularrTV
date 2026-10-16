using System.Diagnostics;

namespace SchedularrTV.GeneratedPromos;

/// <summary>FFmpeg's native VP8/VP9 decoders discard WebM alpha; libvpx preserves it.</summary>
internal static class PromoVideoDecoder
{
    public static async Task<string?> FindAsync(string ffprobePath, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var info = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name",
                     "-of", "default=noprint_wrappers=1:nokey=1", path])
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffprobe could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string codec = (await output.ConfigureAwait(false)).Trim();
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? codec switch { "vp8" => "libvpx", "vp9" => "libvpx-vp9", _ => null } : null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
