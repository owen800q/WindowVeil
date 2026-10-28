namespace WindowVeil.Profiles;

/// <summary>Severity of a <see cref="ProfileDiagnostic"/>.</summary>
public enum ProfileDiagnosticSeverity
{
    /// <summary>Informational (for example "the profile was migrated from an older schema").</summary>
    Info,

    /// <summary>Something suspicious that does not prevent loading (for example an unknown property).</summary>
    Warning,

    /// <summary>The element it refers to is invalid and was not loaded.</summary>
    Error,
}

/// <summary>
/// A problem found while loading, binding or exporting a profile. <see cref="Path"/> is a JSON path into the
/// document, such as <c>$.rules[0].locator.type</c>, so editors and logs can point at the exact location.
/// </summary>
/// <param name="Severity">Severity.</param>
/// <param name="Path">JSON path of the offending element (<c>$</c> for the whole document).</param>
/// <param name="Message">Human-readable description, including how to fix it where possible.</param>
public sealed record ProfileDiagnostic(ProfileDiagnosticSeverity Severity, string Path, string Message)
{
    /// <summary>1-based line number for syntax errors (null when not applicable).</summary>
    public long? LineNumber { get; init; }

    /// <summary>0-based byte position within the line for syntax errors (null when not applicable).</summary>
    public long? BytePositionInLine { get; init; }

    /// <summary>True for <see cref="ProfileDiagnosticSeverity.Error"/>.</summary>
    public bool IsError => Severity == ProfileDiagnosticSeverity.Error;

    internal static ProfileDiagnostic Error(string path, string message) => new(ProfileDiagnosticSeverity.Error, path, message);

    internal static ProfileDiagnostic Warning(string path, string message) => new(ProfileDiagnosticSeverity.Warning, path, message);

    internal static ProfileDiagnostic Info(string path, string message) => new(ProfileDiagnosticSeverity.Info, path, message);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Severity} {Path}: {Message}{(LineNumber is { } l ? $" (line {l}, position {BytePositionInLine ?? 0})" : "")}";
}

/// <summary>Thrown when a profile cannot be used or exported; carries the diagnostics that explain why.</summary>
public sealed class ProfileException : WindowVeilException
{
    /// <summary>Creates an exception from diagnostics.</summary>
    public ProfileException(string message, IReadOnlyList<ProfileDiagnostic> diagnostics)
        : base(WindowVeilErrorCode.InvalidRule, Format(message, diagnostics))
    {
        Diagnostics = diagnostics ?? [];
    }

    /// <summary>The diagnostics (errors first).</summary>
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; }

    private static string Format(string message, IReadOnlyList<ProfileDiagnostic>? diagnostics)
    {
        var errors = diagnostics?.Where(d => d.IsError).ToArray() ?? [];
        if (errors.Length == 0) return message;
        const int max = 10;
        var lines = errors.Take(max).Select(d => "  " + d);
        var more = errors.Length > max ? $"{Environment.NewLine}  ... and {errors.Length - max} more" : "";
        return message + Environment.NewLine + string.Join(Environment.NewLine, lines) + more;
    }
}
