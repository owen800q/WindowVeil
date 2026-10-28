using System.IO;
using WindowVeil.RetroCapDemo;
using Xunit.Abstractions;

namespace WindowVeil.UiTests;

/// <summary>
/// Tier B: drives the real, unmodified RetroCap v1.0 executable and records screenshot evidence.
/// Requires an interactive, unlocked desktop in an isolated account or VM (see docs/CI-CD.md).
/// Without <c>WINDOWVEIL_RETROCAP_EXE</c> the test is reported as skipped — never as passed; with
/// <c>WINDOWVEIL_REQUIRE_INTERACTIVE=1</c> (release pipeline) missing prerequisites fail it.
/// </summary>
[Trait("Category", "Interactive")]
public sealed class RetroCapInteractiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task StartupCheck_IsMaskedReplacedTrackedAndRestored()
    {
        bool required = Environment.GetEnvironmentVariable("WINDOWVEIL_REQUIRE_INTERACTIVE") == "1";
        var exe = Environment.GetEnvironmentVariable("WINDOWVEIL_RETROCAP_EXE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Assert.False(required, $"WINDOWVEIL_RETROCAP_EXE is not set to an existing RetroCap.exe ('{exe}').");
            Skip.If(true, "Set WINDOWVEIL_RETROCAP_EXE (tools/get-retrocap.ps1) to run the interactive RetroCap scenarios.");
        }

        var evidence = Environment.GetEnvironmentVariable("WINDOWVEIL_EVIDENCE_DIR")
            ?? Path.Combine(Path.GetTempPath(), "windowveil-evidence-" + Guid.NewGuid().ToString("n")[..8]);
        var manifest = await RetroCapScenarioRunner.RunAsync(new RetroCapScenarioOptions
        {
            RetroCapExe = exe!,
            EvidenceDir = evidence,
            OcrModelsDir = Environment.GetEnvironmentVariable("WINDOWVEIL_OCR_MODELS"),
            RequireOcr = required,
            CommitSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            Version = Environment.GetEnvironmentVariable("WINDOWVEIL_VERSION") ?? "0.0.0-dev",
            Log = output.WriteLine,
        });

        output.WriteLine($"Evidence: {evidence}");
        var failures = manifest.Scenarios
            .Where(s => !s.Passed && !s.Name.EndsWith("(skipped)", StringComparison.Ordinal))
            .Select(s => $"{s.Name}: {s.Error ?? string.Join("; ", s.Assertions.Where(a => !a.Passed).Select(a => $"{a.Name} ({a.Detail})"))}")
            .ToArray();
        Assert.True(failures.Length == 0, "Failed scenarios:\n" + string.Join("\n", failures));
        Assert.Equal("passed", manifest.Result);
    }
}
