using Kzg.Discord.Voice;

namespace Kzg.Discord.Tests;

internal sealed record SentPacket(ushort Sequence, uint Timestamp, byte[] Opus);

/// <summary>A voice connection with no Discord: the test raises its events and reads what was sent.</summary>
internal sealed class FakeVoiceConnection : IVoiceConnection
{
    private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<uint, ulong> _users = [];

    public List<SentPacket> Sent { get; } = [];

    public List<bool> Speaking { get; } = [];

    public bool Disposed { get; private set; }

    public bool Closed { get; private set; }

    /// <summary>Raised to throw from <see cref="Send"/>, as a library with a dead socket does.</summary>
    public bool ThrowOnSend { get; set; }

    /// <summary>Raised to fail <see cref="SetSpeakingAsync"/>, as a closed gateway socket does.</summary>
    public bool ThrowOnSpeaking { get; set; }

    public bool IsReady { get; set; }

    public Task Lost => _lost.Task;

    public event FrameReceived? Frame;

    public event Action? UserJoined;

    public event Action? Ready;

    public event Action? Resumed;

    public event Action? Interrupted;

    public event Action<Kzg.Core.Abstractions.ChannelId?>? ChannelChanged;

    public void MapUser(uint ssrc, ulong user) => _users[ssrc] = user;

    public bool TryGetUser(uint ssrc, out ulong user) => _users.TryGetValue(ssrc, out user);

    public void Send(ushort sequence, uint timestamp, ReadOnlySpan<byte> opus)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("the socket is closed");
        }
        Sent.Add(new SentPacket(sequence, timestamp, opus.ToArray()));
    }

    public Task SetSpeakingAsync(bool on)
    {
        Speaking.Add(on);
        return ThrowOnSpeaking ? Task.FromException(new InvalidOperationException("the socket is closed")) : Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        Closed = true;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;

    public void Join() => UserJoined?.Invoke();

    public void BecomeReady()
    {
        IsReady = true;
        Ready?.Invoke();
    }

    public void Resume()
    {
        IsReady = true;
        Resumed?.Invoke();
    }

    public void Interrupt()
    {
        IsReady = false;
        Interrupted?.Invoke();
    }

    /// <summary>Discord says the bot's voice state is now this channel, or none.</summary>
    public void MoveTo(Kzg.Core.Abstractions.ChannelId? channel) => ChannelChanged?.Invoke(channel);

    public void LoseForGood() => _lost.TrySetResult();

    /// <summary>A packet from the network; the buffer is handed over as the library does, and the caller may scribble on it afterwards.</summary>
    public void Receive(uint ssrc, ushort sequence, uint timestamp, byte[] frame) => Frame?.Invoke(ssrc, sequence, timestamp, frame);
}
