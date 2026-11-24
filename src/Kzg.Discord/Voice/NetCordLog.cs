using Microsoft.Extensions.Logging;
using NetCord.Logging;

namespace Kzg.Discord.Voice;

/// <summary>
/// NetCord's gateway and voice log, in ours. A voice client reports every packet it can't decrypt as a
/// warning, and a join races with the key exchange, so those go to debug; the rest keeps its level.
/// </summary>
internal sealed class NetCordLog(ILogger log, string bot, LogStats? stats = null) : IGatewayLogger, IVoiceLogger
{
    public bool IsEnabled(NetCord.Logging.LogLevel level) => log.IsEnabled(Map(level));

    public void Log<TState>(NetCord.Logging.LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var mapped = Map(level);
        if (mapped != Microsoft.Extensions.Logging.LogLevel.Warning && !log.IsEnabled(mapped))
        {
            return;
        }
        var message = formatter(state, exception);
        if (mapped == Microsoft.Extensions.Logging.LogLevel.Warning && message.StartsWith("Failed to decrypt", StringComparison.Ordinal))
        {
            stats?.CountDecryptFailure();
            mapped = Microsoft.Extensions.Logging.LogLevel.Debug;
        }
        if (!log.IsEnabled(mapped))
        {
            return;
        }
        log.Log(mapped, exception, "[{Bot}] {Message}", bot, message);
    }

    private static Microsoft.Extensions.Logging.LogLevel Map(NetCord.Logging.LogLevel level) => level switch
    {
        NetCord.Logging.LogLevel.Trace => Microsoft.Extensions.Logging.LogLevel.Trace,
        NetCord.Logging.LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
        NetCord.Logging.LogLevel.Information => Microsoft.Extensions.Logging.LogLevel.Information,
        NetCord.Logging.LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
        NetCord.Logging.LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
        _ => Microsoft.Extensions.Logging.LogLevel.Critical,
    };
}

/// <summary>What a bot's voice client reported, for <c>/gvg status</c> and diagnosis.</summary>
internal sealed class LogStats
{
    private long _decryptFailures;

    public long DecryptFailures => Interlocked.Read(ref _decryptFailures);

    public void CountDecryptFailure() => Interlocked.Increment(ref _decryptFailures);
}
