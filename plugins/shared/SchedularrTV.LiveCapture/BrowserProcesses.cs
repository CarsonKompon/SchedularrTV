using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SchedularrTV.LiveCapture;

/// <summary>
/// Makes sure hidden browsers never outlive the scheduler. On Windows each one is put in a job object that closes with
/// the scheduler, so Windows ends them however it stops (even killed, or its window closed). Each also notes its
/// process in its profile folder, and leftovers from an earlier run that ended some other way are ended the next time
/// the scheduler starts.
/// </summary>
public static class BrowserProcesses
{
    /// <summary>Where every hidden browser's throwaway profile goes.</summary>
    public static string ProfilesRoot => Path.Combine(Path.GetTempPath(), "schedularrtv-browser");

    private const string OwnerFile = "schedularrtv-owner.txt";

    private static int _swept;

    /// <summary>Takes charge of a browser just started: ends it with the scheduler, and notes it for the next start's sweep.</summary>
    public static void Adopt(Process process, string profile)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsJob.Assign(process);
        }

        try
        {
            File.WriteAllText(Path.Combine(profile, OwnerFile),
                $"{process.Id}|{process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Once per run, in the background: ends hidden browsers an earlier run left behind, and clears their profiles.</summary>
    public static void SweepOnce(ILogger? logger = null)
    {
        if (Interlocked.Exchange(ref _swept, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    int ended = SweepLeftovers();
                    if (ended > 0)
                    {
                        logger?.LogInformation("Ended {Count} hidden browser(s) left running by an earlier run.", ended);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            });
        }
    }

    /// <summary>Ends the leftover browsers (only ones started before this run), then deletes the profiles no browser holds.</summary>
    public static int SweepLeftovers()
    {
        if (!Directory.Exists(ProfilesRoot))
        {
            return 0;
        }

        DateTime thisRun = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        int ended = 0;
        foreach (string profile in Directory.EnumerateDirectories(ProfilesRoot))
        {
            string owner = Path.Combine(profile, OwnerFile);
            if (File.Exists(owner) && File.ReadAllText(owner).Split('|') is [var idText, var ticksText]
                && int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                && long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
            {
                try
                {
                    using Process process = Process.GetProcessById(id);

                    // The same process (not a new one given a reused id), and one from before this run.
                    DateTime started = process.StartTime.ToUniversalTime();
                    if (Math.Abs((started - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds) < 2 && started < thisRun)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                        ended++;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
            }

            try
            {
                // Only profiles of this run's browsers (still in use) are left; a locked one is tried again next time.
                if (Directory.GetCreationTimeUtc(profile) < thisRun)
                {
                    Directory.Delete(profile, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return ended;
    }

    /// <summary>A Windows job object that kills every process in it when the scheduler's handle to it closes (as it exits).</summary>
    private static class WindowsJob
    {
        private static readonly Lazy<IntPtr> Handle = new(Create);

        public static void Assign(Process process)
        {
            try
            {
                if (Handle.Value != IntPtr.Zero)
                {
                    AssignProcessToJobObject(Handle.Value, process.Handle);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        private static IntPtr Create()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose }
            };
            return SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>())
                ? job
                : IntPtr.Zero;
        }

        private const int ExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
