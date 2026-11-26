using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using WpfVisualTreeMcp.Automation.Infrastructure;
using WpfVisualTreeMcp.Shared.Ipc;
using WpfVisualTreeMcp.Shared.Models;

namespace WpfVisualTreeMcp.Server;

/// <summary>Starting an application and working with its top-level WPF windows; shared by MCP and CLI.</summary>
internal static class AppWindows
{
    internal const int DefaultLaunchTimeoutMs = 30000, DefaultWaitTimeoutMs = 10000;
    private static readonly string[] Actions = { "list", "wait", "wait_closed", "activate", "minimize", "maximize", "restore", "close" };

    /// <summary>Starts an executable, waits for its first window and attaches. Never stops the process it started.</summary>
    internal static async Task<object> LaunchAsync(IProcessManager processes, IIpcBridge bridge, string path, string? arguments,
        string? workingDirectory, int timeoutMs, bool autoInject, Func<string, Exception> error)
    {
        if (string.IsNullOrWhiteSpace(path)) throw error("path is required: the full path of the application's .exe file.");
        var file = Path.GetFullPath(path);
        if (LaunchRefusal(file) is { } refusal) throw error(refusal);
        if (workingDirectory != null && !Directory.Exists(workingDirectory)) throw error($"Working directory '{workingDirectory}' does not exist.");
        timeoutMs = Math.Clamp(timeoutMs, 1000, 120000);

        // Shell execute: the application must not inherit this server's stdout, which carries the MCP protocol.
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = true, Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(file)!
        };
        using var process = Process.Start(start) ?? throw error($"Windows did not start '{file}'.");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            process.Refresh();
            if (process.HasExited) throw error($"The application exited during startup with code {process.ExitCode}.");
            if (process.MainWindowHandle != IntPtr.Zero) break;
            if (clock.ElapsedMilliseconds >= timeoutMs)
                throw error($"Process {process.Id} was started but showed no window within {timeoutMs} ms. It keeps running; attach to it once its window is up.");
            await Task.Delay(100).ConfigureAwait(false);
        }

        InspectionSession session;
        try { session = await processes.AttachToProcessAsync(process.Id, null, autoInject).ConfigureAwait(false); }
        catch (Exception attach)
        {
            throw error($"Process {process.Id} was started and keeps running, but attaching failed: {attach.Message} Retry attaching to process {process.Id}.");
        }
        List<WpfWindowInfo>? windows = null;
        string? compatibility = null;
        if (session.InspectorReady)
        {
            compatibility = await bridge.GetCompatibilityNoteAsync(session.ProcessId).ConfigureAwait(false);
            // The window list is a convenience here; a busy startup must not fail the launch.
            try { windows = (await bridge.WindowAsync(new WindowRequest()).ConfigureAwait(false)).Windows; }
            catch (Exception) { }
        }
        return new
        {
            success = session.InspectorReady,
            process_id = session.ProcessId,
            startup_ms = clock.ElapsedMilliseconds,
            inspector_status = session.InspectorStatus,
            inspector_ready = session.InspectorReady,
            inspector_compatibility = compatibility ?? (session.InspectorReady ? "Inspector matches this server build." : null),
            windows
        };
    }

    /// <summary>Lists, awaits or acts on top-level WPF windows of the attached application.</summary>
    internal static async Task<object> WindowAsync(IIpcBridge bridge, string? action, string? windowHandle, string? title,
        string? automationId, int timeoutMs, Func<string, Exception> error, int pollMs = 200)
    {
        action = string.IsNullOrWhiteSpace(action) ? "list" : action.Trim().ToLowerInvariant().Replace('-', '_');
        if (!Actions.Contains(action)) throw error($"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}.");
        bool Matches(WpfWindowInfo window) =>
            (windowHandle == null || window.Handle == windowHandle)
            && (title == null || window.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            && (automationId == null || window.AutomationId == automationId || window.Name == automationId);
        var filtered = windowHandle != null || title != null || automationId != null;
        var windows = (await bridge.WindowAsync(new WindowRequest()).ConfigureAwait(false)).Windows;

        if (action == "list") return new { windows = filtered ? windows.Where(Matches).ToList() : windows };

        if (!filtered) throw error($"Action '{action}' needs a window: pass window_handle, title or automation_id. Open windows: {Describe(windows)}");

        if (action is "wait" or "wait_closed")
        {
            timeoutMs = Math.Clamp(timeoutMs, 0, 120000);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var visible = windows.Where(w => w.IsVisible && Matches(w)).ToList();
                if (action == "wait" && visible.Count > 0)
                    return new { window = visible[0], matches = visible.Count, waited_ms = clock.ElapsedMilliseconds, windows };
                if (action == "wait_closed" && visible.Count == 0)
                    return new { closed = true, waited_ms = clock.ElapsedMilliseconds, windows };
                if (clock.ElapsedMilliseconds >= timeoutMs)
                    throw error(action == "wait"
                        ? $"No visible window matched within {timeoutMs} ms. Open windows: {Describe(windows)}"
                        : $"The window was still open after {timeoutMs} ms. Open windows: {Describe(windows)}");
                await Task.Delay(pollMs).ConfigureAwait(false);
                windows = (await bridge.WindowAsync(new WindowRequest()).ConfigureAwait(false)).Windows;
            }
        }

        var targets = windows.Where(Matches).ToList();
        // A hidden window with the same title must not make a visible one ambiguous.
        if (targets.Count > 1 && targets.Count(w => w.IsVisible) == 1) targets = targets.Where(w => w.IsVisible).ToList();
        if (targets.Count != 1)
            throw error($"{targets.Count} windows match; exactly one is required. Open windows: {Describe(windows)}");
        var after = await bridge.WindowAsync(new WindowRequest { Action = action, ElementHandle = targets[0].Handle }).ConfigureAwait(false);
        return new
        {
            result = action == "close"
                ? $"close requested for '{targets[0].Title}'; a Closing handler may ask for confirmation or cancel it"
                : $"{action} applied to '{targets[0].Title}'",
            windows = after.Windows
        };
    }

    // Shells and script hosts run whatever their arguments say; .NET-based ones (pwsh) pass the .NET check.
    private static readonly string[] CommandHosts =
    {
        "cmd", "powershell", "powershell_ise", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
        "msiexec", "wsl", "bash", "sh", "conhost", "wt", "dotnet", "msbuild", "installutil", "regasm", "regsvcs",
        "csi", "csc", "vbc", "fsi", "fsc", "dotnet-script", "vstest.console", "testhost"
    };

    /// <summary>
    /// Why <paramref name="file"/> may not be launched, or null. Launch is limited to local WPF
    /// applications (the program's managed assembly references PresentationFramework) outside the
    /// Windows directory, and never starts a shell or script host: the tool is reachable by an agent
    /// whose instructions may come from text it read, so it must not run arbitrary commands.
    /// </summary>
    internal static string? LaunchRefusal(string file)
    {
        // Device (\\?\, \\.\) and UNC paths could point anywhere, including a network share.
        if (file.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(file))
            return $"'{file}' is not a local path; only applications on a local drive can be launched.";
        try
        {
            if (new DriveInfo(Path.GetPathRoot(file)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
                return $"'{file}' is not on a local drive; only applications on a local drive can be launched.";
        }
        catch (ArgumentException) { return $"'{file}' is not on a local drive."; }
        if (!File.Exists(file) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return $"'{file}' is not an existing .exe file.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && file.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return $"'{file}' is a Windows component; only WPF applications can be launched.";
        var name = Path.GetFileNameWithoutExtension(file);
        if (CommandHosts.Contains(name, StringComparer.OrdinalIgnoreCase))
            return $"'{name}' is a shell or command host; only WPF applications can be launched.";
        // .NET Framework apps are managed .exe files; .NET Core/5+ apps are a native host next to a managed .dll.
        var hostedDll = Path.ChangeExtension(file, ".dll");
        var assembly = IsManaged(file) ? file
            : IsManaged(hostedDll) && File.Exists(Path.ChangeExtension(file, ".runtimeconfig.json")) ? hostedDll : null;
        if (assembly == null) return $"'{file}' is not a .NET application; only WPF applications can be launched.";
        if (!ReferencesWpf(assembly))
            return $"'{file}' is not a WPF application (it does not reference PresentationFramework).";
        return null;
    }

    private static bool IsManaged(string file) => ReadMetadata(file, _ => true);

    private static bool ReferencesWpf(string file) => ReadMetadata(file, metadata =>
        metadata.AssemblyReferences.Any(handle =>
            metadata.GetString(metadata.GetAssemblyReference(handle).Name) == "PresentationFramework"));

    private static bool ReadMetadata(string file, Func<MetadataReader, bool> check)
    {
        try
        {
            if (!File.Exists(file)) return false;
            using var stream = File.OpenRead(file);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && check(reader.GetMetadataReader());
        }
        catch (Exception) { return false; }
    }

    private static string Describe(IEnumerable<WpfWindowInfo> windows)
    {
        var text = string.Join("; ", windows.Select(w => $"{w.Handle} '{w.Title}'" + (w.IsVisible ? "" : " (hidden)")));
        return text.Length == 0 ? "none" : text;
    }
}
