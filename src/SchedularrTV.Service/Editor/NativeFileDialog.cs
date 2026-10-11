using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SchedularrTV.Service.Editor;

/// <summary>What to ask the system's file dialog for.</summary>
/// <param name="Extensions">With the dot (<c>.mp3</c>); empty for any file.</param>
/// <param name="StartFolder">Where it opens; null for the system's choice.</param>
public sealed record FileDialogRequest(string Title, IReadOnlyList<string> Extensions, string? StartFolder, bool Multiple = false, bool Folder = false)
{
    /// <summary>What the filter is called in the dialog, from the kind of file asked for.</summary>
    public string FilterName { get; init; } = "Supported files";
}

/// <summary>What the dialog gave back: the paths chosen (none when cancelled), or why it couldn't be shown.</summary>
public sealed record FileDialogResult(IReadOnlyList<string> Paths, string? Problem = null)
{
    public static readonly FileDialogResult Cancelled = new([]);

    public static FileDialogResult Failed(string problem) => new([], problem);
}

/// <summary>
/// The operating system's own file dialog: the Windows Explorer dialog, the macOS one (through AppleScript), or GTK or
/// KDE's on Linux (zenity or kdialog). The paths are the scheduler's, so it opens on the computer running SchedularrTV;
/// the editor only offers it to a browser on that computer. One is open at a time.
/// </summary>
public static class NativeFileDialog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<FileDialogResult> ShowAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return FileDialogResult.Failed("A file dialog is already open on this computer; finish with that one first.");
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return await WindowsDialog.ShowAsync(request).ConfigureAwait(false);
            }

            if (OperatingSystem.IsMacOS())
            {
                return await RunAsync("osascript", ["-e", AppleScript(request)], cancellationToken).ConfigureAwait(false);
            }

            return await LinuxAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    // --- macOS -------------------------------------------------------------------------------------------------

    /// <summary>An AppleScript that brings its own dialog to the front and prints each POSIX path chosen on a line.</summary>
    public static string AppleScript(FileDialogRequest request)
    {
        var script = new StringBuilder("activate\n");
        string chooser = request.Folder ? "choose folder" : "choose file";
        var options = new StringBuilder($" with prompt {Quote(request.Title)}");
        if (!request.Folder && request.Extensions.Count > 0)
        {
            options.Append($" of type {{{string.Join(", ", request.Extensions.Select(extension => Quote(extension.TrimStart('.'))))}}}");
        }

        if (request.StartFolder is { } start && Directory.Exists(start))
        {
            options.Append($" default location (POSIX file {Quote(start)})");
        }

        if (request.Multiple)
        {
            options.Append(" with multiple selections allowed");
            script.Append($"set chosen to {chooser}{options}\n");
            script.Append("set out to \"\"\nrepeat with f in chosen\nset out to out & POSIX path of f & linefeed\nend repeat\nreturn out");
        }
        else
        {
            script.Append($"return POSIX path of ({chooser}{options})");
        }

        return script.ToString();

        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    // --- Linux -------------------------------------------------------------------------------------------------

    private static async Task<FileDialogResult> LinuxAsync(FileDialogRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return FileDialogResult.Failed("This computer has no desktop for a file dialog to open on; type the path instead.");
        }

        FileDialogResult zenity = await RunAsync("zenity", ZenityArguments(request), cancellationToken).ConfigureAwait(false);
        if (zenity.Problem is not MissingProgram)
        {
            return zenity;
        }

        FileDialogResult kdialog = await RunAsync("kdialog", KdialogArguments(request), cancellationToken).ConfigureAwait(false);
        return kdialog.Problem is MissingProgram
            ? FileDialogResult.Failed("No file dialog is installed: install zenity (or kdialog), or type the path instead.")
            : kdialog;
    }

    public static IReadOnlyList<string> ZenityArguments(FileDialogRequest request)
    {
        List<string> arguments = ["--file-selection", $"--title={request.Title}"];
        if (request.Folder)
        {
            arguments.Add("--directory");
        }

        if (request.Multiple)
        {
            arguments.AddRange(["--multiple", "--separator=\n"]);
        }

        if (request.StartFolder is { } start && Directory.Exists(start))
        {
            arguments.Add($"--filename={start.TrimEnd('/')}/");
        }

        if (!request.Folder && request.Extensions.Count > 0)
        {
            arguments.Add($"--file-filter={request.FilterName} | {Patterns(request.Extensions)}");
            arguments.Add("--file-filter=All files | *");
        }

        return arguments;
    }

    public static IReadOnlyList<string> KdialogArguments(FileDialogRequest request)
    {
        string start = request.StartFolder is { } folder && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (request.Folder)
        {
            return ["--title", request.Title, "--getexistingdirectory", start];
        }

        List<string> arguments = ["--title", request.Title, "--getopenfilename", start];
        arguments.Add(request.Extensions.Count > 0 ? $"{Patterns(request.Extensions)}|{request.FilterName}" : "*|All files");
        if (request.Multiple)
        {
            arguments.AddRange(["--multiple", "--separate-output"]);
        }

        return arguments;
    }

    // Both lower and upper case, since GTK's patterns are case-sensitive.
    private static string Patterns(IEnumerable<string> extensions) =>
        string.Join(' ', extensions.SelectMany(extension => new[] { $"*{extension.ToLowerInvariant()}", $"*{extension.ToUpperInvariant()}" }).Distinct(StringComparer.Ordinal));

    // --- Running a dialog program -------------------------------------------------------------------------------

    private const string MissingProgram = "missing";

    /// <summary>Runs a dialog program; a path a line on success, nothing on cancel (exit code 1).</summary>
    private static async Task<FileDialogResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception();
        }
        catch (Win32Exception)
        {
            return FileDialogResult.Failed(MissingProgram);
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            string chosen = await output.ConfigureAwait(false);
            string problem = (await error.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                // Cancelling: zenity and kdialog exit with 1, AppleScript reports error -128.
                return process.ExitCode == 1 && (problem.Length == 0 || problem.Contains("-128", StringComparison.Ordinal) || program != "osascript")
                    ? FileDialogResult.Cancelled
                    : FileDialogResult.Failed($"The file dialog couldn't be shown: {(problem.Length > 0 ? problem : $"{program} exited with {process.ExitCode}")}");
            }

            return new FileDialogResult(SplitPaths(chosen));
        }
    }

    /// <summary>The paths a dialog program printed, one a line; a folder's trailing slash is kept off.</summary>
    public static IReadOnlyList<string> SplitPaths(string output) =>
        [.. output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Length > 1 ? path.TrimEnd('/') : path)
            .Where(path => path.Length > 0)];

    // --- Windows -----------------------------------------------------------------------------------------------

    /// <summary>The Explorer file dialog (IFileOpenDialog), on its own STA thread as COM requires.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static class WindowsDialog
    {
        public static Task<FileDialogResult> ShowAsync(FileDialogRequest request)
        {
            var done = new TaskCompletionSource<FileDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    done.TrySetResult(Show(request));
                }
                catch (Exception ex)
                {
                    done.TrySetResult(FileDialogResult.Failed($"The file dialog couldn't be shown: {ex.Message}"));
                }
            })
            {
                IsBackground = true,
                Name = "File dialog"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return done.Task;
        }

        private static FileDialogResult Show(FileDialogRequest request)
        {
            if (!Environment.UserInteractive)
            {
                return FileDialogResult.Failed("SchedularrTV is running as a service, with no desktop for a file dialog; type the path instead.");
            }

            var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
            IntPtr owner = IntPtr.Zero;
            try
            {
                uint options = FosForceFileSystem | FosNoChangeDir | FosPathMustExist;
                options |= request.Folder ? FosPickFolders : FosFileMustExist;
                if (request.Multiple)
                {
                    options |= FosAllowMultiSelect;
                }

                dialog.SetOptions(options);
                dialog.SetTitle(request.Title);

                if (!request.Folder && request.Extensions.Count > 0)
                {
                    string spec = string.Join(';', request.Extensions.Select(extension => "*" + extension));
                    dialog.SetFileTypes(2, [new FilterSpec(request.FilterName, spec), new FilterSpec("All files", "*.*")]);
                    dialog.SetFileTypeIndex(1);
                }

                if (request.StartFolder is { } start && Directory.Exists(start)
                    && SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem folder) == 0)
                {
                    dialog.SetFolder(folder);
                }

                // The scheduler isn't the app in front (the browser is), so the dialog gets a topmost owner to open over it.
                owner = CreateOwner();
                int shown = dialog.Show(owner);
                if (shown == ErrorCancelled)
                {
                    return FileDialogResult.Cancelled;
                }

                Marshal.ThrowExceptionForHR(shown);

                List<string> paths = [];
                dialog.GetResults(out IShellItemArray results);
                results.GetCount(out uint count);
                for (uint index = 0; index < count; index++)
                {
                    results.GetItemAt(index, out IShellItem item);
                    item.GetDisplayName(SigdnFileSysPath, out IntPtr name);
                    try
                    {
                        if (Marshal.PtrToStringUni(name) is { Length: > 0 } path)
                        {
                            paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(name);
                    }
                }

                return new FileDialogResult(paths);
            }
            finally
            {
                if (owner != IntPtr.Zero)
                {
                    DestroyWindow(owner);
                }

                Marshal.FinalReleaseComObject(dialog);
            }
        }

        /// <summary>An invisible, topmost window under the mouse that owns the dialog, so it opens in front where the user is.</summary>
        private static IntPtr CreateOwner()
        {
            GetCursorPos(out Point cursor);
            IntPtr window = CreateWindowEx(WsExTopmost | WsExToolWindow, "STATIC", "SchedularrTV", WsPopup, cursor.X, cursor.Y, 0, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window != IntPtr.Zero)
            {
                ShowWindow(window, SwShowNa);
                SetForegroundWindow(window);
            }

            return window;
        }

        private const int ErrorCancelled = unchecked((int)0x800704C7);
        private const uint SigdnFileSysPath = 0x80058000;
        private const uint FosNoChangeDir = 0x8;
        private const uint FosPickFolders = 0x20;
        private const uint FosForceFileSystem = 0x40;
        private const uint FosAllowMultiSelect = 0x200;
        private const uint FosPathMustExist = 0x800;
        private const uint FosFileMustExist = 0x1000;
        private const uint WsExTopmost = 0x8;
        private const uint WsExToolWindow = 0x80;
        private const uint WsPopup = 0x80000000;
        private const int SwShowNa = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FilterSpec(string name, string spec)
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Name = name;
            [MarshalAs(UnmanagedType.LPWStr)] public string Spec = spec;
        }

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCoClass;

        // IModalWindow, then IFileDialog, then IFileOpenDialog, in vtable order.
        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IShellItemArray items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetPropertyStore(int flags, ref Guid interfaceId, out IntPtr result);
            void GetPropertyDescriptionList(IntPtr keyType, ref Guid interfaceId, out IntPtr result);
            void GetAttributes(int flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
            void EnumItems(out IntPtr items);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            out IShellItem item);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);
    }
}
