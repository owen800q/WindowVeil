using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowVeil.Editor.Mvvm;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base class.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a field and raises <see cref="PropertyChanged"/> when the value changes.</summary>
    /// <returns>True when the value changed.</returns>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Raises <see cref="PropertyChanged"/> for several properties.</summary>
    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
