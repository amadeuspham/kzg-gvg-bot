using System.Reflection;

namespace Kzg.App;

public static class AppInfo
{
    public const string Name = "KZG Broadcast";

    /// <summary>The build's version, from <c>Directory.Build.props</c>, without a source-control suffix.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>The server address this build starts with, or empty when none was chosen at build time.</summary>
    public static string DefaultHost { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "KzgDefaultHost")?.Value ?? "";

    /// <summary>The update feed this build starts with (an https address), or empty: updates stay off until one is set.</summary>
    public static string UpdateFeed { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "KzgUpdateFeed")?.Value ?? "";
}
