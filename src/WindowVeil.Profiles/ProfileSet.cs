namespace WindowVeil.Profiles;

/// <summary>The result of loading one file of a <see cref="ProfileSet"/>.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Result">Load result (each file has its own diagnostics).</param>
public sealed record ProfileFileResult(string Path, ProfileLoadResult Result);

/// <summary>
/// Profiles for several applications, one JSON file each (for example <c>%ProgramData%\MyApp\profiles\*.json</c>).
/// Every file is loaded independently: a broken file does not prevent the others from loading.
/// </summary>
public sealed class ProfileSet
{
    /// <summary>Default maximum number of files read from one directory.</summary>
    public const int DefaultMaxFiles = 1000;

    private ProfileSet(IReadOnlyList<ProfileFileResult> files, IReadOnlyList<ProfileDiagnostic> diagnostics)
    {
        Files = files;
        Diagnostics = diagnostics;
    }

    /// <summary>Every file that was read, sorted by file name (ordinal).</summary>
    public IReadOnlyList<ProfileFileResult> Files { get; }

    /// <summary>Set-level diagnostics (for example "too many files"); per-file diagnostics are in <see cref="Files"/>.</summary>
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Profiles from files that loaded without errors. Files with errors are excluded even if some of their rules
    /// were valid, so a partially broken profile is never applied silently; inspect <see cref="Files"/> for details.
    /// </summary>
    public IReadOnlyList<OverlayProfile> Profiles => Files.Where(f => f.Result.Succeeded).Select(f => f.Result.Profile!).ToArray();

    /// <summary>True when any file (or the set itself) has an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.IsError) || Files.Any(f => !f.Result.Succeeded);

    /// <summary>
    /// Loads every <c>*.json</c> file in <paramref name="directory"/> (not recursive). Files beyond
    /// <paramref name="maxFiles"/> are skipped with an error diagnostic.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static ProfileSet LoadDirectory(string directory, ProfileLoadOptions? options = null, int maxFiles = DefaultMaxFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Profile directory '{directory}' does not exist.");
        var paths = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            // EnumerateFiles' pattern also matches "*.jsonx" on some platforms; be exact.
            .Where(p => string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToArray();
        var diagnostics = new List<ProfileDiagnostic>();
        if (paths.Length > maxFiles)
        {
            diagnostics.Add(ProfileDiagnostic.Error("$", $"The directory contains {paths.Length} profile files; only the first {maxFiles} were loaded."));
            paths = paths[..maxFiles];
        }
        var files = paths.Select(p => new ProfileFileResult(Path.GetFullPath(p), ProfileSerializer.LoadFile(p, options))).ToArray();
        return new ProfileSet(files, diagnostics);
    }

    /// <summary>Creates a set from already loaded results (for example profiles embedded as resources).</summary>
    public static ProfileSet FromResults(IEnumerable<ProfileFileResult> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new ProfileSet(files.ToArray(), []);
    }
}
