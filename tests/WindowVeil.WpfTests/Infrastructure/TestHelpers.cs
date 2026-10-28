using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using WindowVeil.Rendering;
using WindowVeil.Wpf;
using WindowVeil.Wpf.Hosting;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WindowVeil.WpfTests.Infrastructure;

internal static class TestHelpers
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Polls <paramref name="condition"/> until it is true or the timeout expires.</summary>
    public static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            if (condition()) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(25);
        }
    }

    /// <summary>Waits until the dispatcher has processed layout and render work.</summary>
    public static Task IdleAsync(Dispatcher dispatcher)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => tcs.TrySetResult()));
        return tcs.Task;
    }

    /// <summary>Runs on the renderer's dispatcher.</summary>
    public static Task<T> OnDispatcher<T>(this WpfOverlayRenderer renderer, Func<T> func) =>
        DispatcherInvoke.RunAsync(renderer.Dispatcher, func, CancellationToken.None);

    /// <summary>Runs on the renderer's dispatcher.</summary>
    public static Task OnDispatcher(this WpfOverlayRenderer renderer, Action action) =>
        DispatcherInvoke.RunAsync(renderer.Dispatcher, action, CancellationToken.None);

    public static WpfOverlayVisual Internal(this IOverlayVisual visual) => (WpfOverlayVisual)visual;

    public static ValueTask ApplyAsync(this WpfOverlayRenderer renderer, params (IOverlayVisual Visual, OverlayFrame Frame)[] updates) =>
        renderer.ApplyAsync(updates.Select(u => new OverlayVisualUpdate(u.Visual, u.Frame)).ToArray());
}

internal sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}

internal sealed class GreetingViewModel : INotifyPropertyChanged
{
    private string _message = "Hello";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
