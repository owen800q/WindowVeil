namespace WindowVeil.Profiles;

/// <summary>Attaches profiles to a <see cref="WindowVeilEngine"/>.</summary>
public static class WindowVeilEngineProfileExtensions
{
    /// <summary>
    /// Creates a session for the profile's application and adds its rules. The session is <b>not started</b>: subscribe
    /// to its events if needed, then call <see cref="OverlaySession.StartAsync"/>.
    /// </summary>
    /// <remarks>
    /// The session always uses <see cref="SessionOptions.AllowDeferredAttach"/> and <see cref="SessionOptions.AutoReconnect"/>
    /// (other options come from <paramref name="options"/> or the engine defaults), so a profile works for an application
    /// that is not running yet and survives restarts. <see cref="Appearance.RegisteredAppearance"/> ids resolve through
    /// <see cref="WindowVeilEngine.Appearances"/>.
    /// </remarks>
    /// <exception cref="ProfileException">A rule is invalid or references an unregistered appearance; no session is created.</exception>
    /// <exception cref="WindowVeilException">The engine cannot host a rule (for example no renderer or resolver supports it); no session is left behind.</exception>
    public static async Task<OverlaySession> AttachProfileAsync(this WindowVeilEngine engine, OverlayProfile profile, SessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(profile);
        var binding = profile.ToRules(engine.Appearances);
        if (!binding.Succeeded) throw new ProfileException($"Profile '{profile.Name}' cannot be attached.", binding.Diagnostics);

        var sessionOptions = (options ?? engine.Options.DefaultSessionOptions) with { AllowDeferredAttach = true, AutoReconnect = true };
        var session = await engine.AttachAsync(profile.Selector, sessionOptions, cancellationToken).ConfigureAwait(false);
        try
        {
            session.AddRules(binding.Rules);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    /// <summary>
    /// Attaches every profile of <paramref name="profiles"/> (see <see cref="AttachProfileAsync"/>). Sessions are not started.
    /// If one profile fails, the sessions created so far are disposed and the exception is rethrown.
    /// </summary>
    public static async Task<IReadOnlyList<OverlaySession>> AttachProfilesAsync(this WindowVeilEngine engine, IEnumerable<OverlayProfile> profiles, SessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(profiles);
        var sessions = new List<OverlaySession>();
        try
        {
            foreach (var profile in profiles)
                sessions.Add(await engine.AttachProfileAsync(profile, options, cancellationToken).ConfigureAwait(false));
            return sessions;
        }
        catch
        {
            foreach (var s in sessions) await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
