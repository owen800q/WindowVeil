using System.Text.Json;
using System.Text.Json.Serialization;
using WindowVeil.Geometry;

namespace WindowVeil.RetroCapDemo.Evidence;

/// <summary>One checked condition.</summary>
public sealed record AssertionResult(string Name, bool Passed, string Detail);

/// <summary>A JSON-friendly rectangle.</summary>
public sealed record RectDto(int X, int Y, int Width, int Height)
{
    /// <summary>Converts from a pixel rectangle.</summary>
    public static RectDto From(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
}

/// <summary>Result of one demo scenario.</summary>
public sealed class ScenarioResult(string name)
{
    /// <summary>Scenario name.</summary>
    public string Name { get; } = name;

    /// <summary>All assertions.</summary>
    public List<AssertionResult> Assertions { get; } = [];

    /// <summary>Target (accessibility) bounds.</summary>
    public RectDto? TargetBounds { get; set; }

    /// <summary>Overlay window bounds.</summary>
    public RectDto? OverlayBounds { get; set; }

    /// <summary>Locator that resolved the target.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>Screenshot file name.</summary>
    public string? Screenshot { get; set; }

    /// <summary>Error that aborted the scenario.</summary>
    public string? Error { get; set; }

    /// <summary>True when no assertion failed and no error occurred.</summary>
    public bool Passed => Error is null && Assertions.Count > 0 && Assertions.All(a => a.Passed);

    /// <summary>Records an assertion.</summary>
    public bool Check(string name, bool passed, string detail)
    {
        Assertions.Add(new AssertionResult(name, passed, detail));
        return passed;
    }
}

/// <summary>The evidence manifest written next to the screenshots.</summary>
public sealed class EvidenceManifest
{
    /// <summary>Manifest schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Commit the binaries were built from.</summary>
    public string CommitSha { get; set; } = "";

    /// <summary>WindowVeil version.</summary>
    public string WindowVeilVersion { get; set; } = "";

    /// <summary>RetroCap identity.</summary>
    public Dictionary<string, string> RetroCap { get; set; } = [];

    /// <summary>Machine and display details.</summary>
    public EnvironmentInfo Environment { get; set; } = new();

    /// <summary>UTC time of the run.</summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Screenshot role → file name.</summary>
    public Dictionary<string, string> Screenshots { get; set; } = [];

    /// <summary>Scenario results.</summary>
    public List<ScenarioDto> Scenarios { get; set; } = [];

    /// <summary>"passed" or "failed".</summary>
    public string Result { get; set; } = "failed";

    /// <summary>Serializer options (camelCase, indented).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes the manifest.</summary>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

/// <summary>Display environment.</summary>
public sealed class EnvironmentInfo
{
    /// <summary>OS description.</summary>
    public string Os { get; set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    /// <summary>.NET runtime.</summary>
    public string Runtime { get; set; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    /// <summary>Machine name.</summary>
    public string Machine { get; set; } = System.Environment.MachineName;

    /// <summary>Captured screen (virtual desktop) size.</summary>
    public Dictionary<string, int> Screen { get; set; } = [];

    /// <summary>DPI of the RetroCap monitor.</summary>
    public int Dpi { get; set; }

    /// <summary>Scaling percentage.</summary>
    public int ScalePercent { get; set; }

    /// <summary>CI runner name, if any.</summary>
    public string? Runner { get; set; } = System.Environment.GetEnvironmentVariable("RUNNER_NAME");
}

/// <summary>Serialized scenario.</summary>
public sealed record ScenarioDto(string Name, bool Passed, string? Error, RectDto? TargetBounds, RectDto? OverlayBounds,
    string? ResolvedBy, string? Screenshot, IReadOnlyList<AssertionResult> Assertions)
{
    /// <summary>Converts a result.</summary>
    public static ScenarioDto From(ScenarioResult r) =>
        new(r.Name, r.Passed, r.Error, r.TargetBounds, r.OverlayBounds, r.ResolvedBy, r.Screenshot, r.Assertions.ToArray());
}
