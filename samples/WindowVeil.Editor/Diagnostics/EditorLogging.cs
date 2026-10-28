using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace WindowVeil.Editor.Diagnostics;

/// <summary>
/// An <see cref="ILoggerProvider"/> that forwards log messages at or above <see cref="MinimumLevel"/> (Warning by
/// default) to the editor's <see cref="DiagnosticLog"/>. Only depends on Microsoft.Extensions.Logging.Abstractions.
/// </summary>
public sealed class DiagnosticLogLoggerProvider(DiagnosticLog log, MsLogLevel minimumLevel = MsLogLevel.Warning) : ILoggerProvider
{
    /// <summary>Lowest forwarded level.</summary>
    public MsLogLevel MinimumLevel { get; } = minimumLevel;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new DiagnosticLogLogger(log, ShortCategory(categoryName), MinimumLevel);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static string ShortCategory(string category)
    {
        int dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private sealed class DiagnosticLogLogger(DiagnosticLog log, string category, MsLogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MsLogLevel logLevel) => logLevel != MsLogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message = formatter(state, exception);
            if (exception is not null) message += $" ({exception.GetType().Name}: {exception.Message})";
            var level = logLevel switch
            {
                MsLogLevel.Trace or MsLogLevel.Debug => LogLevel.Debug,
                MsLogLevel.Information => LogLevel.Info,
                MsLogLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Error,
            };
            log.Write(level, category, message);
        }
    }
}

/// <summary>
/// A minimal <see cref="ILoggerFactory"/> over a fixed set of providers (Microsoft.Extensions.Logging.Abstractions has
/// no concrete factory). Passed to <c>WindowVeilBuilder.UseLoggerFactory</c>.
/// </summary>
public sealed class SimpleLoggerFactory : ILoggerFactory
{
    private readonly List<ILoggerProvider> _providers = [];
    private readonly object _gate = new();

    /// <summary>Creates a factory.</summary>
    public SimpleLoggerFactory(params ILoggerProvider[] providers) => _providers.AddRange(providers);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate) _providers.Add(provider);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        ILoggerProvider[] providers;
        lock (_gate) providers = _providers.ToArray();
        return providers.Length == 1 ? providers[0].CreateLogger(categoryName) : new CompositeLogger(providers.Select(p => p.CreateLogger(categoryName)).ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var p in _providers) p.Dispose();
            _providers.Clear();
        }
    }

    private sealed class CompositeLogger(ILogger[] loggers) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MsLogLevel logLevel) => loggers.Any(l => l.IsEnabled(logLevel));

        public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            foreach (var l in loggers) l.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
