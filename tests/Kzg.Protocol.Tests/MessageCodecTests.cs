using System.Text;

namespace Kzg.Protocol.Tests;

public class MessageCodecTests
{
    public static TheoryData<ClientMessage> Client => [.. Samples.ClientMessages()];

    public static TheoryData<ServerMessage> Server => [.. Samples.ServerMessages()];

    [Theory]
    [MemberData(nameof(Client))]
    public void Every_client_message_round_trips(ClientMessage message)
    {
        var bytes = MessageCodec.Serialize(message);

        var parsed = Assert.IsType<MessageParse<ClientMessage>.Parsed>(MessageCodec.ParseClient(bytes));

        Assert.Equal(message.GetType(), parsed.Message.GetType());
        Assert.Equal(bytes, MessageCodec.Serialize(parsed.Message));
    }

    [Theory]
    [MemberData(nameof(Server))]
    public void Every_server_message_round_trips(ServerMessage message)
    {
        var bytes = MessageCodec.Serialize(message);

        var parsed = Assert.IsType<MessageParse<ServerMessage>.Parsed>(MessageCodec.ParseServer(bytes));

        Assert.Equal(message.GetType(), parsed.Message.GetType());
        Assert.Equal(bytes, MessageCodec.Serialize(parsed.Message));
    }

    [Fact]
    public void A_message_writes_v_and_type_first_and_camel_case_fields()
    {
        Assert.Equal("""{"v":2,"type":"press","action":"group","down":true,"seq":7}""",
            Samples.Json(new PressMessage(WireAction.Group, true, 7)));
        Assert.Equal("""{"v":2,"type":"unlink"}""", Samples.Json(new UnlinkMessage()));
    }

    [Fact]
    public void Welcome_omits_an_unissued_device_token()
    {
        var json = Samples.Json(new WelcomeMessage("42", "Ann", 2000, 6000, "0.1.0"));

        Assert.Equal(
            """{"v":2,"type":"welcome","userId":"42","displayName":"Ann","heartbeatMs":2000,"timeoutMs":6000,"serverVersion":"0.1.0"}""",
            json);
    }

    [Fact]
    public void A_welcome_from_a_bot_with_shot_callers_is_another_version_not_a_malformed_message()
    {
        var old = """{"v":1,"type":"welcome","userId":"42","displayName":"Ann","callerOf":null,"heartbeatMs":2000,"timeoutMs":6000,"serverVersion":"0.1.0"}""";

        Assert.Equal(1, Assert.IsType<MessageParse<ServerMessage>.UnsupportedVersion>(MessageCodec.ParseServer(Samples.Utf8(old))).Version);
    }

    [Fact]
    public void Press_spells_the_all_teams_key_as_the_wire_does_and_reads_it_back()
    {
        Assert.Equal("""{"v":2,"type":"press","action":"allTeams","down":true,"seq":3}""",
            Samples.Json(new PressMessage(WireAction.AllTeams, true, 3)));
        var parsed = MessageCodec.ParseClient("""{"v":2,"type":"heartbeat","seq":1,"held":["group","allTeams"],"openMic":false,"sentAtMs":0}"""u8);
        var heartbeat = Assert.IsType<HeartbeatMessage>(Assert.IsType<MessageParse<ClientMessage>.Parsed>(parsed).Message);
        Assert.Equal([WireAction.Group, WireAction.AllTeams], heartbeat.Held);
    }

    [Fact]
    public void Status_omits_channel_and_group_outside_voice_and_spells_enums_as_the_wire_does()
    {
        Assert.Equal(
            """{"v":2,"type":"status","active":[],"openMic":false,"targets":[],"warnings":["notInTeamVc","botDown"]}""",
            Samples.Json(new StatusMessage([], false, [], [StatusWarning.NotInTeamVc, StatusWarning.BotDown])));
        Assert.Contains("\"group\":\"defense\"",
            Samples.Json(new StatusMessage([WireAction.OpenMic], true, ["Defense 2"], [], "Defense 1", WireGroup.Defense)));
    }

    [Fact]
    public void Status_botDownIn_is_written_when_present_even_empty_and_read_back_and_an_older_bot_simply_omits_it()
    {
        Assert.Contains("\"botDownIn\":[\"Attack 2\",\"Attack 3\"]",
            Samples.Json(new StatusMessage([], false, [], [StatusWarning.BotDown], "Attack 1", WireGroup.Attack, ["Attack 2", "Attack 3"])));
        Assert.Contains("\"botDownIn\":[]",
            Samples.Json(new StatusMessage([], false, [], [], "Attack 1", WireGroup.Attack, [])));

        var withNames = Assert.IsType<MessageParse<ServerMessage>.Parsed>(MessageCodec.ParseServer(
            System.Text.Encoding.UTF8.GetBytes("""{"v":2,"type":"status","active":[],"openMic":false,"targets":[],"warnings":["botDown"],"channelName":"Attack 1","group":"attack","botDownIn":["Attack 2"]}""")));
        Assert.Equal(["Attack 2"], Assert.IsType<StatusMessage>(withNames.Message).BotDownIn);

        var older = Assert.IsType<MessageParse<ServerMessage>.Parsed>(MessageCodec.ParseServer(
            System.Text.Encoding.UTF8.GetBytes("""{"v":2,"type":"status","active":[],"openMic":false,"targets":[],"warnings":["botDown"]}""")));
        Assert.Null(Assert.IsType<StatusMessage>(older.Message).BotDownIn);
    }

    [Fact]
    public void Names_in_other_scripts_stay_utf8_rather_than_escapes()
    {
        Assert.Contains("\"displayName\":\"Тимур\"",
            Samples.Json(new WelcomeMessage("42", "Тимур", 2000, 6000, "0.1.0")));
    }

    [Fact]
    public void Field_order_is_not_significant_when_reading()
    {
        var parsed = MessageCodec.ParseClient("""{"seq":7,"down":false,"type":"press","action":"group","v":2}"""u8);

        var press = Assert.IsType<PressMessage>(Assert.IsType<MessageParse<ClientMessage>.Parsed>(parsed).Message);
        Assert.Equal(new PressMessage(WireAction.Group, false, 7), press);
    }

    [Fact]
    public void A_message_of_exactly_the_cap_is_read_and_one_byte_more_is_not()
    {
        var json = """{"v":2,"type":"unlink"}""";
        var exact = json[..^1] + new string(' ', WireProtocol.MaxMessageBytes - json.Length) + "}";

        Assert.IsType<MessageParse<ClientMessage>.Parsed>(MessageCodec.ParseClient(Samples.Utf8(exact)));
        var tooLarge = Assert.IsType<MessageParse<ClientMessage>.TooLarge>(MessageCodec.ParseClient(Samples.Utf8(exact + " ")));
        Assert.Equal(WireProtocol.MaxMessageBytes + 1, tooLarge.Bytes);
    }

    [Fact]
    public void Writing_a_message_over_the_cap_throws()
    {
        var names = Enumerable.Range(0, 60).Select(i => $"Channel {i} {new string('x', 80)}").ToArray();

        Assert.Throws<InvalidOperationException>(() => MessageCodec.Serialize(new StatusMessage([], false, names, [])));
    }

    [Fact]
    public void Writing_a_message_that_breaks_its_rules_throws()
    {
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new HelloMessage("0.1.0", Samples.InstallId)));
        Assert.Throws<ArgumentNullException>(() => MessageCodec.Serialize(null!));
    }

    [Theory]
    [InlineData("""{"v":3,"type":"hello","anything":"new"}""", 3)]
    [InlineData("""{"v":1,"type":"press","action":"group","down":true,"seq":1}""", 1)]
    [InlineData("""{"v":0,"type":"press"}""", 0)]
    public void Another_version_is_reported_before_its_fields_are_read(string json, int version)
    {
        var result = Assert.IsType<MessageParse<ClientMessage>.UnsupportedVersion>(MessageCodec.ParseClient(Samples.Utf8(json)));

        Assert.Equal(version, result.Version);
    }

    [Theory]
    // Not one JSON object.
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"hello\"")]
    [InlineData("{\"v\":2,\"type\":\"unlink\"")]
    [InlineData("""{"v":2,"type":"unlink"}{}""")]
    [InlineData("""{"v":2,"type":"unlink",}""")]
    [InlineData("""{"v":2,"type":"unlink"/* note */}""")]
    [InlineData("""{'v':1,'type':'unlink'}""")]
    // A broken envelope.
    [InlineData("""{"type":"unlink"}""")]
    [InlineData("""{"v":2}""")]
    [InlineData("""{"v":"1","type":"unlink"}""")]
    [InlineData("""{"v":2.0,"type":"unlink"}""")]
    [InlineData("""{"v":2,"type":1}""")]
    [InlineData("""{"v":2,"type":null}""")]
    [InlineData("""{"v":2,"v":2,"type":"unlink"}""")]
    [InlineData("""{"v":2,"type":"unlink","type":"unlink"}""")]
    [InlineData("""{"v":2,"type":"Unlink"}""")]
    [InlineData("""{"v":2,"type":"nope"}""")]
    // Fields missing, extra, repeated, mistyped or misspelt.
    [InlineData("""{"v":2,"type":"unlink","extra":0}""")]
    [InlineData("""{"v":2,"type":"press","action":"group","down":true}""")]
    [InlineData("""{"v":2,"type":"press","action":"group","down":true,"seq":1,"seq":2}""")]
    [InlineData("""{"v":2,"type":"press","action":"group","down":"true","seq":1}""")]
    [InlineData("""{"v":2,"type":"press","action":"group","down":true,"seq":"1"}""")]
    [InlineData("""{"v":2,"type":"press","action":"group","down":true,"seq":1.5}""")]
    [InlineData("""{"v":2,"type":"press","action":"group","down":null,"seq":1}""")]
    [InlineData("""{"v":2,"type":"press","action":"Group","down":true,"seq":1}""")]
    [InlineData("""{"v":2,"type":"press","action":0,"down":true,"seq":1}""")]
    [InlineData("""{"v":2,"type":"press","Action":"group","down":true,"seq":1}""")]
    [InlineData("""{"v":2,"type":"heartbeat","seq":1,"held":null,"openMic":false,"sentAtMs":0}""")]
    [InlineData("""{"v":2,"type":"heartbeat","seq":1,"held":["shout"],"openMic":false,"sentAtMs":0}""")]
    [InlineData("""{"v":2,"type":"heartbeat","seq":1,"held":[["group"]],"openMic":false,"sentAtMs":0}""")]
    [InlineData("""{"v":2,"type":"heartbeat","seq":1,"held":["allTeams","allTeams"],"openMic":false,"sentAtMs":0}""")]
    [InlineData("""{"v":2,"type":"heartbeat","seq":1,"held":["openMic"],"openMic":true,"sentAtMs":0}""")]
    [InlineData("""{"v":2,"type":"press","action":"AllTeams","down":true,"seq":1}""")]
    [InlineData("""{"v":2,"type":"hello","appVersion":null,"installId":"0f8fad5b-d9cb-469f-a165-70867728950e","loginId":"x"}""")]
    [InlineData("""{"v":2,"type":"hello","appVersion":"1","installId":"not-a-guid","loginId":"x"}""")]
    // A type the other side sends.
    [InlineData("""{"v":2,"type":"heartbeatAck","seq":1,"sentAtMs":0}""")]
    [InlineData("""{"v":2,"type":"error","code":"used","message":""}""")]
    public void The_bot_refuses_a_malformed_message(string json)
    {
        Assert.IsType<MessageParse<ClientMessage>.Malformed>(MessageCodec.ParseClient(Samples.Utf8(json)));
    }

    [Theory]
    [InlineData("""{"v":2,"type":"press","action":"group","down":true,"seq":1}""")]
    [InlineData("""{"v":2,"type":"welcome","userId":"42","displayName":"Ann","heartbeatMs":2000,"serverVersion":"1"}""")]
    [InlineData("""{"v":2,"type":"welcome","userId":42,"displayName":"Ann","heartbeatMs":2000,"timeoutMs":6000,"serverVersion":"1"}""")]
    // Shot callers are gone: a welcome that still names one is from a bot of another build.
    [InlineData("""{"v":2,"type":"welcome","userId":"42","displayName":"Ann","callerOf":null,"heartbeatMs":2000,"timeoutMs":6000,"serverVersion":"1"}""")]
    [InlineData("""{"v":2,"type":"status","active":[],"openMic":false,"targets":[],"warnings":["notCaller"]}""")]
    [InlineData("""{"v":2,"type":"error","code":"notCaller","message":""}""")]
    [InlineData("""{"v":2,"type":"status","active":[],"openMic":false,"targets":[null],"warnings":[]}""")]
    [InlineData("""{"v":2,"type":"status","active":[],"openMic":false,"targets":[],"warnings":["sleepy"]}""")]
    [InlineData("""{"v":2,"type":"error","code":"teapot","message":""}""")]
    [InlineData("""{"v":2,"type":"error","code":"used"}""")]
    public void The_app_refuses_a_malformed_message(string json)
    {
        Assert.IsType<MessageParse<ServerMessage>.Malformed>(MessageCodec.ParseServer(Samples.Utf8(json)));
    }

    [Fact]
    public void Invalid_utf8_is_refused()
    {
        byte[] json = [.. "{\"v\":2,\"type\":\"error\",\"code\":\"used\",\"message\":\""u8, 0xC3, 0x28, .. "\"}"u8];

        Assert.IsType<MessageParse<ServerMessage>.Malformed>(MessageCodec.ParseServer(json));
    }

    [Fact]
    public void Nesting_deeper_than_a_message_needs_is_refused()
    {
        var json = """{"v":2,"type":"unlink","x":""" + string.Concat(Enumerable.Repeat("[", 10)) + string.Concat(Enumerable.Repeat("]", 10)) + "}";

        Assert.IsType<MessageParse<ClientMessage>.Malformed>(MessageCodec.ParseClient(Samples.Utf8(json)));
    }

    [Fact]
    public void A_malformed_reason_never_quotes_a_secret()
    {
        var token = Samples.Secret();
        var json = $$"""{"v":2,"type":"hello","appVersion":"1","installId":"{{Samples.InstallId}}","deviceToken":"{{token}}","loginId":"{{token}}","extra":"{{token}}"}""";

        var result = Assert.IsType<MessageParse<ClientMessage>.Malformed>(MessageCodec.ParseClient(Encoding.UTF8.GetBytes(json)));

        Assert.DoesNotContain(token, result.Reason);
    }
}
