using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SchedularrTV.Service.Performance;

/// <summary>One running process: its id, its parent's, and its program name (without .exe).</summary>
public sealed record ProcessEntry(int Pid, int ParentPid, string Name);

/// <summary>
/// Every running process with its parent, so the processes SchedularrTV and ErsatzTV started (ffmpeg, browsers,
/// ersatztv-channel) can be found by walking down from them. .NET has no way to ask for a process's parent, so each
/// system is asked its own way: a Toolhelp snapshot on Windows, <c>/proc</c> on Linux, and <c>ps</c> elsewhere.
/// </summary>
public static class ProcessTable
{
    public static IReadOnlyList<ProcessEntry> Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows();
            }

            if (OperatingSystem.IsLinux() && Directory.Exists("/proc"))
            {
                return ReadProc();
            }

            return ReadPs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception or ExternalException)
        {
            return [];
        }
    }

    /// <summary>
    /// Every process descended from <paramref name="roots"/> (the roots included), by id. A child must have started after
    /// its parent: Windows keeps a dead parent's id on its children, and that id may since have been given to something else.
    /// </summary>
    public static IReadOnlyList<ProcessEntry> Descendants(IReadOnlyList<ProcessEntry> table, IEnumerable<int> roots, Func<int, DateTime?> startedAt)
    {
        ILookup<int, ProcessEntry> children = table.Where(entry => entry.Pid != entry.ParentPid).ToLookup(entry => entry.ParentPid);
        Dictionary<int, ProcessEntry> byPid = table.GroupBy(entry => entry.Pid).ToDictionary(group => group.Key, group => group.First());

        List<ProcessEntry> found = [];
        HashSet<int> seen = [];
        Queue<int> queue = new();
        foreach (int root in roots)
        {
            if (byPid.TryGetValue(root, out ProcessEntry? entry) && seen.Add(root))
            {
                found.Add(entry);
                queue.Enqueue(root);
            }
        }

        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            DateTime? parentStart = startedAt(parent);
            foreach (ProcessEntry child in children[parent])
            {
                if (!seen.Add(child.Pid))
                {
                    continue;
                }

                if (parentStart is { } before && startedAt(child.Pid) is { } after && after < before)
                {
                    continue;
                }

                found.Add(child);
                queue.Enqueue(child.Pid);
            }
        }

        return found;
    }

    /// <summary>A program name as compared everywhere here: no folder, no .exe, lower case.</summary>
    public static string Normalize(string name)
    {
        // Either separator, whatever this system's is: Path.GetFileName on Linux and macOS leaves a Windows path whole.
        string trimmed = name.Trim();
        string file = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file).ToLowerInvariant();
    }

    private static List<ProcessEntry> ReadProc()
    {
        List<ProcessEntry> entries = [];
        foreach (string folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }

            try
            {
                if (ParseStat(File.ReadAllText(Path.Combine(folder, "stat"))) is { } stat)
                {
                    entries.Add(new ProcessEntry(pid, stat.ParentPid, Normalize(stat.Name)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone between listing and reading, or not ours to read.
            }
        }

        return entries;
    }

    /// <summary>The name and parent from a <c>/proc/&lt;pid&gt;/stat</c> line: <c>pid (name) state ppid …</c>, the name possibly holding spaces or brackets.</summary>
    public static (string Name, int ParentPid)? ParseStat(string stat)
    {
        int open = stat.IndexOf('(');
        int close = stat.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return null;
        }

        string[] rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return rest.Length > 1 && int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parent)
            ? (stat[(open + 1)..close], parent)
            : null;
    }

    private static List<ProcessEntry> ReadPs()
    {
        var start = new ProcessStartInfo("ps", "-A -o pid= -o ppid= -o comm=")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process? ps = Process.Start(start);
        if (ps is null)
        {
            return [];
        }

        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(5000);

        List<ProcessEntry> entries = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parent))
            {
                entries.Add(new ProcessEntry(pid, parent, Normalize(parts[2])));
            }
        }

        return entries;
    }

    private static List<ProcessEntry> ReadWindows()
    {
        List<ProcessEntry> entries = [];
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            return entries;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return entries;
            }

            do
            {
                entries.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, Normalize(entry.ExeFile ?? string.Empty)));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return entries;
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
