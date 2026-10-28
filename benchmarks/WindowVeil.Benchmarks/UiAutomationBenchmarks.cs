using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using WindowVeil.Automation;
using WindowVeil.Locators;
using WindowVeil.Native;
using WindowVeil.Platform;

namespace WindowVeil.Benchmarks;

/// <summary>UI Automation lookup latency against the bundled demo target (an external process).</summary>
public class UiAutomationBenchmarks
{
    private Process _target = null!;
    private TargetWindowInfo _window = null!;
    private UiAutomationLocatorResolver _resolver = null!;
    private LocatorResolution _warm = null!;
    private readonly UiAutomationLocator _byId = ElementLocator.ByAutomationId("StartupCheck");
    private readonly UiAutomationLocator _byName = ElementLocator.ByName("Send anonymous usage data");
    private readonly TextLocator _text = ElementLocator.ByText("secret token");

    /// <summary>Starts the target and warms the UIA connection.</summary>
    [GlobalSetup]
    public void Setup()
    {
        (_target, var hwnd) = Shared.StartDemoTarget();
        _window = new Win32WindowSystem().GetWindow(hwnd) ?? throw new InvalidOperationException("No target window.");
        _resolver = new UiAutomationLocatorResolver();
        _warm = Resolve(_byId, null);
    }

    /// <summary>Stops the target.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _resolver.Dispose();
        _target.Kill();
        _target.Dispose();
    }

    private LocatorResolution Resolve(ElementLocator locator, LocatorResolution? previous) =>
        _resolver.ResolveAsync(locator, new LocatorContext(_window, ResolveReason.Poll) { Previous = previous }, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    /// <summary>Full bounded tree search by AutomationId.</summary>
    [Benchmark]
    public LocatorResolution AutomationIdCold() => Resolve(_byId, null);

    /// <summary>Re-validating a cached element (the common per-poll cost).</summary>
    [Benchmark]
    public LocatorResolution AutomationIdCached() => Resolve(_byId, _warm);

    /// <summary>Tree search by Name.</summary>
    [Benchmark]
    public LocatorResolution NameCold() => Resolve(_byName, null);

    /// <summary>TextPattern range search (two matches, one spanning lines).</summary>
    [Benchmark]
    public LocatorResolution TextPatternRanges() => Resolve(_text, null);
}
