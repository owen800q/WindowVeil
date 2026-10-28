namespace WindowVeil.Ocr;

/// <summary>Registers OCR support on a <see cref="WindowVeilBuilder"/>.</summary>
public static class OcrWindowVeilBuilderExtensions
{
    /// <summary>
    /// Adds an <see cref="OcrLocatorResolver"/> over <paramref name="provider"/> so rules can use
    /// <see cref="Locators.ElementLocator.ByOcrText"/>. A capture backend (<see cref="WindowVeilBuilder.UseCapture"/>) is
    /// also required at runtime. Unless <see cref="OcrLocatorOptions.DisposeProviderWithEngine"/> is false, the engine
    /// disposes the provider together with the resolver.
    /// </summary>
    /// <example>
    /// <code>
    /// using var ocr = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory("models"));
    /// var engine = WindowVeilEngine.CreateBuilder().UseWindowsDesktop().UseOcr(ocr).Build();
    /// </code>
    /// </example>
    public static WindowVeilBuilder UseOcr(this WindowVeilBuilder builder, IOcrProvider provider, OcrLocatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(provider);
        options ??= new OcrLocatorOptions();
        builder.AddResolver(new OcrLocatorResolver(provider, options));
        if (options.DisposeProviderWithEngine && provider is IDisposable or IAsyncDisposable)
            builder.AddOwnedComponent(provider);
        return builder;
    }
}
