using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Automation;
using WindowVeil.Automation;
using WindowVeil.Platform;

namespace WindowVeil.UiTests;

/// <summary>A running instance of samples/WindowVeil.DemoTarget.</summary>
internal sealed class DemoTargetProcess : IDisposable
{
    private DemoTargetProcess(Process process, nint handle, string title)
    {
        Process = process;
        Handle = handle;
        Title = title;
    }

    /// <summary>The process.</summary>
    public Process Process { get; }

    /// <summary>Main window handle.</summary>
    public nint Handle { get; }

    /// <summary>Window title passed on the command line.</summary>
    public string Title { get; }

    /// <summary>A fresh snapshot of the main window.</summary>
    public TargetWindowInfo Snapshot() => Win32Window.Snapshot(Handle, Title) with { ProcessName = "WindowVeil.DemoTarget" };

    /// <summary>
    /// Path of the demo target: <c>WINDOWVEIL_DEMOTARGET</c>, else the path embedded at build time, else a search of
    /// <c>samples/WindowVeil.DemoTarget/bin</c> above the test directory.
    /// </summary>
    public static string LocateExecutable()
    {
        var env = Environment.GetEnvironmentVariable("WINDOWVEIL_DEMOTARGET");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var embedded = typeof(DemoTargetProcess).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DemoTargetPath")?.Value;
        foreach (var candidate in Candidates(embedded))
            if (File.Exists(candidate)) return candidate;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "samples", "WindowVeil.DemoTarget", "bin");
            if (!Directory.Exists(bin)) continue;
            var found = Directory.EnumerateFiles(bin, "WindowVeil.DemoTarget.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found;
        }
        throw new FileNotFoundException(
            "WindowVeil.DemoTarget.exe was not found. Build samples/WindowVeil.DemoTarget or set WINDOWVEIL_DEMOTARGET.");
    }

    private static IEnumerable<string> Candidates(string? embedded)
    {
        if (string.IsNullOrEmpty(embedded)) yield break;
        yield return Path.ChangeExtension(embedded, ".exe");
        yield return embedded;
    }

    /// <summary>Starts the demo target and waits until its window and UI Automation tree are ready.</summary>
    public static async Task<DemoTargetProcess> StartAsync(UiAutomationThread uia, int leftDip, int topDip, string title, bool topmost = true)
    {
        string path = LocateExecutable();
        var args = string.Create(CultureInfo.InvariantCulture, $"--left {leftDip} --top {topDip} --title \"{title}\"") + (topmost ? " --topmost" : "");
        var psi = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("dotnet", $"\"{path}\" {args}")
            : new ProcessStartInfo(path, args);
        psi.UseShellExecute = false;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + path);
        try
        {
            nint handle = 0;
            await Eventually.WaitAsync(() =>
            {
                if (process.HasExited) throw new InvalidOperationException($"The demo target exited with code {process.ExitCode}.");
                process.Refresh();
                handle = process.MainWindowHandle;
                return Task.FromResult(handle != 0);
            }, TimeSpan.FromSeconds(30), "demo target main window");

            // Wait until the content is in the UIA tree (layout done).
            await Eventually.WaitAsync(() => uia.InvokeAsync(_ =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(handle);
                    var check = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "StartupCheck"));
                    return check is not null && !check.Current.BoundingRectangle.IsEmpty;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }), TimeSpan.FromSeconds(20), "demo target UI Automation tree");
            return new DemoTargetProcess(process, handle, title);
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>Kills the process.</summary>
    public void Dispose()
    {
        Kill(Process);
        Process.Dispose();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }
}

/// <summary>Polling helper.</summary>
internal static class Eventually
{
    /// <summary>Polls <paramref name="condition"/> every 100 ms until it returns true or the timeout elapses.</summary>
    public static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await condition().ConfigureAwait(false)) return;
            if (sw.Elapsed > timeout) throw new TimeoutException($"Timed out after {timeout} waiting for {what}.");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    /// <summary>Polls <paramref name="produce"/> until <paramref name="accept"/> holds; returns the last value (or throws on timeout).</summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T>> produce, Func<T, bool> accept, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var value = await produce().ConfigureAwait(false);
            if (accept(value)) return value;
            if (sw.Elapsed > timeout) throw new TimeoutException($"Timed out after {timeout} waiting for {what}; last value: {value}.");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}
