// Deep-dump visitor: streams every field record to the DeepCrcWriter AND folds the identical
// bytes - mirroring the original's deep CRC, which "additionally streams every field to a file
// but folds the identical bytes" (desync-crc-deep-dive §5.2). A deep walk therefore always
// produces the same CRC as a plain XferCrcVisitor walk; a test pins that equivalence.

using System;

namespace OpenSage.SimCore.Sync;

public sealed class XferDeepDump : XferWriteVisitorBase
{
    private readonly DeepCrcWriter _writer;
    private XferCrc _crc;

    public XferDeepDump(DeepCrcWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public override XferMode Mode => XferMode.DeepDump;

    /// <summary>The accumulator after the walk so far - always equal to what a plain CRC
    /// walk over the same state yields.</summary>
    public uint Value => _crc.Value;

    protected override void Consume(string name, Tolerance tol, XferValueKind kind, ReadOnlySpan<byte> bytes)
    {
        _crc.Fold(bytes);
        _writer.Record(CurrentModule, name, tol, kind, bytes);
    }
}
