using WindowVeil.Native;

namespace WindowVeil.NativeTests;

[Trait("Category", "Desktop")]
public sealed class NativeMessageThreadTests
{
    [Fact]
    public async Task InvokeAsync_RunsOnTheMessageThread_AndPropagatesResultsAndExceptions()
    {
        using var thread = new NativeMessageThread("test");
        Assert.True(thread.NativeThreadId != 0);
        Assert.False(thread.CheckAccess());

        var (id, onThread) = await thread.InvokeAsync(() => (Environment.CurrentManagedThreadId, thread.CheckAccess()));
        Assert.Equal(thread.ManagedThreadId, id);
        Assert.True(onThread);

        await Assert.ThrowsAsync<InvalidOperationException>(() => thread.InvokeAsync<int>(() => throw new InvalidOperationException("boom")));

        // Nested invocation on the thread runs inline instead of deadlocking.
        int nested = await thread.InvokeAsync(() => thread.Invoke(() => 42));
        Assert.Equal(42, nested);
    }

    [Fact]
    public async Task Dispose_FailsPendingWork_AndRejectsNewWork()
    {
        var thread = new NativeMessageThread("test");
        using var gate = new ManualResetEventSlim(false);
        var blocker = thread.InvokeAsync(() => gate.Wait(TimeSpan.FromSeconds(10)));
        var pending = thread.InvokeAsync(() => 1);

        var disposing = Task.Run(thread.Dispose);
        Assert.True(SpinWait.SpinUntil(() => thread.IsDisposed, TimeSpan.FromSeconds(5)));
        gate.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(15));

        await blocker;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => thread.InvokeAsync(() => 2));
        thread.Dispose(); // idempotent
    }

    [Fact]
    public async Task InvokeAsync_HonorsCancellationBeforeExecution()
    {
        using var thread = new NativeMessageThread("test");
        using var gate = new ManualResetEventSlim(false);
        var blocker = thread.InvokeAsync(() => gate.Wait(TimeSpan.FromSeconds(10)));
        using var cts = new CancellationTokenSource();
        bool ran = false;
        var cancelled = thread.InvokeAsync(() => ran = true, cts.Token);
        cts.Cancel();
        gate.Set();
        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await thread.InvokeAsync(() => { }); // drain
        Assert.False(ran);
    }
}
