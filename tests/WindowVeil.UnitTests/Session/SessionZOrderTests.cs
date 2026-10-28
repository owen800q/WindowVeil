using WindowVeil.Platform;
using WindowVeil.UnitTests.Fakes;
using static WindowVeil.UnitTests.Fakes.EngineHarness;

namespace WindowVeil.UnitTests.Session;

public class SessionZOrderTests
{
    [Fact]
    public async Task ZOrderChange_ReappliesUnchangedFrames_SoRenderersCanRestack()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        var visual = h.Renderer.LiveFor("r1")!;
        int frames = visual.Frames.Count;

        h.Events.Raise(w.Handle, WindowEventKind.ZOrderChanged);
        await Wait.UntilAsync(() => visual.Frames.Count > frames, "frame re-applied after z-order change");
        Assert.True(visual.Frames[^1].IsEquivalentTo(visual.Frames[frames - 1]));
    }

    [Fact]
    public async Task Reconnect_SkipsWindowsWhoseProcessIsDead()
    {
        var options = Quiet with { ReconnectPollInterval = TimeSpan.FromMilliseconds(20), TargetCheckInterval = TimeSpan.FromMilliseconds(20) };
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync(options);
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        h.Windows.Destroy(w.Handle);
        h.Events.Raise(w.Handle, WindowEventKind.Destroyed);
        await Wait.UntilAsync(() => session.State == SessionState.WaitingForTarget, "target lost");

        var zombie = h.Windows.Relaunch(w);
        h.Windows.KillProcess(zombie.ProcessId);
        await Task.Delay(150);
        Assert.Equal(SessionState.WaitingForTarget, session.State);

        var live = h.Windows.Relaunch(w);
        await Wait.UntilAsync(() => session.State == SessionState.Running && session.Target?.Handle == live.Handle, "reconnected to the live instance");
    }
}
