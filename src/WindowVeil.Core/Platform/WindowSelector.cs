using System.Text.RegularExpressions;

namespace WindowVeil.Platform;

/// <summary>
/// Chooses which external window a session attaches to. Selectors are re-evaluated on reconnection,
/// so they must describe the window by stable properties (process, title, class) rather than by HWND.
/// </summary>
public sealed record WindowSelector
{
    /// <summary>Process name without <c>.exe</c> (case-insensitive).</summary>
    public string? ProcessName { get; init; }

    /// <summary>Exact process id. Process ids are not stable across restarts; prefer <see cref="ProcessName"/>.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Exact window handle. Not stable across restarts; disables reconnection to new instances.</summary>
    public nint? Handle { get; init; }

    /// <summary>Title must contain this text (case-insensitive).</summary>
    public string? TitleContains { get; init; }

    /// <summary>Title must match this regular expression.</summary>
    public string? TitlePattern { get; init; }

    /// <summary>Exact window class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Whether only visible, non-cloaked windows match. Default true.</summary>
    public bool RequireVisible { get; init; } = true;

    /// <summary>Whether windows without a title are ignored. Default true (filters out helper and tray windows).</summary>
    public bool RequireTitle { get; init; } = true;

    /// <summary>Optional additional predicate.</summary>
    public Func<TargetWindowInfo, bool>? Predicate { get; init; }

    /// <summary>Selects the main window of a process by name.</summary>
    public static WindowSelector ByProcessName(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processName = processName[..^4];
        return new WindowSelector { ProcessName = processName };
    }

    /// <summary>Selects a window by title substring.</summary>
    public static WindowSelector ByTitle(string titleContains) => new() { TitleContains = titleContains };

    /// <summary>Selects a specific window handle (no reconnection to other windows).</summary>
    public static WindowSelector ByHandle(nint handle) => new() { Handle = handle, RequireVisible = false, RequireTitle = false };

    /// <summary>Selects windows of a specific process id.</summary>
    public static WindowSelector ByProcessId(int processId) => new() { ProcessId = processId };

    /// <summary>Adds a title condition.</summary>
    public WindowSelector WithTitle(string titleContains) => this with { TitleContains = titleContains };

    /// <summary>Adds a class name condition.</summary>
    public WindowSelector WithClassName(string className) => this with { ClassName = className };

    /// <summary>True when the window satisfies every condition.</summary>
    public bool Matches(TargetWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Handle is { } h && window.Handle != h) return false;
        if (ProcessId is { } pid && window.ProcessId != pid) return false;
        if (ProcessName is not null && !string.Equals(window.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
        if (ClassName is not null && !string.Equals(window.ClassName, ClassName, StringComparison.Ordinal)) return false;
        if (RequireTitle && string.IsNullOrWhiteSpace(window.Title)) return false;
        if (TitleContains is not null && !window.Title.Contains(TitleContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (TitlePattern is not null && !Regex.IsMatch(window.Title, TitlePattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
        if (RequireVisible && (!window.IsVisible || window.Bounds.IsEmpty)) return false;
        if (Predicate is not null && !Predicate(window)) return false;
        return true;
    }

    /// <summary>
    /// Picks the best match: foreground windows first, then the largest visible window (main windows are
    /// usually larger than tool and splash windows).
    /// </summary>
    public TargetWindowInfo? SelectBest(IEnumerable<TargetWindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Where(Matches)
            .OrderByDescending(w => w.IsForeground)
            .ThenByDescending(w => w.IsVisible && !w.IsMinimized)
            .ThenByDescending(w => w.Bounds.Area)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>();
        if (ProcessName is not null) parts.Add($"process={ProcessName}");
        if (ProcessId is not null) parts.Add($"pid={ProcessId}");
        if (Handle is not null) parts.Add($"hwnd=0x{Handle:X}");
        if (TitleContains is not null) parts.Add($"title~'{TitleContains}'");
        if (TitlePattern is not null) parts.Add($"title=/{TitlePattern}/");
        if (ClassName is not null) parts.Add($"class={ClassName}");
        return "Window(" + string.Join(", ", parts) + ")";
    }
}
