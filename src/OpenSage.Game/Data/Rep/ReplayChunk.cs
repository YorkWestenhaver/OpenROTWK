using System;
using System.Diagnostics;
using System.IO;
using OpenSage.FileFormats;
using OpenSage.Logic.Object;
using OpenSage.Logic.Orders;

namespace OpenSage.Data.Rep;

[DebuggerDisplay("[{Header.Timecode}]: {Order.OrderType} ({Order.Arguments.Count})")]
public sealed class ReplayChunk
{
    public ReplayChunkHeader Header { get; private set; }
    public Order Order { get; private set; }

    /// <summary>
    /// Builds a chunk directly from an order and a timecode. Test visibility only (internal,
    /// per <c>InternalsVisibleTo</c>) - see <see cref="ReplayChunkHeader.CreateForTests"/>.
    /// The header's Number mirrors Parse's own convention (player index + 1).
    /// </summary>
    internal static ReplayChunk CreateForTests(uint timecode, Order order)
    {
        return new ReplayChunk
        {
            Header = ReplayChunkHeader.CreateForTests(
                timecode,
                order.OrderType,
                (uint)(order.PlayerIndex + 1)),
            Order = order
        };
    }

    internal static ReplayChunk Parse(BinaryReader reader)
    {
        var result = new ReplayChunk
        {
            Header = ReplayChunkHeader.Parse(reader)
        };

        var numUniqueArgumentTypes = reader.ReadByte();

        // Pairs of {argument type, count}.
        var argumentCounts = new (OrderArgumentType argumentType, byte count)[numUniqueArgumentTypes];
        for (var i = 0; i < numUniqueArgumentTypes; i++)
        {
            argumentCounts[i] = (reader.ReadByteAsEnum<OrderArgumentType>(), reader.ReadByte());
        }

        var order = new Order((int)result.Header.Number - 1, result.Header.OrderType);
        result.Order = order;

        for (var i = 0; i < numUniqueArgumentTypes; i++)
        {
            ref var argumentCount = ref argumentCounts[i];
            var argumentType = argumentCount.argumentType;

            for (var j = 0; j < argumentCount.count; j++)
            {
                switch (argumentType)
                {
                    case OrderArgumentType.Integer:
                        order.AddIntegerArgument(reader.ReadInt32());
                        break;

                    case OrderArgumentType.Float:
                        order.AddFloatArgument(reader.ReadSingle());
                        break;

                    case OrderArgumentType.Boolean:
                        order.AddBooleanArgument(reader.ReadBooleanChecked());
                        break;

                    case OrderArgumentType.ObjectId:
                        order.AddObjectIdArgument(new ObjectId(reader.ReadUInt32()));
                        break;

                    case OrderArgumentType.Position:
                        order.AddPositionArgument(reader.ReadVector3());
                        break;

                    case OrderArgumentType.ScreenPosition:
                        order.AddScreenPositionArgument(reader.ReadPoint2D());
                        break;

                    case OrderArgumentType.ScreenRectangle:
                        order.AddScreenRectangleArgument(reader.ReadRectangle());
                        break;

                    case OrderArgumentType.Unknown4:
                        // in order to align bytes in a random replay, we needed to read 4. has to do with DrawBoxSelection
                        order.AddIntegerArgument(reader.ReadInt32());
                        // skip silently
                        break;

                    // this commented code block is here in case somebody needs to parse a replay file with argumenttype unknown10
                    /*
                    case OrderArgumentType.Unknown10:
                        // seems to be 2 bytes, has to do with OrderType 1091. TODO: check this!
                        order.AddIntegerArgument(reader.ReadInt16());
                        break;
                    */

                    default:
                        throw new InvalidOperationException();
                }
            }
        }

        return result;
    }
}
