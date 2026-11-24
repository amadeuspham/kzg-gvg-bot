namespace Kzg.Bot;

/// <summary>
/// A file in the data folder that is there only while the bot runs. Found at the start, it means the last
/// run never reached its clean stop: a crash, a kill or a power cut, which the admins hear about.
/// </summary>
public sealed class RunMarker(string folder)
{
    public const string FileName = ".running";

    public string FilePath { get; } = Path.Combine(folder, FileName);

    /// <summary>Marks this run as started; true when the run before it ended without stopping cleanly.</summary>
    public bool Begin()
    {
        var unclean = File.Exists(FilePath);
        try
        {
            File.WriteAllText(FilePath, "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The data folder is read-only to us: the config would fail to save too, and says so then.
        }
        return unclean;
    }

    /// <summary>The run stopped cleanly.</summary>
    public void End()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left: the next start reports an unclean stop it didn't need to.
        }
    }
}
