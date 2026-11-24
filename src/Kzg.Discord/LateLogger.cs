using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kzg.Discord;

/// <summary>
/// A logger handed out before the host's loggers exist: parts of the adapter are built first so the engine
/// can be wired to them, and the real logger is set once the host is up. Until then it logs nothing.
/// </summary>
internal sealed class LateLogger : ILogger
{
    private volatile ILogger _inner = NullLogger.Instance;

    public void Set(ILogger inner) => _inner = inner;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}
