using System.Diagnostics;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Rendering;

namespace WindowVeil.NativeTests;

[Trait("Category", "Desktop")]
public sealed class Win32WindowSystemTests : IDisposable
{
    private readonly TestTargetWindow _target = TestTargetWindow.Create(new PixelRect(220, 180, 500, 340));
    private readonly Win32WindowSystem _system = new();

    public void Dispose() => _target.Dispose();

    [Fact]
    public void EnumerateWindows_FindsTheTargetByTitle_WithSaneGeometry()
    {
        var found = WindowSelector.ByTitle(_target.Title).SelectBest(_system.EnumerateWindows());

        Assert.NotNull(found);
        Assert.Equal(_target.Handle, found.Handle);
        Assert.Equal(Environment.ProcessId, found.ProcessId);
        using (var self = Process.GetCurrentProcess()) Assert.Equal(self.ProcessName, found.ProcessName);
        Assert.Equal(TestTargetWindow.ClassName, found.ClassName);
        Assert.Equal(_target.Title, found.Title);
        Assert.True(found.IsVisible);
        Assert.False(found.IsMinimized);
        Assert.True(found.IsPresentable);
        Assert.False(found.Bounds.IsEmpty);
        Assert.False(found.ClientBounds.IsEmpty);
        // The client area lies inside the visible frame; the visible frame lies inside GetWindowRect.
        Assert.True(found.Bounds.Contains(found.ClientBounds), $"{found.Bounds} should contain {found.ClientBounds}");
        Assert.True(NativeDiagnostics.GetWindowRect(_target.Handle).Contains(found.Bounds));
        Assert.True(found.Dpi.Dpi >= 96);
        Assert.False(found.MonitorBounds.IsEmpty);
        Assert.True(_system.VirtualScreenBounds.Contains(found.MonitorBounds));
    }

    [Fact]
    public async Task EnumerateWindows_ExcludesOverlays()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await TestUtil.CreateAsync(renderer, MaskAppearance.Solid("#FF0000"));
        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, TestUtil.Frame(_target, TestUtil.ClientRect(_target, 1, 1, 30, 30)))]);

        var all = _system.EnumerateWindows();
        Assert.DoesNotContain(all, w => w.Handle == visual.Handle);
        Assert.DoesNotContain(all, w => w.ClassName == NativeOverlayRenderer.WindowClassName);
        Assert.Contains(all, w => w.Handle == _target.Handle);
    }

    [Fact]
    public void GetWindow_ReflectsMoves_AndReturnsNullAfterDestroy()
    {
        var before = _system.GetWindow(_target.Handle)!;
        _target.MoveTo(new PixelRect(260, 210, 520, 300));
        var after = _system.GetWindow(_target.Handle)!;
        Assert.Equal(before.Bounds.X + 40, after.Bounds.X);
        Assert.Equal(before.Bounds.Y + 30, after.Bounds.Y);

        _target.Destroy();
        Assert.Null(_system.GetWindow(_target.Handle));
        Assert.Null(_system.GetWindow(0));
    }

    [Fact]
    public void IsProcessAlive_DistinguishesRunningAndExitedProcesses()
    {
        Assert.True(_system.IsProcessAlive(Environment.ProcessId));
        Assert.False(_system.IsProcessAlive(0));

        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        int pid = p.Id;
        Assert.True(p.WaitForExit(10_000));
        // The Process object keeps a handle, so the pid cannot be reused yet; the process is nevertheless exited.
        Assert.False(_system.IsProcessAlive(pid));
    }
}
