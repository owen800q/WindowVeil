using System.Diagnostics;
using Microsoft.Win32;

namespace WindowVeil.RetroCapDemo;

/// <summary>
/// Launches the real, unmodified RetroCap.exe for demos and UI tests and restores the machine state afterwards.
/// </summary>
/// <remarks>
/// RetroCap rewrites (or deletes) its <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RetroCap</c> value on every
/// launch to match its own "start with Windows" setting, and stores <c>%APPDATA%\RetroCap\settings.json</c>. That is
/// RetroCap's existing behavior, not WindowVeil's: this host backs both up before launching and restores them on dispose.
/// Run the automated scenarios in an isolated account or VM anyway.
/// </remarks>
public sealed class RetroCapHost : IAsyncDisposable
{
    /// <summary>Title of RetroCap's main settings window.</summary>
    public const string MainWindowTitle = "RetroCap - Screen Capture";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RetroCap";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroCap", "settings.json");

    private readonly string? _runBackup;
    private readonly bool _runExisted;
    private readonly byte[]? _settingsBackup;
    private Process? _process;

    private RetroCapHost(string exePath)
    {
        ExePath = exePath;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            _runBackup = key?.GetValue(RunValue) as string;
            _runExisted = _runBackup is not null;
        }
        _settingsBackup = File.Exists(SettingsPath) ? File.ReadAllBytes(SettingsPath) : null;
    }

    /// <summary>Path of the executable.</summary>
    public string ExePath { get; }

    /// <summary>The RetroCap process.</summary>
    public Process Process => _process ?? throw new InvalidOperationException("RetroCap is not running.");

    /// <summary>Main window handle.</summary>
    public nint MainWindow { get; private set; }

    /// <summary>File version of the executable.</summary>
    public string FileVersion => FileVersionInfo.GetVersionInfo(ExePath).FileVersion ?? "unknown";

    /// <summary>
    /// Starts RetroCap and waits for its main window. Fails if another RetroCap instance is already running,
    /// because RetroCap is single-instance and we must not interfere with a user's own instance.
    /// </summary>
    public static async Task<RetroCapHost> StartAsync(string exePath, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("RetroCap.exe not found.", exePath);
        var existing = Process.GetProcessesByName("RetroCap");
        if (existing.Length > 0)
        {
            bool kill = Environment.GetEnvironmentVariable("WINDOWVEIL_KILL_EXISTING_RETROCAP") == "1";
            if (!kill)
                throw new InvalidOperationException(
                    "RetroCap is already running. Close it first (or set WINDOWVEIL_KILL_EXISTING_RETROCAP=1 on an isolated test machine).");
            foreach (var p in existing) { try { p.Kill(); p.WaitForExit(5000); } catch (InvalidOperationException) { } finally { p.Dispose(); } }
        }

        var host = new RetroCapHost(exePath);
        try
        {
            host._process = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! })
                ?? throw new InvalidOperationException("Failed to start RetroCap.");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                host._process.Refresh();
                if (host._process.HasExited) throw new InvalidOperationException($"RetroCap exited during startup (code {host._process.ExitCode}).");
                var hwnd = host._process.MainWindowHandle;
                if (hwnd != 0 && host._process.MainWindowTitle == MainWindowTitle && !Win32.GetFrameBounds(hwnd).IsEmpty)
                {
                    host.MainWindow = hwnd;
                    // Give WPF a moment to finish its first layout pass, then wait until the window is responsive.
                    while (!Win32.IsResponsive(hwnd, TimeSpan.FromSeconds(1)) && sw.Elapsed < timeout) await Task.Delay(50, ct);
                    return host;
                }
                await Task.Delay(100, ct);
            }
            throw new TimeoutException($"RetroCap's main window did not appear within {timeout}.");
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>Kills RetroCap and restores the Run value and settings file.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_process is { } p)
        {
            try
            {
                if (!p.HasExited) { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(new CancellationTokenSource(5000).Token); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
            _process = null;
        }
        RestoreState();
    }

    private void RestoreState()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
        {
            if (_runExisted) key.SetValue(RunValue, _runBackup!);
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        if (_settingsBackup is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllBytes(SettingsPath, _settingsBackup);
        }
        else if (File.Exists(SettingsPath))
        {
            File.Delete(SettingsPath);
        }
    }
}
