using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SchedularrTV.Writing.LocalAi;

/// <summary>
/// Running the built-in model and Whisper on the graphics card, through Vulkan: it works with NVIDIA, AMD and Intel
/// graphics on Windows and Linux and needs nothing but the graphics driver. The Vulkan builds of llama.cpp and
/// whisper.cpp are downloaded as add-ons beside the processor ones, which stay as the fallback. (On a Mac with Apple
/// silicon the usual engine already uses the graphics, through Metal.)
/// </summary>
public static class LocalAiGpu
{
    public const string Automatic = "Automatic";
    public const string Off = "Off";

    private static readonly Lock ProbeGate = new();
    private static bool? _driverFound;

    /// <summary>Whether there are graphics card builds for this kind of computer (64-bit Intel or AMD, Windows or Linux).</summary>
    public static bool Supported =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    /// <summary>
    /// Whether a Vulkan driver is installed (its loader library can be opened). Without one the graphics card builds
    /// can't load, so they aren't tried.
    /// </summary>
    public static bool DriverFound
    {
        get
        {
            lock (ProbeGate)
            {
                return _driverFound ??= Probe();
            }
        }
    }

    /// <summary>The NuGet package with llama.cpp's Vulkan build for this system.</summary>
    public static string LlamaPackageUrl
    {
        get
        {
            string package = OperatingSystem.IsLinux() ? "llamasharp.backend.vulkan.linux" : "llamasharp.backend.vulkan.windows";
            return $"https://api.nuget.org/v3-flatcontainer/{package}/{LlamaEngine.BackendVersion}/{package}.{LlamaEngine.BackendVersion}.nupkg";
        }
    }

    /// <summary>The NuGet package with whisper.cpp's Vulkan builds (Windows and Linux in one).</summary>
    public static string WhisperPackageUrl =>
        $"https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.vulkan/{WhisperEngine.RuntimeVersion}/whisper.net.runtime.vulkan.{WhisperEngine.RuntimeVersion}.nupkg";

    /// <summary>Whether a native library with this file name (<c>ggml-vulkan.dll</c>…) is loaded in this process.</summary>
    public static bool IsLoaded(string fileName)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(module.FileName), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        return false;
    }

    /// <summary>A native library's file name on this system: <c>name.dll</c>, or <c>libname.so</c>.</summary>
    public static string LibraryFile(string name) => OperatingSystem.IsWindows() ? $"{name}.dll" : $"lib{name}.so";

    private static bool Probe()
    {
        if (!Supported)
        {
            return false;
        }

        string[] names = OperatingSystem.IsWindows() ? ["vulkan-1.dll"] : ["libvulkan.so.1", "libvulkan.so"];
        foreach (string name in names)
        {
            if (NativeLibrary.TryLoad(name, out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }
}
