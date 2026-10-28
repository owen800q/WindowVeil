using WindowVeil.Geometry;
using WindowVeil.Platform;

namespace WindowVeil.WpfTests.Infrastructure;

/// <summary>A minimal <see cref="IWindowSystem"/> that only knows the in-process test target windows.</summary>
internal sealed class TestWindowSystem(params TestTargetWindow[] targets) : IWindowSystem
{
    public IReadOnlyList<TargetWindowInfo> EnumerateWindows() =>
        targets.Where(t => TestNative.IsWindow(t.Handle)).Select(t => t.Snapshot()).ToArray();

    public TargetWindowInfo? GetWindow(nint handle) =>
        targets.FirstOrDefault(t => t.Handle == handle && TestNative.IsWindow(handle))?.Snapshot();

    public bool IsProcessAlive(int processId) => processId == Environment.ProcessId;

    public PixelRect VirtualScreenBounds => PixelRect.UnionAll(targets.Select(t => TestNative.GetMonitorBounds(t.Handle)));
}
