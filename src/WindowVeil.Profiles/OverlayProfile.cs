using WindowVeil.Appearance;
using WindowVeil.Platform;

namespace WindowVeil.Profiles;

/// <summary>
/// The <c>application</c> section of a profile: which external window the rules apply to. Every specified
/// condition must match (see <see cref="WindowSelector"/>). Only stable properties are representable; process ids,
/// window handles and predicates are not, because they change between runs.
/// </summary>
public sealed record ProfileApplication
{
    /// <summary>Process name without <c>.exe</c> (case-insensitive).</summary>
    public string? ProcessName { get; init; }

    /// <summary>Title must contain this text (case-insensitive).</summary>
    public string? TitleContains { get; init; }

    /// <summary>Title must match this regular expression.</summary>
    public string? TitlePattern { get; init; }

    /// <summary>Exact window class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Whether only visible windows match. Default true.</summary>
    public bool RequireVisible { get; init; } = true;

    /// <summary>Whether untitled windows are ignored. Default true.</summary>
    public bool RequireTitle { get; init; } = true;

    /// <summary>True when no identifying condition is set.</summary>
    public bool IsEmpty => ProcessName is null && TitleContains is null && TitlePattern is null && ClassName is null;

    /// <summary>Converts to the selector used to attach a session.</summary>
    public WindowSelector ToSelector() => new()
    {
        ProcessName = ProcessName,
        TitleContains = TitleContains,
        TitlePattern = TitlePattern,
        ClassName = ClassName,
        RequireVisible = RequireVisible,
        RequireTitle = RequireTitle,
    };

    /// <summary>
    /// Converts a selector. Returns false (with an explanation) when the selector uses conditions a profile cannot
    /// store: <see cref="WindowSelector.Handle"/>, <see cref="WindowSelector.ProcessId"/> or <see cref="WindowSelector.Predicate"/>.
    /// </summary>
    public static bool TryFromSelector(WindowSelector selector, out ProfileApplication? application, out string? error)
    {
        ArgumentNullException.ThrowIfNull(selector);
        application = null;
        var unsupported = new List<string>();
        if (selector.Handle is not null) unsupported.Add("a window handle");
        if (selector.ProcessId is not null) unsupported.Add("a process id");
        if (selector.Predicate is not null) unsupported.Add("a custom predicate");
        if (unsupported.Count > 0)
        {
            error = $"The window selector uses {string.Join(", ", unsupported)}, which change between runs and cannot be stored in a profile. " +
                    "Select the window by process name, title or class name instead.";
            return false;
        }
        application = new ProfileApplication
        {
            ProcessName = selector.ProcessName,
            TitleContains = selector.TitleContains,
            TitlePattern = selector.TitlePattern,
            ClassName = selector.ClassName,
            RequireVisible = selector.RequireVisible,
            RequireTitle = selector.RequireTitle,
        };
        if (application.IsEmpty)
        {
            application = null;
            error = "The window selector has no condition (process name, title or class name); a profile would match every window.";
            return false;
        }
        error = null;
        return true;
    }
}

/// <summary>
/// A named set of overlay rules for one application, as stored in a JSON profile (see <see cref="ProfileSerializer"/>).
/// Rules are ordinary <see cref="OverlayRule"/> values; custom appearances are referenced through
/// <see cref="RegisteredAppearance"/> ids that the host application registers in an <see cref="AppearanceRegistry"/>.
/// </summary>
public sealed record OverlayProfile
{
    /// <summary>The schema version this library reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Display name of the profile.</summary>
    public required string Name { get; init; }

    /// <summary>Schema version. Loaded profiles are always migrated to <see cref="CurrentSchemaVersion"/>.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Which window the rules apply to.</summary>
    public required ProfileApplication Application { get; init; }

    /// <summary>The rules, in document order.</summary>
    public IReadOnlyList<OverlayRule> Rules { get; init; } = [];

    /// <summary>The selector derived from <see cref="Application"/>.</summary>
    public WindowSelector Selector => Application.ToSelector();

    /// <summary>
    /// Index of each rule in the source document's <c>rules</c> array (rules with errors are skipped while loading,
    /// so positions can differ). Used to report binding diagnostics at the original JSON path.
    /// </summary>
    internal IReadOnlyList<int>? SourceIndices { get; init; }

    /// <summary>
    /// Validates the rules for use in a session and returns those that are usable, plus diagnostics.
    /// When <paramref name="registry"/> is given, <see cref="RegisteredAppearance"/> ids must be registered in it.
    /// </summary>
    public ProfileBindingResult ToRules(AppearanceRegistry? registry = null) => ProfileBinder.Bind(this, registry);

    internal string RulePath(int position) =>
        $"$.rules[{(SourceIndices is { } s && position < s.Count ? s[position] : position)}]";

    /// <inheritdoc />
    public bool Equals(OverlayProfile? other) =>
        other is not null && Name == other.Name && SchemaVersion == other.SchemaVersion &&
        Application == other.Application && Rules.SequenceEqual(other.Rules);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, SchemaVersion, Application, Rules.Count);
}
