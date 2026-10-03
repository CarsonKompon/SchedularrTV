using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SchedularrTV.Speech.Piper;

/// <summary>Where the plugin's paths resolve from: relative paths are taken from the scheduler's data folder.</summary>
public sealed class PiperPaths(string dataDirectory)
{
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>The path a setting means: blank is the default, relative is under the data folder.</summary>
    public string Resolve(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback
        : Path.IsPathRooted(value.Trim().Trim('"')) ? value.Trim().Trim('"')
        : Path.GetFullPath(Path.Combine(DataDirectory, value.Trim().Trim('"')));
}

/// <summary>A voice in the Piper voice catalogue (huggingface.co/rhasspy/piper-voices).</summary>
public sealed record PiperCatalogVoice(
    string Key,
    string Name,
    string LanguageCode,
    string Language,
    string Quality,
    int Speakers,
    long SizeBytes,
    string ModelPath,
    string ConfigPath);

/// <summary>Downloads the Piper program and voices.</summary>
public static class PiperDownloads
{
    /// <summary>The last release of the standalone Piper program (the project now ships it only as a Python package).</summary>
    public const string ProgramRelease = "2023.11.14-2";

    public const string VoicesBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SchedularrTV-Piper/1.0");
        return http;
    }

    /// <summary>The release file for this computer, or null when Piper isn't built for it.</summary>
    public static string? ProgramAsset(OSPlatform? os = null, Architecture? architecture = null)
    {
        Architecture arch = architecture ?? RuntimeInformation.OSArchitecture;
        bool Is(OSPlatform platform) => os is { } given ? given == platform : RuntimeInformation.IsOSPlatform(platform);

        if (Is(OSPlatform.Windows))
        {
            // Windows on Arm runs the x64 build.
            return arch is Architecture.X64 or Architecture.Arm64 ? "piper_windows_amd64.zip" : null;
        }

        if (Is(OSPlatform.OSX))
        {
            return arch switch
            {
                Architecture.Arm64 => "piper_macos_aarch64.tar.gz",
                Architecture.X64 => "piper_macos_x64.tar.gz",
                _ => null
            };
        }

        if (Is(OSPlatform.Linux))
        {
            return arch switch
            {
                Architecture.X64 => "piper_linux_x86_64.tar.gz",
                Architecture.Arm64 => "piper_linux_aarch64.tar.gz",
                Architecture.Arm => "piper_linux_armv7l.tar.gz",
                _ => null
            };
        }

        return null;
    }

    public static string ProgramUrl(string asset) => $"https://github.com/rhasspy/piper/releases/download/{ProgramRelease}/{asset}";

    /// <summary>
    /// Downloads Piper and puts the program, with the libraries and espeak data beside it, in the folder of
    /// <paramref name="executablePath"/>. Returns the program's path.
    /// </summary>
    public static async Task<string> InstallProgramAsync(string executablePath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string asset = ProgramAsset() ?? throw new PlatformNotSupportedException("Piper isn't available for this kind of computer.");
        string target = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        string staging = Path.Combine(Path.GetTempPath(), $"schedularrtv-piper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string archive = Path.Combine(staging, asset);
            await DownloadAsync(ProgramUrl(asset), archive, progress, cancellationToken).ConfigureAwait(false);

            string unpacked = Path.Combine(staging, "unpacked");
            Directory.CreateDirectory(unpacked);
            if (asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, unpacked, overwriteFiles: true);
            }
            else
            {
                await using FileStream file = File.OpenRead(archive);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, unpacked, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
            }

            string programName = OperatingSystem.IsWindows() ? "piper.exe" : "piper";
            string program = Directory.EnumerateFiles(unpacked, programName, SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault() ?? throw new InvalidDataException($"The download had no {programName} in it.");

            CopyFolder(Path.GetDirectoryName(program)!, target);
            string installed = Path.Combine(target, programName);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(installed, File.GetUnixFileMode(installed)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            return installed;
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Every voice in the catalogue, by language then name.</summary>
    public static async Task<IReadOnlyList<PiperCatalogVoice>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        await using Stream body = await Http.GetStreamAsync(VoicesBase + "voices.json", cancellationToken).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseCatalog(json.RootElement);
    }

    public static IReadOnlyList<PiperCatalogVoice> ParseCatalog(JsonElement root)
    {
        List<PiperCatalogVoice> voices = [];
        if (root.ValueKind != JsonValueKind.Object)
        {
            return voices;
        }

        foreach (JsonProperty entry in root.EnumerateObject())
        {
            JsonElement voice = entry.Value;
            if (voice.ValueKind != JsonValueKind.Object || !voice.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? model = null;
            string? config = null;
            long size = 0;
            foreach (JsonProperty file in files.EnumerateObject())
            {
                if (file.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                {
                    model = file.Name;
                    size = file.Value.TryGetProperty("size_bytes", out JsonElement bytes) && bytes.TryGetInt64(out long value) ? value : 0;
                }
                else if (file.Name.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
                {
                    config = file.Name;
                }
            }

            if (model is null || config is null)
            {
                continue;
            }

            JsonElement language = voice.TryGetProperty("language", out JsonElement lang) ? lang : default;
            string code = Text(language, "code") ?? entry.Name.Split('-')[0];
            string name = Text(language, "name_english") ?? code;
            string? country = Text(language, "country_english");
            voices.Add(new PiperCatalogVoice(
                Key: Path.GetFileNameWithoutExtension(model),
                Name: Text(voice, "name") ?? entry.Name,
                LanguageCode: code,
                Language: country is { Length: > 0 } ? $"{name} ({country})" : name,
                Quality: Text(voice, "quality") ?? "",
                Speakers: voice.TryGetProperty("num_speakers", out JsonElement speakers) && speakers.TryGetInt32(out int count) ? count : 1,
                SizeBytes: size,
                ModelPath: model,
                ConfigPath: config));
        }

        return [.. voices.OrderBy(voice => voice.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => QualityOrder(voice.Quality))];
    }

    /// <summary>Downloads a voice's model and its settings into the voices folder.</summary>
    public static async Task InstallVoiceAsync(PiperCatalogVoice voice, string voicesFolder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(voicesFolder);
        string model = Path.Combine(voicesFolder, voice.Key + ".onnx");
        await DownloadAsync(FileUrl(voice.ConfigPath), model + ".json", null, cancellationToken).ConfigureAwait(false);
        await DownloadAsync(FileUrl(voice.ModelPath), model, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a voice's model and settings from the voices folder.</summary>
    public static void RemoveVoice(string key, string voicesFolder)
    {
        if (key.Contains('/') || key.Contains('\\') || key.Contains(".."))
        {
            throw new ArgumentException("That isn't a voice name.", nameof(key));
        }

        string model = Path.Combine(voicesFolder, key + ".onnx");
        File.Delete(model);
        File.Delete(model + ".json");
    }

    /// <summary>The voices in the folder: each model's file name without <c>.onnx</c>.</summary>
    public static IReadOnlyList<string> InstalledVoices(string voicesFolder) =>
        Directory.Exists(voicesFolder)
            ? [.. Directory.EnumerateFiles(voicesFolder, "*.onnx").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    public static string FileUrl(string path) =>
        VoicesBase + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static int QualityOrder(string quality) => quality switch
    {
        "x_low" => 0,
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        _ => 4
    };

    /// <summary>Downloads to a temporary name beside the target and moves it into place when complete.</summary>
    private static async Task DownloadAsync(string url, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        string partial = destination + ".part";
        try
        {
            await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream file = File.Create(partial))
            {
                byte[] buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total is > 0)
                    {
                        progress?.Report((double)done / total.Value);
                    }
                }
            }

            File.Move(partial, destination, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
