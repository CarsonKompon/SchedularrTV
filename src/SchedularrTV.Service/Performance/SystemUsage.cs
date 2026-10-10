using System.Globalization;
using System.Runtime.InteropServices;

namespace SchedularrTV.Service.Performance;

/// <summary>
/// The whole computer's CPU and memory use, for comparing what SchedularrTV and ErsatzTV take with everything else.
/// Windows and Linux are asked directly; elsewhere only the installed memory is known.
/// </summary>
public sealed class SystemUsage
{
    private (ulong Idle, ulong Total)? _previous;

    /// <summary>Percent of every core in use since the last call; null on the first call or where it can't be read.</summary>
    public double? CpuPercent()
    {
        (ulong Idle, ulong Total)? now = null;
        try
        {
            now = OperatingSystem.IsWindows() ? WindowsTimes()
                : OperatingSystem.IsLinux() ? ParseProcStat(File.ReadLines("/proc/stat").FirstOrDefault())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        (ulong Idle, ulong Total)? before = _previous;
        _previous = now;
        if (now is not { } current || before is not { } last || current.Total <= last.Total)
        {
            return null;
        }

        double total = current.Total - last.Total;
        double idle = current.Idle >= last.Idle ? current.Idle - last.Idle : 0;
        return Math.Clamp((1 - idle / total) * 100, 0, 100);
    }

    /// <summary>Memory in use and installed, in bytes.</summary>
    public static (long? Used, long? Total) Memory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
                }
            }
            else if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                return ParseMeminfo(File.ReadAllLines("/proc/meminfo"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        long installed = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long? known = installed > 0 ? installed : null;
        return (null, known);
    }

    /// <summary>Idle and total jiffies from the first (<c>cpu</c>) line of <c>/proc/stat</c>; iowait counts as idle.</summary>
    public static (ulong Idle, ulong Total)? ParseProcStat(string? line)
    {
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }

        ulong[] values = [.. line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : 0)];
        if (values.Length < 4)
        {
            return null;
        }

        // user nice system idle iowait irq softirq steal (guest time is already inside user).
        ulong total = values.Take(8).Aggregate(0UL, (sum, value) => sum + value);
        ulong idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (idle, total);
    }

    /// <summary>Used and total memory from <c>/proc/meminfo</c>: total less what's available (caches the kernel will give back count as free).</summary>
    public static (long? Used, long? Total) ParseMeminfo(IEnumerable<string> lines)
    {
        long? total = null, available = null;
        foreach (string line in lines)
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = Kilobytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = Kilobytes(line);
            }
        }

        long? used = total is { } t && available is { } a ? t - a : null;
        return (used, total);

        static long? Kilobytes(string line) =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kb) ? kb * 1024 : null;
    }

    private static (ulong Idle, ulong Total)? WindowsTimes()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            return null;
        }

        // Kernel time includes idle time.
        return (idle.Value, kernel.Value + user.Value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
