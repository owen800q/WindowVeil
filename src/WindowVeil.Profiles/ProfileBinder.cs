using WindowVeil.Appearance;
using WindowVeil.Platform;
using WindowVeil.Profiles.Internal;

namespace WindowVeil.Profiles;

/// <summary>Result of <see cref="ProfileBinder.Bind"/> / <see cref="OverlayProfile.ToRules"/>.</summary>
public sealed record ProfileBindingResult
{
    /// <summary>The usable rules, in profile order. Rules with errors are left out.</summary>
    public IReadOnlyList<OverlayRule> Rules { get; init; } = [];

    /// <summary>Diagnostics, with paths of the form <c>$.rules[i]...</c> (indices refer to the source document when loaded).</summary>
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>True when no rule had an error.</summary>
    public bool Succeeded => !Diagnostics.Any(d => d.IsError);
}

/// <summary>
/// Turns a profile into rules for an <see cref="OverlaySession"/>: validates every rule, rejects duplicate ids and,
/// when an <see cref="AppearanceRegistry"/> is supplied, checks that every <see cref="RegisteredAppearance"/> id is
/// registered. The returned rules keep their <see cref="RegisteredAppearance"/>; the session resolves it through the
/// engine's registry when the rule is added.
/// </summary>
public static class ProfileBinder
{
    /// <summary>Validates <paramref name="profile"/> and returns its usable rules plus diagnostics.</summary>
    /// <remarks>Factories are not invoked here (only their registration is checked), so binding has no side effects.</remarks>
    public static ProfileBindingResult Bind(OverlayProfile profile, AppearanceRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var diagnostics = new List<ProfileDiagnostic>();
        var rules = new List<OverlayRule>(profile.Rules.Count);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < profile.Rules.Count; i++)
        {
            var rule = profile.Rules[i];
            var path = profile.RulePath(i);
            if (rule is null) { diagnostics.Add(ProfileDiagnostic.Error(path, "Rules cannot be null.")); continue; }
            try
            {
                rule.Validate();
            }
            catch (WindowVeilException ex)
            {
                diagnostics.Add(ProfileDiagnostic.Error(path, ex.Message));
                continue;
            }
            if (ids.TryGetValue(rule.Id, out var firstPath))
            {
                diagnostics.Add(ProfileDiagnostic.Error(ProfileReader.Prop(path, "id"), $"Duplicate rule id '{rule.Id}' (first used by {firstPath})."));
                continue;
            }
            ids.Add(rule.Id, path);
            if (registry is not null && rule.Appearance is RegisteredAppearance registered && !registry.Contains(registered.FactoryId))
            {
                var known = registry.Ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                diagnostics.Add(ProfileDiagnostic.Error(ProfileReader.Prop(ProfileReader.Prop(path, "appearance"), "factory"),
                    $"No appearance factory is registered with id '{registered.FactoryId}'. " +
                    (known.Length == 0 ? "The application has not registered any appearance factories." : $"Registered ids: {string.Join(", ", known)}.")));
                continue;
            }
            rules.Add(rule);
        }
        return new ProfileBindingResult { Rules = rules, Diagnostics = diagnostics };
    }
}

/// <summary>Result of <see cref="ProfileExporter.TryFromRules"/>.</summary>
public sealed record ProfileExportResult
{
    /// <summary>The profile, or null when something cannot be represented.</summary>
    public OverlayProfile? Profile { get; init; }

    /// <summary>Diagnostics; paths refer to the JSON the profile would be saved as.</summary>
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>True when <see cref="Profile"/> is available.</summary>
    public bool Succeeded => Profile is not null;
}

/// <summary>Builds profiles from rules created in code (for example by an editor) so they can be saved as JSON.</summary>
public static class ProfileExporter
{
    /// <summary>
    /// Creates a profile from rules, checking that everything is representable: the selector must use stable
    /// conditions (no handle, process id or predicate) and appearances must be built-in or <see cref="RegisteredAppearance"/>.
    /// </summary>
    public static ProfileExportResult TryFromRules(string name, WindowSelector selector, IEnumerable<OverlayRule> rules)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(rules);
        var diagnostics = new List<ProfileDiagnostic>();
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add(ProfileDiagnostic.Error("$.name", "The profile needs a name."));
        if (!ProfileApplication.TryFromSelector(selector, out var application, out var error))
            diagnostics.Add(ProfileDiagnostic.Error("$.application", error!));
        if (diagnostics.Count > 0) return new ProfileExportResult { Diagnostics = diagnostics };

        var profile = new OverlayProfile { Name = name, Application = application!, Rules = rules.ToArray() };
        // The writer is the single source of truth for what a profile can represent.
        var save = ProfileSerializer.TryToJson(profile);
        diagnostics.AddRange(save.Diagnostics);
        return new ProfileExportResult { Profile = save.Succeeded ? profile : null, Diagnostics = diagnostics };
    }

    /// <summary>Like <see cref="TryFromRules"/>, but throws when something cannot be represented.</summary>
    /// <exception cref="ProfileException">The selector or a rule has no profile representation.</exception>
    public static OverlayProfile FromRules(string name, WindowSelector selector, IEnumerable<OverlayRule> rules)
    {
        var result = TryFromRules(name, selector, rules);
        return result.Profile ?? throw new ProfileException("The rules cannot be exported as a profile.", result.Diagnostics);
    }
}
