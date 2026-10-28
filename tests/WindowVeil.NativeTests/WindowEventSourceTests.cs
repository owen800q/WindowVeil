using System.Collections.Concurrent;
using WindowVeil.Geometry;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Tracking;
using static WindowVeil.NativeTests.TestUtil;

namespace WindowVeil.NativeTests;

[Trait("Category", "Desktop")]
public sealed class WindowEventSourceTests : IDisposable
{
    private readonly TestTargetWindow _target = TestTargetWindow.Create(new PixelRect(240, 200, 400, 300));

    public void Dispose() => _target.Dispose();

    [Fact]
    public async Task WinEvent_MovingTheTarget_RaisesLocationChanged_DestroyRaisesDestroyed()
    {
        using var source = new WinEventWindowEventSource();
        var events = new ConcurrentQueue<WindowEvent>();
        using var subscription = source.Subscribe(Snapshot(_target), events.Enqueue);
        Assert.True(source.HookCount > 0);

        _target.MoveTo(new PixelRect(300, 240, 400, 300));
        await EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.LocationChanged && e.Handle == _target.Handle),
            "LocationChanged should arrive within 2 s", TimeSpan.FromSeconds(2));

        _target.Destroy();
        await EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.Destroyed && e.Handle == _target.Handle),
            "Destroyed should arrive within 2 s", TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WinEvent_DisposedSubscription_StopsDelivering_AndHooksAreRemoved()
    {
        using var source = new WinEventWindowEventSource();
        var first = new ConcurrentQueue<WindowEvent>();
        var second = new ConcurrentQueue<WindowEvent>();
        var s1 = source.Subscribe(Snapshot(_target), first.Enqueue);
        var s2 = source.Subscribe(Snapshot(_target), second.Enqueue);
        int sharedHooks = source.HookCount;

        s1.Dispose();
        await source.MessageThread.InvokeAsync(() => { }); // removal is processed on the message thread
        Assert.Equal(sharedHooks, source.HookCount); // still used by s2

        _target.MoveTo(new PixelRect(260, 220, 400, 300));
        await EventuallyAsync(() => second.Any(e => e.Kind == WindowEventKind.LocationChanged), "second subscription still receives events", TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(first, e => e.Kind == WindowEventKind.LocationChanged);

        s2.Dispose();
        await source.MessageThread.InvokeAsync(() => { });
        Assert.Equal(0, source.HookCount);
    }

    [Fact]
    public async Task WinEvent_SharedThread_IsUsedAndSurvivesSourceDisposal()
    {
        using var thread = new NativeMessageThread();
        var source = new WinEventWindowEventSource(thread);
        var events = new ConcurrentQueue<WindowEvent>();
        source.Subscribe(Snapshot(_target), events.Enqueue);
        _target.MoveTo(new PixelRect(250, 230, 400, 300));
        await EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.LocationChanged), "event via shared thread", TimeSpan.FromSeconds(2));
        source.Dispose();
        Assert.False(thread.IsDisposed);
        Assert.Equal(0, source.HookCount);
    }

    [Fact]
    public async Task Polling_ReportsMovesAndDestruction()
    {
        using var source = new PollingWindowEventSource(new Win32WindowSystem(), TimeSpan.FromMilliseconds(20));
        var events = new ConcurrentQueue<WindowEvent>();
        using var subscription = source.Subscribe(Snapshot(_target), events.Enqueue);

        _target.MoveTo(new PixelRect(280, 230, 400, 300));
        await EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.LocationChanged), "polling detects the move", TimeSpan.FromSeconds(2));

        _target.Destroy();
        await EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.Destroyed), "polling detects destruction", TimeSpan.FromSeconds(2));
    }
}
