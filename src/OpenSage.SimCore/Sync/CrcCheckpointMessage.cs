// The checkpoint exchange message - our analog of the original's MSG_LOGIC_CRC 0x44A
// (api-freeze-v1 F8), upgraded from its single uint to the per-channel vector so the packet
// that detects a desync also localizes it. Frozen shape:
//   { uint frame; byte algorithmId; byte channelCount; uint[] channelCrcs; uint combined; }
// The vector is indexed by CrcChannel ordinal; a channel that is excluded or inactive carries
// 0 (peers are same-build with identical excludes by the content-identity check, so positional
// zeros are unambiguous). All integers little-endian on the wire, like everything else here.

using System;
using System.Collections.Generic;

namespace OpenSage.SimCore.Sync;

public sealed class CrcCheckpointMessage : IEquatable<CrcCheckpointMessage>
{
    /// <summary>The F7 rotate-left-1-and-add fold. The escape-hatch id 0; a stronger
    /// self-consistent hash gets the next id, not a wire redesign.</summary>
    public const byte AlgorithmRotlAdd = 0;

    public uint Frame { get; }
    public byte AlgorithmId { get; }
    public IReadOnlyList<uint> ChannelCrcs => _channelCrcs;
    public uint Combined { get; }

    private readonly uint[] _channelCrcs;

    public byte ChannelCount => (byte)_channelCrcs.Length;

    public CrcCheckpointMessage(uint frame, byte algorithmId, uint[] channelCrcs, uint combined)
    {
        ArgumentNullException.ThrowIfNull(channelCrcs);
        if (channelCrcs.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCrcs));
        }
        Frame = frame;
        AlgorithmId = algorithmId;
        _channelCrcs = channelCrcs;
        Combined = combined;
    }

    public int SizeInBytes => 4 + 1 + 1 + _channelCrcs.Length * 4 + 4;

    public byte[] ToBytes()
    {
        var buffer = new byte[SizeInBytes];
        WriteTo(buffer);
        return buffer;
    }

    public void WriteTo(Span<byte> destination)
    {
        XferPrimitives.WriteUInt32(destination, Frame);
        destination[4] = AlgorithmId;
        destination[5] = ChannelCount;
        for (var i = 0; i < _channelCrcs.Length; i++)
        {
            XferPrimitives.WriteUInt32(destination.Slice(6 + i * 4), _channelCrcs[i]);
        }
        XferPrimitives.WriteUInt32(destination.Slice(6 + _channelCrcs.Length * 4), Combined);
    }

    public static CrcCheckpointMessage Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < 10)
        {
            throw new FormatException("Checkpoint message too short.");
        }
        var frame = XferPrimitives.ReadUInt32(source);
        var algorithmId = source[4];
        int channelCount = source[5];
        if (source.Length != 10 + channelCount * 4)
        {
            throw new FormatException("Checkpoint message length does not match its channel count.");
        }
        var crcs = new uint[channelCount];
        for (var i = 0; i < channelCount; i++)
        {
            crcs[i] = XferPrimitives.ReadUInt32(source.Slice(6 + i * 4));
        }
        var combined = XferPrimitives.ReadUInt32(source.Slice(6 + channelCount * 4));
        return new CrcCheckpointMessage(frame, algorithmId, crcs, combined);
    }

    /// <summary>
    /// The localization step (F14): the channels whose CRCs differ between two peers'
    /// checkpoints for the same frame. Empty means in sync.
    /// </summary>
    public List<CrcChannel> DivergingChannels(CrcCheckpointMessage other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Frame != Frame || other.AlgorithmId != AlgorithmId || other.ChannelCount != ChannelCount)
        {
            throw new InvalidOperationException(
                "Checkpoints are only comparable for the same frame, algorithm and channel count.");
        }
        var diverging = new List<CrcChannel>();
        for (var i = 0; i < _channelCrcs.Length; i++)
        {
            if (_channelCrcs[i] != other._channelCrcs[i])
            {
                diverging.Add((CrcChannel)i);
            }
        }
        return diverging;
    }

    public bool Equals(CrcCheckpointMessage? other)
    {
        if (other is null || Frame != other.Frame || AlgorithmId != other.AlgorithmId ||
            Combined != other.Combined || _channelCrcs.Length != other._channelCrcs.Length)
        {
            return false;
        }
        for (var i = 0; i < _channelCrcs.Length; i++)
        {
            if (_channelCrcs[i] != other._channelCrcs[i])
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as CrcCheckpointMessage);

    public override int GetHashCode()
    {
        var h = Numerics.DeterministicHash.Begin();
        h = Numerics.DeterministicHash.Add(h, Frame);
        h = Numerics.DeterministicHash.Add(h, AlgorithmId);
        h = Numerics.DeterministicHash.Add(h, Combined);
        for (var i = 0; i < _channelCrcs.Length; i++)
        {
            h = Numerics.DeterministicHash.Add(h, _channelCrcs[i]);
        }
        return Numerics.DeterministicHash.Finish(h);
    }
}
