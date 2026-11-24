using System.Diagnostics.CodeAnalysis;

namespace Kzg.App.Input;

/// <summary>What the controller helper says on its stdout: one tab-separated line per event.</summary>
public abstract record PadLine
{
    public const int MaxFieldLength = 80;

    private PadLine()
    {
    }

    /// <summary>SDL started; the helper is listening.</summary>
    public sealed record Ready(string Version) : PadLine;

    public sealed record ControllerAdded(string Name) : PadLine;

    public sealed record ControllerRemoved(string Name) : PadLine;

    public sealed record Button(string Name, bool Down) : PadLine;

    /// <summary>The helper can't run, e.g. SDL failed to start.</summary>
    public sealed record Failure(string Message) : PadLine;

    public static string Format(PadLine line) => line switch
    {
        Ready r => $"ready\t{Clean(r.Version)}",
        ControllerAdded a => $"added\t{Clean(a.Name)}",
        ControllerRemoved r => $"removed\t{Clean(r.Name)}",
        Button b => $"{(b.Down ? "down" : "up")}\t{Clean(b.Name)}",
        Failure f => $"error\t{Clean(f.Message)}",
        _ => throw new ArgumentOutOfRangeException(nameof(line)),
    };

    /// <summary>Reads one line; false for anything the helper wouldn't write, so stray output is ignored.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PadLine? line)
    {
        line = null;
        if (text is null)
        {
            return false;
        }
        var tab = text.IndexOf('\t');
        if (tab <= 0 || tab == text.Length - 1)
        {
            return false;
        }
        var value = text[(tab + 1)..];
        if (value.Length > MaxFieldLength || value.Contains('\t'))
        {
            return false;
        }
        line = text[..tab] switch
        {
            "ready" => new Ready(value),
            "added" => new ControllerAdded(value),
            "removed" => new ControllerRemoved(value),
            "down" when InputBinding.TryParse("pad:" + value, out _) => new Button(value, true),
            "up" when InputBinding.TryParse("pad:" + value, out _) => new Button(value, false),
            "error" => new Failure(value),
            _ => null,
        };
        return line is not null;
    }

    // A device name comes from hardware: keep it to one short printable line.
    private static string Clean(string text)
    {
        var clean = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return clean.Length <= MaxFieldLength ? clean : clean[..MaxFieldLength];
    }
}
