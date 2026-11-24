namespace Kzg.Discord.Tests;

public sealed class BotNicknameTests
{
    private readonly List<string?> _sent = [];
    private readonly Queue<NicknameResult> _answers = new();

    private BotNickname Build() => new((name, _) =>
    {
        _sent.Add(name);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : NicknameResult.Set);
    });

    [Fact]
    public async Task The_team_name_is_set_once_and_again_only_when_it_changes()
    {
        var nickname = Build();

        nickname.Wanted = "A1 Broadcaster";
        await nickname.SyncAsync(default);
        await nickname.SyncAsync(default);
        nickname.Wanted = "D2 Broadcaster";
        await nickname.SyncAsync(default);

        Assert.Equal(["A1 Broadcaster", "D2 Broadcaster"], _sent);
        Assert.False(nickname.Refused);
    }

    [Fact]
    public async Task A_bot_without_a_team_clears_only_a_nickname_this_run_set()
    {
        var untouched = Build();
        untouched.Wanted = null;
        await untouched.SyncAsync(default);
        Assert.Empty(_sent);

        var renamed = Build();
        renamed.Wanted = "A1 Broadcaster";
        await renamed.SyncAsync(default);
        renamed.Wanted = null;
        await renamed.SyncAsync(default);
        await renamed.SyncAsync(default);

        Assert.Equal(["A1 Broadcaster", null], _sent);
    }

    [Fact]
    public async Task A_refusal_is_reported_and_tried_again_until_the_permission_is_given()
    {
        var nickname = Build();
        _answers.Enqueue(NicknameResult.Refused);
        _answers.Enqueue(NicknameResult.Refused);

        nickname.Wanted = "A1 Broadcaster";
        await nickname.SyncAsync(default);
        Assert.True(nickname.Refused);
        await nickname.SyncAsync(default);
        Assert.True(nickname.Refused);
        await nickname.SyncAsync(default);

        Assert.False(nickname.Refused);
        Assert.Equal(["A1 Broadcaster", "A1 Broadcaster", "A1 Broadcaster"], _sent);
    }

    [Fact]
    public async Task A_failure_or_a_throw_never_escapes_and_is_tried_again()
    {
        var calls = 0;
        var nickname = new BotNickname((_, _) => ++calls == 1 ? throw new HttpRequestException("down") : Task.FromResult(NicknameResult.Failed));
        nickname.Wanted = "A1 Broadcaster";

        await nickname.SyncAsync(default);
        await nickname.SyncAsync(default);
        await nickname.SyncAsync(default);

        Assert.Equal(3, calls);
        Assert.False(nickname.Refused);
    }
}
