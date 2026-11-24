namespace Kzg.Core.Config;

/// <summary>Where a data file went wrong.</summary>
/// <param name="Line">Counted from 1; null when the problem has no one line, such as two settings that clash.</param>
/// <param name="Setting">The setting as the file spells it, such as "groups.attack.channels[1]"; null outside any.</param>
/// <param name="Problem">What is wrong, for an admin to read. It never quotes a value, so it is safe to log.</param>
public sealed record ConfigError(int? Line, string? Setting, string Problem)
{
    public override string ToString() =>
        (Line, Setting) switch
        {
            ({ } line, { } setting) => $"line {line}, {setting}: {Problem}",
            ({ } line, null) => $"line {line}: {Problem}",
            (null, { } setting) => $"{setting}: {Problem}",
            _ => Problem,
        };
}

/// <summary>What a data file held when it was read.</summary>
public abstract record FileLoad<T>
{
    private FileLoad()
    {
    }

    /// <summary>There is no file.</summary>
    public sealed record Missing : FileLoad<T>;

    /// <summary>The file is there but couldn't be read, perhaps for its permissions or another program locking it.</summary>
    public sealed record Unreadable : FileLoad<T>;

    public sealed record Loaded(T Value) : FileLoad<T>;

    /// <summary>The file was read and is wrong: every problem found, the first in the file first.</summary>
    public sealed record Broken(IReadOnlyList<ConfigError> Errors) : FileLoad<T>;
}

/// <summary>What became of a save.</summary>
public abstract record FileSave
{
    private FileSave()
    {
    }

    public sealed record Saved : FileSave;

    /// <summary>Nothing was written: the value breaks the rules, and the file keeps what it had.</summary>
    public sealed record Invalid(IReadOnlyList<ConfigError> Errors) : FileSave;

    /// <summary>The disk refused the write; the file keeps what it had.</summary>
    public sealed record Unwritable : FileSave;
}
