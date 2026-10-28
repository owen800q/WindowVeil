namespace WindowVeil.Profiles;

/// <summary>
/// Limits applied when reading profiles. Profiles are treated as untrusted input: every limit is enforced before
/// the corresponding work is done, so a hostile document cannot exhaust memory or CPU.
/// </summary>
public sealed record ProfileLoadOptions
{
    /// <summary>Default limits.</summary>
    public static ProfileLoadOptions Default { get; } = new();

    /// <summary>Maximum document size in bytes (UTF-8). Default 1 MiB.</summary>
    public int MaxDocumentBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum number of rules. Default 1000.</summary>
    public int MaxRules { get; init; } = 1000;

    /// <summary>
    /// Maximum nesting of locators (<c>fallback</c> chains, <c>ancestor</c> and <c>scope</c> selectors). Default 8.
    /// </summary>
    public int MaxLocatorDepth { get; init; } = 8;

    /// <summary>Maximum JSON nesting depth accepted by the parser. Default 64.</summary>
    public int MaxJsonDepth { get; init; } = 64;

    /// <summary>Maximum length of any string value. Default 4096 characters.</summary>
    public int MaxStringLength { get; init; } = 4096;

    /// <summary>Maximum length of a regular expression pattern. Default 1024 characters.</summary>
    public int MaxRegexLength { get; init; } = 1024;

    /// <summary>Maximum number of entries in a fallback chain or a <c>mustNotOverlap</c> list, and of registered-appearance parameters. Default 64.</summary>
    public int MaxListItems { get; init; } = 64;

    /// <summary>Migrations applied to documents with an older schema version. Default <see cref="ProfileMigrator.Default"/>.</summary>
    public ProfileMigrator Migrator { get; init; } = ProfileMigrator.Default;

    internal void Validate()
    {
        if (MaxDocumentBytes <= 0 || MaxRules <= 0 || MaxLocatorDepth <= 0 || MaxJsonDepth <= 0 || MaxStringLength <= 0 || MaxRegexLength <= 0 || MaxListItems <= 0)
            throw new ArgumentException("Profile load limits must be positive.");
        ArgumentNullException.ThrowIfNull(Migrator);
    }
}
