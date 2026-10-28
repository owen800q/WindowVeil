using Microsoft.Extensions.Logging;
using WindowVeil.Automation;

namespace WindowVeil;

/// <summary>Registers UI Automation support with a <see cref="WindowVeilBuilder"/>.</summary>
public static class AutomationWindowVeilBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="UiAutomationLocatorResolver"/>, which resolves <see cref="Locators.UiAutomationLocator"/> and
    /// <see cref="Locators.TextLocator"/>. The engine owns and disposes the resolver. The resolver's logger is created
    /// from the builder's current <see cref="WindowVeilBuilder.LoggerFactory"/>, so call
    /// <see cref="WindowVeilBuilder.UseLoggerFactory"/> first.
    /// </summary>
    public static WindowVeilBuilder UseUiAutomation(this WindowVeilBuilder builder, UiAutomationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var resolver = new UiAutomationLocatorResolver(options, builder.LoggerFactory.CreateLogger<UiAutomationLocatorResolver>());
        return builder.AddResolver(resolver).AddOwnedComponent(resolver);
    }
}
