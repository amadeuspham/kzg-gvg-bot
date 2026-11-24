using System.Net;

namespace Kzg.Bot;

/// <summary>The command line: <c>--fake</c>, <c>--setup</c>, <c>--data-dir</c>, <c>--listen</c> and <c>--help</c>.</summary>
public sealed record LaunchOptions(bool Fake, string? DataDir, IPEndPoint? Listen, bool Help)
{
    /// <summary>Opens the setup page even though the bot is set up, to paste the bot tokens again or add bots.</summary>
    public bool Setup { get; init; }

    /// <summary>Where the log goes instead of the console: a file kept to a few tens of MB, for a service.</summary>
    public string? LogFile { get; init; }

    public const string Usage = """
        Usage: Kzg.Bot [--fake | --setup] [--data-dir <folder>] [--listen <ip:port>]

          (no option)        run for real: sign the relay bots in to Discord, as config.json says;
                             on the first start, with no config.json yet, open the setup page first
          --setup            open the setup page again, to paste new bot tokens or add bots
          --fake             run offline against a made-up guild: no Discord, nothing real touched
          --data-dir <dir>   the folder for config.json, secrets.json and devices.json
                             (with --fake: only devices.json, in a temp folder unless given)
          --listen <ip:port> where to listen, instead of the config's 'listen'
          --log-file <file>  write the log to this file, not the console; it is kept small:
                             at 10 MB it moves to <name>.1.log, and two old files are kept
        """;

    /// <exception cref="ArgumentException">An option isn't known or lacks its value; the message says which.</exception>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var options = new LaunchOptions(false, null, null, false);
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--fake":
                    options = options with { Fake = true };
                    break;
                case "--setup":
                    options = options with { Setup = true };
                    break;
                case "--help" or "-h":
                    options = options with { Help = true };
                    break;
                case "--log-file":
                    options = options with { LogFile = Value(args, ref i) };
                    break;
                case "--data-dir":
                    options = options with { DataDir = Value(args, ref i) };
                    break;
                case "--listen":
                    var text = Value(args, ref i);
                    options = IPEndPoint.TryParse(text, out var endpoint)
                        ? options with { Listen = endpoint }
                        : throw new ArgumentException("--listen takes an IP address and a port, such as 127.0.0.1:7878");
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        return options.Fake && options.Setup ? throw new ArgumentException("--fake and --setup don't go together") : options;
    }

    private static string Value(IReadOnlyList<string> args, ref int i) =>
        i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
}
