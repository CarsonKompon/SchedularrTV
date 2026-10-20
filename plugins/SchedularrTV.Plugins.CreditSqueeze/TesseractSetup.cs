using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SchedularrTV.CreditSqueeze;

public sealed record TesseractAvailability(bool Ready, string? Problem);

/// <summary>Opt-in, app-local OCR tools. Downloads are never started by background rendering.</summary>
public sealed class TesseractSetup(CreditSqueezeSettings settings) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    public bool CanDownload => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;
    public string ManualInstructions => OperatingSystem.IsMacOS()
        ? "Install Tesseract on the scheduler computer with Homebrew: brew install tesseract tesseract-lang. Then retry."
        : "Install Tesseract and the required languages on the scheduler computer (Debian/Ubuntu: sudo apt install tesseract-ocr tesseract-ocr-eng), or set TesseractPath in the plugin settings. Then retry.";

    public Task<TesseractAvailability> CheckAsync(CancellationToken cancellationToken) => CheckAsync(settings.Ocr, cancellationToken);

    private static async Task<TesseractAvailability> CheckAsync(CreditsOcrOptions options, CancellationToken cancellationToken)
    {
        try
        {
            string version = await CreditsOcrTools.RunAsync(options.Executable, ["--version"], TimeSpan.FromSeconds(8), cancellationToken);
            if (!version.Contains("tesseract", StringComparison.OrdinalIgnoreCase)) return new(false, "The configured executable is not Tesseract.");
            List<string> args = ["--list-langs"];
            if (options.DataDirectory is { } data) args.AddRange(["--tessdata-dir", data]);
            string output = await CreditsOcrTools.RunAsync(options.Executable, args, TimeSpan.FromSeconds(8), cancellationToken);
            var installed = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            string[] missing = Languages(options.Language).Where(language => !installed.Contains(language)).ToArray();
            return missing.Length == 0 ? new(true, null) : new(false, $"Missing OCR language data: {string.Join(", ", missing)}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
    }

    public static string[] Languages(string language)
    {
        string[] languages = language.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (languages.Length == 0 || languages.Any(value => !Regex.IsMatch(value, @"^(script/)?[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("Choose valid OCR language codes, for example eng or eng+fra.");
        return languages;
    }

    public async Task InstallAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!CanDownload) throw new InvalidOperationException(ManualInstructions);
        string[] languages = Languages(settings.Ocr.Language);
        string configured = settings.ConfiguredTesseract ?? "";
        await _gate.WaitAsync(cancellationToken);
        string? work = null;
        try
        {
            if ((await CheckAsync(cancellationToken)).Ready) return;
            Directory.CreateDirectory(Path.Combine(settings.DataDirectory, "tools"));
            work = Path.Combine(settings.DataDirectory, "tools", ".tesseract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            // Extract checksum-verified upstream distributions rather than running their installers:
            // no elevation, registry edits, PATH changes, or uninstall of an existing OCR engine.
            progress?.Report("Downloading extraction tools…");
            string bootstrap = Path.Combine(work, "7zr.exe"), archive = Path.Combine(work, "7zip.exe");
            await Download("https://github.com/ip7z/7zip/releases/download/25.01/7zr.exe", bootstrap,
                "27cbe3d5804ad09e90bbcaa916da0d5c3b0be9462d0e0fb6cb54be5ed9030875", cancellationToken);
            await Download("https://github.com/ip7z/7zip/releases/download/25.01/7z2501-x64.exe", archive,
                "78afa2a1c773caf3cf7edf62f857d2a8a5da55fb0fff5da416074c0d28b2b55f", cancellationToken);
            string extractor = Path.Combine(work, "extractor");
            await CreditsOcrTools.RunAsync(bootstrap, ["x", archive, "-o" + extractor, "-y"], TimeSpan.FromMinutes(2), cancellationToken);
            progress?.Report("Downloading Tesseract 5.5.3…");
            string package = Path.Combine(work, "tesseract.exe");
            await Download("https://github.com/tesseract-ocr/tesseract/releases/download/5.5.3/tesseract-ocr-w64-setup-5.5.3.20260724.exe", package,
                "bee9e3434bd94fd65387d9be28cd467a41f61b1275383b55b0f59a1331270ae4", cancellationToken);
            string extracted = Path.Combine(work, "ocr");
            await CreditsOcrTools.RunAsync(Path.Combine(extractor, "7z.exe"), ["x", package, "-o" + extracted, "-y"], TimeSpan.FromMinutes(2), cancellationToken);
            string exe = Path.Combine(extracted, "tesseract.exe");
            if (!File.Exists(exe)) throw new InvalidOperationException("The Tesseract download did not contain its executable.");
            foreach (string language in languages)
            {
                progress?.Report($"Downloading OCR language: {language}…");
                string data = Path.Combine(extracted, "tessdata", language.Replace('/', Path.DirectorySeparatorChar) + ".traineddata");
                Directory.CreateDirectory(Path.GetDirectoryName(data)!);
                await Download($"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/{language}.traineddata", data, null, cancellationToken);
            }
            progress?.Report("Verifying OCR installation…");
            var status = await CheckAsync(new CreditsOcrOptions(exe, string.Join('+', languages), 1, DataDirectory: Path.Combine(extracted, "tessdata")), cancellationToken);
            if (!status.Ready) throw new InvalidOperationException(status.Problem);
            await File.WriteAllTextAsync(Path.Combine(extracted, ".ready"), configured, cancellationToken);
            // Only publish a complete install. A failure leaves the previous tools usable.
            string destination = settings.ManagedTesseractFolder;
            string? backup = null;
            if (Directory.Exists(destination)) { backup = Path.Combine(work, "previous"); Directory.Move(destination, backup); }
            try { Directory.Move(extracted, destination); }
            catch { if (backup is not null) Directory.Move(backup, destination); throw; }
        }
        finally
        {
            try { if (work is not null && Directory.Exists(work)) Directory.Delete(work, true); }
            finally { _gate.Release(); }
        }
    }

    private async Task Download(string url, string path, string? sha256, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(path)) await input.CopyToAsync(output, cancellationToken);
        if (sha256 is not null)
        {
            await using var file = File.OpenRead(path);
            string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            if (actual != sha256) throw new InvalidOperationException("The downloaded tool failed its SHA-256 integrity check. Please retry.");
        }
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
