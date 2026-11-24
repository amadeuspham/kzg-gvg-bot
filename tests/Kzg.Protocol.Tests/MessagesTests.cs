namespace Kzg.Protocol.Tests;

public class MessagesTests
{
    private static string Hello(string credentials) =>
        $$"""{"v":2,"type":"hello","appVersion":"0.1.0","installId":"{{Samples.InstallId}}"{{credentials}}}""";

    private static MessageParse<ClientMessage> ParseHello(string credentials) =>
        MessageCodec.ParseClient(Samples.Utf8(Hello(credentials)));

    [Fact]
    public void Hello_with_no_credential_is_refused()
    {
        var result = Assert.IsType<MessageParse<ClientMessage>.Malformed>(ParseHello(""));

        Assert.Contains("exactly one", result.Reason);
    }

    [Fact]
    public void Hello_with_only_null_credentials_is_refused()
    {
        Assert.IsType<MessageParse<ClientMessage>.Malformed>(ParseHello(""","code":null,"loginId":null"""));
    }

    [Theory]
    [InlineData("code", "deviceToken")]
    [InlineData("code", "loginId")]
    [InlineData("deviceToken", "loginId")]
    public void Hello_with_two_credentials_is_refused(string first, string second)
    {
        var values = new Dictionary<string, string>
        {
            ["code"] = Samples.Code(),
            ["deviceToken"] = Samples.Secret(1),
            ["loginId"] = Samples.Secret(2),
        };

        var result = Assert.IsType<MessageParse<ClientMessage>.Malformed>(
            ParseHello($$""","{{first}}":"{{values[first]}}","{{second}}":"{{values[second]}}" """));

        Assert.Contains("exactly one", result.Reason);
    }

    [Fact]
    public void Hello_with_all_three_credentials_is_refused()
    {
        Assert.IsType<MessageParse<ClientMessage>.Malformed>(
            ParseHello($$""","code":"{{Samples.Code()}}","deviceToken":"{{Samples.Secret()}}","loginId":"{{Samples.Secret(2)}}" """));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("deviceToken")]
    [InlineData("loginId")]
    public void Hello_with_one_credential_of_the_wrong_form_is_refused(string credential)
    {
        Assert.IsType<MessageParse<ClientMessage>.Malformed>(ParseHello($$""","{{credential}}":"short" """));
        Assert.IsType<MessageParse<ClientMessage>.Malformed>(ParseHello($$""","{{credential}}":"" """));
    }

    [Fact]
    public void Hello_carries_the_whole_connect_code()
    {
        var code = Samples.Code("gvg.example.com:8443");

        var parsed = Assert.IsType<MessageParse<ClientMessage>.Parsed>(ParseHello($$""","code":"{{code}}" """));

        Assert.Equal(code, Assert.IsType<HelloMessage>(parsed.Message).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1\n2")]
    public void Hello_needs_a_printable_app_version(string version)
    {
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new HelloMessage(version, Samples.InstallId, LoginId: Samples.Secret())));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new HelloMessage(new string('1', 65), Samples.InstallId, LoginId: Samples.Secret())));
    }

    [Fact]
    public void Hello_needs_a_real_install_id()
    {
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new HelloMessage("0.1.0", Guid.Empty, LoginId: Samples.Secret())));
    }

    [Fact]
    public void Hello_and_welcome_never_print_their_secrets()
    {
        var token = Samples.Secret();
        var code = Samples.Code();

        Assert.DoesNotContain(token, new HelloMessage("0.1.0", Samples.InstallId, DeviceToken: token).ToString());
        Assert.DoesNotContain(token, new HelloMessage("0.1.0", Samples.InstallId, LoginId: token).ToString());
        Assert.DoesNotContain(code, new HelloMessage("0.1.0", Samples.InstallId, Code: code).ToString());
        Assert.Contains("credential = invalid", new HelloMessage("0.1.0", Samples.InstallId).ToString());
        Assert.Contains("credential = code", new HelloMessage("0.1.0", Samples.InstallId, Code: code).ToString());
        Assert.DoesNotContain(token, new WelcomeMessage("42", "Ann", 2000, 6000, "1", token).ToString());
    }

    [Fact]
    public void Press_is_for_the_two_held_keys_only()
    {
        Assert.NotEmpty(MessageCodec.Serialize(new PressMessage(WireAction.AllTeams, true, 1)));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new PressMessage(WireAction.OpenMic, true, 1)));
        Assert.IsType<MessageParse<ClientMessage>.Malformed>(MessageCodec.ParseClient(
            """{"v":2,"type":"press","action":"openMic","down":true,"seq":1}"""u8));
    }

    [Fact]
    public void Sequence_numbers_and_times_are_never_negative()
    {
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new PressMessage(WireAction.Group, true, -1)));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new OpenMicMessage(true, -1)));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new HeartbeatMessage(-1, [], false, 0)));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new HeartbeatMessage(1, [], false, -1)));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new HeartbeatAckMessage(-1, 0)));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new HeartbeatAckMessage(1, -1)));
    }

    [Fact]
    public void Heartbeat_holds_each_key_once_and_never_the_open_mic_latch()
    {
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new HeartbeatMessage(1, [WireAction.Group, WireAction.Group], false, 0)));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new HeartbeatMessage(1, [WireAction.OpenMic], true, 0)));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new HeartbeatMessage(1, [WireAction.AllTeams, WireAction.Group, WireAction.AllTeams], false, 0)));
        Assert.NotEmpty(MessageCodec.Serialize(new HeartbeatMessage(1, [WireAction.Group, WireAction.AllTeams], false, 0)));
    }

    [Fact]
    public void Welcome_needs_a_snowflake_names_and_a_timeout_past_the_heartbeat()
    {
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new WelcomeMessage("042", "Ann", 2000, 6000, "1")));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new WelcomeMessage("42", "", 2000, 6000, "1")));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new WelcomeMessage("42", "Ann", 0, 6000, "1")));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new WelcomeMessage("42", "Ann", 2000, 2000, "1")));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new WelcomeMessage("42", "Ann", 2000, 6000, "")));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new WelcomeMessage("42", "Ann", 2000, 6000, "1", "short")));
    }

    [Fact]
    public void Status_rules()
    {
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new StatusMessage([], false, [], [], Group: WireGroup.Attack)));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new StatusMessage([], false, [], [], ChannelName: "")));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new StatusMessage([WireAction.Group, WireAction.Group], false, [], [])));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new StatusMessage([], false, [""], [])));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new StatusMessage([], false, [], [StatusWarning.BotDown, StatusWarning.BotDown])));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new StatusMessage([], false, [], [], BotDownIn: [""])));
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.Serialize(new StatusMessage([], false, [], [], BotDownIn: ["Attack 2", "Attack 2"])));
    }

    [Fact]
    public void Error_message_is_short()
    {
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new ErrorMessage(ErrorCode.Used, new string('x', 501))));
    }

    [Fact]
    public void Every_message_names_its_wire_type()
    {
        Assert.All(Samples.ClientMessages().Concat<KzgMessage>(Samples.ServerMessages()), m =>
        {
            Assert.Equal(WireProtocol.Version, m.V);
            Assert.StartsWith($"{{\"v\":2,\"type\":\"{m.Type}\"", Samples.Json(m));
        });
    }
}
