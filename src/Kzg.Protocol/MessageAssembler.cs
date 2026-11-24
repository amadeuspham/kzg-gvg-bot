namespace Kzg.Protocol;

public enum AssemblyState
{
    /// <summary>More fragments to come.</summary>
    Incomplete,

    /// <summary>The message is whole and in <see cref="MessageAssembler.Message"/>.</summary>
    Complete,

    /// <summary>Over the cap: stop reading and close the connection.</summary>
    TooLarge,
}

/// <summary>
/// Gathers one WebSocket message from its fragments into a fixed buffer and stops at
/// <see cref="WireProtocol.MaxMessageBytes"/>, so a peer can never make the reader hold more. A
/// receive loop reads into <see cref="Remaining"/> and reports each read to <see cref="Advance"/>.
/// </summary>
public sealed class MessageAssembler
{
    // One byte past the cap: a read that fills it proves the message too large, even when the last
    // fragment is empty.
    private readonly byte[] _buffer = new byte[WireProtocol.MaxMessageBytes + 1];
    private int _length;
    private AssemblyState _state = AssemblyState.Incomplete;

    /// <summary>Where the next fragment goes; empty once the message is complete or too large.</summary>
    public Memory<byte> Remaining =>
        _state == AssemblyState.Incomplete ? _buffer.AsMemory(_length) : Memory<byte>.Empty;

    /// <summary>The whole message; empty unless the state is <see cref="AssemblyState.Complete"/>.</summary>
    public ReadOnlySpan<byte> Message =>
        _state == AssemblyState.Complete ? _buffer.AsSpan(0, _length) : ReadOnlySpan<byte>.Empty;

    /// <summary>Records that <paramref name="count"/> bytes were read into <see cref="Remaining"/>.</summary>
    public AssemblyState Advance(int count, bool endOfMessage)
    {
        ThrowUnlessIncomplete();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _length);
        _length += count;
        _state = _length > WireProtocol.MaxMessageBytes ? AssemblyState.TooLarge
            : endOfMessage ? AssemblyState.Complete
            : AssemblyState.Incomplete;
        return _state;
    }

    /// <summary>Copies a fragment in; for callers that already hold the bytes.</summary>
    public AssemblyState Append(ReadOnlySpan<byte> fragment, bool endOfMessage)
    {
        ThrowUnlessIncomplete();
        var room = Remaining.Span;
        var taken = Math.Min(fragment.Length, room.Length);
        fragment[..taken].CopyTo(room);
        if (taken < fragment.Length)
        {
            _length = _buffer.Length;
            _state = AssemblyState.TooLarge;
            return _state;
        }
        return Advance(taken, endOfMessage);
    }

    /// <summary>Empties the buffer for the next message.</summary>
    public void Reset()
    {
        _length = 0;
        _state = AssemblyState.Incomplete;
    }

    private void ThrowUnlessIncomplete()
    {
        if (_state != AssemblyState.Incomplete)
        {
            throw new InvalidOperationException("reset the assembler before the next message");
        }
    }
}
