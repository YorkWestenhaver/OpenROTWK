// CRC visitor: folds each primitive call's canonical byte image independently through the F7
// fold - the per-call word/remainder split is inherited from the base class handing Consume
// exactly one call's buffer at a time. Names, tolerances and module identity never reach the
// accumulator, matching the original engine (type tags are never folded - crc-byteorder §2).

using System;

namespace OpenSage.SimCore.Sync
{
    public sealed class XferCrcVisitor : XferWriteVisitorBase, IXferSink
    {
        private XferCrc _crc;

        public override XferMode Mode => XferMode.Crc;

        /// <summary>The accumulator after the walk so far.</summary>
        public uint Value => _crc.Value;

        protected override void Consume(string name, Tolerance tol, XferValueKind kind, ReadOnlySpan<byte> bytes)
        {
            _crc.Fold(bytes);
        }

        void IXferSink.Bytes(ReadOnlySpan<byte> bytes) => _crc.Fold(bytes);

        /// <summary>
        /// Folds a raw pre-serialised buffer byte-wise, the shape of the original's RNG
        /// seed-fold helper. Used by the LogicRandom channel (crc-byteorder §2.1).
        /// </summary>
        public void FoldBytewise(ReadOnlySpan<byte> bytes) => _crc.FoldBytewise(bytes);
    }
}
