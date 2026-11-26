using Kzg.App.Services;

namespace Kzg.App.Tests;

public sealed class StartAtLoginTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-login-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeRunValues : IRunValues
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Get(string name) => Values.GetValueOrDefault(name);

        public void Set(string name, string command) => Values[name] = command;

        public void Remove(string name) => Values.Remove(name);
    }

    [Fact]
    public void On_a_mac_the_login_entry_is_a_launch_agent_file_that_opens_the_app_minimized()
    {
        var agent = new MacLaunchAgent(_folder, "/Applications/Discord Broadcast.app/Contents/MacOS/Kzg.App");
        Assert.False(agent.IsEnabled);

        agent.Set(true);

        Assert.True(agent.IsEnabled);
        var plist = File.ReadAllText(Path.Combine(_folder, "app.kzg.login.plist"));
        Assert.Contains("<string>/Applications/Discord Broadcast.app/Contents/MacOS/Kzg.App</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>--minimized</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key>", plist, StringComparison.Ordinal);
        Assert.DoesNotContain(".tmp", string.Join(' ', Directory.GetFiles(_folder)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_markup_characters_is_escaped_in_the_launch_agent()
    {
        new MacLaunchAgent(_folder, "/Users/a&b/<x>/Discord Broadcast.app/Contents/MacOS/Kzg.App").Set(true);

        var plist = File.ReadAllText(Path.Combine(_folder, "app.kzg.login.plist"));
        Assert.Contains("a&amp;b/&lt;x&gt;", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_it_off_removes_the_file_and_turning_it_off_twice_is_harmless()
    {
        var agent = new MacLaunchAgent(_folder, "/Applications/Discord Broadcast.app/Contents/MacOS/Kzg.App");
        agent.Set(true);

        agent.Set(false);
        agent.Set(false);

        Assert.False(agent.IsEnabled);
    }

    [Fact]
    public void Only_a_binary_inside_an_app_bundle_may_register_itself_so_a_dev_build_never_does()
    {
        Assert.True(MacLaunchAgent.IsInsideBundle("/Applications/Discord Broadcast.app/Contents/MacOS/Kzg.App"));
        Assert.False(MacLaunchAgent.IsInsideBundle("/Users/me/src/Kzg.App/bin/Debug/net10.0/Kzg.App"));
    }

    [Fact]
    public void On_windows_the_login_entry_is_a_quoted_run_value_that_opens_the_app_minimized()
    {
        var values = new FakeRunValues();
        var key = new WindowsRunKey(values, @"C:\Users\me\AppData\Local\KZGBroadcast\current\Kzg.App.exe");
        Assert.False(key.IsEnabled);

        key.Set(true);

        Assert.True(key.IsEnabled);
        Assert.Equal("\"C:\\Users\\me\\AppData\\Local\\KZGBroadcast\\current\\Kzg.App.exe\" --minimized", values.Values["Discord Broadcast"]);

        key.Set(false);
        Assert.False(key.IsEnabled);
    }

    [Fact]
    public void Writing_the_windows_entry_removes_the_value_an_older_version_left_under_the_old_name()
    {
        var values = new FakeRunValues();
        values.Values["KZG Broadcast"] = "\"C:\\old\\Kzg.App.exe\" --minimized";
        var key = new WindowsRunKey(values, @"C:\Users\me\AppData\Local\KZGBroadcast\current\Kzg.App.exe");
        Assert.True(key.IsEnabled);

        key.Set(true);

        Assert.False(values.Values.ContainsKey("KZG Broadcast"));
        Assert.True(values.Values.ContainsKey("Discord Broadcast"));
    }

    [Fact]
    public void Turning_the_windows_entry_off_also_removes_the_old_named_value()
    {
        var values = new FakeRunValues();
        values.Values["KZG Broadcast"] = "x";
        values.Values["Discord Broadcast"] = "y";
        var key = new WindowsRunKey(values, @"C:\Users\me\AppData\Local\KZGBroadcast\current\Kzg.App.exe");

        key.Set(false);

        Assert.Empty(values.Values);
        Assert.False(key.IsEnabled);
    }

    [Fact]
    public void Only_the_installed_copy_on_windows_may_register_itself()
    {
        Assert.True(WindowsRunKey.IsInstalledCopy(@"C:\Users\me\AppData\Local\KZGBroadcast\current\Kzg.App.exe"));
        Assert.False(WindowsRunKey.IsInstalledCopy(@"C:\src\Kzg.App\bin\Debug\net10.0\Kzg.App.exe"));
    }

    [Fact]
    public void A_dev_run_has_no_login_entry()
    {
        // The test host is neither an app bundle nor a Velopack install.
        Assert.Null(StartAtLogin.ForThisProcess());
    }
}
