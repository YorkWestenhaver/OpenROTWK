using System.IO;
using OpenSage.FileFormats;
using OpenSage.Logic.Orders;

namespace OpenSage.Data.Rep;

public sealed class ReplayChunkHeader
{
    public uint Timecode { get; private set; }
    public OrderType OrderType { get; private set; }
    public uint Number { get; private set; }

    /// <summary>
    /// Builds a chunk header directly, without a file behind it. Test visibility only
    /// (internal, per <c>InternalsVisibleTo</c>): R15 packet BR-P4B's replay canary needs a
    /// replay with exact chosen timecodes, which no recorded .rep can provide.
    /// </summary>
    internal static ReplayChunkHeader CreateForTests(uint timecode, OrderType orderType, uint number)
    {
        return new ReplayChunkHeader
        {
            Timecode = timecode,
            OrderType = orderType,
            Number = number
        };
    }

    internal static ReplayChunkHeader Parse(BinaryReader reader)
    {
        return new ReplayChunkHeader
        {
            Timecode = reader.ReadUInt32(),
            OrderType = reader.ReadUInt32AsEnum<OrderType>(),
            Number = reader.ReadUInt32()
        };
    }
}
