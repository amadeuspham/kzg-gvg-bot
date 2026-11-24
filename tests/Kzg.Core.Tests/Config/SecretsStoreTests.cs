using System.Collections;
using Kzg.Core.Config;

namespace Kzg.Core.Tests.Config;

public sealed class SecretsStoreTests : IDisposable
{
    // Made-up values: nothing here is a real token.
    private const string Token = "fake-token-value-1";

    private readonly TempFolder _folder = new();
    private readonly SecretsStore _store;

    public SecretsStoreTests() => _store = new SecretsStore(_folder.File("secrets.json"));

    public void Dispose() => _folder.Dispose();

    private void Write(string text)
    {
        File.WriteAllText(_store.FilePath, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_store.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Secrets_are_read_by_name_and_blanks_are_not_filled_in()
    {
        Write($$"""{ "relay1": "{{Token}}", "relay2": "", "oauthClientSecret": "s3", }""");

        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(_store.Load(new Hashtable())).Value;

        Assert.True(secrets.TryGet("relay1", out var token));
        Assert.Equal(Token, token);
        Assert.False(secrets.Has("relay2"));
        Assert.True(secrets.Has(Secrets.OAuthClientSecretName));
        Assert.Equal(["oauthClientSecret", "relay1"], secrets.Names.Order(StringComparer.Ordinal));
        Assert.False(secrets.PermissionsTightened);
    }

    [Fact]
    public void The_environment_sets_and_overrides_secrets_but_a_blank_one_does_not()
    {
        Write("""{ "relay1": "from-file", "relay2": "from-file" }""");
        var environment = new Hashtable
        {
            ["KZG_SECRET_relay1"] = "from-env",
            ["KZG_SECRET_relay2"] = "",
            ["KZG_SECRET_relay3"] = "added",
            ["KZG_SECRET_bad-name"] = "ignored",
            ["KZG_SECRET_"] = "ignored",
            ["kzg_secret_relay4"] = "ignored",
            ["PATH"] = "/usr/bin",
        };

        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(_store.Load(environment)).Value;

        Assert.Equal(["relay1", "relay2", "relay3"], secrets.Names.Order(StringComparer.Ordinal));
        Assert.True(secrets.TryGet("relay1", out var one) && one == "from-env");
        Assert.True(secrets.TryGet("relay2", out var two) && two == "from-file");
        Assert.True(secrets.TryGet("relay3", out var three) && three == "added");
    }

    [Fact]
    public void Without_a_file_the_environment_alone_holds_the_secrets()
    {
        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(_store.Load(new Hashtable { ["KZG_SECRET_relay1"] = Token })).Value;

        Assert.Equal(["relay1"], secrets.Names);
    }

    [Theory]
    [InlineData("""{ "relay1": 5 }""")]
    [InlineData("""{ "relay1": null }""")]
    [InlineData("""{ "relay1": "a", "relay1": "b" }""")]
    [InlineData("""{ "relay.1": "a" }""")]
    [InlineData("""["a"]""")]
    [InlineData("""{ "relay1": "fake-token-value-1" """)]
    public void A_broken_file_is_refused_without_quoting_any_value(string text)
    {
        Write(text);

        var errors = Assert.IsType<FileLoad<Secrets>.Broken>(_store.Load(new Hashtable())).Errors;

        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.DoesNotContain("fake-token", e.ToString(), StringComparison.Ordinal));
        Assert.All(errors, e => Assert.DoesNotContain("relay.1", e.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void A_token_pasted_as_a_name_is_never_named_in_the_error()
    {
        Write("""{ "relay1": "a", "MTIz.fake.token": 5 }""");

        var error = Assert.Single(Assert.IsType<FileLoad<Secrets>.Broken>(_store.Load(new Hashtable())).Errors);

        Assert.Null(error.Setting);
    }

    [Fact]
    public void Printing_secrets_shows_how_many_never_what()
    {
        var secrets = Secrets.Combine(new Dictionary<string, string> { ["relay1"] = Token }, new Hashtable());

        Assert.Equal("1 secret(s)", secrets.ToString());
        Assert.Empty(Secrets.Empty.Names);
    }

    [Theory]
    [InlineData("relay1", true)]
    [InlineData("oauthClientSecret", true)]
    [InlineData("A_b_9", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("relay-1", false)]
    [InlineData("relay 1", false)]
    public void A_name_is_letters_digits_and_underscores(string? name, bool valid)
    {
        Assert.Equal(valid, Secrets.IsValidName(name));
        Assert.False(Secrets.IsValidName(new string('a', 65)));
    }

    [Fact]
    public void A_file_others_can_read_is_made_the_owners_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Write("""{ "relay1": "x" }""");
        File.SetUnixFileMode(_store.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(_store.Load(new Hashtable())).Value;

        Assert.True(secrets.PermissionsTightened);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.FilePath));
        Assert.False(Assert.IsType<FileLoad<Secrets>.Loaded>(_store.Load(new Hashtable())).Value.PermissionsTightened);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_unreadable()
    {
        Directory.CreateDirectory(_store.FilePath);

        Assert.IsType<FileLoad<Secrets>.Unreadable>(_store.Load(new Hashtable()));
    }

    [Fact]
    public void Saving_sets_and_removes_names_keeps_the_rest_and_writes_an_owner_only_file()
    {
        Write("""{ "relay1": "old", "relay9": "stale", "oauthClientSecret": "kept", }""");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_store.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }

        var saved = _store.Save(new Dictionary<string, string> { ["relay1"] = Token, ["relay2"] = "new-two" }, ["relay9"]);

        Assert.IsType<FileSave.Saved>(saved);
        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(_store.Load(new Hashtable())).Value;
        Assert.Equal(["oauthClientSecret", "relay1", "relay2"], secrets.Names.Order(StringComparer.Ordinal));
        Assert.True(secrets.TryGet("relay1", out var one) && one == Token);
        Assert.False(secrets.PermissionsTightened);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.FilePath));
        }
    }

    [Fact]
    public void Saving_without_a_file_creates_it_owner_only()
    {
        Assert.IsType<FileSave.Saved>(_store.Save(new Dictionary<string, string> { ["relay1"] = Token }));

        Assert.Contains(Token, File.ReadAllText(_store.FilePath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.FilePath));
        }
    }

    [Fact]
    public void Saving_over_a_broken_file_or_a_bad_name_or_value_writes_nothing()
    {
        Write("{ not json");

        Assert.IsType<FileSave.Invalid>(_store.Save(new Dictionary<string, string> { ["relay1"] = Token }));
        Assert.Equal("{ not json", File.ReadAllText(_store.FilePath));
        Assert.IsType<FileSave.Invalid>(_store.Save(new Dictionary<string, string> { ["bad name"] = Token }));
        Assert.IsType<FileSave.Invalid>(_store.Save(new Dictionary<string, string> { ["relay1"] = "" }));
    }
}
