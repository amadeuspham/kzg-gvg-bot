using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot.Logging;

/// <summary>
/// Writes log lines to a writer after <see cref="Redactor"/> has masked them. Redaction is the last step
/// before the text leaves, so what any component logs, the host's own or Kestrel's, is covered. Message,
/// category and exception are all redacted; scopes are not written.
/// </summary>
public sealed class RedactingLoggerProvider(TextWriter output, Redactor redactor, TimeProvider? clock = null) : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = $"{_clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} {Label(level)} {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }
        var safe = redactor.Redact(line);
        lock (_gate)
        {
            output.WriteLine(safe);
            output.Flush();
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    private sealed class Logger(RedactingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}

public static class RedactingLoggerExtensions
{
    /// <summary>Replaces every log provider with one that redacts what it writes to <paramref name="output"/>.</summary>
    /// <param name="quietFramework">Keeps ASP.NET's own chatter to warnings; it logs each request's query string at Information.</param>
    public static ILoggingBuilder AddRedactingLogger(
        this ILoggingBuilder logging, TextWriter output, Redactor redactor, LogLevel minimum, bool quietFramework = true)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(minimum);
        if (quietFramework)
        {
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        }
        return logging.AddProvider(new RedactingLoggerProvider(output, redactor));
    }
}
