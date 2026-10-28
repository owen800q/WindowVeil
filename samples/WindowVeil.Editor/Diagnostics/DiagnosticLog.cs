using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Threading;

namespace WindowVeil.Editor.Diagnostics;

/// <summary>Severity of a diagnostics entry.</summary>
public enum LogLevel
{
    /// <summary>Verbose detail.</summary>
    Debug,

    /// <summary>Normal information.</summary>
    Info,

    /// <summary>Something worth attention.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>One line of the diagnostics panel.</summary>
/// <param name="Timestamp">Local time.</param>
/// <param name="Level">Severity.</param>
/// <param name="Source">Short source tag (session, rule, profile, picker, engine...).</param>
/// <param name="Message">Text.</param>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message)
{
    /// <summary>Time formatted for the list.</summary>
    public string Time => Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Short level tag.</summary>
    public string LevelText => Level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warning => "WRN", _ => "ERR" };

    /// <inheritdoc />
    public override string ToString() => $"{Time} {LevelText} [{Source}] {Message}";
}

/// <summary>
/// The diagnostics panel's bounded, timestamped log. <see cref="Write"/> can be called from any thread (session
/// threads, the UI Automation thread, the WPF overlay dispatcher); entries are batched and appended on the UI
/// dispatcher, and the oldest entries are dropped beyond <see cref="Capacity"/>.
/// </summary>
public sealed class DiagnosticLog
{
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentQueue<LogEntry> _incoming = new();
    private int _flushScheduled;

    /// <summary>Creates a log bound to the UI dispatcher.</summary>
    public DiagnosticLog(Dispatcher dispatcher, int capacity = 2000)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 10);
        Capacity = capacity;
    }

    /// <summary>Maximum number of kept entries.</summary>
    public int Capacity { get; }

    /// <summary>Entries (UI thread only).</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>Raised on the UI thread after new entries were appended.</summary>
    public event EventHandler? EntriesAppended;

    /// <summary>Appends an entry (any thread).</summary>
    public void Write(LogLevel level, string source, string message)
    {
        _incoming.Enqueue(new LogEntry(DateTime.Now, level, source, message));
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shut down: nothing to show any more.
            }
        }
    }

    /// <summary>Information.</summary>
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);

    /// <summary>Warning.</summary>
    public void Warn(string source, string message) => Write(LogLevel.Warning, source, message);

    /// <summary>Error.</summary>
    public void Error(string source, string message) => Write(LogLevel.Error, source, message);

    /// <summary>Clears every entry (UI thread).</summary>
    public void Clear() => Entries.Clear();

    /// <summary>The whole log as text (UI thread).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    private void Flush()
    {
        Volatile.Write(ref _flushScheduled, 0);
        bool any = false;
        while (_incoming.TryDequeue(out var entry))
        {
            Entries.Add(entry);
            any = true;
        }
        while (Entries.Count > Capacity) Entries.RemoveAt(0);
        if (any) EntriesAppended?.Invoke(this, EventArgs.Empty);
    }
}
