using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowVeil.Profiles.Internal;

namespace WindowVeil.Profiles;

/// <summary>Result of loading a profile.</summary>
public sealed record ProfileLoadResult
{
    /// <summary>
    /// The profile, or null when the document as a whole is unusable (malformed JSON, too large, newer schema,
    /// missing <c>application</c>, ...). Rules with errors are left out of the profile and reported in
    /// <see cref="Diagnostics"/>, so a non-null profile can still have <see cref="Succeeded"/> = false.
    /// </summary>
    public OverlayProfile? Profile { get; init; }

    /// <summary>Errors, warnings and informational messages, in document order.</summary>
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The file the profile was read from, when loaded with <see cref="ProfileSerializer.LoadFile"/>.</summary>
    public string? SourcePath { get; init; }

    /// <summary>True when a profile was produced and no diagnostic is an error.</summary>
    public bool Succeeded => Profile is not null && !Diagnostics.Any(d => d.IsError);

    /// <summary>Errors only.</summary>
    public IEnumerable<ProfileDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

    /// <summary>Returns the profile or throws a <see cref="ProfileException"/> listing every error.</summary>
    /// <exception cref="ProfileException">The profile has errors.</exception>
    public OverlayProfile EnsureSuccess() => Succeeded
        ? Profile!
        : throw new ProfileException($"The profile{(SourcePath is null ? "" : $" '{SourcePath}'")} is invalid.", Diagnostics);
}

/// <summary>Result of <see cref="ProfileSerializer.TryToJson"/>.</summary>
public sealed record ProfileSaveResult
{
    /// <summary>Indented camelCase JSON, or null when the profile cannot be represented.</summary>
    public string? Json { get; init; }

    /// <summary>Diagnostics; paths refer to the JSON that would have been written.</summary>
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>True when <see cref="Json"/> is available.</summary>
    public bool Succeeded => Json is not null;
}

/// <summary>
/// Reads and writes JSON overlay profiles (schema v1, see <c>docs/profile.schema.json</c>).
/// </summary>
/// <remarks>
/// <para>Profiles are untrusted input. Loading never instantiates types named in the document, never throws for
/// invalid content (every problem is a <see cref="ProfileDiagnostic"/> with a JSON path), and enforces the limits of
/// <see cref="ProfileLoadOptions"/> (document size, rule count, nesting depth, string and regex length).
/// Regular expressions are compiled with a match timeout.</para>
/// <para>Older documents are upgraded by <see cref="ProfileMigrator"/>; documents from a newer WindowVeil are rejected.</para>
/// <para>Writing produces indented camelCase JSON that loads back to an equal profile. Rules whose appearance or
/// locator has no profile representation (for example a WPF appearance created in code) are refused: register an
/// appearance factory and use <see cref="Appearance.RegisteredAppearance"/> instead.</para>
/// </remarks>
public static class ProfileSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Profiles are files, not HTML: keep non-ASCII text (window titles, labels) readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Loads a profile from JSON text.</summary>
    public static ProfileLoadResult Load(string json, ProfileLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        options ??= ProfileLoadOptions.Default;
        options.Validate();
        // Cheap pre-check: UTF-8 needs at least one byte per UTF-16 code unit.
        if (json.Length > options.MaxDocumentBytes) return TooLarge(json.Length, options, null);
        int byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > options.MaxDocumentBytes) return TooLarge(byteCount, options, null);
        return LoadCore(Encoding.UTF8.GetBytes(json), options, null);
    }

    /// <summary>Loads a profile from UTF-8 bytes (a leading byte order mark is accepted).</summary>
    public static ProfileLoadResult Load(ReadOnlySpan<byte> utf8Json, ProfileLoadOptions? options = null)
    {
        options ??= ProfileLoadOptions.Default;
        options.Validate();
        if (utf8Json.Length > options.MaxDocumentBytes) return TooLarge(utf8Json.Length, options, null);
        return LoadCore(utf8Json.ToArray(), options, null);
    }

    /// <summary>
    /// Loads a profile file. I/O problems (missing file, access denied) are reported as diagnostics; the file size
    /// limit is checked before the file is read.
    /// </summary>
    public static ProfileLoadResult LoadFile(string path, ProfileLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= ProfileLoadOptions.Default;
        options.Validate();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Failed($"Profile file '{path}' does not exist.", path);
            if (info.Length > options.MaxDocumentBytes) return TooLarge(info.Length, options, path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = ReadBounded(stream, options.MaxDocumentBytes, out bool tooLarge);
            if (tooLarge) return TooLarge(options.MaxDocumentBytes, options, path, atLeast: true);
            return LoadCore(bytes, options, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return Failed($"Cannot read profile file '{path}': {ex.Message}", path);
        }
    }

    /// <summary>
    /// Loads a profile from a stream. At most <see cref="ProfileLoadOptions.MaxDocumentBytes"/> + 1 bytes are read,
    /// so an endless or huge stream is rejected without being buffered. The stream is not disposed.
    /// </summary>
    public static async Task<ProfileLoadResult> LoadAsync(Stream stream, ProfileLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= ProfileLoadOptions.Default;
        options.Validate();
        var buffer = new byte[Math.Min(options.MaxDocumentBytes + 1L, 64 * 1024)];
        using var ms = new MemoryStream();
        while (true)
        {
            int n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            ms.Write(buffer, 0, n);
            if (ms.Length > options.MaxDocumentBytes) return TooLarge(options.MaxDocumentBytes, options, null, atLeast: true);
        }
        return LoadCore(ms.ToArray(), options, null);
    }

    /// <summary>Converts a profile to indented camelCase JSON.</summary>
    /// <exception cref="ProfileException">Something in the profile has no profile representation (see <see cref="TryToJson"/>).</exception>
    public static string ToJson(OverlayProfile profile)
    {
        var result = TryToJson(profile);
        return result.Json ?? throw new ProfileException("The profile cannot be saved.", result.Diagnostics);
    }

    /// <summary>Converts a profile to JSON, reporting anything that cannot be represented instead of throwing.</summary>
    public static ProfileSaveResult TryToJson(OverlayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var diagnostics = new List<ProfileDiagnostic>();
        var node = new ProfileWriter(diagnostics).Write(profile);
        return new ProfileSaveResult { Json = node?.ToJsonString(WriteOptions), Diagnostics = diagnostics };
    }

    /// <summary>Writes a profile to a file (UTF-8, no byte order mark).</summary>
    /// <exception cref="ProfileException">The profile cannot be represented; nothing is written.</exception>
    public static void Save(OverlayProfile profile, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = ToJson(profile);
        File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Writes a profile to a stream (UTF-8). The stream is not disposed.</summary>
    /// <exception cref="ProfileException">The profile cannot be represented; nothing is written.</exception>
    public static async Task SaveAsync(OverlayProfile profile, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = Encoding.UTF8.GetBytes(ToJson(profile) + Environment.NewLine);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ core

    private static ProfileLoadResult LoadCore(byte[] bytes, ProfileLoadOptions options, string? sourcePath)
    {
        var diagnostics = new List<ProfileDiagnostic>();
        var span = bytes.AsMemory();
        if (span.Span.StartsWith(Utf8Bom)) span = span[Utf8Bom.Length..];

        var documentOptions = new JsonDocumentOptions
        {
            MaxDepth = options.MaxJsonDepth,
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // 1. Syntax, depth and duplicate keys. JsonDocument reports precise line/position information.
        try
        {
            using var document = JsonDocument.Parse(span, documentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Result(null, [ProfileDiagnostic.Error("$", "A profile must be a JSON object.")], sourcePath);
            FindDuplicateKeys(document.RootElement, "$", diagnostics);
        }
        catch (JsonException ex)
        {
            return Result(null, [Malformed(ex)], sourcePath);
        }
        if (diagnostics.Any(d => d.IsError)) return Result(null, diagnostics, sourcePath);

        // 2. Version detection and migration on the mutable tree.
        JsonObject root;
        try
        {
            root = JsonNode.Parse(span.Span, nodeOptions: null, documentOptions)!.AsObject();
        }
        catch (JsonException ex)
        {
            return Result(null, [Malformed(ex)], sourcePath);
        }
        var migration = options.Migrator.Migrate(root);
        diagnostics.AddRange(migration.Diagnostics);
        if (!migration.Succeeded) return Result(null, diagnostics, sourcePath);
        if (migration.OriginalVersion != migration.Version)
        {
            // Re-parse so migrated values behave exactly like parsed ones (and the migrated tree is bounded again).
            try
            {
                root = JsonNode.Parse(root.ToJsonString(), nodeOptions: null, documentOptions)!.AsObject();
            }
            catch (JsonException ex)
            {
                diagnostics.Add(ProfileDiagnostic.Error("$", $"The migrated profile is invalid: {ex.Message}"));
                return Result(null, diagnostics, sourcePath);
            }
        }

        // 3. Semantic validation.
        var profile = new ProfileReader(options, diagnostics).ReadProfile(root);
        return Result(profile, diagnostics, sourcePath);
    }

    private static void FindDuplicateKeys(JsonElement element, string path, List<ProfileDiagnostic> diagnostics)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                {
                    var childPath = ProfileReader.Prop(path, p.Name);
                    if (!seen.Add(p.Name)) diagnostics.Add(ProfileDiagnostic.Error(childPath, $"Property '{p.Name}' appears more than once."));
                    FindDuplicateKeys(p.Value, childPath, diagnostics);
                }
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in element.EnumerateArray()) FindDuplicateKeys(item, $"{path}[{i++}]", diagnostics);
                break;
        }
    }

    private static ProfileDiagnostic Malformed(JsonException ex)
    {
        // JsonException line numbers are 0-based; diagnostics use 1-based lines like editors do.
        var message = ex.Message;
        int cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        if (cut > 0) message = message[..cut];
        return new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path, $"Malformed JSON: {message}")
        {
            LineNumber = ex.LineNumber + 1,
            BytePositionInLine = ex.BytePositionInLine,
        };
    }

    private static byte[] ReadBounded(Stream stream, int max, out bool tooLarge)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[Math.Min(max + 1L, 64 * 1024)];
        tooLarge = false;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) { tooLarge = true; break; }
        }
        return ms.ToArray();
    }

    private static ProfileLoadResult TooLarge(long size, ProfileLoadOptions options, string? sourcePath, bool atLeast = false) =>
        Result(null, [ProfileDiagnostic.Error("$", $"The profile is {(atLeast ? "more than " : "")}{size} bytes; at most {options.MaxDocumentBytes} bytes are allowed.")], sourcePath);

    private static ProfileLoadResult Failed(string message, string? sourcePath) =>
        Result(null, [ProfileDiagnostic.Error("$", message)], sourcePath);

    private static ProfileLoadResult Result(OverlayProfile? profile, IReadOnlyList<ProfileDiagnostic> diagnostics, string? sourcePath) =>
        new() { Profile = profile, Diagnostics = diagnostics, SourcePath = sourcePath };
}
