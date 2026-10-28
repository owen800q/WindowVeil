using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowVeil.Profiles;

/// <summary>
/// One step of the profile schema history: rewrites a document from <see cref="FromVersion"/> to <see cref="ToVersion"/>.
/// Migrations operate on the raw JSON tree before it is validated, so they can rename and restructure freely.
/// They must not trust the document: report problems as diagnostics instead of throwing.
/// </summary>
public interface IProfileMigration
{
    /// <summary>Version this migration reads.</summary>
    int FromVersion { get; }

    /// <summary>Version this migration produces (greater than <see cref="FromVersion"/>).</summary>
    int ToVersion { get; }

    /// <summary>Rewrites <paramref name="document"/> in place. The migrator updates <c>schemaVersion</c> afterwards.</summary>
    void Migrate(JsonObject document, ICollection<ProfileDiagnostic> diagnostics);
}

/// <summary>Outcome of <see cref="ProfileMigrator.Migrate"/>.</summary>
public sealed record ProfileMigrationResult
{
    /// <summary>The version found in the document (0 when <c>schemaVersion</c> was absent).</summary>
    public int? OriginalVersion { get; init; }

    /// <summary>The version of the document after migration.</summary>
    public int? Version { get; init; }

    /// <summary>Diagnostics produced by version detection and migrations.</summary>
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>True when the document is now at <see cref="OverlayProfile.CurrentSchemaVersion"/>.</summary>
    public bool Succeeded => Version == OverlayProfile.CurrentSchemaVersion && !Diagnostics.Any(d => d.IsError);
}

/// <summary>
/// Upgrades profile documents from older schema versions to <see cref="OverlayProfile.CurrentSchemaVersion"/> by
/// applying an ordered chain of <see cref="IProfileMigration"/> steps. Documents from a newer WindowVeil are rejected
/// with an explicit error rather than being half-understood.
/// </summary>
/// <remarks>
/// Version history:
/// <list type="bullet">
/// <item><b>0</b> — the pre-release 0.x draft format (see <see cref="V0ToV1Migration"/>). Documents without a
/// <c>schemaVersion</c> are treated as version 0.</item>
/// <item><b>1</b> — the current format (see <c>docs/profile.schema.json</c>).</item>
/// </list>
/// </remarks>
public sealed class ProfileMigrator
{
    private readonly IProfileMigration[] _migrations;

    /// <summary>Creates a migrator from migration steps. Steps must not overlap and must move forward.</summary>
    /// <exception cref="ArgumentException">Two steps start at the same version or a step does not move forward.</exception>
    public ProfileMigrator(IEnumerable<IProfileMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        _migrations = migrations.OrderBy(m => m.FromVersion).ToArray();
        foreach (var m in _migrations)
        {
            if (m is null) throw new ArgumentException("Migrations cannot be null.", nameof(migrations));
            if (m.ToVersion <= m.FromVersion) throw new ArgumentException($"Migration {m.GetType().Name} does not move forward ({m.FromVersion} -> {m.ToVersion}).", nameof(migrations));
            if (m.ToVersion > OverlayProfile.CurrentSchemaVersion) throw new ArgumentException($"Migration {m.GetType().Name} targets unknown version {m.ToVersion}.", nameof(migrations));
        }
        var dup = _migrations.GroupBy(m => m.FromVersion).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new ArgumentException($"Several migrations start at version {dup.Key}.", nameof(migrations));
    }

    /// <summary>The built-in migration chain.</summary>
    public static ProfileMigrator Default { get; } = new([new V0ToV1Migration()]);

    /// <summary>Registered steps, ordered by <see cref="IProfileMigration.FromVersion"/>.</summary>
    public IReadOnlyList<IProfileMigration> Migrations => _migrations;

    /// <summary>
    /// Reads <c>schemaVersion</c> (absent or null means 0) and upgrades the document in place to the current version.
    /// Emits an <see cref="ProfileDiagnosticSeverity.Info"/> diagnostic when a migration ran and an error when the
    /// version is invalid, newer than supported, or has no migration path.
    /// </summary>
    public ProfileMigrationResult Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = new List<ProfileDiagnostic>();
        var version = ReadVersion(document, diagnostics);
        if (version is null) return new ProfileMigrationResult { Diagnostics = diagnostics };
        int original = version.Value;
        if (original > OverlayProfile.CurrentSchemaVersion)
        {
            diagnostics.Add(ProfileDiagnostic.Error("$.schemaVersion",
                $"The profile uses schema version {original}, which was produced by a newer WindowVeil. " +
                $"This version supports schema versions up to {OverlayProfile.CurrentSchemaVersion}; update WindowVeil to load it."));
            return new ProfileMigrationResult { OriginalVersion = original, Version = original, Diagnostics = diagnostics };
        }

        int current = original;
        while (current < OverlayProfile.CurrentSchemaVersion)
        {
            var step = _migrations.FirstOrDefault(m => m.FromVersion == current);
            if (step is null)
            {
                diagnostics.Add(ProfileDiagnostic.Error("$.schemaVersion", $"No migration is available from schema version {current} to {OverlayProfile.CurrentSchemaVersion}."));
                return new ProfileMigrationResult { OriginalVersion = original, Version = current, Diagnostics = diagnostics };
            }
            int errorsBefore = diagnostics.Count(d => d.IsError);
            try
            {
                step.Migrate(document, diagnostics);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or JsonException)
            {
                diagnostics.Add(ProfileDiagnostic.Error("$", $"Migrating from schema version {step.FromVersion} to {step.ToVersion} failed: {ex.Message}"));
            }
            if (diagnostics.Count(d => d.IsError) > errorsBefore)
                return new ProfileMigrationResult { OriginalVersion = original, Version = current, Diagnostics = diagnostics };
            current = step.ToVersion;
            document["schemaVersion"] = current;
        }
        if (current != original)
            diagnostics.Add(ProfileDiagnostic.Info("$.schemaVersion",
                $"The profile was migrated from schema version {original} to {current}. Save it again to upgrade the file."));
        return new ProfileMigrationResult { OriginalVersion = original, Version = current, Diagnostics = diagnostics };
    }

    private static int? ReadVersion(JsonObject document, List<ProfileDiagnostic> diagnostics)
    {
        if (!document.TryGetPropertyValue("schemaVersion", out var node) || node is null) return 0;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number &&
            (v.TryGetValue(out int i) || (v.TryGetValue(out JsonElement e) && e.TryGetInt32(out i))) && i >= 0)
            return i;
        diagnostics.Add(ProfileDiagnostic.Error("$.schemaVersion", "'schemaVersion' must be a non-negative integer."));
        return null;
    }
}
