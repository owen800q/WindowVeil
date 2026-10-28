using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Tracking;
using static WindowVeil.NativeTests.TestUtil;

namespace WindowVeil.NativeTests;

[Trait("Category", "Desktop")]
public sealed class OverlaySessionDesktopTests : IDisposable
{
    private readonly TestTargetWindow _target = TestTargetWindow.Create(new PixelRect(200, 180, 500, 360));

    public void Dispose() => _target.Dispose();

    [Fact]
    public async Task Session_TracksTheTarget_AndHidesTheOverlayWhenTheTargetIsDestroyed()
    {
        await using var engine = WindowVeilEngine.CreateBuilder()
            .UseNativeOverlays()
            .UseWinEventTracking()
            .Build();
        Assert.IsType<Win32WindowSystem>(engine.WindowSystem);
        Assert.IsType<WinEventWindowEventSource>(engine.Options.EventSource);
        var renderer = Assert.IsType<NativeOverlayRenderer>(Assert.Single(engine.Options.Renderers));
        Assert.Same(renderer.MessageThread, ((WinEventWindowEventSource)engine.Options.EventSource!).MessageThread);

        var session = await engine.AttachAsync(_target.Handle);
        var rule = session.AddRule(MaskRule.Create(ElementLocator.WindowRelative(10, 10, 100, 50), MaskAppearance.Solid("#FF00FF"), id: "mask"));
        await session.StartAsync();

        var status = await session.WaitForRuleAsync(rule.Id, s => s.State == RuleState.Active, DefaultTimeout);
        var expected = ClientRect(_target, 10, 10, 100, 50);
        Assert.Equal(expected, Assert.Single(status.OverlayBounds));
        Assert.NotEqual(0, status.OverlayHandle);
        Assert.Equal(expected, NativeDiagnostics.GetWindowRect(status.OverlayHandle));
        Assert.True(NativeDiagnostics.IsWindowVisible(status.OverlayHandle));
        Assert.True(IsAbove(status.OverlayHandle, _target.Handle));

        // Move the target: the overlay follows (driven by WinEvent LocationChanged).
        _target.MoveTo(new PixelRect(320, 260, 500, 360));
        var moved = ClientRect(_target, 10, 10, 100, 50);
        Assert.NotEqual(expected, moved);
        status = await session.WaitForRuleAsync(rule.Id, s => s.OverlayBounds.Count == 1 && s.OverlayBounds[0] == moved, DefaultTimeout);
        await EventuallyAsync(() => NativeDiagnostics.GetWindowRect(status.OverlayHandle) == moved, "overlay window should follow the target");

        // Destroy the target: the session notices, the overlay is hidden.
        nint overlay = status.OverlayHandle;
        _target.Destroy();
        await EventuallyAsync(() => session.State is SessionState.TargetLost or SessionState.WaitingForTarget, "session should lose the target");
        await session.WaitForRuleAsync(rule.Id, s => s.State == RuleState.Hidden, DefaultTimeout);
        await EventuallyAsync(() => !NativeDiagnostics.IsWindowVisible(overlay), "overlay should be hidden");

        await session.DisposeAsync();
        Assert.False(NativeDiagnostics.IsWindow(overlay));
    }
}
