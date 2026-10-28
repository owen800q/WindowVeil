using System.Collections.Concurrent;

namespace WindowVeil.Appearance;

/// <summary>
/// Maps appearance factory ids to factories. This is the only way profiles create custom (for example WPF)
/// appearances: untrusted JSON can reference only ids that the application registered explicitly.
/// </summary>
public sealed class AppearanceRegistry
{
    private readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<string, string>, OverlayAppearance>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a factory. Ids are case-insensitive and must be unique.</summary>
    public AppearanceRegistry Register(string id, Func<IReadOnlyDictionary<string, string>, OverlayAppearance> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryAdd(id, factory)) throw new InvalidOperationException($"An appearance factory with id '{id}' is already registered.");
        return this;
    }

    /// <summary>Registers a parameterless factory.</summary>
    public AppearanceRegistry Register(string id, Func<OverlayAppearance> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Register(id, _ => factory());
    }

    /// <summary>Registered ids.</summary>
    public IReadOnlyCollection<string> Ids => _factories.Keys.ToArray();

    /// <summary>Whether an id is registered.</summary>
    public bool Contains(string id) => _factories.ContainsKey(id);

    /// <summary>
    /// Resolves an appearance: <see cref="RegisteredAppearance"/> is replaced by the factory output, everything else is
    /// returned unchanged. Returns false with an error message when the id is unknown or the factory throws.
    /// </summary>
    public bool TryResolve(OverlayAppearance appearance, out OverlayAppearance? resolved, out string? error)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (appearance is not RegisteredAppearance reg) { resolved = appearance; error = null; return true; }
        resolved = null;
        if (!_factories.TryGetValue(reg.FactoryId, out var factory))
        {
            error = $"No appearance factory is registered with id '{reg.FactoryId}'.";
            return false;
        }
        try
        {
            resolved = factory(reg.Parameters);
            if (resolved is null or RegisteredAppearance) { resolved = null; error = $"Factory '{reg.FactoryId}' returned an invalid appearance."; return false; }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Factory '{reg.FactoryId}' failed: {ex.Message}";
            return false;
        }
    }
}
